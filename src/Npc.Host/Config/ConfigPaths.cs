namespace Npc.Host.Config;

/// <summary>
/// 설정 파일 탐색 기준을 한 곳으로 모은다 (A-04).
///
/// <b>기준이 파일마다 달랐다.</b> <c>appsettings.Llm.json</c> 은 작업 폴더에서 위로 올라가며
/// 찾고, <c>appsettings.json</c> 은 실행 파일 폴더만 봤다. 같은 빌드가 실행 방식에 따라
/// 다른 파일을 읽는데 로그에는 무엇을 읽었는지 남지 않아, "설정이 안 먹는다" 를 재현하려면
/// 두 폴더를 손으로 뒤져야 했다.
///
/// 규칙은 하나다 — <b>실행 파일 폴더에서 위로, 그다음 작업 폴더에서 위로.</b>
/// 실행 파일 폴더를 먼저 보는 이유는 배포 산출물이 자기 옆의 설정을 이겨야 하기 때문이다.
/// </summary>
public static class ConfigPaths
{
    /// <summary>
    /// 설정 파일을 찾는다. 못 찾으면 null.
    /// </summary>
    /// <param name="fileName">파일 이름.</param>
    /// <param name="extraRoots">먼저 볼 폴더. 명시 경로가 있을 때 쓴다.</param>
    public static string? Resolve(string fileName, params string[] extraRoots)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(extraRoots);

        foreach (string root in Roots(extraRoots))
        {
            for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, fileName);

                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> Roots(string[] extraRoots)
    {
        foreach (string root in extraRoots)
        {
            if (!string.IsNullOrEmpty(root))
            {
                yield return root;
            }
        }

        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }
}
