using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Host.Config;
using Npc.Host.Observability;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;
using Npc.Sim.Validation;

namespace Npc.Host.Replan;

/// <summary>
/// 재계획 티어 한 벌. docs/14 §3·§4 · T4-15.
///
/// <b><c>--tier</c> 가 무엇을 켤지 정한다.</b> <c>none</c> 이면 아무것도 만들지 않고
/// 런타임 LLM 호출이 0 이다 — P1 게이트가 그 상태로 돈다.
///
/// <b>여기가 유일하게 "전부를 아는" 곳이다.</b> <c>Npc.Runtime</c> 은 <c>Npc.Llm</c> 을 모르고
/// (CLAUDE.md §3), 워커는 조립 루트에서만 조립된다 (T4-10).
/// </summary>
internal sealed class TierWiring : IAsyncDisposable
{
    private readonly List<ReplanWorker> _workers = [];

    private TierWiring(TierMode mode) => Mode = mode;

    /// <summary>켜진 티어 구성.</summary>
    public TierMode Mode { get; }

    /// <summary>계측 누계기. 티어가 꺼져 있으면 null 이다 — 그때 <c>LlmCalls</c> 는 0 이다.</summary>
    public CompileStatsCollector? Stats { get; private set; }

    /// <summary>예산. 상한 4개를 들고 있다 (docs/14 §3).</summary>
    public ReplanBudget? Budget { get; private set; }

    /// <summary>티어 라우터.</summary>
    public TieredPlanCompiler? Router { get; private set; }

    /// <summary>T2 서킷 브레이커.</summary>
    public CircuitBreaker? Breaker { get; private set; }

    /// <summary>개별 재계획 공급원 (T1).</summary>
    public IndividualReplanSource? Individual { get; private set; }

    /// <summary>아키타입 버킷 미스 공급원 (T2).</summary>
    public BucketReplanSource? Buckets { get; private set; }

    /// <summary>돌고 있는 워커 풀. <c>--tier none</c> 이면 비어 있다.</summary>
    public IReadOnlyList<ReplanWorker> Workers => _workers;

    /// <summary>티어가 하나라도 켜졌는가.</summary>
    public bool Enabled => _workers.Count > 0;

    /// <summary>쓰고 있는 엔진 id. 티어가 꺼져 있으면 빈 문자열이다. 대시보드 비용 패널이 읽는다.</summary>
    public string EngineIds { get; private set; } = string.Empty;

