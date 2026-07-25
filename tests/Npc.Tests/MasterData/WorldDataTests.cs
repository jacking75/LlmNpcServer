using System.Text.Json;

namespace Npc.Tests.MasterData;

/// <summary>masterdata/zones.json · pois.json 의 무결성. docs/01 §4.</summary>
public sealed class WorldDataTests
{
    /// <summary>docs/01 §4 의 POI 타입 8종.</summary>
    private static readonly string[] s_poiTypes =
        ["home", "workplace", "market", "tavern", "temple", "gate", "field", "wilderness"];

    /// <summary>POI 타입 → 그 POI 에 있을 때 서는 플래그.</summary>
    private static readonly Dictionary<string, string> s_typeGrants = new(StringComparer.Ordinal)
    {
        ["home"] = "AtHome",
        ["workplace"] = "AtWorkplace",
        ["market"] = "AtMarket",
        ["tavern"] = "AtTavern",
        ["temple"] = "AtTemple",
        ["gate"] = "AtGate",
        ["field"] = "AtField",
        ["wilderness"] = "InWilderness",
    };

    private static readonly JsonDocument s_zones = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(TestPaths.MasterData, "zones.json")));

    private static readonly JsonDocument s_pois = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(TestPaths.MasterData, "pois.json")));

    private static JsonElement[] Zones() => s_zones.RootElement.GetProperty("zones").EnumerateArray().ToArray();

    private static JsonElement[] Pois() => s_pois.RootElement.GetProperty("pois").EnumerateArray().ToArray();

    [Fact]
    public void ZonesJson_HasTwelveZones()
    {
        Assert.Equal(12, Zones().Length);
    }

    [Fact]
    public void ZonesJson_CodesAndIdsAreUnique()
    {
        JsonElement[] zones = Zones();

        Assert.Equal(zones.Length, zones.Select(z => z.GetProperty("code").GetInt32()).Distinct().Count());
        Assert.Equal(zones.Length, zones.Select(z => z.GetProperty("id").GetString()).Distinct().Count());
    }

    [Fact]
    public void ZonesJson_AdjacencyIsSymmetricAndKnown()
    {
        Dictionary<string, string[]> adj = Zones().ToDictionary(
            z => z.GetProperty("id").GetString()!,
            z => z.GetProperty("adjacent").EnumerateArray().Select(a => a.GetString()!).ToArray(),
            StringComparer.Ordinal);

        foreach ((string zone, string[] neighbours) in adj)
        {
            foreach (string n in neighbours)
            {
                Assert.True(adj.ContainsKey(n), $"{zone} 이 없는 존 '{n}' 을 인접으로 든다.");
                Assert.Contains(zone, adj[n]);
            }
        }
    }

    /// <summary>V11 의 전제 — 고립 존이 없어야 모든 POI 가 도달 가능하다.</summary>
    [Fact]
    public void ZonesJson_GraphIsConnected()
    {
        Dictionary<string, string[]> adj = Zones().ToDictionary(
            z => z.GetProperty("id").GetString()!,
            z => z.GetProperty("adjacent").EnumerateArray().Select(a => a.GetString()!).ToArray(),
            StringComparer.Ordinal);

        HashSet<string> seen = [adj.Keys.First()];
        Stack<string> stack = new([adj.Keys.First()]);

        while (stack.Count > 0)
        {
            foreach (string n in adj[stack.Pop()])
            {
                if (seen.Add(n))
                {
                    stack.Push(n);
                }
            }
        }

        Assert.Equal(adj.Count, seen.Count);
    }

    [Fact]
    public void PoisJson_HasAroundTwoHundredFifty()
    {
        Assert.InRange(Pois().Length, 230, 270);
    }

    [Fact]
    public void PoisJson_CodesAreContiguousFromOne()
    {
        int[] codes = Pois().Select(p => p.GetProperty("code").GetInt32()).Order().ToArray();

        // 거리 행렬의 첨자로 쓰므로 빈 자리가 없어야 한다.
        Assert.Equal(Enumerable.Range(1, codes.Length).ToArray(), codes);
    }

    [Fact]
    public void PoisJson_IdsAreUnique()
    {
        string[] ids = Pois().Select(p => p.GetProperty("id").GetString()!).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PoisJson_ZoneReferencesAreValid()
    {
        HashSet<string> zoneIds = Zones().Select(z => z.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (JsonElement poi in Pois())
        {
            string zone = poi.GetProperty("zone").GetString()!;
            Assert.True(zoneIds.Contains(zone), $"{poi.GetProperty("id").GetString()} 이 없는 존 '{zone}' 을 가리킨다.");
        }
    }

    [Fact]
    public void PoisJson_TypesAreTheEightKnownOnes()
    {
        foreach (JsonElement poi in Pois())
        {
            string type = poi.GetProperty("type").GetString()!;

            Assert.Contains(type, s_poiTypes);
        }

        // 8종이 전부 쓰인다.
        Assert.Equal(
            s_poiTypes.Order().ToArray(),
            Pois().Select(p => p.GetProperty("type").GetString()!).Distinct().Order().ToArray());
    }

    [Fact]
    public void PoisJson_GrantsMatchType()
    {
        foreach (JsonElement poi in Pois())
        {
            string type = poi.GetProperty("type").GetString()!;
            string[] grants = poi.GetProperty("grants").EnumerateArray().Select(g => g.GetString()!).ToArray();

            Assert.Equal([s_typeGrants[type]], grants);
        }
    }

    [Fact]
    public void PoisJson_EveryZoneHasHomesAndEveryPoiHasCapacity()
    {
        var homesPerZone = Pois()
            .Where(p => p.GetProperty("type").GetString() == "home")
            .GroupBy(p => p.GetProperty("zone").GetString()!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (JsonElement zone in Zones())
        {
            string id = zone.GetProperty("id").GetString()!;
            Assert.True(homesPerZone.ContainsKey(id), $"존 {id} 에 주거 POI 가 없다.");
        }

        foreach (JsonElement poi in Pois())
        {
            Assert.True(poi.GetProperty("capacity").GetInt32() > 0);
        }
    }

    /// <summary>주거 정원의 합이 NPC 5,000 을 수용해야 한다 (docs/11 §8).</summary>
    [Fact]
    public void PoisJson_HomeCapacityHousesFiveThousand()
    {
        int homeCapacity = Pois()
            .Where(p => p.GetProperty("type").GetString() == "home")
            .Sum(p => p.GetProperty("capacity").GetInt32());

        Assert.True(homeCapacity >= 5_000, $"주거 정원 합이 {homeCapacity} 로 5,000 에 못 미친다.");
    }

    /// <summary>존 정원의 합도 5,000 을 수용해야 한다.</summary>
    [Fact]
    public void ZonesJson_CapacityHousesFiveThousand()
    {
        int total = Zones().Sum(z => z.GetProperty("capacity").GetInt32());

        Assert.True(total >= 5_000, $"존 정원 합이 {total} 로 5,000 에 못 미친다.");
    }

    [Fact]
    public void PoisJson_OpenHoursUseKnownTimeOfDay()
    {
        string[] timeOfDay = ["Dawn", "Morning", "Noon", "Afternoon", "Evening", "Night"];

        foreach (JsonElement poi in Pois())
        {
            JsonElement hours = poi.GetProperty("open_hours");

            Assert.Contains(hours.GetProperty("from").GetString()!, timeOfDay);
            Assert.Contains(hours.GetProperty("to").GetString()!, timeOfDay);
        }
    }

    /// <summary>field 타입은 채집 가능 자원을 들고 있어야 한다 — 없으면 채집 액션이 갈 곳이 없다.</summary>
    [Fact]
    public void PoisJson_FieldPoisDeclareResources()
    {
        using JsonDocument items = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestPaths.MasterData, "items.json")));

        HashSet<string> itemIds = items.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (JsonElement poi in Pois())
        {
            string[] resources = poi.GetProperty("resources").EnumerateArray()
                .Select(r => r.GetString()!).ToArray();

            if (poi.GetProperty("type").GetString() == "field")
            {
                Assert.NotEmpty(resources);
            }

            foreach (string r in resources)
            {
                Assert.True(itemIds.Contains(r), $"{poi.GetProperty("id").GetString()} 이 없는 아이템 '{r}' 을 낸다.");
            }
        }
    }
}
