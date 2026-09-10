using System.Globalization;
using System.Text;

namespace Npc.Narrative;

/// <summary>
/// markdown 조각을 만드는 도구. <b>여기 있는 것은 서식뿐이다</b> — 판단은 카드 쪽에 있다.
///
/// 표 셀에 <c>|</c> 가 들어가면 표가 깨지므로 그것만 이스케이프한다.
/// 숫자는 <see cref="CultureInfo.InvariantCulture"/> 로 찍는다 — 로케일에 따라
/// 소수점이 <c>,</c> 가 되면 같은 입력에 다른 바이트가 나온다 (F-03 의 결정론 조건).
/// </summary>
public static class Md
{
    /// <summary>표 셀. 빈 값은 <c>—</c> 다 — 칸을 비워 두면 무엇이 없는지 알 수 없다.</summary>
    public static string Cell(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? "—"
            : text.Replace("|", "\\|", StringComparison.Ordinal)
                  .Replace("\r\n", " ", StringComparison.Ordinal)
                  .Replace("\n", " ", StringComparison.Ordinal);

    /// <summary>인라인 코드. 빈 값은 <c>—</c>.</summary>
    public static string Code(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "—" : "`" + Cell(text) + "`";

    /// <summary>코드 조각들을 <c> · </c> 로 잇는다. 비었으면 <c>—</c>.</summary>
    public static string Codes(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        string joined = string.Join(" · ", items.Select(i => "`" + Cell(i) + "`"));

        return joined.Length == 0 ? "—" : joined;
    }

    /// <summary>정수.</summary>
    public static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>소수. 자릿수를 고정한다.</summary>
    public static string F(double value, int digits) =>
        value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>게임 초를 사람이 읽는 길이로. <c>900s</c> 가 아니라 <c>15분</c> 이다.</summary>
    public static string Seconds(int seconds)
    {
        if (seconds <= 0)
        {
            return "—";
        }

        if (seconds < 60)
        {
            return N(seconds) + "초";
        }

        if (seconds % 3600 == 0)
        {
            return N(seconds / 3600) + "시간";
        }

        if (seconds < 3600)
        {
            return N(seconds / 60) + "분";
        }

        return N(seconds / 3600) + "시간 " + N(seconds % 3600 / 60) + "분";
    }

    /// <summary>표 머리. 열 이름을 준다.</summary>
    public static void TableHead(StringBuilder sb, params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(columns);

        sb.Append("| ").Append(string.Join(" | ", columns)).AppendLine(" |");
        sb.Append('|').Append(string.Join("|", columns.Select(_ => "---"))).AppendLine("|");
    }

    /// <summary>표 한 줄. 셀은 이미 이스케이프된 것으로 본다.</summary>
    public static void Row(StringBuilder sb, params string[] cells)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(cells);

        sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
    }

    /// <summary>✓ 또는 ✗. 판정을 글자로 남긴다 — 나중에 grep 할 수 있다.</summary>
    public static string Mark(bool ok) => ok ? "✓" : "✗";
}
