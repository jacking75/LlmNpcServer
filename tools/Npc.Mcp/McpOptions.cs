using System.Collections.Immutable;

namespace Npc.Mcp;

/// <summary>
/// MCP 서버 설정 (E-03).
///
/// <para>
/// <b>쓰기는 기본이 꺼짐이다.</b> LLM 호스트가 이 서버를 붙이는 것만으로 저장소 파일이
/// 바뀔 수 있으면 아무도 붙이지 않는다 — <c>--allow-write</c> 를 명시해야 쓰기 툴이 등록된다.
/// </para>
/// </summary>
public sealed record McpOptions
{
    /// <summary>기본 <c>masterdata/</c> 경로.</summary>
    public string MasterData { get; init; } = "./masterdata";

    /// <summary>기본 <c>planstore/</c> 경로.</summary>
    public string PlanStore { get; init; } = "./planstore";

    /// <summary>저장소 루트. 문서 검색·리소스가 여기 기준이다.</summary>
    public string Root { get; init; } = ".";

    /// <summary>
    /// 쓰기 툴을 등록하는가. <b>기본은 false</b>.
    ///
    /// <para>
    /// 끄면 <c>masterdata_apply</c>·<c>server_admin</c>·<c>prebake_run</c> 이 <b>목록에 뜨지 않는다</b> —
    /// "부르면 거절" 이 아니라 "없다" 여야 한다. 있는데 거절하면 모델이 우회를 시도한다.
    /// </para>
    /// </summary>
    public bool AllowWrite { get; init; }

    /// <summary>
    /// <c>prebake_run</c> 이 한 번에 쓸 수 있는 상한(USD). <b>코드가 강제한다</b> —
    /// 인자로 더 큰 값을 줘도 여기서 잘린다 (CLAUDE.md §2.7).
    /// </summary>
    public const double PrebakeBudgetCapUsd = 1.0;

    /// <summary>사용법.</summary>
    public const string Usage = """
        Npc.Mcp — LLM 호스트가 이 저장소의 도구를 부르는 MCP 서버 (E-03)

        사용법: Npc.Mcp [옵션]

        옵션
          --masterdata <dir>   기본 ./masterdata
          --planstore <dir>    기본 ./planstore
          --root <dir>         저장소 루트. 문서 검색·리소스 기준 (기본 .)
          --allow-write        쓰기 툴을 등록한다. 기본은 읽기 전용이다
          --help               이 도움말

        전송은 stdio 다. 표준출력이 프로토콜이므로 로그는 전부 표준오류로 나간다.
        """;

    /// <summary>인자를 읽는다. 잘못된 값이면 false 이고 <paramref name="error"/> 가 이유다.</summary>
    /// <param name="args">명령행 인자.</param>
    /// <param name="options">읽은 설정.</param>
    /// <param name="error">실패 이유.</param>
    public static bool TryParse(ImmutableArray<string> args, out McpOptions options, out string? error)
    {
        var result = new McpOptions();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "--masterdata":
                    if (!TryValue(args, ref i, arg, out string? masterData, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { MasterData = masterData! };
                    break;

                case "--planstore":
                    if (!TryValue(args, ref i, arg, out string? planStore, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { PlanStore = planStore! };
                    break;

                case "--root":
                    if (!TryValue(args, ref i, arg, out string? root, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Root = root! };
                    break;

                case "--allow-write":
                    result = result with { AllowWrite = true };
                    break;

                default:
                    error = $"모르는 옵션이다: {arg}";
                    options = result;
                    return false;
            }
        }

        options = result;
        return true;
    }

    private static bool TryValue(
        ImmutableArray<string> args, ref int i, string arg, out string? value, out string? error)
    {
        if (i + 1 >= args.Length)
        {
            value = null;
            error = $"{arg} 에 값이 없다.";
            return false;
        }

        value = args[++i];
        error = null;
        return true;
    }
}
