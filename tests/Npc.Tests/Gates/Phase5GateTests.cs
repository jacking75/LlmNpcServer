using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npc.Contracts;
using Npc.Core;
using Npc.Gateway;
using Npc.Host;
using Npc.Host.Metrics;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Gates;

/// <summary>
/// P5 게이트. docs/15 §10 체크리스트 9항목을 자동화한다.
///
/// <b>여기가 이 프로젝트의 마지막 판정이다.</b> 항목마다 테스트가 하나씩 붙어 있어
/// 무엇이 미달인지 이름만 보고 안다.
///
/// <para>
/// <b>실측 산출물이 있어야 판정되는 항목이 셋 있다</b> — 골든 합격률(`W11_golden.md`) ·
/// 블라인드 평가 n ≥ 480(`blind_eval_result.md`) · 오써링 정산(`authoring_result.md`).
/// 그 셋은 <c>Category=Gate</c> 이고 산출물이 없으면 <b>실패한다</b> —
/// 없는 것을 통과로 세면 게이트가 거짓이 된다 (CLAUDE.md §5).
/// </para>
///
/// <para>
/// 나머지는 기제(mechanism)만 보므로 상시 돈다 — 다른 테스트가 이미 강제하고 있고,
/// 여기서는 "게이트 항목으로 세어졌다"는 것을 한자리에 모은다.
/// </para>
/// </summary>
public sealed class Phase5GateTests
{
    /// <summary>docs/15 §10 — 골든 합격률 하한.</summary>
    private const double MinGoldenPassRate = 0.90;

    /// <summary>docs/15 §6 — 블라인드 평가 최소 판정 수.</summary>
    private const int MinJudgements = 480;

    private static string Measurement(string name) => TestPaths.At("docs", "measurements", name);

    // ── 1. 골든 50건 합격률 ≥ 90% ─────────────────────────────────

    [Fact]
    [Trait("Category", "Gate")]
    public void Gate_GoldenPassRateAtLeastNinety()
    {
        string path = Measurement("W11_golden.md");

        Assert.True(File.Exists(path), "골든 실측이 없다. `dotnet test --filter Category=Golden` 을 먼저 돌린다.");

        string text = File.ReadAllText(path);
        Match rate = Regex.Match(
            text, @"단언 합격률 \| (?<v>[\d.]+) %", RegexOptions.CultureInvariant);

        Assert.True(rate.Success, text);

        double value = double.Parse(rate.Groups["v"].Value, CultureInfo.InvariantCulture) / 100;

        Assert.True(value >= MinGoldenPassRate, $"골든 합격률 {value:P1} — {MinGoldenPassRate:P0} 미달");
    }

    // ── 2. 결정론 리플레이 100% 일치 ──────────────────────────────

    /// <summary>
    /// 판정은 <c>ReplayTests</c> 가 한다 (기록 2회 바이트 동일 + 재생 명령 열 동일).
    /// 여기서는 <b>그 테스트가 존재하고 기본 CI 에서 돈다</b>는 것을 고정한다 —
    /// 게이트 항목이 조용히 사라지는 것을 막는다.
    /// </summary>
    [Fact]
    public void Gate_ReplayDeterminismIsEnforcedInCi()
    {
        Type replay = typeof(Npc.Tests.Determinism.ReplayTests);

        Assert.NotNull(replay.GetMethod("Determinism_ReplayMatchesByteForByte"));
        Assert.NotNull(replay.GetMethod("Determinism_TwoRecordingsAreIdentical"));

        // Golden·Gate 필터에 걸리지 않아야 상시 돈다.
        object[] traits = replay.GetCustomAttributes(typeof(TraitAttribute), inherit: false);

        Assert.All(traits, t => Assert.DoesNotContain("Golden", t.ToString()!, StringComparison.Ordinal));
    }

    // ── 3. 시나리오 A / B / C 전부 통과 ───────────────────────────

