using System.Text.RegularExpressions;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>docs/11 §5. 시간 기준은 TickSync 하나뿐이고 DateTime 은 쓰지 않는다.</summary>
public sealed class GameClockTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static GameClock Clock(int timeScale = 600, int startHour = 6) =>
        new(s_data.Buckets, timeScale, startHour);

    /// <summary>T1-29 완료 조건 — 게임 24시간에 6구간을 전부 통과한다.</summary>
    [Fact]
    public void GameClock_TimeOfDayTransitions()
    {
        GameClock clock = Clock();
        var seen = new List<TimeOfDay> { clock.TimeOfDay };
        int transitions = 0;

        long ticks = clock.TicksPerGameDay;
        for (long i = 0; i < ticks; i++)
        {
            Assert.True(clock.Step(out _));

            if (clock.TimeOfDayChanged)
            {
                seen.Add(clock.TimeOfDay);
                transitions++;
            }
        }

        Assert.Equal(Enum.GetValues<TimeOfDay>().Length, seen.Distinct().Count());
        Assert.Equal(6, transitions);
        Assert.Equal(1, clock.GameDay);
    }

    [Fact]
    public void GameClock_DoesNotAdvanceWithoutTickSync()
    {
        GameClock clock = Clock();

        Assert.False(clock.TryAdvance(out Tick tick));
        Assert.Equal(0, tick.Value);

        clock.SyncTo(new Tick(3));

        Assert.True(clock.TryAdvance(out _));
        Assert.True(clock.TryAdvance(out _));
        Assert.True(clock.TryAdvance(out Tick third));
        Assert.Equal(3, third.Value);
        Assert.False(clock.TryAdvance(out _));
    }

    /// <summary>재전송된 TickSync 가 시계를 되감으면 안 된다 (N7).</summary>
    [Fact]
    public void GameClock_SyncNeverGoesBackwards()
    {
        GameClock clock = Clock();

        clock.SyncTo(new Tick(10));
        while (clock.TryAdvance(out _))
        {
        }

        Assert.Equal(10, clock.Current.Value);

        clock.SyncTo(new Tick(3));

        Assert.False(clock.TryAdvance(out _));
        Assert.Equal(10, clock.Current.Value);
    }

    /// <summary>docs/11 §5 — TimeScale 60 이면 게임 하루가 실시간 24분이다.</summary>
    [Fact]
    public void GameClock_TimeScaleMatchesSpec()
    {
        var clock = new GameClock(s_data.Buckets, timeScale: 60);

        // 게임 하루 = 86,400 게임초. 스케일 60 → 실시간 1,440초 → 14,400틱.
        Assert.Equal(14_400, clock.TicksPerGameDay);
        Assert.Equal(1_440, clock.TicksPerGameDay / Tick.PerSecond);

        // CLAUDE.md §1 의 --time-scale 600 --days 7
        var fast = new GameClock(s_data.Buckets, timeScale: 600);
        Assert.Equal(1_440, fast.TicksPerGameDay);
        Assert.Equal(10_080, fast.TicksForGameDays(7));
    }

    [Fact]
    public void GameClock_StartsAtRequestedHour()
    {
        GameClock dawn = Clock(startHour: 5);
        GameClock night = Clock(startHour: 23);

        Assert.Equal(5, dawn.GameHour);
        Assert.Equal(TimeOfDay.Dawn, dawn.TimeOfDay);
        Assert.Equal(23, night.GameHour);
        Assert.Equal(TimeOfDay.Night, night.TimeOfDay);
    }

    [Fact]
    public void GameClock_RejectsBadArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameClock(s_data.Buckets, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameClock(s_data.Buckets, 60, 24));
        Assert.Throws<ArgumentNullException>(() => new GameClock(null!, 60));
    }

    [Fact]
    public void GameClock_AdvanceDoesNotAllocate()
    {
        GameClock clock = Clock();
        clock.SyncTo(new Tick(100_000));

        // JIT 티어 승격이 끝날 때까지 돌린다. 승격 시점에 한 번 할당이 잡힌다.
        for (int i = 0; i < 30_000; i++)
        {
            clock.TryAdvance(out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            clock.TryAdvance(out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>T1-29 완료 조건 — 게임 로직에 DateTime 이 0 개여야 한다 (CLAUDE.md §2.3).</summary>
    [Fact]
    public void Runtime_UsesNoWallClock()
    {
        string[] banned = ["DateTime", "DateTimeOffset", "Stopwatch", "Environment.TickCount"];
        List<string> violations = [];

        foreach (string file in Directory.EnumerateFiles(
            TestPaths.At("src", "Npc.Runtime"), "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);

            foreach (string token in banned)
            {
                // 주석 안의 언급은 허용한다 — 금지 이유를 적어두는 것이 오히려 좋다.
                foreach (Match match in Regex.Matches(text, $@"\b{Regex.Escape(token)}\b"))
                {
                    int lineStart = text.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;
                    string line = text[lineStart..text.IndexOf('\n', match.Index)].TrimStart();

                    if (line.StartsWith("//", StringComparison.Ordinal)
                        || line.StartsWith("///", StringComparison.Ordinal)
                        || line.StartsWith('*'))
                    {
                        continue;
                    }

                    violations.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(violations);
    }
}
