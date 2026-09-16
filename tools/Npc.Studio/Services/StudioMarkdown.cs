using System.Net;
using System.Text;

namespace Npc.Studio.Services;

/// <summary>설명 생성기가 내는 제한된 Markdown을 HTML로 바꾼다. 원문은 항상 인코딩한다.</summary>
internal static class StudioMarkdown
{
    /// <summary>제목·표·목록·강조·인라인 코드를 안전한 HTML로 렌더링한다.</summary>
    public static string ToHtml(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        string[] lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var html = new StringBuilder(markdown.Length + 512);

        for (int i = 0; i < lines.Length;)
        {
            string line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }

            int heading = HeadingLevel(line);
            if (heading > 0)
            {
                html.Append("<h").Append(heading).Append('>')
                    .Append(Inline(line[(heading + 1)..]))
                    .Append("</h").Append(heading).Append('>');
                i++;
                continue;
            }

            if (i + 1 < lines.Length && line.TrimStart().StartsWith('|') && IsTableSeparator(lines[i + 1]))
            {
                string[] headers = Cells(line);
                html.Append("<div class=\"md-table-wrap\"><table><thead><tr>");
                foreach (string cell in headers) html.Append("<th>").Append(Inline(cell)).Append("</th>");
                html.Append("</tr></thead><tbody>");
                i += 2;

                while (i < lines.Length && lines[i].TrimStart().StartsWith('|'))
                {
                    html.Append("<tr>");
                    foreach (string cell in Cells(lines[i])) html.Append("<td>").Append(Inline(cell)).Append("</td>");
                    html.Append("</tr>");
                    i++;
                }

                html.Append("</tbody></table></div>");
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                html.Append("<ul>");
                while (i < lines.Length && lines[i].StartsWith("- ", StringComparison.Ordinal))
                {
                    html.Append("<li>").Append(Inline(lines[i][2..])).Append("</li>");
                    i++;
                }
                html.Append("</ul>");
                continue;
            }

            var paragraph = new StringBuilder(line.Trim());
            i++;
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && HeadingLevel(lines[i]) == 0
                   && !lines[i].StartsWith("- ", StringComparison.Ordinal)
                   && !(i + 1 < lines.Length && lines[i].TrimStart().StartsWith('|') && IsTableSeparator(lines[i + 1])))
            {
                paragraph.Append(' ').Append(lines[i].Trim());
                i++;
            }
            html.Append("<p>").Append(Inline(paragraph.ToString())).Append("</p>");
        }

        return html.ToString();
    }

    private static int HeadingLevel(string line)
    {
        int level = 0;
        while (level < line.Length && level < 6 && line[level] == '#') level++;
        return level > 0 && level < line.Length && line[level] == ' ' ? level : 0;
    }

    private static bool IsTableSeparator(string line)
    {
        string value = line.Replace("|", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Trim();
        return value.Length == 0 && line.Contains('-');
    }

    private static string[] Cells(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        string value = line.Trim();
        int start = value.StartsWith('|') ? 1 : 0;
        int end = value.EndsWith('|') ? value.Length - 1 : value.Length;

        for (int i = start; i < end; i++)
        {
            if (value[i] == '\\' && i + 1 < end && value[i + 1] == '|')
            {
                cell.Append('|');
                i++;
            }
            else if (value[i] == '|')
            {
                cells.Add(cell.ToString().Trim());
                cell.Clear();
            }
            else
            {
                cell.Append(value[i]);
            }
        }

        cells.Add(cell.ToString().Trim());
        return [.. cells];
    }

    private static string Inline(string value)
    {
        var html = new StringBuilder(value.Length + 32);

        for (int i = 0; i < value.Length;)
        {
            if (value[i] == '`')
            {
                int close = value.IndexOf('`', i + 1);
                if (close > i)
                {
                    html.Append("<code>").Append(WebUtility.HtmlEncode(value[(i + 1)..close])).Append("</code>");
                    i = close + 1;
                    continue;
                }
            }

            if (i + 1 < value.Length && value[i] == '*' && value[i + 1] == '*')
            {
                int close = value.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i)
                {
                    html.Append("<strong>").Append(Inline(value[(i + 2)..close])).Append("</strong>");
                    i = close + 2;
                    continue;
                }
            }

            int nextCode = value.IndexOf('`', i);
            int nextStrong = value.IndexOf("**", i, StringComparison.Ordinal);
            int next = new[] { nextCode, nextStrong }.Where(x => x >= 0).DefaultIfEmpty(value.Length).Min();
            if (next == i) next++;
            html.Append(WebUtility.HtmlEncode(value[i..next]));
            i = next;
        }

        return html.ToString();
    }
}
