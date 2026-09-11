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
///
/// <b>킬스위치는 예산보다 앞선다</b> (T5-08 · docs/15 §4). 시나리오 C 는 "끊었는데도 동작하는가"를
/// 보는 것이라, 끊긴 티어에 요청이 한 건이라도 나가면 게이트가 거짓이 된다 —
/// <see cref="SelectTier"/> 가 끊긴 티어를 애초에 고르지 않는다.
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
    private long _spilloverDeferred;
    private long _failovers;
    private long _spillovers;
    private long _localOutageSpillovers;

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
    /// 개체 스필오버 서브 쿼터 (C-07). null 이면 제한하지 않는다 — 지금까지의 동작이다.
    /// </summary>
    public SpilloverQuota? Quota { get; init; }

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

    /// <summary>서브 쿼터를 넘겨 T1 으로 돌린 개체 요청 수 (C-07).</summary>
    public long SpilloverDeferred => Interlocked.Read(ref _spilloverDeferred);

    /// <summary>
    /// T2 서킷 브레이커 (T4-09). 없으면 항상 닫혀 있다고 본다.
    /// 차단 중이면 T2 를 시도하지 않고 곧바로 T1 으로 간다 — 외부 장애에 지연을 태우지 않는다.
    /// </summary>
    public CircuitBreaker? Breaker { get; init; }

    /// <summary>
    /// 킬스위치 상태 (T5-08 · docs/15 §4). 시나리오 러너가 세우고 이 라우터가 읽는다.
    /// 기본값은 아무것도 끊기지 않은 상태다.
    /// </summary>
    public KillSwitchState Switches { get; init; } = KillSwitchState.None;

    /// <summary>
    /// T1 자리에 <b>진짜 로컬 컴파일러</b>가 들어 있는가 (T5-21).
    ///
    /// 조립부(<c>TierWiring</c>)는 한쪽 티어의 엔진이 없으면 없는 쪽을 있는 쪽으로 메꾼다 —
    /// 강등·페일오버가 같은 엔진으로 가게 하려는 의도다. 그러면 <c>--tier t2</c> 에서
    /// T1 자리에 T2 엔진이 앉는데, <b>메꾼 티어는 실제 티어의 차단을 물려받아야 한다.</b>
    /// 그러지 않으면 T2 를 끊어도 같은 외부 엔진이 T1 이름으로 계속 불려
    /// <see cref="KillSwitchTarget"/> 의 주석이 금지한 "안 끊긴 채로 통과" 가 된다.
    ///
    /// 기본값 <c>true</c> 는 "두 자리가 서로 다른 엔진이다" 는 뜻이다.
    /// </summary>
    public bool HasT1 { get; init; } = true;

    /// <summary>T2 자리에 <b>진짜 외부 컴파일러</b>가 들어 있는가. <see cref="HasT1"/> 참조.</summary>
    public bool HasT2 { get; init; } = true;

    /// <summary>
    /// 로컬 추론 프로세스가 살아 있는가 (C-08). null 이면 항상 살아 있다고 본다.
    ///
    /// <para>
    /// <b>죽었으면 T1 요청을 T2 로 흘린다.</b> 큐 폭주 스필오버(<see cref="SpilloverThreshold"/>)와
    /// 같은 장치이고 이유도 같다 — "늦게 오는 것보다 비싸게 오는 게 낫다". 다른 점은 원인이
    /// 우리 쪽 적체가 아니라 <b>사이드카가 죽은 것</b>이라 사람이 알아야 한다는 것뿐이다
    /// (<see cref="LocalEngineProbe.OnChange"/> 가 알린다).
    /// </para>
    ///
    /// <para>
    /// <b>킬스위치와 다르다.</b> 킬스위치는 사람이 끊은 것이고 이것은 프로세스가 죽은 것이다 —
    /// 섞으면 <c>/status</c> 의 킬스위치 목록이 "누가 끊었나" 에 답하지 못한다.
    /// </para>
    /// </summary>
    public Func<bool>? LocalHealthy { get; init; }

    /// <summary>
    /// 로컬 엔진이 죽어 T2 로 흘린 요청 수 (C-08).
    /// <b>0 이 아니면 사이드카가 죽어 있었다</b> — 그동안 T1 단가로 살 것을 T2 단가로 샀다.
    /// </summary>
    public long LocalOutageSpillovers => Interlocked.Read(ref _localOutageSpillovers);

    /// <summary>
    /// 이 티어를 지금 쓸 수 있는가. <b>킬스위치만 본다</b> —
    /// 브레이커는 일시적 차단이라 선택 시점이 아니라 호출 직전에 보고(<see cref="CircuitBreaker.TryEnter"/>)
    /// 단락 횟수를 센다. 여기서 같이 보면 그 계수가 사라진다.
    ///
    /// <b>메꾼 자리는 원 티어의 차단도 같이 본다</b> (T5-21 · docs/15 §E) —
    /// 자기 자리가 안 끊겼더라도, 그 자리에 앉은 것이 남의 엔진이면 그쪽 차단이 그대로 걸린다.
    /// </summary>
    public bool Available(Tier tier) => tier switch
    {
        Tier.T2 => !Switches.IsDisabled(KillSwitchTarget.T2)
            && (HasT2 || !Switches.IsDisabled(KillSwitchTarget.T1)),
        Tier.T1 => !Switches.IsDisabled(KillSwitchTarget.T1)
            && (HasT1 || !Switches.IsDisabled(KillSwitchTarget.T2)),
        _ => false,
    };

    /// <summary>
    /// docs/14 §4 의 티어 선택 규칙. <b>예산은 보지 않는다</b> —
    /// 예산 판정과 강등은 <see cref="IReplanBudget.Acquire"/> 의 몫이다.
    /// 끊긴 티어는 고르지 않는다 (docs/15 §4).
    /// </summary>
    public Tier SelectTier(in PlanRequest request) => Downgrade(Wanted(in request));

    private Tier Wanted(in PlanRequest request)
    {
        // 아키타입 플랜은 수천 NPC 가 재사용한다 → 품질에 투자한다.
        // 프리베이크와 런타임 미스가 같은 값이라 여기서 갈리지 않는다 (§4 표 1·2행).
        if (request.Quality == PlanQuality.Archetype)
        {
            return Tier.T2;
        }

        // C-08 — 로컬 프로세스가 죽었으면 T1 으로 보내 봐야 타임아웃뿐이다. T2 로 흘린다.
        if (LocalHealthy is { } healthy && !healthy() && Available(Tier.T2))
        {
            Interlocked.Increment(ref _localOutageSpillovers);
            return Tier.T2;
        }

        // 개별 재계획은 1회용이라 LOD 와 무관하게 T1 이다 (§4 표 3·4행).
        // 단, T1 이 밀려 있으면 T2 로 흘린다 (§4 표 5행 · T4-08).
        return LocalQueueDepth is { } depth && depth() > SpilloverThreshold ? Tier.T2 : Tier.T1;
    }

    /// <summary>
    /// 끊긴 티어를 한 칸 내린다. <c>T2 → T1 → 거절</c> (docs/14 §3).
    ///
    /// <b>올리지는 않는다.</b> T1 차단은 "재계획 전면 중단, 캐시 플랜만 남는다" 는 뜻이라
    /// (<see cref="KillSwitchTarget.T1"/>) 개별 재계획을 T2 로 올려 보내면 시나리오 C 2단계가
    /// 측정하려던 것이 사라진다.
    /// </summary>
    private Tier Downgrade(Tier wanted)
    {
        if (wanted == Tier.T2 && Available(Tier.T2))
        {
            return Tier.T2;
        }

        return Available(Tier.T1) ? Tier.T1 : Tier.None;
    }

    /// <inheritdoc />
    public async ValueTask<PlanCompileResult> CompileAsync(
        PlanRequest request, CancellationToken cancellationToken)
    {
        Tick now = _now();
        Tier wanted = SelectTier(in request);

        // 킬스위치가 T1·T2 를 다 끊었다. 예산을 보기 전에 거절한다 —
        // 끊긴 티어에 요청을 내는 경로를 남기지 않는다 (docs/15 §4).
        if (wanted == Tier.None)
        {
            Interlocked.Increment(ref _rejected);
            return NoTierResult();
        }

        // 개별 요청이 T2 로 갔다 = 스필오버다. 아키타입 요청은 원래 T2 라 세지 않는다.
        if (wanted == Tier.T2 && request.Quality != PlanQuality.Archetype)
        {
            Interlocked.Increment(ref _spillovers);

            // 서브 쿼터 (C-07). 넘으면 <b>거절이 아니라 T1 대기</b>다 —
            // 개별 재계획은 급하지 않고, 그 NPC 는 기존 플랜을 계속 쓰면 된다.
            //
            // 없으면 개별 스필오버가 하루 예산(약 649건)을 몇 분 만에 태우고, 정작
            // 수천 NPC 가 공유하는 버킷 미스 보충이 굶는다.
            if (Quota is { } quota
                && !quota.TryUseT2(ReplanAccount.Individual, Estimate(in request), now)
                && Available(Tier.T1))
            {
                wanted = Tier.T1;
                Interlocked.Increment(ref _spilloverDeferred);
            }
        }
        else if (wanted == Tier.T2 && Quota is { } bucketQuota)
        {
            bucketQuota.TryUseT2(ReplanAccount.Bucket, Estimate(in request), now);
        }

        // 브레이커가 차단 중이면 시도하지 않는다. 무한 재시도는 외부 장애 시 지연을 폭발시킨다
        // (docs/14 §10). 예산도 여기서 아껴진다 — 죽은 엔드포인트에 T2 예산을 쓰지 않는다.
        if (wanted == Tier.T2 && Breaker is { } breaker && !breaker.TryEnter(now))
        {
            // T1 이 끊겨 있으면 내려갈 곳이 없다.
            if (!Available(Tier.T1))
            {
                Interlocked.Increment(ref _rejected);
                return NoTierResult();
            }

            wanted = Tier.T1;
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
                Breaker?.RecordSuccess();
                return result;
            }

            Breaker?.RecordFailure(now);
            return await FailoverAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // 호출자가 지시한 종료다. 페일오버 대상이 아니고 브레이커에도 세지 않는다
        }
        catch (Exception)
        {
            // <b>취소 예외를 종류만 보고 거르지 않는다.</b> HttpClient 의 타임아웃도
            // TaskCanceledException 이라, 종류로 거르면 외부 타임아웃이 워커를 뚫고 나가고
            // 브레이커는 영영 열리지 않는다 (docs/14 §10).
            Breaker?.RecordFailure(now);
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
        // T1 이 끊겨 있으면 페일오버할 곳이 없다 (docs/15 §4). 페일오버로 세지도 않는다 —
        // 넘긴 적이 없기 때문이다.
        if (!Available(Tier.T1))
        {
            Interlocked.Increment(ref _rejected);
            return NoTierResult();
        }

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

    /// <summary>
    /// 킬스위치가 쓸 티어를 다 끊었다 (T5-08). <c>V0.CALL_FAILED</c> 와 같은 자리에 둔다 —
    /// 스키마 단계 실패로 세어야 통과율 분모가 줄지 않는다 (docs/12 §2).
    /// </summary>
    private static PlanCompileResult NoTierResult() => new(
        null,
        ValidationResult.Fail(
            ValidationStage.Schema,
            "V0.NO_TIER",
            -1,
            "쓸 수 있는 티어가 없다 — 킬스위치가 T1·T2 를 끊었다 (docs/15 §4)."),
        CompileStats.None with { Error = "no_tier" },
        string.Empty);
}
