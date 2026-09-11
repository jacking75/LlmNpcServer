using System.Net.Sockets;
using Npc.Contracts;
using Npc.Gateway;
using Npc.TestBed.Protocol;
using Npc.TestGameServer;
using Npc.TestGameServer.Link;
using Npc.TestGameServer.World;
using Npc.Wire;

namespace Npc.Tests.TestBed;

/// <summary>
/// T6-37 — <see cref="GameServer"/> 조립과 10Hz 루프(1~9단계). docs/20 §7.2 · §7.5.
///
/// <para>
/// <b>여기서 처음으로 부품들이 실제로 불린다.</b> T6-14~T6-25 는 부품만 만들었고
/// <c>Program.cs</c> 는 T6-14 의 골격(파싱 → 출력)이라, <see cref="MirrorLog"/>·
/// <c>ControlHandler</c>·<c>LinkSession.FlushEventsAsync</c> 를 부르는 것은 테스트뿐이었다.
/// </para>
///
/// <para><b>포트는 0(자동 할당)이다</b> (docs/20 §13).</para>
/// </summary>
[Trait("Category", "TestBed")]
public sealed class GameServerTests
{
    /// <summary>완료 조건 — 한 틱이 §7.2 의 단계 순서를 지킨다.</summary>
    [Fact]
    public async Task GameServer_TickOrderMatchesSpec()
    {
        await using GameServer server = Create(npcs: 16);

        var stages = new List<TickStage>();

        server.World.StageObserver = stages.Add;

        await server.TickAsync(new Tick(1), CancellationToken.None);

        Assert.Equal(
            [
                TickStage.Commands,
                TickStage.Scenario,
                TickStage.Players,
                TickStage.Movement,
                TickStage.Interaction,
                TickStage.Transforms,
                TickStage.Needs,
                TickStage.World,
            ],
            stages);

        Assert.Equal(1, server.Tick);
        Assert.Equal(1, server.World.TicksProcessed);
    }

    /// <summary>
    /// 완료 조건 — 명령과 이벤트가 둘 다 미러에 남는다 (docs/20 §7.2 1단계 · §7.4).
    ///
    /// 링크가 없는 회차라 이벤트는 <see cref="GameServer.OrphanEvents"/> 경로로 지나간다 —
    /// 그래도 미러에는 남아야 한다. 안 그러면 NPC 서버 없이 클라이언트만 띄운 데모에서
    /// 로그 패널이 통째로 비어 사람이 "게임서버가 죽었다" 로 읽는다.
    /// </summary>
    [Fact]
    public async Task GameServer_MirrorsCommandsAndEvents()
    {
        await using GameServer server = Create(npcs: 16);

        // 커서는 "지금 꼬리" 에서 시작한다 (새 클라이언트가 과거를 쏟아받지 않게 하는 규약).
        // 아직 아무것도 안 썼을 때 만들어야 처음부터 읽힌다.
        MirrorLog.Cursor cursor = server.Mirror.NewCursor();

        var command = new NpcCommand
        {
            Kind = NpcCommandKind.Stop,

            // A-08 — 와이어의 NpcId 는 전역 인스턴스 id 다. 슬롯 3 의 거주자를 쓴다.
            Npc = server.World.World.NpcIdOf(3),
            IssuedAt = new Tick(1),
            Correlation = new CorrelationId(77),
            Priority = CommandPriority.Normal,
        };

        Assert.True(server.World.Inbox.TryEnqueue(in command));

        await server.TickAsync(new Tick(1), CancellationToken.None);

        Assert.Equal(1, server.Mirror.CommandsWritten);

        var commands = new LoggedCommand[8];
        int got = server.Mirror.ReadCommands(cursor, commands);

        Assert.Equal(1, got);
        // 미러는 뷰어가 쓰는 슬롯으로 남긴다 (A-08).
        Assert.Equal(3, commands[0].Npc);
        Assert.Equal((byte)NpcCommandKind.Stop, commands[0].Kind);
        Assert.Equal(77u, commands[0].Correlation);

        // 이벤트: 스폰 16 + Stop 완료 1 + TickSync 1 이 최소치다.
        Assert.True(server.Mirror.EventsWritten >= 18, $"events={server.Mirror.EventsWritten}");
        Assert.Equal(server.World.World.EventsEmitted, server.OrphanEvents);
    }

