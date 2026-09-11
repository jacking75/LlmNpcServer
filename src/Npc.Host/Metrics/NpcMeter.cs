using System.Diagnostics;
using System.Diagnostics.Metrics;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Host.Persistence;
using Npc.Host.Replan;
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
/// <param name="State">
/// 링크 접속 상태 (A-03). <b>대시보드가 이것을 보기 전에는 Faulted 를 알 길이 없었다</b> —
/// 통계는 전부 0 으로 멈추지만 그것은 "조용한 회차" 와 구별되지 않는다.
/// </param>
public readonly record struct LinkPanel(
    long CommandsEnqueued,
    long CommandsFlushed,
    long CommandsDropped,
    int PendingCommands,
    long EventsDrained,
    long EventGaps,
    long EventBacklogs,
    string State);

/// <summary>티어 하나의 처리율. docs/14 §8 재계획 패널.</summary>
/// <param name="Tier">티어 이름.</param>
/// <param name="Source">일감 공급원 이름 (<c>individual</c>·<c>bucket</c>).</param>
/// <param name="Workers">워커 수.</param>
/// <param name="Busy">지금 LLM 호출 중인 워커 수.</param>
/// <param name="Depth">공급원의 대기 수.</param>
/// <param name="Taken">꺼낸 일감 누계.</param>
/// <param name="Applied">반영한 플랜 누계.</param>
/// <param name="Failed">플랜을 못 만든 누계.</param>
/// <param name="PerSecond">초당 처리율.</param>
/// <param name="AvgWaitTicks">큐에서 기다린 평균 틱.</param>
public readonly record struct TierThroughputRow(
    string Tier,
    string Source,
    int Workers,
    int Busy,
    int Depth,
    long Taken,
    long Applied,
    long Failed,
    double PerSecond,
    double AvgWaitTicks);

/// <summary>재계획 패널. docs/11 §10 · docs/14 §8 (T4-19).</summary>
/// <param name="QueueDepth">큐 깊이.</param>
/// <param name="EnqueuedPerSecond">초당 유입.</param>
/// <param name="Deviations">이탈 판정 누적.</param>
/// <param name="ScanPerTick">마지막 틱의 인지 스캔 수.</param>
/// <param name="InterruptsForced">인터럽트가 즉시 발행한 액션 수.</param>
/// <param name="InterruptsSuppressed">같은 규칙이 이어서 걸려 넘긴 수.</param>
/// <param name="QueueDropped">큐가 포화해 버린 요청 수. docs/14 §6 의 "큐: 거절 수".</param>
/// <param name="QueueDepthP99">관측 창의 큐 깊이 p99.</param>
/// <param name="UrgentCount">큐에 있는 인터럽트 항목 수. 상한은 용량의 50% 다 (T4-03).</param>
/// <param name="UrgentDropped">인터럽트 슬롯 상한에 걸려 거절한 수.</param>
/// <param name="ScoreHistogram">
/// 점수 분포. 마지막 칸이 인터럽트(1000+)이고 앞 칸들이 <c>[0, 1000)</c> 을 균등 분할한다.
/// </param>
/// <param name="Tiers">티어별 처리율. 티어가 꺼져 있으면 빈 배열이다.</param>
/// <param name="StaleDiscarded">낡아서 폐기하고 재삽입한 요청 수 (T4-04).</param>
/// <param name="BandsAttached">
/// 관계 밴드를 서픽스에 실은 횟수 (D-03). <b>기억 저장소를 켰는데 0 이면</b>
/// 저장소가 비어 있다는 뜻이다 — 게임서버가 아직 아무것도 안 썼다.
/// </param>
public readonly record struct ReplanPanel(
    int QueueDepth,
    double EnqueuedPerSecond,
    long Deviations,
    int ScanPerTick,
    long InterruptsForced,
    long InterruptsSuppressed,
    long QueueDropped = 0,
    int QueueDepthP99 = 0,
    int UrgentCount = 0,
    long UrgentDropped = 0,
    int[]? ScoreHistogram = null,
    TierThroughputRow[]? Tiers = null,
    long StaleDiscarded = 0,
    long BandsAttached = 0);

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

