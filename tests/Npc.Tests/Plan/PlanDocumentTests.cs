using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Npc.Core.Plan;

namespace Npc.Tests.Plan;

/// <summary>docs/03 §1, §2. 플랜 DSL 의 C# 모델. 리플렉션 0.</summary>
public sealed class PlanDocumentTests
{
    /// <summary>docs/03 §1 의 예시 그대로.</summary>
    internal const string SpecExample = """
        {
          "schema": 1,
          "goal": "restock_and_forge",
          "reasoning": "ore depleted; siege raises weapon demand",
          "steps": [
            { "action": "MoveTo",  "args": { "poi": "$nearest_field", "speed": "run" }, "timeout_s": 300 },
            { "action": "Mine",    "args": { "resource": "iron_ore", "count": 8 }, "timeout_s": 900 },
            { "action": "MoveTo",  "args": { "poi": "$workplace" }, "timeout_s": 300 },
            { "action": "Craft",   "args": { "recipe": "iron_sword", "count": 3 }, "timeout_s": 1800 },
            { "action": "Store",   "args": { "item": "iron_sword", "count": 3 } },
            { "action": "MoveTo",  "args": { "poi": "$home" }, "timeout_s": 300 },
            { "action": "Sleep",   "args": { "until_time": "Morning" } }
          ],
          "on_step_fail": "fallback",
          "loop": true
        }
        """;

    private static PlanDocument Parse(string json) =>
        JsonSerializer.Deserialize(json, PlanJsonContext.Default.PlanDocument)!;

    [Fact]
    public void PlanDocument_ParsesSpecExample()
    {
        PlanDocument plan = Parse(SpecExample);

        Assert.Equal(1, plan.Schema);
        Assert.Equal("restock_and_forge", plan.Goal);
        Assert.Equal("ore depleted; siege raises weapon demand", plan.Reasoning);
        Assert.Equal(7, plan.Steps.Length);
        Assert.True(plan.Loop);
        Assert.Equal(StepFailPolicy.Fallback, plan.OnStepFail);

        Assert.Equal("MoveTo", plan.Steps[0].Action);
        Assert.Equal("$nearest_field", plan.Steps[0].Args["poi"].GetString());
        Assert.Equal("run", plan.Steps[0].Args["speed"].GetString());
        Assert.Equal(300, plan.Steps[0].TimeoutSeconds);

        Assert.Equal(8, plan.Steps[1].Args["count"].GetInt32());

        // timeout_s 가 없는 스텝은 null 이다 — 컴파일 시 액션의 default_timeout_s 로 채운다.
        Assert.Null(plan.Steps[4].TimeoutSeconds);
    }

    /// <summary>T1-22 완료 조건 — docs/03 §1 예시 JSON 왕복.</summary>
    [Fact]
    public void PlanDocument_RoundTripsSpecExample()
    {
        PlanDocument first = Parse(SpecExample);
        string serialized = JsonSerializer.Serialize(first, PlanJsonContext.Default.PlanDocument);
        PlanDocument second = Parse(serialized);

        Assert.Equal(first.Schema, second.Schema);
        Assert.Equal(first.Goal, second.Goal);
        Assert.Equal(first.Reasoning, second.Reasoning);
        Assert.Equal(first.Loop, second.Loop);
        Assert.Equal(first.OnStepFail, second.OnStepFail);
        Assert.Equal(first.Steps.Length, second.Steps.Length);

        for (int i = 0; i < first.Steps.Length; i++)
        {
            Assert.Equal(first.Steps[i].Action, second.Steps[i].Action);
            Assert.Equal(first.Steps[i].TimeoutSeconds, second.Steps[i].TimeoutSeconds);
            Assert.Equal(
                first.Steps[i].Args.Keys.Order(StringComparer.Ordinal),
                second.Steps[i].Args.Keys.Order(StringComparer.Ordinal));

            foreach ((string key, JsonElement value) in first.Steps[i].Args)
            {
                Assert.Equal(value.GetRawText(), second.Steps[i].Args[key].GetRawText());
            }
        }
    }

    /// <summary>T1-22 완료 조건 — 소스 생성 컨텍스트를 쓴다 (런타임 리플렉션 0).</summary>
    [Fact]
    public void PlanDocument_UsesSourceGeneratedContext()
    {
        JsonTypeInfo<PlanDocument> info = PlanJsonContext.Default.PlanDocument;

        Assert.NotNull(info);
        Assert.Same(PlanJsonContext.Default, info.Options.TypeInfoResolver);

        // 리플렉션 해석기를 빼고도 동작해야 한다.
        var sourceGenOnly = new JsonSerializerOptions { TypeInfoResolver = PlanJsonContext.Default };
        PlanDocument plan = JsonSerializer.Deserialize<PlanDocument>(SpecExample, sourceGenOnly)!;

        Assert.Equal(7, plan.Steps.Length);
    }

    /// <summary>on_step_fail 의 4종이 전부 왕복해야 한다.</summary>
    [Theory]
    [InlineData("fallback", StepFailPolicy.Fallback)]
    [InlineData("retry_once", StepFailPolicy.RetryOnce)]
    [InlineData("skip", StepFailPolicy.Skip)]
    [InlineData("replan", StepFailPolicy.Replan)]
    public void PlanDocument_StepFailPolicyRoundTrips(string text, StepFailPolicy expected)
    {
        PlanDocument plan = Parse(SpecExample.Replace("\"fallback\"", $"\"{text}\"", StringComparison.Ordinal));

        Assert.Equal(expected, plan.OnStepFail);
        Assert.Contains(
            $"\"on_step_fail\":\"{text}\"",
            JsonSerializer.Serialize(plan, PlanJsonContext.Default.PlanDocument),
            StringComparison.Ordinal);
    }

    /// <summary>스키마에 없는 필드는 거부한다 — docs/03 §3 의 V1.EXTRA_FIELD 근거.</summary>
    [Fact]
    public void PlanDocument_RejectsUnmappedMember()
    {
        const string WithExtra = """
            {
              "schema": 1, "goal": "test_goal", "loop": true,
              "npc_id": 7,
              "steps": [
                { "action": "Rest", "args": { "duration_s": 60 } },
                { "action": "Rest", "args": { "duration_s": 60 } },
                { "action": "Rest", "args": { "duration_s": 60 } }
              ]
            }
            """;

        Assert.Throws<JsonException>(() => Parse(WithExtra));
    }

    [Fact]
    public void PlanDocument_MissingRequiredMemberThrows()
    {
        const string NoLoop = """
            {
              "schema": 1, "goal": "test_goal",
              "steps": [
                { "action": "Rest", "args": { "duration_s": 60 } },
                { "action": "Rest", "args": { "duration_s": 60 } },
                { "action": "Rest", "args": { "duration_s": 60 } }
              ]
            }
            """;

        Assert.Throws<JsonException>(() => Parse(NoLoop));
    }

    [Fact]
    public void PlanDocument_ReasoningIsOptionalAndOmittedWhenNull()
    {
        var plan = new PlanDocument
        {
            Schema = 1,
            Goal = "test_goal",
            Loop = true,
            Steps =
            [
                new PlanStep { Action = "Rest", Args = [] },
                new PlanStep { Action = "Rest", Args = [] },
                new PlanStep { Action = "Rest", Args = [] },
            ],
        };

        string json = JsonSerializer.Serialize(plan, PlanJsonContext.Default.PlanDocument);

        Assert.DoesNotContain("reasoning", json, StringComparison.Ordinal);
        Assert.Equal(3, Parse(json).Steps.Length);
    }
}