    /// <summary>
    /// 완료 조건 — NPC 서버 없이 100틱을 돌아도 이벤트 채널이 자라지 않는다.
    ///
    /// <b>이것이 이 태스크의 요점 하나다.</b> 안 비우면 무제한 채널이 프로세스 수명 내내 자라고,
    /// NPC 서버가 붙는 순간 수십만 건이 한꺼번에 나간다.
    /// </summary>
    [Fact]
    public async Task GameServer_RunsWithoutNpcServer()
    {
        await using GameServer server = Create(npcs: 32);

        for (int tick = 1; tick <= 100; tick++)
        {
            await server.TickAsync(new Tick(tick), CancellationToken.None);
        }

        Assert.Equal(100, server.World.TicksProcessed);
        Assert.True(server.OrphanEvents > 100, $"orphan={server.OrphanEvents}");
        Assert.Equal(server.World.World.EventsEmitted, server.OrphanEvents);

        // 채널에 남은 것이 없다.
        Assert.False(server.World.World.Events.TryRead(out _));
    }

    /// <summary>
    /// 완료 조건 — NPC 서버가 붙으면 틱 루프가 재동기화를 보내고 그 뒤 이벤트가 흐른다.
    ///
    /// <b>재동기화는 틱 스레드의 일이다</b> (docs/20 §7.1) — accept 태스크가 부르면
    /// <c>SimWorld</c> 의 단일 기록자 계약이 깨진다. 그래서 <see cref="LinkSession.NeedsResync"/>
    /// 를 루프가 본다.
    /// </summary>
    [Fact]
    public async Task GameServer_ResyncsWhenLinkAttaches()
    {
        const int Npcs = 24;

        await using GameServer server = Create(Npcs);

        server.Start();

        using var cts = new CancellationTokenSource();

        Task accepts = server.AcceptAsync(cts.Token);

        await using var npcServer = await FakeNpcServer.ConnectAsync(server, cts.Token);

        Assert.True(npcServer.Connected);

        long tick = 0;

        // 세션이 자리를 잡을 때까지 틱을 민다. accept 는 다른 태스크라 몇 틱 걸릴 수 있다.
        for (int i = 0; i < 200 && server.Link.Session is not { IsAccepted: true }; i++)
        {
            await server.TickAsync(new Tick(++tick), cts.Token);
            await Task.Delay(5, cts.Token);
        }

        LinkSession session = Assert.IsType<LinkSession>(server.Link.Session);

        Assert.True(session.IsAccepted);

        // 재동기화 + 몇 틱.
        for (int i = 0; i < 10; i++)
        {
            await server.TickAsync(new Tick(++tick), cts.Token);
        }

        Assert.False(session.NeedsResync);
        Assert.Equal(Npcs, session.SpawnsSent);

        // 링크가 붙은 뒤로는 고아 이벤트가 늘지 않는다.
        long orphans = server.OrphanEvents;

        await server.TickAsync(new Tick(++tick), cts.Token);

        Assert.Equal(orphans, server.OrphanEvents);

        // 미러는 링크로 나간 이벤트도 본다.
        Assert.True(server.Mirror.EventsWritten >= Npcs, $"events={server.Mirror.EventsWritten}");

        await cts.CancelAsync();

        try
        {
            await accepts;
        }
        catch (Exception)
        {
            // 취소로 끝난다.
        }
    }

    /// <summary>
    /// 완료 조건 — <c>SkipTime</c> 이 루프에서 소비되어 틱 번호가 점프한다 (docs/20 §8.3).
    ///
    /// <c>ControlHandler</c> 는 건너뛸 틱 수를 쌓아 둘 뿐이고, 실제로 미는 것은 루프다 —
    /// 제어 처리기가 그 권한을 가지면 한 틱이 두 번 도는 회차가 생긴다.
    /// </summary>
    [Fact]
    public async Task GameServer_LoopConsumesTickSkip()
    {
        await using GameServer server = Create(npcs: 8);

        using var cts = new CancellationTokenSource();

        Task run = server.RunAsync(cts.Token);

        // 게임 10분 = 600게임초. time-scale 60 이면 실시간 10초 = 100틱이다
        // (docs/20 §8.3 의 Amount × 60 × 10 / TimeScale).
        Assert.True(server.Controls.Apply(
            new Control { Kind = (byte)ControlKind.SkipTime, Amount = 10 },
            new Tick(1)));

        // 건너뛴 값은 다음 회차의 틱 번호에 실린다. 그 틱이 실제로 돌 때까지 기다린다.
        for (int i = 0; i < 300 && server.Tick <= 100; i++)
        {
            await Task.Delay(10, cts.Token);
        }

        await cts.CancelAsync();

        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
            // 정상 종료다.
        }

