using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using Npc.Contracts;
using Npc.MasterData;
using Npc.TestGameServer.World;

namespace Npc.TestGameServer.Client;

/// <summary>
/// 테스트 클라이언트 수신기. docs/20 §7.1 · §8.
///
/// <para>
/// <b>링크 수신기와 다른 점은 동시 세션 수 하나다.</b> NPC 서버는 하나만 붙지만
/// (둘이면 같은 NPC 에 두 벌의 명령이 들어온다) 사람은 <c>--max-clients</c> 만큼 붙는다 —
/// 여럿이 같은 세계를 다른 각도에서 보는 것이 이 데모의 목적이기 때문이다.
/// </para>
///
/// <para>
/// <b>한 세션이 죽어도 나머지와 링크는 산다.</b> 창 하나가 닫힌 것이 세계를 멈출 이유가 아니다.
/// 죽은 세션의 자리는 다음 <see cref="TickAsync"/> 에서 회수되고, 그때 플레이어도 지워진다 —
/// <see cref="PlayerRegistry"/> 가 틱 스레드 단독 소유라 소켓 태스크가 직접 지울 수 없다.
/// </para>
///
/// <para><b>루프백에만 바인드한다.</b> 인증·암호화는 범위 밖이다 (docs/20 §16).</para>
/// </summary>
public sealed class ClientListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly GameWorld _world;
    private readonly PlayerRegistry _players;
    private readonly GameServerOptions _options;
    private readonly ClientSession?[] _sessions;
    private readonly WorldBounds _bounds;

    /// <summary>수신기를 만든다. <b>여기서 듣기 시작하지 않는다</b> — <see cref="Start"/> 다.</summary>
    public ClientListener(
        GameWorld world, MasterDataSet data, PlayerRegistry players, GameServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(options);

        _world = world;
        _players = players;
        _options = options;
        _sessions = new ClientSession?[Math.Max(0, options.MaxClients)];
        _bounds = BoundsOf(data);
        _listener = new TcpListener(IPAddress.Loopback, options.ClientPort);
    }

    /// <summary>
    /// 실제로 바인드된 포트. <b><c>--client-port 0</c> 이면 OS 가 고른 값이 여기 나온다</b> —
    /// 테스트가 이 값을 읽어 붙는다 (docs/20 §13).
    /// </summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>월드 경계. <c>SrvHello</c> 에 실려 클라이언트의 <c>Home</c> 이 된다.</summary>
    public WorldBounds Bounds => _bounds;

    /// <summary>
    /// 세션들이 <c>Control</c> 을 넘길 처리기. <c>GameServer</c> 가 기동 시 한 번 붙인다.
    ///
    /// <b>세션마다 만들지 않는다.</b> <see cref="ControlHandler"/> 는 <c>SimWorld.Handler</c> 앞에
    /// 서는 프로세스 단위 물건이라 여럿이면 관문이 겹겹이 쌓인다 (docs/20 §8.3).
    /// </summary>
    public ControlHandler? Controls { get; set; }

    /// <summary>동시 접속 상한.</summary>
    public int Capacity => _sessions.Length;

    /// <summary>지금 붙어 있는 세션 수.</summary>
    public int Count
    {
        get
        {
            int live = 0;

            for (int slot = 0; slot < _sessions.Length; slot++)
            {
                if (Volatile.Read(ref _sessions[slot]) is { IsActive: true })
                {
                    live++;
                }
            }

            return live;
        }
    }

    /// <summary>핸드셰이크까지 끝난 접속 수.</summary>
    public long SessionsAccepted { get; private set; }

    /// <summary>정원이 차서 끊어 보낸 접속 수.</summary>
    public long SessionsRejected { get; private set; }

    /// <summary><c>CliHello</c> 가 어긋나 끊은 접속 수.</summary>
    public long HandshakesFailed { get; private set; }

    /// <summary>자리를 회수한 세션 수. 플레이어도 그때 지워진다.</summary>
    public long SessionsReaped { get; private set; }

    /// <summary>지금 살아 있는 세션. 스냅샷 빌더(T6-24)가 훑는다.</summary>
    public IEnumerable<ClientSession> Live
    {
        get
        {
            for (int slot = 0; slot < _sessions.Length; slot++)
            {
                if (Volatile.Read(ref _sessions[slot]) is { IsActive: true } session)
                {
                    yield return session;
                }
            }
        }
    }

    /// <summary>듣기 시작한다. <see cref="Port"/> 는 이 뒤부터 유효하다.</summary>
    public void Start() => _listener.Start();

    /// <summary>
    /// accept 루프. 취소될 때까지 돈다.
    ///
    /// <b><c>CliHello</c> 까지만 여기서 한다.</b> 플레이어 등록과 <c>SrvHello</c> 는
    /// <see cref="TickAsync"/> 의 몫이다 — <see cref="PlayerRegistry"/> 를 소켓 태스크가
    /// 만지면 틱 스레드가 절반만 초기화된 플레이어를 본다.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return;   // 수신기가 닫혔다
            }

            // Nagle 을 끈다. 10Hz 입력을 40ms 지연시키면 조작이 무겁게 느껴진다.
            client.NoDelay = true;

            int slot = FreeSlot();

            if (slot < 0)
            {
                SessionsRejected++;

                await RejectAsync(client, ct).ConfigureAwait(false);
                continue;
            }

            var session = new ClientSession(
                client.GetStream(), _world, _players, _bounds, _options.TimeScale)
            {
                Controls = Controls,
            };

            bool greeted;

            try
            {
                greeted = await session.HandshakeAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // 상대가 도중에 끊었거나 프레임이 깨졌다. 수신기는 계속 산다.
                greeted = false;
            }

            if (!greeted)
            {
                HandshakesFailed++;

                await session.DisposeAsync().ConfigureAwait(false);
                client.Dispose();
                continue;
            }

            SessionsAccepted++;
            Volatile.Write(ref _sessions[slot], session);
        }
    }

    /// <summary>
    /// 틱 3단계의 클라이언트 몫. <b>틱 스레드에서 부른다.</b>
    ///
    /// <list type="number">
    ///   <item>새로 붙은 세션에 플레이어를 배정하고 <c>SrvHello</c> 를 보낸다.</item>
    ///   <item>쌓인 입력을 적용하고 <c>Pong</c> 을 답한다.</item>
    ///   <item>죽은 세션의 자리와 플레이어를 회수한다.</item>
    /// </list>
    ///
    /// <b>세션 하나가 던져도 나머지를 계속 돈다.</b> 창 하나가 닫힌 것이 세계를 멈출 이유가 아니다.
    /// </summary>
    public async Task TickAsync(Tick now, CancellationToken ct)
    {
        for (int slot = 0; slot < _sessions.Length; slot++)
        {
            if (Volatile.Read(ref _sessions[slot]) is not { } session)
            {
                continue;
            }

            try
            {
                if (session.IsActive && session.NeedsJoin)
                {
                    await session.JoinAsync(now, ct).ConfigureAwait(false);
                }

                if (session.IsActive)
                {
                    await session.ApplyAsync(now, ct).ConfigureAwait(false);
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // 소켓이 죽었다. 아래에서 자리를 회수한다.
                session.Close();
            }

            if (!session.IsActive)
            {
                await ReapAsync(slot, session).ConfigureAwait(false);
            }
        }
    }

    /// <summary>죽은 세션의 자리와 플레이어를 회수한다. <b>틱 스레드에서만 부른다.</b></summary>
    private async Task ReapAsync(int slot, ClientSession session)
    {
        Volatile.Write(ref _sessions[slot], null);

        if (session.Player.Value != 0)
        {
            _players.Remove(session.Player);
        }

        SessionsReaped++;

        await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>빈 슬롯. 없으면 -1. accept 태스크만 부른다.</summary>
    private int FreeSlot()
    {
        for (int slot = 0; slot < _sessions.Length; slot++)
        {
            if (Volatile.Read(ref _sessions[slot]) is null or { IsActive: false })
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>정원 초과를 알리고 끊는다. <b>이유를 말하고 끊는다</b> — 조용히 닫으면 원인을 모른다.</summary>
    private async Task RejectAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            await ClientSession.RejectAsync(
                client.GetStream(), _bounds, _options.TimeScale, _world.Roster.Count, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        for (int slot = 0; slot < _sessions.Length; slot++)
        {
            if (Volatile.Read(ref _sessions[slot]) is { } session)
            {
                Volatile.Write(ref _sessions[slot], null);

                await session.DisposeAsync().ConfigureAwait(false);
            }
        }

        _listener.Dispose();
    }

    /// <summary>
    /// 월드 경계 — POI 전체를 감싸는 사각형.
    ///
    /// <b>좌표는 <c>pois.json</c> 의 것을 그대로 쓴다</b> (docs/20 §3.2 · §9.3).
    /// 레이아웃 파일을 따로 만들지 않는다 — 두 벌이 되면 화면과 세계가 어긋난다.
    /// </summary>
    public static WorldBounds BoundsOf(MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        ImmutableArray<PoiDef> pois = data.Pois.Pois;

        if (pois.Length == 0)
        {
            return default;
        }

        float minX = float.MaxValue;
        float minZ = float.MaxValue;
        float maxX = float.MinValue;
        float maxZ = float.MinValue;

        foreach (PoiDef poi in pois)
        {
            minX = Math.Min(minX, poi.Pos.X);
            minZ = Math.Min(minZ, poi.Pos.Z);
            maxX = Math.Max(maxX, poi.Pos.X);
            maxZ = Math.Max(maxZ, poi.Pos.Z);
        }

        return new WorldBounds(minX, minZ, maxX, maxZ);
    }
}
