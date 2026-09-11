using System.Collections.Immutable;
using Npc.Contracts;
using Npc.MasterData;
using Npc.TestGameServer;
using Npc.TestGameServer.World;

namespace Npc.Tests.TestBed;

/// <summary>
/// T6-19 — <see cref="PlayerRegistry"/> 의 이동 · 존 판정 · 근접. docs/20 §7.3.
///
/// <para>
/// <b>여기서 보는 것은 셋이다.</b> 속도가 게임 시간을 따라가는가(안 그러면 <c>--time-scale 60</c>
/// 에서 플레이어가 멈춰 보인다), 히스테리시스가 이벤트 반복을 막는가(안 그러면 재계획 큐가 폭주한다),
/// 존 필터가 실제로 걸러 내는가(안 그러면 5,000 규모에서 틱 예산을 먹는다).
/// </para>
/// </summary>
[Trait("Category", "TestBed")]
public sealed class PlayerRegistryTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static readonly NpcInstanceTable s_instances = NpcInstanceTable.Load(
        Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

    /// <summary>
    /// 완료 조건 — 속도가 <c>TimeScale</c> 에 비례한다.
    ///
    /// 플레이어만 실시간으로 걸으면 <c>--time-scale 60</c> 화면에서 NPC 는 뛰어다니는데
    /// 플레이어는 제자리인 것처럼 보인다 (docs/20 §7.3).
    /// </summary>
    [Fact]
    public async Task Player_MoveSpeedScalesWithTimeScale()
    {
        const double Walk = 2.5;

        await using GameWorld slow = Create(timeScale: 1);
        await using GameWorld fast = Create(timeScale: 60);

        float one = StepDistance(slow, timeScale: 1, run: false);
        float sixty = StepDistance(fast, timeScale: 60, run: false);

        // speed × TimeScale / 10 m/틱 (docs/20 §7.3).
        Assert.Equal(Walk * 1 / GameWorld.TickRate, one, 3);
        Assert.Equal(Walk * 60 / GameWorld.TickRate, sixty, 2);
        Assert.Equal(60.0, sixty / one, 2);

        // 달리기는 두 배다 — 걷기 2.5 · 뛰기 5.0 게임m/게임초.
        await using GameWorld running = Create(timeScale: 60);

        Assert.Equal(sixty * 2, StepDistance(running, timeScale: 60, run: true), 2);
    }

    /// <summary>
    /// 완료 조건 — 경계에서 왕복해도 <c>Enter</c>/<c>Leave</c> 가 반복되지 않는다.
    ///
    /// 200m 로 들어왔다가 히스테리시스 대역(200~220m)을 몇 번 오가도 이벤트는 두 번뿐이다.
    /// 반복되면 그 하나하나가 NPC 서버의 재계획 큐에 들어가 큐가 폭주한다.
    /// </summary>
    [Fact]
    public async Task Player_ProximityEntersAndLeavesOnce()
    {
        await using GameWorld world = Create(timeScale: 60, npcs: 300);

        var registry = new PlayerRegistry(world, s_data, Options(60));

        world.Players = registry.Tick;

        int npc = FirstNpcInSpawnZone(world);
        WorldPos target = world.World.PositionOf(npc);

        var events = new List<(ProximityChange Change, int Distance)>();
        PlayerId player = registry.Add();

        Assert.NotEqual(0, player.Value);

        // 밖 → 안 → 대역 안에서 왕복 → 밖 → 다시 대역. 대역 안 왕복은 아무 일도 없어야 한다.
        float[] path = [300f, 150f, 210f, 150f, 205f, 230f, 210f];

        foreach (float distance in path)
        {
            registry.Teleport(player, Offset(target, distance));

            Drain(world);
            world.Tick(new Tick(PlayerRegistry.ProximityPeriodTicks));
            Collect(world, npc, events);
        }

        Assert.Equal(2, events.Count);
        Assert.Equal(ProximityChange.Enter, events[0].Change);
        Assert.Equal(ProximityChange.Leave, events[1].Change);

        // 거리가 실려 나간다 — NPC 서버의 우선순위 큐 w1 이 이 값을 쓴다.
        Assert.InRange(events[0].Distance, 145, 155);
        Assert.InRange(events[1].Distance, 225, 235);

        Assert.False(world.World.ObservedByPlayer[npc]);
    }

    /// <summary>
    /// 완료 조건 — 플레이어가 있는 존 + 인접 존만 거리를 잰다 (docs/20 §7.3).
    ///
    /// 전수 검사로 돌아가면 5,000 규모에서 그 차이가 곧 틱 예산이다.
    /// </summary>
    [Fact]
    public async Task Player_OnlyScansOwnAndAdjacentZones()
    {
        await using GameWorld world = Create(timeScale: 60, npcs: 500);

        var registry = new PlayerRegistry(world, s_data, Options(60));

        world.Players = registry.Tick;

        PlayerId player = registry.Add();

        // 근접 판정 한 번. 존 판정도 이 틱에서 같이 돈다.
        world.Tick(new Tick(PlayerRegistry.ProximityPeriodTicks));

        ZoneDef zone = s_data.Zones[registry.ZoneOf(player)];
        ImmutableArray<ZoneId> adjacent = zone.Adjacent;

        int expected = 0;

        for (int npc = 0; npc < world.World.Capacity; npc++)
        {
            if (!world.World.IsSpawned(npc))
            {
                continue;
            }

            ZoneId at = world.World.ZoneOf(npc);

            if (at == zone.Code || adjacent.Contains(at))
            {
                expected++;
            }
        }

        // 표본이 전체와 같으면 이 테스트는 공허하다 — 걸러 낼 것이 있어야 걸러진 것을 본다.
        Assert.True(expected > 0, "후보 존에 NPC 가 없다.");
        Assert.True(expected < world.World.Capacity, "존 필터가 걸러 낼 NPC 가 없다.");

        Assert.Equal(expected, registry.NpcsScanned);
    }

    /// <summary>
    /// T6-20 완료 조건 — <c>Interact</c> 는 30m 안에서만 듣는다 (docs/20 §7.3).
    ///
    /// 클라이언트가 화면 밖 NPC 를 찍어서 인터럽트를 걸 수 있으면
    /// "플레이어 근접이 인지 LOD 를 바꾼다" 는 검증이 무의미해진다.
    /// </summary>
    [Fact]
    public async Task Player_InteractRequiresRange()
    {
        await using GameWorld world = Create(timeScale: 60, npcs: 300);

        var registry = new PlayerRegistry(world, s_data, Options(60));

        int npc = FirstNpcInSpawnZone(world);
        WorldPos target = world.World.PositionOf(npc);
        PlayerId player = registry.Add();
        var now = new Tick(1);

        // 31m — 1m 차이로 밖이다. 이벤트도 없고 예외도 없다.
        registry.Teleport(player, Offset(target, PlayerRegistry.InteractRange + 1f));
        Drain(world);

        Assert.False(registry.TryInteract(player, new NpcId(npc), now));
        Assert.Equal(0, Count(world, GameEventKind.PlayerInteracted));
        Assert.Equal(1, registry.InteractsOutOfRange);
        Assert.Equal(0, registry.Interacts);

        // 29m — 안이다.
        registry.Teleport(player, Offset(target, PlayerRegistry.InteractRange - 1f));
        Drain(world);

        Assert.True(registry.TryInteract(player, new NpcId(npc), now));
        Assert.Equal(1, Count(world, GameEventKind.PlayerInteracted));
        Assert.Equal(1, registry.Interacts);
    }

    /// <summary>
    /// T6-20 완료 조건 — 공격이 <see cref="Npc.Sim.NeedsSim"/> 의 HP 를 깎는다.
    ///
    /// HP 를 <c>PlayerRegistry</c> 가 따로 들면 <c>NpcVitalsChanged</c> 와 어긋난
    /// 두 벌의 진실이 생긴다. 이벤트 순서도 같이 못 박는다.
    /// </summary>
    [Fact]
    public async Task Player_AttackReducesHp()
    {
        const int Damage = 17;

        await using GameWorld world = Create(timeScale: 60, npcs: 300);

        var registry = new PlayerRegistry(world, s_data, Options(60));

        int npc = FirstNpcInSpawnZone(world);
        WorldPos target = world.World.PositionOf(npc);
        PlayerId player = registry.Add();
        var now = new Tick(1);

        short before = world.Needs.HpOf(npc);

        // 사거리 밖에서는 HP 가 그대로다.
        registry.Teleport(player, Offset(target, PlayerRegistry.InteractRange + 1f));
        Drain(world);

        Assert.False(registry.TryAttack(player, new NpcId(npc), Damage, now));
        Assert.Equal(before, world.Needs.HpOf(npc));
        Assert.Equal(1, registry.AttacksOutOfRange);

        // 안에서는 깎인다.
        registry.Teleport(player, Offset(target, 10f));
        Drain(world);

        Assert.True(registry.TryAttack(player, new NpcId(npc), Damage, now));
        Assert.Equal(before - Damage, world.Needs.HpOf(npc));

        // CombatStarted → DamageTaken → NpcVitalsChanged 순이다.
        var kinds = new List<GameEventKind>();

        while (world.World.Events.TryRead(out GameEvent ev))
        {
            if (ev.Npc.Value == npc)
            {
                kinds.Add(ev.Kind);
            }
        }

        Assert.Equal(
            [GameEventKind.CombatStarted, GameEventKind.DamageTaken, GameEventKind.NpcVitalsChanged],
            kinds);
    }

    /// <summary>
    /// T6-20 완료 조건 — 같은 시드면 같은 위치열이다 (CLAUDE.md §2.3).
    ///
    /// 데모를 두 번 돌려 비교할 수 있어야 하므로 봇은 <c>Random</c> 을 쓰지 않는다.
    /// 대조군(다른 시드)이 갈리는 것까지 봐야 이 단언이 공허하지 않다.
    /// </summary>
    [Fact]
    public async Task Bots_AreDeterministic()
    {
        const int Bots = 3;
        const int Ticks = 60;

        await using GameWorld a = Create(timeScale: 60, npcs: 64);
        await using GameWorld b = Create(timeScale: 60, npcs: 64);
        await using GameWorld c = Create(timeScale: 60, npcs: 64);

        List<WorldPos> first = RunBots(a, seed: 20260725, Bots, Ticks);
        List<WorldPos> again = RunBots(b, seed: 20260725, Bots, Ticks);
        List<WorldPos> other = RunBots(c, seed: 99, Bots, Ticks);

        Assert.Equal(Bots * Ticks, first.Count);
        Assert.Equal(first, again);
        Assert.NotEqual(first, other);

        // 실제로 움직였는지도 본다 — 전부 제자리면 위 두 단언은 공허하다.
        Assert.True(
            first.Distinct().Count() > Bots,
            "봇이 움직이지 않았다. 같은 자리만 비교하면 결정론을 본 것이 아니다.");
    }

    // ---------------------------------------------------------------- 도우미

    private static GameServerOptions Options(int timeScale) =>
        new() { TimeScale = timeScale, LinkPort = 0, ClientPort = 0 };

    /// <summary>봇 <paramref name="bots"/> 마리를 <paramref name="ticks"/> 틱 돌린 위치열.</summary>
    private static List<WorldPos> RunBots(GameWorld world, int seed, int bots, int ticks)
    {
        GameServerOptions options = Options(world.World.Options.TimeScale) with
        {
            Bots = bots,
            Seed = seed,
        };

        var registry = new PlayerRegistry(world, s_data, options);

        Assert.Equal(bots, registry.Bots);

        var trail = new List<WorldPos>(bots * ticks);

        for (int t = 1; t <= ticks; t++)
        {
            registry.Tick(new Tick(t));

            for (int bot = 1; bot <= bots; bot++)
            {
                var id = new PlayerId(bot);

                Assert.True(registry.IsBot(id));

                trail.Add(registry.PositionOf(id));
            }
        }

        return trail;
    }

    private static int Count(GameWorld world, GameEventKind kind)
    {
        int count = 0;

        while (world.World.Events.TryRead(out GameEvent ev))
        {
            if (ev.Kind == kind)
            {
                count++;
            }
        }

        return count;
    }

    private static GameWorld Create(int timeScale, int npcs = 64)
    {
        NpcRoster roster = NpcRoster.Select(s_instances, npcs);

        return GameWorld.Create(Options(timeScale) with { Npcs = npcs }, s_data, roster, s_instances);
    }

    /// <summary>한 틱 걸었을 때의 이동 거리.</summary>
    private static float StepDistance(GameWorld world, int timeScale, bool run)
    {
        var registry = new PlayerRegistry(world, s_data, Options(timeScale));

        PlayerId player = registry.Add();
        WorldPos from = registry.PositionOf(player);

        registry.SetInput(player, 1, 0, run);

        // 1틱. 근접 판정이 끼지 않는 틱을 고른다 — 이 회차가 재는 것은 이동뿐이다.
        registry.Tick(new Tick(1));

        WorldPos to = registry.PositionOf(player);

        return MathF.Sqrt(
            ((to.X - from.X) * (to.X - from.X)) + ((to.Z - from.Z) * (to.Z - from.Z)));
    }

    /// <summary>스폰 존(<c>town_center</c>)에 있는 첫 NPC. 근접 회차의 대상이다.</summary>
    private static int FirstNpcInSpawnZone(GameWorld world)
    {
        Assert.True(s_data.Zones.TryGet(PlayerRegistry.SpawnZoneId, out ZoneDef spawn));

        for (int npc = 0; npc < world.World.Capacity; npc++)
        {
            if (world.World.IsSpawned(npc) && world.World.ZoneOf(npc) == spawn.Code)
            {
                return npc;
            }
        }

        Assert.Fail($"{PlayerRegistry.SpawnZoneId} 에 NPC 가 없다.");
        return -1;
    }

    /// <summary><paramref name="from"/> 에서 +X 로 <paramref name="distance"/> m 떨어진 자리.</summary>
    private static WorldPos Offset(in WorldPos from, float distance) =>
        new(from.X + distance, from.Y, from.Z);

    private static void Drain(GameWorld world)
    {
        while (world.World.Events.TryRead(out _))
        {
            // 앞 단계의 이벤트는 이 회차의 관심사가 아니다.
        }
    }

    private static void Collect(
        GameWorld world, int npc, List<(ProximityChange Change, int Distance)> into)
    {
        while (world.World.Events.TryRead(out GameEvent ev))
        {
            if (ev.Kind == GameEventKind.PlayerProximity && ev.Npc.Value == npc)
            {
                into.Add(((ProximityChange)ev.Code, ev.Amount));
            }
        }
    }
}
