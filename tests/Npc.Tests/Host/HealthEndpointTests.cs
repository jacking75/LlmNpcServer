using Npc.Contracts;
using Npc.Host;
using Npc.Host.Api;

namespace Npc.Tests.Host;

/// <summary>A-03 — 프로브 세 종. PRODUCTION_ROADMAP §4 A-03.</summary>
public sealed class HealthEndpointTests
{
    [Fact]
    public void Live_IsOk_WhileLoopBeats()
    {
        var probe = new HealthProbe();

        probe.Beat();

        HealthReport report = HealthEndpoints.Live(probe, stallMs: 30_000);

        Assert.Equal("ok", report.Status);
        Assert.Equal(200, HealthEndpoints.StatusCode(report));
    }

    [Fact]
    public void Live_Fails_WhenLoopStalls()
    {
        var probe = new HealthProbe();

        // 상한 0 이면 "지금 친 것" 도 이미 늦은 것이다 — 시간을 기다리지 않고 정지를 표현한다.
        HealthReport report = HealthEndpoints.Live(probe, stallMs: -1);

        Assert.Equal("fail", report.Status);
        Assert.Equal(503, HealthEndpoints.StatusCode(report));
        Assert.Contains(report.Checks, c => c.Name == "loop" && c.Status == "fail");
    }

    [Fact]
    public void Ready_Fails_WhenLinkNotConnected()
    {
        var probe = new HealthProbe();

        probe.Observe(1);

        HealthReport report = HealthEndpoints.Ready(
            probe, loaded: true, LinkState.Faulted, linkRequired: true,
            tickStallMs: 30_000, rejectReason: "MasterDataMismatch");

        Assert.Equal("fail", report.Status);

        HealthCheck link = Assert.Single(report.Checks, c => c.Name == "link");

        Assert.Equal("fail", link.Status);

        // 거절 사유가 본문에 있어야 한다 — 그것이 재시작 루프에 든 이유를 아는 유일한 경로다.
        Assert.Contains("MasterDataMismatch", link.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Ready_IgnoresLink_WhenLoopbackRun()
    {
        var probe = new HealthProbe();

        probe.Observe(1);

        // 루프백·널·재생에는 소켓이 없다. 링크를 요구하면 프로브가 영원히 503 이다.
        HealthReport report = HealthEndpoints.Ready(
            probe, loaded: true, LinkState.Disconnected, linkRequired: false,
            tickStallMs: 30_000, rejectReason: null);

        Assert.Equal("ok", report.Status);
    }

    [Fact]
    public void Ready_Fails_WhenTicksStall()
    {
        var probe = new HealthProbe();

        HealthReport report = HealthEndpoints.Ready(
            probe, loaded: true, LinkState.Connected, linkRequired: true,
            tickStallMs: -1, rejectReason: null);

        Assert.Equal("fail", report.Status);
        Assert.Contains(report.Checks, c => c.Name == "tickSync" && c.Status == "fail");
    }

    [Fact]
    public void Startup_ReportsRestoreDecision()
    {
        HealthReport ok = HealthEndpoints.Startup(loaded: true, restoreDecided: true, "복원 대상 없음");

        Assert.Equal("ok", ok.Status);
        Assert.Contains(ok.Checks, c => c.Name == "restore" && c.Detail == "복원 대상 없음");

        HealthReport pending = HealthEndpoints.Startup(loaded: true, restoreDecided: false, "복원 중");

        Assert.Equal("fail", pending.Status);
    }

    [Fact]
    public void Probe_TracksTickAdvance()
    {
        var probe = new HealthProbe();

        probe.Observe(10);
        Assert.Equal(10, probe.LastTick);

        // 되감기는 무시한다 (N7 — 재주입된 이벤트가 관측을 되돌리면 안 된다).
        probe.Observe(5);
        Assert.Equal(10, probe.LastTick);
    }
}
