using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>docs/13 §2 (정식은 P3). P1 에서는 폴백만 반환하지만 절대 null 이 아니다.</summary>
public sealed class PlanStoreTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static CompiledPlan SamplePlan(string archetype)
    {
        Assert.True(s_data.Archetypes.TryGet(archetype, out ArchetypeDef def));

        const string Json = """
            { "schema": 1, "goal": "sample_plan", "loop": true,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$home" } },
                { "action": "Sleep", "args": { "until_time": "Morning" } },
                { "action": "Rest", "args": { "duration_s": 600 } }
              ] }
            """;

        PlanDocument document = System.Text.Json.JsonSerializer.Deserialize(
            Json, PlanJsonContext.Default.PlanDocument)!;

        return PlanCompiler.Compile(
            document,
            new BucketKey(def.Code, TimeOfDay.Night, RegionState.Peace, Climate.Fair),
            new PlanId(0),
            s_data,
            PlanOrigin.Fallback);
    }

    /// <summary>T1-39 완료 조건 — Resolve 는 절대 null 을 반환하지 않는다.</summary>
    [Fact]
    public void PlanStore_ResolveNeverNull()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        for (int index = 0; index < BucketKey.TotalKeys; index++)
        {
            CompiledPlan plan = store.Resolve(BucketKey.FromIndex(index));

            Assert.NotNull(plan);
            Assert.NotEmpty(plan.Steps);
        }
    }

    /// <summary>최후 플랜은 전제조건이 없어 어떤 상태에서도 실행된다.</summary>
    [Fact]
    public void PlanStore_IdlePlanHasNoPreconditions()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        CompiledPlan idle = store[PlanStore.IdlePlanId];

        Assert.Equal(WorldFlags.None, idle.RequiredFlags);
        Assert.True(idle.Loop);
        Assert.False(idle.NeedsReplan(WorldFlags.None));

        foreach (StepFlags flags in idle.StepFlagSets)
        {
            Assert.Equal(WorldFlags.None, flags.Requires);
            Assert.Equal(WorldFlags.None, flags.RequiresAny);
            Assert.True(flags.IsSatisfiedBy(WorldFlags.None));
        }
    }

    [Fact]
    public void PlanStore_FallbackWinsOverIdle()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));

        var bucket = new BucketKey(smith.Code, TimeOfDay.Night, RegionState.Peace, Climate.Fair);
        Assert.Equal("idle_fallback", store.Resolve(bucket).Goal);

        PlanId id = store.Register(SamplePlan("blacksmith"));
        store.SetFallback(smith.Code, id);

        Assert.Equal("sample_plan", store.Resolve(bucket).Goal);
        Assert.Equal(1, store.FilledFallbacks);

        // 다른 아키타입은 여전히 최후 플랜이다.
        Assert.True(s_data.Archetypes.TryGet("farmer", out ArchetypeDef farmer));
        Assert.Equal(
            "idle_fallback",
            store.Resolve(bucket with { A = farmer.Code }).Goal);
    }

    [Fact]
    public void PlanStore_BucketWinsOverFallback()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));

        var bucket = new BucketKey(smith.Code, TimeOfDay.Evening, RegionState.War, Climate.Cold);

        store.SetFallback(smith.Code, store.Register(SamplePlan("blacksmith")));
        Assert.False(store.HasBucket(bucket));

        CompiledPlan bucketPlan = SamplePlan("blacksmith") with { Goal = "bucket_plan" };
        store.SetBucket(bucket, store.Register(bucketPlan));

        Assert.True(store.HasBucket(bucket));
        Assert.Equal("bucket_plan", store.Resolve(bucket).Goal);
        Assert.Equal(1, store.FilledBuckets);
    }

    [Fact]
    public void PlanStore_RegisterStampsPlanId()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        PlanId first = store.Register(SamplePlan("blacksmith"));
        PlanId second = store.Register(SamplePlan("farmer"));

        Assert.NotEqual(first, second);
        Assert.Equal(first, store[first].Id);
        Assert.Equal(second, store[second].Id);
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void PlanStore_UnknownPlanIdFallsBackToIdle()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        Assert.Equal("idle_fallback", store[9_999].Goal);
        Assert.Equal("idle_fallback", store[new PlanId(-1)].Goal);
    }

    [Fact]
    public void PlanStore_ResolveDoesNotAllocate()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        var bucket = new BucketKey(new ArchetypeId(0), TimeOfDay.Noon, RegionState.Peace, Climate.Fair);

        for (int i = 0; i < 100; i++)
        {
            _ = store.Resolve(bucket);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            _ = store.Resolve(bucket);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
