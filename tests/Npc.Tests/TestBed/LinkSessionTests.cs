using System.Net.Sockets;
using Npc.Contracts;
using Npc.Gateway;
using Npc.MasterData;
using Npc.TestGameServer;
using Npc.TestGameServer.Link;
using Npc.Wire;

namespace Npc.Tests.TestBed;

/// <summary>
/// T6-16 — 링크 리스너와 핸드셰이크. docs/20 §5.5 · §7.1.
///
/// <para>
/// <b>상대역을 흉내내지 않는다.</b> NPC 서버 쪽은 진짜 <see cref="TcpGameServerLink"/> 를 쓰고
/// 진짜 소켓으로 붙인다 — 가짜 상대를 만들면 "두 구현이 서로 맞는가" 라는 정작 볼 것을 못 본다.
/// </para>
///
/// <para><b>포트는 0(자동 할당)이다</b> (docs/20 §13). 데모를 띄워 둔 채 돌려도 깨지지 않는다.</para>
/// </summary>
[Trait("Category", "TestBed")]
public sealed class LinkSessionTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static readonly NpcInstanceTable s_instances = NpcInstanceTable.Load(
        Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

    /// <summary>완료 조건 — 핸드셰이크가 끝난다. 포트 0.</summary>
    [Fact]
    public async Task LinkSession_HandshakeCompletes()
    {
        await using var bed = await Bed.StartAsync(npcs: 8);

        Assert.True(bed.Connected);
        Assert.Equal(LinkState.Connected, bed.Link.State);
        Assert.Equal(LinkRejectCode.None, bed.Link.RejectCode);

        LinkSession session = await bed.WaitForSessionAsync();

        Assert.True(session.IsAccepted);
        Assert.True(session.NeedsResync);
        Assert.Equal(1, bed.Listener.SessionsAccepted);
    }

    /// <summary>완료 조건 — 두 번째 접속은 <c>Bye</c> 를 받고 끊긴다.</summary>
    [Fact]
    public async Task LinkSession_RejectsSecondSession()
    {
        await using var bed = await Bed.StartAsync(npcs: 8);

        _ = await bed.WaitForSessionAsync();

        using var second = new TcpClient();

        await second.ConnectAsync("127.0.0.1", bed.Listener.Port, bed.Token);

        NetworkStream stream = second.GetStream();

        (LinkMessageKind kind, byte[] payload) = await LinkSession.ReadFrameAsync(stream, bed.Token);

        Assert.Equal(LinkMessageKind.Bye, kind);
        Assert.Equal(
            (byte)LinkByeCode.HandshakeRejected,
            MemoryPack.MemoryPackSerializer.Deserialize<WireBye>(payload).Code);

        // 끊는다. 두 번째 접속에는 Hello 가 오지 않는다.
        Assert.Equal(0, await stream.ReadAsync(new byte[1], bed.Token));

        await bed.WaitUntilAsync(() => bed.Listener.SessionsRejected == 1);

        // 첫 세션은 그대로 산다.
        Assert.True(bed.Listener.Session?.IsActive);
        Assert.Equal(1, bed.Listener.SessionsAccepted);
    }

    /// <summary>완료 조건 — 재동기화가 로스터 수만큼 <c>NpcSpawned</c> 를 보낸다.</summary>
    [Fact]
    public async Task LinkSession_SendsSpawnForEveryNpc()
    {
        const int Npcs = 8;

        await using var bed = await Bed.StartAsync(Npcs);

        LinkSession session = await bed.WaitForSessionAsync();

        // 재동기화는 틱 스레드 계약이다. 테스트는 단일 스레드라 그대로 부른다.
        await session.ResyncAsync(bed.Token);

        int zones = s_data.Zones.Zones.Length;
        int expected = Npcs + (zones * 2);

        Dictionary<GameEventKind, int> got = await bed.ReceiveAsync(expected);

        Assert.Equal(Npcs, got.GetValueOrDefault(GameEventKind.NpcSpawned));
        Assert.Equal(zones, got.GetValueOrDefault(GameEventKind.ZoneStateChanged));
        Assert.Equal(zones, got.GetValueOrDefault(GameEventKind.WeatherChanged));

        Assert.Equal(Npcs, session.SpawnsSent);
        Assert.Equal(expected, session.EventsSent);
        Assert.False(session.NeedsResync);

        // N6 — 시퀀스에 구멍이 없다.
        Assert.Equal(0, bed.Link.Stats.EventGapsDetected);
    }

    /// <summary>
    /// 재동기화 프레임은 <see cref="LinkSession.MaxEventsPerFrame"/> 건을 넘지 않는다 (docs/20 §5.5).
    /// 한 프레임에 몰아 보내면 1MiB 상한에 걸리고, 그 순간 프레임 하나가 아니라 스트림이 죽는다.
    /// </summary>
    [Fact]
    public async Task LinkSession_SplitsSpawnBatchesAt256()
    {
        const int Npcs = 300;

        await using var bed = await Bed.StartAsync(Npcs);

        LinkSession session = await bed.WaitForSessionAsync();

        await session.ResyncAsync(bed.Token);

        int expected = Npcs + (s_data.Zones.Zones.Length * 2);
        long frames = (expected + LinkSession.MaxEventsPerFrame - 1) / LinkSession.MaxEventsPerFrame;

        Assert.True(expected > LinkSession.MaxEventsPerFrame, "표본이 한 프레임에 다 들어가면 이 테스트는 공허하다.");
        Assert.Equal(frames, session.FramesSent);

        Dictionary<GameEventKind, int> got = await bed.ReceiveAsync(expected);

        Assert.Equal(Npcs, got.GetValueOrDefault(GameEventKind.NpcSpawned));
        Assert.Equal(0, bed.Link.Stats.EventGapsDetected);
    }

    /// <summary>
    /// 로스터가 다르면 NPC 서버가 거절하고, 게임서버는 그 판정을 받아들인다.
    /// <b>우회 옵션이 없다</b> (docs/20 §5.5) — 첨자가 어긋난 채로 도는 것보다 낫다.
    /// </summary>
    [Fact]
    public async Task LinkSession_AcceptsRejectionFromNpcServer()
    {
        await using var bed = await Bed.StartAsync(npcs: 8, corruptRoster: true);

        Assert.False(bed.Connected);
        Assert.Equal(LinkRejectCode.RosterMismatch, bed.Link.RejectCode);

        await bed.WaitUntilAsync(() => bed.Listener.HandshakesFailed == 1);

        Assert.Null(bed.Listener.Session);
        Assert.Equal(0, bed.Listener.SessionsAccepted);
    }

    /// <summary>
    /// T6-17 완료 조건 — 명령은 <b>다음 틱에</b> 적용된다.
    ///
    /// 도착 즉시가 아니라 링에 담겼다가 틱 1단계에서 나가는 것이 계약이다 (docs/20 §7.2).
    /// 소켓 태스크가 월드를 직접 고치면 틱 중간에 상태가 바뀌어 시나리오가 매번 다르게 흐른다.
    /// </summary>
    [Fact]
    public async Task LinkSession_AppliesCommandsOnNextTick()
    {
        const int Count = 3;

        await using var bed = await Bed.StartAsync(npcs: 8);

        LinkSession session = await bed.WaitForSessionAsync();

        bed.Send(Count);

        await bed.WaitUntilAsync(() => session.CommandsReceived == Count);

        // 아직 틱이 안 돌았다. 링에 담겨 있을 뿐이다.
        Assert.Equal(Count, bed.World.Inbox.Pending);
        Assert.Equal(0, bed.World.CommandsApplied);

        bed.World.Tick(new Tick(1));

        Assert.Equal(Count, bed.World.CommandsApplied);
        Assert.Equal(0, bed.World.Inbox.Pending);
        Assert.Equal(0, bed.World.Inbox.Dropped);
    }

    /// <summary>
    /// T6-17 완료 조건 — 명령 순서가 프레임 안 순서와 같다.
    ///
    /// 뒤집히면 <c>Stop</c> 뒤에 <c>MoveTo</c> 가 적용되는 식이 되고, 증상은
    /// "가끔 이상하게 행동한다" 로만 나타난다. 상관 ID 로 순서를 되짚는다.
    /// </summary>
    [Fact]
    public async Task LinkSession_KeepsCommandOrderWithinFrame()
    {
        const int Count = 32;

        await using var bed = await Bed.StartAsync(npcs: 8);

        LinkSession session = await bed.WaitForSessionAsync();

        bed.Send(Count);

        await bed.WaitUntilAsync(() => session.CommandsReceived == Count);

        bed.World.Tick(new Tick(1));

        // Stop 은 SimWorld 가 즉시 Complete 로 답한다. 그 상관 ID 열이 곧 적용 순서다.
        var applied = new List<uint>();

        while (bed.World.World.Events.TryRead(out GameEvent ev))
        {
            if (ev.Kind == GameEventKind.NpcActionCompleted)
            {
                applied.Add(ev.Correlation.Value);
            }
        }

        Assert.Equal(Enumerable.Range(1, Count).Select(i => (uint)i), applied);
    }

    /// <summary>
    /// T6-17 완료 조건 — 링이 차면 버리고 <b>센다.</b>
    ///
    /// <b>예외를 던지지 않는다.</b> 명령이 유실된다고 가정하는 것이 이 링크의 계약이고
    /// (docs/02 §1), NPC 서버는 <c>timeout_s</c> 만료로 스스로 재개한다.
    /// </summary>
    [Fact]
    public async Task LinkSession_CountsInboxDrops()
    {
        const int Count = 4;

        await using var bed = await Bed.StartAsync(npcs: 8);

        LinkSession session = await bed.WaitForSessionAsync();

        // 링을 먼저 가득 채운다. 이 회차는 틱을 돌리지 않으므로 아무도 빼 가지 않고,
        // 아직 보낸 명령이 없어서 리시버 태스크와 겹치지도 않는다 (SPSC 가 지켜진다).
        var filler = new NpcCommand
        {
            Kind = NpcCommandKind.Stop,
            Npc = new NpcId(0),
            IssuedAt = default,
            Correlation = default,
            Priority = CommandPriority.Normal,
        };

        for (int i = 0; i < CommandInbox.Capacity; i++)
        {
            Assert.True(bed.World.Inbox.TryEnqueue(in filler));
        }

        Assert.Equal(0, bed.World.Inbox.Dropped);

        bed.Send(Count);

        await bed.WaitUntilAsync(() => session.CommandsReceived == Count);
        await bed.WaitUntilAsync(() => bed.World.Inbox.Dropped == Count);

        // 받은 것은 다 셌고, 링은 넘치지 않았다.
        Assert.Equal(Count, session.CommandsReceived);
        Assert.Equal(CommandInbox.Capacity, bed.World.Inbox.Pending);
    }

    /// <summary>
    /// 게임서버 대역 + 진짜 TCP 링크 한 벌. 포트 0 으로 띄우고 끝나면 같이 내린다.
    /// </summary>
    private sealed class Bed : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly List<TcpClient> _clients;
        private Task? _accept;

        private Bed(GameWorld world, LinkListener listener, TcpGameServerLink link, List<TcpClient> clients)
        {
            World = world;
            Listener = listener;
            Link = link;
            _clients = clients;
        }

        public GameWorld World { get; }

        public LinkListener Listener { get; }

        public TcpGameServerLink Link { get; }

        public bool Connected { get; private set; }

        public CancellationToken Token => _cts.Token;

        /// <summary>대역과 링크를 띄우고 핸드셰이크까지 간다.</summary>
        /// <param name="npcs">로스터 크기.</param>
        /// <param name="corruptRoster">
        /// NPC 서버 쪽 로스터 해시를 일부러 어긋나게 한다. 거절 경로를 보는 회차다.
        /// </param>
        public static async Task<Bed> StartAsync(int npcs, bool corruptRoster = false)
        {
            NpcRoster roster = NpcRoster.Select(s_instances, npcs);
            var options = new GameServerOptions { Npcs = npcs, LinkPort = 0 };
            GameWorld world = GameWorld.Create(options, s_data, roster);

            var listener = new LinkListener(world, s_data, options);

            listener.Start();

            var linkOptions = new TcpLinkOptions
            {
                Host = "127.0.0.1",
                Port = listener.Port,
                TimeScale = options.TimeScale,
                NpcCount = roster.Count,
                MasterData = WireHash.FromHex(s_data.ContentHash),
                Roster = corruptRoster
                    ? WireHash.FromHex(NpcRoster.Select(s_instances, npcs + 1).Hash)
                    : WireHash.FromHex(roster.Hash),
            };

            var clients = new List<TcpClient>();
            Stream? opened = null;

            var link = new TcpGameServerLink(linkOptions, ConnectAsync);
            var bed = new Bed(world, listener, link, clients);

            bed._accept = listener.RunAsync(bed.Token);
            bed.Connected = await link.ConnectAsync(bed.Token);

            if (bed.Connected)
            {
                // <b>핸드셰이크가 끝난 뒤에 띄운다.</b> 먼저 띄우면 수신 루프가 Hello 프레임을
                // 먼저 집어삼켜(Deliver 는 EventBatch 만 본다) 핸드셰이크가 영원히 기다린다.
                await link.StartReceiverAsync(opened!, bed.Token);
                await link.StartSenderAsync(opened!, bed.Token);
            }

            return bed;

            async Task<Stream> ConnectAsync(CancellationToken ct)
            {
                var client = new TcpClient { NoDelay = true };

                await client.ConnectAsync("127.0.0.1", listener.Port, ct);

                NetworkStream stream = client.GetStream();

                // 스트림을 우리가 들고 있어야 수신 태스크를 직접 띄울 수 있다.
                // (TcpGameServerLink.RunAsync 는 재접속까지 도는 긴 루프라 여기서는 과하다.)
                clients.Add(client);
                opened = stream;

                return stream;
            }
        }

        /// <summary>
        /// 명령 <paramref name="count"/> 개를 <b>한 배치로</b> 보낸다. 상관 ID 는 1부터 순증한다 —
        /// 수신 순서를 되짚는 근거다.
        ///
        /// <c>Stop</c> 인 이유는 <c>SimWorld</c> 가 하위 시뮬 없이 즉시 <c>Complete</c> 로 답하기 때문이다.
        /// 전부 같은 우선순위라 링이 순서를 바꾸지 않는다.
        /// </summary>
        public void Send(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var command = new NpcCommand
                {
                    Kind = NpcCommandKind.Stop,
                    Npc = new NpcId(i % Math.Max(1, World.Roster.Count)),
                    IssuedAt = new Tick(1),
                    Correlation = new CorrelationId((uint)(i + 1)),
                    Priority = CommandPriority.Normal,
                };

                Link.Enqueue(in command);
            }

            // 한 번의 Flush = 한 개의 CommandBatch 프레임이다 (N8).
            Link.FlushAsync(Token).AsTask().GetAwaiter().GetResult();
        }

        /// <summary>세션이 붙을 때까지 기다린다.</summary>
        public async Task<LinkSession> WaitForSessionAsync()
        {
            await WaitUntilAsync(() => Listener.Session is { IsAccepted: true });

            return Listener.Session!;
        }

        /// <summary>조건이 참이 될 때까지 폴링한다. 5초 안에 안 되면 실패다.</summary>
        public async Task WaitUntilAsync(Func<bool> condition)
        {
            ArgumentNullException.ThrowIfNull(condition);

            for (int i = 0; i < 500; i++)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(10, Token);
            }

            Assert.Fail("5초 안에 조건이 만족되지 않았다.");
        }

        /// <summary>링크가 받은 이벤트를 <paramref name="count"/> 건 모아 종류별로 센다.</summary>
        public async Task<Dictionary<GameEventKind, int>> ReceiveAsync(int count)
        {
            var got = new Dictionary<GameEventKind, int>();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);

            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            int seen = 0;

            while (seen < count)
            {
                if (!await Link.Events.WaitToReadAsync(timeout.Token))
                {
                    break;
                }

                while (seen < count && Link.Events.TryRead(out GameEvent ev))
                {
                    got[ev.Kind] = got.GetValueOrDefault(ev.Kind) + 1;
                    seen++;
                }
            }

            return got;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();

            if (_accept is { } accept)
            {
                try
                {
                    await accept;
                }
                catch (OperationCanceledException)
                {
                    // 종료 지시다.
                }
            }

            await Link.DisposeAsync();
            await Listener.DisposeAsync();
            await World.DisposeAsync();

            foreach (TcpClient client in _clients)
            {
                client.Dispose();
            }

            _cts.Dispose();
        }
    }
}