    /// <summary>세 시나리오가 같은 코드로 돌고 완주한다. 판정 수치는 각 시나리오 테스트에 있다.</summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Gate_ThreeScenariosFinish()
    {
        (string Name, string[] Args)[] scenarios =
        [
            ("A 살아있는 마을", ["--loopback"]),
            ("B 공성", ["--loopback", "--scenario", TestPaths.At("scenarios", "siege.jsonl")]),
            ("C 단계적 차단", ["--loopback", "--scenario", TestPaths.At("scenarios", "blackout.jsonl")]),
        ];

        foreach ((string name, string[] extra) in scenarios)
        {
            string[] args =
            [
                .. extra,
                "--npcs", "1000", "--time-scale", "600", "--days", "5", "--max-speed", "--no-dashboard",
            ];

            Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

            await using NpcHost host = NpcHost.Create(
                options with { MasterData = TestPaths.MasterData }, TextWriter.Null);

            await host.RunAsync(CancellationToken.None);

            MetricsSnapshot metrics = host.Metrics.Snapshot();

            Assert.Equal(5 * 1_440, metrics.Tick.Ticks);
            Assert.Equal(0, metrics.Tick.Overruns);
            Assert.Equal(0, metrics.Link.EventGaps);
            Assert.True(metrics.Tick.P99Ms <= NpcServerLoop.TickBudgetMs, $"{name}: p99 {metrics.Tick.P99Ms}ms");
            Assert.True(host.Snapshot().StepsAdvanced > 1_000, name);
        }
    }

    // ── 4. 링크 4종 교체 시 런타임 코드 diff = 0 ──────────────────

    /// <summary>
    /// 런타임 세 프로젝트가 링크 구현체를 참조하지 않는다.
    /// 참조가 없으면 링크를 갈아끼우는 데 이 코드를 고칠 방법 자체가 없다.
    /// </summary>
    [Fact]
    public void Gate_RuntimeDoesNotDependOnAnyLinkImplementation()
    {
        foreach (string project in new[]
        {
            "src/Npc.Runtime/Npc.Runtime.csproj",
            "src/Npc.Core/Npc.Core.csproj",
            "src/Npc.Planning/Npc.Planning.csproj",
        })
        {
            Assert.DoesNotContain(
                "Npc.Gateway",
                File.ReadAllText(TestPaths.At(project.Split('/'))),
                StringComparison.Ordinal);
        }

        // 4종 + TCP 골격이 전부 같은 인터페이스를 만족한다.
        Assert.Equal(
            5,
            typeof(NullGameServerLink).Assembly.GetTypes()
                .Count(t => t is { IsClass: true, IsAbstract: false } && typeof(IGameServerLink).IsAssignableFrom(t)));
    }

    // ── 5. 블라인드 평가 n ≥ 480 ──────────────────────────────────

    [Fact]
    [Trait("Category", "Gate")]
    public void Gate_BlindEvaluationHasEnoughJudgements()
    {
        string path = Measurement("blind_eval_result.md");

        Assert.True(File.Exists(path), "블라인드 결과가 없다.");

        string text = File.ReadAllText(path);

        // 신뢰구간 병기는 자료 수와 무관하게 지켜져야 한다.
        Assert.Contains("95% 신뢰구간", text, StringComparison.Ordinal);

        Match judgements = Regex.Match(
            text, @"Q1·Q2 판정 \| (?<n>\d+)", RegexOptions.CultureInvariant);

        Assert.True(judgements.Success, text);

        int n = int.Parse(judgements.Groups["n"].Value, CultureInfo.InvariantCulture);

        Assert.True(n >= MinJudgements, $"판정 {n}건 — {MinJudgements} 미달. 평가를 실제로 돌려야 한다.");
    }

    // ── 6. 오써링 공수 축 A·축 B 양쪽 산출 ────────────────────────

