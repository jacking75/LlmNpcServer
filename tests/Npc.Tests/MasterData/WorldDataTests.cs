using System.Text.Json;

namespace Npc.Tests.MasterData;

/// <summary>
/// masterdata/zones.json · pois.json 중 <b>로더도 V1~V11 도 보지 않는 것</b>만 남긴다.
/// code 중복·존 참조·플래그 참조·아이템 참조·PoiType·TimeOfDay 는 <see cref="Npc.MasterData.MasterDataLoader"/> 가 던지고,
/// 인접 대칭·그래프 연결·정원 총합은 V10/V11 이 잡는다 — 여기서 다시 세지 않는다.
/// </summary>
public sealed class WorldDataTests
{
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

    /// <summary>
    /// 로더는 POI id 를 <c>byId[dto.Id] = def</c> 로 <b>덮어쓴다</b> — 중복이 있어도 던지지 않고
    /// 나중 것이 이긴다. <c>$home</c> 같은 심볼이 엉뚱한 POI 로 풀리는 경로라 여기서 본다.
    /// </summary>
    [Fact]
    public void PoisJson_IdsAreUnique()
    {
        string[] ids = Pois().Select(p => p.GetProperty("id").GetString()!).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>타입과 grants 의 대응. 어긋나면 POI 에 도착해도 전제조건 플래그가 안 서서 다음 스텝이 전부 실패한다.</summary>
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

    /// <summary>V10 은 주거 정원의 <b>총합</b>만 본다. 존 하나가 통째로 주거를 안 가진 것은 총합으로 안 드러난다.</summary>
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

    /// <summary>field 타입은 채집 가능 자원을 들고 있어야 한다 — 없으면 채집 액션이 갈 곳이 없다.</summary>
    [Fact]
    public void PoisJson_FieldPoisDeclareResources()
    {
        foreach (JsonElement poi in Pois())
        {
            if (poi.GetProperty("type").GetString() != "field")
            {
                continue;
            }

            Assert.NotEmpty(poi.GetProperty("resources").EnumerateArray().ToArray());
        }
    }
}
