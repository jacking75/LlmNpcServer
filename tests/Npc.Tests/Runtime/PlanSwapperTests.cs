using Npc.Contracts;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>docs/03 §6 스왑 규약. 스텝 경계에서만, 인터럽트는 예외.</summary>
public sealed class PlanSwapperTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private const string PlanA = """
        { "schema": 1, "goal": "plan_a", "loop": true,
          "steps": [
            { "action": "MoveTo", "args": { "poi": "$workplace" }, "timeout_s": 300 },
            { "action": "Work", "args": { "recipe": "iron_sword", "count": 1 }, "timeout_s": 900 },
            { "action": "Rest", "args": { "duration_s": 600 }, "timeout_s": 600 }
          ] }
        """;

    private const string PlanB = """
        { "schema": 1, "goal": "plan_b", "loop": true,
          "steps": [
            { "action": "MoveTo", "args": { "poi": "$home" }, "timeout_s": 300 },
            { "action": "Sleep", "args": { "until_time": "Morning" } },
            { "action": "Rest", "args": { "duration_s": 600 }, "timeout_s": 600 }
          ] }
        """;

    private sealed record Rig(
        PlanExecutorTests.Harness H, PlanSwapper Swapper, PlanExecutor Executor, PlanId A, PlanId B);

    private static Rig NewRig()
    {
        PlanExecutorTests.Harness h = PlanExecutorTests.NewHarness(1);
        var swapper = new PlanSwapper(h.Store);

        var executor = new PlanExecutor(
            s_data, h.Store, h.Plans, h.Correlations,
            new CommandEmitter(s_data, new PoiBinder(s_data.Pois)),
            timeScale: 600)
        {
            Swapper = swapper,
        };

        PlanId a = h.Plans.Register(PlanExecutorTests.Compile(PlanA));
        PlanId b = h.Plans.Register(PlanExecutorTests.Compile(PlanB));

        executor.AssignPlan(0, a);
        return new Rig(h, swapper, executor, a, b);
    }

    /// <summary>T1-34 완료 조건 — 스텝 실행 중 스왑 요청은 경계에서만 교체된다.</summary>
    [Fact]
    public void Executor_AtomicSwap()
    {
        Rig r = NewRig();

        // 스텝 0 을 실행 중으로 만든다.
        r.Executor.Step(new Tick(1), r.H.Link);
        Assert.Equal((byte)StepStatus.Waiting, r.H.Store.StepStatus[0]);
        CorrelationId inFlight = r.H.Link.Commands[0].Correlation;

        // 워커가 새 플랜을 건다.
        r.Swapper.Request(0, r.B);
        Assert.True(r.Swapper.HasPending(0));

        // 아직 진행 중이므로 바꾸지 않는다.
        Assert.Equal(0, r.Swapper.ApplyPendingSwaps(r.Executor));
        Assert.Equal(r.A.Value, r.H.Store.PlanId[0]);
        Assert.Equal(1, r.Swapper.Deferred);

        // 스텝이 끝나면 경계에서 갈아끼운다.
        Complete(r, inFlight, tick: 2);
        r.Executor.Step(new Tick(3), r.H.Link);

        Assert.Equal(r.B.Value, r.H.Store.PlanId[0]);
        Assert.Equal(0, r.H.Store.StepIndex[0]);
        Assert.Equal(1, r.Swapper.Applied);
        Assert.False(r.Swapper.HasPending(0));
    }

    /// <summary>상관 ID 누수 0 — 스왑 전 명령의 응답이 새 플랜의 스텝을 완료시키지 않는다.</summary>
    [Fact]
    public void Executor_SwapDoesNotLeakCorrelation()
    {
        Rig r = NewRig();

        r.Executor.Step(new Tick(1), r.H.Link);
        CorrelationId stale = r.H.Link.Commands[0].Correlation;

        r.Swapper.SwapNow(0, r.B, r.Executor);

        Assert.Equal(r.B.Value, r.H.Store.PlanId[0]);
        Assert.Equal(1, r.Swapper.Immediate);
        Assert.True(r.H.Correlations.IsStale(0, stale));

        // 새 플랜의 첫 스텝을 낸다.
        r.Executor.Step(new Tick(2), r.H.Link);
        Assert.Equal((byte)StepStatus.Waiting, r.H.Store.StepStatus[0]);

        // 낡은 응답이 뒤늦게 도착해도 스텝을 전진시키지 않는다.
        Complete(r, stale, tick: 3);

        Assert.Equal(1, r.H.Applier.StaleResponsesIgnored);
        Assert.Equal(0, r.H.Store.StepIndex[0]);
        Assert.Equal((byte)StepStatus.Waiting, r.H.Store.StepStatus[0]);
    }

    /// <summary>유휴 상태(대기 중이 아님)의 NPC 는 틱 루프의 ApplyPendingSwaps 가 바로 처리한다.</summary>
    [Fact]
    public void Swapper_AppliesToIdleNpcImmediately()
    {
        Rig r = NewRig();

        Assert.Equal((byte)StepStatus.Ready, r.H.Store.StepStatus[0]);

        r.Swapper.Request(0, r.B);

        Assert.Equal(1, r.Swapper.ApplyPendingSwaps(r.Executor));
        Assert.Equal(r.B.Value, r.H.Store.PlanId[0]);
        Assert.Equal(0, r.Swapper.Deferred);
    }

    [Fact]
    public void Swapper_TakeIsAtomicAndOnce()
    {
        Rig r = NewRig();

        r.Swapper.Request(0, r.B);

        Assert.True(r.Swapper.TryTake(0, out PlanId first));
        Assert.Equal(r.B, first);
        Assert.False(r.Swapper.TryTake(0, out _));
        Assert.Equal(1, r.Swapper.Applied);
    }

    [Fact]
    public void Swapper_IgnoresOutOfRangeNpc()
    {
        Rig r = NewRig();

        r.Swapper.Request(999, r.B);

        Assert.Equal(0, r.Swapper.Requested);
    }

    [Fact]
    public void Swapper_ApplyPendingSwapsDoesNotAllocate()
    {
        PlanExecutorTests.Harness h = PlanExecutorTests.NewHarness(500);
        var swapper = new PlanSwapper(h.Store);
        var executor = new PlanExecutor(
            s_data, h.Store, h.Plans, h.Correlations,
            new CommandEmitter(s_data, new PoiBinder(s_data.Pois)))
        {
            Swapper = swapper,
        };

        // JIT 티어 승격이 끝날 때까지 돌린다. 승격 시점에 한 번 할당이 잡힌다.
        for (int i = 0; i < 30_000; i++)
        {
            swapper.ApplyPendingSwaps(executor);
        }

        Assert.Equal(0, AllocationProbe.MinimumBytes(() =>
        {
            for (int i = 0; i < 1_000; i++)
            {
                swapper.ApplyPendingSwaps(executor);
            }
        }));
    }

    private static void Complete(Rig r, CorrelationId correlation, long tick)
    {
        var ev = new GameEvent
        {
            Kind = GameEventKind.NpcActionCompleted,
            Sequence = tick,
            OccurredAt = new Tick(tick),
            Npc = new NpcId(1),   // A-08 — 슬롯 0 의 전역 id
            Correlation = correlation,
        };

        r.H.Applier.Apply(in ev);
    }
}
