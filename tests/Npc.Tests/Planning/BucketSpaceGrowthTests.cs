using System.Text.Json;
using System.Text.Json.Nodes;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.MasterData.Validation;
using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>
/// F-05 완료 조건 — <b>아키타입 추가에 코드 변경이 0 이다.</b>
///
/// masterdata 를 복사해 아키타입을 하나 더 넣고, 코드를 한 줄도 고치지 않은 채
/// 로더·플랜 스토어·계측이 41개를 다루는지 본다. <c>BucketKey.ArchetypeCount</c> 라는
/// C# 상수였을 때는 파일이 41 인데 바이너리가 40 이어서 41번째 아키타입의 버킷 72칸이
/// 조용히 사라졌다 — 그런 오류는 런타임에 티가 나지 않는다.
/// </summary>
public sealed class BucketSpaceGrowthTests : IDisposable
{
    private const string NewId = "beekeeper_probe";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "npc-f05-" + Guid.NewGuid().ToString("N")[..8]);

    public BucketSpaceGrowthTests() => CopyWithExtraArchetype(TestPaths.MasterData, _directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void ExtraArchetype_GrowsTheKeySpaceWithoutCodeChanges()
    {
        MasterDataSet data = MasterDataLoader.Load(_directory);

        Assert.Equal(TestPaths.ArchetypeCount + 1, data.Archetypes.Count);
        Assert.Equal(data.Archetypes.Count, data.Buckets.ArchetypeCount);
        Assert.Equal(data.Archetypes.Count * BucketKey.PerArchetype, data.Buckets.TotalKeys);
        Assert.Equal(TestPaths.TotalKeys + BucketKey.PerArchetype, data.Buckets.TotalKeys);
    }

    [Fact]
    public void ExtraArchetype_PassesValidation()
    {
        // V5(가중치 합) · V6(total_keys) · V7(폴백) 이 새 아키타입을 그대로 받아야 한다.
        MasterDataValidationReport report = MasterDataValidator.Validate(_directory);

        Assert.True(
            report.Violations.IsEmpty,
            string.Join(" · ", report.Violations.Select(v => $"{v.Code} {v.Detail}")));
    }

    [Fact]
    public void ExtraArchetype_IsAddressableInThePlanStore()
    {
        MasterDataSet data = MasterDataLoader.Load(_directory);
        PlanStore store = PlanStore.CreateIdleOnly(data);

        Assert.Equal(data.Buckets.TotalKeys, store.Space.TotalKeys);
        Assert.Equal(data.Buckets.TotalKeys, store.ColdBuckets);

        // 마지막 아키타입의 72칸이 실제로 존재하는가 — 상수였을 때 사라지던 바로 그 구간이다.
        var last = new ArchetypeId((ushort)(data.Archetypes.Count - 1));

        Assert.Equal(NewId, data.Archetypes[last].Id);

        var cache = new CacheMetrics(store);

        Assert.Equal(data.Archetypes.Count, cache.ByArchetype().Length);
        Assert.Equal(BucketKey.PerArchetype, cache.ColdBucketsOf(last));

        var key = new BucketKey(last, TimeOfDay.Night, RegionState.War, Climate.Storm);

        Assert.True(data.Buckets.Contains(key));
        Assert.Equal(key, data.Buckets.FromIndex(key.ToIndex()));
    }

    /// <summary>
    /// <b>기존 40개의 첨자가 변하지 않는다.</b> 프리베이크 플랜 2,880개는 버킷 첨자로
    /// 저장·조회되므로, 아키타입을 뒤에 붙였는데 첨자가 밀리면 전량이 통째로 어긋난다
    /// (CLAUDE.md §2.4 의 "code 재배치 금지" 와 같은 근거다).
    /// </summary>
    [Fact]
    public void ExtraArchetype_LeavesExistingIndicesUnchanged()
    {
        MasterDataSet before = MasterDataLoader.Load(TestPaths.MasterData);
        MasterDataSet after = MasterDataLoader.Load(_directory);

        for (int index = 0; index < before.Buckets.TotalKeys; index++)
        {
            Assert.Equal(before.Buckets.FromIndex(index), after.Buckets.FromIndex(index));
        }

        // 새 아키타입의 칸은 전부 뒤에 붙는다.
        Assert.Equal(before.Buckets.TotalKeys + BucketKey.PerArchetype, after.Buckets.TotalKeys);
    }

    /// <summary>기존 아키타입의 id ↔ code 대응도 그대로여야 한다.</summary>
    [Fact]
    public void ExtraArchetype_LeavesExistingCodesUnchanged()
    {
        MasterDataSet before = MasterDataLoader.Load(TestPaths.MasterData);
        MasterDataSet after = MasterDataLoader.Load(_directory);

        for (int code = 0; code < before.Archetypes.Count; code++)
        {
            var id = new ArchetypeId((ushort)code);

            Assert.Equal(before.Archetypes[id].Id, after.Archetypes[id].Id);
        }
    }

    /// <summary>
    /// masterdata 를 복사하면서 아키타입을 하나 더 넣는다.
    /// 기존 아키타입 하나를 복제하고 그 <c>population_weight</c> 를 반으로 갈라 V5 합을 지킨다 —
    /// 정원(V10)도 같은 이유로 그대로다.
    /// </summary>
    private static void CopyWithExtraArchetype(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(dir));
            Directory.CreateDirectory(target);

            foreach (string file in Directory.GetFiles(dir))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }

        JsonObject archetypes = Read(destination, "archetypes.json");
        JsonArray list = archetypes["archetypes"]!.AsArray();

        JsonObject template = list[0]!.AsObject();
        var clone = JsonNode.Parse(template.ToJsonString())!.AsObject();

        double weight = template["population_weight"]!.GetValue<double>();
        string sourceId = template["id"]!.GetValue<string>();

        template["population_weight"] = weight / 2;
        clone["population_weight"] = weight - (weight / 2);
        clone["id"] = NewId;
        clone["code"] = list.Count;
        clone["name_key"] = "npc." + NewId;
        clone["fallback_plan"] = "fb_" + NewId;

        list.Add(clone);
        Write(destination, "archetypes.json", archetypes);

        JsonObject buckets = Read(destination, "context_buckets.json");
        buckets["total_keys"] = list.Count * BucketKey.PerArchetype;
        Write(destination, "context_buckets.json", buckets);

        JsonObject fallbacks = Read(destination, "fallback_plans.json");
        JsonArray plans = fallbacks["plans"]!.AsArray();

        JsonObject sourcePlan = plans.First(p => p!["archetype"]!.GetValue<string>() == sourceId)!.AsObject();
        var newPlan = JsonNode.Parse(sourcePlan.ToJsonString())!.AsObject();

        newPlan["id"] = "fb_" + NewId;
        newPlan["archetype"] = NewId;

        plans.Add(newPlan);
        Write(destination, "fallback_plans.json", fallbacks);
    }

    private static JsonObject Read(string directory, string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(directory, name)))!.AsObject();

    private static void Write(string directory, string name, JsonObject value) =>
        File.WriteAllText(
            Path.Combine(directory, name),
            value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
