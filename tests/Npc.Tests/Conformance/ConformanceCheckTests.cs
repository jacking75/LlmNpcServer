using System.Collections.Immutable;
using Npc.Conformance;
using Npc.Conformance.Checks;
using Npc.Contracts;

namespace Npc.Tests.Conformance;

/// <summary>
/// B-07 — 적합성 검사가 <b>고의 위반을 실제로 잡는가</b>.
///
/// <para>
/// <b>통과만 시험한 검사는 검사가 아니다.</b> 규약을 지키는 스트림에 대해 "통과" 를 내는 것은
/// 아무것도 안 하는 검사도 할 수 있다. 그래서 검사마다 <b>깨끗한 스트림 + 고의 위반</b> 둘을
/// 준다.
/// </para>
///
/// <para>
/// 검사가 순수 함수인 것이 이 회차를 가능하게 한다 — 소켓을 붙여야만 돌릴 수 있었다면
/// "게임서버가 근접을 잘못 낸다" 를 만들어 낼 방법이 없다.
/// </para>
/// </summary>
public sealed class ConformanceCheckTests
{
    // ---------------------------------------------------------------- 핸드셰이크

    /// <summary>재동기화가 규약대로면 통과한다.</summary>
    [Fact]
    public void Handshake_PassesOnACleanResync()
    {
        CheckResult result = new HandshakeCheck().Run(Clean());

        Assert.Equal(Verdict.Pass, result.Verdict);
    }

