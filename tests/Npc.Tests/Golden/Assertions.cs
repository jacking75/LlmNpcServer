using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;
using Npc.Sim.Validation;

namespace Npc.Tests.Golden;

/// <summary>단언 하나의 판정.</summary>
/// <param name="Passed">통과했는가.</param>
/// <param name="Detail">실패 사유. 통과면 빈 문자열.</param>
public readonly record struct AssertionOutcome(bool Passed, string Detail)
{
    /// <summary>통과.</summary>
    public static AssertionOutcome Pass { get; } = new(true, string.Empty);

    /// <summary>실패 하나.</summary>
    public static AssertionOutcome Fail(string detail) => new(false, detail);
}

/// <summary>
/// 단언 하나를 판정하는 데 필요한 것 전부.
///
/// <see cref="Companion"/> 은 <c>differs_from</c> 전용이다 — 비교 대상 버킷의 플랜을 구해 온다.
/// 이 델리게이트가 없으면 다양성 단언이 골든 러너에만 존재할 수 있게 되고,
/// 그러면 단언 하나만 따로 테스트할 수 없다.
/// </summary>
/// <param name="Fixture">이 플랜을 만들게 한 픽스처.</param>
/// <param name="Plan">판정 대상 플랜.</param>
/// <param name="PlanJson">모델이 뱉은 원문. <c>validates</c> 의 1단이 이것을 본다.</param>
/// <param name="Data">마스터데이터.</param>
/// <param name="DryRun">4단 검증기. 구현체가 <c>Npc.Sim</c> 에 있어 밖에서 주입한다 (docs/15 T5-01).</param>
/// <param name="Companion"><c>differs_from</c> 의 비교 대상. 없으면 null 을 돌려준다.</param>
public sealed record GoldenSubject(
    GoldenFixture Fixture,
    CompiledPlan Plan,
    string PlanJson,
    MasterDataSet Data,
    IDryRunValidator DryRun,
    Func<BucketKey, CompiledPlan?> Companion)
{
    /// <summary>이 플랜의 액션 시퀀스. <c>differs_from</c> 의 비교 기준이자 리포트의 한 줄이다.</summary>
    public string ActionSignature() => SignatureOf(Plan, Data);

    /// <summary>
    /// 액션 id 를 <c>&gt;</c> 로 이은 문자열.
    ///
    /// <b>인자를 넣지 않는다.</b> <c>tools/measure_diversity.cs</c> 가 W6 다양성 실측에 쓴 정의와
    /// 같은 것을 쓴다 — 여기서만 인자까지 보면 같은 플랜이 도구에서는 중복, 골든에서는
    /// 서로 다름으로 세어져 두 수치를 나란히 못 놓는다.
    /// </summary>
    public static string SignatureOf(CompiledPlan plan, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(data);

        return string.Join('>', plan.Steps.Select(s => data.ActionName(s.Action)));
    }
}

/// <summary>
/// 골든 단언 9종. docs/15 §2.
///
/// §2 의 표는 <c>contains_action</c> 과 <c>not_contains</c> 를 한 행에 묶어 8행으로 적혀 있지만
/// 구현해야 할 <c>kind</c> 값은 9개다 (docs/15 T5-01).
///
/// <b>전부 속성 단언이다.</b> 정확한 문자열 비교를 하는 단언은 여기 없고, 앞으로도 추가하지 않는다 —
/// LLM 출력은 매번 다르고 그게 정상이라 문자열을 고정하는 순간 테스트가 모델 버전에 묶인다.
/// </summary>
public static class GoldenAssertions
{
    /// <summary>허용된 <c>kind</c> 값. 픽스처 로더가 이 목록으로 미지의 종류를 거른다.</summary>
    public static readonly ImmutableArray<string> Kinds =
    [
        "validates",
        "contains_action",
        "not_contains",
        "produces_item_of",
        "ends_with_any",
        "step_count_between",
        "acquires_before_use",
        "avoids_flag_while",
        "differs_from",
    ];

    /// <summary>단언 하나를 판정한다. <b>예외를 던지지 않는다</b> — 픽스처가 잘못돼도 실패로 돌려준다.</summary>
    public static AssertionOutcome Evaluate(GoldenAssertionSpec spec, GoldenSubject subject)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(subject);

