using System.Buffers;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;
using MemoryPack;
using Npc.Contracts;
using Npc.Wire;

namespace Npc.Gateway;

/// <summary>
/// 실제 게임서버 TCP 링크. docs/02 §2·§7 · docs/20 §5·§6.
///
/// <para>
/// <b>[T6-06] 지금은 연결과 핸드셰이크까지다.</b> 송신(T6-07)·수신(T6-08)·재접속(T6-09)은
/// 아직이고, 그 전에는 <see cref="FlushAsync"/> 가 던진다. 상태·통계·<see cref="Events"/> 는
/// 이미 계약대로 동작한다.
/// </para>
///
/// <para>
/// <b>핸드셰이크에서 넷을 검증한다</b> (docs/20 §5.5) — 프로토콜 버전 · 타임스케일 ·
/// 마스터데이터 해시 · 로스터 해시. 하나라도 다르면 거절 코드를 담은
/// <see cref="WireHelloAck"/> 와 <see cref="WireBye"/> 를 보낸 뒤 <see cref="LinkState.Faulted"/> 다.
/// </para>
///
/// <para>
/// <b><see cref="LinkState.Faulted"/> 는 사람이 고쳐야 하는 상태다.</b> 재시도하지 않는다 —
/// 마스터데이터 불일치를 무한 재시도로 덮으면 로그만 차고 원인이 묻힌다 (docs/20 §6.3).
/// 접속 실패(상대가 아직 안 떴다)는 성격이 달라 <see cref="LinkState.Connecting"/> 유지다.
/// </para>
/// </summary>
public sealed class TcpGameServerLink : IGameServerLink
{
    private readonly TcpLinkOptions _options;
    private readonly PriorityCommandRing _ring;

    // 기록자는 리시버 태스크 하나뿐이다 (docs/20 §6.1).
    private readonly Channel<GameEvent> _events = Channel.CreateUnbounded<GameEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    private TcpClient? _client;
    private Stream? _stream;
    private LinkState _state = LinkState.Disconnected;

    private long _enqueued;

    /// <summary>링크를 만든다. <b>여기서 I/O 를 하지 않는다</b> — 연결은 <see cref="ConnectAsync"/> 다.</summary>
    public TcpGameServerLink(TcpLinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _ring = new PriorityCommandRing(options.Capacity);
    }

    /// <summary>게임서버 → NPC 서버.</summary>
    public ChannelReader<GameEvent> Events => _events.Reader;

    /// <summary>지금 상태. docs/20 §6.3 의 전이표를 따른다.</summary>
    public LinkState State => _state;

    /// <summary>
    /// 통계. docs/20 §6.4.
    ///
    /// <b>발신·수신 계수는 아직 0 이다</b> — 그 경로가 T6-07·T6-08 이다.
    /// 지금 필드를 미리 두면 <c>TreatWarningsAsErrors</c> 가 "할당되지 않는다" 로 잡는다.
    /// </summary>
    public LinkStats Stats => new(
        _enqueued,
        CommandsFlushed: 0,
        _ring.Dropped,
        EventsReceived: 0,
        EventGapsDetected: 0,
        _ring.Pending);

    /// <summary>거절 사유. 수락됐거나 아직 핸드셰이크 전이면 <see cref="LinkRejectCode.None"/>.</summary>
    public LinkRejectCode RejectCode { get; private set; }

    /// <summary>상태가 바뀔 때마다.</summary>
    public event Action<LinkState>? StateChanged;

    /// <summary>
    /// 접속하고 핸드셰이크한다. docs/20 §5.5.
    /// </summary>
    /// <returns>수락되면 true. 거절이면 false 이고 상태는 <see cref="LinkState.Faulted"/> 다.</returns>
    /// <exception cref="SocketException">
    /// 접속 자체가 실패했다. 상태는 <see cref="LinkState.Connecting"/> 유지 — 재시도 대상이다.
    /// </exception>
    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        SetState(LinkState.Connecting);

        var client = new TcpClient
        {
            // Nagle 을 끈다 (docs/02 §7-4). 10Hz 배치를 40ms 지연시키면 틱 예산이 무의미해진다.
            NoDelay = true,
        };

        await client.ConnectAsync(_options.Host, _options.Port, ct).ConfigureAwait(false);

        _client = client;
        _stream = client.GetStream();

        return await HandshakeAsync(_stream, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 이미 연결된 스트림 위에서 핸드셰이크만 한다.
    ///
    /// <b>소켓을 만들지 않는 이 진입점이 있어야 핸드셰이크를 테스트할 수 있다.</b>
    /// 검증 규칙은 이 프로젝트에서 되돌리기가 가장 비싼 부분이라(잘못 붙으면 원인 찾는 데 하루가 든다)
    /// 소켓 없이도 네 가지 불일치를 각각 재현할 수 있어야 한다.
    /// </summary>
    /// <returns>수락되면 true.</returns>
    public async Task<bool> HandshakeAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        SetState(LinkState.Connecting);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);

        timeout.CancelAfter(_options.HandshakeTimeout);

