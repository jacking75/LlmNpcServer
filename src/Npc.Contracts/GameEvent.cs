namespace Npc.Contracts;

/// <summary>
/// 게임서버 → NPC 서버 통보 종류. docs/02 §3.3.
/// 번호를 재배치하지 않는다 — 기록된 리플레이 로그가 통째로 깨진다.
/// </summary>
public enum GameEventKind : byte
{
    TickSync = 1,
    GameTimeChanged,
    NpcSpawned,
    NpcDespawned,
    NpcTransform,
    NpcArrived,
    NpcActionCompleted,
    NpcActionFailed,
    NpcVitalsChanged,
    NpcInventoryChanged,
    PlayerProximity,
    PlayerInteracted,
    CombatStarted,
    CombatEnded,
    DamageTaken,
    ZoneStateChanged,
    WeatherChanged,

    /// <summary>
    /// 플레이어의 적대 여부가 바뀌었다 (B-06). <c>Code</c> 는 <see cref="Hostility"/>.
    ///
    /// <para>
    /// <b>적대 판정은 게임서버가 한다.</b> 세력·PK 상태·퀘스트 진행이 섞인 판단이고 그것은
    /// 전부 게임서버의 자료다 — NPC 서버는 결과만 받는다. 우리가 판정하려 들면
    /// 세력 테이블을 두 쪽에서 관리해야 하고, 어긋난 순간 "경비병이 아군을 공격한다" 가 된다.
    /// </para>
    ///
    /// <para>
    /// 발행 규약은 근접과 같다 — <b>NPC 당 1건 · 에지 트리거 · 히스테리시스.</b>
    /// 상태가 안 바뀐 NPC 에 이벤트를 내면 재계획 큐가 포화한다.
    /// </para>
    /// </summary>
    PlayerHostility,
}

/// <summary>
/// <see cref="GameEventKind.PlayerHostility"/> 의 <c>Code</c> 필드 (B-06).
///
/// <b>번호를 재배치하지 않는다.</b> 0 이 "중립" 인 것은 <c>default</c> 가 안전한 쪽이어야
/// 하기 때문이다 — 값을 안 실은 이벤트가 적대로 읽히면 경비병이 아무나 공격한다.
/// </summary>
public enum Hostility : byte
{
    /// <summary>중립. 적대 플래그를 내린다.</summary>
    Neutral = 0,

    /// <summary>적대. <c>HostilePlayerNearby</c> 를 세운다.</summary>
    Hostile = 1,

    /// <summary>우호. 중립과 같이 플래그를 내린다 — 구분은 앞으로의 일이다.</summary>
    Friendly = 2,
}

/// <summary>
/// <see cref="GameEventKind.NpcActionFailed"/> 의 Code 필드가 담는 실패 사유. docs/02 §3.3.
/// Timeout 은 게임서버가 보내는 것이 아니라 명령 유실 시 NPC 서버가 로컬에서 합성한다.
/// </summary>
public enum ActionFailReason : byte
{
    None = 0,
    Unreachable,
    Timeout,
    Interrupted,
    PreconditionFailed,
    TargetGone,
    InventoryFull,
    InsufficientResource,
    Dead,
    Rejected,
}

/// <summary><see cref="GameEventKind.PlayerProximity"/> 의 Code 필드.</summary>
public enum ProximityChange : byte
{
    Enter = 0,
    Leave = 1,
}

/// <summary>
/// NPC 서버가 수신하는 통보. docs/02 §3.3.
///
/// N6: Sequence 는 필수다. 수신 측이 유실·중복을 검출한다.
/// N7: 처리는 멱등이어야 한다. 같은 이벤트를 두 번 받아도 상태가 같아야 한다.
/// 상관 없는 이벤트(NpcTransform, DamageTaken 등)의 Correlation 은 default 다.
/// </summary>
public readonly record struct GameEvent
{
    // --- 헤더 (모든 이벤트 공통) ---

    /// <summary>이벤트 종류. 아래 페이로드 중 어느 필드가 유효한지를 결정한다.</summary>
    public required GameEventKind Kind { get; init; }

    /// <summary>순증 시퀀스 번호 (N6). 갭이 생기면 링크가 경보를 올린다.</summary>
    public required long Sequence { get; init; }

    /// <summary>발생 틱 (N4).</summary>
    public required Tick OccurredAt { get; init; }

    // --- 페이로드 ---

    /// <summary>대상 NPC.</summary>
    public NpcId Npc { get; init; }

    /// <summary>명령 응답일 때의 상관 ID (N5). 아니면 default.</summary>
    public CorrelationId Correlation { get; init; }

    /// <summary>NpcTransform · NpcSpawned 의 위치.</summary>
    public WorldPos Pos { get; init; }

    /// <summary>NpcTransform 의 방향(라디안).</summary>
    public float Heading { get; init; }

    /// <summary>NpcArrived 의 도착 POI.</summary>
    public PoiId Poi { get; init; }

    /// <summary>ZoneStateChanged · WeatherChanged 의 대상 존.</summary>
    public ZoneId Zone { get; init; }

    /// <summary>PlayerProximity · PlayerInteracted 의 플레이어.</summary>
    public PlayerId Player { get; init; }

    /// <summary>CombatStarted · DamageTaken 의 상대 NPC.</summary>
    public NpcId OtherNpc { get; init; }

    /// <summary>NpcInventoryChanged 의 아이템.</summary>
    public ItemId Item { get; init; }

    /// <summary>
    /// 수량. NpcInventoryChanged 에서는 부호가 있다(음수 = 차감).
    /// PlayerProximity 에서는 거리(m), DamageTaken 에서는 피해량.
    /// </summary>
    public int Amount { get; init; }

    /// <summary>NpcVitalsChanged 의 HP.</summary>
    public short Hp { get; init; }

    /// <summary>NpcVitalsChanged 의 스태미나.</summary>
    public short Stamina { get; init; }

    /// <summary>
    /// Kind 별 소형 코드.
    /// NpcActionFailed → <see cref="ActionFailReason"/>,
    /// PlayerProximity → <see cref="ProximityChange"/>,
    /// GameTimeChanged → TimeOfDay, ZoneStateChanged → RegionState, WeatherChanged → Climate.
    /// </summary>
    public byte Code { get; init; }

    // --- 확장 슬롯 (B-02) ---
    //
    // <b>v1 링크에서는 이 네 필드가 버려진다.</b> 게임서버가 LinkFeatures.ExtSlots 를 켜야
    // 와이어에 실린다 — 켜지 않으면 v1 DTO 로 나가고 값은 조용히 사라진다.

    /// <summary>채널·인스턴스 던전·레이어 (B-02). 0 = 기본 월드.</summary>
    public InstanceId Instance { get; init; }

    /// <summary>관련 플레이어·NPC 의 세력 (B-02). 0 = 미지정.</summary>
    public FactionId Faction { get; init; }

    /// <summary>
    /// 예약 슬롯 A (B-02). <b>Kind 별 의미는 <see cref="ExtensionSlots"/> 가 정한다.</b>
    /// 정의되지 않은 Kind 에서는 0 이어야 하고 테스트가 그것을 강제한다.
    /// </summary>
    public uint ExtA { get; init; }

    /// <summary>예약 슬롯 B (B-02). <see cref="ExtA"/> 와 같은 규칙이다.</summary>
    public uint ExtB { get; init; }
}