        Assert.Equal(100, server.TicksSkipped);

        // 민 횟수보다 틱 번호가 앞선다 — 그것이 "건너뛴다" 의 뜻이다.
        Assert.True(
            server.Tick > server.World.TicksProcessed,
            $"tick={server.Tick} processed={server.World.TicksProcessed}");
    }

    /// <summary>
    /// 완료 조건 — 모르는 존 id 는 <b>기동 실패</b>다 (docs/20 §10.3).
    ///
    /// 조용히 넘기면 로스터가 어긋난 채 뜨고, 사고는 핸드셰이크 거절로만 나타나
    /// "새로 만든 게임서버가 이상하다" 로 오해하기 쉽다.
    /// </summary>
    [Fact]
    public void GameServer_UnknownZoneFailsStartup()
    {
        var options = new GameServerOptions
        {
            Npcs = 8,
            LinkPort = 0,
            ClientPort = 0,
            MasterData = TestPaths.MasterData,
            Zones = ["no_such_zone"],
        };

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => GameServer.Create(options, TextWriter.Null));

        Assert.Contains("no_such_zone", error.Message, StringComparison.Ordinal);
    }

    /// <summary>완료 조건 — 종료 요약이 §7.5 의 값을 담는다.</summary>
    [Fact]
    public async Task GameServer_SummaryReportsCounters()
    {
        await using GameServer server = Create(npcs: 8);

        for (int tick = 1; tick <= 5; tick++)
        {
            await server.TickAsync(new Tick(tick), CancellationToken.None);
        }

        string summary = server.Summarize();

        Assert.Contains("ticks 5", summary, StringComparison.Ordinal);
        Assert.Contains("commands in", summary, StringComparison.Ordinal);
        Assert.Contains("events out", summary, StringComparison.Ordinal);
        Assert.Contains("p99 tick", summary, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- 보조

    private static GameServer Create(int npcs) => GameServer.Create(
        new GameServerOptions
        {
            Npcs = npcs,
            LinkPort = 0,
            ClientPort = 0,
            MasterData = TestPaths.MasterData,
        },
        TextWriter.Null);

    /// <summary>
    /// NPC 서버 역할. <b>진짜 <see cref="TcpGameServerLink"/> 를 쓴다</b> —
    /// 가짜 상대를 만들면 "두 구현이 서로 맞는가" 라는 정작 볼 것을 못 본다.
    /// </summary>
    private sealed class FakeNpcServer : IAsyncDisposable
    {
        private readonly TcpGameServerLink _link;
        private readonly TcpClient _client;

        private FakeNpcServer(TcpGameServerLink link, TcpClient client)
        {
            _link = link;
            _client = client;
        }

        public bool Connected { get; private set; }

        public TcpGameServerLink Link => _link;

        public static async Task<FakeNpcServer> ConnectAsync(GameServer server, CancellationToken ct)
        {
            var options = new TcpLinkOptions
            {
                Host = "127.0.0.1",
                Port = server.LinkPort,
                TimeScale = 60,
                NpcCount = server.World.Roster.Count,
                MasterData = WireHash.FromHex(server.Data.ContentHash),

                // B-04 — v2 핸드셰이크는 두 해시를 따로 본다. 하나만 채우면
                // 구조 해시가 내용 해시와 비교되어 언제나 어긋난다.
                MasterDataStructural = WireHash.FromHex(server.Data.StructuralHash),
                MasterDataContent = WireHash.FromHex(server.Data.ContentHash),
                Roster = WireHash.FromHex(server.World.Roster.Hash),
            };

            TcpClient? client = null;
            Stream? opened = null;

            var link = new TcpGameServerLink(options, ConnectStreamAsync);
            bool connected = await link.ConnectAsync(ct);

            if (connected)
            {
                await link.StartReceiverAsync(opened!, ct);
                await link.StartSenderAsync(opened!, ct);
            }

            return new FakeNpcServer(link, client!) { Connected = connected };

            async Task<Stream> ConnectStreamAsync(CancellationToken token)
            {
                client = new TcpClient { NoDelay = true };

                await client.ConnectAsync("127.0.0.1", server.LinkPort, token);

                opened = client.GetStream();

                return opened;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _link.DisposeAsync();

            _client?.Dispose();
        }
    }
}
