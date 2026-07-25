using System.Text.Json;
using Npc.Contracts;
using Npc.MasterData;

namespace Npc.Tests.MasterData;

/// <summary>docs/01 §5. 40종 · population_weight 합 1.0 · allowed_actions 무결성.</summary>
public sealed class ArchetypeTableTests
{
    private static readonly ItemTable s_items =
        ItemTable.Load(Path.Combine(TestPaths.MasterData, "items.json"));

    private static readonly ActionCatalog s_actions =
        ActionCatalog.Load(Path.Combine(TestPaths.MasterData, "actions.json"), s_items);

    private static readonly ArchetypeTable s_table =
        ArchetypeTable.Load(Path.Combine(TestPaths.MasterData, "archetypes.json"), s_actions, s_items);

    /// <summary>docs/01 §5 의 그룹 구성안. 합이 40 이다.</summary>
    private static readonly (string Group, int Count)[] s_groups =
    [
        ("생산", 10), ("채집", 7), ("상업", 5), ("치안", 5), ("종교/학문", 4), ("주민", 6), ("특수", 3),
    ];

    [Fact]
    public void ArchetypeTable_HasFortyArchetypes()
    {
        Assert.Equal(40, s_table.Count);
        Assert.Equal(40, s_groups.Sum(g => g.Count));
    }

    /// <summary>V5 — population_weight 합 = 1.0 (±0.001).</summary>
    [Fact]
    public void ArchetypeTable_PopulationWeightSumsToOne()
    {
        Assert.Equal(1.0, s_table.TotalPopulationWeight, 3);
    }

    /// <summary>
    /// code 는 0..39 여야 한다. 버킷 키 인덱스 ((A*6+T)*4+R)*3+C 가 0..2879 전단사이려면
    /// 아키타입 첨자가 0 부터 시작해야 한다 (docs/01 §6).
    /// </summary>
    [Fact]
    public void ArchetypeTable_CodesAreZeroBasedAndContiguous()
    {
        int[] codes = s_table.Archetypes.Select(a => (int)a.Code.Value).Order().ToArray();

        Assert.Equal(Enumerable.Range(0, 40).ToArray(), codes);
    }

    [Fact]
    public void ArchetypeTable_IndexesByCode()
    {
        foreach (ArchetypeDef def in s_table.Archetypes)
        {
            Assert.Equal(def, s_table[def.Code]);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => s_table[new ArchetypeId(40)]);
    }

    /// <summary>V4 — allowed_actions 가 전부 카탈로그에 있다.</summary>
    [Fact]
    public void ArchetypeTable_AllowedActionsExistInCatalog()
    {
        foreach (ArchetypeDef def in s_table.Archetypes)
        {
            Assert.NotEmpty(def.AllowedActions);

            foreach (ActionId action in def.AllowedActions)
            {
                Assert.True(s_actions.IsDefined(action), $"{def.Id} 가 없는 액션 code {action.Value} 를 허용한다.");
                Assert.True(def.Allows(action));
            }
        }
    }

    [Fact]
    public void ArchetypeTable_AllowedMaskMatchesList()
    {
        foreach (ArchetypeDef def in s_table.Archetypes)
        {
            foreach (ActionDef action in s_actions.Actions)
            {
                Assert.Equal(def.AllowedActions.Contains(action.Code), def.Allows(action.Code));
            }
        }
    }

    /// <summary>어느 아키타입이든 하루를 닫을 수 있어야 한다 — 이동·식사·수면은 공통이다.</summary>
    [Fact]
    public void ArchetypeTable_EveryArchetypeCanMoveEatAndSleep()
    {
        foreach (string required in new[] { "MoveTo", "Eat", "Drink", "Sleep", "Rest" })
        {
            Assert.True(s_actions.TryGet(required, out ActionDef action));

            foreach (ArchetypeDef def in s_table.Archetypes)
            {
                Assert.True(def.Allows(action.Code), $"{def.Id} 가 {required} 를 못 쓴다.");
            }
        }
    }

    /// <summary>V3 — primary_recipes 가 items.json 의 레시피에 있다.</summary>
    [Fact]
    public void ArchetypeTable_PrimaryRecipesExist()
    {
        foreach (ArchetypeDef def in s_table.Archetypes)
        {
            foreach (string recipe in def.PrimaryRecipes)
            {
                Assert.True(s_items.TryGetRecipe(recipe, out _), $"{def.Id} 가 없는 레시피 '{recipe}' 를 든다.");
            }
        }
    }

