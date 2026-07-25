using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;

namespace Npc.Tests.MasterData;

/// <summary>docs/01 §11. 전량 로드 → 읽기 전용 인덱스 → 콘텐츠 해시.</summary>
public sealed class MasterDataSetTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    [Fact]
    public void MasterData_LoadsEveryTable()
    {
        Assert.Equal(37, s_data.Actions.Count);
        Assert.Equal(40, s_data.Archetypes.Count);
        Assert.Equal(12, s_data.Zones.Count);
        Assert.Equal(243, s_data.Pois.Count);
        Assert.True(s_data.Items.Items.Length >= 70);
        Assert.True(s_data.Interrupts.Count >= 12);
        Assert.Equal(BucketKey.TotalKeys, s_data.Buckets.DeclaredTotalKeys);
    }

    /// <summary>T1-19 완료 조건 — 동일 입력 → 동일 해시, 100회.</summary>
    [Fact]
    public void MasterData_ContentHashIsStable()
    {
        string first = s_data.ContentHash;

        Assert.Equal(64, first.Length);

        for (int i = 0; i < 100; i++)
        {
            ImmutableArray<FileHash> hashes = MasterDataLoader.HashFiles(TestPaths.MasterData);

            Assert.Equal(first, MasterDataLoader.CombineHashes(hashes));
        }
    }

    /// <summary>파일 하나가 바뀌면 전체 해시가 바뀌어야 한다 — 프리베이크 무효화의 근거다.</summary>
    [Fact]
    public void MasterData_ContentHashChangesWhenAFileChanges()
    {
        ImmutableArray<FileHash> original = MasterDataLoader.HashFiles(TestPaths.MasterData);
        string baseline = MasterDataLoader.CombineHashes(original);

        ImmutableArray<FileHash> tampered = original.SetItem(
            0, original[0] with { Sha256 = new string('0', 64) });

        Assert.NotEqual(baseline, MasterDataLoader.CombineHashes(tampered));
    }

    /// <summary>파일 순서가 달라도 같은 해시가 나와야 한다 (파일명 정렬).</summary>
    [Fact]
    public void MasterData_ContentHashIsOrderIndependent()
    {
        ImmutableArray<FileHash> original = MasterDataLoader.HashFiles(TestPaths.MasterData);
        ImmutableArray<FileHash> reversed = [.. original.Reverse()];

        Assert.Equal(MasterDataLoader.CombineHashes(original), MasterDataLoader.CombineHashes(reversed));
    }

    [Fact]
    public void MasterData_ExposesPerFileHashes()
    {
        foreach (string name in new[] { "actions.json", "pois.json", "poi_distances.bin" })
        {
            Assert.NotNull(s_data.HashOf(name));
        }

        Assert.Null(s_data.HashOf("does_not_exist.json"));
    }

    [Fact]
    public void PoiTable_DistanceMatchesMatrixAndIsSymmetric()
    {
        PoiDef a = s_data.Pois.Pois[0];
        PoiDef b = s_data.Pois.Pois[100];

        Assert.Equal(0f, s_data.Pois.Distance(a.Code, a.Code));
        Assert.Equal(s_data.Pois.Distance(a.Code, b.Code), s_data.Pois.Distance(b.Code, a.Code));
        Assert.True(s_data.Pois.Distance(a.Code, b.Code) > 0);
    }

    [Fact]
    public void PoiTable_DistanceDoesNotAllocate()
    {
        PoiId a = s_data.Pois.Pois[0].Code;
        PoiId b = s_data.Pois.Pois[50].Code;

        // JIT 티어 승격이 끝날 때까지 돌린다. 승격 시점에 한 번 할당이 잡힌다.
        for (int i = 0; i < 30_000; i++)
        {
            _ = s_data.Pois.Distance(a, b);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            _ = s_data.Pois.Distance(a, b);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void PoiTable_IndexesByZoneTypeAndSubtype()
    {
        int fromZones = s_data.Zones.Zones.Sum(z => s_data.Pois.InZone(z.Code).Length);
        int fromTypes = Enum.GetValues<PoiType>().Sum(t => s_data.Pois.OfType(t).Length);

        Assert.Equal(s_data.Pois.Count, fromZones);
        Assert.Equal(s_data.Pois.Count, fromTypes);

        Assert.NotEmpty(s_data.Pois.OfSubtype("smithy"));
        Assert.Empty(s_data.Pois.OfSubtype("no_such_subtype"));

        foreach (PoiId id in s_data.Pois.OfSubtype("smithy"))
        {
            Assert.Equal("smithy", s_data.Pois[id].Subtype);
        }
    }

    [Fact]
    public void PoiTable_AllowedArchetypeMaskWorks()
    {
        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));
        Assert.True(s_data.Archetypes.TryGet("farmer", out ArchetypeDef farmer));

        PoiDef smithy = s_data.Pois[s_data.Pois.OfSubtype("smithy")[0]];
        Assert.True(smithy.Allows(smith.Code));
        Assert.False(smithy.Allows(farmer.Code));

        // 주거는 제한이 없다 (마스크 0).
        PoiDef house = s_data.Pois[s_data.Pois.OfSubtype("house")[0]];
        Assert.Equal(0UL, house.AllowedArchetypeMask);
        Assert.True(house.Allows(farmer.Code));
    }

    [Fact]
    public void ZoneTable_AdjacencyResolvesToCodes()
    {
        foreach (ZoneDef zone in s_data.Zones.Zones)
        {
            Assert.NotEmpty(zone.Adjacent);

            foreach (ZoneId neighbour in zone.Adjacent)
            {
                Assert.Contains(zone.Code, s_data.Zones[neighbour].Adjacent);
            }
        }
    }

    /// <summary>검증기 3단의 초기 상태. docs/03 §3.</summary>
    [Fact]
    public void BucketSpace_InitialFlagsFollowContextBuckets()
    {
        var night = new BucketKey(new ArchetypeId(0), TimeOfDay.Night, RegionState.Peace, Climate.Fair);
        var warStorm = new BucketKey(new ArchetypeId(0), TimeOfDay.Noon, RegionState.War, Climate.Storm);

        Assert.Equal(WorldFlags.IsNight | WorldFlags.RegionPeaceful, s_data.Buckets.InitialFlags(night));
        Assert.Equal(
            WorldFlags.IsDay | WorldFlags.RegionUnderAttack | WorldFlags.WeatherHarsh,
            s_data.Buckets.InitialFlags(warStorm));
    }

    [Fact]
    public void BucketSpace_TimeOfDayCoversTwentyFourHours()
    {
        var seen = new HashSet<TimeOfDay>();

        for (int hour = 0; hour < 24; hour++)
        {
            seen.Add(s_data.Buckets.TimeOfDayAt(hour));
        }

        Assert.Equal(Enum.GetValues<TimeOfDay>().Length, seen.Count);

        // context_buckets.json 의 game_hours 와 일치해야 한다.
        Assert.Equal(TimeOfDay.Dawn, s_data.Buckets.TimeOfDayAt(5));
        Assert.Equal(TimeOfDay.Morning, s_data.Buckets.TimeOfDayAt(7));
        Assert.Equal(TimeOfDay.Noon, s_data.Buckets.TimeOfDayAt(12));
        Assert.Equal(TimeOfDay.Afternoon, s_data.Buckets.TimeOfDayAt(15));
        Assert.Equal(TimeOfDay.Evening, s_data.Buckets.TimeOfDayAt(20));
        Assert.Equal(TimeOfDay.Night, s_data.Buckets.TimeOfDayAt(23));
        Assert.Equal(TimeOfDay.Night, s_data.Buckets.TimeOfDayAt(2));
    }

    [Fact]
    public void MasterData_MissingDirectoryThrows()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => MasterDataLoader.Load(Path.Combine(TestPaths.RepoRoot, "no_such_masterdata")));
    }
}
