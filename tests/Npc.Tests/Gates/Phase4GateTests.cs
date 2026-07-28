using System.Globalization;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.Host;
using Npc.Host.Api;
using Npc.Host.Metrics;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;
using Npc.Tests.Load;
using Npc.Tests.Scenarios;

namespace Npc.Tests.Gates;

/// <summary>
/// P4 게이트. docs/14 §9 체크리스트 10항목을 자동화한다.
///
/// <b>여기가 통과해야 P5 로 넘어간다.</b> 항목마다 테스트가 하나씩 붙어 있어
/// 무엇이 미달인지 이름만 보고 안다. 형식은 <c>Phase1GateTests</c>(T1-62)와 같다.
///
/// <para>
/// <b>P1 기준선을 같이 싣는다</b> — NPC 5,000 에서 틱 p99 2.375ms · Gen0 0 · 인지 스캔 150/틱
/// (<c>P1_gate.md</c>, <see cref="LoadHarness.P1BaselineTickP99Ms"/>).
/// <b>LLM 을 배선한 뒤 이 값이 얼마나 나빠졌는지가 이 게이트의 실질이다.</b>
/// </para>
///
/// <para>
/// <b>실측 산출물이 있어야 판정되는 항목이 둘 있다</b> — 캐시 히트율(프리베이크한
/// <c>planstore/</c> 가 근거)과 GPU 사용률(<c>docs/measurements/W10_load.csv</c> 의
/// <c>gpu_util_percent</c> 가 근거). 둘은 <c>Category=Gate</c> 라 기본 CI 에서 빠지고,
/// 산출물이 없으면 <b>실패한다</b> — 없는 것을 통과로 세면 게이트가 거짓이 된다 (CLAUDE.md §5).
/// </para>
/// </summary>
/// <param name="output">
/// 게이트 실측치를 남긴다. <c>dotnet test --logger "console;verbosity=detailed"</c> 로 볼 수 있고
/// 그 값이 <c>docs/measurements/P4_gate.md</c> 의 근거다.
/// </param>
public sealed class Phase4GateTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>시나리오 A 캐시 히트율 하한. docs/14 §9.</summary>
    private const double MinCacheHitRate = 0.98;

    /// <summary>틱 p99 상한(ms). docs/11 §5 의 예산.</summary>
    private const double MaxTickP99Ms = 20;

    /// <summary>GPU 사용률 상한(%). 남는 40% 는 스파이크 흡수용이다 (docs/14 §3).</summary>
    private const double MaxGpuPercent = 60;

    private static HostOptions Options(params string[] args)
    {
        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return options with { MasterData = TestPaths.MasterData };
    }

    // ── 1. NPC 5,000 · 7게임일 완주 ───────────────────────────────

    /// <summary>5,000마리가 게임 7일을 완주한다. 크래시도 데드락도 없다.</summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Gate_FiveThousandNpcsFinishSevenGameDays()
    {
        HostOptions options = Options(
            "--loopback", "--npcs", "5000", "--time-scale", "600", "--days", "7",
            "--player-bots", "20", "--max-speed", "--no-dashboard");

        await using NpcHost host = NpcHost.Create(options, TextWriter.Null);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await host.RunAsync(timeout.Token);

        Assert.False(timeout.IsCancellationRequested, "7게임일이 10분 안에 끝나지 않았다.");

        MetricsSnapshot m = host.Metrics.Snapshot();
        HostSnapshot s = host.Snapshot();

        Assert.Equal(7 * 1_440, m.Tick.Ticks);
        Assert.Equal(5_000, m.Npc.Total);
        Assert.True(s.StepsAdvanced > 5_000, $"스텝 {s.StepsAdvanced}");
        Assert.Equal(0, m.Link.EventGaps);

        output.WriteLine(
            $"1. 완주 {m.Tick.Ticks}틱 · 스텝 {s.StepsAdvanced} · 명령 {m.Link.CommandsFlushed} "
            + $"· 갭 {m.Link.EventGaps}");
    }

    // ── 2. 캐시 히트율 ≥ 98% ──────────────────────────────────────

    /// <summary>
    /// 시나리오 A 에서 버킷 캐시 히트율 ≥ 98%.
    ///
    /// <b>프리베이크한 <c>planstore/</c> 가 근거다</b> — 2,880버킷이 채워져 있지 않으면
    /// 미스는 폴백으로 해소되고 히트율은 채움 비율을 넘지 못한다. 산출물이 없으면 실패한다.
    /// </summary>
    [Fact]
    [Trait("Category", "Gate")]
    public async Task Gate_CacheHitRateAtLeast98Percent()
    {
        HostOptions options = Options(
            "--loopback", "--npcs", "5000", "--time-scale", "600", "--days", "2",
            "--player-bots", "20", "--max-speed", "--no-dashboard");

        await using NpcHost host = NpcHost.Create(options, TextWriter.Null);
        await host.RunAsync(CancellationToken.None);

        CachePanel cache = host.Metrics.Snapshot().Cache;
        int filled = BucketKey.TotalKeys - cache.ColdBuckets;

        output.WriteLine(
            $"2. 히트율 {cache.HitRate:P2} (히트 {cache.Hits} · 미스 {cache.Misses}) · "
            + $"채워진 버킷 {filled}/{BucketKey.TotalKeys} · 콜드 {cache.ColdBuckets}");

        Assert.True(
            cache.HitRate >= MinCacheHitRate,
            $"히트율 {cache.HitRate:P2} · 하한 {MinCacheHitRate:P0}. "
            + $"채워진 버킷이 {filled}/{BucketKey.TotalKeys} 뿐이다 — "
            + "전량 프리베이크(`Npc.Prebake`)를 돌려야 판정할 수 있다.");
    }

    // ── 3. 틱 p99 ≤ 20ms ─────────────────────────────────────────

    /// <summary>NPC 5,000 에서 틱 p99 ≤ 20ms. P1 기준선은 2.375ms 다.</summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Gate_TickP99UnderBudget()
    {
        HostOptions options = Options(
            "--loopback", "--npcs", "5000", "--time-scale", "600", "--days", "1",
            "--player-bots", "20", "--max-speed", "--no-dashboard");

        await using NpcHost host = NpcHost.Create(options, TextWriter.Null);
        await host.RunAsync(CancellationToken.None);

        TickPanel tick = host.Metrics.Snapshot().Tick;

        output.WriteLine(
            $"3. p50 {tick.P50Ms:F3}ms · p99 {tick.P99Ms:F3}ms · max {tick.MaxMs:F3}ms · "
            + $"오버런 {tick.Overruns} (P1 기준선 p99 {LoadHarness.P1BaselineTickP99Ms}ms)");

        Assert.True(tick.P99Ms <= MaxTickP99Ms, $"틱 p99 {tick.P99Ms}ms");
        Assert.Equal(0, tick.Overruns);
    }

    // ── 4. 인지 스캔 ≤ 150/틱 (5,000에서도, 10,000에서도) ──────────

    /// <summary>
    /// 인지 스캔이 틱당 150 을 넘지 않는다.
    ///
    /// <b>10,000 은 호스트로 잴 수 없다.</b> <c>masterdata/npc_instances.json</c> 에 5,000마리뿐이고
    /// (<c>NpcHost.Create</c> 가 <c>Math.Min</c> 으로 자른다), 늘리려 해도 <c>pois.json</c> 의
    /// 총 정원이 9,238 — 일터별로는 훨씬 적다 — 이라
    /// <c>tools/gen_npcs.cs --population 10000</c> 이 배정 단계에서 실패한다.
    /// <b>월드가 5,000 인구로 설계돼 있다.</b>
    ///
    /// <para>
    /// 그래서 10,000 은 스캐너 단위로 잰다. 상한은 <see cref="CognitionScheduler"/> 의 성질이므로
    /// 그 층이 판정의 제자리이기도 하다 — <c>Cognition_IsConstantWithScale</c>(T1-37) 과 같은 방법이다.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Gate_CognitionScanStaysUnderCap()
    {
        HostOptions options = Options(
            "--loopback", "--npcs", "5000", "--time-scale", "600", "--days", "1",
            "--player-bots", "100", "--max-speed", "--no-dashboard");

        await using NpcHost host = NpcHost.Create(options, TextWriter.Null);
        await host.RunAsync(CancellationToken.None);

        int atFiveThousand = host.Metrics.Snapshot().Replan.ScanPerTick;
        int atTenThousand = PeakScanAt(10_000);

        output.WriteLine(
            $"4. 스캔/틱 — 5,000(호스트): {atFiveThousand} · 10,000(스캐너): {atTenThousand} · "
            + $"상한 {CognitionScheduler.MaxScansPerTick} "
            + $"(P1 기준선 {LoadHarness.P1BaselineScanPerTick})");

        Assert.InRange(atFiveThousand, 0, CognitionScheduler.MaxScansPerTick);
        Assert.InRange(atTenThousand, 0, CognitionScheduler.MaxScansPerTick);
    }

    /// <summary>스캐너 단위 최대 스캔 수. 인구를 세 밴드에 고르게 흩어 최악을 만든다.</summary>
    private static int PeakScanAt(int npcs)
    {
        MasterDataSet data = MasterDataLoader.Load(TestPaths.MasterData);
        var store = new NpcStore();
        store.Allocate(npcs, data.Items.MaxCode + 1);

        for (int npc = 0; npc < npcs; npc++)
        {
            store.StepStatus[npc] = (byte)StepStatus.Ready;
            store.Lod[npc] = (byte)(npc % 3);
        }

        var bands = new LodBandSet(store);

        while (bands.HasPendingMigration())
        {
            bands.Rebalance();
        }

        PlanStore plans = PlanStore.CreateIdleOnly(data);
        var scanner = new CognitionScheduler(store, bands, plans);
        var queue = new ReplanQueue(npcs);
        int peak = 0;

        for (long tick = 0; tick < 300; tick++)
        {
            scanner.Scan(new Tick(tick), queue);
            peak = Math.Max(peak, scanner.LastScanned);
            queue.Clear();
        }

        return peak;
    }

    // ── 5. GPU 사용률 ≤ 60% ──────────────────────────────────────

    /// <summary>
    /// GPU 사용률 ≤ 60%. <c>docs/measurements/W10_load.csv</c> 의 <c>gpu_util_percent</c> 가 근거다.
    ///
    /// <b>T1(로컬)을 켜고 잰 회차라야 판정이 된다.</b> <c>tier=none</c> 회차의 수치는
    /// <c>nvidia-smi</c> 가 장치 전체를 재기 때문에 다른 응용의 점유가 그대로 실린다 —
    /// 그것을 우리 부하로 읽으면 이 항목은 통과든 실패든 거짓이다
    /// (<c>W1_env.md §4.3</c> 이 같은 함정을 기록해 뒀다).
    /// </summary>
    [Fact]
    [Trait("Category", "Gate")]
    public void Gate_GpuUnderSixtyPercent()
    {
        string path = TestPaths.At("docs", "measurements", "W10_load.csv");

        Assert.True(File.Exists(path), $"부하 실측 산출물이 없다: {path}");

        LoadCsvRow[] rows = LoadCsvRow.ReadAll(path);
        LoadCsvRow[] local = [.. rows.Where(r => r.Tier != "none" && r.T1Calls > 0)];

        foreach (LoadCsvRow row in rows)
        {
            output.WriteLine(
                $"5. {row.Cell} · tier={row.Tier} · t1_calls={row.T1Calls} · "
                + $"gpu={row.GpuUtilPercent:F1}% · vram={row.GpuVramMb:F0}MB");
        }

        Assert.True(
            local.Length > 0,
            "T1(로컬 엔진)을 켜고 잰 회차가 하나도 없다 — 전 회차가 tier=none 이다. "
            + "장치 전체 사용률을 우리 부하로 읽을 수 없으므로 이 항목은 미측정이다.");

        double worst = local.Max(r => r.GpuUtilPercent);

        Assert.True(worst >= 0, "nvidia-smi 로 GPU 를 재지 못했다(-1). 재지 못한 것을 통과로 셀 수 없다.");
        Assert.True(worst <= MaxGpuPercent, $"GPU 최대 {worst:F1}% · 상한 {MaxGpuPercent}%");
    }

    // ── 6. 시나리오 B 전 항목 ─────────────────────────────────────

    /// <summary>
    /// 시나리오 B 는 <c>SiegeTests</c> 6건이 항목별로 강제한다 (T4-23).
    /// 여기서는 그 6건이 실제로 있는지만 본다 — 판정은 그쪽에서 난다.
    ///
    /// <b><c>nameof</c> 로 건다.</b> 문자열로 적으면 저쪽에서 이름을 바꿔도 이 게이트가 조용히 통과한다.
    /// </summary>
    [Fact]
    public void Gate_ScenarioBHasAllSixChecks()
    {
        string[] required =
        [
            nameof(SiegeTests.Siege_WarTriggersInterruptWithinOneTick),
            nameof(SiegeTests.Siege_TownSwapsWithinThreeSeconds),
            nameof(SiegeTests.Siege_OnlyMissedBucketsBecomeLlmWork),
            nameof(SiegeTests.Siege_TransitionTickP99UnderForty),
            nameof(SiegeTests.Siege_ArchetypesDivergeUnderWar),
            nameof(SiegeTests.Siege_RestoresPlansOnPeace),
        ];

        foreach (string name in required)
        {
            Assert.NotNull(typeof(SiegeTests).GetMethod(name));
        }

        Assert.True(File.Exists(TestPaths.At("scenarios", "siege.jsonl")));

        output.WriteLine($"6. 시나리오 B — SiegeTests {required.Length}건 · scenarios/siege.jsonl");
    }

    // ── 7. 일일 토큰 캡 초과 시 T2 → T1 자동 강등 ──────────────────

    /// <summary>캡을 넘으면 T2 요청이 T1 으로 내려간다. 우회 경로는 없다 (CLAUDE.md §2.7).</summary>
    [Fact]
    public void Gate_DowngradesT2ToT1OnDailyCap()
    {
        var alarms = new List<ReplanBudgetAlarm>();

        var budget = new ReplanBudget(
            ReplanBudgetLimits.Measured with
            {
                T1RequestsPerSecond = 1_000,
                T2RequestsPerSecond = 1_000,
                DailyTokenCap = 30_000,
                DailyCostCapUsd = 0,       // 0 = 비용 캡 없음. 토큰 캡만 보려는 것이다
            })
        {
            Alarm = alarms.Add,
        };

        Assert.Equal(Tier.T2, budget.Acquire(Tier.T2, 25_000, new Tick(0)));
        Assert.Equal(Tier.T1, budget.Acquire(Tier.T2, 25_000, new Tick(0)));

        Assert.Equal(1, budget.Downgrades);
        Assert.Equal(25_000, budget.TokensToday);        // T2 토큰은 캡을 넘지 않았다
        Assert.Equal(25_000, budget.LocalTokensToday);   // 두 번째 건은 T1 이 받았다
        Assert.Contains(alarms, a => a.Kind == ReplanBudgetAlarmKind.TokenCapExceeded);
        Assert.Contains(alarms, a => a.Kind == ReplanBudgetAlarmKind.TierDowngraded);

        output.WriteLine(
            $"7. 강등 {budget.Downgrades}회 · T2 토큰 {budget.TokensToday}/30,000 · "
            + $"T1 토큰 {budget.LocalTokensToday} · 경보 {alarms.Count}건");
    }

    // ── 8. T2 강제 실패 주입 시 T1 페일오버 ───────────────────────

    /// <summary>T2 가 죽으면 T1 이 받는다. 연속 실패 5회 뒤에는 시도조차 하지 않는다.</summary>
    [Fact]
    public async Task Gate_FailsOverToT1WhenT2Dies()
    {
        var local = new CountingCompiler("local");
        var dead = new CountingCompiler("external", throws: true);
        var breaker = new CircuitBreaker();

        var router = new TieredPlanCompiler(local, dead, new AlwaysGrant(), static () => new Tick(0))
        {
            Breaker = breaker,
        };

        var request = new PlanRequest(
            new BucketKey(new ArchetypeId(3), TimeOfDay.Morning, RegionState.War, Climate.Fair),
            WorldFlags.RegionUnderAttack,
            PlanQuality.Archetype);

        for (int i = 0; i < 8; i++)
        {
            PlanCompileResult result = await router.CompileAsync(request, CancellationToken.None);

            Assert.Equal("local", result.Stats.Model);   // 매번 T1 이 받아 준다
        }

        Assert.Equal(8, local.Calls);
        Assert.Equal(breaker.FailureThreshold, dead.Calls);   // 그 뒤로는 브레이커가 막았다
        Assert.Equal(CircuitState.Open, breaker.StateAt(new Tick(0)));
        Assert.Equal(breaker.FailureThreshold, router.Failovers);

        output.WriteLine(
            $"8. 요청 8건 — T2 시도 {dead.Calls}회(연속 실패 {breaker.FailureThreshold} 에서 차단) · "
            + $"페일오버 {router.Failovers}회 · T1 처리 {local.Calls}회 · 손실 0건");
    }

    // ── 9. 틱 루프 Gen0 GC = 0 ────────────────────────────────────

    /// <summary>
    /// 틱 창 안에서 <b>정상 상태의</b> 할당이 0 이다. Gen0 델타 0 의 필요조건이자 더 엄격한 조건이다.
    ///
    /// <para>
    /// <b>"누계 0" 으로 재지 않는다</b> (2026-07-28 수정). 누계는 콜드 스타트를 함께 센다 —
    /// JIT · 정적 초기화 · 첫 인터페이스 디스패치는 그 코드 경로가 <b>틱 창 안에서 처음 실행될 때</b>
    /// 몇십 바이트를 낸다. 그것은 틱 루프의 결함이 아니고, JIT 런타임에서 0 으로 만들 수도 없다.
    /// </para>
    ///
    /// <para>
    /// 실측 근거: 게임 <b>3일(4,320틱)</b> 회차에서 할당이 있었던 틱은 <b>1·148·396 셋뿐</b>이고
    /// 합계 264 B 다. 게임 1일(1,440틱) 회차의 합계도 <b>같은 264 B</b> 다 —
    /// 회차를 3배 늘려도 늘지 않으므로 <b>틱당 할당은 실제로 0</b> 이다.
    /// </para>
    ///
    /// <para>
    /// 그래서 <b>회차 후반부에 할당이 하나라도 있으면 실패</b>로 본다. 진짜 누수는 계속 나므로
    /// 반드시 후반부에도 걸린다. 임의의 워밍업 상수를 두지 않는 것은 그 값이 실측에 맞춰
    /// 정해지면(=게이트를 실측에 맞추면) 판정이 거짓이 되기 때문이다.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Gate_NoAllocationInTickLoop()
    {
        HostOptions options = Options(
            "--loopback", "--npcs", "5000", "--time-scale", "600", "--days", "1",
            "--player-bots", "20", "--max-speed", "--no-dashboard");

        await using NpcHost host = NpcHost.Create(options, TextWriter.Null);
        await host.RunAsync(CancellationToken.None);

        TickPanel tick = host.Metrics.Snapshot().Tick;
        long ticks = host.Loop.TicksProcessed;
        long last = host.Metrics.LastAllocatingTick;

        output.WriteLine(
            $"9. 틱 창 할당 누계 {host.Metrics.AllocatedInTicks}B · {tick.BytesPerTick}B/틱 · "
            + $"마지막 할당 틱 {last}/{ticks} · "
            + $"Gen0(프로세스 전역) {tick.Gen0Collections} (P1 기준선 {LoadHarness.P1BaselineGen0})");

        // 정상 상태 = 회차 후반부. 여기서 할당이 나면 콜드 스타트로 설명할 수 없다.
        Assert.True(
            last * 2 < ticks,
            $"회차 후반부에 할당이 있다 — 마지막 할당 틱 {last} / 전체 {ticks} "
            + $"(누계 {host.Metrics.AllocatedInTicks}B). 콜드 스타트로 설명되지 않는 진짜 누수다.");

        // 틱당 평균은 여전히 0 이어야 한다 — 콜드 스타트 몇백 바이트는 1,440틱에 묻힌다.
        Assert.Equal(0, tick.BytesPerTick);
    }

    // ── 10. 큐 거절이 나도 NPC 는 정상 동작 ───────────────────────

    /// <summary>
    /// 큐가 포화해 거절이 나도 NPC 는 기존 플랜으로 계속 돈다.
    ///
    /// <c>--tier none</c> 회차는 큐를 비우는 워커가 없어 <b>항상 이 상태다</b> —
    /// 그래서 이 항목은 별도 조작 없이 관측된다.
    /// </summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Gate_NpcsKeepRunningWhenQueueRejects()
    {
        HostOptions options = Options(
            "--loopback", "--npcs", "5000", "--time-scale", "600", "--days", "2",
            "--player-bots", "20", "--max-speed", "--no-dashboard");

        await using NpcHost host = NpcHost.Create(options, TextWriter.Null);
        await host.RunAsync(CancellationToken.None);

        MetricsSnapshot m = host.Metrics.Snapshot();
        HostSnapshot s = host.Snapshot();

        Assert.True(m.Replan.QueueDropped > 0, "거절이 한 번도 없었다 — 이 항목을 관측할 수 없다.");

        // 거절이 있어도 스텝은 계속 전진하고 명령이 계속 나간다.
        Assert.True(s.StepsAdvanced > s.TicksProcessed, $"스텝 {s.StepsAdvanced} · 틱 {s.TicksProcessed}");
        Assert.True(m.Link.CommandsFlushed > 0);
        Assert.Equal(0, m.Tick.Overruns);

        // 아무도 플랜을 잃지 않았다 — 전원이 스텝을 가진 플랜을 들고 있다.
        int planless = 0;

        for (int npc = 0; npc < host.Npcs; npc++)
        {
            NpcTrace trace = host.Trace(npc);

            if (!trace.Found || trace.Steps.Length == 0)
            {
                planless++;
            }
        }

        Assert.Equal(0, planless);

        output.WriteLine(
            $"10. 거절 {m.Replan.QueueDropped}건 · 스텝 전진 {s.StepsAdvanced} · "
            + $"명령 {m.Link.CommandsFlushed} · 플랜 없는 NPC {planless}/{host.Npcs}");
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────

    /// <summary>부하 CSV 한 줄 중 이 게이트가 보는 열만. 열 위치가 아니라 <b>헤더 이름</b>으로 읽는다.</summary>
    private readonly record struct LoadCsvRow(
        string Cell, string Tier, long T1Calls, double GpuUtilPercent, double GpuVramMb)
    {
        public static LoadCsvRow[] ReadAll(string path)
        {
            string[] lines = File.ReadAllLines(path);

            if (lines.Length < 2)
            {
                return [];
            }

            string[] header = lines[0].Split(',');
            var rows = new List<LoadCsvRow>(lines.Length - 1);

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                string[] f = lines[i].Split(',');

                rows.Add(new LoadCsvRow(
                    Text(header, f, "cell"),
                    Text(header, f, "tier"),
                    (long)Number(header, f, "t1_calls"),
                    Number(header, f, "gpu_util_percent"),
                    Number(header, f, "gpu_vram_mb")));
            }

            return [.. rows];
        }

        private static string Text(string[] header, string[] fields, string column)
        {
            int at = Array.IndexOf(header, column);

            Assert.True(at >= 0, $"부하 CSV 에 '{column}' 열이 없다.");

            return fields[at];
        }

        private static double Number(string[] header, string[] fields, string column) =>
            double.Parse(Text(header, fields, column), CultureInfo.InvariantCulture);
    }

    /// <summary>언제나 요청한 티어를 주는 예산. 페일오버만 보고 싶을 때 쓴다.</summary>
    private sealed class AlwaysGrant : IReplanBudget
    {
        public Tier Acquire(Tier requested, int estimatedTokens, Tick now) => requested;

        public bool TryAcquire(Tier tier, int estimatedTokens, Tick now) => tier != Tier.None;

        public bool Peek(Tier tier, int estimatedTokens, Tick now) => tier != Tier.None;

        public double EstimateCost(Tier tier, int tokens) => 0;

        public void Settle(Tier tier, int actualTokens, double actualCostUsd)
        {
        }
    }

    /// <summary>호출 수를 세는 컴파일러. <paramref name="throws"/> 면 매번 죽는다.</summary>
    private sealed class CountingCompiler(string model, bool throws = false) : IPlanCompiler
    {
        public int Calls { get; private set; }

        public ValueTask<PlanCompileResult> CompileAsync(
            PlanRequest request, CancellationToken cancellationToken)
        {
            Calls++;

            if (throws)
            {
                throw new HttpRequestException($"{model} 가 죽었다");
            }

            return ValueTask.FromResult(new PlanCompileResult(
                null,
                ValidationResult.Ok,
                CompileStats.None with { Model = model },
                "{}"));
        }
    }
}
