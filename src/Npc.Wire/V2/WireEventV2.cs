using MemoryPack;
using Npc.Contracts;

namespace Npc.Wire.V2;

/// <summary>
/// <see cref="GameEvent"/> 의 v2 와이어 표현 (B-02).
///
/// <b>v1 <see cref="WireEvent"/> 는 64바이트로 동결돼 있다.</b> 확장 슬롯 4개와 꼬리 패딩을
/// 명시해 <b>80바이트</b>가 된다. <c>Reserved</c> 를 필드로 둔 이유는
/// <see cref="WireCommandV2"/> 요약에 있다.
///
/// <para>
/// <b><see cref="Sequence"/> 는 v1 과 같은 규약이다</b> — 게임서버 프로세스 수명 동안 순증하고
/// 재접속해도 리셋하지 않는다 (N6).
/// </para>
/// </summary>
[MemoryPackable]
public partial struct WireEventV2
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

    /// <summary>예약 슬롯 A (B-02). 의미는 <see cref="ExtensionSlots"/> 가 정한다.</summary>
    public uint ExtA;

    /// <summary>예약 슬롯 B (B-02).</summary>
    public uint ExtB;

    /// <summary>꼬리 패딩. <b>항상 0 이다.</b></summary>
    public uint Reserved;

    /// <summary>관련 POI.</summary>
    public ushort Poi;

    /// <summary>관련 존.</summary>
    public ushort Zone;

    /// <summary>관련 아이템.</summary>
    public ushort Item;

    /// <summary>채널·인스턴스 던전·레이어 (B-02). 0 = 기본 월드.</summary>
    public ushort Instance;

    /// <summary>관련 플레이어·NPC 의 세력 (B-02). 0 = 미지정.</summary>
    public ushort Faction;

    /// <summary>체력.</summary>
    public short Hp;

    /// <summary>기력.</summary>
    public short Stamina;

    /// <summary>이벤트 종류. <see cref="GameEventKind"/>.</summary>
    public byte Kind;

    /// <summary>Kind 별 소형 코드 (실패 사유·지역상태 등).</summary>
    public byte Code;

    /// <summary>도메인 → 와이어.</summary>
    public static WireEventV2 From(in GameEvent e) => new()
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
        ExtA = e.ExtA,
        ExtB = e.ExtB,
        Reserved = 0,
        Poi = e.Poi.Value,
        Zone = e.Zone.Value,
        Item = e.Item.Value,
        Instance = e.Instance.Value,
        Faction = e.Faction.Value,
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
        Instance = new InstanceId(Instance),
        Faction = new FactionId(Faction),
        ExtA = ExtA,
        ExtB = ExtB,
    };
}
