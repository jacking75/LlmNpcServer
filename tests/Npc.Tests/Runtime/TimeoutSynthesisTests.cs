using Npc.Contracts;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;
using Npc.Tests.Fakes;

namespace Npc.Tests.Runtime;

/// <summary>
/// docs/02 §3.4 · docs/03 §6. 명령은 유실된다고 가정한다.
/// 응답 이벤트가 안 오면 로컬에서 ActionFailed(Timeout) 을 합성해 진행을 재개한다.
/// </summary>
public sealed class TimeoutSynthesisTests
{
    private const string SkipPlan = """
        { "schema": 1, "goal": "timeout_plan", "loop": true, "on_step_fail": "skip",
          "steps": [
            { "action": "MoveTo", "args": { "poi": "$workplace" }, "timeout_s": 300 },
            { "action": "Wait", "args": { "duration_s": 60 }, "timeout_s": 60 },
            { "action": "Rest", "args": { "duration_s": 600 }, "timeout_s": 600 }
          ] }
        """;

    private static PlanExecutorTests.Harness NewHarness(int timeScale)
    {
        PlanExecutorTests.Harness h = PlanExecutorTests.NewHarness(1);

        var executor = new PlanExecutor(
            MasterDataLoader.Load(TestPaths.MasterData),
            h.Store,
            h.Plans,
            h.Correlations,
            new CommandEmitter(
                MasterDataLoader.Load(TestPaths.MasterData),
                new PoiBinder(MasterDataLoader.Load(TestPaths.MasterData).Pois)),
            timeScale);

        return h with { Executor = executor };
    }

    /// <summary>T1-33 완료 조건 — 명령 드롭 → 타임아웃 후 다음 스텝으로 진행한다.</summary>
    [Fact]
    public void Executor_TimeoutSynthesis()
    {
        PlanExecutorTests.Harness h = NewHarness(timeScale: 600);
        h.Executor.AssignPlan(0, h.Plans.Register(PlanExecutorTests.Compile(SkipPlan)));

        // 스텝 0 의 명령을 낸다. 게임서버(여기서는 아무도)가 응답하지 않는다.
        h.Executor.Step(new Tick(1), h.Link);

        Assert.Equal((byte)StepStatus.Waiting, h.Store.StepStatus[0]);
        Assert.Equal(0, h.Store.StepIndex[0]);

        long budget = h.Executor.TimeoutTicks(300);
        Assert.True(budget > 0);

        // 예산 안에서는 아무 일도 없다.
        h.Executor.Step(new Tick(1 + budget - 1), h.Link);
        Assert.Equal((byte)StepStatus.Waiting, h.Store.StepStatus[0]);
        Assert.Equal(0, h.Executor.TimeoutsSynthesized);

        // 예산을 넘기면 합성된다.
        h.Executor.Step(new Tick(1 + budget), h.Link);

        Assert.Equal(1, h.Executor.TimeoutsSynthesized);
        Assert.Equal((byte)ActionFailReason.Timeout, h.Store.LastFailReason[0]);

        // on_step_fail = skip 이므로 다음 스텝으로 진행하고 바로 명령을 낸다.
        Assert.Equal(1, h.Store.StepIndex[0]);
        Assert.Equal((byte)StepStatus.Waiting, h.Store.StepStatus[0]);
        Assert.Equal(2, h.Link.Commands.Count);
    }

    /// <summary>타임아웃으로 진행이 막히지 않는다 — 응답이 하나도 없어도 플랜이 계속 돈다.</summary>
    [Fact]
    public void Executor_KeepsMovingWithZeroResponses()
    {
        PlanExecutorTests.Harness h = NewHarness(timeScale: 600);
        h.Executor.AssignPlan(0, h.Plans.Register(PlanExecutorTests.Compile(SkipPlan)));

        for (long tick = 1; tick <= 200; tick++)
        {
            h.Executor.Step(new Tick(tick), h.Link);
        }

        Assert.True(h.Executor.TimeoutsSynthesized >= 3, $"합성이 {h.Executor.TimeoutsSynthesized}회뿐이다.");
        Assert.True(h.Link.Commands.Count >= 4);
        Assert.NotEqual((byte)StepStatus.Done, h.Store.StepStatus[0]);
    }

    /// <summary>타임아웃 뒤 도착한 응답은 낡은 상관 ID 라 무시된다.</summary>
    [Fact]
    public void Executor_LateResponseAfterTimeoutIsIgnored()
    {
        PlanExecutorTests.Harness h = NewHarness(timeScale: 600);
        h.Executor.AssignPlan(0, h.Plans.Register(PlanExecutorTests.Compile(SkipPlan)));

        h.Executor.Step(new Tick(1), h.Link);
        CorrelationId dropped = h.Link.Commands[0].Correlation;

        long budget = h.Executor.TimeoutTicks(300);
        h.Executor.Step(new Tick(1 + budget), h.Link);

        // 뒤늦게 도착한 응답.
        var late = new GameEvent
        {
            Kind = GameEventKind.NpcArrived,
            Sequence = 1_000,
            OccurredAt = new Tick(1 + budget + 5),
            Npc = new NpcId(1),   // A-08 — 슬롯 0 의 전역 id
            Correlation = dropped,
            Poi = new PoiId(h.Store.WorkPoi[0]),
        };

        byte stepBefore = h.Store.StepIndex[0];
        h.Applier.Apply(in late);

        Assert.Equal(1, h.Applier.StaleResponsesIgnored);
        Assert.Equal(stepBefore, h.Store.StepIndex[0]);
    }

    /// <summary>게임 초 → 틱 환산이 배속을 따른다 (docs/11 §5).</summary>
    [Theory]
    [InlineData(1, 300, 3_000)]
    [InlineData(60, 300, 50)]
    [InlineData(600, 300, 5)]
    [InlineData(600, 5, 1)]
    public void Executor_TimeoutTicksFollowTimeScale(int timeScale, int seconds, long expected)
    {
        PlanExecutorTests.Harness h = NewHarness(timeScale);

        Assert.Equal(expected, h.Executor.TimeoutTicks(seconds));
    }

    /// <summary>모든 스텝에 타임아웃이 있어야 한다 — 하나라도 0 이면 그 스텝에서 영구 정지한다.</summary>
    [Fact]
    public void Executor_EveryCompiledStepHasTimeout()
    {
        CompiledPlan plan = PlanExecutorTests.Compile(SkipPlan);

        Assert.All(plan.Steps, s => Assert.True(s.TimeoutSeconds >= PlanDocument.MinTimeoutSeconds));
    }
}
