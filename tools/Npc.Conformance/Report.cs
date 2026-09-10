using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Npc.Conformance.Checks;

namespace Npc.Conformance;

/// <summary>
/// 적합성 보고서 (B-07).
///
/// <para>
/// <b>게임서버 팀에 <c>reference_link.html</c> 과 함께 건네는 것이다.</b> 그래서 "통과/불합격"
/// 만 적지 않는다 — 무엇을 어떻게 쟀는지와 <b>판정하지 못한 항목</b>을 같이 적는다.
/// 미판정을 통과로 세면 보고서가 거짓이 된다.
/// </para>
/// </summary>
public static class Report
{
    /// <summary>검사 목록. <b>순서가 보고서 순서다.</b></summary>
    public static ImmutableArray<IConformanceCheck> Checks { get; } =
    [
        new HandshakeCheck(),
        new TickSyncCheck(),
        new SequenceCheck(),
        new ProximityCheck(),
        new CombatCheck(),
        new CommandResponseCheck(),
        new ThroughputCheck(),
    ];

    /// <summary>전부 돌린다.</summary>
    public static ImmutableArray<CheckResult> Run(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var results = ImmutableArray.CreateBuilder<CheckResult>(Checks.Length);

        foreach (IConformanceCheck check in Checks)
        {
            results.Add(check.Run(observation));
        }

        return results.ToImmutable();
    }

    /// <summary><b>불합격이 하나라도 있으면 false.</b> 미판정은 불합격이 아니다.</summary>
    public static bool Passed(ImmutableArray<CheckResult> results)
    {
        foreach (CheckResult result in results)
        {
            if (result.Verdict == Verdict.Fail)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>사람이 읽는 보고서.</summary>
    /// <param name="results">판정.</param>
    /// <param name="observation">관찰 기록.</param>
    /// <param name="target">무엇에 붙었는가 (호스트:포트 또는 "게임서버 대역").</param>
    /// <param name="stamp">회차 시각. <b>밖에서 넣는다</b> — 결정론 때문이다 (CLAUDE.md §7).</param>
    public static string Markdown(
        ImmutableArray<CheckResult> results, Observation observation, string target, string stamp)
    {
        ArgumentNullException.ThrowIfNull(observation);

        int pass = results.Count(r => r.Verdict == Verdict.Pass);
        int fail = results.Count(r => r.Verdict == Verdict.Fail);
        int skipped = results.Count(r => r.Verdict == Verdict.NotChecked);

        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"# 게임서버 적합성 보고서 — {target}\n\n");
        text.Append(CultureInfo.InvariantCulture, $"회차: {stamp}\n\n");
        text.Append("> 규약 전문은 `docs/reference_link.html` §11 이다. 이 보고서는 그 규약을\n");
        text.Append("> **관찰로 확인한 결과**이고, 관찰하지 못한 항목은 미판정으로 적는다 —\n");
        text.Append("> **미판정은 통과가 아니다.**\n\n");

        text.Append(CultureInfo.InvariantCulture,
            $"**판정: {(fail == 0 ? "합격" : "불합격")}** — 통과 {pass} · 불합격 {fail} · 미판정 {skipped}\n\n");

        text.Append("## 회차\n\n");
        text.Append("| 항목 | 값 |\n|---|---|\n");
        text.Append(CultureInfo.InvariantCulture, $"| 협상 | {observation.NegotiationDetail} |\n");
        text.Append(CultureInfo.InvariantCulture, $"| NPC | {observation.NpcCount} |\n");
        text.Append(CultureInfo.InvariantCulture, $"| 존 | {observation.ZoneCount} |\n");
        text.Append(CultureInfo.InvariantCulture, $"| 관찰 이벤트 | {observation.Events.Length}건 |\n");
        text.Append(CultureInfo.InvariantCulture, $"| 발행 명령 | {observation.Commands.Length}건 |\n");
        text.Append(CultureInfo.InvariantCulture, $"| 마지막 틱 | {observation.LastTick} |\n");
        text.Append(CultureInfo.InvariantCulture,
            $"| 벽시계 | {(observation.WallClockMillis == 0 ? "구동 회차 (없음)" : observation.WallClockMillis + "ms")} |\n\n");

        text.Append("## 판정\n\n");
        text.Append("| 검사 | 판정 | 근거 |\n|---|---|---|\n");

        foreach (CheckResult result in results)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"| `{result.Id}` {result.Title} | {Mark(result.Verdict)} | {result.Detail} |\n");
        }

