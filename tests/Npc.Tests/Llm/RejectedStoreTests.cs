using System.Text.Json;
using Npc.Core;
using Npc.Core.Validation;
using Npc.Llm;
using Npc.MasterData;
using Npc.Tests.Fakes;

namespace Npc.Tests.Llm;

/// <summary>T2-18 — 검증 실패 산출물 보존. docs/03 §7 · docs/13 §8.</summary>
public sealed class RejectedStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "npc_rejected_" + Guid.NewGuid().ToString("N")[..8]);

    private const string AlwaysFails = """
        {"schema":1,"goal":"forge_now","loop":true,"steps":[
          {"action":"Craft","args":{"recipe":"iron_sword","count":1}},
          {"action":"MoveTo","args":{"poi":"$home"}},
          {"action":"Sleep","args":{"until_time":"Morning"}}]}
        """;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Rejected_WritesOneFilePerFailedAttempt()
    {
        var store = new RejectedStore(_directory, LlmPlanCompilerTests.Data);
        BucketKey bucket = LlmPlanCompilerTests.BlacksmithMorning();

        var compiler = new LlmPlanCompiler(
            LlmPlanCompilerTests.Data,
            LlmPlanCompilerTests.Prefix,
            LlmPlanCompilerTests.Options.Engine("gemini-3.1-flash-lite"),
            new FakeChatClient(AlwaysFails),
            stats: null,
            dryRun: null,
            reuse: null,
            rejected: store);

        await compiler.CompileAsync(
            new PlanRequest(bucket, LlmPlanCompilerTests.Data.InitialFlags(bucket)),
            CancellationToken.None);

        // 시도 1과 시도 2가 서로를 덮어쓰지 않는다.
        Assert.Equal(2, store.Count);
        Assert.True(File.Exists(store.PathOf(bucket, 1)));
        Assert.True(File.Exists(store.PathOf(bucket, 2)));

        // 파일명은 docs/03 §7 의 <bucket>.<attempt>.json 이다.
        Assert.Equal("blacksmith@Morning.Peace.Fair.1.json", Path.GetFileName(store.PathOf(bucket, 1)));
    }

    [Fact]
    public void Rejected_KeepsFailureCodeAndRawOutput()
    {
        var store = new RejectedStore(_directory, LlmPlanCompilerTests.Data);
        BucketKey bucket = LlmPlanCompilerTests.BlacksmithMorning();

        var stats = new CompileStats(
            12_000, 11_500, 400, 2_100.5, 0.00034, "gemini-3.5-flash-lite", 1,
            LlmPlanCompilerTests.Prefix.Sha256, false, null);

        ValidationResult failure = ValidationResult.Fail(
            ValidationStage.Coherence, "V3.PRECONDITION_UNMET", 0,
            "Craft requires AtWorkplace but no preceding step grants it.");

        store.Save(bucket, in stats, in failure, AlwaysFails);

        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(store.PathOf(bucket, 1)));
        JsonElement root = saved.RootElement;

        Assert.Equal("blacksmith@Morning.Peace.Fair", root.GetProperty("bucket").GetString());
        Assert.Equal("blacksmith", root.GetProperty("archetype").GetString());
        Assert.Equal(1, root.GetProperty("attempt").GetInt32());
        Assert.Equal("Coherence", root.GetProperty("stage").GetString());
        Assert.Equal("V3.PRECONDITION_UNMET", root.GetProperty("code").GetString());
        Assert.Equal(0, root.GetProperty("step").GetInt32());
        Assert.Contains("AtWorkplace", root.GetProperty("detail").GetString()!, StringComparison.Ordinal);

        // 계측도 같이 남는다 — 실패에 얼마를 썼는지가 비용 보고의 일부다.
        Assert.Equal("gemini-3.5-flash-lite", root.GetProperty("model").GetString());
        Assert.Equal(LlmPlanCompilerTests.Prefix.Sha256, root.GetProperty("prefix_sha").GetString());
        Assert.Equal(12_000, root.GetProperty("prompt_tokens").GetInt32());
        Assert.False(root.GetProperty("forced_decoding").GetBoolean());

        // 모델이 실제로 뱉은 문장이 그대로 있다.
        Assert.Equal(AlwaysFails, root.GetProperty("response").GetString());
    }

    [Fact]
    public void Rejected_IsDeterministic()
    {
        // 시각을 넣지 않는다 — 같은 입력이면 같은 파일이어야 diff 가 의미를 갖는다 (CLAUDE.md §2.3).
        var store = new RejectedStore(_directory, LlmPlanCompilerTests.Data);
        BucketKey bucket = LlmPlanCompilerTests.BlacksmithMorning();

        var stats = new CompileStats(1, 0, 1, 1, 0, "m", 1, "sha", false, null);
        ValidationResult failure = ValidationResult.Fail(ValidationStage.Schema, "V1.PARSE", -1, "bad json");

        store.Save(bucket, in stats, in failure, "not json");
        string first = File.ReadAllText(store.PathOf(bucket, 1));

        store.Save(bucket, in stats, in failure, "not json");
        string second = File.ReadAllText(store.PathOf(bucket, 1));

        Assert.Equal(first, second, StringComparer.Ordinal);
    }

    [Fact]
    public void Rejected_IgnoresPassingResults()
    {
        var store = new RejectedStore(_directory, LlmPlanCompilerTests.Data);
        BucketKey bucket = LlmPlanCompilerTests.BlacksmithMorning();
        var stats = new CompileStats(1, 0, 1, 1, 0, "m", 1, "sha", false, null);

        store.Save(bucket, in stats, ValidationResult.Ok, "{}");

        Assert.Equal(0, store.Count);
        Assert.False(Directory.Exists(store.Directory));
    }

    [Fact]
    public void Rejected_DefaultLocationIsPlanStoreRejected()
    {
        RejectedStore store = RejectedStore.CreateDefault("planstore", LlmPlanCompilerTests.Data);

        Assert.Equal(Path.Combine("planstore", "rejected"), store.Directory);
    }
}
