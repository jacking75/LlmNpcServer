using System.Reflection;
using System.Runtime.CompilerServices;
using MemoryPack;
using Npc.Contracts;
using Npc.Wire;

namespace Npc.Tests.Wire;

/// <summary>
/// docs/20 §5.3 — 와이어 DTO 가 계약 타입과 1:1 인가, 크기가 고정인가.
///
/// <b>이 테스트가 존재하는 이유가 T6-01 의 설계 판단 그 자체다.</b>
/// <c>Npc.Contracts</c> 에 <c>[MemoryPackable]</c> 을 붙일 수 없어(CLAUDE.md §3) DTO 를 갈랐고,
/// 가른 대가로 <b>드리프트</b>가 생길 수 있다 — <c>NpcCommand</c> 에 필드를 추가하고 와이어를 잊는 것.
/// 그것을 빌드가 아니라 여기서 잡는다.
/// </summary>
[Trait("Category", "Wire")]
public sealed class WireDtoTests
{
    /// <summary>docs/20 §5.3 이 못 박은 크기. 누가 필드를 끼워 넣으면 여기가 먼저 깨진다.</summary>
    private const int CommandSize = 56;

    /// <summary>같다.</summary>
    private const int EventSize = 64;

    // ---------------------------------------------------------------- 레이아웃

    /// <summary>
    /// 크기를 못 박는다 (T6-02 완료 조건). 패딩이 생기면 값이 달라진다 —
    /// 사양의 필드 순서(큰 타입부터)를 지켰는지를 이 한 줄이 검사한다.
    /// </summary>
    [Fact]
    public void Wire_LayoutIsFrozen()
    {
        Assert.Equal(CommandSize, Unsafe.SizeOf<WireCommand>());
        Assert.Equal(EventSize, Unsafe.SizeOf<WireEvent>());
    }

