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
            "\"llmCalls\"",
        })
        {
            Assert.Contains(key, json, StringComparison.Ordinal);
        }

        // N4 — 어디에도 벽시계 문자열이 없다.
        Assert.DoesNotContain("DateTime", json, StringComparison.Ordinal);
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
