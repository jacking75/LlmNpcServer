using System.Text.RegularExpressions;
using Npc.Host;
using Npc.Host.Config;

namespace Npc.Tests.Deploy;

/// <summary>
/// A-09 — 배포 산출물이 코드와 어긋나지 않는다.
///
/// <b>compose·k8s 의 환경변수는 옵션 표에서 도출된 이름이다</b> (A-04). 옵션을 지우거나
/// 이름을 바꾸면 컨테이너가 <c>모르는 인자다</c> 로 기동 실패하는데, 그것은 배포해 봐야
/// 알 수 있다. 여기서 먼저 깨뜨린다.
/// </summary>
public sealed class DeployArtifactTests
{
    private static readonly string s_root = FindRepoRoot();

    public static TheoryData<string> RequiredFiles =>
    [
        "deploy/Dockerfile",
        "deploy/Dockerfile.testgameserver",
        "deploy/healthcheck.sh",
        "deploy/compose.yaml",
        "deploy/prometheus.yml",
        "deploy/grafana/npc-server.json",
        "deploy/k8s/deployment.yaml",
        "deploy/k8s/secrets.example.yaml",
        ".dockerignore",
        "Directory.Packages.props",
    ];

    [Theory]
    [MemberData(nameof(RequiredFiles))]
    public void DeployFile_Exists(string relative)
    {
        Assert.True(File.Exists(Path.Combine(s_root, relative)), $"{relative} 이 없다");
    }

    [Theory]
    [InlineData("deploy/compose.yaml")]
    [InlineData("deploy/k8s/deployment.yaml")]
    public void EnvironmentNames_MapToRealOptions(string relative)
    {
        string text = File.ReadAllText(Path.Combine(s_root, relative));

        var known = HostOptionsSource.Options
            .Select(o => HostOptionsSource.EnvNameOf(o.Name))
            .ToHashSet(StringComparer.Ordinal);

        // 옵션이 아닌 NPC_ 변수도 있다 — 시크릿·프로파일 이름이 그렇다.
        var exempt = new HashSet<string>(StringComparer.Ordinal)
        {
            "NPC_ADMIN_TOKEN",
            "NPC_LINK_SECRET",
            "NPC_VERSION",
            "NPC_REVISION",
            "NPC_HEALTH_PORT_FOR_HEALTHCHECK",
        };

        foreach (Match match in Regex.Matches(text, @"\bNPC_[A-Z0-9_]+\b"))
        {
            string name = match.Value;

            Assert.True(
                known.Contains(name) || exempt.Contains(name),
                $"{relative} 의 {name} 은 옵션 표에 없다. "
                + "옵션을 지웠거나 이름이 바뀌었다 — 컨테이너가 기동 실패한다.");
        }
    }

    [Fact]
    public void Dockerfile_DoesNotBakeGeneratedPlans()
    {
        // planstore/plans/ 는 생성물이라 커밋도 이미지 포함도 하지 않는다 (CLAUDE.md §6).
        // pinned/ 는 사람이 검수한 것이라 반드시 들어간다.
        string dockerfile = File.ReadAllText(Path.Combine(s_root, "deploy/Dockerfile"));

        Assert.Contains("planstore/pinned/", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("COPY planstore/plans/", dockerfile, StringComparison.Ordinal);

        // dotLLM 은 GPLv3 경계다. 이미지에 넣지 않는다 (CLAUDE.md §2.7).
        Assert.DoesNotContain("tools/dotllm", dockerfile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dockerignore_ExcludesGeneratedAndSecretPaths()
    {
        string text = File.ReadAllText(Path.Combine(s_root, ".dockerignore"));

        foreach (string path in new[] { "planstore/plans/", "state/", "models/", "tools/dotllm/", ".git/" })
        {
            Assert.Contains(path, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildScript_IsThePipeline()
    {
        string build = File.ReadAllText(Path.Combine(s_root, "build.ps1"));

        // 워크플로 파일은 두지 않는다 (2026-09-10 결정). 제공자를 고르지 않았고,
        // 고르지 않은 채 .github/workflows/*.yml 을 두면 "CI 가 있다" 는 거짓 신호가 된다.
        // 그래서 파이프라인의 내용은 build.ps1 하나가 정의한다 — 어느 CI 든 이것을 부른다.
        Assert.False(
            Directory.Exists(Path.Combine(s_root, ".github")),
            ".github/ 이 생겼다. 워크플로 파일을 두지 않기로 했다 — "
            + "파이프라인은 build.ps1 · npc validate · npc regen --check 세 명령이다.");

        // 로컬과 CI 가 같은 것을 돌아야 "로컬은 되는데 CI 는 깨진다" 가 환경 차이로 좁혀진다.
        Assert.Contains("Category!=Golden&Category!=Gate&Category!=Load", build, StringComparison.Ordinal);
        Assert.Contains("--verify-no-changes", build, StringComparison.Ordinal);
    }

    [Fact]
    public void CentralPackages_LeaveNoVersionInProjects()
    {
        // Version 을 csproj 에 남기면 NU1008 로 빌드가 깨지지만, 그 전에 여기서 알려 준다.
        foreach (string csproj in Directory.EnumerateFiles(s_root, "*.csproj", SearchOption.AllDirectories))
        {
            string[] parts = csproj.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (parts.Contains("bin") || parts.Contains("obj"))
            {
                continue;
            }

            string text = File.ReadAllText(csproj);

            Assert.DoesNotMatch(
                new Regex("<PackageReference[^>]*\\sVersion=\""),
                text);
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NpcServer.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("저장소 루트를 찾지 못했다 (NpcServer.sln).");
    }
}
