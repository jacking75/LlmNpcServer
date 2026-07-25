using System.Text.Json;
using System.Text.RegularExpressions;
using Npc.Core.Plan;

namespace Npc.Core.Validation;

/// <summary>검증 단계. docs/03 §3.</summary>
public enum ValidationStage
{
    /// <summary>통과.</summary>
    None = 0,

    /// <summary>1단 — 스키마.</summary>
    Schema = 1,

    /// <summary>2단 — 어휘.</summary>
    Vocabulary = 2,

    /// <summary>3단 — 정합성.</summary>
    Coherence = 3,

    /// <summary>4단 — 드라이런. P2 에서 구현한다.</summary>
    DryRun = 4,
}

/// <summary>검증 결과. docs/03 §3.</summary>
/// <param name="FailedAt">실패한 단계. <see cref="ValidationStage.None"/> 이면 통과.</param>
/// <param name="Code">"V2.UNKNOWN_POI" 같은 실패 코드.</param>
/// <param name="StepIndex">몇 번째 스텝인가. -1 = 문서 전체.</param>
/// <param name="Detail">재시도 프롬프트에 실릴 설명. <b>비트마스크 숫자를 그대로 쓰지 않는다.</b></param>
public readonly record struct ValidationResult(
    ValidationStage FailedAt,
    string Code,
    int StepIndex,
    string Detail)
{
    /// <summary>통과.</summary>
    public static ValidationResult Ok { get; } = new(ValidationStage.None, string.Empty, -1, string.Empty);

    /// <summary>통과했는가.</summary>
    public bool IsValid => FailedAt == ValidationStage.None;

    /// <summary>실패 하나 만들기.</summary>
    public static ValidationResult Fail(ValidationStage stage, string code, int stepIndex, string detail) =>
        new(stage, code, stepIndex, detail);
}

/// <summary>
/// 검증기 1단 — 스키마. docs/03 §3.
///
/// <b>강제 디코딩을 신뢰하지 않고 재검증한다.</b> 외부 API 의 strict 지원 수준이 제공사마다 다르다
/// (CLAUDE.md §2.6). 여기를 건너뛰면 2단·3단이 이상한 입력에서 터진다.
/// </summary>
public static partial class SchemaValidator
{
    /// <summary>문서 최상위에 허용되는 필드. docs/03 §2 의 additionalProperties:false.</summary>
    private static readonly string[] s_documentFields =
        ["schema", "goal", "reasoning", "loop", "on_step_fail", "steps"];

    /// <summary>스텝에 허용되는 필드.</summary>
    private static readonly string[] s_stepFields = ["action", "args", "timeout_s"];

    /// <summary>on_step_fail 의 허용값.</summary>
    private static readonly string[] s_failPolicies = ["fallback", "retry_once", "skip", "replan"];

