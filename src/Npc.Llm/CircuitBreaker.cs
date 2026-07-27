namespace Npc.Llm;

/// <summary>서킷 상태. docs/14 §10.</summary>
public enum CircuitState
{
    /// <summary>정상. 호출이 그대로 나간다.</summary>
    Closed = 0,

    /// <summary>차단. 호출을 시도조차 하지 않는다.</summary>
    Open = 1,

    /// <summary>반개방. 한 번만 흘려 보고 결과에 따라 닫거나 다시 연다.</summary>
    HalfOpen = 2,
}

/// <summary>
/// 서킷 브레이커. docs/14 §10 — <b>T2 페일오버가 무한 재시도가 되면 외부 장애 때 지연이 폭발한다.</b>
///
/// 연속 실패 <see cref="FailureThreshold"/> 회에 열리고, <see cref="OpenMillis"/> 뒤에
/// 반개방으로 넘어가 한 번만 시도한다. 그 한 번이 성공하면 닫고, 실패하면 다시 연다.
///
/// <b>시각을 밖에서 받는다.</b> 안에서 <c>Environment.TickCount64</c> 를 읽으면
/// "60초 뒤 반개방" 을 테스트하려고 60초를 기다려야 한다. 기본값만 실제 시계를 쓴다.
/// </summary>
public sealed class CircuitBreaker
{
    /// <summary>이만큼 연속 실패하면 연다. docs/14 §10.</summary>
    public const int FailureThreshold = 5;

    /// <summary>열린 채로 버티는 시간(ms). docs/14 §10 의 60초.</summary>
    public const int OpenMillis = 60_000;

    private readonly Func<long> _now;
    private readonly int _threshold;
    private readonly int _openMillis;
    private readonly Lock _gate = new();

    private int _consecutiveFailures;
    private long _openedAt;
    private CircuitState _state = CircuitState.Closed;

    /// <summary>브레이커 하나.</summary>
    /// <param name="now">밀리초 시계. 기본은 <c>Environment.TickCount64</c>.</param>
    /// <param name="threshold">연속 실패 임계.</param>
    /// <param name="openMillis">열린 채로 버티는 시간(ms).</param>
    public CircuitBreaker(Func<long>? now = null, int threshold = FailureThreshold, int openMillis = OpenMillis)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threshold);
        ArgumentOutOfRangeException.ThrowIfNegative(openMillis);

        _now = now ?? (() => Environment.TickCount64);
        _threshold = threshold;
        _openMillis = openMillis;
    }

    /// <summary>지금 상태. 열린 지 오래됐으면 이 호출이 반개방으로 넘긴다.</summary>
    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                Refresh();
                return _state;
            }
        }
    }

    /// <summary>연속 실패 수. 성공 하나로 0 이 된다.</summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (_gate)
            {
                return _consecutiveFailures;
            }
        }
    }

    /// <summary>열린 횟수. 메트릭·리포트용.</summary>
    public long Trips { get; private set; }

    /// <summary>지금 호출해도 되는가. 반개방이면 <b>한 번만</b> 참이다.</summary>
    public bool TryEnter()
    {
        lock (_gate)
        {
            Refresh();

            if (_state == CircuitState.Open)
            {
                return false;
            }

            // 반개방은 시험 호출 하나만 통과시킨다. 통과시킨 순간 다시 닫아 걸어
            // 두 번째 호출이 같이 나가지 않게 한다.
            if (_state == CircuitState.HalfOpen)
            {
                _state = CircuitState.Open;
                _openedAt = _now();
            }

            return true;
        }
    }

    /// <summary>호출이 성공했다. 서킷을 닫고 실패 누계를 0 으로.</summary>
    public void OnSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _state = CircuitState.Closed;
        }
    }

    /// <summary>호출이 실패했다. 임계에 닿으면 연다.</summary>
    public void OnFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;

            if (_consecutiveFailures < _threshold)
            {
                return;
            }

            if (_state != CircuitState.Open)
            {
                Trips++;
            }

            _state = CircuitState.Open;
            _openedAt = _now();
        }
    }

    /// <summary>전부 되돌린다. 테스트가 회차 사이에 쓴다.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _state = CircuitState.Closed;
            _openedAt = 0;
        }
    }

    /// <summary>열린 지 <see cref="OpenMillis"/> 가 지났으면 반개방으로 넘긴다. 락 안에서만 부른다.</summary>
    private void Refresh()
    {
        if (_state == CircuitState.Open && _now() - _openedAt >= _openMillis)
        {
            _state = CircuitState.HalfOpen;
        }
    }
}
