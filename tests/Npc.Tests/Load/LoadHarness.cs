using System.Diagnostics;
using System.Globalization;
using System.Text;
using Npc.Host;
using Npc.Host.Metrics;
using Npc.Host.Replan;
using Npc.Llm;
using Npc.Planning;

namespace Npc.Tests.Load;

/// <summary>
/// 부하 매트릭스의 한 셀. docs/14 §6 측정 매트릭스.
/// </summary>
/// <param name="Npcs">NPC 수.</param>
/// <param name="TimeScale">시간 배속.</param>
/// <param name="Tier">티어 구성.</param>
/// <param name="PlayerBots">가상 플레이어 수.</param>
/// <param name="Days">돌릴 게임 일수.</param>
/// <param name="T1Workers">T1 워커 수. T4-15 가 1/2/4 를 재서 기본값을 확정한다.</param>
/// <param name="MaxSpeed">
/// 페이싱을 끄는가. <b>틱 지연 매트릭스는 페이싱을 켜고 잰다</b> —
/// <c>--max-speed</c> 는 처리량 측정용이다 (<c>P1_gate.md §4</c>).
/// </param>
/// <param name="ScanCap">
/// 인지 스캔 틱당 상한. -1 이면 기본(150), <b>0 이면 상한 해제</b>.
/// 차수 판정은 상한을 푼 회차로만 의미가 있다 (docs/14 §6).
/// </param>
internal readonly record struct LoadCell(
    int Npcs,
    int TimeScale,
    TierMode Tier,
    int PlayerBots,
    int Days = 1,
    int T1Workers = 2,
    bool MaxSpeed = true,
    int ScanCap = -1)
{
    /// <summary>인지 스캔 상한이 풀린 셀인가.</summary>
    public bool ScanUncapped => ScanCap == 0;

    /// <summary>CSV 의 <c>cell</c> 열. 사람이 읽고 스크립트가 grep 한다.</summary>
    public string Id =>
        $"n{Npcs}-x{TimeScale}-{Tier.ToString().ToLowerInvariant()}-b{PlayerBots}-w{T1Workers}"
        + (MaxSpeed ? "-max" : "-paced")
        + (ScanUncapped ? "-uncapped" : string.Empty);

    /// <summary>이 셀을 재현하는 명령줄.</summary>
    public string[] Args() =>
    [
        "--loopback",
        "--npcs", Npcs.ToString(CultureInfo.InvariantCulture),
        "--time-scale", TimeScale.ToString(CultureInfo.InvariantCulture),
        "--days", Days.ToString(CultureInfo.InvariantCulture),
        "--player-bots", PlayerBots.ToString(CultureInfo.InvariantCulture),
        "--tier", Tier.ToString().ToLowerInvariant(),
        "--t1-workers", T1Workers.ToString(CultureInfo.InvariantCulture),
        .. ScanCap >= 0 ? new[] { "--scan-cap", ScanCap.ToString(CultureInfo.InvariantCulture) } : [],
        .. MaxSpeed ? new[] { "--max-speed" } : [],
        "--no-dashboard",
    ];
}

/// <summary>
/// 한 셀의 측정값. docs/14 §6 "기록 지표" 8분류를 그대로 담는다.
///
/// <b>필드 이름이 CSV 헤더다.</b> 순서를 바꾸면 지난 회차와 비교할 수 없다.
/// </summary>
internal sealed record LoadResult
{
    /// <summary>어느 셀인가.</summary>
    public required LoadCell Cell { get; init; }

    /// <summary>완주했는가. 타임아웃이면 false 이고 그 행의 나머지는 참고값이다.</summary>
    public required bool Completed { get; init; }

    /// <summary>
    /// <b>실제로 돌린 NPC 수.</b> <c>npc_instances.json</c> 에 있는 수를 넘길 수 없으므로
    /// (<c>NpcHost.Create</c> 가 <c>Math.Min</c> 한다) 요청값과 다를 수 있다.
    ///
    /// <b>차수 판정은 이 값으로 한다.</b> 요청값으로 하면 존재하지 않는 규모를 근거로 삼는다.
    /// </summary>
    public required int NpcsActual { get; init; }

