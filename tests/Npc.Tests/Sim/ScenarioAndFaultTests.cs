using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.Sim;

namespace Npc.Tests.Sim;

/// <summary>docs/02 §5 · docs/11 §6. 시나리오 주입과 고장 주입.</summary>
public sealed class ScenarioAndFaultTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    // ---------------------------------------------------------------- T1-52

    /// <summary>T1-52 완료 조건 — 지정 틱 ±0 에 이벤트가 발생한다.</summary>
    [Fact]
    public void Sim_ScenarioInjectsAtTick()
    {
        SimWorld world = SimWorldTests.NewWorld(4);
        ScenarioRunner scenario = ScenarioRunner.Parse(
            [
                """{"at_tick": 100, "event": "ZoneStateChanged", "zone": "town_center", "code": "War"}""",
                """{"at_tick": 100, "event": "WeatherChanged", "zone": "town_center", "code": "Storm"}""",
                """{"at_tick": 250, "event": "ZoneStateChanged", "zone": "town_center", "code": "Peace"}""",
            ],
            s_data);

        var fired = new List<(long Tick, GameEventKind Kind, byte Code)>();

        for (long tick = 1; tick <= 300; tick++)
        {
            scenario.Tick(new Tick(tick), world);

            foreach (GameEvent ev in SimWorldTests.Drain(world))
            {
                fired.Add((ev.OccurredAt.Value, ev.Kind, ev.Code));
            }
        }

        Assert.Equal(3, fired.Count);
        Assert.Equal(100, fired[0].Tick);
        Assert.Equal(100, fired[1].Tick);
        Assert.Equal(250, fired[2].Tick);

        Assert.Equal(GameEventKind.ZoneStateChanged, fired[0].Kind);
        Assert.Equal((byte)RegionState.War, fired[0].Code);
        Assert.Equal(GameEventKind.WeatherChanged, fired[1].Kind);
        Assert.Equal((byte)Climate.Storm, fired[1].Code);
        Assert.Equal((byte)RegionState.Peace, fired[2].Code);
    }

    /// <summary>저장소의 siege.jsonl 이 실제로 읽히고 존 참조가 유효해야 한다.</summary>
    [Fact]
    public void Sim_LoadsSiegeScenario()
    {
        ScenarioRunner scenario = ScenarioRunner.Load(
            Path.Combine(TestPaths.Scenarios, "siege.jsonl"), s_data);

        Assert.NotEmpty(scenario.Steps);

        // 틱 오름차순이어야 결정론이다.
        for (int i = 1; i < scenario.Steps.Length; i++)
        {
            Assert.True(scenario.Steps[i - 1].AtTick <= scenario.Steps[i].AtTick);
        }

        SimWorld world = SimWorldTests.NewWorld(4);
        long last = scenario.Steps[^1].AtTick;

        for (long tick = 1; tick <= last; tick++)
        {
            scenario.Tick(new Tick(tick), world);
        }

        Assert.False(scenario.HasMore);
        Assert.True(scenario.Injected > 0);

        // KillSwitch 는 이벤트가 아니라 티어 차단 신호다 (docs/15 §4).
        Assert.Equal([KillSwitchTarget.T2, KillSwitchTarget.T1], scenario.KillSwitchesFired);

        // 기록만 남기면 "끊었다고 적어 두고 실제로는 계속 도는" 상태를 못 잡는다 — 상태도 선다.
        Assert.True(scenario.Switches.IsDisabled(KillSwitchTarget.T2));
        Assert.True(scenario.Switches.IsDisabled(KillSwitchTarget.T1));
        Assert.False(scenario.Switches.IsDisabled(KillSwitchTarget.PlanStore));
    }

    /// <summary>
    /// T5-08 완료 조건 — 미지의 타깃 문자열은 <b>로드 시점에</b> 기동 실패다.
    /// 조용히 넘기면 안 끊긴 채로 시나리오 C 가 통과해 게이트가 거짓이 된다.
    /// </summary>
    [Fact]
    public void Sim_ScenarioRejectsUnknownKillSwitchTarget()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ScenarioRunner.Parse(
            ["""{"at_tick": 1, "event": "KillSwitch", "target": "T3"}"""],
            s_data));

        Assert.Contains("T3", error.Message, StringComparison.Ordinal);
        Assert.Contains("PlanStore", error.Message, StringComparison.Ordinal);

        // target 자체가 없어도 실패다.
        Assert.Throws<InvalidDataException>(() => ScenarioRunner.Parse(
            ["""{"at_tick": 1, "event": "KillSwitch"}"""],
            s_data));
    }

    /// <summary>3종 전부 파싱되고 대소문자를 가리지 않는다.</summary>
    [Fact]
    public void Sim_ScenarioParsesAllThreeKillSwitchTargets()
    {
        ScenarioRunner scenario = ScenarioRunner.Parse(
            [
                """{"at_tick": 1, "event": "KillSwitch", "target": "T2"}""",
                """{"at_tick": 2, "event": "KillSwitch", "target": "t1"}""",
                """{"at_tick": 3, "event": "KillSwitch", "target": "planstore"}""",
            ],
            s_data);

        SimWorld world = SimWorldTests.NewWorld(1);

        for (long tick = 1; tick <= 3; tick++)
        {
            scenario.Tick(new Tick(tick), world);
        }

        Assert.Equal(
            [KillSwitchTarget.T2, KillSwitchTarget.T1, KillSwitchTarget.PlanStore],
            scenario.KillSwitchesFired);

        Assert.All(
            Enum.GetValues<KillSwitchTarget>(),
            t => Assert.True(scenario.Switches.IsDisabled(t)));
    }

    [Fact]
    public void Sim_ScenarioRejectsUnknownZone()
    {
        Assert.Throws<InvalidDataException>(() => ScenarioRunner.Parse(
            ["""{"at_tick": 1, "event": "ZoneStateChanged", "zone": "no_such_zone", "code": "War"}"""],
            s_data));
    }

    [Fact]
    public void Sim_ScenarioRejectsUnknownEvent()
    {
        Assert.Throws<InvalidDataException>(() => ScenarioRunner.Parse(
            ["""{"at_tick": 1, "event": "MeteorStrike"}"""],
            s_data));
    }

    // ---------------------------------------------------------------- T1-53

    /// <summary>T1-53 완료 조건 — drop 0.1 이면 타임아웃 합성 경로가 실행된다.</summary>
    [Fact]
    public void Sim_DropRateTriggersTimeoutPath()
    {
        SimWorld world = SimWorldTests.NewWorld(50, new SimOptions(DropRate: 0.1));
        var movement = new MovementSim(world);
        var faults = new FaultInjector(world, movement.TryHandle);
        world.Handler = faults.TryHandle;

        PoiId target = s_data.Pois.OfSubtype("mine")[0];

        for (int npc = 0; npc < 50; npc++)
        {
            var move = new NpcCommand
            {
                Kind = NpcCommandKind.MoveTo,
                Npc = new NpcId(npc),
                IssuedAt = new Tick(1),
                Correlation = new CorrelationId((uint)(npc + 1)),
                Priority = CommandPriority.Normal,
                TargetPoi = target,
            };

            world.ApplyCommand(in move, new Tick(1));
        }

        Assert.True(faults.Dropped > 0, "drop 0.1 인데 하나도 안 버렸다.");
        Assert.True(faults.Passed > 0, "전부 버렸다.");

        // 버려진 명령은 응답이 아예 없다 — 실패 이벤트조차 없어야 타임아웃 합성이 검증된다.
        int moving = movement.Moving;
        Assert.Equal(50 - (int)faults.Dropped, moving);
        Assert.DoesNotContain(SimWorldTests.Drain(world), e => e.Kind == GameEventKind.NpcActionFailed);
    }

    /// <summary>fail-rate 는 실패 이벤트로 답한다 — 드롭과 다르다.</summary>
    [Fact]
    public void Sim_FailRateEmitsFailure()
    {
        SimWorld world = SimWorldTests.NewWorld(50, new SimOptions(FailRate: 0.5));
        var movement = new MovementSim(world);
        var faults = new FaultInjector(world, movement.TryHandle);
        world.Handler = faults.TryHandle;

        PoiId target = s_data.Pois.OfSubtype("mine")[0];

        for (int npc = 0; npc < 50; npc++)
        {
            var move = new NpcCommand
            {
                Kind = NpcCommandKind.MoveTo,
                Npc = new NpcId(npc),
                IssuedAt = new Tick(1),
                Correlation = new CorrelationId((uint)(npc + 1)),
                Priority = CommandPriority.Normal,
                TargetPoi = target,
            };

            world.ApplyCommand(in move, new Tick(1));
        }

        Assert.True(faults.Failed > 0);
        Assert.Equal(
            faults.Failed,
            SimWorldTests.Drain(world).Count(e => e.Kind == GameEventKind.NpcActionFailed));
    }

    /// <summary>같은 시드면 같은 곳에서 같은 고장이 난다.</summary>
    [Fact]
    public void Sim_FaultsAreDeterministic()
    {
        long[] dropped = new long[2];

        for (int run = 0; run < 2; run++)
        {
            SimWorld world = SimWorldTests.NewWorld(100, new SimOptions(DropRate: 0.2, FailRate: 0.2));
            var movement = new MovementSim(world);
            var faults = new FaultInjector(world, movement.TryHandle);
            world.Handler = faults.TryHandle;

            for (int npc = 0; npc < 100; npc++)
            {
                var command = new NpcCommand
                {
                    Kind = NpcCommandKind.MoveTo,
                    Npc = new NpcId(npc),
                    IssuedAt = new Tick(1),
                    Correlation = new CorrelationId((uint)(npc + 1)),
                    Priority = CommandPriority.Normal,
                    TargetPoi = s_data.Pois.OfSubtype("mine")[0],
                };

                world.ApplyCommand(in command, new Tick(1));
            }

            dropped[run] = faults.Dropped;
        }

        Assert.Equal(dropped[0], dropped[1]);
        Assert.True(dropped[0] > 0);
    }

    /// <summary>비율이 0 이면 아무것도 건드리지 않는다.</summary>
    [Fact]
    public void Sim_ZeroRatesPassEverything()
    {
        SimWorld world = SimWorldTests.NewWorld(20);
        var movement = new MovementSim(world);
        var faults = new FaultInjector(world, movement.TryHandle);
        world.Handler = faults.TryHandle;

        for (int npc = 0; npc < 20; npc++)
        {
            var move = new NpcCommand
            {
                Kind = NpcCommandKind.MoveTo,
                Npc = new NpcId(npc),
                IssuedAt = new Tick(1),
                Correlation = new CorrelationId((uint)(npc + 1)),
                Priority = CommandPriority.Normal,
                TargetPoi = s_data.Pois.OfSubtype("mine")[0],
            };

            world.ApplyCommand(in move, new Tick(1));
        }

        Assert.Equal(0, faults.Dropped);
        Assert.Equal(0, faults.Failed);
        Assert.Equal(20, faults.Passed);
    }
}
