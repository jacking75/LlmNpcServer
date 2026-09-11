using System.Collections.Immutable;

namespace Npc.Eval.Core;

/// <summary>
/// 평가 대상 플랜 하나 (C-04).
///
/// <para>
/// <b>계약 타입을 쓰지 않는다.</b> <c>BucketOutcome</c>·<c>CompileStats</c> 는
/// <c>Npc.Prebake</c>·<c>Npc.Llm</c> 에 있고, 판정 로직이 그것에 묶이면 <b>가짜 엔진으로
/// 파이프라인을 돌릴 수 없다</b> — 게이트를 테스트하려고 LLM 을 불러야 하는 상태가 된다.
/// 그래서 여기서 필요한 숫자만 평평한 레코드로 받는다.
/// </para>
/// </summary>
/// <param name="Archetype">아키타입 id. 아키타입 안 다양성의 묶음 키다.</param>
/// <param name="Bucket">버킷 이름. 사람이 읽는 좌표다.</param>
/// <param name="Ok">4단 검증을 통과했는가.</param>
/// <param name="Origin">플랜 출처. <c>Fallback</c> 이면 생성이 아니라 폴백으로 떨어진 것이다.</param>
/// <param name="Goal">플랜의 goal.</param>
/// <param name="Actions">액션 시퀀스. <b>다양성의 실체다</b> — 빈 문자열이면 분모에서 뺀다.</param>
/// <param name="FailStage">검증 실패 단계. 통과면 빈 문자열.</param>
/// <param name="FailCode">실패 코드. 통과면 빈 문자열.</param>
/// <param name="FailStep">실패한 스텝 첨자. 통과거나 스텝 밖이면 -1.</param>
/// <param name="Attempt">시도 횟수. 0 이면 <b>던지지도 않았다</b> — 분모에서 뺀다.</param>
/// <param name="CostUsd">이 버킷에 든 비용(재시도 포함).</param>
/// <param name="LatencyMs">이 버킷에 든 지연(재시도 포함).</param>
/// <param name="PromptTokens">입력 토큰 누계.</param>
/// <param name="CachedTokens">그중 캐시 적중 토큰.</param>
public readonly record struct PlanSample(
    string Archetype,
    string Bucket,
    bool Ok,
    string Origin,
    string Goal,
    string Actions,
    string FailStage,
    string FailCode,
    int FailStep,
    int Attempt,
    double CostUsd,
    double LatencyMs,
    long PromptTokens,
    long CachedTokens)
{
    /// <summary>던지기는 했는가. 예산 캡에 걸려 시도조차 못한 버킷을 가른다.</summary>
    public bool Attempted => Attempt > 0;

    /// <summary>
    /// 다양성 분모에 들어가는가.
    ///
    /// <b>폴백은 뺀다.</b> 사람이 쓴 같은 플랜이라 분모에 넣으면 지표가 거짓으로 낮아진다 —
    /// 질문은 "LLM 이 상황마다 다른 플랜을 내는가" 다.
    /// </summary>
    public bool CountsForDiversity => Ok && Actions.Length > 0;
}

/// <summary>
/// 엔진 하나의 회차 결과 (C-04).
/// </summary>
/// <param name="Engine">엔진 id. <c>appsettings.Llm.json</c> 의 이름이다.</param>
/// <param name="Model">실제로 응답한 모델 이름. 엔진 id 와 다를 수 있다.</param>
/// <param name="Samples">버킷별 결과.</param>
/// <param name="WallClockSeconds">전체 소요.</param>
/// <param name="Golden">골든 회귀 단언 합격률 0~1. 안 돌렸으면 -1.</param>
/// <param name="GoldenAssertions">평가한 단언 수. 안 돌렸으면 0.</param>
/// <param name="Judge">LLM 심사원 평균 점수 1~5. 안 돌렸으면 -1. <b>권고 신호이지 게이트가 아니다.</b></param>
/// <param name="StoppedByBudget">예산 캡에 걸려 중단됐는가. 서면 통과율이 표본을 대표하지 않는다.</param>
public sealed record EngineRun(
    string Engine,
    string Model,
    ImmutableArray<PlanSample> Samples,
    double WallClockSeconds,
    double Golden = -1,
    int GoldenAssertions = 0,
    double Judge = -1,
    bool StoppedByBudget = false)
{
    /// <summary>던진 버킷 수.</summary>
    public int Attempted => Samples.Count(s => s.Attempted);

    /// <summary>검증까지 통과한 수.</summary>
    public int Passed => Samples.Count(s => s.Attempted && s.Ok);

    /// <summary>폴백으로 떨어진 수.</summary>
    public int FellBack => Samples.Count(
        s => s.Attempted && string.Equals(s.Origin, "Fallback", StringComparison.Ordinal));

    /// <summary>통과율. 던진 것 기준이다.</summary>
    public double PassRate => Attempted == 0 ? 0 : (double)Passed / Attempted;

    /// <summary>폴백률.</summary>
    public double FallbackRate => Attempted == 0 ? 0 : (double)FellBack / Attempted;

    /// <summary>총 비용.</summary>
    public double CostUsd => Samples.Sum(s => s.CostUsd);

    /// <summary>요청당 비용. 던진 것이 없으면 0.</summary>
    public double CostPerRequest => Attempted == 0 ? 0 : CostUsd / Attempted;

    /// <summary>요청당 평균 지연(ms).</summary>
    public double AverageLatencyMs => Attempted == 0 ? 0 : Samples.Sum(s => s.LatencyMs) / Attempted;

    /// <summary>입력 토큰 누계.</summary>
    public long PromptTokens => Samples.Sum(s => s.PromptTokens);

    /// <summary>프롬프트 캐시 적중률. 토큰이 0 이면 0.</summary>
    public double CacheHitRate =>
        PromptTokens == 0 ? 0 : (double)Samples.Sum(s => s.CachedTokens) / PromptTokens;
}
