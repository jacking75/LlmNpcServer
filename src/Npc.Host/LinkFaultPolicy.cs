using Npc.Contracts;

namespace Npc.Host;

/// <summary><see cref="LinkState.Faulted"/> 를 만났을 때 무엇을 할까 (A-03).</summary>
public enum LinkFaultAction
{
    /// <summary>
    /// 유예 뒤 종료 코드 3 으로 죽는다 (기본). 오케스트레이터가 재시작한다.
    ///
    /// <b>이것이 기본인 이유.</b> 핸드셰이크가 거절되면 소켓 태스크가 조용히 끝나고 틱 루프는
    /// 이벤트를 기다리며 영원히 블록된다 — 프로세스는 살아 있고 <c>/status</c> 는 200 인데
    /// 아무 일도 일어나지 않는 좀비다.
    /// </summary>
    Exit,

    /// <summary>그대로 둔다. 같은 프로세스를 띄워 둔 채 게임서버를 고치는 개발 회차용.</summary>
    Wait,
}

/// <summary>
/// 링크 결함 감시 (A-03).
///
/// <c>StateChanged</c> 를 구독해 <see cref="LinkState.Faulted"/> 진입을 보고, 유예 시간이 지나면
/// <see cref="Exit"/> 콜백을 부른다. <b>프로세스를 직접 죽이지 않는다</b> — 그러면 테스트할 수 없다.
/// </summary>
public sealed class LinkFaultPolicy : IDisposable
{
    /// <summary>결함 종료 코드. 오케스트레이터가 이 값으로 재시작 사유를 가른다.</summary>
    public const int FaultExitCode = 3;

    private readonly LinkFaultAction _action;
    private readonly TimeSpan _grace;
    private readonly Action<int, string> _exit;
    private readonly Func<string?> _reason;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;

    /// <summary>감시기를 만든다.</summary>
    /// <param name="action">정책.</param>
    /// <param name="graceSeconds">Faulted 진입 후 종료까지의 유예(초).</param>
    /// <param name="reason">거절 사유를 읽는 함수. 종료 로그에 실린다.</param>
    /// <param name="exit">종료 요청. (코드, 사유).</param>
    public LinkFaultPolicy(
        LinkFaultAction action, int graceSeconds, Func<string?> reason, Action<int, string> exit)
    {
        ArgumentNullException.ThrowIfNull(reason);
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentOutOfRangeException.ThrowIfNegative(graceSeconds);

        _action = action;
        _grace = TimeSpan.FromSeconds(graceSeconds);
        _reason = reason;
        _exit = exit;
    }

    /// <summary>결함으로 종료를 요청했는가. 테스트가 읽는다.</summary>
    public bool Fired { get; private set; }

    /// <summary>마지막으로 관측한 상태.</summary>
    public LinkState LastState { get; private set; } = LinkState.Disconnected;

    /// <summary>링크에 붙인다.</summary>
    public void Attach(IGameServerLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        link.StateChanged += OnStateChanged;
        OnStateChanged(link.State);
    }

    /// <summary>상태 전이 처리. 테스트가 직접 부른다.</summary>
    public void OnStateChanged(LinkState state)
    {
        LastState = state;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (state != LinkState.Faulted)
            {
                // 결함에서 빠져나왔다 — 유예 타이머를 접는다. 실제로는 Faulted 에서
                // 회복하는 경로가 없지만, 정책이 그 가정을 코드로 굳히지는 않는다.
                _timer?.Dispose();
                _timer = null;
                return;
            }

            if (_action == LinkFaultAction.Wait || _timer is not null || Fired)
            {
                return;
            }

            _timer = new Timer(_ => FireExit(), null, _grace, Timeout.InfiniteTimeSpan);
        }
    }

    private void FireExit()
    {
        lock (_gate)
        {
            if (Fired || _disposed)
            {
                return;
            }

            Fired = true;
        }

        string? reason = _reason();

        _exit(
            FaultExitCode,
            reason is null
                ? "링크가 Faulted 다. 게임서버 핸드셰이크를 확인한다."
                : $"링크가 Faulted 다: {reason}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
