using System.Collections.Immutable;

namespace Npc.Tests.Docs;

/// <summary>
/// E-06 — 에이전트 벤치의 모양을 지킨다.
///
/// <para>
/// <b>여기서 에이전트를 돌리지 않는다.</b> 그것은 사람이 시키는 일이고 비용이 든다.
/// 이 테스트가 지키는 것은 <b>벤치가 돌 수 있는 상태인가</b> 하나다 —
/// 과제마다 요청과 채점기가 짝을 이루고, 채점기가 PowerShell 로 파싱되고,
/// 러너가 그 과제들을 실제로 가리키는가.
/// </para>
/// </summary>
public sealed class AgentBenchTests
{
    /// <summary>로드맵 E-06 이 정한 과제 수.</summary>
    private const int TaskCount = 10;

    private static readonly string s_bench = TestPaths.At("tests", "agent-bench");

    private static ImmutableArray<string> TaskDirectories() =>
    [
        .. Directory
            .EnumerateDirectories(s_bench)
            .Where(d => !string.Equals(Path.GetFileName(d), "lib", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>과제가 10종이고 전부 요청과 채점기를 갖는다.</summary>
    [Fact]
    public void EveryTask_HasRequestAndChecker()
    {
        ImmutableArray<string> directories = TaskDirectories();

        Assert.Equal(TaskCount, directories.Length);

        foreach (string directory in directories)
        {
            string name = Path.GetFileName(directory);

            Assert.True(File.Exists(Path.Combine(directory, "task.md")), $"{name}/task.md 이 없다");
            Assert.True(File.Exists(Path.Combine(directory, "check.ps1")), $"{name}/check.ps1 이 없다");
        }
    }

    /// <summary>
    /// 요청에 front matter 가 있다 — 러너가 <c>title</c> 을 읽고, 사람이 <c>allowed</c> 로
    /// 무엇이 바뀌어도 되는지 안다.
    /// </summary>
    [Fact]
    public void EveryTask_HasFrontMatter()
    {
        foreach (string directory in TaskDirectories())
        {
            string name = Path.GetFileName(directory);
            string text = File.ReadAllText(Path.Combine(directory, "task.md"));

            Assert.StartsWith("---", text, StringComparison.Ordinal);
            Assert.Contains($"id: {name}", text, StringComparison.Ordinal);
            Assert.Contains("title:", text, StringComparison.Ordinal);
            Assert.Contains("allowed:", text, StringComparison.Ordinal);
            Assert.Contains("timeout_minutes:", text, StringComparison.Ordinal);

            // 채점 기준이 요청에 적혀 있어야 한다 — 모르는 기준으로 채점하면 벤치가 아니라 함정이다.
            Assert.Contains("# 채점", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 채점기가 잘리지 않았다 — 괄호가 맞고 UTF-8 BOM 이 있다.
    ///
    /// <para>
    /// <b>PowerShell 파서를 부르지 않는다.</b> 이 테스트 프로젝트는 크로스 플랫폼이고
    /// <c>System.Management.Automation</c> 은 없다. 진짜 파싱·실행 검사는
    /// <c>tools/agent-bench.ps1 -SelfTest</c> 의 일이다 — 여기서는 <b>파일이 온전한가</b>만 본다.
    /// </para>
    ///
    /// <para>
    /// <b>BOM 이 필요한 이유.</b> Windows PowerShell 5.1 은 BOM 없는 UTF-8 을 ANSI 로 읽어
    /// 한글 판정 문구가 통째로 깨진다.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryChecker_IsIntact()
    {
        foreach (string path in TaskDirectories()
            .Select(d => Path.Combine(d, "check.ps1"))
            .Append(Path.Combine(s_bench, "lib", "Check.ps1"))
            .Append(TestPaths.At("tools", "agent-bench.ps1")))
        {
            string name = Path.GetFileName(path);
            byte[] head = File.ReadAllBytes(path)[..3];

            Assert.True(
                head is [0xEF, 0xBB, 0xBF],
                $"{name} 에 UTF-8 BOM 이 없다 — PS 5.1 이 한글을 ANSI 로 읽는다");

            string text = File.ReadAllText(path);

            Assert.Equal(text.Count(c => c == '{'), text.Count(c => c == '}'));
            Assert.Equal(text.Count(c => c == '('), text.Count(c => c == ')'));
        }
    }

    /// <summary>
    /// 채점기가 공통 함수를 쓴다. 각자 판정을 새로 짜면 <b>과제마다 다른 기준</b>이 생긴다.
    /// </summary>
    [Fact]
    public void EveryChecker_UsesTheSharedLibrary()
    {
        foreach (string directory in TaskDirectories())
        {
            string text = File.ReadAllText(Path.Combine(directory, "check.ps1"));

            Assert.Contains("lib/Check.ps1", text, StringComparison.Ordinal);
            Assert.Contains("Complete-Check", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 과제 7 의 표본은 <b>실제로 검증에 떨어지는 플랜</b>이어야 한다.
    /// 통과하는 플랜을 고치라고 하면 그 과제는 언제나 만점이다.
    /// </summary>
    [Fact]
    public void RepairTask_HasEightBrokenPlans()
    {
        string broken = Path.Combine(s_bench, "07-repair-plans", "broken");

        Assert.True(Directory.Exists(broken), "07-repair-plans/broken 이 없다");

        string[] files = [.. Directory.EnumerateFiles(broken, "*.json").Order(StringComparer.Ordinal)];

        Assert.Equal(8, files.Length);

        // 파일 이름이 버킷 키다 — 채점기가 그것으로 검증한다.
        foreach (string file in files)
        {
            Assert.Contains('@', Path.GetFileNameWithoutExtension(file));
        }
    }

    /// <summary>
    /// <b>거절이 정답인 과제가 있다</b> (09). 이것이 빠지면 벤치는 "시키는 대로 하는가" 만 잰다.
    /// </summary>
    [Fact]
    public void Bench_HasARefusalTask()
    {
        string task = File.ReadAllText(Path.Combine(s_bench, "09-refuse-linq", "task.md"));

        Assert.Contains("정답은 거절이다", task, StringComparison.Ordinal);

        string check = File.ReadAllText(Path.Combine(s_bench, "09-refuse-linq", "check.ps1"));

        Assert.Contains("Test-ChangedFiles", check, StringComparison.Ordinal);
    }

    /// <summary>러너가 있고 자체 시험을 갖는다.</summary>
    [Fact]
    public void Runner_ExistsAndSelfTests()
    {
        string runner = File.ReadAllText(TestPaths.At("tools", "agent-bench.ps1"));

        Assert.Contains("SelfTest", runner, StringComparison.Ordinal);
        Assert.Contains("agent_bench.csv", runner, StringComparison.Ordinal);

        // <b>벽시계를 읽지 않는다.</b> 같은 회차를 다시 채점하면 같은 줄이 남아야 한다.
        Assert.DoesNotContain("Get-Date", runner, StringComparison.Ordinal);
    }

    /// <summary>기록 CSV 의 머리글이 러너가 쓰는 것과 같다.</summary>
    [Fact]
    public void Csv_HeaderMatchesTheRunner()
    {
        string path = TestPaths.At("docs", "measurements", "agent_bench.csv");

        Assert.True(File.Exists(path), "agent_bench.csv 이 없다 — 첫 실측이 아직 없다");

        string header = File.ReadLines(path).First().TrimStart('﻿');

        Assert.Equal("stamp,agent,task,run,pass,minutes,changed_files,note", header);

        string runner = File.ReadAllText(TestPaths.At("tools", "agent-bench.ps1"));

        Assert.Contains(header, runner, StringComparison.Ordinal);
    }
}
