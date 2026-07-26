using Npc.Host;
using Npc.Host.Api;
using Npc.Planning;

namespace Npc.Tests.Host;

/// <summary>docs/14 §8 "NPC 추적" 패널 · T4-21.</summary>
public sealed class NpcTraceTests
{
    private static readonly string s_html =
        File.ReadAllText(TestPaths.At("src", "Npc.Host", "wwwroot", "dashboard.html"));

    private static HostOptions Options(params string[] args)
    {
        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return options with { MasterData = TestPaths.MasterData };
    }

    /// <summary>
    /// T4-21 완료 조건 — 임의 NPC id 로 상태를 읽고 틱마다 갱신된다.
    /// </summary>
    [Fact]
    public async Task Trace_ReturnsLiveStateForAnyNpc()
    {
        await using NpcHost host = NpcHost.Create(
            Options("--loopback", "--npcs", "200", "--time-scale", "600", "--days", "1",
                    "--max-speed", "--no-dashboard"),
            TextWriter.Null);

        // 기동 직후 — 아직 한 틱도 안 돌았다.
        NpcTrace before = host.Trace(7);

        Assert.True(before.Found);
        Assert.Equal(7, before.Npc);
        Assert.NotEmpty(before.Archetype);
        Assert.NotEmpty(before.PlanGoal);
        Assert.NotEmpty(before.Flags);
        Assert.NotEmpty(before.Steps);

        await host.RunAsync(CancellationToken.None);

        NpcTrace after = host.Trace(7);

        Assert.True(after.Found);
        Assert.True(after.Tick > before.Tick, "틱이 진행되지 않았다.");
        Assert.Equal(before.Archetype, after.Archetype);

        // 하루를 돌았으면 무언가는 일어났어야 한다 — 기억이든 스텝 전진이든.
        Assert.True(
            after.Recent.Length > 0 || after.Steps.Any(s => s.Current),
            "하루를 돌았는데 기억도 현재 스텝도 없다.");

        // 최근 사건은 시각 내림차순이다 — 링 슬롯 순서는 시간 순서가 아니다 (docs/11 §3).
        for (int i = 1; i < after.Recent.Length; i++)
        {
            Assert.True(after.Recent[i - 1].At >= after.Recent[i].At, "최근 사건이 시각순이 아니다.");
        }
    }

    /// <summary>없는 첨자는 <c>Found=false</c> 다. 예외를 던지면 대시보드가 통째로 멈춘다.</summary>
    [Fact]
    public async Task Trace_ReportsMissingNpc()
    {
        await using NpcHost host = NpcHost.Create(
            Options("--loopback", "--npcs", "40", "--time-scale", "600", "--days", "1",
                    "--max-speed", "--no-dashboard"),
            TextWriter.Null);

        Assert.False(host.Trace(9_999).Found);
        Assert.False(host.Trace(-1).Found);
        Assert.True(host.Trace(0).Found);
        Assert.True(host.Trace(39).Found);
        Assert.False(host.Trace(40).Found);
    }

    /// <summary>
    /// T4-21 완료 조건 — 추적 조회가 틱 루프를 막지 않는다.
    ///
    /// <b>절대 지연으로 판정하지 않는다.</b> 이 테스트는 다른 테스트와 같이 도는데,
    /// 그러면 기계 부하가 그대로 측정치에 섞여 무엇을 재는지 알 수 없게 된다
    /// (<c>PlanStore_ResolveIsO1</c> 에서 겪은 것과 같은 문제다).
    /// 같은 회차 안에서 <b>폴링 없이</b> 한 번, <b>폴링하면서</b> 한 번 돌려 비교한다 —
    /// 조회가 락을 잡으면 둘째가 크게 나빠진다.
    /// </summary>
    [Fact]
    public async Task Trace_DoesNotBlockTickLoop()
    {
        double quiet = await MeasureAsync(poll: false);
        double polled = await MeasureAsync(poll: true);

        Assert.True(quiet > 0, "기준 회차의 p99 가 0 이다. 측정이 무의미하다.");

        // 슬랙을 크게 잡는다. 여기서 잡고 싶은 것은 "락 때문에 배로 느려지는가" 이지
        // 몇 % 의 차이가 아니다.
        Assert.True(
            polled <= (quiet * 4) + 2,
            $"폴링 없이 p99 {quiet:F3}ms · 폴링하며 p99 {polled:F3}ms — 추적 조회가 틱을 막는다.");

        static async Task<double> MeasureAsync(bool poll)
        {
            await using NpcHost host = NpcHost.Create(
                Options("--loopback", "--npcs", "2000", "--time-scale", "600", "--days", "1",
                        "--player-bots", "20", "--max-speed", "--no-dashboard"),
                TextWriter.Null);

            using var stop = new CancellationTokenSource();
            long polls = 0;

            // 스핀이 아니라 짧은 대기다. 스핀으로 코어를 태우면 그 자체가 틱을 느리게 만들어
            // 무엇을 재는지 알 수 없어진다.
            Task poller = poll
                ? Task.Run(
                    async () =>
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            Assert.True(host.Trace((int)(polls % host.Npcs)).Found);
                            polls++;

                            await Task.Yield();
                        }
                    },
                    CancellationToken.None)
                : Task.CompletedTask;

