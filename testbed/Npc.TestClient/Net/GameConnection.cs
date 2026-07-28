using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Channels;
using MemoryPack;
using Npc.TestBed.Protocol;

// System.Windows.Forms 가 암시적 using 에 들어 있어 Control 이 겹친다.
// 이 파일에서 Control 은 언제나 프로토콜 쪽이다 — 여기 위젯은 없다.
using Control = Npc.TestBed.Protocol.Control;

namespace Npc.TestClient.Net;

/// <summary>
/// 스냅샷 한 장과 그것이 도착한 시각.
///
/// <b>참조 타입인 것이 요점이다.</b> <see cref="Snapshot"/> 은 struct 라 원자 참조 교체를 할 수
/// 없다 — 수신 태스크가 struct 를 통째로 쓰는 동안 UI 가 절반만 바뀐 것을 읽는다.
/// 도착 시각을 같이 묶어 둔 것도 같은 이유다: 둘이 따로 바뀌면 보간이 엉뚱한 구간을 잡는다.
/// </summary>
/// <param name="Snapshot">스냅샷.</param>
/// <param name="AtMillis">도착 시각(ms, 프로세스 기준).</param>
public sealed record SnapshotFrame(Snapshot Snapshot, long AtMillis);

/// <summary>연결 상태. 상태바가 이것을 색으로 보여 준다 (docs/20 §9.2).</summary>
public enum ConnectionState
{
    /// <summary>아직 안 붙었다.</summary>
    Disconnected = 0,

    /// <summary>붙는 중이거나 재시도 대기 중이다.</summary>
    Connecting,

    /// <summary><c>SrvHello</c> 까지 받았다.</summary>
    Connected,
}

/// <summary>
/// 게임서버와의 연결 한 벌. docs/20 §8 · §9.2.
///
/// <para>
/// <b>UI 스레드로 마샬링하는 방법이 <c>BeginInvoke</c> 가 아니다.</b> 수신 태스크는 받은 것을
/// <b>원자 참조 교체</b>(<see cref="Volatile"/>)로 여기 놓고, UI 는 16ms 렌더 타이머에서 읽는다.
/// 5Hz × 세션마다 <c>BeginInvoke</c> 를 던지면 메시지 큐가 렌더보다 앞서 밀리고,
/// 무엇보다 폼이 닫히는 순간 <c>ObjectDisposedException</c> 이 나는 전형적인 자리가 된다.
/// </para>
///
/// <para>
/// <b>게임서버가 없어도 크래시하지 않는다.</b> 붙을 때까지 250ms → 4s 백오프로 재시도한다 —
/// 데모에서 세 프로세스가 어떤 순서로 떠도 되어야 한다 (docs/20 §12).
/// </para>
///
/// <para>
/// <b>보내는 것은 쓰기 태스크 하나뿐이다.</b> UI 스레드가 소켓에 직접 쓰면 스냅샷 프레임과
/// 바이트가 섞여 스트림이 죽는다 — 프레임 하나가 아니라 연결이 죽는다.
/// </para>
/// </summary>
public sealed class GameConnection : IAsyncDisposable
{
    /// <summary>로그 링의 칸 수. 화면은 500줄을 보여 주므로 여유를 둔다 (docs/20 §9.6).</summary>
    public const int LogCapacity = 1_024;

    /// <summary>재접속 백오프 하한(ms). docs/20 §5.6 의 링크와 같은 규약이다.</summary>
    public const int MinBackoffMillis = 250;

    /// <summary>재접속 백오프 상한(ms).</summary>
    public const int MaxBackoffMillis = 4_000;

    /// <summary>보낼 프레임을 담아 두는 칸 수. 넘치면 가장 오래된 것을 버린다.</summary>
    private const int OutboundCapacity = 256;

