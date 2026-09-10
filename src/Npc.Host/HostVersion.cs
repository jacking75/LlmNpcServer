using System.Reflection;

namespace Npc.Host;

/// <summary>
/// 릴리스 버전 (A-09).
///
/// <b>버전은 빌드가 정한다.</b> <c>Directory.Build.props</c> 의 <c>VersionPrefix</c> 를
/// CI 가 태그에서 덮어쓰고(<c>-p:VersionPrefix=1.2.3</c>), 커밋 해시는
/// <c>SourceRevisionId</c> 로 들어와 정보 버전의 <c>+</c> 뒤에 붙는다.
///
/// 여기에 상수를 손으로 적지 않는다 — 적는 순간 태그와 어긋나는 날이 온다.
/// </summary>
public static class HostVersion
{
    /// <summary>
    /// 어셈블리 정보 버전. 없으면 <c>0.0.0</c>.
    /// </summary>
    public static string Assembly { get; } =
        typeof(HostVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";
}
