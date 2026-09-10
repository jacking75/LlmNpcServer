using Npc.Core;
using Npc.Host.Observability;
using Npc.Planning;

namespace Npc.Tests.Host;

/// <summary>A-05 · C-02 — 경보 싱크와 예산 임계. PRODUCTION_ROADMAP §4 A-05 · §6 C-02.</summary>
public sealed class AlarmSinkTests
{
    [Fact]
    public void Text_WritesOneParsableLine()
    {
        var writer = new StringWriter();
        var sink = new TextAlarmSink(writer);

        sink.Raise(new AlarmPayload(
            AlarmKind.TokenBudget, AlarmSeverity.Warning, "TokenBudget:0.80", "80% 소진", 0.81));

        string line = writer.ToString().Trim();

        // 형식을 고정한다 — 수집기가 파싱할 수 있어야 하고 사람도 읽을 수 있어야 한다.
        Assert.StartsWith("alarm kind=TokenBudget sev=Warning key=TokenBudget:0.80", line, StringComparison.Ordinal);
        Assert.Contains("value=0.81", line, StringComparison.Ordinal);
        Assert.Contains("msg=80% 소진", line, StringComparison.Ordinal);
        Assert.Equal(1, sink.Raised);
    }

    [Fact]
    public void Cooldown_SuppressesTheSameKey()
    {
        long now = 0;
        var writer = new StringWriter();
        var text = new TextAlarmSink(writer);
        var sink = new CooldownAlarmSink(text, cooldownSeconds: 300, () => now);

        var payload = new AlarmPayload(AlarmKind.LinkState, AlarmSeverity.Critical, "Faulted", "링크 결함");

        sink.Raise(payload);
        sink.Raise(payload);
        sink.Raise(payload);

        // 없으면 링크가 흔들릴 때 초당 수십 건이 나가고 진짜 경보가 묻힌다.
        Assert.Equal(1, text.Raised);
        Assert.Equal(2, sink.Suppressed);

        // 쿨다운이 지나면 다시 통과한다.
        now = 300_001;
        sink.Raise(payload);

        Assert.Equal(2, text.Raised);
    }

    [Fact]
    public void Cooldown_DoesNotSuppressDifferentKeys()
    {
        var writer = new StringWriter();
        var text = new TextAlarmSink(writer);
        var sink = new CooldownAlarmSink(text, cooldownSeconds: 300, () => 0);

        sink.Raise(new AlarmPayload(AlarmKind.LinkState, AlarmSeverity.Info, "Connected", "연결"));
        sink.Raise(new AlarmPayload(AlarmKind.LinkState, AlarmSeverity.Critical, "Faulted", "결함"));
        sink.Raise(new AlarmPayload(AlarmKind.EventGap, AlarmSeverity.Critical, "gap", "갭"));

        // 억제는 (종류, 열쇠) 단위다. 같은 종류의 다른 사건을 접으면 사고를 놓친다.
        Assert.Equal(3, text.Raised);
        Assert.Equal(0, sink.Suppressed);
    }

    [Fact]
    public void Cooldown_ZeroDisablesSuppression()
    {
        var writer = new StringWriter();
        var text = new TextAlarmSink(writer);
        var sink = new CooldownAlarmSink(text, cooldownSeconds: 0, () => 0);

        var payload = new AlarmPayload(AlarmKind.RateLimited, AlarmSeverity.Info, "rl", "제한");

        sink.Raise(payload);
        sink.Raise(payload);

        Assert.Equal(2, text.Raised);
    }

    [Fact]
    public void Composite_FansOut()
    {
        var a = new TextAlarmSink(new StringWriter());
        var b = new TextAlarmSink(new StringWriter());
        var sink = new CompositeAlarmSink(a, b);

        sink.Raise(new AlarmPayload(AlarmKind.Failover, AlarmSeverity.Warning, "engine", "페일오버"));

        Assert.Equal(1, a.Raised);
        Assert.Equal(1, b.Raised);
    }

