using Npc.Contracts;
using Npc.MasterData;
using Npc.Sim;

namespace Npc.Tests.Sim;

/// <summary>docs/02 §5 · docs/11 §6. 게임서버 대역의 골격.</summary>
public sealed class SimWorldTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    internal static SimWorld NewWorld(int capacity = 4, SimOptions? options = null)
    {
        var world = new SimWorld(s_data, capacity, options);

        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));
        PoiId home = s_data.Pois.OfSubtype("house")[0];

        for (int npc = 0; npc < capacity; npc++)
        {
            world.Place(npc, smith.Code, home);
        }

        return world;
    }

    internal static NpcCommand Command(NpcCommandKind kind, int npc, uint correlation = 1) => new()
    {
        Kind = kind,
        Npc = new NpcId(npc),
        IssuedAt = new Tick(1),
        Correlation = new CorrelationId(correlation),
        Priority = CommandPriority.Normal,
    };

    internal static List<GameEvent> Drain(SimWorld world)
    {
        var events = new List<GameEvent>();

        while (world.Events.TryRead(out GameEvent ev))
        {
            events.Add(ev);
        }

        return events;
    }

    /// <summary>T1-46 완료 조건 — Spawn → NpcSpawned 왕복.</summary>
    [Fact]
    public void Sim_SpawnRoundTrips()
    {
        SimWorld world = NewWorld();

        NpcCommand spawn = Command(NpcCommandKind.Spawn, 2, correlation: 42);
        world.ApplyCommand(in spawn, new Tick(10));

        GameEvent ev = Assert.Single(Drain(world));

        Assert.Equal(GameEventKind.NpcSpawned, ev.Kind);
        Assert.Equal(new NpcId(2), ev.Npc);
        Assert.Equal(new CorrelationId(42), ev.Correlation);
        Assert.Equal(10, ev.OccurredAt.Value);
        Assert.Equal(world.PoiOf(2), ev.Poi);
        Assert.True(world.IsSpawned(2));
        Assert.Equal(1, ev.Sequence);
    }

    /// <summary>N6 — 모든 이벤트에 순증 시퀀스가 붙는다.</summary>
    [Fact]
    public void Sim_SequenceIsMonotonic()
    {
        SimWorld world = NewWorld();

        for (int npc = 0; npc < 4; npc++)
        {
            NpcCommand spawn = Command(NpcCommandKind.Spawn, npc);
            world.ApplyCommand(in spawn, new Tick(1));
        }

        world.Tick(new Tick(2));

        List<GameEvent> events = Drain(world);

        Assert.Equal(5, events.Count);

        for (int i = 0; i < events.Count; i++)
        {
            Assert.Equal(i + 1, events[i].Sequence);
        }

        Assert.Equal(5, world.Sequence);
    }

    [Fact]
    public void Sim_DespawnStopsTheNpc()
    {
        SimWorld world = NewWorld();

        NpcCommand spawn = Command(NpcCommandKind.Spawn, 0);
        world.ApplyCommand(in spawn, new Tick(1));
        Assert.True(world.IsSpawned(0));

        NpcCommand despawn = Command(NpcCommandKind.Despawn, 0);
        world.ApplyCommand(in despawn, new Tick(2));

        Assert.False(world.IsSpawned(0));
        Assert.Contains(Drain(world), e => e.Kind == GameEventKind.NpcDespawned);
    }

    /// <summary>연출 명령은 즉시 완료로 답한다.</summary>
    [Fact]
    public void Sim_CosmeticCommandsCompleteImmediately()
    {
        SimWorld world = NewWorld();

        foreach (NpcCommandKind kind in new[]
        {
            NpcCommandKind.SetVisualState, NpcCommandKind.PlayAnimation,
            NpcCommandKind.Speak, NpcCommandKind.Stop, NpcCommandKind.FaceTo,
        })
        {
            NpcCommand command = Command(kind, 0);
            world.ApplyCommand(in command, new Tick(1));
        }

        List<GameEvent> events = Drain(world);

        Assert.Equal(5, events.Count);
        Assert.All(events, e => Assert.Equal(GameEventKind.NpcActionCompleted, e.Kind));
    }

    /// <summary>Tick 은 TickSync 를 낸다 — NPC 서버의 시간 기준은 이것뿐이다 (docs/02 §3.3).</summary>
    [Fact]
    public void Sim_TickEmitsTickSync()
    {
        SimWorld world = NewWorld();

        world.Tick(new Tick(7));

        GameEvent ev = Assert.Single(Drain(world));

        Assert.Equal(GameEventKind.TickSync, ev.Kind);
        Assert.Equal(7, ev.OccurredAt.Value);
    }

    /// <summary>아직 붙지 않은 하위 시뮬의 명령은 거절로 답한다 — 조용히 삼키지 않는다.</summary>
    [Fact]
    public void Sim_UnhandledCommandIsRejectedNotIgnored()
    {
        SimWorld world = NewWorld();

        NpcCommand move = Command(NpcCommandKind.MoveTo, 0);
        world.ApplyCommand(in move, new Tick(1));

        GameEvent ev = Assert.Single(Drain(world));

        Assert.Equal(GameEventKind.NpcActionFailed, ev.Kind);
        Assert.Equal((byte)ActionFailReason.Rejected, ev.Code);
    }

    [Fact]
    public void Sim_HandlerInterceptsCommands()
    {
        SimWorld world = NewWorld();
        int seen = 0;

        world.Handler = (in NpcCommand command, Tick now) =>
        {
            seen++;
            return true;
        };

        NpcCommand move = Command(NpcCommandKind.MoveTo, 0);
        world.ApplyCommand(in move, new Tick(1));

        Assert.Equal(1, seen);
        Assert.Empty(Drain(world));
    }

    [Fact]
    public void Sim_IgnoresOutOfRangeNpc()
    {
        SimWorld world = NewWorld(2);

        NpcCommand spawn = Command(NpcCommandKind.Spawn, 99);
        world.ApplyCommand(in spawn, new Tick(1));

        Assert.Empty(Drain(world));
    }
}