    /// <summary>벽시계 소요(초). 처리량 판정의 분모다.</summary>
    public required double WallClockSeconds { get; init; }

    // ── 1. 틱 ──
    public required long Ticks { get; init; }

    public required double TickP50Ms { get; init; }

    public required double TickP95Ms { get; init; }

    public required double TickP99Ms { get; init; }

    public required double TickMaxMs { get; init; }

    public required long TickOverruns { get; init; }

    // ── 2. 인지 ──
    public required int ScanPerTick { get; init; }

    public required long BandMigrations { get; init; }

    // ── 3. 큐 ──
    public required int QueueDepthP50 { get; init; }

    public required int QueueDepthP99 { get; init; }

    public required long QueueDropped { get; init; }

    public required double QueueAvgWaitTicks { get; init; }

    // ── 4. 캐시 ──
    public required double CacheHitRate { get; init; }

    public required int ColdBuckets { get; init; }

    public required double IndividualTurnover { get; init; }

    // ── 5. LLM ──
    public required long T1Calls { get; init; }

    public required long T2Calls { get; init; }

    public required double LlmAvgLatencyTicks { get; init; }

    public required long LlmFailures { get; init; }

    public required long PromptTokens { get; init; }

    public required double CostUsd { get; init; }

    public required double PromptCacheHitRate { get; init; }

    // ── 6. 링크 ──
    public required double CommandsPerSecond { get; init; }

    public required long CommandsDropped { get; init; }

    public required double EventsPerSecond { get; init; }

    public required long SequenceGaps { get; init; }

    // ── 7. GC ──
    public required int Gen0 { get; init; }

    public required int Gen1 { get; init; }

    public required int Gen2 { get; init; }

    public required long BytesPerTick { get; init; }

    public required double HeapMb { get; init; }

    // ── 8. GPU (nvidia-smi 폴링. 없으면 -1 = 미측정) ──
    public required double GpuUtilPercent { get; init; }

    public required double GpuVramMb { get; init; }
}

/// <summary>
/// 부하 테스트 하네스. docs/14 §6 · T4-15.
///
/// <b>매트릭스를 코드가 들고 있다.</b> <c>tools/run_load.ps1</c> 은 이 하네스를 부르는 껍데기다 —
/// 셀 목록이 스크립트에 있으면 지난 회차와 무엇이 달랐는지 알 수 없다.
///
/// <b>GPU 는 <c>nvidia-smi</c> 폴링이다.</b> 없는 기계에서는 -1(미측정)로 남긴다 —
/// 0 으로 적으면 "GPU 를 안 썼다"로 읽혀 게이트 항목(≤ 60%)이 거짓으로 통과한다.
/// </summary>
internal static class LoadHarness
{
    /// <summary>CSV 산출 경로 환경변수. 없으면 <c>docs/measurements/W10_load.csv</c>.</summary>
    public const string CsvPathVariable = "NPC_LOAD_CSV";

    /// <summary><c>full</c> 이면 전량 매트릭스(135셀)를 돈다. 그 밖이면 스모크다.</summary>
    public const string MatrixVariable = "NPC_LOAD_MATRIX";

    /// <summary>
    /// P1 게이트 실측 기준선 — NPC 5,000 · 1,440틱의 틱 p99(ms). <c>P1_gate.md</c>.
    /// <b>LLM 배선 후 이 값이 얼마나 나빠지는지가 P4 게이트의 실질이다</b> (docs/14 T4-24).
    /// </summary>
    public const double P1BaselineTickP99Ms = 2.375;

    /// <summary>P1 게이트 실측 기준선 — 틱당 인지 스캔. 상한(150)에 붙어 있었다.</summary>
    public const int P1BaselineScanPerTick = 150;

