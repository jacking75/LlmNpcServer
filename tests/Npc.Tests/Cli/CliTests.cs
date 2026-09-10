using System.Text.Json;
using Npc.Cli;
using Npc.MasterData;
using CliProgram = Npc.Cli.Program;

namespace Npc.Tests.Cli;

/// <summary>
/// F-01 <c>npc</c> CLI.
///
/// <b>CLI 는 껍질이다.</b> 여기서 확인하는 것은 "코어를 제대로 부르는가" 와
/// "종료 코드가 판정과 맞는가" 두 가지다 — 판정 자체의 정확성은 코어의 테스트가 본다.
///
/// <para>
/// <b>종료 코드가 틀리면 CI 가 거짓말을 한다.</b> 0/1/2 의 뜻이 일정해야
/// <c>npc regen --check</c> 를 파이프라인에 걸 수 있다.
/// </para>
/// </summary>
public sealed class CliTests
{
    [Fact]
    public void Cli_ValidatePassesOnTheRepository()
    {
        (int code, string output, string error) = Run("validate");

        Assert.Equal(CliProgram.Ok, code);
        Assert.Contains("검증 통과", output, StringComparison.Ordinal);
        Assert.Contains("structural_hash", output, StringComparison.Ordinal);
        Assert.Empty(error);
    }

