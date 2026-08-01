using System.Globalization;
using System.Net.Sockets;
using MemoryPack;
using Npc.Contracts;
using Npc.Core;
using Npc.Host;
using Npc.Host.Api;
using Npc.Runtime;
using Npc.TestBed.Protocol;
using Npc.TestGameServer;
using Npc.TestGameServer.Client;
using Npc.TestGameServer.World;

namespace Npc.Tests.TestBed;

/// <summary>
/// T6-35 — 종단 테스트. docs/20 §13 · §14.1.
///
/// <para>
/// <b>여기서 처음으로 진짜 두 프로세스가 붙는다.</b> 게임서버(<see cref="GameServer"/>)와
/// NPC 서버(<see cref="NpcHost"/>)를 <b>인프로세스로, 포트 0 에</b> 띄워 실제 소켓으로 잇는다.
/// 가짜 상대를 하나라도 끼우면 "두 구현이 서로 맞는가" 라는 정작 볼 것을 못 본다.
/// </para>
///
/// <para>
/// <b>포트는 0(자동 할당)이다</b> (docs/20 §13) — 고정 포트를 쓰면 개발자가 데모를 띄워 둔 채
/// 테스트를 돌릴 때 깨진다.
/// </para>
///
/// <para>
/// <b>틱은 테스트가 민다.</b> <see cref="GameServer.RunAsync"/> 의 100ms 페이싱을 쓰면 300틱에
/// 30초가 걸린다. 대신 <see cref="GameServer.TickAsync"/> 를 직접 부르고, 매 틱
/// NPC 서버가 <see cref="Bed.MaxLead"/> 틱 이상 뒤처지지 않게 기다린다 —
/// <c>Npc.Host</c> 의 펌프가 루프백에서 하는 것과 같은 락스텝이다.
/// </para>
///
/// <para>
/// <b>결정론은 여기서 성립하지 않는다</b> (docs/20 §14.3). 명령이 몇 틱 늦게 도착할 수 있고
/// 그것이 스텝 경계를 바꾼다. 그래서 단언은 전부 <b>속성</b>이다 — 정확한 수를 세지 않는다.
/// </para>
///
/// <para>
/// <b><c>AllocationCollection</c> 에 넣는다.</b> 이 회차는 서버 둘을 띄우고 소켓 태스크 넷을
/// 돌리므로 스레드 풀을 꽤 먹는다 — 옆에서 <c>GC.GetAllocatedBytesForCurrentThread</c> 델타를
/// 재는 테스트가 돌면 그 값이 오염된다. 실제로 <c>Transition_DoesNotAllocate</c> 가
/// 2,608바이트로 한 번 흔들렸다. 병렬을 끄는 컬렉션에 같이 두는 것이 제일 싸다.
/// </para>
/// </summary>
[Trait("Category", "TestBed")]
[Collection(Npc.Tests.Runtime.AllocationCollection.Name)]
public sealed class EndToEndTests
{
    /// <summary>docs/20 §13 이 정한 회차 길이.</summary>
    private const int RunTicks = 300;

    /// <summary>
    /// T6-35 완료 조건 — 소켓 종단이 실제로 돈다. <c>NpcArrived ≥ 1</c> · 시퀀스 갭 0 · 드롭 0.
    ///
    /// <para>
    /// <b>이 하나가 G6-3 이다.</b> NPC 서버가 낸 <c>MoveTo</c> 가 소켓을 건너 게임서버에 닿고,
    /// <c>MovementSim</c> 이 도착시키고, <c>NpcArrived</c> 가 다시 소켓을 건너와 스텝이 전진한다 —
    /// 그 한 바퀴가 돌지 않으면 나머지 아홉 게이트는 전부 의미가 없다.
    /// </para>
    ///
    /// <para>가짜 클라이언트 소켓도 붙여 스냅샷이 오는 것까지 본다 (docs/20 §8.1).</para>
    /// </summary>
    [Fact]
    public async Task TestBed_EndToEnd_NpcArrives()
    {
        await using Bed bed = await Bed.StartAsync(npcs: 16);
        await using Peer client = await bed.JoinClientAsync();

        await bed.DriveAsync(RunTicks);
        await client.DrainAsync();

        LinkStats link = bed.Host.Snapshot().Link;

        Assert.True(
            bed.EventsOf(GameEventKind.NpcArrived) >= 1,
            $"도착이 한 건도 없다. {bed.Describe()}");

        // N6 — 갭이 0 이 아니면 이벤트가 새고 있다는 뜻이다.
        Assert.Equal(0, link.EventGapsDetected);

        // 역압에 걸린 명령이 없어야 한다. 16마리 회차에서 걸리면 링 크기가 아니라 경로가 이상한 것이다.
        Assert.Equal(0, link.CommandsDropped);

        // 스텝이 전진했다 = 도착 이벤트를 NPC 서버가 실제로 소비했다.
        Assert.True(bed.Host.Snapshot().StepsAdvanced > 0, bed.Describe());

        // 틱 예산은 소켓 경로에서도 그대로다 (docs/20 §14.1).
        Assert.Equal(0, bed.Host.Metrics.Snapshot().Tick.Overruns);

        // 클라이언트도 붙어서 세계를 본다. 2틱마다 한 장이므로 300틱이면 넉넉하다.
        Assert.NotEqual(0, client.PlayerId);
        Assert.True(client.Snapshots > 0, $"스냅샷을 한 장도 못 받았다 ({client.Snapshots}).");
    }

