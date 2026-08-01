using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.Sim;
using Npc.Tests.Sim;

namespace Npc.Tests.TestBed;

/// <summary>
/// T6-33 — 데모 시나리오 3종. docs/20 §11.
///
/// <para>
/// <b>기존 <c>scenarios/</c> 는 건드리지 않는다.</b> 그쪽은 <c>--time-scale 600</c>·5,000 NPC
/// 게이트 회차용이라 틱 좌표가 다르다. 데모는 <c>--time-scale 60</c> 기준이고
/// 게임 1시간 = 600틱 = 실시간 60초다.
/// </para>
///
/// <para>
/// <b>이 테스트가 잡는 것은 오타다.</b> 존 id 하나가 틀리면 데모 도중에 조용히 아무 일도
/// 일어나지 않고, 그때는 "공성이 왜 안 걸리지" 부터 시작해서 한참을 헤맨다 —
/// <c>ScenarioRunner.Parse</c> 는 모르는 존·이벤트·킬스위치 대상에 예외를 던지므로
/// 세 파일을 읽어 보는 것만으로 그 종류의 사고가 전부 걸린다.
/// </para>
/// </summary>
public sealed class DemoScenarioTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>데모 시나리오 파일 이름. docs/20 §11 · §12.</summary>
    public static TheoryData<string> Files => new("demo_day.jsonl", "demo_siege.jsonl", "demo_blackout.jsonl");

    private static string PathOf(string file) => TestPaths.At("testbed", "scenarios", file);

    /// <summary>
    /// T6-33 완료 조건 — 세 파일을 예외 없이 읽는다.
    /// 존 id·이벤트 이름·킬스위치 대상의 오타가 여기서 전부 걸린다.
    /// </summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void DemoScenario_Parses(string file)
    {
        ScenarioRunner scenario = ScenarioRunner.Load(PathOf(file), s_data);

        // 틱 오름차순이어야 결정론이다 (docs/02 §5).
        for (int i = 1; i < scenario.Steps.Length; i++)
        {
            Assert.True(
                scenario.Steps[i - 1].AtTick <= scenario.Steps[i].AtTick,
                $"{file}: 틱이 거꾸로 간다");
        }
    }

    /// <summary>
    /// T6-33 완료 조건 — 존 id 가 전부 <c>zones.json</c> 에 있다.
    ///
    /// <b><see cref="ScenarioRunner.Parse"/> 가 이미 던지지만 다시 본다.</b> 파서가 거르는 것은
    /// "이름을 못 찾는 것" 이고, 여기서 보는 것은 "찾은 code 가 실제 존인가" 다 —
    /// 존을 안 적은 줄의 기본값 0 이 우연히 어떤 존의 code 가 되는 일이 없어야 한다.
    /// </summary>
    [Theory]
    [MemberData(nameof(Files))]
    public void DemoScenario_ZonesExist(string file)
    {
        ScenarioRunner scenario = ScenarioRunner.Load(PathOf(file), s_data);
        ImmutableArray<ZoneDef> zones = s_data.Zones.Zones;

        foreach (ScenarioStep step in scenario.Steps)
        {
            if (step.KillSwitch is not null)
            {
                continue;   // 킬스위치 줄에는 존이 없다
            }

            Assert.Contains(zones, zone => zone.Code == step.Zone);
        }
    }

    /// <summary>
    /// T6-33 완료 조건 — <c>demo_blackout</c> 의 <c>KillSwitch</c> 줄을 게임서버가 무시한다.
    ///
    /// <para>
    /// 게임서버는 이 파일을 그대로 <see cref="ScenarioRunner"/> 에 물리는데, 킬스위치는
    /// <b>NPC 서버 안쪽 상태</b>라 세계에 낼 이벤트가 없다 (docs/20 §11.4). 그래서
    /// 주입 수가 0 이고 이벤트 채널도 비어 있어야 한다 — 여기서 무엇이든 나오면
    /// 게임서버가 NPC 서버의 일을 대신 하고 있는 것이다.
    /// </para>
    /// </summary>
    [Fact]
    public void DemoBlackout_GameServerInjectsNothing()
    {
        ScenarioRunner scenario = ScenarioRunner.Load(PathOf("demo_blackout.jsonl"), s_data);
        SimWorld world = SimWorldTests.NewWorld(4);

        Assert.Equal(3, scenario.Steps.Length);

        long last = scenario.Steps[^1].AtTick;
        var fired = new List<GameEvent>();

        for (long tick = 1; tick <= last; tick++)
        {
            scenario.Tick(new Tick(tick), world);
            fired.AddRange(SimWorldTests.Drain(world));
        }

        Assert.False(scenario.HasMore);
        Assert.Equal(0, scenario.Injected);
        Assert.Empty(fired);

        // 끊긴 것은 러너 자신의 상태에만 남는다. 게임서버는 이 상태를 아무 데도 물리지 않는다 —
        // NPC 서버가 같은 파일을 따로 읽어 자기 KillSwitchState 를 세운다 (docs/20 §11.4).
        Assert.Equal(
            [KillSwitchTarget.T2, KillSwitchTarget.T1, KillSwitchTarget.PlanStore],
            scenario.KillSwitchesFired);
    }

    /// <summary>
    /// <c>demo_day</c> 는 주입 이벤트가 0개다 (docs/20 §11.1).
    ///
    /// <b>비어 있는 것이 이 파일의 내용이다.</b> 나중에 누가 "심심하니 뭐라도 넣자" 로
    /// 이벤트를 하나 더하면 이 시나리오가 보여 주려던 것 — 아무 일도 없이 NPC 가 하루를
    /// 사는 것 — 이 사라진다.
    /// </summary>
    [Fact]
    public void DemoDay_HasNoInjectedEvents()
    {
        ScenarioRunner scenario = ScenarioRunner.Load(PathOf("demo_day.jsonl"), s_data);

        Assert.Empty(scenario.Steps);
    }

    /// <summary>
    /// <c>demo_siege</c> 는 War 로 올렸다가 Peace 로 되돌린다 (docs/20 §11.2).
    ///
    /// <b>되돌리지 않으면 데모가 한쪽으로만 흐른다.</b> 공성이 걸리는 것보다
    /// <b>풀리는 것</b>이 버킷 키가 양방향으로 도는지를 보여 준다.
    /// </summary>
    [Fact]
    public void DemoSiege_ReturnsToPeace()
    {
        ScenarioRunner scenario = ScenarioRunner.Load(PathOf("demo_siege.jsonl"), s_data);

        Assert.Contains(
            scenario.Steps,
            step => step.Kind == GameEventKind.ZoneStateChanged && step.Code == (byte)RegionState.War);

        // 마지막 ZoneStateChanged 는 전부 Peace 여야 한다.
        long last = scenario.Steps[^1].AtTick;

        foreach (ScenarioStep step in scenario.Steps)
        {
            if (step.AtTick == last && step.Kind == GameEventKind.ZoneStateChanged)
            {
                Assert.Equal((byte)RegionState.Peace, step.Code);
            }
        }

        Assert.Contains(
            scenario.Steps,
            step => step.Kind == GameEventKind.WeatherChanged && step.Code == (byte)Climate.Fair);
    }
}
