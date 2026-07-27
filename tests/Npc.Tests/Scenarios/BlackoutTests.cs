using System.Globalization;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Host;
using Npc.Host.Metrics;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;
using Npc.Sim;

namespace Npc.Tests.Scenarios;

/// <summary>
/// T5-09 — 시나리오 C. docs/15 §4 · docs/00 §2.
///
/// <b>"만들 수 있는가" 가 아니라 "끊겨도 도는가" 를 본다.</b>
/// <c>scenarios/blackout.jsonl</c> 이 T2 → T1 → PlanStore 를 차례로 끊는다.
///
/// 틱 수(6,000 / 12,000 / 18,000)는 시나리오 파일에 고정돼 있으므로, 어느 단계까지
/// 진행할지는 <c>--days</c> 로 고른다. 600배속에서 게임 하루는 1,440틱이다.
/// </summary>
[Trait("Category", "FaultInjection")]
public sealed class BlackoutTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>600배속의 게임 하루.</summary>
    private const int TicksPerDay = 1_440;

    /// <summary>단계별 <c>--days</c>. 각 값의 총 틱이 그 단계의 차단 틱을 막 넘는다.</summary>
    private const int DaysBeforeAnyCut = 4;    //  5,760 <  6,000

    private const int DaysAfterT2Cut = 5;      //  7,200 < 12,000

    private const int DaysAfterT1Cut = 9;      // 12,960 < 18,000

    private const int DaysAfterStoreCut = 13;  // 18,720 > 18,000

    // ---------------------------------------------------------------- 1. 3단계가 순서대로 끊긴다

    [Fact]
    public async Task Blackout_CutsThreeTiersInOrder()
    {
        (int days, KillSwitchTarget[] expected)[] stages =
        [
            (DaysBeforeAnyCut, []),
            (DaysAfterT2Cut, [KillSwitchTarget.T2]),
            (DaysAfterT1Cut, [KillSwitchTarget.T2, KillSwitchTarget.T1]),
            (DaysAfterStoreCut, [KillSwitchTarget.T2, KillSwitchTarget.T1, KillSwitchTarget.PlanStore]),
        ];

        long previousSteps = 0;

        foreach ((int days, KillSwitchTarget[] expected) in stages)
        {
            await using NpcHost host = Host(npcs: 200, days: days);

            await host.RunAsync(CancellationToken.None);

            ScenarioRunner scenario = host.Driver!.Scenario;

            Assert.Equal(expected, scenario.KillSwitchesFired);

            // 기록만이 아니라 상태도 서야 한다 — 안 끊긴 채로 통과하면 게이트가 거짓이 된다.
            foreach (KillSwitchTarget target in Enum.GetValues<KillSwitchTarget>())
            {
                Assert.Equal(expected.Contains(target), scenario.Switches.IsDisabled(target));
            }

            // NPC 가 멈추지 않았다 — 단계가 깊어질수록 스텝은 계속 늘어난다.
            long steps = host.Snapshot().StepsAdvanced;

            Assert.True(steps > previousSteps, Report(days, host));
            previousSteps = steps;

            AssertHealthy(days, host);
        }
    }

    // ---------------------------------------------------------------- 2. 5,000 NPC 가 3단계 전부를 버틴다

    /// <summary>
    /// docs/15 §4 의 완료 조건 — <b>세 단계 모두에서 NPC 가 멈추지 않고, 크래시가 없고,
    /// 틱 시간이 예산 내다.</b> 3단계에서는 폴백 40개만 남는다.
    /// </summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Blackout_FiveThousandNpcsSurviveEveryStage()
    {
        await using NpcHost host = Host(npcs: 5_000, days: DaysAfterStoreCut);

        await host.RunAsync(CancellationToken.None);

        MetricsSnapshot metrics = host.Metrics.Snapshot();

        Assert.Equal(5_000, metrics.Npc.Total);
        Assert.Equal(DaysAfterStoreCut * TicksPerDay, metrics.Tick.Ticks);

        Assert.Equal(
            [KillSwitchTarget.T2, KillSwitchTarget.T1, KillSwitchTarget.PlanStore],
            host.Driver!.Scenario.KillSwitchesFired);

        // 폴백 40개는 끝까지 살아 있어야 한다 — 여기가 비면 NPC 가 멈춘다.
        Assert.Equal(s_data.Archetypes.Count, s_data.Fallbacks!.Count);

        AssertHealthy(DaysAfterStoreCut, host);

        // 마지막 단계에서도 스텝이 계속 나왔다.
        Assert.True(host.Snapshot().StepsAdvanced > 5_000, Report(DaysAfterStoreCut, host));
    }

    // ---------------------------------------------------------------- 3. PlanStore 차단이 실제로 먹는다

    /// <summary>
    /// 프리베이크 버킷을 채워 두고 3단계까지 간다.
    /// <b>차단 전에는 캐시가 맞고, 차단 뒤에는 전부 미스가 되어야 한다</b> —
    /// 빈 스토어로 돌리면 원래 전부 미스라 아무것도 증명하지 못한다.
    /// </summary>
    [Fact]
    public async Task Blackout_PlanStoreCutFallsBackToTheFortyPlans()
    {
        string store = SeedPlanStore();

        try
        {
            CachePanel alive = await CacheAfterAsync(store, DaysAfterT1Cut);
            CachePanel cut = await CacheAfterAsync(store, DaysAfterStoreCut);

            // 스토어가 살아 있는 동안에는 2,880 버킷이 전부 채워져 있으므로 미스가 날 수 없다.
            Assert.Equal(BucketKey.TotalKeys, alive.FilledBuckets);
            Assert.Equal(0, alive.ColdBuckets);
            Assert.True(alive.Hits > 0, "차단 전에 버킷 히트가 하나도 없다 — 씨앗이 잘못됐다.");
            Assert.Equal(0, alive.Misses);

            // 차단 뒤의 조회는 전부 미스다. 두 실행의 길이가 다르므로 절대 수가 아니라
            // 비율을 본다 — 긴 쪽이 차단 전 구간에서 히트를 더 쌓는 것은 당연하다.
            Assert.True(cut.Misses > 0, Rate(alive, cut));
            Assert.True(cut.HitRate < alive.HitRate, Rate(alive, cut));
            Assert.True(cut.Hits >= alive.Hits, Rate(alive, cut));

            // 버킷은 그대로 채워져 있다 — 지운 것이 아니라 보지 않는 것이다.
            Assert.Equal(BucketKey.TotalKeys, cut.FilledBuckets);
        }
        finally
        {
            Directory.Delete(store, recursive: true);
        }
    }

    private static async Task<CachePanel> CacheAfterAsync(string store, int days)
    {
        await using NpcHost host = Host(npcs: 200, days: days, planStore: store);

        await host.RunAsync(CancellationToken.None);

        AssertHealthy(days, host);

        return host.Metrics.Snapshot().Cache;
    }

    /// <summary>
    /// 2,880 버킷을 아키타입 폴백으로 채운 임시 스토어를 만든다.
    /// 내용은 폴백과 같아도 <see cref="PlanOrigin.Prebaked"/> 라 히트/미스로 구분된다.
    /// </summary>
    private static string SeedPlanStore()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"npc-blackout-{Guid.NewGuid():N}");

        Directory.CreateDirectory(dir);

        for (int index = 0; index < BucketKey.TotalKeys; index++)
        {
            BucketKey bucket = BucketKey.FromIndex(index);

            if (s_data.Fallbacks!.For(bucket.A) is not { } fallback)
            {
                continue;
            }

            PlanStoreIo.SavePlan(
                dir,
                PlanLayer.Plans,
                bucket,
                fallback with { Bucket = bucket, Origin = PlanOrigin.Prebaked },
                s_data);
        }

        return dir;
    }

    // ---------------------------------------------------------------- 공통

    private static NpcHost Host(int npcs, int days, string? planStore = null)
    {
        string[] args =
        [
            "--loopback",
            "--scenario", TestPaths.At("scenarios", "blackout.jsonl"),
            "--npcs", npcs.ToString(CultureInfo.InvariantCulture),
            "--time-scale", "600",
            "--days", days.ToString(CultureInfo.InvariantCulture),
            "--max-speed",
            "--no-dashboard",
            .. planStore is null ? Array.Empty<string>() : ["--planstore", planStore],
        ];

        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return NpcHost.Create(options with { MasterData = TestPaths.MasterData }, TextWriter.Null);
    }

    /// <summary>크래시 0 · 틱 예산 내 · 이벤트 시퀀스 갭 0.</summary>
    private static void AssertHealthy(int days, NpcHost host)
    {
        MetricsSnapshot metrics = host.Metrics.Snapshot();

        Assert.Equal(days * TicksPerDay, metrics.Tick.Ticks);
        Assert.True(metrics.Tick.P99Ms <= NpcServerLoop.TickBudgetMs, Report(days, host));
        Assert.Equal(0, metrics.Tick.Overruns);
        Assert.Equal(0, metrics.Link.EventGaps);
    }

    private static string Report(int days, NpcHost host)
    {
        MetricsSnapshot m = host.Metrics.Snapshot();
        HostSnapshot s = host.Snapshot();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{days}일 · 틱 {m.Tick.Ticks} · p99 {m.Tick.P99Ms:0.###}ms · 오버런 {m.Tick.Overruns} · "
            + $"스텝 {s.StepsAdvanced} · 타임아웃 {s.TimeoutsSynthesized} · 갭 {m.Link.EventGaps}");
    }

    private static string Rate(in CachePanel alive, in CachePanel cut) => string.Create(
        CultureInfo.InvariantCulture,
        $"차단 전 히트 {alive.Hits}/미스 {alive.Misses} ({alive.HitRate:P1}) · "
        + $"차단 후 히트 {cut.Hits}/미스 {cut.Misses} ({cut.HitRate:P1})");
}
