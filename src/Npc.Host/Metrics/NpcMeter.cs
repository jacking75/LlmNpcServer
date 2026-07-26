using System.Diagnostics;
using System.Diagnostics.Metrics;
using Npc.Contracts;
using Npc.Core.Plan;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Host.Metrics;

/// <summary>액션 하나의 실행 중 개체 수. 대시보드 "액션" 패널.</summary>
/// <param name="Action">액션 id.</param>
/// <param name="Count">지금 그 스텝에 있는 NPC 수.</param>
public readonly record struct ActionCount(string Action, int Count);

/// <summary>틱 패널. docs/11 §10.</summary>
/// <param name="P50Ms">틱 실행 시간 p50(ms).</param>
/// <param name="P99Ms">틱 실행 시간 p99(ms).</param>
/// <param name="MaxMs">관측 창 안의 최댓값(ms).</param>
/// <param name="Overruns">예산 20ms 를 넘긴 틱 수.</param>
/// <param name="Ticks">처리한 틱 수.</param>
/// <param name="GameDay">게임 일수.</param>
/// <param name="GameHour">게임 시각(0~23).</param>
/// <param name="TimeOfDay">시간대.</param>
/// <param name="Gen0Collections">기동 이후 Gen0 컬렉션 수.</param>
/// <param name="BytesPerTick">틱 하나가 할당한 평균 바이트. 0 이 목표다.</param>
/// <param name="ManagedHeapMb">관리 힙(MB).</param>
public readonly record struct TickPanel(
    double P50Ms,
    double P99Ms,
    double MaxMs,
    long Overruns,
    long Ticks,
    long GameDay,
    int GameHour,
    string TimeOfDay,
    int Gen0Collections,
    long BytesPerTick,
    double ManagedHeapMb);

/// <summary>NPC 패널. docs/11 §10.</summary>
/// <param name="Total">총원.</param>
/// <param name="ByLod">LOD 밴드별 인원. 첨자 = LOD.</param>
/// <param name="ByStepStatus">
/// 실행 상태별 인원. 첨자 = <see cref="StepStatus"/>.
/// P1 의 NpcStore 는 VisualState 를 들고 있지 않다 — 연출 상태는 게임서버 소관이라
/// NPC 서버에는 미러가 없다. 대신 스텝 실행 상태를 보여준다 (docs/11 §10).
/// </param>
/// <param name="BandMigrations">밴드 이동 누적.</param>
public readonly record struct NpcPanel(
    int Total,
    int[] ByLod,
    int[] ByStepStatus,
    long BandMigrations);

/// <summary>
/// 링크 패널. docs/11 §10.
///
/// 수신 이벤트와 시퀀스 갭은 링크가 아니라 틱 루프에서 온다 — 루프백 링크는 이벤트 리더를
/// 그대로 넘겨주므로 세지 못한다. 모든 이벤트가 지나는 곳은 <c>NpcServerLoop.DrainEvents</c> 뿐이다.
/// </summary>
/// <param name="CommandsEnqueued">큐에 넣은 명령.</param>
/// <param name="CommandsFlushed">실제로 송출한 명령.</param>
/// <param name="CommandsDropped">역압으로 버린 명령.</param>
/// <param name="PendingCommands">아직 안 나간 명령.</param>
/// <param name="EventsDrained">배수한 이벤트.</param>
/// <param name="EventGaps">시퀀스 갭 (N6).</param>
/// <param name="EventBacklogs">한 틱 상한에 걸려 다음 틱으로 넘긴 횟수.</param>
public readonly record struct LinkPanel(
    long CommandsEnqueued,
    long CommandsFlushed,
    long CommandsDropped,
    int PendingCommands,
    long EventsDrained,
    long EventGaps,
    long EventBacklogs);