    /// <summary>
    /// 옵션대로 조립한다. 기동 시 1회.
    ///
    /// <b>엔진이 설정되지 않았으면(키가 없으면) 경고만 하고 그 티어를 끈다</b> —
    /// 기동을 막으면 부하 매트릭스의 다른 셀도 같이 죽는다. 무엇이 꺼졌는지는 로그에 남는다.
    /// </summary>
    public static TierWiring Build(
        HostOptions options,
        MasterDataSet data,
        string masterDataDir,
        NpcStore store,
        PlanStore plans,
        ReplanQueue queue,
        ReplanHandoff handoff,
        ReplanSnapshots snapshots,
        IndividualPlanPool pool,
        PlanSwapper swapper,
        ZoneStateTable zoneStates,
        GameClock clock,
        TextWriter log,
        KillSwitchState? switches = null,
        IAlarmSink? alarms = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        // 싱크가 없으면 예산 경보는 로그로만 간다. 테스트가 그 경로를 쓴다.
        IAlarmSink sink = alarms ?? NullAlarmSink.Instance;

        if (options.Tier == TierMode.None)
        {
            return new TierWiring(TierMode.None);
        }

        // 탐색 기준은 ConfigPaths 한 곳이다 (A-04). 예전에는 작업 폴더 기준이라
        // 같은 빌드가 실행 방식에 따라 다른 파일을 읽었고, 로그에는 무엇을 읽었는지 없었다.
        string? llmPath = ConfigPaths.Resolve(LlmOptions.FileName);

        if (llmPath is null)
        {
            log.WriteLine($"warn: {LlmOptions.FileName} 을 찾지 못해 --tier {options.Tier} 를 끈다.");
            return new TierWiring(TierMode.None);
        }

        LlmOptions llm;
        try
        {
            llm = LlmOptions.Load(llmPath);
        }
        catch (FileNotFoundException)
        {
            log.WriteLine($"warn: {LlmOptions.FileName} 을 찾지 못해 --tier {options.Tier} 를 끈다.");
            return new TierWiring(TierMode.None);
        }

        log.WriteLine($"llm-config: {llmPath}");

        var stats = new CompileStatsCollector();
        ReplanBudgetLimits limits = ReplanBudgetLimits.Measured.ForWorkers(options.T1Workers);

        // 예산 경보는 싱크로 간다 (A-05 · C-02). 예전에는 콘솔 한 줄이었고
        // RateLimited 는 로그조차 없어서 "왜 처리율이 안 오르나" 를 추적할 수 없었다.
        var bridge = new BudgetAlarmBridge(sink, limits);

        var budget = new ReplanBudget(limits, clock.Current)
        {
            Alarm = alarm =>
            {
                bridge.OnBudgetAlarm(alarm);
                LogAlarm(log, alarm);
            },
        };

        var breaker = new CircuitBreaker();
        PromptPrefix prefix = PromptPrefix.Build(data, masterDataDir);

        // reasoning 정화 (C-06). 금칙어 파일이 없으면 빈 정화기다 — 없는 것은 오류가 아니다.
        ReasoningSanitizer moderator = ReasoningSanitizer.Load(Path.Combine(masterDataDir, "prompt"));

        if (moderator.BlockedWordCount > 0)
        {
            log.WriteLine($"moderation: 금칙어 {moderator.BlockedWordCount}건");
        }

        var dryRun = new DryRunValidator(data);

        // 인접 버킷 재사용은 LLM 을 부르기 전에 공짜로 한 번 더 시도하는 경로다 (docs/12 §7).
        var reuse = new NeighborReuseSource(plans, data);

        IPlanCompiler? local = TryBuildCompiler(
            llm, options.T1Engine ?? FirstLocalEngineId(llm), data, prefix, stats, dryRun, reuse,
            log, "T1", () => clock.Current, sink, moderator);

        IPlanCompiler? external = TryBuildCompiler(
            llm, options.T2Engine, data, prefix, stats, dryRun, reuse,
            log, "T2", () => clock.Current, sink, moderator);

        // 요청한 티어의 엔진이 없으면 그 티어는 꺼진다. 라우터는 둘 다 필요하므로
        // 없는 쪽을 있는 쪽으로 대신 채운다 — 그러면 강등·페일오버가 같은 엔진으로 간다.
        IPlanCompiler? t1 = options.UsesT1 ? local : null;
        IPlanCompiler? t2 = options.UsesT2 ? external : null;

        if (t1 is null && t2 is null)
        {
            log.WriteLine($"warn: --tier {options.Tier} 에 쓸 엔진이 하나도 없다. 티어를 끈다.");
            return new TierWiring(TierMode.None);
        }

        // 메꾼 자리는 반드시 알려 준다 (T5-21). 이게 없으면 --tier t2 에서 T2 를 끊어도
        // T1 자리에 앉은 같은 외부 컴파일러가 계속 불려 킬스위치가 아무것도 끊지 못한다.
        var router = new TieredPlanCompiler(t1 ?? t2!, t2 ?? t1!, budget, () => clock.Current)
        {
            LocalQueueDepth = () => queue.Count,
            Breaker = breaker,
            Switches = switches ?? KillSwitchState.None,
            HasT1 = t1 is not null,
            HasT2 = t2 is not null,
        };

        var wiring = new TierWiring(options.Tier)
        {
            Stats = stats,
            Budget = budget,
            Router = router,
            Breaker = breaker,
        };

        if (t1 is not null)
        {
            wiring.Individual = new IndividualReplanSource(
                store, queue, handoff, snapshots, pool, swapper, data, () => clock.TimeOfDay)
            {
                ZoneStates = zoneStates,
            };

            wiring._workers.Add(
                new ReplanWorker(wiring.Individual, router, () => clock.Current, options.T1Workers));
        }

        if (t2 is not null)
        {
            wiring.Buckets = new BucketReplanSource(plans, data);

            wiring._workers.Add(
                new ReplanWorker(wiring.Buckets, router, () => clock.Current, options.T2Workers));
        }

        wiring.EngineIds = string.Join(
            " + ",
            new[]
            {
                t1 is null ? null : $"T1 {EngineIdOf(t1)}",
                t2 is null ? null : $"T2 {EngineIdOf(t2)}",
            }.Where(s => s is not null));

        return wiring;
    }

    /// <summary>컴파일러가 쓰는 엔진 id. 라우터가 아닌 실제 컴파일러에서 읽는다.</summary>
    private static string EngineIdOf(IPlanCompiler compiler) =>
        compiler is LlmPlanCompiler llm ? llm.Engine.Id : "?";

