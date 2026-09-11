using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.TestBed.Protocol;
using Npc.TestGameServer;
using Npc.TestGameServer.Client;
using Npc.TestGameServer.World;

namespace Npc.Tests.TestBed;

/// <summary>
/// T6-25 — 클라이언트 제어 적용. docs/20 §8.3.
///
/// <para>
/// 데모에서 사람이 세계를 흔들어 보는 자리다. <b>NPC 서버는 이 존재를 모른다</b> —
/// 링크로 나가는 것은 평소와 같은 이벤트뿐이고, 그것이 P6 의 주장을 지탱한다.
/// </para>
/// </summary>
[Trait("Category", "TestBed")]
public sealed class ControlHandlerTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static readonly NpcInstanceTable s_instances = NpcInstanceTable.Load(
        Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

    /// <summary>
    /// 완료 조건 — <c>SetZoneState</c> 가 <c>ZoneStateChanged</c> 를 낸다.
    ///
    /// <b>여기서 플랜을 갈아 끼우지 않는다.</b> 존 전체의 플랜이 갈리는 것은 NPC 서버가
    /// 이 이벤트를 받고 버킷 키를 다시 푸는 결과다 (docs/14 §5).
    /// </summary>
    [Fact]
    public async Task Control_SetZoneStateEmitsEvent()
    {
        await using GameWorld world = Create();

        var control = new ControlHandler(world, s_data, new PlayerRegistry(world, s_data, RegistryOptions));
        ZoneDef zone = s_data.Zones.Zones[0];
        var now = new Tick(7);

        Drain(world);

        Assert.True(control.Apply(
            new Control
            {
                Kind = (byte)ControlKind.SetZoneState,
                ZoneCode = zone.Code.Value,
                Code = (byte)RegionState.War,
            },
            now));

        GameEvent ev = Single(world, GameEventKind.ZoneStateChanged);

        Assert.Equal(zone.Code, ev.Zone);
        Assert.Equal((byte)RegionState.War, ev.Code);
        Assert.Equal(now, ev.OccurredAt);
        Assert.True(ev.Sequence > 0, "시퀀스가 안 붙었다 (N6).");

        // 기후도 같은 길이다.
        Drain(world);

        Assert.True(control.Apply(
            new Control
            {
                Kind = (byte)ControlKind.SetWeather,
                ZoneCode = zone.Code.Value,
                Code = (byte)Climate.Storm,
            },
            now));

        Assert.Equal((byte)Climate.Storm, Single(world, GameEventKind.WeatherChanged).Code);
        Assert.Equal(2, control.Applied);

        // 모르는 존과 범위 밖 코드는 거절이다. 예외를 던지지 않는다 —
        // 낡은 클라이언트 하나가 게임서버를 죽이면 데모가 못 돈다.
        Drain(world);

        Assert.False(control.Apply(
            new Control { Kind = (byte)ControlKind.SetZoneState, ZoneCode = 9_999, Code = 0 }, now));
        Assert.False(control.Apply(
            new Control
            {
                Kind = (byte)ControlKind.SetZoneState,
                ZoneCode = zone.Code.Value,
                Code = 200,
            },
            now));

        Assert.Equal(2, control.Rejected);
        Assert.Empty(Events(world));
    }

    /// <summary>
    /// 완료 조건 — <c>SkipTime</c> 이 <b>정확한 틱 수</b>만큼 건너뛴다.
    ///
    /// 그리고 그 부작용이 실제로 일어난다는 것까지 본다 — 진행 중이던 이동이
    /// 다음 틱에 한꺼번에 끝난다. 데모 편의 기능이지 결정론 리플레이 대상이 아니다.
    /// </summary>
    [Fact]
    public async Task Control_SkipTimeAdvancesTick()
    {
        const int GameMinutes = 90;
        const int TimeScale = 60;

        await using GameWorld world = Create(timeScale: TimeScale);

        var control = new ControlHandler(world, s_data, new PlayerRegistry(world, s_data, RegistryOptions));
        var now = new Tick(10);

        // 이동 중인 NPC 를 하나 만든다.
        int npc = FirstSpawned(world);
        PoiId from = world.World.PoiOf(npc);
        PoiId to = OtherPoiInZone(world.World.ZoneOf(npc), from);

        world.World.ApplyCommand(
            new NpcCommand
            {
                Kind = NpcCommandKind.MoveTo,
                Npc = new NpcId(npc),
                IssuedAt = now,
                Correlation = new CorrelationId(1),
                Priority = CommandPriority.Normal,
                TargetPoi = to,
            },
            now);

        world.Tick(now);

        Assert.True(world.Movement.IsMoving(npc), "이동이 시작되지 않았다. 표본이 잘못됐다.");

        Assert.True(control.Apply(
            new Control { Kind = (byte)ControlKind.SkipTime, Amount = GameMinutes }, now));

        // docs/20 §8.3 의 식 그대로다: Amount × 60 × 10 / TimeScale.
        long expected = (long)GameMinutes * 60 * Tick.PerSecond / TimeScale;

        Assert.Equal(expected, control.PendingTickSkip);
        Assert.Equal(900, expected);

        // 두 번 부르면 쌓인다. 루프가 가져가면 0 이 된다.
        control.Apply(new Control { Kind = (byte)ControlKind.SkipTime, Amount = GameMinutes }, now);

        Assert.Equal(expected * 2, control.PendingTickSkip);
        Assert.Equal(expected * 2, control.TakeTickSkip());
        Assert.Equal(0, control.PendingTickSkip);

        // 부작용 — 건너뛴 틱으로 한 번 돌면 진행 중이던 이동이 즉시 끝난다.
        world.Tick(new Tick(now.Value + (expected * 2)));

        Assert.False(world.Movement.IsMoving(npc), "건너뛰었는데 이동이 안 끝났다.");
        Assert.Equal(to, world.World.PoiOf(npc));

        // 0 이하는 거절이다.
        Assert.False(control.Apply(new Control { Kind = (byte)ControlKind.SkipTime, Amount = 0 }, now));
        Assert.Equal(1, control.Rejected);
    }

    /// <summary>완료 조건 — <c>Despawn</c> 이 <c>NpcDespawned</c> 를 낸다. <c>TargetGone</c> 경로 확인용이다.</summary>
    [Fact]
    public async Task Control_DespawnEmitsNpcDespawned()
    {
        await using GameWorld world = Create();

        var control = new ControlHandler(world, s_data, new PlayerRegistry(world, s_data, RegistryOptions));
        int npc = FirstSpawned(world);
        var now = new Tick(3);

        Drain(world);

        Assert.True(control.Apply(
            new Control { Kind = (byte)ControlKind.Despawn, Amount = npc }, now));

        GameEvent ev = Single(world, GameEventKind.NpcDespawned);

        Assert.Equal(npc, ev.Npc.Value);
        Assert.False(world.World.IsSpawned(npc));

        // 두 번째는 거절이다 — 이미 없는 NPC 다.
        Drain(world);

        Assert.False(control.Apply(
            new Control { Kind = (byte)ControlKind.Despawn, Amount = npc }, now));
        Assert.False(control.Apply(
            new Control { Kind = (byte)ControlKind.Despawn, Amount = 999_999 }, now));

        Assert.Equal(2, control.Rejected);
        Assert.Empty(Events(world));
    }

    /// <summary>
    /// 완료 조건 — 모르는 <see cref="ControlKind"/> 는 무시하고 <b>센다.</b>
    ///
    /// 낡은 클라이언트가 붙었을 때 게임서버가 죽으면 데모가 못 돈다.
    /// 센 값이 남아 있어야 원인을 말할 수 있다.
    /// </summary>
    [Fact]
    public async Task Control_IgnoresUnknownKind()
    {
        await using GameWorld world = Create();

        var control = new ControlHandler(world, s_data, new PlayerRegistry(world, s_data, RegistryOptions));
        var now = new Tick(1);

        Drain(world);

        Assert.False(control.Apply(new Control { Kind = 0 }, now));
        Assert.False(control.Apply(new Control { Kind = 200 }, now));

        Assert.Equal(2, control.Ignored);
        Assert.Equal(0, control.Applied);
        Assert.Equal(0, control.Rejected);
        Assert.Empty(Events(world));
    }

    /// <summary>
    /// <c>SetFaultRate</c> 가 실제로 명령을 삼킨다.
    ///
    /// <b>드롭은 응답을 아예 주지 않는다.</b> 실패 이벤트조차 없어야 NPC 서버가
    /// 타임아웃을 합성해야만 진행된다 (docs/02 §1) — 그 경로를 데모에서 눌러 보는 것이
    /// 이 제어의 목적이다.
    /// </summary>
    [Fact]
    public async Task Control_SetFaultRateDropsCommands()
    {
        const int Commands = 400;

        await using GameWorld world = Create();

        var control = new ControlHandler(world, s_data, new PlayerRegistry(world, s_data, RegistryOptions));
        var now = new Tick(1);

        // 100% 드롭. 만분율이라 10,000 이 전부다.
        Assert.True(control.Apply(
            new Control
            {
                Kind = (byte)ControlKind.SetFaultRate,
                Code = 1,
                Amount = ControlHandler.RatePerUnit,
            },
            now));

        Assert.Equal(ControlHandler.RatePerUnit, control.DropRatePerUnit);

        Drain(world);

        for (int i = 0; i < Commands; i++)
        {
            world.World.ApplyCommand(
                new NpcCommand
                {
                    Kind = NpcCommandKind.MoveTo,
                    Npc = new NpcId(i % world.Roster.Count),
                    IssuedAt = now,
                    Correlation = new CorrelationId((uint)i + 1),
                    Priority = CommandPriority.Normal,
                    TargetPoi = new PoiId(1),
                },
                now);
        }

        Assert.Equal(Commands, control.Dropped);
        Assert.Empty(Events(world));   // 실패 이벤트조차 없다

        // 0 으로 되돌리면 다시 통과한다.
        Assert.True(control.Apply(
            new Control { Kind = (byte)ControlKind.SetFaultRate, Code = 1, Amount = 0 }, now));

        world.World.ApplyCommand(
            new NpcCommand
            {
                Kind = NpcCommandKind.Stop,
                Npc = new NpcId(0),
                IssuedAt = now,
                Correlation = new CorrelationId(9_999),
                Priority = CommandPriority.Normal,
            },
            now);

        Assert.Equal(Commands, control.Dropped);
        Assert.NotEmpty(Events(world));

        // 범위 밖 인자는 거절이다.
        Assert.False(control.Apply(
            new Control { Kind = (byte)ControlKind.SetFaultRate, Code = 2, Amount = 0 }, now));
        Assert.False(control.Apply(
            new Control
            {
                Kind = (byte)ControlKind.SetFaultRate,
                Code = 0,
                Amount = ControlHandler.RatePerUnit + 1,
            },
            now));

        Assert.Equal(2, control.Rejected);
    }

    // ---------------------------------------------------------------- 도우미

    /// <summary>제어 처리기에 넘길 등록부용 옵션. 슬롯 수만 쓰인다.</summary>
    private static GameServerOptions RegistryOptions => new()
    {
        Npcs = 32,
        TimeScale = 60,
        LinkPort = 0,
        ClientPort = 0,
    };

    private static GameWorld Create(int npcs = 32, int timeScale = 60)
    {
        var options = new GameServerOptions
        {
            Npcs = npcs,
            TimeScale = timeScale,
            LinkPort = 0,
            ClientPort = 0,
        };

        return GameWorld.Create(options, s_data, NpcRoster.Select(s_instances, npcs), s_instances);
    }

    private static void Drain(GameWorld world)
    {
        while (world.World.Events.TryRead(out _))
        {
            // 앞 단계의 이벤트는 이 회차의 관심사가 아니다.
        }
    }

    private static List<GameEvent> Events(GameWorld world)
    {
        var events = new List<GameEvent>();

        while (world.World.Events.TryRead(out GameEvent ev))
        {
            events.Add(ev);
        }

        return events;
    }

    private static GameEvent Single(GameWorld world, GameEventKind kind) =>
        Assert.Single(Events(world), ev => ev.Kind == kind);

    private static int FirstSpawned(GameWorld world)
    {
        for (int npc = 0; npc < world.World.Capacity; npc++)
        {
            if (world.World.IsSpawned(npc))
            {
                return npc;
            }
        }

        Assert.Fail("스폰된 NPC 가 없다.");
        return -1;
    }

    private static PoiId OtherPoiInZone(ZoneId zone, PoiId except)
    {
        foreach (PoiId poi in s_data.Pois.InZone(zone))
        {
            if (poi != except)
            {
                return poi;
            }
        }

        Assert.Fail($"존 {zone.Value} 에 POI 가 하나뿐이다.");
        return default;
    }
}
