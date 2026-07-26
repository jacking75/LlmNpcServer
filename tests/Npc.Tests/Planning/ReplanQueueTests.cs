using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>docs/14 §2 — 점수순 이진 힙. 중복 제거 · 최하위 축출 · 할당 0.</summary>
public sealed class ReplanQueueTests
{
    /// <summary>T1-38 · T4-01 완료 조건 — 같은 NPC 를 두 번 넣지 않는다.</summary>
    [Fact]
    public void ReplanQueue_DeduplicatesNpc()
    {
        var queue = new ReplanQueue(100);

        Assert.True(queue.TryEnqueue(7, 10));
        Assert.False(queue.TryEnqueue(7, 20));
        Assert.False(queue.TryEnqueue(7, 5));

        Assert.Equal(1, queue.Count);
        Assert.Equal(2, queue.Deduplicated);

        // 더 급한 요청이 들어왔으면 점수는 올라간다. 낮은 요청은 점수를 내리지 않는다.
        Assert.Equal(20f, queue.ScoreOf(7));

        Assert.True(queue.TryDequeue(out int npc, out float score));
        Assert.Equal(7, npc);
        Assert.Equal(20f, score);
        Assert.False(queue.Contains(7));

        // 꺼낸 뒤에는 다시 넣을 수 있다.
        Assert.True(queue.TryEnqueue(7, 1));
    }

    /// <summary>
    /// T4-01 완료 조건 — 같은 NPC 재삽입은 힙에 항목을 늘리지 않고 점수만 갱신한다.
    /// 4096 칸이 같은 NPC 로 차 버리면 큐가 즉시 무의미해진다 (CLAUDE.md §7).
    /// </summary>
    [Fact]
    public void ReplanQueue_NoDuplicateInsert()
    {
        var queue = new ReplanQueue(npcCapacity: 64, queueCapacity: 16);

        for (int i = 0; i < 1_000; i++)
        {
            queue.TryEnqueue(3, i);
        }

        Assert.Equal(1, queue.Count);
        Assert.Equal(999f, queue.ScoreOf(3));
        Assert.Equal(1, queue.TotalEnqueued);
        Assert.Equal(999, queue.Deduplicated);
        Assert.Equal(0, queue.Dropped);
    }

    /// <summary>T4-01 — 점수순으로 나온다. P1 의 FIFO 를 대체한 것이 이 태스크다.</summary>
    [Fact]
    public void ReplanQueue_PopsHighestScoreFirst()
    {
        var queue = new ReplanQueue(100);

        queue.TryEnqueue(3, 1);
        queue.TryEnqueue(1, 99);
        queue.TryEnqueue(2, 50);

        Assert.True(queue.TryDequeueMax(out int first));
        Assert.True(queue.TryDequeueMax(out int second));
        Assert.True(queue.TryDequeueMax(out int third));

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(3, third);
        Assert.False(queue.TryDequeueMax(out _));
        Assert.Equal(-1, queue.DequeueMax());
    }

    /// <summary>
    /// 동점은 npc 첨자 오름차순이다. 힙 구조에 순서를 맡기면 삽입 이력에 따라 달라져
    /// 리플레이가 깨진다 (CLAUDE.md §2.3).
    /// </summary>
    [Fact]
    public void ReplanQueue_BreaksTiesDeterministically()
    {
        int[] forward = Drain(NewFilled([9, 4, 7, 1, 6, 2]));
        int[] backward = Drain(NewFilled([2, 6, 1, 7, 4, 9]));

        Assert.Equal([1, 2, 4, 6, 7, 9], forward);
        Assert.Equal(forward, backward);

        static ReplanQueue NewFilled(int[] order)
        {
            var queue = new ReplanQueue(16);

            foreach (int npc in order)
            {
                queue.TryEnqueue(npc, 5f);   // 전부 같은 점수
            }

            return queue;
        }

        static int[] Drain(ReplanQueue queue)
        {
            var got = new List<int>();

            while (queue.TryDequeueMax(out int npc))
            {
                got.Add(npc);
            }

            return [.. got];
        }
    }

    /// <summary>인터럽트는 일반 점수 범위를 넘어선다 (docs/14 §2 의 1000 + urgency).</summary>
    [Fact]
    public void ReplanQueue_UrgentOutranksEverything()
    {
        var queue = new ReplanQueue(100);

        queue.TryEnqueue(1, 999f);
        Assert.True(queue.TryEnqueueUrgent(2, 0f));

        Assert.Equal(ReplanQueue.UrgentBase, queue.ScoreOf(2));
        Assert.True(queue.TryDequeueMax(out int first));
        Assert.Equal(2, first);
    }

