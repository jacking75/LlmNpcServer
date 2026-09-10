namespace Npc.Contracts;

/// <summary>
/// 계약 버전 (B-01).
///
/// <b>계약(의미)에 버전이 없었다.</b> 와이어의 <c>Ver</c> 1바이트는 표현의 버전이고, 불일치하면
/// 협상 없이 즉시 절단이다. <c>NpcCommandKind</c> 에 항목 하나를 뒤에 추가해도 알릴 방법이 없었고,
/// 알리려면 양쪽을 동시에 내려야 해서 라이브 서비스의 롤링 배포와 정면으로 충돌했다.
///
/// <para><b>규칙.</b></para>
/// <list type="bullet">
///   <item><description>
///     <b>뒤에 추가만 하면 Minor.</b> <c>NpcCommandKind</c>·<c>GameEventKind</c>·
///     <c>ActionFailReason</c> 에 값을 뒤에 붙이는 변경이 여기 해당한다.
///     <c>ContractVersionTests</c> 가 열거형 멤버 수 스냅샷을 들고 있어, 늘었는데 Minor 를
///     안 올리면 테스트가 깨진다.
///   </description></item>
///   <item><description>
///     <b>기존 필드의 의미·크기 변경은 Major.</b> 이쪽은 한쪽만 배포할 수 없다 —
///     핸드셰이크에서 <c>ContractMismatch</c> 로 거절한다.
///   </description></item>
/// </list>
///
/// <para>
/// <b>Minor 가 다를 때의 동작.</b> 낮은 쪽 기준으로 돈다. 높은 쪽은 상대가 모르는 Kind 를
/// 보내지 않는다 — 명령 발행부가 협상된 Minor 를 보고 새 Kind 를 드롭하고 카운터를 올린다.
/// 조용히 버리는 것이 아니라 <b>세면서</b> 버린다.
/// </para>
/// </summary>
public static class ContractVersion
{
    /// <summary>주 버전. 기존 필드의 의미·크기가 바뀌면 올린다.</summary>
    public const ushort Major = 1;

    /// <summary>
    /// 부 버전. 열거형에 값을 뒤에 추가하거나 <b>패킷 뒤에 필드를 더하면</b> 올린다.
    ///
    /// <para><b>2</b> — 확장 슬롯 <c>Instance</c>·<c>Faction</c>·<c>ExtA</c>·<c>ExtB</c> (B-02).
    /// v1 코덱은 이 넷을 싣지 않으므로 옛 게임서버는 그대로 돈다.</para>
    /// </summary>
    public const ushort Minor = 2;

    /// <summary>사람이 읽는 표기.</summary>
    public static string Text => $"{Major}.{Minor}";

    /// <summary>
    /// 이 주 버전끼리만 붙는다.
    /// </summary>
    public static bool IsCompatible(ushort major) => major == Major;

    /// <summary>협상된 부 버전. 낮은 쪽이 이긴다.</summary>
    public static ushort NegotiateMinor(ushort theirs) => Math.Min(Minor, theirs);
}

/// <summary>
/// 기능 비트 (B-01). 게임서버가 지원하는 것만 켜서 보낸다.
///
/// <b>비트는 재배치하지 않는다.</b> 마스터데이터의 <c>code</c>·<c>bit</c> 와 같은 규칙이다
/// (CLAUDE.md §2.4) — 추가는 뒤에만.
///
/// NPC 서버는 켜진 비트에 맞춰 동작을 바꾼다. 예를 들어 <see cref="Auth"/> 가 꺼져 있으면
/// v1 처럼 인증 없이 붙는다 — 단 <c>--link-require-auth</c> 면 거절한다.
/// </summary>
[Flags]
public enum LinkFeatures : ulong
{
    /// <summary>아무것도 없다. v1 게임서버가 보내는 값과 같다.</summary>
    None = 0,

    /// <summary>핸드셰이크 상호 인증 (A-06).</summary>
    Auth = 1UL << 0,

    /// <summary>전역 <c>NpcId</c>·샤드 식별 (A-08).</summary>
    GlobalIds = 1UL << 1,

    /// <summary>패킷 확장 슬롯 — <c>InstanceId</c>·<c>Faction</c>·<c>ExtA</c>·<c>ExtB</c> (B-02).</summary>
    ExtSlots = 1UL << 2,

    /// <summary>런타임 스폰·디스폰 (B-05).</summary>
    DynamicRoster = 1UL << 3,

    /// <summary>적대 플레이어 감지 — <c>PlayerHostility</c> (B-06).</summary>
    Hostility = 1UL << 4,

    /// <summary>세션 에포크 — 게임서버 재기동 시 시퀀스 기준을 새로 잡는다 (G-02).</summary>
    SessionEpoch = 1UL << 5,
}
