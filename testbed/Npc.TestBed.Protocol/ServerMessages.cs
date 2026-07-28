using MemoryPack;
using Npc.Wire;

namespace Npc.TestBed.Protocol;

/// <summary><see cref="EntityState.Kind"/> 값. docs/20 §8.1.</summary>
public enum EntityKind : byte
{
    /// <summary>NPC. <see cref="EntityState.Id"/> 는 런타임 첨자다.</summary>
    Npc = 0,

    /// <summary>플레이어. <see cref="EntityState.Id"/> 는 <c>PlayerId</c> 다.</summary>
    Player = 1,
}

/// <summary><see cref="EntityState.StateFlags"/> 의 비트. docs/20 §8.1.</summary>
[Flags]
public enum EntityFlags : byte
{
    /// <summary>없음.</summary>
    None = 0,

    /// <summary>bit0 — 이동 중.</summary>
    Moving = 1 << 0,

    /// <summary>bit1 — 작업 중.</summary>
    Working = 1 << 1,

    /// <summary>bit2 — 전투 중.</summary>
    InCombat = 1 << 2,

    /// <summary>bit3 — 플레이어에게 관측 중. 인지 LOD 가 올라간 상태다.</summary>
    Observed = 1 << 3,
}

/// <summary>
/// 스냅샷 한 칸. docs/20 §8.1.
///
/// <para>
/// <b>정확히 32바이트다</b>(30바이트 + 정렬 패딩 2). 필드 순서를 바꾸면 크기가 달라지고,
/// 그러면 MemoryPack 의 unmanaged 배열 고속 경로가 내는 바이트 수가 바뀐다 —
/// <c>ClientProtocol_EntityLayoutIsFrozen</c> 이 먼저 깨진다.
/// </para>
///
/// <para>
/// <b>y 좌표가 없다.</b> 이 지도는 평면이고(<c>pois.json</c> 은 y 가 전부 0),
/// 클라이언트는 위에서 내려다본 2D 로 그린다 (docs/20 §9.3).
/// </para>
/// </summary>
[MemoryPackable]
public partial struct EntityState
{
    /// <summary>NPC 첨자 또는 <c>PlayerId</c>. 어느 쪽인지는 <see cref="Kind"/> 가 정한다.</summary>
    public int Id;

    /// <summary>X 좌표.</summary>
    public float X;

    /// <summary>Z 좌표.</summary>
    public float Z;

    /// <summary>바라보는 방향(라디안).</summary>
    public float Heading;

    /// <summary>지금 있는 POI code. 0 이면 이동 중이다.</summary>
    public ushort Poi;

    /// <summary>이동 목적지 POI code. 0 이면 정지. <b>클라이언트가 이 값으로 이동선을 그린다.</b></summary>
    public ushort TargetPoi;

    /// <summary>존 code.</summary>
    public ushort Zone;

    /// <summary>아키타입 code. 클라이언트가 색을 여기서 고른다 (docs/20 §9.3).</summary>
    public ushort Archetype;

    /// <summary>HP.</summary>
    public short Hp;

    /// <summary><see cref="EntityKind"/>.</summary>
    public byte Kind;

    /// <summary><c>Npc.Contracts.VisualState</c>. 클라이언트가 테두리를 여기서 고른다.</summary>
    public byte Visual;

    /// <summary><see cref="EntityFlags"/>.</summary>
    public byte StateFlags;

    /// <summary>정렬 패딩. <b>지금은 0 이다</b> — 필드를 늘려야 할 때 여기부터 쓴다.</summary>
    public byte Reserved;
}

/// <summary>존 하나의 상태. docs/20 §8.1 의 <c>ZoneStates</c> 원소다.</summary>
[MemoryPackable]
public partial struct ZoneState
{
    /// <summary>존 code.</summary>
    public ushort ZoneCode;

    /// <summary><c>Npc.Contracts.RegionState</c>.</summary>
    public byte RegionState;

    /// <summary><c>Npc.Contracts.Climate</c>.</summary>
    public byte Climate;
}

/// <summary>명령 로그 한 줄. docs/20 §7.4 · §8.1.</summary>
[MemoryPackable]
public partial struct LogCommand
{
    /// <summary>발행 틱 (N4).</summary>
    public long Tick;

    /// <summary>대상 NPC 첨자.</summary>
    public int Npc;

    /// <summary>상관 ID (N5). 로그 패널이 명령과 응답 이벤트를 잇는 유일한 끈이다.</summary>
    public uint Correlation;

    /// <summary>목표 POI code. 없으면 0.</summary>
    public ushort TargetPoi;

    /// <summary><c>Npc.Contracts.NpcCommandKind</c>.</summary>
    public byte Kind;
}

/// <summary>이벤트 로그 한 줄. docs/20 §7.4 · §8.1.</summary>
[MemoryPackable]
public partial struct LogEvent
{
    /// <summary>발생 틱.</summary>
    public long Tick;

    /// <summary>대상 NPC 첨자.</summary>
    public int Npc;

    /// <summary>수량. 근접이면 거리(m), 피해면 피해량.</summary>
    public int Amount;

    /// <summary><c>Npc.Contracts.GameEventKind</c>.</summary>
    public byte Kind;

    /// <summary>Kind 별 소형 코드. <c>NpcActionFailed</c> 면 <c>ActionFailReason</c> 이다.</summary>
    public byte Code;
}

