using Npc.Planning;
using Npc.Tests.Runtime;

namespace Npc.Tests.Load;

/// <summary>docs/14 §2 튜닝 절차 · T4-17. 4세트 A/B 와 선정.</summary>
[Collection(AllocationCollection.Name)]
public sealed class WeightAbTests
{
    /// <summary>4세트가 docs/14 §2 표 그대로다. 표가 바뀌면 지난 회차와 비교할 수 없다.</summary>
    [Fact]
    public void Weights_AbSetsMatchSpecTable()
    {
        Assert.Equal(
            ["A-proximity", "B-baseline", "C-deviation", "D-uniform"],
            Weights.AbSets.Select(s => s.Name));

        Assert.Equal(new Weights(5.0f, 0.2f, 1.5f, 4.0f, 36_000), Weights.ProximityFirst);
        Assert.Equal(new Weights(3.0f, 0.5f, 2.0f, 4.0f, 36_000), Weights.Baseline);
        Assert.Equal(new Weights(2.0f, 0.3f, 4.0f, 4.0f, 36_000), Weights.DeviationFirst);
        Assert.Equal(new Weights(1.0f, 1.0f, 1.0f, 1.0f, 36_000), Weights.Uniform);
    }

    /// <summary>이름으로 세트를 고른다. 오타는 조용히 기본값이 되면 안 된다.</summary>
    [Theory]
    [InlineData("A-proximity")]
    [InlineData("a")]
    [InlineData("A")]
    [InlineData("d-UNIFORM")]
    [InlineData("")]
    [InlineData(null)]
    public void Weights_ParseAcceptsSetNames(string? name)
    {
        Assert.True(Weights.TryParse(name, out Weights weights));

        if (!string.IsNullOrEmpty(name))
        {
            Assert.Equal(
                name.StartsWith('d') || name.StartsWith('D') ? Weights.Uniform : Weights.ProximityFirst,
                weights);
        }
        else
        {
            Assert.Equal(Weights.Default, weights);
        }
    }

    [Fact]
    public void Weights_ParseRejectsUnknownName()
    {
        Assert.False(Weights.TryParse("E-nope", out _));
        Assert.False(Weights.TryParse("zzz", out _));

        Assert.Equal("B-baseline", Weights.Baseline.Name);
        Assert.Equal("custom", new Weights(9, 9, 9, 9, 1).Name);
    }

    /// <summary>선정 규칙 — 같은 예산에서 근처에 더 집중하고 더 신선한 쪽이 이긴다.</summary>
    [Fact]
    public void Weights_ChoosesHigherNearShareAtSameCost()
    {
        WeightAbResult[] results =
        [
            Result("A-proximity", requests: 100, nearShare: 0.90, nearFresh: 300),
            Result("B-baseline", requests: 100, nearShare: 0.60, nearFresh: 500),
            Result("C-deviation", requests: 100, nearShare: 0.40, nearFresh: 800),
            Result("D-uniform", requests: 100, nearShare: 0.25, nearFresh: 1_200),
        ];

        Assert.Equal("A-proximity", WeightAbHarness.Choose(results));

        // 요청 수가 2배면 같은 집중도라도 점수가 절반이다 — "요청 수를 최소화" 가 규칙에 들어 있다.
        WeightAbResult cheap = Result("B-baseline", requests: 100, nearShare: 0.6, nearFresh: 500);
        WeightAbResult expensive = Result("A-proximity", requests: 200, nearShare: 0.6, nearFresh: 500);

        Assert.True(
            WeightAbHarness.Score(in cheap, 100) > WeightAbHarness.Score(in expensive, 100),
            "요청 수가 2배인데 점수가 낮지 않다.");
    }

    /// <summary>리포트에 4세트가 다 들어가고 선정된 세트가 강조된다.</summary>
    [Fact]
    public void Weights_ReportListsAllSets()
    {
        WeightAbResult[] results =
        [
            Result("A-proximity", 100, 0.9, 300),
            Result("B-baseline", 100, 0.6, 500),
            Result("C-deviation", 100, 0.4, 800),
            Result("D-uniform", 100, 0.25, 1_200),
        ];

        string report = WeightAbHarness.Report(results, "A-proximity");

        foreach ((string name, _) in Weights.AbSets)
        {
            Assert.Contains(name, report, StringComparison.Ordinal);
        }

        Assert.Contains("**A-proximity**", report, StringComparison.Ordinal);
        Assert.Contains("선정", report, StringComparison.Ordinal);
        Assert.Contains("W1", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// T4-17 완료 조건 — 4세트 × 시나리오 A 를 돌려 결과표를 내고 선정값을
    /// <c>Weights.Default</c> 와 대조한다.
    /// </summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Weights_RunsAbMatrix()
    {
        WeightAbResult[] results = await WeightAbHarness.RunAllAsync(CancellationToken.None);

        Assert.Equal(4, results.Length);

        string chosen = WeightAbHarness.Choose(results);
        WeightAbHarness.Write(WeightAbHarness.OutPath(), results, chosen);

        // 회차가 실제로 일했는가 — 요청이 0 이면 표가 무의미하다.
        Assert.All(results, r => Assert.True(r.Requests > 0, $"{r.Name} 요청 0건"));
        Assert.All(results, r => Assert.True(r.Enqueued > 0, $"{r.Name} 유입 0건"));

        // 예산이 같으므로 요청 수는 세트마다 거의 같아야 한다 — 다르면 예산이 아니라
        // 큐가 마른 것이고, 그때는 신선도 비교가 성립하지 않는다.
        long min = results.Min(r => r.Requests);
        long max = results.Max(r => r.Requests);

        Assert.True(max <= min * 1.5, $"세트별 요청 수가 {min}~{max} 로 벌어졌다.");

        // 근접 우선(A) 세트가 근처 집중도에서 균등(D) 세트보다 나아야 한다 —
        // 그게 W1 을 크게 잡는 이유다 (docs/14 §2 · 상위 계획 §2.2).
        WeightAbResult proximity = results.Single(r => r.Name == "A-proximity");
        WeightAbResult uniform = results.Single(r => r.Name == "D-uniform");

        Assert.True(
            proximity.NearShare >= uniform.NearShare,
            $"A 근처 집중도 {proximity.NearShare:P1} < D {uniform.NearShare:P1}");

        // 선정값이 Weights.Default 와 같아야 한다. 다르면 코드를 고치라는 신호다.
        Assert.True(
            Weights.TryParse(chosen, out Weights chosenWeights),
            $"선정된 세트 이름 '{chosen}' 을 파싱하지 못했다.");

        Assert.Equal(chosenWeights, Weights.Default);
    }

    private static WeightAbResult Result(string name, long requests, double nearShare, double nearFresh)
    {
        Weights.TryParse(name, out Weights weights);

        return new WeightAbResult(
            name, weights,
            Enqueued: requests * 10,
            Requests: requests,
            Dropped: 0,
            CacheHitRate: 0.5,
            NearFreshnessTicks: nearFresh,
            FarFreshnessTicks: nearFresh * 3,
            NearShare: nearShare,
            TickP99Ms: 1.0);
    }
}
