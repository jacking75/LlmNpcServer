namespace Npc.Contracts;

/// <summary>
/// NPC 서버 → 게임서버 명령 종류. docs/02 §3.2.
/// 번호를 재배치하지 않는다 — 기록된 리플레이 로그가 통째로 깨진다.
/// </summary>
public enum NpcCommandKind : byte
{
    Spawn = 1,
    Despawn,
    MoveTo,
    Stop,
    FaceTo,
    PlayAnimation,
    SetVisualState,
    Interact,
    Speak,
    InventoryChange,
    CombatAction,
    SetAggro,
}

/// <summary>
/// 역압(backpressure) 시 드롭 우선순위. docs/02 §1.
/// 채널이 포화되면 Cosmetic 부터 버린다. Critical 은 무손실이어야 한다.
/// </summary>
public enum CommandPriority : byte
{
    Critical = 0,
    Normal = 1,
    Cosmetic = 2,
}

/// <summary>NPC 의 겉보기 상태. 게임서버가 애니메이션·모델 상태로 옮긴다.</summary>
public enum VisualState : byte
{
    Idle,
    Walking,
    Running,
    Working,
    Fighting,
    Sleeping,
    Sitting,
    Dead,
}

/// <summary><see cref="NpcCommandKind.CombatAction"/> 의 Flags 필드가 담는 행동 종류.</summary>
public enum CombatActionKind : byte
{
    None = 0,
    MeleeAttack = 1,
    RangedAttack = 2,
    Block = 3,
    Dodge = 4,
}

/// <summary><see cref="NpcCommandKind.MoveTo"/> 의 Flags 필드가 담는 이동 속도 등급.</summary>
public enum MoveSpeed : byte
{
    Walk = 0,
    Run = 1,
}

/// <summary>
/// NPC 서버가 발행하는 명령. docs/02 §3.2.
///
/// 판별 공용체를 struct 로 표현한다. 필드 유니온(explicit layout) 대신 명시 필드를 쓴다 —
/// 직렬화기가 단순해지고 디버깅이 쉽다. Kind 에 따라 사용하는 필드가 달라진다.
///
/// N1: 이 명령은 fire-and-forget 이다. 결과는 GameEvent 로만 돌아온다.
/// N3: string 필드가 없다. 전부 강타입 ID 다.
/// N4: 시간은 Tick 뿐이다.
/// N5: Correlation 은 필수다.
/// </summary>
public readonly record struct NpcCommand
{
    // --- 헤더 (모든 명령 공통) ---

    /// <summary>명령 종류. 아래 페이로드 중 어느 필드가 유효한지를 결정한다.</summary>
    public required NpcCommandKind Kind { get; init; }

    /// <summary>대상 NPC.</summary>
    public required NpcId Npc { get; init; }

    /// <summary>발행 틱 (N4).</summary>
    public required Tick IssuedAt { get; init; }

    /// <summary>상관 ID (N5). 응답 이벤트가 이 값으로 돌아온다.</summary>
    public required CorrelationId Correlation { get; init; }

    /// <summary>역압 시 드롭 우선순위.</summary>
    public required CommandPriority Priority { get; init; }

    // --- 페이로드 (Kind 에 따라 사용 필드가 달라짐) ---

    /// <summary>MoveTo · Interact · Defend/Guard 계열의 목표 POI.</summary>
    public PoiId TargetPoi { get; init; }

    /// <summary>Spawn · MoveTo · FaceTo 의 목표 좌표.</summary>
    public WorldPos TargetPos { get; init; }

    /// <summary>FaceTo · Interact · Speak · CombatAction 의 대상 NPC.</summary>
    public NpcId TargetNpc { get; init; }

    /// <summary>CombatAction 의 대상 플레이어.</summary>
    public PlayerId TargetPlayer { get; init; }

    /// <summary>Interact · InventoryChange 의 대상 아이템.</summary>
    public ItemId Item { get; init; }

    /// <summary>수량. InventoryChange 에서는 부호가 있다(음수 = 차감). PlayAnimation 에서는 반복 횟수.</summary>
    public int Amount { get; init; }

    /// <summary>PlayAnimation 의 애니메이션.</summary>
    public AnimationId Animation { get; init; }

    /// <summary>Speak 의 대사 ID. 자연어 생성은 범위 밖이다 (N3).</summary>
    public DialogueId Dialogue { get; init; }

    /// <summary>SetVisualState 의 상태.</summary>
    public VisualState Visual { get; init; }

    /// <summary>Spawn 의 아키타입.</summary>
    public ArchetypeId Archetype { get; init; }

    /// <summary>Spawn 의 존.</summary>
    public ZoneId Zone { get; init; }

    /// <summary>
    /// Kind 별 소형 파라미터.
    /// MoveTo → <see cref="MoveSpeed"/>, CombatAction → <see cref="CombatActionKind"/>,
    /// SetAggro → 0/1.
    /// </summary>
    public byte Flags { get; init; }

    // --- 확장 슬롯 (B-02) ---
    //
    // <b>v1 링크에서는 이 네 필드가 버려진다.</b> 게임서버가 LinkFeatures.ExtSlots 를 켜야
    // 와이어에 실린다 — 켜지 않으면 v1 DTO 로 나가고 값은 조용히 사라진다.

    /// <summary>채널·인스턴스 던전·레이어 (B-02). 0 = 기본 월드.</summary>
    public InstanceId Instance { get; init; }

    /// <summary><c>SetAggro</c>·<c>CombatAction</c> 의 대상 세력 (B-02). 0 = 미지정.</summary>
    public FactionId Faction { get; init; }

    /// <summary>
    /// 예약 슬롯 A (B-02). <b>Kind 별 의미는 <see cref="ExtensionSlots"/> 가 정한다.</b>
    /// 정의되지 않은 Kind 에서는 0 이어야 하고 테스트가 그것을 강제한다.
    /// </summary>
    public uint ExtA { get; init; }

    /// <summary>예약 슬롯 B (B-02). <see cref="ExtA"/> 와 같은 규칙이다.</summary>
    public uint ExtB { get; init; }
}