    [GeneratedRegex("^[a-z][a-z0-9_]{2,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex GoalPattern();

    /// <summary>JSON 문자열을 검증하고 파싱한다.</summary>
    public static ValidationResult Validate(string json, out PlanDocument? document)
    {
        document = null;

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return ValidationResult.Fail(ValidationStage.Schema, "V1.PARSE", -1, ex.Message);
        }

        using (parsed)
        {
            ValidationResult structure = ValidateStructure(parsed.RootElement);
            if (!structure.IsValid)
            {
                return structure;
            }
        }

        // 구조가 맞으면 소스 생성 컨텍스트로 모델을 만든다.
        try
        {
            document = JsonSerializer.Deserialize(json, PlanJsonContext.Default.PlanDocument);
        }
        catch (JsonException ex)
        {
            return ValidationResult.Fail(ValidationStage.Schema, "V1.SCHEMA", -1, ex.Message);
        }

        return document is null
            ? ValidationResult.Fail(ValidationStage.Schema, "V1.PARSE", -1, "문서가 null 이다.")
            : ValidationResult.Ok;
    }

    private static ValidationResult ValidateStructure(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ValidationResult.Fail(
                ValidationStage.Schema, "V1.SCHEMA", -1, $"최상위가 객체가 아니다: {root.ValueKind}");
        }

        // --- 추가 필드 (additionalProperties: false) ---
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!s_documentFields.Contains(property.Name, StringComparer.Ordinal))
            {
                return ValidationResult.Fail(
                    ValidationStage.Schema, "V1.EXTRA_FIELD", -1,
                    $"스키마에 없는 필드 '{property.Name}'. 허용: {string.Join(", ", s_documentFields)}");
            }
        }

        // --- 필수 필드 ---
        foreach (string required in new[] { "schema", "goal", "steps", "loop" })
        {
            if (!root.TryGetProperty(required, out _))
            {
                return ValidationResult.Fail(
                    ValidationStage.Schema, "V1.SCHEMA", -1, $"필수 필드 '{required}' 가 없다.");
            }
        }

        // --- schema ---
        JsonElement schema = root.GetProperty("schema");
        if (schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1)
        {
            return ValidationResult.Fail(ValidationStage.Schema, "V1.SCHEMA", -1, $"schema 는 1 이어야 한다: {schema}");
        }

        // --- goal ---
        JsonElement goal = root.GetProperty("goal");
        if (goal.ValueKind != JsonValueKind.String || !GoalPattern().IsMatch(goal.GetString()!))
        {
            return ValidationResult.Fail(
                ValidationStage.Schema, "V1.SCHEMA", -1,
                $"goal 이 ^[a-z][a-z0-9_]{{2,31}}$ 를 만족하지 않는다: {goal}");
        }

        // --- loop ---
        JsonElement loop = root.GetProperty("loop");
        if (loop.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return ValidationResult.Fail(ValidationStage.Schema, "V1.SCHEMA", -1, $"loop 이 불리언이 아니다: {loop}");
        }

        // --- reasoning ---
        if (root.TryGetProperty("reasoning", out JsonElement reasoning))
        {
            if (reasoning.ValueKind != JsonValueKind.String)
            {
                return ValidationResult.Fail(
                    ValidationStage.Schema, "V1.SCHEMA", -1, $"reasoning 이 문자열이 아니다: {reasoning}");
            }

            if (reasoning.GetString()!.Length > PlanDocument.MaxReasoningLength)
            {
                return ValidationResult.Fail(
                    ValidationStage.Schema, "V1.SCHEMA", -1,
                    $"reasoning 이 {reasoning.GetString()!.Length}자다. {PlanDocument.MaxReasoningLength}자 이하여야 한다.");
            }
        }

        // --- on_step_fail ---
        if (root.TryGetProperty("on_step_fail", out JsonElement onFail))
        {
            if (onFail.ValueKind != JsonValueKind.String
                || !s_failPolicies.Contains(onFail.GetString()!, StringComparer.Ordinal))
            {
                return ValidationResult.Fail(
                    ValidationStage.Schema, "V1.SCHEMA", -1,
                    $"on_step_fail 이 {string.Join("/", s_failPolicies)} 중 하나가 아니다: {onFail}");
            }
        }

        // --- steps ---
        JsonElement steps = root.GetProperty("steps");
        if (steps.ValueKind != JsonValueKind.Array)
        {
            return ValidationResult.Fail(ValidationStage.Schema, "V1.SCHEMA", -1, "steps 가 배열이 아니다.");
        }

        int count = steps.GetArrayLength();
        if (count is < PlanDocument.MinSteps or > PlanDocument.MaxSteps)
        {
            return ValidationResult.Fail(
                ValidationStage.Schema, "V1.STEP_COUNT", -1,
                $"스텝이 {count}개다. {PlanDocument.MinSteps}~{PlanDocument.MaxSteps} 이어야 한다.");
        }

        int index = 0;
        foreach (JsonElement step in steps.EnumerateArray())
        {
            ValidationResult result = ValidateStep(step, index++);
            if (!result.IsValid)
            {
                return result;
            }
        }

        return ValidationResult.Ok;
    }

    private static ValidationResult ValidateStep(JsonElement step, int index)
    {
        if (step.ValueKind != JsonValueKind.Object)
        {
            return ValidationResult.Fail(
                ValidationStage.Schema, "V1.SCHEMA", index, $"스텝이 객체가 아니다: {step.ValueKind}");
        }

        foreach (JsonProperty property in step.EnumerateObject())
        {
            if (!s_stepFields.Contains(property.Name, StringComparer.Ordinal))
            {
                return ValidationResult.Fail(
                    ValidationStage.Schema, "V1.EXTRA_FIELD", index,
                    $"스텝에 스키마에 없는 필드 '{property.Name}'. 허용: {string.Join(", ", s_stepFields)}");
            }
        }

        if (!step.TryGetProperty("action", out JsonElement action) || action.ValueKind != JsonValueKind.String)
        {
            return ValidationResult.Fail(ValidationStage.Schema, "V1.SCHEMA", index, "스텝에 action 문자열이 없다.");
        }

        if (!step.TryGetProperty("args", out JsonElement args) || args.ValueKind != JsonValueKind.Object)
        {
            return ValidationResult.Fail(ValidationStage.Schema, "V1.SCHEMA", index, "스텝에 args 객체가 없다.");
        }

        if (step.TryGetProperty("timeout_s", out JsonElement timeout))
        {
            if (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetInt32(out int seconds))
            {
                return ValidationResult.Fail(
                    ValidationStage.Schema, "V1.SCHEMA", index, $"timeout_s 가 정수가 아니다: {timeout}");
            }

            if (seconds is < PlanDocument.MinTimeoutSeconds or > PlanDocument.MaxTimeoutSeconds)
            {
                return ValidationResult.Fail(
                    ValidationStage.Schema, "V1.SCHEMA", index,
                    $"timeout_s 가 {seconds} 다. {PlanDocument.MinTimeoutSeconds}~{PlanDocument.MaxTimeoutSeconds} 이어야 한다.");
            }
        }

        return ValidationResult.Ok;
    }
}
