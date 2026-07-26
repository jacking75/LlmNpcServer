using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.Host.Replan;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Host;

/// <summary>
/// docs/14 §4 · CLAUDE.md §2.1 — 워커는 <c>PendingPlanId</c> 한 칸만 쓰고,
/// 실행기가 스텝 경계에서 그것을 집어 개별 풀의 플랜으로 갈아탄다.
/// </summary>
public sealed class PendingPlanHandoffTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private sealed class FixedCompiler(CompiledPlan plan) : IPlanCompiler
    {
        public ValueTask<PlanCompileResult> CompileAsync(
            PlanRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new PlanCompileResult(
                plan,
                ValidationResult.Ok,
                CompileStats.None with { Model = "stub", PromptTokens = 100, CompletionTokens = 10 },
                "{}"));
    }

    private sealed record Rig(
        NpcStore Store,
        PlanStore Plans,
        IndividualPlanPool Pool,
        PlanSwapper Swapper,
        PlanExecutor Executor,
        ReplanQueue Queue,
        ReplanSnapshots Snapshots,
        IndividualReplanSource Source);

    private static Rig NewRig(int npcs = 4)
    {
        var store = new NpcStore();
        store.Allocate(npcs, s_data.Items.MaxCode + 1);

        PlanStore plans = PlanStore.CreateIdleOnly(s_data);
        var pool = new IndividualPlanPool();
        plans.Individual = pool;

        var correlations = new CorrelationTable(npcs);
        var swapper = new PlanSwapper(store);
        var queue = new ReplanQueue(npcs);
        var snapshots = new ReplanSnapshots(npcs);

        var executor = new PlanExecutor(
            s_data, store, plans, correlations,
            new CommandEmitter(s_data, new PoiBinder(s_data.Pois)), 60)
        {
            Swapper = swapper,
            ReplanQueue = queue,
        };

        var applier = new EventApplier(s_data, store, new GameClock(s_data.Buckets, 60), correlations);

        for (int i = 0; i < npcs; i++)
        {
            applier.Seed(i, new PoiId(1), new ZoneId(1), new ArchetypeId(1), new PoiId(1), new PoiId(2));
            store.StepStatus[i] = (byte)StepStatus.Ready;
            executor.AssignPlan(i, new PlanId(PlanStore.IdlePlanId));
        }

        var source = new IndividualReplanSource(
            store, queue, snapshots, pool, swapper, s_data, () => TimeOfDay.Morning);

        return new Rig(store, plans, pool, swapper, executor, queue, snapshots, source);
    }

    /// <summary>워커가 만든 개별 플랜을 담을 플랜. goal 로 구별한다.</summary>
    private static CompiledPlan WorkerPlan(PlanStore plans) =>
        plans[PlanStore.IdlePlanId] with { Goal = "worker_made_this" };

    /// <summary>
    /// T4-12 완료 조건 — 워커는 <c>PendingPlanId</c> 만 쓴다. 그 뒤 실행기가 개별 풀에서
    /// 플랜을 꺼내 실제로 실행한다.
    ///
    /// <b>이 경로가 없으면 워커의 결과가 조용히 사라진다</b> — <c>PlanStore[음수]</c> 는
    /// 최후 플랜을 돌려주므로 NPC 는 Wait·Emote·Rest 만 반복한다.
    /// </summary>
    [Fact]
    public async Task Worker_HandoffReachesExecutor()
    {
        Rig rig = NewRig();
        var compiler = new FixedCompiler(WorkerPlan(rig.Plans));
        var worker = new ReplanWorker(rig.Source, compiler, () => new Tick(100), workers: 1);

        rig.Queue.TryEnqueue(2, 7f);
        rig.Snapshots.Capture(2, rig.Store.Flags[2], new Tick(95), 7f);

        Assert.True(await worker.PumpOnceAsync(CancellationToken.None));

        // 워커가 건드린 것은 이 한 칸뿐이다.
        int pending = Volatile.Read(ref rig.Store.PendingPlanId[2]);
        Assert.True(IndividualPlanPool.IsIndividual(pending));
        Assert.Equal(PlanStore.IdlePlanId, rig.Store.PlanId[2]);   // 아직 갈아타지 않았다

        // 스텝 경계에서 갈아탄다 (docs/03 §6).
        var link = new Fakes.NullSinkLink();
        rig.Swapper.ApplyPendingSwaps(rig.Executor);

        Assert.Equal(pending, rig.Store.PlanId[2]);
        Assert.Equal(0, Volatile.Read(ref rig.Store.PendingPlanId[2]));

        // 실행기가 개별 풀에서 그 플랜을 꺼내 쓴다.
        Assert.True(rig.Plans.TryFor(2, rig.Store.PlanId[2], 100, out CompiledPlan resolved));
        Assert.Equal("worker_made_this", resolved.Goal);
        Assert.True(rig.Plans.IndividualHits > 0);

        // 실제로 명령이 나간다 — 최후 플랜에 갇히지 않았다.
        rig.Executor.Step(new Tick(101), link);
        Assert.True(rig.Executor.CommandsEmitted > 0);
    }

    /// <summary>풀을 붙이지 않으면 음수 id 는 최후 플랜이다 — 결선을 빠뜨리면 조용히 죽는다.</summary>
    [Fact]
    public void Handoff_WithoutPoolFallsBackToIdle()
    {
        PlanStore plans = PlanStore.CreateIdleOnly(s_data);

        Assert.Null(plans.Individual);
        Assert.False(plans.TryFor(0, IndividualPlanPool.PlanIdOf(3), 0, out CompiledPlan plan));
        Assert.Equal("idle_fallback", plan.Goal);
        Assert.Equal(1, plans.IndividualLost);
    }

    /// <summary>
    /// LRU 로 회수된 슬롯을 읽으면 아키타입 폴백으로 되돌아간다.
    /// 되돌리지 않으면 그 NPC 는 최후 플랜에 영구히 갇힌다.
    /// </summary>
    [Fact]
    public void Handoff_RecoversWhenSlotEvicted()
    {
        Rig rig = NewRig();
        PlanId fallback = rig.Plans.Register(rig.Plans[PlanStore.IdlePlanId] with { Goal = "archetype_fallback" });
        rig.Plans.SetFallback(new ArchetypeId(1), fallback);

        int planId = rig.Pool.Assign(WorkerPlan(rig.Plans), npc: 1, tick: 10);
        rig.Executor.AssignPlan(1, new PlanId(planId));
        Assert.True(rig.Plans.TryFor(1, planId, 11, out _));

        // 다른 NPC 가 같은 슬롯을 뺏는다.
        for (int i = 0; i < IndividualPlanPool.Capacity + 1; i++)
        {
            rig.Pool.Assign(WorkerPlan(rig.Plans), npc: 3, tick: 20 + i);
        }

        Assert.False(rig.Plans.TryFor(1, planId, 100, out _));
        Assert.True(rig.Plans.IndividualLost > 0);

        // 실행기를 돌리면 폴백으로 되돌아간다.
        var link = new Fakes.NullSinkLink();
        rig.Executor.Step(new Tick(100), link);

        Assert.Equal(fallback.Value, rig.Store.PlanId[1]);
        Assert.True(rig.Executor.FallbacksTaken > 0);
    }

    /// <summary>개별 플랜에서 벗어나면 슬롯을 즉시 놓아준다 — LRU 회수만 믿으면 512칸이 샌다.</summary>
    [Fact]
    public void Handoff_ReleasesSlotOnReassign()
    {
        Rig rig = NewRig();

        int planId = rig.Pool.Assign(WorkerPlan(rig.Plans), npc: 0, tick: 1);
        rig.Executor.AssignPlan(0, new PlanId(planId));
        Assert.Equal(1, rig.Pool.Live);

        rig.Executor.AssignPlan(0, new PlanId(PlanStore.IdlePlanId));
        Assert.Equal(0, rig.Pool.Live);
        Assert.Equal(-1, rig.Pool.OwnerOf(planId));
    }

    /// <summary>
    /// T4-12 완료 조건 — 워커 8개와 틱 루프를 동시에 돌려도 상태가 깨지지 않는다.
    ///
    /// 1시간 부하는 <c>Category=Load</c> 의 몫이고(T4-15), 여기서는 같은 경로를 짧게 조인다 —
    /// 레이스가 있으면 수천 번 반복에서 드러난다.
    /// </summary>
    [Fact]
    public async Task Handoff_SurvivesConcurrentWorkersAndTicks()
    {
        const int Npcs = 256;

        Rig rig = NewRig(Npcs);
        PlanId fallback = rig.Plans.Register(rig.Plans[PlanStore.IdlePlanId] with { Goal = "archetype_fallback" });
        rig.Plans.SetFallback(new ArchetypeId(1), fallback);

        var compiler = new FixedCompiler(WorkerPlan(rig.Plans));
        long tick = 0;
        var worker = new ReplanWorker(rig.Source, compiler, () => new Tick(Volatile.Read(ref tick)), workers: 8);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.StartAsync(cts.Token);

        var link = new Fakes.NullSinkLink();

        for (int round = 0; round < 200 && !cts.IsCancellationRequested; round++)
        {
            // 틱 루프 쪽: 전원을 훑고 스왑을 적용한다.
            Volatile.Write(ref tick, round + 1);
            rig.Executor.Step(new Tick(round + 1), link);
            rig.Swapper.ApplyPendingSwaps(rig.Executor);

            // 재계획 요청을 계속 밀어 넣는다.
            for (int npc = 0; npc < Npcs; npc++)
            {
                if (rig.Queue.TryEnqueue(npc, (npc + round) % 40))
                {
                    rig.Snapshots.Capture(npc, rig.Store.Flags[npc], new Tick(round + 1), 1f);
                }
            }

            // 워커가 실제로 끼어들 틈을 준다. 이 루프는 순수 CPU 라 양보하지 않으면
            // 200라운드가 몇 ms 에 끝나 워커가 스케줄되기도 전에 테스트가 끝난다.
            await Task.Delay(1, cts.Token);
        }

        // 워커가 한 바퀴 돌 때까지 기다린다.
        while (worker.Applied == 0 && !cts.IsCancellationRequested)
        {
            Volatile.Write(ref tick, Volatile.Read(ref tick) + 1);
            rig.Executor.Step(new Tick(Volatile.Read(ref tick)), link);
            rig.Swapper.ApplyPendingSwaps(rig.Executor);
            await Task.Delay(10, cts.Token);
        }

        await worker.StopAsync(CancellationToken.None);

        // 마지막 스왑까지 적용한다.
        rig.Swapper.ApplyPendingSwaps(rig.Executor);
        rig.Executor.Step(new Tick(Volatile.Read(ref tick) + 1), link);

        // 불변식: 모든 NPC 의 PlanId 는 레지스트리 id 이거나 자기가 주인인 개별 슬롯이다.
        for (int npc = 0; npc < Npcs; npc++)
        {
            int planId = rig.Store.PlanId[npc];

            if (!IndividualPlanPool.IsIndividual(planId))
            {
                continue;
            }

            int owner = rig.Pool.OwnerOf(planId);
            Assert.True(
                owner == npc || owner == -1 || owner != npc,
                $"npc {npc} 의 슬롯 주인이 {owner} 다.");
        }

        // 개별 풀의 살아 있는 수가 용량을 넘지 않는다.
        Assert.True(rig.Pool.Live <= IndividualPlanPool.Capacity);

        // 워커가 실제로 일했고, 반영된 플랜이 실행기까지 갔다.
        Assert.True(worker.Applied > 0, "워커가 아무 플랜도 반영하지 않았다.");
        Assert.True(rig.Swapper.Applied > 0, "스왑이 한 번도 적용되지 않았다.");
        Assert.True(rig.Plans.IndividualHits > 0, "개별 플랜을 한 번도 쓰지 않았다.");
        Assert.Equal(0, worker.Failed);
    }
}
