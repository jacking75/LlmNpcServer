using System.Text.Json;
using Npc.Host;
using Npc.Host.Metrics;

namespace Npc.Tests.Host;

/// <summary>docs/11 §9 · §10. /metrics 가 5패널에 필요한 지표를 전부 낸다.</summary>
public sealed class NpcMeterTests
{
    private static HostOptions Options(params string[] args)
    {
        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return options with { MasterData = TestPaths.MasterData };
    }

    private static async Task<MetricsSnapshot> RunAsync(params string[] args)
    {
        await using NpcHost host = NpcHost.Create(Options(args), TextWriter.Null);

        await host.RunAsync(CancellationToken.None);

        return host.Metrics.Snapshot();
    }

    /// <summary>T1-58 완료 조건 — 5패널이 필요로 하는 지표가 전부 있다.</summary>
    [Fact]
    public async Task Metrics_HaveEveryPanelField()
    {
        MetricsSnapshot m = await RunAsync(
            "--loopback", "--npcs", "100", "--time-scale", "600", "--days", "1",
            "--max-speed", "--no-dashboard");

        // 틱 패널 — p50/p99, 오버런, 게임 시각
        Assert.True(m.Tick.Ticks > 0);
        Assert.True(m.Tick.P50Ms >= 0);
        Assert.True(m.Tick.P99Ms >= m.Tick.P50Ms);
        Assert.True(m.Tick.MaxMs >= m.Tick.P99Ms);
        Assert.InRange(m.Tick.GameHour, 0, 23);
        Assert.False(string.IsNullOrEmpty(m.Tick.TimeOfDay));
        Assert.True(m.Tick.ManagedHeapMb > 0);

        // NPC 패널 — 총원, LOD 분포, 상태 분포
        Assert.Equal(100, m.Npc.Total);
        Assert.Equal(100, m.Npc.ByLod.Sum());
        Assert.Equal(100, m.Npc.ByStepStatus.Sum());

        // 액션 패널 — Top 10
        Assert.NotEmpty(m.Actions);
        Assert.True(m.Actions.Length <= 10);
        Assert.All(m.Actions, a => Assert.True(a.Count > 0));

        // 개체 수 내림차순이어야 대시보드가 안 깜빡인다.
        for (int i = 1; i < m.Actions.Length; i++)
        {
            Assert.True(m.Actions[i - 1].Count >= m.Actions[i].Count);
        }

        // 링크 패널 — 송출/드롭/수신/시퀀스 갭
        Assert.True(m.Link.CommandsEnqueued > 0);
        Assert.True(m.Link.CommandsFlushed > 0);
        Assert.True(m.Link.EventsDrained > 0);
        Assert.Equal(0, m.Link.EventGaps);
        Assert.Equal(0, m.Link.EventBacklogs);

        // 재계획 패널
        Assert.True(m.Replan.QueueDepth >= 0);
        Assert.True(m.Replan.EnqueuedPerSecond >= 0);
        Assert.True(m.Replan.ScanPerTick >= 0);

        // P1 에는 LLM 이 없다.
        Assert.Equal(0, m.LlmCalls);
    }

