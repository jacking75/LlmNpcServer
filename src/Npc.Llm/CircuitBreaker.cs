using Npc.Contracts;

namespace Npc.Llm;

/// <summary>서킷 브레이커 상태. docs/14 §10.</summary>
public enum CircuitState
{
    /// <summary>닫힘 — 정상. 요청이 그대로 나간다.</summary>
    Closed = 0,

    /// <summary>열림 — 차단. 시도조차 하지 않고 곧바로 T1 으로 간다.</summary>
    Open = 1,

    /// <summary>반개방 — 탐색. 한 건만 흘려보내 살아났는지 본다.</summary>
    HalfOpen = 2,
}

/// <summary>
/// T2 서킷 브레이커. docs/14 §10 "T2 페일오버가 무한 재시도".
///
/// <b>무한 재시도는 외부 장애 시 지연을 폭발시킨다.</b> 외부 API 가 죽었을 때 매 요청이
/// 타임아웃까지 기다린 뒤 T1 으로 넘어가면, 재계획 하나가 (외부 타임아웃 + 로컬 5.1s)를 먹는다.
/// 워커 8개가 그러고 있으면 큐가 즉시 포화하고 스냅샷 낡음 판정(T4-04)이 전부 폐기한다.
///
/// 연속 실패 <see cref="FailureThreshold"/>회 → <see cref="CooldownSeconds"/>초 차단 →
/// 반개방(한 건 탐색) → 성공이면 닫힘, 실패면 다시 차단.
///
/// <b>시계는 <see cref="Tick"/> 이다.</b> <see cref="System.DateTime"/> 을 보면
/// 리플레이가 깨진다 (CLAUDE.md §2.3). 틱은 실시간 10Hz 이므로 60초 = 600틱이다.
///
/// <b>워커 여러 개가 동시에 부른다.</b> 상태는 <see cref="Interlocked"/> 로만 다룬다.
/// 반개방에서 두 워커가 동시에 탐색을 통과할 수 있는데, 그건 해롭지 않다 —
/// 탐색 요청이 하나 더 나갈 뿐이고 실패하면 둘 다 같은 결론을 낸다.
/// </summary>
public sealed class CircuitBreaker
{
    /// <summary>차단으로 넘어가는 연속 실패 수. docs/14 §10 의 5.</summary>
    public const int DefaultFailureThreshold = 5;

    /// <summary>차단 유지 시간(초). docs/14 §10 의 60.</summary>
    public const int DefaultCooldownSeconds = 60;

    private readonly int _threshold;
    private readonly long _cooldownTicks;

    private int _consecutiveFailures;
    private long _openedAt = -1;
    private long _opens;
    private long _shortCircuits;
    private long _probes;

    /// <summary>브레이커를 만든다. 기동 시 1회.</summary>
    /// <param name="failureThreshold">연속 실패 임계. 0 이면 <see cref="DefaultFailureThreshold"/>.</param>
    /// <param name="cooldownSeconds">차단 시간(초). 0 이면 <see cref="DefaultCooldownSeconds"/>.</param>
    public CircuitBreaker(int failureThreshold = 0, int cooldownSeconds = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failureThreshold);
        ArgumentOutOfRangeException.ThrowIfNegative(cooldownSeconds);

