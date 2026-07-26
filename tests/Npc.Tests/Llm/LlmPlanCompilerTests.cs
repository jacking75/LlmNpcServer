using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.Llm;
using Npc.MasterData;
using Npc.Tests.Fakes;

namespace Npc.Tests.Llm;

/// <summary>
/// T2-09 · T2-11 — 단건 생성과 검증 결선. docs/12 §2 · §5.
/// LLM 을 부르지 않는다. 실제 호출은 <see cref="LlmPlanCompilerGoldenTests"/> 가 한다.
/// </summary>
public sealed class LlmPlanCompilerTests
{
    internal static readonly MasterDataSet Data = MasterDataLoader.Load(TestPaths.MasterData);
    internal static readonly PromptPrefix Prefix = PromptPrefix.Build(Data, TestPaths.MasterData);
    internal static readonly LlmOptions Options = LlmOptions.Load(TestPaths.At(LlmOptions.FileName));

    /// <summary>few-shot 1번과 같은 모양의, 실제로 통과하는 플랜.</summary>
    private const string ValidPlan = """
        {
          "schema": 1,
          "goal": "restock_and_forge",
          "reasoning": "test",
          "steps": [
            { "action": "MoveTo", "args": { "poi": "$nearest_field", "speed": "walk" }, "timeout_s": 600 },
            { "action": "Mine", "args": { "resource": "iron_ore", "count": 6 }, "timeout_s": 1800 },
            { "action": "MoveTo", "args": { "poi": "$workplace" }, "timeout_s": 600 },
            { "action": "Craft", "args": { "recipe": "iron_sword", "count": 3 }, "timeout_s": 1800 },
            { "action": "Store", "args": { "item": "iron_sword", "count": 3 }, "timeout_s": 120 },
            { "action": "MoveTo", "args": { "poi": "$home" }, "timeout_s": 600 },
            { "action": "Sleep", "args": { "until_time": "Morning" } }
          ],
          "on_step_fail": "fallback",
          "loop": true
        }
        """;

    internal static BucketKey BlacksmithMorning()
    {
        Assert.True(Data.Archetypes.TryGet("blacksmith", out ArchetypeDef archetype));
        return new BucketKey(archetype.Code, TimeOfDay.Morning, RegionState.Peace, Climate.Fair);
    }

    internal static LlmPlanCompiler CompilerWith(FakeChatClient client, string engineId = "gemini-3.1-flash-lite") =>
        new(Data, Prefix, Options.Engine(engineId), client);

    private static PlanRequest Request() => new(BlacksmithMorning(), Data.InitialFlags(BlacksmithMorning()));

