using Npc.Host;

namespace Npc.Tests.Host;

/// <summary>README §주요 실행 옵션 · docs/11 §11. 호스트 인자 파싱.</summary>
public sealed class HostOptionsTests
{
    private static HostOptions Parse(params string[] args)
    {
        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return options;
    }

    [Fact]
    public void Options_HaveDocumentedDefaults()
    {
        HostOptions options = Parse();

        Assert.Equal(LinkKind.Loopback, options.Link);
        Assert.Equal(500, options.Npcs);
        Assert.Equal(60, options.TimeScale);
        Assert.Equal(1, options.Days);
        Assert.Equal(HostOptions.DefaultPort, options.Port);
        Assert.False(options.NoLlm);
        Assert.Equal(0, options.FailRate);
        Assert.Equal(0, options.DropRate);
        Assert.Null(options.Scenario);
    }

    /// <summary>README 의 예제 명령이 그대로 파싱돼야 한다.</summary>
    [Fact]
    public void Options_ParseReadmeSmokeCommand()
    {
        HostOptions options = Parse("--loopback", "--npcs", "500", "--time-scale", "600", "--days", "7", "--no-llm");

        Assert.Equal(LinkKind.Loopback, options.Link);
        Assert.Equal(500, options.Npcs);
        Assert.Equal(600, options.TimeScale);
        Assert.Equal(7, options.Days);
        Assert.True(options.NoLlm);
    }

    /// <summary>T1-57 완료 조건 — 링크 3종이 인자만으로 갈린다.</summary>
    [Theory]
    [InlineData("null", LinkKind.Null)]
    [InlineData("record", LinkKind.Record)]
    [InlineData("replay", LinkKind.Replay)]
    [InlineData("loopback", LinkKind.Loopback)]
    [InlineData("NULL", LinkKind.Null)]
    public void Options_SwapLinkByArgument(string text, LinkKind expected)
    {
        HostOptions options = Parse("--link", text, "--trace", "trace.jsonl");

        Assert.Equal(expected, options.Link);
    }

    [Fact]
    public void Options_ParseScenarioAndFaultRates()
    {
        HostOptions options = Parse(
            "--scenario", "./scenarios/siege.jsonl", "--fail-rate", "0.1", "--drop-rate", "0.05");

        Assert.Equal("./scenarios/siege.jsonl", options.Scenario);
        Assert.Equal(0.1, options.FailRate, 6);
        Assert.Equal(0.05, options.DropRate, 6);
    }

    [Theory]
    [InlineData("--npcs")]
    [InlineData("--time-scale")]
    [InlineData("--days")]
    [InlineData("--link")]
    [InlineData("--scenario")]
    public void Options_RejectMissingValue(string flag)
    {
        Assert.False(HostOptions.TryParse([flag], out _, out string? error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("--npcs", "0")]
    [InlineData("--time-scale", "0")]
    [InlineData("--fail-rate", "1.5")]
    [InlineData("--drop-rate", "-0.1")]
    [InlineData("--link", "carrier-pigeon")]
    public void Options_RejectOutOfRange(string flag, string value)
    {
        Assert.False(HostOptions.TryParse([flag, value], out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Options_RejectUnknownArgument()
    {
        Assert.False(HostOptions.TryParse(["--turbo"], out _, out string? error));
        Assert.Contains("--turbo", error!, StringComparison.Ordinal);
    }

    /// <summary>재생은 읽을 파일이 있어야 한다.</summary>
    [Fact]
    public void Options_RejectReplayWithoutTrace()
    {
        Assert.False(HostOptions.TryParse(["--link", "replay"], out _, out string? error));
        Assert.Contains("--trace", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_HelpStopsParsing()
    {
        Assert.True(HostOptions.TryParse(["--help"], out HostOptions options, out _));
        Assert.True(options.Help);
        Assert.Contains("--link", HostOptions.Usage, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>dotnet run --project src/Npc.Host</c> 는 작업 폴더를 프로젝트 폴더로 잡는다.
    /// README 예제가 그대로 돌아야 하므로 기본값이 저장소 루트를 찾아 올라간다.
    /// </summary>
    [Fact]
    public void Options_ResolveMasterDataFromAnyWorkingDirectory()
    {
        string resolved = new HostOptions().ResolveMasterData();

        Assert.True(Directory.Exists(resolved), resolved);
        Assert.True(File.Exists(Path.Combine(resolved, "archetypes.json")), resolved);
    }
}