    /// <summary>워커를 띄운다. 틱 루프와 다른 스레드에서 돈다 (CLAUDE.md §2.1).</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        foreach (ReplanWorker worker in _workers)
        {
            await worker.StartAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>워커를 세운다.</summary>
    public async Task StopAsync()
    {
        foreach (ReplanWorker worker in _workers)
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>기동 로그 한 줄.</summary>
    public string Describe()
    {
        if (!Enabled)
        {
            return "llm off (런타임 호출 없음)";
        }

        var parts = new List<string>(2);

        if (Individual is not null)
        {
            parts.Add($"T1 워커 {_workers[0].Workers}");
        }

        if (Buckets is not null)
        {
            parts.Add($"T2 워커 {_workers[^1].Workers}");
        }

        return $"tier {Mode} · {string.Join(" · ", parts)}";
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        foreach (ReplanWorker worker in _workers)
        {
            worker.Dispose();
        }
    }

    /// <summary>기록만 남긴다 — 워커 스레드에서 불리므로 블록하지 않는다.</summary>
    private static void LogAlarm(TextWriter log, ReplanBudgetAlarm alarm)
    {
        // 레이트리미터는 정상 동작의 일부라 로그를 남기지 않는다. 캡·강등·거절만 올린다.
        if (alarm.Kind == ReplanBudgetAlarmKind.RateLimited)
        {
            return;
        }

        log.WriteLine(
            $"budget: {alarm.Kind} · 요청 {alarm.Requested} → {alarm.Granted} · "
            + $"오늘 {alarm.TokensToday} tok · ${alarm.CostToday:0.####} · tick {alarm.At.Value}");
    }

    private static string? FirstLocalEngineId(LlmOptions llm)
    {
        foreach (LlmEngineOptions engine in llm.Engines)
        {
            if (engine.IsLocal)
            {
                return engine.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// 이 티어의 컴파일러를 만든다. <b>체인이 있으면 페일오버 클라이언트를 끼운다</b> (C-01).
    ///
    /// 체인의 첫 엔진이 단가·모델 태그의 기준이다 — 페일오버는 "같은 모델을 다른 경로로" 를
    /// 전제하므로 체인 안에서 단가가 크게 달라지면 그것은 체인 구성이 잘못된 것이다.
    /// </summary>
    private static IPlanCompiler? TryBuildCompiler(
        LlmOptions llm,
        string? engineId,
        MasterDataSet data,
        PromptPrefix prefix,
        CompileStatsCollector stats,
        DryRunValidator dryRun,
        IPlanReuseSource reuse,
        TextWriter log,
        string label,
        Func<Tick> now,
        IAlarmSink alarms,
        IContentModerator moderator)
    {
        // --t1-engine·--t2-engine 은 체인의 첫 자리를 덮어쓰는 것으로 의미를 유지한다.
        ImmutableArray<LlmEngineOptions> chain = llm.Chain(label.ToLowerInvariant(), engineId);

        if (chain.Length == 0)
        {
            LlmEngineOptions probe;

            try
            {
                probe = llm.Engine(engineId);
            }
            catch (InvalidDataException ex)
            {
                log.WriteLine($"warn: {label} 엔진을 못 찾았다 — {ex.Message}");
                return null;
            }

            log.WriteLine($"warn: {label} 엔진 '{probe.Id}' 의 키({probe.ApiKeyEnv})가 없다. 이 티어를 끈다.");
            return null;
        }

        LlmEngineOptions head = chain[0];

        if (chain.Length == 1)
        {
            log.WriteLine($"{label}: {head.Id}");

            return new LlmPlanCompiler(
                data, prefix, head, ChatClientFactory.Create(head), stats, dryRun, reuse)
            {
                Moderator = moderator,
            };
        }

        var engines = new List<FailoverEngine>(chain.Length);

        foreach (LlmEngineOptions engine in chain)
        {
            engines.Add(new FailoverEngine(
                engine.Id, ChatClientFactory.Create(engine), new CircuitBreaker()));
        }

        var failover = new FailoverChatClient(
            engines,
            now,
            e => alarms.Raise(new AlarmPayload(
                AlarmKind.Failover,
                e.To is null ? AlarmSeverity.Critical : AlarmSeverity.Warning,
                e.From,
                e.To is null
                    ? $"{label} 체인이 전부 실패했다 ({e.From}: {e.Reason}). 티어 강등으로 간다"
                    : $"{label} 페일오버 {e.From} → {e.To} ({e.Reason}). "
                      + "프리픽스 캐시는 제공사별이라 미적중이 난다")));

        log.WriteLine($"{label}: 체인 {string.Join(" → ", failover.EngineIds)}");

        return new LlmPlanCompiler(data, prefix, head, failover, stats, dryRun, reuse)
        {
            Moderator = moderator,
        };
    }
}

/// <summary>
/// 인접 버킷 재사용 공급원. docs/12 §7.
///
/// <c>tools/Npc.Prebake</c> 의 같은 이름 타입과 같은 한 줄이다 — 구현이
/// <c>BucketNeighbors</c>(Npc.Planning)의 정적 메서드이고 <c>Npc.Llm</c> 은 그것을 참조하지 않으므로
/// (CLAUDE.md §3) 어댑터는 참조하는 쪽마다 필요하다.
/// </summary>
internal sealed class NeighborReuseSource(PlanStore store, MasterDataSet data) : IPlanReuseSource
{
    /// <inheritdoc />
    public bool TryReuse(BucketKey target, out CompiledPlan? plan, out BucketKey source) =>
        BucketNeighbors.TryReuse(target, store, data, out plan, out source);
}
