using System.Text.Json;
using Npc.Contracts;
using Npc.Core;

namespace Npc.Tests.Planning;

/// <summary>docs/01 §6. 버킷 인덱스가 0..2879 전단사여야 플랜 스토어가 고정 배열로 성립한다.</summary>
public sealed class BucketKeyTests
{
    private static readonly JsonDocument s_buckets = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(TestPaths.MasterData, "context_buckets.json")));

    [Fact]
    public void BucketKey_IndexIsBijective()
    {
        var seen = new bool[BucketKey.TotalKeys];

        for (int a = 0; a < BucketKey.ArchetypeCount; a++)
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

                        Assert.InRange(index, 0, BucketKey.TotalKeys - 1);
                        Assert.False(seen[index], $"인덱스 {index} 가 두 번 나왔다 ({key}).");
                        seen[index] = true;

                        Assert.Equal(key, BucketKey.FromIndex(index));
                    }
                }
            }
        }

        Assert.DoesNotContain(false, seen);
    }

    [Fact]
    public void BucketKey_TotalKeysIsTwentyEightEighty()
    {
        Assert.Equal(2880, BucketKey.TotalKeys);
        Assert.Equal(2880, s_buckets.RootElement.GetProperty("total_keys").GetInt32());
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
        Assert.Equal(BucketKey.ArchetypeCount, archetypeCount);
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
        Assert.Throws<ArgumentOutOfRangeException>(() => BucketKey.FromIndex(BucketKey.TotalKeys));
        Assert.Throws<ArgumentOutOfRangeException>(() => BucketKey.FromIndex(-1));
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
