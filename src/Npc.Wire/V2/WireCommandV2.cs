using MemoryPack;
using Npc.Contracts;

namespace Npc.Wire.V2;

/// <summary>
/// <see cref="NpcCommand"/> 의 v2 와이어 표현 (B-02).
///
/// <b>v1 <see cref="WireCommand"/> 는 건드리지 않는다.</b> 56바이트로 동결돼 있고,
/// v1 게임서버가 그 배치를 그대로 읽는다. 확장은 새 타입으로만 한다.
///
/// <para>
/// <b>72바이트다.</b> 로드맵 초안은 64 를 권했지만 산술이 맞지 않는다 —
/// v1 이 56B(패딩 0)이고 <c>Instance</c>(2)·<c>Faction</c>(2)·<c>ExtA</c>(4)·<c>ExtB</c>(4)
/// 를 더하면 68B, 정렬 8 이라 72B 다. 64 에 넣으려면 슬롯 하나를 버려야 한다.
/// </para>
///
/// <para>
/// <b><c>Reserved</c> 는 꼬리 패딩을 명시한 것이다.</b> MemoryPack 은 unmanaged struct 를
/// 원시 복사하므로 암묵 패딩이 있으면 <b>초기화되지 않은 바이트가 그대로 소켓에 나간다</b> —
/// 골든 바이트 벡터(B-03)가 회차마다 달라지고, 다른 언어 구현이 그 바이트를 해석하려 든다.
/// 필드로 만들어 0 으로 못 박는다.
/// </para>
/// </summary>
[MemoryPackable]
public partial struct WireCommandV2
{
    /// <summary>발행 틱 (N4). <see cref="Tick"/> 의 원시값.</summary>
    public long IssuedAt;

    /// <summary>대상 NPC.</summary>
    public int Npc;

    /// <summary>FaceTo · Interact · Speak · CombatAction 의 대상 NPC.</summary>
    public int TargetNpc;

    /// <summary>CombatAction 의 대상 플레이어.</summary>
    public int TargetPlayer;

    /// <summary>수량. InventoryChange 에서는 부호가 있다.</summary>
    public int Amount;

    /// <summary>상관 ID (N5).</summary>
    public uint Correlation;

    /// <summary>목표 좌표 X. <see cref="WorldPos"/> 를 편 것이다.</summary>
    public float PosX;

    /// <summary>목표 좌표 Y.</summary>
    public float PosY;

    /// <summary>목표 좌표 Z.</summary>
    public float PosZ;

    /// <summary>예약 슬롯 A (B-02). 의미는 <see cref="ExtensionSlots"/> 가 정한다.</summary>
    public uint ExtA;

    /// <summary>예약 슬롯 B (B-02).</summary>
    public uint ExtB;

    /// <summary>꼬리 패딩. <b>항상 0 이다</b> — 위 요약의 이유로 필드로 만들었다.</summary>
    public uint Reserved;

    /// <summary>목표 POI.</summary>
    public ushort TargetPoi;

    /// <summary>대상 아이템.</summary>
    public ushort Item;

    /// <summary>PlayAnimation 의 애니메이션.</summary>
    public ushort Animation;

    /// <summary>Speak 의 대사 ID. 자연어가 아니라 ID 다 (N3).</summary>
    public ushort Dialogue;

    /// <summary>Spawn 의 아키타입.</summary>
    public ushort Archetype;

    /// <summary>Spawn 의 존.</summary>
    public ushort Zone;

    /// <summary>채널·인스턴스 던전·레이어 (B-02). 0 = 기본 월드.</summary>
    public ushort Instance;

    /// <summary>대상 세력 (B-02). 0 = 미지정.</summary>
    public ushort Faction;

    /// <summary>명령 종류. <see cref="NpcCommandKind"/>.</summary>
    public byte Kind;

    /// <summary>역압 시 드롭 우선순위. <see cref="CommandPriority"/>.</summary>
    public byte Priority;

    /// <summary>SetVisualState 의 상태. <see cref="VisualState"/>.</summary>
    public byte Visual;

    /// <summary>Kind 별 소형 파라미터.</summary>
    public byte Flags;

    /// <summary>도메인 → 와이어.</summary>
    public static WireCommandV2 From(in NpcCommand c) => new()
    {
        IssuedAt = c.IssuedAt.Value,
        Npc = c.Npc.Value,
        TargetNpc = c.TargetNpc.Value,
        TargetPlayer = c.TargetPlayer.Value,
        Amount = c.Amount,
        Correlation = c.Correlation.Value,
        PosX = c.TargetPos.X,
        PosY = c.TargetPos.Y,
        PosZ = c.TargetPos.Z,
        ExtA = c.ExtA,
        ExtB = c.ExtB,
        Reserved = 0,
        TargetPoi = c.TargetPoi.Value,
        Item = c.Item.Value,
        Animation = c.Animation.Value,
        Dialogue = c.Dialogue.Value,
        Archetype = c.Archetype.Value,
        Zone = c.Zone.Value,
        Instance = c.Instance.Value,
        Faction = c.Faction.Value,
        Kind = (byte)c.Kind,
        Priority = (byte)c.Priority,
        Visual = (byte)c.Visual,
        Flags = c.Flags,
    };

    /// <summary>와이어 → 도메인.</summary>
    public readonly NpcCommand To() => new()
    {
        Kind = (NpcCommandKind)Kind,
        Npc = new NpcId(Npc),
        IssuedAt = new Tick(IssuedAt),
        Correlation = new CorrelationId(Correlation),
        Priority = (CommandPriority)Priority,
        TargetPoi = new PoiId(TargetPoi),
        TargetPos = new WorldPos(PosX, PosY, PosZ),
        TargetNpc = new NpcId(TargetNpc),
        TargetPlayer = new PlayerId(TargetPlayer),
        Item = new ItemId(Item),
        Amount = Amount,
        Animation = new AnimationId(Animation),
        Dialogue = new DialogueId(Dialogue),
        Visual = (VisualState)Visual,
        Archetype = new ArchetypeId(Archetype),
        Zone = new ZoneId(Zone),
        Flags = Flags,
        Instance = new InstanceId(Instance),
        Faction = new FactionId(Faction),
        ExtA = ExtA,
        ExtB = ExtB,
    };
}
