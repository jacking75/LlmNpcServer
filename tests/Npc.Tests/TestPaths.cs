using System.Reflection;
using System.Text.Json;
using Npc.Core;

namespace Npc.Tests;

/// <summary>
/// 테스트가 저장소 안의 원본 파일(masterdata, csproj, scenarios)을 찾기 위한 경로 헬퍼.
/// 출력 폴더로 복사하지 않고 소스 폴더를 직접 읽는다 — 마스터데이터는 단일 원천(SSOT)이라
/// 복사본이 생기는 순간 어느 쪽이 진짜인지 알 수 없게 된다.
/// </summary>
public static class TestPaths
{
    private static readonly string s_repoRoot = FindRepoRoot();
    private static readonly int s_archetypeCount = CountArchetypes();

    /// <summary>저장소 루트 (NpcServer.sln 이 있는 폴더).</summary>
    public static string RepoRoot => s_repoRoot;

    /// <summary>masterdata/ 절대 경로.</summary>
    public static string MasterData => Path.Combine(s_repoRoot, "masterdata");

    /// <summary>scenarios/ 절대 경로.</summary>
    public static string Scenarios => Path.Combine(s_repoRoot, "scenarios");

    /// <summary>저장소 루트 기준 상대 경로를 절대 경로로.</summary>
    public static string At(params string[] parts) => Path.Combine([s_repoRoot, .. parts]);

    /// <summary>
    /// 저장소 masterdata 의 아키타입 수 (F-05).
    ///
    /// <b>테스트에 40 을 적지 않는다.</b> 아키타입을 하나 추가하면 파일만 고치고 스위트가
    /// 따라와야 한다 — 그것이 F-05 가 없애려는 결손이고, 상수를 코드에서 뺀 뒤 테스트에
    /// 다시 적으면 같은 결손을 테스트 쪽에 옮겨 둔 것뿐이다.
    ///
    /// <para>
    /// <c>archetypes.json</c> 의 배열 길이만 읽는다 — 로더 전체를 돌릴 이유가 없다.
    /// 자기 픽스처를 쓰는 테스트는 이것 대신 그 <c>MasterDataSet.Buckets</c> 를 본다.
    /// </para>
    /// </summary>
    public static int ArchetypeCount => s_archetypeCount;

    /// <summary>저장소 masterdata 가 함의하는 전체 버킷 키 수.</summary>
    public static int TotalKeys => s_archetypeCount * BucketKey.PerArchetype;

    private static int CountArchetypes()
    {
        using FileStream stream = File.OpenRead(Path.Combine(MasterData, "archetypes.json"));
        using JsonDocument document = JsonDocument.Parse(stream);

        return document.RootElement.GetProperty("archetypes").GetArrayLength();
    }

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