/// <summary>재계획 패널. docs/11 §10.</summary>
/// <param name="QueueDepth">큐 깊이.</param>
/// <param name="EnqueuedPerSecond">초당 유입.</param>
/// <param name="Deviations">이탈 판정 누적.</param>
/// <param name="ScanPerTick">마지막 틱의 인지 스캔 수.</param>
/// <param name="InterruptsForced">인터럽트가 즉시 발행한 액션 수.</param>
/// <param name="InterruptsSuppressed">같은 규칙이 이어서 걸려 넘긴 수.</param>
/// <param name="QueueDropped">큐가 포화해 버린 요청 수. docs/14 §6 의 "큐: 거절 수".</param>
public readonly record struct ReplanPanel(
    int QueueDepth,
    double EnqueuedPerSecond,
    long Deviations,
    int ScanPerTick,
    long InterruptsForced,
    long InterruptsSuppressed,
    long QueueDropped = 0);

/// <summary>미스가 많은 버킷 하나. docs/13 §6 의 <c>top_miss</c>.</summary>
/// <param name="Bucket"><c>blacksmith@Dawn.Peace.Fair</c> 표기.</param>
/// <param name="Misses">미스 수.</param>
public readonly record struct BucketMissRow(string Bucket, long Misses);

/// <summary>아키타입 하나의 캐시 성적. docs/13 §6.</summary>
/// <param name="Archetype">아키타입 id 문자열.</param>
/// <param name="HitRate">히트율.</param>
/// <param name="Hits">히트 수.</param>
/// <param name="Misses">미스 수.</param>
/// <param name="ColdBuckets">72버킷 중 미생성 수.</param>
public readonly record struct ArchetypeCacheRow(
    string Archetype,
    double HitRate,
    long Hits,
    long Misses,
    int ColdBuckets);

/// <summary>
/// 캐시 패널. docs/13 §6 의 4개 지표 + 아키타입별 분해. 패널을 그리는 것은 T4-18 이다.
/// </summary>
/// <param name="HitRate">전체 히트율. P3 게이트는 시나리오 A 에서 ≥ 0.98 이다.</param>
/// <param name="Hits">버킷 히트 누계.</param>
/// <param name="Misses">폴백으로 해소된 미스 누계.</param>
/// <param name="FilledBuckets">채워진 버킷 수 (2,880 중).</param>
/// <param name="ColdBuckets">미생성 버킷 수. 0 이 아니면 <c>--resume</c> 으로 마저 생성한다.</param>
/// <param name="PinnedBuckets">사람이 고정한 버킷 수.</param>
/// <param name="IndividualTurnover">개별 플랜 풀 회전율. 크면 개별 재계획이 과다하다.</param>
/// <param name="IndividualLive">살아 있는 개별 플랜 수.</param>
/// <param name="TopMisses">미스 상위 10 버킷.</param>
/// <param name="WorstArchetypes">히트율 최하위 아키타입 10종 (조회가 있었던 것만).</param>
public readonly record struct CachePanel(
    double HitRate,
    long Hits,
    long Misses,
    int FilledBuckets,
    int ColdBuckets,
    int PinnedBuckets,
    double IndividualTurnover,
    int IndividualLive,
    BucketMissRow[] TopMisses,
    ArchetypeCacheRow[] WorstArchetypes);

/// <summary>대시보드가 폴링하는 /metrics 한 장. docs/11 §10 의 5패널 + docs/13 §6 의 캐시 패널.</summary>
/// <param name="Tick">틱 패널.</param>
/// <param name="Npc">NPC 패널.</param>
/// <param name="Actions">액션 Top 10.</param>
/// <param name="Link">링크 패널 (N6 시퀀스 갭 포함).</param>
/// <param name="Replan">재계획 패널.</param>
/// <param name="Cache">플랜 캐시 패널 (docs/13 §6).</param>
/// <param name="LlmCalls">
/// LLM 호출 수 (재시도 포함). 컴파일 계측기가 붙어 있지 않으면 0 이다 —
/// <c>--no-llm</c> 으로 도는 P1 경로가 그렇다.
/// </param>
public readonly record struct MetricsSnapshot(
    TickPanel Tick,
    NpcPanel Npc,
    ActionCount[] Actions,
    LinkPanel Link,
    ReplanPanel Replan,
    CachePanel Cache,
    long LlmCalls);