    /// <summary>
    /// T6-35 완료 조건 — 플레이어를 NPC 30m 안에 놓으면 그 NPC 의 LOD 가 0 이 된다.
    ///
    /// <para>
    /// <b>이 경로는 전부 게임서버가 계산해서 보내 준다</b> (docs/02 §3.3). NPC 서버는 거리를
    /// 재지 않는다 — 재면 NPC 5,000 × 플레이어 20 이 틱당 100,000회 거리 계산이 되고
    /// 그것만으로 틱 예산이 날아간다 (docs/11 §4).
    /// </para>
    ///
    /// <para>
    /// <b>플레이어를 매 틱 다시 NPC 위에 놓는다.</b> NPC 는 일터로 걸어가는 중이라
    /// 한 번만 놓으면 곧 <see cref="LodUpdater.LodZeroDistance"/> 밖으로 나간다.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TestBed_ProximityChangesLod()
    {
        const int Watched = 0;

        await using Bed bed = await Bed.StartAsync(npcs: 16);

        // 스폰 확인이 끝나야 NPC 가 세계에 있다.
        await bed.DriveAsync(10);

        PlayerId player = bed.Server.Players.Add();

        Assert.NotEqual(0, player.Value);

        // 붙기 전에는 관측 대상이 아니다.
        Assert.True(bed.Host.Trace(Watched).Lod > 0, bed.Describe());

        await bed.DriveUntilAsync(
            () => bed.Host.Trace(Watched).Lod == 0,
            maxTicks: 200,
            beforeTick: () => bed.Follow(player, Watched));

        Assert.Equal(0, bed.Host.Trace(Watched).Lod);
    }

