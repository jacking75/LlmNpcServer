namespace Npc.Contracts;

// docs/02 §3.1 — 강타입 ID.
// 오용 방지(NpcId 자리에 PoiId 를 못 넣는다) + 직렬화 시에는 원시값 그대로 나간다.
// 전부 readonly record struct 다 (N2). 참조 필드·문자열(N3)·DateTime(N4) 은 없다.

/// <summary>NPC 인스턴스 식별자. npc_instances.json 의 id.</summary>
public readonly record struct NpcId(int Value);

/// <summary>플레이어 식별자. 게임서버가 부여한다.</summary>
public readonly record struct PlayerId(int Value);

/// <summary>아키타입 식별자. archetypes.json 의 code. 0..39.</summary>
public readonly record struct ArchetypeId(ushort Value);

/// <summary>존 식별자. zones.json 의 code.</summary>
public readonly record struct ZoneId(ushort Value);

/// <summary>관심지점 식별자. pois.json 의 code. 거리 행렬의 첨자이기도 하다.</summary>
public readonly record struct PoiId(ushort Value);

/// <summary>액션 식별자. actions.json 의 code. ActionCatalog 배열의 첨자다.</summary>
public readonly record struct ActionId(ushort Value);

/// <summary>아이템 식별자. items.json 의 code. 인벤토리 배열의 첨자다.</summary>
public readonly record struct ItemId(ushort Value);

/// <summary>대사 식별자. 자연어 문자열은 패킷에 싣지 않는다 (N3).</summary>
public readonly record struct DialogueId(ushort Value);

/// <summary>애니메이션 식별자.</summary>
public readonly record struct AnimationId(ushort Value);

/// <summary>
/// 채널·인스턴스 던전·레이어 식별자 (B-02). <b>0 = 기본 월드다.</b>
///
/// 같은 좌표에 있어도 인스턴스가 다르면 서로 보이지 않는다. NPC 서버는 이 값을
/// <b>해석하지 않고 되돌려준다</b> — 무엇이 인스턴스인가는 게임서버의 개념이다.
/// </summary>
public readonly record struct InstanceId(ushort Value);

/// <summary>
/// 세력 식별자 (B-02). <b>0 = 미지정이다.</b>
///
/// 명령에서는 <c>SetAggro</c>·<c>CombatAction</c> 의 대상 세력, 이벤트에서는 플레이어의 세력이다.
/// <b>세력 테이블 자체는 아직 없다</b> (D-04) — 지금은 값이 통과만 한다.
/// </summary>
public readonly record struct FactionId(ushort Value);

/// <summary>플랜 식별자. PlanStore 의 첨자.</summary>
public readonly record struct PlanId(int Value);

/// <summary>
/// 명령↔이벤트 상관 ID (N5). 순증한다.
/// CLAUDE.md §2.3 — Guid.NewGuid() 를 쓰지 않는다. 결정론 리플레이가 깨진다.
/// </summary>
public readonly record struct CorrelationId(uint Value);

/// <summary>
/// 게임 틱. 10Hz. NPC 서버의 유일한 시간 기준이다 (N4).
/// CLAUDE.md §2.3 — DateTime/Stopwatch 를 게임 로직에 쓰지 않는다.
/// </summary>
public readonly record struct Tick(long Value)
{
    /// <summary>1초에 해당하는 틱 수. docs/11 §5.</summary>
    public const int PerSecond = 10;

    /// <summary>초를 틱으로. 소수점은 버린다.</summary>
    public static Tick FromSeconds(int seconds) => new((long)seconds * PerSecond);

    /// <summary>틱을 초로. 소수점은 버린다.</summary>
    public int ToSeconds() => (int)(Value / PerSecond);

    public static Tick operator +(Tick left, long ticks) => new(left.Value + ticks);

    public static Tick operator -(Tick left, long ticks) => new(left.Value - ticks);

    /// <summary>연산자를 못 쓰는 호출자를 위한 대체 (CA2225).</summary>
    public Tick Add(long ticks) => new(Value + ticks);

    /// <summary>연산자를 못 쓰는 호출자를 위한 대체 (CA2225).</summary>
    public Tick Subtract(long ticks) => new(Value - ticks);
}

/// <summary>
/// 게임서버 좌표계 그대로. 고정소수점이 아니다 —
/// 게임서버 규약에 맞춰 교체할 수 있도록 별도 타입으로 둔다. docs/02 §3.1.
/// </summary>
public readonly record struct WorldPos(float X, float Y, float Z);
