using Npc.Contracts;
using Npc.MasterData;
using Npc.TestGameServer;
using Npc.TestGameServer.Link;

namespace Npc.Tests.TestBed;

/// <summary>T6-15 — 게임서버 대역의 월드 조립과 틱 순서. docs/20 §7.2 · §10.2.</summary>
public sealed class GameWorldTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static readonly NpcInstanceTable s_instances = NpcInstanceTable.Load(
        Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

    /// <summary>
    /// <c>Npc.Host</c> 의 <c>SimDriver.Tick</c> 순서 그대로다 (docs/20 §7.2).
    ///
    /// <b>이 배열을 실측에 맞춰 고치지 않는다.</b> 순서가 어긋나면 같은 시나리오가 다르게 흐르고,
    /// 그러면 루프백과 소켓을 비교할 수 없게 된다 — 그 비교가 P6 합격 기준 2 다.
    /// </summary>
    private static readonly TickStage[] SimDriverOrder =
    [
        TickStage.Commands,      // 1. CommandInbox 배수 → SimWorld.ApplyCommand
        TickStage.Scenario,      // 2. ScenarioRunner.Tick
        TickStage.Players,       // 3. PlayerRegistry.Tick  (SimDriver 의 PlayerBots 자리)
        TickStage.Movement,      // 4. MovementSim.Tick
        TickStage.Interaction,   // 5. InteractionSim.Tick
        TickStage.Transforms,    // 6. TransformEmitter.Tick
        TickStage.Needs,         // 7. NeedsSim.Tick
        TickStage.World,         // 8. SimWorld.Tick — TickSync 발행
    ];

    private static GameWorld Create(int npcs = 32, GameServerOptions? options = null)
    {
        options ??= new GameServerOptions();
        NpcRoster roster = NpcRoster.Select(s_instances, npcs);

        return GameWorld.Create(options with { Npcs = npcs }, s_data, roster, s_instances);
    }

    /// <summary>완료 조건 — §7.2 순서대로 호출된다. 호출 기록으로 확인한다.</summary>
    [Fact]
    public async Task GameWorld_TickOrderMatchesSimDriver()
    {
        await using GameWorld world = Create();

        var stages = new List<TickStage>();
        world.StageObserver = stages.Add;

        world.Tick(new Tick(1));

        Assert.Equal(SimDriverOrder, stages);

        // 두 번째 틱도 같은 순서다 — 첫 틱만 맞고 이후가 갈리는 것을 잡는다.
        stages.Clear();
        world.Tick(new Tick(2));

        Assert.Equal(SimDriverOrder, stages);
    }

    /// <summary>
    /// 3단계(<c>PlayerRegistry</c>)가 비어 있어도 단계 알림은 나가고, 채워지면 그 자리에서 불린다.
    /// 자리 채움 여부에 순서 단언이 흔들리면 T6-19 가 들어올 때 이 테스트가 무의미해진다.
    /// </summary>
    [Fact]
    public async Task GameWorld_CallsPlayersInThirdStage()
    {
        await using GameWorld world = Create();

        var stages = new List<TickStage>();
        long playersAt = -1;

        world.StageObserver = stages.Add;
        world.Players = _ => playersAt = stages.Count;

        world.Tick(new Tick(1));

        // Players 알림 직후에 불렸다 = 3단계다.
        Assert.Equal(3, playersAt);
        Assert.Equal(TickStage.Players, stages[2]);
    }

    /// <summary>완료 조건 — 매 틱 <c>TickSync</c> 가 하나씩 나간다.</summary>
    [Fact]
    public async Task GameWorld_EmitsTickSyncEveryTick()
    {
        await using GameWorld world = Create();

        // 스폰 이벤트를 먼저 걷어낸다. 여기서 볼 것은 틱당 TickSync 수다.
        int spawns = Drain(world, out int syncsBefore);

        Assert.Equal(world.Roster.Count, spawns);
        Assert.Equal(0, syncsBefore);

        const int Ticks = 25;

        for (int t = 1; t <= Ticks; t++)
        {
            world.Tick(new Tick(t));
        }

        _ = Drain(world, out int syncs);

        Assert.Equal(Ticks, syncs);
        Assert.Equal(Ticks, world.TicksProcessed);
    }

    /// <summary>완료 조건 — 기동 시 로스터 전원이 세계에 올라간다.</summary>
    [Fact]
    public async Task GameWorld_SpawnsRosterAtStart()
    {
        await using GameWorld world = Create(64);

        Assert.Equal(64, world.Roster.Count);

        for (int i = 0; i < world.Roster.Count; i++)
        {
            Assert.True(world.World.IsSpawned(i), $"npc {i} 가 스폰되지 않았다.");

            // 첨자 i 는 로스터 i 번이다. 이것이 어긋나면 대장장이에게 밭을 갈라고 명령하게 된다.
            Assert.Equal(world.Roster.Npcs[i].Archetype, world.World.ArchetypeOf(i));
            Assert.Equal(world.Roster.Npcs[i].Home, world.World.PoiOf(i));
        }

        int spawns = Drain(world, out _);

        Assert.Equal(64, spawns);
    }

    /// <summary>
    /// <b><see cref="Npc.Sim.PlayerBots"/> 를 쓰지 않는다</b> (docs/20 §7.3).
    /// 봇이 돌면 <c>ObservedByPlayer</c> 가 켜지므로, 아무도 안 켜는 것으로 확인한다 —
    /// <c>PlayerRegistry</c>(T6-19)가 그 배열의 유일한 주인이어야 한다.
    /// </summary>
    [Fact]
    public async Task GameWorld_DoesNotRunPlayerBots()
    {
        await using GameWorld world = Create();

        Assert.Equal(0, world.World.Options.PlayerBots);

        for (int t = 1; t <= 50; t++)
        {
            world.Tick(new Tick(t));
        }

        Assert.DoesNotContain(true, world.World.ObservedByPlayer);
    }

    /// <summary>받은 명령은 1단계에서 적용된다. 링에 넣은 순서 그대로다.</summary>
    [Fact]
    public async Task GameWorld_AppliesInboxCommandsInOrder()
    {
        await using GameWorld world = Create();

        _ = Drain(world, out _);

        PoiId target = world.Roster.Npcs[1].Home;

        Assert.True(world.Inbox.TryEnqueue(new NpcCommand
        {
            Kind = NpcCommandKind.MoveTo,
            Npc = world.World.NpcIdOf(0),   // A-08 — 전역 id
            IssuedAt = new Tick(1),
            Correlation = new CorrelationId(1),
            Priority = CommandPriority.Normal,
            TargetPoi = target,
        }));

        Assert.Equal(1, world.Inbox.Pending);

        world.Tick(new Tick(1));

        Assert.Equal(0, world.Inbox.Pending);
        Assert.Equal(1, world.CommandsApplied);
        Assert.Equal(target, world.Movement.TargetOf(0));
    }

    /// <summary>링이 차면 버리고 센다. 예외를 던지지 않는다 (docs/02 §1).</summary>
    [Fact]
    public void Inbox_DropsWhenFull()
    {
        var inbox = new CommandInbox();

        for (int i = 0; i < CommandInbox.Capacity; i++)
        {
            Assert.True(inbox.TryEnqueue(Noop(i)));
        }

        Assert.False(inbox.TryEnqueue(Noop(0)));
        Assert.Equal(1, inbox.Dropped);
        Assert.Equal(CommandInbox.Capacity, inbox.Accepted);
    }

    /// <summary>
    /// 페이싱은 누적 오차가 없어야 한다 (docs/20 §7.2). 매 틱 100ms 를 더하면
    /// 처리 시간이 그대로 쌓여 게임 시각이 뒤로 밀린다.
    /// </summary>
    [Fact]
    public void GameWorld_DueMillisHasNoDrift()
    {
        Assert.Equal(0, GameWorld.DueMillis(0));
        Assert.Equal(100, GameWorld.DueMillis(1));
        Assert.Equal(600_000, GameWorld.DueMillis(6_000));
    }

    /// <summary>링 채우기용 최소 명령. 세계에 적용하지 않으므로 내용은 중요하지 않다.</summary>
    private static NpcCommand Noop(int npc) => new()
    {
        Kind = NpcCommandKind.Stop,
        Npc = new NpcId(npc),   // 부르는 쪽이 이미 전역 id 를 준다
        IssuedAt = default,
        Correlation = default,
        Priority = CommandPriority.Normal,
    };

    /// <summary>이벤트 채널을 비우고 스폰 수를 센다.</summary>
    private static int Drain(GameWorld world, out int tickSyncs)
    {
        int spawns = 0;
        tickSyncs = 0;

        while (world.World.Events.TryRead(out GameEvent ev))
        {
            switch (ev.Kind)
            {
                case GameEventKind.NpcSpawned:
                    spawns++;
                    break;

                case GameEventKind.TickSync:
                    tickSyncs++;
                    break;

                default:
                    break;
            }
        }

        return spawns;
    }
}
