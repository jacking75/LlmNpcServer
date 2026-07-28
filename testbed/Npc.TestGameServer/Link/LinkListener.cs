using System.Net;
using System.Net.Sockets;
using Npc.MasterData;
using Npc.Wire;

namespace Npc.TestGameServer.Link;

/// <summary>
/// NPC 서버 링크 수신기. docs/20 §7.1 · §2.
///
/// <para>
/// <b>왜 게임서버가 듣는가.</b> 실제 배치에서 NPC 서버는 게임서버에 붙는 위성 서비스다.
/// 게임서버가 먼저 떠 있고 NPC 서버가 나중에 붙었다 끊겼다 한다 — 재접속 로직을
/// 한쪽에만 두면 되므로 <c>TcpGameServerLink</c> 도 단순해진다 (docs/20 §2).
/// </para>
///
/// <para>
/// <b>동시 세션은 하나다.</b> 두 번째 접속은 <c>Bye</c> 를 받고 끊긴다 — NPC 서버가 둘 붙으면
/// 같은 NPC 에 두 벌의 명령이 들어와 어느 쪽이 이겼는지 알 수 없게 된다.
/// 앞선 세션이 죽으면 자리가 비고, 그때 다음 접속이 받아들여진다 (docs/20 §5.6 재접속).
/// </para>
///
/// <para>
/// <b>루프백에만 바인드한다.</b> 인증·암호화가 범위 밖이라(docs/20 §16) 밖에서 붙을 수 있으면
/// 안 된다 — 테스트 베드다.
/// </para>
/// </summary>
public sealed class LinkListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly GameWorld _world;
    private readonly MasterDataSet _data;
    private readonly GameServerOptions _options;

    private LinkSession? _session;

    /// <summary>수신기를 만든다. <b>여기서 듣기 시작하지 않는다</b> — <see cref="Start"/> 다.</summary>
    public LinkListener(GameWorld world, MasterDataSet data, GameServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);

        _world = world;
        _data = data;
        _options = options;
        _listener = new TcpListener(IPAddress.Loopback, options.LinkPort);
    }

    /// <summary>
    /// 실제로 바인드된 포트. <b><c>--link-port 0</c> 이면 OS 가 고른 값이 여기 나온다</b> —
    /// 테스트가 이 값을 읽어 붙는다 (docs/20 §13).
    /// </summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// 지금 붙어 있는 세션. 없으면 null.
    ///
    /// accept 태스크가 쓰고 틱 스레드가 읽으므로 <see cref="Volatile"/> 로만 오간다 —
    /// <c>lock</c> 은 쓰지 않는다.
    /// </summary>
    public LinkSession? Session => Volatile.Read(ref _session);

    /// <summary>핸드셰이크까지 끝난 세션 수. 재접속하면 늘어난다.</summary>
    public long SessionsAccepted { get; private set; }

    /// <summary>이미 세션이 있어서 끊어 보낸 접속 수.</summary>
    public long SessionsRejected { get; private set; }

    /// <summary>핸드셰이크가 실패한 접속 수 (NPC 서버가 거절했거나 프로토콜이 어긋났다).</summary>
    public long HandshakesFailed { get; private set; }

    /// <summary>세션이 붙어 핸드셰이크까지 끝났다.</summary>
    public event Action<LinkSession>? SessionReady;

    /// <summary>듣기 시작한다. <see cref="Port"/> 는 이 뒤부터 유효하다.</summary>
    public void Start() => _listener.Start();

    /// <summary>
    /// accept 루프. 취소될 때까지 돈다.
    ///
    /// <b>핸드셰이크를 여기서 끝낸다</b> — 붙자마자 <c>Hello</c> 를 보내는 쪽이 게임서버이고
    /// (docs/20 §5.5), 그 왕복은 소켓 태스크의 일이다. 재동기화(<c>NpcSpawned</c> 전원)는
    /// 월드에 이벤트를 내는 일이라 <b>틱 스레드</b> 몫으로 남긴다 —
    /// <c>SimWorld</c> 의 이벤트 채널은 기록자가 하나여야 한다.
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

            // Nagle 을 끈다 (docs/02 §7-4). 10Hz 배치를 40ms 지연시키면 틱 예산이 무의미해진다.
            client.NoDelay = true;

            if (Volatile.Read(ref _session) is { IsActive: true })
            {
                SessionsRejected++;

                await RejectAsync(client, ct).ConfigureAwait(false);
                continue;
            }

            var session = new LinkSession(client.GetStream(), _world, _data, _options);

            bool accepted;

            try
            {
                accepted = await session.HandshakeAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // 상대가 도중에 끊었거나 프레임이 깨졌다. 수신기는 계속 산다.
                accepted = false;
            }

            if (!accepted)
            {
                HandshakesFailed++;

                await session.DisposeAsync().ConfigureAwait(false);
                client.Dispose();
                continue;
            }

            SessionsAccepted++;
            Volatile.Write(ref _session, session);

            SessionReady?.Invoke(session);
        }
    }

    /// <summary>두 번째 접속을 끊는다. <b>이유를 말하고 끊는다</b> — 조용히 닫으면 상대가 원인을 모른다.</summary>
    private static async Task RejectAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            await LinkSession.SendByeAsync(client.GetStream(), LinkByeCode.HandshakeRejected, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 상대가 이미 끊었다. 종료 통보는 최선 노력이다.
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _session) is { } session)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _session, null);
        }

        _listener.Dispose();
    }
}