/// <summary>
/// 비용 패널. docs/14 §8 · T4-20.
///
/// <b>로컬 티어는 <c>cached_tokens</c> 를 항상 0 으로 보고한다</b> (<c>W1_env.md §4.4</c>) —
/// 그것을 0% 로 그리면 "캐시가 안 걸렸다" 로 읽힌다. 그래서
/// <see cref="PromptCacheReported"/> 로 "잴 수 있는 값인가" 를 따로 낸다.
/// </summary>
/// <param name="Enabled">티어가 켜져 있는가. 꺼져 있으면 나머지는 전부 0 이다.</param>
/// <param name="Engine">쓰고 있는 엔진 id. 티어가 꺼져 있으면 빈 문자열.</param>
/// <param name="Calls">호출 수 (재시도 포함).</param>
/// <param name="PromptTokens">입력 토큰 누계.</param>
/// <param name="CompletionTokens">출력 토큰 누계.</param>
/// <param name="CostUsd">비용 누계(USD).</param>
/// <param name="PromptCacheHitRate">프롬프트 캐시 적중률 (입력 토큰 기준).</param>
/// <param name="PromptCacheReported">
/// 엔진이 캐시 적중 토큰을 실제로 보고하는가. <b>false 면 적중률을 0% 가 아니라 "미보고" 로 그린다.</b>
/// </param>
/// <param name="UniquePrefixHashes">
/// 관측된 프리픽스 SHA 종류 수. <b>1 이 아니면 즉시 경보다</b> — 프롬프트 캐시가 깨졌다는 뜻이다
/// (docs/01 §10.2 · 리스크 R11).
/// </param>
/// <param name="TokensToday">오늘 쓴 T2 토큰. 일일 캡의 대상이다.</param>
/// <param name="LocalTokensToday">오늘 쓴 T1 토큰. 캡의 대상이 아니다.</param>
/// <param name="TokenCapUsage">일일 토큰 캡 소진율 0~1.</param>
/// <param name="CostToday">오늘 쓴 비용(USD).</param>
/// <param name="CostCapUsage">일일 비용 캡 소진율 0~1.</param>
/// <param name="Downgrades">T2 → T1 자동 강등 횟수.</param>
/// <param name="Rejections">예산 소진으로 거절한 횟수.</param>
/// <param name="Failovers">T2 장애로 T1 에 넘긴 횟수.</param>
/// <param name="BreakerState">서킷 브레이커 상태.</param>
/// <param name="T1Calls">T1 으로 처리한 요청 수.</param>
/// <param name="T2Calls">T2 로 처리한 요청 수.</param>
/// <param name="Spillovers">T1 큐 폭주로 T2 에 흘린 횟수.</param>
public readonly record struct CostPanel(
    bool Enabled,
    string Engine,
    long Calls,
    long PromptTokens,
    long CompletionTokens,
    double CostUsd,
    double PromptCacheHitRate,
    bool PromptCacheReported,
    int UniquePrefixHashes,
    long TokensToday,
    long LocalTokensToday,
    double TokenCapUsage,
    double CostToday,
    double CostCapUsage,
    long Downgrades,
    long Rejections,
    long Failovers,
    string BreakerState,
    long T1Calls,
    long T2Calls,
    long Spillovers,
    long SpilloverDeferred,
    long IndividualTokensToday,
    long IndividualTokenCap,
    double WallClockSpentUsd,
    double WallClockCapUsd);

/// <summary>
/// 버킷 히트맵. docs/14 §8 · T4-22.
///
/// <b>대부분 비어 있을 것이고, 그게 곧 발견이다</b> — "2,880이 아니라 300이면 충분했다".
/// W12 보고서 §6 의 원자료가 될 값이라 <see cref="Cells"/> 를 통째로 낸다 (T5-18 이 읽는다).
///
/// <c>Cells</c> 는 <c>BucketKey.ToIndex()</c> 순서의 2,880칸이고 값은 그 버킷의 조회 수다
/// (히트 + 미스). 40 × 72 로 접으면 행이 아키타입, 열이 (시간대 × 지역상태 × 기후) 다.
/// </summary>
/// <param name="Archetypes">행 수 = 40.</param>
/// <param name="Columns">열 수 = 6 × 4 × 3 = 72.</param>
/// <param name="Cells">버킷별 조회 수. 길이 2,880.</param>
/// <param name="Filled">플랜이 있는 버킷 수.</param>
/// <param name="Used">한 번이라도 조회된 버킷 수.</param>
/// <param name="UsedRatio">실사용 버킷 비율. <b>이 숫자가 §6 "발견" 의 본문이다.</b></param>
/// <param name="Peak">가장 많이 조회된 버킷의 조회 수. 히트맵 색 스케일의 분모다.</param>
/// <param name="ArchetypeNames">행 이름. 길이 40.</param>
public readonly record struct BucketHeatmap(
    int Archetypes,
    int Columns,
    long[] Cells,
    int Filled,
    int Used,
    double UsedRatio,
    long Peak,
    string[] ArchetypeNames);