    /// <summary>P1 게이트 실측 기준선 — 틱 창 Gen0 컬렉션.</summary>
    public const int P1BaselineGen0 = 0;

    /// <summary>기준선과 비교할 셀 — 5,000 NPC · 페이싱 켜짐. 그 밖의 셀은 처리량 측정용이다.</summary>
    public static LoadCell BaselineCell => new(5_000, 600, TierMode.None, 20, MaxSpeed: false);

    /// <summary>docs/14 §6 의 NPC 축.</summary>
    public static readonly int[] NpcLevels = [500, 1_000, 2_500, 5_000, 10_000];

    /// <summary>docs/14 §6 의 배속 축.</summary>
    public static readonly int[] TimeScales = [1, 60, 600];

    /// <summary>docs/14 §6 의 티어 축.</summary>
    public static readonly TierMode[] Tiers = [TierMode.None, TierMode.T1, TierMode.All];

    /// <summary>docs/14 §6 의 플레이어 봇 축.</summary>
    public static readonly int[] PlayerBotLevels = [0, 20, 100];

    /// <summary>
    /// 전량 매트릭스. <b>5 × 3 × 3 × 3 = 135 셀</b> 이다.
    ///
    /// ⚠ 배속 1 은 게임 하루가 실시간 24시간이라 <c>--max-speed</c> 없이는 돌 수 없다.
    /// 여기서는 전 셀을 <c>--max-speed</c> 로 돌려 처리량을 재고,
    /// <b>틱 지연은 페이싱을 켠 별도 셀</b>(<see cref="PacedCells"/>)에서 잰다 (<c>P1_gate.md §4</c>).
    /// </summary>
    public static LoadCell[] FullMatrix()
    {
        var cells = new List<LoadCell>(
            NpcLevels.Length * TimeScales.Length * Tiers.Length * PlayerBotLevels.Length);

        foreach (int npcs in NpcLevels)
        {
            foreach (int scale in TimeScales)
            {
                foreach (TierMode tier in Tiers)
                {
                    foreach (int bots in PlayerBotLevels)
                    {
                        cells.Add(new LoadCell(npcs, scale, tier, bots));
                    }
                }
            }
        }

        return [.. cells];
    }

    /// <summary>
    /// 페이싱을 켠 셀. <b>틱 지연 매트릭스는 이쪽으로 잰다.</b>
    /// 배속 600 으로 게임 하루면 1,440틱 = 실시간 144초다.
    /// </summary>
    public static LoadCell[] PacedCells() =>
    [
        .. NpcLevels.Select(n => new LoadCell(n, 600, TierMode.None, 20, MaxSpeed: false)),
    ];

    /// <summary>
    /// 인지 스캔 상한을 푼 셀. <b>차수 판정(T4-16)은 이 셀들로만 한다.</b>
    ///
    /// 상한이 걸린 회차는 무엇을 넣어도 150 에서 잘려 O(1) 로 보인다 (docs/14 §6).
    /// <c>docs/11 §4</c> 의 "상한이 없으면 612 까지 간다" 추정과 대조하는 것이 이 회차의 목적이다.
    /// </summary>
    public static LoadCell[] UncappedCells() =>
    [
        .. NpcLevels.Select(n => new LoadCell(n, 600, TierMode.None, 20, ScanCap: 0)),
    ];

    /// <summary>
    /// 스모크 매트릭스. <b>기본값이다</b> — 전량은 20분을 넘으므로 사람이 명시적으로 켠다.
    /// 축마다 최소 2점을 남겨 스케일 판정(T4-16)이 성립한다.
    /// </summary>
    public static LoadCell[] SmokeMatrix() =>
    [
        new(500, 600, TierMode.None, 0),
        new(500, 600, TierMode.None, 20),
        new(1_000, 600, TierMode.None, 20),
        new(2_500, 600, TierMode.None, 20),
        new(5_000, 600, TierMode.None, 20),
        new(10_000, 600, TierMode.None, 20),
        new(5_000, 60, TierMode.None, 20),
        new(5_000, 600, TierMode.None, 100),

        // 틱 지연은 페이싱을 켜고 잰다 (P1_gate.md §4). 배속 600 · 게임 하루 = 1,440틱 = 실시간 144초.
        // P1 기준선(5,000 NPC p99 2.375ms)과 비교할 수 있는 셀은 이것뿐이다.
        new(5_000, 600, TierMode.None, 20, MaxSpeed: false),

        // 차수 판정용 — 상한을 풀어야 인지 스캔의 O(1) 여부가 보인다 (T4-16).
        .. UncappedCells(),
    ];

