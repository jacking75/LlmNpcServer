using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Npc.Contracts;
using Npc.Wire;
using Npc.Wire.V2;

namespace Npc.Tests.Wire;

/// <summary>
/// B-02 — 패킷 확장 슬롯.
///
/// <b>v1 은 동결이고 v2 가 그 위에 얹힌다.</b> 여기서 지키는 것은 셋이다 —
/// v2 크기 고정 · 계약과의 1:1 · <b>정의되지 않은 예약 슬롯은 0</b>.
/// 마지막 것이 없으면 예약 슬롯은 "아무거나 넣는 칸" 이 되고, 두 팀이 같은 칸에 서로 다른
/// 것을 넣기 시작하면 되돌릴 방법이 없다.
/// </summary>
[Trait("Category", "Wire")]
public sealed class WireV2DtoTests
{
    /// <summary>
    /// v2 명령 크기. <b>로드맵 초안의 64 가 아니라 72 다.</b>
    ///
    /// v1 이 56B(패딩 0)이고 <c>Instance</c>(2)+<c>Faction</c>(2)+<c>ExtA</c>(4)+<c>ExtB</c>(4)
    /// = 68B, 정렬 8 이라 72B 다. 64 에 넣으려면 슬롯 하나를 버려야 하고, 그러면
    /// "예약 슬롯 2개" 라는 설계 자체가 없어진다.
    /// </summary>
    private const int CommandSizeV2 = 72;

    /// <summary>v2 이벤트 크기. v1 64B + 12B + 꼬리 정렬 = 80B. 로드맵의 수와 같다.</summary>
    private const int EventSizeV2 = 80;

    // ---------------------------------------------------------------- 레이아웃

    /// <summary>크기를 못 박는다. 누가 필드를 끼워 넣으면 여기가 먼저 깨진다.</summary>
    [Fact]
    public void Wire_LayoutIsFrozen_V2()
    {
        Assert.Equal(CommandSizeV2, Unsafe.SizeOf<WireCommandV2>());
        Assert.Equal(EventSizeV2, Unsafe.SizeOf<WireEventV2>());
    }

    /// <summary>v1 은 그대로다. v2 를 더한 것이 v1 을 건드리지 않았음을 여기서 못 박는다.</summary>
    [Fact]
    public void Wire_V1StaysFrozen()
    {
        Assert.Equal(56, Unsafe.SizeOf<WireCommand>());
        Assert.Equal(64, Unsafe.SizeOf<WireEvent>());
    }

