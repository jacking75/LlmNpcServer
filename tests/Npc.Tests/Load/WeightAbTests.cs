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

        // ⚠ "근접 우선(A)이 균등(D)보다 낫다" 를 단언하지 않는다.
        //    그것은 docs/14 §2 의 예측이지 요구사항이 아니고, 실측이 회차마다 순위를 바꾼다
        //    (2026-07-27 OnDuty 수정 전후로 A 47.0% > D 45.8% 가 A 45.0% < D 47.6% 로 뒤집혔다).
        //    예측을 단언으로 박으면 그 순간부터 이 테스트는 측정이 아니라 소망을 검사한다.
        //    여기서 강제하는 것은 "네 세트가 실제로 서로 다르게 동작하는가" 뿐이다.
        Assert.True(
            results.Select(r => Math.Round(r.NearShare, 3)).Distinct().Count() > 1,
            "네 세트의 근처 집중도가 전부 같다 — 가중치가 아무것도 바꾸지 못하고 있다.");

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
