using System.Collections.Immutable;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Llm;
using Npc.Prebake;
using Npc.Tests.Fakes;
using Npc.Tests.Llm;

namespace Npc.Tests.Prebake;

/// <summary>
/// T2-19 — 전량 생성 러너. docs/12 §8.
/// <b>LLM 을 부르지 않는다.</b> 실제 회차는 <c>dotnet run --project tools/Npc.Prebake</c> 로 돌린다.
/// </summary>
public sealed class BulkRunnerTests
{
    private const string ValidPlan = """
        {"schema":1,"goal":"forge_batch","loop":true,"steps":[
          {"action":"MoveTo","args":{"poi":"$workplace"},"timeout_s":600},
          {"action":"Work","args":{"recipe":"$recipe","count":1},"timeout_s":1800},
          {"action":"MoveTo","args":{"poi":"$home"},"timeout_s":600},
          {"action":"Sleep","args":{"until_time":"Morning"}}]}
        """;

    private const string AlwaysFails = """
        {"schema":1,"goal":"forge_now","loop":true,"steps":[
          {"action":"Craft","args":{"recipe":"iron_sword","count":1}},
          {"action":"MoveTo","args":{"poi":"$home"}},
          {"action":"Sleep","args":{"until_time":"Morning"}}]}
        """;

    private static BulkRunner Runner(BulkRunOptions? options = null) =>
        new(
            LlmPlanCompilerTests.Data,
            LlmPlanCompilerTests.Prefix,
            LlmPlanCompilerTests.Options.Engine("gemini-3.1-flash-lite"),
            options);

    /// <summary>대장장이 버킷만 골라 쓴다 — 한 플랜이 전 아키타입에서 통과할 수는 없다.</summary>
    private static ImmutableArray<BucketKey> BlacksmithBuckets(int count)
    {
        Assert.True(LlmPlanCompilerTests.Data.Archetypes.TryGet("blacksmith", out Npc.MasterData.ArchetypeDef def));

        var builder = ImmutableArray.CreateBuilder<BucketKey>(count);

        for (int i = 0; i < count; i++)
        {
            builder.Add(new BucketKey(
                def.Code,
                (TimeOfDay)(i % BucketKey.TimeOfDayCount),
                (RegionState)(i / BucketKey.TimeOfDayCount % BucketKey.RegionStateCount),
                (Climate)(i % BucketKey.ClimateCount)));
        }

        return builder.ToImmutable();
    }

    [Fact]
    public void AllBuckets_Covers2880()
    {
        ImmutableArray<BucketKey> all = BulkRunner.AllBuckets();

        Assert.Equal(2_880, all.Length);
        Assert.Equal(2_880, all.Distinct().Count());

        // 순서는 버킷 인덱스 그대로 — 회차 간 diff 가 의미를 가져야 한다.
        Assert.Equal(BucketKey.FromIndex(0), all[0]);
        Assert.Equal(BucketKey.FromIndex(2_879), all[^1]);
    }

    [Fact]
    public async Task Run_KeepsBucketOrderAndCountsPasses()
    {
        ImmutableArray<BucketKey> buckets = BlacksmithBuckets(12);

        BulkRunReport report = await Runner().RunAsync(
            buckets,
            () => new FakeChatClient(ValidPlan.Replace("$recipe", "iron_sword", StringComparison.Ordinal)));

        Assert.Equal(buckets.Length, report.Total);
        Assert.Equal([.. buckets], [.. report.Outcomes.Select(o => o.Bucket)]);
        Assert.Equal(buckets.Length, report.Passed);
        Assert.Equal(1.0, report.PassRate);
        Assert.Equal(0, report.FellBack);

        // 통과한 플랜은 스토어에 들어가 뒤 버킷이 빌려 갈 수 있다.
        Assert.Equal(buckets.Length, report.Outcomes.Count(o => o.Origin == PlanOrigin.Runtime));
        Assert.All(report.Outcomes, o => Assert.NotEmpty(o.Actions));
    }

