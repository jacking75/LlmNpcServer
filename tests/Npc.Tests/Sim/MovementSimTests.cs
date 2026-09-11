using Npc.Contracts;
using Npc.MasterData;
using Npc.Sim;

namespace Npc.Tests.Sim;

/// <summary>docs/02 §5. 거리 ÷ 속도 = 소요 틱. 패스파인딩은 하지 않는다.</summary>
public sealed class MovementSimTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private sealed record Rig(SimWorld World, MovementSim Movement);

    private static Rig NewRig(int timeScale = 600)
    {
        SimWorld world = SimWorldTests.NewWorld(4, new SimOptions(TimeScale: timeScale));
        var movement = new MovementSim(world);

        world.Handler = movement.TryHandle;
        return new Rig(world, movement);
    }

    private static NpcCommand Move(int npc, PoiId target, MoveSpeed speed = MoveSpeed.Walk) => new()
    {
        Kind = NpcCommandKind.MoveTo,
        // A-08 — 와이어의 NpcId 는 전역 id 다. 이 픽스처는 슬롯 i 에 id i+1 을 앉힌다.
        Npc = new NpcId(npc + 1),
        IssuedAt = new Tick(0),
        Correlation = new CorrelationId(7),
        Priority = CommandPriority.Normal,
        TargetPoi = target,
        Flags = (byte)speed,
    };

    /// <summary>T1-47 완료 조건 — 도착 시각이 거리와 맞는다 (오차 ±1틱).</summary>
    [Fact]
    public void Sim_ArrivalTimingMatchesDistance()
    {
        Rig r = NewRig();

        PoiId from = r.World.PoiOf(0);
        PoiId to = s_data.Pois.OfSubtype("smithy")[0];
        float distance = s_data.Pois.Distance(from, to);

        Assert.True(distance > 0);

        NpcCommand move = Move(0, to);
        r.World.ApplyCommand(in move, new Tick(0));

        long expected = r.Movement.TravelTicks(distance, MoveSpeed.Walk);
        long? actual = null;

        for (long tick = 1; tick <= expected + 5; tick++)
        {
            r.Movement.Tick(new Tick(tick));

            foreach (GameEvent ev in SimWorldTests.Drain(r.World))
            {
                if (ev.Kind == GameEventKind.NpcArrived)
                {
                    actual = ev.OccurredAt.Value;
                    Assert.Equal(to, ev.Poi);
                    Assert.Equal(new CorrelationId(7), ev.Correlation);
                }
            }

            if (actual is not null)
            {
                break;
            }
        }

        Assert.NotNull(actual);
        Assert.InRange(actual!.Value, expected - 1, expected + 1);
        Assert.Equal(to, r.World.PoiOf(0));
    }

    /// <summary>뛰면 더 빨리 도착한다.</summary>
    [Fact]
    public void Sim_RunningIsFasterThanWalking()
    {
        Rig r = NewRig(timeScale: 1);

        PoiId from = r.World.PoiOf(0);
        PoiId to = s_data.Pois.OfSubtype("mine")[0];
        float distance = s_data.Pois.Distance(from, to);

        long walk = r.Movement.TravelTicks(distance, MoveSpeed.Walk);
        long run = r.Movement.TravelTicks(distance, MoveSpeed.Run);

        Assert.True(run < walk, $"뛰기 {run}틱이 걷기 {walk}틱보다 빠르지 않다.");
    }

    /// <summary>배속이 오르면 같은 거리를 더 적은 틱에 간다.</summary>
    [Fact]
    public void Sim_TravelTicksFollowTimeScale()
    {
        Rig slow = NewRig(timeScale: 60);
        Rig fast = NewRig(timeScale: 600);

        Assert.Equal(10 * fast.Movement.TravelTicks(1_000, MoveSpeed.Walk),
            slow.Movement.TravelTicks(1_000, MoveSpeed.Walk));
    }

    [Fact]
    public void Sim_ArrivalIsAtLeastOneTick()
    {
        Rig r = NewRig();

        Assert.True(r.Movement.TravelTicks(0.1, MoveSpeed.Run) >= 1);
    }

    [Fact]
    public void Sim_MoveWithoutPoiCompletesImmediately()
    {
        Rig r = NewRig();

        // Wander 는 Zone 만 싣고 TargetPoi 가 없다.
        NpcCommand wander = Move(0, default) with { Zone = new ZoneId(1), Amount = 50 };
        r.World.ApplyCommand(in wander, new Tick(1));

        GameEvent ev = Assert.Single(SimWorldTests.Drain(r.World));

        Assert.Equal(GameEventKind.NpcActionCompleted, ev.Kind);
        Assert.False(r.Movement.IsMoving(0));
    }

    [Fact]
    public void Sim_TracksMovingState()
    {
        Rig r = NewRig();

        PoiId to = s_data.Pois.OfSubtype("mine")[0];
        NpcCommand move = Move(0, to);
        r.World.ApplyCommand(in move, new Tick(1));

        Assert.True(r.Movement.IsMoving(0));
        Assert.Equal(1, r.Movement.Moving);
        Assert.Equal(r.World.PoiOf(0), r.Movement.OriginOf(0));
        Assert.Equal(to, r.Movement.TargetOf(0));

        r.Movement.Cancel(0);
        Assert.False(r.Movement.IsMoving(0));
    }

    /// <summary>여러 NPC 가 동시에 움직여도 각자 자기 시각에 도착한다.</summary>
    [Fact]
    public void Sim_MovesEveryoneIndependently()
    {
        Rig r = NewRig();

        PoiId near = s_data.Pois.OfSubtype("house")[1];
        PoiId far = s_data.Pois.OfSubtype("mine")[0];

        NpcCommand a = Move(0, near);
        NpcCommand b = Move(1, far);
        r.World.ApplyCommand(in a, new Tick(0));
        r.World.ApplyCommand(in b, new Tick(0));

        var arrived = new Dictionary<int, long>();

        for (long tick = 1; tick <= 5_000 && arrived.Count < 2; tick++)
        {
            r.Movement.Tick(new Tick(tick));

            foreach (GameEvent ev in SimWorldTests.Drain(r.World))
            {
                if (ev.Kind == GameEventKind.NpcArrived)
                {
                    // A-08 — 이벤트의 NpcId 는 전역 id 다. 슬롯으로 되돌려 센다.
                    arrived[r.World.SlotOf(ev.Npc.Value)] = ev.OccurredAt.Value;
                }
            }
        }

        Assert.Equal(2, arrived.Count);
        Assert.True(arrived[0] < arrived[1], "가까운 쪽이 먼저 도착해야 한다.");
    }
}
