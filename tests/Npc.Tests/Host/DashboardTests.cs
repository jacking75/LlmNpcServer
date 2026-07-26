using System.Text.Json;
using System.Text.RegularExpressions;
using Npc.Host;
using Npc.Host.Metrics;

namespace Npc.Tests.Host;

/// <summary>docs/11 §10. 대시보드 1차 — 단일 HTML, 5패널, /metrics 폴링.</summary>
public sealed class DashboardTests
{
    private static readonly string s_html =
        File.ReadAllText(TestPaths.At("src", "Npc.Host", "wwwroot", "dashboard.html"));

    /// <summary>T1-59 완료 조건 — 5패널이 전부 있고 /metrics 를 폴링한다.</summary>
    [Fact]
    public void Dashboard_HasFivePanelsAndPollsMetrics()
    {
        foreach (string panel in new[] { "p-tick", "p-npc", "p-action", "p-link", "p-replan" })
        {
            Assert.Contains($"id=\"{panel}\"", s_html, StringComparison.Ordinal);
        }

        Assert.Contains("fetch(\"/metrics\"", s_html, StringComparison.Ordinal);
        Assert.Contains("setInterval(poll", s_html, StringComparison.Ordinal);
    }

    /// <summary>
    /// T4-18 완료 조건 — 캐시 패널의 3요소(히트율 시계열 · 콜드 버킷 수 · 아키타입별 히트율).
    /// </summary>
    [Fact]
    public void Dashboard_HasCachePanel()
    {
        Assert.Contains("id=\"p-cache\"", s_html, StringComparison.Ordinal);
        Assert.Contains("id=\"p-cache-spark\"", s_html, StringComparison.Ordinal);
        Assert.Contains("id=\"p-cache-archetype\"", s_html, StringComparison.Ordinal);

        // 히트율은 하한 지표다 — grade(넘으면 나쁨)가 아니라 gradeAtLeast 를 써야 한다.
        Assert.Contains("gradeAtLeast(c.hitRate", s_html, StringComparison.Ordinal);
        Assert.Contains("CACHE_HIT_FLOOR = 0.98", s_html, StringComparison.Ordinal);

        // 시계열은 클라이언트가 창을 들고 있는다. 서버는 스냅샷만 준다.
        Assert.Contains("SPARK_LEN", s_html, StringComparison.Ordinal);
        Assert.Contains("spark(\"p-cache-spark\"", s_html, StringComparison.Ordinal);
    }

    /// <summary>
    /// T4-19 완료 조건 — 재계획 패널의 4요소
    /// (큐 깊이 · 티어별 처리율 · 점수 분포 히스토그램 · 거절 수).
    /// </summary>
    [Fact]
    public void Dashboard_HasReplanPanelWithFourElements()
    {
        Assert.Contains("id=\"p-replan\"", s_html, StringComparison.Ordinal);
        Assert.Contains("id=\"p-replan-tier\"", s_html, StringComparison.Ordinal);
        Assert.Contains("id=\"p-replan-histogram\"", s_html, StringComparison.Ordinal);

        Assert.Contains("r.queueDepth", s_html, StringComparison.Ordinal);
        Assert.Contains("r.queueDropped", s_html, StringComparison.Ordinal);
        Assert.Contains("renderTiers(r.tiers)", s_html, StringComparison.Ordinal);
        Assert.Contains("renderHistogram(r.scoreHistogram", s_html, StringComparison.Ordinal);

        // 티어가 꺼져 있으면 0 행을 그리지 않는다 — "돌고 있는데 처리량 0" 으로 읽힌다.
        Assert.Contains("티어 꺼짐", s_html, StringComparison.Ordinal);
    }

