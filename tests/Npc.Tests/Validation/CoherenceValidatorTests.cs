using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;

namespace Npc.Tests.Validation;

/// <summary>docs/03 §3 3단 · docs/12 §5. V3.* 7종 전부 유발 픽스처로 확인한다.</summary>
public sealed class CoherenceValidatorTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static BucketKey Bucket(string archetype, TimeOfDay time = TimeOfDay.Morning)
    {
        Assert.True(s_data.Archetypes.TryGet(archetype, out ArchetypeDef def));
        return new BucketKey(def.Code, time, RegionState.Peace, Climate.Fair);
    }

    private static ValidationResult Validate(string json, string archetype = "blacksmith")
    {
        ValidationResult schema = SchemaValidator.Validate(json, out PlanDocument? document);
        Assert.True(schema.IsValid, $"1단에서 걸렸다: {schema.Code} {schema.Detail}");

        BucketKey bucket = Bucket(archetype);
        ValidationResult vocabulary = VocabularyValidator.Validate(document!, bucket.A, s_data);
        Assert.True(vocabulary.IsValid, $"2단에서 걸렸다: {vocabulary.Code} {vocabulary.Detail}");

        return CoherenceValidator.Validate(document!, bucket, bucket.A, s_data);
    }

    /// <summary>docs/03 §1 의 예시는 3단을 통과해야 한다. 통과 못 하면 사양이 스스로를 반증한다.</summary>
    [Fact]
    public void Coherence_AcceptsSpecExample()
    {
        ValidationResult result = Validate(Npc.Tests.Plan.PlanDocumentTests.SpecExample);

        Assert.True(result.IsValid, $"{result.Code}: {result.Detail}");
    }

    [Fact]
    public void Coherence_V3_PRECONDITION_UNMET()
    {
        // 일터에 가지 않고 바로 제작한다.
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "Craft", "args": { "recipe": "iron_sword", "count": 1 } },
                { "action": "MoveTo", "args": { "poi": "$home" } },
                { "action": "Sleep", "args": { "until_time": "Morning" } }
              ] }
            """;

        ValidationResult result = Validate(Json);

        Assert.Equal(ValidationStage.Coherence, result.FailedAt);
        Assert.Equal("V3.PRECONDITION_UNMET", result.Code);
        Assert.Equal(0, result.StepIndex);

        // Explain 이 비트마스크가 아니라 플래그 이름을 내보내야 한다.
        Assert.Contains("AtWorkplace", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Coherence_V3_PRECONDITION_UNMET_RequiresAny()
    {
        // Cook 은 AtHome 또는 AtTavern 이어야 한다. 채집지에서는 못 한다.
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$nearest_field" } },
                { "action": "Gather", "args": { "resource": "herb", "count": 3 } },
                { "action": "Cook", "args": { "recipe": "tea", "count": 1 } }
              ] }
            """;

        ValidationResult result = Validate(Json, "healer");

        Assert.Equal("V3.PRECONDITION_UNMET", result.Code);
        Assert.Equal(2, result.StepIndex);
        Assert.Contains("AtHome", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Coherence_V3_FORBIDDEN_FLAG()
    {
        // 금지 플래그 위반은 초기 상태에 그 플래그가 서 있을 때 생긴다.
        // 액션의 clears 가 IsSleeping 을 잘 내리고 있어서 플랜 중간에서는 만들 수 없다 —
        // 그래서 초기 상태에 IsSleeping 을 얹는 어휘로 확인한다.
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$home" } },
                { "action": "Sleep", "args": { "until_time": "Morning" } },
                { "action": "Bathe", "args": {} }
              ] }
            """;

        // 정상 경로는 통과한다.
        Assert.True(Validate(Json, "noble").IsValid);

        // 초기 상태에 IsSleeping 이 있으면 첫 MoveTo 가 금지에 걸린다.
        ValidationResult result = CoherenceValidator.Validate(
            Parse(Json),
            Bucket("noble"),
            Bucket("noble").A,
            new SleepingVocabulary(s_data));

        Assert.Equal("V3.FORBIDDEN_FLAG", result.Code);
        Assert.Contains("IsSleeping", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Coherence_V3_LOOP_NOT_CLOSED()
    {
        // 첫 스텝 Eat 은 HasFood 를 요구하고 동시에 소비한다.
        // 다시 채우지 않으면 두 번째 사이클의 첫 스텝이 성립하지 않는다.
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "Eat", "args": {} },
                { "action": "Rest", "args": { "duration_s": 600 } },
                { "action": "Wait", "args": { "duration_s": 60 } }
              ] }
            """;

        ValidationResult result = Validate(Json);

        Assert.Equal("V3.LOOP_NOT_CLOSED", result.Code);
        Assert.Equal(-1, result.StepIndex);
        Assert.Contains("HasFood", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Coherence_StepPreconditionIsCheckedBeforeLoopClosure()
    {
        // Sleep 은 AtHome 을 요구하는데 마지막 위치가 채집지다.
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$workplace" } },
                { "action": "Work", "args": { "recipe": "iron_sword", "count": 1 } },
                { "action": "MoveTo", "args": { "poi": "$nearest_field" } },
                { "action": "Mine", "args": { "resource": "iron_ore", "count": 3 } },
                { "action": "Sleep", "args": { "until_time": "Morning" } }
              ] }
            """;

        ValidationResult result = Validate(Json);

        Assert.Equal("V3.PRECONDITION_UNMET", result.Code);
        Assert.Equal(4, result.StepIndex);
    }

    [Fact]
    public void Coherence_LoopClosesWhenFirstStepNeedsNothing()
    {
        // 첫 스텝이 MoveTo 라 전제가 없다 — 어디서 끝나든 loop 가 닫힌다.
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$workplace" } },
                { "action": "Work", "args": { "recipe": "iron_sword", "count": 1 } },
                { "action": "Trade", "args": { "item": "iron_sword", "amount": 1 } },
                { "action": "MoveTo", "args": { "poi": "$nearest_field" } },
                { "action": "Mine", "args": { "resource": "iron_ore", "count": 3 } },
                { "action": "Gather", "args": { "resource": "stone", "count": 1 } }
              ] }
            """;

        ValidationResult result = Validate(Json);

        Assert.True(result.IsValid, $"{result.Code}: {result.Detail}");
    }

    [Fact]
    public void Coherence_V3_UNREACHABLE_POI()
    {
        // 주민은 일터가 없다. $workplace 를 바인딩할 수 없다.
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$workplace" } },
                { "action": "MoveTo", "args": { "poi": "$home" } },
                { "action": "Sleep", "args": { "until_time": "Morning" } }
              ] }
            """;

        ValidationResult result = Validate(Json, "villager");

        Assert.Equal("V3.UNREACHABLE_POI", result.Code);
        Assert.Equal(0, result.StepIndex);
        Assert.Contains("$workplace", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Coherence_V3_RESOURCE_IMBALANCE()
    {
        // 광석 3개로 검 3자루 — docs/03 §3 이 예시로 든 위반이다.
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$nearest_field" } },
                { "action": "Mine", "args": { "resource": "iron_ore", "count": 3 } },
                { "action": "MoveTo", "args": { "poi": "$workplace" } },
                { "action": "Craft", "args": { "recipe": "iron_sword", "count": 3 } },
                { "action": "MoveTo", "args": { "poi": "$home" } },
                { "action": "Sleep", "args": { "until_time": "Morning" } }
              ] }
            """;

        ValidationResult result = Validate(Json);

        Assert.Equal("V3.RESOURCE_IMBALANCE", result.Code);
    }

    [Fact]
    public void Coherence_V3_NO_TERMINAL()
    {
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": false,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$workplace" } },
                { "action": "Work", "args": { "recipe": "iron_sword", "count": 1 } },
                { "action": "MoveTo", "args": { "poi": "$market" } }
              ] }
            """;

        ValidationResult result = Validate(Json);

        Assert.Equal("V3.NO_TERMINAL", result.Code);
    }

    [Fact]
    public void Coherence_V3_NO_TERMINAL_AcceptsRest()
    {
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": false,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$workplace" } },
                { "action": "Work", "args": { "recipe": "iron_sword", "count": 1 } },
                { "action": "Rest", "args": { "duration_s": 600 } }
              ] }
            """;

        Assert.True(Validate(Json).IsValid);
    }

    [Fact]
    public void Coherence_V3_DEGENERATE()
    {
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": false,
              "steps": [
                { "action": "Greet", "args": { "npc": "self" } },
                { "action": "Greet", "args": { "npc": "self" } },
                { "action": "Greet", "args": { "npc": "self" } },
                { "action": "Rest", "args": { "duration_s": 600 } }
              ] }
            """;

        ValidationResult result = Validate(Json);

        Assert.Equal("V3.DEGENERATE", result.Code);
        Assert.Equal(2, result.StepIndex);
    }

    /// <summary>T1-27 완료 조건 — Explain() 이 플래그 이름을 포함한다.</summary>
    [Fact]
    public void Coherence_ExplainUsesFlagNamesNotBitmasks()
    {
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "Craft", "args": { "recipe": "iron_sword", "count": 1 } },
                { "action": "MoveTo", "args": { "poi": "$home" } },
                { "action": "Sleep", "args": { "until_time": "Morning" } }
              ] }
            """;

        ValidationResult result = Validate(Json);
        string explained = CoherenceValidator.Explain(result);

        Assert.Contains("previous_attempt_failed", explained, StringComparison.Ordinal);
        Assert.Contains("V3.PRECONDITION_UNMET", explained, StringComparison.Ordinal);
        Assert.Contains("AtWorkplace", explained, StringComparison.Ordinal);

        // 비트마스크 숫자가 그대로 나가면 모델이 못 고친다.
        Assert.DoesNotContain("18446744", explained, StringComparison.Ordinal);
        Assert.Empty(CoherenceValidator.Explain(ValidationResult.Ok));
    }

    [Fact]
    public void Coherence_PoiSymbolGrantsMatchLocationFlags()
    {
        Assert.Equal(WorldFlags.AtHome, CoherenceValidator.GrantsOf(PoiSymbol.Home));
        Assert.Equal(WorldFlags.AtWorkplace, CoherenceValidator.GrantsOf(PoiSymbol.Workplace));
        Assert.Equal(WorldFlags.AtField, CoherenceValidator.GrantsOf(PoiSymbol.NearestField));

        // 어느 장소인지 모르는 심볼은 아무것도 세우지 않는다.
        Assert.Equal(WorldFlags.None, CoherenceValidator.GrantsOf(PoiSymbol.NearestShelter));
        Assert.Equal(WorldFlags.None, CoherenceValidator.GrantsOf(PoiSymbol.None));
    }

    private static PlanDocument Parse(string json)
    {
        Assert.True(SchemaValidator.Validate(json, out PlanDocument? document).IsValid);
        return document!;
    }

    /// <summary>초기 상태에 IsSleeping 을 얹는 어휘. V3.FORBIDDEN_FLAG 픽스처용.</summary>
    private sealed class SleepingVocabulary(MasterDataSet inner) : IPlanValidationVocabulary
    {
        public WorldFlags InitialFlags(BucketKey bucket) => inner.InitialFlags(bucket) | WorldFlags.IsSleeping;

        public bool TryGetAction(string actionId, out ActionId action) => inner.TryGetAction(actionId, out action);

        public string ActionName(ActionId action) => inner.ActionName(action);

        public StepFlags FlagsOf(ActionId action) => inner.FlagsOf(action);

        public int DefaultTimeoutSeconds(ActionId action) => inner.DefaultTimeoutSeconds(action);

        public bool TryGetItem(string itemId, out ItemId item) => inner.TryGetItem(itemId, out item);

        public bool TryPackArgs(
            ActionId action,
            IReadOnlyDictionary<string, System.Text.Json.JsonElement> args,
            out PackedArgs packed,
            out string error) => inner.TryPackArgs(action, args, out packed, out error);

        public void UnpackArgs(
            ActionId action, in PackedArgs packed, IDictionary<string, System.Text.Json.JsonElement> args) =>
            inner.UnpackArgs(action, packed, args);

        public bool IsActionAllowed(ArchetypeId archetype, ActionId action) =>
            inner.IsActionAllowed(archetype, action);

        public ValidationResult ValidateArgs(
            ActionId action, int stepIndex, IReadOnlyDictionary<string, System.Text.Json.JsonElement> args) =>
            inner.ValidateArgs(action, stepIndex, args);

        public bool CompletesOnArrival(ActionId action) => inner.CompletesOnArrival(action);

        public bool CanBindSymbol(ArchetypeId archetype, PoiSymbol symbol) =>
            inner.CanBindSymbol(archetype, symbol);

        public bool TryGetRecipeInputs(
            ItemId recipe, out System.Collections.Immutable.ImmutableArray<PlanRecipeInput> inputs) =>
            inner.TryGetRecipeInputs(recipe, out inputs);

        public bool ProducesItem(ActionId action) => inner.ProducesItem(action);

        public bool ConsumesRecipe(ActionId action) => inner.ConsumesRecipe(action);
    }
}
