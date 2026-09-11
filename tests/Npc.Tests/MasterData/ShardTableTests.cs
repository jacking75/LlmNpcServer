using Npc.Contracts;
using Npc.MasterData;

namespace Npc.Tests.MasterData;

/// <summary>
/// A-08 · V15 — 샤드 정의 검증.
///
/// <para>
/// <b>겹치면 같은 NPC 를 두 프로세스가 움직인다.</b> 증상은 "가끔 NPC 가 두 곳에 있는 것처럼
/// 보인다" 이고, 그 원인을 로그에서 찾는 것은 거의 불가능하다. 그래서 로드에서 던진다 —
/// 마스터데이터 검증 V1~V13 과 같은 규칙이다 (경고 후 진행 없음).
/// </para>
/// </summary>
public sealed class ShardTableTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>저장소의 <c>deploy/shards.json</c> 이 실제로 읽히고 존을 빠짐없이 나눈다.</summary>
    [Fact]
    public void Repository_ShardsCoverEveryZoneExactlyOnce()
    {
        ShardTable table = ShardTable.Load(TestPaths.At("deploy", "shards.json"), s_data);

        Assert.True(table.Count >= 2, "샤드가 둘은 있어야 나누는 의미가 있다");

        var seen = new HashSet<ushort>();
        ulong union = 0;

        foreach (ShardDef shard in table.Shards)
        {
            Assert.NotEmpty(shard.Zones);

            foreach (ZoneId zone in shard.Zones)
            {
                Assert.True(seen.Add(zone.Value), $"존 {zone.Value} 이 두 샤드에 있다");
            }

            // 마스크와 목록이 같은 것을 말해야 한다.
            Assert.Equal(ShardTable.MaskOf([.. shard.Zones]), shard.Mask);

            Assert.Equal(0UL, union & shard.Mask);
            union |= shard.Mask;
        }

        // zones.json 의 존이 전부 어느 샤드엔가 있어야 한다 — 빠진 존의 NPC 는 아무도 안 맡는다.
        foreach (ZoneDef zone in s_data.Zones.Zones)
        {
            Assert.True(
                seen.Contains(zone.Code.Value),
                $"존 '{zone.Id}' 이 어느 샤드에도 없다 — 그 NPC 는 아무도 안 맡는다");
        }
    }

    /// <summary>번호로 찾는다. 모르는 번호는 null 이다 — 조용히 전체로 떨어지지 않는다.</summary>
    [Fact]
    public void TryGet_ReturnsNullForUnknownShards()
    {
        ShardTable table = ShardTable.Load(TestPaths.At("deploy", "shards.json"), s_data);

        Assert.NotNull(table.TryGet(1));
        Assert.Null(table.TryGet(999));
    }

    /// <summary><b>존이 겹치면 로드가 던진다.</b></summary>
    [Fact]
    public void Load_ThrowsWhenZonesOverlap()
    {
        string path = Write("""
            { "version": 1, "shards": [
              { "shard": 1, "zones": ["town_center", "farmlands"] },
              { "shard": 2, "zones": ["farmlands"] } ] }
            """);

        try
        {
            InvalidDataException e = Assert.Throws<InvalidDataException>(
                () => ShardTable.Load(path, s_data));

            Assert.Contains("둘 다 있다", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>모르는 존 id 는 오타다. 조용히 무시하면 그 존의 NPC 를 아무도 안 맡는다.</summary>
    [Fact]
    public void Load_ThrowsOnUnknownZone()
    {
        string path = Write("""
            { "version": 1, "shards": [ { "shard": 1, "zones": ["town_centre"] } ] }
            """);

        try
        {
            InvalidDataException e = Assert.Throws<InvalidDataException>(
                () => ShardTable.Load(path, s_data));

            Assert.Contains("town_centre", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>샤드 번호 0 은 "단일 샤드" 를 뜻하므로 정의에 쓸 수 없다.</summary>
    [Fact]
    public void Load_ThrowsOnShardZeroAndDuplicates()
    {
        string zero = Write("""
            { "version": 1, "shards": [ { "shard": 0, "zones": ["town_center"] } ] }
            """);

        string dup = Write("""
            { "version": 1, "shards": [
              { "shard": 1, "zones": ["town_center"] },
              { "shard": 1, "zones": ["farmlands"] } ] }
            """);

        try
        {
            Assert.Throws<InvalidDataException>(() => ShardTable.Load(zero, s_data));

            InvalidDataException e = Assert.Throws<InvalidDataException>(
                () => ShardTable.Load(dup, s_data));

            Assert.Contains("두 번", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(zero);
            File.Delete(dup);
        }
    }

    /// <summary>빈 샤드는 아무 NPC 도 못 맡는다. 의도한 상태일 수 없다.</summary>
    [Fact]
    public void Load_ThrowsOnEmptyShard()
    {
        string path = Write("""{ "version": 1, "shards": [ { "shard": 1, "zones": [] } ] }""");

        try
        {
            Assert.Throws<InvalidDataException>(() => ShardTable.Load(path, s_data));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// <see cref="ShardTable.Covers"/> — <b>0 은 전체</b>다. 단일 샤드 회차가 오늘과 같으려면
    /// 이 규칙이 있어야 한다.
    /// </summary>
    [Fact]
    public void Covers_TreatsZeroAsEverything()
    {
        Assert.True(ShardTable.Covers(0, new ZoneId(1)));
        Assert.True(ShardTable.Covers(0, new ZoneId(63)));

        ulong mask = ShardTable.MaskOf([new ZoneId(1), new ZoneId(3)]);

        Assert.True(ShardTable.Covers(mask, new ZoneId(1)));
        Assert.True(ShardTable.Covers(mask, new ZoneId(3)));
        Assert.False(ShardTable.Covers(mask, new ZoneId(2)));

        // 64 이상은 마스크에 담기지 않는다 — MaskOf 가 버리므로 Covers 도 false 다.
        Assert.False(ShardTable.Covers(mask, new ZoneId(64)));
    }

    private static string Write(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"npc-a08-{Guid.NewGuid():N}.json");

        File.WriteAllText(path, json);

        return path;
    }
}
