using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>
/// D-04 — 개체 파라미터가 런타임에서 실제로 쓰인다.
///
/// <para>
/// 마스터데이터가 읽히는 것과 NPC 가 그대로 도는 것은 다른 문제다. 여기서 보는 것은 셋이다 —
/// <b>순찰 지점이 스텝마다 돈다</b>, <b>전환 오프셋이 결정론이다</b>, <b>세력은 대상 쪽만 나간다</b>.
/// </para>
/// </summary>
public sealed class InstanceParamRuntimeTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>
    /// 순찰 지점은 <b>커서로 돈다</b>.
    ///
    /// <para>
    /// 스텝 번호로 돌리면 <c>loop: true</c> 인 플랜에서 같은 번호가 영원히 돌아와
    /// 그 NPC 는 순찰로의 한 지점만 오간다 — "순찰" 이 아니라 "두 번째 일터" 다.
    /// </para>
    /// </summary>
    [Fact]
    public void Patrol_AdvancesThroughTheWholeRoute()
    {
        NpcStore store = NewStore(4);
        Span<ushort> route = store.PatrolRouteOf(0);

        route[0] = 11;
        route[1] = 22;
        route[2] = 33;
        store.PatrolCount[0] = 3;

        Assert.Equal(11, store.PatrolPointOf(0).Value);
        store.AdvancePatrol(0);
        Assert.Equal(22, store.PatrolPointOf(0).Value);
        store.AdvancePatrol(0);
        Assert.Equal(33, store.PatrolPointOf(0).Value);
        store.AdvancePatrol(0);
        Assert.Equal(11, store.PatrolPointOf(0).Value);

        // 커서는 지점 수 안에 머문다 — 순증만 시키면 byte 가 256 에서 돌아 순서가 튄다.
        for (int i = 0; i < 300; i++)
        {
            store.AdvancePatrol(0);
            Assert.True(store.PatrolCursor[0] < 3, $"커서가 {store.PatrolCursor[0]} 이다");
        }

        Assert.Equal(11, store.PatrolPointOf(0).Value);

        // 순찰로가 없는 NPC 는 0 이고, 커서도 나아가지 않는다.
        store.AdvancePatrol(1);
        Assert.Equal(0, store.PatrolPointOf(1).Value);
        Assert.Equal(0, store.PatrolCursor[1]);
    }

    /// <summary>
    /// <c>$patrol_route</c> 는 순찰 지점으로, 없으면 일터로, 일터도 없으면 집으로 간다.
    ///
    /// <b>실패로 두지 않는 이유</b>는 버킷 플랜이 아키타입 단위라서다 — 같은 위병 플랜을
    /// 순찰로가 있는 개체와 없는 개체가 같이 쓴다.
    /// </summary>
    [Fact]
    public void PatrolSymbol_FallsBackToWorkplace()
    {
        var binder = new PoiBinder(s_data.Pois);
        PoiId home = s_data.Pois.Pois[0].Code;
        PoiId work = s_data.Pois.Pois[1].Code;
        PoiId patrol = s_data.Pois.Pois[2].Code;
        ArchetypeId archetype = s_data.Archetypes.Archetypes[0].Code;

        var with = new PoiBindContext(new NpcId(1), archetype, home, work, home, patrol);
        Assert.True(binder.TryBind(PoiSymbol.PatrolRoute, with, out PoiId bound));
        Assert.Equal(patrol, bound);

        var without = new PoiBindContext(new NpcId(1), archetype, home, work, home);
        Assert.True(binder.TryBind(PoiSymbol.PatrolRoute, without, out bound));
        Assert.Equal(work, bound);

        var homeOnly = new PoiBindContext(new NpcId(1), archetype, home, default, home);
        Assert.True(binder.TryBind(PoiSymbol.PatrolRoute, homeOnly, out bound));
        Assert.Equal(home, bound);

        var nothing = new PoiBindContext(new NpcId(1), archetype, default, default, home);
        Assert.False(binder.TryBind(PoiSymbol.PatrolRoute, nothing, out _));
    }

    /// <summary>심볼 표기가 왕복한다 — 플랜 DSL 이 이 문자열을 쓴다.</summary>
    [Fact]
    public void PatrolSymbol_RoundTrips()
    {
        Assert.True(PoiSymbols.TryParse("$patrol_route", out PoiSymbol symbol));
        Assert.Equal(PoiSymbol.PatrolRoute, symbol);
        Assert.Equal("$patrol_route", PoiSymbols.ToText(PoiSymbol.PatrolRoute));
        Assert.Equal(PoiSymbols.Count + 1, PoiSymbols.Names.Length);
    }

    /// <summary>
    /// <c>schedule_offset_min</c> 은 지터에 <b>가산</b>이다.
    ///
    /// <para>
    /// 대체가 아니라 가산인 이유: 같은 오프셋을 준 NPC 들이 한 틱에 몰리면 완화하려던
    /// 스파이크가 그대로 돌아온다 (docs/14 §5).
    /// </para>
    ///
    /// <para><b>같은 입력이면 같은 결과다</b> — 난수가 아니라 마스터데이터 값이라 리플레이가 산다.</para>
    /// </summary>
    [Fact]
    public void ScheduleOffset_ShiftsDeterministically()
    {
        const int TimeScale = 600;

        // 배속 600 이면 게임 1분 = 1틱이다 (60초 × 10틱 ÷ 600).
        const long TicksPerGameMinute = 60L * Tick.PerSecond / TimeScale;

        long baseline = FirstDue(0, TimeScale);

        Assert.True(baseline >= 0, "오프셋 0 인데 예약이 발화하지 않았다");
        Assert.Equal(baseline + (15 * TicksPerGameMinute), FirstDue(15, TimeScale));
        Assert.Equal(baseline - (15 * TicksPerGameMinute), FirstDue(-15, TimeScale));

        // 같은 입력이면 같은 결과다. 난수를 쓰면 여기가 깨진다.
        Assert.Equal(baseline, FirstDue(0, TimeScale));
    }

    /// <summary>
    /// 명령의 <c>Faction</c> 은 <b>대상</b>의 세력이다.
    ///
    /// <para>
    /// 자기 세력을 찍으면 게임서버가 아군을 적으로 읽는다. 값은 <c>PlayerHostility</c> 가
    /// 실어 준 것 하나뿐이고, 안 실어 주면 0 이다 — <b>지어내지 않는다.</b>
    /// </para>
    /// </summary>
    [Fact]
    public void Faction_OnlyRidesOnCombatCommands()
    {
        var emitter = new CommandEmitter(s_data, new PoiBinder(s_data.Pois));
        PoiId home = s_data.Pois.Pois[0].Code;
        ArchetypeId archetype = s_data.Archetypes.Archetypes[0].Code;

        var ctx = new EmitContext(
            new NpcId(1), archetype, new Tick(1), new CorrelationId(1),
            home, home, home, s_data.Pois[home].Zone,
            Target: default, Instance: default, HostilePlayer: new PlayerId(7),
            Patrol: default, TargetFaction: new FactionId(5));

        Span<NpcCommand> buffer = stackalloc NpcCommand[CommandEmitter.MaxCommandsPerStep];
        Span<int> inventory = stackalloc int[s_data.Items.MaxCode + 1];

        foreach (ActionDef action in s_data.Actions.Actions)
        {
            var step = new CompiledStep { Action = action.Code };
            int written = emitter.Emit(in step, in ctx, inventory, buffer);

            for (int i = 0; i < written; i++)
            {
                NpcCommand command = buffer[i];

                if (command.Kind == NpcCommandKind.CombatAction)
                {
                    Assert.Equal(5, command.Faction.Value);
                }
                else
                {
                    Assert.Equal(0, command.Faction.Value);
                }
            }
        }
    }

    /// <summary>
    /// <b>경비병이 순찰로를 한 바퀴 돈다.</b> D-04 의 완료 조건이다.
    ///
    /// <para>
    /// 발행된 <c>MoveTo</c> 의 <c>TargetPoi</c> 를 본다 — 저장된 값이 아니라
    /// <b>게임서버로 실제로 나가는 값</b>이어야 이 기능이 동작한다고 말할 수 있다.
    /// </para>
    /// </summary>
    [Fact]
    public void Guard_WalksItsWholeRoute()
    {
        PlanExecutorTests.Harness h = PlanExecutorTests.NewHarness(1, "town_guard");

        ImmutableArray<PoiId> route = [.. s_data.Pois.Pois.Take(3).Select(p => p.Code)];
        Span<ushort> slots = h.Store.PatrolRouteOf(0);

        for (int i = 0; i < route.Length; i++)
        {
            slots[i] = route[i].Value;
        }

        h.Store.PatrolCount[0] = (byte)route.Length;

        const string PatrolPlan = """
            { "schema": 1, "goal": "walk_the_beat", "loop": true,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$patrol_route" }, "timeout_s": 300 }
              ] }
            """;

        h.Executor.AssignPlan(
            0, h.Plans.Register(PlanExecutorTests.Compile(PatrolPlan, "town_guard")));

        var visited = new List<ushort>();

        for (int loop = 0; loop < route.Length; loop++)
        {
            h.Link.Commands.Clear();
            h.Store.StepStatus[0] = (byte)StepStatus.Ready;
            h.Executor.Step(new Tick(loop + 1), h.Link);

            visited.Add(Assert.Single(h.Link.Commands).TargetPoi.Value);
        }

        Assert.Equal([.. route.Select(p => p.Value)], visited);
    }

    /// <summary>
    /// NPC 하나를 예약하고 <b>실제로 발화한 틱</b>을 돌려준다. 식을 다시 쓰지 않고 관찰한다 —
    /// 같은 식을 테스트에 옮겨 적으면 그 테스트는 아무것도 지키지 않는다.
    /// </summary>
    private static long FirstDue(short offsetMinutes, int timeScale)
    {
        NpcStore store = NewStore(1);
        PlanStore plans = PlanStore.CreateIdleOnly(s_data);
        var transition = new BucketTransition(store, plans, s_data) { TimeScale = timeScale };
        var swapper = new PlanSwapper(store);

        store.ScheduleOffsetMin[0] = offsetMinutes;

        // 경계를 창 하나만큼 뒤로 잡는다 — 음수 오프셋이 0틱 앞으로 가면 관찰할 수 없다.
        const long Boundary = BucketTransition.JitterWindow;

        transition.Schedule(new Tick(Boundary), TimeOfDay.Noon);

        for (long tick = 0; tick < 100_000; tick++)
        {
            if (transition.Apply(new Tick(tick), swapper) > 0)
            {
                return tick;
            }
        }

        return -1;
    }

    private static NpcStore NewStore(int npcs)
    {
        var store = new NpcStore();
        store.Allocate(npcs, s_data.Items.MaxCode + 1);
        return store;
    }
}