/// <summary>
/// 호스트 계측. docs/11 §9 · §10.
///
/// <b>시계는 여기에만 있다.</b> <see cref="ITickObserver"/> 가 존재하는 이유가 이것이다 —
/// 게임 로직이 <c>Stopwatch</c> 를 보면 리플레이가 깨진다 (CLAUDE.md §2.3).
/// 측정값은 게임 상태에 되먹임되지 않는다. 읽기만 한다.
///
/// <c>Meter</c> 계측기(OpenTelemetry 수집용)와 <c>/metrics</c> JSON 한 장을 같이 낸다.
/// 대시보드는 후자만 쓴다 — 브라우저 하나 띄우자고 수집기를 세울 이유가 없다.
/// </summary>
internal sealed class NpcMeter : ITickObserver, IDisposable
{
    /// <summary>계측기 이름. OpenTelemetry 수집 시 이 이름으로 건다.</summary>
    public const string MeterName = "Npc.Server";

    /// <summary>백분위를 계산할 관측 창(틱 수). 2의 거듭제곱.</summary>
    public const int Window = 4_096;

    private readonly Meter _meter;
    private readonly Histogram<double> _tickDuration;
    private readonly Counter<long> _overruns;

    private readonly NpcStore _store;
    private readonly LodBandSet _bands;
    private readonly PlanStore _plans;
    private readonly ReplanQueue _replanQueue;
    private readonly CognitionScheduler _cognition;
    private readonly InterruptMatcher _interrupts;
    private readonly IGameServerLink _link;
    private readonly NpcServerLoop _loop;
    private readonly GameClock _clock;
    private readonly MasterDataSet _data;
    private readonly CompileStatsCollector? _compile;
    private readonly CacheMetrics _cache;

    private readonly double[] _samples = new double[Window];
    private readonly double[] _sorted = new double[Window];

    // 큐 깊이는 틱마다 찍는다 — docs/14 §6 의 "큐: 깊이 p50/p99".
    // 대시보드 폴링 간격으로 재면 스파이크를 통째로 놓친다.
    private readonly int[] _queueDepths = new int[Window];
    private readonly int[] _queueSorted = new int[Window];
    private readonly int[] _byLod = new int[LodBandSet.BandCount];
    private readonly int[] _byStatus = new int[8];
    private readonly int[] _byAction;

    private readonly long _gen0AtStart = GC.CollectionCount(0);
    private readonly double _ticksPerMs = Stopwatch.Frequency / 1_000.0;

    private long _tickStartTimestamp;
    private long _tickStartAllocated;
    private long _samplesWritten;
    private long _overrunCount;
    private long _allocatedInTicks;
    private long _replanEnqueuedAtWindowStart;
    private long _windowStartTick;

