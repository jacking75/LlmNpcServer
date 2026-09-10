using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.AI;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Prebake;

/// <summary>전량 생성 설정. docs/12 §8 · T2-19.</summary>
/// <param name="Concurrency">
/// 시작 동시성. <b>8 이다.</b> "동시 32" 는 상위 계획의 추정이고 W1 은 외부 동시성을 재지 않았다
/// (`W1_concurrency.md` 는 로컬 2종뿐). 여기서 AIMD 로 올리며 429 최초 발생 지점을 실측한다.
/// </param>
/// <param name="MaxConcurrency">AIMD 가 올릴 수 있는 상한.</param>
/// <param name="SuccessesBeforeIncrease">
/// 이만큼 연속 성공하면 동시성을 1 올린다 (additive increase). docs/13 §4 의 16 이다.
/// </param>
/// <param name="MaxRateLimitRetries">429 로 물러난 버킷을 다시 던지는 횟수 상한.</param>
/// <param name="BackoffMs">
/// 429 를 만났을 때 물러나는 기본 시간. 실제 지연은 <see cref="RetryPolicy.DelayMs"/> 가
/// 지수 + 결정론 지터로 계산한다. 테스트는 0 을 줘서 기다리지 않게 한다.
/// </param>
/// <param name="PlanStoreDirectory">산출물을 쓸 곳. null 이면 파일을 쓰지 않는다.</param>
/// <param name="DryRunSample">드라이런(4단)을 돌릴 비율. 1.0 이면 전수 (docs/13 §8).</param>
/// <param name="Budget">
/// 예산 하드 캡. null 이면 제한이 없다. 캡에 닿으면 회차를 중단하고
/// <see cref="BulkRunReport.StoppedByBudget"/> 이 선다 (T3-14).
/// </param>
public sealed record BulkRunOptions(
    int Concurrency = 8,
    int MaxConcurrency = 32,
    int SuccessesBeforeIncrease = AdaptiveConcurrency.DefaultSuccessStreak,
    int MaxRateLimitRetries = 3,
    int BackoffMs = 5_000,
    string? PlanStoreDirectory = null,
    double DryRunSample = 1.0,
    BudgetGuard? Budget = null);

/// <summary>버킷 하나의 결과.</summary>
/// <param name="Bucket">어느 버킷인가.</param>
/// <param name="Validation">최종 검증 결과.</param>
/// <param name="Stats">계측 누계 (재시도 포함).</param>
/// <param name="Origin">돌려받은 플랜의 출처. <c>Fallback</c> 이면 폴백으로 떨어진 것이다.</param>
/// <param name="Goal">플랜의 goal. 다양성 지표(T2-22)가 쓴다.</param>
/// <param name="Actions">액션 시퀀스. 다양성 지표가 쓴다.</param>
public readonly record struct BucketOutcome(
    BucketKey Bucket,
    ValidationResult Validation,
    CompileStats Stats,
    PlanOrigin Origin,
    string Goal,
    ImmutableArray<string> Actions);

