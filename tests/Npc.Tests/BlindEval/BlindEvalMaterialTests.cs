using System.Text.Json;
using System.Text.RegularExpressions;

namespace Npc.Tests.BlindEval;

/// <summary>
/// T5-13 완료 조건 — 40건 · 짝 매칭 · 정답 키 미노출. docs/15 §6.
///
/// <b>자료 자체(<c>artifacts/blind_eval/*.md</c>)는 <c>.gitignore</c> 대상이라 CI 에 없다.</b>
/// 그래서 커밋되는 산출물인 <b>정답 키</b>를 검사한다 — 키가 성립하면 자료도 성립한다
/// (같은 생성기가 같은 시드로 둘을 같이 만든다).
/// </summary>
public sealed class BlindEvalMaterialTests
{
    private static readonly string s_keyPath = TestPaths.At("docs", "measurements", "blind_eval_key.md");

    /// <summary>배치 표의 한 줄. 예: <c>| 07 | A | #1 | blacksmith | Peace | Fair |</c>.</summary>
    private static readonly Regex s_row = new(
        @"^\|\s*(?<no>\d{2})\s*\|\s*(?<group>[AB])\s*\|\s*#(?<npc>\d+)\s*\|\s*(?<archetype>\S+)\s*\|",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static string Key => File.ReadAllText(s_keyPath);

    [Fact]
    public void BlindEval_HasFortyCasesTwentyPerGroup()
    {
        MatchCollection rows = s_row.Matches(Key);

        Assert.Equal(40, rows.Count);
        Assert.Equal(20, rows.Count(r => r.Groups["group"].Value == "A"));
        Assert.Equal(20, rows.Count(r => r.Groups["group"].Value == "B"));

        // 제시번호는 01..40 이 한 번씩이다.
        Assert.Equal(
            Enumerable.Range(1, 40),
            rows.Select(r => int.Parse(r.Groups["no"].Value, System.Globalization.CultureInfo.InvariantCulture)).Order());
    }

    /// <summary>
    /// 짝 매칭 — 같은 NPC 가 A·B 에 한 번씩 나온다.
    /// 아키타입·시간대·지역상태를 따로 맞출 필요가 없는 것은 <b>같은 NPC 가 같은 세계를 두 번 살았기</b> 때문이다.
    /// </summary>
    [Fact]
    public void BlindEval_EveryCaseIsPaired()
    {
        var byNpc = s_row.Matches(Key)
            .GroupBy(r => r.Groups["npc"].Value, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(20, byNpc.Length);

        foreach (var group in byNpc)
        {
            Assert.Equal(2, group.Count());
            Assert.Equal(["A", "B"], group.Select(r => r.Groups["group"].Value).Order(StringComparer.Ordinal));

            // 짝은 같은 아키타입이어야 한다.
            Assert.Single(group.Select(r => r.Groups["archetype"].Value).Distinct(StringComparer.Ordinal));
        }

        // 아키타입이 서로 다른 20종이다 — 같은 아키타입만 20쌍이면 표본이 아니다.
        Assert.Equal(20, byNpc.Select(g => g.First().Groups["archetype"].Value).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// 정답 키가 자료에 노출되지 않는다.
    /// 키는 <c>docs/measurements/</c> 에 있고 자료는 <c>artifacts/</c> 에 있으며 그쪽은 커밋되지 않는다.
    /// </summary>
    [Fact]
    public void BlindEval_KeyLivesOutsideTheMaterials()
    {
        Assert.True(File.Exists(s_keyPath), "정답 키가 없다.");

        Assert.DoesNotContain(
            "artifacts",
            Path.GetDirectoryName(s_keyPath)!,
            StringComparison.OrdinalIgnoreCase);

        // artifacts/ 는 버전 관리에서 빠져 있다 (CLAUDE.md §6).
        Assert.Contains(
            "artifacts/",
            File.ReadAllText(TestPaths.At(".gitignore")),
            StringComparison.Ordinal);

        // 배치를 다시 만들 수 있어야 한다 — 참가자를 추가할 때 같은 배치가 나와야 한다.
        Assert.Matches(new Regex(@"배치 시드 \| `\d+`", RegexOptions.CultureInvariant), Key);
    }

    /// <summary>
    /// 자료가 실제로 있다면(로컬 실행 뒤) 어느 군인지 드러나지 않아야 한다.
    /// CI 에는 없으므로 있을 때만 본다 — 없을 때 통과시키는 것이 아니라, 검사할 것이 없는 것이다.
    /// </summary>
    [Fact]
    public void BlindEval_MaterialsCarryNoGroupMarker()
    {
        string dir = TestPaths.At("artifacts", "blind_eval");

        if (!Directory.Exists(dir))
        {
            return;
        }

        string[] cases = [.. Directory.GetFiles(dir, "case_*.md").Order(StringComparer.Ordinal)];

        if (cases.Length == 0)
        {
            return;
        }

        Assert.Equal(40, cases.Length);

        foreach (string file in cases)
        {
            string text = File.ReadAllText(file);

            foreach (string marker in new[] { "A군", "B군", "LLM", "fallback", "폴백", "prebake", "planstore", "Prebaked" })
            {
                Assert.DoesNotContain(marker, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ---------------------------------------------------------------- T5-14 응답 수집 포맷

    /// <summary>
    /// 원자료 파일이 스키마와 수집 계획을 들고 있다 (T5-14).
    /// 응답이 하나도 안 들어온 상태에서도 "무엇을 어떻게 모으는가"가 커밋돼 있어야
    /// 분석기(T5-15)를 먼저 만들 수 있다.
    /// </summary>
    [Fact]
    public void BlindEval_RawFileDeclaresSchemaAndPlan()
    {
        string[] lines = File.ReadAllLines(TestPaths.At("docs", "measurements", "blind_eval_raw.jsonl"));

        Assert.NotEmpty(lines);

        string text = string.Join('\n', lines);

        // Q1 강제 선택 · Q2 1~5 · 쌍대 비교 셋이 다 선언돼 있다.
        Assert.Contains("\"q1\"", text, StringComparison.Ordinal);
        Assert.Contains("\"q2\"", text, StringComparison.Ordinal);
        Assert.Contains("\"prefer\"", text, StringComparison.Ordinal);

        // 12명 × 40건 = 480 판정 계획.
        Assert.Contains("\"target_judgements\":480", text, StringComparison.Ordinal);
        Assert.Contains("\"minimum_participants\":6", text, StringComparison.Ordinal);

        // 전부 유효한 JSON 이다 — 분석기가 첫 줄에서 터지면 안 된다.
        foreach (string line in lines.Where(l => l.Length > 0))
        {
            using JsonDocument document = JsonDocument.Parse(line);

            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }

    /// <summary>
    /// 양식이 참가자에게 <b>"구분 불가도 성공"</b> 이라는 프레이밍을 알리지 않는다 (docs/15 §6).
    /// 알려 주면 "구분 안 됨" 쪽으로 응답이 쏠린다.
    /// </summary>
    [Fact]
    public void BlindEval_FormDoesNotFrameTheOutcome()
    {
        string path = TestPaths.At("artifacts", "blind_eval", "response_form.md");

        if (!File.Exists(path))
        {
            return;
        }

        string text = File.ReadAllText(path);

        foreach (string leak in new[] { "구분 불가", "구분이 안", "성공", "가설", "A군", "B군", "프리베이크" })
        {
            Assert.DoesNotContain(leak, text, StringComparison.Ordinal);
        }

        // Q1 은 강제 선택이다.
        Assert.Contains("llm", text, StringComparison.Ordinal);
        Assert.Contains("human", text, StringComparison.Ordinal);
        Assert.Contains("모르겠다", text, StringComparison.Ordinal);

        // 쌍대 비교는 1부 뒤에 온다 — 짝을 먼저 알려 주면 Q1 에 단서가 샌다.
        Assert.True(
            text.IndexOf("1부", StringComparison.Ordinal) < text.IndexOf("2부", StringComparison.Ordinal),
            "쌍대 비교가 1부보다 앞에 있다.");
    }
}