/// <summary>대시보드가 폴링하는 /metrics 한 장. docs/11 §10 의 5패널 + docs/13 §6 의 캐시 패널.</summary>
/// <param name="Tick">틱 패널.</param>
/// <param name="Npc">NPC 패널.</param>
/// <param name="Actions">액션 Top 10.</param>
/// <param name="Link">링크 패널 (N6 시퀀스 갭 포함).</param>
/// <param name="Replan">재계획 패널.</param>
/// <param name="Cache">플랜 캐시 패널 (docs/13 §6).</param>
/// <param name="Cost">비용 패널 (docs/14 §8).</param>
/// <param name="Heatmap">버킷 히트맵 (docs/14 §8).</param>
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
    long LlmCalls,
    CostPanel Cost = default,
    BucketHeatmap Heatmap = default);

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

    /// <summary>점수 히스토그램 칸 수. 마지막 칸은 인터럽트 전용이다 (docs/14 §8).</summary>
    public const int ScoreBuckets = 11;

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
    private readonly TierWiring? _tiers;

    /// <summary>점수 히스토그램 칸. 마지막 칸이 인터럽트(1000+)다 (docs/14 §8).</summary>
    private readonly int[] _scoreBuckets = new int[ScoreBuckets];

    /// <summary>
    /// 히트맵 행 이름. 기동 시 1회 만든다 — 스냅샷마다 문자열을 다시 뽑을 이유가 없다.
    /// 길이는 masterdata 가 정한다 (F-05).
    /// </summary>
    private readonly string[] _archetypeNames;

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
    private long _lastAllocatingTick;
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
        CacheMetrics? cache = null,
        TierWiring? tiers = null)
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
        _tiers = tiers;

        // 카운터는 PlanStore·IndividualPlanPool 이 들고 있다. 계측기만 여기서 만든다 (docs/13 §6).
        _cache = cache ?? new CacheMetrics(plans);
        _byAction = new int[data.Actions.MaxCode + 1];
        _archetypeNames = new string[data.Buckets.ArchetypeCount];

        for (int code = 0; code < _archetypeNames.Length; code++)
        {
            _archetypeNames[code] = data.Archetypes[new ArchetypeId((ushort)code)].Id;
        }

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

        // ── 스냅샷 (A-01) ──
        //
        // 상태 손실 창의 계기다. last_tick 이 현재 틱에서 주기 이상 벌어지면 창이 커진 것이고,
        // failures 가 0 이 아니면 그 창이 얼마인지 아무도 모른다.
        _meter.CreateObservableGauge(
            "npc.snapshot.last_tick", () => Snapshots?.LastTick ?? 0, description: "마지막 스냅샷의 게임 틱");
        _meter.CreateObservableGauge(
            "npc.snapshot.write_ms", () => LastSnapshotWriteMs, description: "마지막 스냅샷 쓰기 시간(ms)");
        _meter.CreateObservableGauge(
            "npc.snapshot.bytes", () => LastSnapshotBytes, description: "마지막 스냅샷 크기(B)");
        _meter.CreateObservableCounter(
            "npc.snapshot.failures", () => Snapshots?.Failures ?? 0, description: "스냅샷 실패 누계");

        // ── 게임 시계 (A-10) ──
        //
        // ticks_behind 가 계속 크면 게임서버가 우리보다 빠르다는 뜻이고,
        // stalled 는 TickSync 자체가 멈춘 사건이다. 둘은 다른 장애다.
        _meter.CreateObservableGauge(
            "npc.clock.ticks_since_sync",
            () => TickSync?.TicksBehind ?? 0,
            description: "게임서버 틱과 처리한 틱의 차이. 0 이 정상");
        _meter.CreateObservableGauge(
            "npc.clock.stalled",
            () => TickSync is { Stalled: true } ? 1 : 0,
            description: "TickSync 가 임계를 넘겨 멈춰 있으면 1");
        _meter.CreateObservableGauge(
            "npc.clock.game_hour", () => _clock.GameHour, description: "게임 시각 0~23");
    }

    /// <summary><c>TickSync</c> 워치독 (A-10). 조립 순서상 계측기가 먼저 생기므로 init 이 아니다.</summary>
    public TickSyncWatchdog? TickSync { get; set; }

    /// <summary>벽시계 청구 캡 (C-02). 조립 순서상 계측기가 먼저 생기므로 init 이 아니다.</summary>
    public Npc.Host.Replan.BillingGuard? Billing { get; set; }

    /// <summary>
    /// 스냅샷 쓰기 (A-01). 조립 순서상 계측기가 먼저 생기므로 init 이 아니다.
    /// 꺼져 있으면 null 이고 계측기는 0 을 낸다.
    /// </summary>
    public SnapshotWriter? Snapshots { get; set; }

    /// <summary>마지막 스냅샷 쓰기에 걸린 밀리초.</summary>
    public double LastSnapshotWriteMs { get; private set; }

    /// <summary>마지막 스냅샷 파일 크기.</summary>
    public long LastSnapshotBytes { get; private set; }

    /// <summary>스냅샷 쓰기 결과를 받는다. <see cref="SnapshotWriter"/> 가 부른다.</summary>
    public void OnSnapshotWritten(SnapshotWriteResult result)
    {
        if (result.Error is not null)
        {
            return;
        }

        LastSnapshotWriteMs = result.ElapsedMs;
        LastSnapshotBytes = result.Bytes;
    }

    /// <summary>예산을 넘긴 틱 수.</summary>
    public long Overruns => _overrunCount;

    /// <summary>기동 이후 Gen0 컬렉션 수. 틱 루프에서 0 이어야 한다 (docs/11 §9).</summary>
    public int Gen0Collections => (int)(GC.CollectionCount(0) - _gen0AtStart);

    /// <summary>틱 루프 안에서 할당한 총 바이트. 0 이 목표다.</summary>
    public long AllocatedInTicks => _allocatedInTicks;

    /// <summary>
    /// 틱 창 안에서 <b>마지막으로</b> 할당이 있었던 틱. 한 번도 없었으면 0.
    ///
    /// <para>
    /// <b><see cref="AllocatedInTicks"/> 만으로는 판정할 수 없다.</b> 그 값은 누계라
    /// 콜드 스타트(JIT · 정적 초기화 · 첫 인터페이스 디스패치)까지 함께 센다.
    /// 실측(2026-07-28): 게임 3일(4,320틱) 회차에서 할당은 <b>틱 1·148·396 세 번뿐</b>이고
    /// 나머지 3,924틱은 0 이다 — 회차를 3배 늘려도 총량이 264 B 그대로다.
    /// 즉 <b>정상 상태의 틱당 할당은 실제로 0</b> 이고, 누계에 잡히는 것은 첫 실행 비용이다.
    /// </para>
    ///
    /// <para>
    /// 그래서 게이트는 "누계 0" 이 아니라 <b>"정상 상태에서 0"</b> 을 본다 —
    /// 회차 후반부에 할당이 하나라도 있으면 그것이 진짜 누수다 (docs/14 §9 항목 9).
    /// </para>
    /// </summary>
    public long LastAllocatingTick => _lastAllocatingTick;

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
            _lastAllocatingTick = tick.Value;
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
                QueueDropped: _replanQueue.Dropped,
                QueueDepthP99: QueueDepthPercentile(0.99),
                UrgentCount: _replanQueue.UrgentCount,
                UrgentDropped: _replanQueue.UrgentDropped,
                ScoreHistogram: Histogram(),
                Tiers: TierRows(seconds),
                StaleDiscarded: _tiers?.Individual?.Discarded ?? 0,
                BandsAttached: _tiers?.BandsAttached ?? 0),
            Cache: CacheOf(_cache.Snapshot()),
            LlmCalls: _compile?.Calls ?? 0,
            Cost: CostOf(),
            Heatmap: HeatmapOf());
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
        EventBacklogs: _loop.EventBacklogs,
        State: _link.State.ToString());

    /// <summary>
    /// 비용 패널. docs/14 §8 (T4-20).
    ///
    /// 티어가 꺼져 있으면 <c>Enabled=false</c> 하나만 의미가 있다 —
    /// 0 으로 채운 값을 그리면 "돌고 있는데 비용 0" 으로 읽힌다.
    /// </summary>
    private CostPanel CostOf()
    {
        if (_tiers is not { Enabled: true } tiers || _compile is not { } stats)
        {
            return default;
        }

        ReplanBudget? budget = tiers.Budget;

        // 로컬 엔진은 cached_tokens 를 항상 0 으로 보고한다 (W1_env.md §4.4).
        // 적중률이 0 인 것과 "잴 수 없는 것" 은 다르다.
        bool reported = stats.CachedTokens > 0 || tiers.Mode is TierMode.T2 or TierMode.All;

        return new CostPanel(
            Enabled: true,
            Engine: tiers.EngineIds,
            Calls: stats.Calls,
            PromptTokens: stats.PromptTokens,
            CompletionTokens: stats.CompletionTokens,
            CostUsd: Math.Round(stats.CostUsd, 6),
            PromptCacheHitRate: Math.Round(stats.CacheHitRate, 4),
            PromptCacheReported: reported,
            UniquePrefixHashes: stats.UniquePrefixHashes,
            TokensToday: budget?.TokensToday ?? 0,
            LocalTokensToday: budget?.LocalTokensToday ?? 0,
            TokenCapUsage: Math.Round(budget?.TokenCapUsage ?? 0, 4),
            CostToday: Math.Round(budget?.CostToday ?? 0, 6),
            CostCapUsage: Math.Round(budget?.CostCapUsage ?? 0, 4),
            Downgrades: budget?.Downgrades ?? 0,
            Rejections: budget?.Rejections ?? 0,
            Failovers: tiers.Router?.Failovers ?? 0,
            BreakerState: (tiers.Breaker?.StateAt(_clock.Current) ?? CircuitState.Closed).ToString(),
            T1Calls: tiers.Router?.T1Calls ?? 0,
            T2Calls: tiers.Router?.T2Calls ?? 0,
            Spillovers: tiers.Router?.Spillovers ?? 0,

            // C-07 — 개체 몫이 먼저 마르는지 본다. 버킷 미스 보충이 굶고 있으면 여기가 꽉 차 있다.
            SpilloverDeferred: tiers.Router?.SpilloverDeferred ?? 0,
            IndividualTokensToday: tiers.Quota?.IndividualTokensToday ?? 0,
            IndividualTokenCap: tiers.Quota?.IndividualCap ?? 0,

            // C-02 — 벽시계 오늘 지출. ReplanBudget 의 틱 기준 하루와 별개다.
            WallClockSpentUsd: Billing?.SpentToday ?? 0,
            WallClockCapUsd: Billing?.CapUsd ?? 0);
    }

    /// <summary>
    /// 버킷 히트맵. docs/14 §8 (T4-22).
    ///
    /// 전체 칸(아키타입 수 × 72)을 매 스냅샷마다 새로 만든다. 폴링 간격이 1초라 비용이
    /// 문제되지 않고, <b>이 배열이 그대로 W12 보고서의 원자료</b>라 자르지 않는다 (T5-18 이 읽는다).
    /// </summary>
    private BucketHeatmap HeatmapOf()
    {
        var cells = new long[_data.Buckets.TotalKeys];
        int used = 0;
        long peak = 0;

        for (int i = 0; i < cells.Length; i++)
        {
            BucketKey key = BucketKey.FromIndex(i);

            // 조회 수 = 히트 + 미스. "쓰였는가" 를 보는 것이지 "채워졌는가" 가 아니다 —
            // 미생성 버킷이 조회되는 것이야말로 프리베이크 우선순위의 근거다 (docs/13 §6).
            long queries = _plans.HitsOf(key) + _plans.MissesOf(key);

            cells[i] = queries;

            if (queries > 0)
            {
                used++;
                peak = Math.Max(peak, queries);
            }
        }

        return new BucketHeatmap(
            Archetypes: _data.Buckets.ArchetypeCount,
            Columns: BucketKey.PerArchetype,
            Cells: cells,
            Filled: _plans.FilledBuckets,
            Used: used,
            UsedRatio: cells.Length == 0 ? 0 : Math.Round((double)used / cells.Length, 4),
            Peak: peak,
            ArchetypeNames: _archetypeNames);
    }

    /// <summary>점수 분포. 배열은 매 스냅샷마다 새로 만든다 — 대시보드가 JSON 으로 가져간다.</summary>
    private int[] Histogram()
    {
        _replanQueue.ScoreHistogram(_scoreBuckets);

        return [.. _scoreBuckets];
    }

    /// <summary>
    /// 티어별 처리율. 티어가 꺼져 있으면 빈 배열이다 — 0 으로 채운 행을 내면
    /// "돌고 있는데 처리량이 0" 으로 읽힌다.
    /// </summary>
    private TierThroughputRow[] TierRows(double seconds)
    {
        if (_tiers is not { Enabled: true })
        {
            return [];
        }

        var rows = new List<TierThroughputRow>(_tiers.Workers.Count);

        foreach (ReplanWorker worker in _tiers.Workers)
        {
            bool individual = worker.Name == "individual";

            rows.Add(new TierThroughputRow(
                Tier: individual ? "T1" : "T2",
                Source: worker.Name,
                Workers: worker.Workers,
                Busy: worker.Busy,
                Depth: individual ? _replanQueue.Count : _tiers.Buckets?.Depth ?? 0,
                Taken: worker.Taken,
                Applied: worker.Applied,
                Failed: worker.Failed,
                PerSecond: seconds <= 0 ? 0 : Math.Round(worker.Taken / seconds, 3),
                AvgWaitTicks: Math.Round(worker.AverageWaitTicks, 1)));
        }

        return [.. rows];
    }

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