    [Fact]
    public void WebhookPayload_IsSlackCompatible()
    {
        string json = WebhookAlarmSink.Format(new AlarmPayload(
            AlarmKind.CostBudget, AlarmSeverity.Critical, "CostBudget:1.00", "캡 초과", 1.02));

        // Slack·Teams 가 둘 다 이해하는 최소 형태는 "text" 하나다. 나머지는 라우팅용 곁가지다.
        Assert.Contains("\"text\"", json, StringComparison.Ordinal);
        Assert.Contains("[Critical] CostBudget", json, StringComparison.Ordinal);
        Assert.Contains("\"severity\":\"Critical\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_RaisesEachThresholdOncePerDay()
    {
        var writer = new StringWriter();
        var text = new TextAlarmSink(writer);
        var limits = new ReplanBudgetLimits(1, 1, DailyTokenCap: 1_000, DailyCostCapUsd: 2.0, 0.1);
        var bridge = new BudgetAlarmBridge(text, limits);

        bridge.Observe(tokensToday: 700, costToday: 0.1, day: 0);
        Assert.Equal(0, bridge.ThresholdAlarms);

        // 80% 를 넘었다 — "아직 넘지 않았지만 곧 넘는다" 가 사람이 조치할 수 있는 유일한 시점이다.
        bridge.Observe(tokensToday: 800, costToday: 0.1, day: 0);
        Assert.Equal(1, bridge.ThresholdAlarms);

        // 같은 임계를 두 번 울리지 않는다.
        bridge.Observe(tokensToday: 850, costToday: 0.1, day: 0);
        Assert.Equal(1, bridge.ThresholdAlarms);

        // 95% · 100% 는 각각 한 번씩.
        bridge.Observe(tokensToday: 1_000, costToday: 0.1, day: 0);
        Assert.Equal(3, bridge.ThresholdAlarms);

        // 날이 바뀌면 다시 센다. 90% 는 80% 임계 하나만 넘는다.
        bridge.Observe(tokensToday: 900, costToday: 0.1, day: 1);
        Assert.Equal(4, bridge.ThresholdAlarms);
    }

    [Fact]
    public void Budget_TracksTokensAndCostSeparately()
    {
        var writer = new StringWriter();
        var text = new TextAlarmSink(writer);
        var limits = new ReplanBudgetLimits(1, 1, DailyTokenCap: 1_000, DailyCostCapUsd: 2.0, 0.1);
        var bridge = new BudgetAlarmBridge(text, limits);

        // 비용만 80% 를 넘었다. 토큰 임계는 그대로여야 한다.
        bridge.Observe(tokensToday: 100, costToday: 1.7, day: 0);

        Assert.Equal(1, bridge.ThresholdAlarms);
        Assert.Contains("kind=CostBudget", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("kind=TokenBudget", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_MapsRateLimitedInsteadOfSwallowingIt()
    {
        var writer = new StringWriter();
        var text = new TextAlarmSink(writer);
        var bridge = new BudgetAlarmBridge(text, ReplanBudgetLimits.Measured);

        bridge.OnBudgetAlarm(new ReplanBudgetAlarm(
            ReplanBudgetAlarmKind.RateLimited, Tier.T2, Tier.T1, 0, 0, default));

        // 예전에는 로그조차 없어서 "왜 처리율이 안 오르나" 를 추적할 방법이 없었다.
        Assert.Equal(1, text.Raised);
        Assert.Contains("kind=RateLimited sev=Info", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_MapsCapsToCritical()
    {
        var writer = new StringWriter();
        var text = new TextAlarmSink(writer);
        var bridge = new BudgetAlarmBridge(text, ReplanBudgetLimits.Measured);

        bridge.OnBudgetAlarm(new ReplanBudgetAlarm(
            ReplanBudgetAlarmKind.TokenCapExceeded, Tier.T2, Tier.T1, 15_000_000, 2.0, default));

        Assert.Contains("kind=TokenBudget sev=Critical", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Null_SwallowsEverything()
    {
        // 경보가 프로세스를 죽이면 그것이 장애다. 널 싱크는 아무것도 하지 않고 던지지 않는다.
        NullAlarmSink.Instance.Raise(
            new AlarmPayload(AlarmKind.TickAllocation, AlarmSeverity.Critical, "k", "m"));
    }

    [Fact]
    public void Thresholds_AreAscending()
    {
        ReadOnlySpan<double> thresholds = BudgetAlarmBridge.Thresholds;

        for (int i = 1; i < thresholds.Length; i++)
        {
            Assert.True(thresholds[i] > thresholds[i - 1], "임계는 오름차순이어야 한다");
        }

        Assert.Equal(1.0, thresholds[^1]);
    }
}
