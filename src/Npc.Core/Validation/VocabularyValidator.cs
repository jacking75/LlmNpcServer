using System.Text.Json;
using Npc.Contracts;
using Npc.Core.Plan;

namespace Npc.Core.Validation;

/// <summary>
/// 검증기 2단·3단이 필요로 하는 어휘. docs/03 §3.
///
/// <see cref="IPlanVocabulary"/> 와 마찬가지로, <c>Npc.Core</c> 가 <c>Npc.MasterData</c> 를
/// 참조할 수 없어서(CLAUDE.md §3) Core 가 선언하고 MasterData 가 구현한다.
/// <see cref="ValidateArgs"/> 를 어휘 쪽에 둔 이유는 액션별 파라미터 정의를 아는 쪽이 거기이기 때문이다.
/// </summary>
public interface IPlanValidationVocabulary : IPlanVocabulary
{
    /// <summary>이 아키타입이 그 액션을 쓸 수 있는가 (archetypes.json 의 allowed_actions).</summary>
    bool IsActionAllowed(ArchetypeId archetype, ActionId action);

    /// <summary>스텝 인자 검증. V2.UNKNOWN_ARG / MISSING_REQUIRED_ARG / TYPE_MISMATCH / UNKNOWN_POI / UNKNOWN_ITEM / UNKNOWN_RECIPE / RANGE.</summary>
    ValidationResult ValidateArgs(
        ActionId action, int stepIndex, IReadOnlyDictionary<string, JsonElement> args);

    /// <summary>버킷이 함의하는 초기 상태. 검증기 3단이 여기서 시작한다 (docs/03 §3).</summary>
    WorldFlags InitialFlags(BucketKey bucket);
}

/// <summary>
/// 검증기 2단 — 어휘. docs/03 §3.
/// 액션이 카탈로그에 있는가, 이 아키타입이 쓸 수 있는가, 인자가 파라미터 정의에 맞는가.
/// </summary>
public static class VocabularyValidator
{
    /// <summary>플랜의 모든 스텝을 검사한다. 첫 실패에서 멈춘다 (재시도 프롬프트에는 하나면 충분하다).</summary>
    public static ValidationResult Validate(
        PlanDocument document, ArchetypeId archetype, IPlanValidationVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(vocabulary);

        for (int i = 0; i < document.Steps.Length; i++)
        {
            PlanStep step = document.Steps[i];

            if (!vocabulary.TryGetAction(step.Action, out ActionId action))
            {
                return ValidationResult.Fail(
                    ValidationStage.Vocabulary, "V2.UNKNOWN_ACTION", i,
                    $"'{step.Action}' 은 액션 카탈로그에 없다.");
            }

            if (!vocabulary.IsActionAllowed(archetype, action))
            {
                return ValidationResult.Fail(
                    ValidationStage.Vocabulary, "V2.ACTION_NOT_ALLOWED", i,
                    $"'{step.Action}' 은 이 아키타입의 allowed_actions 에 없다.");
            }

            ValidationResult args = vocabulary.ValidateArgs(action, i, step.Args);
            if (!args.IsValid)
            {
                return args;
            }
        }

        return ValidationResult.Ok;
    }
}
