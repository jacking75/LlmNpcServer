using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Npc.Tests.Runtime;

namespace Npc.Tests.BlindEval;

/// <summary>
/// T5-15 — 통계 처리. docs/15 §6.
///
/// <c>tools/analyze_blind_eval.cs</c> 는 파일 기반 앱이라 테스트가 참조할 수 없다.
/// 그래서 <b>하위 프로세스로 돌린다.</b> 검정 구현은 도구 안의 <c>--self-test</c> 가
/// 알려진 값으로 검증하고, 여기서는 그 종료 코드와 합성 자료의 왕복을 본다.
///
/// <b>통계가 틀리면 보고서 전체가 거짓이 된다.</b> 라이브러리를 안 쓰기로 한 이상
/// 검증이 유일한 방어다.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class BlindEvalStatisticsTests
{
    private const string Tool = "tools/analyze_blind_eval.cs";

    /// <summary>검정 구현이 알려진 값과 맞는가 (이항·Wilson·t 임계값·표준편차).</summary>
    [Fact]
    public void Statistics_SelfTestPasses()
    {
        (int exit, string output) = Run("--self-test");

        // 한국어는 단언하지 않는다 — 하위 프로세스의 콘솔 인코딩이 기계마다 다르다.
        // 판정은 종료 코드이고, "FAIL" 은 ASCII 라 어느 인코딩에서도 그대로 보인다.
        Assert.True(exit == 0, output);
        Assert.DoesNotContain("FAIL", output, StringComparison.Ordinal);
        Assert.Contains("[ok] binom(15,20)", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 합성 응답 480건을 넣으면 §6 판정표의 어느 사분면인지 말한다.
    ///
    /// 자료는 <b>정답률 정확히 50%</b> · <b>Q2 는 A 가 B 보다 높게</b> 만든다 —
    /// §6 표의 첫 행("구분 불가 + A ≥ B → 오써링 자동화로 가치 확정")에 떨어져야 한다.
    /// </summary>
    [Fact]
    public void Statistics_ReportsTheQuadrantOnFullSample()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"npc-stats-{Guid.NewGuid():N}");

        Directory.CreateDirectory(dir);

        try
        {
            string raw = Path.Combine(dir, "raw.jsonl");
            string result = Path.Combine(dir, "result.md");

            File.WriteAllText(raw, Synthetic(participants: 12), new UTF8Encoding(false));

            (int exit, string output) = Run(
                "--raw", raw,
                "--key", TestPaths.At("docs", "measurements", "blind_eval_key.md"),
                "--out", result,
                "--date", "(합성)");

            Assert.True(exit == 0, output);

            string text = File.ReadAllText(result);

            Assert.Contains("| 참가자 | 12명", text, StringComparison.Ordinal);
            Assert.Contains("| 정답 / 전체 | 240 / 480 |", text, StringComparison.Ordinal);
            Assert.DoesNotContain("결론 보류", text, StringComparison.Ordinal);

            // 정답률 50% 는 H0 와 같으므로 유의하지 않다.
            Assert.Contains("0.5 와 다르다고 할 수 없다", text, StringComparison.Ordinal);

            // A 가 B 보다 높다 — 평균 차이가 양수여야 한다.
            Match delta = Regex.Match(
                text, @"평균 차이 \(A − B\) \| (?<v>-?\d+\.\d+)", RegexOptions.CultureInvariant);

            Assert.True(delta.Success, text);
            Assert.True(
                double.Parse(delta.Groups["v"].Value, CultureInfo.InvariantCulture) > 0,
                delta.Groups["v"].Value);

            Assert.Contains("오써링 자동화로 가치 확정", text, StringComparison.Ordinal);

            // 신뢰구간을 반드시 병기한다 (docs/15 §6).
            Assert.Contains("95% 신뢰구간 (Wilson)", text, StringComparison.Ordinal);
            Assert.Contains("Cohen's d", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// 참가자가 6명 미만이면 <b>결론을 내지 않는다</b> (docs/15 §6).
    /// 숫자는 계산하되 판정으로 쓰지 않는다고 적어야 한다.
    /// </summary>
    [Fact]
    public void Statistics_WithholdsTheVerdictBelowSixParticipants()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"npc-stats-{Guid.NewGuid():N}");

        Directory.CreateDirectory(dir);

        try
        {
            string raw = Path.Combine(dir, "raw.jsonl");
            string result = Path.Combine(dir, "result.md");

            File.WriteAllText(raw, Synthetic(participants: 3), new UTF8Encoding(false));

            (int exit, string output) = Run("--raw", raw, "--out", result, "--date", "(합성)");

            // 2 = 자료 부족. 실패가 아니라 "아직 아니다" 다.
            Assert.True(exit == 2, output);
            Assert.Contains("결론 보류", File.ReadAllText(result), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>커밋된 결과 파일이 지금 상태를 정직하게 적고 있다.</summary>
    [Fact]
    public void Statistics_CommittedResultMatchesTheCollectedData()
    {
        string path = TestPaths.At("docs", "measurements", "blind_eval_result.md");

        Assert.True(File.Exists(path), "결과 파일이 없다.");

        string text = File.ReadAllText(path);
        string rawText = File.ReadAllText(TestPaths.At("docs", "measurements", "blind_eval_raw.jsonl"));

        // 아직 응답이 없다면 결론 보류여야 한다 — 없는 것을 결론으로 세면 보고서가 거짓이 된다.
        bool hasResponses = rawText.Contains("\"participant\":\"P", StringComparison.Ordinal);

        if (!hasResponses)
        {
            Assert.Contains("결론 보류", text, StringComparison.Ordinal);
        }

        Assert.Contains("95% 신뢰구간", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 헬퍼

    /// <summary>
    /// 정답률 50% · Q2 는 A 가 높게. 정답 키의 A/B 배치를 읽어서 만든다.
    /// </summary>
    private static string Synthetic(int participants)
    {
        var groups = new Dictionary<int, string>();

        foreach (Match m in Regex.Matches(
            File.ReadAllText(TestPaths.At("docs", "measurements", "blind_eval_key.md")),
            @"^\|\s*(?<no>\d{2})\s*\|\s*(?<group>[AB])\s*\|\s*#\d+\s*\|",
            RegexOptions.Multiline | RegexOptions.CultureInvariant))
        {
            groups[int.Parse(m.Groups["no"].Value, CultureInfo.InvariantCulture)] = m.Groups["group"].Value;
        }

        Assert.Equal(40, groups.Count);

        var sb = new StringBuilder(64 * 1024);

        for (int p = 1; p <= participants; p++)
        {
            foreach ((int number, string group) in groups.OrderBy(kv => kv.Key))
            {
                // 짝수 번호만 맞힌다 → 정답률 정확히 50%.
                bool correct = number % 2 == 0;
                string answer = (group == "A") == correct ? "llm" : "human";

                // A 는 4~5, B 는 3 — 짝지은 차이가 1 또는 2 라 표준편차가 0 이 아니다.
                int q2 = group == "A" ? 4 + (number % 2) : 3;

                sb.Append(CultureInfo.InvariantCulture,
                    $"{{\"participant\":\"P{p:D2}\",\"case\":{number},\"q1\":\"{answer}\",\"q2\":{q2}}}\n");
            }
        }

        return sb.ToString();
    }

    private static (int Exit, string Output) Run(params string[] toolArgs)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = TestPaths.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        info.ArgumentList.Add("run");
        info.ArgumentList.Add(Tool);
        info.ArgumentList.Add("--");

        foreach (string argument in toolArgs)
        {
            info.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("dotnet 을 실행하지 못했다.");

        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(5)), "분석기가 5분 안에 끝나지 않았다.");

        return (process.ExitCode, stdout + stderr);
    }
}
