using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Npc.Narrative;

namespace Npc.Tests.Studio;

/// <summary>
/// 화면 문구 드리프트 (H21·H27).
///
/// <b>여기가 잡는 것은 "같은 것을 두 이름으로 부른다" 하나다.</b> 화면은 "직업" 이라 하고
/// 토스트는 "아키타입 'x' 이 이미 있다" 라고 하면, 사람은 직업과 아키타입의 관계를
/// 먼저 배워야 한다 — 그것이 이 도구의 목적은 아니다. 조용히 일어나므로 테스트로 잡는다.
/// </summary>
public sealed partial class StudioTextTests
{
    /// <summary>검사 대상 — Studio 의 서비스와 화면.</summary>
    private static readonly string[] SourceRoots =
    [
        Path.Combine("tools", "Npc.Studio", "Services"),
        Path.Combine("tools", "Npc.Studio", "Components"),
    ];

    /// <summary>
    /// 개발자 낱말이 문자열 리터럴에 나오지 않는다.
    ///
    /// <para>
    /// <b>주석과 XML 문서는 본다 — 거기는 개발자가 읽는 자리다.</b> 막는 것은
    /// 사람에게 보이는 문자열뿐이다. 파일 이름(<c>archetypes.json</c>)은 주소라 그대로 쓴다.
    /// </para>
    /// </summary>
    [Fact]
    public void Ui_UsesNoDeveloperWords()
    {
        var offenders = new List<string>();

        foreach (string path in SourceFiles())
        {
            int number = 0;

            foreach (string line in File.ReadAllLines(path))
            {
                number++;

                string trimmed = line.TrimStart();

                // 주석·문서 주석·Razor 주석은 개발자가 읽는 자리다.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith("@*", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in Literal().Matches(line))
                {
                    if (Lexicon.Ui.OffendingWord(match.Value) is { } word)
                    {
                        offenders.Add($"{Path.GetFileName(path)}:{number} '{word}' — {match.Value.Trim('"')}");
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "화면 문구에 개발자 낱말이 있다 (부록 B 용어표를 쓴다):" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// 대조군 — 검사기가 실제로 잡는가.
    /// <b>이것이 없으면 "아무것도 못 찾는 상태" 로도 통과한다.</b>
    /// </summary>
    [Fact]
    public void Ui_OffendingWord_FindsPlantedWord()
    {
        Assert.Equal("아키타입", Lexicon.Ui.OffendingWord("아키타입 'x' 이 이미 있다."));
        Assert.Null(Lexicon.Ui.OffendingWord("직업 'x' 가 이미 있다."));
        Assert.NotEmpty(Lexicon.Ui.Words);
    }

    /// <summary>
    /// 매뉴얼이 적은 화면 낱말이 실제로 화면에 있다 (H27).
    ///
    /// <para>
    /// <b>매뉴얼은 없는 동작을 설명하기 쉽다</b> — "지도를 클릭해도 된다" 가 그랬다.
    /// <c>&lt;span class="ui"&gt;</c> 로 감싼 낱말은 버튼·탭 이름이므로 소스에 있어야 한다.
    /// </para>
    /// </summary>
    [Fact]
    public void Manual_UiLabelsExistInSource()
    {
        string manual = TestPaths.At("docs", "npc_studio_manual.html");

        if (!File.Exists(manual))
        {
            return;
        }

        string html = File.ReadAllText(manual);
        string sources = string.Join("\n", SourceFiles().Select(file => File.ReadAllText(file)));
        var missing = new List<string>();

        foreach (Match match in UiLabel().Matches(html))
        {
            string label = match.Groups[1].Value.Trim();

            if (label.Length > 0 && !sources.Contains(label, StringComparison.Ordinal))
            {
                missing.Add(label);
            }
        }

        Assert.True(
            missing.Count == 0,
            "매뉴얼이 적은 화면 낱말이 소스에 없다: " + string.Join(" · ", missing.Distinct(StringComparer.Ordinal)));
    }

    private static ImmutableArray<string> SourceFiles() =>
    [
        .. SourceRoots
            .Select(relative => TestPaths.At(relative))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal),
    ];

    /// <summary>한 줄 안의 문자열 리터럴. 이스케이프는 이 저장소의 문구에 없다.</summary>
    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex Literal();

    /// <summary>매뉴얼의 화면 낱말 표시.</summary>
    [GeneratedRegex("<span class=\"ui\">([^<]*)</span>")]
    private static partial Regex UiLabel();
}
