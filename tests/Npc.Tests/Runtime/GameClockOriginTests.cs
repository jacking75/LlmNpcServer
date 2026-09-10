using Npc.Contracts;
using Npc.Host;
using Npc.MasterData;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>A-10 — 게임 시각 복원과 <c>TickSync</c> 워치독. PRODUCTION_ROADMAP §4 A-10.</summary>
public sealed class GameClockOriginTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    [Fact]
    public void Origin_SetsGameTimeToWhatTheServerSaid()
    {
        var clock = new GameClock(s_data.Buckets, timeScale: 60);

        // 기본은 새벽 6시다 — 재기동마다 여기로 돌아가는 것이 A-10 이 고치는 결함이다.
        Assert.Equal(6, clock.GameHour);

        // 게임서버가 "지금 정오다" 라고 알린다.
        clock.RequestOrigin(startTick: 12_345, minuteOfDay: 12 * 60);

        // 예약만으로는 바뀌지 않는다 — 반영은 틱 경계에서만 한다.
        Assert.Equal(6, clock.GameHour);

        Assert.True(clock.TryApplyPendingOrigin());
        Assert.Equal(12, clock.GameHour);
        Assert.Equal(12_345, clock.Current.Value);

        // 두 번째 호출은 할 일이 없다.
        Assert.False(clock.TryApplyPendingOrigin());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(60, 1)]
    [InlineData(23 * 60 + 59, 23)]
    [InlineData(18 * 60, 18)]
    public void Origin_HandlesEveryHour(int minuteOfDay, int expectedHour)
    {
        var clock = new GameClock(s_data.Buckets, timeScale: 600);

        clock.RequestOrigin(startTick: 98_765, minuteOfDay: minuteOfDay);

        Assert.True(clock.TryApplyPendingOrigin());
        Assert.Equal(expectedHour, clock.GameHour);

        // 원점을 정규화하므로 게임 초가 음수로 내려가지 않는다.
        Assert.True(clock.GameSeconds >= 0, $"GameSeconds={clock.GameSeconds}");
        Assert.True(clock.GameDay >= 0, $"GameDay={clock.GameDay}");
    }

    [Fact]
    public void Origin_IsIgnoredWhenTheServerDoesNotKnow()
    {
        var clock = new GameClock(s_data.Buckets, timeScale: 60);

        // v1 게임서버는 시각을 모른다. -1 이 그 뜻이고, 없는 것을 있는 척하지 않는다.
        clock.RequestOrigin(startTick: 500, minuteOfDay: -1);

        Assert.False(clock.TryApplyPendingOrigin());
        Assert.Equal(6, clock.GameHour);
        Assert.Equal(0, clock.Current.Value);
    }

    [Fact]
    public void Origin_BeatsTheRestoredSnapshotClock()
    {
        var clock = new GameClock(s_data.Buckets, timeScale: 60);

        // A-01 이 스냅샷에서 복원했다.
        clock.RestoreTo(new Tick(1_000), syncedTick: 1_000);
        Assert.Equal(1_000, clock.Current.Value);

        // 그 뒤 게임서버가 다른 시각을 알린다 — 게임서버가 이긴다.
        clock.RequestOrigin(startTick: 7_000, minuteOfDay: 3 * 60);

        Assert.True(clock.TryApplyPendingOrigin());
        Assert.Equal(7_000, clock.Current.Value);
        Assert.Equal(3, clock.GameHour);
    }

    [Fact]
    public void Watchdog_AlarmsWhenTicksStop()
    {
        var clock = new GameClock(s_data.Buckets, timeScale: 60);
        var alarms = new List<string>();

        using var watchdog = new TickSyncWatchdog(clock, thresholdSeconds: 5, alarms.Add);

        // 아직 임계 안이다.
        Assert.False(watchdog.Check(1_000));
        Assert.False(watchdog.Stalled);

        // 5초가 지났는데 틱이 그대로다.
        Assert.True(watchdog.Check(7_000));
        Assert.True(watchdog.Stalled);
        Assert.Single(alarms);

        // 같은 정지로 경보를 반복하지 않는다 — 한 사건에 한 번이다.
        Assert.False(watchdog.Check(20_000));
        Assert.Single(alarms);
    }

    [Fact]
    public void Watchdog_ReportsRecovery()
    {
        var clock = new GameClock(s_data.Buckets, timeScale: 60);
        var alarms = new List<string>();

        using var watchdog = new TickSyncWatchdog(clock, thresholdSeconds: 5, alarms.Add);

        watchdog.Check(0);
        Assert.True(watchdog.Check(6_000));

        // 틱이 다시 돈다.
        clock.SyncTo(new Tick(10));
        clock.TryAdvance(out _);

        Assert.False(watchdog.Check(6_100));
        Assert.False(watchdog.Stalled);
        Assert.Equal(2, alarms.Count);
        Assert.Contains("복구", alarms[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Watchdog_MeasuresHowFarBehindWeAre()
    {
        var clock = new GameClock(s_data.Buckets, timeScale: 60);

        using var watchdog = new TickSyncWatchdog(clock, thresholdSeconds: 5, _ => { });

        Assert.Equal(0, watchdog.TicksBehind);

        // 게임서버는 100틱까지 알렸는데 우리는 아직 0틱이다.
        clock.SyncTo(new Tick(100));

        Assert.Equal(100, watchdog.TicksBehind);

        clock.TryAdvance(out _);

        Assert.Equal(99, watchdog.TicksBehind);
    }

    [Fact]
    public void Watchdog_CanBeDisabled()
    {
        var clock = new GameClock(s_data.Buckets, timeScale: 60);
        var alarms = new List<string>();

        using var watchdog = new TickSyncWatchdog(clock, thresholdSeconds: 0, alarms.Add);

        watchdog.Start();

        // 임계 0 이면 주기 루프를 띄우지 않는다. 직접 Check 를 불러도 임계가 0 이라 즉시 걸리지만,
        // 그 경로를 타지 않는 것이 옵션의 뜻이다.
        Assert.Empty(alarms);
    }
}
