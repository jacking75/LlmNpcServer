namespace Npc.Core;

/// <summary>
/// 시나리오가 끊을 수 있는 것. docs/02 §5 · docs/15 §4.
///
/// <b>문자열이 아니라 열거형이다.</b> 시나리오 파일에 오타가 있으면 아무 일도 일어나지 않고
/// 지나가 버리는데, 시나리오 C 는 "끊었는데도 동작하는가"를 보는 것이라
/// 안 끊긴 채로 통과하면 게이트가 거짓이 된다. 로드 시점에 기동 실패시킨다.
/// </summary>
public enum KillSwitchTarget : byte
{
    /// <summary>외부 API 티어. 끊으면 T1 으로 페일오버한다.</summary>
    T2 = 0,

    /// <summary>로컬 LLM 티어. 끊으면 재계획이 전면 중단되고 캐시 플랜만 남는다.</summary>
    T1 = 1,

    /// <summary>프리베이크 플랜 캐시. 끊으면 아키타입 폴백 40개만 남는다.</summary>
    PlanStore = 2,
}

/// <summary>
/// 킬스위치 발동 상태. docs/15 §4.
///
/// 시나리오 러너(게임서버 대역 스레드)가 세우고 틱 루프·재계획 워커가 읽는다.
/// <b>락을 쓰지 않는다</b> — 비트마스크 하나에 <c>Interlocked</c>/<c>Volatile</c> 만 쓴다
/// (CLAUDE.md §2.1). 한 번 켜지면 꺼지지 않으므로 읽는 쪽에서 경합할 것이 없다.
/// </summary>
public sealed class KillSwitchState
{
    private int _fired;

    /// <summary>아무것도 끊기지 않은 상태. 공유해도 안전하다 — 읽기만 한다.</summary>
    public static KillSwitchState None { get; } = new();

    /// <summary>하나라도 끊겼는가.</summary>
    public bool AnyFired => Volatile.Read(ref _fired) != 0;

    /// <summary>이 대상이 끊겼는가. 핫패스에서 불린다.</summary>
    public bool IsDisabled(KillSwitchTarget target) =>
        (Volatile.Read(ref _fired) & (1 << (int)target)) != 0;

    /// <summary>끊는다. 멱등이다 — 같은 대상을 두 번 끊어도 상태가 같다 (N7).</summary>
    public void Fire(KillSwitchTarget target) => Interlocked.Or(ref _fired, 1 << (int)target);

    /// <summary>전부 되돌린다. 테스트가 회차 사이에 쓴다.</summary>
    public void Reset() => Volatile.Write(ref _fired, 0);

    /// <summary>
    /// 시나리오 파일의 <c>target</c> 문자열을 대상으로. <b>대소문자를 가리지 않는다.</b>
    /// 모르는 문자열은 false — 호출자가 로드 시점에 기동 실패시킨다.
    /// </summary>
    public static bool TryParse(string? text, out KillSwitchTarget target)
    {
        target = default;

        // 숫자는 거절한다. Enum.TryParse 는 "2" 를 PlanStore 로 받아 주는데,
        // 시나리오 파일에 숫자가 적혔다면 그건 ordinal 을 손으로 적은 것이고
        // 나중에 열거형 순서가 바뀌면 조용히 다른 것을 끊게 된다.
        return !string.IsNullOrWhiteSpace(text)
            && !char.IsAsciiDigit(text.TrimStart()[0])
            && Enum.TryParse(text, ignoreCase: true, out target)
            && Enum.IsDefined(target);
    }

    /// <summary>허용된 대상 이름. 실패 메시지에 그대로 싣는다.</summary>
    public static string TargetNames => string.Join(" / ", Enum.GetNames<KillSwitchTarget>());
}