        (LinkMessageKind kind, byte[] payload) =
            await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);

        if (kind != LinkMessageKind.Hello)
        {
            await RejectAsync(stream, LinkRejectCode.ProtocolVersion, LinkByeCode.ProtocolViolation, ct)
                .ConfigureAwait(false);

            return false;
        }

        WireHello hello = MemoryPackSerializer.Deserialize<WireHello>(payload);
        LinkRejectCode reject = Validate(in hello);

        if (reject != LinkRejectCode.None)
        {
            await RejectAsync(stream, reject, LinkByeCode.HandshakeRejected, ct).ConfigureAwait(false);
            return false;
        }

        await WriteFrameAsync(
            stream, LinkMessageKind.HelloAck, AckPayload(accepted: true, LinkRejectCode.None), ct)
            .ConfigureAwait(false);

        _stream ??= stream;
        RejectCode = LinkRejectCode.None;

        SetState(LinkState.Connected);

        return true;
    }

    /// <summary>
    /// 게임서버가 보낸 <see cref="WireHello"/> 를 검증한다. docs/20 §5.5 의 네 항목.
    ///
    /// <b>우회 옵션이 없다.</b> 하나라도 다르면 거절이다.
    /// </summary>
    public LinkRejectCode Validate(in WireHello hello)
    {
        if (hello.ProtocolVersion != FrameCodec.Version)
        {
            return LinkRejectCode.ProtocolVersion;
        }

        if (hello.TimeScale != _options.TimeScale)
        {
            return LinkRejectCode.TimeScaleMismatch;
        }

        if (hello.MasterData != _options.MasterData)
        {
            return LinkRejectCode.MasterDataMismatch;
        }

        if (hello.Roster != _options.Roster)
        {
            return LinkRejectCode.RosterMismatch;
        }

        // NPC 수는 기본으로 보지 않는다 — 로스터 해시가 이미 그 집합을 담고 있다 (TcpLinkOptions).
        if (_options.StrictNpcCount && hello.NpcCount != _options.NpcCount)
        {
            return LinkRejectCode.RosterMismatch;
        }

        return LinkRejectCode.None;
    }

    /// <summary>명령을 링에 넣는다. 할당 0 (docs/20 §6.2).</summary>
    public void Enqueue(in NpcCommand command)
    {
        _enqueued++;
        _ring.Enqueue(in command);
    }

    /// <summary>[T6-07] 송신 경로. 아직 구현되지 않았다.</summary>
    public ValueTask FlushAsync(CancellationToken ct) =>
        throw new NotSupportedException("TODO: T6-07 — 센더 태스크와 게시 꼬리 배선이 아직이다.");

    /// <summary>소켓을 닫고 상태를 <see cref="LinkState.Disconnected"/> 로 되돌린다.</summary>
    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();

        if (_stream is { } stream)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        _client?.Dispose();
        _client = null;

        SetState(LinkState.Disconnected);
    }

    // ---------------------------------------------------------------- 내부

    private async Task RejectAsync(
        Stream stream, LinkRejectCode reject, LinkByeCode bye, CancellationToken ct)
    {
        RejectCode = reject;

        await WriteFrameAsync(stream, LinkMessageKind.HelloAck, AckPayload(accepted: false, reject), ct)
            .ConfigureAwait(false);

        await WriteFrameAsync(stream, LinkMessageKind.Bye, ByePayload(bye), ct).ConfigureAwait(false);

        // 재시도하지 않는다. 사람이 고쳐야 하는 상태다 (docs/20 §6.3).
        SetState(LinkState.Faulted);
    }

    private byte[] AckPayload(bool accepted, LinkRejectCode reject) =>
        MemoryPackSerializer.Serialize(new WireHelloAck
        {
            ProtocolVersion = FrameCodec.Version,
            TimeScale = _options.TimeScale,
            NpcCount = _options.NpcCount,
            MasterData = _options.MasterData,
            Roster = _options.Roster,
            Accepted = accepted ? (byte)1 : (byte)0,
            RejectCode = (byte)reject,
        });

    private static byte[] ByePayload(LinkByeCode code) =>
        MemoryPackSerializer.Serialize(new WireBye { Code = (byte)code });

    private static async Task WriteFrameAsync(
        Stream stream, LinkMessageKind kind, byte[] payload, CancellationToken ct)
    {
        var writer = new ArrayBufferWriter<byte>(FrameCodec.HeaderSize + payload.Length);

        FrameCodec.WriteHeader(writer, kind, payload.Length);
        writer.Write(payload);

        await stream.WriteAsync(writer.WrittenMemory, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 프레임 하나를 읽는다. <b>핸드셰이크 전용</b>이라 단순 동기 읽기다 —
    /// 상시 수신은 <c>PipeReader</c> 로 간다 (T6-08 · docs/20 §6.1).
    /// </summary>
    private static async Task<(LinkMessageKind Kind, byte[] Payload)> ReadFrameAsync(
        Stream stream, CancellationToken ct)
    {
        var header = new byte[FrameCodec.HeaderSize];

        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        // 버전·길이 상한은 코덱이 본다. 헤더만으로는 페이로드가 안 왔으니 반환값은 false 다 —
        // 여기서 필요한 것은 그 검사가 도는 것이고, 어기면 InvalidDataException 이 나온다.
        var probe = new ReadOnlySequence<byte>(header);

        FrameCodec.TryReadFrame(ref probe, out _, out _);

        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        var kind = (LinkMessageKind)header[4];
        var payload = new byte[length];

        if (length > 0)
        {
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        }

        return (kind, payload);
    }

    private void SetState(LinkState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(state);
    }
}