    /// <summary>계측을 건다. 기동 시 1회.</summary>
    public NpcMeter(
        NpcStore store,
        LodBandSet bands,
        PlanStore plans,
        ReplanQueue replanQueue,
        CognitionScheduler cognition,
        InterruptMatcher interrupts,
        IGameServerLink link,
        NpcServerLoop loop,
        GameClock clock,
        MasterDataSet data,
        CompileStatsCollector? compile = null,
        CacheMetrics? cache = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(replanQueue);
        ArgumentNullException.ThrowIfNull(cognition);
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(data);

        _store = store;
        _bands = bands;
        _plans = plans;
        _replanQueue = replanQueue;
        _cognition = cognition;
        _interrupts = interrupts;
        _link = link;
        _loop = loop;
        _clock = clock;
        _data = data;
        _compile = compile;

        // 카운터는 PlanStore·IndividualPlanPool 이 들고 있다. 계측기만 여기서 만든다 (docs/13 §6).
        _cache = cache ?? new CacheMetrics(plans);
        _byAction = new int[data.Actions.MaxCode + 1];

        _meter = new Meter(MeterName);

        _tickDuration = _meter.CreateHistogram<double>(
            "npc.tick.duration", unit: "ms", description: "틱 실행 시간");
        _overruns = _meter.CreateCounter<long>(
            "npc.tick.overruns", description: $"예산 {NpcServerLoop.TickBudgetMs}ms 를 넘긴 틱");

        _meter.CreateObservableGauge("npc.count", () => _store.Count, description: "NPC 총원");
        _meter.CreateObservableGauge(
            "npc.cognition.scan_per_tick", () => _cognition.LastScanned, description: "인지 스캔 대상/틱");
        _meter.CreateObservableGauge(
            "npc.replan.queue_depth", () => _replanQueue.Count, description: "재계획 큐 깊이");
        _meter.CreateObservableGauge(
            "npc.gc.gen0", () => GC.CollectionCount(0) - _gen0AtStart, description: "기동 이후 Gen0 컬렉션");
        _meter.CreateObservableGauge(
            "npc.gc.heap_mb", () => GC.GetTotalMemory(false) / (1024.0 * 1024.0), description: "관리 힙(MB)");

        _meter.CreateObservableCounter(
            "npc.link.commands_flushed", () => _link.Stats.CommandsFlushed, description: "송출한 명령");
        _meter.CreateObservableCounter(
            "npc.link.commands_dropped", () => _link.Stats.CommandsDropped, description: "버린 명령");
        _meter.CreateObservableCounter(
            "npc.link.events_drained", () => _loop.EventsDrained, description: "배수한 이벤트");
        _meter.CreateObservableCounter(
            "npc.link.event_gaps", () => _loop.EventGaps, description: "시퀀스 갭 (N6)");

        // ── 플랜 캐시. docs/13 §6 의 4개 지표 ──
        //
        // 상위 미스 버킷은 게이지 하나에 담을 수 없어(값이 배열이다) 1위의 미스 수만 낸다.
        // 목록 전체는 /metrics 의 캐시 패널에 있다.
        _meter.CreateObservableGauge(
            "npc.plan.cache.hit_ratio", () => _cache.HitRate, description: "버킷 캐시 히트율. 게이트는 ≥ 0.98");
        _meter.CreateObservableGauge(
            "npc.plan.cache.cold_buckets", () => _cache.ColdBuckets, description: "미생성 버킷 수 (2,880 중)");
        _meter.CreateObservableGauge(
            "npc.plan.cache.top_miss",
            () => _cache.TopMisses(1) is [{ Misses: var top }, ..] ? top : 0,
            description: "가장 많이 미스한 버킷의 미스 수");
        _meter.CreateObservableGauge(
            "npc.plan.individual.turnover",
            () => _cache.IndividualTurnover,
            description: "개별 플랜 풀 회전율. 크면 개별 재계획이 과다하다");

        // 프리픽스 해시가 2종 이상이면 프롬프트 캐시가 깨진 것이다 (docs/01 §10.2).
        // 상시 감시 대상이라 대시보드가 아니라 계측기에 둔다.
        _meter.CreateObservableGauge(
            "npc.llm.unique_prefix_hashes",
            () => _compile?.UniquePrefixHashes ?? 0,
            description: "관측된 프리픽스 SHA 종류 수. 1 이 아니면 경보");
    }

    /// <summary>예산을 넘긴 틱 수.</summary>
    public long Overruns => _overrunCount;

    /// <summary>기동 이후 Gen0 컬렉션 수. 틱 루프에서 0 이어야 한다 (docs/11 §9).</summary>
    public int Gen0Collections => (int)(GC.CollectionCount(0) - _gen0AtStart);

    /// <summary>틱 루프 안에서 할당한 총 바이트. 0 이 목표다.</summary>
    public long AllocatedInTicks => _allocatedInTicks;

