using System.Collections.Immutable;
using Npc.Mcp.Tools;

namespace Npc.Mcp;

/// <summary>
/// 이 서버가 등록하는 툴 타입 목록 (E-03).
///
/// <para>
/// <b><c>Program</c> 에 두지 않는다.</b> 최상위 문 파일의 <c>Program</c> 은 도구마다 하나씩
/// 생기고, 테스트 프로젝트가 도구 여럿을 참조하는 순간 <c>Program</c> 이 모호해진다
/// (실제로 <c>Npc.Conformance</c>·<c>Npc.Eval</c> 과 부딪혔다).
/// </para>
/// </summary>
public static class McpToolSet
{
    /// <summary>호스트에 보이는 서버 이름. <c>.mcp.json</c> 의 키와 같다.</summary>
    public const string ServerName = "npc-server";

    /// <summary>언제나 등록하는 읽기 툴 타입.</summary>
    public static ImmutableArray<Type> ReadToolTypes { get; } =
    [
        typeof(MasterDataTools),
        typeof(PlanTools),
        typeof(ServerTools),
        typeof(DocsTools),
    ];

    /// <summary>
    /// <c>--allow-write</c> 에서만 등록하는 쓰기 툴 타입.
    ///
    /// <b>목록에 뜨지도 않아야 한다</b> — "있는데 거절" 이면 모델이 우회를 시도한다.
    /// </summary>
    public static ImmutableArray<Type> WriteToolTypes { get; } = [typeof(WriteTools)];
}
