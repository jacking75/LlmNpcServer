using Microsoft.Extensions.AI;
using Npc.Llm;
using Npc.MasterData;

namespace Npc.Tests.Llm;

/// <summary>T2-08 — IChatClient 생성. docs/10 §2 T0-2 · CLAUDE.md §2.7.</summary>
public sealed class ChatClientFactoryTests
{
    private static readonly LlmOptions s_options = LlmOptions.Load(TestPaths.At(LlmOptions.FileName));

    [Fact]
    public void Options_LoadEveryEngineFromConfiguration()
    {
        Assert.NotEmpty(s_options.Engines);
        Assert.Contains(s_options.Engines, e => e.Kind == LlmEngineKind.DotLlm);
        Assert.Contains(s_options.Engines, e => e.Kind == LlmEngineKind.LlamaCpp);
        Assert.Contains(s_options.Engines, e => e.Kind == LlmEngineKind.External);

        // 기본 엔진이 목록에 있어야 한다.
        Assert.NotNull(s_options.Engine());
    }

    [Fact]
    public void Options_SwitchLocalToExternalByConfigurationOnly()
    {
        // 같은 코드 경로가 로컬·외부를 다 만든다. 코드에 제공사 이름이 나오지 않는다.
        //
        // 키가 없는 외부 엔진까지 만들라고 요구하면 이 테스트가 기계의 환경변수에 매인다.
        // 대신 두 갈래를 다 단언한다 — 있으면 만들어지고, 없으면 어느 환경변수가 비었는지 말한다.
        foreach (LlmEngineOptions engine in s_options.Engines)
        {
            if (!ChatClientFactory.IsAvailable(engine))
            {
                var missing = Assert.Throws<InvalidOperationException>(() => ChatClientFactory.Create(engine));

                Assert.Contains(engine.ApiKeyEnv!, missing.Message, StringComparison.Ordinal);
                continue;
            }

            using IChatClient client = ChatClientFactory.Create(engine);

            Assert.NotNull(client);
        }

        // 로컬 엔진은 키가 필요 없으므로 어느 기계에서든 만들어져야 한다.
        Assert.All(
            s_options.Engines.Where(e => e.IsLocal),
            e => Assert.True(ChatClientFactory.IsAvailable(e), e.Id));

        Assert.True(s_options.Engine("dotllm-qwen2.5-7b").IsLocal);
        Assert.False(s_options.Engine("gemini-3.1-flash-lite").IsLocal);
    }

    [Fact]
    public void Options_KeepApiKeysOutOfTheFile()
    {
        string text = File.ReadAllText(TestPaths.At(LlmOptions.FileName));

        // 설정 파일에는 환경변수 "이름"만 있어야 한다.
        Assert.Contains("GEMINI_API_KEY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AIza", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_DoNotForceJsonSchemaByDefault()
    {
        // W1(T0-09): 강제 디코딩은 유효 JSON 99/100 인데 어휘 검증 통과가 0 이었다.
        Assert.All(s_options.Engines, e => Assert.False(e.ForceJsonSchema));
    }

    [Fact]
    public void Options_UnknownEngineThrows() =>
        Assert.Throws<InvalidDataException>(() => s_options.Engine("no-such-engine"));

    [Fact]
    public void Engine_CostUsesCachedRateForCachedTokens()
    {
        LlmEngineOptions engine = s_options.Engine("gemini-3.1-flash-lite");

        // 12,000 입력 중 11,000 이 캐시 적중, 출력 400.
        double cost = engine.CostUsd(12_000, 11_000, 400);
        double expected = ((1_000 * 0.10) + (11_000 * 0.025) + (400 * 0.40)) / 1_000_000d;

        Assert.Equal(expected, cost, 12);

        // 로컬은 언제나 0 이다.
        Assert.Equal(0, s_options.Engine("dotllm-phi4-mini").CostUsd(12_000, 0, 400));
    }

    [Fact]
    public void Usage_ExposesCachedTokensFromAdditionalCounts()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 12_000,
                OutputTokenCount = 400,
                AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cached_tokens"] = 11_000 },
            },
        };

        Assert.Equal((12_000, 11_000, 400), ChatClientFactory.ReadUsage(response));
    }

    [Fact]
    public void Usage_ScrapesCachedTokensFromRawJson()
    {
        // dotLLM 처럼 AdditionalCounts 를 안 채우는 서버 대비 경로 (W1_env.md §4.4).
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))
        {
            Usage = new UsageDetails { InputTokenCount = 4_400, OutputTokenCount = 210 },
            RawRepresentation = BinaryData.FromString(
                """{"usage":{"prompt_tokens":4400,"prompt_tokens_details":{"cached_tokens":4079}}}"""),
        };

        Assert.Equal((4_400, 4_079, 210), ChatClientFactory.ReadUsage(response));
    }

    [Fact]
    public void Usage_ReturnsZeroCachedWhenServerDoesNotReport()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))
        {
            Usage = new UsageDetails { InputTokenCount = 4_400, OutputTokenCount = 210 },
        };

        Assert.Equal((4_400, 0, 210), ChatClientFactory.ReadUsage(response));
    }

    [Fact]
    public void Messages_PutEngineTagOnSuffixOnly()
    {
        LlmEngineOptions engine = s_options.Engine("llamacpp-qwen3-8b");

        Assert.Equal(" /no_think", engine.SuffixTag);

        List<ChatMessage> messages = ChatClientFactory.BuildMessages(engine, "PREFIX", "SUFFIX");

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal("PREFIX", messages[0].Text);
        Assert.Equal("SUFFIX /no_think", messages[1].Text);
    }

    [Fact]
    public void ChatOptions_RaiseTemperatureOnRetry()
    {
        MasterDataSet data = MasterDataLoader.Load(TestPaths.MasterData);
        SchemaProvider schema = SchemaProvider.Build(data);
        LlmEngineOptions engine = s_options.Engine("gemini-3.1-flash-lite");

        // docs/12 §6 — 시도 1은 0.4, 시도 2는 0.6.
        Assert.Equal(0.4f, ChatClientFactory.BuildChatOptions(engine, schema, 1).Temperature);
        Assert.Equal(0.6f, ChatClientFactory.BuildChatOptions(engine, schema, 2).Temperature);

        Assert.Null(ChatClientFactory.BuildChatOptions(engine, schema, 1).ResponseFormat);
        Assert.NotNull(ChatClientFactory
            .BuildChatOptions(engine with { ForceJsonSchema = true }, schema, 1).ResponseFormat);
    }

    [Fact]
    public void Options_LoadDefaultFindsTheFileByWalkingUp()
    {
        LlmOptions found = LlmOptions.LoadDefault(TestPaths.At("tests", "Npc.Tests"));

        Assert.Equal(s_options.Default, found.Default);
        Assert.Equal(s_options.Engines.Length, found.Engines.Length);
    }
}
