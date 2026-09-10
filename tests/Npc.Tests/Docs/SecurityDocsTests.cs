using System.Text.RegularExpressions;

namespace Npc.Tests.Docs;

/// <summary>
/// G-04 라이선스·보안 문서.
///
/// <b>여기서 지키려는 것은 "미실시" 표시다.</b> 법무 확인·침투 시험·리뷰 회의는 사람이 하는
/// 일이고 아직 안 했다. 그 사실이 문서에서 사라지면 **판단이 거짓이 된다** —
/// 다음 사람이 "보안 문서가 있으니 검토가 끝났겠지" 라고 읽는다.
/// </summary>
public sealed partial class SecurityDocsTests
{
    public static TheoryData<string> Documents =>
    [
        "docs/legal/dotllm.md",
        "docs/legal/models.md",
        "docs/security/threat_model.md",
        "docs/security/privacy.md",
        "docs/security/secrets.md",
    ];

    [Theory]
    [MemberData(nameof(Documents))]
    public void Document_Exists(string relative)
    {
        Assert.True(File.Exists(TestPaths.At(relative.Split('/'))), $"{relative} 이 없다");
    }

    /// <summary>
    /// <b>안 한 것은 안 했다고 적혀 있어야 한다.</b> 이 단언이 깨지는 경우는 둘이다 —
    /// 진짜로 했거나(그러면 결과를 적고 이 테스트를 고친다), 표시를 지웠거나.
    /// </summary>
    [Theory]
    [MemberData(nameof(Documents))]
    public void Document_KeepsThePendingMarks(string relative)
    {
        string text = File.ReadAllText(TestPaths.At(relative.Split('/')));

        Assert.True(
            text.Contains("미실시", StringComparison.Ordinal)
            || text.Contains("미확인", StringComparison.Ordinal)
            || text.Contains("미정", StringComparison.Ordinal)
            || text.Contains("미구현", StringComparison.Ordinal),
            $"{relative} 에 미완 표시가 없다. 정말로 전부 끝났다면 결과를 적고 이 테스트를 고친다 — "
            + "표시만 지우면 판단이 거짓이 된다.");
    }

