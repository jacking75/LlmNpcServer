using System.Collections.Immutable;
using System.Text;

namespace Npc.MasterData.Validation;

/// <summary>
/// 검증 오류 사전을 markdown 으로 (E-04).
///
/// <b>문서는 생성물이다.</b> 검증 코드와 힌트를 손으로 두 벌 관리하면 반드시 어긋나고,
/// 어긋난 문서는 없는 것보다 나쁘다 — LLM 이 그것을 근거로 삼는다.
/// <c>prompt/</c> 카탈로그가 <c>actions.json</c> 에서 생성되는 것과 같은 원칙이다 (CLAUDE.md §2.4).
///
/// <para>
/// <b>여기 있는 이유.</b> 껍질이 둘이다 — <c>Npc.Host hints</c> 와 <c>npc hints</c>.
/// 둘이 각자 markdown 을 만들면 같은 사전에서 다른 문서가 나온다.
/// </para>
/// </summary>
public static class FixHintDocument
{
    /// <summary>생성물 경로. 저장소 루트 기준이다.</summary>
    public const string Path = "docs/llm/VALIDATION.md";

    /// <summary>사전을 markdown 으로. <b>시각도 난수도 섞지 않는다</b> — 같은 입력이면 바이트 동일이다.</summary>
    public static string Render()
    {
        var sb = new StringBuilder(16 * 1024);

        sb.AppendLine("# 검증 오류 사전");
        sb.AppendLine();
        sb.AppendLine("> **이 파일은 생성물이다.** 손으로 고치지 않는다 —");
        sb.AppendLine("> `npc hints --out " + Path + "` 가 다시 만든다");
        sb.AppendLine("> (`dotnet run --project src/Npc.Host -- hints --out …` 도 같은 것을 만든다).");
        sb.AppendLine("> 원천은 `src/Npc.MasterData/Validation/FixHints.cs` 다.");
        sb.AppendLine();
        sb.AppendLine("검증이 실패하면 코드가 나온다. 그 코드로 여기를 찾아 **무엇을 하면 되는지**를 읽는다.");
        sb.AppendLine("`npc validate --json` 은 같은 힌트를 `fix_hint` 필드에 실어 준다.");
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
                : string.Join(" · ", hint.Related.Select(r => "`" + r + "`"));

            sb.Append("| `").Append(code).Append("` | ")
              .Append(hint.Hint.Replace("|", "\\|", StringComparison.Ordinal))
              .Append(" | ").Append(related).AppendLine(" |");
        }

        sb.AppendLine();
    }
}