            await host.RunAsync(CancellationToken.None);
            await stop.CancelAsync();
            await poller;

            if (poll)
            {
                Assert.True(polls > 100, $"추적 조회가 {polls} 번밖에 안 돌았다. 측정이 무의미하다.");
            }

            Npc.Host.Metrics.MetricsSnapshot m = host.Metrics.Snapshot();

            Assert.Equal(0, m.Tick.Overruns);
            return m.Tick.P99Ms;
        }
    }

    /// <summary>
    /// 추적 조회는 개별 플랜의 LRU 를 건드리지 않는다.
    /// 대시보드를 열어 둔 것만으로 누가 슬롯을 잃는지가 달라지면 리플레이가 깨진다 (T4-12).
    /// </summary>
    [Fact]
    public async Task Trace_DoesNotTouchIndividualPoolLru()
    {
        await using NpcHost host = NpcHost.Create(
            Options("--loopback", "--npcs", "40", "--time-scale", "600", "--days", "1",
                    "--max-speed", "--no-dashboard"),
            TextWriter.Null);

        string source = File.ReadAllText(
            TestPaths.At("src", "Npc.Host", "Api", "NpcTraceEndpoint.cs"));

        // TryFor 는 LRU 를 갱신한다. 추적은 TryPeekFor 만 써야 한다.
        Assert.Contains("TryPeekFor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("plans.TryFor", source, StringComparison.Ordinal);

        await Task.CompletedTask;
    }

    /// <summary>Program.cs 가 /npc/{id} 를 매핑하고 대시보드가 그것을 읽는다.</summary>
    [Fact]
    public void Trace_IsMappedAndRenderedByDashboard()
    {
        string program = File.ReadAllText(TestPaths.At("src", "Npc.Host", "Program.cs"));

        Assert.Contains("NpcTraceEndpoint.Route", program, StringComparison.Ordinal);
        Assert.Contains("host.Trace(id)", program, StringComparison.Ordinal);
        Assert.Equal("/npc/{id:int}", NpcTraceEndpoint.Route);

        Assert.Contains("id=\"p-trace\"", s_html, StringComparison.Ordinal);
        Assert.Contains("id=\"p-trace-steps\"", s_html, StringComparison.Ordinal);
        Assert.Contains("id=\"p-trace-events\"", s_html, StringComparison.Ordinal);
        Assert.Contains("id=\"trace-id\"", s_html, StringComparison.Ordinal);
        Assert.Contains("fetch(`/npc/${id}`", s_html, StringComparison.Ordinal);

        // 폴링 루프가 추적도 같이 갱신해야 "틱마다 갱신" 이 성립한다.
        Assert.Contains("await pollTrace()", s_html, StringComparison.Ordinal);
    }

    /// <summary>개별 플랜 슬롯이면 <c>individual</c> 로, 회수됐으면 그렇게 표시한다.</summary>
    [Fact]
    public void Trace_LabelsPlanOrigin()
    {
        string source = File.ReadAllText(
            TestPaths.At("src", "Npc.Host", "Api", "NpcTraceEndpoint.cs"));

        Assert.Contains("\"individual\"", source, StringComparison.Ordinal);
        Assert.Contains("individual(회수됨)", source, StringComparison.Ordinal);
        Assert.Contains("\"fallback\"", source, StringComparison.Ordinal);
        Assert.Contains("\"bucket\"", source, StringComparison.Ordinal);

        Assert.True(IndividualPlanPool.IsIndividual(-1));
    }
}
