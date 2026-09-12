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
        Assert.Equal(0, options.FailRate);
        Assert.Equal(0, options.DropRate);
        Assert.Null(options.Scenario);

        // T4-15 — 기본 티어는 none 이다. LLM 을 기본으로 켜면 `dotnet run` 한 번에
        // 외부 크레딧이 나간다. P1 은 이 값이 무의미했다(런타임에 LLM 이 없었다).
        Assert.Equal(TierMode.None, options.Tier);
        Assert.True(options.NoLlm);
        Assert.False(options.UsesT1);
        Assert.False(options.UsesT2);
        Assert.Equal(2, options.T1Workers);
        Assert.Equal(8, options.T2Workers);
        Assert.Null(options.T1Engine);
        Assert.Null(options.T2Engine);

        // A-03 — 프로브·결함 정책의 기본값.
        Assert.Equal(LinkFaultAction.Exit, options.OnLinkFault);
        Assert.Equal(5, options.FaultGraceSeconds);
        Assert.Equal(30, options.LiveStallSeconds);
        Assert.Equal(10, options.ReadyTickStallSeconds);
        Assert.Null(options.HealthPort);
    }

    [Fact]
    public void HealthOptions_Parse()
    {
        HostOptions options = Parse(
            "--on-link-fault", "wait",
            "--fault-grace-s", "12",
            "--live-stall-s", "45",
            "--ready-tick-stall-s", "7",
            "--health-port", "5099");

        Assert.Equal(LinkFaultAction.Wait, options.OnLinkFault);
        Assert.Equal(12, options.FaultGraceSeconds);
        Assert.Equal(45, options.LiveStallSeconds);
        Assert.Equal(7, options.ReadyTickStallSeconds);
        Assert.Equal(5099, options.HealthPort);
    }

    [Fact]
    public void OnLinkFault_RejectsUnknownValue()
    {
        Assert.False(
            HostOptions.TryParse(["--on-link-fault", "restart"], out _, out string? error));

        Assert.Contains("--on-link-fault", error!, StringComparison.Ordinal);
    }

    /// <summary>T4-15 — 티어 축을 인자만으로 가른다 (docs/14 §6).</summary>
    [Theory]
    [InlineData("none", TierMode.None, false, false)]
    [InlineData("t1", TierMode.T1, true, false)]
    [InlineData("t2", TierMode.T2, false, true)]
    [InlineData("all", TierMode.All, true, true)]
    [InlineData("ALL", TierMode.All, true, true)]
    public void Options_SwapTierByArgument(string text, TierMode expected, bool t1, bool t2)
    {
        HostOptions options = Parse("--tier", text);

        Assert.Equal(expected, options.Tier);
        Assert.Equal(t1, options.UsesT1);
        Assert.Equal(t2, options.UsesT2);
        Assert.Equal(expected == TierMode.None, options.NoLlm);
    }

    /// <summary><c>--no-llm</c> 은 <c>--tier none</c> 의 별칭이다. P1 게이트 스크립트가 쓴다.</summary>
    [Fact]
    public void Options_NoLlmIsAliasForTierNone()
    {
        Assert.Equal(TierMode.None, Parse("--tier", "all", "--no-llm").Tier);
        Assert.Equal(TierMode.All, Parse("--no-llm", "--tier", "all").Tier);
    }

    [Fact]
    public void Options_ParseWorkerCountsAndEngines()
    {
        HostOptions options = Parse(
            "--tier", "all",
            "--t1-workers", "4", "--t2-workers", "16",
            "--t1-engine", "llamacpp-qwen3-8b", "--t2-engine", "poe-gemini-2.5-flash-lite");

        Assert.Equal(4, options.T1Workers);
        Assert.Equal(16, options.T2Workers);
        Assert.Equal("llamacpp-qwen3-8b", options.T1Engine);
        Assert.Equal("poe-gemini-2.5-flash-lite", options.T2Engine);
    }

    [Fact]
    public void Options_RejectUnknownTier()
    {
        Assert.False(HostOptions.TryParse(["--tier", "t3"], out _, out string? error));
        Assert.Contains("--tier", error!, StringComparison.Ordinal);

        Assert.False(HostOptions.TryParse(["--t1-workers", "0"], out _, out _));
        Assert.False(HostOptions.TryParse(["--t2-workers", "999"], out _, out _));
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

    /// <summary>
    /// T1-57 완료 조건 — 링크가 인자만으로 갈린다. 전부 <c>Enum.TryParse(ignoreCase)</c> 한 줄을 지나므로
    /// 이름 하나와 대소문자 하나면 된다. null·record·replay 는 <c>LinkSwapTests</c> 가 실제로 갈아끼워 본다.
    /// </summary>
    [Theory]
    [InlineData("tcp", LinkKind.Tcp)]
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

    /// <summary>값 없는 플래그. 모든 옵션이 <c>TryValue</c> 하나를 지나므로 대표 하나면 된다.</summary>
    [Fact]
    public void Options_RejectMissingValue()
    {
        Assert.False(HostOptions.TryParse(["--npcs"], out _, out string? error));
        Assert.NotNull(error);
    }

    // 기제가 다른 것만: int 하한 · double 상한 초과 · double 하한 미만 · enum 파싱 실패.
    [Theory]
    [InlineData("--npcs", "0")]
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

    /// <summary>T6-12 완료 조건 — TCP 옵션 셋이 파싱된다 (docs/20 §10.3).</summary>
    [Fact]
    public void HostOptions_ParsesTcpOptions()
    {
        HostOptions options = Parse(
            "--link", "tcp",
            "--gs-host", "10.0.0.7",
            "--gs-port", "7011",
            "--zone", "town_center, gate_east");

        Assert.Equal(LinkKind.Tcp, options.Link);
        Assert.Equal("10.0.0.7", options.GameServerHost);
        Assert.Equal(7011, options.GameServerPort);
        Assert.Equal(new[] { "town_center", "gate_east" }, options.Zones.ToArray());

        // --link record 가 무엇을 감쌀지 가르는 값이다.
        Assert.True(options.UsesGameServer);
    }

    /// <summary>
    /// <c>--gs-*</c> 를 주지 않으면 기본값이고 <c>UsesGameServer</c> 는 거짓이다.
    ///
    /// <b>기본값과 명시값을 구별하지 못하면</b> "포트가 7010 이니까 TCP 겠지" 라는 추측이 되고,
    /// <c>--link record</c> 가 엉뚱한 것을 감싼다.
    /// </summary>
    [Fact]
    public void HostOptions_TcpDefaultsAreNotMistakenForExplicit()
    {
        HostOptions options = Parse("--link", "loopback");

        Assert.Equal("127.0.0.1", options.GameServerHost);
        Assert.Equal(7010, options.GameServerPort);
        Assert.Empty(options.Zones);
        Assert.False(options.UsesGameServer);
    }
}