    /// <summary>관측 창의 재계획 큐 깊이 백분위. docs/14 §6 의 기록 지표다.</summary>
    public int QueueDepthPercentile(double q)
    {
        int count = (int)Math.Min(_samplesWritten, Window);

        if (count == 0)
        {
            return 0;
        }

        Array.Copy(_queueDepths, _queueSorted, count);
        Array.Sort(_queueSorted, 0, count);

        return _queueSorted[Math.Clamp((int)Math.Ceiling(q * count) - 1, 0, count - 1)];
    }

    /// <summary>관측 창의 백분위(ms).</summary>
    public double Percentile(double q)
    {
        int count = (int)Math.Min(_samplesWritten, Window);

        if (count == 0)
        {
            return 0;
        }

        Array.Copy(_samples, _sorted, count);
        Array.Sort(_sorted, 0, count);

        int index = Math.Clamp((int)Math.Ceiling(q * count) - 1, 0, count - 1);
        return _sorted[index];
    }

    /// <inheritdoc />
    public void OnTickBegin(Tick tick)
    {
        _ = tick;

        _tickStartAllocated = GC.GetAllocatedBytesForCurrentThread();
        _tickStartTimestamp = Stopwatch.GetTimestamp();
    }

    /// <inheritdoc />
    public void OnTickEnd(Tick tick, int scanned, int drained)
    {
        _ = scanned;
        _ = drained;

        double ms = (Stopwatch.GetTimestamp() - _tickStartTimestamp) / _ticksPerMs;

        // 같은 스레드에서 시작·종료하므로 델타가 이 틱의 할당이다.
        long allocated = GC.GetAllocatedBytesForCurrentThread() - _tickStartAllocated;

        if (allocated > 0)
        {
            _allocatedInTicks += allocated;
        }

        _queueDepths[(int)(_samplesWritten & (Window - 1))] = _replanQueue.Count;
        _samples[(int)(_samplesWritten & (Window - 1))] = ms;
        _samplesWritten++;

        _tickDuration.Record(ms);

        if (ms > NpcServerLoop.TickBudgetMs)
        {
            _overrunCount++;
            _overruns.Add(1);
        }

        if (_windowStartTick == 0)
        {
            _windowStartTick = tick.Value;
            _replanEnqueuedAtWindowStart = _replanQueue.TotalEnqueued;
        }
    }

    /// <summary>대시보드가 폴링하는 한 장. 5패널 전부 여기서 나온다.</summary>
    public MetricsSnapshot Snapshot()
    {
        Array.Clear(_byLod);
        Array.Clear(_byStatus);
        Array.Clear(_byAction);

        for (int i = 0; i < _store.Count; i++)
        {
            _byLod[Math.Clamp((int)_store.Lod[i], 0, LodBandSet.BandCount - 1)]++;

            byte status = _store.StepStatus[i];
            _byStatus[Math.Min(status, _byStatus.Length - 1)]++;

            if ((StepStatus)status is StepStatus.Unspawned or StepStatus.Done)
            {
                continue;
            }

            // 계측은 상태를 바꾸지 않는다 — LRU 도 건드리지 않고, 회수된 개별 슬롯이면
            // 최후 플랜으로 세고 넘어간다.
            _plans.TryPeekFor(i, _store.PlanId[i], out CompiledPlan plan);

            int step = Math.Min(_store.StepIndex[i], plan.Steps.Length - 1);

            if (step >= 0)
            {
                _byAction[plan.Steps[step].Action.Value]++;
            }
        }

        long ticks = _samplesWritten;
        long elapsedTicks = Math.Max(1, _clock.Current.Value - _windowStartTick);
        double seconds = (double)elapsedTicks / Tick.PerSecond;

        return new MetricsSnapshot(
            Tick: new TickPanel(
                P50Ms: Math.Round(Percentile(0.50), 3),
                P99Ms: Math.Round(Percentile(0.99), 3),
                MaxMs: Math.Round(Percentile(1.0), 3),
                Overruns: _overrunCount,
                Ticks: ticks,
                GameDay: _clock.GameDay,
                GameHour: _clock.GameHour,
                TimeOfDay: _clock.TimeOfDay.ToString(),
                Gen0Collections: Gen0Collections,
                BytesPerTick: ticks == 0 ? 0 : _allocatedInTicks / ticks,
                ManagedHeapMb: Math.Round(GC.GetTotalMemory(false) / (1024.0 * 1024.0), 1)),
            Npc: new NpcPanel(
                Total: _store.Count,
                ByLod: [.. _byLod],
                ByStepStatus: [.. _byStatus],
                BandMigrations: _bands.Migrations),
            Actions: TopActions(10),
            Link: LinkOf(_link.Stats),
            Replan: new ReplanPanel(
                QueueDepth: _replanQueue.Count,
                EnqueuedPerSecond: Math.Round(
                    (_replanQueue.TotalEnqueued - _replanEnqueuedAtWindowStart) / seconds, 2),
                Deviations: _cognition.Deviations,
                ScanPerTick: _cognition.LastScanned,
                InterruptsForced: _interrupts.Forced,
                InterruptsSuppressed: _interrupts.Suppressed,
                QueueDropped: _replanQueue.Dropped),
            Cache: CacheOf(_cache.Snapshot()),
            LlmCalls: _compile?.Calls ?? 0);
    }

