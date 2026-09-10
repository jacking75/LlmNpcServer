using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>캐시 계측. docs/13 §6 · T3-04.</summary>
public sealed class CacheMetricsTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static CompiledPlan Plan(BucketKey bucket)
    {
        const string Json = """
            { "schema": 1, "goal": "cache_sample", "loop": true,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$home" } },
                { "action": "Rest", "args": { "duration_s": 600 } },
                { "action": "Wait", "args": { "duration_s": 60 } }
              ] }
            """;

        PlanDocument document = System.Text.Json.JsonSerializer.Deserialize(
            Json, PlanJsonContext.Default.PlanDocument)!;

        return Npc.Core.Plan.PlanCompiler.Compile(
            document, bucket, new PlanId(0), s_data, PlanOrigin.Prebaked);
    }

    private static ArchetypeId Code(string id)
    {
        Assert.True(s_data.Archetypes.TryGet(id, out ArchetypeDef def));

        return def.Code;
    }

    /// <summary>docs/13 §6 의 4개 지표가 다 나온다.</summary>
    [Fact]
    public void CacheMetrics_ExposesFourIndicators()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        var pool = new IndividualPlanPool();
        var metrics = new CacheMetrics(store, pool);

        Assert.Equal(TestPaths.TotalKeys, metrics.ColdBuckets);
        Assert.Equal(0, metrics.HitRate);
        Assert.Empty(metrics.TopMisses());
        Assert.Equal(0, metrics.IndividualTurnover);

        var hot = new BucketKey(Code("blacksmith"), TimeOfDay.Dawn, RegionState.Peace, Climate.Fair);
        var cold = new BucketKey(Code("farmer"), TimeOfDay.Night, RegionState.War, Climate.Storm);

        store.SetBucket(hot, Plan(hot));

        for (int i = 0; i < 49; i++)
        {
            _ = store.Resolve(hot);
        }

        _ = store.Resolve(cold);

        // (1) 히트율
        Assert.Equal(0.98, metrics.HitRate, 6);
        Assert.Equal(49, metrics.Hits);
        Assert.Equal(1, metrics.Misses);

        // (2) 콜드 버킷 수
        Assert.Equal(TestPaths.TotalKeys - 1, metrics.ColdBuckets);
        Assert.Equal(1, metrics.FilledBuckets);

        // (3) 미스 상위 버킷
        Assert.Equal(cold, Assert.Single(metrics.TopMisses()).Bucket);

        // (4) 개별 풀 회전율
        for (int i = 0; i < IndividualPlanPool.Capacity + 8; i++)
        {
            pool.Assign(Plan(hot), npc: i, tick: i + 1);
        }

        Assert.Equal(8.0 / IndividualPlanPool.Capacity, metrics.IndividualTurnover, 9);
        Assert.Equal(IndividualPlanPool.Capacity, metrics.IndividualLive);
    }

    /// <summary>아키타입별로 분해된다. 전체 평균만 보면 특정 아키타입의 재앙적 미스를 놓친다.</summary>
    [Fact]
    public void CacheMetrics_DecomposesByArchetype()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        var metrics = new CacheMetrics(store);

        ArchetypeId smith = Code("blacksmith");
        ArchetypeId farmer = Code("farmer");

        // 대장장이는 72버킷을 전부 채운다. 농부는 하나도 안 채운다.
        for (int i = 0; i < CacheMetrics.BucketsPerArchetype; i++)
        {
            var bucket = BucketKey.FromIndex((smith.Value * CacheMetrics.BucketsPerArchetype) + i);

            store.SetBucket(bucket, Plan(bucket));
            _ = store.Resolve(bucket);
        }

        for (int i = 0; i < 5; i++)
        {
            _ = store.Resolve(new BucketKey(farmer, TimeOfDay.Noon, RegionState.Peace, Climate.Fair));
        }

        ArchetypeCacheStats smithStats = metrics.StatsOf(smith);
        ArchetypeCacheStats farmerStats = metrics.StatsOf(farmer);

        Assert.Equal(1.0, smithStats.HitRate, 6);
        Assert.Equal(CacheMetrics.BucketsPerArchetype, smithStats.Hits);
        Assert.Equal(0, smithStats.ColdBuckets);

        Assert.Equal(0, farmerStats.HitRate);
        Assert.Equal(5, farmerStats.Misses);
        Assert.Equal(CacheMetrics.BucketsPerArchetype, farmerStats.ColdBuckets);

        Assert.Equal(TestPaths.ArchetypeCount, metrics.ByArchetype().Length);

        // 최하위 목록 — 조회가 있었던 둘만 올라오고 농부가 앞이다.
        var worst = metrics.WorstArchetypes();

        Assert.Equal(2, worst.Length);
        Assert.Equal(farmer, worst[0].Archetype);
        Assert.Equal(smith, worst[1].Archetype);
    }

    /// <summary>스냅샷 한 장이 패널에 필요한 것을 다 담는다.</summary>
    [Fact]
    public void CacheMetrics_SnapshotCarriesEveryField()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        var metrics = new CacheMetrics(store, new IndividualPlanPool());

        var bucket = new BucketKey(Code("baker"), TimeOfDay.Morning, RegionState.Peace, Climate.Fair);

        store.Pin(bucket, Plan(bucket));
        _ = store.Resolve(bucket);
        _ = store.Resolve(bucket with { C = Climate.Storm });

        CacheStats snapshot = metrics.Snapshot();

        Assert.Equal(0.5, snapshot.HitRate, 6);
        Assert.Equal(1, snapshot.Hits);
        Assert.Equal(1, snapshot.Misses);
        Assert.Equal(1, snapshot.FilledBuckets);
        Assert.Equal(TestPaths.TotalKeys - 1, snapshot.ColdBuckets);
        Assert.Equal(1, snapshot.PinnedBuckets);
        Assert.Equal(0, snapshot.IndividualTurnover);
        Assert.Equal(0, snapshot.IndividualLive);
        Assert.Single(snapshot.TopMisses);
        Assert.Single(snapshot.WorstArchetypes);
    }
}
