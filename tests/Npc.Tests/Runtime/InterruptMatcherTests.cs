using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>docs/01 §7 · docs/11 §5. 인터럽트는 LLM 개입 없이 1틱 안에 반응한다.</summary>
public sealed class InterruptMatcherTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private sealed record Rig(
        PlanExecutorTests.Harness H, InterruptMatcher Matcher, PlanExecutor Executor, ReplanQueue Queue);

    private static Rig NewRig(string archetype)
    {
        PlanExecutorTests.Harness h = PlanExecutorTests.NewHarness(1, archetype);
        var queue = new ReplanQueue(1);

        var executor = new PlanExecutor(
            s_data, h.Store, h.Plans, h.Correlations,
            new CommandEmitter(s_data, new PoiBinder(s_data.Pois)),
            timeScale: 600)
        {
            ReplanQueue = queue,
        };

        return new Rig(h, new InterruptMatcher(s_data, h.Store), executor, queue);
    }

    private static GameEvent Combat(int npc, int attacker) => new()
    {
        Kind = GameEventKind.CombatStarted,
        Sequence = 1,
        OccurredAt = new Tick(10),
        Npc = new NpcId(npc),
        OtherNpc = new NpcId(attacker),
    };

    /// <summary>T1-35 완료 조건 — 전투 이벤트가 1틱 안에 Flee/Attack 명령을 낸다.</summary>
    [Fact]
    public void Interrupt_ImmediateWithinOneTick()
    {
        // 겁 많은 아키타입은 도망친다.
        Rig timid = NewRig("child");
        timid.H.Store.Flags[0] |= WorldFlags.ThreatNearby;

        GameEvent ev = Combat(0, 88);
        Assert.True(timid.Matcher.Handle(in ev, new Tick(10), timid.Executor, timid.H.Link, timid.Queue));

        NpcCommand fled = Assert.Single(timid.H.Link.Commands);
        Assert.Equal(NpcCommandKind.MoveTo, fled.Kind);
        Assert.Equal(CommandPriority.Critical, fled.Priority);
        Assert.Equal(10, fled.IssuedAt.Value);
        Assert.NotEqual(0, fled.TargetPoi.Value);

        // 후속 재계획이 큐에 들어간다 — 먼저 도망치고 계획은 나중이다.
        Assert.True(timid.Queue.Contains(0));
        Assert.Equal(100, timid.Queue.ScoreOf(0));

        // 용감한 전투 가능 아키타입은 맞선다.
        Rig brave = NewRig("town_guard");
        brave.H.Store.Flags[0] |= WorldFlags.ThreatNearby;

        GameEvent ev2 = Combat(0, 88);
        Assert.True(brave.Matcher.Handle(in ev2, new Tick(10), brave.Executor, brave.H.Link, brave.Queue));

        NpcCommand fought = Assert.Single(brave.H.Link.Commands);
        Assert.Equal(NpcCommandKind.CombatAction, fought.Kind);
        Assert.Equal(CommandPriority.Critical, fought.Priority);
        Assert.Equal((byte)CombatActionKind.MeleeAttack, fought.Flags);

        // $threat 대상은 이벤트가 알려준다.
        Assert.Equal(new NpcId(88), fought.TargetNpc);
    }

    /// <summary>인터럽트는 진행 중이던 명령을 상관 ID 로 무효화한다 (docs/03 §6).</summary>
    [Fact]
    public void Interrupt_InvalidatesInFlightCommand()
    {
        Rig r = NewRig("town_guard");

        const string Plan = """
            { "schema": 1, "goal": "guard_daily", "loop": true,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$gate" }, "timeout_s": 300 },
                { "action": "Guard", "args": { "poi": "$gate", "duration_s": 1800 }, "timeout_s": 3600 },
                { "action": "Rest", "args": { "duration_s": 600 }, "timeout_s": 600 }
              ] }
            """;

        r.Executor.AssignPlan(0, r.H.Plans.Register(PlanExecutorTests.Compile(Plan, "town_guard")));
        r.Executor.Step(new Tick(1), r.H.Link);

        CorrelationId inFlight = r.H.Link.Commands[0].Correlation;
        r.H.Store.Flags[0] |= WorldFlags.ThreatNearby;

        GameEvent ev = Combat(0, 88);
        r.Matcher.Handle(in ev, new Tick(2), r.Executor, r.H.Link, r.Queue);

        Assert.True(r.H.Correlations.IsStale(0, inFlight));
        Assert.Equal(2, r.H.Link.Commands.Count);
    }

    /// <summary>조건이 안 맞으면 아무 일도 없다.</summary>
    [Fact]
    public void Interrupt_NoMatchDoesNothing()
    {
        Rig r = NewRig("blacksmith");

        var quiet = new GameEvent
        {
            Kind = GameEventKind.NpcTransform,
            Sequence = 1,
            OccurredAt = new Tick(1),
            Npc = new NpcId(0),
        };

        Assert.False(r.Matcher.Handle(in quiet, new Tick(1), r.Executor, r.H.Link, r.Queue));
        Assert.Empty(r.H.Link.Commands);
        Assert.False(r.Queue.Contains(0));
    }

    /// <summary>스폰 확인 전에는 인터럽트도 명령을 내지 않는다.</summary>
    [Fact]
    public void Interrupt_DoesNotEmitBeforeSpawn()
    {
        Rig r = NewRig("child");
        r.H.Store.StepStatus[0] = (byte)StepStatus.Unspawned;
        r.H.Store.Flags[0] |= WorldFlags.ThreatNearby;

        GameEvent ev = Combat(0, 88);
        r.Matcher.Handle(in ev, new Tick(1), r.Executor, r.H.Link, r.Queue);

        Assert.Empty(r.H.Link.Commands);
    }

    /// <summary>플레이어 상호작용은 즉시 멈춰 서서 기다린다.</summary>
    [Fact]
    public void Interrupt_YieldsToPlayer()
    {
        Rig r = NewRig("villager");

        var ev = new GameEvent
        {
            Kind = GameEventKind.PlayerInteracted,
            Sequence = 1,
            OccurredAt = new Tick(5),
            Npc = new NpcId(0),
            Player = new PlayerId(3),
        };

        Assert.True(r.Matcher.Handle(in ev, new Tick(5), r.Executor, r.H.Link, r.Queue));

        NpcCommand waited = Assert.Single(r.H.Link.Commands);
        Assert.Equal(NpcCommandKind.SetVisualState, waited.Kind);
        Assert.Equal(VisualState.Idle, waited.Visual);
        Assert.Equal(70, r.Queue.ScoreOf(0));
    }

    [Fact]
    public void Interrupt_HandleDoesNotAllocate()
    {
        Rig r = NewRig("child");
        r.H.Store.Flags[0] |= WorldFlags.ThreatNearby;

        var sink = new Npc.Tests.Fakes.NullSinkLink();
        GameEvent ev = Combat(0, 88);

        // JIT 티어 승격이 끝날 때까지 충분히 돌린다. 승격 시점에 한 번 할당이 잡힌다.
        for (int i = 0; i < 30_000; i++)
        {
            r.Matcher.Handle(in ev, new Tick(i), r.Executor, sink, r.Queue);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            r.Matcher.Handle(in ev, new Tick(i), r.Executor, sink, r.Queue);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