    private readonly string _host;
    private readonly int _port;
    private readonly Channel<byte[]> _outbound = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(OutboundCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    private readonly LogCommand[] _commands = new LogCommand[LogCapacity];
    private readonly LogEvent[] _events = new LogEvent[LogCapacity];

    private long _commandTail;
    private long _eventTail;

    private int _state;
    private SrvHello _hello;
    private SnapshotFrame? _latest;
    private SnapshotFrame? _previous;
    private ZoneStates _zones;
    private LinkStatus _link;
    private long _roundTripMillis = -1;

    /// <summary>연결 하나를 만든다. <b>여기서 붙지 않는다</b> — <see cref="RunAsync"/> 다.</summary>
    public GameConnection(string host, int port)
    {
        _host = host;
        _port = port;
    }

    /// <summary>지금 상태.</summary>
    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

    /// <summary>서버가 준 인사. 월드 경계·<c>PlayerId</c>·타임스케일이 여기 있다.</summary>
    public SrvHello Hello => _hello;

    /// <summary>이 창의 플레이어. 0 이면 아직 배정 전이거나 정원 초과다.</summary>
    public int PlayerId => _hello.PlayerId;

    /// <summary>가장 최근 스냅샷. 없으면 null.</summary>
    public SnapshotFrame? Latest => Volatile.Read(ref _latest);

    /// <summary>그 직전 스냅샷. 둘 사이를 보간한다 (docs/20 §9.3).</summary>
    public SnapshotFrame? Previous => Volatile.Read(ref _previous);

    /// <summary>가장 최근 존 상태.</summary>
    public ZoneStates Zones => _zones;

    /// <summary>가장 최근 링크 상태. 상태바의 "gs − npc" 가 여기서 나온다 (docs/20 §9.2).</summary>
    public LinkStatus Link => _link;

    /// <summary>마지막으로 잰 왕복 시간(ms). 아직 없으면 -1.</summary>
    public long RoundTripMillis => Volatile.Read(ref _roundTripMillis);

    /// <summary>접속을 시도한 횟수. 0 이 아닌데 <see cref="State"/> 가 Connecting 이면 재시도 중이다.</summary>
    public long Attempts { get; private set; }

    /// <summary>받은 스냅샷 수.</summary>
    public long SnapshotsReceived { get; private set; }

    /// <summary>지금까지 받은 명령 로그 줄 수. 링을 덮어쓴 것도 센다.</summary>
    public long CommandsLogged => Volatile.Read(ref _commandTail);

    /// <summary>지금까지 받은 이벤트 로그 줄 수.</summary>
    public long EventsLogged => Volatile.Read(ref _eventTail);

    /// <summary>
    /// 붙고, 끊기면 다시 붙는다. 취소될 때까지 돈다.
    ///
    /// <b>예외를 밖으로 내보내지 않는다.</b> 창 하나가 서버를 못 찾은 것이 앱을 죽일 이유가 아니다.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        int backoff = MinBackoffMillis;

        while (!ct.IsCancellationRequested)
        {
            Volatile.Write(ref _state, (int)ConnectionState.Connecting);
            Attempts++;

            try
            {
                await SessionAsync(ct).ConfigureAwait(false);

                backoff = MinBackoffMillis;   // 한 번이라도 붙었으면 다음 재시도는 빠르게
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 서버가 아직 안 떴거나 죽었다. 조용히 다시 시도한다 — 데모에서 세 프로세스가
                // 어떤 순서로 떠도 되어야 한다 (docs/20 §12).
            }

            Volatile.Write(ref _state, (int)ConnectionState.Disconnected);

            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = Math.Min(MaxBackoffMillis, backoff * 2);
        }

        Volatile.Write(ref _state, (int)ConnectionState.Disconnected);
    }

    // ---------------------------------------------------------------- 송신

    /// <summary>이동 입력. 10Hz, 키가 눌린 동안만 (docs/20 §9.4).</summary>
    public void SendInput(int seq, float dirX, float dirZ, bool run) => Send(
        ClientMessageKind.Input,
        new Input { Seq = seq, DirX = dirX, DirZ = dirZ, Run = (byte)(run ? 1 : 0) });

    /// <summary>대화. 사거리 판정은 서버가 한다 (docs/20 §7.3).</summary>
    public void SendInteract(int npc) => Send(ClientMessageKind.Interact, new Interact { NpcId = npc });

    /// <summary>공격.</summary>
    public void SendAttack(int npc, int amount) =>
        Send(ClientMessageKind.Attack, new Attack { NpcId = npc, Amount = amount });

    /// <summary>선택. 음수면 해제다.</summary>
    public void SendSelect(int npc) => Send(ClientMessageKind.Select, new Select { NpcId = npc });

    /// <summary>시나리오 제어 (docs/20 §8.3).</summary>
    public void SendControl(ControlKind kind, ushort zone, byte code, int amount) => Send(
        ClientMessageKind.Control,
        new Control { Kind = (byte)kind, ZoneCode = zone, Code = code, Amount = amount });

    /// <summary>왕복 시간 측정.</summary>
    public void SendPing() =>
        Send(ClientMessageKind.Ping, new Ping { ClientStamp = Environment.TickCount64 });

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _outbound.Writer.TryComplete();

        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------- 로그 읽기

    /// <summary>
    /// 최근 명령 로그를 <b>새 것부터</b> 채운다. 로그 패널은 시간 역순이다 (docs/20 §9.6).
    /// </summary>
    /// <returns>채운 줄 수.</returns>
    public int ReadRecentCommands(Span<LogCommand> into) =>
        ReadRecent(_commands, CommandsLogged, into);

    /// <summary>최근 이벤트 로그를 새 것부터 채운다.</summary>
    /// <returns>채운 줄 수.</returns>
    public int ReadRecentEvents(Span<LogEvent> into) => ReadRecent(_events, EventsLogged, into);

    /// <summary>링에서 새 것부터 <paramref name="into"/> 만큼 뜬다. 꼬리는 호출부가 읽어 준다.</summary>
    private static int ReadRecent<T>(T[] ring, long tail, Span<T> into)
    {
        int count = (int)Math.Min(into.Length, Math.Min(tail, LogCapacity));

        for (int i = 0; i < count; i++)
        {
            into[i] = ring[(tail - 1 - i) % LogCapacity];
        }

        return count;
    }

