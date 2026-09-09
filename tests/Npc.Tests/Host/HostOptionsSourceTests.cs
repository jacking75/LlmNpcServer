using Npc.Host;
using Npc.Host.Config;

namespace Npc.Tests.Host;

/// <summary>A-04 — 설정 소스 통합. PRODUCTION_ROADMAP §4 A-04.</summary>
public sealed class HostOptionsSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "npc-config-" + Guid.NewGuid().ToString("N"));

    public HostOptionsSourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void EnvName_FollowsRule()
    {
        Assert.Equal("NPC_GS_HOST", HostOptionsSource.EnvNameOf("--gs-host"));
        Assert.Equal("NPC_LINK", HostOptionsSource.EnvNameOf("--link"));
        Assert.Equal("NPC_READY_TICK_STALL_S", HostOptionsSource.EnvNameOf("--ready-tick-stall-s"));
    }

    [Fact]
    public void EveryDocumentedOption_IsInTheTable()
    {
        // 옵션 표와 사용법이 갈리면 "도움말에는 있는데 환경변수로는 안 되는" 옵션이 생긴다.
        // 사용법 문자열이 사실의 출처다.
        var documented = new HashSet<string>(StringComparer.Ordinal);

        foreach (string line in HostOptions.Usage.Split('\n'))
        {
            string trimmed = line.TrimStart();

            if (!trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string name = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];

            // "--link null|record|..." 처럼 값 목록이 이름에 붙은 줄은 이름만 뗀다.
            documented.Add(name.Split('|')[0]);
        }

        var table = HostOptionsSource.Options.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(documented.Except(table));
    }

    [Fact]
    public void Env_OverridesFile_AndCliOverridesEnv()
    {
        string config = Path.Combine(_dir, HostOptionsSource.DefaultFileName);

        File.WriteAllText(config, """{ "npcs": 100, "time-scale": 10, "seed": 7 }""");

        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["NPC_NPCS"] = "200",
            ["NPC_TIME_SCALE"] = "20",
            ["NPC_CONFIG"] = config,
        };

        Assert.True(
            HostOptions.TryParseLayered(["--npcs", "300"], env, out HostOptions options, out string? error),
            error);

        Assert.Equal(300, options.Npcs);   // CLI 가 가장 세다
        Assert.Equal(20, options.TimeScale);   // 환경변수가 파일을 덮는다
        Assert.Equal(7, options.Seed);   // 파일만 정한 값
        Assert.Equal(config, options.LoadedConfigPath);
    }

    [Fact]
    public void File_CannotSetItsOwnPath()
    {
        string config = Path.Combine(_dir, "cycle.json");

        File.WriteAllText(config, """{ "config": "other.json" }""");

        Assert.False(
            HostOptions.TryParseLayered(
                ["--config", config], new Dictionary<string, string?>(), out _, out string? error));

        Assert.Contains("자기 경로", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void File_RejectsUnknownKey()
    {
        string config = Path.Combine(_dir, "bad.json");

        File.WriteAllText(config, """{ "npc-count": 100 }""");

        Assert.False(
            HostOptions.TryParseLayered(["--config", config], new Dictionary<string, string?>(), out _, out string? error));

        Assert.Contains("npc-count", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void File_ProfileSection_OverridesTopLevel()
    {
        string config = Path.Combine(_dir, "profiles.json");

        File.WriteAllText(config, """
            {
              "npcs": 100,
              "profiles": {
                "service": { "npcs": 5000, "player-bots": 0 }
              }
            }
            """);

        Assert.True(
            HostOptions.TryParseLayered(
                ["--config", config, "--profile", "service"],
                new Dictionary<string, string?>(),
                out HostOptions options,
                out string? error),
            error);

        Assert.Equal(5000, options.Npcs);
        Assert.Equal(0, options.PlayerBots);

        // 서비스 프로파일은 무제한 실행을 강제한다.
        Assert.Equal(0, options.Days);
    }

    [Fact]
    public void ServiceProfile_ForcesUnlimitedDays()
    {
        Assert.True(HostOptions.TryParse(["--profile", "service", "--days", "3"], out HostOptions o, out _));

        // --days 를 명시해도 프로파일이 이긴다. 운영 프로세스가 조용히 exit 0 하는 함정을 막는 것이
        // 프로파일의 존재 이유다.
        Assert.Equal(0, o.Days);
        Assert.Equal(HostProfile.Service, o.Profile);
    }

    [Fact]
    public void Switches_ReadTruthyEnvValues()
    {
        var on = HostOptionsSource.FromEnv(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["NPC_NO_DASHBOARD"] = "true" });

        Assert.Contains("--no-dashboard", on);

        var off = HostOptionsSource.FromEnv(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["NPC_NO_DASHBOARD"] = "false" });

        Assert.DoesNotContain("--no-dashboard", off);
    }

    [Fact]
    public void WildcardBind_RequiresAdminToken()
    {
        Assert.True(HostOptions.TryParse(["--bind", "0.0.0.0"], out HostOptions options, out _));

        Assert.False(options.TryValidateBind(adminToken: null, out string? error));
        Assert.Contains("NPC_ADMIN_TOKEN", error!, StringComparison.Ordinal);

        Assert.True(options.TryValidateBind("secret", out _));
    }

    [Fact]
    public void LoopbackBind_NeedsNoToken()
    {
        Assert.True(HostOptions.TryParse([], out HostOptions options, out _));

        Assert.Equal("127.0.0.1", options.Bind);
        Assert.True(options.TryValidateBind(adminToken: null, out _));
    }

    [Fact]
    public void MissingExplicitConfig_IsAnError()
    {
        Assert.False(
            HostOptions.TryParseLayered(
                ["--config", Path.Combine(_dir, "none.json")],
                new Dictionary<string, string?>(),
                out _,
                out string? error));

        Assert.Contains("설정 파일", error!, StringComparison.Ordinal);
    }
}