    /// <summary>N2 — unmanaged struct 다.</summary>
    [Fact]
    public void Wire_IsUnmanaged_V2()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<WireCommandV2>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<WireEventV2>());
    }

    /// <summary>
    /// <b>암묵 패딩이 없다.</b> MemoryPack 은 unmanaged struct 를 원시 복사하므로 패딩이 있으면
    /// <b>초기화되지 않은 바이트가 그대로 소켓에 나간다</b> — 골든 바이트 벡터(B-03)가
    /// 회차마다 달라지고, 다른 언어 구현이 그 바이트를 해석하려 든다.
    /// 그래서 꼬리 정렬을 <c>Reserved</c> 필드로 명시했다.
    /// </summary>
    [Theory]
    [InlineData(typeof(WireCommandV2), CommandSizeV2)]
    [InlineData(typeof(WireEventV2), EventSizeV2)]
    public void Wire_HasNoImplicitPadding_V2(Type type, int size)
    {
        ArgumentNullException.ThrowIfNull(type);

        int sum = 0;

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            sum += Marshal.SizeOf(field.FieldType);
        }

        Assert.Equal(size, sum);
    }

    // ---------------------------------------------------------------- 왕복

    /// <summary>12개 명령 종류 전부, 확장 슬롯을 포함해 모든 필드가 왕복한다.</summary>
    [Fact]
    public void Wire_CommandRoundTrips_V2()
    {
        int seed = 1;

        foreach (NpcCommandKind kind in Enum.GetValues<NpcCommandKind>())
        {
            NpcCommand original = SampleCommand(kind, seed);

            Assert.Equal(original, WireCommandV2.From(in original).To());

            seed += 21;
        }
    }

    /// <summary>17개 이벤트 종류 전부.</summary>
    [Fact]
    public void Wire_EventRoundTrips_V2()
    {
        int seed = 1;

        foreach (GameEventKind kind in Enum.GetValues<GameEventKind>())
        {
            GameEvent original = SampleEvent(kind, seed);

            Assert.Equal(original, WireEventV2.From(in original).To());

            seed += 22;
        }
    }

    /// <summary>
    /// <b>v1 은 확장 슬롯을 버린다.</b> 이것이 결함이 아니라 v1 의 의미다 —
    /// 게임서버가 <see cref="LinkFeatures.ExtSlots"/> 를 켜지 않으면 그 값을 이해할 수 없고,
    /// 이해하지 못하는 값을 실어 보내는 것보다 <b>버리는 편이 정직하다</b>.
    /// </summary>
    [Fact]
    public void Wire_V1DropsExtensionSlots()
    {
        NpcCommand command = SampleCommand(NpcCommandKind.MoveTo, 7);
        NpcCommand throughV1 = WireCommand.From(in command).To();

        Assert.NotEqual(default, command.Instance);
        Assert.Equal(default, throughV1.Instance);
        Assert.Equal(default, throughV1.Faction);
        Assert.Equal(0u, throughV1.ExtA);
        Assert.Equal(0u, throughV1.ExtB);

        // 나머지는 하나도 안 잃는다.
        Assert.Equal(
            command with { Instance = default, Faction = default, ExtA = 0, ExtB = 0 },
            throughV1);
    }

    /// <summary><c>Reserved</c> 는 변환기가 항상 0 으로 쓴다.</summary>
    [Fact]
    public void Wire_ReservedIsAlwaysZero_V2()
    {
        Assert.Equal(0u, WireCommandV2.From(SampleCommand(NpcCommandKind.Speak, 3)).Reserved);
        Assert.Equal(0u, WireEventV2.From(SampleEvent(GameEventKind.NpcArrived, 3)).Reserved);
    }

    // ---------------------------------------------------------------- 드리프트

    /// <summary>
    /// v2 DTO 는 계약 전부를 싣는다. <c>Reserved</c> 하나만 와이어에만 있는 필드다 —
    /// 꼬리 정렬을 명시한 것이라 계약에는 대응물이 없다.
    /// </summary>
    [Theory]
    [InlineData(typeof(NpcCommand), typeof(WireCommandV2))]
    [InlineData(typeof(GameEvent), typeof(WireEventV2))]
    public void Wire_MirrorsContractMembers_V2(Type contract, Type wire)
    {
        ArgumentNullException.ThrowIfNull(wire);

        SortedSet<string> expected = WireDtoTests.ContractMemberNames(contract);

        expected.Add("Reserved");

        var actual = new SortedSet<string>(
            wire.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name),
            StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    // ---------------------------------------------------------------- 예약 슬롯

    /// <summary>
    /// <b>의미가 정해지지 않은 슬롯은 0 이어야 한다</b> (B-02 완료 조건).
    ///
    /// 지금 <see cref="ExtensionSlots"/> 에 등록된 것이 하나도 없으므로, 모든 Kind 에서
    /// <c>ExtA</c>·<c>ExtB</c> 가 0 이어야 깨끗하다. 슬롯을 쓰려면 등록부에 줄을 추가하고
    /// <c>docs/reference_link.html</c> 의 표를 같은 커밋에서 고친다.
    /// </summary>
    [Fact]
    public void Ext_ZeroForUndefinedKinds()
    {
        foreach (NpcCommandKind kind in Enum.GetValues<NpcCommandKind>())
        {
            var clean = new NpcCommand
            {
                Kind = kind,
                Npc = new NpcId(1),
                IssuedAt = new Tick(1),
                Correlation = new CorrelationId(1),
                Priority = CommandPriority.Normal,
            };

            Assert.True(ExtensionSlots.IsClean(in clean), $"{kind}: 0 인데 더럽다고 한다");

            if (!ExtensionSlots.IsDefined(kind, ExtSlot.A))
            {
                Assert.False(
                    ExtensionSlots.IsClean(clean with { ExtA = 1 }),
                    $"{kind}: ExtA 에 의미가 없는데 값을 넣어도 통과한다");
                Assert.Null(ExtensionSlots.MeaningOf(kind, ExtSlot.A));
            }

            if (!ExtensionSlots.IsDefined(kind, ExtSlot.B))
            {
                Assert.False(ExtensionSlots.IsClean(clean with { ExtB = 1 }));
            }
        }

        foreach (GameEventKind kind in Enum.GetValues<GameEventKind>())
        {
            var clean = new GameEvent { Kind = kind, Sequence = 1, OccurredAt = new Tick(1) };

            Assert.True(ExtensionSlots.IsClean(in clean));

            if (!ExtensionSlots.IsDefined(kind, ExtSlot.A))
            {
                Assert.False(ExtensionSlots.IsClean(clean with { ExtA = 1 }));
                Assert.Null(ExtensionSlots.MeaningOf(kind, ExtSlot.A));
            }
        }
    }

    /// <summary>
    /// <b>등록된 슬롯은 값이 있어도 깨끗하다</b> (B-05).
    ///
    /// <c>NpcSpawned.ExtA</c> = 인스턴스 정의 id 가 지금 유일한 등록 항목이다.
    /// 이 단언이 깨지면 등록부가 지워진 것이고, 그러면 동적 로스터가 조용히 멈춘다 —
    /// 스폰 이벤트의 "누구인가" 가 규약 위반으로 읽히기 때문이다.
    /// </summary>
    [Fact]
    public void Ext_NpcSpawnedSlotAIsRegistered()
    {
        Assert.True(ExtensionSlots.IsDefined(GameEventKind.NpcSpawned, ExtSlot.A));
        Assert.Contains(
            "인스턴스",
            ExtensionSlots.MeaningOf(GameEventKind.NpcSpawned, ExtSlot.A),
            StringComparison.Ordinal);

        var spawned = new GameEvent
        {
            Kind = GameEventKind.NpcSpawned,
            Sequence = 1,
            OccurredAt = new Tick(1),
            ExtA = 1234,
        };

        Assert.True(ExtensionSlots.IsClean(in spawned));

        // B 는 여전히 등록되지 않았다.
        Assert.False(ExtensionSlots.IsDefined(GameEventKind.NpcSpawned, ExtSlot.B));
        Assert.False(ExtensionSlots.IsClean(spawned with { ExtB = 1 }));
    }

    /// <summary>
    /// <see cref="InstanceId"/>·<see cref="FactionId"/> 는 예약 슬롯이 아니다 —
    /// 의미가 이미 정해져 있으므로 등록부를 거치지 않고, 값이 있어도 깨끗하다.
    /// </summary>
    [Fact]
    public void Ext_InstanceAndFactionAreNotReservedSlots()
    {
        var command = new NpcCommand
        {
            Kind = NpcCommandKind.MoveTo,
            Npc = new NpcId(1),
            IssuedAt = new Tick(1),
            Correlation = new CorrelationId(1),
            Priority = CommandPriority.Normal,
            Instance = new InstanceId(2),
            Faction = new FactionId(3),
        };

        Assert.True(ExtensionSlots.IsClean(in command));
    }

    // ---------------------------------------------------------------- 표본

    private static NpcCommand SampleCommand(NpcCommandKind kind, int seed) => new()
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
        Amount = -(seed + 11),
        Animation = new AnimationId((ushort)(seed + 12)),
        Dialogue = new DialogueId((ushort)(seed + 13)),
        Visual = VisualState.Working,
        Archetype = new ArchetypeId((ushort)(seed + 14)),
        Zone = new ZoneId((ushort)(seed + 15)),
        Flags = (byte)(seed + 16),
        Instance = new InstanceId((ushort)(seed + 17)),
        Faction = new FactionId((ushort)(seed + 18)),
        ExtA = (uint)seed + 19,
        ExtB = (uint)seed + 20,
    };

    private static GameEvent SampleEvent(GameEventKind kind, int seed) => new()
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
        Stamina = (short)-(seed + 16),
        Code = (byte)(seed + 17),
        Instance = new InstanceId((ushort)(seed + 18)),
        Faction = new FactionId((ushort)(seed + 19)),
        ExtA = (uint)seed + 20,
        ExtB = (uint)seed + 21,
    };
}
