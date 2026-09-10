using System.Collections.Immutable;
using System.Text;
using Npc.MasterData.Validation;

namespace Npc.Host.Commands;

/// <summary>
/// <c>Npc.Host hints --out docs/llm/VALIDATION.md</c> (E-04 · E-01).
///
/// <b>문서는 생성물이다.</b> 검증 코드와 힌트를 손으로 두 벌 관리하면 반드시 어긋나고,
/// 어긋난 문서는 없는 것보다 나쁘다 — LLM 이 그것을 근거로 삼는다.
///
/// <c>prompt/</c> 카탈로그가 <c>actions.json</c> 에서 생성되는 것과 같은 원칙이다 (CLAUDE.md §2.4).
/// </summary>
public static class HintsCommand
{
    /// <summary>서브커맨드 이름.</summary>
    public const string Name = "hints";

    /// <summary>사용법.</summary>
    public const string Usage = "사용법: Npc.Host hints [--out <path>]";

    /// <summary>돌린다. 0 = 성공, 2 = 인자 오류.</summary>
    public static int Run(string[] args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        string? outPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length:
                    outPath = args[++i];
                    break;

                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;

                default:
                    output.WriteLine($"모르는 인자: {args[i]}");
                    output.WriteLine(Usage);
                    return 2;
            }
        }

        string markdown = Render();

        if (outPath is null)
        {
            output.Write(markdown);
            return 0;
        }

        if (Path.GetDirectoryName(Path.GetFullPath(outPath)) is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outPath, markdown);
        output.WriteLine($"{outPath} 에 검증 코드 {FixHints.Codes.Count()}건을 썼다.");

        return 0;
    }

    /// <summary>
    /// 사전을 markdown 으로.
    /// <b>실체는 <see cref="FixHintDocument.Render"/> 다</b> — 껍질이 둘이라
    /// 각자 만들면 같은 사전에서 다른 문서가 나온다.
    /// </summary>
    public static string Render() => FixHintDocument.Render();
}
