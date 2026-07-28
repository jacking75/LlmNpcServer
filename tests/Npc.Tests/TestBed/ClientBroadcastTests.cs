using System.Net.Sockets;
using MemoryPack;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.TestBed.Protocol;
using Npc.TestGameServer;
using Npc.TestGameServer.Client;

namespace Npc.Tests.TestBed;

/// <summary>
/// T6-38 — 클라이언트 방송(틱 10단계). docs/20 §7.2 · §7.4 · §8.1 · §8.3.
///
/// <para>
/// <b>진짜 소켓으로 진짜 프레임을 주고받는다.</b> 서버가 소켓에 쓰는 것은 틱 스레드뿐이라
/// (docs/20 §7.1) 테스트도 틱을 밀면서 읽는다 — 안 밀면 아무것도 오지 않는다.
/// </para>
///
/// <para><b>포트는 0(자동 할당)이다</b> (docs/20 §13).</para>
/// </summary>
[Trait("Category", "TestBed")]
public sealed class ClientBroadcastTests
{
    /// <summary>완료 조건 — 스냅샷이 2틱마다 나간다 (docs/20 §8.1).</summary>
    [Fact]
    public async Task Broadcast_SnapshotEveryTwoTicks()
    {
        await using var bed = await Bed.StartAsync();
        await using Peer peer = await bed.JoinAsync();

        peer.Reset();

        await peer.PumpAsync(bed, ticks: 20);

        // 20틱 = 스냅샷 10장. 첫 틱이 홀수일 수 있어 ±1 을 허용한다.
        Assert.InRange(peer.Snapshots.Count, 9, 11);

        Snapshot snapshot = peer.Snapshots[^1];

        Assert.True(snapshot.EntityCount > 0, "스냅샷이 비어 있다.");
        Assert.Equal(snapshot.EntityCount, snapshot.Entities!.Length);

        // 보는 사람 자신이 들어 있다 (docs/20 §8.1) — 안 넣으면 화면에 제 캐릭터가 안 보인다.
        Assert.Contains(
            snapshot.Entities,
            e => e.Kind == (byte)EntityKind.Player && e.Id == peer.PlayerId);
    }

    /// <summary>완료 조건 — 링크 상태가 1초마다 나간다 (docs/20 §8.1).</summary>
    [Fact]
    public async Task Broadcast_LinkStatusEverySecond()
    {
        await using var bed = await Bed.StartAsync();
        await using Peer peer = await bed.JoinAsync();

        peer.Reset();

        await peer.PumpAsync(bed, ticks: 30);

        Assert.InRange(peer.LinkStatuses.Count, 2, 4);

        LinkStatus status = peer.LinkStatuses[^1];

        // NPC 서버는 안 붙였다. 그것이 그대로 보여야 한다.
        Assert.Equal(0, status.Connected);
        Assert.True(status.GsTick > 0, "게임서버 틱이 0 이다.");

        // 링크가 없으니 낸 시퀀스가 전부 갭이다 — 재접속하면 NPC 서버가 셀 값과 같다.
        Assert.True(status.Gaps > 0, $"gaps={status.Gaps}");
    }

    /// <summary>
    /// 완료 조건 — 존 상태가 변화 시에 나가고, 값이 실제로 바뀐다 (docs/20 §8.1 · §8.3).
    /// </summary>
    [Fact]
    public async Task Broadcast_ZoneStatesOnChange()
    {
        await using var bed = await Bed.StartAsync();
        await using Peer peer = await bed.JoinAsync();

        // 접속 직후 한 장은 마스터데이터 기본값이다.
        await peer.PumpAsync(bed, ticks: 4);

        Assert.NotEmpty(peer.ZoneStates);

        ZoneDef target = bed.Server.Data.Zones.Zones[0];

        peer.Reset();

        Assert.True(bed.Server.Controls.Apply(
            new Control
            {
                Kind = (byte)ControlKind.SetZoneState,
                ZoneCode = target.Code.Value,
                Code = (byte)RegionState.War,
            },
            new Tick(bed.Server.Tick)));

        await peer.PumpAsync(bed, ticks: 6);

        Assert.NotEmpty(peer.ZoneStates);

        ZoneState[] zones = peer.ZoneStates[^1].Zones!;
        ZoneState changed = Assert.Single(zones, z => z.ZoneCode == target.Code.Value);

        Assert.Equal((byte)RegionState.War, changed.RegionState);
    }

