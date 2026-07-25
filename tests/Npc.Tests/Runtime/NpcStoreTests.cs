using Npc.Contracts;
using Npc.Core;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>docs/11 §3. SoA 레이아웃과 구조화 기억.</summary>
public sealed class NpcStoreTests
{
    private const int FiveThousand = 5_000;

    private static NpcStore NewStore(int count = FiveThousand, int stride = 83)
    {
        var store = new NpcStore();
        store.Allocate(count, stride);
        return store;
    }

    /// <summary>T1-28 완료 조건 — NPC 5,000 기준 핫 배열이 128KB 미만이어야 L2 에 들어간다.</summary>
    [Fact]
    public void NpcStore_HotArraysUnder128KB()
    {
        NpcStore store = NewStore();

        Assert.True(
            store.HotBytes < 128 * 1024,
            $"핫 배열이 {store.HotBytes:N0} 바이트다. 128KB 미만이어야 한다.");

        // docs/11 §3 이 말하는 ~100KB 규모인지도 확인한다.
        Assert.InRange(store.HotBytes, 80 * 1024, 128 * 1024);
    }

    [Fact]
    public void NpcStore_AllocatesEveryArray()
    {
        NpcStore store = NewStore(100, 83);

        Assert.Equal(100, store.Count);
        Assert.Equal(100, store.Flags.Length);
        Assert.Equal(100, store.PlanId.Length);
        Assert.Equal(100, store.Recent.Length);
        Assert.Equal(100 * 83, store.Inventory.Length);

        // 스폰 확인 전에는 명령을 발행하지 않는다 (docs/02 §3.3).
        Assert.All(store.StepStatus, s => Assert.Equal((byte)StepStatus.Unspawned, s));
        Assert.All(store.LastEventSequence, s => Assert.Equal(-1, s));
    }

    [Fact]
    public void NpcStore_InventoryIsFlatAndSliced()
    {
        NpcStore store = NewStore(10, 83);

        store.InventoryOf(3)[40] = 7;

        Assert.Equal(7, store.Inventory[(3 * 83) + 40]);
        Assert.Equal(7, store.ReadInventoryOf(3)[40]);
        Assert.Equal(0, store.ReadInventoryOf(4)[40]);
    }

    [Fact]
    public void NpcStore_InventorySliceDoesNotAllocate()
    {
        NpcStore store = NewStore(1_000, 83);

        // JIT 티어 승격이 끝날 때까지 돌린다. 승격 시점에 한 번 할당이 잡힌다.
        for (int i = 0; i < 30_000; i++)
        {
            _ = store.ReadInventoryOf(i % 1_000)[0];
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            _ = store.ReadInventoryOf(i % 1_000)[0];
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>같은 상태면 같은 해시. 멱등성 테스트(N7)와 리플레이 일치 검사의 기반이다.</summary>
    [Fact]
    public void NpcStore_StateHashIsDeterministic()
    {
        NpcStore a = NewStore(50, 83);
        NpcStore b = NewStore(50, 83);

        Assert.Equal(a.StateHash(), b.StateHash());

        a.Flags[7] = WorldFlags.AtHome | WorldFlags.HasFood;
        Assert.NotEqual(a.StateHash(), b.StateHash());

        b.Flags[7] = WorldFlags.AtHome | WorldFlags.HasFood;
        Assert.Equal(a.StateHash(), b.StateHash());

        a.InventoryOf(3)[40] = 1;
        Assert.NotEqual(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void NpcStore_StateHashNoticesPositionChange()
    {
        NpcStore store = NewStore(5, 83);
        ulong before = store.StateHash();

        store.Pos[2] = new WorldPos(0.0001f, 0f, 0f);

        Assert.NotEqual(before, store.StateHash());
    }

    // ---------------------------------------------------------------- RingBuffer8

    /// <summary>T1-28 완료 조건 — salience 낮은 것부터 밀려난다.</summary>
    [Fact]
    public void RingBuffer8_EvictsLowestSalience()
    {
        var buffer = default(RingBuffer8<RecentEvent>);

        for (int i = 0; i < RingBuffer8<RecentEvent>.Capacity; i++)
        {
            Assert.True(buffer.Add(new RecentEvent(GameEventKind.NpcTransform, new Tick(i), i, (byte)(i + 1))));
        }

        Assert.True(buffer.IsFull);
        Assert.Equal(8, buffer.Count);

        // salience 9 는 가장 낮은 1 을 밀어낸다.
        Assert.True(buffer.Add(new RecentEvent(GameEventKind.DamageTaken, new Tick(100), 99, 9)));

        RecentEvent[] items = buffer.AsSpan().ToArray();

        Assert.DoesNotContain(items, e => e.Salience == 1);
        Assert.Contains(items, e => e.Kind == GameEventKind.DamageTaken);
        Assert.Equal(8, items.Length);
    }

    /// <summary>새 항목이 모든 기존 항목보다 덜 중요하면 버린다 — 더 중요한 기억을 지우지 않는다.</summary>
    [Fact]
    public void RingBuffer8_DropsLessSalientNewcomer()
    {
        var buffer = default(RingBuffer8<RecentEvent>);

        for (int i = 0; i < RingBuffer8<RecentEvent>.Capacity; i++)
        {
            buffer.Add(new RecentEvent(GameEventKind.CombatStarted, new Tick(i), i, 200));
        }

        Assert.False(buffer.Add(new RecentEvent(GameEventKind.NpcTransform, new Tick(99), 99, 1)));
        Assert.DoesNotContain(buffer.AsSpan().ToArray(), e => e.Kind == GameEventKind.NpcTransform);
    }

    /// <summary>salience 가 같으면 오래된 것을 먼저 버린다.</summary>
    [Fact]
    public void RingBuffer8_EvictsOldestAmongEqualSalience()
    {
        var buffer = default(RingBuffer8<RecentEvent>);

        for (int i = 0; i < RingBuffer8<RecentEvent>.Capacity; i++)
        {
            buffer.Add(new RecentEvent(GameEventKind.NpcTransform, new Tick(i), i, 50));
        }

        buffer.Add(new RecentEvent(GameEventKind.NpcArrived, new Tick(100), 100, 50));

        RecentEvent[] items = buffer.AsSpan().ToArray();

        // 가장 먼저 넣은 subject 0 이 사라진다.
        Assert.DoesNotContain(items, e => e.Subject == 0);
        Assert.Contains(items, e => e.Subject == 100);
        Assert.Contains(items, e => e.Subject == 7);
    }

    [Fact]
    public void RingBuffer8_AddDoesNotAllocate()
    {
        var buffer = default(RingBuffer8<RecentEvent>);
        var item = new RecentEvent(GameEventKind.NpcTransform, new Tick(1), 1, 10);

        // JIT 티어 승격이 끝날 때까지 돌린다.
        for (int i = 0; i < 30_000; i++)
        {
            buffer.Add(item);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            buffer.Add(item);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void RingBuffer8_ClearEmptiesBuffer()
    {
        var buffer = default(RingBuffer8<RecentEvent>);
        buffer.Add(new RecentEvent(GameEventKind.NpcArrived, new Tick(1), 1, 10));

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.False(buffer.IsFull);
        Assert.Empty(buffer.AsSpan().ToArray());
    }

    [Fact]
    public void RingBuffer8_IndexerRejectsOutOfRange()
    {
        var buffer = default(RingBuffer8<RecentEvent>);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[0]);
    }
}