    /// <summary>
    /// T6-35 완료 조건 — <c>Interact</c> 가 그 NPC 의 최근 사건과 플래그에 남는다.
    ///
    /// <b>사거리 밖에서는 아무 일도 없다</b> (docs/20 §7.3) — 그래서 플레이어를 NPC 위에 올린다.
    /// 사거리 판정은 게임서버가 하고, 거절 사유는 클라이언트에 돌려주지 않는다.
    /// </summary>
    [Fact]
    public async Task TestBed_InteractRaisesInterrupt()
    {
        const int Watched = 0;

        await using Bed bed = await Bed.StartAsync(npcs: 16);

        await bed.DriveAsync(10);

        PlayerId player = bed.Server.Players.Add();

        await bed.DriveUntilAsync(
            () => Saw(bed, Watched, GameEventKind.PlayerInteracted),
            maxTicks: 200,
            beforeTick: () =>
            {
                bed.Follow(player, Watched);
                bed.Server.Players.TryInteract(player, new NpcId(Watched), new Tick(bed.Now));
            });

        Assert.True(bed.Server.Players.Interacts > 0, bed.Describe());
        Assert.True(Saw(bed, Watched, GameEventKind.PlayerInteracted), bed.Describe());

        // 근접 판정은 5틱마다다. 상호작용이 닿았으면 그 사이에 이미 들어와 있어야 한다.
        Assert.Contains(
            nameof(WorldFlags.PlayerNearby),
            bed.Host.Trace(Watched).Flags,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// T6-35 완료 조건 — <c>--drop-rate 0.3</c> 에서 합성 타임아웃 &gt; 0, 멈춘 NPC 0.
    ///
    /// <para>
    /// <b>명령은 유실된다고 가정한다</b> (CLAUDE.md §2.2). 게임서버가 명령을 조용히 버리면
    /// 응답 이벤트가 아예 오지 않고, NPC 서버는 스텝의 <c>timeout_s</c> 로 <c>ActionFailed</c> 를
    /// 스스로 합성해 진행을 재개해야 한다. <b>이 경로가 깨지면 NPC 가 영원히 멈춘다</b> —
    /// 평소에는 아무 일도 안 일어나므로 일부러 깨뜨려야 보인다.
    /// </para>
    ///
    /// <para>
    /// <b>"멈춘 NPC" 를 무엇으로 세는가.</b> 스텝은 늦게라도 전진하므로 "지금 대기 중" 은
    /// 멈춘 것이 아니다. 여기서 세는 것은 <b>영원히 못 움직이는 상태</b> 둘이다 —
    /// <c>Unspawned</c>(스폰 확인을 못 받아 명령을 하나도 못 내는 상태)와
    /// <c>Done</c>(플랜이 끝났는데 순환이 아니라 다음이 없는 상태).
    /// </para>
    /// </summary>
    [Fact]
    public async Task TcpLink_CommandLossSynthesizesTimeout()
    {
        await using Bed bed = await Bed.StartAsync(npcs: 16, dropRate: 0.3);

        await bed.DriveAsync(RunTicks);

        HostSnapshot snapshot = bed.Host.Snapshot();

        Assert.True(snapshot.TimeoutsSynthesized > 0, $"합성이 0 이다. {bed.Describe()}");
        Assert.True(snapshot.StepsAdvanced > 0, bed.Describe());

        int stuck = 0;

        for (int npc = 0; npc < bed.Host.Npcs; npc++)
        {
            var status = (StepStatus)bed.Host.Store.StepStatus[npc];

            if (status is StepStatus.Unspawned or StepStatus.Done)
            {
                stuck++;
            }
        }

        Assert.Equal(0, stuck);

        // 드롭이 있어도 링크 자체는 멀쩡하다 — 버리는 것은 게임서버 안쪽이다.
        Assert.Equal(0, bed.Host.Snapshot().Link.EventGapsDetected);
    }

    // ---------------------------------------------------------------- 보조

    private static bool Saw(Bed bed, int npc, GameEventKind kind)
    {
        foreach (TraceEvent recent in bed.Host.Trace(npc).Recent)
        {
            if (string.Equals(recent.Kind, kind.ToString(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 게임서버 + NPC 서버 한 벌. <b>둘 다 진짜다.</b>
    /// </summary>
    private sealed class Bed : IAsyncDisposable
    {
        /// <summary>NPC 서버가 뒤처져도 되는 틱 수. <c>Npc.Host</c> 펌프의 값과 같다.</summary>
        public const int MaxLead = 1;

        /// <summary>따라오기를 기다리는 상한(ms). 넘으면 테스트 실패다 — 조용히 넘어가면 회차가 비어 버린다.</summary>
        public const int CatchUpTimeoutMillis = 10_000;

        private readonly CancellationTokenSource _cts = new();
        private readonly MirrorLog.Cursor _cursor;
        private readonly LoggedEvent[] _buffer = new LoggedEvent[MirrorLog.Capacity];
        private readonly long[] _counts = new long[256];

        private Task? _accept;
        private Task? _run;

        private Bed(GameServer server, NpcHost host)
        {
            Server = server;
            Host = host;
            _cursor = server.Mirror.NewCursor();
        }

        public GameServer Server { get; }

        public NpcHost Host { get; }

        public CancellationToken Token => _cts.Token;

        /// <summary>지금 틱 번호. <b>이름이 <c>Tick</c> 이 아닌 이유는 그것이 타입이기 때문이다.</b></summary>
        public long Now { get; private set; }

        /// <summary>게임서버가 낸 이 종류의 이벤트 수. 미러에서 샌다 (docs/20 §7.4).</summary>
        public long EventsOf(GameEventKind kind) => _counts[(byte)kind];

        public static async Task<Bed> StartAsync(int npcs = 32, double dropRate = 0)
        {
            GameServer server = GameServer.Create(
                new GameServerOptions
                {
                    Npcs = npcs,
                    LinkPort = 0,
                    ClientPort = 0,
                    TimeScale = 60,
                    DropRate = dropRate,
                    MasterData = TestPaths.MasterData,
                },
                TextWriter.Null);

            server.Start();

            // --npcs·--time-scale·로스터·마스터데이터가 넷 다 같아야 핸드셰이크가 통과한다
            // (docs/20 §5.5). 하나라도 다르면 여기서 거절되고, 그것이 의도된 동작이다.
            string[] args =
            [
                "--link", "tcp",
                "--gs-port", server.LinkPort.ToString(CultureInfo.InvariantCulture),
                "--npcs", npcs.ToString(CultureInfo.InvariantCulture),
                "--time-scale", "60",
                "--days", "0",
                "--tier", "none",
                "--no-dashboard",

                // <b>프리베이크 스토어를 일부러 안 읽는다.</b> 이 회차가 보는 것은 소켓 경로이지
                // 플랜 품질이 아니고, 폴백 40개만으로도 MoveTo → 도착이 돈다.
                // 읽으면 회차마다 254개 파일을 다시 로드하는데, 그 디스크·할당이
                // 옆에서 도는 예산 테스트(Host_LoadsEveryBucketWithinThreeSeconds 등)를 흔든다.
                // 이름을 유니크하게 둬야 ResolvePlanStore 가 위로 올라가 저장소의 것을 찾지 않는다.
                "--planstore", "no-planstore-testbed-e2e",
            ];

            Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

            NpcHost host = NpcHost.Create(
                options with { MasterData = TestPaths.MasterData }, TextWriter.Null);

            var bed = new Bed(server, host);

            bed._accept = server.AcceptAsync(bed.Token);
            bed._run = host.RunAsync(bed.Token);

            // accept 도 접속도 다른 태스크라 몇 틱 걸린다. 붙을 때까지 민다.
            for (int i = 0; i < 600 && server.Link.Session is not { IsAccepted: true }; i++)
            {
                await bed.TickAsync();
                await Task.Delay(5, bed.Token);
            }

            Assert.True(
                server.Link.Session is { IsAccepted: true },
                $"링크 세션이 붙지 않았다. {bed.Describe()}");

            // <b>NPC 서버가 첫 틱을 돌 때까지는 락스텝을 걸지 않는다.</b> 걸면 교착한다 —
            // NPC 서버의 시계는 <c>TickSync</c> 로만 움직이는데(docs/15 §3), 그 TickSync 는
            // 게임서버가 다음 틱을 돌아야 나간다. 세션이 붙기 전 틱들의 TickSync 는
            // 링크 없는 구간의 이벤트로 이미 배수돼 버렸으므로, 여기서 기다리면
            // "아직 안 돈 NPC 서버" 를 기다리며 "TickSync 를 낼 게임서버" 를 멈춰 세우게 된다.
            for (int i = 0; i < 200 && host.Loop.TicksCommitted == 0; i++)
            {
                await bed.TickAsync();
                await Task.Delay(1, bed.Token);
            }

            Assert.True(host.Loop.TicksCommitted > 0, $"NPC 서버가 틱을 돌지 않았다. {bed.Describe()}");

            return bed;
        }

        /// <summary>틱 한 번. §7.2 의 1~10단계 전부다.</summary>
        public async Task TickAsync()
        {
            await Server.TickAsync(new Tick(++Now), Token);

            Harvest();
        }

        /// <summary>NPC 서버와 보조를 맞춰 <paramref name="ticks"/> 틱을 민다.</summary>
        public async Task DriveAsync(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                await TickAsync();
                await CatchUpAsync();
            }
        }

        /// <summary>조건이 참이 될 때까지 민다. 안 되면 실패다.</summary>
        public async Task DriveUntilAsync(Func<bool> until, int maxTicks, Action? beforeTick = null)
        {
            ArgumentNullException.ThrowIfNull(until);

            for (int i = 0; i < maxTicks; i++)
            {
                beforeTick?.Invoke();

                await TickAsync();
                await CatchUpAsync();

                if (until())
                {
                    return;
                }
            }

            Assert.Fail($"{maxTicks}틱 안에 조건이 만족되지 않았다. {Describe()}");
        }

        /// <summary>플레이어를 그 NPC 위로 옮긴다. 근접·상호작용 사거리 판정의 기준이 위치뿐이다.</summary>
        public void Follow(PlayerId player, int npc) =>
            Server.Players.Teleport(player, Server.World.Transforms.Interpolate(npc, new Tick(Now)));

        /// <summary>실패 메시지에 붙일 한 줄. <b>숫자가 없으면 왜 실패했는지 알 수 없다.</b></summary>
        public string Describe()
        {
            HostSnapshot s = Host.Snapshot();

            return string.Create(
                CultureInfo.InvariantCulture,
                $"gs tick {Now} · npc tick {s.TicksProcessed}/{Host.Loop.TicksCommitted} · "
                + $"cmd {s.CommandsEmitted}→{Server.World.CommandsApplied} · "
                + $"step {s.StepsAdvanced} · timeout {s.TimeoutsSynthesized} · "
                + $"arrived {EventsOf(GameEventKind.NpcArrived)} · "
                + $"gap {s.Link.EventGapsDetected} · drop {s.Link.CommandsDropped}");
        }

        /// <summary>클라이언트 소켓 하나를 붙인다. <c>CliHello</c> → <c>SrvHello</c> 까지 간다.</summary>
        public async Task<Peer> JoinClientAsync()
        {
            var client = new TcpClient { NoDelay = true };

            await client.ConnectAsync("127.0.0.1", Server.ClientPort, Token);

            var peer = new Peer(client, Token);

            await peer.SendHelloAsync();

            for (int i = 0; i < 200 && peer.PlayerId == 0; i++)
            {
                await TickAsync();
                await peer.DrainAsync();
            }

            Assert.NotEqual(0, peer.PlayerId);

            return peer;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();

            foreach (Task? task in new[] { _run, _accept })
            {
                if (task is null)
                {
                    continue;
                }

                try
                {
                    await task;
                }
                catch (Exception)
                {
                    // 취소·소켓 종료로 끝난다.
                }
            }

            await Host.DisposeAsync();
            await Server.DisposeAsync();

            _cts.Dispose();
        }

        /// <summary>
        /// NPC 서버가 <see cref="MaxLead"/> 틱 이상 뒤처지면 기다린다.
        ///
        /// <b>이것이 없으면 게임서버가 300틱을 혼자 다 돌아 버린다</b> — 그때 NPC 서버의 명령은
        /// 전부 지나간 세계에 도착하고, 회차가 "아무 일도 없었다" 로 끝난다.
        /// </summary>
        private async Task CatchUpAsync()
        {
            long deadline = Environment.TickCount64 + CatchUpTimeoutMillis;

            while (Now - Host.Loop.TicksCommitted > MaxLead)
            {
                if (Environment.TickCount64 > deadline)
                {
                    Assert.Fail($"NPC 서버가 따라오지 못했다. {Describe()}");
                }

                await Task.Delay(1, Token);
            }
        }

        /// <summary>미러에 새로 쌓인 이벤트를 종류별로 센다. 링이 1,024칸이라 틱마다 걷는다.</summary>
        private void Harvest()
        {
            int got;

            while ((got = Server.Mirror.ReadEvents(_cursor, _buffer)) > 0)
            {
                for (int i = 0; i < got; i++)
                {
                    _counts[_buffer[i].Kind]++;
                }
            }
        }
    }

    /// <summary>테스트 쪽 클라이언트 소켓. 받은 것을 세기만 한다.</summary>
    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly CancellationToken _ct;

        public Peer(TcpClient client, CancellationToken ct)
        {
            _client = client;
            _stream = client.GetStream();
            _ct = ct;
        }

        public int PlayerId { get; private set; }

        public int Snapshots { get; private set; }

        public Task SendHelloAsync() => ClientSession.WriteFrameAsync(
            _stream,
            ClientMessageKind.CliHello,
            MemoryPackSerializer.Serialize(
                new CliHello { ProtocolVersion = ClientProtocol.Version, ClientVersion = 1 }),
            _ct);

        /// <summary>지금 와 있는 프레임을 전부 읽는다.</summary>
        public async Task DrainAsync()
        {
            while (_client.Available > 0)
            {
                (ClientMessageKind kind, byte[] payload) =
                    await ClientSession.ReadFrameAsync(_stream, _ct);

                switch (kind)
                {
                    case ClientMessageKind.SrvHello:
                        PlayerId = MemoryPackSerializer.Deserialize<SrvHello>(payload).PlayerId;
                        break;

                    case ClientMessageKind.Snapshot:
                        Snapshots++;
                        break;

                    default:
                        break;
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
