using Npc.Host;

namespace Npc.Tests.Host;

/// <summary>A-02 — 정상 종료 시퀀스. PRODUCTION_ROADMAP §4 A-02.</summary>
public sealed class HostShutdownTests
{
    [Fact]
    public async Task Sequence_RunsInOrder()
    {
        var shutdown = new HostShutdown();
        var order = new List<string>();

        int code = await shutdown.RunAsync(
            TimeSpan.FromSeconds(15),
            stopTickLoop: _ => Record(order, "tick"),
            writeSnapshot: _ => Record(order, "snapshot"),
            stopWorkers: _ => Record(order, "workers"),
            closeLink: _ => Record(order, "link"),
            stopWeb: _ => Record(order, "web"),
            TextWriter.Null);

        Assert.Equal(0, code);

        // 순서가 규약이다. 워커를 먼저 세우면 마지막 스왑이 유실되고,
        // 스냅샷을 나중에 쓰면 틱 루프가 이미 멈춰 사본을 만들 수 없다.
        Assert.Equal(["tick", "snapshot", "workers", "link", "web"], order);

        Assert.Equal(
            [
                ShutdownStage.TickLoop, ShutdownStage.Snapshot, ShutdownStage.Workers,
                ShutdownStage.Link, ShutdownStage.Web, ShutdownStage.Done,
            ],
            shutdown.Steps.Select(s => s.Stage));

        Assert.False(shutdown.TimedOut);
    }

    [Fact]
    public async Task Sequence_SkipsSnapshotWhenDisabled()
    {
        var shutdown = new HostShutdown();
        var order = new List<string>();

        await shutdown.RunAsync(
            TimeSpan.FromSeconds(15),
            stopTickLoop: _ => Record(order, "tick"),
            writeSnapshot: null,
            stopWorkers: _ => Record(order, "workers"),
            closeLink: _ => Record(order, "link"),
            stopWeb: _ => Record(order, "web"),
            TextWriter.Null);

        Assert.Equal(["tick", "workers", "link", "web"], order);
        Assert.DoesNotContain(shutdown.Steps, s => s.Stage == ShutdownStage.Snapshot);
    }

    [Fact]
    public async Task Overrun_ReturnsExitCodeTwo()
    {
        var shutdown = new HostShutdown();

        int code = await shutdown.RunAsync(
            TimeSpan.FromMilliseconds(1),
            stopTickLoop: async _ =>
            {
                await Task.Delay(60);
                return "느린 틱 루프";
            },
            writeSnapshot: null,
            stopWorkers: _ => Task.FromResult("ok"),
            closeLink: _ => Task.FromResult("ok"),
            stopWeb: _ => Task.FromResult("ok"),
            TextWriter.Null);

        Assert.Equal(HostShutdown.TimeoutExitCode, code);
        Assert.True(shutdown.TimedOut);
    }

    [Fact]
    public async Task FailedStage_DoesNotStopTheSequence()
    {
        var shutdown = new HostShutdown();
        var order = new List<string>();

        // 스냅샷을 못 써도 Bye 는 보내야 한다. 한 단계의 실패가 나머지를 막으면
        // 게임서버는 우리가 왜 사라졌는지 영영 모른다.
        int code = await shutdown.RunAsync(
            TimeSpan.FromSeconds(15),
            stopTickLoop: _ => Record(order, "tick"),
            writeSnapshot: _ => throw new IOException("디스크가 가득 찼다"),
            stopWorkers: _ => Record(order, "workers"),
            closeLink: _ => Record(order, "link"),
            stopWeb: _ => Record(order, "web"),
            TextWriter.Null);

        Assert.Equal(0, code);
        Assert.Equal(["tick", "workers", "link", "web"], order);

        ShutdownStep snapshot = Assert.Single(shutdown.Steps, s => s.Stage == ShutdownStage.Snapshot);

        Assert.Contains("디스크가 가득 찼다", snapshot.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Signals_RegisterAndUnregister()
    {
        using var cancel = new CancellationTokenSource();

        // 등록·해제가 던지지 않는 것만 본다. 실제 신호는 프로세스 밖에서 오고,
        // 그것은 G-02 의 컨테이너 시험이 잰다.
        IDisposable registration = HostShutdown.RegisterSignals(cancel, TextWriter.Null);

        registration.Dispose();

        Assert.False(cancel.IsCancellationRequested);
    }

    private static Task<string> Record(List<string> order, string name)
    {
        order.Add(name);
        return Task.FromResult(name);
    }
}
