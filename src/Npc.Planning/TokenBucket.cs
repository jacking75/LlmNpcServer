using Npc.Contracts;

namespace Npc.Planning;

/// <summary>
/// 토큰 버킷 레이트리미터. docs/14 §3.
///
/// <b>시계가 <see cref="Tick"/> 이다.</b> <see cref="System.DateTime"/> 도
/// <see cref="System.Diagnostics.Stopwatch"/> 도 쓰지 않는다 (CLAUDE.md §2.3) —
/// 예산 판정이 벽시계를 보면 같은 리플레이가 두 번 다르게 흐른다.
/// 틱은 실시간 10Hz 이므로 <c>초 = 틱 / <see cref="Tick.PerSecond"/></c> 다.
///
/// <b><c>--max-speed</c> 에서는 틱이 벽시계보다 빠르다.</b> 그때 리미터도 같이 빨라지는 것은
/// 의도한 동작이다 — 측정 회차는 "게임 며칠치 예산" 을 재는 것이지 "실시간 몇 초" 를 재는 것이 아니다.
/// 실제 API 의 분당 한도는 이 리미터가 아니라 <c>AdaptiveConcurrency</c>(T3-12)의 429 감지가 지킨다.
///
/// <b>스레드 안전하다.</b> T1 워커 2개 · T2 워커 8개가 동시에 부른다.
/// 잔량(double)과 기준 틱(long)을 <b>같이</b> 갱신해야 해서 원자 연산 하나로는 안 되므로
/// <c>lock</c> 을 쓴다. CLAUDE.md §2.1 의 락 금지는 <b>틱 루프 안</b> 이야기이고,
/// 이 타입은 틱 루프에서 불리지 않는다 — 호출자는 <c>ReplanWorker</c>(BackgroundService)와
/// 대시보드 HTTP 핸들러뿐이다. 경합도 초당 몇 건 수준이라 실질 비용이 없다.
/// </summary>
public sealed class TokenBucket
{
    private readonly double _ratePerSecond;
    private readonly double _burst;
    private readonly object _gate = new();

    private double _tokens;
    private long _lastTick;
    private long _granted;
    private long _denied;

    /// <summary>버킷을 만든다.</summary>
    /// <param name="ratePerSecond">초당 채워지는 토큰 수.</param>
    /// <param name="burst">
    /// 담을 수 있는 최대치. 0 이면 <c>max(1, rate)</c> —
    /// 1 보다 작으면 요청 하나도 통과하지 못해 리미터가 아니라 차단기가 된다.
    /// </param>
    /// <param name="startTick">기준 틱.</param>
    public TokenBucket(double ratePerSecond, double burst = 0, long startTick = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ratePerSecond);

        _ratePerSecond = ratePerSecond;
        _burst = burst > 0 ? burst : Math.Max(1.0, ratePerSecond);
        _tokens = _burst;
        _lastTick = startTick;
    }

    /// <summary>초당 충전 속도.</summary>
    public double RatePerSecond => _ratePerSecond;

    /// <summary>버스트 용량.</summary>
    public double Burst => _burst;

    /// <summary>통과시킨 요청 수.</summary>
    public long Granted => Interlocked.Read(ref _granted);

    /// <summary>막은 요청 수.</summary>
    public long Denied => Interlocked.Read(ref _denied);

    /// <summary>지금 남아 있는 토큰. 대시보드의 "소진율" 이 이 값을 본다.</summary>
    public double Available(Tick now)
    {
        lock (_gate)
        {
            Refill(now.Value);
            return _tokens;
        }
    }

    /// <summary>토큰을 쓴다. 모자라면 false 이고 아무것도 소비하지 않는다.</summary>
    public bool TryTake(Tick now, double amount = 1.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        lock (_gate)
        {
            Refill(now.Value);

            if (_tokens < amount)
            {
                Interlocked.Increment(ref _denied);
                return false;
            }

            _tokens -= amount;
            Interlocked.Increment(ref _granted);
            return true;
        }
    }

    /// <summary>쓸 수 있는지만 본다. 소비하지 않고 카운터도 건드리지 않는다.</summary>
    public bool CanTake(Tick now, double amount = 1.0)
    {
        lock (_gate)
        {
            Refill(now.Value);
            return _tokens >= amount;
        }
    }

    /// <summary>가득 채운다. 측정 구간을 가를 때만 쓴다.</summary>
    public void Reset(Tick now)
    {
        lock (_gate)
        {
            _tokens = _burst;
            _lastTick = now.Value;
        }
    }

    /// <summary>
    /// 지난 시간만큼 채운다. 되감김(리플레이 재주입)에는 채우지 않는다 —
    /// 시계가 뒤로 가면 토큰이 음수만큼 줄어 리미터가 영구히 잠긴다.
    /// </summary>
    private void Refill(long nowTick)
    {
        long elapsed = nowTick - _lastTick;

        if (elapsed <= 0)
        {
            _lastTick = Math.Max(_lastTick, nowTick);
            return;
        }

        _lastTick = nowTick;
        _tokens = Math.Min(_burst, _tokens + (elapsed / (double)Tick.PerSecond * _ratePerSecond));
    }
}