/// <summary>전량 생성 한 회차의 집계.</summary>
/// <param name="Outcomes">버킷 순서대로의 결과.</param>
/// <param name="WallClockSeconds">전체 소요.</param>
/// <param name="FirstRateLimitConcurrency">429 가 처음 난 시점의 동시성. 한 번도 안 났으면 0.</param>
/// <param name="PeakConcurrency">도달한 최대 동시성.</param>
/// <param name="RateLimitHits">429 를 만난 횟수.</param>
/// <param name="StartConcurrency">AIMD 초기 동시성. manifest 에 근거로 남는다 (T3-12).</param>
/// <param name="StoppedByBudget">
/// 예산 캡에 걸려 중단됐는가. manifest 의 <c>partial</c> 이 이 값이고 <c>--resume</c> 이 그것을 본다 (T3-14).
/// </param>
/// <param name="Attempted">
/// 실제로 시도한 버킷 수. 예산 중단이면 <see cref="Total"/> 보다 작다 —
/// 시도하지 않은 버킷의 결과는 <c>default</c> 로 남는다.
/// </param>
public sealed record BulkRunReport(
    ImmutableArray<BucketOutcome> Outcomes,
    double WallClockSeconds,
    int FirstRateLimitConcurrency,
    int PeakConcurrency,
    int RateLimitHits,
    int StartConcurrency = AdaptiveConcurrency.DefaultStart,
    bool StoppedByBudget = false,
    int Attempted = -1)
{
    /// <summary>대상이었던 버킷 수.</summary>
    public int Total => Outcomes.Length;

    /// <summary>실제로 시도한 버킷 수.</summary>
    public int AttemptedCount => Attempted < 0 ? Total : Attempted;

    /// <summary>
    /// 검증까지 통과한 수 (재시도 1회 포함).
    ///
    /// <b><c>Attempt &gt; 0</c> 을 같이 본다.</b> 시도하지 않은 버킷의 결과는 <c>default</c> 인데
    /// <c>default(ValidationResult).FailedAt</c> 이 <c>None</c> 이라 그것만 보면 "통과"로 읽힌다 —
    /// 예산 캡에 걸려 던지지도 못한 버킷이 통과로 세어지면 통과율이 통째로 거짓이 된다.
    /// </summary>
    public int Passed => Outcomes.Count(o => o.Stats.Attempt > 0 && o.Validation.IsValid);

    /// <summary>폴백으로 떨어진 수. 게이트는 ≤ 5% 다.</summary>
    public int FellBack => Outcomes.Count(o => o.Stats.Attempt > 0 && o.Origin == PlanOrigin.Fallback);

    /// <summary>인접 버킷에서 빌려 온 수. 시도조차 못 한 버킷은 세지 않는다.</summary>
    public int Reused => Outcomes.Count(
        o => !o.Validation.IsValid && o.Origin != PlanOrigin.Fallback && o.Stats.Attempt > 0);

    /// <summary>
    /// 통과율. P2 게이트는 ≥ 0.90 이다.
    /// <b>분모는 시도한 수다</b> — 예산 캡에 걸려 던지지도 못한 버킷을 실패로 세면 통과율이 왜곡된다.
    /// </summary>
    public double PassRate => AttemptedCount == 0 ? 0 : (double)Passed / AttemptedCount;

    /// <summary>폴백 비율. 분모는 시도한 수다.</summary>
    public double FallbackRate => AttemptedCount == 0 ? 0 : (double)FellBack / AttemptedCount;

    /// <summary>비용 합계(USD).</summary>
    public double CostUsd => Outcomes.Sum(o => o.Stats.CostUsd);

    /// <summary>입력 토큰 합계.</summary>
    public long PromptTokens => Outcomes.Sum(o => (long)o.Stats.PromptTokens);

    /// <summary>캐시 적중 입력 토큰 합계.</summary>
    public long CachedTokens => Outcomes.Sum(o => (long)o.Stats.CachedTokens);

    /// <summary>캐시 적중률 (입력 토큰 기준). 게이트는 ≥ 0.95 다 (외부 API).</summary>
    public double CacheHitRate => PromptTokens == 0 ? 0 : (double)CachedTokens / PromptTokens;

    /// <summary>요청당 평균 지연(ms). 재시도 포함 누계를 버킷 수로 나눈다.</summary>
    public double AverageLatencyMs => Total == 0 ? 0 : Outcomes.Sum(o => o.Stats.LatencyMs) / Total;

    /// <summary>1회 만에 통과한 수.</summary>
    public int PassedFirstAttempt => Outcomes.Count(o => o.Stats.Attempt == 1 && o.Validation.IsValid);
}

/// <summary>
/// 2,880 버킷 전량 생성. docs/12 §8 · T2-19.
///
/// <b>동시성은 8에서 시작한다.</b> 올리는 것은 AIMD 다 — 연속 성공이 쌓이면 1씩 올리고,
/// 429 를 만나면 절반으로 접는다. 그 과정에서 <b>429 가 처음 난 동시성</b>을 기록하는 것이
/// 이 회차의 실측 목표 중 하나다 (T3-08 의 <c>--concurrency</c> 기본값이 여기서 나온다).
///
/// 버킷 순서는 입력 순서 그대로이고 결과도 그 순서로 돌려준다 — 회차 간 diff 가 의미를 가져야 한다.
/// </summary>
public sealed class BulkRunner
{
    private readonly MasterDataSet _data;
    private readonly PromptPrefix _prefix;
    private readonly LlmEngineOptions _engine;
    private readonly BulkRunOptions _options;
    private readonly PlanStore _store;
    private readonly CompileStatsCollector _stats;

