using Npc.Core;
using Npc.Host.Observability;
using Npc.Host.Replan;

namespace Npc.Tests.Host;

/// <summary>C-02 — 벽시계 청구 캡. PRODUCTION_ROADMAP §6 C-02.</summary>
public sealed class BillingGuardTests
{
    [Fact]
    public void Disabled_WhenCapIsZero()
    {
        var switches = new KillSwitchState();
        var guard = new BillingGuard(switches, NullAlarmSink.Instance, capUsd: 0);

        Assert.False(guard.Enabled);
        Assert.False(guard.Observe(1_000));
        Assert.False(switches.IsDisabled(KillSwitchTarget.T2));
    }

    [Fact]
    public void CutsT2_WhenTheWallClockCapIsExceeded()
    {
        var switches = new KillSwitchState();
        var writer = new StringWriter();
        var alarms = new TextAlarmSink(writer);
        var guard = new BillingGuard(switches, alarms, capUsd: 2.0, now: Fixed);

        Assert.False(guard.Observe(1.5));
        Assert.False(switches.IsDisabled(KillSwitchTarget.T2));

        Assert.True(guard.Observe(2.0));
        Assert.True(switches.IsDisabled(KillSwitchTarget.T2));
        Assert.Equal(1, guard.Blocks);
        Assert.Contains("kind=CostBudget sev=Critical", writer.ToString(), StringComparison.Ordinal);

        // 이미 끊긴 뒤에는 다시 울리지 않는다.
        Assert.False(guard.Observe(3.0));
        Assert.Equal(1, guard.Blocks);
    }

    [Fact]
    public void Rollover_ResetsSpendButNotTheKillSwitch()
    {
        var switches = new KillSwitchState();
        var writer = new StringWriter();
        var alarms = new TextAlarmSink(writer);
        DateTimeOffset now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

        var guard = new BillingGuard(switches, alarms, capUsd: 2.0, resetHourUtc: 0, now: () => now);

        Assert.True(guard.Observe(2.0));
        Assert.True(switches.IsDisabled(KillSwitchTarget.T2));

        // 다음 청구일.
        now = now.AddDays(1);

        Assert.False(guard.Observe(2.0));
        Assert.Equal(0, guard.SpentToday);

        // <b>킬스위치는 자동으로 풀리지 않는다.</b> 자동 해제를 만들면
        // "왜 다시 돈이 나갔나" 에 답할 수 없다.
        Assert.True(switches.IsDisabled(KillSwitchTarget.T2));

        // 새 날의 지출은 기준점 이후만 센다.
        Assert.False(guard.Observe(3.0));
        Assert.Equal(1.0, guard.SpentToday, 3);
    }

    [Fact]
    public void ResetHour_ShiftsTheBillingDay()
    {
        var switches = new KillSwitchState();
        DateTimeOffset now = new(2026, 9, 10, 20, 0, 0, TimeSpan.Zero);

        // 청구일이 UTC 22시에 바뀐다. 20시는 아직 전날이다.
        var guard = new BillingGuard(
            switches, NullAlarmSink.Instance, capUsd: 2.0, resetHourUtc: 22, now: () => now);

        guard.Observe(1.0);

        // 21시 — 여전히 같은 청구일이다.
        now = now.AddHours(1);
        guard.Observe(1.5);
        Assert.Equal(1.5, guard.SpentToday, 3);

        // 23시 — 청구일이 넘어갔다.
        now = now.AddHours(2);
        guard.Observe(1.5);
        Assert.Equal(0, guard.SpentToday);
    }

    [Fact]
    public void SpentToday_IsMeasuredFromTheDayBaseline()
    {
        var switches = new KillSwitchState();
        var guard = new BillingGuard(switches, NullAlarmSink.Instance, capUsd: 100, now: Fixed);

        // 누계는 프로세스 기동 이후 값이다. 첫 관측이 곧 기준점은 아니다 —
        // 기준점은 청구일이 바뀔 때만 옮긴다.
        guard.Observe(0.5);
        Assert.Equal(0.5, guard.SpentToday, 3);

        guard.Observe(2.5);
        Assert.Equal(2.5, guard.SpentToday, 3);
    }

    [Fact]
    public void CapAndResetHour_AreValidated()
    {
        var switches = new KillSwitchState();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BillingGuard(switches, NullAlarmSink.Instance, capUsd: -1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BillingGuard(switches, NullAlarmSink.Instance, capUsd: 1, resetHourUtc: 24));
    }

    private static DateTimeOffset Fixed() => new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
}
