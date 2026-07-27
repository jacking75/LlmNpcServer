using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Npc.Contracts;
using Npc.Core;
using Npc.Gateway;
using Npc.Host;
using Npc.Host.Commands;
using Npc.Host.Metrics;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;
using Npc.Sim;

// ASP.NET 의 Microsoft.Extensions.Hosting.HostOptions 와 이름이 겹친다. 우리 것을 쓴다.
using HostOptions = Npc.Host.HostOptions;

// 조립 루트. docs/11 §11 · README §주요 실행 옵션.
//
// 여기가 유일하게 "전부를 아는" 곳이다. Npc.Runtime 은 Npc.Llm 도 Npc.Sim 도 모른다.
// 링크 구현체는 --link 로만 갈린다 — 아래 어느 코드도 구현체를 이름으로 알지 못한다.

if (args.Length > 0 && args[0] == ValidateCommand.Name)
{
    return ValidateCommand.Run(args[1..], Console.Out);
}

if (!HostOptions.TryParse(args, out HostOptions options, out string? parseError))
{
    Console.Error.WriteLine(parseError);
    Console.Error.WriteLine();
    Console.Error.WriteLine(HostOptions.Usage);
    return 2;
}

if (options.Help)
{
    Console.Out.WriteLine(HostOptions.Usage);
    return 0;
}

using var lifetime = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    lifetime.Cancel();
};

await using var host = NpcHost.Create(options, Console.Out);

if (options.NoDashboard)
{
    await host.RunAsync(lifetime.Token);
    host.Report(Console.Out);
    return 0;
}