/// <summary>
/// 접속 인사. 서버 → 클라이언트, 1회. docs/20 §8.1.
///
/// <b>월드 경계가 여기 실린다.</b> 클라이언트가 <c>Home</c> 키로 화면을 맞추려면
/// 지도가 어디까지인지 알아야 하고, 그것을 마스터데이터에서 두 번 계산하면
/// 서버와 어긋난 화면이 나온다.
/// </summary>
[MemoryPackable]
public partial struct SrvHello
{
    /// <summary><see cref="ClientProtocol.Version"/>.</summary>
    public int ProtocolVersion;

    /// <summary>틱 레이트(Hz). 10 이다.</summary>
    public int TickRate;

    /// <summary>게임서버의 타임스케일. 클라이언트가 플레이어 속도를 이 값으로 이해한다.</summary>
    public int TimeScale;

    /// <summary>이 세션에 배정된 <c>PlayerId</c>.</summary>
    public int PlayerId;

    /// <summary>로스터 크기.</summary>
    public int NpcCount;

    /// <summary>월드 경계 최소 X.</summary>
    public float MinX;

    /// <summary>월드 경계 최소 Z.</summary>
    public float MinZ;

    /// <summary>월드 경계 최대 X.</summary>
    public float MaxX;

    /// <summary>월드 경계 최대 Z.</summary>
    public float MaxZ;

    /// <summary>
    /// 마스터데이터 콘텐츠 해시.
    ///
    /// <b>클라이언트도 확인한다.</b> 다른 마스터데이터로 그리면 POI 이름과 위치가
    /// 어긋난 채 그럴듯한 화면이 나오고, 그게 가장 찾기 어려운 종류의 사고다.
    /// </summary>
    public WireHash MasterData;
}

/// <summary>
/// AOI 스냅샷. 2틱(5Hz). docs/20 §8.1.
///
/// <b><see cref="EntityCount"/> 가 진실이다.</b> <see cref="Entities"/> 가 그보다 길 수 있다 —
/// 송신 측이 고정 버퍼를 재사용해도 프로토콜을 바꾸지 않아도 되게 둔 여지다.
/// 받는 쪽은 앞에서 <see cref="EntityCount"/> 개만 읽는다.
/// </summary>
[MemoryPackable]
public partial struct Snapshot
{
    /// <summary>이 스냅샷의 틱 (N4).</summary>
    public long Tick;

    /// <summary>게임 날짜(일).</summary>
    public int GameDay;

    /// <summary>게임 시각(시). 0~23.</summary>
    public byte GameHour;

    /// <summary><c>Npc.Contracts.TimeOfDay</c>.</summary>
    public byte TimeOfDay;

    /// <summary>유효한 엔티티 수. AOI 상한 256 이하다.</summary>
    public int EntityCount;

    /// <summary>엔티티. unmanaged 배열이라 원소마다 태그가 붙지 않는다 (docs/20 §5.2).</summary>
    public EntityState[]? Entities;
}

/// <summary>존 상태 전체. 변화 시 + 5초마다. docs/20 §8.1.</summary>
[MemoryPackable]
public partial struct ZoneStates
{
    /// <summary>이 상태가 관측된 틱.</summary>
    public long Tick;

    /// <summary>존별 상태. 존 수만큼이라 짧다.</summary>
    public ZoneState[]? Zones;
}

/// <summary>명령 로그 배치. 2틱. docs/20 §8.1.</summary>
[MemoryPackable]
public partial struct CommandLog
{
    /// <summary>줄. 오래된 것부터다.</summary>
    public LogCommand[]? Commands;
}

/// <summary>이벤트 로그 배치. 2틱. docs/20 §8.1.</summary>
[MemoryPackable]
public partial struct EventLog
{
    /// <summary>줄. 오래된 것부터다.</summary>
    public LogEvent[]? Events;
}

/// <summary>
/// NPC 서버 링크의 상태. 1초. docs/20 §8.1.
///
/// <b>상태바가 이것으로 "링크 지연" 을 보여 준다</b> —
/// <see cref="GsTick"/> − <see cref="NpcServerTick"/> 이 그 값이다 (docs/20 §9.2).
/// </summary>
[MemoryPackable]
public partial struct LinkStatus
{
    /// <summary>게임서버의 현재 틱.</summary>
    public long GsTick;

    /// <summary>NPC 서버가 마지막으로 알린 틱.</summary>
    public long NpcServerTick;

    /// <summary>받은 명령 수.</summary>
    public long CommandsIn;

    /// <summary>보낸 이벤트 수.</summary>
    public long EventsOut;

    /// <summary>버린 명령 수.</summary>
    public long Dropped;

    /// <summary>시퀀스 갭 누계 (N6). 0 이 아니면 경보다.</summary>
    public long Gaps;

    /// <summary>NPC 서버가 붙어 있으면 1. <c>bool</c> 대신 바이트다 — 크기가 고정된다.</summary>
    public byte Connected;
}

/// <summary><see cref="Ping"/> 응답. docs/20 §8.1.</summary>
[MemoryPackable]
public partial struct Pong
{
    /// <summary>클라이언트가 보낸 값 그대로. 왕복 시간은 클라이언트가 잰다.</summary>
    public long ClientStamp;

    /// <summary>서버의 현재 틱 (N4).</summary>
    public long ServerTick;
}
