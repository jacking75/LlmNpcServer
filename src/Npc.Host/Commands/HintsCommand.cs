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

    /// <summary>사전을 markdown 으로. <b>시각도 난수도 섞지 않는다</b> — 같은 입력이면 바이트 동일이다.</summary>
    public static string Render()
    {
        var sb = new StringBuilder(16 * 1024);

        sb.AppendLine("# 검증 오류 사전");
        sb.AppendLine();
        sb.AppendLine("> **이 파일은 생성물이다.** 손으로 고치지 않는다 —");
        sb.AppendLine("> `dotnet run --project src/Npc.Host -- hints --out docs/llm/VALIDATION.md` 가 다시 만든다.");
        sb.AppendLine("> 원천은 `src/Npc.MasterData/Validation/FixHints.cs` 다.");
        sb.AppendLine();
        sb.AppendLine("검증이 실패하면 코드가 나온다. 그 코드로 여기를 찾아 **무엇을 하면 되는지**를 읽는다.");
        sb.AppendLine("`validate --format json` 은 같은 힌트를 `fix_hint` 필드에 실어 준다.");
        sb.AppendLine();

        AppendSection(sb, "마스터데이터 (V0~V15)", code => code.Length <= 3 && code[0] == 'V');
        AppendSection(sb, "플랜 검증 4단 (V1.~V4.)", code => code.Contains('.', StringComparison.Ordinal));
        AppendSection(sb, "핸드셰이크 거절", code => code[0] != 'V');

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string title, Func<string, bool> filter)
    {
        ImmutableArray<string> codes =
        [
            .. FixHints.Codes.Where(filter).OrderBy(c => c, StringComparer.Ordinal),
        ];

        if (codes.Length == 0)
        {
            return;
        }

        sb.Append("## ").AppendLine(title);
        sb.AppendLine();
        sb.AppendLine("| 코드 | 무엇을 하면 되는가 | 근거 |");
        sb.AppendLine("|---|---|---|");

        foreach (string code in codes)
        {
            FixHint hint = FixHints.For(code)!.Value;

            string related = hint.Related.IsEmpty
                ? "—"
                : string.Join(" · ", hint.Related.Select(r => $"`{r}`"));

            sb.Append("| `").Append(code).Append("` | ")
              .Append(hint.Hint.Replace("|", "\\|", StringComparison.Ordinal))
              .Append(" | ").Append(related).AppendLine(" |");
        }

        sb.AppendLine();
    }
}
