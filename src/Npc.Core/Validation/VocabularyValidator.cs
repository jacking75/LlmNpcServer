using System.Collections.Immutable;
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

    /// <summary>
    /// 이 액션이 <c>NpcArrived</c> 로 완료되는가.
    /// 참이면 도착한 POI 심볼이 함의하는 장소 플래그가 선다 —
    /// docs/01 §2.2 가 <c>MoveTo</c> 의 grants 를 "(POI별)" 이라고 쓴 부분이다.
    /// </summary>
    bool CompletesOnArrival(ActionId action);

    /// <summary>이 아키타입이 그 심볼을 바인딩할 수 있는가. V3.UNREACHABLE_POI 가 쓴다.</summary>
    bool CanBindSymbol(ArchetypeId archetype, PoiSymbol symbol);

    /// <summary>레시피의 입력 자원. 없으면 false. V3.RESOURCE_IMBALANCE 가 쓴다.</summary>
    bool TryGetRecipeInputs(ItemId recipe, out ImmutableArray<PlanRecipeInput> inputs);

    /// <summary>
    /// 아이템 code → 문자열 id.
    ///
    /// <b>이게 없으면 <c>V3.RESOURCE_IMBALANCE</c> 의 설명이 아이템을 숫자로 찍는다</b> —
    /// "숫자를 그대로 내보내면 모델이 못 고친다"(docs/12 §5)에 정확히 걸리는 경우다.
    /// </summary>
    string ItemName(ItemId item);

    /// <summary>이 액션이 자원을 인벤토리에 넣는가(채집·수령). V3.RESOURCE_IMBALANCE 가 쓴다.</summary>
    bool ProducesItem(ActionId action);

    /// <summary>
    /// 이 아이템을 하나 이상 가지면 서는 플래그 (<c>items.json</c> 의 grants).
    ///
    /// <b>수령 액션(<c>Withdraw</c>·<c>PickUp</c>)의 효과가 아이템에 달려 있어서 필요하다.</b>
    /// 액션 정의의 <c>grants</c> 는 아이템을 모르므로 비어 있는데, 그렇다고 아무 플래그도 안 세우면
    /// 채집 액션이 없는 생산 아키타입(재단사·양조사)은 <c>Craft</c> 의 <c>HasRawMaterial</c> 을
    /// 영원히 못 세운다. 런타임은 인벤토리에서 플래그를 다시 계산하므로(EventApplier) 3단도 그래야 한다.
    /// </summary>
    WorldFlags ItemGrants(ItemId item);

    /// <summary>이 액션이 레시피를 소비하는가(제작·조리). V3.RESOURCE_IMBALANCE 가 쓴다.</summary>
    bool ConsumesRecipe(ActionId action);
}

/// <summary>레시피 입력 한 줄. <see cref="IPlanValidationVocabulary"/> 가 돌려준다.</summary>
public readonly record struct PlanRecipeInput(ItemId Item, int Count);

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
