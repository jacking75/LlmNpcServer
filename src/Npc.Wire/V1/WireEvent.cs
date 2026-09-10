using MemoryPack;
using Npc.Contracts;

// v1 이벤트 DTO. <b>이 파일은 수정하지 않는다</b> (B-01). 새 필드는 V2/ 에만 넣는다.
namespace Npc.Wire;

/// <summary>
/// <see cref="GameEvent"/> 의 와이어 표현. docs/20 §5.3.
///
/// <b>필드 순서는 사양의 것을 그대로 쓴다</b> — 패딩 없이 64바이트가 나오는 순서다.
/// <c>Wire_LayoutIsFrozen</c> 이 크기를 못 박는다.
///
/// <b><see cref="Sequence"/> 는 게임서버 프로세스 수명 동안 순증한다</b> (N6 · docs/20 §5.6).
/// 재접속해도 리셋하지 않는다 — 리셋하면 <c>EventApplier</c> 가 전부 중복으로 버린다.
/// </summary>
[MemoryPackable]
public partial struct WireEvent
{
    /// <summary>이벤트 시퀀스 (N6). 갭이 나면 경보다.</summary>
    public long Sequence;

    /// <summary>발생 틱 (N4). <see cref="Tick"/> 의 원시값.</summary>
    public long OccurredAt;

    /// <summary>이 이벤트의 주체 NPC.</summary>
    public int Npc;

    /// <summary>상호작용 상대 NPC.</summary>
    public int OtherNpc;

    /// <summary>관련 플레이어.</summary>
    public int Player;

    /// <summary>수량.</summary>
    public int Amount;

    /// <summary>어느 명령에 대한 응답인가 (N5).</summary>
    public uint Correlation;

    /// <summary>좌표 X. <see cref="WorldPos"/> 를 편 것이다.</summary>
    public float PosX;

    /// <summary>좌표 Y.</summary>
    public float PosY;

    /// <summary>좌표 Z.</summary>
    public float PosZ;

    /// <summary>바라보는 방향.</summary>
    public float Heading;

    /// <summary>관련 POI.</summary>
    public ushort Poi;

    /// <summary>관련 존.</summary>
    public ushort Zone;

    /// <summary>관련 아이템.</summary>
    public ushort Item;

    /// <summary>체력.</summary>
    public short Hp;

    /// <summary>기력.</summary>
    public short Stamina;

    /// <summary>이벤트 종류. <see cref="GameEventKind"/>.</summary>
    public byte Kind;

    /// <summary>Kind 별 소형 코드 (실패 사유·지역상태 등).</summary>
    public byte Code;

    /// <summary>도메인 → 와이어.</summary>
    public static WireEvent From(in GameEvent e) => new()
    {
        Sequence = e.Sequence,
        OccurredAt = e.OccurredAt.Value,
        Npc = e.Npc.Value,
        OtherNpc = e.OtherNpc.Value,
        Player = e.Player.Value,
        Amount = e.Amount,
        Correlation = e.Correlation.Value,
        PosX = e.Pos.X,
        PosY = e.Pos.Y,
        PosZ = e.Pos.Z,
        Heading = e.Heading,
        Poi = e.Poi.Value,
        Zone = e.Zone.Value,
        Item = e.Item.Value,
        Hp = e.Hp,
        Stamina = e.Stamina,
        Kind = (byte)e.Kind,
        Code = e.Code,
    };

    /// <summary>와이어 → 도메인.</summary>
    public readonly GameEvent To() => new()
    {
        Kind = (GameEventKind)Kind,
        Sequence = Sequence,
        OccurredAt = new Tick(OccurredAt),
        Npc = new NpcId(Npc),
        Correlation = new CorrelationId(Correlation),
        Pos = new WorldPos(PosX, PosY, PosZ),
        Heading = Heading,
        Poi = new PoiId(Poi),
        Zone = new ZoneId(Zone),
        Player = new PlayerId(Player),
        OtherNpc = new NpcId(OtherNpc),
        Item = new ItemId(Item),
        Amount = Amount,
        Hp = Hp,
        Stamina = Stamina,
        Code = Code,
    };
}
