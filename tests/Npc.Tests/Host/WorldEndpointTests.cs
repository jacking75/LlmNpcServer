using Npc.Host.Api;
using Npc.MasterData;
using Npc.Runtime;

namespace Npc.Tests.Host;

public sealed class WorldEndpointTests
{
    [Fact]
    public void DespawnedSlot_IsAbsentAfterCacheExpires()
    {
        MasterDataSet data = MasterDataLoader.Load(TestPaths.MasterData);
        var store = new NpcStore();
        store.Allocate(2, data.Items.MaxCode + 1, maxGlobalId: 202);
        Assert.True(store.Bind(0, 101));
        Assert.True(store.Bind(1, 202));
        long millis = 0;
        var world = new WorldEndpoints(store, data, new ZoneStateTable(data), () => 7, () => millis);

        Assert.Equal([101, 202], world.Npcs().Id);
        store.ClearSlot(0);
        Assert.Equal([101, 202], world.Npcs().Id); // 1초 캐시
        millis = WorldEndpoints.CacheMillis;
        Assert.Equal([202], world.Npcs().Id);
    }
}
