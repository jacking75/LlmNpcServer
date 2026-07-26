using Npc.Contracts;
using Npc.Core;
using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>docs/14 §10 — 큐 대기 중 상황이 바뀐 요청은 폐기하고 재삽입한다.</summary>
public sealed class ReplanSnapshotTests
{
    /// <summary>
    /// T4-04 완료 조건 — 큐에 넣을 때와 꺼낼 때의 플래그가 크게 다르면 폐기한다.
    ///
    /// T1 지연이 실측 3.7~5.1s 이고 10Hz 틱이므로 한 요청이 37~51틱을 대기한다.
    /// 그 사이 상황이 바뀐 플랜을 그대로 스왑하면 다음 스캔이 즉시 이탈로 판정해
    /// LLM 예산이 왕복만 하고 진전이 없다.
    /// </summary>
    [Fact]
    public void Replan_DiscardsStaleRequest()
    {
        var queue = new ReplanQueue(64);
        var snapshots = new ReplanSnapshots(64);

        const WorldFlags AtEnqueue =
            WorldFlags.AtWorkplace | WorldFlags.HasTool | WorldFlags.IsDay | WorldFlags.IsRested;

        Assert.True(queue.TryEnqueue(7, 3.5f));
        snapshots.Capture(7, AtEnqueue, new Tick(100), 3.5f);

        Assert.Equal(1, snapshots.Captured);
        Assert.True(snapshots.Of(7).Exists);
        Assert.Equal(AtEnqueue, snapshots.Of(7).Flags);
        Assert.Equal(100, snapshots.Of(7).QueuedTick);

        // ── 상황이 크게 바뀌었다: 밤 · 전투 · 부상 · 일터 이탈 (6비트) ──
        const WorldFlags Now =
            WorldFlags.InWilderness | WorldFlags.HasTool | WorldFlags.IsNight
            | WorldFlags.InCombat | WorldFlags.ThreatNearby | WorldFlags.IsInjured;

        Assert.True(ReplanSnapshots.Drift(AtEnqueue, Now) > snapshots.MaxDrift);
        Assert.True(snapshots.IsStale(7, Now));

        Assert.True(queue.TryDequeueMax(out int npc));
        Assert.Equal(7, npc);

        Assert.False(snapshots.TryAccept(7, Now, out ReplanRequestSnapshot stale));
        Assert.Equal(AtEnqueue, stale.Flags);
        Assert.Equal(1, snapshots.Discarded);
        Assert.Equal(0, snapshots.Accepted);

        // 폐기했으면 재삽입한다 — 그냥 버리면 다음 스캔까지(LOD 2 는 100틱) 이탈 상태로 방치된다.
        Assert.True(snapshots.Requeue(7, Now, new Tick(140), stale.Score, queue));
        Assert.True(queue.Contains(7));
        Assert.Equal(Now, snapshots.Of(7).Flags);
        Assert.Equal(140, snapshots.Of(7).QueuedTick);

        // 이번에는 상황이 그대로다 — 받아들인다.
        Assert.True(queue.TryDequeueMax(out _));
        Assert.True(snapshots.TryAccept(7, Now, out _));
        Assert.Equal(1, snapshots.Accepted);
        Assert.Equal(0.5, snapshots.DiscardRate, 6);

        // 소진된 스냅샷은 지워진다.
        Assert.False(snapshots.Of(7).Exists);
    }

    /// <summary>평범한 진행(장소 이동 + 시간대 전환 = 4비트)은 낡은 것이 아니다.</summary>
    [Fact]
    public void Replan_AcceptsOrdinaryDrift()
    {
        var snapshots = new ReplanSnapshots(8);

        const WorldFlags Before = WorldFlags.AtHome | WorldFlags.IsDawn | WorldFlags.IsRested;
        const WorldFlags After = WorldFlags.AtWorkplace | WorldFlags.IsDay | WorldFlags.IsRested;

        Assert.Equal(4, ReplanSnapshots.Drift(Before, After));
        Assert.Equal(4, ReplanSnapshots.DefaultMaxDrift);

        snapshots.Capture(0, Before, new Tick(1), 2f);

        Assert.False(snapshots.IsStale(0, After));
        Assert.True(snapshots.TryAccept(0, After, out _));
        Assert.Equal(0, snapshots.Discarded);
    }

    /// <summary>스냅샷이 없으면 낡은 것으로 보지 않는다 — 표를 안 붙인 경로가 막히면 안 된다.</summary>
    [Fact]
    public void Replan_MissingSnapshotIsNotStale()
    {
        var snapshots = new ReplanSnapshots(8);

        Assert.False(snapshots.Of(3).Exists);
        Assert.False(snapshots.IsStale(3, WorldFlags.InCombat));
        Assert.True(snapshots.TryAccept(3, WorldFlags.InCombat, out ReplanRequestSnapshot none));
        Assert.False(none.Exists);

        // 범위 밖 첨자는 조용히 무시한다.
        snapshots.Capture(999, WorldFlags.InCombat, new Tick(1), 1f);
        Assert.False(snapshots.Of(999).Exists);
        Assert.False(snapshots.IsStale(999, WorldFlags.None));
    }

    /// <summary>임계를 좁히면 더 많이 폐기한다. ClearAll 은 표를 통째로 되돌린다.</summary>
    [Fact]
    public void Replan_MaxDriftIsConfigurable()
    {
        var strict = new ReplanSnapshots(8, maxDrift: 1);

        strict.Capture(0, WorldFlags.AtHome | WorldFlags.IsDawn, new Tick(1), 1f);
        Assert.True(strict.IsStale(0, WorldFlags.AtWorkplace | WorldFlags.IsDay));

        strict.ClearAll();
        Assert.False(strict.Of(0).Exists);
        Assert.Equal(1, strict.MaxDrift);
    }

    /// <summary>찍기·판정 경로의 할당이 0 이다 — 찍기는 틱 루프 안에서 돈다.</summary>
    [Fact]
    public void Replan_SnapshotPathDoesNotAllocate()
    {
        var snapshots = new ReplanSnapshots(1_000);

        Churn(snapshots, 20_000);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Churn(snapshots, 20_000);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        static void Churn(ReplanSnapshots snapshots, int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                int npc = i % 1_000;

                snapshots.Capture(npc, (WorldFlags)(ulong)i, new Tick(i), i % 10);
                _ = snapshots.IsStale(npc, (WorldFlags)(ulong)(i + 1));
                _ = snapshots.TryAccept(npc, (WorldFlags)(ulong)i, out _);
            }
        }
    }
}
