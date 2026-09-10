using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Npc.Host;
using Npc.Host.Config;
using Npc.MasterData.Validation;

namespace Npc.Tests.Docs;

/// <summary>
/// E-01 LLM 온보딩 팩.
///
/// <b>이 문서들이 틀리면 없는 것보다 나쁘다.</b> LLM 은 여기 적힌 경로를 열려 하고 여기 적힌
/// 명령을 부른다 — 없는 파일을 가리키면 헤매고, 없는 옵션을 쓰면 기동이 실패한다.
/// 그래서 <b>언급된 것이 실제로 있는지</b>를 기계가 확인한다.
/// </summary>
public sealed partial class LlmPackTests
{
    private static readonly string s_pack = TestPaths.At("docs", "llm");

    /// <summary>팩을 이루는 파일. 하나라도 없으면 스킬이 반쪽이다.</summary>
    public static TheoryData<string> PackFiles =>
    [
        "SKILL.md",
        "CONTEXT.md",
        "PROMPTS.md",
        "ANTIPATTERNS.md",
        "GLOSSARY.md",
        "VALIDATION.md",
    ];

    /// <summary>레시피. 작업마다 하나씩이다.</summary>
    public static TheoryData<string> Recipes =>
    [
        "add-item-poi.md",
        "add-archetype.md",
        "add-action.md",
        "write-fallback-plan.md",
        "add-interrupt.md",
        "write-scenario.md",
        "connect-game-server.md",
        "prebake-and-review.md",
        "operate.md",
        "diagnose-npc.md",
        "diagnose-validation.md",
    ];

    [Theory]
    [MemberData(nameof(PackFiles))]
    public void Pack_FileExists(string name)
    {
        Assert.True(File.Exists(Path.Combine(s_pack, name)), $"docs/llm/{name} 이 없다");
    }

