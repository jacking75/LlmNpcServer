using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>docs/11 §4. 밴드 이동은 틱당 64건으로 나눠 처리한다.</summary>
public sealed class LodBandTests
{
    private static NpcStore NewStore(int count)
    {
        var store = new NpcStore();
        store.Allocate(count, 8);
        return store;
    }

    [Fact]
    public void LodBand_StartsWithEveryoneInBandZero()
    {
        NpcStore store = NewStore(100);
        var bands = new LodBandSet(store);

        Assert.Equal(100, bands.CountOf(0));
        Assert.Equal(0, bands.CountOf(1));
        Assert.False(bands.HasPendingMigration());
    }

    /// <summary>T1-36 완료 조건 — 1,000건 동시 변경이 16틱에 걸쳐 처리된다.</summary>
    [Fact]
    public void Lod_MigrationCappedPerTick()
    {
        NpcStore store = NewStore(1_000);
        var bands = new LodBandSet(store);

        // 1,000마리가 한꺼번에 등급이 바뀐다.
        for (int npc = 0; npc < 1_000; npc++)
        {
            store.Lod[npc] = 2;
        }

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

        // 1,000 / 64 = 15.6 → 16틱
        Assert.Equal(16, ticks);
        Assert.Equal(1_000, bands.CountOf(2));
        Assert.Equal(0, bands.CountOf(0));
        Assert.Equal(1_000, bands.Migrations);
    }

    [Fact]
    public void LodBand_MembershipStaysConsistent()
    {
        NpcStore store = NewStore(300);
        var bands = new LodBandSet(store);

        for (int npc = 0; npc < 300; npc++)
        {
            store.Lod[npc] = (byte)(npc % 4);
        }

        while (bands.HasPendingMigration())
        {
            bands.Rebalance();
        }

        var seen = new HashSet<int>();

        for (int lod = 0; lod < LodBandSet.BandCount; lod++)
        {
            foreach (int npc in bands.MembersOf(lod))
            {
                Assert.Equal(lod, store.Lod[npc]);
                Assert.Equal(lod, bands.BandOf(npc));
                Assert.True(seen.Add(npc), $"NPC {npc} 가 두 밴드에 있다.");
            }
        }

        Assert.Equal(300, seen.Count);
        Assert.Equal(75, bands.CountOf(0));
        Assert.Equal(75, bands.CountOf(3));
    }

    /// <summary>커서가 돌기 때문에 뒤쪽 NPC 도 굶지 않는다.</summary>
    [Fact]
    public void LodBand_CursorIsFair()
    {
        NpcStore store = NewStore(500);
        var bands = new LodBandSet(store);

        store.Lod[499] = 3;

        int ticks = 0;
        while (bands.HasPendingMigration() && ticks < 20)
        {
            bands.Rebalance();
            ticks++;
        }

        Assert.False(bands.HasPendingMigration());
        Assert.Equal(3, bands.BandOf(499));
    }

    /// <summary>슬라이스는 Period 틱에 밴드를 정확히 한 바퀴 돈다.</summary>
    [Fact]
    public void LodBand_SlicesCoverBandExactlyOncePerPeriod()
    {
        NpcStore store = NewStore(1_000);
        var bands = new LodBandSet(store);

        for (int npc = 0; npc < 1_000; npc++)
        {
            store.Lod[npc] = 1;
        }

        while (bands.HasPendingMigration())
        {
            bands.Rebalance();
        }

        int period = LodBandSet.Bands[1].Period;
        var visits = new int[1_000];

        for (long tick = 0; tick < period; tick++)
        {
            (int start, int end) = bands.SliceOf(1, tick);
            ReadOnlySpan<int> members = bands.MembersOf(1);

            for (int k = start; k < end; k++)
            {
                visits[members[k]]++;
            }
        }

        Assert.All(visits, v => Assert.Equal(1, v));
    }

    [Fact]
    public void LodBand_BandZeroIsScannedEveryTick()
    {
        NpcStore store = NewStore(50);
        var bands = new LodBandSet(store);

        for (long tick = 0; tick < 5; tick++)
        {
            (int start, int end) = bands.SliceOf(0, tick);

            Assert.Equal(0, start);
            Assert.Equal(50, end);
        }
    }

    /// <summary>비활성 밴드(Period 0)는 스캔하지 않는다 — 이벤트 시에만 본다.</summary>
    [Fact]
    public void LodBand_InactiveBandIsNeverScanned()
    {
        NpcStore store = NewStore(50);
        var bands = new LodBandSet(store);

        for (int npc = 0; npc < 50; npc++)
        {
            store.Lod[npc] = 3;
        }

        while (bands.HasPendingMigration())
        {
            bands.Rebalance();
        }

        for (long tick = 0; tick < 200; tick++)
        {
            (int start, int end) = bands.SliceOf(3, tick);

            Assert.Equal(start, end);
        }
    }

    [Fact]
    public void LodBand_RebalanceDoesNotAllocate()
    {
        NpcStore store = NewStore(2_000);
        var bands = new LodBandSet(store);

        for (int i = 0; i < 100; i++)
        {
            store.Lod[i % 2_000] = (byte)(i % 4);
            bands.Rebalance();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            store.Lod[i % 2_000] = (byte)(i % 4);
            bands.Rebalance();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