    /// <summary>환경변수가 고른 매트릭스.</summary>
    public static LoadCell[] SelectedMatrix() =>
        string.Equals(
            Environment.GetEnvironmentVariable(MatrixVariable), "full", StringComparison.OrdinalIgnoreCase)
            ? [.. FullMatrix(), .. PacedCells(), .. UncappedCells()]
            : SmokeMatrix();

    /// <summary>CSV 산출 경로.</summary>
    public static string CsvPath() =>
        Environment.GetEnvironmentVariable(CsvPathVariable) is { Length: > 0 } path
            ? path
            : TestPaths.At("docs", "measurements", "W10_load.csv");

    /// <summary>셀 하나를 돌린다.</summary>
    public static async Task<LoadResult> RunAsync(LoadCell cell, TimeSpan timeout, CancellationToken ct)
    {
        HostOptions options = Parse(cell.Args()) with { MasterData = TestPaths.MasterData };

        int gen0 = GC.CollectionCount(0);
        int gen1 = GC.CollectionCount(1);
        int gen2 = GC.CollectionCount(2);

        using var gpu = new GpuSampler();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

        deadline.CancelAfter(timeout);
        gpu.Start(deadline.Token);

        await using NpcHost host = NpcHost.Create(options, TextWriter.Null);

        long started = Stopwatch.GetTimestamp();

        await host.RunAsync(deadline.Token);

        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

        gpu.Stop();

        MetricsSnapshot m = host.Metrics.Snapshot();
        TierWiring tiers = host.Tiers;
        CompileStatsCollector? stats = tiers.Stats;

        return new LoadResult
        {
            Cell = cell,
            Completed = !deadline.IsCancellationRequested,
            NpcsActual = m.Npc.Total,
            WallClockSeconds = Math.Round(seconds, 3),

            Ticks = m.Tick.Ticks,
            TickP50Ms = m.Tick.P50Ms,
            TickP95Ms = Math.Round(host.Metrics.Percentile(0.95), 3),
            TickP99Ms = m.Tick.P99Ms,
            TickMaxMs = m.Tick.MaxMs,
            TickOverruns = m.Tick.Overruns,

            ScanPerTick = m.Replan.ScanPerTick,
            BandMigrations = m.Npc.BandMigrations,

            QueueDepthP50 = host.Metrics.QueueDepthPercentile(0.50),
            QueueDepthP99 = host.Metrics.QueueDepthPercentile(0.99),
            QueueDropped = m.Replan.QueueDropped,
            QueueAvgWaitTicks = Math.Round(AverageWait(tiers), 2),

            CacheHitRate = m.Cache.HitRate,
            ColdBuckets = m.Cache.ColdBuckets,
            IndividualTurnover = m.Cache.IndividualTurnover,

            T1Calls = tiers.Router?.T1Calls ?? 0,
            T2Calls = tiers.Router?.T2Calls ?? 0,
            LlmAvgLatencyTicks = Math.Round(AverageLatency(tiers), 2),
            LlmFailures = (stats?.CallFailures ?? 0) + (tiers.Router?.Rejected ?? 0),
            PromptTokens = stats?.PromptTokens ?? 0,
            CostUsd = Math.Round(stats?.CostUsd ?? 0, 6),
            PromptCacheHitRate = Math.Round(stats?.CacheHitRate ?? 0, 4),

            CommandsPerSecond = Rate(m.Link.CommandsFlushed, seconds),
            CommandsDropped = m.Link.CommandsDropped,
            EventsPerSecond = Rate(m.Link.EventsDrained, seconds),
            SequenceGaps = m.Link.EventGaps,

            Gen0 = GC.CollectionCount(0) - gen0,
            Gen1 = GC.CollectionCount(1) - gen1,
            Gen2 = GC.CollectionCount(2) - gen2,
            BytesPerTick = m.Tick.BytesPerTick,
            HeapMb = m.Tick.ManagedHeapMb,

            GpuUtilPercent = gpu.AverageUtilPercent,
            GpuVramMb = gpu.PeakVramMb,
        };
    }

