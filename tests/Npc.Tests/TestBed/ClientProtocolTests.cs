using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using MemoryPack;
using Npc.TestBed.Protocol;
using Npc.Wire;

namespace Npc.Tests.TestBed;

/// <summary>
/// T6-22 — 클라이언트 프로토콜. docs/20 §8.
///
/// <para>
/// <b>여기서 못 박는 것은 셋이다.</b> <see cref="EntityState"/> 의 크기(32바이트),
/// 모든 메시지의 왕복, 그리고 <c>string</c>·<c>DateTime</c> 이 하나도 없다는 것(N3·N4).
/// 마지막 것이 docs/20 §3.5 를 <b>구조적으로</b> 지키는 방법이다 —
/// 프로토콜에 문자열이 없으면 플레이어가 쓴 글자가 들어올 길이 없다.
/// </para>
/// </summary>
[Trait("Category", "TestBed")]
public sealed class ClientProtocolTests
{
    /// <summary>docs/20 §8.1 이 못 박은 크기. 30바이트 + 정렬 패딩 2.</summary>
    private const int EntitySize = 32;

    /// <summary>AOI 상한. docs/20 §8.1.</summary>
    private const int AoiCap = 256;

    // ---------------------------------------------------------------- 레이아웃

    /// <summary>
    /// 완료 조건 — <c>sizeof(EntityState) == 32</c>.
    ///
    /// 누가 필드를 끼워 넣으면 여기가 먼저 깨진다. 크기가 흔들리면 MemoryPack 의
    /// unmanaged 배열 고속 경로가 내는 바이트 수가 바뀌고, 대역폭 어림이 통째로 틀어진다.
    /// </summary>
    [Fact]
    public void ClientProtocol_EntityLayoutIsFrozen()
    {
        Assert.Equal(EntitySize, Unsafe.SizeOf<EntityState>());

        // unmanaged 여야 배열 고속 경로를 탄다. 참조 필드가 하나라도 있으면 거짓이 된다.
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<EntityState>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<ZoneState>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<LogCommand>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<LogEvent>());
    }

    /// <summary>
    /// docs/20 §8.2 — 서버 Kind 는 1~63, 클라 Kind 는 64~127 이다.
    ///
    /// 갈라 두는 이유는 로그에서 방향을 헷갈리지 않기 위해서다. 새 메시지를 엉뚱한
    /// 대역에 넣으면 그 규약이 조용히 무너지므로 여기서 잡는다.
    /// </summary>
    [Fact]
    public void ClientProtocol_KindRangesAreSplitByDirection()
    {
        ClientMessageKind[] server =
        [
            ClientMessageKind.SrvHello, ClientMessageKind.Snapshot, ClientMessageKind.ZoneStates,
            ClientMessageKind.CommandLog, ClientMessageKind.EventLog, ClientMessageKind.LinkStatus,
            ClientMessageKind.Pong,
        ];

        ClientMessageKind[] client =
        [
            ClientMessageKind.CliHello, ClientMessageKind.Input, ClientMessageKind.Interact,
            ClientMessageKind.Attack, ClientMessageKind.Select, ClientMessageKind.Control,
            ClientMessageKind.Ping,
        ];

        // 정의된 것이 이 14개뿐이다 (None 제외). 새로 늘리면 이 단언이 먼저 걸린다.
        Assert.Equal(
            server.Length + client.Length + 1,
            Enum.GetValues<ClientMessageKind>().Length);

        foreach (ClientMessageKind kind in server)
        {
            Assert.InRange((byte)kind, 1, ClientProtocol.MaxServerKind);
            Assert.True(ClientProtocol.IsServerBound(kind));
        }

        foreach (ClientMessageKind kind in client)
        {
            Assert.InRange((byte)kind, 64, 127);
            Assert.False(ClientProtocol.IsServerBound(kind));
        }

        Assert.False(ClientProtocol.IsServerBound(ClientMessageKind.None));
    }

    // ---------------------------------------------------------------- 왕복

    /// <summary>
    /// 완료 조건 — 256 엔티티 스냅샷이 왕복한다.
    ///
    /// <b>모든 필드에 서로 다른 값을 넣는다.</b> 값이 같으면 매핑이 뒤바뀌어도 통과한다.
    /// </summary>
    [Fact]
    public void ClientProtocol_SnapshotRoundTrips()
    {
        var entities = new EntityState[AoiCap];

        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = new EntityState
            {
                Id = i + 1,
                X = i + 0.25f,
                Z = -(i + 0.75f),          // 음수도 살아남아야 한다 — 월드 좌표는 원점 기준이다
                Heading = (i % 628) / 100f,
                Poi = (ushort)(i + 2),
                TargetPoi = (ushort)(i + 3),
                Zone = (ushort)((i % 12) + 1),
                Archetype = (ushort)((i % 40) + 1),
                Hp = (short)(100 - (i % 100)),
                Kind = (byte)(i % 2 == 0 ? EntityKind.Npc : EntityKind.Player),
                Visual = (byte)(i % 8),
                StateFlags = (byte)(i % 16),
                Reserved = 0,
            };
        }

        var original = new Snapshot
        {
            Tick = 123_456,
            GameDay = 7,
            GameHour = 19,
            TimeOfDay = 3,
            EntityCount = entities.Length,
            Entities = entities,
        };

        Snapshot copy = RoundTrip(original, ClientMessageKind.Snapshot);

        Assert.Equal(original.Tick, copy.Tick);
        Assert.Equal(original.GameDay, copy.GameDay);
        Assert.Equal(original.GameHour, copy.GameHour);
        Assert.Equal(original.TimeOfDay, copy.TimeOfDay);
        Assert.Equal(AoiCap, copy.EntityCount);

        Assert.NotNull(copy.Entities);
        Assert.Equal(entities, copy.Entities);

        // unmanaged 배열은 원소마다 태그가 붙지 않는다 (docs/20 §5.2). 길이 접두 4바이트 +
        // count × sizeof(T) 가 페이로드 안에 그대로 들어가야 한다 — 안 그러면 고속 경로를
        // 안 탄 것이고, 대역폭 어림(§5.3)이 틀어진다.
        byte[] payload = MemoryPackSerializer.Serialize(original);

        Assert.InRange(payload.Length, (AoiCap * EntitySize) + 4, (AoiCap * EntitySize) + 64);
    }

    /// <summary>
    /// 완료 조건 — §8 의 메시지 14종이 전부 왕복한다.
    ///
    /// 프레임 코덱은 <see cref="FrameCodec"/> 의 것을 그대로 쓴다 — 헤더가 붙었다 떨어지는
    /// 것까지 같이 본다.
    /// </summary>
    [Fact]
    public void ClientProtocol_AllMessagesRoundTrip()
    {
        // ── 서버 → 클라이언트 ─────────────────────────────────
        var hello = new SrvHello
        {
            ProtocolVersion = ClientProtocol.Version,
            TickRate = 10,
            TimeScale = 60,
            PlayerId = 3,
            NpcCount = 300,
            MinX = -1_800.5f,
            MinZ = -1_700.25f,
            MaxX = 1_900.75f,
            MaxZ = 1_850.5f,
            MasterData = WireHash.FromHex(new string('a', 63) + "b"),
        };

        SrvHello helloCopy = RoundTrip(hello, ClientMessageKind.SrvHello);

        Assert.Equal(hello, helloCopy);
        Assert.Equal(hello.MasterData.ToHex(), helloCopy.MasterData.ToHex());

        var zones = new ZoneStates
        {
            Tick = 900,
            Zones =
            [
                new ZoneState { ZoneCode = 1, RegionState = 2, Climate = 3 },
                new ZoneState { ZoneCode = 12, RegionState = 0, Climate = 1 },
            ],
        };

        ZoneStates zonesCopy = RoundTrip(zones, ClientMessageKind.ZoneStates);

        Assert.Equal(zones.Tick, zonesCopy.Tick);
        Assert.Equal(zones.Zones, zonesCopy.Zones);

        var commands = new CommandLog
        {
            Commands =
            [
                new LogCommand { Tick = 11, Npc = 12, Correlation = 13, TargetPoi = 14, Kind = 3 },
                new LogCommand { Tick = 21, Npc = 22, Correlation = 23, TargetPoi = 0, Kind = 4 },
            ],
        };

        Assert.Equal(commands.Commands, RoundTrip(commands, ClientMessageKind.CommandLog).Commands);

        var events = new EventLog
        {
            Events =
            [
                new LogEvent { Tick = 31, Npc = 32, Amount = -33, Kind = 8, Code = 2 },
                new LogEvent { Tick = 41, Npc = 42, Amount = 43, Kind = 11, Code = 0 },
            ],
        };

        Assert.Equal(events.Events, RoundTrip(events, ClientMessageKind.EventLog).Events);

        var status = new LinkStatus
        {
            GsTick = 5_000,
            NpcServerTick = 4_997,
            CommandsIn = 1_234,
            EventsOut = 56_789,
            Dropped = 7,
            Gaps = 0,
            Connected = 1,
        };

        Assert.Equal(status, RoundTrip(status, ClientMessageKind.LinkStatus));

        var pong = new Pong { ClientStamp = -9_876_543_210L, ServerTick = 5_001 };

        Assert.Equal(pong, RoundTrip(pong, ClientMessageKind.Pong));

        // ── 클라이언트 → 서버 ─────────────────────────────────
        var cli = new CliHello { ProtocolVersion = ClientProtocol.Version, ClientVersion = 42 };

        Assert.Equal(cli, RoundTrip(cli, ClientMessageKind.CliHello));

        var input = new Input { Seq = 77, DirX = -0.5f, DirZ = 0.875f, Run = 1 };

        Assert.Equal(input, RoundTrip(input, ClientMessageKind.Input));

        var interact = new Interact { NpcId = 88 };

        Assert.Equal(interact, RoundTrip(interact, ClientMessageKind.Interact));

        var attack = new Attack { NpcId = 99, Amount = 17 };

        Assert.Equal(attack, RoundTrip(attack, ClientMessageKind.Attack));

        // 음수는 선택 해제다.
        var select = new Select { NpcId = -1 };

        Assert.Equal(select, RoundTrip(select, ClientMessageKind.Select));

        var control = new Control
        {
            Amount = -300,
            ZoneCode = 5,
            Kind = (byte)ControlKind.SetZoneState,
            Code = 2,
        };

        Assert.Equal(control, RoundTrip(control, ClientMessageKind.Control));

        var ping = new Ping { ClientStamp = long.MaxValue };

        Assert.Equal(ping, RoundTrip(ping, ClientMessageKind.Ping));
    }

    /// <summary>
    /// 프레임이 쪼개져 와도 복원된다. TCP 는 경계를 지켜 주지 않는다.
    ///
    /// <see cref="FrameCodec"/> 의 성질이지만 <b>이 프로토콜이 그것을 재사용하고 있는지</b>를
    /// 여기서 본다 — 코덱을 따로 짜면 이 테스트가 먼저 깨진다.
    /// </summary>
    [Fact]
    public void ClientProtocol_FrameSplitAcrossReads()
    {
        var ping = new Ping { ClientStamp = 4_242 };
        byte[] frame = Frame(ping, ClientMessageKind.Ping);

        var buffer = new ReadOnlySequence<byte>(frame);

        // 마지막 한 바이트가 빠지면 아직 프레임이 아니다.
        var partial = new ReadOnlySequence<byte>(frame, 0, frame.Length - 1);

        Assert.False(ClientProtocol.TryReadFrame(ref partial, out _, out _));
        Assert.Equal(frame.Length - 1, partial.Length);   // 버퍼를 건드리지 않는다

        Assert.True(ClientProtocol.TryReadFrame(
            ref buffer, out ClientMessageKind kind, out ReadOnlySequence<byte> payload));

        Assert.Equal(ClientMessageKind.Ping, kind);
        Assert.Equal(0, buffer.Length);
        Assert.Equal(ping, MemoryPackSerializer.Deserialize<Ping>(payload));
    }

    // ---------------------------------------------------------------- N3 · N4

    /// <summary>
    /// 완료 조건 — 이 어셈블리에도 <c>string</c>·<c>DateTime</c> 이 없다 (N3·N4 · docs/20 §3.4).
    ///
    /// <para>
    /// <b>이것이 docs/20 §3.5 를 지키는 방법이다.</b> "플레이어가 쓴 문자열을 넣지 마라" 가
    /// 아니라 <b>넣을 자리가 없게</b> 만든다 — 닉네임도 채팅도 프로토콜에 자리가 없으므로
    /// 게임서버가 무엇을 받든 문자열이 NPC 서버 쪽으로 갈 길이 없다.
    /// </para>
    ///
    /// <para><see cref="WireDtoTests.Wire_NoStringOrDateTimeFields"/> 와 같은 검사다.</para>
    /// </summary>
    [Fact]
    public void Wire_NoStringOrDateTimeFields()
    {
        Type[] offenders = [typeof(string), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan)];

        int checkedTypes = 0;

        foreach (Type type in typeof(EntityState).Assembly.GetTypes())
        {
            if (!type.IsValueType || type.IsEnum || !type.IsPublic)
            {
                continue;
            }

            checkedTypes++;

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.DoesNotContain(field.FieldType, offenders);
            }
        }

        // 아무것도 안 봤으면 이 테스트는 공허하다. §8 의 메시지 14종 + 원소 4종이 최소다.
        Assert.True(checkedTypes >= 18, $"검사한 타입이 {checkedTypes}개뿐이다.");
    }

    // ---------------------------------------------------------------- 도우미

    /// <summary>메시지 하나를 프레임으로 감싼다.</summary>
    private static byte[] Frame<T>(in T message, ClientMessageKind kind)
    {
        byte[] payload = MemoryPackSerializer.Serialize(message);
        var writer = new ArrayBufferWriter<byte>(FrameCodec.HeaderSize + payload.Length);

        ClientProtocol.WriteHeader(writer, kind, payload.Length);
        writer.Write(payload);

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>프레임으로 감쌌다가 도로 푼다. 헤더의 <c>Kind</c> 도 같이 확인한다.</summary>
    private static T RoundTrip<T>(in T message, ClientMessageKind kind)
    {
        var buffer = new ReadOnlySequence<byte>(Frame(in message, kind));

        Assert.True(ClientProtocol.TryReadFrame(
            ref buffer, out ClientMessageKind read, out ReadOnlySequence<byte> payload));

        Assert.Equal(kind, read);
        Assert.Equal(0, buffer.Length);

        return MemoryPackSerializer.Deserialize<T>(payload)!;
    }
}