WebApplicationBuilder builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls($"http://localhost:{options.Port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

WebApplication app = builder.Build();

app.MapGet("/", () => Results.Redirect("/dashboard"));
app.MapGet("/status", () => host.Snapshot());
app.MapGet("/metrics", () => host.Metrics.Snapshot());

// 대시보드 1차. docs/11 §10. 단일 HTML 이고 /metrics 를 폴링한다.
app.MapGet("/dashboard", () =>
{
    IFileInfo file = app.Environment.WebRootFileProvider.GetFileInfo("dashboard.html");

    return file.Exists
        ? Results.File(file.CreateReadStream(), "text/html; charset=utf-8")
        : Results.NotFound("dashboard.html 이 없다.");
});

await app.StartAsync(CancellationToken.None);
Console.Out.WriteLine($"dashboard: http://localhost:{options.Port}/dashboard");

await host.RunAsync(lifetime.Token);
host.Report(Console.Out);

await app.StopAsync(CancellationToken.None);
return 0;

/// <summary>
/// 조립된 NPC 서버 한 벌. docs/11 §11.
///
/// <b>Sim 은 틱 루프와 다른 스레드에서 돈다.</b> 게임서버 대역의 이벤트 채널은 단일 기록자라
/// 틱 루프가 <c>FlushAsync</c> 중에 직접 <c>ApplyCommand</c> 를 부르면 기록자가 둘이 된다.
/// 명령은 <see cref="CommandRing"/> 을 거쳐 드라이버 쪽에서 적용한다 — 실제 게임서버가
/// 자기 스레드에서 수신 패킷을 처리하는 것과 같은 모양이고, 덤으로 할당이 0 이다.
/// </summary>
internal sealed class NpcHost : IAsyncDisposable
{
    /// <summary>드라이버가 틱 루프보다 앞설 수 있는 틱 수. 1 이면 사실상 락스텝이다.</summary>
    private const int MaxLeadTicks = 1;

    private readonly HostOptions _options;
    private readonly IGameServerLink _link;
    private readonly NpcServerLoop _loop;
    private readonly GameClock _clock;
    private readonly PlanExecutor _executor;
    private readonly CognitionScheduler _cognition;
    private readonly InterruptMatcher _interrupts;
    private readonly ReplanQueue _replanQueue;
    private readonly NpcMeter _meter;
    private readonly SimDriver? _driver;
    private readonly NullGameServerLink? _nullLink;
    private readonly long _totalTicks;

    private NpcHost(
        HostOptions options,
        IGameServerLink link,
        NpcServerLoop loop,
        GameClock clock,
        PlanExecutor executor,
        CognitionScheduler cognition,
        InterruptMatcher interrupts,
        ReplanQueue replanQueue,
        NpcMeter meter,
        SimDriver? driver,
        NullGameServerLink? nullLink,
        long totalTicks,
        int npcs)
    {
        _options = options;
        _link = link;
        _loop = loop;
        _clock = clock;
        _executor = executor;
        _cognition = cognition;
        _interrupts = interrupts;
        _replanQueue = replanQueue;
        _meter = meter;
        _driver = driver;
        _nullLink = nullLink;
        _totalTicks = totalTicks;
        Npcs = npcs;
    }

    /// <summary>이 호스트가 돌리는 NPC 수.</summary>
    public int Npcs { get; }

    /// <summary>틱 루프. 게이트 러너가 통계를 읽는다.</summary>
    public NpcServerLoop Loop => _loop;

    /// <summary>
    /// 조립된 링크. 결정론 테스트가 재생 결과(<c>ReplayGameServerLink.Commands</c>)를 읽는다 —
    /// 재생은 명령을 파일로 내보내지 않고 메모리에만 모으기 때문이다.
    /// </summary>
    public IGameServerLink Link => _link;

    /// <summary>게임서버 대역. Loopback·Record 일 때만 있다.</summary>
    public SimDriver? Driver => _driver;

    /// <summary>계측. /metrics 와 게이트 러너가 읽는다.</summary>
    public NpcMeter Metrics => _meter;

    /// <summary>옵션대로 전부 조립한다. 기동 시 1회.</summary>
    public static NpcHost Create(HostOptions options, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        string masterDataDir = options.ResolveMasterData();
        MasterDataSet data = MasterDataLoader.Load(masterDataDir);

        string instancePath = Path.Combine(masterDataDir, "npc_instances.json");
        NpcInstanceTable instances = NpcInstanceTable.Load(instancePath, data);

        int npcs = Math.Min(options.Npcs, instances.Count);

        if (npcs < options.Npcs)
        {
            log.WriteLine($"warn: npc_instances.json 에 {instances.Count} 마리뿐이라 {npcs} 로 줄였다.");
        }

        // ── 런타임 ────────────────────────────────────────────────
        var store = new NpcStore();
        store.Allocate(npcs, data.Items.MaxCode + 1);

        var clock = new GameClock(data.Buckets, options.TimeScale);
        var correlations = new CorrelationTable(npcs);
        var applier = new EventApplier(data, store, clock, correlations);
        var emitter = new CommandEmitter(data, new PoiBinder(data.Pois));
        var swapper = new PlanSwapper(store);
        var replanQueue = new ReplanQueue(npcs);

        PlanStore plans = BuildPlanStore(options, data, masterDataDir, log, out int[] fallbackOf);

        var executor = new PlanExecutor(data, store, plans, correlations, emitter, options.TimeScale)
        {
            Swapper = swapper,
            ReplanQueue = replanQueue,
        };

        var bands = new LodBandSet(store);
        var cognition = new CognitionScheduler(store, bands, plans);
        var interrupts = new InterruptMatcher(data, store);

        // ── 인구 배치 ─────────────────────────────────────────────
        // --npcs 가 전체보다 작으면 균등 간격으로 뽑는다. 앞에서부터 자르면
        // 아키타입 code 순이라 대장장이·목수만 뽑히고 농부가 한 마리도 안 나온다.
        var chosen = new NpcInstanceDef[npcs];

        for (int i = 0; i < npcs; i++)
        {
            chosen[i] = instances[(int)((long)i * instances.Count / npcs)];
        }

        for (int i = 0; i < npcs; i++)
        {
            NpcInstanceDef def = chosen[i];

            applier.Seed(i, def.Home, def.Zone, def.Archetype, def.Home, def.Workplace);
            store.StepStatus[i] = (byte)StepStatus.Ready;
            executor.AssignPlan(i, new PlanId(fallbackOf[def.Archetype.Value]));
        }

        bands.Rebalance();

        // ── 링크 · 게임서버 대역 ──────────────────────────────────
        SimDriver? driver = null;
        NullGameServerLink? nullLink = null;
        IGameServerLink link;

        switch (options.Link)
        {
            case LinkKind.Null:
                nullLink = new NullGameServerLink();
                link = nullLink;
                break;

            case LinkKind.Replay:
                link = ReplayGameServerLink.Load(options.TracePath!);
                break;

            case LinkKind.Record:
            {
                driver = SimDriver.Create(options, data, chosen);
                string path = options.TracePath ?? Path.Combine("artifacts", "link.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                link = new RecordingGameServerLink(driver.Link, path);
                break;
            }

            case LinkKind.Loopback:
            default:
                driver = SimDriver.Create(options, data, chosen);
                link = driver.Link;
                break;
        }

        // 킬스위치를 실제 티어에 결선한다 (docs/15 §4). 대역이 없으면(Null·Replay)
        // 시나리오도 없으므로 아무것도 끊기지 않은 상태 그대로다.
        //
        // T1·T2 는 이 빌드의 런타임에 아직 없다 — P4 가 재계획 워커를 붙이면 라우터가
        // 같은 상태를 읽는다(TieredPlanCompiler). 지금 실제로 끊기는 것은 PlanStore 다.
        if (driver?.Scenario.Switches is { } switches)
        {
            plans.Switches = switches;
        }

        long totalTicks = options.Days == 0 ? 0 : clock.TicksForGameDays(options.Days);

        var loop = new NpcServerLoop(
            link, clock, applier, interrupts, cognition, executor, swapper, bands, replanQueue)
        {
            StopAtTick = totalTicks,
            Transition = new BucketTransition(store, plans, data),
        };

        var meter = new NpcMeter(
            store, bands, plans, replanQueue, cognition, interrupts, link, loop, clock, data);

        loop.Observer = meter;

        log.WriteLine(
            $"npcs {npcs} · link {options.Link} · time-scale {options.TimeScale} · "
            + $"days {options.Days} ({(totalTicks == 0 ? "무제한" : totalTicks + " ticks")}) · "
            + $"버킷 {plans.FilledBuckets}/{BucketKey.TotalKeys} · llm off (런타임 호출 없음)");

        return new NpcHost(
            options, link, loop, clock, executor, cognition, interrupts, replanQueue, meter,
            driver, nullLink, totalTicks, npcs);
    }

    /// <summary>드라이버와 틱 루프를 함께 돌린다. 둘 중 하나가 끝나면 정리한다.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Task pump = Task.Run(() => PumpAsync(ct), CancellationToken.None);
        Task loop = _loop.RunAsync(ct);

        try
        {
            await Task.WhenAll(pump, loop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C. 정상 종료다.
        }
    }

    /// <summary>대시보드·게이트가 읽는 현재 상태.</summary>
    public HostSnapshot Snapshot() => new(
        Tick: _clock.Current.Value,
        GameDay: _clock.GameDay,
        GameHour: _clock.GameHour,
        TimeOfDay: _clock.TimeOfDay.ToString(),
        Npcs: Npcs,
        TicksProcessed: _loop.TicksProcessed,
        EventsDrained: _loop.EventsDrained,
        CommandsEmitted: _executor.CommandsEmitted,
        StepsAdvanced: _executor.StepsAdvanced,
        TimeoutsSynthesized: _executor.TimeoutsSynthesized,
        ScanPerTick: _cognition.LastScanned,
        Deviations: _cognition.Deviations,
        InterruptsForced: _interrupts.Forced,
        ReplanQueued: _replanQueue.Count,
        EventBacklogs: _loop.EventBacklogs,
        LlmCalls: 0,
        Link: _link.Stats);

    /// <summary>종료 요약. 게이트 러너가 이 숫자를 본다.</summary>
    public void Report(TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(log);

        HostSnapshot s = Snapshot();
        MetricsSnapshot m = _meter.Snapshot();

        log.WriteLine(
            $"tick p50 {m.Tick.P50Ms:0.###}ms · p99 {m.Tick.P99Ms:0.###}ms · max {m.Tick.MaxMs:0.###}ms · "
            + $"overruns {m.Tick.Overruns} · gen0 {m.Tick.Gen0Collections} · "
            + $"bytes/tick {m.Tick.BytesPerTick} · heap {m.Tick.ManagedHeapMb}MB");

        // 드롭은 대시보드에만 있으면 안 된다 — 헤드리스로 돌린 사람은 못 본다.
        // link 는 발행 큐가 넘친 수, sim 은 게임서버 대역의 수신 링이 넘친 수다. 둘 다 0 이어야 정상이다.
        log.WriteLine(
            $"ticks {s.TicksProcessed} · game day {s.GameDay} · events {s.EventsDrained} · "
            + $"commands {s.CommandsEmitted} · steps {s.StepsAdvanced} · "
            + $"timeouts {s.TimeoutsSynthesized} · scan/tick {s.ScanPerTick} · "
            + $"interrupts {s.InterruptsForced} · replan-q {s.ReplanQueued} · "
            + $"drops link {s.Link.CommandsDropped} sim {_driver?.CommandsDropped ?? 0} · "
            + $"backlogs {s.EventBacklogs} · llm {s.LlmCalls}");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _meter.Dispose();

        await _link.DisposeAsync().ConfigureAwait(false);

        if (_driver is not null)
        {
            await _driver.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 폴백 40개 + 프리베이크된 버킷 플랜을 스토어에 올린다. docs/13 §2.
    ///
    /// <b>폴백을 먼저 등록한다.</b> 버킷이 하나도 없어도 <c>Resolve</c> 가 유효한 플랜을 주는 것은
    /// 폴백 때문이고, 그것이 시나리오 C(LLM 전면 차단)가 통과하는 이유다 (CLAUDE.md §2.6).
    /// 버킷 로드는 그 위에 얹는다 — 미생성 버킷은 그대로 폴백으로 해소된다.
    ///
    /// 마스터데이터가 스토어보다 새로우면 <b>경고만</b> 하고 계속 간다. 기동을 막지 않는 이유는
    /// 낡은 플랜이라도 폴백보다는 나은 경우가 있고, 재생성 판단은 사람이 할 일이기 때문이다.
    /// </summary>
    private static PlanStore BuildPlanStore(
        HostOptions options, MasterDataSet data, string masterDataDir, TextWriter log, out int[] fallbackOf)
    {
        PlanStore plans = PlanStore.CreateIdleOnly(data);
        fallbackOf = new int[data.Archetypes.Count];

        if (data.Fallbacks is { } table)
        {
            foreach (FallbackPlanEntry entry in table.Plans)
            {
                PlanId id = plans.Register(entry.Plan);

                plans.SetFallback(entry.Archetype, id);
                fallbackOf[entry.Archetype.Value] = id.Value;
            }
        }

        string storeDir = options.ResolvePlanStore();

        if (!Directory.Exists(storeDir))
        {
            log.WriteLine($"planstore 없음 ({storeDir}) — 폴백 {plans.FilledFallbacks}개로 돈다.");
            return plans;
        }

        long started = Stopwatch.GetTimestamp();
        PlanStoreLoadReport report = PlanStoreIo.LoadAll(storeDir, plans, data);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;

        log.WriteLine(
            $"planstore {storeDir} · 버킷 {report.Total}/{BucketKey.TotalKeys} "
            + $"(pinned {report.Pinned}) · 폴백 {plans.FilledFallbacks} · {elapsed:0.###}s");

        if (report.Skipped + report.Failed > 0)
        {
            log.WriteLine($"warn: 플랜 {report.Skipped + report.Failed}건을 못 올렸다.");

            foreach (string error in report.Errors.Take(5))
            {
                log.WriteLine($"  {error}");
            }
        }

        if (plans.ColdBuckets > 0)
        {
            log.WriteLine($"warn: 미생성 버킷 {plans.ColdBuckets}건. 아키타입 폴백으로 해소된다.");
        }

        WarnIfStale(storeDir, data, masterDataDir, log);

        return plans;
    }

    /// <summary>
    /// 마스터데이터·프롬프트가 스토어보다 새로우면 경고한다. 판정은 T3-06 이 한다.
    ///
    /// 프리픽스는 여기서만 조립한다 — <c>Npc.Runtime</c> 은 <c>Npc.Llm</c> 을 모른다 (CLAUDE.md §3).
    /// </summary>
    private static void WarnIfStale(
        string storeDir, MasterDataSet data, string masterDataDir, TextWriter log)
    {
        Manifest? manifest = Manifest.LoadFrom(storeDir);

        if (manifest is null)
        {
            log.WriteLine("warn: manifest.json 이 없다. 스토어가 지금 마스터데이터로 만들어진 것인지 알 수 없다.");
            return;
        }

        InvalidationScope scope = PlanStoreValidator.Compare(
            manifest,
            data,
            PromptPrefix.Build(data, masterDataDir).Sha256,
            out ImmutableArray<string> changed);

        if (scope == InvalidationScope.None)
        {
            return;
        }

        log.WriteLine(
            $"warn: 플랜 스토어가 낡았다 — 무효화 {scope}"
            + (changed.IsEmpty ? string.Empty : $" (바뀐 것: {string.Join(", ", changed)})")
            + ". tools/Npc.Prebake 로 재생성한다.");
    }

    /// <summary>
    /// 세계를 한 틱씩 민다. 게임서버가 없을 때(Null) 는 TickSync 만,
    /// Loopback·Record 면 Sim 전체를 돌린다. Replay 는 파일이 이미 이벤트를 갖고 있어 아무것도 안 한다.
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        if (_driver is null && _nullLink is null)
        {
            return;   // Replay — 채널에 이미 다 들어 있다
        }

        // 벽시계는 호스트 쪽에만 있다. 게임 로직은 여전히 Tick 만 본다 (CLAUDE.md §2.3).
        var stopwatch = Stopwatch.StartNew();
        long tick = 0;

        while (!ct.IsCancellationRequested && (_totalTicks == 0 || tick < _totalTicks))
        {
            tick++;

            var now = new Tick(tick);

            // 틱 루프보다 앞서 나가면 안 된다. 앞서면 명령이 나오기도 전에 세계가 다음 틱으로
            // 넘어가 버려서 응답 이벤트가 하나도 돌아오지 않는다 (전부 타임아웃 합성이 된다).
            //
            // 기다리는 것을 <b>세계를 밀기 전으로</b> 옮겼다. 밀고 나서 기다리면 tick N+1 의
            // 이벤트가 이미 채널에 들어간 뒤라, 틱 루프가 N 을 돌리는 동안 N+1 이 섞여 들어온다.
            // 기준도 TicksProcessed 가 아니라 TicksCommitted 다 — 처리만 끝나고 명령이 아직
            // 링크 큐에 있는 순간에 세계를 밀면 어떤 명령이 반영되는지가 스레드 스케줄에 달린다.
            // 이 둘이 docs/15 §3 의 "리플레이 100% 일치"가 성립하기 위한 조건이다.
            var spin = new SpinWait();

            while (tick - _loop.TicksCommitted > MaxLeadTicks && !ct.IsCancellationRequested)
            {
                // sleep1Threshold: -1 — Thread.Sleep(1) 로 넘어가지 않게 한다.
                // 락스텝이라 매 틱 한 번은 기다리게 되는데, 여기서 1ms 를 자면
                // 1,440틱짜리 하루가 그것만으로 20초를 더 쓴다 (실측 1분 39초 → 9분 8초).
                spin.SpinOnce(sleep1Threshold: -1);
            }

            if (_driver is not null)
            {
                _driver.Tick(now);
            }
            else
            {
                _nullLink!.PushTick(now);
            }

            if (_options.MaxSpeed)
            {
                continue;   // 측정용 — 페이싱 없이 루프 속도에 맞춰 민다
            }

            long due = tick * 1_000 / NpcServerLoop.TickHz;
            long wait = due - stopwatch.ElapsedMilliseconds;

            if (wait > 0)
            {
                await Task.Delay((int)wait, ct).ConfigureAwait(false);
            }
        }

        // 채널을 닫아야 틱 루프의 WaitToReadAsync 가 풀린다.
        if (_driver is not null)
        {
            await _driver.CompleteAsync().ConfigureAwait(false);
        }
        else
        {
            await _nullLink!.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>대시보드·게이트가 읽는 상태 한 장. docs/11 §10.</summary>
/// <param name="Tick">현재 틱.</param>
/// <param name="GameDay">게임 일수.</param>
/// <param name="GameHour">게임 시각(0~23).</param>
/// <param name="TimeOfDay">시간대 이름.</param>
/// <param name="Npcs">NPC 수.</param>
/// <param name="TicksProcessed">처리한 틱 수.</param>
/// <param name="EventsDrained">배수한 이벤트 수.</param>
/// <param name="CommandsEmitted">발행한 명령 수.</param>
/// <param name="StepsAdvanced">전진한 스텝 수.</param>
/// <param name="TimeoutsSynthesized">합성한 타임아웃 수.</param>
/// <param name="ScanPerTick">마지막 틱의 인지 스캔 수.</param>
/// <param name="Deviations">이탈로 판정해 재계획 큐에 넣은 수.</param>
/// <param name="InterruptsForced">인터럽트가 즉시 발행한 액션 수.</param>
/// <param name="ReplanQueued">재계획 큐 깊이.</param>
/// <param name="EventBacklogs">한 틱 상한에 걸려 다음 틱으로 넘긴 횟수.</param>
/// <param name="LlmCalls">LLM 호출 수. P1 에서는 항상 0 이다.</param>
/// <param name="Link">링크 통계.</param>
internal readonly record struct HostSnapshot(
    long Tick,
    long GameDay,
    int GameHour,
    string TimeOfDay,
    int Npcs,
    long TicksProcessed,
    long EventsDrained,
    long CommandsEmitted,
    long StepsAdvanced,
    long TimeoutsSynthesized,
    int ScanPerTick,
    long Deviations,
    long InterruptsForced,
    int ReplanQueued,
    long EventBacklogs,
    long LlmCalls,
    LinkStats Link);

/// <summary>게임서버 대역 한 벌. Sim 하위 시뮬을 조립하고 틱마다 민다.</summary>
internal sealed class SimDriver : IAsyncDisposable
{
    private readonly SimWorld _world;
    private readonly MovementSim _movement;
    private readonly InteractionSim _interaction;
    private readonly TransformEmitter _transforms;
    private readonly NeedsSim _needs;
    private readonly PlayerBots _bots;
    private readonly ScenarioRunner _scenario;
    private readonly CommandRing _inbox;

    private SimDriver(
        SimWorld world,
        MovementSim movement,
        InteractionSim interaction,
        TransformEmitter transforms,
        NeedsSim needs,
        PlayerBots bots,
        ScenarioRunner scenario,
        CommandRing inbox,
        LoopbackGameServerLink link)
    {
        _world = world;
        _movement = movement;
        _interaction = interaction;
        _transforms = transforms;
        _needs = needs;
        _bots = bots;
        _scenario = scenario;
        _inbox = inbox;
        Link = link;
    }

    /// <summary>NPC 서버가 물리는 링크.</summary>
    public LoopbackGameServerLink Link { get; }

    /// <summary>월드.</summary>
    public SimWorld World => _world;

    /// <summary>시나리오 러너. 호스트가 킬스위치 상태를 여기서 꺼내 결선한다 (docs/15 §4).</summary>
    public ScenarioRunner Scenario => _scenario;

    /// <summary>링이 가득 차 버린 명령 수. 0 이 아니면 Sim 이 밀린 것이다.</summary>
    public long CommandsDropped => _inbox.Dropped;

    /// <summary>조립한다.</summary>
    public static SimDriver Create(HostOptions options, MasterDataSet data, NpcInstanceDef[] npcs)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(npcs);

        var world = new SimWorld(data, npcs.Length, new SimOptions(
            Seed: options.Seed,
            TimeScale: options.TimeScale,
            FailRate: options.FailRate,
            DropRate: options.DropRate,
            PlayerBots: options.PlayerBots));

        for (int i = 0; i < npcs.Length; i++)
        {
            world.Place(i, npcs[i].Archetype, npcs[i].Home);
        }

        var movement = new MovementSim(world);
        var interaction = new InteractionSim(world);
        var faults = new FaultInjector(
            world,
            (in NpcCommand c, Tick t) => movement.TryHandle(in c, t) || interaction.TryHandle(in c, t));

        world.Handler = faults.TryHandle;

        // 인구를 세계에 올린다. 스폰되지 않은 NPC 는 PlayerBots 도 MovementSim 도 건너뛴다.
        // 실제 게임서버라면 접속·로딩이 하는 일이고, 대역에서는 기동 시 한 번에 한다.
        for (int i = 0; i < npcs.Length; i++)
        {
            var spawn = new NpcCommand
            {
                Kind = NpcCommandKind.Spawn,
                Npc = new NpcId(i),
                IssuedAt = default,
                Correlation = default,
                Priority = CommandPriority.Critical,
                Archetype = npcs[i].Archetype,
                Zone = npcs[i].Zone,
                TargetPoi = npcs[i].Home,
            };

            world.ApplyCommand(in spawn, default);
        }

        var inbox = new CommandRing();

        // 링크 큐는 <b>한 틱치 명령</b>을 담아야 한다. 소비자(FlushAsync)가 매 틱 전량을 비우므로
        // 여기서 넘치는 것은 역압(docs/02 §1)이 아니라 사이징 실수다 —
        // 틱 0 에 전원이 첫 스텝을 내면 npcs × MaxCommandsPerStep 이 한 번에 들어온다.
        // 기본값 4,096 을 그대로 두면 NPC 5,000 에서 첫 틱에 904건이 버려지고
        // 그 NPC 들은 timeout_s 가 만료될 때까지 첫 걸음을 못 뗀다.
        int linkCapacity = Math.Max(
            LoopbackGameServerLink.DefaultCapacity,
            npcs.Length * CommandEmitter.MaxCommandsPerStep);

        return new SimDriver(
            world,
            movement,
            interaction,
            new TransformEmitter(world, movement),
            new NeedsSim(world),
            new PlayerBots(world),
            options.Scenario is { } path ? ScenarioRunner.Load(path, data) : ScenarioRunner.Empty,
            inbox,
            new LoopbackGameServerLink(inbox.Enqueue, world.Events, linkCapacity));
    }

    /// <summary>한 틱. 받은 명령을 적용하고 하위 시뮬을 민 뒤 TickSync 를 낸다.</summary>
    public void Tick(Tick now)
    {
        while (_inbox.TryDequeue(out NpcCommand command))
        {
            _world.ApplyCommand(in command, now);
        }

        _scenario.Tick(now, _world);
        _bots.Tick(now);
        _movement.Tick(now);
        _interaction.Tick(now);
        _transforms.Tick(now);
        _needs.Tick(now);
        _world.Tick(now);
    }

    /// <summary>이벤트 스트림을 닫는다. 틱 루프가 여기서 풀려 나온다.</summary>
    public ValueTask CompleteAsync() => _world.DisposeAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Link.DisposeAsync().ConfigureAwait(false);
        await _world.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 단일 생산자(틱 루프) · 단일 소비자(Sim 드라이버) 링 버퍼.
///
/// <c>ConcurrentQueue</c> 를 쓰지 않는 이유는 하나다 — <c>FlushAsync</c> 는 틱 루프 안에서
/// 불리므로 할당이 있으면 안 된다 (CLAUDE.md §2.1). 여기는 미리 잡은 배열뿐이다.
/// </summary>
internal sealed class CommandRing
{
    private const int Capacity = 1 << 16;   // 5,000 NPC 한 틱치를 넉넉히 담는다
    private const int Mask = Capacity - 1;

    private readonly NpcCommand[] _slots = new NpcCommand[Capacity];
    private long _head;   // 소비자
    private long _tail;   // 생산자

    /// <summary>버린 명령 수. 링이 가득 찼다면 Sim 이 밀린 것이다.</summary>
    public long Dropped { get; private set; }

    /// <summary>틱 루프에서 부른다. 할당 0.</summary>
    public void Enqueue(in NpcCommand command, Tick now)
    {
        _ = now;

        long tail = _tail;

        if (tail - Volatile.Read(ref _head) >= Capacity)
        {
            Dropped++;
            return;   // 명령은 유실될 수 있다 (docs/02 §1). 타임아웃 합성이 받아준다
        }

        _slots[tail & Mask] = command;
        Volatile.Write(ref _tail, tail + 1);
    }

    /// <summary>드라이버에서 부른다.</summary>
    public bool TryDequeue(out NpcCommand command)
    {
        long head = _head;

        if (head >= Volatile.Read(ref _tail))
        {
            command = default;
            return false;
        }

        command = _slots[head & Mask];
        Volatile.Write(ref _head, head + 1);
        return true;
    }
}
