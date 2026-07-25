using Npc.Contracts;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;

namespace Npc.Tests.Validation;

/// <summary>docs/03 §3 2단. V2.* 8종 전부 유발 픽스처로 확인한다.</summary>
public sealed class VocabularyValidatorTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static ArchetypeId Archetype(string id)
    {
        Assert.True(s_data.Archetypes.TryGet(id, out ArchetypeDef def));
        return def.Code;
    }

    private static ValidationResult Validate(string json, string archetype = "blacksmith")
    {
        ValidationResult schema = SchemaValidator.Validate(json, out PlanDocument? document);

        Assert.True(schema.IsValid, $"1단에서 걸렸다: {schema.Code} {schema.Detail}");

        return VocabularyValidator.Validate(document!, Archetype(archetype), s_data);
    }

    /// <summary>스텝 하나를 감싼 최소 플랜. 나머지 두 스텝은 어떤 아키타입이든 쓸 수 있는 Rest 다.</summary>
    private static string Plan(string step) => $$"""
        { "schema": 1, "goal": "test_goal", "loop": true,
          "steps": [
            {{step}},
            { "action": "Rest", "args": { "duration_s": 60 } },
            { "action": "Rest", "args": { "duration_s": 60 } }
          ] }
        """;

    [Fact]
    public void Vocabulary_AcceptsSpecExample()
    {
        ValidationResult result = Validate(Npc.Tests.Plan.PlanDocumentTests.SpecExample);

        Assert.True(result.IsValid, $"{result.Code}: {result.Detail}");
    }

    [Fact]
    public void Vocabulary_V2_UNKNOWN_ACTION()
    {
        ValidationResult result = Validate(Plan("""{ "action": "Teleport", "args": {} }"""));

        Assert.Equal(ValidationStage.Vocabulary, result.FailedAt);
        Assert.Equal("V2.UNKNOWN_ACTION", result.Code);
        Assert.Equal(0, result.StepIndex);
    }

    [Fact]
    public void Vocabulary_V2_ACTION_NOT_ALLOWED()
    {
        // 대장장이는 Fish 를 못 쓴다.
        ValidationResult result = Validate(Plan("""{ "action": "Fish", "args": { "count": 3 } }"""));

        Assert.Equal("V2.ACTION_NOT_ALLOWED", result.Code);
        Assert.Equal(0, result.StepIndex);
    }

    [Fact]
    public void Vocabulary_V2_UNKNOWN_ARG()
    {
        ValidationResult result = Validate(
            Plan("""{ "action": "MoveTo", "args": { "poi": "$home", "hurry": true } }"""));

        Assert.Equal("V2.UNKNOWN_ARG", result.Code);
        Assert.Contains("hurry", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Vocabulary_V2_MISSING_REQUIRED_ARG()
    {
        ValidationResult result = Validate(Plan("""{ "action": "MoveTo", "args": { "speed": "walk" } }"""));

        Assert.Equal("V2.MISSING_REQUIRED_ARG", result.Code);
        Assert.Contains("poi", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Vocabulary_V2_TYPE_MISMATCH()
    {
        ValidationResult result = Validate(
            Plan("""{ "action": "MoveTo", "args": { "poi": "$home", "speed": "sprint" } }"""));

        Assert.Equal("V2.TYPE_MISMATCH", result.Code);
    }

    [Fact]
    public void Vocabulary_V2_TYPE_MISMATCH_WrongJsonKind()
    {
        ValidationResult result = Validate(
            Plan("""{ "action": "Craft", "args": { "recipe": "iron_sword", "count": "three" } }"""));

        Assert.Equal("V2.TYPE_MISMATCH", result.Code);
    }

    [Fact]
    public void Vocabulary_V2_UNKNOWN_POI()
    {
        ValidationResult result = Validate(Plan("""{ "action": "MoveTo", "args": { "poi": "smithy_01" } }"""));

        Assert.Equal("V2.UNKNOWN_POI", result.Code);
        Assert.Contains("$home", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Vocabulary_V2_UNKNOWN_ITEM()
    {
        ValidationResult result = Validate(
            Plan("""{ "action": "Store", "args": { "item": "unobtainium", "count": 1 } }"""));

        Assert.Equal("V2.UNKNOWN_ITEM", result.Code);
    }

    [Fact]
    public void Vocabulary_V2_UNKNOWN_RECIPE()
    {
        // iron_ore 는 아이템이지만 레시피가 아니다.
        ValidationResult result = Validate(
            Plan("""{ "action": "Craft", "args": { "recipe": "iron_ore", "count": 1 } }"""));

        Assert.Equal("V2.UNKNOWN_RECIPE", result.Code);
    }

    [Fact]
    public void Vocabulary_V2_RANGE()
    {
        ValidationResult result = Validate(
            Plan("""{ "action": "Craft", "args": { "recipe": "iron_sword", "count": 99 } }"""));

        Assert.Equal("V2.RANGE", result.Code);
        Assert.Contains("99", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Vocabulary_RouteMustBeTwoToEightSymbols()
    {
        ValidationResult tooShort = Validate(
            Plan("""{ "action": "Patrol", "args": { "route": ["$gate"], "laps": 1 } }"""),
            "town_guard");

        Assert.Equal("V2.RANGE", tooShort.Code);

        ValidationResult badSymbol = Validate(
            Plan("""{ "action": "Patrol", "args": { "route": ["$gate", "gatehouse_001_06"], "laps": 1 } }"""),
            "town_guard");

        Assert.Equal("V2.UNKNOWN_POI", badSymbol.Code);

        ValidationResult ok = Validate(
            Plan("""{ "action": "Patrol", "args": { "route": ["$gate", "$market"], "laps": 1 } }"""),
            "town_guard");

        Assert.True(ok.IsValid, $"{ok.Code}: {ok.Detail}");
    }

    [Fact]
    public void Vocabulary_NpcRefSymbolsOnly()
    {
        ValidationResult direct = Validate(
            Plan("""{ "action": "Talk", "args": { "npc": "npc_42", "topic": "trade" } }"""));

        Assert.Equal("V2.TYPE_MISMATCH", direct.Code);

        ValidationResult symbolic = Validate(
            Plan("""{ "action": "Talk", "args": { "npc": "nearest:merchant", "topic": "trade" } }"""));

        Assert.True(symbolic.IsValid, $"{symbolic.Code}: {symbolic.Detail}");
    }

    [Fact]
    public void Vocabulary_OptionalArgsMayBeOmitted()
    {
        ValidationResult result = Validate(Plan("""{ "action": "MoveTo", "args": { "poi": "$home" } }"""));

        Assert.True(result.IsValid, $"{result.Code}: {result.Detail}");
    }
}
