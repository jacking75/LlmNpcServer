using MemoryPack;
using Npc.Contracts;
using Npc.MasterData;
using Npc.TestBed.Protocol;
using Npc.TestGameServer;
using Npc.TestGameServer.Client;
using Npc.TestGameServer.World;

namespace Npc.Tests.TestBed;

/// <summary>
/// T6-24 — AOI 스냅샷 빌더. docs/20 §8.1.
///
/// <para>
/// <b>소켓이 없다.</b> 여기서 볼 것은 "무엇을 골라 담는가" 이고, 그것은 순수 계산이다 —
/// 소켓을 끼우면 느려지기만 하고 잡히는 버그는 같다.
/// </para>
/// </summary>
[Trait("Category", "TestBed")]
public sealed class SnapshotBuilderTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static readonly NpcInstanceTable s_instances = NpcInstanceTable.Load(
        Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

    /// <summary>
    /// 완료 조건 — NPC 1,000 중 <b>가까운 순 256</b> 만 담는다.
    ///
    /// 상한이 곧 대역폭 상한이다. 자르는 기준이 "가까운 순" 이 아니면 화면 한복판의 NPC 가
    /// 사라지고 먼 것이 남는, 가장 알아채기 어려운 종류의 버그가 된다.
    /// </summary>
    [Fact]
    public async Task ClientProtocol_AoiCapsAt256()
    {
        await using GameWorld world = Create(npcs: 1_000);

        var players = new PlayerRegistry(world, s_data, Options());
        var builder = new SnapshotBuilder(world, s_data, players, Options().TimeScale);

        PlayerId viewer = players.Add();
        var now = new Tick(2);

        Snapshot snapshot = builder.Build(viewer, now);

        Assert.Equal(SnapshotBuilder.MaxEntities, snapshot.EntityCount);
        Assert.NotNull(snapshot.Entities);
        Assert.Equal(snapshot.EntityCount, snapshot.Entities!.Length);

        // 같은 규칙을 테스트가 독립적으로 다시 푼다. 빌더의 힙이 틀리면 여기서 갈린다.
        WorldPos eye = players.PositionOf(viewer);

        List<(int Id, float Distance)> expected =
        [
            .. Enumerable.Range(0, world.World.Capacity)
                .Where(world.World.IsSpawned)
                .Select(npc => (npc, Distance(world.Transforms.Interpolate(npc, now), eye)))
                .Where(e => e.Item2 <= SnapshotBuilder.AoiRadius),
        ];

        // 표본이 상한보다 커야 이 테스트가 공허하지 않다.
        Assert.True(
            expected.Count > SnapshotBuilder.MaxEntities,
            $"AOI 안 후보가 {expected.Count}개뿐이라 자를 것이 없다.");

        // 플레이어 자신도 한 칸을 쓴다 (거리 0).
        float[] wanted =
        [
            .. expected.Select(e => e.Distance).Order()
                .Take(SnapshotBuilder.MaxEntities - 1)
                .Select(d => MathF.Round(d, 3)),
        ];

        // <b>id 로 비교하지 않는다.</b> 여러 NPC 가 같은 POI 에 서 있어 거리가 같으므로,
        // 256번째 자리를 누가 차지하는지는 동점 처리에 달렸고 그것은 계약이 아니다.
        // 계약은 "가까운 순" 이고, 그것을 재는 것은 거리 열이다.
        float[] got =
        [
            .. snapshot.Entities!
                .Where(e => e.Kind == (byte)EntityKind.Npc)
                .Select(e => MathF.Round(Distance(new WorldPos(e.X, 0, e.Z), eye), 3))
                .Order(),
        ];

        Assert.Equal(wanted, got);

        // 가까운 순으로 나간다 — 상한에 잘린 회차의 결과가 회차마다 흔들리지 않는다.
        float previous = -1;

        foreach (EntityState entity in snapshot.Entities!)
        {
            float distance = Distance(new WorldPos(entity.X, 0, entity.Z), eye);

            Assert.True(distance >= previous - 0.01f, "가까운 순이 아니다.");
            Assert.True(distance <= SnapshotBuilder.AoiRadius, "AOI 밖이 들어왔다.");

            previous = distance;
        }
    }

    /// <summary>
    /// 완료 조건 — 다른 플레이어도 담는다 (docs/20 §8.1).
    ///
    /// 보는 사람 자신도 담는다. 위치의 주인은 서버라 클라이언트가 제 위치를 모르고,
    /// 안 담으면 화면에 제 캐릭터가 안 보인다.
    /// </summary>
    [Fact]
    public async Task Snapshot_IncludesOtherPlayers()
    {
        await using GameWorld world = Create(npcs: 64);

        var players = new PlayerRegistry(world, s_data, Options());
        var builder = new SnapshotBuilder(world, s_data, players, Options().TimeScale);

        PlayerId me = players.Add();
        PlayerId other = players.Add();
        var now = new Tick(2);

        // 상대를 AOI 안(400m)으로 옮긴다.
        WorldPos eye = players.PositionOf(me);

        players.Teleport(other, new WorldPos(eye.X + 400f, eye.Y, eye.Z));

        Snapshot snapshot = builder.Build(me, now);

        EntityState[] seen =
        [
            .. snapshot.Entities!.Where(e => e.Kind == (byte)EntityKind.Player),
        ];

        Assert.Equal(2, seen.Length);
        Assert.Contains(seen, e => e.Id == me.Value);

        EntityState mirror = Assert.Single(seen, e => e.Id == other.Value);

        Assert.Equal(eye.X + 400f, mirror.X, 2);
        Assert.Equal(eye.Z, mirror.Z, 2);
        Assert.NotEqual(0, mirror.Zone);

        // AOI 밖으로 나가면 사라진다.
        players.Teleport(other, new WorldPos(eye.X + SnapshotBuilder.AoiRadius + 100f, eye.Y, eye.Z));

        Snapshot after = builder.Build(me, now);

        Assert.DoesNotContain(after.Entities!, e => e.Id == other.Value && e.Kind == (byte)EntityKind.Player);
    }

    /// <summary>
    /// 완료 조건 — 이동 중인 NPC 는 <c>TargetPoi</c> 가 있고 <c>Poi</c> 는 0 이다.
    ///
    /// 클라이언트가 이 두 값으로 이동선을 그린다 (docs/20 §9.3). <c>Poi</c> 를 그대로 실으면
    /// 도착할 때까지 출발지에 서 있는 것으로 보이고, 도착하는 순간 순간이동한다.
    /// </summary>
    [Fact]
    public async Task Snapshot_MovingNpcHasTargetPoi()
    {
        await using GameWorld world = Create(npcs: 64);

        var players = new PlayerRegistry(world, s_data, Options());
        var builder = new SnapshotBuilder(world, s_data, players, Options().TimeScale);

        PlayerId viewer = players.Add();

        // 보는 사람 옆의 NPC 하나를 고르고, 같은 존의 다른 POI 로 보낸다.
        var start = new Tick(2);
        WorldPos eye = players.PositionOf(viewer);
        int npc = Nearest(world, eye, start);
        PoiId from = world.World.PoiOf(npc);
        PoiId to = OtherPoiInZone(world.World.ZoneOf(npc), from);

        EntityState before = Find(builder.Build(viewer, start), npc);

        Assert.Equal(from.Value, before.Poi);
        Assert.Equal(0, before.TargetPoi);
        Assert.Equal(0, before.StateFlags & (byte)EntityFlags.Moving);

        world.World.ApplyCommand(
            new NpcCommand
            {
                Kind = NpcCommandKind.MoveTo,
                Npc = new NpcId(npc),
                IssuedAt = start,
                Correlation = new CorrelationId(1),
                Priority = CommandPriority.Normal,
                TargetPoi = to,
            },
            start);

        EntityState moving = Find(builder.Build(viewer, new Tick(4)), npc);

        Assert.Equal(0, moving.Poi);
        Assert.Equal(to.Value, moving.TargetPoi);
        Assert.Equal((byte)EntityFlags.Moving, moving.StateFlags & (byte)EntityFlags.Moving);
        Assert.Equal((byte)VisualState.Walking, moving.Visual);

        // 보간 위치는 출발지에서 목적지 쪽으로 나아가 있다.
        WorldPos a = s_data.Pois[from].Pos;
        WorldPos b = s_data.Pois[to].Pos;

        Assert.True(
            Distance(new WorldPos(moving.X, 0, moving.Z), b) < Distance(a, b),
            "보간이 안 걸렸다. POI 좌표 그대로다.");
    }

    /// <summary>
    /// 완료 조건 — 세션당 ≤ 41KB/s.
    ///
    /// 256 × 32B × 5Hz ≈ 41KB/s 다. 상한을 넘기는 유일한 길은 <c>EntityState</c> 가 커지는 것이고,
    /// 그건 <c>ClientProtocol_EntityLayoutIsFrozen</c> 이 먼저 잡는다. 여기서는 <b>실제로 나가는
    /// 바이트</b>로 다시 확인한다 — 배열 태그가 붙기 시작하면 레이아웃은 그대로여도 넘친다.
    /// </summary>
    [Fact]
    public async Task Snapshot_FitsBandwidthBudget()
    {
        const int BudgetBytesPerSecond = 41 * 1024;

        await using GameWorld world = Create(npcs: 1_000);

        var players = new PlayerRegistry(world, s_data, Options());
        var builder = new SnapshotBuilder(world, s_data, players, Options().TimeScale);

        PlayerId viewer = players.Add();
        Snapshot snapshot = builder.Build(viewer, new Tick(2));

        Assert.Equal(SnapshotBuilder.MaxEntities, snapshot.EntityCount);   // 최악의 경우다

        int bytes = MemoryPackSerializer.Serialize(snapshot).Length;
        int perSecond = bytes * (GameWorld.TickRate / SnapshotBuilder.PeriodTicks);

        Assert.True(
            perSecond <= BudgetBytesPerSecond,
            $"세션당 {perSecond}B/s 로 예산 {BudgetBytesPerSecond}B/s 를 넘는다.");
    }

    /// <summary>스냅샷은 2틱마다다 (docs/20 §7.2 10단계 · §8.1).</summary>
    [Fact]
    public void Snapshot_IsDueEveryTwoTicks()
    {
        Assert.True(SnapshotBuilder.DueAt(new Tick(0)));
        Assert.False(SnapshotBuilder.DueAt(new Tick(1)));
        Assert.True(SnapshotBuilder.DueAt(new Tick(2)));
        Assert.False(SnapshotBuilder.DueAt(new Tick(3)));
    }

    // ---------------------------------------------------------------- 도우미

    private static GameServerOptions Options() =>
        new() { TimeScale = 60, LinkPort = 0, ClientPort = 0 };

    private static GameWorld Create(int npcs)
    {
        NpcRoster roster = NpcRoster.Select(s_instances, npcs);

        return GameWorld.Create(Options() with { Npcs = npcs }, s_data, roster, s_instances);
    }

    private static float Distance(in WorldPos a, in WorldPos b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;

        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    private static int Nearest(GameWorld world, WorldPos eye, Tick now)
    {
        int best = -1;
        float nearest = float.PositiveInfinity;

        for (int npc = 0; npc < world.World.Capacity; npc++)
        {
            if (!world.World.IsSpawned(npc))
            {
                continue;
            }

            float distance = Distance(world.Transforms.Interpolate(npc, now), eye);

            if (distance < nearest)
            {
                nearest = distance;
                best = npc;
            }
        }

        Assert.True(best >= 0, "스폰된 NPC 가 없다.");

        return best;
    }

    private static PoiId OtherPoiInZone(ZoneId zone, PoiId except)
    {
        foreach (PoiId poi in s_data.Pois.InZone(zone))
        {
            if (poi != except)
            {
                return poi;
            }
        }

        Assert.Fail($"존 {zone.Value} 에 POI 가 하나뿐이다.");
        return default;
    }

    private static EntityState Find(Snapshot snapshot, int npc)
    {
        Assert.NotNull(snapshot.Entities);

        return Assert.Single(
            snapshot.Entities!, e => e.Kind == (byte)EntityKind.Npc && e.Id == npc);
    }
}
