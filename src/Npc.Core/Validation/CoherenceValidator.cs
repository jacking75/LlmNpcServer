using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core.Plan;

namespace Npc.Core.Validation;

/// <summary>
/// 검증기 3단 — 정합성. docs/03 §3 · docs/12 §5.
/// GOAP 스타일 정적 상태 전이 시뮬. <b>여기가 실질적으로 가장 많이 잡는다.</b>
///
/// 실패 설명은 <b>영어</b>로 만든다. 이 문자열이 그대로 재시도 프롬프트에 실리기 때문이다
/// (docs/03 §4 의 previous_attempt_failed.detail). 비트마스크 숫자는 절대 내보내지 않고
/// <see cref="WorldFlagTable.Format"/> 로 플래그 이름을 쓴다.
/// </summary>
public static class CoherenceValidator
{
    /// <summary>같은 액션이 이만큼 연속되면 V3.DEGENERATE.</summary>
    public const int MaxConsecutiveSameAction = 3;

    /// <summary>POI 심볼이 함의하는 장소 플래그. docs/01 §2.2 의 "(POI별)" grants.</summary>
    public static WorldFlags GrantsOf(PoiSymbol symbol) => symbol switch
    {
        PoiSymbol.Home => WorldFlags.AtHome,
        PoiSymbol.Workplace => WorldFlags.AtWorkplace,
        PoiSymbol.Market => WorldFlags.AtMarket,
        PoiSymbol.Tavern => WorldFlags.AtTavern,
        PoiSymbol.Temple => WorldFlags.AtTemple,
        PoiSymbol.Gate => WorldFlags.AtGate,
        PoiSymbol.NearestField => WorldFlags.AtField,

        // $nearest_safe 와 $nearest_shelter 는 성문일 수도 집일 수도 있다.
        // 어느 쪽인지 모르므로 아무 장소 플래그도 세우지 않는다 — 관대하게 잡으면 V3 가 무의미해진다.
        _ => WorldFlags.None,
    };

