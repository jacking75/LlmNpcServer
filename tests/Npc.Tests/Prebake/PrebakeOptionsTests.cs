using Npc.Prebake;

namespace Npc.Tests.Prebake;

/// <summary>프리베이크 CLI 옵션. docs/13 §4 · T3-08.</summary>
public sealed class PrebakeOptionsTests
{
    private static PrebakeOptions Parse(params string[] args)
    {
        Assert.True(PrebakeOptions.TryParse(args, out PrebakeOptions options, out string? error), error);

        return options;
    }

    /// <summary>T3-08 완료 조건 — docs/13 §4 의 9개 옵션이 전부 있고 <c>--help</c> 가 나온다.</summary>
    [Fact]
    public void Prebake_ParsesEverySpecOption()
    {
        PrebakeOptions options = Parse(
            "--masterdata", "./md",
            "--out", "./store",
            "--tier", "T2",
            "--model", "gpt-5-nano",
            "--concurrency", "12",
            "--dryrun-sample", "1.0",
            "--resume",
            "--only", "blacksmith@*",
            "--budget-usd", "5.00");

        Assert.Equal("./md", options.MasterData);
        Assert.Equal("./store", options.Out);
        Assert.Equal(PrebakeTier.T2, options.Tier);
        Assert.Equal("gpt-5-nano", options.Model);
        Assert.Equal(12, options.Concurrency);
        Assert.Equal(1.0, options.DryRunSample);
        Assert.True(options.Resume);
        Assert.Equal(["blacksmith@*"], options.Only.ToArray());
        Assert.Equal(5.00, options.BudgetUsd);

        // 도움말에 9개 옵션이 다 적혀 있다.
        string usage = PrebakeOptions.Usage;

        foreach (string flag in new[]
        {
            "--masterdata", "--out", "--tier", "--model", "--concurrency",
            "--dryrun-sample", "--resume", "--only", "--budget-usd",
        })
        {
            Assert.Contains(flag, usage, StringComparison.Ordinal);
        }

        Assert.True(Parse("--help").Help);
        Assert.True(Parse("-h").Help);
    }

    /// <summary>기본값. 동시성 8 은 실측 근거가 있는 값이다 (W6_compile_stats §6).</summary>
    [Fact]
    public void Prebake_HasSpecDefaults()
    {
        PrebakeOptions options = Parse();

        Assert.Equal("./masterdata", options.MasterData);
        Assert.Equal("./planstore", options.Out);
        Assert.Equal(PrebakeTier.T2, options.Tier);
        Assert.Null(options.Model);
        Assert.Equal(8, options.Concurrency);
        Assert.Equal(1.0, options.DryRunSample);   // 프리베이크는 전수 드라이런이다
        Assert.False(options.Resume);
        Assert.Empty(options.Only);
        Assert.Equal(5.00, options.BudgetUsd);
        Assert.Equal(string.Empty, options.GeneratedAt);   // 시각은 외부 주입이다
    }

    /// <summary>
    /// T3-08 완료 조건 — <c>--only "blacksmith@*"</c> glob 파싱.
    /// </summary>
    [Fact]
    public void Prebake_ParsesOnlyGlob()
    {
        PrebakeOptions smiths = Parse("--only", "blacksmith@*");

        Assert.True(smiths.IncludesBucket("blacksmith@Dawn.Peace.Fair"));
        Assert.True(smiths.IncludesBucket("blacksmith@Night.Disaster.Storm"));
        Assert.False(smiths.IncludesBucket("farmer@Dawn.Peace.Fair"));

        // 아키타입이 아니라 상황으로 자르기.
        PrebakeOptions dawn = Parse("--only", "*@Dawn.*.*");

        Assert.True(dawn.IncludesBucket("blacksmith@Dawn.Peace.Fair"));
        Assert.True(dawn.IncludesBucket("farmer@Dawn.War.Storm"));
        Assert.False(dawn.IncludesBucket("farmer@Noon.War.Storm"));

        // 여러 번 주면 OR 이다.
        PrebakeOptions two = Parse("--only", "blacksmith@*", "--only", "farmer@*");

        Assert.True(two.IncludesBucket("blacksmith@Dawn.Peace.Fair"));
        Assert.True(two.IncludesBucket("farmer@Dawn.Peace.Fair"));
        Assert.False(two.IncludesBucket("baker@Dawn.Peace.Fair"));

        // 목록이 비어 있으면 전 대상이다.
        Assert.True(Parse().IncludesBucket("baker@Dawn.Peace.Fair"));
    }

