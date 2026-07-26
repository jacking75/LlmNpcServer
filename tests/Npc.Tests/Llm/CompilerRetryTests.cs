using Npc.Core;
using Npc.Core.Validation;
using Npc.Llm;
using Npc.Sim.Validation;
using Npc.Tests.Fakes;

namespace Npc.Tests.Llm;

/// <summary>T2-15 — 재시도 파이프라인. docs/12 §6.</summary>
public sealed class CompilerRetryTests
{
    /// <summary>1·2단은 통과하지만 3단에서 걸리는 플랜 — 일터에 가지 않고 제작한다.</summary>
    private const string CoherenceFailure = """
        {"schema":1,"goal":"forge_now","loop":true,"steps":[
          {"action":"Craft","args":{"recipe":"iron_sword","count":1}},
          {"action":"MoveTo","args":{"poi":"$home"}},
          {"action":"Sleep","args":{"until_time":"Morning"}}]}
        """;

    private const string ValidPlan = """
        {"schema":1,"goal":"forge_batch","loop":true,"steps":[
          {"action":"MoveTo","args":{"poi":"$workplace"},"timeout_s":600},
          {"action":"Work","args":{"recipe":"iron_sword","count":1},"timeout_s":1800},
          {"action":"Store","args":{"item":"iron_sword","count":1},"timeout_s":120},
          {"action":"MoveTo","args":{"poi":"$home"},"timeout_s":600},
          {"action":"Sleep","args":{"until_time":"Morning"}}]}
        """;

    private static PlanRequest Request()
    {
        BucketKey bucket = LlmPlanCompilerTests.BlacksmithMorning();
        return new PlanRequest(bucket, LlmPlanCompilerTests.Data.InitialFlags(bucket));
    }

    private static LlmPlanCompiler Compiler(FakeChatClient client, ICompileStatsSink? stats = null) =>
        new(
            LlmPlanCompilerTests.Data,
            LlmPlanCompilerTests.Prefix,
            LlmPlanCompilerTests.Options.Engine("gemini-3.1-flash-lite"),
            client,
            stats,
            new DryRunValidator(LlmPlanCompilerTests.Data));

    [Fact]
    public async Task Retry_MaxOnce()
    {
        // 두 번 다 실패해도 세 번째는 없다. docs/12 §6 — 3회 이상은 어떤 결과가 나와도 하지 않는다.
        var client = new FakeChatClient(CoherenceFailure);

        PlanCompileResult result = await Compiler(client).CompileAsync(Request(), CancellationToken.None);

        Assert.Equal(LlmPlanCompiler.MaxAttempts, client.Calls);
        Assert.Equal(2, client.Calls);
        Assert.False(result.Validation.IsValid);
        Assert.Equal("V3.PRECONDITION_UNMET", result.Validation.Code);
    }

    [Fact]
    public async Task Retry_DoesNotHappenWhenFirstAttemptPasses()
    {
        var client = new FakeChatClient(ValidPlan);

        PlanCompileResult result = await Compiler(client).CompileAsync(Request(), CancellationToken.None);

        Assert.True(result.Validation.IsValid, result.Validation.Detail);
        Assert.Equal(1, client.Calls);
        Assert.Equal(1, result.Stats.Attempt);
    }

    [Fact]
    public async Task Retry_DoesNotTouchPrefix()
    {
        var client = new FakeChatClient(CoherenceFailure);

        PlanCompileResult result = await Compiler(client).CompileAsync(Request(), CancellationToken.None);

        // 프리픽스가 1바이트라도 달라지면 프롬프트 캐시가 전면 미적중이 된다.
        Assert.Equal(2, client.SystemPrompts.Count);
        Assert.Equal(client.SystemPrompts[0], client.SystemPrompts[1], StringComparer.Ordinal);
        Assert.Equal(LlmPlanCompilerTests.Prefix.Text, client.SystemPrompts[1]);
        Assert.Equal(LlmPlanCompilerTests.Prefix.Sha256, result.Stats.PrefixSha);
    }

