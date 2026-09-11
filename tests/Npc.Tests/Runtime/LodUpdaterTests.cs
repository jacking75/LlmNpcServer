using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>
/// docs/11 §4 "LOD 등급 갱신" — 등급은 이벤트 수신 시에만 정하고, 스캔은 읽기만 한다.
/// </summary>
public sealed class LodUpdaterTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static NpcStore NewStore(int npcs, int zones = 4)
    {
        var store = new NpcStore();
        store.Allocate(npcs, s_data.Items.MaxCode + 1);

        for (int npc = 0; npc < npcs; npc++)
        {
            store.StepStatus[npc] = (byte)StepStatus.Ready;
            store.ZoneCode[npc] = (ushort)((npc % zones) + 1);

            // A-08 — 슬롯 i 에 전역 id i+1 을 앉힌다. 0 은 "없음" 이라 쓸 수 없다.
            store.Bind(npc, npc + 1);
        }

        return store;
    }

    /// <summary>
    /// T4-14 완료 조건 — 등급은 <c>PlayerProximity</c> 로만 바뀐다.
    /// 거리 임계는 docs/11 §4 의 50m · 200m 다.
    /// </summary>
    [Fact]
    public void Lod_UpdatesFromProximityOnly()
    {
        NpcStore store = NewStore(8);
        var lod = new LodUpdater(store);

        // 전원이 비활성으로 시작한다 — 플레이어가 다가와야 승격된다.
        Assert.Equal(NpcStore.InactiveLod, store.Lod[0]);

        Assert.True(lod.OnProximity(0, ProximityChange.Enter, 10));
        Assert.Equal(0, store.Lod[0]);

        Assert.True(lod.OnProximity(0, ProximityChange.Enter, 120));
        Assert.Equal(1, store.Lod[0]);

        Assert.True(lod.OnProximity(0, ProximityChange.Enter, 500));
        Assert.Equal(2, store.Lod[0]);

        // 임계는 "미만" 이다.
        Assert.Equal(0, LodUpdater.GradeOf(LodUpdater.LodZeroDistance - 1));
        Assert.Equal(1, LodUpdater.GradeOf(LodUpdater.LodZeroDistance));
        Assert.Equal(1, LodUpdater.GradeOf(LodUpdater.LodOneDistance - 1));
        Assert.Equal(2, LodUpdater.GradeOf(LodUpdater.LodOneDistance));
        Assert.Equal(50, LodUpdater.LodZeroDistance);
        Assert.Equal(200, LodUpdater.LodOneDistance);

        // 같은 등급이면 아무 일도 하지 않는다.
        Assert.False(lod.OnProximity(0, ProximityChange.Enter, 400));
        Assert.Equal(1, lod.Promotions);
        Assert.Equal(2, lod.Demotions);

        // 범위 밖 첨자는 조용히 무시한다.
        Assert.False(lod.OnProximity(99, ProximityChange.Enter, 1));
    }

    /// <summary>
    /// 평시 존에서 플레이어가 떠나면 비활성(3)으로 내린다.
    ///
    /// 여기서 내리지 않으면 한 번이라도 플레이어를 만난 NPC 가 영원히 스캔 대상으로 남고,
    /// NPC 를 늘릴수록 틱당 스캔이 선형으로 자란다 — docs/11 §4 의 O(1) 설계가 깨진다.
    /// </summary>
    [Fact]
    public void Lod_DemotesToInactiveWhenPlayerLeavesPeacefulZone()
    {
        NpcStore store = NewStore(4);
        var zones = new ZoneStateTable(s_data);
        var lod = new LodUpdater(store) { ZoneStates = zones };

        lod.OnProximity(0, ProximityChange.Enter, 10);
        Assert.Equal(0, store.Lod[0]);

        lod.OnProximity(0, ProximityChange.Leave, 0);
        Assert.Equal(NpcStore.InactiveLod, store.Lod[0]);

        // 활성(전쟁) 존이면 원거리(2)까지만 내린다 — 상황이 계속 바뀌는 존이다.
        zones.Set(new ZoneId(store.ZoneCode[1]), RegionState.War);
        lod.OnProximity(1, ProximityChange.Enter, 10);
        lod.OnProximity(1, ProximityChange.Leave, 0);

        Assert.Equal(LodUpdater.ActiveZoneIdleLod, store.Lod[1]);
        Assert.Equal(2, LodUpdater.ActiveZoneIdleLod);
    }

    /// <summary>표가 없으면 NPC 의 <c>RegionUnderAttack</c> 플래그로 활성 여부를 본다.</summary>
    [Fact]
    public void Lod_FallsBackToFlagWhenNoZoneTable()
    {
        NpcStore store = NewStore(2);
        var lod = new LodUpdater(store);

        store.Flags[0] |= WorldFlags.RegionUnderAttack;

        lod.OnProximity(0, ProximityChange.Enter, 10);
        lod.OnProximity(0, ProximityChange.Leave, 0);
        Assert.Equal(LodUpdater.ActiveZoneIdleLod, store.Lod[0]);

        lod.OnProximity(1, ProximityChange.Enter, 10);
        lod.OnProximity(1, ProximityChange.Leave, 0);
        Assert.Equal(NpcStore.InactiveLod, store.Lod[1]);
    }

    /// <summary>
    /// T4-14 완료 조건 — <c>ZoneStateChanged(War)</c> 는 그 존 전체를 LOD 1 로 승격한다.
    /// 이미 더 가까운 NPC(LOD 0)는 끌어내리지 않는다.
    /// </summary>
    [Fact]
    public void Lod_PromotesZoneOnWar()
    {
        NpcStore store = NewStore(400, zones: 4);
        var lod = new LodUpdater(store);

        // 존 1 의 첫 NPC 는 플레이어 옆이다.
        lod.OnProximity(0, ProximityChange.Enter, 5);
        Assert.Equal(0, store.Lod[0]);

        var target = new ZoneId(1);
        int inZone = CountInZone(store, target);

        int promoted = lod.OnZoneState(target, RegionState.War);

        // 존 인원에서 이미 LOD 0/1 인 NPC 하나를 뺀 수만 승격된다.
        Assert.Equal(inZone - 1, promoted);
        Assert.Equal(promoted, lod.WarPromotions);

        for (int npc = 0; npc < store.Count; npc++)
        {
            if (store.ZoneCode[npc] == target.Value)
            {
                Assert.True(store.Lod[npc] <= LodUpdater.WarLod, $"npc {npc} 등급 {store.Lod[npc]}");
            }
            else
            {
                Assert.Equal(NpcStore.InactiveLod, store.Lod[npc]);   // 다른 존은 그대로
            }
        }

        // LOD 0 은 유지된다 — 승격만 하고 강등은 하지 않는다.
        Assert.Equal(0, store.Lod[0]);

        // Disaster 도 같은 규칙이다. Peace·Alert 는 승격하지 않는다.
        Assert.Equal(0, lod.OnZoneState(new ZoneId(2), RegionState.Peace));
        Assert.Equal(0, lod.OnZoneState(new ZoneId(2), RegionState.Alert));
        Assert.True(lod.OnZoneState(new ZoneId(2), RegionState.Disaster) > 0);
    }

    /// <summary>
    /// T4-14 완료 조건 — 존 전체 승격 뒤에도 밴드 이동 상한(틱당 64)이 지켜진다.
    /// 한 틱에 밴드 배열을 다시 짜면 그 자체가 스파이크다 (docs/11 §4).
    /// </summary>
    [Fact]
    public void Lod_BandMigrationStaysCappedAfterZonePromotion()
    {
        NpcStore store = NewStore(1_000, zones: 2);
        var bands = new LodBandSet(store);
        var lod = new LodUpdater(store);

        Assert.Equal(0, bands.PendingMigrations);

        int promoted = lod.OnZoneState(new ZoneId(1), RegionState.War);
        Assert.Equal(500, promoted);
        Assert.Equal(500, bands.PendingMigrations);

        int ticks = 0;

        while (bands.HasPendingMigration())
        {
            int moved = bands.Rebalance();

            Assert.True(
                moved <= LodBandSet.MaxBandMigrationsPerTick,
                $"한 틱에 {moved}건을 옮겼다. 상한은 {LodBandSet.MaxBandMigrationsPerTick} 이다.");

            ticks++;
            Assert.True(ticks < 100, "이동이 끝나지 않는다.");
        }

        // 500 / 64 = 7.8 → 8틱
        Assert.Equal(8, ticks);
        Assert.Equal(500, bands.CountOf(LodUpdater.WarLod));
        Assert.Equal(0, bands.PendingMigrations);
    }

    /// <summary>
    /// T4-14 완료 조건 — 인지 스캔 안에서 거리 계산이 0 이다.
    /// 소스에 좌표·거리 연산이 없는 것으로 확인한다.
    /// </summary>
    [Fact]
    public void Lod_ScanDoesNoDistanceMath()
    {
        string scan = File.ReadAllText(TestPaths.At("src", "Npc.Runtime", "CognitionScheduler.cs"));

        Assert.DoesNotContain("Pos[", scan, StringComparison.Ordinal);
        Assert.DoesNotContain("Distance", scan, StringComparison.Ordinal);
        Assert.DoesNotContain("Sqrt", scan, StringComparison.Ordinal);
        Assert.DoesNotContain("LodUpdater", scan, StringComparison.Ordinal);

        // 등급은 읽기만 한다. 점수 계산이 Lod 를 읽는 것은 맞지만 쓰면 안 된다.
        Assert.Contains("_store.Lod[npc]", scan, StringComparison.Ordinal);
        Assert.DoesNotContain("Lod[npc] =", scan, StringComparison.Ordinal);
        Assert.DoesNotContain("Lod[i] =", scan, StringComparison.Ordinal);
    }

    /// <summary>거리 임계가 두 곳에 있으면 반드시 어긋난다 — 상수는 한 곳에서만 온다.</summary>
    [Fact]
    public void Lod_ThresholdsHaveSingleSource()
    {
        Assert.Equal(LodUpdater.LodZeroDistance, EventApplier.LodZeroDistance);
        Assert.Equal(LodUpdater.LodOneDistance, EventApplier.LodOneDistance);

        string applier = File.ReadAllText(TestPaths.At("src", "Npc.Runtime", "EventApplier.cs"));

        // 등급 산출 switch 가 EventApplier 에 남아 있으면 안 된다.
        Assert.DoesNotContain("(byte)0,", applier, StringComparison.Ordinal);
        Assert.Contains("_lod.OnProximity", applier, StringComparison.Ordinal);
        Assert.Contains("_lod.OnZoneState", applier, StringComparison.Ordinal);
    }

    /// <summary>이벤트 경로로 들어와도 같은 결과다 — <see cref="EventApplier"/> 가 위임한다.</summary>
    [Fact]
    public void Lod_AppliedThroughEventPath()
    {
        NpcStore store = NewStore(200, zones: 2);
        var clock = new GameClock(s_data.Buckets, 60);
        var lod = new LodUpdater(store);
        var applier = new EventApplier(s_data, store, clock, new CorrelationTable(store.Count), lod);

        Assert.Same(lod, applier.Lod);

        var near = new GameEvent
        {
            Kind = GameEventKind.PlayerProximity,
            Sequence = 1,
            OccurredAt = new Tick(1),
            Npc = new NpcId(1),   // 슬롯 0 의 전역 id (A-08)
            Player = new PlayerId(1),
            Code = (byte)ProximityChange.Enter,
            Amount = 10,
        };

        applier.Apply(in near);

        Assert.Equal(0, store.Lod[0]);
        Assert.True((store.Flags[0] & WorldFlags.PlayerNearby) != 0);

        var war = new GameEvent
        {
            Kind = GameEventKind.ZoneStateChanged,
            Sequence = 2,
            OccurredAt = new Tick(2),
            Zone = new ZoneId(2),
            Code = (byte)RegionState.War,
        };

        applier.Apply(in war);

        Assert.True(lod.WarPromotions > 0);
        Assert.Equal(LodUpdater.WarLod, store.Lod[1]);
    }

    /// <summary>등급 갱신 경로의 할당이 0 이다 — 틱 루프의 이벤트 배수 구간에서 돈다.</summary>
    [Fact]
    public void Lod_UpdateDoesNotAllocate()
    {
        NpcStore store = NewStore(2_000, zones: 8);
        var lod = new LodUpdater(store) { ZoneStates = new ZoneStateTable(s_data) };

        // 여러 창의 최솟값을 본다 — 계층 JIT 재컴파일이 창 안에 떨어지면 잡음이 섞인다.
        Assert.Equal(0, AllocationProbe.MinimumBytes(() => Churn(lod, 5)));

        static void Churn(LodUpdater lod, int rounds)
        {
            for (int round = 0; round < rounds; round++)
            {
                for (int npc = 0; npc < 2_000; npc++)
                {
                    lod.OnProximity(npc, ProximityChange.Enter, (npc * 7) % 400);
                    lod.OnProximity(npc, ProximityChange.Leave, 0);
                }

                lod.OnZoneState(new ZoneId((ushort)((round % 8) + 1)), RegionState.War);
            }
        }
    }

    private static int CountInZone(NpcStore store, ZoneId zone)
    {
        int count = 0;

        for (int npc = 0; npc < store.Count; npc++)
        {
            if (store.ZoneCode[npc] == zone.Value)
            {
                count++;
            }
        }

        return count;
    }
}
