using System.Diagnostics;
using Npc.TestGameServer;

namespace Npc.Tests.TestBed;

/// <summary>T6-14 — 게임서버 대역 인자 파싱. docs/20 §7.5.</summary>
public sealed class GameServerOptionsTests
{
    /// <summary>docs/20 §7.5 의 전 옵션. 하나라도 빠지면 여기가 깨진다.</summary>
    private static readonly string[] DocumentedOptions =
    [
        "--link-port", "--client-port", "--npcs", "--zone", "--time-scale", "--masterdata",
        "--scenario", "--fail-rate", "--drop-rate", "--bots", "--seed", "--player-speed",
        "--max-clients", "--headless", "--help",
    ];

    private static GameServerOptions Parse(params string[] args)
    {
        Assert.True(GameServerOptions.TryParse(args, out GameServerOptions options, out string? error), error);

        return options;
    }

    [Fact]
    public void GameServerOptions_HaveDocumentedDefaults()
    {
        GameServerOptions options = Parse();

        Assert.Equal(GameServerOptions.DefaultLinkPort, options.LinkPort);
        Assert.Equal(GameServerOptions.DefaultClientPort, options.ClientPort);
        Assert.Equal(300, options.Npcs);
        Assert.Empty(options.Zones);
        Assert.Equal(60, options.TimeScale);
        Assert.Equal("./masterdata", options.MasterData);
        Assert.Null(options.Scenario);
        Assert.Equal(0, options.FailRate);
        Assert.Equal(0, options.DropRate);
        Assert.Equal(0, options.Bots);
        Assert.Equal(20260725, options.Seed);
        Assert.Equal(2.5, options.PlayerSpeed);
        Assert.Equal(4, options.MaxClients);
        Assert.False(options.Headless);
        Assert.False(options.Help);
    }

    /// <summary>완료 조건 — §7.5 의 전 옵션이 파싱된다.</summary>
    [Fact]
    public void GameServerOptions_ParsesAll()
    {
        GameServerOptions options = Parse(
            "--link-port", "17010",
            "--client-port", "17020",
            "--npcs", "500",
            "--zone", "town_center, gate_east",
            "--time-scale", "600",
            "--masterdata", "./masterdata",
            "--scenario", "testbed/scenarios/demo_siege.jsonl",
            "--fail-rate", "0.25",
            "--drop-rate", "0.3",
            "--bots", "12",
            "--seed", "1234",
            "--player-speed", "4.5",
            "--max-clients", "8",
            "--headless");

        Assert.Equal(17010, options.LinkPort);
        Assert.Equal(17020, options.ClientPort);
        Assert.Equal(500, options.Npcs);
        Assert.Equal<string[]>(["town_center", "gate_east"], [.. options.Zones]);
        Assert.Equal(600, options.TimeScale);
        Assert.Equal("testbed/scenarios/demo_siege.jsonl", options.Scenario);
        Assert.Equal(0.25, options.FailRate);
        Assert.Equal(0.3, options.DropRate);
        Assert.Equal(12, options.Bots);
        Assert.Equal(1234, options.Seed);
        Assert.Equal(4.5, options.PlayerSpeed);
        Assert.Equal(8, options.MaxClients);
        Assert.True(options.Headless);
    }

    /// <summary>
    /// 포트 0 을 받아야 한다. 테스트는 포트 0(자동 할당)으로 연다 (docs/20 §13) —
    /// 고정 포트를 쓰면 데모를 띄워 둔 채 테스트를 돌릴 때 깨진다.
    /// </summary>
    [Fact]
    public void GameServerOptions_AllowsEphemeralPorts()
    {
        GameServerOptions options = Parse("--link-port", "0", "--client-port", "0");

        Assert.Equal(0, options.LinkPort);
        Assert.Equal(0, options.ClientPort);
    }