    /// <summary>N2 — unmanaged struct 다. 참조 필드가 하나라도 있으면 거짓이 된다.</summary>
    [Fact]
    public void Wire_IsUnmanaged()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<WireCommand>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<WireEvent>());
    }

    // ---------------------------------------------------------------- 왕복

    /// <summary>
    /// 12개 <see cref="NpcCommandKind"/> 전부, <b>모든 필드에 서로 다른 값</b>을 넣고 왕복시킨다.
    /// 값이 같으면 매핑이 뒤바뀌어도 통과한다 — 그래서 전부 다른 값을 준다.
    /// </summary>
    [Fact]
    public void Wire_CommandRoundTrips()
    {
        NpcCommandKind[] kinds = Enum.GetValues<NpcCommandKind>();

        Assert.Equal(12, kinds.Length);

        int seed = 1;

        foreach (NpcCommandKind kind in kinds)
        {
            NpcCommand original = new()
            {
                Kind = kind,
                Npc = new NpcId(seed + 1),
                IssuedAt = new Tick(seed + 2),
                Correlation = new CorrelationId((uint)seed + 3),
                Priority = CommandPriority.Normal,
                TargetPoi = new PoiId((ushort)(seed + 4)),
                TargetPos = new WorldPos(seed + 5.5f, seed + 6.5f, seed + 7.5f),
                TargetNpc = new NpcId(seed + 8),
                TargetPlayer = new PlayerId(seed + 9),
                Item = new ItemId((ushort)(seed + 10)),
                Amount = -(seed + 11),      // 음수도 살아남아야 한다 (InventoryChange 차감)
                Animation = new AnimationId((ushort)(seed + 12)),
                Dialogue = new DialogueId((ushort)(seed + 13)),
                Visual = VisualState.Working,
                Archetype = new ArchetypeId((ushort)(seed + 14)),
                Zone = new ZoneId((ushort)(seed + 15)),
                Flags = (byte)(seed + 16),
            };

            NpcCommand round = WireCommand.From(in original).To();

            Assert.Equal(original, round);

            seed += 17;
        }
    }

    /// <summary>17개 <see cref="GameEventKind"/> 전부.</summary>
    [Fact]
    public void Wire_EventRoundTrips()
    {
        GameEventKind[] kinds = Enum.GetValues<GameEventKind>();

        Assert.Equal(17, kinds.Length);

        int seed = 1;

        foreach (GameEventKind kind in kinds)
        {
            GameEvent original = new()
            {
                Kind = kind,
                Sequence = seed + 1L,
                OccurredAt = new Tick(seed + 2),
                Npc = new NpcId(seed + 3),
                Correlation = new CorrelationId((uint)seed + 4),
                Pos = new WorldPos(seed + 5.5f, seed + 6.5f, seed + 7.5f),
                Heading = seed + 8.5f,
                Poi = new PoiId((ushort)(seed + 9)),
                Zone = new ZoneId((ushort)(seed + 10)),
                Player = new PlayerId(seed + 11),
                OtherNpc = new NpcId(seed + 12),
                Item = new ItemId((ushort)(seed + 13)),
                Amount = -(seed + 14),
                Hp = (short)(seed + 15),
                Stamina = (short)-(seed + 16),   // 음수 기력도 표현 가능해야 한다
                Code = (byte)(seed + 17),
            };

            GameEvent round = WireEvent.From(in original).To();

            Assert.Equal(original, round);

            seed += 18;
        }
    }

    // ---------------------------------------------------------------- 드리프트

    /// <summary>
    /// <b>계약 타입에 멤버를 추가하고 와이어를 잊으면 여기가 깨진다</b> (docs/20 §3.1·§13).
    ///
    /// 이름으로 1:1 을 맞추되 <see cref="WorldPos"/> 만 예외다 — 와이어는 참조 타입을 실을 수 없어
    /// <c>PosX</c>·<c>PosY</c>·<c>PosZ</c> 세 필드로 편다. 그 예외를 여기 한 곳에 적어 둔다.
    /// </summary>
    [Theory]
    [InlineData(typeof(NpcCommand), typeof(WireCommand))]
    [InlineData(typeof(GameEvent), typeof(WireEvent))]
    public void Wire_MirrorsContractMembers(Type contract, Type wire)
    {
        var expected = new SortedSet<string>(StringComparer.Ordinal);

        foreach (PropertyInfo p in contract.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.PropertyType == typeof(WorldPos))
            {
                expected.Add("PosX");
                expected.Add("PosY");
                expected.Add("PosZ");
                continue;
            }

            expected.Add(p.Name);
        }

        var actual = new SortedSet<string>(
            wire.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name),
            StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    // ---------------------------------------------------------------- 제어 메시지 (T6-03)

    /// <summary>
    /// <c>WireHash</c> 왕복. <b>hex 는 로그용이고 와이어에는 안 나간다</b> —
    /// 실리는 것은 <c>ulong</c> 4개다 (N3 · docs/20 §5.4).
    /// </summary>
    [Fact]
    public void WireHash_RoundTripsThroughHex()
    {
        // 실제 SHA-256 모양의 값. 0 으로 시작하는 워드를 포함시켜 자릿수 누락을 잡는다.
        const string Hex = "00f3a91b2c4d5e6f7081920304a5b6c7d8e9f0010203040506070809a0b0c0d0";

        WireHash hash = WireHash.FromHex(Hex);

        Assert.Equal(Hex, hash.ToHex());
        Assert.Equal(WireHash.HexLength, hash.ToHex().Length);

        // 접두가 붙어 있어도 받는다 — MasterDataSet.ContentHash 가 그 형태로 다닐 수 있다.
        Assert.Equal(hash, WireHash.FromHex("sha256:" + Hex));

        // 소문자로 되돌린다. 대문자로 넣어도 같은 값이다.
        Assert.Equal(hash, WireHash.FromHex(Hex.ToUpperInvariant()));
        Assert.DoesNotContain(hash.ToHex(), c => char.IsUpper(c));
    }

    /// <summary>길이가 틀리면 거절한다. 조용히 잘라 쓰면 해시 비교가 거짓으로 통과한다.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("00f3a91b2c4d5e6f7081920304a5b6c7d8e9f0010203040506070809a0b0c0d")]     // 63자
    [InlineData("00f3a91b2c4d5e6f7081920304a5b6c7d8e9f0010203040506070809a0b0c0d0e")]  // 65자
    public void WireHash_RejectsWrongLength(string hex) =>
        Assert.Throws<FormatException>(() => WireHash.FromHex(hex));

    /// <summary>제어 메시지 4종이 왕복한다 (T6-03 완료 조건).</summary>
    [Fact]
    public void Wire_ControlMessagesRoundTrip()
    {
        WireHash md = WireHash.FromHex(new string('a', 64));
        WireHash roster = WireHash.FromHex(new string('5', 64));

        var hello = new WireHello
        {
            ProtocolVersion = 1,
            TickRate = 10,
            TimeScale = 600,
            NpcCount = 5_000,
            StartTick = 1_234_567L,
            MasterData = md,
            Roster = roster,
        };

        var ack = new WireHelloAck
        {
            ProtocolVersion = 1,
            TimeScale = 600,
            NpcCount = 5_000,
            MasterData = md,
            Roster = roster,
            Accepted = 0,
            RejectCode = (byte)LinkRejectCode.RosterMismatch,
        };

        var beat = new WireHeartbeat { Tick = 98_765L, Sequence = 4_321L };
        var bye = new WireBye { Code = (byte)LinkByeCode.Timeout };

        Assert.Equal(hello, Roundtrip(hello));
        Assert.Equal(ack, Roundtrip(ack));
        Assert.Equal(beat, Roundtrip(beat));
        Assert.Equal(bye, Roundtrip(bye));

        static T Roundtrip<T>(T value) => MemoryPackSerializer.Deserialize<T>(
            MemoryPackSerializer.Serialize(value))!;
    }

    /// <summary>
    /// N3 — 와이어에 <c>string</c> 이 없다. N4 — <c>DateTime</c>/<c>TimeSpan</c> 도 없다.
    /// <c>Npc.Wire</c> 어셈블리 전체를 본다. T6-03 이 제어 메시지를 추가해도 그대로 걸린다.
    /// </summary>
    /// <remarks>
    /// <b>이 하나만 <c>Contracts</c> 로 겹쳐 단다</b> (docs/20 §13). N3·N4 는 와이어의 성질이
    /// 아니라 <b>계약의 성질</b>이고, <c>Category=Contracts</c> 로 N1~N8 을 한 번에 돌릴 때
    /// 여기가 빠지면 그 회차가 "패킷에 문자열이 없다" 를 안 보게 된다.
    /// xUnit 은 클래스와 메서드의 트레이트를 합치므로 <c>Wire</c> 회차에도 그대로 든다.
    /// </remarks>
    [Fact]
    [Trait("Category", "Contracts")]
    public void Wire_NoStringOrDateTimeFields()
    {
        Type[] offenders = [typeof(string), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan)];

        foreach (Type type in typeof(WireCommand).Assembly.GetTypes())
        {
            if (!type.IsValueType || type.IsEnum || !type.IsPublic)
            {
                continue;
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.DoesNotContain(field.FieldType, offenders);
            }
        }
    }
}
