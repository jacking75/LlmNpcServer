using System.Text.Json;
using System.Text.Json.Nodes;
using Npc.MasterData.Validation;

namespace Npc.Tests.MasterData;

/// <summary>
/// docs/01 §11 의 V1~V11. 각 규칙마다 위반 픽스처를 하나씩 만들어
/// <b>정확히 그 코드로</b> 실패하는지 확인한다.
/// 검증 실패는 기동 실패다 — 경고 후 진행을 허용하지 않는다.
/// </summary>
public sealed class MasterDataValidatorTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 테스트 정리 실패는 테스트 실패가 아니다.
            }
        }
    }

    /// <summary>masterdata 를 임시 폴더로 복사하고 파일 하나를 손본다.</summary>
    private string Tamper(string fileName, Action<JsonNode> mutate)
    {
        string dir = Path.Combine(Path.GetTempPath(), "npc-md-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        foreach (string source in Directory.GetFiles(TestPaths.MasterData))
        {
            File.Copy(source, Path.Combine(dir, Path.GetFileName(source)));
        }

        string target = Path.Combine(dir, fileName);
        JsonNode root = JsonNode.Parse(File.ReadAllText(target))!;
        mutate(root);
        File.WriteAllText(target, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return dir;
    }

    /// <summary>임시 폴더에 파일 하나를 새로 쓴다 (fallback_plans.json 처럼 원래 없는 파일).</summary>
    private string WithExtraFile(string fileName, string content)
    {
        string dir = Path.Combine(Path.GetTempPath(), "npc-md-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        foreach (string source in Directory.GetFiles(TestPaths.MasterData))
        {
            File.Copy(source, Path.Combine(dir, Path.GetFileName(source)));
        }

        File.WriteAllText(Path.Combine(dir, fileName), content);
        return dir;
    }

    private static void AssertOnly(MasterDataValidationReport report, string code)
    {
        Assert.False(report.IsValid);
        Assert.True(
            report.HasViolation(code),
            $"{code} 위반이 없다. 실제: {string.Join(", ", report.Violations.Select(v => v.Code).Distinct())}");
    }

    // ---------------------------------------------------------------- 정상 데이터

    [Fact]
    public void MasterData_RealDataPassesAllRules()
    {
        MasterDataValidationReport report = MasterDataValidator.Validate(TestPaths.MasterData);

        Assert.True(
            report.IsValid,
            string.Join('\n', report.Violations.Select(v => $"{v.Code}: {v.Detail}")));

        // P1 시점에 아직 없는 입력은 건너뛴다. 무엇을 왜 건너뛰었는지 보고서에 남아야 한다.
        Assert.All(report.Skipped, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
    }

    [Fact]
    public void MasterData_ValidateOrThrowIsSilentOnGoodData()
    {
        MasterDataValidator.ValidateOrThrow(TestPaths.MasterData);
    }

    // ---------------------------------------------------------------- V1 ~ V11

    [Fact]
    public void MasterData_V1_DuplicateCode()
    {
        string dir = Tamper("items.json", root =>
        {
            JsonArray items = root["items"]!.AsArray();
            items[1]!["code"] = items[0]!["code"]!.GetValue<int>();
        });

        AssertOnly(MasterDataValidator.Validate(dir), "V1");
    }

    [Fact]
    public void MasterData_V2_UnknownFlagReference()
    {
        string dir = Tamper("actions.json", root =>
            root["actions"]![0]!["forbids"]!.AsArray().Add("NoSuchFlag"));

        AssertOnly(MasterDataValidator.Validate(dir), "V2");
    }

    [Fact]
    public void MasterData_V3_UnknownItemReference()
    {
        string dir = Tamper("pois.json", root =>
        {
            foreach (JsonNode? poi in root["pois"]!.AsArray())
            {
                if (poi!["type"]!.GetValue<string>() == "field")
                {
                    poi["resources"]!.AsArray().Add("unobtainium");
                    return;
                }
            }
        });

        AssertOnly(MasterDataValidator.Validate(dir), "V3");
    }

    [Fact]
    public void MasterData_V4_UnknownAllowedAction()
    {
        string dir = Tamper("archetypes.json", root =>
            root["archetypes"]![0]!["allowed_actions"]!.AsArray().Add("Teleport"));

        AssertOnly(MasterDataValidator.Validate(dir), "V4");
    }

    [Fact]
    public void MasterData_V5_PopulationWeightDoesNotSumToOne()
    {
        string dir = Tamper("archetypes.json", root =>
            root["archetypes"]![0]!["population_weight"] = 0.5);

        AssertOnly(MasterDataValidator.Validate(dir), "V5");
    }

    [Fact]
    public void MasterData_V6_TotalKeysMismatch()
    {
        string dir = Tamper("context_buckets.json", root => root["total_keys"] = 9_999);

        AssertOnly(MasterDataValidator.Validate(dir), "V6");
    }

    [Fact]
    public void MasterData_V7_MissingFallbackPlan()
    {
        // 폴백 플랜 파일은 있는데 아키타입이 가리키는 플랜이 없다.
        string dir = WithExtraFile("fallback_plans.json", """
            { "version": 1, "plans": [] }
            """);

        AssertOnly(MasterDataValidator.Validate(dir), "V7");
    }

    [Fact]
    public void MasterData_V7_FallbackPlanIsNotLoopable()
    {
        string dir = WithExtraFile("fallback_plans.json", """
            {
              "version": 1,
              "plans": [
                { "id": "fb_blacksmith", "archetype": "blacksmith", "goal": "survive_and_work",
                  "steps": [{ "action": "MoveTo", "args": { "poi": "$workplace" }, "timeout_s": 300 }],
                  "loop": false }
              ]
            }
            """);

        MasterDataValidationReport report = MasterDataValidator.Validate(dir);

        Assert.Contains(report.Violations, v => v.Code == "V7" && v.Detail.Contains("loop", StringComparison.Ordinal));
    }

    [Fact]
    public void MasterData_V8_FallbackUsesDisallowedAction()
    {
        // blacksmith 는 Fish 를 못 쓴다.
        string dir = WithExtraFile("fallback_plans.json", """
            {
              "version": 1,
              "plans": [
                { "id": "fb_blacksmith", "archetype": "blacksmith", "goal": "survive_and_work",
                  "steps": [{ "action": "Fish", "args": { "count": 1 }, "timeout_s": 300 }],
                  "loop": true }
              ]
            }
            """);

        AssertOnly(MasterDataValidator.Validate(dir), "V8");
    }

    [Fact]
    public void MasterData_V9_PrefixTooShortForPromptCache()
    {
        MasterDataValidationReport report = MasterDataValidator.Validate(
            TestPaths.MasterData, new MasterDataValidationOptions(PromptPrefixTokens: 100));

        AssertOnly(report, "V9");
    }

    [Fact]
    public void MasterData_V9_IsSkippedWhenPrefixIsUnknown()
    {
        MasterDataValidationReport report = MasterDataValidator.Validate(TestPaths.MasterData);

        Assert.Contains(report.Skipped, s => s.Code == "V9");
    }

    [Fact]
    public void MasterData_V10_WorkplaceCapacityTooSmall()
    {
        string dir = Tamper("pois.json", root =>
        {
            foreach (JsonNode? poi in root["pois"]!.AsArray())
            {
                if (poi!["subtype"]!.GetValue<string>() == "smithy")
                {
                    poi["capacity"] = 1;
                }
            }
        });

        AssertOnly(MasterDataValidator.Validate(dir), "V10");
    }

    [Fact]
    public void MasterData_V11_IsolatedZone()
    {
        string dir = Tamper("zones.json", root =>
        {
            JsonArray zones = root["zones"]!.AsArray();
            string isolated = zones[^1]!["id"]!.GetValue<string>();

            // 마지막 존을 그래프에서 떼어낸다 — 양방향 모두.
            zones[^1]!["adjacent"] = new JsonArray();

            foreach (JsonNode? zone in zones)
            {
                JsonArray adjacent = zone!["adjacent"]!.AsArray();
                for (int i = adjacent.Count - 1; i >= 0; i--)
                {
                    if (adjacent[i]!.GetValue<string>() == isolated)
                    {
                        adjacent.RemoveAt(i);
                    }
                }
            }
        });

        AssertOnly(MasterDataValidator.Validate(dir), "V11");
    }

    [Fact]
    public void MasterData_V11_AsymmetricAdjacency()
    {
        string dir = Tamper("zones.json", root =>
        {
            JsonArray zones = root["zones"]!.AsArray();
            zones[0]!["adjacent"]!.AsArray().Add(zones[^1]!["id"]!.GetValue<string>());
        });

        MasterDataValidationReport report = MasterDataValidator.Validate(dir);

        Assert.Contains(report.Violations, v => v.Code == "V11" && v.Detail.Contains("비대칭", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 기동 실패

    [Fact]
    public void MasterData_ValidationFailureIsStartupFailure()
    {
        string dir = Tamper("context_buckets.json", root => root["total_keys"] = 1);

        MasterDataValidationException ex =
            Assert.Throws<MasterDataValidationException>(() => MasterDataValidator.ValidateOrThrow(dir));

        Assert.True(ex.Report.HasViolation("V6"));
        Assert.Contains("V6", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MasterData_MissingFileIsReported()
    {
        string dir = Tamper("items.json", _ => { });
        File.Delete(Path.Combine(dir, "actions.json"));

        MasterDataValidationReport report = MasterDataValidator.Validate(dir);

        Assert.False(report.IsValid);
        Assert.Contains(report.Violations, v => v.Detail.Contains("actions.json", StringComparison.Ordinal));
    }
}
