using Npc.Eval;
using Npc.Eval.Core;

namespace Npc.Tests.Eval;

/// <summary>
/// C-04 — <c>Npc.Eval</c> 인자와 LLM 심사원.
///
/// <b>모르는 인자는 실패다.</b> 조용히 넘기면 오타가 기본값으로 돌고, 회차가 끝난 뒤에야
/// "왜 48건만 돌았지" 를 묻게 된다.
/// </summary>
public sealed class EvalOptionsTests
{
    /// <summary>기본값과 필수 인자.</summary>
    [Fact]
    public void Parse_RequiresEngines()
    {
        Assert.False(EvalOptions.TryParse([], out _, out string? error));
        Assert.Contains("--engines", error!, StringComparison.Ordinal);

        Assert.True(EvalOptions.TryParse(["--engines", "a,b"], out EvalOptions options, out _));

        Assert.Equal(["a", "b"], [.. options.Engines]);
        Assert.Equal(48, options.Sample);
        Assert.Equal(1, options.Runs);

        // 골든은 기본이 미판정이다 — 안 돌린 것을 통과로 세지 않는다.
        Assert.Equal(-1, options.GoldenRate);
        Assert.Equal(0, options.GoldenAssertions);
    }

    /// <summary>모르는 인자는 거절이다.</summary>
    [Fact]
    public void Parse_RejectsUnknownArguments()
    {
        Assert.False(
            EvalOptions.TryParse(["--engines", "a", "--sampel", "10"], out _, out string? error));

        Assert.Contains("--sampel", error!, StringComparison.Ordinal);
    }

    /// <summary>범위 밖 값도 거절이다. 0 표본 회차는 "전부 미판정" 을 만든다.</summary>
    [Fact]
    public void Parse_RejectsNonPositiveCounts()
    {
        Assert.False(EvalOptions.TryParse(["--engines", "a", "--sample", "0"], out _, out _));
        Assert.False(EvalOptions.TryParse(["--engines", "a", "--runs", "-1"], out _, out _));
        Assert.False(EvalOptions.TryParse(["--engines", "a", "--concurrency", "0"], out _, out _));
    }

    /// <summary>골든 숫자를 받아 게이트에 넣는다.</summary>
    [Fact]
    public void Parse_TakesGoldenNumbers()
    {
        Assert.True(
            EvalOptions.TryParse(
                ["--engines", "a", "--golden-rate", "0.936", "--golden-assertions", "220"],
                out EvalOptions options,
                out _));

        Assert.Equal(0.936, options.GoldenRate);
        Assert.Equal(220, options.GoldenAssertions);
    }

    /// <summary>
    /// 심사원 응답을 읽는다. <b>개수가 다르면 통째로 버린다</b> —
    /// 어느 플랜의 점수인지 모르는 채로 평균에 넣으면 그 숫자는 아무 말도 안 한다.
    /// </summary>
    [Fact]
    public void Judge_ParsesScoresOrGivesUp()
    {
        Assert.Equal([4, 5, 3], PlanJudge.Parse("""{"scores":[4,5,3]}""", 3));

        // 앞뒤에 말이 붙어도 JSON 만 뽑는다 — 모델은 "Here you go:" 를 자주 붙인다.
        Assert.Equal([1, 2], PlanJudge.Parse("""Here you go: {"scores":[1,2]} done""", 2));

        // 개수가 다르면 버린다.
        Assert.Empty(PlanJudge.Parse("""{"scores":[4,5]}""", 3));

        // 모양이 아니면 버린다. 3점으로 채우지 않는다.
        Assert.Empty(PlanJudge.Parse("좋아 보인다", 1));
        Assert.Empty(PlanJudge.Parse("""{"verdict":"good"}""", 1));
        Assert.Empty(PlanJudge.Parse(null, 1));
        Assert.Empty(PlanJudge.Parse("""{"scores":[""", 1));
    }

    /// <summary>
    /// <b>심사원 프롬프트에 플레이어 문자열이 들어갈 자리가 없다</b> (CLAUDE.md §2.5).
    /// 보는 것은 버킷 좌표·goal·액션 시퀀스뿐이고 전부 우리 어휘다.
    /// </summary>
    [Fact]
    public void Judge_PromptCarriesOnlyOurVocabulary()
    {
        PlanSample[] batch =
        [
            new("smith", "smith@Dawn.Peace.Fair", true, "Runtime", "forge", "MoveTo → Work",
                string.Empty, string.Empty, -1, 1, 0, 0, 0, 0),
        ];

        string prompt = PlanJudge.Prompt(batch, "RUBRIC LINE");

        Assert.Contains("smith@Dawn.Peace.Fair", prompt, StringComparison.Ordinal);
        Assert.Contains("MoveTo → Work", prompt, StringComparison.Ordinal);
        Assert.Contains("RUBRIC LINE", prompt, StringComparison.Ordinal);

        // 점수 형식을 못 박아야 파싱이 성립한다.
        Assert.Contains("scores", prompt, StringComparison.Ordinal);
    }
}
