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

/// <summary>docs/14 §4 — 워커는 틱 루프 밖에서 돌고 PendingPlanId 한 칸만 건드린다.</summary>
public sealed class ReplanWorkerTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>고정 플랜을 돌려주는 컴파일러. <see cref="Gate"/> 로 워커를 안에 붙잡아 둘 수 있다.</summary>
    private sealed class StubCompiler(CompiledPlan? plan) : IPlanCompiler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public List<int> ThreadIds { get; } = [];

        /// <summary>호출이 안에 들어오면 신호한다.</summary>
        public ManualResetEventSlim? Entered { get; init; }

        /// <summary>
        /// 신호가 올 때까지 <b>동기로</b> 기다린다. 비동기 대기로 하면 워커가 스레드를 놓아
        /// 그 스레드가 테스트 스레드로 재사용될 수 있고, 그러면 스레드 id 비교가 무의미해진다.
        /// </summary>
        public ManualResetEventSlim? Hold { get; init; }

        /// <summary>지금까지 기록된 스레드 id. 잠금 아래에서 복사한다.</summary>
        public int[] Snapshot()
        {
            lock (ThreadIds)
            {
                return [.. ThreadIds];
            }
        }

        public ValueTask<PlanCompileResult> CompileAsync(
            PlanRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            lock (ThreadIds)
            {
                ThreadIds.Add(Environment.CurrentManagedThreadId);
            }

            Entered?.Set();
            Hold?.Wait(cancellationToken);

            return ValueTask.FromResult(new PlanCompileResult(
                plan,
                plan is null ? ValidationResult.Fail(ValidationStage.Schema, "V1.PARSE", -1, "테스트") : ValidationResult.Ok,
                CompileStats.None with { Model = "stub", PromptTokens = 100, CompletionTokens = 10 },
                "{}"));
        }
    }

    private sealed record Rig(
        NpcStore Store,
        ReplanQueue Queue,
        ReplanSnapshots Snapshots,
        IndividualPlanPool Pool,
        PlanSwapper Swapper,
        IndividualReplanSource Source,
        PlanStore Plans);

    private static Rig NewRig(int npcs = 8)
    {
        var store = new NpcStore();
        store.Allocate(npcs, s_data.Items.MaxCode + 1);

        for (int i = 0; i < npcs; i++)
        {
            store.StepStatus[i] = (byte)StepStatus.Ready;
            store.ArchetypeCode[i] = 1;
            store.Flags[i] = WorldFlags.IsDay | WorldFlags.RegionPeaceful;
        }

        var queue = new ReplanQueue(npcs);
        var snapshots = new ReplanSnapshots(npcs);
        var pool = new IndividualPlanPool();
        var swapper = new PlanSwapper(store);
        PlanStore plans = PlanStore.CreateIdleOnly(s_data);

        var source = new IndividualReplanSource(
            store, queue, snapshots, pool, swapper, s_data, () => TimeOfDay.Morning);

        return new Rig(store, queue, snapshots, pool, swapper, source, plans);
    }

    private static CompiledPlan SomePlan(PlanStore plans) => plans[PlanStore.IdlePlanId];

    /// <summary>
    /// T4-10 완료 조건 — 워커는 틱 스레드와 다른 스레드에서 돈다.
    /// 워커가 틱 루프를 블록하면 틱 p99 가 폭증한다 (docs/14 §10).
    ///
    /// <b>스레드 id 를 그냥 비교하면 안 된다</b> — xunit 도 워커도 스레드 풀에서 돌기 때문에
    /// 테스트 스레드가 나중에 워커에 재사용될 수 있다. 대신 <b>워커를 컴파일러 안에서 붙잡아 두고</b>
    /// 그 동안 이 스레드가 틱을 계속 돌린다. 틱이 진행됐다는 것 자체가 "워커가 틱 루프를 막지 않았다" 이고,
    /// 붙잡힌 순간의 스레드 id 는 지금 틱을 돌리는 이 스레드와 반드시 다르다.
    /// </summary>
    [Fact]
    public async Task Worker_RunsOutsideTickLoop()
    {
        Rig rig = NewRig(64);

        using var entered = new ManualResetEventSlim(false);
        using var hold = new ManualResetEventSlim(false);
        var compiler = new StubCompiler(SomePlan(rig.Plans))
        {
            Entered = entered,
            Hold = hold,
        };

        var worker = new ReplanWorker(
            rig.Source, compiler, () => new Tick(0), workers: ReplanWorker.DefaultT1Workers);

        for (int npc = 0; npc < 32; npc++)
        {
            rig.Queue.TryEnqueue(npc, npc);
            rig.Snapshots.Capture(npc, rig.Store.Flags[npc], new Tick(0), npc);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await worker.StartAsync(cts.Token);

        // 워커가 컴파일러 안에 들어와 스레드를 점유한 채 붙잡혔다.
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "워커가 컴파일러에 들어오지 않았다.");

        // 이 시점부터 아래 200틱까지 이 스레드는 틱만 돈다. 붙잡힌 워커의 스레드와 겹칠 수 없다.
        int tickThread = Environment.CurrentManagedThreadId;

        // 워커가 블록하고 있어도 틱 루프는 막히지 않는다.
        int ticks = 0;
        var loop = new NpcServerLoop(
            new Fakes.NullSinkLink(),
            new GameClock(s_data.Buckets, 60),
            new EventApplier(s_data, rig.Store, new GameClock(s_data.Buckets, 60), new CorrelationTable(rig.Store.Count)),
            new InterruptMatcher(s_data, rig.Store),
            new CognitionScheduler(rig.Store, new LodBandSet(rig.Store), rig.Plans),
            NewExecutor(rig),
            rig.Swapper,
            new LodBandSet(rig.Store),
            rig.Queue);

        for (; ticks < 200; ticks++)
        {
            loop.RunTick(new Tick(ticks + 1));
        }

        Assert.Equal(200, loop.TicksProcessed);
        Assert.Equal(tickThread, Environment.CurrentManagedThreadId);

        // 붙잡힌 워커는 이 스레드가 아니다 — 이 스레드는 방금 200틱을 돌렸다.
        int[] workerThreads = compiler.Snapshot();
        Assert.NotEmpty(workerThreads);
        Assert.DoesNotContain(tickThread, workerThreads);

        hold.Set();

        while (rig.Queue.Count > 0 && !cts.IsCancellationRequested)
        {
            await Task.Delay(10, cts.Token);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(2, worker.Workers);
        Assert.True(worker.Applied > 0, "워커가 아무 플랜도 반영하지 않았다.");
        Assert.Equal(0, worker.Failed);
    }

    private static PlanExecutor NewExecutor(Rig rig) => new(
        s_data,
        rig.Store,
        rig.Plans,
        new CorrelationTable(rig.Store.Count),
        new CommandEmitter(s_data, new PoiBinder(s_data.Pois)),
        60)
    {
        Swapper = rig.Swapper,
        ReplanQueue = rig.Queue,
    };

    /// <summary>워커는 <c>PendingPlanId</c> 한 칸만 쓴다. 그 밖의 상태는 건드리지 않는다.</summary>
    [Fact]
    public async Task Worker_OnlyWritesPendingPlanId()
    {
        Rig rig = NewRig();
        var compiler = new StubCompiler(SomePlan(rig.Plans));

        var worker = new ReplanWorker(rig.Source, compiler, () => new Tick(100), workers: 1);

        rig.Queue.TryEnqueue(3, 5f);
        rig.Snapshots.Capture(3, rig.Store.Flags[3], new Tick(90), 5f);

        ulong before = rig.Store.StateHash();

        Assert.True(await worker.PumpOnceAsync(CancellationToken.None));

        // PendingPlanId 는 StateHash 에 들어가지 않는다 — 아직 아무 상태도 안 바뀌었어야 한다.
        Assert.Equal(before, rig.Store.StateHash());

        // 대여된 개별 플랜 슬롯이 음수 id 로 걸려 있다.
        int pending = Volatile.Read(ref rig.Store.PendingPlanId[3]);
        Assert.True(IndividualPlanPool.IsIndividual(pending), $"PendingPlanId 가 {pending} 다.");
        Assert.Equal(3, rig.Pool.OwnerOf(pending));
        Assert.Equal(1, rig.Pool.Assigned);
        Assert.Equal(1, rig.Swapper.Requested);

        // 다른 NPC 는 손대지 않았다.
        for (int npc = 0; npc < rig.Store.Count; npc++)
        {
            if (npc != 3)
            {
                Assert.Equal(0, Volatile.Read(ref rig.Store.PendingPlanId[npc]));
            }
        }
    }

    /// <summary>일감이 없으면 아무 일도 하지 않는다.</summary>
    [Fact]
    public async Task Worker_IdlesOnEmptyQueue()
    {
        Rig rig = NewRig();
        var compiler = new StubCompiler(SomePlan(rig.Plans));
        var worker = new ReplanWorker(rig.Source, compiler, () => new Tick(0), workers: 1);

        Assert.False(await worker.PumpOnceAsync(CancellationToken.None));
        Assert.Equal(0, compiler.Calls);
        Assert.Equal(0, worker.Taken);
    }

    /// <summary>낡은 요청은 꺼내는 자리에서 폐기하고 지금 상태로 다시 넣는다 (T4-04 결선).</summary>
    [Fact]
    public async Task Worker_DiscardsStaleJobAndRequeues()
    {
        Rig rig = NewRig();
        var compiler = new StubCompiler(SomePlan(rig.Plans));
        var worker = new ReplanWorker(rig.Source, compiler, () => new Tick(200), workers: 1);

        rig.Queue.TryEnqueue(2, 4f);
        rig.Snapshots.Capture(2, WorldFlags.AtHome | WorldFlags.IsDawn, new Tick(100), 4f);

        // 상황이 6비트 바뀌었다.
        rig.Store.Flags[2] = WorldFlags.InWilderness | WorldFlags.IsNight | WorldFlags.InCombat
            | WorldFlags.ThreatNearby | WorldFlags.IsInjured | WorldFlags.HasTool;

        // 폐기 후 재삽입하므로 이번 TryTake 는 같은 NPC 를 새 스냅샷으로 집어 든다.
        Assert.True(await worker.PumpOnceAsync(CancellationToken.None));
        Assert.Equal(1, rig.Source.Discarded);
        Assert.Equal(1, compiler.Calls);

        // 새로 집어 든 요청의 버킷은 지금 플래그에서 나온다.
        Assert.Equal(1, worker.Applied);
    }

    /// <summary>플랜을 못 만들면 아무것도 반영하지 않는다 — NPC 는 기존 플랜을 계속 쓴다.</summary>
    [Fact]
    public async Task Worker_LeavesPlanAloneOnFailure()
    {
        Rig rig = NewRig();
        var compiler = new StubCompiler(plan: null);
        var worker = new ReplanWorker(rig.Source, compiler, () => new Tick(10), workers: 1);

        rig.Queue.TryEnqueue(1, 3f);
        rig.Snapshots.Capture(1, rig.Store.Flags[1], new Tick(10), 3f);

        Assert.True(await worker.PumpOnceAsync(CancellationToken.None));

        Assert.Equal(1, worker.Failed);
        Assert.Equal(0, worker.Applied);
        Assert.Equal(0, Volatile.Read(ref rig.Store.PendingPlanId[1]));
        Assert.Equal(0, rig.Pool.Assigned);

        // 실패를 다시 큐에 넣지 않는다 — 넣으면 실패가 무한 루프가 된다.
        Assert.False(rig.Queue.Contains(1));
    }

    /// <summary>기본 워커 수는 docs/14 §4 의 T1 2 · T2 8 이다.</summary>
    [Fact]
    public void Worker_DefaultCountsMatchSpec()
    {
        Assert.Equal(2, ReplanWorker.DefaultT1Workers);
        Assert.Equal(8, ReplanWorker.DefaultT2Workers);
        Assert.Equal(50, ReplanWorker.DefaultIdleDelayMs);
    }

    /// <summary>플래그에서 지역상태·기후를 읽는다. 버킷 키 4차원이 다 채워진다.</summary>
    [Fact]
    public void Worker_BuildsFullBucketKey()
    {
        BucketSpace buckets = s_data.Buckets;

        WorldFlags war = buckets.FlagsOf(RegionState.War);
        WorldFlags storm = buckets.FlagsOf(Climate.Storm);

        Assert.Equal(RegionState.Peace, buckets.RegionStateOf(WorldFlags.RegionPeaceful));
        Assert.Equal(RegionState.War, buckets.RegionStateOf(war));
        Assert.Equal(Climate.Fair, buckets.ClimateOf(WorldFlags.None));
        Assert.Equal(Climate.Storm, buckets.ClimateOf(storm));

        BucketKey key = buckets.KeyOf(new ArchetypeId(7), TimeOfDay.Night, war | storm);

        Assert.Equal(7, key.A.Value);
        Assert.Equal(TimeOfDay.Night, key.T);
        Assert.Equal(RegionState.War, key.R);
        Assert.Equal(Climate.Storm, key.C);
    }
}
