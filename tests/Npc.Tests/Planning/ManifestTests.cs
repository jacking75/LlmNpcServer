using Npc.MasterData;
using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>플랜 스토어 manifest. docs/03 §7 · T3-05.</summary>
public sealed class ManifestTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static Manifest Sample(string? generatedAt = null) => Manifest.For(
        s_data,
        prefixHash: "56a0c601f48392e0",
        new ManifestGeneratedBy("T2", "openrouter-gemini-2.5-flash-lite", 0.4, 8, 18, 0),
        generatedAt);

    /// <summary>
    /// T3-05 완료 조건 — 소스에 <c>DateTime.Now</c>/<c>UtcNow</c> 가 하나도 없다.
    ///
    /// manifest 에 벽시계를 섞으면 같은 입력이 같은 파일을 내지 못해 리플레이가 깨진다
    /// (CLAUDE.md §2.3 · docs/13 §8 의 "흔한 실수" 표).
    /// </summary>
    [Fact]
    public void Manifest_HasNoDateTimeNow()
    {
        // 주석은 뺀다 — "DateTime.Now 를 부르지 않는다" 라고 적은 문장 자체가 걸리면 안 된다.
        string source = string.Join(
            '\n',
            File.ReadLines(TestPaths.At("src", "Npc.Planning", "Manifest.cs"))
                .Select(line => line.TrimStart())
                .Where(line => !line.StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("DateTime.Now", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime.UtcNow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTimeOffset.Now", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTimeOffset.UtcNow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopwatch", source, StringComparison.Ordinal);

        // 시각은 외부에서 주입하는 문자열이다.
        Assert.Equal("2026-07-26T09:00:00Z", Sample("2026-07-26T09:00:00Z").GeneratedAt);
        Assert.Equal(string.Empty, Sample().GeneratedAt);
    }

    /// <summary>시각을 안 주면 같은 입력이 같은 바이트를 낸다. 결정론적 산출물이다.</summary>
    [Fact]
    public void Manifest_IsDeterministicWithoutTimestamp()
    {
        Assert.Equal(Sample().ToJson(), Sample().ToJson());
        Assert.NotEqual(Sample().ToJson(), Sample("2026-07-26T09:00:00Z").ToJson());
    }

    /// <summary>docs/03 §7 의 필드가 전부 있고 마스터데이터 해시는 ContentHash 를 그대로 쓴다.</summary>
    [Fact]
    public void Manifest_CarriesEverySpecField()
    {
        Manifest manifest = Sample("2026-07-26T09:00:00Z") with
        {
            Counts = new ManifestCounts(2_880, 2_841, 12, 27),
            Validation = new ManifestValidation(2_841, 3, 11, 22, 3),
            CostUsd = 0.23,
            WallClockSeconds = 187,
            CacheHitRate = 0.96,
        };

        Assert.Equal(s_data.ContentHash, manifest.MasterdataHash);
        Assert.Equal(Manifest.CurrentSchema, manifest.Schema);

        string json = manifest.ToJson();

        foreach (string key in new[]
        {
            "\"schema\"", "\"masterdata_hash\"", "\"prefix_hash\"", "\"generated_at\"", "\"partial\"",
            "\"generated_by\"", "\"tier\"", "\"model\"", "\"temperature\"",
            "\"concurrency\"", "\"peak_concurrency\"", "\"first_rate_limit_concurrency\"",
            "\"counts\"", "\"total\"", "\"generated\"", "\"pinned\"", "\"fallback\"", "\"reused\"",
            "\"validation\"", "\"pass\"", "\"fail_schema\"", "\"fail_vocab\"",
            "\"fail_coherence\"", "\"fail_dryrun\"", "\"fail_call\"",
            "\"cost_usd\"", "\"wall_clock_s\"", "\"cache_hit_rate\"", "\"file_hashes\"",
        })
        {
            Assert.Contains(key, json, StringComparison.Ordinal);
        }

        // 파생값 — 게이트가 읽는 것들.
        Assert.Equal((2_841 + 12) / 2_880.0, manifest.Counts.GeneratedRate, 6);
        Assert.Equal(2_841 / 2_880.0, manifest.Validation.PassRate, 6);
    }

    /// <summary>파일 왕복. 부분 무효화 판정이 <c>file_hashes</c> 를 읽으므로 그것까지 살아야 한다.</summary>
    [Fact]
    public void Manifest_RoundTripsThroughFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "npc-manifest-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Manifest original = Sample("2026-07-26T09:00:00Z") with
            {
                Counts = new ManifestCounts(2_880, 2_700, 5, 175, 30),
                Validation = new ManifestValidation(2_700, 1, 2, 3, 4, 5),
                CostUsd = 1.2345,
                WallClockSeconds = 288.5,
                CacheHitRate = 0.9612,
                Partial = true,
            };

            string path = Path.Combine(directory, Manifest.FileName);
            original.Save(path);

            Manifest? loaded = Manifest.LoadFrom(directory);

            Assert.NotNull(loaded);
            Assert.Equal(original.MasterdataHash, loaded.MasterdataHash);
            Assert.Equal(original.PrefixHash, loaded.PrefixHash);
            Assert.Equal(original.GeneratedAt, loaded.GeneratedAt);
            Assert.True(loaded.Partial);
            Assert.Equal(original.GeneratedBy, loaded.GeneratedBy);
            Assert.Equal(original.Counts, loaded.Counts);
            Assert.Equal(original.Validation, loaded.Validation);
            Assert.Equal(1.2345, loaded.CostUsd, 6);
            Assert.Equal(288.5, loaded.WallClockSeconds, 6);
            Assert.Equal(0.9612, loaded.CacheHitRate, 6);

            // 마스터데이터의 파일 해시 전부가 살아 있어야 부분 무효화를 판정할 수 있다.
            Assert.Equal(s_data.FileHashes.Length, loaded.FileHashes.Length);
            Assert.Equal(s_data.HashOf("actions.json"), loaded.HashOf("actions.json"));
            Assert.Null(loaded.HashOf("없는파일.json"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>manifest 가 없으면 null 이다 — 첫 프리베이크에는 없다.</summary>
    [Fact]
    public void Manifest_LoadReturnsNullWhenMissing() =>
        Assert.Null(Manifest.LoadFrom(Path.Combine(Path.GetTempPath(), "npc-manifest-없음")));
}
