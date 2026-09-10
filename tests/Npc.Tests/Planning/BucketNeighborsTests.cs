using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>T2-16 — 인접 버킷 재사용. docs/12 §7.</summary>
public sealed class BucketNeighborsTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static BucketKey Bucket(string archetype, TimeOfDay time, RegionState region, Climate climate)
    {
        Assert.True(s_data.Archetypes.TryGet(archetype, out ArchetypeDef def));
        return new BucketKey(def.Code, time, region, climate);
    }

    [Fact]
    public void Neighbors_NeverCrossArchetype()
    {
        // 전 버킷 2,880개. 아키타입이 바뀌는 후보가 하나라도 있으면 안 된다 —
        // 대장장이 플랜을 농부에게 주면 허용 액션 목록부터 어긋난다.
        for (int index = 0; index < TestPaths.TotalKeys; index++)
        {
            BucketKey key = BucketKey.FromIndex(index);

            foreach (BucketKey neighbor in BucketNeighbors.Of(key))
            {
                Assert.Equal(key.A, neighbor.A);
                Assert.NotEqual(key, neighbor);
            }
        }
    }

    [Fact]
    public void Neighbors_AreDistinctAndBounded()
    {
        for (int index = 0; index < TestPaths.TotalKeys; index++)
        {
            ImmutableArray<BucketKey> neighbors = BucketNeighbors.Of(BucketKey.FromIndex(index));

            Assert.InRange(neighbors.Length, 1, BucketNeighbors.MaxNeighbors);
            Assert.Equal(neighbors.Length, neighbors.Distinct().Count());
        }
    }

    [Fact]
    public void Neighbors_FollowSpecOrder()
    {
        // docs/12 §7 의 5순위 — 기후 완화 → 지역 완화 → 둘 다 → 인접 시간대 → 최후.
        BucketKey key = Bucket("blacksmith", TimeOfDay.Evening, RegionState.War, Climate.Cold);

        Assert.Equal(
            [
                key with { C = Climate.Fair },
                key with { R = RegionState.Alert },
                key with { R = RegionState.Alert, C = Climate.Fair },
                key with { T = TimeOfDay.Afternoon },
                key with { R = RegionState.Peace, C = Climate.Fair },
            ],
            [.. BucketNeighbors.Of(key)]);
    }

    [Fact]
    public void Neighbors_RelaxOneStepAtATime()
    {
        Assert.Equal(RegionState.War, BucketNeighbors.Relax(RegionState.Disaster));
        Assert.Equal(RegionState.Alert, BucketNeighbors.Relax(RegionState.War));
        Assert.Equal(RegionState.Peace, BucketNeighbors.Relax(RegionState.Alert));
        Assert.Equal(RegionState.Peace, BucketNeighbors.Relax(RegionState.Peace));

        Assert.Equal(TimeOfDay.Night, BucketNeighbors.AdjacentTime(TimeOfDay.Dawn));
        Assert.Equal(TimeOfDay.Evening, BucketNeighbors.AdjacentTime(TimeOfDay.Night));
    }

    [Fact]
    public void Neighbors_ReusedPlanRevalidated()
    {
        BucketKey source = Bucket("blacksmith", TimeOfDay.Evening, RegionState.Peace, Climate.Fair);
        BucketKey target = Bucket("blacksmith", TimeOfDay.Evening, RegionState.War, Climate.Cold);

        // target 의 후보 안에 source 가 있어야 이 테스트가 의미가 있다.
        Assert.Contains(source, BucketNeighbors.Of(target));

        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        store.SetBucket(source, store.Register(Compile(ValidPlan, source)));

        Assert.True(BucketNeighbors.TryReuse(target, store, s_data, out CompiledPlan? reused, out BucketKey from));

        Assert.NotNull(reused);
        Assert.Equal(source, from);

        // 빌려 온 플랜은 대상 버킷으로 다시 표시된다.
        Assert.Equal(target, reused!.Bucket);

        // 그리고 대상 버킷 기준으로 2·3단을 통과한 것이다.
        Assert.True(BucketNeighbors.Revalidate(reused, target, s_data));
    }

    [Fact]
    public void Neighbors_RejectPlanThatFailsRevalidationInTargetBucket()
    {
        // 이웃에 플랜이 있어도 대상 버킷에서 3단을 못 넘기면 빌리지 않는다.
        BucketKey source = Bucket("blacksmith", TimeOfDay.Evening, RegionState.Peace, Climate.Fair);
        BucketKey target = Bucket("blacksmith", TimeOfDay.Evening, RegionState.War, Climate.Cold);

        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        store.SetBucket(source, store.Register(Compile(IncoherentPlan, source)));

        Assert.False(BucketNeighbors.TryReuse(target, store, s_data, out CompiledPlan? reused, out _));
        Assert.Null(reused);
    }

    [Fact]
    public void Neighbors_ReturnNothingWhenStoreIsEmpty()
    {
        BucketKey target = Bucket("farmer", TimeOfDay.Noon, RegionState.Alert, Climate.Storm);
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        Assert.False(BucketNeighbors.TryReuse(target, store, s_data, out CompiledPlan? reused, out _));
        Assert.Null(reused);
    }

    private static CompiledPlan Compile(string json, BucketKey bucket)
    {
        Assert.True(SchemaValidator.Validate(json, out PlanDocument? document).IsValid);

        return PlanCompiler.Compile(document!, bucket, default, s_data, sourceJson: json);
    }

    private const string ValidPlan = """
        {"schema":1,"goal":"forge_batch","loop":true,"steps":[
          {"action":"MoveTo","args":{"poi":"$workplace"},"timeout_s":600},
          {"action":"Work","args":{"recipe":"iron_sword","count":1},"timeout_s":1800},
          {"action":"Store","args":{"item":"iron_sword","count":1},"timeout_s":120},
          {"action":"MoveTo","args":{"poi":"$home"},"timeout_s":600},
          {"action":"Sleep","args":{"until_time":"Morning"}}]}
        """;

    /// <summary>일터에 가지 않고 제작한다 — 어느 버킷에서도 3단을 못 넘긴다.</summary>
    private const string IncoherentPlan = """
        {"schema":1,"goal":"forge_now","loop":true,"steps":[
          {"action":"Craft","args":{"recipe":"iron_sword","count":1}},
          {"action":"MoveTo","args":{"poi":"$home"}},
          {"action":"Sleep","args":{"until_time":"Morning"}}]}
        """;
}
