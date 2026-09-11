using System.Collections.Immutable;
using Npc.Cli;

namespace Npc.Mcp;

/// <summary>명령 하나의 결과 (E-03).</summary>
/// <param name="ExitCode">0 이면 성공. CLI 와 같은 값이다.</param>
/// <param name="Output">표준출력에 나갔을 내용.</param>
public readonly record struct CliResult(int ExitCode, string Output)
{
    /// <summary>성공했는가.</summary>
    public bool Ok => ExitCode == 0;

    /// <summary>
    /// MCP 툴이 돌려줄 문자열. <b>실패해도 예외를 던지지 않는다</b> —
    /// 모델에게는 "무엇이 왜 틀렸나" 가 답이고, 예외는 그 정보를 스택 추적으로 바꿔 버린다.
    /// </summary>
    public override string ToString() =>
        Ok ? Output : $"[exit {ExitCode}]\n{Output}";
}

/// <summary>
/// CLI 명령을 부르는 얇은 껍질 (E-03).
///
/// <para>
/// <b>MCP 툴은 로직을 갖지 않는다.</b> <c>npc validate</c> 와 <c>masterdata_validate</c> 가
/// 다른 답을 내면 그중 하나는 반드시 틀린 답이고, 틀린 쪽을 보는 것은 사람이 아니라 모델이다.
/// 그래서 껍질만 두 개이고 코어는 하나다 (F-01 의 설계 그대로).
/// </para>
/// </summary>
internal static class CliShell
{
    /// <summary>명령 하나를 돌리고 출력을 모은다.</summary>
    /// <param name="options">서버 설정.</param>
    /// <param name="run">명령 본체.</param>
    /// <param name="args">위치 인자.</param>
    /// <param name="json">기계가 읽는 출력인가.</param>
    /// <param name="apply">파일을 실제로 고칠 것인가.</param>
    public static CliResult Run(
        McpOptions options,
        Func<CliContext, int> run,
        IEnumerable<string>? args = null,
        bool json = false,
        bool apply = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(run);

        using var writer = new StringWriter();

        var context = new CliContext
        {
            MasterData = options.MasterData,
            PlanStore = options.PlanStore,
            Out = writer,
            Json = json,
            Apply = apply,
            Args = args is null ? [] : [.. args],
        };

        int code;

        try
        {
            code = run(context);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            // 마스터데이터가 깨져 있거나 인자가 틀린 경우다. 둘 다 모델이 고칠 수 있는 것이라
            // 메시지를 그대로 돌려준다 — 스택 추적은 모델에게 아무것도 알려 주지 않는다.
            return new CliResult(2, writer.ToString() + ex.Message);
        }

        return new CliResult(code, writer.ToString());
    }

    /// <summary>위치 인자 배열. null·빈 문자열은 뺀다.</summary>
    public static ImmutableArray<string> Args(params string?[] values) =>
        [.. values.Where(v => !string.IsNullOrEmpty(v)).Select(v => v!)];
}
