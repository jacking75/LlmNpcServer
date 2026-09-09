using Npc.Contracts;
using Npc.Host;

namespace Npc.Tests.Host;

/// <summary>A-03 — <c>Faulted</c> 좀비 제거. PRODUCTION_ROADMAP §4 A-03.</summary>
public sealed class LinkFaultPolicyTests
{
    [Fact]
    public async Task Faulted_ExitsWithCodeThree()
    {
        var fired = new TaskCompletionSource<(int Code, string Reason)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var policy = new LinkFaultPolicy(
            LinkFaultAction.Exit,
            graceSeconds: 0,
            reason: () => "MasterDataMismatch",
            exit: (code, reason) => fired.TrySetResult((code, reason)));

        policy.OnStateChanged(LinkState.Faulted);

        (int code, string reason) = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(LinkFaultPolicy.FaultExitCode, code);
        Assert.Contains("MasterDataMismatch", reason, StringComparison.Ordinal);
        Assert.True(policy.Fired);
    }

    [Fact]
    public async Task Wait_DoesNotExit()
    {
        var fired = false;

        using var policy = new LinkFaultPolicy(
            LinkFaultAction.Wait,
            graceSeconds: 0,
            reason: () => null,
            exit: (_, _) => fired = true);

        policy.OnStateChanged(LinkState.Faulted);

        await Task.Delay(100);

        Assert.False(fired);
        Assert.False(policy.Fired);
    }

    [Fact]
    public async Task HealthyStates_DoNotExit()
    {
        var fired = false;

        using var policy = new LinkFaultPolicy(
            LinkFaultAction.Exit,
            graceSeconds: 0,
            reason: () => null,
            exit: (_, _) => fired = true);

        policy.OnStateChanged(LinkState.Connecting);
        policy.OnStateChanged(LinkState.Connected);
        policy.OnStateChanged(LinkState.Degraded);

        await Task.Delay(100);

        Assert.False(fired);
    }

    [Fact]
    public async Task Grace_DelaysExit()
    {
        var fired = false;

        using var policy = new LinkFaultPolicy(
            LinkFaultAction.Exit,
            graceSeconds: 30,
            reason: () => null,
            exit: (_, _) => fired = true);

        policy.OnStateChanged(LinkState.Faulted);

        await Task.Delay(100);

        // 유예 30초짜리는 100ms 뒤에 아직 발화하지 않는다.
        Assert.False(fired);
    }
}