    /// <summary>CSV 헤더. 열 순서는 <see cref="LoadResult"/> 선언 순서다.</summary>
    public static string CsvHeader() =>
        "cell,npcs,npcs_actual,scan_cap,time_scale,tier,player_bots,t1_workers,max_speed,completed,wall_clock_s,"
        + "ticks,tick_p50_ms,tick_p95_ms,tick_p99_ms,tick_max_ms,tick_overruns,"
        + "scan_per_tick,band_migrations,"
        + "queue_p50,queue_p99,queue_dropped,queue_avg_wait_ticks,"
        + "cache_hit_rate,cold_buckets,individual_turnover,"
        + "t1_calls,t2_calls,llm_avg_latency_ticks,llm_failures,prompt_tokens,cost_usd,prompt_cache_hit_rate,"
        + "commands_per_s,commands_dropped,events_per_s,sequence_gaps,"
        + "gen0,gen1,gen2,bytes_per_tick,heap_mb,"
        + "gpu_util_percent,gpu_vram_mb";

    /// <summary>한 줄.</summary>
    public static string CsvRow(LoadResult r)
    {
        ArgumentNullException.ThrowIfNull(r);

        var invariant = CultureInfo.InvariantCulture;

        return string.Join(
            ',',
            r.Cell.Id,
            r.Cell.Npcs.ToString(invariant),
            r.NpcsActual.ToString(invariant),
            r.Cell.ScanCap.ToString(invariant),
            r.Cell.TimeScale.ToString(invariant),
            r.Cell.Tier.ToString().ToLowerInvariant(),
            r.Cell.PlayerBots.ToString(invariant),
            r.Cell.T1Workers.ToString(invariant),
            r.Cell.MaxSpeed ? "1" : "0",
            r.Completed ? "1" : "0",
            r.WallClockSeconds.ToString("F3", invariant),
            r.Ticks.ToString(invariant),
            r.TickP50Ms.ToString("F3", invariant),
            r.TickP95Ms.ToString("F3", invariant),
            r.TickP99Ms.ToString("F3", invariant),
            r.TickMaxMs.ToString("F3", invariant),
            r.TickOverruns.ToString(invariant),
            r.ScanPerTick.ToString(invariant),
            r.BandMigrations.ToString(invariant),
            r.QueueDepthP50.ToString(invariant),
            r.QueueDepthP99.ToString(invariant),
            r.QueueDropped.ToString(invariant),
            r.QueueAvgWaitTicks.ToString("F2", invariant),
            r.CacheHitRate.ToString("F4", invariant),
            r.ColdBuckets.ToString(invariant),
            r.IndividualTurnover.ToString("F4", invariant),
            r.T1Calls.ToString(invariant),
            r.T2Calls.ToString(invariant),
            r.LlmAvgLatencyTicks.ToString("F2", invariant),
            r.LlmFailures.ToString(invariant),
            r.PromptTokens.ToString(invariant),
            r.CostUsd.ToString("F6", invariant),
            r.PromptCacheHitRate.ToString("F4", invariant),
            r.CommandsPerSecond.ToString("F1", invariant),
            r.CommandsDropped.ToString(invariant),
            r.EventsPerSecond.ToString("F1", invariant),
            r.SequenceGaps.ToString(invariant),
            r.Gen0.ToString(invariant),
            r.Gen1.ToString(invariant),
            r.Gen2.ToString(invariant),
            r.BytesPerTick.ToString(invariant),
            r.HeapMb.ToString("F1", invariant),
            r.GpuUtilPercent.ToString("F1", invariant),
            r.GpuVramMb.ToString("F1", invariant));
    }

