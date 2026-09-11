using System.Text;

namespace Npc.Tests.Prebake;

/// <summary>
/// 검수 도구 스크립트. docs/13 §5 · W1_env.md §4.6 · T3-17 · T3-19.
///
/// 스크립트 자체의 상호작용은 사람이 돌린다. 여기서 지키는 것은 <b>돌아가기는 하는가</b>다 —
/// BOM 이 없으면 Windows PowerShell 5.1 이 ANSI 로 읽어 파서 오류가 나고, 그때는 아무것도 못 한다.
///
/// <para>
/// <b><c>review.ps1</c> 은 지웠다</b> (F-06). 검수는 <c>npc review</c> 가 하고 그쪽은
/// <c>ReviewTests</c> 가 지킨다 — 같은 일을 하는 도구가 둘이면 한쪽이 반드시 낡는다.
/// 승격 스크립트(<c>pin_plan.ps1</c>)는 남겼다: 검수 기록에서 골라 일괄 승격하는 배치 경로다.
/// </para>
/// </summary>
public sealed class ReviewToolTests
{
    /// <summary>
    /// T3-17 완료 조건의 전제 — 한국어 주석이 들어가는 <c>.ps1</c> 은 UTF-8 BOM 이어야 한다.
    ///
    /// BOM 이 없으면 PowerShell 5.1 이 ANSI 로 읽어 <c>-?</c> 조차 파서 오류로 죽는다
    /// (<c>W1_env.md §4.6</c>). P3·P4 의 신규 스크립트 전부에 적용된다.
    /// </summary>
    [Theory]
    [InlineData("pin_plan.ps1")]
    public void ReviewScripts_AreUtf8WithBom(string name)
    {
        string path = TestPaths.At("tools", name);

        Assert.True(File.Exists(path), $"{name} 이 없다.");

        byte[] head = new byte[3];

        using (FileStream stream = File.OpenRead(path))
        {
            Assert.Equal(3, stream.Read(head, 0, 3));
        }

        Assert.Equal([0xEF, 0xBB, 0xBF], head);

        // 한국어가 실제로 들어 있어야 이 요구가 의미를 갖는다.
        string text = File.ReadAllText(path, Encoding.UTF8);

        Assert.Contains("검수", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// T3-17 — 도움말이 파서 오류 없이 나온다. 주석 기반 도움말 블록과 param 블록이 있어야 한다.
    /// </summary>
    [Theory]
    [InlineData("pin_plan.ps1")]
    public void ReviewScripts_HaveHelpAndParamBlock(string name)
    {
        string text = File.ReadAllText(TestPaths.At("tools", name), Encoding.UTF8);

        Assert.Contains("<#", text, StringComparison.Ordinal);
        Assert.Contains(".SYNOPSIS", text, StringComparison.Ordinal);
        Assert.Contains(".DESCRIPTION", text, StringComparison.Ordinal);
        Assert.Contains(".EXAMPLE", text, StringComparison.Ordinal);
        Assert.Contains("[CmdletBinding(", text, StringComparison.Ordinal);
        Assert.Contains("param(", text, StringComparison.Ordinal);

        // StrictMode 를 켜 둔다 — 없는 속성을 조용히 $null 로 읽으면 판정이 조용히 틀린다.
        Assert.Contains("Set-StrictMode", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// T3-19 완료 조건 — <c>.gitignore</c> 가 <c>planstore/pinned/</c> 를 무시하지 않는다.
    ///
    /// 무시되면 승격해도 커밋되지 않아 검수 작업이 조용히 사라진다 (CLAUDE.md §6).
    /// 생성물인 <c>plans/</c>·<c>rejected/</c> 는 반대로 무시되어야 한다.
    /// </summary>
    [Fact]
    public void GitIgnore_KeepsPinnedButDropsGenerated()
    {
        string[] lines = File.ReadAllLines(TestPaths.At(".gitignore"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

        Assert.Contains("planstore/plans/", lines);
        Assert.Contains("planstore/rejected/", lines);

        // pinned 를 통째로 삼키는 규칙이 없어야 한다.
        Assert.DoesNotContain("planstore/", lines);
        Assert.DoesNotContain("planstore/*", lines);
        Assert.DoesNotContain("planstore/pinned/", lines);
        Assert.DoesNotContain("pinned/", lines);

        // manifest 도 커밋 대상이다 (docs/03 §7 · CLAUDE.md §6).
        Assert.DoesNotContain("planstore/manifest.json", lines);
    }

    /// <summary>T3-19 — 승격 도구가 origin 을 Pinned 로 바꾸고 plans/ 에서 옮긴다.</summary>
    [Fact]
    public void PinScript_MovesAndRewritesOrigin()
    {
        string text = File.ReadAllText(TestPaths.At("tools", "pin_plan.ps1"), Encoding.UTF8);

        Assert.Contains("'Pinned'", text, StringComparison.Ordinal);
        Assert.Contains("Remove-Item", text, StringComparison.Ordinal);
        Assert.Contains("check-ignore", text, StringComparison.Ordinal);

        // 검수 기록에서 edit 판정만 골라 승격한다 (docs/13 §5).
        Assert.Contains("$entry.verdict -eq 'edit'", text, StringComparison.Ordinal);

        // 플랜 파일은 BOM 없이 쓴다 — PlanStoreIo 가 읽는 파일이다.
        Assert.Contains("UTF8Encoding($false)", text, StringComparison.Ordinal);
    }
}