    /// <summary>
    /// <b>존 상태가 스폰보다 먼저 오면 위반이다.</b> NPC 서버는 <c>NpcSpawned</c> 를 받아야
    /// 그 NPC 가 세계에 있다고 본다.
    /// </summary>
    [Fact]
    public void Handshake_CatchesZoneBeforeSpawn()
    {
        Observation observation = Clean() with
        {
            Events =
            [
                Event(GameEventKind.ZoneStateChanged, 1, tick: 100, zone: 1),
                Event(GameEventKind.WeatherChanged, 2, tick: 100, zone: 1),
                Event(GameEventKind.NpcSpawned, 3, tick: 100, npc: 0),
                Event(GameEventKind.NpcSpawned, 4, tick: 100, npc: 1),
            ],
        };

        CheckResult result = new HandshakeCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("먼저 왔다", StringComparison.Ordinal));
    }

    /// <summary>프레임 하나에 256건을 넘기면 위반이다.</summary>
    [Fact]
    public void Handshake_CatchesOversizedFrame()
    {
        var events = ImmutableArray.CreateBuilder<Observed>();

        for (int i = 0; i < HandshakeCheck.MaxEventsPerFrame + 1; i++)
        {
            events.Add(Event(GameEventKind.NpcSpawned, i + 1, tick: 100, npc: i, frame: 1));
        }

        Observation observation = Clean() with { Events = events.ToImmutable(), ZoneCount = 0 };

        CheckResult result = new HandshakeCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("상한", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- TickSync

    /// <summary>구동 회차에서는 속도를 판정하지 않는다. <b>미판정이지 통과가 아니다.</b></summary>
    [Fact]
    public void TickSync_DoesNotJudgeRateWithoutAWallClock()
    {
        CheckResult result = new TickSyncCheck().Run(Ticking(20));

        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Contains("속도 미판정", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>틱이 되감기면 위반이다 — 타임아웃 합성이 통째로 어긋난다.</summary>
    [Fact]
    public void TickSync_CatchesRewind()
    {
        Observation observation = Ticking(10) with
        {
            Events =
            [
                Event(GameEventKind.TickSync, 1, tick: 100),
                Event(GameEventKind.TickSync, 2, tick: 101),
                Event(GameEventKind.TickSync, 3, tick: 50),
            ],
        };

        CheckResult result = new TickSyncCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("되감겼다", StringComparison.Ordinal));
    }

    /// <summary>벽시계가 있으면 10Hz 를 실제로 판정한다.</summary>
    [Fact]
    public void TickSync_CatchesWrongRate()
    {
        // 100틱을 20초에 흘렸다 = 5Hz. 규약의 절반이다.
        Observation observation = Ticking(101) with { WallClockMillis = 20_000 };

        CheckResult result = new TickSyncCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("틱 레이트", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 시퀀스

    /// <summary>
    /// <b>시퀀스 리셋은 NPC 를 영원히 멈춘다.</b> <c>EventApplier</c> 가 이후 이벤트를
    /// 전부 중복으로 버린다.
    /// </summary>
    [Fact]
    public void Sequence_CatchesReset()
    {
        Observation observation = Clean() with
        {
            Events =
            [
                Event(GameEventKind.TickSync, 100, tick: 1),
                Event(GameEventKind.TickSync, 101, tick: 2),
                Event(GameEventKind.TickSync, 1, tick: 3),      // 재접속하며 되감았다
                Event(GameEventKind.TickSync, 2, tick: 4),
            ],
        };

        CheckResult result = new SequenceCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("되감겼다", StringComparison.Ordinal));
    }

    /// <summary>갭도 잡는다.</summary>
    [Fact]
    public void Sequence_CatchesGap()
    {
        Observation observation = Clean() with
        {
            Events =
            [
                Event(GameEventKind.TickSync, 1, tick: 1),
                Event(GameEventKind.TickSync, 9, tick: 2),
            ],
        };

        CheckResult result = new SequenceCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("갭", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 근접

    /// <summary>Enter → Leave 한 쌍은 통과한다.</summary>
    [Fact]
    public void Proximity_PassesOnAProperPair()
    {
        Observation observation = Clean() with
        {
            Events =
            [
                Proximity(1, npc: 7, tick: 100, distance: 150, ProximityChange.Enter),
                Proximity(2, npc: 7, tick: 110, distance: 240, ProximityChange.Leave),
            ],
        };

        CheckResult result = new ProximityCheck().Run(observation);

        Assert.Equal(Verdict.Pass, result.Verdict);
    }

    /// <summary>
    /// <b>히스테리시스가 없으면 재계획 큐가 포화한다.</b> 210m 에서 Leave 를 내는 것은
    /// 200/220 규약을 어긴 것이다 — 그 NPC 는 경계에서 계속 들락날락한다.
    /// </summary>
    [Fact]
    public void Proximity_CatchesMissingHysteresis()
    {
        Observation observation = Clean() with
        {
            Events =
            [
                Proximity(1, npc: 7, tick: 100, distance: 150, ProximityChange.Enter),
                Proximity(2, npc: 7, tick: 110, distance: 210, ProximityChange.Leave),
            ],
        };

        CheckResult result = new ProximityCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("히스테리시스", StringComparison.Ordinal));
    }

    /// <summary>에지 트리거 — 관측 중인데 Enter 를 또 내면 위반이다.</summary>
    [Fact]
    public void Proximity_CatchesRepeatedEnter()
    {
        Observation observation = Clean() with
        {
            Events =
            [
                Proximity(1, npc: 7, tick: 100, distance: 150, ProximityChange.Enter),
                Proximity(2, npc: 7, tick: 110, distance: 140, ProximityChange.Enter),
            ],
        };

        CheckResult result = new ProximityCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("또 왔다", StringComparison.Ordinal));
    }

    /// <summary>판정 주기 — 5틱보다 촘촘하면 매 틱 재고 있다는 뜻이다.</summary>
    [Fact]
    public void Proximity_CatchesTooFrequentJudgement()
    {
        Observation observation = Clean() with
        {
            Events =
            [
                Proximity(1, npc: 7, tick: 100, distance: 150, ProximityChange.Enter),
                Proximity(2, npc: 7, tick: 101, distance: 230, ProximityChange.Leave),
            ],
        };

        CheckResult result = new ProximityCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("판정 주기", StringComparison.Ordinal));
    }

    /// <summary>근접 이벤트가 0건이면 <b>미판정</b>이다. 통과로 세지 않는다.</summary>
    [Fact]
    public void Proximity_IsNotCheckedWithoutEvents()
    {
        CheckResult result = new ProximityCheck().Run(Clean());

        Assert.Equal(Verdict.NotChecked, result.Verdict);
    }

    // ---------------------------------------------------------------- 전투

    /// <summary><c>CombatStarted</c> 없는 <c>DamageTaken</c> 은 위반이다.</summary>
    [Fact]
    public void Combat_CatchesDamageWithoutCombat()
    {
        Observation observation = Clean() with
        {
            Events = [Damage(1, npc: 3, tick: 100, amount: 10)],
        };

        CheckResult result = new CombatCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("CombatStarted 없이", StringComparison.Ordinal));
    }

    /// <summary>순서가 맞으면 통과한다.</summary>
    [Fact]
    public void Combat_PassesWhenOrdered()
    {
        Observation observation = Clean() with
        {
            Events =
            [
                Event(GameEventKind.CombatStarted, 1, tick: 100, npc: 3),
                Damage(2, npc: 3, tick: 101, amount: 10),
                Event(GameEventKind.CombatEnded, 3, tick: 120, npc: 3),
            ],
        };

        CheckResult result = new CombatCheck().Run(observation);

        Assert.Equal(Verdict.Pass, result.Verdict);
    }

    // ---------------------------------------------------------------- 명령 응답

    /// <summary>
    /// <b>응답을 안 보내는 게임서버는 겉보기에 잘 돈다.</b> NPC 서버가 타임아웃을 합성해
    /// 진행하기 때문이다 — 그래서 응답률을 재야 한다.
    /// </summary>
    [Fact]
    public void CommandResponse_CatchesSilentServer()
    {
        var commands = ImmutableArray.CreateBuilder<Issued>();

        for (int i = 0; i < 10; i++)
        {
            commands.Add(new Issued(
                new CorrelationId((uint)(i + 1)), NpcCommandKind.MoveTo, new NpcId(i), 100, 50));
        }

        Observation observation = Clean() with { Commands = commands.ToImmutable() };

        CheckResult result = new CommandResponseCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("응답률", StringComparison.Ordinal));
    }

    /// <summary>마감을 넘겨 온 응답도 잡는다.</summary>
    [Fact]
    public void CommandResponse_CatchesLateAnswer()
    {
        Observation observation = Clean() with
        {
            Commands = [new Issued(new CorrelationId(1), NpcCommandKind.MoveTo, new NpcId(0), 100, 10)],
            Events = [Arrived(1, npc: 0, tick: 500, correlation: 1)],
        };

        CheckResult result = new CommandResponseCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("마감", StringComparison.Ordinal));
    }

    /// <summary>상관 ID 를 안 되돌려주면 잡는다 (N5).</summary>
    [Fact]
    public void CommandResponse_CatchesUnknownCorrelation()
    {
        Observation observation = Clean() with
        {
            Commands = [new Issued(new CorrelationId(1), NpcCommandKind.MoveTo, new NpcId(0), 100, 100)],
            Events = [Arrived(1, npc: 0, tick: 110, correlation: 1), Arrived(2, npc: 0, tick: 111, correlation: 999)],
        };

        CheckResult result = new CommandResponseCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("모르는 상관 ID", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 처리량

    /// <summary>드롭이 있으면 위반이다.</summary>
    [Fact]
    public void Throughput_CatchesDrops()
    {
        Observation observation = Clean() with { CommandsDropped = 3 };

        CheckResult result = new ThroughputCheck().Run(observation);

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Contains(result.Violations, v => v.Contains("드롭", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- 보고서

    /// <summary>
    /// <b>미판정은 불합격이 아니다.</b> 그러나 보고서에는 반드시 남는다 —
    /// 미판정을 통과로 세면 보고서가 거짓이 된다.
    /// </summary>
    [Fact]
    public void Report_SeparatesNotCheckedFromPass()
    {
        ImmutableArray<CheckResult> results = Report.Run(Clean());

        Assert.True(Report.Passed(results));
        Assert.Contains(results, r => r.Verdict == Verdict.NotChecked);

        string markdown = Report.Markdown(results, Clean(), "게임서버 대역", "2026-01-01 00:00");

        Assert.Contains("미판정 — **지우지 않는다**", markdown, StringComparison.Ordinal);
        Assert.Contains("판정: 합격", markdown, StringComparison.Ordinal);

        // 미판정 사유가 비어 있으면 통과로 읽힌다.
        foreach (CheckResult result in results)
        {
            if (result.Verdict == Verdict.NotChecked)
            {
                Assert.False(string.IsNullOrWhiteSpace(result.Detail), $"{result.Id}: 미판정 사유가 없다");
            }
        }
    }

    /// <summary>불합격이 하나라도 있으면 보고서가 불합격이다.</summary>
    [Fact]
    public void Report_FailsWhenAnyCheckFails()
    {
        Observation observation = Clean() with { CommandsDropped = 1 };

        ImmutableArray<CheckResult> results = Report.Run(observation);

        Assert.False(Report.Passed(results));
        Assert.Contains("불합격", Report.Markdown(results, observation, "x", "y"), StringComparison.Ordinal);
        Assert.Contains("\"passed\": false", Report.Json(results, "x", "y"), StringComparison.Ordinal);
    }

    /// <summary>JSON 이 따옴표·줄바꿈을 이스케이프한다. 안 하면 파서가 깨진다.</summary>
    [Fact]
    public void Report_EscapesJsonStrings()
    {
        ImmutableArray<CheckResult> results =
        [
            CheckResult.Fail("x", "따옴표 \" 와 줄바꿈", "줄1\n줄2", ["역슬래시 \\ 하나"]),
        ];

        string json = Report.Json(results, "t", "s");

        Assert.Contains("\\\"", json, StringComparison.Ordinal);
        Assert.Contains("\\n", json, StringComparison.Ordinal);
        Assert.Contains("\\\\", json, StringComparison.Ordinal);
        Assert.DoesNotContain("줄1\n줄2", json, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 표본

    /// <summary>규약을 지키는 최소 스트림. 스폰 → 존 상태 → 날씨.</summary>
    private static Observation Clean() => new()
    {
        Connected = true,
        ProtocolVersion = 2,
        ContractMinor = 2,
        NpcCount = 2,
        ZoneCount = 1,
        NegotiationDetail = "테스트",
        Events =
        [
            Event(GameEventKind.NpcSpawned, 1, tick: 100, npc: 0),
            Event(GameEventKind.NpcSpawned, 2, tick: 100, npc: 1),
            Event(GameEventKind.ZoneStateChanged, 3, tick: 100, zone: 1),
            Event(GameEventKind.WeatherChanged, 4, tick: 100, zone: 1),
        ],
    };

    /// <summary><paramref name="count"/> 틱 동안 TickSync 만 흐르는 스트림.</summary>
    private static Observation Ticking(int count)
    {
        var events = ImmutableArray.CreateBuilder<Observed>(count);

        for (int i = 0; i < count; i++)
        {
            events.Add(Event(GameEventKind.TickSync, i + 1, tick: 100 + i));
        }

        return Clean() with { Events = events.ToImmutable() };
    }

    private static Observed Event(
        GameEventKind kind, long sequence, long tick, int npc = 0, ushort zone = 0, int frame = 1) =>
        new(
            new GameEvent
            {
                Kind = kind,
                Sequence = sequence,
                OccurredAt = new Tick(tick),
                Npc = new NpcId(npc),
                Zone = new ZoneId(zone),
            },
            frame,
            0);

    private static Observed Proximity(
        long sequence, int npc, long tick, int distance, ProximityChange change) =>
        new(
            new GameEvent
            {
                Kind = GameEventKind.PlayerProximity,
                Sequence = sequence,
                OccurredAt = new Tick(tick),
                Npc = new NpcId(npc),
                Player = new PlayerId(1),
                Amount = distance,
                Code = (byte)change,
            },
            1,
            0);

    private static Observed Damage(long sequence, int npc, long tick, int amount) =>
        new(
            new GameEvent
            {
                Kind = GameEventKind.DamageTaken,
                Sequence = sequence,
                OccurredAt = new Tick(tick),
                Npc = new NpcId(npc),
                Amount = amount,
            },
            1,
            0);

    private static Observed Arrived(long sequence, int npc, long tick, uint correlation) =>
        new(
            new GameEvent
            {
                Kind = GameEventKind.NpcArrived,
                Sequence = sequence,
                OccurredAt = new Tick(tick),
                Npc = new NpcId(npc),
                Correlation = new CorrelationId(correlation),
            },
            1,
            0);
}