    /// <summary>러너를 만든다.</summary>
    public BulkRunner(
        MasterDataSet data,
        PromptPrefix prefix,
        LlmEngineOptions engine,
        BulkRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(engine);

        _data = data;
        _prefix = prefix;
        _engine = engine;
        _options = options ?? new BulkRunOptions();
        _store = PlanStore.CreateIdleOnly(data);
        _stats = new CompileStatsCollector();
    }

    /// <summary>이 회차의 계측 누계.</summary>
    public CompileStatsCollector Stats => _stats;

    /// <summary>생성된 플랜들. 인접 버킷 재사용이 여기를 본다.</summary>
    public PlanStore Store => _store;

    /// <summary>전 버킷. docs/01 §6. 개수는 masterdata 가 정한다 (F-05).</summary>
    public static ImmutableArray<BucketKey> AllBuckets(BucketSpace space) => TargetSelector.All(space);

    /// <summary>
    /// 버킷들을 순서대로 생성한다.
    /// </summary>
    /// <param name="buckets">대상 버킷.</param>
    /// <param name="clientFactory">
    /// 클라이언트 공급자. 테스트는 가짜를 넣고 실제 회차는 <see cref="ChatClientFactory.Create"/> 를 넣는다.
    /// </param>
    /// <param name="progress">진행 보고. null 이면 조용히 돈다.</param>
    /// <param name="cancellationToken">취소.</param>
    public async Task<BulkRunReport> RunAsync(
        IReadOnlyList<BucketKey> buckets,
        Func<IChatClient> clientFactory,
        Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(clientFactory);

        var outcomes = new BucketOutcome?[buckets.Count];
        var pending = new Queue<(int Index, int Retries)>();

        for (int i = 0; i < buckets.Count; i++)
        {
            pending.Enqueue((i, 0));
        }

        // AIMD 는 AdaptiveConcurrency 가 맡는다 (T3-12). 여기서는 물결 크기만 물어본다.
        var aimd = new AdaptiveConcurrency(
            start: Math.Max(1, _options.Concurrency),
            max: _options.MaxConcurrency,
            successStreak: _options.SuccessesBeforeIncrease);

        int done = 0;

        using IChatClient client = clientFactory();

        RejectedStore? rejected = _options.PlanStoreDirectory is { } directory
            ? RejectedStore.CreateDefault(directory, _data)
            : null;

        var compiler = new LlmPlanCompiler(
            _data,
            _prefix,
            _engine,
            client,
            _stats,
            new Npc.Sim.Validation.DryRunValidator(_data),
            new NeighborReuseSource(_store, _data),
            rejected);

        long started = Stopwatch.GetTimestamp();

        BudgetGuard? budget = _options.Budget;
        bool stoppedByBudget = false;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 한 물결 = 지금 동시성만큼. 물결이 끝날 때마다 동시성을 조정한다.
            int concurrency = aimd.Current;
            var wave = new List<(int Index, int Retries)>(concurrency);

            while (wave.Count < concurrency && pending.Count > 0)
            {
                wave.Add(pending.Dequeue());
            }

            // 예산 판정은 던지기 전에 한다 (docs/13 §4). 캡을 넘으면 여기서 끝이다 —
            // 남은 버킷은 --resume 이 이어서 만든다.
            if (budget is not null && !budget.TryReserve(wave.Count))
            {
                stoppedByBudget = true;
                progress?.Invoke(done, buckets.Count, budget.StopMessage(done, buckets.Count));
                break;
            }

            PlanCompileResult[] results = await Task.WhenAll(
                wave.Select(item => CompileAsync(compiler, buckets[item.Index], cancellationToken)))
                .ConfigureAwait(false);

            bool sawRateLimit = false;
            int backoffMs = 0;

            for (int i = 0; i < wave.Count; i++)
            {
                (int index, int retries) = wave[i];
                PlanCompileResult result = results[i];

                budget?.Record(result.Stats.CostUsd);

                if (RetryPolicy.IsRateLimited(result.Stats.Error))
                {
                    sawRateLimit = true;

                    // 아직 여유가 있으면 다시 던진다. 물러난 것은 결과로 세지 않는다.
                    if (RetryPolicy.ShouldRetry(retries, _options.MaxRateLimitRetries))
                    {
                        pending.Enqueue((index, retries + 1));

                        // 물결 안에서 가장 오래 기다려야 하는 만큼 물러난다.
                        // 지터는 (버킷 인덱스, 시도) 해시라 결정론이다 (CLAUDE.md §2.3).
                        backoffMs = Math.Max(
                            backoffMs,
                            RetryPolicy.DelayMs(buckets[index].ToIndex(), retries + 1, _options.BackoffMs));

                        continue;
                    }
                }

                outcomes[index] = Record(buckets[index], result);
                done++;
                progress?.Invoke(done, buckets.Count, Describe(buckets[index], result));
            }

            if (sawRateLimit)
            {
                aimd.OnThrottled();   // multiplicative decrease

                if (backoffMs > 0)
                {
                    await Task.Delay(backoffMs, cancellationToken).ConfigureAwait(false);
                }

                continue;
            }

            aimd.OnSuccess(wave.Count);   // additive increase
        }