    [Theory]
    [MemberData(nameof(Recipes))]
    public void Recipe_HasEverySection(string name)
    {
        string path = Path.Combine(s_pack, "RECIPES", name);

        Assert.True(File.Exists(path), $"docs/llm/RECIPES/{name} 이 없다");

        string text = File.ReadAllText(path);

        // 같은 틀이라야 LLM 이 어디를 볼지 안다. "확인" 이 없으면 판정 없이 끝난다.
        Assert.Contains("## 확인", text, StringComparison.Ordinal);
        Assert.Contains("## 파급", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>CONTEXT.md</c> 는 <b>첫 메시지에 통째로 넣는 용도</b>다. 길면 그 자리에서 예산을 먹는다.
    /// </summary>
    [Fact]
    public void Context_FitsTheBudget()
    {
        string text = File.ReadAllText(Path.Combine(s_pack, "CONTEXT.md"));

        // 토크나이저를 붙이지 않고 보수적으로 센다 — 한글은 대략 글자당 1토큰 이상이다.
        // 실제 토큰보다 적게 세면 예산을 넘긴 채 통과하므로 문자 수를 그대로 상한에 쓴다.
        Assert.True(
            text.Length <= 12_000,
            $"CONTEXT.md 가 {text.Length}자다. 3,000토큰 예산(대략 12,000자)을 넘는다 — "
            + "긴 것은 RECIPES 로 뺀다.");
    }

    /// <summary>
    /// <b>언급한 파일이 실제로 있어야 한다.</b> 없는 경로를 가리키면 LLM 이 그것을 찾다 헤맨다.
    /// </summary>
    [Theory]
    [MemberData(nameof(PackFiles))]
    public void Pack_ReferencedPathsExist(string name)
    {
        string text = File.ReadAllText(Path.Combine(s_pack, name));

        foreach (string path in PathsIn(text))
        {
            Assert.True(
                File.Exists(TestPaths.At(path)) || Directory.Exists(TestPaths.At(path)),
                $"docs/llm/{name} 이 없는 경로를 가리킨다: {path}");
        }
    }

    [Theory]
    [MemberData(nameof(Recipes))]
    public void Recipe_ReferencedPathsExist(string name)
    {
        string text = File.ReadAllText(Path.Combine(s_pack, "RECIPES", name));

        foreach (string path in PathsIn(text))
        {
            Assert.True(
                File.Exists(TestPaths.At(path)) || Directory.Exists(TestPaths.At(path)),
                $"RECIPES/{name} 이 없는 경로를 가리킨다: {path}");
        }
    }

    /// <summary>
    /// <b>언급한 옵션이 실제로 있어야 한다.</b> 없는 옵션을 쓰면 기동이 "모르는 인자다" 로 실패한다.
    /// </summary>
    [Fact]
    public void Pack_ReferencedOptionsExist()
    {
        var known = HostOptionsSource.Options.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);

        // npc CLI 와 도구의 플래그. 호스트 옵션표에는 없다.
        var toolFlags = new HashSet<string>(StringComparer.Ordinal)
        {
            "--masterdata", "--planstore", "--json", "--apply", "--population",
            "--out", "--from", "--weight", "--workplace", "--bucket", "--base",
            "--check", "--state", "--archetype", "--budget-usd", "--limit",
            "--concurrency", "--resume", "--seed", "--trace", "--npc", "--url",
            "--headless", "--format", "--help",
        };

        foreach (string file in Files())
        {
            string text = File.ReadAllText(file);

            foreach (Match match in OptionPattern().Matches(text))
            {
                string option = match.Groups[1].Value;

                Assert.True(
                    known.Contains(option) || toolFlags.Contains(option),
                    $"{Path.GetFileName(file)} 이 모르는 옵션을 쓴다: {option}");
            }
        }
    }

    /// <summary>
    /// 검증 코드를 언급했으면 사전에 있어야 한다 — 없는 코드를 설명하면 LLM 이 그것을 찾다 만다.
    /// </summary>
    [Fact]
    public void Pack_ReferencedValidationCodesExist()
    {
        var known = FixHints.Codes.ToHashSet(StringComparer.Ordinal);

        foreach (string file in Files())
        {
            foreach (Match match in ValidationCodePattern().Matches(File.ReadAllText(file)))
            {
                string code = match.Groups[1].Value;

                Assert.True(
                    known.Contains(code),
                    $"{Path.GetFileName(file)} 이 사전에 없는 검증 코드를 쓴다: {code}");
            }
        }
    }

    /// <summary>스킬 파일은 저장소 안에도 링크돼 있어야 에이전트가 찾는다.</summary>
    [Fact]
    public void Skill_IsLinkedForAgents()
    {
        string linked = TestPaths.At(".claude", "skills", "npc-server", "SKILL.md");

        Assert.True(File.Exists(linked), ".claude/skills/npc-server/SKILL.md 이 없다");

        Assert.Equal(
            File.ReadAllText(Path.Combine(s_pack, "SKILL.md")).ReplaceLineEndings(),
            File.ReadAllText(linked).ReplaceLineEndings());
    }

    /// <summary>스킬 정의에 frontmatter 가 있어야 에이전트가 언제 쓸지 안다.</summary>
    [Fact]
    public void Skill_HasFrontMatter()
    {
        string text = File.ReadAllText(Path.Combine(s_pack, "SKILL.md")).ReplaceLineEndings("\n");

        Assert.StartsWith("---\n", text, StringComparison.Ordinal);
        Assert.Contains("\nname: npc-server\n", text, StringComparison.Ordinal);
        Assert.Contains("\ndescription: ", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>CONTEXT.md</c> 는 절 순서가 고정이다 — LLM 이 "2번이 절대 규칙" 이라고 배우면
    /// 그 자리가 바뀌면 안 된다.
    /// </summary>
    [Fact]
    public void Context_KeepsSectionOrder()
    {
        string text = File.ReadAllText(Path.Combine(s_pack, "CONTEXT.md"));

        ImmutableArray<string> expected =
        [
            "## 1. 이것은 무엇이고 무엇이 아닌가",
            "## 2. 절대 규칙",
            "## 3. 마스터데이터 작업 순서",
            "## 4. 무엇을 하려면 어디를 여는가",
            "## 5. 확인 명령",
            "## 6. 손대면 안 되는 것",
            "## 7. 무엇을 바꾸면 무엇이 무효인가",
            "## 8. 실측 수치",
            "## 9. 모르면 멈추고 물어본다",
        ];

        int at = 0;

        foreach (string heading in expected)
        {
            int found = text.IndexOf(heading, at, StringComparison.Ordinal);

            Assert.True(found >= 0, $"CONTEXT.md 에 '{heading}' 절이 없거나 순서가 바뀌었다");

            at = found;
        }
    }

    private static IEnumerable<string> Files()
    {
        foreach (string file in Directory.EnumerateFiles(s_pack, "*.md"))
        {
            yield return file;
        }

        foreach (string file in Directory.EnumerateFiles(Path.Combine(s_pack, "RECIPES"), "*.md"))
        {
            yield return file;
        }
    }

    /// <summary>본문에서 저장소 상대 경로를 뽑는다. 코드 펜스 안의 명령줄은 제외한다.</summary>
    private static IEnumerable<string> PathsIn(string text)
    {
        foreach (Match match in PathPattern().Matches(text))
        {
            string path = match.Groups[1].Value.TrimEnd('/');

            // 와일드카드·글롭은 실재를 확인할 수 없다.
            if (path.Contains('*', StringComparison.Ordinal)
                || path.Contains('<', StringComparison.Ordinal))
            {
                continue;
            }

            yield return path;
        }
    }

    /// <summary>백틱 안의 저장소 경로. <c>`src/Npc.Host/Program.cs`</c> 꼴이다.</summary>
    [GeneratedRegex(@"`((?:src|tests|tools|docs|masterdata|testbed|samples|scenarios|deploy|planstore|\.vscode|\.claude)/[A-Za-z0-9_./*<>-]+)`")]
    private static partial Regex PathPattern();

    /// <summary>백틱 안의 <c>--option</c>.</summary>
    [GeneratedRegex(@"`(--[a-z][a-z0-9-]*)")]
    private static partial Regex OptionPattern();

    /// <summary><c>V10</c> · <c>V3.PRECONDITION_UNMET</c> 꼴. 굵게 표시된 것도 잡는다.</summary>
    [GeneratedRegex(@"\b(V\d{1,2}(?:\.[A-Z_]+)?)\b")]
    private static partial Regex ValidationCodePattern();
}
