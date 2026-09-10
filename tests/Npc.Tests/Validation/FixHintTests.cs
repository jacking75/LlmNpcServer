using System.Text.RegularExpressions;
using Npc.Host.Commands;
using Npc.MasterData;
using Npc.MasterData.Validation;

namespace Npc.Tests.Validation;

/// <summary>
/// E-04 — 모든 검증 코드에 수정 힌트가 있다.
///
/// <b>새 검증 규칙을 추가하고 힌트를 빼먹으면 그 규칙은 LLM 에게 "이유를 모르는 실패" 가 된다.</b>
/// 소스에서 코드 문자열을 전부 긁어 사전과 대조한다 — 손으로 관리하는 목록을 또 두면
/// 그것이 어긋난다.
/// </summary>
public sealed partial class FixHintTests
{
    [Fact]
    public void EveryMasterDataCode_HasAHint()
    {
        foreach (string code in CodesIn(
            TestPaths.At("src", "Npc.MasterData", "Validation", "MasterDataValidator.cs"),
            MasterDataCodePattern()))
        {
            Assert.True(
                FixHints.For(code) is not null,
                $"{code} 에 수정 힌트가 없다. src/Npc.MasterData/Validation/FixHints.cs 에 추가한다.");
        }
    }

    [Theory]
    [InlineData("Npc.Core", "Validation", "SchemaValidator.cs")]
    [InlineData("Npc.Core", "Validation", "VocabularyValidator.cs")]
    [InlineData("Npc.Core", "Validation", "CoherenceValidator.cs")]
    [InlineData("Npc.Sim", "Validation", "DryRunValidator.cs")]
    public void EveryPlanCode_HasAHint(string project, string folder, string file)
    {
        foreach (string code in CodesIn(TestPaths.At("src", project, folder, file), PlanCodePattern()))
        {
            Assert.True(
                FixHints.For(code) is not null,
                $"{code} 에 수정 힌트가 없다 ({file}).");
        }
    }

    [Fact]
    public void EveryRejectCode_HasAHint()
    {
        foreach (Npc.Wire.LinkRejectCode code in Enum.GetValues<Npc.Wire.LinkRejectCode>())
        {
            if (code == Npc.Wire.LinkRejectCode.None)
            {
                continue;
            }

            Assert.True(
                FixHints.For(code.ToString()) is not null,
                $"핸드셰이크 거절 {code} 에 수정 힌트가 없다.");
        }
    }

    [Fact]
    public void Hints_AreImperativeAndNotEmpty()
    {
        foreach (string code in FixHints.Codes)
        {
            string hint = FixHints.HintOf(code);

            Assert.False(string.IsNullOrWhiteSpace(hint), $"{code} 의 힌트가 비었다");

            // "무엇을 하면 되는가" 여야 한다. 증상만 다시 적으면 힌트가 아니다.
            Assert.True(hint.Length >= 20, $"{code} 의 힌트가 너무 짧다: {hint}");
        }
    }

    [Fact]
    public void Json_CarriesHintsAndPaths()
    {
        MasterDataValidationReport report = MasterDataValidator.Validate(TestPaths.MasterData);
        MasterDataSet data = MasterDataLoader.Load(TestPaths.MasterData);

        ValidationResultJson result = ValidationJson.From(
            report, TestPaths.MasterData, data, loadError: null);

        Assert.True(result.Ok);
        Assert.Equal(data.ContentHash, result.ContentHash);
        Assert.Equal(data.StructuralHash, result.StructuralHash);

        string json = ValidationJson.Serialize(result);

        // 한글을 escape 하지 않는다 — LLM 도 사람도 읽는다.
        Assert.Contains("\"ok\": true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_ReportsLoadFailureAsAViolation()
    {
        MasterDataValidationReport report = MasterDataValidator.Validate(TestPaths.MasterData);

        ValidationResultJson result = ValidationJson.From(
            report, TestPaths.MasterData, data: null, loadError: "poi_distances.bin 이 낡았다");

        // 규칙만 통과하고 참조가 깨진 상태를 ok:true 로 내면 호출부가 그 위에 다음 작업을 쌓는다.
        Assert.False(result.Ok);

        ViolationJson load = Assert.Single(result.Violations, v => v.Code == "LOAD");

        Assert.Contains("낡았다", load.Message, StringComparison.Ordinal);
        Assert.NotEmpty(load.FixHint);
    }

    [Fact]
    public void Violation_ExposesHintThroughTheRecord()
    {
        var violation = new MasterDataViolation("V5", "합이 0.996 이다", "archetypes.json", "/archetypes");

        Assert.Equal("archetypes.json", violation.File);
        Assert.Equal("/archetypes", violation.Path);
        Assert.Contains("population_weight", violation.FixHint, StringComparison.Ordinal);
        Assert.NotEmpty(violation.Related);
    }

    [Fact]
    public void GeneratedDocument_IsUpToDate()
    {
        // 문서는 생성물이다. 두 벌 관리하면 반드시 어긋나고, 어긋난 문서는 없는 것보다 나쁘다 —
        // LLM 이 그것을 근거로 삼는다.
        string path = TestPaths.At("docs", "llm", "VALIDATION.md");

        Assert.True(
            File.Exists(path),
            "docs/llm/VALIDATION.md 이 없다. "
            + "dotnet run --project src/Npc.Host -- hints --out docs/llm/VALIDATION.md");

        Assert.Equal(
            HintsCommand.Render().ReplaceLineEndings(),
            File.ReadAllText(path).ReplaceLineEndings());
    }

    [Fact]
    public void Render_IsDeterministic()
    {
        // 시각도 난수도 섞지 않는다. 같은 입력이면 바이트 동일이어야 CI 의 diff 검사가 성립한다.
        Assert.Equal(HintsCommand.Render(), HintsCommand.Render());
    }

    private static IEnumerable<string> CodesIn(string path, Regex pattern)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in pattern.Matches(File.ReadAllText(path)))
        {
            string code = match.Groups[1].Value;

            if (seen.Add(code))
            {
                yield return code;
            }
        }
    }

    /// <summary><c>new MasterDataViolation("V5", …)</c> 의 첫 인자.</summary>
    [GeneratedRegex(@"MasterDataViolation\(\s*""(V\d+)""")]
    private static partial Regex MasterDataCodePattern();

    /// <summary>플랜 검증 코드 리터럴. <c>"V3.PRECONDITION_UNMET"</c> 꼴이다.</summary>
    [GeneratedRegex(@"""(V\d\.[A-Z_]+)""")]
    private static partial Regex PlanCodePattern();
}
