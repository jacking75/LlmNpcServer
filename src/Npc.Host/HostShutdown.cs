using System.Runtime.InteropServices;

namespace Npc.Host;

/// <summary>종료 시퀀스 한 단계. 순서가 곧 규약이다.</summary>
public enum ShutdownStage
{
    /// <summary>1. 틱 루프에 정지를 알리고 마지막 틱이 끝나기를 기다린다.</summary>
    TickLoop,

    /// <summary>2. 마지막 스냅샷을 쓴다 (A-01). 상태 손실 창을 0 으로 만든다.</summary>
    Snapshot,

    /// <summary>3. 재계획 워커를 세운다. 진행 중인 LLM 호출 결과는 버린다 — 반영할 곳이 없다.</summary>
    Workers,

    /// <summary>4. 남은 명령을 내보내고 <c>Bye(Shutdown)</c> 을 보낸 뒤 소켓을 닫는다.</summary>
    Link,

    /// <summary>5. 웹 호스트를 세운다.</summary>
    Web,

    /// <summary>6. 끝. 종료 코드를 정한다.</summary>
    Done,
}

/// <summary>종료 한 단계의 결과.</summary>
/// <param name="Stage">단계.</param>
/// <param name="ElapsedMs">걸린 밀리초.</param>
/// <param name="Detail">사람이 읽는 결과.</param>
public readonly record struct ShutdownStep(ShutdownStage Stage, double ElapsedMs, string Detail);

/// <summary>
/// 정상 종료 (A-02).
///
/// <b><c>docker stop</c>·k8s 종료·Windows 서비스 정지는 SIGTERM 이다.</b> 지금까지는 SIGINT 만
/// 처리해 컨테이너에서 드레인 없이 즉사했고, 게임서버는 NPC 서버가 왜 사라졌는지 몰랐다.
///
/// <b>순서가 규약이다.</b> 워커를 먼저 세우면 마지막 스왑이 유실되고, 스냅샷을 나중에 쓰면
/// 틱 루프가 이미 멈춰 사본을 만들 수 없다.
/// </summary>
public sealed class HostShutdown
{
    /// <summary>타임아웃을 넘겼을 때의 종료 코드.</summary>
    public const int TimeoutExitCode = 2;

    private readonly List<ShutdownStep> _steps = [];

    /// <summary>지난 단계들. 로그와 테스트가 읽는다.</summary>
    public IReadOnlyList<ShutdownStep> Steps => _steps;

    /// <summary>타임아웃을 넘겼는가.</summary>
    public bool TimedOut { get; private set; }

    /// <summary>
    /// 신호를 등록한다. SIGTERM·SIGINT·SIGQUIT 과 콘솔 Ctrl-C 를 같은 취소로 모은다.
    ///
    /// <b>.NET 은 윈도우에서도 콘솔 종료 이벤트를 같은 API 로 준다.</b> 플랫폼 분기가 필요 없다.
    /// </summary>
    /// <param name="cancel">취소를 발동할 소스.</param>
    /// <param name="log">신호를 받았다고 적을 곳.</param>
    /// <returns>등록 해제용. 프로세스 수명 동안 살려 둔다.</returns>
    public static IDisposable RegisterSignals(CancellationTokenSource cancel, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        ArgumentNullException.ThrowIfNull(log);

        var registrations = new List<IDisposable>(4);

        foreach (PosixSignal signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT })
        {
            try
            {
                PosixSignal captured = signal;

                registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    // Cancel = true 로 두어야 런타임이 프로세스를 즉사시키지 않는다.
                    // 그래야 아래 시퀀스가 돌 시간이 생긴다.
                    context.Cancel = true;
                    log.WriteLine($"signal: {captured} — 정상 종료를 시작한다.");
                    cancel.Cancel();
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // 이 플랫폼에 없는 신호는 건너뛴다. SIGQUIT 이 그렇다.
            }
        }

        return new Registrations(registrations);
    }

    /// <summary>
    /// 종료 시퀀스를 돈다. 각 단계는 남은 예산 안에서만 기다린다.
    /// </summary>
    /// <param name="timeout">전체 예산.</param>
    /// <param name="stopTickLoop">1. 틱 루프 정지. 마지막 틱이 끝나면 완료된다.</param>
    /// <param name="writeSnapshot">2. 마지막 스냅샷. 없으면 null.</param>
    /// <param name="stopWorkers">3. 워커 정지.</param>
    /// <param name="closeLink">4. Flush → Bye → 소켓 닫기.</param>
    /// <param name="stopWeb">5. 웹 호스트 정지.</param>
    /// <param name="log">단계별 로그.</param>
    /// <returns>종료 코드. 타임아웃이면 2.</returns>
    public async Task<int> RunAsync(
        TimeSpan timeout,
        Func<TimeSpan, Task<string>> stopTickLoop,
        Func<TimeSpan, Task<string>>? writeSnapshot,
        Func<TimeSpan, Task<string>> stopWorkers,
        Func<TimeSpan, Task<string>> closeLink,
        Func<TimeSpan, Task<string>> stopWeb,
        TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(stopTickLoop);
        ArgumentNullException.ThrowIfNull(stopWorkers);
        ArgumentNullException.ThrowIfNull(closeLink);
        ArgumentNullException.ThrowIfNull(stopWeb);
        ArgumentNullException.ThrowIfNull(log);

        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        TimeSpan Remaining()
        {
            TimeSpan left = timeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);

            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        await StepAsync(ShutdownStage.TickLoop, stopTickLoop, Remaining(), log).ConfigureAwait(false);

        if (writeSnapshot is not null)
        {
            // 스냅샷에는 예산의 절반까지만 쓴다. 여기서 다 쓰면 Bye 를 못 보낸다.
            await StepAsync(ShutdownStage.Snapshot, writeSnapshot, Half(Remaining()), log).ConfigureAwait(false);
        }

        await StepAsync(ShutdownStage.Workers, stopWorkers, Remaining(), log).ConfigureAwait(false);
        await StepAsync(ShutdownStage.Link, closeLink, Remaining(), log).ConfigureAwait(false);
        await StepAsync(ShutdownStage.Web, stopWeb, Remaining(), log).ConfigureAwait(false);

        double total = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        TimedOut = total > timeout.TotalMilliseconds;
        _steps.Add(new ShutdownStep(ShutdownStage.Done, total, TimedOut ? "타임아웃 초과" : "정상"));

        log.WriteLine($"shutdown: 6/6 done · {total:0}ms" + (TimedOut ? " (타임아웃 초과)" : string.Empty));

        return TimedOut ? TimeoutExitCode : 0;
    }

    private static TimeSpan Half(TimeSpan span) => TimeSpan.FromTicks(span.Ticks / 2);

    private async Task StepAsync(
        ShutdownStage stage, Func<TimeSpan, Task<string>> action, TimeSpan budget, TextWriter log)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string detail;

        try
        {
            detail = await action(budget).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or TimeoutException)
        {
            detail = $"실패: {e.Message}";
        }

        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        _steps.Add(new ShutdownStep(stage, elapsed, detail));
        log.WriteLine($"shutdown: {(int)stage + 1}/6 {stage} · {elapsed:0}ms · {detail}");
    }

    private sealed class Registrations(List<IDisposable> items) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable item in items)
            {
                item.Dispose();
            }

            items.Clear();
        }
    }
}
