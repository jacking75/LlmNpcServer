using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;

namespace Npc.Tests.Planning;

/// <summary>docs/01 §6. 버킷 인덱스가 전단사여야 플랜 스토어가 고정 배열로 성립한다.</summary>
public sealed class BucketKeyTests
{
    private static readonly JsonDocument s_buckets = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(TestPaths.MasterData, "context_buckets.json")));

    [Fact]
    public void BucketKey_IndexIsBijective()
    {
        var seen = new bool[TestPaths.TotalKeys];

        for (int a = 0; a < TestPaths.ArchetypeCount; a++)
        {
            for (int t = 0; t < BucketKey.TimeOfDayCount; t++)
            {
                for (int r = 0; r < BucketKey.RegionStateCount; r++)
                {
                    for (int c = 0; c < BucketKey.ClimateCount; c++)
                    {
                        var key = new BucketKey(
                            new ArchetypeId((ushort)a), (TimeOfDay)t, (RegionState)r, (Climate)c);
                        int index = key.ToIndex();

                        Assert.InRange(index, 0, TestPaths.TotalKeys - 1);
                        Assert.False(seen[index], $"인덱스 {index} 가 두 번 나왔다 ({key}).");
                        seen[index] = true;

                        Assert.Equal(key, BucketKey.FromIndex(index));
                    }
                }
            }
        }

        Assert.DoesNotContain(false, seen);
    }

    /// <summary>
    /// F-05 — 키 공간의 크기는 masterdata 가 정한다.
    ///
    /// <b>여기에 2,880 을 적지 않는다.</b> 적는 순간 아키타입 하나 추가에 테스트가 깨지고,
    /// 그것이 F-05 가 코드에서 없앤 바로 그 결손이다 — 상수를 테스트로 옮긴 것에 지나지 않는다.
    /// </summary>
    [Fact]
    public void BucketSpace_SizeComesFromMasterData()
    {
        MasterDataSet data = MasterDataLoader.Load(TestPaths.MasterData);

        Assert.Equal(data.Archetypes.Count, data.Buckets.ArchetypeCount);
        Assert.Equal(data.Archetypes.Count * BucketKey.PerArchetype, data.Buckets.TotalKeys);

        // 선언값은 사람이 읽는 기록이다. 코드는 읽지 않지만 V6 이 대조하므로 여기서도 본다.
        Assert.Equal(data.Buckets.TotalKeys, data.Buckets.DeclaredTotalKeys);
        Assert.Equal(
            data.Buckets.TotalKeys, s_buckets.RootElement.GetProperty("total_keys").GetInt32());
    }

    /// <summary>아키타입 수가 권장·하드 상한 안인가.</summary>
    [Fact]
    public void BucketSpace_ArchetypeCountIsWithinLimits()
    {
        Assert.InRange(TestPaths.ArchetypeCount, 1, BucketSpace.RecommendedMaxArchetypes);

        // 하드 상한은 npc_ref 의 payload 다 — 넘으면 nearest:<archetype> 을 인코딩할 수 없다.
        Assert.True(BucketSpace.MaxArchetypes > BucketSpace.RecommendedMaxArchetypes);
    }

    /// <summary>V6 — context_buckets.json 의 total_keys 가 실제 조합 수와 같아야 한다.</summary>
    [Fact]
    public void ContextBucketsJson_TotalKeysMatchesDimensions()
    {
        JsonElement dims = s_buckets.RootElement.GetProperty("dimensions");

        int time = dims.GetProperty("time_of_day").GetProperty("values").GetArrayLength();
        int region = dims.GetProperty("region_state").GetProperty("values").GetArrayLength();
        int climate = dims.GetProperty("climate").GetProperty("values").GetArrayLength();

        using JsonDocument archetypes = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestPaths.MasterData, "archetypes.json")));
        int archetypeCount = archetypes.RootElement.GetProperty("archetypes").GetArrayLength();

        Assert.Equal(BucketKey.TimeOfDayCount, time);
        Assert.Equal(BucketKey.RegionStateCount, region);
        Assert.Equal(BucketKey.ClimateCount, climate);
        Assert.Equal(TestPaths.ArchetypeCount, archetypeCount);
        Assert.Equal(
            s_buckets.RootElement.GetProperty("total_keys").GetInt32(),
            archetypeCount * time * region * climate);
    }

    /// <summary>열거형 ordinal 이 JSON 의 values 순서와 같아야 한다 — GameEvent.Code 가 이 값을 싣는다.</summary>
    [Fact]
    public void BucketKey_EnumOrdinalsMatchJsonOrder()
    {
        JsonElement dims = s_buckets.RootElement.GetProperty("dimensions");

        AssertOrder<TimeOfDay>(dims.GetProperty("time_of_day"));
        AssertOrder<RegionState>(dims.GetProperty("region_state"));
        AssertOrder<Climate>(dims.GetProperty("climate"));

        static void AssertOrder<TEnum>(JsonElement dimension) where TEnum : struct, Enum
        {
            string[] values = dimension.GetProperty("values").EnumerateArray()
                .Select(v => v.GetString()!).ToArray();

            for (int i = 0; i < values.Length; i++)
            {
                Assert.True(Enum.TryParse(values[i], out TEnum parsed), $"{typeof(TEnum).Name} 에 {values[i]} 가 없다.");
                Assert.Equal(i, Convert.ToInt32(parsed, System.Globalization.CultureInfo.InvariantCulture));
            }

            Assert.Equal(values.Length, Enum.GetValues<TEnum>().Length);
        }
    }

    /// <summary>world_flag 매핑이 world_flags.json 안에 있어야 한다 (검증기 3단의 초기 상태).</summary>
    [Fact]
    public void ContextBucketsJson_WorldFlagMappingsExist()
    {
        foreach (JsonProperty dimension in s_buckets.RootElement.GetProperty("dimensions").EnumerateObject())
        {
            if (!dimension.Value.TryGetProperty("world_flag", out JsonElement map))
            {
                continue;
            }

            foreach (JsonProperty entry in map.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                Assert.True(
                    WorldFlagTable.TryParse(entry.Value.GetString(), out WorldFlags flag),
                    $"{dimension.Name}.{entry.Name} 이 없는 플래그 '{entry.Value.GetString()}' 를 가리킨다.");
                Assert.NotEqual(WorldFlags.None, flag);
            }
        }
    }

    [Fact]
    public void BucketKey_FormatMatchesPlanStoreFileName()
    {
        var key = new BucketKey(new ArchetypeId(0), TimeOfDay.Evening, RegionState.War, Climate.Cold);

        Assert.Equal("blacksmith@Evening.War.Cold", key.Format("blacksmith"));
        Assert.Equal("0@Evening.War.Cold", key.ToString());
    }

    [Fact]
    public void BucketKey_FromIndexRejectsOutOfRange()
    {
        // BucketKey 는 아키타입 수를 모른다 (F-05). ArchetypeId 가 넘치는 것만 막는다.
        Assert.Throws<ArgumentOutOfRangeException>(() => BucketKey.FromIndex(-1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BucketKey.FromIndex(((ushort.MaxValue + 1) * BucketKey.PerArchetype) + 1));

        // 로스터 상한을 아는 것은 BucketSpace 다.
        BucketSpace space = MasterDataLoader.Load(TestPaths.MasterData).Buckets;

        Assert.Throws<ArgumentOutOfRangeException>(() => space.FromIndex(space.TotalKeys));
        Assert.Throws<ArgumentOutOfRangeException>(() => space.FromIndex(-1));
        Assert.Equal(BucketKey.FromIndex(7), space.FromIndex(7));
        Assert.False(space.Contains(space.TotalKeys));
        Assert.True(space.Contains(BucketKey.FromIndex(space.TotalKeys - 1)));
    }

    [Fact]
    public void BucketKey_SameContextIgnoresArchetype()
    {
        var a = new BucketKey(new ArchetypeId(0), TimeOfDay.Noon, RegionState.Peace, Climate.Fair);
        var b = new BucketKey(new ArchetypeId(31), TimeOfDay.Noon, RegionState.Peace, Climate.Fair);
        var c = new BucketKey(new ArchetypeId(0), TimeOfDay.Night, RegionState.Peace, Climate.Fair);

        Assert.True(a.SameContext(b));
        Assert.False(a.SameContext(c));
    }
}