    /// <summary>위협 모델은 자산 → 경계 → 위협 → 잔여 순이어야 쓸모가 있다.</summary>
    [Fact]
    public void ThreatModel_HasEverySection()
    {
        string text = File.ReadAllText(TestPaths.At("docs", "security", "threat_model.md"));

        foreach (string section in new[] { "자산", "신뢰 경계", "위협", "실측", "잔여 위험" })
        {
            Assert.Contains(section, text, StringComparison.Ordinal);
        }

        // 잔여 위험이 비어 있으면 그것이 거짓이다 — 지금 T12~T15 가 미구현이다.
        Assert.Contains("C-08", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>대응이 "구현됨" 이라고 적힌 것은 실제로 있어야 한다.</b>
    /// 없는 방어를 있다고 적으면 위협 모델이 가장 위험한 문서가 된다.
    /// </summary>
    [Fact]
    public void ThreatModel_ClaimedMitigationsExist()
    {
        string text = File.ReadAllText(TestPaths.At("docs", "security", "threat_model.md"));

        // 각 대응의 근거 파일. 하나라도 없으면 문서가 없는 것을 있다고 말한다.
        foreach ((string claim, string[] path) in new (string, string[])[]
        {
            ("HMAC", ["src", "Npc.Wire", "V2", "LinkAuth.cs"]),
            ("TLS", ["src", "Npc.Gateway", "TlsStreamFactory.cs"]),
            ("토큰", ["src", "Npc.Host", "Api", "AdminAuth.cs"]),
            ("감사 로그", ["src", "Npc.Host", "Api", "AuditLog.cs"]),
            ("reasoning", ["src", "Npc.Llm", "ReasoningSanitizer.cs"]),
            ("하드 캡", ["src", "Npc.Planning", "ReplanBudget.cs"]),
            ("derived.lock.json", ["src", "Npc.MasterData", "Authoring", "DerivedArtifacts.cs"]),
        })
        {
            Assert.Contains(claim, text, StringComparison.Ordinal);
            Assert.True(File.Exists(TestPaths.At(path)), $"{string.Join("/", path)} 이 없는데 문서가 그것을 근거로 든다");
        }
    }

    /// <summary>
    /// 시크릿 문서는 <b>실제로 읽히는 환경변수만</b> 적어야 한다. 없는 변수를 적으면
    /// 회전 절차가 아무것도 돌리지 않는다.
    /// </summary>
    [Fact]
    public void Secrets_ListsEveryEnvironmentVariableTheCodeReads()
    {
        string text = File.ReadAllText(TestPaths.At("docs", "security", "secrets.md"));

        foreach (string name in new[] { "NPC_LINK_SECRET", "NPC_LINK_CERT_PASSWORD", "NPC_ADMIN_TOKEN" })
        {
            Assert.Contains(name, text, StringComparison.Ordinal);

            // 코드가 실제로 읽는가. 문서에만 있는 변수는 회전해도 아무 일이 없다.
            Assert.Contains(
                name,
                File.ReadAllText(TestPaths.At("src", "Npc.Host", "HostOptions.cs"))
                + File.ReadAllText(TestPaths.At("src", "Npc.Host", "Program.cs"))
                + File.ReadAllText(TestPaths.At("src", "Npc.Host", "Api", "AdminAuth.cs")),
                StringComparison.Ordinal);
        }

        // 무중단 회전이 없다는 사실을 지우지 않는다.
        Assert.Contains("재기동", text, StringComparison.Ordinal);
    }

    /// <summary>SBOM 스크립트는 도구가 없을 때 <b>조용히 넘어가지 않아야</b> 한다.</summary>
    [Fact]
    public void Sbom_ScriptFailsLoudlyWithoutTheTool()
    {
        string path = TestPaths.At("tools", "sbom.ps1");

        Assert.True(File.Exists(path), "tools/sbom.ps1 이 없다");

        string text = File.ReadAllText(path);

        Assert.Contains("exit 1", text, StringComparison.Ordinal);
        Assert.Contains("dotnet tool install", text, StringComparison.Ordinal);

        // 도구 없이도 도는 부분이 있어야 한다 — 그것이 지금 유일한 실측이다.
        Assert.Contains("--vulnerable", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 개인정보 문서가 <b>실제 저장 위치</b>를 가리켜야 한다. 없는 폴더를 적으면 삭제 절차가 거짓이다.
    /// </summary>
    [Fact]
    public void Privacy_ListsRealStorageLocations()
    {
        string text = File.ReadAllText(TestPaths.At("docs", "security", "privacy.md"));
        string ignore = File.ReadAllText(TestPaths.At(".gitignore"));

        foreach (string folder in new[] { "logs/", "replays/", "state/" })
        {
            Assert.Contains(folder, text, StringComparison.Ordinal);

            // 전부 gitignore 여야 한다 — 저장소에 개인정보가 들어가면 삭제가 불가능해진다.
            Assert.Contains(folder, ignore, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// dotLLM 문서가 <b>코드로 강제되는 조치</b>를 정확히 적어야 한다.
    /// "NuGet 참조를 두지 않는다" 는 실제로 그런지 여기서 확인한다.
    /// </summary>
    [Fact]
    public void DotLlm_NoNuGetReferenceExists()
    {
        foreach (string csproj in Directory.EnumerateFiles(TestPaths.RepoRoot, "*.csproj", SearchOption.AllDirectories))
        {
            if (csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            // 주석에서 "HTTP 로만 부른다" 고 설명하는 것은 허용한다. 참조 Include 만 본다.
            foreach (Match include in Included().Matches(File.ReadAllText(csproj)))
            {
                Assert.DoesNotContain("dotllm", include.Groups[1].Value, StringComparison.OrdinalIgnoreCase);
            }
        }

        // 이미지에도 없어야 한다. 주석에서 "넣지 않는다" 고 말하는 것은 허용 — 명령줄만 본다.
        foreach (string line in File.ReadAllLines(TestPaths.At("deploy", "Dockerfile")))
        {
            string trimmed = line.TrimStart();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            Assert.DoesNotContain("dotllm", trimmed, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary><c>&lt;PackageReference Include="..." /&gt;</c> 의 Include 값.</summary>
    [GeneratedRegex(@"Include\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Included();
}
