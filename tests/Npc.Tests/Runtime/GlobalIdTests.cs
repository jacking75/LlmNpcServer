using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>
/// A-08 — 전역 <c>NpcId</c>.
///
/// <para>
/// <b>왜 이 구분이 필요한가.</b> 계약은 처음부터 <c>NpcId</c> 를 "npc_instances.json 의 id" 로
/// 적어 뒀지만 런타임은 슬롯 첨자를 그대로 실어 보내고 있었다. 존을 나눠 두 NPC 서버를 띄우는
/// 순간 양쪽의 슬롯 7번은 서로 다른 NPC 이고, 그 상태로 명령이 오가면
/// <b>대장장이에게 밭을 갈라고 명령</b>하게 된다.
/// </para>
/// </summary>
public sealed class GlobalIdTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>왕복. 묶은 뒤에는 양방향이 맞아야 한다.</summary>
    [Fact]
    public void Map_RoundTrips()
    {
        var map = new GlobalIdMap(100);

        Assert.Equal(100, map.MaxGlobalId);
        Assert.Equal(0, map.Bound);
        Assert.Equal(GlobalIdMap.NotFound, map.SlotOf(7));

        Assert.True(map.Bind(7, 3));

        Assert.Equal(3, map.SlotOf(7));
        Assert.Equal(1, map.Bound);

        // 같은 id 를 다른 슬롯으로 옮긴다. 이중 계수가 없어야 한다.
        Assert.True(map.Bind(7, 5));

        Assert.Equal(5, map.SlotOf(7));
        Assert.Equal(1, map.Bound);

        Assert.True(map.Unbind(7));
        Assert.Equal(GlobalIdMap.NotFound, map.SlotOf(7));
        Assert.Equal(0, map.Bound);

        // 멱등 (N7).
        Assert.False(map.Unbind(7));
        Assert.Equal(0, map.Bound);
    }

    /// <summary>
    /// <b>용량 밖·0 은 묶지 않는다.</b> 0 은 "없음" 이고, 용량 밖은 틱 루프에서 배열을
    /// 늘릴 수 없으므로 (CLAUDE.md §2.1) 거절만이 유일한 선택지다.
    /// </summary>
    [Fact]
    public void Map_RefusesZeroAndOutOfRange()
    {
        var map = new GlobalIdMap(10);

        Assert.False(map.Bind(0, 1));
        Assert.False(map.Bind(11, 1));
        Assert.False(map.Bind(-1, 1));
        Assert.False(map.Bind(5, -1));

        Assert.Equal(0, map.Bound);
        Assert.Equal(GlobalIdMap.NotFound, map.SlotOf(11));
        Assert.Equal(GlobalIdMap.NotFound, map.SlotOf(-1));
    }

    /// <summary><b>조회는 할당 0 이다.</b> 이벤트 배수 구간에서 이벤트마다 돈다.</summary>
    [Fact]
    public void Map_LookupDoesNotAllocate()
    {
        var map = new GlobalIdMap(1_000);

        for (int i = 1; i <= 100; i++)
        {
            map.Bind(i, i - 1);
        }

        // 예열.
        for (int i = 0; i < 8; i++)
        {
            _ = map.SlotOf(i + 1);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int sum = 0;

        for (int i = 0; i < 10_000; i++)
        {
            sum += map.SlotOf((i % 100) + 1);
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(delta == 0, $"조회 10,000회에 {delta}B 할당됐다");
        Assert.True(sum > 0);
    }

    /// <summary>
    /// <see cref="NpcStore"/> 의 두 방향이 같이 움직인다. 한쪽만 쓰면 어긋난다.
    /// </summary>
    [Fact]
    public void Store_BindKeepsBothDirections()
    {
        NpcStore store = NewStore(8, maxGlobalId: 500);

        Assert.True(store.Bind(3, 42));

        Assert.Equal(42, store.Occupant[3]);
        Assert.Equal(42, store.GlobalOf(3));
        Assert.Equal(3, store.SlotOf(42));

        // 같은 슬롯에 다른 거주자. 옛 id 는 풀려야 한다.
        Assert.True(store.Bind(3, 77));

        Assert.Equal(77, store.GlobalOf(3));
        Assert.Equal(3, store.SlotOf(77));
        Assert.Equal(GlobalIdMap.NotFound, store.SlotOf(42));

        store.ClearSlot(3);

        Assert.Equal(0, store.GlobalOf(3));
        Assert.Equal(GlobalIdMap.NotFound, store.SlotOf(77));
    }

    /// <summary>
    /// <b>빈 슬롯은 앞에서부터 고른다.</b> 결정론이 필요하다 — 무작위로 고르면 같은
    /// 스폰 순서가 회차마다 다른 슬롯 배치를 만들고, 리플레이가 안 맞는다 (§2.3).
    /// </summary>
    [Fact]
    public void Store_FreeSlotIsDeterministic()
    {
        NpcStore store = NewStore(4, maxGlobalId: 500);

        Assert.Equal(0, store.FreeSlot());

        store.Bind(0, 10);
        store.Bind(1, 11);

        Assert.Equal(2, store.FreeSlot());

        store.ClearSlot(0);

        Assert.Equal(0, store.FreeSlot());

        store.Bind(0, 10);
        store.Bind(2, 12);
        store.Bind(3, 13);

        Assert.Equal(-1, store.FreeSlot());
    }

    /// <summary>
    /// <b>역방향 표는 파생물이라 스냅샷에 담지 않는다</b> — 담으면 <c>Occupant</c> 와
    /// 어긋난 스냅샷이 존재할 수 있게 된다. 복원이 다시 세운다.
    /// </summary>
    [Fact]
    public void Store_RebuildsTheMapFromOccupant()
    {
        NpcStore store = NewStore(4, maxGlobalId: 500);

        store.Occupant[0] = 100;
        store.Occupant[2] = 300;

        store.RebuildIds();

        Assert.Equal(0, store.SlotOf(100));
        Assert.Equal(2, store.SlotOf(300));
        Assert.Equal(GlobalIdMap.NotFound, store.SlotOf(200));
        Assert.Equal(2, store.Ids.Bound);
    }

    /// <summary>
    /// <b>로스터가 뽑은 id 는 듬성듬성하다.</b> 균등 간격으로 뽑으므로 1..N 이 아니고,
    /// 그래서 역방향 표의 용량은 로스터 크기가 아니라 <b>인스턴스 표 전체</b> 기준이다.
    /// </summary>
    [Fact]
    public void Roster_SelectsSparseIds()
    {
        NpcInstanceTable instances = NpcInstanceTable.Load(
            Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

        NpcRoster roster = NpcRoster.Select(instances, 50);

        Assert.Equal(50, roster.Count);

        int max = 0;

        foreach (NpcInstanceDef def in roster.Npcs)
        {
            max = Math.Max(max, def.Id);
        }

        // 50 마리를 뽑았는데 가장 큰 id 는 50 을 훨씬 넘는다 — 앞에서 자르지 않기 때문이다.
        Assert.True(max > roster.Count, $"최대 id {max} · 로스터 {roster.Count}");
    }

    private static NpcStore NewStore(int capacity, int maxGlobalId)
    {
        var store = new NpcStore();

        store.Allocate(capacity, s_data.Items.MaxCode + 1, maxGlobalId);

        return store;
    }
}