    /// <summary>플랜의 정합성을 검사한다.</summary>
    public static ValidationResult Validate(
        PlanDocument document,
        BucketKey bucket,
        ArchetypeId archetype,
        IPlanValidationVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(vocabulary);

        WorldFlags state = vocabulary.InitialFlags(bucket);

        ActionId previousAction = default;
        int repeats = 0;

        // 자원 수지. 이 플랜이 스스로 모은 양과 스스로 쓴 양만 센다.
        var gathered = new Dictionary<ItemId, int>();
        var consumed = new Dictionary<ItemId, int>();

        // 어느 스텝이 그 자원을 마지막으로 썼는가. 실패 설명에 "몇 번째 스텝을 고쳐라"를 넣기 위해서다.
        var consumedAtStep = new Dictionary<ItemId, int>();

        ActionId firstAction = default;
        PoiSymbol firstPoi = PoiSymbol.None;
        StepFlags firstFlags = default;
        WorldFlags lastGrants = WorldFlags.None;

        for (int i = 0; i < document.Steps.Length; i++)
        {
            PlanStep step = document.Steps[i];

            if (!vocabulary.TryGetAction(step.Action, out ActionId action))
            {
                // 2단이 이미 걸렀어야 한다. 여기까지 왔으면 호출 순서가 잘못된 것이다.
                return ValidationResult.Fail(
                    ValidationStage.Coherence, "V3.PRECONDITION_UNMET", i,
                    $"Unknown action '{step.Action}' reached stage 3.");
            }

            StepFlags flags = vocabulary.FlagsOf(action);
            PoiSymbol poi = ReadPoiSymbol(step);

            // --- V3.UNREACHABLE_POI ---
            if (poi != PoiSymbol.None && !vocabulary.CanBindSymbol(archetype, poi))
            {
                return ValidationResult.Fail(
                    ValidationStage.Coherence, "V3.UNREACHABLE_POI", i,
                    $"{step.Action} targets {PoiSymbols.ToText(poi)}, but this archetype has no such POI available.");
            }

            // --- V3.PRECONDITION_UNMET ---
            WorldFlags missing = flags.Requires & ~state;
            if (missing != WorldFlags.None)
            {
                return ValidationResult.Fail(
                    ValidationStage.Coherence, "V3.PRECONDITION_UNMET", i,
                    $"{step.Action} requires {WorldFlagTable.Format(missing)} but no preceding step grants it. "
                    + Remedy(missing)
                    + $"State before this step: {WorldFlagTable.Format(state)}.");
            }

            if (flags.RequiresAny != WorldFlags.None && (flags.RequiresAny & state) == 0)
            {
                return ValidationResult.Fail(
                    ValidationStage.Coherence, "V3.PRECONDITION_UNMET", i,
                    $"{step.Action} requires at least one of {WorldFlagTable.Format(flags.RequiresAny)}. "
                    + $"State before this step: {WorldFlagTable.Format(state)}.");
            }

            // --- V3.FORBIDDEN_FLAG ---
            WorldFlags violated = flags.Forbids & state;
            if (violated != WorldFlags.None)
            {
                return ValidationResult.Fail(
                    ValidationStage.Coherence, "V3.FORBIDDEN_FLAG", i,
                    $"{step.Action} is forbidden while {WorldFlagTable.Format(violated)} is set. "
                    + $"State before this step: {WorldFlagTable.Format(state)}.");
            }

            // --- V3.DEGENERATE ---
            repeats = action == previousAction ? repeats + 1 : 1;
            previousAction = action;

            if (repeats >= MaxConsecutiveSameAction)
            {
                return ValidationResult.Fail(
                    ValidationStage.Coherence, "V3.DEGENERATE", i,
                    $"{step.Action} repeats {repeats} times in a row. Merge them or vary the plan.");
            }

            TrackResources(vocabulary, action, step, i, gathered, consumed, consumedAtStep);

            // 도착으로 완료되는 액션은 그 POI 가 함의하는 장소 플래그를 세운다.
            WorldFlags grants = flags.Grants;
            if (vocabulary.CompletesOnArrival(action))
            {
                grants |= GrantsOf(poi);
            }

            // 수령 액션(Withdraw · PickUp)의 효과는 아이템에 달려 있다. 액션 정의의 grants 는
            // 아이템을 모르므로 비어 있지만, 런타임은 인벤토리에서 플래그를 다시 계산한다.
            // 이걸 빼면 채집 액션이 없는 생산 아키타입은 Craft 를 영원히 못 쓴다 (T2-21 3차 실측).
            if (vocabulary.ProducesItem(action)
                && TryReadItem(vocabulary, step, "item", out ItemId received))
            {
                grants |= vocabulary.ItemGrants(received);
            }

            state = (state & ~flags.Clears) | grants;

            if (i == 0)
            {
                firstAction = action;
                firstPoi = poi;
                firstFlags = flags;
            }

            lastGrants = grants;
        }

        // --- V3.RESOURCE_IMBALANCE ---
        // 플랜이 스스로 모으기도 하고 쓰기도 한 자원만 본다.
        // 아예 안 모으는 자원은 인벤토리·보관함에서 온다고 가정한다 (그건 3단이 알 수 없다).
        foreach (ItemId item in consumed.Keys.OrderBy(k => k.Value))
        {
            if (!gathered.TryGetValue(item, out int produced))
            {
                continue;
            }

            if (produced < consumed[item])
            {
                // 아이템을 code 숫자로 찍지 않는다 — 숫자를 그대로 내보내면 모델이 못 고친다 (docs/12 §5).
                string name = vocabulary.ItemName(item);
                int step = consumedAtStep.GetValueOrDefault(item, -1);

                return ValidationResult.Fail(
                    ValidationStage.Coherence, "V3.RESOURCE_IMBALANCE", step,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The plan gathers {produced} {name} but consumes {consumed[item]}. "
                        + $"Gather more {name} before this step, or lower the count."));
            }
        }

        if (document.Loop)
        {
            // --- V3.LOOP_NOT_CLOSED ---
            WorldFlags missing = firstFlags.Requires & ~state;
            if (missing != WorldFlags.None)
            {
                return ValidationResult.Fail(
                    ValidationStage.Coherence, "V3.LOOP_NOT_CLOSED", -1,
                    $"loop is true but the first step requires {WorldFlagTable.Format(missing)}, "
                    + $"which is not set after the last step. Final state: {WorldFlagTable.Format(state)}.");
            }

            WorldFlags violated = firstFlags.Forbids & state;
            if (violated != WorldFlags.None)
            {
                return ValidationResult.Fail(
                    ValidationStage.Coherence, "V3.LOOP_NOT_CLOSED", -1,
                    $"loop is true but {WorldFlagTable.Format(violated)} is still set after the last step, "
                    + $"which the first step forbids.");
            }

            if (firstFlags.RequiresAny != WorldFlags.None && (firstFlags.RequiresAny & state) == 0)
            {
                return ValidationResult.Fail(
                    ValidationStage.Coherence, "V3.LOOP_NOT_CLOSED", -1,
                    $"loop is true but the first step requires at least one of "
                    + $"{WorldFlagTable.Format(firstFlags.RequiresAny)}. Final state: {WorldFlagTable.Format(state)}.");
            }

            _ = firstAction;
            _ = firstPoi;
        }
        else
        {
            // --- V3.NO_TERMINAL ---
            // 마지막이 휴식 상태로 끝나야 한다. IsRested 를 세우는 액션(Sleep/Rest/Pray)이 그것이다.
            if ((lastGrants & WorldFlags.IsRested) == 0)
            {
                return ValidationResult.Fail(
                    ValidationStage.Coherence, "V3.NO_TERMINAL", document.Steps.Length - 1,
                    $"loop is false, so the plan must end in a rest state, but the last step is "
                    + $"{vocabulary.ActionName(previousAction)}, which does not grant IsRested. "
                    + $"End with Sleep at $home or with Rest.");
            }
        }