    /// <summary><c>--json</c> 은 <c>Npc.Host validate --format json</c> 과 <b>같은 형식</b>이어야 한다.</summary>
    [Fact]
    public void Cli_ValidateJsonMatchesTheHostFormat()
    {
        (int code, string output, _) = Run("validate", "--json");

        Assert.Equal(CliProgram.Ok, code);

        using JsonDocument document = JsonDocument.Parse(output);

        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.NotEmpty(document.RootElement.GetProperty("content_hash").GetString()!);
        Assert.NotEmpty(document.RootElement.GetProperty("structural_hash").GetString()!);

        // 한글을 escape 하지 않는다 — LLM 도 사람도 읽는다.
        Assert.DoesNotContain("\\u", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_NoArgumentsPrintsUsage()
    {
        (int code, string output, _) = Run();

        Assert.Equal(CliProgram.BadUsage, code);
        Assert.Contains("사용법", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_HelpSucceeds()
    {
        (int code, string output, _) = Run("--help");

        Assert.Equal(CliProgram.Ok, code);
        Assert.Contains("npc", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// 아직 없는 명령은 <b>"모르는 명령" 이 아니라 "아직 없다"</b> 로 답한다 —
    /// 둘은 다른 말이고, 오타와 미구현을 구별하지 못하면 사람이 헤맨다.
    /// </summary>
    [Theory]
    [InlineData("repair", "C-05")]
    [InlineData("review", "F-06")]
    [InlineData("serve", "B-08")]
    public void Cli_PendingCommandsSayWhichTaskTheyWaitFor(string command, string task)
    {
        (int code, _, string error) = Run(command);

        Assert.Equal(CliProgram.BadUsage, code);
        Assert.Contains("아직 없다", error, StringComparison.Ordinal);
        Assert.Contains(task, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_UnknownCommandFails()
    {
        (int code, _, string error) = Run("nonsense");

        Assert.Equal(CliProgram.BadUsage, code);
        Assert.Contains("모르는 명령", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_ExplainCoversEveryKind()
    {
        MasterDataSet data = MasterDataLoader.Load(TestPaths.MasterData);

        foreach ((string kind, string id) in new[]
        {
            ("archetype", data.Archetypes.Archetypes[0].Id),
            ("action", data.Actions.Actions[0].Id),
            ("item", data.Items.Items[0].Id),
            ("poi", data.Pois.Pois[0].Id),
            ("flag", "OnDuty"),
            ("interrupt", data.Interrupts.Rules[0].Id),
        })
        {
            (int code, string output, string error) = Run("explain", kind, id);

            Assert.Equal(CliProgram.Ok, code);
            Assert.StartsWith("# ", output, StringComparison.Ordinal);
            Assert.Empty(error);
        }
    }

    [Fact]
    public void Cli_ExplainRejectsUnknownId()
    {
        (int code, _, string error) = Run("explain", "archetype", "no_such_archetype");

        Assert.Equal(CliProgram.Failed, code);
        Assert.Contains("없다", error, StringComparison.Ordinal);

        // 스택 추적을 내지 않는다 — 터미널에서 읽는 사람에게 도움이 안 된다.
        Assert.DoesNotContain("   at ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_CardMatchesTheLibrary()
    {
        MasterDataSet data = MasterDataLoader.Load(TestPaths.MasterData);
        ArchetypeDef def = data.Archetypes.Archetypes[0];

        (int code, string output, _) = Run("card", "archetype", def.Id);

        Assert.Equal(CliProgram.Ok, code);

        // CLI 가 자기 형식을 만들지 않는다 — Npc.Narrative 를 그대로 낸다.
        Assert.Equal(
            Npc.Narrative.ArchetypeCard.Render(data, def.Code).ReplaceLineEndings(),
            output.ReplaceLineEndings());
    }

    [Fact]
    public void Cli_TimelineShowsDutyAndFallback()
    {
        MasterDataSet data = MasterDataLoader.Load(TestPaths.MasterData);
        ArchetypeDef onDuty = data.Archetypes.Archetypes.First(a => !a.DutyHours.IsEmpty);

        (int code, string output, _) = Run("timeline", "archetype", onDuty.Id);

        Assert.Equal(CliProgram.Ok, code);
        Assert.Contains("근무 ✓", output, StringComparison.Ordinal);
        Assert.Contains("## 폴백 하루", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_NextCodeKnowsEveryAlias()
    {
        foreach (string alias in NextCodeCommand.Aliases.Keys)
        {
            (int code, string output, _) = Run("next-code", alias);

            Assert.Equal(CliProgram.Ok, code);
            Assert.Contains("다음 번호", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Cli_NextCodeFlagsPointsAtTheReservedRange()
    {
        (int code, string output, _) = Run("next-code", "flags", "--json");

        Assert.Equal(CliProgram.Ok, code);

        using JsonDocument document = JsonDocument.Parse(output);

        int next = document.RootElement.GetProperty("next").GetInt32();
        int[] reserved = [.. document.RootElement.GetProperty("reserved").EnumerateArray().Select(e => e.GetInt32())];

        Assert.Contains(next, reserved);
        Assert.Empty(document.RootElement.GetProperty("gaps").EnumerateArray());
    }

    [Fact]
    public void Cli_RegenReportsFreshRepository()
    {
        (int code, string output, _) = Run("regen");

        Assert.Equal(CliProgram.Ok, code);
        Assert.Contains("최신", output, StringComparison.Ordinal);
    }

    /// <summary><c>--check</c> 는 CI 가 쓴다. 낡았으면 비0 이어야 파이프라인이 멈춘다.</summary>
    [Fact]
    public void Cli_RegenCheckFailsWhenStale()
    {
        string directory = TempCopy();

        try
        {
            File.Delete(Path.Combine(directory, "derived.lock.json"));

            (int code, string output, _) = RunIn(directory, "regen", "--check");

            Assert.Equal(CliProgram.Failed, code);
            Assert.Contains("낡음", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// <b>F-04 완료 조건.</b> 7장 실습의 아키타입 부분이 이 한 줄로 대체된다.
    /// dry-run 이 기본이고, 파일은 한 글자도 바뀌지 않아야 한다.
    /// </summary>
    [Fact]
    public void Cli_ScaffoldIsDryRunByDefault()
    {
        string directory = TempCopy();
        string path = Path.Combine(directory, "archetypes.json");
        string before = File.ReadAllText(path);

        try
        {
            (int code, string output, _) = RunIn(
                directory, "scaffold", "archetype", "beekeeper", "--from", "shepherd", "--weight", "0.004");

            Assert.Equal(CliProgram.Ok, code);
            Assert.Contains("dry-run", output, StringComparison.Ordinal);
            Assert.Contains("Largest", output, StringComparison.Ordinal);
            Assert.Contains("SameTrade", output, StringComparison.Ordinal);
            Assert.Contains("Proportional", output, StringComparison.Ordinal);
            Assert.Contains("프리픽스", output, StringComparison.Ordinal);

            Assert.Equal(before, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// <c>--apply</c> 는 <b>가중치 합을 지키면서</b> 새 줄을 넣는다.
    /// 서식이 보존되므로 diff 가 읽힌다 — 그것이 F-04 의 요점이다.
    /// </summary>
    [Fact]
    public void Cli_ScaffoldApplyKeepsWeightsValidAndFormatReadable()
    {
        string directory = TempCopy();
        string path = Path.Combine(directory, "archetypes.json");
        string before = File.ReadAllText(path);

        try
        {
            (int code, _, _) = RunIn(
                directory, "scaffold", "archetype", "beekeeper",
                "--from", "shepherd", "--weight", "0.004", "--apply");

            Assert.Equal(CliProgram.Ok, code);

            string after = File.ReadAllText(path);

            using JsonDocument document = JsonDocument.Parse(after);
            JsonElement archetypes = document.RootElement.GetProperty("archetypes");

            Assert.Equal(
                MasterDataLoader.Load(TestPaths.MasterData).Archetypes.Count + 1,
                archetypes.GetArrayLength());

            double sum = archetypes.EnumerateArray()
                .Sum(a => a.GetProperty("population_weight").GetDouble());

            Assert.Equal(1.0, sum, 3);   // V5

            JsonElement added = archetypes[archetypes.GetArrayLength() - 1];

            Assert.Equal("beekeeper", added.GetProperty("id").GetString());
            Assert.Equal(40, added.GetProperty("code").GetInt32());
            Assert.Equal("fb_beekeeper", added.GetProperty("fallback_plan").GetString());

            // desc 를 도구가 지어내지 않는다 — 프롬프트에 실리는 문장이다.
            Assert.StartsWith("TODO:", added.GetProperty("desc").GetString()!, StringComparison.Ordinal);

            // 한글이 escape 되지 않는다. 파일의 다른 줄과 표기가 같아야 한다.
            Assert.DoesNotContain("\\u", after, StringComparison.Ordinal);

            // 서식이 보존된다 — 바뀐 줄이 손에 꼽을 수 있어야 diff 를 읽는다.
            string[] a = before.ReplaceLineEndings("\n").Split('\n');
            string[] b = after.ReplaceLineEndings("\n").Split('\n');
            int common = 0;

            while (common < a.Length && common < b.Length && a[common] == b[common])
            {
                common++;
            }

            Assert.True(
                common > a.Length / 2,
                $"{common} 번째 줄부터 달라졌다 (전체 {a.Length}줄) — 서식이 보존되지 않았다.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Cli_ScaffoldRejectsExistingArchetype()
    {
        (int code, _, string error) = Run(
            "scaffold", "archetype", "blacksmith", "--from", "shepherd", "--weight", "0.004");

        Assert.Equal(CliProgram.Failed, code);
        Assert.Contains("이미 있다", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_ScaffoldRequiresFromAndWeight()
    {
        Assert.Equal(CliProgram.Failed, Run("scaffold", "archetype", "x", "--weight", "0.004").Code);
        Assert.Equal(CliProgram.Failed, Run("scaffold", "archetype", "x", "--from", "shepherd").Code);
    }

    /// <summary>플랜 검증은 4단 전부를 돌린다. 폴백은 다 통과해야 한다.</summary>
    [Fact]
    public void Cli_PlanValidateAcceptsAFallback()
    {
        MasterDataSet data = MasterDataLoader.Load(TestPaths.MasterData);
        FallbackPlanEntry entry = data.Fallbacks!.Plans[0];

        string file = Path.Combine(Path.GetTempPath(), $"npc-cli-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(file, Npc.Planning.PlanStoreIo.Serialize(entry.Plan.Bucket, entry.Plan, data));

            (int code, string output, _) = Run(
                "plan", "validate", file, "--bucket", entry.Plan.Bucket.Format(data.Archetypes[entry.Archetype].Id));

            Assert.Equal(CliProgram.Ok, code);
            Assert.Contains("4단 전부 통과", output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Cli_PlanValidateReportsTheFailingStage()
    {
        string file = Path.Combine(Path.GetTempPath(), $"npc-cli-{Guid.NewGuid():N}.json");

        try
        {
            // 스키마가 깨진 플랜. 1단에서 떨어져야 한다.
            File.WriteAllText(file, """{ "goal": "x" }""");

            (int code, string output, _) = Run(
                "plan", "validate", file, "--bucket", "blacksmith@Dawn.Peace.Fair");

            Assert.Equal(CliProgram.Failed, code);
            Assert.Contains("FAIL 1단", output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Cli_PlanRejectsMissingFile()
    {
        (int code, _, string error) = Run("plan", "validate", "no_such_plan.json");

        Assert.Equal(CliProgram.Failed, code);
        Assert.Contains("없다", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_HintsListsEveryCode()
    {
        (int code, string output, _) = Run("hints");

        Assert.Equal(CliProgram.Ok, code);

        foreach (string validation in Npc.MasterData.Validation.FixHints.Codes)
        {
            Assert.Contains(validation, output, StringComparison.Ordinal);
        }
    }

    /// <summary>같은 저장소 상태면 같은 출력이다 — 시각도 난수도 섞지 않았다.</summary>
    [Fact]
    public void Cli_OutputIsDeterministic()
    {
        foreach (string[] command in new[]
        {
            new[] { "validate" },
            ["card", "archetype", "blacksmith"],
            ["timeline", "archetype", "town_guard"],
            ["next-code", "archetypes"],
            ["hints"],
        })
        {
            Assert.Equal(Run(command).Output, Run(command).Output);
        }
    }

    private static (int Code, string Output, string Error) Run(params string[] args) =>
        RunIn(TestPaths.MasterData, args);

    private static (int Code, string Output, string Error) RunIn(string masterData, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        int code = CliProgram.Run(
            [.. args, "--masterdata", masterData, "--planstore", TestPaths.At("planstore")],
            output,
            error);

        return (code, output.ToString(), error.ToString());
    }

    private static string TempCopy()
    {
        string directory = Path.Combine(Path.GetTempPath(), "npc-cli-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(directory);

        foreach (string file in Directory.GetFiles(TestPaths.MasterData))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.GetDirectories(TestPaths.MasterData))
        {
            string target = Path.Combine(directory, Path.GetFileName(dir));
            Directory.CreateDirectory(target);

            foreach (string file in Directory.GetFiles(dir))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }

        return directory;
    }
}
