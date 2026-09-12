using Npc.Contracts;
using Npc.Core.Plan;
using Npc.Gateway;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Persistence;

/// <summary>
/// A-01 — 복원한 회차가 끊기지 않은 회차와 같은 상태로 이어진다.
///
/// <b>이것이 스냅샷의 유일한 합격 조건이다.</b> 파일이 왕복하는 것만으로는 부족하다 —
/// 복원한 뒤 같은 입력으로 같은 틱 수를 돌렸을 때 상태 해시가 같아야 "이어 간다" 가 참이다.
/// </summary>
public sealed class SnapshotDeterminismTests
{
    private const int Npcs = 200;
    private const int Before = 300;
    private const int After = 400;

    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    [Fact]
    [Trait("Category", "Determinism")]
    public void RestoredRun_MatchesContinuousRun()
    {
        // A 회차: 끊지 않고 Before + After 틱.
        Rig a = NewRig();

        for (long tick = 1; tick <= Before + After; tick++)
        {
            a.Loop.RunTick(new Tick(tick));
        }

        ulong continuous = a.Store.StateHash();

        // B 회차: Before 틱에서 스냅샷 → 새 리그에 복원 → After 틱 더.
        Rig b = NewRig();

        for (long tick = 1; tick <= Before; tick++)
        {
            b.Loop.RunTick(new Tick(tick));
        }

        var shadow = new ShadowBuffer(Npcs, b.Store.InventoryStride, b.Zones.Capacity);

        b.Store.CopyTo(shadow);
        b.Zones.CopyTo(shadow.ZoneRegion, shadow.ZoneClimate);

        Rig c = NewRig();

        c.Store.LoadFrom(shadow);
        c.Zones.LoadFrom(shadow.ZoneRegion, shadow.ZoneClimate);
        c.Clock.RestoreTo(new Tick(Before), Before);

        for (long tick = Before + 1; tick <= Before + After; tick++)
        {
            c.Loop.RunTick(new Tick(tick));
        }

        Assert.Equal(continuous, c.Store.StateHash());
    }

    [Fact]
    [Trait("Category", "Determinism")]
    public void SnapshotTick_DoesNotChangeTheRun()
    {
        // 스냅샷을 뜨는 것 자체가 상태를 바꾸면 안 된다.
        Rig plain = NewRig();
        Rig snapped = NewRig();

        var port = new SnapshotPort
        {
            Buffer = new ShadowBuffer(Npcs, snapped.Store.InventoryStride, snapped.Zones.Capacity),
            Store = snapped.Store,
            Zones = snapped.Zones,
            Correlations = snapped.Correlations,
        };

        snapped.Loop.Snapshots = port;

        for (long tick = 1; tick <= Before; tick++)
        {
            plain.Loop.RunTick(new Tick(tick));

            if (tick % 37 == 0)
            {
                port.Request();
            }

            snapped.Loop.RunTick(new Tick(tick));

            if (port.ReadyTick != 0)
            {
                port.Release();
            }
        }

        Assert.True(port.Copies > 0, "복사가 한 번도 일어나지 않았다");
        Assert.Equal(plain.Store.StateHash(), snapped.Store.StateHash());
    }

    [Fact]
    [Trait("Category", "Load")]
    public void SnapshotCopy_AllocatesNothing()
    {
        Rig rig = NewRig();

        var port = new SnapshotPort
        {
            Buffer = new ShadowBuffer(Npcs, rig.Store.InventoryStride, rig.Zones.Capacity),
            Store = rig.Store,
            Zones = rig.Zones,
            Correlations = rig.Correlations,
        };

        rig.Loop.Snapshots = port;

        // JIT 승격을 측정 창 밖으로 뺀다.
        for (long tick = 1; tick <= 200; tick++)
        {
            port.Request();
            rig.Loop.RunTick(new Tick(tick));
            port.Release();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long from = 201;

        Assert.Equal(0, AllocationProbe.MinimumBytes(
            () =>
            {
                for (long tick = from; tick < from + 200; tick++)
                {
                    port.Request();
                    rig.Loop.RunTick(new Tick(tick));
                    port.Release();
                }

                from += 200;
            },
            warmup: 1,
            windows: 3));
    }

    private sealed record Rig(
        NpcServerLoop Loop,
        NpcStore Store,
        GameClock Clock,
        ZoneStateTable Zones,
        CorrelationTable Correlations);

    private static Rig NewRig()
    {
        NpcInstanceTable instances = NpcInstanceTable.Load(
            Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

        var store = new NpcStore();

        store.Allocate(Npcs, s_data.Items.MaxCode + 1);

        var link = new NullGameServerLink();
        var clock = new GameClock(s_data.Buckets, timeScale: 600);
        var correlations = new CorrelationTable(Npcs);
        var zones = new ZoneStateTable(s_data);
        var applier = new EventApplier(s_data, store, clock, correlations);
        var emitter = new CommandEmitter(s_data, new PoiBinder(s_data.Pois));
        var swapper = new PlanSwapper(store);
        var queue = new ReplanQueue(Npcs);

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

        for (int i = 0; i < Npcs; i++)
        {
            NpcInstanceDef def = instances[i % instances.Count];

            applier.Seed(i, def.Home, def.Zone, def.Archetype, def.Home, def.Workplace);

            // A-08 — 와이어의 NpcId 는 전역 인스턴스 id 다. 이 픽스처는 슬롯 i 에
            // id i+1 을 앉힌다 (0 은 "없음" 이라 쓸 수 없다).
            store.Bind(i, i + 1);
            store.StepStatus[i] = (byte)StepStatus.Ready;
            executor.AssignPlan(i, new PlanId(fallbackOf[def.Archetype.Value]));
            store.Lod[i] = (byte)(i % LodBandSet.BandCount);
        }

        while (bands.Rebalance() > 0)
        {
            // 초기 배치는 측정 밖에서 끝낸다.
        }

        var loop = new NpcServerLoop(
            link, clock, applier, interrupts, cognition, executor, swapper, bands, queue);

        return new Rig(loop, store, clock, zones, correlations);
    }
}
