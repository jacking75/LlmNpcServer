using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;
using Npc.Tests.Fakes;

namespace Npc.Tests.Runtime;

/// <summary>docs/03 §6 · docs/01 §2.1 emits. 액션 → 명령 매핑은 데이터가 결정한다.</summary>
public sealed class PlanExecutorTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    internal sealed record Harness(
        NpcStore Store,
        PlanStore Plans,
        CorrelationTable Correlations,
        EventApplier Applier,
        PlanExecutor Executor,
        RecordingLink Link,
        GameClock Clock);

    internal static Harness NewHarness(int npcs = 2, string archetype = "blacksmith")
    {
        var store = new NpcStore();
        store.Allocate(npcs, s_data.Items.MaxCode + 1);

        var clock = new GameClock(s_data.Buckets, timeScale: 600);
        var correlations = new CorrelationTable(npcs);
        var applier = new EventApplier(s_data, store, clock, correlations);
        PlanStore plans = PlanStore.CreateIdleOnly(s_data);
        var emitter = new CommandEmitter(s_data, new PoiBinder(s_data.Pois));
        var executor = new PlanExecutor(s_data, store, plans, correlations, emitter);

        Assert.True(s_data.Archetypes.TryGet(archetype, out ArchetypeDef def));
        PoiId home = s_data.Pois.OfSubtype("house")[0];
        PoiId work = def.WorkplacePoiType is { } subtype && s_data.Pois.OfSubtype(subtype).Length > 0
            ? s_data.Pois.OfSubtype(subtype)[0]
            : default;

        for (int i = 0; i < npcs; i++)
        {
            applier.Seed(i, home, s_data.Pois[home].Zone, def.Code, home, work);

            // A-08 — 와이어의 NpcId 는 전역 인스턴스 id 다. 이 하네스는 슬롯 i 에 id i+1 을 앉힌다
            // (0 은 "없음" 이라 쓸 수 없다).
            store.Bind(i, i + 1);
            store.StepStatus[i] = (byte)StepStatus.Ready;
        }

        return new Harness(store, plans, correlations, applier, executor, new RecordingLink(), clock);
    }

    internal static CompiledPlan Compile(string json, string archetype = "blacksmith")
    {
        Assert.True(s_data.Archetypes.TryGet(archetype, out ArchetypeDef def));

        PlanDocument document = JsonSerializer.Deserialize(json, PlanJsonContext.Default.PlanDocument)!;

        return PlanCompiler.Compile(
            document,
            new BucketKey(def.Code, TimeOfDay.Morning, RegionState.Peace, Climate.Fair),
            new PlanId(0),
            s_data);
    }

    private const string ForgePlan = """
        { "schema": 1, "goal": "forge_daily", "loop": true,
          "steps": [
            { "action": "MoveTo", "args": { "poi": "$workplace", "speed": "run" }, "timeout_s": 300 },
            { "action": "Work", "args": { "recipe": "iron_sword", "count": 2 }, "timeout_s": 900 },
            { "action": "MoveTo", "args": { "poi": "$home" }, "timeout_s": 300 },
            { "action": "Sleep", "args": { "until_time": "Morning" } }
          ] }
        """;

    /// <summary>T1-32 완료 조건 — 발행 명령이 actions.json 의 emits 와 일치한다.</summary>
    [Fact]
    public void Executor_EmitsPerCatalog()
    {
        Harness h = NewHarness(1);
        h.Executor.AssignPlan(0, h.Plans.Register(Compile(ForgePlan)));

        h.Executor.Step(new Tick(1), h.Link);

        NpcCommand command = Assert.Single(h.Link.Commands);

        // MoveTo 의 emits: { command: MoveTo, map: { TargetPoi: $poi, Flags: $speed } }
        Assert.Equal(NpcCommandKind.MoveTo, command.Kind);
        Assert.Equal(h.Store.WorkPoi[0], command.TargetPoi.Value);
        Assert.Equal((byte)MoveSpeed.Run, command.Flags);
        Assert.Equal(CommandPriority.Normal, command.Priority);
        Assert.Equal(1, command.IssuedAt.Value);
        Assert.True(h.Correlations.IsCurrent(0, command.Correlation));
        Assert.Equal((byte)StepStatus.Waiting, h.Store.StepStatus[0]);
    }

    [Fact]
    public void Executor_AdvancesOnCompletion()
    {
        Harness h = NewHarness(1);
        h.Executor.AssignPlan(0, h.Plans.Register(Compile(ForgePlan)));

        h.Executor.Step(new Tick(1), h.Link);
        Assert.Equal(0, h.Store.StepIndex[0]);

        Complete(h, 0, tick: 2);
        h.Executor.Step(new Tick(3), h.Link);

        Assert.Equal(1, h.Store.StepIndex[0]);

        // 두 번째 스텝은 Work — emits 는 Interact 다.
        NpcCommand second = h.Link.Commands[^1];
        Assert.Equal(NpcCommandKind.Interact, second.Kind);
        Assert.Equal(h.Store.WorkPoi[0], second.TargetPoi.Value);
        Assert.Equal(2, second.Amount);
    }

    [Fact]
    public void Executor_LoopsBackToFirstStep()
    {
        Harness h = NewHarness(1);
        h.Executor.AssignPlan(0, h.Plans.Register(Compile(ForgePlan)));

        long tick = 1;
        for (int step = 0; step < 4; step++)
        {
            h.Executor.Step(new Tick(tick++), h.Link);
            Complete(h, 0, tick++);
        }

        h.Executor.Step(new Tick(tick), h.Link);

        Assert.Equal(0, h.Store.StepIndex[0]);
        Assert.Equal(5, h.Executor.CommandsEmitted);
    }

    [Fact]
    public void Executor_StopsWhenPlanDoesNotLoop()
    {
        const string Once = """
            { "schema": 1, "goal": "one_shot", "loop": false,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$home" } },
                { "action": "Bathe", "args": {} },
                { "action": "Sleep", "args": { "until_time": "Morning" } }
              ] }
            """;

        Harness h = NewHarness(1, "noble");
        h.Executor.AssignPlan(0, h.Plans.Register(Compile(Once, "noble")));

        long tick = 1;
        for (int step = 0; step < 3; step++)
        {
            h.Executor.Step(new Tick(tick++), h.Link);
            Complete(h, 0, tick++);
        }

        h.Executor.Step(new Tick(tick), h.Link);

        Assert.Equal((byte)StepStatus.Done, h.Store.StepStatus[0]);
    }

    /// <summary>스폰 확인 전에는 명령을 내지 않는다 (docs/02 §3.3).</summary>
    [Fact]
    public void Executor_DoesNotEmitBeforeSpawn()
    {
        Harness h = NewHarness(1);
        h.Store.StepStatus[0] = (byte)StepStatus.Unspawned;
        h.Executor.AssignPlan(0, h.Plans.Register(Compile(ForgePlan)));

        h.Executor.Step(new Tick(1), h.Link);

        Assert.Empty(h.Link.Commands);
    }

    /// <summary>Eat 의 소비 아이템은 인벤토리에서 고른다 — 코드가 "빵"을 모른다.</summary>
    [Fact]
    public void Executor_ResolvesFirstItemWithFlag()
    {
        const string EatPlan = """
            { "schema": 1, "goal": "eat_then_rest", "loop": true,
              "steps": [
                { "action": "Eat", "args": {} },
                { "action": "Rest", "args": { "duration_s": 600 } },
                { "action": "Wait", "args": { "duration_s": 60 } }
              ] }
            """;

        Harness h = NewHarness(1);
        h.Executor.AssignPlan(0, h.Plans.Register(Compile(EatPlan)));

        h.Executor.Step(new Tick(1), h.Link);

        NpcCommand command = Assert.Single(h.Link.Commands);
        Assert.Equal(NpcCommandKind.InventoryChange, command.Kind);
        Assert.Equal(-1, command.Amount);

        // 대장장이 기본 인벤토리의 식량은 bread 다.
        Assert.True(s_data.Items.TryGet("bread", out ItemDef bread));
        Assert.Equal(bread.Code, command.Item);
    }

    /// <summary>T1-32 완료 조건 — 발행 경로 할당 0.</summary>
    [Fact]
    public void Executor_EmitPathDoesNotAllocate()
    {
        Harness h = NewHarness(200);
        PlanId plan = h.Plans.Register(Compile(ForgePlan));

        for (int i = 0; i < h.Store.Count; i++)
        {
            h.Executor.AssignPlan(i, plan);
        }

        var sink = new NullSinkLink();

        // JIT 티어 승격이 끝날 때까지 돌린다. 승격 시점에 한 번 할당이 잡힌다.
        for (int t = 1; t <= 300; t++)
        {
            h.Executor.Step(new Tick(t), sink);
            for (int i = 0; i < h.Store.Count; i++)
            {
                h.Store.StepStatus[i] = (byte)StepStatus.Ready;
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int t = 100; t < 200; t++)
        {
            h.Executor.Step(new Tick(t), sink);
            for (int i = 0; i < h.Store.Count; i++)
            {
                h.Store.StepStatus[i] = (byte)StepStatus.Ready;
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Executor_FailPolicySkipAdvances()
    {
        const string SkipPlan = """
            { "schema": 1, "goal": "skip_plan", "loop": true, "on_step_fail": "skip",
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$workplace" } },
                { "action": "Wait", "args": { "duration_s": 60 } },
                { "action": "Rest", "args": { "duration_s": 600 } }
              ] }
            """;

        Harness h = NewHarness(1);
        h.Executor.AssignPlan(0, h.Plans.Register(Compile(SkipPlan)));

        h.Executor.Step(new Tick(1), h.Link);
        Fail(h, 0, tick: 2, ActionFailReason.Unreachable);
        h.Executor.Step(new Tick(3), h.Link);

        Assert.Equal(1, h.Store.StepIndex[0]);
    }

    [Fact]
    public void Executor_FailPolicyRetryOnceRepeatsThenFallsBack()
    {
        const string RetryPlan = """
            { "schema": 1, "goal": "retry_plan", "loop": true, "on_step_fail": "retry_once",
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$workplace" } },
                { "action": "Wait", "args": { "duration_s": 60 } },
                { "action": "Rest", "args": { "duration_s": 600 } }
              ] }
            """;

        Harness h = NewHarness(1);
        h.Executor.AssignPlan(0, h.Plans.Register(Compile(RetryPlan)));

        h.Executor.Step(new Tick(1), h.Link);
        Fail(h, 0, tick: 2, ActionFailReason.Unreachable);
        h.Executor.Step(new Tick(3), h.Link);

        // 같은 스텝을 다시 낸다.
        Assert.Equal(0, h.Store.StepIndex[0]);
        Assert.Equal(2, h.Link.Commands.Count);

        Fail(h, 0, tick: 4, ActionFailReason.Unreachable);
        h.Executor.Step(new Tick(5), h.Link);

        // 두 번째 실패는 폴백으로 간다.
        Assert.Equal(1, h.Executor.FallbacksTaken);
    }

    [Fact]
    public void Executor_FailPolicyReplanEnqueues()
    {
        const string ReplanPlan = """
            { "schema": 1, "goal": "replan_plan", "loop": true, "on_step_fail": "replan",
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$workplace" } },
                { "action": "Wait", "args": { "duration_s": 60 } },
                { "action": "Rest", "args": { "duration_s": 600 } }
              ] }
            """;

        Harness h = NewHarness(1);
        var queue = new ReplanQueue(1);
        var executor = new PlanExecutor(
            s_data, h.Store, h.Plans, h.Correlations,
            new CommandEmitter(s_data, new PoiBinder(s_data.Pois)))
        {
            ReplanQueue = queue,
        };

        executor.AssignPlan(0, h.Plans.Register(Compile(ReplanPlan)));
        executor.Step(new Tick(1), h.Link);
        Fail(h, 0, tick: 2, ActionFailReason.Unreachable);
        executor.Step(new Tick(3), h.Link);

        Assert.Equal(1, executor.ReplansRequested);
        Assert.True(queue.Contains(0));
    }

    internal static void Complete(Harness h, int npc, long tick)
    {
        var ev = new GameEvent
        {
            Kind = GameEventKind.NpcActionCompleted,
            Sequence = tick,
            OccurredAt = new Tick(tick),
            Npc = new NpcId(h.Store.GlobalOf(npc)),
            Correlation = h.Correlations.Current(npc),
        };

        h.Applier.Apply(in ev);
    }

    internal static void Fail(Harness h, int npc, long tick, ActionFailReason reason)
    {
        var ev = new GameEvent
        {
            Kind = GameEventKind.NpcActionFailed,
            Sequence = tick,
            OccurredAt = new Tick(tick),
            Npc = new NpcId(h.Store.GlobalOf(npc)),
            Correlation = h.Correlations.Current(npc),
            Code = (byte)reason,
        };

        h.Applier.Apply(in ev);
    }
}
