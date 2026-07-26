using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;
using Npc.Prebake;

namespace Npc.Tests.Prebake;

/// <summary>manifest 작성. docs/03 §7 · T3-16.</summary>
public sealed class ManifestWriterTests : IDisposable
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);
    private static readonly PromptPrefix s_prefix = PromptPrefix.Build(s_data, TestPaths.MasterData);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "npc-manifest-writer-" + Guid.NewGuid().ToString("N")[..8]);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static LlmEngineOptions Engine => new(
        "test-engine", LlmEngineKind.External, "test-model", "https://example.invalid/v1",
        InputUsdPerMTok: 0.10, CachedInputUsdPerMTok: 0.01, OutputUsdPerMTok: 0.40, Temperature: 0.4f);

    private static CompiledPlan Plan(BucketKey bucket, PlanOrigin origin)
    {
        const string Json = """
            {"schema":1,"goal":"manifest_sample","loop":true,"steps":[
              {"action":"MoveTo","args":{"poi":"$home"}},
              {"action":"Rest","args":{"duration_s":600}},
              {"action":"Wait","args":{"duration_s":60}}]}
            """;

        PlanDocument document = System.Text.Json.JsonSerializer.Deserialize(
            Json, PlanJsonContext.Default.PlanDocument)!;

        return Npc.Core.Plan.PlanCompiler.Compile(
            document, bucket, new PlanId(0), s_data, origin, version: 1, sourceJson: Json);
    }

    private static CompileStats Stats(int attempt, double cost, string? error = null) =>
        new(12_000, 11_500, 400, 1_234.5, cost, "test-engine", attempt, s_prefix.Sha256, false, error);

    /// <summary>
    /// T3-16 완료 조건 — counts/validation/cost/wall_clock 이 전부 들어간다.
    /// </summary>
    [Fact]
    public void Manifest_CarriesCountsValidationCostAndWallClock()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        // 10 생성 + 2 pinned.
        for (int index = 0; index < 10; index++)
        {
            var bucket = BucketKey.FromIndex(index);

            store.SetBucket(bucket, Plan(bucket, PlanOrigin.Prebaked));
        }

        for (int index = 100; index < 102; index++)
        {
            var bucket = BucketKey.FromIndex(index);

            store.Pin(bucket, Plan(bucket, PlanOrigin.Pinned));
        }

        var outcomes = ImmutableArray.CreateBuilder<BucketOutcome>();

        // 통과 10 · 3단 실패 2(폴백) · 429 1.
        for (int i = 0; i < 10; i++)
        {
            outcomes.Add(new BucketOutcome(
                BucketKey.FromIndex(i), ValidationResult.Ok, Stats(1, 0.001), PlanOrigin.Prebaked, "ok", []));
        }

        for (int i = 10; i < 12; i++)
        {
            outcomes.Add(new BucketOutcome(
                BucketKey.FromIndex(i),
                ValidationResult.Fail(ValidationStage.Coherence, "V3.PRECONDITION_UNMET", 1, "재료가 없다"),
                Stats(2, 0.002),
                PlanOrigin.Fallback,
                "fallback",
                []));
        }

        outcomes.Add(new BucketOutcome(
            BucketKey.FromIndex(12),
            ValidationResult.Fail(ValidationStage.Schema, "V0.CALL_FAILED", -1, "429"),
            Stats(2, 0, "Status: 429 (Too Many Requests)"),
            PlanOrigin.Fallback,
            string.Empty,
            []));

        // 시도조차 못 한 버킷 — 예산 캡에 걸린 몫이다.
        outcomes.Add(default);

        var report = new BulkRunReport(
            outcomes.ToImmutable(),
            WallClockSeconds: 187.4,
            FirstRateLimitConcurrency: 12,
            PeakConcurrency: 18,
            RateLimitHits: 3,
            StartConcurrency: 8,
            StoppedByBudget: true,
            Attempted: 13);

        var dryRun = new DryRunReport([], Checked: 12, Skipped: 0, WallClockSeconds: 0.03, Parallelism: 16);

        Manifest manifest = ManifestWriter.Build(
            s_data, s_prefix, Engine, "T2", report, dryRun, store, "2026-07-26T09:00:00Z");

        // counts
        Assert.Equal(BucketKey.TotalKeys, manifest.Counts.Total);
        Assert.Equal(10, manifest.Counts.Generated);
        Assert.Equal(2, manifest.Counts.Pinned);
        Assert.Equal(3, manifest.Counts.Fallback);

        // validation — 시도조차 못 한 버킷은 어느 칸에도 안 들어간다.
        Assert.Equal(10, manifest.Validation.Pass);
        Assert.Equal(2, manifest.Validation.FailCoherence);
        Assert.Equal(1, manifest.Validation.FailCall);
        Assert.Equal(0, manifest.Validation.FailSchema);
        Assert.Equal(13, manifest.Validation.Total);

        // cost · wall_clock · cache
        Assert.Equal(0.014, manifest.CostUsd, 6);   // 10×0.001 + 2×0.002 + 0
        Assert.Equal(187.4, manifest.WallClockSeconds, 3);
        Assert.Equal(report.CacheHitRate, manifest.CacheHitRate, 6);

        // 예산 캡에 걸렸으면 부분 상태다 — --resume 이 이것을 본다.
        Assert.True(manifest.Partial);

        // 실측 근거 3필드
        Assert.Equal(8, manifest.GeneratedBy.Concurrency);
        Assert.Equal(18, manifest.GeneratedBy.PeakConcurrency);
        Assert.Equal(12, manifest.GeneratedBy.FirstRateLimitConcurrency);
        Assert.Equal("T2", manifest.GeneratedBy.Tier);
        Assert.Equal(s_data.ContentHash, manifest.MasterdataHash);
        Assert.Equal(s_prefix.Sha256, manifest.PrefixHash);
    }

    /// <summary>전수 드라이런에서 새로 걸린 것이 <c>fail_dryrun</c> 에 합산된다.</summary>
    [Fact]
    public void Manifest_AddsDryRunSweepFailures()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        var report = new BulkRunReport(
            [new BucketOutcome(BucketKey.FromIndex(0), ValidationResult.Ok, Stats(1, 0.001), PlanOrigin.Prebaked, "ok", [])],
            1.0, 0, 8, 0, 8);

        var dryRun = new DryRunReport(
            [
                new DryRunOutcome(
                    BucketKey.FromIndex(5),
                    ValidationResult.Fail(ValidationStage.DryRun, "V4.DEADLOCK", 0, "막혔다")),
            ],
            Checked: 1,
            Skipped: 0,
            WallClockSeconds: 0.01,
            Parallelism: 16);

        Manifest manifest = ManifestWriter.Build(
            s_data, s_prefix, Engine, "T2", report, dryRun, store, string.Empty);

        Assert.Equal(1, manifest.Validation.Pass);
        Assert.Equal(1, manifest.Validation.FailDryRun);
        Assert.False(manifest.Partial);
    }

    /// <summary>
    /// T3-16 완료 조건 — 프리베이크 실행마다 보존한다.
    /// <c>manifest.json</c> 은 최신 회차이고 요약은 이력에 한 줄씩 쌓인다.
    /// </summary>
    [Fact]
    public void Manifest_PreservesEveryRun()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        var dryRun = new DryRunReport([], 0, 0, 0.01, 16);

        var report = new BulkRunReport([], 10.0, 0, 8, 0, 8);

        Manifest first = ManifestWriter.Build(
            s_data, s_prefix, Engine, "T2", report, dryRun, store, "2026-07-26T09:00:00Z");
        Manifest second = first with { GeneratedAt = "2026-07-27T09:00:00Z", CostUsd = 1.5 };

        string path = ManifestWriter.Save(_directory, first, dryRun);

        Assert.Equal(Path.Combine(_directory, Manifest.FileName), path);

        ManifestWriter.Save(_directory, second, dryRun);

        // manifest.json 은 최신 회차다.
        Manifest? latest = Manifest.LoadFrom(_directory);

        Assert.NotNull(latest);
        Assert.Equal("2026-07-27T09:00:00Z", latest.GeneratedAt);

        // 두 회차가 다 이력에 남아 있다.
        string[] history = File.ReadAllLines(Path.Combine(_directory, ManifestWriter.HistoryFileName));

        Assert.Equal(2, history.Length);
        Assert.Contains("2026-07-26T09:00:00Z", history[0], StringComparison.Ordinal);
        Assert.Contains("2026-07-27T09:00:00Z", history[1], StringComparison.Ordinal);

        // 이력 한 줄이 유효한 JSON 이고 필요한 필드를 다 갖는다.
        foreach (string line in history)
        {
            using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(line);

            foreach (string key in new[]
            {
                "generated_at", "model", "tier", "prefix_hash", "masterdata_hash", "partial",
                "generated", "pinned", "fallback", "pass", "cost_usd", "wall_clock_s",
                "cache_hit_rate", "dryrun_checked", "dryrun_failed", "concurrency",
            })
            {
                Assert.True(parsed.RootElement.TryGetProperty(key, out _), key);
            }
        }
    }

    /// <summary>manifest 작성기도 시계를 부르지 않는다 (CLAUDE.md §2.3).</summary>
    [Fact]
    public void Manifest_WriterHasNoClock()
    {
        string source = string.Join(
            '\n',
            File.ReadLines(TestPaths.At("tools", "Npc.Prebake", "ManifestWriter.cs"))
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("DateTime.Now", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime.UtcNow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTimeOffset", source, StringComparison.Ordinal);
    }
}
