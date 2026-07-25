using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.Tests.Plan;

namespace Npc.Tests.Validation;

/// <summary>docs/03 §3 1단. V1.* 각 코드마다 유발 픽스처가 있어야 한다.</summary>
public sealed class SchemaValidatorTests
{
    private static ValidationResult Validate(string json) => SchemaValidator.Validate(json, out _);

    [Fact]
    public void Schema_AcceptsSpecExample()
    {
        ValidationResult result = SchemaValidator.Validate(PlanDocumentTests.SpecExample, out PlanDocument? doc);

        Assert.True(result.IsValid, result.Detail);
        Assert.NotNull(doc);
        Assert.Equal(7, doc!.Steps.Length);
    }

    [Fact]
    public void Schema_V1_PARSE()
    {
        ValidationResult result = Validate("{ this is not json ");

        Assert.Equal(ValidationStage.Schema, result.FailedAt);
        Assert.Equal("V1.PARSE", result.Code);
    }

    [Fact]
    public void Schema_V1_SCHEMA_MissingRequiredField()
    {
        const string Json = """
            { "schema": 1, "goal": "test_goal",
              "steps": [
                { "action": "Rest", "args": {} },
                { "action": "Rest", "args": {} },
                { "action": "Rest", "args": {} }
              ] }
            """;

        ValidationResult result = Validate(Json);

        Assert.Equal("V1.SCHEMA", result.Code);
        Assert.Contains("loop", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"Goal_With_Caps\"")]
    [InlineData("\"ab\"")]
    [InlineData("\"9starts_with_digit\"")]
    [InlineData("123")]
    public void Schema_V1_SCHEMA_BadGoalPattern(string goal)
    {
        string json = $$"""
            { "schema": 1, "goal": {{goal}}, "loop": true,
              "steps": [
                { "action": "Rest", "args": {} },
                { "action": "Rest", "args": {} },
                { "action": "Rest", "args": {} }
              ] }
            """;

        Assert.Equal("V1.SCHEMA", Validate(json).Code);
    }

    [Fact]
    public void Schema_V1_STEP_COUNT_TooFew()
    {
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [ { "action": "Rest", "args": {} }, { "action": "Rest", "args": {} } ] }
            """;

        ValidationResult result = Validate(Json);

        Assert.Equal("V1.STEP_COUNT", result.Code);
        Assert.Contains("2", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_V1_STEP_COUNT_TooMany()
    {
        string steps = string.Join(',', Enumerable.Repeat("{ \"action\": \"Rest\", \"args\": {} }", 11));
        string json = $$"""
            { "schema": 1, "goal": "test_goal", "loop": true, "steps": [ {{steps}} ] }
            """;

        Assert.Equal("V1.STEP_COUNT", Validate(json).Code);
    }

    [Fact]
    public void Schema_V1_EXTRA_FIELD_OnDocument()
    {
        // docs/03 §1 이 의도적으로 배제한 필드. 플랜이 개체에 묶이면 캐시가 무의미해진다.
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true, "npc_id": 7,
              "steps": [
                { "action": "Rest", "args": {} },
                { "action": "Rest", "args": {} },
                { "action": "Rest", "args": {} }
              ] }
            """;

        ValidationResult result = Validate(Json);

        Assert.Equal("V1.EXTRA_FIELD", result.Code);
        Assert.Contains("npc_id", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_V1_EXTRA_FIELD_OnStep()
    {
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "Rest", "args": {}, "comment": "왜 쉬는지" },
                { "action": "Rest", "args": {} },
                { "action": "Rest", "args": {} }
              ] }
            """;

        ValidationResult result = Validate(Json);

        Assert.Equal("V1.EXTRA_FIELD", result.Code);
        Assert.Equal(0, result.StepIndex);
    }

    [Fact]
    public void Schema_RejectsOutOfRangeTimeout()
    {
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "Rest", "args": {}, "timeout_s": 99999 },
                { "action": "Rest", "args": {} },
                { "action": "Rest", "args": {} }
              ] }
            """;

        ValidationResult result = Validate(Json);

        Assert.Equal("V1.SCHEMA", result.Code);
        Assert.Equal(0, result.StepIndex);
    }

    [Fact]
    public void Schema_RejectsTooLongReasoning()
    {
        string json = $$"""
            { "schema": 1, "goal": "test_goal", "loop": true,
              "reasoning": "{{new string('x', 201)}}",
              "steps": [
                { "action": "Rest", "args": {} },
                { "action": "Rest", "args": {} },
                { "action": "Rest", "args": {} }
              ] }
            """;

        Assert.Equal("V1.SCHEMA", Validate(json).Code);
    }

    [Fact]
    public void Schema_RejectsUnknownFailPolicy()
    {
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true, "on_step_fail": "explode",
              "steps": [
                { "action": "Rest", "args": {} },
                { "action": "Rest", "args": {} },
                { "action": "Rest", "args": {} }
              ] }
            """;

        Assert.Equal("V1.SCHEMA", Validate(Json).Code);
    }

    [Fact]
    public void Schema_RejectsNonObjectArgs()
    {
        const string Json = """
            { "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "Rest", "args": "duration_s=60" },
                { "action": "Rest", "args": {} },
                { "action": "Rest", "args": {} }
              ] }
            """;

        ValidationResult result = Validate(Json);

        Assert.Equal("V1.SCHEMA", result.Code);
        Assert.Equal(0, result.StepIndex);
    }
}
