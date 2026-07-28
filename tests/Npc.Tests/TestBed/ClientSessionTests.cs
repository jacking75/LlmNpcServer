using System.Net.Sockets;
using MemoryPack;
using Npc.Contracts;
using Npc.MasterData;
using Npc.TestBed.Protocol;
using Npc.TestGameServer;
using Npc.TestGameServer.Client;
using Npc.TestGameServer.World;

namespace Npc.Tests.TestBed;

/// <summary>
/// T6-23 — 클라이언트 리스너와 세션. docs/20 §8.1 · §8.2.
///
/// <para>
/// <b>진짜 소켓으로 붙인다.</b> 가짜 스트림을 물리면 "두 스레드가 같은 소켓에 쓰지 않는가"
/// 라는 정작 볼 것을 못 본다 — 그것이 이 세션 설계의 유일한 위험이다.
/// </para>
///
/// <para><b>포트는 0(자동 할당)이다</b> (docs/20 §13). 데모를 띄워 둔 채 돌려도 깨지지 않는다.</para>
/// </summary>
[Trait("Category", "TestBed")]
public sealed class ClientSessionTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static readonly NpcInstanceTable s_instances = NpcInstanceTable.Load(
        Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

    /// <summary>완료 조건 — <c>CliHello</c> 에 <c>SrvHello</c> 로 답하고 <c>PlayerId</c> 를 배정한다.</summary>
    [Fact]
    public async Task ClientSession_HelloAssignsPlayerId()
    {
        await using var bed = await Bed.StartAsync();
        await using Peer peer = await bed.ConnectAsync();

        SrvHello hello = await peer.HelloAsync(bed);

        Assert.Equal(ClientProtocol.Version, hello.ProtocolVersion);
        Assert.Equal(GameWorld.TickRate, hello.TickRate);
        Assert.Equal(bed.Options.TimeScale, hello.TimeScale);
        Assert.Equal(bed.World.Roster.Count, hello.NpcCount);

        // 1부터 배정된다. 0 은 "자리가 없다" 는 뜻이다.
        Assert.Equal(1, hello.PlayerId);
        Assert.True(bed.Players.IsActive(new PlayerId(hello.PlayerId)));
        Assert.Equal(1, bed.Players.Count);
        Assert.Equal(1, bed.Listener.Count);

        // 월드 경계는 pois.json 그대로다 — 클라이언트가 두 번 계산하지 않는다 (docs/20 §9.3).
        WorldBounds bounds = ClientListener.BoundsOf(s_data);

        Assert.Equal(bounds.MinX, hello.MinX);
        Assert.Equal(bounds.MinZ, hello.MinZ);
        Assert.Equal(bounds.MaxX, hello.MaxX);
        Assert.Equal(bounds.MaxZ, hello.MaxZ);
        Assert.True(bounds.MaxX > bounds.MinX, "경계가 비었다. 좌표를 못 읽은 것이다.");

        // 마스터데이터 해시가 실린다. 다른 데이터로 그리면 그럴듯하게 어긋난 화면이 나온다.
        Assert.Equal(s_data.ContentHash, hello.MasterData.ToHex());
    }

    /// <summary>
    /// 완료 조건 — 세션이 끊기면 플레이어가 사라진다.
    ///
    /// 안 지우면 유령 플레이어가 근접 판정에 남아 NPC 를 영원히 LOD 1 에 붙잡아 둔다.
    /// </summary>
    [Fact]
    public async Task ClientSession_DisconnectRemovesPlayer()
    {
        await using var bed = await Bed.StartAsync();

        Peer peer = await bed.ConnectAsync();
        SrvHello hello = await peer.HelloAsync(bed);
        var player = new PlayerId(hello.PlayerId);

        Assert.True(bed.Players.IsActive(player));

        await peer.DisposeAsync();

        // 회수는 틱 스레드가 한다 — PlayerRegistry 는 소켓 태스크가 만지지 않는다.
        await bed.WaitUntilAsync(async () =>
        {
            await bed.TickAsync();
            return bed.Listener.SessionsReaped == 1;
        });

        Assert.False(bed.Players.IsActive(player));
        Assert.Equal(0, bed.Players.Count);
        Assert.Equal(0, bed.Listener.Count);

        // 자리가 비었으니 다음 접속이 그 자리를 받는다.
        await using Peer next = await bed.ConnectAsync();

        Assert.Equal(1, (await next.HelloAsync(bed)).PlayerId);
    }

    /// <summary>완료 조건 — <c>--max-clients</c> 를 넘으면 <c>PlayerId = 0</c> 을 받고 끊긴다.</summary>
    [Fact]
    public async Task ClientSession_RejectsOverMaxClients()
    {
        await using var bed = await Bed.StartAsync(maxClients: 2);
        await using Peer first = await bed.ConnectAsync();
        await using Peer second = await bed.ConnectAsync();

        Assert.Equal(1, (await first.HelloAsync(bed)).PlayerId);
        Assert.Equal(2, (await second.HelloAsync(bed)).PlayerId);

        await using Peer third = await bed.ConnectAsync();

        // 거절도 SrvHello 로 온다. §8 에 거절 메시지가 따로 없고, 조용히 닫으면
        // 클라이언트가 "서버가 죽었나" 와 구별하지 못한다.
        SrvHello rejected = await third.ReadHelloAsync(bed);

        Assert.Equal(0, rejected.PlayerId);
        await bed.WaitUntilAsync(() => bed.Listener.SessionsRejected == 1);

        // 앞의 둘은 그대로 산다.
        Assert.Equal(2, bed.Listener.Count);
        Assert.Equal(2, bed.Players.Count);
    }

    /// <summary>
    /// 완료 조건 — 한 세션이 죽어도 다른 세션과 세계는 산다.
    ///
    /// <b>여기서 세션을 곱게 닫지 않는다.</b> <c>Bye</c> 없이 소켓을 끊는 것이
    /// 실제로 클라이언트가 죽는 방식이고, 그때 게임서버가 같이 죽으면 데모가 못 돈다.
    /// </summary>
    [Fact]
    public async Task ClientSession_OneDeadSessionDoesNotKillOthers()
    {
        await using var bed = await Bed.StartAsync(maxClients: 4);

        Peer dying = await bed.ConnectAsync();
        await using Peer survivor = await bed.ConnectAsync();

        _ = await dying.HelloAsync(bed);

        SrvHello alive = await survivor.HelloAsync(bed);

        Assert.Equal(2, bed.Players.Count);

        long ticksBefore = bed.World.TicksProcessed;

        dying.Kill();

        await bed.WaitUntilAsync(async () =>
        {
            await bed.TickAsync();
            return bed.Listener.SessionsReaped == 1;
        });

        // 살아남은 세션은 여전히 응답한다.
        await survivor.SendAsync(ClientMessageKind.Ping, new Ping { ClientStamp = 777 }, bed.Token);

        Pong pong = await survivor.ReceiveAsync<Pong>(ClientMessageKind.Pong, bed);

        Assert.Equal(777, pong.ClientStamp);

        // 세계도 계속 돌았다.
        Assert.True(bed.World.TicksProcessed > ticksBefore);
        Assert.Equal(1, bed.Listener.Count);
        Assert.True(bed.Players.IsActive(new PlayerId(alive.PlayerId)));

        // 리스너도 계속 받는다.
        await using Peer late = await bed.ConnectAsync();

        Assert.NotEqual(0, (await late.HelloAsync(bed)).PlayerId);
    }

    /// <summary>
    /// 수신한 입력은 <b>다음 틱에</b> 적용된다 (docs/20 §7.2 3단계).
    ///
    /// 리시버 태스크가 <see cref="PlayerRegistry"/> 를 직접 만지면 틱 중간에 플레이어가
    /// 움직이고, 그 위치로 근접 판정이 돌면 같은 시나리오가 매번 다르게 흐른다.
    /// </summary>
    [Fact]
    public async Task ClientSession_AppliesInputAndSelectOnTick()
    {
        await using var bed = await Bed.StartAsync();
        await using Peer peer = await bed.ConnectAsync();

        SrvHello hello = await peer.HelloAsync(bed);
        var player = new PlayerId(hello.PlayerId);

        WorldPos before = bed.Players.PositionOf(player);

        await peer.SendAsync(
            ClientMessageKind.Input, new Input { Seq = 1, DirX = 1, DirZ = 0, Run = 0 }, bed.Token);
        await peer.SendAsync(ClientMessageKind.Select, new Select { NpcId = 3 }, bed.Token);

        ClientSession session = Assert.Single(bed.Listener.Live);

        await bed.WaitUntilAsync(() => session.InputsReceived == 1);

        // 아직 적용 전이다 — 링에 담겨 있을 뿐이다.
        Assert.Equal(before, bed.Players.PositionOf(player));
        Assert.Equal(-1, session.SelectedNpc);

        await bed.TickAsync();

        Assert.NotEqual(before, bed.Players.PositionOf(player));
        Assert.Equal(3, session.SelectedNpc);
        Assert.Equal(0, session.ActionsDropped);

        // 범위 밖 첨자는 선택 해제로 접는다 — 스냅샷 빌더가 그 값으로 배열을 찌르지 않게 한다.
        await peer.SendAsync(ClientMessageKind.Select, new Select { NpcId = 999_999 }, bed.Token);
        await bed.WaitUntilAsync(async () =>
        {
            await bed.TickAsync();
            return session.SelectedNpc == -1;
        });
    }

    // ---------------------------------------------------------------- 하네스

    /// <summary>게임서버 대역 + 클라이언트 수신기 한 벌. 포트 0 으로 띄우고 끝나면 같이 내린다.</summary>
    private sealed class Bed : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task? _accept;
        private long _tick;

        private Bed(GameWorld world, PlayerRegistry players, ClientListener listener, GameServerOptions options)
        {
            World = world;
            Players = players;
            Listener = listener;
            Options = options;
        }

        public GameWorld World { get; }

        public PlayerRegistry Players { get; }

        public ClientListener Listener { get; }

        public GameServerOptions Options { get; }

        public CancellationToken Token => _cts.Token;

        public static async Task<Bed> StartAsync(int maxClients = 4, int npcs = 32)
        {
            var options = new GameServerOptions
            {
                Npcs = npcs,
                LinkPort = 0,
                ClientPort = 0,
                MaxClients = maxClients,
            };

            NpcRoster roster = NpcRoster.Select(s_instances, npcs);
            GameWorld world = GameWorld.Create(options, s_data, roster);
            var players = new PlayerRegistry(world, s_data, options);
            var listener = new ClientListener(world, s_data, players, options);

            listener.Start();

            var bed = new Bed(world, players, listener, options);

            bed._accept = listener.RunAsync(bed.Token);

            await Task.Yield();

            return bed;
        }

        /// <summary>틱 한 번. §7.2 3단계의 클라이언트 몫 → <see cref="PlayerRegistry"/> → 나머지.</summary>
        public async Task TickAsync()
        {
            var now = new Tick(++_tick);

            await Listener.TickAsync(now, Token);

            World.Players = Players.Tick;
            World.Tick(now);
        }

        public async Task<Peer> ConnectAsync()
        {
            var client = new TcpClient { NoDelay = true };

            await client.ConnectAsync("127.0.0.1", Listener.Port, Token);

            return new Peer(client);
        }

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            ArgumentNullException.ThrowIfNull(condition);

            await WaitUntilAsync(() => Task.FromResult(condition()));
        }

        /// <summary>조건이 참이 될 때까지 폴링한다. 5초 안에 안 되면 실패다.</summary>
        public async Task WaitUntilAsync(Func<Task<bool>> condition)
        {
            ArgumentNullException.ThrowIfNull(condition);

            for (int i = 0; i < 500; i++)
            {
                if (await condition())
                {
                    return;
                }

                await Task.Delay(10, Token);
            }

            Assert.Fail("5초 안에 조건이 만족되지 않았다.");
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

            await Listener.DisposeAsync();
            await World.DisposeAsync();

            _cts.Dispose();
        }
    }

    /// <summary>테스트 쪽 클라이언트 소켓. 진짜 프레임을 주고받는다.</summary>
    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        public Peer(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        /// <summary><c>CliHello</c> 를 보내고 틱을 밀어 <c>SrvHello</c> 를 받는다.</summary>
        public async Task<SrvHello> HelloAsync(Bed bed)
        {
            await SendAsync(
                ClientMessageKind.CliHello,
                new CliHello { ProtocolVersion = ClientProtocol.Version, ClientVersion = 1 },
                bed.Token);

            return await ReadHelloAsync(bed);
        }

        /// <summary><c>SrvHello</c> 만 기다린다.</summary>
        public Task<SrvHello> ReadHelloAsync(Bed bed) =>
            ReceiveAsync<SrvHello>(ClientMessageKind.SrvHello, bed);

        public async Task SendAsync<T>(ClientMessageKind kind, T message, CancellationToken ct) =>
            await ClientSession.WriteFrameAsync(
                _stream, kind, MemoryPackSerializer.Serialize(message), ct);

        /// <summary>
        /// 프레임 하나를 받는다.
        ///
        /// <b>틱을 밀면서 기다린다.</b> 서버가 소켓에 쓰는 것은 틱 스레드뿐이라
        /// (참여·<c>Pong</c>·나중의 스냅샷) 여기서 틱을 안 밀면 영원히 오지 않는다.
        /// </summary>
        public async Task<T> ReceiveAsync<T>(ClientMessageKind expected, Bed bed)
        {
            for (int i = 0; i < 500 && !_client.Client.Poll(0, SelectMode.SelectRead); i++)
            {
                await bed.TickAsync();
                await Task.Delay(5, bed.Token);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(bed.Token);

            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            (ClientMessageKind kind, byte[] payload) =
                await ClientSession.ReadFrameAsync(_stream, timeout.Token);

            Assert.Equal(expected, kind);

            return MemoryPackSerializer.Deserialize<T>(payload)!;
        }

        /// <summary><b>곱게 닫지 않는다.</b> 실제로 클라이언트가 죽는 방식이다.</summary>
        public void Kill()
        {
            _client.Client.Close(0);   // linger 0 = RST
            _client.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
