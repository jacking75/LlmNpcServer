using System.Collections.Immutable;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;
using Npc.Prebake;

namespace Npc.Tests.Prebake;

/// <summary>전수 드라이런. docs/13 §8 · T3-15.</summary>
/// <param name="output">
/// 건당 소요를 남긴다. <c>dotnet test --logger "console;verbosity=detailed"</c> 로 볼 수 있고
/// 그 값이 <c>docs/measurements/W8_prebake.md §4</c> 의 근거다.
/// </param>
public sealed class DryRunStageTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>
    /// 2,880 버킷 전부를 아키타입 폴백 플랜으로 채운 스토어.
    ///
    /// 폴백은 V7 이 기동 시점에 4단 통과를 보장하므로 <b>전량 통과가 기대값</b>이다 —
    /// 여기서 실패가 나오면 4단이나 폴백 데이터가 깨진 것이다.
    /// </summary>
    private static PlanStore FullStore()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        Assert.NotNull(s_data.Fallbacks);

        for (int index = 0; index < BucketKey.TotalKeys; index++)
        {
            var bucket = BucketKey.FromIndex(index);

            if (s_data.Fallbacks!.For(bucket.A) is { } fallback)
            {
                store.SetBucket(bucket, fallback with { Bucket = bucket, Origin = PlanOrigin.Prebaked });
            }
        }

        return store;
    }

    /// <summary>
    /// T3-15 완료 조건 — 2,880건 드라이런이 60초 이내이고, 2회 실행이 같은 판정을 낸다.
    /// </summary>
    [Fact]
    public void DryRun_ChecksEveryBucketWithinBudget()
    {
        PlanStore store = FullStore();

        Assert.Equal(BucketKey.TotalKeys, store.FilledBuckets);

        DryRunReport first = DryRunStage.Run(store, s_data);

        output.WriteLine(
            $"2,880건 드라이런: {first.WallClockSeconds:F2}s · {first.MsPerPlan:F2}ms/건 · 병렬 {first.Parallelism}");

        DryRunReport serial = DryRunStage.Run(store, s_data, parallelism: 1);

        output.WriteLine(
            $"직렬(병렬 1)   : {serial.WallClockSeconds:F2}s · {serial.MsPerPlan:F2}ms/건");

        Assert.Equal(BucketKey.TotalKeys, first.Checked);
        Assert.Equal(0, first.Skipped);
        Assert.True(
            first.WallClockSeconds <= 60,
            $"2,880건 드라이런이 {first.WallClockSeconds:F1}초 걸렸다 (건당 {first.MsPerPlan:F1}ms · 병렬 {first.Parallelism}). "
            + "병렬도를 올리기 전에 T2-13 의 건당 소요부터 확인한다.");

        // 폴백은 V7 이 4단 통과를 보장한다.
        Assert.Equal(0, first.Failed);
        Assert.Equal(1.0, first.PassRate);

        // 결정론 — 2회 실행 → 동일 판정.
        DryRunReport second = DryRunStage.Run(store, s_data);

        Assert.Equal(first.Checked, second.Checked);
        Assert.Equal(first.Failed, second.Failed);
        Assert.Equal(
            first.Failures.Select(f => (f.Bucket, f.Validation.Code)).ToArray(),
            second.Failures.Select(f => (f.Bucket, f.Validation.Code)).ToArray());
    }

    /// <summary>병렬도를 바꿔도 판정이 같다. 병렬 완료 순서에 기대는 코드가 없어야 한다.</summary>
    [Fact]
    public void DryRun_IsDeterministicAcrossParallelism()
    {
        PlanStore store = FullStore();

        DryRunReport serial = DryRunStage.Run(store, s_data, parallelism: 1);
        DryRunReport parallel = DryRunStage.Run(store, s_data, parallelism: 8);

        Assert.Equal(1, serial.Parallelism);
        Assert.Equal(8, parallel.Parallelism);
        Assert.Equal(serial.Checked, parallel.Checked);
        Assert.Equal(
            serial.Failures.Select(f => f.Bucket).ToArray(),
            parallel.Failures.Select(f => f.Bucket).ToArray());
    }

    /// <summary>
    /// 데드락 플랜을 심으면 잡아낸다. 이것이 없으면 데드락 플랜이 런타임에 배포된다.
    /// </summary>
    [Fact]
    public void DryRun_CatchesPlansThatCannotRun()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));

        var bucket = new BucketKey(smith.Code, TimeOfDay.Dawn, RegionState.Peace, Climate.Fair);

        // 재료도 도구도 없이 곧장 제작한다 — 4단에서 교착이거나 전제 미충족이다.
        const string Deadlock = """
            {"schema":1,"goal":"forge_now","loop":true,"steps":[
              {"action":"Craft","args":{"recipe":"iron_sword","count":1}},
              {"action":"MoveTo","args":{"poi":"$home"}},
              {"action":"Sleep","args":{"until_time":"Morning"}}]}
            """;

        PlanDocument document = System.Text.Json.JsonSerializer.Deserialize(
            Deadlock, PlanJsonContext.Default.PlanDocument)!;

        store.SetBucket(
            bucket,
            Npc.Core.Plan.PlanCompiler.Compile(
                document, bucket, new Npc.Contracts.PlanId(0), s_data, PlanOrigin.Prebaked));

        DryRunReport report = DryRunStage.Run(store, s_data);

        Assert.Equal(1, report.Checked);
        Assert.Equal(1, report.Failed);

        DryRunOutcome failure = Assert.Single(report.Failures);

        Assert.Equal(bucket, failure.Bucket);
        Assert.StartsWith("V4.", failure.Validation.Code, StringComparison.Ordinal);
    }

    /// <summary>표본 비율이 결정론이다. 회차마다 다른 버킷을 보면 "2회 → 동일 판정"이 깨진다.</summary>
    [Fact]
    public void DryRun_SampleSelectionIsDeterministic()
    {
        ImmutableArray<int> half =
            [.. Enumerable.Range(0, BucketKey.TotalKeys).Where(i => DryRunStage.InSample(i, 0.5))];
        ImmutableArray<int> again =
            [.. Enumerable.Range(0, BucketKey.TotalKeys).Where(i => DryRunStage.InSample(i, 0.5))];

        Assert.Equal(half.ToArray(), again.ToArray());

        // 대략 절반이다. 해시라 정확히 절반은 아니다.
        Assert.InRange(half.Length, BucketKey.TotalKeys * 45 / 100, BucketKey.TotalKeys * 55 / 100);

        // 1.0 은 전수, 0 은 전무.
        Assert.All(Enumerable.Range(0, 100), i => Assert.True(DryRunStage.InSample(i, 1.0)));
        Assert.All(Enumerable.Range(0, 100), i => Assert.False(DryRunStage.InSample(i, 0)));

        // 표본이 작으면 Skipped 가 그만큼 잡힌다.
        DryRunReport report = DryRunStage.Run(FullStore(), s_data, sample: 0.1);

        Assert.Equal(BucketKey.TotalKeys, report.Checked + report.Skipped);
        Assert.InRange(report.Checked, BucketKey.TotalKeys / 20, BucketKey.TotalKeys / 5);
    }

    /// <summary>빈 스토어는 볼 것이 없다.</summary>
    [Fact]
    public void DryRun_EmptyStoreChecksNothing()
    {
        DryRunReport report = DryRunStage.Run(PlanStore.CreateIdleOnly(s_data), s_data);

        Assert.Equal(0, report.Checked);
        Assert.Equal(0, report.Failed);
        Assert.Equal(1.0, report.PassRate);
    }
}
