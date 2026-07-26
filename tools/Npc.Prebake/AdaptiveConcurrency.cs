namespace Npc.Prebake;

/// <summary>
/// AIMD 동시성 제어. docs/13 §4 · <c>docs/measurements/W1_concurrency.md</c>.
///
/// <b>성공 16연속 → +1, throttle → /2.</b> additive increase · multiplicative decrease 다.
/// <c>SemaphoreSlim</c> 의 카운트를 동적으로 조절하는 대신 워커가 각자 <see cref="Current"/> 를
/// 확인하고 초과분은 대기하는 방식이 단순하다 (docs/13 §4).
///
/// <para>
/// <b>초기값을 "W1 T0-11 실측값"이라고 쓸 수 없다.</b> T0-11 은 로컬 2종만 쟀고 외부 API 는
/// 측정되지 않았으며, 로컬에서는 429 가 한 번도 나지 않았다 (<c>W1_concurrency.md</c>).
/// </para>
///
/// <para>
/// <b>실측 근거는 이렇다.</b> 초기값 규칙은 "T2-19 첫 회차에서 실측한 429 최초 발생 동시성의 절반,
/// 그 실행 전이면 8" 이다. T2-19 첫 회차(288버킷 · OpenRouter)에서 <b>동시 18까지 429 가
/// 한 번도 나지 않았고</b>(<c>W6_compile_stats.md §6</c>), T3-11 실측(Poe)에서도 동시 32까지
/// 나지 않았다(<c>W8_prebake.md §2</c>). 따라서 절반 규칙이 발동할 값이 없어 **8 을 유지한다.**
/// 이 값과 근거는 회차마다 <c>manifest.json</c> 의 <c>generated_by.concurrency</c> ·
/// <c>first_rate_limit_concurrency</c> 에 남는다.
/// </para>
/// </summary>
public sealed class AdaptiveConcurrency
{
    /// <summary>초기값. 실측 근거는 위 주석과 <c>W6_compile_stats.md §6</c> 에 있다.</summary>
    public const int DefaultStart = 8;

    /// <summary>동시성을 1 올리는 데 필요한 연속 성공 수. docs/13 §4 의 16.</summary>
    public const int DefaultSuccessStreak = 16;

    private readonly int _max;
    private readonly int _min;
    private readonly int _streakTarget;

    private int _current;
    private int _streak;

    /// <summary>제어기를 만든다.</summary>
    /// <param name="start">초기 동시성.</param>
    /// <param name="max">상한.</param>
    /// <param name="successStreak">이만큼 연속 성공하면 +1.</param>
    /// <param name="min">하한. 1 미만으로 내려가지 않는다.</param>
    public AdaptiveConcurrency(
        int start = DefaultStart,
        int max = 32,
        int successStreak = DefaultSuccessStreak,
        int min = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(min);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(successStreak);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, min);

        _min = min;
        _max = Math.Max(max, min);
        _streakTarget = successStreak;
        _current = Math.Clamp(start, _min, _max);

        Start = _current;
        Peak = _current;
    }

    /// <summary>지금 허용 동시성.</summary>
    public int Current => Volatile.Read(ref _current);

    /// <summary>초기값. manifest 에 그대로 실린다.</summary>
    public int Start { get; }

    /// <summary>상한.</summary>
    public int Max => _max;

    /// <summary>하한.</summary>
    public int Min => _min;

    /// <summary>도달한 최대 동시성.</summary>
    public int Peak { get; private set; }

    /// <summary>지금까지의 연속 성공 수.</summary>
    public int Streak => Volatile.Read(ref _streak);

    /// <summary>throttle 을 만난 횟수.</summary>
    public int ThrottleCount { get; private set; }

    /// <summary>
    /// 429 가 <b>처음</b> 난 시점의 동시성. 한 번도 안 났으면 0.
    /// <b>이 값이 다음 회차 초기값의 근거다</b> — 절반으로 잡는다.
    /// </summary>
    public int FirstThrottleConcurrency { get; private set; }

    /// <summary>다음 회차에 권하는 초기값. 429 를 본 적이 없으면 지금 값을 그대로 권한다.</summary>
    public int RecommendedStart =>
        FirstThrottleConcurrency > 0 ? Math.Max(_min, FirstThrottleConcurrency / 2) : Peak;

    /// <summary>성공 하나. 연속이 목표에 닿으면 +1 한다 (additive increase).</summary>
    public void OnSuccess()
    {
        if (Interlocked.Increment(ref _streak) < _streakTarget)
        {
            return;
        }

        Volatile.Write(ref _streak, 0);

        int next = Math.Min(_max, Current + 1);

        Volatile.Write(ref _current, next);
        Peak = Math.Max(Peak, next);
    }

    /// <summary>성공 여러 건을 한 번에. 물결 단위로 도는 러너가 쓴다.</summary>
    public void OnSuccess(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        for (int i = 0; i < count; i++)
        {
            OnSuccess();
        }
    }

    /// <summary>throttle(429) 하나. 절반으로 접는다 (multiplicative decrease).</summary>
    public void OnThrottled()
    {
        ThrottleCount++;

        if (FirstThrottleConcurrency == 0)
        {
            FirstThrottleConcurrency = Current;
        }

        Volatile.Write(ref _current, Math.Max(_min, Current / 2));
        Volatile.Write(ref _streak, 0);
    }

    /// <summary>회차 요약 한 줄. 로그·manifest 가 쓴다.</summary>
    public override string ToString() =>
        $"start {Start} · current {Current} · peak {Peak} · max {_max} · "
        + $"throttled {ThrottleCount}회 (최초 동시성 {FirstThrottleConcurrency})";
}