    [Fact]
    public void GameServerOptions_RejectsUnknownArg()
    {
        Assert.False(GameServerOptions.TryParse(["--turbo"], out _, out string? error));
        Assert.Contains("--turbo", error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--npcs", "0")]
    [InlineData("--npcs", "abc")]
    [InlineData("--time-scale", "0")]
    [InlineData("--fail-rate", "1.5")]
    [InlineData("--drop-rate", "-0.1")]
    [InlineData("--player-speed", "0")]
    [InlineData("--link-port", "70000")]
    public void GameServerOptions_RejectsOutOfRange(string name, string value)
    {
        Assert.False(GameServerOptions.TryParse([name, value], out _, out string? error));
        Assert.Contains(name, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void GameServerOptions_RejectsMissingValue()
    {
        Assert.False(GameServerOptions.TryParse(["--npcs"], out _, out string? error));
        Assert.NotNull(error);
    }

    /// <summary>사용법에 §7.5 의 옵션이 전부 적혀 있어야 한다.</summary>
    [Fact]
    public void GameServerOptions_UsageListsEveryOption()
    {
        foreach (string option in DocumentedOptions)
        {
            Assert.Contains(option, GameServerOptions.Usage, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 마스터데이터 기본값은 작업 폴더가 어디든 저장소 루트를 찾아 올라간다.
    /// <c>dotnet run --project testbed/Npc.TestGameServer</c> 가 docs/20 §12 그대로 돌아야 한다.
    /// </summary>
    [Fact]
    public void GameServerOptions_ResolveMasterDataFromAnyWorkingDirectory()
    {
        string resolved = new GameServerOptions().ResolveMasterData();

        Assert.True(Directory.Exists(resolved), resolved);
        Assert.True(File.Exists(Path.Combine(resolved, "archetypes.json")), resolved);
    }

    /// <summary>완료 조건 — <c>--help</c> 가 사용법을 찍고 0 으로 끝난다. 실제로 프로세스를 띄워 본다.</summary>
    [Fact]
    public void GameServerOptions_HelpExitsZero()
    {
        (int exitCode, string stdout) = RunGameServer("--help");

        Assert.Equal(0, exitCode);

        // 한국어 부분은 단언하지 않는다. 리다이렉트된 자식 프로세스는 콘솔 기본 코드페이지로
        // 인코딩하므로(이 기계는 cp949) 부모가 UTF-8 로 읽으면 한글만 깨진다 —
        // 사람이 콘솔에서 직접 보는 경로는 멀쩡하다. 여기서 볼 것은 "사용법이 나왔는가" 다.
        Assert.Contains("--link-port", stdout, StringComparison.Ordinal);
        Assert.Contains("--headless", stdout, StringComparison.Ordinal);
    }

    /// <summary>모르는 인자는 사용법과 함께 0 이 아닌 코드로 끝난다.</summary>
    [Fact]
    public void GameServerOptions_UnknownArgExitsNonZero()
    {
        (int exitCode, _) = RunGameServer("--turbo");

        Assert.NotEqual(0, exitCode);
    }

    /// <summary>
    /// 빌드된 대역 실행 파일을 <c>dotnet &lt;dll&gt;</c> 로 띄운다.
    ///
    /// 테스트 프로젝트가 이 프로젝트를 참조하므로 dll 은 테스트 출력 폴더에 같이 있다.
    /// 콘솔은 UTF-8 로 읽는다 — 사용법이 한국어라 기본 코드페이지로 읽으면 깨진다.
    /// </summary>
    private static (int ExitCode, string Stdout) RunGameServer(params string[] args)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "Npc.TestGameServer.dll");

        Assert.True(File.Exists(dll), dll);

        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
        };

        info.ArgumentList.Add(dll);

        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("dotnet 프로세스를 띄우지 못했다.");

        string stdout = process.StandardOutput.ReadToEnd();
        _ = process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(30_000), "게임서버 대역이 30초 안에 끝나지 않았다.");

        return (process.ExitCode, stdout);
    }
}
