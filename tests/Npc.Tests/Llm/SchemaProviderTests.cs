using System.Text.Json;
using Json.Schema;
using Npc.Llm;
using Npc.MasterData;

namespace Npc.Tests.Llm;

/// <summary>
/// T2-02 — 스키마 생성기. docs/03 §2 · docs/12 §4 · docs/measurements/W1_schema.md.
/// </summary>
public sealed class SchemaProviderTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>docs/03 §1 의 예시 플랜. 사양에서 그대로 옮겼다.</summary>
    private const string SpecExamplePlan = """
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

    [Fact]
    public void Schema_GeneratedFromCatalog()
    {
        SchemaProvider provider = SchemaProvider.Build(s_data);

        string[] catalog = [.. s_data.Actions.Actions.Select(a => a.Id)];

        Assert.Equal(37, catalog.Length);
        Assert.Equal(catalog, provider.ActionIds);

        // 스키마 본문의 열거값도 같아야 한다 — 손으로 유지하는 목록이 어디에도 없어야 한다.
        using JsonDocument document = JsonDocument.Parse(provider.PlanSchemaJson);

        JsonElement actionEnum = document.RootElement
            .GetProperty("properties").GetProperty("steps")
            .GetProperty("items").GetProperty("properties")
            .GetProperty("action").GetProperty("enum");

        Assert.Equal(catalog, actionEnum.EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Theory]
    [InlineData("minItems")]
    [InlineData("maxItems")]
    [InlineData("additionalProperties")]
    [InlineData("required")]
    [InlineData("maximum")]
    [InlineData("minimum")]
    [InlineData("pattern")]
    [InlineData("oneOf")]
    [InlineData("anyOf")]
    public void Schema_OmitsKeywordsThatW1Flagged(string keyword)
    {
        // 전부 검증기 1·2단이 다시 잡는다. 스키마로 강제하면 모델이 문법을 지키며 인자를 지어낸다.
        foreach (SchemaProfile profile in new[] { SchemaProfile.Full, SchemaProfile.Bare })
        {
            SchemaProvider provider = SchemaProvider.Build(s_data, profile);

            Assert.DoesNotContain($"\"{keyword}\"", provider.PlanSchemaJson, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Schema_AcceptsSpecExamplePlan()
    {
        foreach (SchemaProfile profile in new[] { SchemaProfile.Full, SchemaProfile.Bare })
        {
            SchemaProvider provider = SchemaProvider.Build(s_data, profile);

            (bool ok, string why) = Evaluate(provider.PlanSchemaJson, SpecExamplePlan);

            Assert.True(ok, $"{profile}: {why}");
        }
    }

    [Fact]
    public void Schema_RejectsUnknownAction()
    {
        SchemaProvider provider = SchemaProvider.Build(s_data);

        string plan = SpecExamplePlan.Replace("\"Mine\"", "\"Excavate\"", StringComparison.Ordinal);

        (bool ok, _) = Evaluate(provider.PlanSchemaJson, plan);

        Assert.False(ok);
    }

    [Fact]
    public void Schema_RejectsHallucinatedPoiSymbol()
    {
        SchemaProvider provider = SchemaProvider.Build(s_data);

        string plan = SpecExamplePlan.Replace("\"$workplace\"", "\"smithy_01\"", StringComparison.Ordinal);

        (bool ok, _) = Evaluate(provider.PlanSchemaJson, plan);

        Assert.False(ok);
    }

    [Fact]
    public void Schema_IsDeterministic()
    {
        string first = SchemaProvider.Build(s_data).Sha256;

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(first, SchemaProvider.Build(s_data).Sha256);
        }
    }

    [Fact]
    public void Schema_BareProfileLeavesArgsOpen()
    {
        SchemaProvider bare = SchemaProvider.Build(s_data, SchemaProfile.Bare);
        SchemaProvider full = SchemaProvider.Build(s_data, SchemaProfile.Full);

        JsonElement bareArgs = ArgsOf(bare);
        JsonElement fullArgs = ArgsOf(full);

        Assert.False(bareArgs.TryGetProperty("properties", out _));
        Assert.True(fullArgs.TryGetProperty("properties", out JsonElement properties));

        // 평탄화 합집합에는 카탈로그의 모든 파라미터 이름이 들어 있어야 한다.
        string[] names = [.. s_data.Actions.Actions.SelectMany(a => a.Params).Select(p => p.Name).Distinct()];

        foreach (string name in names)
        {
            Assert.True(properties.TryGetProperty(name, out _), name);
        }
    }

    private static JsonElement ArgsOf(SchemaProvider provider) =>
        JsonDocument.Parse(provider.PlanSchemaJson).RootElement
            .GetProperty("properties").GetProperty("steps")
            .GetProperty("items").GetProperty("properties")
            .GetProperty("args")
            .Clone();

    private static (bool Ok, string Detail) Evaluate(string schemaJson, string instanceJson)
    {
        using JsonDocument instance = JsonDocument.Parse(instanceJson);

        EvaluationResults results = JsonSchema.FromText(schemaJson).Evaluate(
            instance.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = false });

        if (results.IsValid)
        {
            return (true, string.Empty);
        }

        string[] reasons = [.. Flatten(results)
            .Where(r => r is { IsValid: false, Errors.Count: > 0 })
            .SelectMany(r => r.Errors!.Select(e => $"{r.InstanceLocation}: {e.Key} {e.Value}"))
            .Distinct(StringComparer.Ordinal)
            .Take(5)];

        return (false, string.Join(" | ", reasons));
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults results)
    {
        yield return results;

        foreach (EvaluationResults detail in results.Details ?? [])
        {
            foreach (EvaluationResults nested in Flatten(detail))
            {
                yield return nested;
            }
        }
    }
}