    /// <summary>
    /// 완료 조건 — 클라이언트의 <c>Control</c> 이 <c>ControlHandler</c> 까지 간다 (docs/20 §8.3).
    ///
    /// T6-25 까지 <see cref="ClientSession"/> 은 이 종류를 <b>세지도 않고 버렸다.</b>
    /// </summary>
    [Fact]
    public async Task Broadcast_RoutesControlToHandler()
    {
        await using var bed = await Bed.StartAsync();
        await using Peer peer = await bed.JoinAsync();

        long applied = bed.Server.Controls.Applied;
        ZoneDef target = bed.Server.Data.Zones.Zones[0];

        await peer.SendAsync(
            ClientMessageKind.Control,
            new Control
            {
                Kind = (byte)ControlKind.SetWeather,
                ZoneCode = target.Code.Value,
                Code = (byte)Climate.Storm,
            },
            bed.Token);

        await bed.WaitUntilAsync(async () =>
        {
            await bed.TickAsync();

            return bed.Server.Controls.Applied > applied;
        });

        Assert.Equal(applied + 1, bed.Server.Controls.Applied);

        // 모르는 종류는 무시하고 센다 — 낡은 클라이언트가 게임서버를 죽이면 안 된다.
        long ignored = bed.Server.Controls.Ignored;

        await peer.SendAsync(ClientMessageKind.Control, new Control { Kind = 200 }, bed.Token);

        await bed.WaitUntilAsync(async () =>
        {
            await bed.TickAsync();

            return bed.Server.Controls.Ignored > ignored;
        });
    }

    /// <summary>
    /// 완료 조건 — 선택 NPC 의 줄은 전부 나가고, 그 외는 배치당
    /// <see cref="ClientSession.MaxOtherLogsPerBatch"/> 건까지다 (docs/20 §7.4).
    /// </summary>
    [Fact]
    public async Task Broadcast_SelectedNpcLogsAreComplete()
    {
        const int Selected = 5;

        await using var bed = await Bed.StartAsync(npcs: 300);
        await using Peer peer = await bed.JoinAsync();

        await peer.SendAsync(
            ClientMessageKind.Select, new Select { NpcId = Selected }, bed.Token);

        // 선택이 반영되고 첫 스냅샷이 나갈 때까지.
        await peer.PumpAsync(bed, ticks: 6);

        peer.Reset();

        // 선택 NPC 의 명령 50건을 한 틱에 붓는다. 전부 실려야 한다.
        for (int i = 0; i < 50; i++)
        {
            var command = new NpcCommand
            {
                Kind = NpcCommandKind.Stop,
                Npc = new NpcId(Selected),
                IssuedAt = new Tick(bed.Server.Tick),
                Correlation = new CorrelationId((uint)(i + 1)),
                Priority = CommandPriority.Normal,
            };

            Assert.True(bed.Server.World.Inbox.TryEnqueue(in command));
        }

        await peer.PumpAsync(bed, ticks: 4);

        int mine = peer.Commands.Count(c => c.Npc == Selected);

        Assert.Equal(50, mine);
    }

    /// <summary>
    /// 완료 조건 — 선택 NPC 가 아닌 줄은 배치당 상한을 넘지 않는다 (docs/20 §7.4).
    ///
    /// <b>AOI 안 NPC 를 스냅샷에서 고른다.</b> 마스터데이터 좌표로 직접 재면 필터가 보는 집합과
    /// 다른 집합을 테스트가 만들게 되고, 그러면 통과해도 아무것도 보증하지 못한다.
    /// </summary>
    [Fact]
    public async Task Broadcast_CapsOtherNpcLogsPerBatch()
    {
        await using var bed = await Bed.StartAsync(npcs: 300);
        await using Peer peer = await bed.JoinAsync();

        await peer.PumpAsync(bed, ticks: 6);

        Assert.NotEmpty(peer.Snapshots);

        int[] visible = [.. peer.Snapshots[^1].Entities!
            .Where(e => e.Kind == (byte)EntityKind.Npc)
            .Select(e => e.Id)
            .Take(100)];

        Assert.True(visible.Length >= 64, $"AOI 안 NPC 가 {visible.Length} 마리뿐이라 상한을 못 본다.");

        peer.Reset();

        uint correlation = 1;

        foreach (int npc in visible)
        {
            var command = new NpcCommand
            {
                Kind = NpcCommandKind.Stop,
                Npc = new NpcId(npc),
                IssuedAt = new Tick(bed.Server.Tick),
                Correlation = new CorrelationId(correlation++),
                Priority = CommandPriority.Normal,
            };

            Assert.True(bed.Server.World.Inbox.TryEnqueue(in command));
        }

        await peer.PumpAsync(bed, ticks: 4);

        Assert.NotEmpty(peer.CommandBatches);
        Assert.All(
            peer.CommandBatches,
            batch => Assert.True(
                batch.Length <= ClientSession.MaxOtherLogsPerBatch,
                $"배치에 {batch.Length} 줄이 실렸다."));

        ClientSession session = bed.Server.Clients.Live.Single();

        Assert.True(session.LogLinesCulled > 0, "상한에 걸린 줄이 하나도 없다.");
    }

    // ---------------------------------------------------------------- 대역

    private sealed class Bed : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task? _accept;
        private long _tick;

        private Bed(GameServer server) => Server = server;

        public GameServer Server { get; }

        public CancellationToken Token => _cts.Token;

