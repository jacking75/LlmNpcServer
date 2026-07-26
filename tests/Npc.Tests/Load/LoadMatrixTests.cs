using Npc.Host;
using Npc.Tests.Runtime;

namespace Npc.Tests.Load;

/// <summary>
/// docs/14 §6 부하 매트릭스 · T4-15.
///
/// <b>기본은 스모크다.</b> 전량 135셀은 20분을 넘으므로
/// <c>NPC_LOAD_MATRIX=full</c> 로 사람이 명시적으로 켠다 (<c>tools/run_load.ps1 -Full</c>).
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class LoadMatrixTests
{
    /// <summary>셀 하나의 상한. 넘으면 그 행은 <c>completed=0</c> 으로 남는다.</summary>
    private static readonly TimeSpan CellTimeout = TimeSpan.FromMinutes(5);

    /// <summary>매트릭스 정의가 docs/14 §6 표와 같은가. LLM 도 호스트도 안 띄운다.</summary>
    [Fact]
    public void Load_MatrixMatchesSpecTable()
    {
        Assert.Equal([500, 1_000, 2_500, 5_000, 10_000], LoadHarness.NpcLevels);
        Assert.Equal([1, 60, 600], LoadHarness.TimeScales);
        Assert.Equal([TierMode.None, TierMode.T1, TierMode.All], LoadHarness.Tiers);
        Assert.Equal([0, 20, 100], LoadHarness.PlayerBotLevels);

        // 5 × 3 × 3 × 3 = 135
        LoadCell[] full = LoadHarness.FullMatrix();
        Assert.Equal(135, full.Length);
        Assert.Equal(135, full.Distinct().Count());

        // 티어 축이 실제로 갈린다 — P1 에는 --no-llm 하나뿐이라 이게 불가능했다.
        Assert.Contains(full, c => c.Tier == TierMode.None);
        Assert.Contains(full, c => c.Tier == TierMode.T1);
        Assert.Contains(full, c => c.Tier == TierMode.All);

        // 틱 지연용 셀은 페이싱을 켠다 (P1_gate.md §4).
        Assert.All(LoadHarness.PacedCells(), c => Assert.False(c.MaxSpeed));
        Assert.All(full, c => Assert.True(c.MaxSpeed));

        // 셀 id 가 유일해야 CSV 를 grep 할 수 있다.
        string[] ids = [.. full.Concat(LoadHarness.PacedCells()).Select(c => c.Id)];
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    /// <summary>모든 셀의 인자가 파싱된다 — 매트릭스를 돌리다 중간에 죽으면 안 된다.</summary>
    [Fact]
    public void Load_EveryCellParses()
    {
        foreach (LoadCell cell in LoadHarness.FullMatrix().Concat(LoadHarness.PacedCells()))
        {
            Assert.True(
                HostOptions.TryParse(cell.Args(), out HostOptions options, out string? error),
                $"{cell.Id}: {error}");

            Assert.Equal(cell.Npcs, options.Npcs);
            Assert.Equal(cell.TimeScale, options.TimeScale);
            Assert.Equal(cell.Tier, options.Tier);
            Assert.Equal(cell.PlayerBots, options.PlayerBots);
            Assert.Equal(cell.MaxSpeed, options.MaxSpeed);
            Assert.True(options.NoDashboard);
        }
    }

    /// <summary>CSV 헤더의 열 수와 한 줄의 열 수가 같다. 어긋나면 리포트(T4-16)가 통째로 틀린다.</summary>
    [Fact]
    public void Load_CsvColumnsLineUp()
    {
        LoadResult sample = Sample();

        int header = LoadHarness.CsvHeader().Split(',').Length;
        int row = LoadHarness.CsvRow(sample).Split(',').Length;

        Assert.Equal(header, row);

        // docs/14 §6 의 8분류가 헤더에 다 있다.
        string headerText = LoadHarness.CsvHeader();

        foreach (string needle in new[]
        {
            "tick_p99_ms", "tick_overruns",           // 1. 틱
            "scan_per_tick", "band_migrations",        // 2. 인지
            "queue_p99", "queue_dropped", "queue_avg_wait_ticks",   // 3. 큐
            "cache_hit_rate", "cold_buckets", "individual_turnover", // 4. 캐시
            "t1_calls", "t2_calls", "cost_usd", "prompt_cache_hit_rate", // 5. LLM
            "commands_per_s", "events_per_s", "sequence_gaps",       // 6. 링크
            "gen0", "bytes_per_tick", "heap_mb",                     // 7. GC
            "gpu_util_percent", "gpu_vram_mb",                       // 8. GPU
        })
        {
            Assert.Contains(needle, headerText, StringComparison.Ordinal);
        }
    }

    /// <summary>CSV 를 쓰고 다시 읽는다.</summary>
    [Fact]
    public void Load_WritesCsv()
    {
        string path = Path.Combine(Path.GetTempPath(), $"npc-load-{Guid.NewGuid():N}.csv");

        try
        {
            LoadHarness.WriteCsv(path, [Sample()]);

            string[] lines = File.ReadAllLines(path);

            Assert.Equal(2, lines.Length);
            Assert.Equal(LoadHarness.CsvHeader(), lines[0]);
            Assert.StartsWith("n500-x600-none-b20-w2-max,", lines[1], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// T4-15 완료 조건 — 매트릭스 1회 완주 · <c>docs/measurements/W10_load.csv</c> 산출.
    ///
    /// 기본은 스모크 8셀이다. <c>NPC_LOAD_MATRIX=full</c> 이면 135 + 5셀을 돈다.
    /// </summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Load_RunsSelectedMatrix()
    {
        LoadCell[] cells = LoadHarness.SelectedMatrix();
        var results = new List<LoadResult>(cells.Length);

        Assert.NotEmpty(cells);

        foreach (LoadCell cell in cells)
        {
            results.Add(await LoadHarness.RunAsync(cell, CellTimeout, CancellationToken.None));
        }

        LoadHarness.WriteCsv(LoadHarness.CsvPath(), results);

        Assert.All(results, r => Assert.True(r.Completed, $"{r.Cell.Id} 가 완주하지 못했다."));
        Assert.All(results, r => Assert.True(r.Ticks > 0, $"{r.Cell.Id} 가 한 틱도 못 돌았다."));

        // 티어 none 셀은 LLM 호출이 0 이어야 한다.
        foreach (LoadResult r in results.Where(r => r.Cell.Tier == TierMode.None))
        {
            Assert.Equal(0, r.T1Calls + r.T2Calls);
            Assert.Equal(0, r.CostUsd);
        }

        // P1 기준선(5,000 NPC p99 2.375ms)과 나란히 놓을 셀이 있어야 한다.
        // 그 셀은 페이싱을 켠 것이어야 한다 — max-speed 로 잰 값은 기준선과 비교할 수 없다.
        LoadResult baseline = Assert.Single(results, r => r.Cell == LoadHarness.BaselineCell);

        Assert.Equal(LoadHarness.P1BaselineScanPerTick, baseline.ScanPerTick);
        Assert.True(
            baseline.TickP99Ms <= 20,
            $"기준선 셀 p99 {baseline.TickP99Ms}ms — 예산 20ms 를 넘는다 "
            + $"(P1 실측 {LoadHarness.P1BaselineTickP99Ms}ms).");
        // ⚠ 페이싱 셀은 bytes_per_tick 이 정확히 0 이 아니다 (실측 4 = 총 ~5.7KB).
        //    페이싱을 켜면 틱 루프가 await 마다 다른 스레드 풀 스레드로 옮겨 다니고,
        //    GC.GetAllocatedBytesForCurrentThread 는 스레드별이라 새 스레드 최초 진입 비용이 섞인다.
        //    max-speed 셀은 같은 틱 수에서 0 이다. P1 이 Runtime_NoGen0GcInTickLoop 를 따로 둔 이유가
        //    이것이고(P1 게이트 주석), 틱 루프 자체의 할당 0 은 그 테스트가 본다.
        Assert.True(
            baseline.BytesPerTick <= 8,
            $"기준선 셀 bytes/tick {baseline.BytesPerTick} — 스레드 이동 1회 비용을 넘는다.");

        Assert.All(
            results.Where(r => r.Cell.MaxSpeed),
            r => Assert.Equal(0, r.BytesPerTick));
    }

    private static LoadResult Sample() => new()
    {
        Cell = new LoadCell(500, 600, TierMode.None, 20),
        Completed = true,
        WallClockSeconds = 1.5,
        Ticks = 1_440,
        TickP50Ms = 0.1,
        TickP95Ms = 0.2,
        TickP99Ms = 0.3,
        TickMaxMs = 1.0,
        TickOverruns = 0,
        ScanPerTick = 42,
        BandMigrations = 100,
        QueueDepthP50 = 0,
        QueueDepthP99 = 3,
        QueueDropped = 0,
        QueueAvgWaitTicks = 0,
        CacheHitRate = 0.98,
        ColdBuckets = 12,
        IndividualTurnover = 0,
        T1Calls = 0,
        T2Calls = 0,
        LlmAvgLatencyTicks = 0,
        LlmFailures = 0,
        PromptTokens = 0,
        CostUsd = 0,
        PromptCacheHitRate = 0,
        CommandsPerSecond = 1_234.5,
        CommandsDropped = 0,
        EventsPerSecond = 2_345.6,
        SequenceGaps = 0,
        Gen0 = 1,
        Gen1 = 0,
        Gen2 = 0,
        BytesPerTick = 0,
        HeapMb = 12.3,
        GpuUtilPercent = -1,
        GpuVramMb = -1,
    };
}