        _threshold = failureThreshold > 0 ? failureThreshold : DefaultFailureThreshold;
        CooldownSeconds = cooldownSeconds > 0 ? cooldownSeconds : DefaultCooldownSeconds;
        _cooldownTicks = (long)CooldownSeconds * Tick.PerSecond;
    }

    /// <summary>연속 실패 임계.</summary>
    public int FailureThreshold => _threshold;

    /// <summary>차단 시간(초).</summary>
    public int CooldownSeconds { get; }

    /// <summary>차단 시간(틱).</summary>
    public long CooldownTicks => _cooldownTicks;

    /// <summary>지금까지의 연속 실패 수.</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    /// <summary>차단으로 넘어간 횟수. 이게 오르면 외부 API 가 불안정하다.</summary>
    public long Opens => Interlocked.Read(ref _opens);

    /// <summary>차단 중이라 시도조차 하지 않은 요청 수.</summary>
    public long ShortCircuits => Interlocked.Read(ref _shortCircuits);

    /// <summary>반개방에서 흘려보낸 탐색 요청 수.</summary>
    public long Probes => Interlocked.Read(ref _probes);

    /// <summary>현재 상태. 차단 시간이 지났으면 반개방이다.</summary>
    public CircuitState StateAt(Tick now)
    {
        long openedAt = Volatile.Read(ref _openedAt);

        if (openedAt < 0)
        {
            return CircuitState.Closed;
        }

        return now.Value - openedAt >= _cooldownTicks ? CircuitState.HalfOpen : CircuitState.Open;
    }

    /// <summary>
    /// 지금 T2 를 시도해도 되는가. <b>false 면 라우터는 곧바로 T1 으로 간다.</b>
    /// 반개방이면 true 를 주고 탐색으로 센다.
    /// </summary>
    public bool TryEnter(Tick now)
    {
        switch (StateAt(now))
        {
            case CircuitState.Closed:
                return true;

            case CircuitState.HalfOpen:
                Interlocked.Increment(ref _probes);
                return true;

            case CircuitState.Open:
            default:
                Interlocked.Increment(ref _shortCircuits);
                return false;
        }
    }

    /// <summary>호출이 성공했다. 연속 실패를 0 으로 되돌리고 차단을 푼다.</summary>
    public void RecordSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Interlocked.Exchange(ref _openedAt, -1);
    }

    /// <summary>
    /// 호출이 실패했다. 임계에 닿으면 차단으로 넘어간다.
    /// <b>반개방에서의 실패는 즉시 재차단이다</b> — 탐색이 실패했으면 아직 죽어 있다.
    /// </summary>
    public void RecordFailure(Tick now)
    {
        int failures = Interlocked.Increment(ref _consecutiveFailures);

        switch (StateAt(now))
        {
            case CircuitState.Open:
                // 이미 차단 중이다. 브레이커가 열리기 직전에 이미 나가 있던 요청이 뒤늦게
                // 실패한 것이므로 쿨다운을 늘리지 않는다 — 늘리면 워커 8개가 서로 타이머를
                // 밀어 차단이 영원히 풀리지 않는다.
                return;

            case CircuitState.Closed when failures < _threshold:
                return;

            case CircuitState.HalfOpen:
            case CircuitState.Closed:
            default:
                break;
        }

        Volatile.Write(ref _openedAt, now.Value);
        Interlocked.Increment(ref _opens);
    }

    /// <summary>강제로 닫는다. 테스트와 운영 수동 복구용.</summary>
    public void Reset() => RecordSuccess();

    /// <summary>
    /// 강제로 연다 (C-08). <b>호출 실패가 아니라 바깥의 판정으로 차단할 때</b> 쓴다 —
    /// 로컬 추론 프로세스가 죽은 것을 헬스 프로브가 먼저 알았을 때가 그렇다.
    ///
    /// <para>
    /// <b>연속 실패 계수는 건드리지 않는다.</b> 그 숫자는 "호출이 몇 번 연달아 실패했나" 이고,
    /// 여기서 올리면 프로브가 브레이커의 자기 계측을 오염시킨다.
    /// </para>
    ///
    /// <para>이미 열려 있으면 타이머를 밀지 않는다 — 프로브가 60초마다 부르면 영원히 안 풀린다.</para>
    /// </summary>
    /// <param name="now">지금 틱.</param>
    /// <returns>이번 호출로 열렸으면 true. 이미 열려 있었으면 false.</returns>
    public bool ForceOpen(Tick now)
    {
        if (StateAt(now) == CircuitState.Open)
        {
            return false;
        }

        Volatile.Write(ref _openedAt, now.Value);
        Interlocked.Increment(ref _opens);

        return true;
    }
}
