using System.Text.Json;
using System.Text.Json.Serialization;

namespace Npc.Core.Plan;

/// <summary>스텝 실패 시 정책. docs/03 §2 의 <c>on_step_fail</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StepFailPolicy>))]
public enum StepFailPolicy
{
    /// <summary>아키타입 폴백 플랜으로 전환한다. 기본값.</summary>
    [JsonStringEnumMemberName("fallback")]
    Fallback = 0,

    /// <summary>같은 스텝을 한 번만 다시 시도한다.</summary>
    [JsonStringEnumMemberName("retry_once")]
    RetryOnce = 1,

    /// <summary>그 스텝을 건너뛰고 다음으로 간다.</summary>
    [JsonStringEnumMemberName("skip")]
    Skip = 2,

    /// <summary>재계획 큐에 넣는다.</summary>
    [JsonStringEnumMemberName("replan")]
    Replan = 3,
}

/// <summary>플랜 스텝 하나. docs/03 §1.</summary>
public sealed record PlanStep
{
    /// <summary>액션 id. actions.json 의 id 문자열.</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }

    /// <summary>
    /// 액션 파라미터. 스키마 단계에서는 object 로만 두고,
    /// 액션별 파라미터 검증은 검증기 2단이 한다 (docs/03 §2).
    /// </summary>
    [JsonPropertyName("args")]
    public required Dictionary<string, JsonElement> Args { get; init; }

    /// <summary>
    /// 이 스텝의 타임아웃(초). 없으면 액션의 default_timeout_s 를 쓴다.
    /// <b>모든 스텝에 타임아웃이 있어야 한다</b> — 완료 이벤트를 못 받으면 NPC 가 영구 정지한다.
    /// </summary>
    [JsonPropertyName("timeout_s")]
    public int? TimeoutSeconds { get; init; }
}

/// <summary>
/// LLM 이 생성하는 유일한 산출물. docs/03 §1.
/// <b>자연어는 한 글자도 없다.</b> <see cref="Reasoning"/> 만 예외이며 런타임이 무시한다.
///
/// 이 모델은 (1) 강제 디코딩의 입력, (2) 검증기의 기준, (3) 컴파일러의 입력 —
/// 세 곳에서 동시에 쓰인다.
/// </summary>
public sealed record PlanDocument
{
    /// <summary>스키마 버전. 항상 1.</summary>
    [JsonPropertyName("schema")]
    public required int Schema { get; init; }

    /// <summary>목표 식별자. <c>^[a-z][a-z0-9_]{2,31}$</c>.</summary>
    [JsonPropertyName("goal")]
    public required string Goal { get; init; }

    /// <summary>
    /// 왜 이 플랜인가. <b>디버깅·검수 전용이며 런타임이 무시한다.</b>
    /// 200자 상한 — 여기가 길어지면 토큰만 태운다.
    /// </summary>
    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; init; }

    /// <summary>스텝. 3~10개. 선형 시퀀스다 — 분기도 반복문도 없다.</summary>
    [JsonPropertyName("steps")]
    public required PlanStep[] Steps { get; init; }

    /// <summary>스텝 실패 시 정책.</summary>
    [JsonPropertyName("on_step_fail")]
    public StepFailPolicy OnStepFail { get; init; } = StepFailPolicy.Fallback;

    /// <summary>마지막 스텝 뒤에 첫 스텝으로 돌아가는가. 폴백 플랜은 반드시 true 다.</summary>
    [JsonPropertyName("loop")]
    public required bool Loop { get; init; }

    /// <summary>docs/03 §2 가 정한 스텝 수 하한.</summary>
    public const int MinSteps = 3;

    /// <summary>docs/03 §2 가 정한 스텝 수 상한. 넘으면 실행기의 StepIndex(byte)도 위험해진다.</summary>
    public const int MaxSteps = 10;

    /// <summary>docs/03 §2 가 정한 timeout_s 하한.</summary>
    public const int MinTimeoutSeconds = 5;

    /// <summary>docs/03 §2 가 정한 timeout_s 상한.</summary>
    public const int MaxTimeoutSeconds = 7_200;

    /// <summary>docs/03 §1 이 정한 reasoning 길이 상한.</summary>
    public const int MaxReasoningLength = 200;
}
