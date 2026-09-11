using Npc.Contracts;
using Npc.MasterData;
using Npc.Sim;

namespace Npc.Tests.Sim;

/// <summary>docs/11 §6, §12. Transform 발행 · 욕구 진행 · 플레이어 봇.</summary>
public sealed class SimSubsystemTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static SimWorld SpawnedWorld(int capacity, SimOptions? options = null)
    {
        SimWorld world = SimWorldTests.NewWorld(capacity, options);

        for (int npc = 0; npc < capacity; npc++)
        {
            NpcCommand spawn = SimWorldTests.Command(NpcCommandKind.Spawn, npc);
            world.ApplyCommand(in spawn, new Tick(0));
        }

        SimWorldTests.Drain(world);
        return world;
    }

    // ---------------------------------------------------------------- T1-49

    /// <summary>T1-49 완료 조건 — 보고 있지 않은 NPC 에게는 Transform 을 발행하지 않는다.</summary>
    [Fact]
    public void Sim_NoTransformForFarLod()
    {
        SimWorld world = SpawnedWorld(10);
        var movement = new MovementSim(world);
        var transforms = new TransformEmitter(world, movement);
        world.Handler = movement.TryHandle;

        PoiId target = s_data.Pois.OfSubtype("mine")[0];

        for (int npc = 0; npc < 10; npc++)
        {
            var move = new NpcCommand
            {
                Kind = NpcCommandKind.MoveTo,
                Npc = new NpcId(npc + 1),   // A-08 — 전역 id
                IssuedAt = new Tick(0),
                Correlation = new CorrelationId(1),
                Priority = CommandPriority.Normal,
                TargetPoi = target,
            };

            world.ApplyCommand(in move, new Tick(0));
        }

        SimWorldTests.Drain(world);

        // 아무도 보고 있지 않다.
        for (long tick = 1; tick <= 100; tick++)
        {
            transforms.Tick(new Tick(tick));
        }

        Assert.Equal(0, transforms.Emitted);
        Assert.True(transforms.SkippedUnobserved > 0);

        // 두 마리만 관측 대상으로 바꾼다.
        world.ObservedByPlayer[0] = true;
        world.ObservedByPlayer[1] = true;

        for (long tick = 101; tick <= 200; tick++)
        {
            transforms.Tick(new Tick(tick));
        }

        Assert.True(transforms.Emitted > 0);
        Assert.All(
            SimWorldTests.Drain(world).Where(e => e.Kind == GameEventKind.NpcTransform),
            e => Assert.True(world.SlotOf(e.Npc.Value) is 0 or 1));
    }

    /// <summary>NPC 5,000 에서 이벤트/초 ≤ 3,000.</summary>
    [Fact]
    public void Sim_TransformRateStaysUnderBudget()
    {
        const int Npcs = 5_000;
        var options = new SimOptions(PlayerBots: 20, TransformPeriodTicks: 10);

        SimWorld world = SpawnedWorld(Npcs, options);
        var movement = new MovementSim(world);
        var transforms = new TransformEmitter(world, movement);
        var bots = new PlayerBots(world);
        world.Handler = movement.TryHandle;

        PoiId target = s_data.Pois.OfSubtype("mine")[0];

        for (int npc = 0; npc < Npcs; npc++)
        {
            var move = new NpcCommand
            {
                Kind = NpcCommandKind.MoveTo,
                Npc = new NpcId(npc + 1),   // A-08 — 전역 id
                IssuedAt = new Tick(0),
                Correlation = new CorrelationId(1),
                Priority = CommandPriority.Normal,
                TargetPoi = target,
            };

            world.ApplyCommand(in move, new Tick(0));
        }

        SimWorldTests.Drain(world);

        const int Ticks = 100;   // 10초
        for (long tick = 1; tick <= Ticks; tick++)
        {
            bots.Tick(new Tick(tick));
            transforms.Tick(new Tick(tick));
            SimWorldTests.Drain(world);
        }

        double perSecond = transforms.Emitted / (Ticks / (double)Tick.PerSecond);

        Assert.True(perSecond <= 3_000, $"Transform 이 초당 {perSecond:F0}건이다. 3,000 이하여야 한다.");
    }

    // ---------------------------------------------------------------- T1-50

    /// <summary>T1-50 완료 조건 — 게임 12시간이면 지쳐야 한다.</summary>
    [Fact]
    public void Sim_NeedsProgress()
    {
        SimWorld world = SpawnedWorld(20, new SimOptions(TimeScale: 600));
        var needs = new NeedsSim(world);

        // 게임 12시간 = 43,200 게임초 → 배속 600 에서 720틱.
        long ticks = 12 * 3_600 * Tick.PerSecond / world.Options.TimeScale;

        for (long tick = 1; tick <= ticks; tick++)
        {
            needs.Tick(new Tick(tick));
        }

        Assert.True(needs.VitalsEmitted > 0, "NpcVitalsChanged 가 하나도 안 나왔다.");

        for (int npc = 0; npc < 20; npc++)
        {
            Assert.True(
                needs.StaminaOf(npc) < NeedsSim.ExhaustedThreshold,
                $"NPC {npc} 의 스태미나가 {needs.StaminaOf(npc)} 다. 12시간 뒤엔 탈진해야 한다.");
        }
    }

    /// <summary>개체별 위상차가 있어 전원이 같은 틱에 이벤트를 쏟지 않는다.</summary>
    [Fact]
    public void Sim_NeedsAreStaggered()
    {
        SimWorld world = SpawnedWorld(200, new SimOptions(TimeScale: 600));
        var needs = new NeedsSim(world);

        int peak = 0;

        for (long tick = 1; tick <= needs.PeriodTicks; tick++)
        {
            long before = needs.VitalsEmitted;
            needs.Tick(new Tick(tick));
            peak = Math.Max(peak, (int)(needs.VitalsEmitted - before));
        }

        Assert.True(peak < 200, $"한 틱에 {peak}건이 몰렸다. 위상차가 안 먹었다.");
    }

    [Fact]
    public void Sim_NeedsCanBeRestored()
    {
        SimWorld world = SpawnedWorld(2, new SimOptions(TimeScale: 600));
        var needs = new NeedsSim(world);

        for (long tick = 1; tick <= 500; tick++)
        {
            needs.Tick(new Tick(tick));
        }

        short before = needs.StaminaOf(0);
        needs.Restore(0, hp: 50, stamina: 100, new Tick(501));

        Assert.True(needs.StaminaOf(0) > before);
        Assert.Equal(100, needs.StaminaOf(0));
    }

    // ---------------------------------------------------------------- T1-51

    /// <summary>T1-51 완료 조건 — 봇은 결정론적이고, 20명이면 관측 대상 NPC 가 생긴다.</summary>
    [Fact]
    public void Sim_PlayerBotsAreDeterministic()
    {
        var first = new List<(int Npc, byte Code)>();

        for (int run = 0; run < 2; run++)
        {
            SimWorld world = SpawnedWorld(300, new SimOptions(PlayerBots: 20));
            var bots = new PlayerBots(world);
            var seen = new List<(int, byte)>();

            Assert.Equal(20, bots.Count);

            for (long tick = 1; tick <= 200; tick++)
            {
                bots.Tick(new Tick(tick));

                foreach (GameEvent ev in SimWorldTests.Drain(world))
                {
                    if (ev.Kind == GameEventKind.PlayerProximity)
                    {
                        seen.Add((ev.Npc.Value, ev.Code));
                    }
                }
            }

            Assert.True(seen.Count > 0, "PlayerProximity 가 하나도 안 나왔다.");

            if (run == 0)
            {
                first = seen;
            }
            else
            {
                Assert.Equal(first, seen);
            }
        }
    }

    /// <summary>봇 20명이면 LOD 0/1 이 될 NPC 가 실제로 생긴다.</summary>
    [Fact]
    public void Sim_PlayerBotsProduceObservedNpcs()
    {
        SimWorld world = SpawnedWorld(500, new SimOptions(PlayerBots: 20));
        var bots = new PlayerBots(world);

        int peak = 0;

        for (long tick = 1; tick <= 400; tick++)
        {
            bots.Tick(new Tick(tick));
            SimWorldTests.Drain(world);
            peak = Math.Max(peak, bots.ObservedNpcs);
        }

        Assert.True(peak > 0, "봇 20명인데 관측되는 NPC 가 하나도 없다.");
    }

    /// <summary>봇이 없으면 근접 이벤트도 없다.</summary>
    [Fact]
    public void Sim_NoBotsNoProximityEvents()
    {
        SimWorld world = SpawnedWorld(50);
        var bots = new PlayerBots(world);

        for (long tick = 1; tick <= 100; tick++)
        {
            bots.Tick(new Tick(tick));
        }

        Assert.Equal(0, bots.Count);
        Assert.Equal(0, bots.ProximityEvents);
    }
}