        try
        {
            return spec.Kind switch
            {
                "validates" => Validates(spec, subject),
                "contains_action" => ContainsAction(spec, subject, expected: true),
                "not_contains" => ContainsAction(spec, subject, expected: false),
                "produces_item_of" => ProducesItemOf(spec, subject),
                "ends_with_any" => EndsWithAny(spec, subject),
                "step_count_between" => StepCountBetween(spec, subject),
                "acquires_before_use" => AcquiresBeforeUse(spec, subject),
                "avoids_flag_while" => AvoidsFlagWhile(spec, subject),
                "differs_from" => DiffersFrom(spec, subject),
                _ => AssertionOutcome.Fail($"모르는 단언 종류 '{spec.Kind}'."),
            };
        }
        catch (InvalidDataException e)
        {
            return AssertionOutcome.Fail($"{spec.Describe()}: 픽스처가 잘못됐다 — {e.Message}");
        }
    }

    // ---------------------------------------------------------------- 1. validates

    /// <summary>
    /// 지정 단계까지 검증을 통과하는가. <c>stage</c> 를 생략하면 4단(DryRun)까지 본다.
    /// 4단 구현체는 <c>Npc.Sim</c> 에 있으므로 <see cref="GoldenSubject.DryRun"/> 으로 주입받는다.
    /// </summary>
    private static AssertionOutcome Validates(GoldenAssertionSpec spec, GoldenSubject subject)
    {
        ValidationStage target = ParseStage(spec.Stage);
        ArchetypeId archetype = subject.Fixture.Bucket.A;

        ValidationResult schema = SchemaValidator.Validate(subject.PlanJson, out PlanDocument? document);

        if (!schema.IsValid || document is null)
        {
            return Rejected(schema);
        }

        if (target == ValidationStage.Schema)
        {
            return AssertionOutcome.Pass;
        }

        ValidationResult vocabulary = VocabularyValidator.Validate(document, archetype, subject.Data);

        if (!vocabulary.IsValid)
        {
            return Rejected(vocabulary);
        }

        if (target == ValidationStage.Vocabulary)
        {
            return AssertionOutcome.Pass;
        }

        ValidationResult coherence = CoherenceValidator.Validate(
            document, subject.Fixture.Bucket, archetype, subject.Data);

        if (!coherence.IsValid)
        {
            return Rejected(coherence);
        }

        if (target == ValidationStage.Coherence)
        {
            return AssertionOutcome.Pass;
        }

        // 4단은 픽스처가 선언한 시작 플래그에서 출발한다 — 시드는 버킷에서 유도되므로
        // 같은 플랜은 몇 번을 돌려도 같은 판정을 받는다 (ValidationContext.SeedOf).
        ValidationResult dryRun = subject.DryRun.Validate(
            subject.Plan,
            ValidationContext.For(subject.Fixture.Bucket, subject.Fixture.Flags));

        return dryRun.IsValid ? AssertionOutcome.Pass : Rejected(dryRun);

        static AssertionOutcome Rejected(in ValidationResult result) =>
            AssertionOutcome.Fail($"{result.Code}@step{result.StepIndex} {result.Detail}");
    }

    private static ValidationStage ParseStage(string? stage) => stage switch
    {
        null or "" or "DryRun" => ValidationStage.DryRun,
        "Schema" => ValidationStage.Schema,
        "Vocabulary" => ValidationStage.Vocabulary,
        "Coherence" => ValidationStage.Coherence,
        _ => throw new InvalidDataException(
            $"'{stage}' 는 검증 단계가 아니다. Schema / Vocabulary / Coherence / DryRun."),
    };

    // ------------------------------------------------- 2·3. contains_action / not_contains

    private static AssertionOutcome ContainsAction(
        GoldenAssertionSpec spec, GoldenSubject subject, bool expected)
    {
        string wanted = Required(spec.Action, "action");
        bool found = false;

        foreach (CompiledStep step in subject.Plan.Steps)
        {
            if (string.Equals(subject.Data.ActionName(step.Action), wanted, StringComparison.Ordinal))
            {
                found = true;
                break;
            }
        }

        if (found == expected)
        {
            return AssertionOutcome.Pass;
        }

        return AssertionOutcome.Fail(
            expected
                ? $"'{wanted}' 이 없다. 실제: {subject.ActionSignature()}"
                : $"'{wanted}' 이 있으면 안 된다. 실제: {subject.ActionSignature()}");
    }

    // ---------------------------------------------------------------- 4. produces_item_of

    /// <summary>
    /// 결과물이 이 분류인가.
    ///
    /// 무엇이 무엇을 만드는지는 <b>마스터데이터가 정한다</b> — 액션 id 를 코드에 나열하지 않는다
    /// (CLAUDE.md §2.4). 레시피를 쓰는 액션은 <c>items.json</c> 의 outputs 를,
    /// 채집·수령 계열은 인자로 받은 아이템 자체를 결과물로 본다.
    ///
    /// <b>한계:</b> <c>Fish</c> 처럼 산출물이 인자가 아니라 액션에 박혀 있는 경우는 잡지 못한다.
    /// 그런 픽스처는 <c>contains_action</c> 을 쓴다.
    /// </summary>
    private static AssertionOutcome ProducesItemOf(GoldenAssertionSpec spec, GoldenSubject subject)
    {
        string wanted = Required(spec.Category, "category");

        if (!Enum.TryParse(wanted, ignoreCase: true, out ItemCategory category))
        {
            throw new InvalidDataException(
                $"'{wanted}' 는 items.json 의 category 가 아니다. {string.Join(", ", Enum.GetNames<ItemCategory>())}.");
        }

        var produced = new List<string>();

        foreach (CompiledStep step in subject.Plan.Steps)
        {
            foreach (ItemId item in OutputsOf(step, subject.Data))
            {
                ItemDef def = subject.Data.Items[item];

                if (def.Category == category)
                {
                    return AssertionOutcome.Pass;
                }

                produced.Add($"{def.Id}({def.Category})");
            }
        }

        return AssertionOutcome.Fail(
            produced.Count == 0
                ? $"아무것도 만들지 않는다. 실제: {subject.ActionSignature()}"
                : $"{category} 를 만들지 않는다. 만든 것: {string.Join(", ", produced)}");
    }

    /// <summary>이 스텝이 만들어 내는 아이템.</summary>
    private static ImmutableArray<ItemId> OutputsOf(in CompiledStep step, MasterDataSet data)
    {
        if (step.Item.Value == 0)
        {
            return [];
        }

        if (data.ConsumesRecipe(step.Action)
            && data.Items.TryGetRecipe(data.Items[step.Item].Id, out RecipeDef recipe))
        {
            return [.. recipe.Outputs.Select(o => o.Item)];
        }

        return data.ProducesItem(step.Action) ? [step.Item] : [];
    }

    /// <summary>이 스텝이 소비하는 아이템.</summary>
    private static ImmutableArray<ItemId> InputsOf(in CompiledStep step, MasterDataSet data)
    {
        if (step.Item.Value == 0)
        {
            return [];
        }

        if (data.ConsumesRecipe(step.Action))
        {
            return data.Items.TryGetRecipe(data.Items[step.Item].Id, out RecipeDef recipe)
                ? [.. recipe.Inputs.Select(i => i.Item)]
                : [];
        }

        // Drop·Store·Trade·Equip 처럼 아이템을 인자로 받으면서 만들어 내지는 않는 액션.
        return data.ProducesItem(step.Action) ? [] : [step.Item];
    }

    // ---------------------------------------------------------------- 5. ends_with_any

    private static AssertionOutcome EndsWithAny(GoldenAssertionSpec spec, GoldenSubject subject)
    {
        if (spec.Actions.IsDefaultOrEmpty)
        {
            throw new InvalidDataException("ends_with_any 에 actions 가 없다.");
        }

        if (subject.Plan.Steps.Length == 0)
        {
            return AssertionOutcome.Fail("스텝이 하나도 없다.");
        }

        string last = subject.Data.ActionName(subject.Plan.Steps[^1].Action);

        return spec.Actions.Contains(last, StringComparer.Ordinal)
            ? AssertionOutcome.Pass
            : AssertionOutcome.Fail($"마지막 스텝이 '{last}' 다. {string.Join('/', spec.Actions)} 중 하나여야 한다.");
    }

    // ---------------------------------------------------------------- 6. step_count_between

    private static AssertionOutcome StepCountBetween(GoldenAssertionSpec spec, GoldenSubject subject)
    {
        int count = subject.Plan.Steps.Length;

        return count >= spec.Min && count <= spec.Max
            ? AssertionOutcome.Pass
            : AssertionOutcome.Fail(
                string.Create(CultureInfo.InvariantCulture, $"스텝이 {count}개다. {spec.Min}~{spec.Max} 이어야 한다."));
    }

    // ---------------------------------------------------------------- 7. acquires_before_use

    /// <summary>
    /// 소비 전에 획득하는가.
    ///
    /// <b>시작 인벤토리를 센다.</b> 픽스처가 <c>"iron_ore": 0</c> 을 적는 이유가 이것이다 —
    /// 이미 갖고 있으면 획득 스텝이 없어도 정합하고, 0 이면 앞에서 캐 와야 한다.
    /// 대상 액션이 플랜에 아예 없으면 소비도 없으므로 통과다.
    /// </summary>
    private static AssertionOutcome AcquiresBeforeUse(GoldenAssertionSpec spec, GoldenSubject subject)
    {
        string itemId = Required(spec.Item, "item");
        string actionId = Required(spec.Action, "action");

        if (!subject.Data.Items.TryGet(itemId, out ItemDef item))
        {
            throw new InvalidDataException($"items.json 에 없는 아이템 '{itemId}'.");
        }

        int use = -1;

        for (int i = 0; i < subject.Plan.Steps.Length; i++)
        {
            CompiledStep step = subject.Plan.Steps[i];

            if (!string.Equals(subject.Data.ActionName(step.Action), actionId, StringComparison.Ordinal))
            {
                continue;
            }

            if (InputsOf(step, subject.Data).Contains(item.Code))
            {
                use = i;
                break;
            }
        }

        if (use < 0)
        {
            // 그 액션이 이 아이템을 쓰지 않는다 — 소비가 없으니 정합성 위반도 없다.
            return AssertionOutcome.Pass;
        }

        if (subject.Fixture.StartingCount(item.Code) > 0)
        {
            return AssertionOutcome.Pass;
        }

        for (int i = 0; i < use; i++)
        {
            if (OutputsOf(subject.Plan.Steps[i], subject.Data).Contains(item.Code))
            {
                return AssertionOutcome.Pass;
            }
        }

        return AssertionOutcome.Fail(
            string.Create(
                CultureInfo.InvariantCulture,
                $"step{use} 의 {actionId} 가 {itemId} 를 쓰는데 앞에서 얻지 않는다. 실제: {subject.ActionSignature()}"));
    }

    // ---------------------------------------------------------------- 8. avoids_flag_while

    /// <summary>
    /// 조건 플래그가 서 있는 동안 회피 플래그를 세우지 않는가.
    ///
    /// 스텝의 플래그 전이를 픽스처의 시작 상태에서부터 굴린다. 조건이 성립한 시점에
    /// 회피 플래그가 서면 실패다. 예: <c>WeatherHarsh</c> 인데 <c>AtField</c> 로 나간다.
    ///
    /// <b>장소 플래그는 액션이 아니라 POI 가 세운다</b> (<c>pois.json</c> 의 grants).
    /// 액션의 <c>grants</c> 만 보면 <c>AtField</c>·<c>InWilderness</c> 같은 것이 한 번도 서지 않아
    /// 이 단언이 통째로 무연산이 된다 — 그래서 <see cref="CoherenceValidator.GrantsOf"/> 로
    /// 스텝이 향하는 POI 심볼의 장소 플래그도 같이 세운다. 3단 검증기가 쓰는 것과 같은 표다.
    /// </summary>
    private static AssertionOutcome AvoidsFlagWhile(GoldenAssertionSpec spec, GoldenSubject subject)
    {
        WorldFlags avoid = ParseFlag(Required(spec.Flag, "flag"));
        WorldFlags when = ParseFlag(Required(spec.When, "when"));

        WorldFlags state = subject.Fixture.Flags;

        for (int i = 0; i < subject.Plan.Steps.Length; i++)
        {
            StepFlags flags = subject.Plan.FlagsOf(i);

            // 조건은 스텝을 실행하기 직전 상태로 본다 — 스텝이 조건을 스스로 지우고
            // 금지 플래그를 세우는 것까지 통과시키면 단언이 무의미해진다.
            bool conditionHolds = (state & when) == when;

            state = flags.Apply(state) | CoherenceValidator.GrantsOf(subject.Plan.Steps[i].Poi);

            if (conditionHolds && (state & avoid) != 0)
            {
                return AssertionOutcome.Fail(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"step{i} ({subject.Data.ActionName(subject.Plan.Steps[i].Action)}) 이 "
                        + $"{WorldFlagTable.Format(when)} 상태에서 {WorldFlagTable.Format(avoid)} 를 세운다."));
            }
        }

        return AssertionOutcome.Pass;
    }

    private static WorldFlags ParseFlag(string name) =>
        WorldFlagTable.TryParse(name, out WorldFlags flag)
            ? flag
            : throw new InvalidDataException($"world_flags.json 에 없는 플래그 '{name}'.");

    // ---------------------------------------------------------------- 9. differs_from

    /// <summary>
    /// 다른 버킷의 플랜과 다른가. <b>다양성 단언이다.</b>
    ///
    /// 비교 대상을 구하지 못하면 <b>실패로 본다.</b> 통과로 세면 비교를 안 한 것이
    /// 다양성이 있는 것으로 집계되어 지표가 거짓이 된다.
    /// </summary>
    private static AssertionOutcome DiffersFrom(GoldenAssertionSpec spec, GoldenSubject subject)
    {
        string text = Required(spec.Bucket, "bucket");
        (_, BucketKey other) = GoldenFixture.ParseBucket(text, subject.Data);

        if (other == subject.Fixture.Bucket)
        {
            throw new InvalidDataException($"differs_from 이 자기 자신을 가리킨다: {text}");
        }

        CompiledPlan? companion = subject.Companion(other);

        if (companion is null)
        {
            return AssertionOutcome.Fail($"비교 대상 '{text}' 의 플랜을 구하지 못했다.");
        }

        string mine = subject.ActionSignature();
        string theirs = GoldenSubject.SignatureOf(companion, subject.Data);

        return string.Equals(mine, theirs, StringComparison.Ordinal)
            ? AssertionOutcome.Fail($"'{text}' 와 액션 시퀀스가 같다: {mine}")
            : AssertionOutcome.Pass;
    }

    private static string Required(string? value, string field) =>
        string.IsNullOrEmpty(value)
            ? throw new InvalidDataException($"단언에 '{field}' 가 없다.")
            : value;
}