    /// <summary>캐시 계측. 게이트 러너가 히트율을 여기서 읽는다.</summary>
    public CacheMetrics Cache => _cache;

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();

    /// <summary>
    /// 버킷 code 를 사람이 읽는 표기로 바꾼다. 아키타입 문자열 id 를 아는 곳은 여기뿐이라
    /// <c>Npc.Planning</c> 의 집계를 호스트에서 한 번 옮긴다.
    /// </summary>
    private CachePanel CacheOf(CacheStats stats) => new(
        HitRate: Math.Round(stats.HitRate, 4),
        Hits: stats.Hits,
        Misses: stats.Misses,
        FilledBuckets: stats.FilledBuckets,
        ColdBuckets: stats.ColdBuckets,
        PinnedBuckets: stats.PinnedBuckets,
        IndividualTurnover: Math.Round(stats.IndividualTurnover, 4),
        IndividualLive: stats.IndividualLive,
        TopMisses:
        [
            .. stats.TopMisses.Select(m => new BucketMissRow(
                m.Bucket.Format(_data.Archetypes[m.Bucket.A].Id), m.Misses)),
        ],
        WorstArchetypes:
        [
            .. stats.WorstArchetypes.Select(a => new ArchetypeCacheRow(
                _data.Archetypes[a.Archetype].Id,
                Math.Round(a.HitRate, 4),
                a.Hits,
                a.Misses,
                a.ColdBuckets)),
        ]);

    private LinkPanel LinkOf(LinkStats stats) => new(
        CommandsEnqueued: stats.CommandsEnqueued,
        CommandsFlushed: stats.CommandsFlushed,
        CommandsDropped: stats.CommandsDropped,
        PendingCommands: stats.PendingCommands,
        EventsDrained: _loop.EventsDrained,
        EventGaps: _loop.EventGaps,
        EventBacklogs: _loop.EventBacklogs);

    private ActionCount[] TopActions(int take)
    {
        var top = new List<ActionCount>(take);

        for (int code = 0; code < _byAction.Length; code++)
        {
            if (_byAction[code] == 0)
            {
                continue;
            }

            top.Add(new ActionCount(_data.ActionName(new ActionId((ushort)code)), _byAction[code]));
        }

        // 개체 수 내림차순, 같으면 이름 오름차순 — 순서가 흔들리면 대시보드가 깜빡인다.
        top.Sort((a, b) => a.Count != b.Count
            ? b.Count.CompareTo(a.Count)
            : string.CompareOrdinal(a.Action, b.Action));

        return [.. top.Take(take)];
    }
}
