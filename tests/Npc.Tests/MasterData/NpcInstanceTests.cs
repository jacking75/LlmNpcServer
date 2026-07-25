using System.Diagnostics;
using System.Text.Json;

namespace Npc.Tests.MasterData;

/// <summary>
/// docs/01 §9 · docs/11 §8. masterdata/npc_instances.json 은 산출물이다.
/// tools/gen_npcs.cs 가 만든다 — 손으로 편집하지 않는다.
/// </summary>
public sealed class NpcInstanceTests
{
    private const int Population = 5_000;
    private const int TraitSpread = 15;

    private static readonly JsonDocument s_instances = Load("npc_instances.json");
    private static readonly JsonDocument s_pois = Load("pois.json");
    private static readonly JsonDocument s_zones = Load("zones.json");
    private static readonly JsonDocument s_archetypes = Load("archetypes.json");

    private static JsonDocument Load(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.MasterData, file)));

    private static JsonElement[] Npcs => [.. s_instances.RootElement.GetProperty("npcs").EnumerateArray()];

    /// <summary>
    /// T1-56 완료 조건 — 같은 seed 로 다시 돌리면 바이트 동일한 파일이 나온다.
    /// 생성기에 시각·난수가 섞이면 여기서 깨진다.
    /// </summary>
    [Fact]
    [Trait("Category", "Determinism")]
    public void GenNpcs_IsReproducible()
    {
        string temp = Path.Combine(Path.GetTempPath(), $"npc-instances-{Guid.NewGuid():N}.json");

        try
        {
            var info = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = TestPaths.RepoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            info.ArgumentList.Add("run");
            info.ArgumentList.Add(Path.Combine("tools", "gen_npcs.cs"));
            info.ArgumentList.Add("--out");
            info.ArgumentList.Add(temp);

            using Process process = Process.Start(info)
                ?? throw new InvalidOperationException("dotnet 을 실행하지 못했다.");

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            Assert.True(process.WaitForExit(TimeSpan.FromMinutes(5)), "gen_npcs.cs 가 5분 안에 끝나지 않았다.");
            Assert.True(process.ExitCode == 0, $"gen_npcs.cs 실패 ({process.ExitCode}):\n{stdout}\n{stderr}");

            byte[] regenerated = File.ReadAllBytes(temp);
            byte[] committed = File.ReadAllBytes(Path.Combine(TestPaths.MasterData, "npc_instances.json"));

            Assert.Equal(committed.Length, regenerated.Length);
            Assert.True(
                committed.AsSpan().SequenceEqual(regenerated),
                "같은 seed 인데 바이트가 다르다. 생성기에 비결정적 입력이 섞였다.");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public void NpcInstances_HasFiveThousand()
    {
        Assert.Equal(Population, Npcs.Length);
        Assert.Equal(1, s_instances.RootElement.GetProperty("version").GetInt32());

        // seed 가 기록돼 있어야 재현할 수 있다.
        Assert.True(s_instances.RootElement.TryGetProperty("seed", out JsonElement seed));
        Assert.NotEqual(0, seed.GetInt32());

        // id 는 1..5000 연속이다.
        int expected = 1;

        foreach (JsonElement npc in Npcs)
        {
            Assert.Equal(expected++, npc.GetProperty("id").GetInt32());
        }
    }

    /// <summary>docs/11 §8 — 전원이 집을 갖는다. 일터는 아키타입이 요구할 때만 있다.</summary>
    [Fact]
    public void NpcInstances_EveryNpcHasHomeAndRequiredWorkplace()
    {
        Dictionary<string, string?> workplaceOf = s_archetypes.RootElement.GetProperty("archetypes")
            .EnumerateArray()
            .ToDictionary(
                a => a.GetProperty("id").GetString()!,
                a => a.GetProperty("workplace_poi_type").ValueKind == JsonValueKind.Null
                    ? null
                    : a.GetProperty("workplace_poi_type").GetString(),
                StringComparer.Ordinal);

        Dictionary<string, JsonElement> poiOf = s_pois.RootElement.GetProperty("pois")
            .EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetString()!, p => p, StringComparer.Ordinal);

        foreach (JsonElement npc in Npcs)
        {
            string archetype = npc.GetProperty("archetype").GetString()!;
            string home = npc.GetProperty("home_poi").GetString()!;

            Assert.True(poiOf.TryGetValue(home, out JsonElement homePoi), $"없는 집 '{home}'");
            Assert.Equal("home", homePoi.GetProperty("type").GetString());

            // zone 필드는 집이 있는 존이다.
            Assert.Equal(homePoi.GetProperty("zone").GetString(), npc.GetProperty("zone").GetString());

            JsonElement workplace = npc.GetProperty("workplace_poi");
            string? required = workplaceOf[archetype];

            if (required is null)
            {
                Assert.Equal(JsonValueKind.Null, workplace.ValueKind);
                continue;
            }

            string id = workplace.GetString()!;

            Assert.True(poiOf.TryGetValue(id, out JsonElement workPoi), $"없는 일터 '{id}'");
            Assert.Equal(required, workPoi.GetProperty("subtype").GetString());

            // 그 일터가 이 아키타입을 받는가 (docs/01 §4).
            string[] allowed = [.. workPoi.GetProperty("allowed_archetypes").EnumerateArray().Select(a => a.GetString()!)];

            Assert.True(
                allowed.Length == 0 || allowed.Contains(archetype, StringComparer.Ordinal),
                $"{archetype} 가 '{id}' 에 배정됐는데 allowed_archetypes 에 없다.");
        }
    }

    /// <summary>V10 — 어떤 POI 도 정원을 넘지 않는다.</summary>
    [Fact]
    public void NpcInstances_RespectsPoiCapacity()
    {
        Dictionary<string, int> capacity = s_pois.RootElement.GetProperty("pois")
            .EnumerateArray()
            .ToDictionary(
                p => p.GetProperty("id").GetString()!,
                p => p.GetProperty("capacity").GetInt32(),
                StringComparer.Ordinal);

        var used = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (JsonElement npc in Npcs)
        {
            used[npc.GetProperty("home_poi").GetString()!] =
                used.GetValueOrDefault(npc.GetProperty("home_poi").GetString()!) + 1;

            JsonElement workplace = npc.GetProperty("workplace_poi");

            if (workplace.ValueKind != JsonValueKind.Null)
            {
                used[workplace.GetString()!] = used.GetValueOrDefault(workplace.GetString()!) + 1;
            }
        }

        foreach ((string poi, int count) in used.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Assert.True(count <= capacity[poi], $"'{poi}' 정원 {capacity[poi]} 에 {count} 마리가 들어갔다.");
        }
    }

    /// <summary>docs/11 §8 — 존별 인구가 zones.capacity 이내다.</summary>
    [Fact]
    public void NpcInstances_RespectsZoneCapacity()
    {
        Dictionary<string, int> capacity = s_zones.RootElement.GetProperty("zones")
            .EnumerateArray()
            .ToDictionary(
                z => z.GetProperty("id").GetString()!,
                z => z.GetProperty("capacity").GetInt32(),
                StringComparer.Ordinal);

        var population = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (JsonElement npc in Npcs)
        {
            string zone = npc.GetProperty("zone").GetString()!;
            population[zone] = population.GetValueOrDefault(zone) + 1;
        }

        foreach ((string zone, int count) in population.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Assert.True(count <= capacity[zone], $"존 '{zone}' 정원 {capacity[zone]} 에 {count} 마리가 산다.");
        }

        Assert.Equal(Population, population.Values.Sum());
    }

    /// <summary>docs/01 §9 — 개체 편차는 ±15 다.</summary>
    [Fact]
    public void NpcInstances_TraitOffsetsAreWithinSpread()
    {
        Dictionary<string, string[]> traitsOf = s_archetypes.RootElement.GetProperty("archetypes")
            .EnumerateArray()
            .ToDictionary(
                a => a.GetProperty("id").GetString()!,
                a => a.GetProperty("traits").EnumerateObject().Select(t => t.Name).ToArray(),
                StringComparer.Ordinal);

        var distinct = new HashSet<int>();

        foreach (JsonElement npc in Npcs)
        {
            string[] traits = traitsOf[npc.GetProperty("archetype").GetString()!];
            JsonElement offsets = npc.GetProperty("trait_offsets");

            Assert.Equal(traits.Length, offsets.EnumerateObject().Count());

            foreach (string trait in traits)
            {
                int offset = offsets.GetProperty(trait).GetInt32();

                Assert.InRange(offset, -TraitSpread, TraitSpread);
                distinct.Add(offset);
            }
        }

        // 전부 0 이면 다양성의 원천이 없는 것이다.
        Assert.Equal(2 * TraitSpread + 1, distinct.Count);
    }

    /// <summary>인구가 population_weight 를 따른다 (최대잔여법이라 오차는 1 이내).</summary>
    [Fact]
    public void NpcInstances_MatchesPopulationWeights()
    {
        var actual = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (JsonElement npc in Npcs)
        {
            string archetype = npc.GetProperty("archetype").GetString()!;
            actual[archetype] = actual.GetValueOrDefault(archetype) + 1;
        }

        foreach (JsonElement archetype in s_archetypes.RootElement.GetProperty("archetypes").EnumerateArray())
        {
            string id = archetype.GetProperty("id").GetString()!;
            double expected = archetype.GetProperty("population_weight").GetDouble() * Population;

            Assert.True(
                Math.Abs(actual.GetValueOrDefault(id) - expected) < 1.0,
                $"{id}: 기대 {expected:0.##} 인데 {actual.GetValueOrDefault(id)} 마리다.");
        }
    }
}
