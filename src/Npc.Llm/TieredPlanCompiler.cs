using Npc.Contracts;
using Npc.Core;
using Npc.Core.Validation;

namespace Npc.Llm;

/// <summary>
/// 3-티어 라우터. docs/14 §4 · 상위 계획 §10.4.
///
/// <b>재사용 횟수에 비례해 품질에 투자한다</b> (CLAUDE.md §8). 아키타입 플랜은 수천 NPC 가
/// 공유하므로 T2(외부·고품질)로 보내고, 개별 NPC 1회용 재계획은 T1(로컬·저지연·무비용)로 보낸다.
///
/// <b>예산 판정은 <see cref="IReplanBudget"/> 가 한다.</b> 구현(<c>ReplanBudget</c>)은
/// <c>Npc.Planning</c> 에 있고 이 프로젝트는 그것을 참조하지 않는다 (CLAUDE.md §3) —
/// <see cref="IPlanReuseSource"/>·<c>IDryRunValidator</c> 와 같은 방법이다.
///
/// <b>시계는 <see cref="Tick"/> 이고 외부에서 주입한다.</b> 컴파일러가 <c>DateTime</c> 을 보면
/// 예산 판정이 리플레이마다 달라진다 (CLAUDE.md §2.3).
/// </summary>
public sealed class TieredPlanCompiler : IPlanCompiler
{
    /// <summary>
    /// T1 큐 깊이 스필오버 임계. docs/14 §4 표의 64.
    ///
    /// T1 지연이 실측 5.1s 이므로 큐에 64건이 밀려 있으면 마지막 건은 <b>5분 뒤</b>에 처리된다
    /// (`W1_perf.csv`). 그 시점에는 스냅샷 낡음 판정(T4-04)이 거의 확실히 폐기한다 —
    /// 즉 임계를 넘긴 뒤의 T1 요청은 대부분 헛일이다. 그래서 T2 로 흘린다.
    /// </summary>
    public const int DefaultSpilloverThreshold = 64;

    /// <summary>
    /// 요청 하나의 기본 토큰 추정치. 실측 <b>23,109 tok/요청</b>
    /// (6,655,438 / 288, `W8_prebake.md §4` — 프리픽스 13,488 × 재시도 포함).
    /// </summary>
    public const int DefaultEstimatedTokens = 23_109;

    private readonly IPlanCompiler _local;
    private readonly IPlanCompiler _external;
    private readonly IReplanBudget _budget;
    private readonly Func<Tick> _now;

    private long _t1Calls;
    private long _t2Calls;
    private long _rejected;
    private long _failovers;
    private long _spillovers;

    /// <summary>라우터를 만든다. 기동 시 1회.</summary>
    /// <param name="local">T1 — 로컬 엔진 컴파일러.</param>
    /// <param name="external">T2 — 외부 API 컴파일러.</param>
    /// <param name="budget">예산. 상한 4개를 들고 있다 (docs/14 §3).</param>
    /// <param name="now">
    /// 현재 틱 공급자. <b>호스트가 <c>GameClock.Current</c> 를 넘긴다.</b>
    /// 이 자리에 벽시계를 넣으면 예산 판정이 비결정론이 된다 (CLAUDE.md §2.3).
    /// </param>
    public TieredPlanCompiler(
        IPlanCompiler local, IPlanCompiler external, IReplanBudget budget, Func<Tick> now)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(external);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(now);

