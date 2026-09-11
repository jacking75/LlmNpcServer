using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace Npc.Mcp.Tools;

/// <summary>
/// 문서 검색 (E-03).
///
/// <para>
/// <b>인덱스를 만들지 않는다.</b> 문서는 수십 개이고 한 번 훑는 데 수십 밀리초다 —
/// 인덱스를 두면 그것이 낡았는지 아무도 모르는 상태가 된다(이 저장소의 파생물 문제 그대로).
/// </para>
///
/// <para>
/// <b>HTML 태그를 벗겨서 검색한다.</b> 레퍼런스가 HTML 이라 태그째 찾으면
/// <c>&lt;code&gt;</c> 사이에 낀 단어를 놓친다.
/// </para>
/// </summary>
[McpServerToolType]
public sealed partial class DocsTools
{
    /// <summary>내는 결과 수.</summary>
    public const int MaxHits = 5;

    /// <summary>결과 하나의 발췌 길이(문자).</summary>
    public const int SnippetChars = 320;

    private readonly McpOptions _options;

    /// <summary>DI 가 만든다.</summary>
    /// <param name="options">서버 설정.</param>
    public DocsTools(McpOptions options) => _options = options;

    /// <summary>문서를 찾는다.</summary>
    /// <param name="query">찾을 말.</param>
    [McpServerTool(Name = "docs_search")]
    [Description(
        "저장소 문서(docs/**·CLAUDE.md·CODEMAP.md·README.md)에서 찾는다. 상위 5건의 파일·발췌를 낸다. "
        + "**추측하기 전에 부른다** — 이 저장소의 규칙은 대부분 문서에 적혀 있고, "
        + "어긴 코드는 빌드가 아니라 테스트가 잡는다.")]
    public string Search([Description("찾을 말. 예: 프리픽스 캐시 · PoiSymbol · 샤딩")] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "query 가 비어 있다.";
        }

        var hits = new List<(string File, int Score, string Snippet)>();

        foreach (string path in Sources())
        {
            string text = Strip(File.ReadAllText(path));
            int score = Count(text, query);

            if (score == 0)
            {
                continue;
            }

            hits.Add((Relative(path), score, Snippet(text, query)));
        }

        if (hits.Count == 0)
        {
            return $"'{query}' 를 찾지 못했다. 다른 말로 다시 찾거나 CODEMAP.md 의 작업 표를 본다.";
        }

        // 점수 내림차순, 동점이면 파일 이름 — 같은 질의에 같은 순서가 나와야 한다.
        hits.Sort((a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(a.File, b.File);
        });

        var sb = new StringBuilder(4_096);

        foreach ((string file, int score, string snippet) in hits.Take(MaxHits))
        {
            sb.Append(CultureInfo.InvariantCulture, $"## {file} ({score}건)\n\n");
            sb.Append(snippet).Append("\n\n");
        }

        return sb.ToString();
    }

    /// <summary>검색 대상 파일. 생성물(스키마·와이어 표)은 뺀다 — 읽을 산문이 아니다.</summary>
    private IEnumerable<string> Sources()
    {
        foreach (string name in new[] { "CLAUDE.md", "CODEMAP.md", "README.md", "PRODUCTION_ROADMAP.md" })
        {
            string path = Path.Combine(_options.Root, name);

            if (File.Exists(path))
            {
                yield return path;
            }
        }

        string docs = Path.Combine(_options.Root, "docs");

        if (!Directory.Exists(docs))
        {
            yield break;
        }

        foreach (string path in Directory
            .EnumerateFiles(docs, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                || p.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains(Path.Combine("docs", "schema"), StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains(Path.Combine("docs", "wire"), StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal))
        {
            yield return path;
        }
    }

    private string Relative(string path) =>
        Path.GetRelativePath(_options.Root, path).Replace('\\', '/');

    /// <summary>HTML 태그와 잉여 공백을 벗긴다.</summary>
    private static string Strip(string raw) =>
        WhitespaceRegex().Replace(TagRegex().Replace(raw, " "), " ");

    private static int Count(string text, string query)
    {
        int count = 0;
        int at = 0;

        while ((at = text.IndexOf(query, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            at += query.Length;
        }

        return count;
    }

    private static string Snippet(string text, string query)
    {
        int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);

        if (at < 0)
        {
            return string.Empty;
        }

        int start = Math.Max(0, at - (SnippetChars / 2));
        int length = Math.Min(SnippetChars, text.Length - start);

        return (start > 0 ? "…" : string.Empty)
            + text.Substring(start, length).Trim()
            + (start + length < text.Length ? "…" : string.Empty);
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