    [Fact]
    [Trait("Category", "Gate")]
    public void Gate_AuthoringHasBothAxes()
    {
        string path = Measurement("authoring_result.md");

        Assert.True(File.Exists(path), "오써링 정산이 없다.");

        string text = File.ReadAllText(path);

        Assert.Contains("축 A", text, StringComparison.Ordinal);
        Assert.Contains("축 B", text, StringComparison.Ordinal);
        Assert.Contains("커버리지 배수", text, StringComparison.Ordinal);

        // 축 A 는 수작성 실측이 있어야 확정된다.
        Assert.DoesNotContain("**미측정.** `authoring_time.jsonl`", text, StringComparison.Ordinal);
    }

    // ── 7. 비용 실측과 추정의 오차율 제시 ─────────────────────────

    [Fact]
    public void Gate_CostReportShowsTheErrorAgainstTheEstimate()
    {
        string path = Measurement("cost_actual.md");

        Assert.True(File.Exists(path), "비용 정산이 없다.");

        string text = File.ReadAllText(path);

        Assert.Contains("오차", text, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"\+\d+ %", RegexOptions.CultureInvariant), text);

        // 측정 기기·VRAM 제약을 밝혀야 T1 수치를 옮겨 쓰지 않는다.
        Assert.Contains("RTX 4060", text, StringComparison.Ordinal);
        Assert.Contains("VRAM", text, StringComparison.Ordinal);
    }

    // ── 8. docs/00 §4 수용 기준 전 항목 체크 ──────────────────────

    /// <summary>
    /// 수용 기준의 모든 줄이 판정돼 있어야 한다 — <c>[ ]</c> 가 남아 있으면 안 본 것이다.
    /// 통과(<c>[x]</c>)든 미달(<c>[-]</c>)이든 판정은 있어야 한다.
    /// </summary>
    [Fact]
    public void Gate_EveryAcceptanceCriterionIsJudged()
    {
        string text = File.ReadAllText(TestPaths.At("docs", "00_Deliverables.md"));

        int start = text.IndexOf("## 4. 최종 수용 기준", StringComparison.Ordinal);
        int end = text.IndexOf("## 5.", StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, "수용 기준 절을 찾지 못했다.");

        string section = text[start..end];

        Assert.DoesNotContain("- [ ]", section, StringComparison.Ordinal);
        Assert.Contains("- [x]", section, StringComparison.Ordinal);
    }

    // ── 9. 보고서 작성 완료 ───────────────────────────────────────

    [Fact]
    public void Gate_ReportHasAllEightSections()
    {
        string text = File.ReadAllText(TestPaths.At("docs", "RnD_Report.md"));

        foreach (string heading in new[]
        {
            "## 1. 요약",
            "## 2. 무엇을 만들었나",
            "## 3. 기술 검증 결과",
            "## 4. 품질 검증 결과",
            "## 5. 오써링 공수",
            "## 6. 발견과 예상 밖의 것",
            "## 7. 상용 전환 시 남는 과제",
            "## 8. 권고",
        })
        {
            Assert.Contains(heading, text, StringComparison.Ordinal);
        }

        // 7번의 4항목.
        foreach (string task in new[] { "### 7.1", "### 7.2", "### 7.3", "### 7.4" })
        {
            Assert.Contains(task, text, StringComparison.Ordinal);
        }
    }

    // ── 부록: 플랜 스토어가 절대 null 을 주지 않는다 ──────────────

    /// <summary>
    /// 시나리오 C 3단계가 성립하는 근거. 캐시가 통째로 꺼져도 실행기는 플랜을 받는다.
    /// </summary>
    [Fact]
    public void Gate_PlanStoreNeverReturnsNullEvenWhenKilled()
    {
        PlanStore plans = PlanStore.CreateIdleOnly(GoldenSuiteData);
        var switches = new KillSwitchState();

        plans.Switches = switches;
        switches.Fire(KillSwitchTarget.PlanStore);

        for (int i = 0; i < BucketKey.TotalKeys; i += 97)
        {
            Assert.NotNull(plans.Resolve(BucketKey.FromIndex(i)));
        }
    }

    private static MasterDataSet GoldenSuiteData => MasterDataLoader.Load(TestPaths.MasterData);
}
