using Npc.Contracts;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>수면이 한 틱에 끝나던 실제 회차의 회귀를 막는다.</summary>
public sealed class HoldingTests
{
    private const string SleepPlan = """
        { "schema": 1, "goal": "sleep_until_morning", "loop": true,
          "steps": [
            { "action": "Sleep", "args": { "until_time": "Morning" } },
            { "action": "MoveTo", "args": { "poi": "$workplace" } }
          ] }
        """;

    [Fact]
    public void SleepDoesNotEmitNextStepBeforeMorning()
    {
        PlanExecutorTests.Harness h = PlanExecutorTests.NewHarness(1);
        h.Executor.AssignPlan(0, h.Plans.Register(PlanExecutorTests.Compile(SleepPlan)));

        h.Executor.Step(new Tick(1), h.Link);
        NpcCommand sleep = Assert.Single(h.Link.Commands);
        Assert.Equal(NpcCommandKind.SetVisualState, sleep.Kind);

        h.Applier.Apply(new GameEvent
        {
            Kind = GameEventKind.NpcActionCompleted,
            Sequence = 1,
            OccurredAt = new Tick(2),
            Npc = new NpcId(1),
            Correlation = sleep.Correlation,
        });
        h.Executor.Step(new Tick(3), h.Link);

        Assert.Equal((byte)StepStatus.Holding, h.Store.StepStatus[0]);
        long wake = h.Store.StepDeadlineTick[0];
        Assert.True(wake >= 60);

        h.Executor.Step(new Tick(wake - 1), h.Link);
        Assert.Single(h.Link.Commands);
        h.Executor.Step(new Tick(wake), h.Link);
        Assert.Equal(NpcCommandKind.MoveTo, h.Link.Commands[^1].Kind);
    }

    [Fact]
    public void HoldingIsSafeSwapBoundary()
    {
        PlanExecutorTests.Harness h = PlanExecutorTests.NewHarness(1);
        var swapper = new PlanSwapper(h.Store);
        var executor = new PlanExecutor(
            Npc.MasterData.MasterDataLoader.Load(TestPaths.MasterData), h.Store, h.Plans, h.Correlations,
            new CommandEmitter(
                Npc.MasterData.MasterDataLoader.Load(TestPaths.MasterData),
                new PoiBinder(Npc.MasterData.MasterDataLoader.Load(TestPaths.MasterData).Pois)),
            timeScale: 600, clock: h.Clock)
        { Swapper = swapper };
        executor.AssignPlan(0, h.Plans.Register(PlanExecutorTests.Compile(SleepPlan)));
        executor.Step(new Tick(1), h.Link);
        NpcCommand sleep = Assert.Single(h.Link.Commands);
        h.Applier.Apply(new GameEvent
        {
            Kind = GameEventKind.NpcActionCompleted,
            Sequence = 1,
            OccurredAt = new Tick(2),
            Npc = new NpcId(1),
            Correlation = sleep.Correlation,
        });
        executor.Step(new Tick(3), h.Link);
        Assert.Equal((byte)StepStatus.Holding, h.Store.StepStatus[0]);

        var replacement = h.Plans.Register(PlanExecutorTests.Compile("""
            { "schema": 1, "goal": "work_now", "loop": true,
              "steps": [{ "action": "MoveTo", "args": { "poi": "$workplace" } }] }
            """));
        swapper.Request(0, replacement);
        Assert.Equal(1, swapper.ApplyPendingSwaps(executor));
        Assert.Equal(replacement.Value, h.Store.PlanId[0]);
        Assert.Equal((byte)StepStatus.Ready, h.Store.StepStatus[0]);
    }
}
