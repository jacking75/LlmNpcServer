using MemoryPack;
using Npc.Contracts;

namespace Npc.Wire;

/// <summary>
/// <see cref="NpcCommand"/> 의 와이어 표현. docs/20 §5.3.
///
/// <b>필드 순서는 사양의 것을 그대로 쓴다</b> — 큰 타입부터 늘어놓아 패딩 없이 56바이트가 나오는
/// 순서다. 누가 가운데에 필드를 끼워 넣으면 <c>Wire_LayoutIsFrozen</c> 이 먼저 깨진다.
///
/// <b>강타입 ID 를 원시 타입으로 편다</b> (N3 은 그대로다 — 문자열이 아니라 숫자다).
/// <see cref="NpcId"/> 같은 record struct 를 그대로 실으면 MemoryPack 이 그 타입에도
/// 속성을 요구하고, 그러면 <c>Npc.Contracts</c> 가 직렬화기에 묶인다 (docs/20 §3.1).
///
/// <b>unmanaged struct 다</b> (N2). 참조 필드·배열·<c>object</c> 가 없다.
/// </summary>
[MemoryPackable]
public partial struct WireCommand
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

    /// <summary>명령 종류. <see cref="NpcCommandKind"/>.</summary>
    public byte Kind;

    /// <summary>역압 시 드롭 우선순위. <see cref="CommandPriority"/>.</summary>
    public byte Priority;

    /// <summary>SetVisualState 의 상태. <see cref="VisualState"/>.</summary>
    public byte Visual;

    /// <summary>Kind 별 소형 파라미터.</summary>
    public byte Flags;

    /// <summary>도메인 → 와이어.</summary>
    public static WireCommand From(in NpcCommand c) => new()
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
        TargetPoi = c.TargetPoi.Value,
        Item = c.Item.Value,
        Animation = c.Animation.Value,
        Dialogue = c.Dialogue.Value,
        Archetype = c.Archetype.Value,
        Zone = c.Zone.Value,
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
    };
}
