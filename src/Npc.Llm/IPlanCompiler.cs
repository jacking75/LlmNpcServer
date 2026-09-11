using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;

namespace Npc.Llm;

/// <summary>
/// 호출 한 번의 계측값. docs/12 §2.
///
/// <b>모든 호출에서 기록한다.</b> P3 의 프리베이크 manifest 와 W12 보고서의 원자료가 여기서 나온다.
/// 실패한 호출도 기록한다 — 실패만 빠지면 통과율 분모가 조용히 줄어든다.
/// </summary>
/// <param name="PromptTokens">입력 토큰 (캐시 적중분 포함).</param>
/// <param name="CachedTokens">그중 프롬프트 캐시가 적중한 토큰. 로컬 엔진은 항상 0 이다 (W1_env.md §4.4).</param>
/// <param name="CompletionTokens">출력 토큰.</param>
/// <param name="LatencyMs">요청 전체 소요.</param>
/// <param name="CostUsd">이 호출의 비용.</param>
/// <param name="Model">엔진 id.</param>
/// <param name="Attempt">몇 번째 시도인가. 1 부터. 재시도는 <b>1회만</b> (docs/12 §6).</param>
/// <param name="PrefixSha">쓰인 프리픽스의 SHA-256. <b>유니크 해시가 2개 이상이면 캐시가 깨진 것이다.</b></param>
/// <param name="Forced"><c>response_format=json_schema</c> 로 강제했는가. 두 모드 통과율 비교용.</param>
/// <param name="Error">호출 자체가 실패했으면 그 사유. 성공이면 null.</param>
public readonly record struct CompileStats(
    int PromptTokens,
    int CachedTokens,
    int CompletionTokens,
    double LatencyMs,
    double CostUsd,
    string Model,
    int Attempt,
    string PrefixSha,
    bool Forced,
    string? Error)
{
    /// <summary>아무 호출도 하지 않은 상태.</summary>
    public static CompileStats None { get; } =
        new(0, 0, 0, 0, 0, string.Empty, 0, string.Empty, false, null);

    /// <summary>호출이 서버에 닿았는가.</summary>
    public bool Reached => Error is null;

    /// <summary>
    /// 재시도까지 합친 누계. 토큰·지연·비용은 더하고 시도 횟수는 큰 쪽을 남긴다 —
    /// 비용 보고는 "이 버킷 하나에 얼마 들었나"여야 하지 마지막 호출값이 아니다.
    /// </summary>
    public CompileStats Accumulate(in CompileStats next) => new(
        PromptTokens + next.PromptTokens,
        CachedTokens + next.CachedTokens,
        CompletionTokens + next.CompletionTokens,
        LatencyMs + next.LatencyMs,
        CostUsd + next.CostUsd,
        string.IsNullOrEmpty(next.Model) ? Model : next.Model,
        Math.Max(Attempt, next.Attempt),
        string.IsNullOrEmpty(next.PrefixSha) ? PrefixSha : next.PrefixSha,
        Forced || next.Forced,
        next.Error ?? Error);
}

/// <summary>
/// 플랜 생성 결과. docs/12 §2.
/// </summary>
/// <param name="Plan">
/// 컴파일된 플랜. 검증을 통과하지 못했으면 null 이다.
/// <see cref="CompiledPlan.Id"/> 는 <c>default</c> — 진짜 id 는 <c>PlanStore.Register</c> 가 박는다.
/// </param>
/// <param name="Validation">최종 검증 결과. 통과면 <see cref="ValidationResult.IsValid"/> 가 참이다.</param>
/// <param name="Stats">계측값 (재시도 누계).</param>
/// <param name="ResponseText">
/// 모델이 뱉은 원문(울타리를 벗긴 뒤). 검수·<c>planstore/rejected/</c> 보존용이다 —
/// 실패 산출물을 조용히 버리면 품질 개선의 원자료가 사라진다 (docs/13 §8).
/// </param>
/// <param name="Repairs">
/// 결정론 수선이 적용된 횟수 (C-05). 0 이면 모델이 낸 그대로 통과했다.
///
/// <b>통과율과 따로 센다.</b> "수선 전 실패율" 과 "수선 후 실패율" 을 가르지 않으면
/// 수선 규칙이 얼마나 벌어 주는지, 혹은 나쁜 플랜을 통과시키고 있는지 알 수 없다.
/// </param>
public readonly record struct PlanCompileResult(
    CompiledPlan? Plan,
    ValidationResult Validation,
    CompileStats Stats,
    string ResponseText,
    int Repairs = 0);

/// <summary>
/// 인접 버킷 재사용의 공급원. docs/12 §6·§7.
///
/// 구현(<c>BucketNeighbors</c>·<c>PlanStore</c>)은 <c>Npc.Planning</c> 에 있고
/// 이 프로젝트는 그것을 참조하지 않는다 (CLAUDE.md §3). <see cref="IDryRunValidator"/> 와 같은 방법이다.
/// </summary>
public interface IPlanReuseSource
{
    /// <summary>이 버킷을 채울 만한 인접 버킷 플랜이 있으면 준다. <b>이미 재검증된 것이어야 한다.</b></summary>
    bool TryReuse(BucketKey target, out CompiledPlan? plan, out BucketKey source);
}

/// <summary>
/// 플랜 생성 계약. docs/12 §2.
///
/// <b>구현체 이름은 <see cref="LlmPlanCompiler"/> 다.</b> <c>docs/12 §2</c> 가 쓴 <c>PlanCompiler</c> 는
/// 이미 <c>Npc.Core.Plan.PlanCompiler</c>(PlanDocument ↔ CompiledPlan)가 쓰고 있어서,
/// 같은 이름을 두면 이 프로젝트에서 그 타입이 가려진다.
/// </summary>
public interface IPlanCompiler
{
    /// <summary>요청 하나로 플랜을 만든다. <b>예외를 던지지 않는다</b> — 실패는 결과로 돌려준다.</summary>
    ValueTask<PlanCompileResult> CompileAsync(PlanRequest request, CancellationToken cancellationToken);
}
