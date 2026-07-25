using System.Text.Json;
using Npc.Contracts;
using Npc.Gateway;
using Npc.Host;
using Npc.Host.Metrics;
using Npc.MasterData;
using Npc.MasterData.Validation;
using Npc.Runtime;
using Npc.Tests.Runtime;

namespace Npc.Tests.Gates;

/// <summary>
/// P1 게이트. docs/11 §11 체크리스트 8항목을 자동화한다.
///
/// <b>여기가 통과해야 P2 로 넘어간다.</b> 항목마다 테스트가 하나씩 붙어 있어
/// 무엇이 깨졌는지 이름만 보고 안다.
///
/// 할당·백분위 측정이 섞여 있어 다른 테스트와 같이 돌리지 않는다
/// (<see cref="AllocationCollection"/>).
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class Phase1GateTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static HostOptions Options(params string[] args)
    {
        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return options with { MasterData = TestPaths.MasterData };
    }

    // ── 1. 500 NPC × 게임 7일 완주 ────────────────────────────────

    /// <summary>500마리가 게임 7일을 완주한다. 크래시도 데드락도 없다.</summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Gate_FiveHundredNpcsFinishSevenGameDays()
    {
        HostOptions options = Options(
            "--loopback", "--npcs", "500", "--time-scale", "600", "--days", "7",
            "--player-bots", "20", "--max-speed", "--no-dashboard");

        await using NpcHost host = NpcHost.Create(options, TextWriter.Null);

        // 데드락이면 여기서 걸린다 — 완주는 5분 안에 끝나야 한다.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        await host.RunAsync(timeout.Token);

        Assert.False(timeout.IsCancellationRequested, "7게임일이 5분 안에 끝나지 않았다.");

        MetricsSnapshot m = host.Metrics.Snapshot();

        // 600배속에서 게임 하루는 1,440틱이다.
        Assert.Equal(7 * 1_440, m.Tick.Ticks);
        Assert.Equal(7, m.Tick.GameDay);
        Assert.Equal(500, m.Npc.Total);

        // 완주했다면 스텝이 계속 전진했어야 한다 — 전원이 멈춰 있으면 완주가 아니다.
        Assert.True(host.Snapshot().StepsAdvanced > 500, $"스텝 {host.Snapshot().StepsAdvanced}");
        Assert.Equal(0, m.Link.EventGaps);
    }

    // ── 2. LLM 호출 카운터 = 0 ───────────────────────────────────

    /// <summary>P1 에는 LLM 이 없다. 코드에도 없고 카운터에도 없다.</summary>
    [Fact]
    public async Task Gate_LlmCallCounterIsZero()
    {
        HostOptions options = Options(
            "--loopback", "--npcs", "200", "--time-scale", "600", "--days", "1",
            "--no-llm", "--max-speed", "--no-dashboard");

        await using NpcHost host = NpcHost.Create(options, TextWriter.Null);
        await host.RunAsync(CancellationToken.None);

        Assert.Equal(0, host.Metrics.Snapshot().LlmCalls);

        // 틱 루프가 Npc.Llm 을 참조하면 언젠가 반드시 호출이 생긴다 (CLAUDE.md §3).
        foreach (string file in Directory.EnumerateFiles(
            TestPaths.At("src", "Npc.Runtime"), "*.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("Npc.Llm", File.ReadAllText(file), StringComparison.Ordinal);
        }

        // 주석에는 "참조하지 않는다"고 적혀 있다. 실제 참조만 잡는다.
        Assert.DoesNotContain(
            "Npc.Llm.csproj",
            File.ReadAllText(TestPaths.At("src", "Npc.Runtime", "Npc.Runtime.csproj")),
            StringComparison.Ordinal);
    }

    // ── 3. 대장장이 하루 사이클 ──────────────────────────────────

    /// <summary>
    /// 대장장이 한 마리를 추적하면 기상 → 일터 → 선술집 → 귀가 사이클이 보인다.
    /// 기록 링크로 명령을 남기고 그 NPC 의 MoveTo 대상 POI 를 순서대로 읽는다.
    /// </summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Gate_BlacksmithShowsDailyCycle()
    {
        string trace = Path.Combine(Path.GetTempPath(), $"npc-gate-{Guid.NewGuid():N}.jsonl");

        try
        {
            HostOptions options = Options(
                "--link", "record", "--trace", trace,
                "--npcs", "500", "--time-scale", "600", "--days", "2",
                "--max-speed", "--no-dashboard");

            int blacksmith;

            await using (NpcHost host = NpcHost.Create(options, TextWriter.Null))
            {
                blacksmith = FindBlacksmith(host);

                await host.RunAsync(CancellationToken.None);
            }

            string[] visited = [.. MoveTargets(trace, blacksmith)];

            Assert.NotEmpty(visited);

            // 순서가 있는 사이클이어야 한다 — 방문 집합만으로는 "돌고 있다"를 증명하지 못한다.
            Assert.True(
                ContainsCycle(visited, ["house", "smithy", "tavern", "house"]),
                $"방문 순서: {string.Join(" → ", visited)}");
        }
        finally
        {
            File.Delete(trace);
        }
    }

    // ── 4·5·8. 틱 p99 · 인지 스캔 · Gen0 ─────────────────────────

    /// <summary>NPC 5,000 에서 틱 p99 ≤ 20ms, 인지 스캔 ≤ 150/틱, 틱 창 할당 0.</summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Gate_TickBudgetHoldsAtFiveThousandNpcs()
    {
        HostOptions options = Options(
            "--loopback", "--npcs", "5000", "--time-scale", "600", "--days", "1",
            "--player-bots", "20", "--max-speed", "--no-dashboard");

        await using NpcHost host = NpcHost.Create(options, TextWriter.Null);
        await host.RunAsync(CancellationToken.None);

        MetricsSnapshot m = host.Metrics.Snapshot();

        Assert.Equal(5_000, m.Npc.Total);

        // 4. 틱 p99 ≤ 20ms
        Assert.True(m.Tick.P99Ms <= NpcServerLoop.TickBudgetMs, $"p99 {m.Tick.P99Ms}ms");
        Assert.Equal(0, m.Tick.Overruns);

        // 5. 인지 스캔 ≤ 150/틱
        Assert.InRange(m.Replan.ScanPerTick, 0, CognitionScheduler.MaxScansPerTick);

        // 8. 틱 루프 할당 0 — Gen0 델타 0 의 필요조건이자 더 엄격한 조건이다.
        //    (Gen0 카운트 자체는 프로세스 전역이라 같은 프로세스의 Sim 이 올린다.
        //     틱 루프만 격리한 측정은 Runtime_NoGen0GcInTickLoop 에 있다.)
        Assert.Equal(0, host.Metrics.AllocatedInTicks);
        Assert.Equal(0, m.Tick.BytesPerTick);
    }

    // ── 6. Null 링크로 교체해도 기동 ─────────────────────────────

    /// <summary>docs/02 §6 — 링크를 바꿔도 NPC 서버 코드는 그대로다.</summary>
    [Theory]
    [InlineData("loopback")]
    [InlineData("null")]
    [InlineData("record")]
    public async Task Gate_BootsWithAnyLink(string link)
    {
        string trace = Path.Combine(Path.GetTempPath(), $"npc-gate-{Guid.NewGuid():N}.jsonl");

        try
        {
            HostOptions options = Options(
                "--link", link, "--trace", trace,
                "--npcs", "100", "--time-scale", "600", "--days", "1",
                "--max-speed", "--no-dashboard");

            await using NpcHost host = NpcHost.Create(options, TextWriter.Null);
            await host.RunAsync(CancellationToken.None);

            MetricsSnapshot m = host.Metrics.Snapshot();

            Assert.Equal(1_440, m.Tick.Ticks);
            Assert.Equal(100, m.Npc.Total);
            Assert.True(m.Link.CommandsEnqueued > 0);
        }
        finally
        {
            File.Delete(trace);
        }
    }

    /// <summary>기록한 것을 재생하면 같은 이벤트 수가 다시 흐른다.</summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Gate_ReplaysRecordedSession()
    {
        string trace = Path.Combine(Path.GetTempPath(), $"npc-gate-{Guid.NewGuid():N}.jsonl");

        try
        {
            long recorded;

            await using (NpcHost host = NpcHost.Create(
                Options("--link", "record", "--trace", trace, "--npcs", "100",
                        "--time-scale", "600", "--days", "1", "--max-speed", "--no-dashboard"),
                TextWriter.Null))
            {
                await host.RunAsync(CancellationToken.None);
                recorded = host.Metrics.Snapshot().Link.EventsDrained;
            }

            await using NpcHost replay = NpcHost.Create(
                Options("--link", "replay", "--trace", trace, "--npcs", "100",
                        "--time-scale", "600", "--days", "1", "--max-speed", "--no-dashboard"),
                TextWriter.Null);

            await replay.RunAsync(CancellationToken.None);

            Assert.Equal(recorded, replay.Metrics.Snapshot().Link.EventsDrained);
        }
        finally
        {
            File.Delete(trace);
        }
    }

    // ── 7. 마스터데이터 V1~V11 ──────────────────────────────────

    /// <summary>V1~V11 전부 통과한다. 실패는 기동 실패다 (CLAUDE.md §2.4).</summary>
    [Fact]
    public void Gate_MasterDataPassesV1ToV11()
    {
        MasterDataValidationReport report = MasterDataValidator.Validate(TestPaths.MasterData);

        Assert.True(
            report.IsValid,
            string.Join("\n", report.Violations.Select(v => $"{v.Code}: {v.Detail}")));

        // 건너뛴 규칙은 P1 에 없는 것(V9 프롬프트)만이어야 한다.
        Assert.All(report.Skipped, s => Assert.Equal("V9", s.Code));

        // 폴백 40개도 로드 시점에 검증기 1~3단을 통과한 상태다.
        Assert.NotNull(s_data.Fallbacks);
        Assert.Equal(s_data.Archetypes.Count, s_data.Fallbacks!.Count);
    }

    // ── 헬퍼 ────────────────────────────────────────────────────

    private static int FindBlacksmith(NpcHost host)
    {
        SimWorldView world = new(host);

        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));

        for (int npc = 0; npc < host.Npcs; npc++)
        {
            if (world.ArchetypeOf(npc) == smith.Code)
            {
                return npc;
            }
        }

        Assert.Fail("대장장이가 한 마리도 뽑히지 않았다.");
        return -1;
    }

    /// <summary>기록에서 이 NPC 의 MoveTo 대상 POI 를 순서대로 읽는다.</summary>
    private static IEnumerable<string> MoveTargets(string trace, int npc)
    {
        string previous = string.Empty;

        foreach (string line in File.ReadLines(trace))
        {
            if (line.Length == 0)
            {
                continue;
            }

            LinkRecord? record = JsonSerializer.Deserialize(line, LinkRecordJsonContext.Default.LinkRecord);

            if (record?.Command is not { } command
                || command.Kind != NpcCommandKind.MoveTo
                || command.Npc.Value != npc
                || command.TargetPoi.Value == 0)
            {
                continue;
            }

            string subtype = s_data.Pois[command.TargetPoi].Subtype;

            if (!string.Equals(subtype, previous, StringComparison.Ordinal))
            {
                previous = subtype;
                yield return subtype;
            }
        }
    }

    /// <summary>방문 순서 안에 이 사이클이 통째로 들어 있는가.</summary>
    private static bool ContainsCycle(string[] visited, string[] cycle)
    {
        for (int start = 0; start + cycle.Length <= visited.Length; start++)
        {
            bool match = true;

            for (int k = 0; k < cycle.Length; k++)
            {
                if (!string.Equals(visited[start + k], cycle[k], StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>호스트가 들고 있는 Sim 월드를 읽기 전용으로 본다.</summary>
    private readonly struct SimWorldView(NpcHost host)
    {
        public ArchetypeId ArchetypeOf(int npc) =>
            host.Driver is { } driver ? driver.World.ArchetypeOf(npc) : default;
    }
}