    [Fact]
    public async Task Compiler_CompilesValidResponse()
    {
        var client = new FakeChatClient(ValidPlan);

        PlanCompileResult result = await CompilerWith(client).CompileAsync(Request(), CancellationToken.None);

        Assert.True(result.Validation.IsValid, result.Validation.Detail);
        Assert.NotNull(result.Plan);
        Assert.Equal("restock_and_forge", result.Plan!.Goal);
        Assert.Equal(7, result.Plan.Steps.Length);
        Assert.True(result.Plan.Loop);

        // 원본 JSON 은 검수·리플레이용으로 그대로 보존된다 (docs/03 §5).
        Assert.Contains("restock_and_forge", result.Plan.SourceJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compiler_StripsMarkdownFences()
    {
        var client = new FakeChatClient("```json\n" + ValidPlan + "\n```");

        PlanCompileResult result = await CompilerWith(client).CompileAsync(Request(), CancellationToken.None);

        Assert.True(result.Validation.IsValid, result.Validation.Detail);
        Assert.StartsWith("{", result.ResponseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compiler_RecordsStatsOnEveryCall()
    {
        var client = new FakeChatClient(ValidPlan);

        PlanCompileResult result = await CompilerWith(client).CompileAsync(Request(), CancellationToken.None);

        Assert.Equal(12_000, result.Stats.PromptTokens);
        Assert.Equal(11_500, result.Stats.CachedTokens);
        Assert.Equal(400, result.Stats.CompletionTokens);
        Assert.Equal("gemini-3.1-flash-lite", result.Stats.Model);
        Assert.Equal(1, result.Stats.Attempt);
        Assert.Equal(Prefix.Sha256, result.Stats.PrefixSha);
        Assert.True(result.Stats.CostUsd > 0);
        Assert.True(result.Stats.LatencyMs >= 0);
        Assert.Null(result.Stats.Error);
    }

    [Fact]
    public async Task Compiler_ReturnsCallFailedInsteadOfThrowing()
    {
        var client = new FakeChatClient(new HttpRequestException("429 Too Many Requests"));

        PlanCompileResult result = await CompilerWith(client).CompileAsync(Request(), CancellationToken.None);

        Assert.False(result.Validation.IsValid);
        Assert.Equal("V0.CALL_FAILED", result.Validation.Code);

        // 호출이 안 돼도 플랜은 나온다 — 아키타입 폴백이다 (T2-17).
        Assert.NotNull(result.Plan);
        Assert.Equal(PlanOrigin.Fallback, result.Plan!.Origin);
        Assert.Contains("429", result.Stats.Error!, StringComparison.Ordinal);
    }

    [Theory]
    // 1단 — 파싱·스키마
    [InlineData("not json at all", "V1.PARSE")]
    [InlineData("""{"schema":1,"goal":"nap","loop":false,"steps":[{"action":"Rest","args":{}}]}""", "V1.STEP_COUNT")]
    // 2단 — 어휘
    [InlineData(
        """
        {"schema":1,"goal":"dig","loop":true,"steps":[
          {"action":"Excavate","args":{}},{"action":"Rest","args":{}},{"action":"Wait","args":{}}]}
        """,
        "V2.UNKNOWN_ACTION")]
    [InlineData(
        """
        {"schema":1,"goal":"guarding","loop":true,"steps":[
          {"action":"Guard","args":{"poi":"$gate"}},{"action":"Rest","args":{}},{"action":"Wait","args":{}}]}
        """,
        "V2.ACTION_NOT_ALLOWED")]
    [InlineData(
        """
        {"schema":1,"goal":"walking","loop":true,"steps":[
          {"action":"MoveTo","args":{"poi":"smithy_01"}},{"action":"Rest","args":{}},{"action":"Wait","args":{}}]}
        """,
        "V2.UNKNOWN_POI")]
    public async Task Compiler_AlwaysValidates(string response, string expectedCode)
    {
        // 검증 우회 경로가 없다 — 강제 디코딩을 신뢰하지 않는다 (CLAUDE.md §2.6).
        var client = new FakeChatClient(response);

        PlanCompileResult result = await CompilerWith(client).CompileAsync(Request(), CancellationToken.None);

        Assert.Equal(expectedCode, result.Validation.Code);
        Assert.Equal(PlanOrigin.Fallback, result.Plan!.Origin);

        // 실패해도 원문은 보존된다 — planstore/rejected/ 가 이것을 쓴다.
        Assert.Equal(response.Trim(), result.ResponseText);
    }

    [Fact]
    public async Task Compiler_SendsPrefixAsSystemAndSuffixAsUser()
    {
        var client = new FakeChatClient(ValidPlan);

        await CompilerWith(client).CompileAsync(Request(), CancellationToken.None);

        Assert.Equal(Prefix.Text, client.SystemPrompts[0]);
        Assert.Contains("# REQUEST", client.UserPrompts[0], StringComparison.Ordinal);
        Assert.Contains("\"archetype\":\"blacksmith\"", client.UserPrompts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compiler_DoesNotForceDecodingByDefault()
    {
        var client = new FakeChatClient(ValidPlan);

        await CompilerWith(client).CompileAsync(Request(), CancellationToken.None);

        Assert.Null(client.Options[0]!.ResponseFormat);
        Assert.False(client.Options[0]!.Temperature is null);
    }

    [Fact]
    public void StripFences_LeavesPlainJsonAlone()
    {
        Assert.Equal("{\"a\":1}", LlmPlanCompiler.StripFences("  {\"a\":1}\n"));
        Assert.Equal("{\"a\":1}", LlmPlanCompiler.StripFences("```json\n{\"a\":1}\n```"));
        Assert.Equal("{\"a\":1}", LlmPlanCompiler.StripFences("```\n{\"a\":1}\n```"));
        Assert.Equal(string.Empty, LlmPlanCompiler.StripFences(null));
    }
}