        public static async Task<Bed> StartAsync(int npcs = 64, int maxClients = 4)
        {
            GameServer server = GameServer.Create(
                new GameServerOptions
                {
                    Npcs = npcs,
                    LinkPort = 0,
                    ClientPort = 0,
                    MaxClients = maxClients,
                    MasterData = TestPaths.MasterData,
                },
                TextWriter.Null);

            server.Start();

            var bed = new Bed(server);

            bed._accept = server.AcceptAsync(bed.Token);

            await Task.Yield();

            return bed;
        }

        /// <summary>틱 한 번. §7.2 의 1~10단계 전부다.</summary>
        public Task TickAsync() => Server.TickAsync(new Tick(++_tick), Token);

        /// <summary>붙어서 <c>CliHello</c> → <c>SrvHello</c> 까지 간다.</summary>
        public async Task<Peer> JoinAsync()
        {
            var client = new TcpClient { NoDelay = true };

            await client.ConnectAsync("127.0.0.1", Server.ClientPort, Token);

            var peer = new Peer(client);

            await peer.SendAsync(
                ClientMessageKind.CliHello,
                new CliHello { ProtocolVersion = ClientProtocol.Version, ClientVersion = 1 },
                Token);

            await WaitUntilAsync(async () =>
            {
                await TickAsync();
                await peer.DrainAsync(this);

                return peer.PlayerId != 0;
            });

            return peer;
        }

        /// <summary>조건이 참이 될 때까지 폴링한다. 10초 안에 안 되면 실패다.</summary>
        public async Task WaitUntilAsync(Func<Task<bool>> condition)
        {
            ArgumentNullException.ThrowIfNull(condition);

            for (int i = 0; i < 1_000; i++)
            {
                if (await condition())
                {
                    return;
                }

                await Task.Delay(5, Token);
            }

            Assert.Fail("10초 안에 조건이 만족되지 않았다.");
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
                catch (Exception)
                {
                    // 취소로 끝난다.
                }
            }

            await Server.DisposeAsync();

            _cts.Dispose();
        }
    }

    /// <summary>테스트 쪽 클라이언트 소켓. 받은 프레임을 종류별로 모은다.</summary>
    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;

        public Peer(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public int PlayerId { get; private set; }

        public List<Snapshot> Snapshots { get; } = [];

        public List<ZoneStates> ZoneStates { get; } = [];

        public List<LinkStatus> LinkStatuses { get; } = [];

        /// <summary>받은 <c>CommandLog</c> 배치들. 배치 경계가 §7.4 상한의 단위다.</summary>
        public List<LogCommand[]> CommandBatches { get; } = [];

        public List<LogEvent[]> EventBatches { get; } = [];

        /// <summary>받은 명령 줄 전부.</summary>
        public IEnumerable<LogCommand> Commands => CommandBatches.SelectMany(b => b);

        public async Task SendAsync<T>(ClientMessageKind kind, T message, CancellationToken ct) =>
            await ClientSession.WriteFrameAsync(
                _stream, kind, MemoryPackSerializer.Serialize(message), ct);

        /// <summary>모은 것을 비운다. <c>PlayerId</c> 는 남긴다.</summary>
        public void Reset()
        {
            Snapshots.Clear();
            ZoneStates.Clear();
            LinkStatuses.Clear();
            CommandBatches.Clear();
            EventBatches.Clear();
        }

        /// <summary>틱을 밀면서 오는 프레임을 전부 받는다.</summary>
        public async Task PumpAsync(Bed bed, int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                await bed.TickAsync();
                await DrainAsync(bed);
            }
        }

        /// <summary>지금 와 있는 프레임을 전부 읽어 분류한다.</summary>
        public async Task DrainAsync(Bed bed)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(bed.Token);

            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            while (_client.Available > 0)
            {
                (ClientMessageKind kind, byte[] payload) =
                    await ClientSession.ReadFrameAsync(_stream, timeout.Token);

                Classify(kind, payload);
            }
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();

            return ValueTask.CompletedTask;
        }

        private void Classify(ClientMessageKind kind, byte[] payload)
        {
            switch (kind)
            {
                case ClientMessageKind.SrvHello:
                    PlayerId = MemoryPackSerializer.Deserialize<SrvHello>(payload).PlayerId;
                    break;

                case ClientMessageKind.Snapshot:
                    Snapshots.Add(MemoryPackSerializer.Deserialize<Snapshot>(payload));
                    break;

                case ClientMessageKind.ZoneStates:
                    ZoneStates.Add(MemoryPackSerializer.Deserialize<ZoneStates>(payload));
                    break;

                case ClientMessageKind.LinkStatus:
                    LinkStatuses.Add(MemoryPackSerializer.Deserialize<LinkStatus>(payload));
                    break;

                case ClientMessageKind.CommandLog:
                    CommandBatches.Add(
                        MemoryPackSerializer.Deserialize<CommandLog>(payload).Commands ?? []);
                    break;

                case ClientMessageKind.EventLog:
                    EventBatches.Add(
                        MemoryPackSerializer.Deserialize<EventLog>(payload).Events ?? []);
                    break;

                default:
                    break;
            }
        }
    }
}
