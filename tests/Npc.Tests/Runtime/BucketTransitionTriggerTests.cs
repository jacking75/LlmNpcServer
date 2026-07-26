using System.Diagnostics;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Gateway;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>
/// docs/14 §5 T4-13 — 전환 계기 셋(시간대 · 지역상태 · 기후)과 존 범위 예약.
/// P1(T1-61)의 <see cref="BucketTransitionTests"/> 는 전원 예약 경로를 본다.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class BucketTransitionTriggerTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private sealed record Rig(
        NpcServerLoop Loop,
        NpcStore Store,
        GameClock Clock,
        PlanSwapper Swapper,
        BucketTransition Transition,
        ZoneStateTable Zones,
        PlanStore Plans,
        NullGameServerLink Link);

    /// <summary>버킷을 실제로 채워 둔다 — 비어 있으면 전부 같은 폴백으로 해소돼 스왑이 안 걸린다.</summary>
    private static Rig NewRig(int npcs, bool fillBuckets = true, bool withZoneTable = true)
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
        var zones = new ZoneStateTable(s_data);

        var transition = new BucketTransition(store, plans, s_data)
        {
            ZoneStates = withZoneTable ? zones : null,
        };

        for (int i = 0; i < npcs; i++)
        {
            NpcInstanceDef def = instances[i % instances.Count];

            applier.Seed(i, def.Home, def.Zone, def.Archetype, def.Home, def.Workplace);
            store.StepStatus[i] = (byte)StepStatus.Ready;
            executor.AssignPlan(i, new PlanId(fallbackOf[def.Archetype.Value]));
        }

        if (fillBuckets)
        {
            FillBuckets(plans);
        }

        while (bands.Rebalance() > 0)
        {
            // 초기 배치는 측정 밖에서 끝낸다.
        }

        var loop = new NpcServerLoop(
            link, clock, applier,
            new InterruptMatcher(s_data, store),
            new CognitionScheduler(store, bands, plans),
            executor, swapper, bands, queue)
        {
            Transition = transition,
        };

        return new Rig(loop, store, clock, swapper, transition, zones, plans, link);
    }

    /// <summary>2,880 버킷 전부에 서로 다른 플랜을 건다. 버킷마다 goal 이 달라 전환이 관측된다.</summary>
    private static void FillBuckets(PlanStore plans)
    {
        CompiledPlan template = plans[PlanStore.IdlePlanId];

        for (int i = 0; i < BucketKey.TotalKeys; i++)
        {
            BucketKey key = BucketKey.FromIndex(i);

            plans.SetBucket(key, template with { Bucket = key, Goal = $"b{i}" });
        }
    }

    private static ZoneId ZoneOf(Rig rig, int npc) => new(rig.Store.ZoneCode[npc]);

    private static GameEvent ZoneEvent(long sequence, ZoneId zone, RegionState state) => new()
    {
        Kind = GameEventKind.ZoneStateChanged,
        Sequence = sequence,
        OccurredAt = new Tick(sequence),
        Zone = zone,
        Code = (byte)state,
    };

    private static GameEvent WeatherEvent(long sequence, ZoneId zone, Climate climate) => new()
    {
        Kind = GameEventKind.WeatherChanged,
        Sequence = sequence,
        OccurredAt = new Tick(sequence),
        Zone = zone,
        Code = (byte)climate,
    };

    /// <summary>계기 1 — 시간대 전환. P1 이 이미 갖고 있던 경로다.</summary>
    [Fact]
    public void Transition_TimeOfDayTriggersSchedule()
    {
        Rig rig = NewRig(200);

        Assert.False(rig.Transition.InProgress);

        rig.Transition.Schedule(new Tick(100), TimeOfDay.Evening);

        Assert.True(rig.Transition.InProgress);
        Assert.Equal(200, rig.Transition.Pending);
        Assert.Equal(1, rig.Transition.TimeOfDayTransitions);
        Assert.Equal(0, rig.Transition.ZoneTransitions);

        int applied = 0;

        for (long tick = 100; tick < 100 + BucketTransition.JitterWindow; tick++)
        {
            applied += rig.Transition.Apply(new Tick(tick), rig.Swapper);
        }

        Assert.Equal(200, applied);
        Assert.False(rig.Transition.InProgress);
        Assert.True(rig.Transition.Swapped > 0, "시간대가 바뀌었는데 아무 플랜도 갈아타지 않았다.");
    }

    /// <summary>
    /// 계기 2 — <c>ZoneStateChanged</c>. <b>그 존만</b> 예약된다.
    /// P1 은 이 계기가 없어 플래그는 War 인데 다음 시간대 경계까지 평시 플랜을 쓰고 있었다.
    /// </summary>
    [Fact]
    public void Transition_ZoneStateTriggersScheduleForThatZoneOnly()
    {
        Rig rig = NewRig(300);

        ZoneId target = ZoneOf(rig, 0);
        int inZone = CountInZone(rig, target);
        int outside = rig.Store.Count - inZone;

        Assert.True(inZone > 0 && outside > 0, "한 존에 전원이 모여 있으면 범위 테스트가 무의미하다.");

        GameEvent ev = ZoneEvent(1, target, RegionState.War);
        Assert.True(rig.Transition.Observe(in ev, rig.Clock));

        Assert.Equal(inZone, rig.Transition.Pending);
        Assert.Equal(1, rig.Transition.ZoneTransitions);
        Assert.Equal(0, rig.Transition.TimeOfDayTransitions);
        Assert.Equal(RegionState.War, rig.Zones.RegionOf(target));

        // 같은 상태가 다시 오면 예약하지 않는다 — 엣지 트리거다 (docs/01 §7 과 같은 이유).
        GameEvent again = ZoneEvent(2, target, RegionState.War);
        Assert.False(rig.Transition.Observe(in again, rig.Clock));
        Assert.Equal(inZone, rig.Transition.Pending);

        // 다른 존은 손대지 않았다.
        int applied = Drain(rig, from: 0);
        Assert.Equal(inZone, applied);
    }

    /// <summary>계기 3 — <c>WeatherChanged</c>. 역시 그 존만.</summary>
    [Fact]
    public void Transition_WeatherTriggersScheduleForThatZoneOnly()
    {
        Rig rig = NewRig(300);

        ZoneId target = ZoneOf(rig, 0);
        int inZone = CountInZone(rig, target);

        GameEvent ev = WeatherEvent(1, target, Climate.Storm);
        Assert.True(rig.Transition.Observe(in ev, rig.Clock));

        Assert.Equal(inZone, rig.Transition.Pending);
        Assert.Equal(Climate.Storm, rig.Zones.ClimateOf(target));
        Assert.Equal(inZone, Drain(rig, from: 0));

        // Cold 는 플래그가 없어 표 없이는 못 가른다. 표가 있으면 정확하다.
        GameEvent cold = WeatherEvent(2, target, Climate.Cold);
        Assert.True(rig.Transition.Observe(in cold, rig.Clock));
        Assert.Equal(Climate.Cold, rig.Zones.ClimateOf(target));
    }

    /// <summary>세 계기가 겹쳐도 NPC 는 예약 리스트에 한 번만 들어간다.</summary>
    [Fact]
    public void Transition_OverlappingTriggersKeepOneReservationPerNpc()
    {
        Rig rig = NewRig(300);
        ZoneId target = ZoneOf(rig, 0);

        rig.Transition.Schedule(new Tick(0), TimeOfDay.Evening);
        Assert.Equal(300, rig.Transition.Pending);

        GameEvent zone = ZoneEvent(1, target, RegionState.War);
        rig.Transition.Observe(in zone, rig.Clock);

        GameEvent weather = WeatherEvent(2, target, Climate.Storm);
        rig.Transition.Observe(in weather, rig.Clock);

        // 겹쳐도 예약 수는 그대로다 — 나중 계기가 앞의 것을 덮어쓴다.
        Assert.Equal(300, rig.Transition.Pending);

        // 전부 정확히 한 번 처리된다.
        Assert.Equal(300, Drain(rig, from: 0));
        Assert.False(rig.Transition.InProgress);
    }

    /// <summary>
    /// T4-13 완료 조건 — 전환이 결정론이다. 같은 이벤트 열을 두 번 먹이면 상태 해시가 같다.
    /// </summary>
    [Fact]
    public void Transition_IsDeterministic()
    {
        ulong first = RunScript();
        ulong second = RunScript();

        Assert.Equal(first, second);

        static ulong RunScript()
        {
            Rig rig = NewRig(400);
            ZoneId zoneA = ZoneOf(rig, 0);
            ZoneId zoneB = ZoneOf(rig, rig.Store.Count - 1);

            // 시계를 스스로 굴린다 — 그래야 시간대 경계가 실제로 지나가고
            // NpcServerLoop 이 Transition.Tick 에서 예약과 적용을 둘 다 한다.
            for (int i = 0; i < 1_400; i++)
            {
                Assert.True(rig.Clock.Step(out Tick tick));

                // 정해진 틱에 정해진 계기를 넣는다. Random 도 벽시계도 쓰지 않는다.
                if (tick.Value == 10)
                {
                    GameEvent war = ZoneEvent(tick.Value, zoneA, RegionState.War);
                    rig.Transition.Observe(in war, rig.Clock);
                }

                if (tick.Value == 50)
                {
                    GameEvent storm = WeatherEvent(tick.Value, zoneB, Climate.Storm);
                    rig.Transition.Observe(in storm, rig.Clock);
                }

                if (tick.Value == 400)
                {
                    GameEvent peace = ZoneEvent(tick.Value, zoneA, RegionState.Peace);
                    rig.Transition.Observe(in peace, rig.Clock);
                }

                rig.Loop.RunTick(tick);
            }

            Assert.True(rig.Transition.TimeOfDayTransitions > 0, "시간대 경계가 한 번도 지나지 않았다.");
            Assert.Equal(3, rig.Transition.ZoneTransitions);
            Assert.True(rig.Transition.Swapped > 0);

            return rig.Store.StateHash();
        }
    }

    /// <summary>
    /// 존 상태 표가 없으면 <c>Alert</c>·<c>Disaster</c>·<c>Cold</c> 버킷을 찾지 못한다.
    /// 표가 있으면 정확하다 — 이게 프리베이크한 버킷을 실제로 쓰는 근거다 (P4 게이트 히트율).
    /// </summary>
    [Fact]
    public void Transition_ZoneTableRecoversWhatFlagsCannot()
    {
        // 플래그 추정: RegionPeaceful -> Peace(가중치 10), RegionUnderAttack -> War(2).
        // Alert 와 Disaster 는 절대 나오지 않는다.
        Assert.Equal(RegionState.Peace, s_data.Buckets.RegionStateOf(s_data.Buckets.FlagsOf(RegionState.Peace)));
        Assert.Equal(RegionState.Peace, s_data.Buckets.RegionStateOf(s_data.Buckets.FlagsOf(RegionState.Alert)));
        Assert.Equal(RegionState.War, s_data.Buckets.RegionStateOf(s_data.Buckets.FlagsOf(RegionState.War)));
        Assert.Equal(RegionState.War, s_data.Buckets.RegionStateOf(s_data.Buckets.FlagsOf(RegionState.Disaster)));
        Assert.Equal(Climate.Fair, s_data.Buckets.ClimateOf(s_data.Buckets.FlagsOf(Climate.Cold)));

        // 표는 들어온 값을 그대로 보관한다.
        var zones = new ZoneStateTable(s_data);
        var zone = new ZoneId(1);

        Assert.True(zones.Set(zone, RegionState.Alert));
        Assert.Equal(RegionState.Alert, zones.RegionOf(zone));
        Assert.False(zones.Set(zone, RegionState.Alert));   // 안 바뀌었다

        Assert.True(zones.Set(zone, Climate.Cold));
        Assert.Equal(Climate.Cold, zones.ClimateOf(zone));
        Assert.Equal(2, zones.Changes);

        // 모르는 존은 기본값이다.
        Assert.Equal(RegionState.Peace, zones.RegionOf(new ZoneId(60_000)));
        Assert.Equal(Climate.Fair, zones.ClimateOf(new ZoneId(60_000)));
    }

    /// <summary>예약·처리 경로의 할당이 0 이다 — 틱 루프 안에서 돈다.</summary>
    [Fact]
    public void Transition_DoesNotAllocate()
    {
        Rig rig = NewRig(1_000);
        ZoneId target = ZoneOf(rig, 0);

        Churn(rig, target, 20);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Churn(rig, target, 20);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        static void Churn(Rig rig, ZoneId zone, int rounds)
        {
            for (int round = 0; round < rounds; round++)
            {
                rig.Transition.ScheduleZone(new Tick(round * 700), TimeOfDay.Noon, zone);
                rig.Transition.Schedule(new Tick(round * 700), TimeOfDay.Evening);

                for (long tick = round * 700; tick < (round * 700) + BucketTransition.JitterWindow; tick++)
                {
                    rig.Transition.Apply(new Tick(tick), rig.Swapper);
                }
            }
        }
    }

    /// <summary>
    /// T4-13 완료 조건 — 존 이벤트가 몰려도 전환 구간 틱 p99 가 평시의 2배를 넘지 않는다.
    ///
    /// P1 의 <c>Transition_TickP99StaysUnderTwiceBaseline</c> 은 전원 예약 한 번을 본다.
    /// 여기서는 <b>존 이벤트를 매 틱 쏘면서</b> 잰다 — 존 이벤트마다 5,000마리를 다시 세면
    /// 완화하려던 스파이크가 그대로 돌아온다 (docs/14 §5).
    /// </summary>
    [Fact]
    [Trait("Category", "Load")]
    public void Transition_P99Under2xBaseline()
    {
        Rig rig = NewRig(5_000);

        Run(rig, 1, 1_000);
        double baseline = Percentile(Run(rig, 1_001, 2_000), 0.99);

        // 전원 예약 + 존 이벤트를 매 틱 번갈아 쏜다.
        rig.Transition.Schedule(new Tick(2_000), TimeOfDay.Evening);

        var samples = new double[BucketTransition.JitterWindow];
        double perMs = Stopwatch.Frequency / 1_000.0;
        RegionState[] states = [RegionState.Alert, RegionState.War, RegionState.Disaster, RegionState.Peace];

        for (int i = 0; i < samples.Length; i++)
        {
            long tick = 2_001 + i;
            var zone = new ZoneId(rig.Store.ZoneCode[i % rig.Store.Count]);
            GameEvent ev = ZoneEvent(tick, zone, states[i % states.Length]);

            long start = Stopwatch.GetTimestamp();
            rig.Transition.Observe(in ev, rig.Clock);
            rig.Loop.RunTick(new Tick(tick));
            samples[i] = (Stopwatch.GetTimestamp() - start) / perMs;
        }

        double transition = Percentile(samples, 0.99);

        Assert.True(
            transition <= Math.Max(baseline * 2, 0.5),
            $"전환 p99 {transition:0.###}ms · 평시 p99 {baseline:0.###}ms");

        // 실제로 계기가 여러 번 걸렸는지 확인한다 — 0 이면 측정이 무의미하다.
        Assert.True(rig.Transition.ZoneTransitions > 100, $"존 전환 {rig.Transition.ZoneTransitions}회");
    }

    private static int CountInZone(Rig rig, ZoneId zone)
    {
        int count = 0;

        for (int npc = 0; npc < rig.Store.Count; npc++)
        {
            if (rig.Store.ZoneCode[npc] == zone.Value)
            {
                count++;
            }
        }

        return count;
    }

    private static int Drain(Rig rig, long from)
    {
        int applied = 0;

        for (long tick = from; tick < from + BucketTransition.JitterWindow; tick++)
        {
            applied += rig.Transition.Apply(new Tick(tick), rig.Swapper);
        }

        return applied;
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
