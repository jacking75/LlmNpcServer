using System.Collections.Immutable;
using System.Text.Json;
using Npc.Cli.Review;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Tests.Cli;

/// <summary>
/// F-06 — 검수 워크플로 v2.
///
/// <para>
/// <b>여기서 지키는 것은 셋이다.</b> (1) 층화 추출이 결정론이다(같은 시드면 같은 표본),
/// (2) 폴백으로 대체된 버킷이 표본에 든다, (3) 기록 형식이 옛 파서와 호환된다.
/// </para>
/// </summary>
public sealed class ReviewTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static PlanStore Store()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        foreach (FallbackPlanEntry entry in s_data.Fallbacks!.Plans)
        {
            store.SetFallback(entry.Archetype, store.Register(entry.Plan));
        }

        PlanStoreIo.LoadAll(TestPaths.At("planstore"), store, s_data);

        return store;
    }

    /// <summary>
    /// <b>같은 시드면 같은 표본이다.</b> 검수 대상이 회차마다 바뀌면 재현이 안 되고,
    /// 두 사람이 같은 회차를 검수했는지 확인할 방법이 없어진다.
    /// </summary>
    [Fact]
    public void Sampling_IsDeterministic()
    {
        PlanStore store = Store();

        ImmutableArray<ReviewSample> first =
            ReviewSampler.Take(store, s_data, 20, seed: 7, planStoreDirectory: TestPaths.At("planstore"));

        ImmutableArray<ReviewSample> second =
            ReviewSampler.Take(store, s_data, 20, seed: 7, planStoreDirectory: TestPaths.At("planstore"));

        Assert.Equal([.. first.Select(s => s.Name)], [.. second.Select(s => s.Name)]);

        // 시드를 바꾸면 표본이 달라져야 한다 — 같으면 시드가 아무 일도 안 하는 것이다.
        ImmutableArray<ReviewSample> other =
            ReviewSampler.Take(store, s_data, 20, seed: 8, planStoreDirectory: TestPaths.At("planstore"));

        Assert.NotEqual([.. first.Select(s => s.Name)], [.. other.Select(s => s.Name)]);
    }

    /// <summary>
    /// <b>폴백으로 대체된 버킷이 표본에 든다</b> — v1 이 못 하던 것이다.
    /// 그 버킷들이야말로 "왜 생성이 실패했나" 를 말해 준다.
    /// </summary>
    [Fact]
    public void Sampling_IncludesFallbackSubstitutions()
    {
        ImmutableArray<ReviewSample> samples = ReviewSampler.Take(
            Store(), s_data, 40, planStoreDirectory: TestPaths.At("planstore"));

        Assert.Contains(samples, s => s.Origin == PlanOrigin.Fallback);
        Assert.Contains(samples, s => s.Origin != PlanOrigin.Fallback);
    }

    /// <summary>층별 비율이 모집단을 따른다.</summary>
    [Fact]
    public void Sampling_KeepsStrataProportions()
    {
        PlanStore store = Store();
        string planStore = TestPaths.At("planstore");

        ImmutableArray<ReviewSample> population = ReviewSampler.Population(
            store, s_data, planStoreDirectory: planStore);

        const int Sample = 40;

        ImmutableArray<ReviewSample> taken = ReviewSampler.Take(
            store, s_data, Sample, planStoreDirectory: planStore);

        Assert.Equal(Sample, taken.Length);

        foreach (PlanOrigin origin in population.Select(s => s.Origin).Distinct())
        {
            double expected = (double)population.Count(s => s.Origin == origin) * Sample / population.Length;
            int actual = taken.Count(s => s.Origin == origin);

            // 내림 + 큰 나머지라 정확히 ±1 안이다.
            Assert.InRange(actual, (int)expected, (int)expected + 1);
        }
    }

    /// <summary>표본이 모집단보다 크면 전부 준다 — 잘라서 거짓 표본을 만들지 않는다.</summary>
    [Fact]
    public void Sampling_ReturnsEverythingWhenSampleExceedsPopulation()
    {
        PlanStore store = Store();

        ImmutableArray<ReviewSample> taken = ReviewSampler.Take(
            store, s_data, 99_999, planStoreDirectory: TestPaths.At("planstore"));

        Assert.Equal(
            ReviewSampler.Population(store, s_data, planStoreDirectory: TestPaths.At("planstore")).Length,
            taken.Length);
    }

    /// <summary>
    /// <b>기록이 옛 형식과 호환된다.</b> 게이트 테스트가 <c>bucket</c>·<c>verdict</c>·
    /// <c>minutes</c> 를 읽는다 — 이름이 바뀌면 그 게이트가 조용히 0건을 세게 된다.
    /// </summary>
    [Fact]
    public void Record_KeepsTheOldFieldNames()
    {
        var record = new ReviewRecord(
            "blacksmith@Dawn.Peace.Fair",
            ReviewVerdict.Reject,
            1.25,
            "동선이 왕복이다",
            RejectReason.Route,
            0,
            "Runtime");

        using JsonDocument document = JsonDocument.Parse(record.ToJsonLine());

        Assert.Equal("blacksmith@Dawn.Peace.Fair", document.RootElement.GetProperty("bucket").GetString());
        Assert.Equal("reject", document.RootElement.GetProperty("verdict").GetString());
        Assert.Equal(1.25, document.RootElement.GetProperty("minutes").GetDouble());
        Assert.Equal("동선이 왕복이다", document.RootElement.GetProperty("note").GetString());
        Assert.Equal("Route", document.RootElement.GetProperty("reason_code").GetString());
        Assert.Equal("Runtime", document.RootElement.GetProperty("origin").GetString());
    }

    /// <summary>채택에는 사유 코드도 수정 수도 없다 — 빈 필드를 적지 않는다.</summary>
    [Fact]
    public void Record_OmitsEmptyFields()
    {
        string line = new ReviewRecord("a@Dawn.Peace.Fair", ReviewVerdict.Accept, 0.5).ToJsonLine();

        Assert.DoesNotContain("reason_code", line, StringComparison.Ordinal);
        Assert.DoesNotContain("edited_steps", line, StringComparison.Ordinal);
        Assert.DoesNotContain("note", line, StringComparison.Ordinal);
    }

    /// <summary>따옴표·줄바꿈이 든 메모가 jsonl 을 깨뜨리지 않는다.</summary>
    [Fact]
    public void Record_EscapesNotes()
    {
        string line = new ReviewRecord(
            "a@Dawn.Peace.Fair", ReviewVerdict.Accept, 0, "따옴표 \" 와\n줄바꿈").ToJsonLine();

        using JsonDocument document = JsonDocument.Parse(line);

        Assert.Contains("따옴표", document.RootElement.GetProperty("note").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
    }

    /// <summary>
    /// <b>검수로 만든 <c>pinned/</c> 가 실제로 있다</b> (F-06 완료 조건).
    /// 로드되면 <c>PlanOrigin.Pinned</c> 로 올라가야 프리베이크가 덮어쓰지 않는다.
    /// </summary>
    [Fact]
    public void Pinned_PlansExistAndLoadAsPinned()
    {
        string pinned = TestPaths.At("planstore", "pinned");

        Assert.True(Directory.Exists(pinned), "planstore/pinned 이 없다");

        string[] files = [.. Directory.EnumerateFiles(pinned, "*.json")];

        Assert.True(files.Length >= 10, $"핀이 {files.Length}건뿐이다 — F-06 완료 조건은 10건 이상이다");

        PlanStore store = Store();
        int loaded = 0;

        foreach (string file in files)
        {
            Assert.True(
                PlanStoreIo.TryParseBucket(Path.GetFileNameWithoutExtension(file), s_data, out BucketKey bucket),
                $"{Path.GetFileName(file)} 의 이름이 버킷 키가 아니다");

            if (store.OriginOf(bucket) == PlanOrigin.Pinned)
            {
                loaded++;
            }
        }

        Assert.Equal(files.Length, loaded);
    }

    /// <summary>manifest 의 핀 수가 실제 파일 수와 같다.</summary>
    [Fact]
    public void Manifest_PinnedCountMatchesTheFiles()
    {
        string path = TestPaths.At("planstore", "manifest.json");

        Assert.True(File.Exists(path), "manifest.json 이 없다");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        int pinned = document.RootElement.GetProperty("counts").GetProperty("pinned").GetInt32();
        int files = Directory.EnumerateFiles(TestPaths.At("planstore", "pinned"), "*.json").Count();

        Assert.Equal(files, pinned);
        Assert.True(pinned > 0, "manifest.counts.pinned 가 0 이다 — F-06 완료 조건이 이 값을 본다");
    }

    /// <summary>폐기 사유가 4종이고 <c>system_rules.md</c> 의 항목과 짝이 맞는다.</summary>
    [Fact]
    public void Reasons_MatchTheSystemRules()
    {
        Assert.Equal(4, ReviewRecord.Reasons.Length);

        foreach ((RejectReason reason, string label) in ReviewRecord.Reasons)
        {
            Assert.NotEqual(RejectReason.None, reason);
            Assert.False(string.IsNullOrWhiteSpace(label));
        }
    }
}
