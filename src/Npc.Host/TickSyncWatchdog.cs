using Npc.Runtime;

namespace Npc.Host;

/// <summary>
/// <c>TickSync</c> 워치독 (A-10).
///
/// <b>게임서버가 <c>TickSync</c> 를 멈추면 NPC 서버는 조용히 얼어붙는다.</b> 틱 루프는
/// 이벤트를 기다리며 살아 있고(그래서 liveness 는 200), 이벤트는 오지 않으며, 아무 로그도 없다.
/// 하트비트 타임아웃은 소켓이 죽었을 때만 뜬다 — 소켓은 멀쩡한데 틱만 안 오는 경우가 남는다.
///
/// 여기서 벽시계를 본다. <b>게임 로직이 아니라 호스트 계측이다</b> (CLAUDE.md §2.3) —
/// 시계를 읽어 경보를 낼 뿐, 그 값이 NPC 의 판단에 되먹여지지 않는다.
/// </summary>
public sealed class TickSyncWatchdog : IDisposable
{
    private readonly GameClock _clock;
    private readonly TimeSpan _threshold;
    private readonly Action<string> _alarm;
    private readonly CancellationTokenSource _stop = new();
    private long _lastTick;

    // 첫 Check 가 기준점을 잡는다. 생성 시각으로 잡으면 "기동 직후 아직 틱이 없다" 를
    // 정지로 읽는다 — 게임서버 핸드셰이크가 끝나기 전에는 원래 틱이 없다.
    private long _lastMovedMs = long.MinValue;
    private Task? _loop;

    /// <summary>워치독을 만든다.</summary>
    /// <param name="clock">감시할 시계.</param>
    /// <param name="thresholdSeconds">이 시간 동안 틱이 안 늘면 경보. 0 이면 끈다.</param>
    /// <param name="alarm">경보 싱크.</param>
    public TickSyncWatchdog(GameClock clock, int thresholdSeconds, Action<string> alarm)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(alarm);
        ArgumentOutOfRangeException.ThrowIfNegative(thresholdSeconds);

        _clock = clock;
        _threshold = TimeSpan.FromSeconds(thresholdSeconds);
        _alarm = alarm;
        _lastTick = clock.Current.Value;
    }

    /// <summary>경보를 올린 횟수. 테스트·계측이 읽는다.</summary>
    public long Alarms { get; private set; }

    /// <summary>지금 정지 상태인가.</summary>
    public bool Stalled { get; private set; }

    /// <summary>
    /// 게임서버가 알려준 틱과 우리가 처리한 틱의 차이. 클수록 밀린 것이다.
    ///
    /// 0 이 정상이다 — 루프백은 항상 0 이고, 소켓 경로에서도 한두 틱 안에 따라잡는다.
    /// </summary>
    public long TicksBehind => Math.Max(0, _clock.SyncedTick - _clock.Current.Value);

    /// <summary>감시를 시작한다. 임계가 0 이면 아무것도 하지 않는다.</summary>
    public void Start()
    {
        if (_threshold <= TimeSpan.Zero)
        {
            return;
        }

        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>한 번 검사한다. 테스트가 직접 부른다 — 시간을 기다리지 않게.</summary>
    /// <param name="nowMs">지금 시각(ms). 단조 증가하는 값이면 무엇이든 된다.</param>
    /// <returns>이번 검사에서 경보를 올렸으면 true.</returns>
    public bool Check(long nowMs)
    {
        long tick = _clock.Current.Value;

        if (_lastMovedMs == long.MinValue)
        {
            _lastTick = tick;
            _lastMovedMs = nowMs;
            return false;
        }

        if (tick != _lastTick)
        {
            _lastTick = tick;
            _lastMovedMs = nowMs;

            if (Stalled)
            {
                Stalled = false;
                _alarm($"tick-sync 복구 — tick {tick}");
            }

            return false;
        }

        if (Stalled || nowMs - _lastMovedMs < (long)_threshold.TotalMilliseconds)
        {
            return false;
        }

        Stalled = true;
        Alarms++;

        _alarm(
            $"tick-sync 정지 — tick {tick} 에서 {(nowMs - _lastMovedMs) / 1000}s 동안 진행이 없다 "
            + $"(임계 {_threshold.TotalSeconds:0}s). 게임서버가 TickSync 를 보내고 있는지 확인한다.");

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Check(Environment.TickCount64);
        }
    }
}
