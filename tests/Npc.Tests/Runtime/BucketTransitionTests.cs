using System.Diagnostics;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Gateway;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>docs/14 §5. 시간대 전환 스파이크 완화.</summary>
[Collection(AllocationCollection.Name)]
public sealed class BucketTransitionTests
{
    private const int Npcs = 5_000;

    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private sealed record Rig(
        NpcServerLoop Loop,
        NpcStore Store,
        GameClock Clock,
        PlanSwapper Swapper,
        BucketTransition Transition,
        PlanStore Plans);

    private static Rig NewRig(int npcs)
    {
        NpcInstanceTable instances = NpcInstanceTable.Load(
            Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

        var store = new NpcStore();
        store.Allocate(npcs, s_data.Items.MaxCode + 1);

        var link = new NullGameServerLink();
        var clock = new GameClock(s_data.Buckets, timeScale: 600);
        var correlations = new CorrelationTable(npcs);
        var applier = new EventApplier(s_data, store, clock, correlations);
        var emitter = new CommandEmitter(s_data, new PoiBinder(s_data.Pois));
        var swapper = new PlanSwapper(store);
        var queue = new ReplanQueue(npcs);

        PlanStore plans = PlanStore.CreateIdleOnly(s_data);
        var fallbackOf = new int[s_data.Archetypes.Count];

        foreach (FallbackPlanEntry entry in s_data.Fallbacks!.Plans)
        {
            PlanId id = plans.Register(entry.Plan);

            plans.SetFallback(entry.Archetype, id);
            fallbackOf[entry.Archetype.Value] = id.Value;
        }

        var executor = new PlanExecutor(s_data, store, plans, correlations, emitter, timeScale: 600)
        {
            Swapper = swapper,
            ReplanQueue = queue,
        };

        var bands = new LodBandSet(store);
        var cognition = new CognitionScheduler(store, bands, plans);
        var interrupts = new InterruptMatcher(s_data, store);
        var transition = new BucketTransition(store, plans, s_data);

        for (int i = 0; i < npcs; i++)
        {
            NpcInstanceDef def = instances[i % instances.Count];

            applier.Seed(i, def.Home, def.Zone, def.Archetype, def.Home, def.Workplace);
            store.StepStatus[i] = (byte)StepStatus.Ready;
            executor.AssignPlan(i, new PlanId(fallbackOf[def.Archetype.Value]));
            store.Lod[i] = (byte)(i % LodBandSet.BandCount);
        }

        while (bands.Rebalance() > 0)
        {
            // 초기 배치는 측정 밖에서 끝낸다.
        }

        var loop = new NpcServerLoop(
            link, clock, applier, interrupts, cognition, executor, swapper, bands, queue)
        {
            Transition = transition,
        };

        return new Rig(loop, store, clock, swapper, transition, plans);
    }

    /// <summary>T1-61 완료 조건 — 지터가 결정론이다. Random 을 쓰면 리플레이가 깨진다.</summary>
    [Fact]
    public void Transition_JitterIsDeterministic()
    {
        for (int npc = 0; npc < 10_000; npc++)
        {
            int first = BucketTransition.JitterTicks(new NpcId(npc));
            int second = BucketTransition.JitterTicks(new NpcId(npc));

            Assert.Equal(first, second);
            Assert.InRange(first, -BucketTransition.JitterSpread, BucketTransition.JitterSpread);
        }

        // 예약 시각도 같은 입력이면 같다.
        Assert.Equal(
            BucketTransition.DueTick(1_000, new NpcId(77)),
            BucketTransition.DueTick(1_000, new NpcId(77)));
    }

    /// <summary>docs/14 §5 — 소스에 Random 이 없다.</summary>
    [Fact]
    public void Transition_UsesNoRandom()
    {
        string source = File.ReadAllText(TestPaths.At("src", "Npc.Runtime", "BucketTransition.cs"));

        // 주석에는 나온다 (쓰지 말라는 설명이다). 실제 호출만 잡는다.
        Assert.DoesNotContain("new Random", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Random.Shared", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stopwatch", source, StringComparison.Ordinal);
        Assert.Contains("PlanHash", source, StringComparison.Ordinal);
    }

    /// <summary>전환 한 번에 전원이 정확히 한 번 예약된다. 굶는 NPC 도 두 번 도는 NPC 도 없다.</summary>
    [Fact]
    public void Transition_SchedulesEveryNpcExactlyOnce()
    {
        Rig rig = NewRig(1_000);
        var seen = new int[1_000];

        rig.Transition.Schedule(new Tick(100), TimeOfDay.Evening);

        int applied = 0;

        for (long tick = 100; tick < 100 + BucketTransition.JitterWindow; tick++)
        {
            int batch = rig.Transition.Apply(new Tick(tick), rig.Swapper);

            applied += batch;

            // 이 틱에 처리된 NPC 를 세려면 예약 시각으로 역산한다.
            for (int npc = 0; npc < seen.Length; npc++)
            {
                if (BucketTransition.DueTick(100, new NpcId(npc)) == tick)
                {
                    seen[npc]++;
                }
            }
        }

        Assert.Equal(1_000, applied);
        Assert.All(seen, s => Assert.Equal(1, s));
        Assert.False(rig.Transition.InProgress);
    }

    /// <summary>지터가 601틱에 고르게 흩어진다. 한 틱에 몰리면 스파이크가 그대로 남는다.</summary>
    [Fact]
    public void Transition_SpreadsAcrossWholeWindow()
    {
        var histogram = new int[BucketTransition.JitterWindow];

        for (int npc = 0; npc < Npcs; npc++)
        {
            histogram[BucketTransition.JitterSpread + BucketTransition.JitterTicks(new NpcId(npc))]++;
        }

        // 5,000 / 601 ≈ 8.3. 한 오프셋에 몰리면 스파이크가 그대로다.
        int worst = histogram.Max();
        int used = histogram.Count(c => c > 0);

        Assert.True(worst <= 30, $"한 틱에 {worst} 마리가 몰린다.");
        Assert.True(used >= BucketTransition.JitterWindow * 0.95, $"쓰인 오프셋 {used}");
    }

    /// <summary>T1-61 완료 조건 — 전환 구간의 틱 p99 가 평시의 2배를 넘지 않는다.</summary>
    [Fact]
    [Trait("Category", "Load")]
    public void Transition_TickP99StaysUnderTwiceBaseline()
    {
        Rig rig = NewRig(Npcs);

        // 워밍업 — JIT 승격이 측정 창 안에서 일어나면 안 된다.
        Run(rig, 1, 1_000);

        double baseline = Percentile(Run(rig, 1_001, 2_000), 0.99);

        // 전환을 강제한다. 지터가 없으면 이 한 틱에 5,000회 스왑이 몰린다.
        rig.Transition.Schedule(new Tick(2_000), TimeOfDay.Evening);

        double transition = Percentile(
            Run(rig, 2_001, 2_000 + BucketTransition.JitterWindow), 0.99);

        Assert.True(
            transition <= Math.Max(baseline * 2, 0.5),
            $"전환 p99 {transition:0.###}ms · 평시 p99 {baseline:0.###}ms");
    }

    private static double[] Run(Rig rig, long from, long to)
    {
        var samples = new double[to - from + 1];
        double perMs = Stopwatch.Frequency / 1_000.0;

        for (long tick = from; tick <= to; tick++)
        {
            long start = Stopwatch.GetTimestamp();
            rig.Loop.RunTick(new Tick(tick));
            samples[tick - from] = (Stopwatch.GetTimestamp() - start) / perMs;
        }

        return samples;
    }

    private static double Percentile(double[] samples, double q)
    {
        double[] sorted = [.. samples];
        Array.Sort(sorted);

        return sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