    /// <summary>
    /// T4-03 완료 조건 — 인터럽트 5,000건을 주입해도 일반 항목이 처리된다 (docs/14 §10).
    ///
    /// 상한이 없으면 인터럽트 점수(1000 이상)가 일반 항목(상한 9.5)을 전부 밀어내
    /// 이탈 판정과 스텝 실패가 영원히 처리되지 않는다.
    /// </summary>
    [Fact]
    public void ReplanQueue_InterruptSlotCapped()
    {
        var queue = new ReplanQueue(npcCapacity: 5_000, queueCapacity: 4_096);

        Assert.Equal(2_048, queue.MaxUrgentSlots);

        for (int npc = 0; npc < 5_000; npc++)
        {
            queue.TryEnqueueUrgent(npc, 100);
        }

        // 인터럽트는 절반까지만 들어간다. 나머지 절반은 일반 항목을 위해 남는다.
        Assert.Equal(2_048, queue.UrgentCount);
        Assert.Equal(2_048, queue.Count);
        Assert.Equal(5_000 - 2_048, queue.UrgentDropped);

        // 일반 항목은 여전히 들어가고, 인터럽트가 다 빠진 뒤 처리된다.
        Assert.True(queue.TryEnqueue(4_999, 5f));
        Assert.Equal(2_049, queue.Count);

        int urgentPopped = 0;
        bool normalPopped = false;

        while (queue.TryDequeue(out int npc, out float score))
        {
            if (ReplanQueue.IsUrgent(score))
            {
                urgentPopped++;
                continue;
            }

            Assert.Equal(4_999, npc);
            normalPopped = true;
        }

        Assert.Equal(2_048, urgentPopped);
        Assert.True(normalPopped, "인터럽트가 큐를 독점해 일반 항목이 처리되지 않았다.");
        Assert.Equal(0, queue.UrgentCount);

        // 슬롯이 비면 다시 받는다.
        Assert.True(queue.TryEnqueueUrgent(0, 100));
    }

    /// <summary>이미 큐에 있는 항목의 승격은 칸을 더 쓰지 않으므로 상한과 무관하다.</summary>
    [Fact]
    public void ReplanQueue_PromotionDoesNotConsumeUrgentSlot()
    {
        var queue = new ReplanQueue(npcCapacity: 64, queueCapacity: 4);

        Assert.Equal(2, queue.MaxUrgentSlots);

        queue.TryEnqueue(0, 1f);
        queue.TryEnqueue(1, 2f);
        queue.TryEnqueue(2, 3f);
        Assert.Equal(0, queue.UrgentCount);

        // 인터럽트 신규 삽입은 2칸까지.
        Assert.True(queue.TryEnqueueUrgent(3, 0));
        Assert.Equal(1, queue.UrgentCount);

        // 이미 들어 있던 0 이 인터럽트로 승격된다 — 칸 수는 그대로 4 다.
        Assert.False(queue.TryEnqueueUrgent(0, 50));
        Assert.Equal(2, queue.UrgentCount);
        Assert.Equal(4, queue.Count);

        // 상한이 찼으니 새 인터럽트는 거절한다.
        Assert.False(queue.TryEnqueueUrgent(9, 100));
        Assert.Equal(1, queue.UrgentDropped);

        // 카운터가 정확해야 꺼낼 때 음수로 새지 않는다.
        Assert.Equal([0, 3, 2, 1], Order(queue));
        Assert.Equal(0, queue.UrgentCount);
    }

    /// <summary>T4-01 완료 조건 — 포화 시 최하위를 밀어낸다. 낮은 요청은 거절한다.</summary>
    [Fact]
    public void ReplanQueue_EvictsLowest()
    {
        var queue = new ReplanQueue(npcCapacity: 100, queueCapacity: 4);

        queue.TryEnqueue(0, 10);
        queue.TryEnqueue(1, 20);
        queue.TryEnqueue(2, 30);
        queue.TryEnqueue(3, 40);

        // 최하위(10)보다 낮으면 거절. 기존 4마리는 그대로 남는다.
        Assert.False(queue.TryEnqueue(4, 5));
        Assert.Equal(4, queue.Count);
        Assert.Equal(1, queue.Dropped);
        Assert.False(queue.Contains(4));

        // 최하위보다 급하면 최하위를 밀어내고 들어간다.
        Assert.True(queue.TryEnqueue(5, 15));
        Assert.Equal(4, queue.Count);
        Assert.Equal(1, queue.Evicted);
        Assert.False(queue.Contains(0));   // 10 점이 밀려났다
        Assert.True(queue.Contains(5));

        Assert.Equal([3, 2, 1, 5], Order(queue));
    }