    /// <summary>glob 매처의 경계. <c>*</c>·<c>?</c> 만 지원하고 대소문자를 안 가린다.</summary>
    [Fact]
    public void Prebake_GlobMatcherHandlesEdges()
    {
        Assert.True(PrebakeOptions.Matches("*", "blacksmith@Dawn.Peace.Fair"));
        Assert.True(PrebakeOptions.Matches("**", "blacksmith@Dawn.Peace.Fair"));
        Assert.True(PrebakeOptions.Matches("blacksmith@Dawn.Peace.Fair", "blacksmith@Dawn.Peace.Fair"));
        Assert.True(PrebakeOptions.Matches("BLACKSMITH@dawn.*.*", "blacksmith@Dawn.Peace.Fair"));
        Assert.True(PrebakeOptions.Matches("*@*.Peace.*", "blacksmith@Dawn.Peace.Fair"));
        Assert.True(PrebakeOptions.Matches("*Fair", "blacksmith@Dawn.Peace.Fair"));
        Assert.True(PrebakeOptions.Matches("blacksmith@Daw?.Peace.Fair", "blacksmith@Dawn.Peace.Fair"));

        Assert.False(PrebakeOptions.Matches("blacksmith", "blacksmith@Dawn.Peace.Fair"));
        Assert.False(PrebakeOptions.Matches("blacksmith@Daw?.Peace.Fair", "blacksmith@Noon.Peace.Fair"));
        Assert.False(PrebakeOptions.Matches("farmer*", "blacksmith@Dawn.Peace.Fair"));
        Assert.False(PrebakeOptions.Matches(string.Empty, "blacksmith@Dawn.Peace.Fair"));
        Assert.True(PrebakeOptions.Matches(string.Empty, string.Empty));
    }

    /// <summary>T2-19 러너의 옵션 이름을 별칭으로 남긴다 — 예전 회차를 재현할 수 있어야 한다.</summary>
    [Fact]
    public void Prebake_KeepsRunnerAliases()
    {
        Assert.Equal("./store", Parse("--planstore", "./store").Out);
        Assert.Equal("ling", Parse("--engine", "ling").Model);
        Assert.True(Parse("--print-prefix").PrintPrefix);
        Assert.Equal(991, Parse("--stride", "991").Stride);
        Assert.Equal(4, Parse("--archetypes", "4").Archetypes);
        Assert.Equal(60, Parse("--limit", "60").Limit);

        // --out 은 이제 플랜 스토어 폴더다. JSONL 결과는 --report 다.
        Assert.Equal("./x.jsonl", Parse("--report", "./x.jsonl").Report);
    }

    /// <summary>잘못된 인자는 이유를 준다. ConfigurationBuilder 처럼 조용히 무시하지 않는다.</summary>
    [Theory]
    [InlineData("--tier")]
    [InlineData("--tier", "T3")]
    [InlineData("--concurrency", "0")]
    [InlineData("--concurrency", "아홉")]
    [InlineData("--budget-usd", "-1")]
    [InlineData("--dryrun-sample", "1.5")]
    [InlineData("--only")]
    [InlineData("--모르는옵션")]
    [InlineData("--concurrency", "16", "--max-concurrency", "8")]
    public void Prebake_RejectsBadArgs(params string[] args)
    {
        Assert.False(PrebakeOptions.TryParse(args, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    /// <summary>T1 티어도 파싱된다 (로컬 엔진).</summary>
    [Fact]
    public void Prebake_ParsesLocalTier()
    {
        Assert.Equal(PrebakeTier.T1, Parse("--tier", "T1").Tier);
        Assert.Equal(PrebakeTier.T1, Parse("--tier", "t1").Tier);
    }
}
