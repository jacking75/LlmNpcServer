using Npc.Llm;

namespace Npc.Tests.Llm;

/// <summary>C-06 — <c>reasoning</c> 정화. PRODUCTION_ROADMAP §6 C-06.</summary>
public sealed class ReasoningSanitizerTests
{
    [Fact]
    public void PlainText_PassesThrough()
    {
        var sanitizer = new ReasoningSanitizer();

        Assert.Equal(
            "대장장이는 아침에 일터로 간다",
            sanitizer.Sanitize("대장장이는 아침에 일터로 간다", out bool blocked));

        Assert.False(blocked);
    }

    [Theory]
    [InlineData("자세한 것은 https://example.com/x 를 보라", "자세한 것은 를 보라")]
    [InlineData("http://a.b/c", null)]
    [InlineData("www.evil.example 에서", "에서")]
    [InlineData("문의는 a.b@c.example 로", "문의는 로")]
    public void UrlsAndEmails_AreRemoved(string input, string? expected)
    {
        var sanitizer = new ReasoningSanitizer();

        // 모델이 만들어 낸 링크는 예외 없이 환각이다. 검수자가 그것을 눌러 볼 이유가 없다.
        Assert.Equal(expected, sanitizer.Sanitize(input, out _));
    }

    [Fact]
    public void ControlCharacters_AreStripped()
    {
        var sanitizer = new ReasoningSanitizer();

        // 터미널 이스케이프가 검수 화면을 깨뜨린다.
        string? cleaned = sanitizer.Sanitize("앞[31m빨강\n뒤", out _);

        Assert.NotNull(cleaned);
        Assert.DoesNotContain('', cleaned);
        Assert.DoesNotContain('', cleaned);
        Assert.DoesNotContain('\n', cleaned);
    }

    [Fact]
    public void TooLong_IsTruncated()
    {
        var sanitizer = new ReasoningSanitizer(maxLength: 10);

        // 스키마가 200자를 요구해도 자른다 — 강제 디코딩을 신뢰하지 않는다 (CLAUDE.md §2.6).
        Assert.Equal("0123456789", sanitizer.Sanitize("0123456789abcdef", out _));
    }

    [Fact]
    public void BlockedWord_EmptiesTheWholeField()
    {
        var sanitizer = new ReasoningSanitizer(["금칙"]);

        // 한 글자만 가리면 그 자리를 보고 원문을 짐작할 수 있다.
        Assert.Null(sanitizer.Sanitize("여기에 금칙 이 있다", out bool blocked));
        Assert.True(blocked);

        Assert.Equal("여기에는 없다", sanitizer.Sanitize("여기에는 없다", out bool clean));
        Assert.False(clean);
    }

    [Fact]
    public void Blocklist_IgnoresCommentsAndBlanks()
    {
        var sanitizer = new ReasoningSanitizer(["# 주석", string.Empty, "  ", "금칙"]);

        Assert.Equal(1, sanitizer.BlockedWordCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInput_StaysNull(string? input)
    {
        var sanitizer = new ReasoningSanitizer();

        Assert.Null(sanitizer.Sanitize(input, out bool blocked));
        Assert.False(blocked);
    }

    [Fact]
    public void MissingBlocklistFile_IsNotAnError()
    {
        // 없는 것은 오류가 아니다. 금칙어를 안 쓰는 회차가 정상이다.
        ReasoningSanitizer sanitizer = ReasoningSanitizer.Load(
            Path.Combine(Path.GetTempPath(), "npc-nope-" + Guid.NewGuid().ToString("N")));

        Assert.Equal(0, sanitizer.BlockedWordCount);
        Assert.Equal("괜찮다", sanitizer.Sanitize("괜찮다", out _));
    }

    [Fact]
    public void WhitespaceRuns_Collapse()
    {
        var sanitizer = new ReasoningSanitizer();

        Assert.Equal("앞 뒤", sanitizer.Sanitize("  앞    뒤  ", out _));
    }
}