    /// <summary>포화 상태에서 반복 축출해도 힙 불변식이 유지된다.</summary>
    [Fact]
    public void ReplanQueue_StaysOrderedUnderChurn()
    {
        var queue = new ReplanQueue(npcCapacity: 512, queueCapacity: 32);

        // 결정론 의사난수 — Random 을 쓰지 않는다 (CLAUDE.md §2.3).
        for (int i = 0; i < 5_000; i++)
        {
            int npc = (int)(((uint)i * 2654435761u) % 512u);
            queue.TryEnqueue(npc, (i * 37) % 101);

            if (i % 7 == 0)
            {
                queue.TryDequeueMax(out _);
            }
        }

        int[] order = Order(queue);

        for (int i = 1; i < order.Length; i++)
        {
            Assert.True(order[i - 1] != order[i], "같은 NPC 가 두 번 나왔다.");
        }

        Assert.True(order.Length <= 32);
    }

    /// <summary>기본 용량은 docs/14 §2 의 4096 이다. NPC 가 더 적으면 그만큼만.</summary>
    [Fact]
    public void ReplanQueue_CapacityIsCappedAt4096()
    {
        Assert.Equal(4_096, new ReplanQueue(10_000).Capacity);
        Assert.Equal(500, new ReplanQueue(500).Capacity);
        Assert.Equal(7, new ReplanQueue(npcCapacity: 500, queueCapacity: 7).Capacity);
        Assert.Equal(4_096, ReplanQueue.DefaultCapacity);
    }

    [Fact]
    public void ReplanQueue_IgnoresOutOfRangeNpc()
    {
        var queue = new ReplanQueue(4);

        Assert.False(queue.TryEnqueue(99, 1));
        Assert.False(queue.TryEnqueue(-1, 1));
        Assert.Equal(0, queue.Count);
        Assert.Equal(-1f, queue.ScoreOf(99));
    }

    /// <summary>T4-01 완료 조건 — 힙 연산 경로의 할당이 0 이다.</summary>
    [Fact]
    public void ReplanQueue_ZeroAlloc()
    {
        var queue = new ReplanQueue(1_000);

        // JIT 티어 승격이 끝날 때까지 돌린다.
        Churn(queue, 30_000);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Churn(queue, 10_000);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        static void Churn(ReplanQueue queue, int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                queue.TryEnqueue(i % 1_000, (i * 31) % 97);
                queue.TryEnqueueUrgent(i % 997, i % 100);
                queue.TryDequeueMax(out _);
                queue.TryDequeue(out _, out _);
            }
        }
    }

    /// <summary>포화 상태의 축출 경로도 할당하지 않는다 — 잎 스캔이 배열 첨자 연산뿐이다.</summary>
    [Fact]
    public void ReplanQueue_ZeroAllocWhenSaturated()
    {
        var queue = new ReplanQueue(npcCapacity: 5_000, queueCapacity: 4_096);

        // 점수는 인터럽트 기준값 아래로만 쓴다 — 넘기면 슬롯 상한(T4-03)에 걸려
        // 포화 경로가 아니라 거절 경로를 재게 된다.
        for (int i = 0; i < 5_000; i++)
        {
            queue.TryEnqueue(i, i % 900);
        }

        Assert.Equal(4_096, queue.Count);
        Assert.Equal(0, queue.UrgentCount);

        for (int i = 0; i < 20_000; i++)
        {
            queue.TryEnqueue(i % 5_000, (i * 7) % 900);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 20_000; i++)
        {
            queue.TryEnqueue(i % 5_000, (i * 7) % 900);
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

    /// <summary>큐를 비우면서 나온 순서.</summary>
    internal static int[] Order(ReplanQueue queue)
    {
        var got = new List<int>();

        while (queue.TryDequeueMax(out int npc))
        {
            got.Add(npc);
        }

        return [.. got];
    }
}
