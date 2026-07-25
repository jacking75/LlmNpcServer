using System.Reflection;

namespace Npc.Tests;

/// <summary>
/// 테스트가 저장소 안의 원본 파일(masterdata, csproj, scenarios)을 찾기 위한 경로 헬퍼.
/// 출력 폴더로 복사하지 않고 소스 폴더를 직접 읽는다 — 마스터데이터는 단일 원천(SSOT)이라
/// 복사본이 생기는 순간 어느 쪽이 진짜인지 알 수 없게 된다.
/// </summary>
public static class TestPaths
{
    private static readonly string s_repoRoot = FindRepoRoot();

    /// <summary>저장소 루트 (NpcServer.sln 이 있는 폴더).</summary>
    public static string RepoRoot => s_repoRoot;

    /// <summary>masterdata/ 절대 경로.</summary>
    public static string MasterData => Path.Combine(s_repoRoot, "masterdata");

    /// <summary>scenarios/ 절대 경로.</summary>
    public static string Scenarios => Path.Combine(s_repoRoot, "scenarios");

    /// <summary>저장소 루트 기준 상대 경로를 절대 경로로.</summary>
    public static string At(params string[] parts) => Path.Combine([s_repoRoot, .. parts]);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "NpcServer.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("NpcServer.sln 을 찾지 못했다. 테스트가 저장소 밖에서 실행되고 있다.");
    }
}
