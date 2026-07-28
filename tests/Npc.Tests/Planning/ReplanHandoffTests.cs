using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>
/// 틱 루프 ↔ 워커 인계 통로. docs/14 §4.
///
/// <b>이 통로가 없던 시절의 결함이 이 파일의 존재 이유다.</b> 워커가
/// <see cref="ReplanQueue"/>(동기화 없는 힙)를 직접 꺼내 쓰면 틱 루프의 <c>TryEnqueue</c> 와
/// 경합해 <c>_heap[_count]</c> 가 범위를 벗어난다 — 2026-07-28 실측으로 8회 중 2회
/// <c>IndexOutOfRangeException</c> 이 났다.
/// </summary>
public sealed class ReplanHandoffTests
{
    /// <summary>
    /// <b>결함 재현 테스트.</b> 틱 루프 하나가 계속 밀어 넣고 워커 8개가 계속 가져간다.
    ///
    /// 통로를 거치지 않고 힙을 직접 공유하면 여기서 <c>IndexOutOfRangeException</c> 이 난다.
    /// 통로를 거치면 예외가 없고 <b>한 건도 잃거나 겹치지 않는다.</b>
    /// </summary>
    [Fact]
    public async Task Handoff_SurvivesOneProducerAndManyConsumers()
    {
        const int Capacity = 4_096;
        const int Items = 20_000;
        const int Consumers = 8;

        var queue = new ReplanQueue(npcCapacity: Items, queueCapacity: Capacity);
        var handoff = new ReplanHandoff(capacity: 64);   // 좁게 잡아 포화 경로까지 태운다

        var claimed = new int[Items];
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var consumers = new Task[Consumers];

        for (int i = 0; i < Consumers; i++)
        {
            consumers[i] = Task.Run(
                () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        if (handoff.TryClaim(out int npc, out _))
                        {
                            Interlocked.Increment(ref claimed[npc]);
                            continue;
                        }

                        Thread.SpinWait(4);
                    }
                },
                CancellationToken.None);
        }

        // 생산자 = 틱 루프. 힙에 넣고 통로로 옮기는 것을 반복한다.
        int published = 0;

        for (int npc = 0; npc < Items; npc++)
        {
            queue.TryEnqueue(npc, npc % 97);
            published += handoff.Pump(queue);
        }

        // 힙에 남은 것을 전부 밀어낸다.
        while (queue.Count > 0 && !stop.IsCancellationRequested)
        {
            published += handoff.Pump(queue);
        }

        while (handoff.PendingCount > 0 && !stop.IsCancellationRequested)
        {
            await Task.Delay(1, CancellationToken.None);
        }

        await stop.CancelAsync();
        await Task.WhenAll(consumers);

        // 남은 것을 정리한다 — 취소와 마지막 Claim 사이에 몇 건이 남을 수 있다.
        while (handoff.TryClaim(out int leftover, out _))
        {
            claimed[leftover]++;
        }

        int total = 0;

        for (int npc = 0; npc < Items; npc++)
        {
            // 겹쳐 나온 것이 하나라도 있으면 링이 깨진 것이다.
            Assert.True(claimed[npc] <= 1, $"npc {npc} 가 {claimed[npc]} 번 나왔다 — 중복 소비다.");
            total += claimed[npc];
        }

        // <b>통로를 지난 것과 가져간 것이 정확히 같다.</b> 하나도 잃지 않고 겹치지도 않았다.
        Assert.Equal(published, total);

        // 통로를 좁게(64) 잡았으므로 포화 경로가 실제로 돌았어야 한다 —
        // 그 경로가 안 돌면 이 테스트는 링의 어려운 부분을 건드리지 않은 것이다.
        Assert.True(handoff.PumpBlocked > 0, "통로 포화 경로가 한 번도 돌지 않았다.");

        // 힙에 남은 것 + 통로를 지난 것이 실제로 있었다.
        Assert.True(published > 1_000, $"통로를 지난 것이 {published} 건뿐이다.");
    }

    /// <summary>
    /// 워커가 되돌린 것을 틱 루프가 힙으로 옮긴다. <b>워커는 힙을 만지지 않는다</b> —
    /// 그 경로가 원래 경합의 두 번째 통로였다 (<c>ReplanSnapshots.Requeue</c>).
    /// </summary>
    [Fact]
    public void Handoff_ReturnsGoBackToQueueOnDrain()
    {
        var queue = new ReplanQueue(npcCapacity: 16);
        var handoff = new ReplanHandoff(capacity: 8);

        queue.TryEnqueue(3, 5f);

        Assert.Equal(1, handoff.Pump(queue));
        Assert.True(handoff.TryClaim(out int npc, out float score));
        Assert.Equal(3, npc);
        Assert.Equal(5f, score);

        // 워커가 "낡았다" 고 판단해 되돌린다. 힙은 아직 비어 있다.
        Assert.True(handoff.TryReturn(npc, score));
        Assert.Equal(0, queue.Count);
        Assert.Equal(1, handoff.ReturnCount);

        // 틱 루프가 다음 틱에 옮긴다.
        Assert.Equal(1, handoff.DrainReturns(queue));
        Assert.Equal(1, queue.Count);
        Assert.Equal(0, handoff.ReturnCount);
    }

    /// <summary>
    /// 통로가 차면 <see cref="ReplanHandoff.Pump"/> 는 거기서 멈추고 <b>힙에 남겨 둔다.</b>
    /// 요청을 잃지 않는 것이 중요하다 — 힙은 점수순이라 다음 틱에 더 급한 것이 앞설 수 있다.
    /// </summary>
    [Fact]
    public void Handoff_PumpStopsWhenFullAndKeepsItemsInQueue()
    {
        var queue = new ReplanQueue(npcCapacity: 64);
        var handoff = new ReplanHandoff(capacity: 8);

        for (int npc = 0; npc < 32; npc++)
        {
            queue.TryEnqueue(npc, npc);
        }

        int moved = handoff.Pump(queue, max: 32);

        Assert.Equal(8, moved);                       // 통로 용량까지만
        Assert.Equal(32 - 8, queue.Count);            // 나머지는 힙에 그대로
        Assert.True(handoff.PumpBlocked > 0);
    }

    /// <summary>한 틱에 옮기는 양에 상한이 있다. 없으면 포화 큐가 한 틱을 통째로 먹는다.</summary>
    [Fact]
    public void Handoff_PumpRespectsPerTickLimit()
    {
        var queue = new ReplanQueue(npcCapacity: 512);
        var handoff = new ReplanHandoff(capacity: 512);

        for (int npc = 0; npc < 300; npc++)
        {
            queue.TryEnqueue(npc, npc);
        }

        Assert.Equal(ReplanHandoff.DefaultPumpPerTick, handoff.Pump(queue));
        Assert.Equal(10, handoff.Pump(queue, max: 10));
    }

    /// <summary>급한 것부터 통로에 놓인다 — 힙의 점수 순서가 그대로 이어진다.</summary>
    [Fact]
    public void Handoff_PumpTakesHighestScoreFirst()
    {
        var queue = new ReplanQueue(npcCapacity: 16);
        var handoff = new ReplanHandoff(capacity: 8);

        queue.TryEnqueue(1, 1f);
        queue.TryEnqueue(2, 9f);
        queue.TryEnqueue(3, 5f);

        handoff.Pump(queue);

        Assert.True(handoff.TryClaim(out int first, out _));
        Assert.True(handoff.TryClaim(out int second, out _));
        Assert.True(handoff.TryClaim(out int third, out _));

        Assert.Equal(2, first);
        Assert.Equal(3, second);
        Assert.Equal(1, third);
    }

    /// <summary>빈 통로에서 가져가려 하면 false 다. 예외가 아니다.</summary>
    [Fact]
    public void Handoff_ClaimOnEmptyIsFalse()
    {
        var handoff = new ReplanHandoff(capacity: 8);

        Assert.False(handoff.TryClaim(out int npc, out float score));
        Assert.Equal(-1, npc);
        Assert.Equal(0f, score);
    }
}
