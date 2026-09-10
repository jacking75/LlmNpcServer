using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;
using Npc.Prebake;

namespace Npc.Tests.Prebake;

/// <summary>생성 대상 버킷 산출. docs/13 §4 · T3-09.</summary>
public sealed class TargetSelectorTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static PrebakeOptions Options(params string[] args)
    {
        Assert.True(PrebakeOptions.TryParse(args, out PrebakeOptions options, out string? error), error);

        return options;
    }

    private static CompiledPlan Plan(BucketKey bucket, PlanOrigin origin)
    {
        const string Json = """
            {"schema":1,"goal":"target_sample","loop":true,"steps":[{"action":"MoveTo","args":{"poi":"$home"}},{"action":"Rest","args":{"duration_s":600}},{"action":"Wait","args":{"duration_s":60}}]}
            """;

        PlanDocument document = System.Text.Json.JsonSerializer.Deserialize(
            Json, PlanJsonContext.Default.PlanDocument)!;

        return Npc.Core.Plan.PlanCompiler.Compile(
            document, bucket, new PlanId(0), s_data, origin, version: 1, sourceJson: Json);
    }

    /// <summary>버킷 앞 n 개를 그 출처로 채운 스토어.</summary>
    private static PlanStore StoreWith(int filled, PlanOrigin origin = PlanOrigin.Prebaked)
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        for (int index = 0; index < filled; index++)
        {
            var bucket = BucketKey.FromIndex(index);

            if (origin == PlanOrigin.Pinned)
            {
                store.Pin(bucket, Plan(bucket, origin));
            }
            else
            {
                store.SetBucket(bucket, Plan(bucket, origin));
            }
        }

        return store;
    }

    /// <summary>모드 1 — Full. 무효화가 Full 이면 2,880 전량이다.</summary>
    [Fact]
    public void Target_FullSelectsEveryBucket()
    {
        TargetSelection selection = TargetSelector.Select(
            Options(), s_data, existing: null, InvalidationScope.Full);

        Assert.Equal(TargetMode.Full, selection.Mode);
        Assert.Equal(TestPaths.TotalKeys, selection.Count);
        Assert.Equal(TestPaths.TotalKeys, selection.Buckets.Distinct().Count());

        // 이미 채워진 스토어가 있어도 Full 은 전량이다 — 프롬프트가 바뀌면 다 다시 만들어야 한다.
        Assert.Equal(
            TestPaths.TotalKeys,
            TargetSelector.Select(Options(), s_data, StoreWith(2_000), InvalidationScope.Full).Count);
    }

    /// <summary>모드 2 — Partial. 미생성 + 폴백·재사용으로 메운 버킷만이다.</summary>
    [Fact]
    public void Target_PartialSelectsOnlyChanged()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        // 1,000 개는 정상 생성, 500 개는 폴백으로 메웠다, 나머지 1,380 개는 미생성.
        for (int index = 0; index < 1_000; index++)
        {
            store.SetBucket(BucketKey.FromIndex(index), Plan(BucketKey.FromIndex(index), PlanOrigin.Prebaked));
        }

        for (int index = 1_000; index < 1_500; index++)
        {
            store.SetBucket(BucketKey.FromIndex(index), Plan(BucketKey.FromIndex(index), PlanOrigin.Fallback));
        }

        TargetSelection selection = TargetSelector.Select(
            Options(), s_data, store, InvalidationScope.Partial);

        Assert.Equal(TargetMode.Partial, selection.Mode);
        Assert.Equal(500 + (TestPaths.TotalKeys - 1_500), selection.Count);

        // 정상 생성된 1,000 개는 대상이 아니다.
        Assert.DoesNotContain(BucketKey.FromIndex(0), selection.Buckets);
        Assert.Contains(BucketKey.FromIndex(1_000), selection.Buckets);
        Assert.Contains(BucketKey.FromIndex(2_879), selection.Buckets);
    }

    /// <summary>모드 3 — <c>--resume</c>. 미생성 버킷만. 폴백으로 메운 것도 "있는 것"이다.</summary>
    [Fact]
    public void Target_ResumeSelectsMissingOnly()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        for (int index = 0; index < 1_500; index++)
        {
            var bucket = BucketKey.FromIndex(index);

            store.SetBucket(bucket, Plan(bucket, index < 1_000 ? PlanOrigin.Prebaked : PlanOrigin.Fallback));
        }

        TargetSelection selection = TargetSelector.Select(
            Options("--resume"), s_data, store, InvalidationScope.Full);

        Assert.Equal(TargetMode.Resume, selection.Mode);
        Assert.Equal(TestPaths.TotalKeys - 1_500, selection.Count);
        Assert.DoesNotContain(BucketKey.FromIndex(1_200), selection.Buckets);   // 폴백도 "있는 것"
        Assert.Contains(BucketKey.FromIndex(1_500), selection.Buckets);

        // 스토어가 아예 없으면 resume 도 전량이다.
        Assert.Equal(
            TestPaths.TotalKeys,
            TargetSelector.Select(Options("--resume"), s_data, null, InvalidationScope.Full).Count);
    }

    /// <summary>모드 4 — <c>--only</c> glob. 한 아키타입은 72버킷이다.</summary>
    [Fact]
    public void Target_OnlySelectsGlobMatches()
    {
        TargetSelection smiths = TargetSelector.Select(
            Options("--only", "blacksmith@*"), s_data, null, InvalidationScope.None);

        Assert.Equal(TargetMode.Only, smiths.Mode);
        Assert.Equal(72, smiths.Count);   // 6 × 4 × 3
        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));
        Assert.All(smiths.Buckets, b => Assert.Equal(smith.Code, b.A));

        // 상황으로 자르면 아키타입 40 × 기후 3 = 120 이다.
        TargetSelection dawnPeace = TargetSelector.Select(
            Options("--only", "*@Dawn.Peace.*"), s_data, null, InvalidationScope.None);

        Assert.Equal(TestPaths.ArchetypeCount * BucketKey.ClimateCount, dawnPeace.Count);
        Assert.All(dawnPeace.Buckets, b =>
        {
            Assert.Equal(TimeOfDay.Dawn, b.T);
            Assert.Equal(RegionState.Peace, b.R);
        });

        // --only 는 무효화가 None 이어도 돈다 — 부분 재생성은 사람이 지시한 것이다.
        Assert.Equal(InvalidationScope.None, smiths.Scope);
    }

    /// <summary>무효화가 None 이면 할 것이 없다.</summary>
    [Fact]
    public void Target_NoneSelectsNothing()
    {
        TargetSelection selection = TargetSelector.Select(
            Options(), s_data, StoreWith(TestPaths.TotalKeys), InvalidationScope.None);

        Assert.Equal(TargetMode.None, selection.Mode);
        Assert.Equal(0, selection.Count);
    }

    /// <summary>pinned 버킷은 어느 모드에서도 대상이 아니다. 사람이 고친 것을 다시 만들지 않는다.</summary>
    [Fact]
    public void Target_SkipsPinnedInEveryMode()
    {
        PlanStore store = StoreWith(10, PlanOrigin.Pinned);

        foreach ((PrebakeOptions options, InvalidationScope scope, int skipped) in new[]
        {
            // Full·Only 는 전량에서 시작하므로 pinned 를 실제로 걸러낸다.
            (Options(), InvalidationScope.Full, 10),
            (Options("--only", "*"), InvalidationScope.Full, 10),

            // Resume·Partial 은 후보를 만들 때 이미 제외한다 — pinned 는 "있는 것"이고 Origin 도 Pinned 다.
            (Options("--resume"), InvalidationScope.Full, 0),
            (Options(), InvalidationScope.Partial, 0),
        })
        {
            TargetSelection selection = TargetSelector.Select(options, s_data, store, scope);

            Assert.Equal(skipped, selection.SkippedPinned);
            Assert.Equal(TestPaths.TotalKeys - 10, selection.Count);

            for (int index = 0; index < 10; index++)
            {
                Assert.DoesNotContain(BucketKey.FromIndex(index), selection.Buckets);
            }
        }
    }

    /// <summary>측정 회차용 축소가 그대로 동작한다 (T2-19 회차 재현).</summary>
    [Fact]
    public void Target_NarrowsForMeasurementRuns()
    {
        // --archetypes 4 = 앞 4 아키타입의 72버킷 = 288. W6 연속 288버킷 회차와 같다.
        Assert.Equal(
            288,
            TargetSelector.Select(Options("--archetypes", "4"), s_data, null, InvalidationScope.Full).Count);

        // --limit 60
        Assert.Equal(
            60,
            TargetSelector.Select(Options("--limit", "60"), s_data, null, InvalidationScope.Full).Count);

        // --stride 991 로 흩은 표본. 2,880 과 서로소라 전 아키타입에 퍼진다.
        TargetSelection strided = TargetSelector.Select(
            Options("--stride", "991", "--limit", "20"), s_data, null, InvalidationScope.Full);

        Assert.Equal(20, strided.Count);
        Assert.True(strided.Buckets.Select(b => b.A).Distinct().Count() >= 15);
    }

    /// <summary>
    /// T3-10 완료 조건 — 정렬 결과의 앞 25% 가 전부 <see cref="RegionState.Peace"/> 다.
    ///
    /// Peace 는 4개 지역 상태 중 하나라 정확히 25%(720개)이고 가중치가 가장 높다(10).
    /// 중단되어도 실제로 많이 쓰이는 버킷이 먼저 채워져 있어야 한다.
    /// </summary>
    [Fact]
    public void Target_SortsPeaceFirst()
    {
        ImmutableArray<BucketKey> sorted =
            TargetSelector.Select(Options(), s_data, null, InvalidationScope.Full).Buckets;

        Assert.Equal(TestPaths.TotalKeys, sorted.Length);

        int quarter = TestPaths.TotalKeys / 4;

        for (int i = 0; i < quarter; i++)
        {
            Assert.Equal(RegionState.Peace, sorted[i].R);
        }

        // 그 뒤는 가중치 순 — Alert(3) → War(2) → Disaster(1).
        Assert.Equal(RegionState.Alert, sorted[quarter].R);
        Assert.Equal(RegionState.War, sorted[quarter * 2].R);
        Assert.Equal(RegionState.Disaster, sorted[quarter * 3].R);

        // 가중치는 마스터데이터에서 온다. 코드에 하드코딩하지 않는다.
        Assert.Equal(10, s_data.Buckets.PrebakePriorityOf(RegionState.Peace));
        Assert.Equal(3, s_data.Buckets.PrebakePriorityOf(RegionState.Alert));
        Assert.Equal(2, s_data.Buckets.PrebakePriorityOf(RegionState.War));
        Assert.Equal(1, s_data.Buckets.PrebakePriorityOf(RegionState.Disaster));

        // 같은 가중치 안에서는 버킷 인덱스 오름차순 — 회차 간 diff 가 의미를 가져야 한다.
        for (int i = 1; i < quarter; i++)
        {
            Assert.True(sorted[i - 1].ToIndex() < sorted[i].ToIndex());
        }
    }

    /// <summary>부분 집합도 같은 순서 규칙을 따른다.</summary>
    [Fact]
    public void Target_SortsSubsetsByPriorityToo()
    {
        ImmutableArray<BucketKey> smiths =
            TargetSelector.Select(Options("--only", "blacksmith@*"), s_data, null, InvalidationScope.Full).Buckets;

        Assert.Equal(72, smiths.Length);

        // 아키타입 하나는 6 × 3 = 18 개의 Peace 버킷을 갖는다.
        for (int i = 0; i < 18; i++)
        {
            Assert.Equal(RegionState.Peace, smiths[i].R);
        }

        Assert.NotEqual(RegionState.Peace, smiths[18].R);
    }

    /// <summary>대상 순서가 결정론이다. 두 번 부르면 같은 목록이 나온다.</summary>
    [Fact]
    public void Target_IsDeterministic()
    {
        ImmutableArray<BucketKey> first =
            TargetSelector.Select(Options(), s_data, StoreWith(700), InvalidationScope.Partial).Buckets;
        ImmutableArray<BucketKey> second =
            TargetSelector.Select(Options(), s_data, StoreWith(700), InvalidationScope.Partial).Buckets;

        Assert.Equal(first.ToArray(), second.ToArray());
    }
}
