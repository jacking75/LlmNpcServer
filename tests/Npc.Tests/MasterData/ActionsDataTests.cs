using System.Text.Json;

namespace Npc.Tests.MasterData;

/// <summary>
/// masterdata/actions.json 자체의 무결성. docs/01 §2.
/// 이 파일이 곧 프롬프트 프리픽스의 본문이 되므로, 여기가 어긋나면 검증 실패율이 즉시 치솟는다.
/// </summary>
public sealed class ActionsDataTests
{
    /// <summary>docs/01 §2.3 의 파라미터 타입 7종. 늘리지 않는다.</summary>
    private static readonly string[] s_paramTypes =
        ["poi_ref", "item_ref", "npc_ref", "zone_ref", "enum", "int", "route"];

    /// <summary>docs/01 §2.2 의 카테고리별 개수.</summary>
    private static readonly (string Category, int Count)[] s_categories =
    [
        ("movement", 5), ("labor", 8), ("social", 6),
        ("needs", 5), ("combat", 5), ("items", 5), ("misc", 3),
    ];

    private static readonly JsonDocument s_actions = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(TestPaths.MasterData, "actions.json")));

    private static readonly HashSet<string> s_knownFlags = LoadFlagIds();

    private static JsonElement[] Actions() =>
        s_actions.RootElement.GetProperty("actions").EnumerateArray().ToArray();

    private static HashSet<string> LoadFlagIds()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestPaths.MasterData, "world_flags.json")));

        return doc.RootElement.GetProperty("flags").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void ActionsJson_HasThirtySevenActions()
    {
        Assert.Equal(37, Actions().Length);
    }

    [Fact]
    public void ActionsJson_CodesAreUniqueAndCoverOneToThirtySeven()
    {
        int[] codes = Actions().Select(a => a.GetProperty("code").GetInt32()).Order().ToArray();

        Assert.Equal(Enumerable.Range(1, 37).ToArray(), codes);
    }

    [Fact]
    public void ActionsJson_IdsAreUnique()
    {
        string[] ids = Actions().Select(a => a.GetProperty("id").GetString()!).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ActionsJson_CategoryCountsMatchSpecTable()
    {
        Dictionary<string, int> actual = Actions()
            .GroupBy(a => a.GetProperty("category").GetString()!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach ((string category, int count) in s_categories)
        {
            Assert.True(actual.TryGetValue(category, out int got), $"카테고리 {category} 가 없다.");
            Assert.Equal(count, got);
        }

        Assert.Equal(s_categories.Length, actual.Count);
    }

    /// <summary>T1-12 완료 조건 — 모든 requires/requires_any/forbids/grants/clears 가 world_flags 에 존재한다.</summary>
    [Fact]
    public void ActionsJson_AllFlagReferencesExist()
    {
        List<string> violations = [];

        foreach (JsonElement action in Actions())
        {
            string id = action.GetProperty("id").GetString()!;

            foreach (string field in new[] { "requires", "requires_any", "forbids", "grants", "clears" })
            {
                Assert.True(action.TryGetProperty(field, out JsonElement arr), $"{id} 에 {field} 가 없다.");

                foreach (JsonElement flag in arr.EnumerateArray())
                {
                    string flagId = flag.GetString()!;
                    if (!s_knownFlags.Contains(flagId))
                    {
                        violations.Add($"{id}.{field}: {flagId}");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>T1-12 완료 조건 — 모든 param type 이 docs/01 §2.3 의 7종 중 하나다.</summary>
    [Fact]
    public void ActionsJson_AllParamTypesAreKnown()
    {
        List<string> violations = [];

        foreach (JsonElement action in Actions())
        {
            string id = action.GetProperty("id").GetString()!;

            foreach (JsonProperty p in action.GetProperty("params").EnumerateObject())
            {
                string type = p.Value.GetProperty("type").GetString()!;
                if (!s_paramTypes.Contains(type, StringComparer.Ordinal))
                {
                    violations.Add($"{id}.{p.Name}: {type}");
                }

                if (type == "enum")
                {
                    Assert.True(p.Value.TryGetProperty("values", out _), $"{id}.{p.Name}: enum 인데 values 가 없다.");
                }

                if (type == "int")
                {
                    Assert.True(p.Value.TryGetProperty("min", out _), $"{id}.{p.Name}: int 인데 min 이 없다.");
                    Assert.True(p.Value.TryGetProperty("max", out _), $"{id}.{p.Name}: int 인데 max 가 없다.");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ActionsJson_EveryActionHasTimeoutAndEmits()
    {
        foreach (JsonElement action in Actions())
        {
            string id = action.GetProperty("id").GetString()!;

            // 모든 스텝에 기본 타임아웃이 있어야 NPC 가 완료 이벤트 미수신으로 영구 정지하지 않는다.
            int timeout = action.GetProperty("default_timeout_s").GetInt32();
            Assert.InRange(timeout, 5, 7200);

            JsonElement emits = action.GetProperty("emits");
            Assert.True(emits.GetArrayLength() > 0, $"{id} 에 emits 가 없다.");

            foreach (JsonElement emit in emits.EnumerateArray())
            {
                string command = emit.GetProperty("command").GetString()!;
                Assert.True(
                    Enum.TryParse<Npc.Contracts.NpcCommandKind>(command, out _),
                    $"{id} 가 모르는 명령 '{command}' 를 발행한다.");

                if (emit.TryGetProperty("priority", out JsonElement priority))
                {
                    Assert.True(
                        Enum.TryParse<Npc.Contracts.CommandPriority>(priority.GetString(), out _),
                        $"{id} 의 priority '{priority.GetString()}' 를 모른다.");
                }
            }

            Assert.True(action.GetProperty("completes_on").GetArrayLength() > 0, $"{id} 에 completes_on 이 없다.");
            Assert.True(action.GetProperty("fails_on").GetArrayLength() > 0, $"{id} 에 fails_on 이 없다.");
        }
    }

    [Fact]
    public void ActionsJson_EventNamesAreKnown()
    {
        foreach (JsonElement action in Actions())
        {
            foreach (string field in new[] { "completes_on", "fails_on" })
            {
                foreach (JsonElement ev in action.GetProperty(field).EnumerateArray())
                {
                    Assert.True(
                        Enum.TryParse<Npc.Contracts.GameEventKind>(ev.GetString(), out _),
                        $"{action.GetProperty("id").GetString()}.{field}: {ev.GetString()} 를 모른다.");
                }
            }
        }
    }

    /// <summary>desc 는 LLM 프롬프트에 그대로 실린다. 비어 있거나 한 문장짜리면 플랜 품질이 떨어진다.</summary>
    [Fact]
    public void ActionsJson_DescriptionsAreSentences()
    {
        foreach (JsonElement action in Actions())
        {
            string desc = action.GetProperty("desc").GetString()!;

            Assert.InRange(desc.Length, 40, 400);
            Assert.EndsWith(".", desc, StringComparison.Ordinal);
        }
    }

    /// <summary>W1 스파이크가 쓴 12개의 code 는 재배치되면 안 된다 (spike/data/actions.min.json).</summary>
    [Theory]
    [InlineData("MoveTo", 1)]
    [InlineData("Work", 6)]
    [InlineData("Gather", 7)]
    [InlineData("Craft", 11)]
    [InlineData("Talk", 14)]
    [InlineData("Trade", 15)]
    [InlineData("Eat", 20)]
    [InlineData("Sleep", 22)]
    [InlineData("Rest", 23)]
    [InlineData("Guard", 27)]
    [InlineData("Store", 32)]
    [InlineData("Wait", 35)]
    public void ActionsJson_SpikeCodesAreStable(string id, int code)
    {
        JsonElement action = Actions().Single(a => a.GetProperty("id").GetString() == id);

        Assert.Equal(code, action.GetProperty("code").GetInt32());
    }
}