/// <summary>
/// T5-01 완료 조건 — 9종 각각의 통과·실패 케이스.
///
/// 테스트를 별도 파일로 빼지 않은 것은 태스크의 파일 목록이 두 개(GoldenFixture·Assertions)이고,
/// 단언 구현과 그 반례는 같이 읽혀야 하기 때문이다.
/// </summary>
public sealed class GoldenAssertionTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);
    private static readonly DryRunValidator s_dryRun = new(s_data);

    /// <summary>대장장이의 정상적인 하루. 여러 단언의 통과 케이스로 쓴다.</summary>
    private const string SmithDayJson = """
        {
          "schema": 1,
          "goal": "forge_and_rest",
          "steps": [
            { "action": "MoveTo", "args": { "poi": "$nearest_field", "speed": "walk" } },
            { "action": "Mine",   "args": { "resource": "iron_ore", "count": 6 } },
            { "action": "MoveTo", "args": { "poi": "$workplace", "speed": "walk" } },
            { "action": "Craft",  "args": { "recipe": "iron_sword", "count": 2 } },
            { "action": "Store",  "args": { "item": "iron_sword", "count": 2 } },
            { "action": "MoveTo", "args": { "poi": "$home", "speed": "walk" } },
            { "action": "Sleep",  "args": { "until_time": "Dawn" } }
          ],
          "on_step_fail": "fallback",
          "loop": true
        }
        """;

    /// <summary>재료를 캐지 않고 바로 만드는 플랜. acquires_before_use 의 반례다.</summary>
    private const string SmithNoMiningJson = """
        {
          "schema": 1,
          "goal": "forge_without_ore",
          "steps": [
            { "action": "MoveTo", "args": { "poi": "$workplace", "speed": "walk" } },
            { "action": "Craft",  "args": { "recipe": "iron_sword", "count": 2 } },
            { "action": "Wander", "args": { "duration_s": 600 } }
          ],
          "on_step_fail": "fallback",
          "loop": false
        }
        """;

    [Fact]
    public void Kinds_AreTheNineDeclaredInSpec()
    {
        // docs/15 §2 표는 8행이지만 contains_action / not_contains 가 한 행에 묶여 있다.
        Assert.Equal(9, GoldenAssertions.Kinds.Length);
        Assert.Equal(GoldenAssertions.Kinds.Length, GoldenAssertions.Kinds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Validates_PassesGoodPlan_AndFailsUnknownAction()
    {
        GoldenSubject good = Subject("blacksmith@Morning.Peace.Fair", SmithDayJson);

        Assert.True(Run(good, Spec("validates", stage: "Coherence")).Passed);

        // 카탈로그에 없는 액션 — 2단에서 걸린다.
        GoldenSubject bad = Subject(
            "blacksmith@Morning.Peace.Fair",
            SmithDayJson.Replace("\"Mine\"", "\"Excavate\"", StringComparison.Ordinal),
            compile: false);

        AssertionOutcome outcome = Run(bad, Spec("validates", stage: "Vocabulary"));

        Assert.False(outcome.Passed);
        Assert.Contains("V2.", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Validates_RunsDryRunStage()
    {
        // 4단 구현체는 Npc.Sim 에 있고 테스트가 주입한다 (docs/15 T5-01).
        GoldenSubject subject = Subject("blacksmith@Morning.Peace.Fair", SmithDayJson);

        AssertionOutcome outcome = Run(subject, Spec("validates"));

        // 통과 여부와 무관하게 4단까지 실제로 내려갔는지가 이 테스트의 관심사다.
        Assert.True(outcome.Passed || outcome.Detail.StartsWith("V4.", StringComparison.Ordinal), outcome.Detail);
    }

    [Fact]
    public void Validates_RejectsUnknownStage()
    {
        AssertionOutcome outcome = Run(
            Subject("blacksmith@Morning.Peace.Fair", SmithDayJson), Spec("validates", stage: "Nonsense"));

        Assert.False(outcome.Passed);
        Assert.Contains("검증 단계가 아니다", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainsAction_PassesWhenPresent_FailsWhenAbsent()
    {
        GoldenSubject subject = Subject("blacksmith@Morning.Peace.Fair", SmithDayJson);

        Assert.True(Run(subject, Spec("contains_action", action: "Craft")).Passed);
        Assert.False(Run(subject, Spec("contains_action", action: "Pray")).Passed);
    }

    [Fact]
    public void NotContains_PassesWhenAbsent_FailsWhenPresent()
    {
        GoldenSubject subject = Subject("blacksmith@Morning.Peace.Fair", SmithDayJson);

        Assert.True(Run(subject, Spec("not_contains", action: "Wander")).Passed);
        Assert.False(Run(subject, Spec("not_contains", action: "Craft")).Passed);
    }

    [Fact]
    public void ProducesItemOf_LooksThroughRecipeOutputs()
    {
        GoldenSubject subject = Subject("blacksmith@Morning.Peace.Fair", SmithDayJson);

        // iron_sword 는 product, iron_ore 는 raw. 둘 다 이 플랜이 만든다.
        //
        // weapon 이 아니라 product 다 — items.json 에서 iron_sword 의 category 는 product 이고
        // weapon 은 hunting_bow·axe·guard_spear·guard_sword 넷뿐이며 이 넷에는 레시피가 없다.
        // 어떤 레시피도 weapon 을 산출하지 않으므로 docs/15 §2 예시를 같은 커밋에서 고쳤다.
        Assert.True(Run(subject, Spec("produces_item_of", category: "product")).Passed);
        Assert.True(Run(subject, Spec("produces_item_of", category: "raw")).Passed);

        AssertionOutcome outcome = Run(subject, Spec("produces_item_of", category: "food"));

        Assert.False(outcome.Passed);
        Assert.Contains("iron_sword", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ProducesItemOf_RejectsUnknownCategory()
    {
        AssertionOutcome outcome = Run(
            Subject("blacksmith@Morning.Peace.Fair", SmithDayJson),
            Spec("produces_item_of", category: "siege_engine"));

        Assert.False(outcome.Passed);
        Assert.Contains("category", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EndsWithAny_LooksAtTheLastStepOnly()
    {
        GoldenSubject subject = Subject("blacksmith@Morning.Peace.Fair", SmithDayJson);

        Assert.True(Run(subject, Spec("ends_with_any", actions: ["Sleep", "Rest"])).Passed);
        Assert.False(Run(subject, Spec("ends_with_any", actions: ["Craft"])).Passed);
    }

    [Fact]
    public void StepCountBetween_IsInclusive()
    {
        GoldenSubject subject = Subject("blacksmith@Morning.Peace.Fair", SmithDayJson);

        Assert.Equal(7, subject.Plan.Steps.Length);
        Assert.True(Run(subject, Spec("step_count_between", min: 7, max: 7)).Passed);
        Assert.False(Run(subject, Spec("step_count_between", min: 3, max: 6)).Passed);
    }

    [Fact]
    public void AcquiresBeforeUse_PassesWhenMined_FailsWhenNot()
    {
        GoldenSubject mined = Subject("blacksmith@Morning.Peace.Fair", SmithDayJson);
        GoldenSubject empty = Subject("blacksmith@Morning.Peace.Fair", SmithNoMiningJson);

        Assert.True(Run(mined, Spec("acquires_before_use", item: "iron_ore", action: "Craft")).Passed);

        AssertionOutcome outcome = Run(empty, Spec("acquires_before_use", item: "iron_ore", action: "Craft"));

        Assert.False(outcome.Passed);
        Assert.Contains("앞에서 얻지 않는다", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AcquiresBeforeUse_CountsStartingInventory()
    {
        // 창고에 이미 있으면 캐 오지 않아도 정합하다.
        GoldenSubject stocked = Subject(
            "blacksmith@Morning.Peace.Fair", SmithNoMiningJson, inventory: """{ "iron_ore": 8, "coal": 4 }""");

        Assert.True(Run(stocked, Spec("acquires_before_use", item: "iron_ore", action: "Craft")).Passed);
    }

    [Fact]
    public void AcquiresBeforeUse_IsVacuousWhenActionAbsent()
    {
        GoldenSubject subject = Subject("blacksmith@Morning.Peace.Fair", SmithDayJson);

        Assert.True(Run(subject, Spec("acquires_before_use", item: "timber", action: "Cook")).Passed);
    }

    [Fact]
    public void AvoidsFlagWhile_CatchesTheForbiddenTransition()
    {
        GoldenSubject stormy = Subject(
            "blacksmith@Morning.Peace.Storm", SmithDayJson, flags: """["AtHome", "WeatherHarsh", "HasTool"]""");

        // 이 플랜의 첫 스텝은 MoveTo($nearest_field) 다 — 폭풍인데 채집지로 나간다.
        // 장소 플래그는 액션이 아니라 POI 가 세우므로, 심볼을 안 보면 이 실패가 안 잡힌다.
        AssertionOutcome outdoors = Run(stormy, Spec("avoids_flag_while", flag: "AtField", when: "WeatherHarsh"));

        Assert.False(outdoors.Passed);
        Assert.Contains("AtField", outdoors.Detail, StringComparison.Ordinal);

        // 야외 POI 심볼이 없으므로 InWilderness 는 서지 않는다.
        Assert.True(Run(stormy, Spec("avoids_flag_while", flag: "InWilderness", when: "WeatherHarsh")).Passed);

        // 액션이 세우는 플래그도 같은 방식으로 걸린다: Craft 는 HasProduct 를 세운다.
        AssertionOutcome crafted = Run(stormy, Spec("avoids_flag_while", flag: "HasProduct", when: "WeatherHarsh"));

        Assert.False(crafted.Passed);
        Assert.Contains("HasProduct", crafted.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AvoidsFlagWhile_IgnoresStepsBeforeTheConditionHolds()
    {
        // 조건 플래그가 한 번도 서지 않으면 언제나 통과다.
        GoldenSubject fair = Subject(
            "blacksmith@Morning.Peace.Fair", SmithDayJson, flags: """["AtHome", "HasTool"]""");

        Assert.True(Run(fair, Spec("avoids_flag_while", flag: "HasProduct", when: "WeatherHarsh")).Passed);
    }

    [Fact]
    public void DiffersFrom_ComparesActionSequences()
    {
        CompiledPlan same = Compile(SmithDayJson, Bucket("blacksmith@Evening.War.Cold"));
        CompiledPlan other = Compile(SmithNoMiningJson, Bucket("blacksmith@Evening.War.Cold"));

        GoldenSubject vsSame = Subject(
            "blacksmith@Morning.Peace.Fair", SmithDayJson, companion: _ => same);
        GoldenSubject vsOther = Subject(
            "blacksmith@Morning.Peace.Fair", SmithDayJson, companion: _ => other);

        Assert.False(Run(vsSame, Spec("differs_from", bucket: "blacksmith@Evening.War.Cold")).Passed);
        Assert.True(Run(vsOther, Spec("differs_from", bucket: "blacksmith@Evening.War.Cold")).Passed);
    }

    [Fact]
    public void DiffersFrom_FailsWhenCompanionIsMissing()
    {
        GoldenSubject subject = Subject("blacksmith@Morning.Peace.Fair", SmithDayJson, companion: _ => null);

        AssertionOutcome outcome = Run(subject, Spec("differs_from", bucket: "blacksmith@Evening.War.Cold"));

        Assert.False(outcome.Passed);
        Assert.Contains("구하지 못했다", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_ParsesTheSpecExample()
    {
        // docs/15 §2 의 예시 픽스처가 그대로 읽혀야 한다.
        const string Json = """
            {
              "id": "G-014",
              "input": {
                "bucket": "blacksmith@Evening.War.Cold",
                "flags": ["AtWorkplace","HasTool","IsHungry","RegionUnderAttack","WeatherHarsh"],
                "inventory": { "coal": 3, "iron_ore": 0 }
              },
              "assertions": [
                { "kind": "validates",        "stage": "DryRun" },
                { "kind": "contains_action",  "action": "Craft" },
                { "kind": "produces_item_of", "category": "product" },
                { "kind": "not_contains",     "action": "Wander" },
                { "kind": "ends_with_any",    "actions": ["Sleep","Rest"] },
                { "kind": "step_count_between", "min": 4, "max": 9 },
                { "kind": "acquires_before_use", "item": "iron_ore", "action": "Craft" },
                { "kind": "avoids_flag_while", "flag": "InWilderness", "when": "WeatherHarsh" }
              ]
            }
            """;

        using JsonDocument document = JsonDocument.Parse(Json);
        GoldenFixture fixture = GoldenFixture.Parse(document.RootElement, s_data, "inline");

        Assert.Equal("G-014", fixture.Id);
        Assert.Equal(TimeOfDay.Evening, fixture.Bucket.T);
        Assert.Equal(RegionState.War, fixture.Bucket.R);
        Assert.Equal(Climate.Cold, fixture.Bucket.C);
        Assert.Equal("blacksmith", fixture.Archetype);
        Assert.Equal(8, fixture.Assertions.Length);

        Assert.True((fixture.Flags & WorldFlags.RegionUnderAttack) != 0);
        Assert.Equal(3, fixture.StartingCount(s_data.Items.Items.Single(i => i.Id == "coal").Code));
        Assert.Equal(0, fixture.StartingCount(s_data.Items.Items.Single(i => i.Id == "iron_ore").Code));
    }

    [Fact]
    public void Fixture_RejectsUnknownAssertionKind()
    {
        const string Json = """
            {
              "id": "G-999",
              "input": { "bucket": "blacksmith@Evening.War.Cold" },
              "assertions": [ { "kind": "smells_right" } ]
            }
            """;

        using JsonDocument document = JsonDocument.Parse(Json);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => GoldenFixture.Parse(document.RootElement, s_data, "inline"));

        Assert.Contains("smells_right", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_RejectsUnknownFlagAndItem()
    {
        Assert.Throws<InvalidDataException>(() => ParseInput("""
            { "bucket": "blacksmith@Evening.War.Cold", "flags": ["IsVeryTired"] }
            """));

        Assert.Throws<InvalidDataException>(() => ParseInput("""
            { "bucket": "blacksmith@Evening.War.Cold", "inventory": { "mithril": 1 } }
            """));

        Assert.Throws<InvalidDataException>(() => ParseInput("""
            { "bucket": "blacksmith" }
            """));

        static void ParseInput(string input)
        {
            using JsonDocument document = JsonDocument.Parse(
                $$"""{ "id": "G-000", "input": {{input}}, "assertions": [] }""");

            GoldenFixture.Parse(document.RootElement, s_data, "inline");
        }
    }

    // ------------------------------------------------------------------ 헬퍼

    private static AssertionOutcome Run(GoldenSubject subject, GoldenAssertionSpec spec) =>
        GoldenAssertions.Evaluate(spec, subject);

    private static GoldenAssertionSpec Spec(
        string kind,
        string? action = null,
        ImmutableArray<string> actions = default,
        string? category = null,
        string? item = null,
        string? flag = null,
        string? when = null,
        string? bucket = null,
        string? stage = null,
        int min = 0,
        int max = int.MaxValue) =>
        new(kind, action, actions, category, item, flag, when, bucket, stage, min, max);

    private static BucketKey Bucket(string text) => GoldenFixture.ParseBucket(text, s_data).Bucket;

    private static GoldenSubject Subject(
        string bucketText,
        string planJson,
        string? flags = null,
        string? inventory = null,
        Func<BucketKey, CompiledPlan?>? companion = null,
        bool compile = true)
    {
        string fixtureJson = $$"""
            {
              "id": "G-000",
              "input": {
                "bucket": {{JsonSerializer.Serialize(bucketText)}},
                "flags": {{flags ?? """["AtWorkplace", "HasTool"]"""}},
                "inventory": {{inventory ?? "{}"}}
              },
              "assertions": []
            }
            """;

        using JsonDocument document = JsonDocument.Parse(fixtureJson);
        GoldenFixture fixture = GoldenFixture.Parse(document.RootElement, s_data, "inline");

        // 컴파일이 안 되는 플랜(모르는 액션)은 1·2단 단언만 볼 수 있다. 빈 플랜을 대신 넣는다.
        CompiledPlan plan = compile
            ? Compile(planJson, fixture.Bucket)
            : Compile(SmithDayJson, fixture.Bucket);

        return new GoldenSubject(
            fixture, plan, planJson, s_data, s_dryRun, companion ?? (_ => null));
    }

    private static CompiledPlan Compile(string json, BucketKey bucket)
    {
        ValidationResult schema = SchemaValidator.Validate(json, out PlanDocument? document);

        Assert.True(schema.IsValid, $"{schema.Code} {schema.Detail}");

        return PlanCompiler.Compile(document!, bucket, default, s_data, PlanOrigin.Runtime, 1, json);
    }
}
