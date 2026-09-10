using System.Text;
using System.Text.RegularExpressions;

namespace Npc.Llm;

/// <summary>내용 모더레이션 훅 (C-06). D-01 이 외부 모더레이션 API 구현체를 끼운다.</summary>
public interface IContentModerator
{
    /// <summary>
    /// 사람이 읽게 될 텍스트를 정화한다.
    /// </summary>
    /// <param name="text">원본. null 이면 null 을 돌려준다.</param>
    /// <param name="blocked">금칙에 걸려 통째로 비웠으면 true.</param>
    /// <returns>정화된 텍스트. 통째로 막혔으면 null.</returns>
    string? Sanitize(string? text, out bool blocked);
}

/// <summary>
/// <c>reasoning</c> 정화 (C-06).
///
/// <b><c>reasoning</c> 은 모델이 자유롭게 쓰는 유일한 자연어다.</b> 플랜 문서의 나머지는 전부
/// enum·id 인데 이것만 200자짜리 사람 말이고, <c>planstore</c> 에 그대로 저장돼 검수자가 읽는다.
/// 모델이 URL 이나 제어문자를 뱉으면 그것이 그대로 파일에 남고 터미널·웹 뷰어를 지난다.
///
/// <para>
/// <b>여기서 하는 일은 넷이다.</b>
/// </para>
/// <list type="number">
///   <item><description>제어 문자 제거 — 터미널 이스케이프가 검수 화면을 깨뜨린다</description></item>
///   <item><description>URL·이메일 제거 — 모델이 만들어 낸 링크는 예외 없이 환각이다</description></item>
///   <item><description>길이 절단 — 스키마가 200자를 요구하지만 강제 디코딩을 신뢰하지 않는다 (§2.6)</description></item>
///   <item><description>금칙어 — 걸리면 <c>reasoning</c> 을 통째로 비운다</description></item>
/// </list>
///
/// <para>
/// <b>플랜 자체는 건드리지 않는다.</b> 정화가 스텝을 바꾸면 그것은 검증을 지난 뒤에
/// 플랜을 고치는 것이고, 그런 경로를 만들면 "검증된 플랜" 이라는 말이 거짓이 된다.
/// </para>
/// </summary>
public sealed partial class ReasoningSanitizer : IContentModerator
{
    /// <summary>금칙어 파일 이름. <c>masterdata/prompt/</c> 아래.</summary>
    public const string BlocklistFileName = "blocklist.txt";

    private readonly int _maxLength;
    private readonly string[] _blocked;

    /// <summary>정화기를 만든다.</summary>
    /// <param name="blocked">금칙어. 대소문자를 가리지 않는다.</param>
    /// <param name="maxLength">길이 상한.</param>
    public ReasoningSanitizer(
        IEnumerable<string>? blocked = null,
        int maxLength = Npc.Core.Plan.PlanDocument.MaxReasoningLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        _maxLength = maxLength;
        _blocked = blocked is null
            ? []
            : [.. blocked
                .Select(w => w.Trim())
                .Where(w => w.Length > 0 && !w.StartsWith('#'))];
    }

    /// <summary>금칙어 수. 0 이면 목록 검사를 건너뛴다.</summary>
    public int BlockedWordCount => _blocked.Length;

    /// <summary>
    /// 금칙어 파일을 읽는다. 없으면 빈 정화기다 — <b>파일이 없는 것은 오류가 아니다.</b>
    /// </summary>
    public static ReasoningSanitizer Load(string promptDirectory)
    {
        ArgumentNullException.ThrowIfNull(promptDirectory);

        string path = Path.Combine(promptDirectory, BlocklistFileName);

        return File.Exists(path)
            ? new ReasoningSanitizer(File.ReadAllLines(path))
            : new ReasoningSanitizer();
    }

    /// <inheritdoc />
    public string? Sanitize(string? text, out bool blocked)
    {
        blocked = false;

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string cleaned = UrlPattern().Replace(text, string.Empty);

        cleaned = EmailPattern().Replace(cleaned, string.Empty);
        cleaned = StripControl(cleaned);
        cleaned = WhitespaceRun().Replace(cleaned, " ").Trim();

        if (cleaned.Length == 0)
        {
            return null;
        }

        foreach (string word in _blocked)
        {
            if (cleaned.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                // 한 글자만 가리면 그 자리를 보고 원문을 짐작할 수 있다. 통째로 비운다.
                blocked = true;
                return null;
            }
        }

        // 강제 디코딩을 신뢰하지 않는다 (CLAUDE.md §2.6). 스키마가 200자를 요구해도 자른다.
        return cleaned.Length <= _maxLength ? cleaned : cleaned[.._maxLength];
    }

    /// <summary>제어 문자를 뺀다. 터미널 이스케이프가 검수 화면을 깨뜨린다.</summary>
    private static string StripControl(string text)
    {
        var sb = new StringBuilder(text.Length);

        foreach (char c in text)
        {
            if (!char.IsControl(c) || c == '\t')
            {
                sb.Append(c == '\t' ? ' ' : c);
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\b(?:https?|ftp)://\S+|\bwww\.\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\b[\w.+-]+@[\w-]+\.[\w.-]+\b")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRun();
}