    /// <summary>
    /// 외부 의존이 없어야 한다. 이 서버는 인터넷이 없는 사내망에서도 뜬다 —
    /// CDN 스크립트 하나가 섞이면 거기서는 빈 화면이 나온다.
    /// </summary>
    [Fact]
    public void Dashboard_IsSelfContained()
    {
        Assert.DoesNotMatch(new Regex(@"https?://", RegexOptions.IgnoreCase), s_html);
        Assert.DoesNotContain("<script src=", s_html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link rel=\"stylesheet\"", s_html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Program.cs 가 /dashboard 를 매핑한다.</summary>
    [Fact]
    public void Dashboard_IsMappedByHost()
    {
        string program = File.ReadAllText(TestPaths.At("src", "Npc.Host", "Program.cs"));

        Assert.Contains("MapGet(\"/dashboard\"", program, StringComparison.Ordinal);
        Assert.Contains("dashboard.html", program, StringComparison.Ordinal);
        Assert.Contains("MapGet(\"/metrics\"", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// HTML 이 읽는 필드가 /metrics JSON 에 실제로 있어야 한다.
    /// 필드 이름을 바꾸면 패널이 조용히 빈 채로 남는다 — 그걸 여기서 잡는다.
    /// </summary>
    [Fact]
    public async Task Dashboard_ReadsOnlyFieldsThatExist()
    {
        Assert.True(
            HostOptions.TryParse(
                ["--loopback", "--npcs", "40", "--time-scale", "600", "--days", "1", "--max-speed", "--no-dashboard"],
                out HostOptions options,
                out string? error),
            error);

        await using NpcHost host = NpcHost.Create(options with { MasterData = TestPaths.MasterData }, TextWriter.Null);
        await host.RunAsync(CancellationToken.None);

        using JsonDocument metrics = JsonDocument.Parse(
            JsonSerializer.Serialize(host.Metrics.Snapshot(), JsonSerializerOptions.Web));

        // HTML 이 쓰는 접근자 — 패널 객체 이름은 위에서 t/n/l/r 로 받아 쓴다.
        (string Panel, string[] Fields)[] used =
        [
            ("tick", ["p50Ms", "p99Ms", "maxMs", "overruns", "ticks", "gameDay", "gameHour",
                      "timeOfDay", "gen0Collections", "bytesPerTick", "managedHeapMb"]),
            ("npc", ["total", "byLod", "byStepStatus", "bandMigrations"]),
            ("link", ["commandsEnqueued", "commandsFlushed", "commandsDropped", "pendingCommands",
                      "eventsDrained", "eventGaps", "eventBacklogs"]),
            ("replan", ["queueDepth", "enqueuedPerSecond", "scanPerTick", "deviations",
                        "interruptsForced", "interruptsSuppressed", "queueDropped", "queueDepthP99",
                        "urgentCount", "urgentDropped", "scoreHistogram", "tiers", "staleDiscarded"]),
            ("cache", ["hitRate", "hits", "misses", "filledBuckets", "coldBuckets", "pinnedBuckets",
                       "individualTurnover", "individualLive", "worstArchetypes"]),
        ];

        foreach ((string panel, string[] fields) in used)
        {
            Assert.True(metrics.RootElement.TryGetProperty(panel, out JsonElement node), panel);

            foreach (string field in fields)
            {
                Assert.True(node.TryGetProperty(field, out _), $"{panel}.{field}");
                Assert.Contains(field, s_html, StringComparison.Ordinal);
            }
        }

        Assert.True(metrics.RootElement.TryGetProperty("actions", out JsonElement actions));
        Assert.Equal(JsonValueKind.Array, actions.ValueKind);
        Assert.True(metrics.RootElement.TryGetProperty("llmCalls", out _));
    }

    /// <summary>LOD 밴드 이름표가 실제 밴드 수와 맞아야 한다.</summary>
    [Fact]
    public void Dashboard_LabelsEveryLodBand()
    {
        Match match = Regex.Match(s_html, @"LOD_NAMES\s*=\s*\[(?<body>[^\]]*)\]");

        Assert.True(match.Success);
        Assert.Equal(
            Npc.Runtime.LodBandSet.BandCount,
            match.Groups["body"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