    /// <summary>CSV 를 쓴다. 디렉터리가 없으면 만든다.</summary>
    public static void WriteCsv(string path, IEnumerable<LoadResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var text = new StringBuilder();
        text.AppendLine(CsvHeader());

        foreach (LoadResult result in results)
        {
            text.AppendLine(CsvRow(result));
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static HostOptions Parse(string[] args)
    {
        if (!HostOptions.TryParse(args, out HostOptions options, out string? error))
        {
            throw new InvalidOperationException($"부하 셀 인자를 파싱하지 못했다: {error}");
        }

        return options;
    }

    private static double Rate(long count, double seconds) =>
        seconds <= 0 ? 0 : Math.Round(count / seconds, 1);

    private static double AverageWait(TierWiring tiers)
    {
        double sum = 0;
        int counted = 0;

        foreach (ReplanWorker worker in tiers.Workers)
        {
            if (worker.AverageWaitTicks > 0)
            {
                sum += worker.AverageWaitTicks;
                counted++;
            }
        }

        return counted == 0 ? 0 : sum / counted;
    }

    private static double AverageLatency(TierWiring tiers)
    {
        double sum = 0;
        int counted = 0;

        foreach (ReplanWorker worker in tiers.Workers)
        {
            if (worker.Taken > 0)
            {
                sum += worker.AverageLatencyTicks;
                counted++;
            }
        }

        return counted == 0 ? 0 : sum / counted;
    }
}

/// <summary>
/// <c>nvidia-smi</c> 폴링. docs/14 §6 "GPU: 사용률, VRAM".
///
/// <b>없으면 -1(미측정)이다.</b> 0 으로 적으면 "GPU 를 안 썼다"로 읽혀
/// 게이트 항목(GPU ≤ 60%)이 거짓으로 통과한다.
/// </summary>
internal sealed class GpuSampler : IDisposable
{
    private const int IntervalMs = 500;

    private readonly List<double> _util = [];
    private readonly List<double> _vram = [];
    private CancellationTokenSource? _own;
    private Task? _loop;

    /// <summary>평균 사용률(%). 표본이 없으면 -1.</summary>
    public double AverageUtilPercent
    {
        get
        {
            lock (_util)
            {
                return _util.Count == 0 ? -1 : _util.Average();
            }
        }
    }

    /// <summary>최대 VRAM 사용(MB). 표본이 없으면 -1.</summary>
    public double PeakVramMb
    {
        get
        {
            lock (_vram)
            {
                return _vram.Count == 0 ? -1 : _vram.Max();
            }
        }
    }

    /// <summary>폴링을 시작한다. <c>nvidia-smi</c> 가 없으면 조용히 아무것도 하지 않는다.</summary>
    public void Start(CancellationToken ct)
    {
        _own = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_own.Token), CancellationToken.None);
    }

    /// <summary>폴링을 멈춘다.</summary>
    public void Stop()
    {
        _own?.Cancel();

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 취소는 정상 종료다.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        _own?.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!TrySample())
            {
                return;   // nvidia-smi 가 없다. 다시 시도하지 않는다
            }

            try
            {
                await Task.Delay(IntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool TrySample()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=utilization.gpu,memory.used --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3_000);

            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split(',', StringSplitOptions.TrimEntries);

                if (parts.Length < 2
                    || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out double util)
                    || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out double vram))
                {
                    continue;
                }

                lock (_util)
                {
                    _util.Add(util);
                }

                lock (_vram)
                {
                    _vram.Add(vram);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;   // nvidia-smi 가 PATH 에 없다
        }
    }
}
