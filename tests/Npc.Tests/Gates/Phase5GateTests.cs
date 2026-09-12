using System.Globalization;
using System.Text.RegularExpressions;
using Npc.Core;
using Npc.Host;
using Npc.Host.Metrics;
using Npc.Runtime;
using Npc.Tests.Runtime;

namespace Npc.Tests.Gates;

/// <summary>
/// 최종 품질 게이트. 항목마다 테스트가 하나씩 붙어 있어 무엇이 미달인지 이름만 보고 안다.
///
/// <para>
/// <b>실측 산출물이 있어야 판정되는 항목</b>(골든 합격률 · <c>W11_golden.md</c>)은
/// <c>Category=Gate</c> 이고 산출물이 없으면 <b>실패한다</b> —
/// 없는 것을 통과로 세면 게이트가 거짓이 된다 (CLAUDE.md §5).
/// </para>
///
/// <para>
/// 나머지는 기제(mechanism)만 보므로 상시 돈다 — 다른 테스트가 이미 강제하고 있고,
/// 여기서는 "게이트 항목으로 세어졌다"는 것을 한자리에 모은다.
/// </para>
///
/// <para>
/// <b>R&amp;D 연구 장치는 2026-08-06 에 걷어냈다</b> — 블라인드 평가 판정 수 · 오써링 공수 정산 ·
/// 비용 오차율 · 수용 기준 체크 · 보고서 절 구성. 그 다섯은 삭제된 연구 문서를 읽는
/// 검사였고, 판정 결과는 <c>docs/reference_metrics.html</c> 에 보존돼 있다.
/// </para>
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class Phase5GateTests
{
    /// <summary>골든 합격률 하한.</summary>
    private const double MinGoldenPassRate = 0.90;

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
    //
    // 이 항목은 LinkSwapTests.LinkSwap_RuntimeNeverNamesAConcreteLink 가 판정한다.
    // 그쪽은 csproj 참조에 더해 IL 까지 훑어 전이 참조로 새어 든 구현체 이름도 잡는다 —
    // 여기서 csproj 만 다시 읽으면 같은 규칙을 약하게 두 번 세는 것이다.
    //
    // ── 부록: 플랜 스토어가 절대 null 을 주지 않는다 ──────────────
    //
    // 킬스위치를 당긴 채로 Resolve 를 부르는 것은 TieredPlanCompilerTests 가,
    // 시나리오 C 3단계 전체는 BlackoutTests 가 종단으로 본다.
}