    [Fact]
    public async Task Run_FallsBackAndKeepsFailureCodes()
    {
        ImmutableArray<BucketKey> buckets = BlacksmithBuckets(6);

        BulkRunReport report = await Runner().RunAsync(buckets, () => new FakeChatClient(AlwaysFails));

        Assert.Equal(0, report.Passed);
        Assert.Equal(buckets.Length, report.FellBack);
        Assert.Equal(1.0, report.FallbackRate);
        Assert.All(report.Outcomes, o => Assert.Equal("V3.PRECONDITION_UNMET", o.Validation.Code));
    }

    [Fact]
    public async Task Run_HalvesConcurrencyOnRateLimitAndRecordsFirstHit()
    {
        // 처음 8건은 429, 그 뒤로는 정상. AIMD 가 동시성을 접고 다시 던져야 한다.
        int calls = 0;

        BulkRunReport report = await Runner(new BulkRunOptions(Concurrency: 8, BackoffMs: 1)).RunAsync(
            BlacksmithBuckets(16),
            () => new FakeChatClient(_ =>
                Interlocked.Increment(ref calls) <= 8
                    ? throw new HttpRequestException("Service request failed. Status: 429 (Too Many Requests)")
                    : ValidPlan.Replace("$recipe", "iron_sword", StringComparison.Ordinal)));

        // 429 가 처음 난 시점의 동시성이 남는다 — T3-08 의 기본값이 여기서 나온다.
        Assert.Equal(8, report.FirstRateLimitConcurrency);
        Assert.True(report.RateLimitHits > 0);

        // 물러났다가 다시 던져 전부 끝낸다.
        Assert.Equal(16, report.Total);
        Assert.Equal(16, report.Passed);
    }

    /// <summary>
    /// T3-13 완료 조건 — 429 를 계속 주입해도 전량 완주한다.
    ///
    /// 세 번에 한 번씩 429 를 던진다. 백오프가 물러나고 AIMD 가 동시성을 접으면서도
    /// 버킷 하나도 빠뜨리지 않아야 한다 — 빠지면 그 버킷은 영구 미생성으로 남는다.
    /// </summary>
    [Fact]
    public async Task Run_CompletesEveryBucketUnderSustainedRateLimits()
    {
        int calls = 0;

        BulkRunReport report = await Runner(
            new BulkRunOptions(Concurrency: 8, BackoffMs: 0, MaxRateLimitRetries: 8)).RunAsync(
            BlacksmithBuckets(24),
            () => new FakeChatClient(_ =>
                Interlocked.Increment(ref calls) % 3 == 0
                    ? throw new HttpRequestException("Service request failed. Status: 429 (Too Many Requests)")
                    : ValidPlan.Replace("$recipe", "iron_sword", StringComparison.Ordinal)));

        Assert.True(report.RateLimitHits > 0, "429 가 한 번도 주입되지 않았다.");
        Assert.Equal(24, report.Total);
        Assert.Equal(24, report.Passed);

        // 결과 배열에 구멍이 없다 — 버킷 순서도 입력 순서 그대로다.
        ImmutableArray<BucketKey> expected = BlacksmithBuckets(24);

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], report.Outcomes[i].Bucket);
        }
    }

    [Fact]
    public async Task Run_WritesRejectedArtifacts()
    {
        string directory = Path.Combine(Path.GetTempPath(), "npc_bulk_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            await Runner(new BulkRunOptions(PlanStoreDirectory: directory)).RunAsync(
                BlacksmithBuckets(3), () => new FakeChatClient(AlwaysFails));

            string[] files = Directory.GetFiles(Path.Combine(directory, "rejected"), "*.json");

            // 버킷 3개 × 시도 2회.
            Assert.Equal(6, files.Length);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Run_RecordsStatsForEveryBucket()
    {
        BulkRunner runner = Runner();

        BulkRunReport report = await runner.RunAsync(
            BlacksmithBuckets(8),
            () => new FakeChatClient(ValidPlan.Replace("$recipe", "iron_sword", StringComparison.Ordinal)));

        Assert.Equal(8, runner.Stats.Calls);
        Assert.Equal(8, runner.Stats.Passed);
        Assert.Equal(1, runner.Stats.UniquePrefixHashes);
        Assert.True(report.CacheHitRate > 0.9);
        Assert.True(report.AverageLatencyMs >= 0);
    }
}