        if (fail > 0)
        {
            text.Append("\n## 위반\n\n");

            foreach (CheckResult result in results)
            {
                if (result.Verdict != Verdict.Fail)
                {
                    continue;
                }

                text.Append(CultureInfo.InvariantCulture, $"### `{result.Id}` {result.Title}\n\n");

                foreach (string violation in result.Violations)
                {
                    text.Append(CultureInfo.InvariantCulture, $"- {violation}\n");
                }

                text.Append('\n');
            }
        }

        if (skipped > 0)
        {
            text.Append("\n## 미판정 — **지우지 않는다**\n\n");
            text.Append("| 검사 | 왜 못 봤나 |\n|---|---|\n");

            foreach (CheckResult result in results)
            {
                if (result.Verdict == Verdict.NotChecked)
                {
                    text.Append(CultureInfo.InvariantCulture,
                        $"| `{result.Id}` | {result.Detail} |\n");
                }
            }

            text.Append("\n관찰 회차를 늘리거나 시나리오를 주입해야 판정할 수 있는 항목들이다.\n");
        }

        return text.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>기계가 읽는 보고서. 손으로 조립한다 — 필드 6개에 직렬화기를 끌어오지 않는다.</summary>
    /// <param name="results">판정.</param>
    /// <param name="target">대상.</param>
    /// <param name="stamp">회차 시각.</param>
    public static string Json(ImmutableArray<CheckResult> results, string target, string stamp)
    {
        var text = new StringBuilder();

        text.Append("{\n");
        text.Append(CultureInfo.InvariantCulture, $"  \"target\": {Quote(target)},\n");
        text.Append(CultureInfo.InvariantCulture, $"  \"run_at\": {Quote(stamp)},\n");
        text.Append(CultureInfo.InvariantCulture,
            $"  \"passed\": {(Passed(results) ? "true" : "false")},\n");
        text.Append("  \"checks\": [\n");

        for (int i = 0; i < results.Length; i++)
        {
            CheckResult result = results[i];

            text.Append("    {\n");
            text.Append(CultureInfo.InvariantCulture, $"      \"id\": {Quote(result.Id)},\n");
            text.Append(CultureInfo.InvariantCulture, $"      \"title\": {Quote(result.Title)},\n");
            text.Append(CultureInfo.InvariantCulture,
                $"      \"verdict\": {Quote(result.Verdict.ToString().ToLowerInvariant())},\n");
            text.Append(CultureInfo.InvariantCulture, $"      \"detail\": {Quote(result.Detail)},\n");
            text.Append("      \"violations\": [");
            text.Append(string.Join(", ", result.Violations.Select(Quote)));
            text.Append("]\n");
            text.Append(i + 1 < results.Length ? "    },\n" : "    }\n");
        }

        text.Append("  ]\n}\n");

        return text.ToString().ReplaceLineEndings("\n");
    }

    private static string Mark(Verdict verdict) => verdict switch
    {
        Verdict.Pass => "**통과**",
        Verdict.Fail => "**불합격**",
        _ => "미판정",
    };

    private static string Quote(string value)
    {
        var text = new StringBuilder("\"");

        foreach (char c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        text.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    }
                    else
                    {
                        text.Append(c);
                    }

                    break;
            }
        }

        return text.Append('"').ToString();
    }
}
