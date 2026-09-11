using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.Memory;
using Npc.TestGameServer;
using Npc.TestGameServer.World;

namespace Npc.Tests.TestBed;

/// <summary>
/// D-03 — <b>기억을 쓰는 것은 게임서버다.</b>
///
/// <para>
/// NPC 서버는 밴드를 읽기만 한다. 그 읽기가 의미를 가지려면 쓰는 쪽이 실제로 있어야 하고,
/// 대역이 그 자리다 — 이 테스트가 없으면 D-03 은 "읽을 것이 없는 읽기" 로 남는다.
/// </para>
///
/// <para>
/// 로드맵의 완료 조건("세 번 거래한 상인이 <c>friendly</c>")을 <b>대역의 실제 경로</b>로 확인한다.
/// </para>
/// </summary>
[Trait("Category", "TestBed")]
public sealed class MemoryWriteTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static readonly NpcInstanceTable s_instances = NpcInstanceTable.Load(
        Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

    /// <summary>
    /// 상호작용 세 번이면 우호다. <b>대역의 상수를 그대로 쓴다</b> —
    /// 여기 10 을 적으면 대역이 값을 바꿀 때 이 테스트가 거짓으로 통과한다.
    /// </summary>
    [Fact]
    public async Task ThreeInteractions_MakeTheNpcFriendly()
    {
        var store = new InMemoryStore();
        (PlayerRegistry players, GameWorld world, PlayerId player) = Rig(store);

        const int Slot = 0;

        // 사거리 안으로 옮긴다. 밖이면 이벤트도 기억도 나가지 않는다.
        players.Teleport(player, world.World.PositionOf(Slot));

        for (int i = 1; i <= 3; i++)
        {
            Assert.True(players.TryInteract(player, Slot, new Tick(i * 10)), $"{i}번째 상호작용이 실패했다");
        }

        int npc = world.World.NpcIdOf(Slot).Value;

        Assert.Equal(RelationshipBand.Friendly, await store.BandAsync(npc, player.Value));

        Relationship relationship = Assert.NotNull(await store.RelationshipAsync(npc, player.Value));

        Assert.Equal(PlayerRegistry.InteractAffinity * 3, relationship.Affinity);
        Assert.Equal(3, relationship.Interactions);
        Assert.Equal(RelationshipTags.Talked, relationship.Tags);

        // 기억도 같이 남는다. <b>자연어가 아니라 키다</b> (CLAUDE.md §2.5).
        Assert.All(
            await store.EpisodesAsync(npc, 10),
            e => Assert.StartsWith("dialogue.", e.SummaryKey, StringComparison.Ordinal));
    }

    /// <summary>공격 한 번이 우호를 되돌린다 — 밴드가 실제로 움직여야 의미가 있다.</summary>
    [Fact]
    public async Task AnAttack_UndoesTheGoodwill()
    {
        var store = new InMemoryStore();
        (PlayerRegistry players, GameWorld world, PlayerId player) = Rig(store);

        const int Slot = 0;

        players.Teleport(player, world.World.PositionOf(Slot));

        for (int i = 1; i <= 3; i++)
        {
            players.TryInteract(player, Slot, new Tick(i * 10));
        }

        int npc = world.World.NpcIdOf(Slot).Value;

        Assert.Equal(RelationshipBand.Friendly, await store.BandAsync(npc, player.Value));

        Assert.True(players.TryAttack(player, Slot, 5, new Tick(100)));

        Assert.Equal(RelationshipBand.Neutral, await store.BandAsync(npc, player.Value));
    }

    /// <summary>저장소를 안 주면 아무것도 쓰지 않는다 — <b>기본이 꺼짐이다</b>.</summary>
    [Fact]
    public void WithoutAStore_NothingIsWritten()
    {
        (PlayerRegistry players, GameWorld world, PlayerId player) = Rig(memory: null);

        players.Teleport(player, world.World.PositionOf(0));

        Assert.True(players.TryInteract(player, 0, new Tick(10)));
        Assert.Equal(0, players.MemoryWrites);
        Assert.Null(players.Memory);
    }

    private static (PlayerRegistry Players, GameWorld World, PlayerId Player) Rig(IMemoryStore? memory)
    {
        var options = new GameServerOptions
        {
            Npcs = 8,
            TimeScale = 60,
            LinkPort = 0,
            ClientPort = 0,
        };

        GameWorld world = GameWorld.Create(
            options, s_data, NpcRoster.Select(s_instances, options.Npcs), s_instances);

        var players = new PlayerRegistry(world, s_data, options) { Memory = memory };

        return (players, world, players.Add());
    }
}