    // ---------------------------------------------------------------- 세션

    private async Task SessionAsync(CancellationToken ct)
    {
        using var client = new TcpClient { NoDelay = true };

        await client.ConnectAsync(_host, _port, ct).ConfigureAwait(false);

        NetworkStream stream = client.GetStream();

        await WriteFrameAsync(
            stream,
            ClientMessageKind.CliHello,
            MemoryPackSerializer.Serialize(new CliHello
            {
                ProtocolVersion = ClientProtocol.Version,
                ClientVersion = 1,
            }),
            ct).ConfigureAwait(false);

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task writer = Task.Run(() => WriteLoopAsync(stream, session.Token), CancellationToken.None);

        try
        {
            await ReadLoopAsync(stream, session.Token).ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);

            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 취소로 끝난다.
            }
        }
    }

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        PipeReader reader = PipeReader.Create(stream);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (ClientProtocol.TryReadFrame(
                    ref buffer, out ClientMessageKind kind, out ReadOnlySequence<byte> payload))
                {
                    Receive(kind, payload);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;   // EOF. 서버가 끊었다
                }
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async Task WriteLoopAsync(Stream stream, CancellationToken ct)
    {
        await foreach (byte[] frame in _outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private void Receive(ClientMessageKind kind, ReadOnlySequence<byte> payload)
    {
        switch (kind)
        {
            case ClientMessageKind.SrvHello:
                _hello = MemoryPackSerializer.Deserialize<SrvHello>(payload);

                // PlayerId 0 은 "자리가 없다" 다 (docs/20 §8.1). 그래도 연결은 산 것으로 둔다 —
                // 상태바가 "정원 초과" 를 말할 수 있어야 한다.
                Volatile.Write(ref _state, (int)ConnectionState.Connected);
                return;

            case ClientMessageKind.Snapshot:
            {
                var frame = new SnapshotFrame(
                    MemoryPackSerializer.Deserialize<Snapshot>(payload), Environment.TickCount64);

                // 직전 것을 밀어 두고 새것을 올린다. 렌더러는 이 둘 사이를 보간한다 (docs/20 §9.3).
                Volatile.Write(ref _previous, Volatile.Read(ref _latest));
                Volatile.Write(ref _latest, frame);

                SnapshotsReceived++;
                return;
            }

            case ClientMessageKind.ZoneStates:
                _zones = MemoryPackSerializer.Deserialize<ZoneStates>(payload);
                return;

            case ClientMessageKind.LinkStatus:
                _link = MemoryPackSerializer.Deserialize<LinkStatus>(payload);
                return;

            case ClientMessageKind.CommandLog:
                Append(MemoryPackSerializer.Deserialize<CommandLog>(payload).Commands, _commands, ref _commandTail);
                return;

            case ClientMessageKind.EventLog:
                Append(MemoryPackSerializer.Deserialize<EventLog>(payload).Events, _events, ref _eventTail);
                return;

            case ClientMessageKind.Pong:
            {
                var pong = MemoryPackSerializer.Deserialize<Pong>(payload);

                Volatile.Write(ref _roundTripMillis, Environment.TickCount64 - pong.ClientStamp);
                return;
            }

            default:
                // 클라 → 서버 종류가 거꾸로 왔거나 모르는 것이다. 프레임 경계는 맞았으므로 스트림은 멀쩡하다.
                return;
        }
    }

    private static void Append<T>(T[]? lines, T[] ring, ref long tail)
    {
        if (lines is null)
        {
            return;
        }

        foreach (T line in lines)
        {
            ring[tail % LogCapacity] = line;

            // 값 쓰기가 먼저 보이고 그 다음 꼬리가 보여야 한다.
            Volatile.Write(ref tail, tail + 1);
        }
    }

    private void Send<T>(ClientMessageKind kind, T message)
    {
        if (State != ConnectionState.Connected)
        {
            return;   // 안 붙어 있으면 조용히 버린다. 큐에 쌓아 두면 재접속 순간 과거가 쏟아진다
        }

        byte[] payload = MemoryPackSerializer.Serialize(message);
        var writer = new ArrayBufferWriter<byte>(ClientProtocol.HeaderSize + payload.Length);

        ClientProtocol.WriteHeader(writer, kind, payload.Length);
        writer.Write(payload);

        _outbound.Writer.TryWrite(writer.WrittenSpan.ToArray());
    }

    private static async Task WriteFrameAsync(
        Stream stream, ClientMessageKind kind, byte[] payload, CancellationToken ct)
    {
        var writer = new ArrayBufferWriter<byte>(ClientProtocol.HeaderSize + payload.Length);

        ClientProtocol.WriteHeader(writer, kind, payload.Length);
        writer.Write(payload);

        await stream.WriteAsync(writer.WrittenMemory, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}
