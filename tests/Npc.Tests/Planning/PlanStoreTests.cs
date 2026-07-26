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

    /// <summary>
    /// T3-02 완료 조건 — pinned 는 프리베이크가 덮어쓰지 않는다 (docs/13 §2·§5).
    /// 사람이 검수·수정한 플랜이 재프리베이크로 날아가면 검수 작업이 통째로 사라진다.
    /// </summary>
    [Fact]
    public void PlanStore_PinnedIsNotOverwritten()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));

        var bucket = new BucketKey(smith.Code, TimeOfDay.Evening, RegionState.War, Climate.Cold);

        // 프리베이크가 먼저 한 번 채운다 — 이건 덮어써도 된다.
        store.SetBucket(bucket, SamplePlan("blacksmith") with { Goal = "prebaked_v1" });
        Assert.Equal("prebaked_v1", store.Resolve(bucket).Goal);
        Assert.False(store.IsPinned(bucket));

        store.SetBucket(bucket, SamplePlan("blacksmith") with { Goal = "prebaked_v2" });
        Assert.Equal("prebaked_v2", store.Resolve(bucket).Goal);

        // 사람이 검수해서 고정한다.
        PlanId pinned = store.Pin(bucket, SamplePlan("blacksmith") with { Goal = "human_reviewed" });

        Assert.True(store.IsPinned(bucket));
        Assert.Equal(1, store.PinnedBuckets);
        Assert.Equal(PlanOrigin.Pinned, store.OriginOf(bucket));

        // 재프리베이크. 두 오버로드 다 거절되어야 한다.
        int countBefore = store.Count;

        Assert.Equal(pinned, store.SetBucket(bucket, SamplePlan("blacksmith") with { Goal = "prebaked_v3" }));
        Assert.Equal(pinned, store.SetBucket(bucket, store.Register(SamplePlan("blacksmith"))));

        Assert.Equal("human_reviewed", store.Resolve(bucket, out PlanOrigin origin).Goal);
        Assert.Equal(PlanOrigin.Pinned, origin);

        // CompiledPlan 오버로드는 등록조차 하지 않는다 — 레지스트리에 쓰레기를 남기지 않는다.
        // PlanId 오버로드는 호출부가 이미 등록해 버린 것이라 +1 이다.
        Assert.Equal(countBefore + 1, store.Count);

        // 다른 버킷은 영향이 없다.
        var other = bucket with { C = Climate.Fair };
        store.SetBucket(other, SamplePlan("blacksmith") with { Goal = "prebaked_other" });
        Assert.Equal("prebaked_other", store.Resolve(other).Goal);
    }

    /// <summary>
    /// T3-01 완료 조건 — 락이 없다. 동시 읽기 1M 회 중 예외가 하나도 나지 않는다.
    /// 쓰는 쪽이 동시에 등록·교체를 하는 동안에도 읽는 쪽은 항상 유효한 플랜을 본다.
    /// </summary>
    [Fact]
    public async Task PlanStore_IsLockFree()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        const int Readers = 4;
        const int ReadsPerReader = 250_000;   // 4 × 250k = 1M

        var exceptions = new List<Exception>();
        var gate = new Lock();
        using var stop = new CancellationTokenSource();

        // 쓰는 쪽 — 읽는 동안 계속 등록하고 버킷을 갈아 끼운다.
        var writer = Task.Run(() =>
        {
            try
            {
                int index = 0;

                while (!stop.Token.IsCancellationRequested)
                {
                    BucketKey bucket = BucketKey.FromIndex(index++ % BucketKey.TotalKeys);

                    store.SetBucket(bucket, SamplePlan("blacksmith") with { Goal = "hot_swap" });
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    exceptions.Add(ex);
                }
            }
        });

        Task[] readers = [.. Enumerable.Range(0, Readers).Select(worker => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < ReadsPerReader; i++)
                {
                    BucketKey bucket = BucketKey.FromIndex((i + (worker * 7)) % BucketKey.TotalKeys);

                    CompiledPlan plan = store.Resolve(bucket, out PlanOrigin origin);

                    // null 도, 빈 플랜도, 범위 밖도 없다.
                    Assert.NotNull(plan);
                    Assert.NotEmpty(plan.Steps);
                    Assert.True(Enum.IsDefined(origin));
                    Assert.NotNull(store[plan.Id]);
                }
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    exceptions.Add(ex);
                }
            }
        }))];

        await Task.WhenAll(readers);
        await stop.CancelAsync();
        await writer;

        Assert.Empty(exceptions);
        Assert.Equal(Readers * ReadsPerReader, store.Hits + store.Misses);
    }

    /// <summary>
    /// T3-01 완료 조건 — 조회가 O(1) 이다.
    /// 2,880 버킷을 전부 채워도 빈 스토어와 같은 시간에 답해야 한다. 첨자 두 번이면 그렇다.
    /// </summary>
    [Fact]
    public void PlanStore_ResolveIsO1()
    {
        const int Iterations = 200_000;

        PlanStore empty = PlanStore.CreateIdleOnly(s_data);
        PlanStore full = PlanStore.CreateIdleOnly(s_data);

        for (int index = 0; index < BucketKey.TotalKeys; index++)
        {
            full.SetBucket(BucketKey.FromIndex(index), SamplePlan("blacksmith"));
        }

        Assert.Equal(BucketKey.TotalKeys, full.FilledBuckets);
        Assert.Equal(0, full.ColdBuckets);

        // 워밍업 — JIT 티어 승격을 두 쪽 다 끝낸다.
        _ = Measure(empty, 20_000);
        _ = Measure(full, 20_000);

        double emptyMs = Measure(empty, Iterations);
        double fullMs = Measure(full, Iterations);

        // 스토어 크기가 조회 시간에 실리면 O(1) 이 아니다. 슬랙을 크게 잡아도 4배는 안 넘는다.
        Assert.True(
            fullMs <= (emptyMs * 4) + 5,
            $"빈 스토어 {emptyMs:F2}ms · 2,880 채운 스토어 {fullMs:F2}ms — 크기에 비례한다.");

        static double Measure(PlanStore store, int iterations)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();

            for (int i = 0; i < iterations; i++)
            {
                _ = store.Resolve(BucketKey.FromIndex(i % BucketKey.TotalKeys));
            }

            return System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    /// <summary>미스 상위 버킷과 아키타입별 분해. docs/13 §6.</summary>
    [Fact]
    public void PlanStore_TracksHitsAndMisses()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));

        var hot = new BucketKey(smith.Code, TimeOfDay.Dawn, RegionState.Peace, Climate.Fair);
        var cold = new BucketKey(smith.Code, TimeOfDay.Night, RegionState.War, Climate.Storm);

        store.SetBucket(hot, SamplePlan("blacksmith"));

        for (int i = 0; i < 10; i++)
        {
            _ = store.Resolve(hot);
        }

        for (int i = 0; i < 3; i++)
        {
            _ = store.Resolve(cold);
        }

        Assert.Equal(10, store.Hits);
        Assert.Equal(3, store.Misses);
        Assert.Equal(10 / 13.0, store.HitRate, 6);
        Assert.Equal(10, store.HitsOf(hot));
        Assert.Equal(3, store.MissesOf(cold));

        Assert.Equal(cold, Assert.Single(store.TopMisses(10)).Bucket);

        Span<long> hits = new long[BucketKey.ArchetypeCount];
        Span<long> misses = new long[BucketKey.ArchetypeCount];
        store.HitsByArchetype(hits, misses);

        Assert.Equal(10, hits[smith.Code.Value]);
        Assert.Equal(3, misses[smith.Code.Value]);

        store.ResetCounters();
        Assert.Equal(0, store.Hits);
        Assert.Equal(0, store.Misses);
    }

    /// <summary>등록 id 는 여러 워커가 동시에 불러도 겹치지 않는다.</summary>
    [Fact]
    public void PlanStore_RegisterIsThreadSafe()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        CompiledPlan plan = SamplePlan("blacksmith");

        const int Workers = 8;
        const int PerWorker = 200;

        var ids = new PlanId[Workers][];

        Parallel.For(0, Workers, worker =>
        {
            var mine = new PlanId[PerWorker];

            for (int i = 0; i < PerWorker; i++)
            {
                mine[i] = store.Register(plan);
            }

            ids[worker] = mine;
        });

        var all = ids.SelectMany(x => x).Select(id => id.Value).ToList();

        Assert.Equal(Workers * PerWorker, all.Distinct().Count());
        Assert.Equal((Workers * PerWorker) + 1, store.Count);

        foreach (int id in all)
        {
            Assert.Equal("sample_plan", store[id].Goal);
            Assert.Equal(id, store[id].Id.Value);
        }
    }

    [Fact]
    public void PlanStore_ResolveDoesNotAllocate()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        var bucket = new BucketKey(new ArchetypeId(0), TimeOfDay.Noon, RegionState.Peace, Climate.Fair);

        // JIT 티어 승격이 끝날 때까지 돌린다. 승격 시점에 한 번 할당이 잡힌다.
        for (int i = 0; i < 30_000; i++)
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