        return ValidationResult.Ok;
    }

    /// <summary>
    /// 실패를 재시도 프롬프트에 실을 블록으로. docs/03 §4.
    /// <b>프리픽스는 건드리지 않는다</b> — 이 문자열은 서픽스에만 들어간다 (CLAUDE.md §2.5).
    /// </summary>
    public static string Explain(ValidationResult result)
    {
        if (result.IsValid)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(256);

        sb.Append("{\"previous_attempt_failed\":{\"stage\":\"")
          .Append(result.FailedAt)
          .Append("\",\"code\":\"")
          .Append(result.Code)
          .Append("\",\"step\":")
          .Append(result.StepIndex.ToString(CultureInfo.InvariantCulture))
          .Append(",\"detail\":")
          .Append(JsonSerializer.Serialize(result.Detail))
          .Append("}}");

        return sb.ToString();
    }

    /// <summary>
    /// 모자란 플래그를 <b>어떻게 세우는지</b> 한 문장으로. docs/12 §5 —
    /// "실패 이유를 자연어로" 의 다음 단계다. 장소 플래그는 고치는 방법이 하나뿐이라
    /// (그 POI 로 <c>MoveTo</c>) 그걸 그대로 적어 준다. 재시도가 같은 실수를 반복하는 것을 줄인다.
    /// </summary>
    private static string Remedy(WorldFlags missing)
    {
        for (int i = 1; i < PoiSymbols.Names.Length; i++)
        {
            var symbol = (PoiSymbol)i;

            if (GrantsOf(symbol) != WorldFlags.None && (missing & GrantsOf(symbol)) != 0)
            {
                return $"Insert a MoveTo step with poi {PoiSymbols.ToText(symbol)} before it. ";
            }
        }

        if ((missing & WorldFlags.HasRawMaterial) != 0)
        {
            return "Gather/Mine/Farm/Fish it first, or Withdraw a raw material at your home or workplace. ";
        }

        return string.Empty;
    }

    private static PoiSymbol ReadPoiSymbol(PlanStep step)
    {
        foreach (string key in new[] { "poi", "target", "route" })
        {
            if (!step.Args.TryGetValue(key, out JsonElement value))
            {
                continue;
            }

            JsonElement candidate = value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0
                ? value[0]
                : value;

            if (candidate.ValueKind == JsonValueKind.String
                && PoiSymbols.TryParse(candidate.GetString(), out PoiSymbol symbol))
            {
                return symbol;
            }
        }

        return PoiSymbol.None;
    }

    private static void TrackResources(
        IPlanValidationVocabulary vocabulary,
        ActionId action,
        PlanStep step,
        int stepIndex,
        Dictionary<ItemId, int> gathered,
        Dictionary<ItemId, int> consumed,
        Dictionary<ItemId, int> consumedAtStep)
    {
        int count = ReadCount(step);

        if (vocabulary.ConsumesRecipe(action)
            && TryReadItem(vocabulary, step, "recipe", out ItemId recipe)
            && vocabulary.TryGetRecipeInputs(recipe, out ImmutableArray<PlanRecipeInput> inputs))
        {
            foreach (PlanRecipeInput input in inputs)
            {
                consumed[input.Item] = consumed.GetValueOrDefault(input.Item) + (input.Count * Math.Max(1, count));
                consumedAtStep[input.Item] = stepIndex;
            }

            return;
        }

        if (!vocabulary.ProducesItem(action))
        {
            return;
        }

        foreach (string key in new[] { "resource", "crop", "item" })
        {
            if (TryReadItem(vocabulary, step, key, out ItemId item))
            {
                gathered[item] = gathered.GetValueOrDefault(item) + Math.Max(1, count);
                return;
            }
        }
    }

    private static bool TryReadItem(
        IPlanValidationVocabulary vocabulary, PlanStep step, string key, out ItemId item)
    {
        item = default;

        return step.Args.TryGetValue(key, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && vocabulary.TryGetItem(value.GetString()!, out item);
    }

    private static int ReadCount(PlanStep step)
    {
        foreach (string key in new[] { "count", "amount" })
        {
            if (step.Args.TryGetValue(key, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int number))
            {
                return number;
            }
        }

        return 1;
    }
}