    [Fact]
    public async Task Retry_PutsFailureBlockInTheSuffixOnly()
    {
        var client = new FakeChatClient(CoherenceFailure);

        await Compiler(client).CompileAsync(Request(), CancellationToken.None);

        Assert.DoesNotContain("previous_attempt_failed", client.UserPrompts[0], StringComparison.Ordinal);
        Assert.Contains("previous_attempt_failed", client.UserPrompts[1], StringComparison.Ordinal);
        Assert.Contains("V3.PRECONDITION_UNMET", client.UserPrompts[1], StringComparison.Ordinal);

        // 프리픽스에는 절대 실리지 않는다.
        Assert.DoesNotContain("previous_attempt_failed", client.SystemPrompts[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_RaisesTemperature()
    {
        var client = new FakeChatClient(CoherenceFailure);

        await Compiler(client).CompileAsync(Request(), CancellationToken.None);

        // docs/12 §6 — 시도 1은 0.4, 시도 2는 0.6.
        Assert.Equal(0.4f, client.Options[0]!.Temperature);
        Assert.Equal(0.6f, client.Options[1]!.Temperature);
    }

    [Fact]
    public async Task Retry_SucceedsOnSecondAttempt()
    {
        var collector = new CompileStatsCollector();
        var client = new FakeChatClient(attempt => attempt == 1 ? CoherenceFailure : ValidPlan);

        PlanCompileResult result = await Compiler(client, collector).CompileAsync(Request(), CancellationToken.None);

        Assert.True(result.Validation.IsValid, result.Validation.Detail);
        Assert.NotNull(result.Plan);
        Assert.Equal(2, result.Stats.Attempt);

        // 누계는 두 호출을 합친 값이다.
        Assert.Equal(24_000, result.Stats.PromptTokens);

        // 두 호출 다 기록되고, 성공은 "2회째 성공"으로 쌓인다.
        Assert.Equal(2, collector.Calls);
        Assert.Equal(1, collector.Passed);
        Assert.Equal(0, collector.AttemptHistogram[0]);
        Assert.Equal(1, collector.AttemptHistogram[1]);
    }

    [Fact]
    public async Task Compiler_RunsAllFourStages()
    {
        // 4단만 걸리는 플랜 — loop 인데 한 사이클이 몇 분이다.
        const string ShortLoop = """
            {"schema":1,"goal":"twitch_loop","loop":true,"steps":[
              {"action":"Wait","args":{"duration_s":60}},
              {"action":"Emote","args":{"animation":"nod"}},
              {"action":"Wait","args":{"duration_s":60}}]}
            """;

        var client = new FakeChatClient(ShortLoop);

        PlanCompileResult result = await Compiler(client).CompileAsync(Request(), CancellationToken.None);

        Assert.Equal(ValidationStage.DryRun, result.Validation.FailedAt);
        Assert.Equal("V4.INFINITE_LOOP", result.Validation.Code);
        Assert.Equal(Npc.Core.Plan.PlanOrigin.Fallback, result.Plan!.Origin);
    }

    [Fact]
    public async Task Compiler_SkipsStage4WhenNoValidatorInjected()
    {
        // Npc.Llm 은 Npc.Sim 을 참조하지 않는다. 4단 구현이 없으면 3단까지가 전부다.
        const string ShortLoop = """
            {"schema":1,"goal":"twitch_loop","loop":true,"steps":[
              {"action":"Wait","args":{"duration_s":60}},
              {"action":"Emote","args":{"animation":"nod"}},
              {"action":"Wait","args":{"duration_s":60}}]}
            """;

        var compiler = new LlmPlanCompiler(
            LlmPlanCompilerTests.Data,
            LlmPlanCompilerTests.Prefix,
            LlmPlanCompilerTests.Options.Engine("gemini-3.1-flash-lite"),
            new FakeChatClient(ShortLoop));

        PlanCompileResult result = await compiler.CompileAsync(Request(), CancellationToken.None);

        Assert.True(result.Validation.IsValid);
        Assert.NotNull(result.Plan);
    }
}