        _local = local;
        _external = external;
        _budget = budget;
        _now = now;
    }

    /// <summary>T1 큐 깊이 스필오버 임계.</summary>
    public int SpilloverThreshold { get; init; } = DefaultSpilloverThreshold;

    /// <summary>
    /// T1 큐 깊이를 읽는 함수. 없으면 스필오버 판정을 하지 않는다 (T4-08).
    /// 호스트가 <c>ReplanQueue.Count</c> 나 T1 워커의 대기 수를 넘긴다.
    /// </summary>
    public Func<int>? LocalQueueDepth { get; init; }

    /// <summary>
    /// 요청당 토큰 추정 함수. 없으면 <see cref="DefaultEstimatedTokens"/> 를 쓴다.
    /// 예산은 추정으로 선차감하고 실측으로 정산한다 (<see cref="IReplanBudget.Settle"/>).
    /// </summary>
    public Func<PlanRequest, int>? EstimateTokens { get; init; }

    /// <summary>T1 로 처리한 요청 수.</summary>
    public long T1Calls => Interlocked.Read(ref _t1Calls);

    /// <summary>T2 로 처리한 요청 수.</summary>
    public long T2Calls => Interlocked.Read(ref _t2Calls);

    /// <summary>예산이 없어 거절한 요청 수. 그 NPC 는 기존 플랜을 계속 쓴다.</summary>
    public long Rejected => Interlocked.Read(ref _rejected);

    /// <summary>T2 실패 후 T1 으로 넘긴 횟수. docs/14 §4 의 <c>TierFailover</c>.</summary>
    public long Failovers => Interlocked.Read(ref _failovers);

    /// <summary>
    /// T1 큐 폭주로 T2 로 흘린 횟수 (docs/14 §4 표 5행).
    ///
    /// <b>이게 T2 호출 수의 상당 부분이면 T1 워커가 모자란다.</b> 개별 재계획은 1회용이라
    /// 원래 T2 로 갈 성질이 아니고, 스필오버는 "늦게 오는 것보다 비싸게 오는 게 낫다" 는
    /// 임시 조치다 — 상시로 발동하면 워커 수(T4-10)나 예산(T4-05)을 다시 잡아야 한다.
    /// </summary>
    public long Spillovers => Interlocked.Read(ref _spillovers);

    /// <summary>지금 T1 큐가 임계를 넘었는가. 없는 공급자면 항상 거짓이다.</summary>
    public bool IsSpilling => LocalQueueDepth is { } depth && depth() > SpilloverThreshold;

    /// <summary>
    /// docs/14 §4 의 티어 선택 규칙. <b>예산은 보지 않는다</b> —
    /// 예산 판정과 강등은 <see cref="IReplanBudget.Acquire"/> 의 몫이다.
    /// </summary>
    public Tier SelectTier(in PlanRequest request)
    {
        // 아키타입 플랜은 수천 NPC 가 재사용한다 → 품질에 투자한다.
        // 프리베이크와 런타임 미스가 같은 값이라 여기서 갈리지 않는다 (§4 표 1·2행).
        if (request.Quality == PlanQuality.Archetype)
        {
            return Tier.T2;
        }

        // 개별 재계획은 1회용이라 LOD 와 무관하게 T1 이다 (§4 표 3·4행).
        // 단, T1 이 밀려 있으면 T2 로 흘린다 (§4 표 5행 · T4-08).
        return LocalQueueDepth is { } depth && depth() > SpilloverThreshold ? Tier.T2 : Tier.T1;
    }

    /// <inheritdoc />
    public async ValueTask<PlanCompileResult> CompileAsync(
        PlanRequest request, CancellationToken cancellationToken)
    {
        Tick now = _now();
        Tier wanted = SelectTier(in request);

        // 개별 요청이 T2 로 갔다 = 스필오버다. 아키타입 요청은 원래 T2 라 세지 않는다.
        if (wanted == Tier.T2 && request.Quality != PlanQuality.Archetype)
        {
            Interlocked.Increment(ref _spillovers);
        }

        Tier granted = _budget.Acquire(wanted, Estimate(in request), now);

        if (granted == Tier.None)
        {
            Interlocked.Increment(ref _rejected);
            return RejectedResult(wanted);
        }

        if (granted == Tier.T1)
        {
            return await RunAsync(Tier.T1, request, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            PlanCompileResult result =
                await RunAsync(Tier.T2, request, cancellationToken).ConfigureAwait(false);

            // 호출이 서버에 닿았으면 그대로 돌려준다.
            // 검증 실패는 외부 장애가 아니라 모델 품질 문제라 페일오버 대상이 아니다.
            if (result.Stats.Reached)
            {
                return result;
            }

            return await FailoverAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;   // 종료 신호는 페일오버 대상이 아니다
        }
        catch (Exception)
        {
            return await FailoverAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- 내부

    private int Estimate(in PlanRequest request) =>
        EstimateTokens is { } estimate ? estimate(request) : DefaultEstimatedTokens;

    /// <summary>외부 장애 → 로컬. 예산은 다시 확보한다 — T2 분을 T1 으로 유용하지 않는다.</summary>
    private async ValueTask<PlanCompileResult> FailoverAsync(
        PlanRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _failovers);

        if (_budget.Acquire(Tier.T1, Estimate(in request), _now()) == Tier.None)
        {
            Interlocked.Increment(ref _rejected);
            return RejectedResult(Tier.T1);
        }

        return await RunAsync(Tier.T1, request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<PlanCompileResult> RunAsync(
        Tier tier, PlanRequest request, CancellationToken cancellationToken)
    {
        IPlanCompiler compiler;

        if (tier == Tier.T2)
        {
            compiler = _external;
            Interlocked.Increment(ref _t2Calls);
        }
        else
        {
            compiler = _local;
            Interlocked.Increment(ref _t1Calls);
        }

        int estimated = Estimate(in request);

        PlanCompileResult result = await compiler
            .CompileAsync(request, cancellationToken)
            .ConfigureAwait(false);

        // 추정과 실측의 차이를 정산한다. 정산을 빠뜨리면 캡이 실제 사용량과 어긋난다.
        int actual = result.Stats.PromptTokens + result.Stats.CompletionTokens;

        _budget.Settle(
            tier,
            actual - estimated,
            result.Stats.CostUsd - _budget.EstimateCost(tier, estimated));

        return result;
    }

    /// <summary>
    /// 예산이 없어 거절한 결과. <b>예외를 던지지 않는다</b> — 실패는 결과로 돌려준다
    /// (<see cref="IPlanCompiler"/>). 호출부(워커)는 기존 플랜을 그대로 둔다.
    /// </summary>
    private static PlanCompileResult RejectedResult(Tier wanted) => new(
        null,
        ValidationResult.Fail(
            ValidationStage.None,
            "T4.BUDGET_EXHAUSTED",
            -1,
            $"예산 소진으로 {wanted} 요청을 거절했다. NPC 는 기존 플랜을 유지한다 (docs/14 §3)."),
        CompileStats.None with { Error = "budget_exhausted" },
        string.Empty);
}