    /// <summary>대시보드는 이 JSON 을 폴링한다. 필드 이름이 바뀌면 패널이 빈다.</summary>
    [Fact]
    public async Task Metrics_SerializeEveryPanelToJson()
    {
        MetricsSnapshot m = await RunAsync(
            "--loopback", "--npcs", "50", "--time-scale", "600", "--days", "1",
            "--max-speed", "--no-dashboard");

        // ASP.NET 의 기본 JSON 옵션(camelCase)으로 나간다. 대시보드가 읽는 이름 그대로 확인한다.
        string json = JsonSerializer.Serialize(m, JsonSerializerOptions.Web);

        foreach (string key in new[]
        {
            "\"tick\"", "\"p50Ms\"", "\"p99Ms\"", "\"overruns\"", "\"gameHour\"", "\"timeOfDay\"",
            "\"gen0Collections\"", "\"bytesPerTick\"", "\"managedHeapMb\"",
            "\"npc\"", "\"total\"", "\"byLod\"", "\"byStepStatus\"", "\"bandMigrations\"",
            "\"actions\"", "\"action\"", "\"count\"",
            "\"link\"", "\"commandsFlushed\"", "\"commandsDropped\"",
            "\"eventsDrained\"", "\"eventGaps\"", "\"eventBacklogs\"",
            "\"replan\"", "\"queueDepth\"", "\"enqueuedPerSecond\"", "\"deviations\"",
            "\"scanPerTick\"", "\"interruptsForced\"",
            // 캐시 패널 — docs/13 §6 의 4개 지표 + 아키타입별 분해
            "\"cache\"", "\"hitRate\"", "\"coldBuckets\"", "\"topMisses\"", "\"individualTurnover\"",
            "\"filledBuckets\"", "\"pinnedBuckets\"", "\"worstArchetypes\"",
            "\"llmCalls\"",
        })
        {
            Assert.Contains(key, json, StringComparison.Ordinal);
        }

        // N4 — 어디에도 벽시계 문자열이 없다.
        Assert.DoesNotContain("DateTime", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// T3-04 완료 조건 — <c>/metrics</c> 에 docs/13 §6 의 4개 지표가 있고 아키타입별로 분해된다.
    ///
    /// P1 경로(<c>--no-llm</c>·프리베이크 없음)에서는 2,880 버킷이 전부 비어 있으므로
    /// 히트율 0 · 콜드 2,880 이 <b>정상</b>이다. 채워진 뒤의 값은 T3-21 게이트가 본다.
    /// </summary>
    [Fact]
    public async Task Metrics_HaveCachePanel()
    {
        MetricsSnapshot m = await RunAsync(
            "--loopback", "--npcs", "200", "--time-scale", "600", "--days", "1",
            "--max-speed", "--no-dashboard");

        // (1) 히트율 (2) 콜드 버킷 수 (3) 미스 상위 버킷 (4) 개별 풀 회전율
        Assert.InRange(m.Cache.HitRate, 0, 1);
        Assert.Equal(2_880, m.Cache.ColdBuckets + m.Cache.FilledBuckets);
        Assert.NotNull(m.Cache.TopMisses);
        Assert.True(m.Cache.TopMisses.Length <= 10);
        Assert.Equal(0, m.Cache.IndividualTurnover);   // 개별 풀은 P4 에서 결선한다

        // 프리베이크 전이므로 전 버킷이 비어 있고 조회는 전부 폴백으로 해소된다.
        Assert.Equal(2_880, m.Cache.ColdBuckets);
        Assert.Equal(0, m.Cache.PinnedBuckets);
        Assert.Equal(0, m.Cache.Hits);
        Assert.True(m.Cache.Misses > 0, "버킷 전환이 한 번도 안 일어났다 — 미스조차 세지 못했다.");

        // 아키타입별 분해가 된다. 조회가 있었던 아키타입만 올라온다.
        Assert.NotEmpty(m.Cache.WorstArchetypes);
        Assert.All(m.Cache.WorstArchetypes, row =>
        {
            Assert.False(string.IsNullOrEmpty(row.Archetype));
            Assert.InRange(row.HitRate, 0, 1);
            Assert.True(row.Hits + row.Misses > 0);
            Assert.InRange(row.ColdBuckets, 0, 72);
        });

        // 미스 상위는 내림차순이어야 대시보드가 안 깜빡인다.
        for (int i = 1; i < m.Cache.TopMisses.Length; i++)
        {
            Assert.True(m.Cache.TopMisses[i - 1].Misses >= m.Cache.TopMisses[i].Misses);
        }

        // 버킷 표기는 blacksmith@Dawn.Peace.Fair 형식이다 (docs/03 §7).
        Assert.All(m.Cache.TopMisses, row => Assert.Contains("@", row.Bucket, StringComparison.Ordinal));
    }

    /// <summary>docs/11 §9 — 인지 스캔 대상은 틱당 150 이하여야 한다.</summary>
    [Fact]
    public async Task Metrics_ScanStaysUnderBudget()
    {
        MetricsSnapshot m = await RunAsync(
            "--loopback", "--npcs", "1000", "--time-scale", "600", "--days", "1",
            "--max-speed", "--no-dashboard");

        Assert.InRange(m.Replan.ScanPerTick, 0, 150);
    }
}