    /// <summary>workplace_poi_type 은 pois.json 의 subtype 이어야 하고, 그 POI 가 이 아키타입을 받아야 한다.</summary>
    [Fact]
    public void ArchetypeTable_WorkplaceTypesResolveToPois()
    {
        using JsonDocument pois = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestPaths.MasterData, "pois.json")));

        var allowedBySubtype = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (JsonElement poi in pois.RootElement.GetProperty("pois").EnumerateArray())
        {
            string subtype = poi.GetProperty("subtype").GetString()!;
            if (!allowedBySubtype.TryGetValue(subtype, out HashSet<string>? set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                allowedBySubtype[subtype] = set;
            }

            foreach (JsonElement a in poi.GetProperty("allowed_archetypes").EnumerateArray())
            {
                set.Add(a.GetString()!);
            }
        }

        foreach (ArchetypeDef def in s_table.Archetypes)
        {
            Assert.Equal("home", def.HomePoiType);

            if (def.WorkplacePoiType is null)
            {
                continue;
            }

            Assert.True(
                allowedBySubtype.TryGetValue(def.WorkplacePoiType, out HashSet<string>? allowed),
                $"{def.Id} 의 일터 subtype '{def.WorkplacePoiType}' 인 POI 가 없다.");
            Assert.Contains(def.Id, allowed!);
        }
    }

    /// <summary>V10 — 일터 정원 합이 그 일터를 쓰는 아키타입 인구 합 이상이어야 한다.</summary>
    [Fact]
    public void ArchetypeTable_WorkplaceCapacityCoversPopulation()
    {
        using JsonDocument pois = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestPaths.MasterData, "pois.json")));

        var capacity = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (JsonElement poi in pois.RootElement.GetProperty("pois").EnumerateArray())
        {
            string subtype = poi.GetProperty("subtype").GetString()!;
            capacity[subtype] = capacity.GetValueOrDefault(subtype) + poi.GetProperty("capacity").GetInt32();
        }

        var need = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (ArchetypeDef def in s_table.Archetypes)
        {
            if (def.WorkplacePoiType is null)
            {
                continue;
            }

            need[def.WorkplacePoiType] =
                need.GetValueOrDefault(def.WorkplacePoiType) + (int)Math.Ceiling(def.PopulationWeight * 5_000);
        }

        foreach ((string subtype, int population) in need.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Assert.True(
                capacity.GetValueOrDefault(subtype) >= population,
                $"{subtype}: 정원 {capacity.GetValueOrDefault(subtype)} < 인구 {population}");
        }
    }

    [Fact]
    public void ArchetypeTable_EveryArchetypeDeclaresFallbackPlan()
    {
        foreach (ArchetypeDef def in s_table.Archetypes)
        {
            Assert.Equal($"fb_{def.Id}", def.FallbackPlanId);
            Assert.InRange(def.Description.Length, 20, 300);
            Assert.NotEmpty(def.DefaultGoals);
        }
    }

    [Fact]
    public void ArchetypeTable_TraitsAreInRange()
    {
        foreach (ArchetypeDef def in s_table.Archetypes)
        {
            foreach (TraitKind kind in Enum.GetValues<TraitKind>())
            {
                Assert.InRange(def.Traits[kind], 0, 100);
            }
        }
    }

    [Fact]
    public void ArchetypeTable_UnknownActionThrows()
    {
        const string Json = """
            {
              "version": 1,
              "archetypes": [
                {
                  "id": "ghost", "code": 0, "name_key": "npc.ghost", "desc": "없는 액션을 허용하는 아키타입이다.",
                  "allowed_actions": ["Teleport"],
                  "home_poi_type": "home", "workplace_poi_type": null,
                  "primary_recipes": [],
                  "traits": { "diligence": 50, "sociability": 50, "courage": 50, "greed": 50 },
                  "default_goals": ["haunt"], "fallback_plan": "fb_ghost",
                  "combat_capable": false, "population_weight": 1.0
                }
              ]
            }
            """;

        InvalidDataException ex =
            Assert.Throws<InvalidDataException>(() => ArchetypeTable.Parse(Json, s_actions, s_items));

        Assert.Contains("Teleport", ex.Message, StringComparison.Ordinal);
    }
}
