using System.Text;
using System.Text.RegularExpressions;

namespace Npc.Tests.TestBed;

/// <summary>
/// T6-34 — <c>testbed/run_demo.ps1</c>. docs/20 §12.
///
/// <para>
/// <b>스크립트가 실제로 세 프로세스를 띄우는지는 사람이 본다.</b> 여기서 지키는 것은
/// 그 전에 깨지는 것들이다 — BOM 이 없으면 Windows PowerShell 5.1 이 ANSI 로 읽어
/// 한국어 주석에서 파서 오류가 나고, 그때는 <c>-?</c> 조차 안 된다 (<c>W1_env.md §4.6</c>).
/// </para>
///
/// <para>
/// <b>시나리오 목록이 파일과 어긋나는 것도 여기서 잡는다.</b> 스크립트의
/// <c>ValidateSet</c> 과 <c>testbed/scenarios/</c> 가 갈라지면 증상이 둘로 나온다 —
/// 없는 파일을 가리키면 기동 실패이고, 파일을 추가했는데 목록에 없으면
/// <b>그 시나리오를 띄울 방법이 사라진다.</b> 뒤쪽은 아무도 눈치채지 못한다.
/// </para>
/// </summary>
public sealed class RunDemoScriptTests
{
    private static string Path => TestPaths.At("testbed", "run_demo.ps1");

    /// <summary>T6-34 완료 조건 — 스크립트 첫 3바이트가 <c>EF BB BF</c> 다.</summary>
    [Fact]
    public void RunDemo_IsUtf8WithBom()
    {
        Assert.True(File.Exists(Path), "testbed/run_demo.ps1 이 없다.");

        byte[] head = new byte[3];

        using (FileStream stream = File.OpenRead(Path))
        {
            Assert.Equal(3, stream.Read(head, 0, 3));
        }

        Assert.Equal([0xEF, 0xBB, 0xBF], head);

        // 한국어가 실제로 들어 있어야 이 요구가 의미를 갖는다.
        Assert.Contains("게임서버", File.ReadAllText(Path, Encoding.UTF8), StringComparison.Ordinal);
    }

    /// <summary>T6-34 — <c>-Scenario</c>·<c>-Npcs</c>·<c>-TimeScale</c> 을 받는다.</summary>
    [Theory]
    [InlineData("$Scenario")]
    [InlineData("$Npcs")]
    [InlineData("$TimeScale")]
    public void RunDemo_TakesParameter(string name)
    {
        Assert.Contains(name, File.ReadAllText(Path, Encoding.UTF8), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ValidateSet</c> 의 시나리오 이름과 <c>testbed/scenarios/demo_*.jsonl</c> 이 1:1 이다.
    /// </summary>
    [Fact]
    public void RunDemo_ScenarioSetMatchesFiles()
    {
        string script = File.ReadAllText(Path, Encoding.UTF8);
        Match match = Regex.Match(
            script,
            @"\[ValidateSet\(([^)]*)\)\]\s*\r?\n\s*\[string\]\s*\$Scenario",
            RegexOptions.None,
            TimeSpan.FromSeconds(2));

        Assert.True(match.Success, "-Scenario 의 ValidateSet 을 찾지 못했다.");

        string[] names = [.. Regex
            .Matches(match.Groups[1].Value, "'([^']+)'", RegexOptions.None, TimeSpan.FromSeconds(2))
            .Select(m => m.Groups[1].Value)
            .Order(StringComparer.Ordinal)];

        string[] files = [.. Directory
            .EnumerateFiles(TestPaths.At("testbed", "scenarios"), "demo_*.jsonl")
            .Select(f => System.IO.Path.GetFileNameWithoutExtension(f)["demo_".Length..])
            .Order(StringComparer.Ordinal)];

        Assert.Equal(files, names);
    }

    /// <summary>
    /// 포트 세 개가 docs/20 §12 의 것과 같다.
    /// <b>스크립트가 셋을 한 번씩만 적는다</b> — 세 프로세스에 같은 값이 가야 하기 때문이다.
    /// </summary>
    [Theory]
    [InlineData("$LinkPort = 7010")]
    [InlineData("$ClientPort = 7020")]
    [InlineData("$HttpPort = 5080")]
    public void RunDemo_UsesSpecPorts(string assignment)
    {
        Assert.Contains(assignment, File.ReadAllText(Path, Encoding.UTF8), StringComparison.Ordinal);
    }

    /// <summary>
    /// 킬스위치 버튼이 먹으려면 <c>--dev-control</c> 이 있어야 한다 (docs/20 §11.4).
    /// 없으면 라우트 자체가 없어 404 이고, 제어 패널은 "미연결" 로만 보인다.
    /// </summary>
    [Fact]
    public void RunDemo_PassesDevControl()
    {
        string script = File.ReadAllText(Path, Encoding.UTF8);

        Assert.Contains("--dev-control", script, StringComparison.Ordinal);

        // 같은 jsonl 을 양쪽에 준다 — 게임서버는 KillSwitch 줄을 무시하고
        // NPC 서버는 그 줄만 처리한다 (docs/20 §11.4).
        // 인자로 실린 것만 센다. 주석의 "--scenario" 는 세지 않는다.
        Assert.Equal(
            2,
            Regex.Matches(script, "'--scenario',", RegexOptions.None, TimeSpan.FromSeconds(2)).Count);
    }
}