        double wallClock = Stopwatch.GetElapsedTime(started).TotalSeconds;

        var final = ImmutableArray.CreateBuilder<BucketOutcome>(buckets.Count);

        foreach (BucketOutcome? outcome in outcomes)
        {
            final.Add(outcome ?? default);
        }

        return new BulkRunReport(
            final.ToImmutable(),
            wallClock,
            aimd.FirstThrottleConcurrency,
            aimd.Peak,
            aimd.ThrottleCount,
            aimd.Start,
            stoppedByBudget,
            done);
    }

    private async Task<PlanCompileResult> CompileAsync(
        LlmPlanCompiler compiler, BucketKey bucket, CancellationToken cancellationToken) =>
        await compiler.CompileAsync(
            new PlanRequest(bucket, _data.InitialFlags(bucket)), cancellationToken).ConfigureAwait(false);

    /// <summary>통과한 플랜은 스토어에 넣는다 — 뒤 버킷이 인접 재사용으로 빌려 갈 수 있다.</summary>
    private BucketOutcome Record(BucketKey bucket, in PlanCompileResult result)
    {
        CompiledPlan? plan = result.Plan;

        if (result.Validation.IsValid && plan is not null)
        {
            _store.SetBucket(bucket, _store.Register(plan));
        }

        ImmutableArray<string> actions = plan is null
            ? []
            : [.. plan.Steps.Select(s => _data.ActionName(s.Action))];

        return new BucketOutcome(
            bucket,
            result.Validation,
            result.Stats,
            plan?.Origin ?? PlanOrigin.Fallback,
            plan?.Goal ?? string.Empty,
            actions);
    }

    private string Describe(BucketKey bucket, in PlanCompileResult result) =>
        result.Validation.IsValid
            ? string.Create(CultureInfo.InvariantCulture, $"{Format(bucket)} ok (attempt {result.Stats.Attempt})")
            : $"{Format(bucket)} {result.Validation.Code}@{result.Validation.StepIndex.ToString(CultureInfo.InvariantCulture)}";

    private string Format(BucketKey bucket) => bucket.Format(_data.Archetypes[bucket.A].Id);
}

/// <summary>
/// <see cref="BucketNeighbors"/> 를 컴파일러에 물리는 어댑터.
/// 계약은 <c>Npc.Llm</c> 에 있고 구현은 <c>Npc.Planning</c> 을 아는 이쪽이 갖는다.
/// </summary>
internal sealed class NeighborReuseSource(PlanStore store, MasterDataSet data) : IPlanReuseSource
{
    /// <inheritdoc />
    public bool TryReuse(BucketKey target, out CompiledPlan? plan, out BucketKey source) =>
        BucketNeighbors.TryReuse(target, store, data, out plan, out source);
}
