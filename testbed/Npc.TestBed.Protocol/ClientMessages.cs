using MemoryPack;

namespace Npc.TestBed.Protocol;

/// <summary>
/// <see cref="Control"/> 이 시키는 일. docs/20 §8.3.
///
/// <b>번호를 재배치하지 않는다.</b> 모르는 값은 무시하고 세는 것이 규약이라(T6-25),
/// 재배치하면 낡은 클라이언트가 <b>다른 일을 시킨다</b> — 조용히, 그리고 그럴듯하게.
/// </summary>
public enum ControlKind : byte
{
    /// <summary>없음.</summary>
    None = 0,

    /// <summary>
    /// <c>ZoneCode</c> 존의 지역 상태를 <c>Code</c>(<c>RegionState</c>)로.
    /// <b>존 전체의 플랜이 갈린다</b> — 버킷 키의 축이기 때문이다.
    /// </summary>
    SetZoneState = 1,

    /// <summary><c>ZoneCode</c> 존의 기후를 <c>Code</c>(<c>Climate</c>)로.</summary>
    SetWeather = 2,

    /// <summary><c>Amount</c> 게임 분만큼 게임서버 틱을 건너뛴다. <b>부작용이 있다</b> — T6-25 주석.</summary>
    SkipTime = 3,

    /// <summary>고장 주입률. <c>Code</c> 0=fail · 1=drop, <c>Amount</c> 는 만분율(‱).</summary>
    SetFaultRate = 4,

    /// <summary><c>Amount</c> 번 NPC 를 despawn 한다. <c>TargetGone</c> 경로 확인용이다.</summary>
    Despawn = 5,
}

/// <summary>
/// 접속 인사. 클라이언트 → 서버, 1회. docs/20 §8.2.
///
/// <b>닉네임이 없다</b> (docs/20 §3.5). 플레이어가 쓴 문자열을 프로토콜에 넣지 않는 것이
/// 프롬프트 인젝션을 "넣지 마라" 가 아니라 "넣을 수 없다" 로 만드는 방법이다.
/// 나중에 닉네임이 필요하면 <b>클라이언트 프로토콜에만</b> 넣고 게임서버에서 끊는다.
/// </summary>
[MemoryPackable]
public partial struct CliHello
{
    /// <summary><see cref="ClientProtocol.Version"/>. 다르면 서버가 끊는다.</summary>
    public int ProtocolVersion;

    /// <summary>클라이언트 빌드 번호. 진단용이고 접속 판정에는 쓰지 않는다.</summary>
    public int ClientVersion;
}

/// <summary>
/// 이동 입력. 10Hz, 키가 눌린 동안만. docs/20 §8.2.
///
/// <b>방향이지 위치가 아니다.</b> 클라이언트가 위치를 보내면 그것이 곧 텔레포트 치트이고,
/// 무엇보다 서버와 클라이언트가 서로 다른 위치를 진실이라고 믿기 시작한다.
/// </summary>
[MemoryPackable]
public partial struct Input
{
    /// <summary>입력 순번. 서버는 쓰지 않고 클라이언트가 유실을 보는 데 쓴다.</summary>
    public int Seq;

    /// <summary>X 방향. 서버가 정규화한다 — 반쯤 기운 스틱이 반 속도가 되지 않는다.</summary>
    public float DirX;

    /// <summary>Z 방향.</summary>
    public float DirZ;

    /// <summary>달리기면 1. <c>bool</c> 대신 바이트다 — 크기가 고정된다.</summary>
    public byte Run;
}

/// <summary>대화. 거리 ≤ 30m 일 때만 듣는다. docs/20 §8.2 · §7.3.</summary>
[MemoryPackable]
public partial struct Interact
{
    /// <summary>대상 NPC 첨자.</summary>
    public int NpcId;
}

/// <summary>공격. 거리 ≤ 30m 일 때만 듣는다. docs/20 §8.2 · §7.3.</summary>
[MemoryPackable]
public partial struct Attack
{
    /// <summary>대상 NPC 첨자.</summary>
    public int NpcId;

    /// <summary>피해량. 서버가 1~100 으로 자른다.</summary>
    public int Amount;
}

/// <summary>선택. 로그 필터와 인스펙터의 대상이 된다. docs/20 §8.2.</summary>
[MemoryPackable]
public partial struct Select
{
    /// <summary>대상 NPC 첨자. 음수면 선택 해제다.</summary>
    public int NpcId;
}

/// <summary>
/// 시나리오 제어. docs/20 §8.3.
///
/// <b>필드 이름이 <see cref="Kind"/> 마다 다른 것을 뜻한다.</b> 판별 공용체를 struct 로
/// 표현한 것이고, 이는 <c>NpcCommand</c> 가 쓰는 방식과 같다 (docs/02 §3.2).
/// </summary>
[MemoryPackable]
public partial struct Control
{
    /// <summary>수량. <c>SkipTime</c> 은 게임 분, <c>SetFaultRate</c> 는 ‱, <c>Despawn</c> 은 NPC 첨자.</summary>
    public int Amount;

    /// <summary>대상 존. <c>SetZoneState</c>·<c>SetWeather</c> 만 쓴다.</summary>
    public ushort ZoneCode;

    /// <summary><see cref="ControlKind"/>.</summary>
    public byte Kind;

    /// <summary>Kind 별 소형 코드. <c>RegionState</c>·<c>Climate</c>·고장 종류가 여기 온다.</summary>
    public byte Code;
}

/// <summary>왕복 시간 측정. 1초. docs/20 §8.2.</summary>
[MemoryPackable]
public partial struct Ping
{
    /// <summary>
    /// 클라이언트가 정한 값. 서버는 <see cref="Pong"/> 에 그대로 돌려준다.
    ///
    /// <b>서버가 이 값을 해석하지 않는다.</b> 시각이든 순번이든 클라이언트 사정이고,
    /// 서버 쪽 시간은 <c>Tick</c> 뿐이라는 규약(N4)이 이것으로 지켜진다.
    /// </summary>
    public long ClientStamp;
}
