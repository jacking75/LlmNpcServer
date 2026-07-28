using Npc.Contracts;
using Npc.Core;
using Npc.Host;
using Npc.MasterData;
using Npc.Runtime;

namespace Npc.Tests.Host;

/// <summary>
/// 틱 기반 킬스위치 스케줄. docs/20 §11.4 · T6-13.
///
/// 킬스위치는 NPC 서버 안쪽 상태라 링크로 보낼 수단이 없다. 같은 jsonl 을 양쪽에 주고
/// <b>게임서버는 <c>KillSwitch</c> 줄을 무시, NPC 서버는 그 줄만 처리</b>한다.
/// </summary>
public sealed class KillSwitchScheduleTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>T6-13 완료 조건 — <b>±0틱</b>에 발동한다.</summary>
    [Fact]
    public void KillSwitchSchedule_FiresAtTick()
    {
        var switches = new KillSwitchState();

        KillSwitchSchedule schedule = KillSwitchSchedule.Parse(
            [
                """{"at_tick": 400, "event": "KillSwitch", "target": "T2"}""",
                """{"at_tick": 500, "event": "KillSwitch", "target": "T1"}""",
            ],
            s_data,
            switches);

        Assert.Equal(2, schedule.Count);

        // 399틱까지는 아무것도 끊기지 않는다.
        for (long t = 0; t <= 399; t++)
        {
            schedule.Advance(new Tick(t));
        }

        Assert.False(switches.IsDisabled(KillSwitchTarget.T2));
        Assert.Empty(schedule.Fired);

        // 정확히 400틱에.
        schedule.Advance(new Tick(400));

        Assert.True(switches.IsDisabled(KillSwitchTarget.T2));
        Assert.False(switches.IsDisabled(KillSwitchTarget.T1));
        Assert.Equal([KillSwitchTarget.T2], schedule.Fired);

        schedule.Advance(new Tick(499));

        Assert.False(switches.IsDisabled(KillSwitchTarget.T1));

        schedule.Advance(new Tick(500));

        Assert.True(switches.IsDisabled(KillSwitchTarget.T1));
        Assert.Equal([KillSwitchTarget.T2, KillSwitchTarget.T1], schedule.Fired);
    }

    /// <summary>
    /// T6-13 완료 조건 — <c>KillSwitch</c> 가 아닌 줄은 전부 무시한다.
    ///
    /// <b>여기서 이벤트를 같이 내면 세계가 두 번 밀린다</b> — <c>--link tcp</c> 에서
    /// <c>ZoneStateChanged</c>·<c>WeatherChanged</c> 를 내는 것은 게임서버다.
    /// </summary>
    [Fact]
    public void KillSwitchSchedule_IgnoresNonSwitchLines()
    {
        var switches = new KillSwitchState();

        KillSwitchSchedule schedule = KillSwitchSchedule.Parse(
            [
                """{"_comment": "주석 줄"}""",
                """{"at_tick": 100, "event": "ZoneStateChanged", "zone": "town_center", "code": "Alert"}""",
                """{"at_tick": 200, "event": "WeatherChanged", "zone": "town_center", "code": "Storm"}""",
                """{"at_tick": 300, "event": "KillSwitch", "target": "PlanStore"}""",
            ],
            s_data,
            switches);

        // 넷 중 하나만 스케줄에 들어간다.
        Assert.Equal(1, schedule.Count);

        schedule.Advance(new Tick(1_000));

        Assert.Equal([KillSwitchTarget.PlanStore], schedule.Fired);
        Assert.True(switches.IsDisabled(KillSwitchTarget.PlanStore));
        Assert.False(switches.IsDisabled(KillSwitchTarget.T1));
        Assert.False(switches.IsDisabled(KillSwitchTarget.T2));
    }

    /// <summary>틱을 건너뛰어도 그 사이 예약분이 전부 발동한다 — 지나친 것을 잃지 않는다.</summary>
    [Fact]
    public void KillSwitchSchedule_FiresEverythingUpToNow()
    {
        var switches = new KillSwitchState();

        KillSwitchSchedule schedule = KillSwitchSchedule.Parse(
            [
                """{"at_tick": 10, "event": "KillSwitch", "target": "T2"}""",
                """{"at_tick": 20, "event": "KillSwitch", "target": "T1"}""",
            ],
            s_data,
            switches);

        schedule.Advance(new Tick(1_000));

        Assert.Equal(2, schedule.Fired.Count);
    }

    /// <summary>시나리오가 없으면 빈 스케줄이다. 파일이 없어도 던지지 않는다.</summary>
    [Fact]
    public void KillSwitchSchedule_EmptyWhenNoScenario()
    {
        var switches = new KillSwitchState();

        Assert.Equal(0, KillSwitchSchedule.Load(null, s_data, switches).Count);
        Assert.Equal(0, KillSwitchSchedule.Load("no-such-file.jsonl", s_data, switches).Count);
    }

    /// <summary>감싼 관측자를 그대로 통과시킨다 — 계측이 사라지면 안 된다.</summary>
    [Fact]
    public void KillSwitchSchedule_ForwardsToInnerObserver()
    {
        var inner = new CountingObserver();
        var switches = new KillSwitchState();

        KillSwitchSchedule schedule = KillSwitchSchedule.Parse([], s_data, switches, inner);

        ((ITickObserver)schedule).OnTickBegin(new Tick(1));
        ((ITickObserver)schedule).OnTickEnd(new Tick(1), 5, 3);

        Assert.Equal(1, inner.Begins);
        Assert.Equal(1, inner.Ends);
    }

    /// <summary>
    /// T6-13 완료 조건 — <c>--dev-control</c> 없이 기동하면 <c>/control/*</c> 이 404 다.
    ///
    /// <para>
    /// 라우트를 <b>등록조차 하지 않는</b> 것으로 막는다. 조건부 401/403 이었다면 여기서
    /// 상태 코드만 보게 되는데, 그건 "핸들러가 있고 거절한다" 라서 실수 하나로 열린다.
    /// 그래서 소스에서 <c>MapPost("/control/...")</c> 가 <c>options.DevControl</c> 안쪽에
    /// 있는지를 본다 — 이 저장소가 엔드포인트를 검사해 온 방식이다(<c>DashboardTests</c>).
    /// </para>
    /// </summary>
    [Fact]
    public void DevControl_ControlRoutesAreRegisteredOnlyBehindTheFlag()
    {
        string program = File.ReadAllText(TestPaths.At("src", "Npc.Host", "Program.cs"));

        int guard = program.IndexOf("if (options.DevControl)", StringComparison.Ordinal);

        Assert.True(guard >= 0, "--dev-control 가드가 없다");

        // 가드 블록의 끝. 중첩이 없는 단순 블록이라 여는 중괄호부터 짝을 센다.
        int open = program.IndexOf('{', guard);
        int depth = 0;
        int end = open;

        for (; end < program.Length; end++)
        {
            if (program[end] == '{')
            {
                depth++;
            }
            else if (program[end] == '}' && --depth == 0)
            {
                break;
            }
        }

        string guarded = program[guard..end];

        // /control/ 을 다는 모든 자리가 가드 안쪽이어야 한다.
        foreach (int at in Occurrences(program, "\"/control/"))
        {
            Assert.True(
                at > guard && at < end,
                $"/control/ 라우트가 --dev-control 가드 밖에 있다 (오프셋 {at})");
        }

        Assert.Contains("MapPost(\"/control/killswitch\"", guarded, StringComparison.Ordinal);
    }

    /// <summary>기본값은 꺼짐이고 <c>--dev-control</c> 로만 켜진다.</summary>
    [Fact]
    public void DevControl_IsOffByDefault()
    {
        Assert.True(HostOptions.TryParse([], out HostOptions off, out _));
        Assert.False(off.DevControl);

        Assert.True(HostOptions.TryParse(["--dev-control"], out HostOptions on, out _));
        Assert.True(on.DevControl);
    }

    private static IEnumerable<int> Occurrences(string text, string needle)
    {
        for (int at = text.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            yield return at;
        }
    }

    private sealed class CountingObserver : ITickObserver
    {
        public int Begins { get; private set; }

        public int Ends { get; private set; }

        public void OnTickBegin(Tick tick) => Begins++;

        public void OnTickEnd(Tick tick, int scanned, int drained) => Ends++;
    }
}
