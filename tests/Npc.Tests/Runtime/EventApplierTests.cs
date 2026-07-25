using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>docs/02 §3.3 · N6 · N7. 이벤트 반영은 멱등이고 갭을 검출한다.</summary>
public sealed class EventApplierTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private sealed record Harness(
        NpcStore Store, GameClock Clock, CorrelationTable Correlations, EventApplier Applier);

    private static Harness NewHarness(int npcs = 4)
    {
        var store = new NpcStore();
        store.Allocate(npcs, s_data.Items.MaxCode + 1);

        var clock = new GameClock(s_data.Buckets, timeScale: 600);
        var correlations = new CorrelationTable(npcs);
        var applier = new EventApplier(s_data, store, clock, correlations);

        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));
        PoiId home = s_data.Pois.OfSubtype("house")[0];
        PoiId work = s_data.Pois.OfSubtype("smithy")[0];

        for (int i = 0; i < npcs; i++)
        {
            applier.Seed(i, home, s_data.Pois[home].Zone, smith.Code, home, work);
        }

        return new Harness(store, clock, correlations, applier);
    }

    private static GameEvent Event(GameEventKind kind, long sequence, int npc = 0) => new()
    {
        Kind = kind,
        Sequence = sequence,
        OccurredAt = new Tick(sequence * 10),
        Npc = new NpcId(npc),
    };

    /// <summary>N7 — 동일 이벤트 2회 주입 → 상태 해시 동일.</summary>
    [Fact]
    public void Link_Idempotent()
    {
        Harness h = NewHarness();

        GameEvent[] script =
        [
            Event(GameEventKind.NpcSpawned, 1) with { Pos = new WorldPos(1, 0, 2) },
            Event(GameEventKind.NpcInventoryChanged, 2) with { Item = new ItemId(1), Amount = 5 },
            Event(GameEventKind.NpcVitalsChanged, 3) with { Hp = 40, Stamina = 10 },
            Event(GameEventKind.NpcTransform, 4) with { Pos = new WorldPos(3, 0, 4) },
            Event(GameEventKind.PlayerProximity, 5) with
            {
                Player = new PlayerId(1), Amount = 30, Code = (byte)ProximityChange.Enter,
            },
        ];

        foreach (GameEvent ev in script)
        {
            h.Applier.Apply(in ev);
        }

        ulong once = h.Store.StateHash();

        // 같은 이벤트를 그대로 다시 주입한다.
        foreach (GameEvent ev in script)
        {
            h.Applier.Apply(in ev);
        }

        Assert.Equal(once, h.Store.StateHash());
        Assert.Equal(script.Length, h.Applier.DuplicatesIgnored);
        Assert.Equal(0, h.Applier.GapsDetected);
    }

    /// <summary>N6 — 시퀀스 갭을 검출한다.</summary>
    [Fact]
    public void Link_SequenceGap()
    {
        Harness h = NewHarness();

        GameEvent first = Event(GameEventKind.NpcSpawned, 1);
        h.Applier.Apply(in first);
        Assert.Equal(0, h.Applier.GapsDetected);

        // 2, 3, 4 가 유실됐다.
        GameEvent jumped = Event(GameEventKind.NpcTransform, 5) with { Pos = new WorldPos(1, 0, 1) };
        h.Applier.Apply(in jumped);

        Assert.Equal(3, h.Applier.GapsDetected);

        // 그 뒤로는 정상이면 더 세지 않는다.
        GameEvent next = Event(GameEventKind.NpcTransform, 6) with { Pos = new WorldPos(2, 0, 2) };
        h.Applier.Apply(in next);

        Assert.Equal(3, h.Applier.GapsDetected);
    }

    /// <summary>인벤 변경 → 플래그 재계산이 items.json 의 grants 만 보고 동작한다.</summary>
    [Fact]
    public void EventApplier_InventoryFlagsAreDataDriven()
    {
        Harness h = NewHarness();

        Assert.True(s_data.Items.TryGet("iron_ore", out ItemDef ore));
        Assert.Equal(WorldFlags.None, h.Store.Flags[0] & WorldFlags.HasRawMaterial);

        GameEvent gained = Event(GameEventKind.NpcInventoryChanged, 1) with { Item = ore.Code, Amount = 3 };
        h.Applier.Apply(in gained);

        Assert.Equal(ore.Grants, h.Store.Flags[0] & ore.Grants);

        GameEvent spent = Event(GameEventKind.NpcInventoryChanged, 2) with { Item = ore.Code, Amount = -3 };
        h.Applier.Apply(in spent);

        Assert.Equal(WorldFlags.None, h.Store.Flags[0] & WorldFlags.HasRawMaterial);

        // 인벤토리에서 오지 않는 플래그는 건드리지 않는다.
        Assert.NotEqual(WorldFlags.None, h.Store.Flags[0] & WorldFlags.AtHome);
    }

    [Fact]
    public void EventApplier_InventoryNeverGoesNegative()
    {
        Harness h = NewHarness();

        Assert.True(s_data.Items.TryGet("iron_ore", out ItemDef ore));
        GameEvent spent = Event(GameEventKind.NpcInventoryChanged, 1) with { Item = ore.Code, Amount = -10 };
        h.Applier.Apply(in spent);

        Assert.Equal(0, h.Store.ReadInventoryOf(0)[ore.Code.Value]);
    }

    /// <summary>도착하면 그 POI 가 정한 장소 플래그가 서고 나머지 장소 플래그는 내려간다.</summary>
    [Fact]
    public void EventApplier_ArrivalSetsLocationFlagsFromPoi()
    {
        Harness h = NewHarness();
        PoiId smithy = s_data.Pois.OfSubtype("smithy")[0];

        CorrelationId correlation = h.Correlations.Next(0);
        GameEvent arrived = Event(GameEventKind.NpcArrived, 1) with
        {
            Poi = smithy,
            Correlation = correlation,
        };

        h.Applier.Apply(in arrived);

        Assert.Equal(WorldFlags.AtWorkplace, h.Store.Flags[0] & EventApplier.LocationFlags);
        Assert.Equal(smithy.Value, h.Store.CurrentPoi[0]);
        Assert.Equal((byte)StepStatus.Completed, h.Store.StepStatus[0]);
    }

    /// <summary>낡은 상관 ID 의 응답은 스텝을 전진시키지 않는다 (docs/02 §3.4).</summary>
    [Fact]
    public void EventApplier_IgnoresStaleCorrelation()
    {
        Harness h = NewHarness();

        CorrelationId issued = h.Correlations.Next(0);
        h.Correlations.Invalidate(0);   // 플랜 스왑

        h.Store.StepStatus[0] = (byte)StepStatus.Waiting;

        GameEvent late = Event(GameEventKind.NpcActionCompleted, 1) with { Correlation = issued };
        h.Applier.Apply(in late);

        Assert.Equal((byte)StepStatus.Waiting, h.Store.StepStatus[0]);
        Assert.Equal(1, h.Applier.StaleResponsesIgnored);
    }

    [Fact]
    public void EventApplier_VitalsDriveInjuredAndExhausted()
    {
        Harness h = NewHarness();

        GameEvent hurt = Event(GameEventKind.NpcVitalsChanged, 1) with { Hp = 30, Stamina = 10 };
        h.Applier.Apply(in hurt);

        Assert.NotEqual(WorldFlags.None, h.Store.Flags[0] & WorldFlags.IsInjured);
        Assert.NotEqual(WorldFlags.None, h.Store.Flags[0] & WorldFlags.IsExhausted);

        GameEvent healed = Event(GameEventKind.NpcVitalsChanged, 2) with { Hp = 100, Stamina = 100 };
        h.Applier.Apply(in healed);

        Assert.Equal(WorldFlags.None, h.Store.Flags[0] & (WorldFlags.IsInjured | WorldFlags.IsExhausted));
    }

    /// <summary>docs/11 §4 — LOD 등급은 PlayerProximity 로만 바뀐다.</summary>
    [Fact]
    public void EventApplier_ProximitySetsLodBand()
    {
        Harness h = NewHarness();

        GameEvent close = Event(GameEventKind.PlayerProximity, 1) with
        {
            Player = new PlayerId(1), Amount = 20, Code = (byte)ProximityChange.Enter,
        };
        h.Applier.Apply(in close);
        Assert.Equal(0, h.Store.Lod[0]);
        Assert.NotEqual(WorldFlags.None, h.Store.Flags[0] & WorldFlags.PlayerNearby);

        GameEvent mid = Event(GameEventKind.PlayerProximity, 2) with
        {
            Player = new PlayerId(1), Amount = 150, Code = (byte)ProximityChange.Enter,
        };
        h.Applier.Apply(in mid);
        Assert.Equal(1, h.Store.Lod[0]);

        // 평시 존이면 비활성(3)으로 내린다. 여기서 2 에 머물면 한 번이라도 플레이어를 만난 NPC 가
        // 영원히 스캔 대상으로 남아 틱당 스캔이 파퓰레이션에 비례해 자란다.
        GameEvent gone = Event(GameEventKind.PlayerProximity, 3) with
        {
            Player = new PlayerId(1), Code = (byte)ProximityChange.Leave,
        };
        h.Applier.Apply(in gone);
        Assert.Equal(NpcStore.InactiveLod, h.Store.Lod[0]);
        Assert.Equal(WorldFlags.None, h.Store.Flags[0] & WorldFlags.PlayerNearby);

        // 공격받는 존이면 2 로 남긴다 (docs/11 §4 — 존 활성도에 따라 2 또는 3).
        h.Store.Flags[0] |= WorldFlags.RegionUnderAttack;
        GameEvent goneInWar = Event(GameEventKind.PlayerProximity, 4) with
        {
            Player = new PlayerId(1), Code = (byte)ProximityChange.Leave,
        };
        h.Applier.Apply(in goneInWar);
        Assert.Equal(2, h.Store.Lod[0]);
    }

    /// <summary>존 상태 변경은 그 존의 NPC 만 건드린다.</summary>
    [Fact]
    public void EventApplier_ZoneStateAffectsOnlyThatZone()
    {
        Harness h = NewHarness(2);

        ZoneId zone = s_data.Pois[new PoiId(h.Store.CurrentPoi[0])].Zone;
        ZoneId other = new((ushort)(zone.Value == 1 ? 2 : 1));
        h.Store.ZoneCode[1] = other.Value;

        GameEvent war = Event(GameEventKind.ZoneStateChanged, 1) with
        {
            Zone = zone, Code = (byte)RegionState.War,
        };
        h.Applier.Apply(in war);

        Assert.NotEqual(WorldFlags.None, h.Store.Flags[0] & WorldFlags.RegionUnderAttack);
        Assert.Equal(WorldFlags.None, h.Store.Flags[0] & WorldFlags.RegionPeaceful);
        Assert.NotEqual(WorldFlags.None, h.Store.Flags[1] & WorldFlags.RegionPeaceful);

        // 공격받는 존은 LOD 1 로 승격된다 (docs/11 §4).
        Assert.True(h.Store.Lod[0] <= 1);
    }

    [Fact]
    public void EventApplier_TimeOfDayFlagsAreMutuallyExclusive()
    {
        Harness h = NewHarness();

        GameEvent night = Event(GameEventKind.GameTimeChanged, 1) with { Code = (byte)TimeOfDay.Night };
        h.Applier.Apply(in night);

        Assert.Equal(WorldFlags.IsNight, h.Store.Flags[0] & EventApplier.TimeFlags);

        GameEvent morning = Event(GameEventKind.GameTimeChanged, 2) with { Code = (byte)TimeOfDay.Morning };
        h.Applier.Apply(in morning);

        Assert.Equal(WorldFlags.IsDay, h.Store.Flags[0] & EventApplier.TimeFlags);
    }

    [Fact]
    public void EventApplier_TickSyncDrivesClock()
    {
        Harness h = NewHarness();

        GameEvent sync = Event(GameEventKind.TickSync, 1) with { OccurredAt = new Tick(42) };
        h.Applier.Apply(in sync);

        int advanced = 0;
        while (h.Clock.TryAdvance(out _))
        {
            advanced++;
        }

        Assert.Equal(42, advanced);
    }

    /// <summary>NpcTransform 은 기억에 남기지 않는다 — 최다 이벤트라 8슬롯을 그것만으로 채운다.</summary>
    [Fact]
    public void EventApplier_DoesNotRememberTransforms()
    {
        Harness h = NewHarness();

        for (int i = 1; i <= 20; i++)
        {
            GameEvent moved = Event(GameEventKind.NpcTransform, i) with { Pos = new WorldPos(i, 0, 0) };
            h.Applier.Apply(in moved);
        }

        Assert.Equal(0, h.Store.Recent[0].Count);

        GameEvent hit = Event(GameEventKind.DamageTaken, 21) with { OtherNpc = new NpcId(3), Amount = 12 };
        h.Applier.Apply(in hit);

        Assert.Equal(1, h.Store.Recent[0].Count);
        Assert.Equal(GameEventKind.DamageTaken, h.Store.Recent[0][0].Kind);
    }

    [Fact]
    public void EventApplier_ApplyDoesNotAllocate()
    {
        Harness h = NewHarness();
        long sequence = 1;

        // JIT 티어 승격이 끝날 때까지 돌린다.
        for (int i = 0; i < 30_000; i++)
        {
            GameEvent warm = Event(GameEventKind.NpcTransform, sequence++) with { Pos = new WorldPos(i, 0, 0) };
            h.Applier.Apply(in warm);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            GameEvent ev = Event(GameEventKind.NpcTransform, sequence++) with { Pos = new WorldPos(i, 0, 0) };
            h.Applier.Apply(in ev);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void EventApplier_SeedGivesArchetypeBaseline()
    {
        Harness h = NewHarness();

        // 대장장이는 망치·빵·물·동전을 들고 시작한다 (archetypes.json 의 initial_inventory).
        Assert.NotEqual(WorldFlags.None, h.Store.Flags[0] & WorldFlags.HasTool);
        Assert.NotEqual(WorldFlags.None, h.Store.Flags[0] & WorldFlags.HasFood);
        Assert.NotEqual(WorldFlags.None, h.Store.Flags[0] & WorldFlags.HasWater);
        Assert.NotEqual(WorldFlags.None, h.Store.Flags[0] & WorldFlags.HasCoin);
        Assert.NotEqual(WorldFlags.None, h.Store.Flags[0] & WorldFlags.AtHome);
    }
}
