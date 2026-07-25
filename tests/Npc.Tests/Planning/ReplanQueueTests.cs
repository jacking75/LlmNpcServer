using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>docs/14 §2 (정식은 P4). P1 은 FIFO + 중복 제거만.</summary>
public sealed class ReplanQueueTests
{
    /// <summary>T1-38 완료 조건 — 같은 NPC 를 두 번 넣지 않는다.</summary>
    [Fact]
    public void ReplanQueue_DeduplicatesNpc()
    {
        var queue = new ReplanQueue(100);

        Assert.True(queue.TryEnqueue(7, 10));
        Assert.False(queue.TryEnqueue(7, 20));
        Assert.False(queue.TryEnqueue(7, 5));

        Assert.Equal(1, queue.Count);
        Assert.Equal(2, queue.Deduplicated);

        // 더 급한 요청이 들어왔으면 점수는 올라간다.
        Assert.Equal(20, queue.ScoreOf(7));

        Assert.True(queue.TryDequeue(out int npc, out int score));
        Assert.Equal(7, npc);
        Assert.Equal(20, score);
        Assert.False(queue.Contains(7));

        // 꺼낸 뒤에는 다시 넣을 수 있다.
        Assert.True(queue.TryEnqueue(7, 1));
    }

    [Fact]
    public void ReplanQueue_IsFifoInPhaseOne()
    {
        var queue = new ReplanQueue(100);

        queue.TryEnqueue(3, 1);
        queue.TryEnqueue(1, 99);   // 점수가 높아도 P1 은 순서를 바꾸지 않는다
        queue.TryEnqueue(2, 50);

        Assert.True(queue.TryDequeue(out int first, out _));
        Assert.True(queue.TryDequeue(out int second, out _));
        Assert.True(queue.TryDequeue(out int third, out _));

        Assert.Equal(3, first);
        Assert.Equal(1, second);
        Assert.Equal(2, third);
        Assert.False(queue.TryDequeue(out _, out _));
    }

    [Fact]
    public void ReplanQueue_DropsWhenFull()
    {
        var queue = new ReplanQueue(npcCapacity: 100, queueCapacity: 3);

        Assert.True(queue.TryEnqueue(0, 1));
        Assert.True(queue.TryEnqueue(1, 1));
        Assert.True(queue.TryEnqueue(2, 1));
        Assert.False(queue.TryEnqueue(3, 1));

        Assert.Equal(3, queue.Count);
        Assert.Equal(1, queue.Dropped);
    }

    [Fact]
    public void ReplanQueue_WrapsAroundRing()
    {
        var queue = new ReplanQueue(npcCapacity: 10, queueCapacity: 4);

        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < 4; i++)
            {
                Assert.True(queue.TryEnqueue(i, round));
            }

            for (int i = 0; i < 4; i++)
            {
                Assert.True(queue.TryDequeue(out int npc, out _));
                Assert.Equal(i, npc);
            }
        }

        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.Dropped);
    }

    [Fact]
    public void ReplanQueue_IgnoresOutOfRangeNpc()
    {
        var queue = new ReplanQueue(4);

        Assert.False(queue.TryEnqueue(99, 1));
        Assert.False(queue.TryEnqueue(-1, 1));
        Assert.Equal(0, queue.Count);
        Assert.Equal(-1, queue.ScoreOf(99));
    }

    [Fact]
    public void ReplanQueue_DoesNotAllocate()
    {
        var queue = new ReplanQueue(1_000);

        // JIT 티어 승격이 끝날 때까지 돌린다.
        for (int i = 0; i < 30_000; i++)
        {
            queue.TryEnqueue(i, i);
            queue.TryDequeue(out _, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            queue.TryEnqueue(i % 1_000, i);
            queue.TryDequeue(out _, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ReplanQueue_ClearEmptiesEverything()
    {
        var queue = new ReplanQueue(10);

        queue.TryEnqueue(1, 1);
        queue.TryEnqueue(2, 2);
        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.False(queue.Contains(1));
        Assert.True(queue.TryEnqueue(1, 1));
    }
}
