using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Threading.Channels;
using MemoryPack;
using Npc.Contracts;
using Npc.Wire;
using Npc.Wire.V2;

namespace Npc.Gateway;

/// <summary>
/// 실제 게임서버 TCP 링크. docs/02 §2·§7 · docs/20 §5·§6.
///
/// <para>
/// <b>구현 완료 (T6-06~T6-09).</b> 연결·핸드셰이크 · 송신 · 수신 · 재접속·하트비트.
/// <see cref="RunAsync"/> 하나가 링크 수명 전체를 돈다.
/// </para>
///
/// <para>
/// <b>스레드는 셋이다</b> (docs/20 §6.1). 틱 루프는 <see cref="Enqueue"/>·<see cref="FlushAsync"/> 만
/// 부르고 소켓을 만지지 않는다 — 커널 송신 버퍼가 차기를 틱 루프에서 기다리면 그 순간 틱이 밀린다.
/// <list type="bullet">
///   <item><b>틱 루프</b> — <see cref="PriorityCommandRing"/>(단독 소유) → <c>BatchQueue</c> 게시</item>
///   <item><b>센더</b> — 배치 → <see cref="WireCommand"/>[] → MemoryPack → 소켓</item>
///   <item><b>리시버</b> — <c>PipeReader</c> → 프레임 → <see cref="GameEvent"/> → 채널(유일한 기록자)</item>
/// </list>
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
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly PriorityCommandRing _ring;

    // 기록자는 리시버 태스크 하나뿐이다 (docs/20 §6.1).
    private readonly Channel<GameEvent> _events = Channel.CreateUnbounded<GameEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    /// <summary>
    /// 틱 루프 → 센더 태스크 배치 인계.
    ///
    /// <b>링을 스레드 너머로 넘기지 않는다.</b> docs/20 §6.1 의 그림은 센더가
    /// <c>publishedTail</c> 까지 링에서 직접 Pop 하지만, <see cref="PriorityCommandRing"/> 은
    /// 삽입과 꺼냄이 <c>_count</c> 를 함께 만져 <b>SPSC 로 안전하지 않다</b> —
    /// 같은 실수를 <c>ReplanQueue</c> 에서 이미 한 번 했다(2026-07-28 결정 16).
    /// 그래서 링은 틱 루프 단독 소유로 두고, 경계를 <b>배치 단위</b>로 옮겼다.
    /// </summary>
    private readonly BatchQueue _outbound;

    /// <summary>와이어 변환 스테이징. 센더 태스크만 만진다.</summary>
    private readonly WireCommand[] _staging;

    /// <summary>
    /// v2 스테이징 (B-02). 확장 슬롯이 협상되면 이쪽으로 나간다.
    ///
    /// <b>두 벌을 들고 있는 이유는 협상이 런타임 값이기 때문이다.</b> 어느 쪽으로 말할지는
    /// 핸드셰이크가 끝나야 정해지는데, 스테이징 배열은 틱마다 만들 수 없다(할당 0).
    /// </summary>
    private readonly WireCommandV2[] _stagingV2;

    private TcpClient? _client;
    private Stream? _stream;
    private Task? _sender;
    private Task? _receiver;
    private LinkState _state = LinkState.Disconnected;

    /// <summary>세션당 nonce 기억 (A-06). 재생 공격을 막는다.</summary>
    private readonly NonceCache _nonces = new();

    private long _enqueued;
    private long _flushed;
    private long _framesSent;
    private long _flushesSkipped;

    // 아래 셋은 리시버 태스크만 쓴다. 읽기는 Interlocked.Read 로 한다.
    private long _eventsReceived;
    private long _eventGaps;
    private long _lastSequence = -1;

    /// <summary>마지막 수신 시각(벽시계 ms). 하트비트 감시가 본다.</summary>
    private long _lastReceivedTicks;

    /// <summary>
    /// 링크를 만든다. <b>여기서 I/O 를 하지 않는다</b> — 연결은 <see cref="ConnectAsync"/> ·
    /// <see cref="RunAsync"/> 다.
    /// </summary>
    /// <param name="options">설정.</param>
    /// <param name="connect">
    /// 연결 생성기. null 이면 <see cref="TcpClient"/> 로 붙는다.
    ///
    /// <b>이 이음매가 있어야 재접속을 테스트할 수 있다.</b> 재접속은 "소켓을 새로 만든다" 가
    /// 본질이라 소켓을 만드는 자리를 밖에서 갈아끼울 수 있어야 하고, 그러지 못하면
    /// 포트·타이밍에 기대는 느리고 흔들리는 테스트가 된다.
    /// </param>
    public TcpGameServerLink(TcpLinkOptions options, Func<CancellationToken, Task<Stream>>? connect = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _connect = connect ?? ConnectSocketAsync;
        _ring = new PriorityCommandRing(options.Capacity);
        _outbound = new BatchQueue(options.OutboundBatches, options.Capacity);
        _staging = new WireCommand[options.Capacity];
        _stagingV2 = new WireCommandV2[options.Capacity];
    }

    /// <summary>게임서버 → NPC 서버.</summary>
    public ChannelReader<GameEvent> Events => _events.Reader;

    /// <summary>지금 상태. docs/20 §6.3 의 전이표를 따른다.</summary>
    public LinkState State => _state;

    /// <summary>통계. docs/20 §6.4 의 여섯 필드를 전부 채운다.</summary>
    public LinkStats Stats => new(
        _enqueued,
        Interlocked.Read(ref _flushed),
        _ring.Dropped,
        Interlocked.Read(ref _eventsReceived),
        Interlocked.Read(ref _eventGaps),
        _ring.Pending);

    /// <summary>보낸 <c>CommandBatch</c> 프레임 수. <b>Flush 횟수와 같아야 한다</b> (N8).</summary>
    public long FramesSent => Interlocked.Read(ref _framesSent);

    /// <summary>
    /// 센더가 밀려 이번 Flush 를 건너뛴 횟수. <b>명령을 버린 것이 아니다</b> —
    /// 링에 남아 다음 Flush 에 실린다. 상시로 오르면 게임서버가 못 따라오는 것이고,
    /// 그때 실제로 버려지는 것은 링의 우선순위 역압이 정한다 (Cosmetic 부터).
    /// </summary>
    public long FlushesSkipped => _flushesSkipped;

    /// <summary>거절 사유. 수락됐거나 아직 핸드셰이크 전이면 <see cref="LinkRejectCode.None"/>.</summary>
    public LinkRejectCode RejectCode { get; private set; }

    /// <summary>
    /// 협상된 와이어 프로토콜 버전 (B-01). 핸드셰이크 전에는 0.
    ///
    /// <b><c>IGameServerLink</c> 에 넣지 않는다</b> — 계약은 <c>Enqueue</c>·<c>FlushAsync</c>·
    /// <c>Events</c> 뿐이고(N1), 협상 결과는 진단용이다. <c>/status</c> 가 여기서 읽는다.
    /// </summary>
    public int NegotiatedVersion { get; private set; }

    /// <summary>협상된 계약 부 버전 (B-01). 낮은 쪽이 이긴다.</summary>
    public ushort NegotiatedContractMinor { get; private set; }

    /// <summary>협상된 기능 비트 (B-01). 양쪽 교집합이다.</summary>
    public ulong NegotiatedFeatures { get; private set; }

    /// <summary>협상 결과를 사람 말로. 로그·<c>/status</c> 가 쓴다.</summary>
    public string NegotiationDetail { get; private set; } = "핸드셰이크 전";

    /// <summary>
    /// 확장 슬롯을 쓸 수 있는가 (B-02).
    ///
    /// <b>둘 다 필요하다.</b> 프로토콜 2 여야 v2 프레임을 쓸 수 있고,
    /// <see cref="LinkFeatures.ExtSlots"/> 여야 상대가 그 배치를 이해한다.
    /// </summary>
    public bool ExtSlotsNegotiated =>
        NegotiatedVersion >= 2 && VersionNegotiation.Has(NegotiatedFeatures, LinkFeatures.ExtSlots);

    /// <summary>
    /// 게임서버가 알려준 게임 시각 (하루의 몇 분째). v1 이면 <c>-1</c> (A-10).
    /// </summary>
    public int StartGameMinuteOfDay { get; private set; } = -1;

    /// <summary>게임서버가 알려준 시작 틱. 핸드셰이크 전에는 0 (A-10).</summary>
    public long StartTick { get; private set; }

    /// <summary>세션 에포크 (G-02). 게임서버가 다시 뜨면 바뀐다.</summary>
    public uint SessionEpoch { get; private set; }

    /// <summary>
    /// 내용 해시가 달라 경고로 수락했는가 (B-04). 구조 해시는 여전히 완전 일치를 요구한다.
    /// </summary>
    public bool ContentHashWarning { get; private set; }

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

        _stream = await _connect(ct).ConfigureAwait(false);

        return await HandshakeAsync(_stream, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <b>링크 수명 전체를 돈다.</b> 접속 → 핸드셰이크 → 송·수신 → 끊기면 재접속. docs/20 §5.6 · §6.3.
    ///
    /// <para>
    /// <b><see cref="LinkState.Faulted"/> 면 즉시 끝낸다.</b> 프로토콜·마스터데이터 불일치는
    /// 사람이 고쳐야 하고, 무한 재시도로 덮으면 로그만 차고 원인이 묻힌다.
    /// 접속 실패(상대가 아직 안 떴다)는 성격이 달라 백오프 재시도한다.
    /// </para>
    ///
    /// <para>
    /// <b>재접속 시 링에 남은 명령을 버린다</b> (docs/20 §5.6). 상관 ID 는 이미 타임아웃으로
    /// 정리됐고, 뒤늦게 도착한 명령은 NPC 를 과거로 되돌린다. 버린 수는 드롭에 계상한다.
    /// </para>
    ///
    /// <para>
    /// <b>수신 시퀀스는 재접속해도 리셋하지 않는다</b> (N6 · docs/20 §5.6). 게임서버가
    /// 재동기화 이벤트를 새 시퀀스로 다시 보내므로 멱등하게 반영된다.
    /// </para>
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        TimeSpan backoff = _options.ReconnectBackoff;

        while (!ct.IsCancellationRequested && _state != LinkState.Faulted)
        {
            Stream? stream = null;

            try
            {
                SetState(LinkState.Connecting);

                stream = await _connect(ct).ConfigureAwait(false);
                _stream = stream;

                if (!await HandshakeAsync(stream, ct).ConfigureAwait(false))
                {
                    return;   // 거절 = Faulted. 재시도하지 않는다
                }

                backoff = _options.ReconnectBackoff;   // 붙었으니 되돌린다

                await PumpUntilBrokenAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // 접속 실패. 상대가 아직 안 떴을 수 있다 — Connecting 유지로 재시도한다.
            }
            finally
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }

                _client?.Dispose();
                _client = null;
                _stream = null;
            }

            if (ct.IsCancellationRequested || _state == LinkState.Faulted)
            {
                return;
            }

            // 끊긴 동안 쌓인 명령은 버린다 (docs/20 §5.6).
            _ring.Clear(countAsDropped: true);
            _outbound.Reset();

            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = backoff * 2 > _options.MaxReconnectBackoff
                ? _options.MaxReconnectBackoff
                : backoff * 2;
        }
    }

    /// <summary>
    /// 한 세션 동안 송·수신·하트비트를 돌린다. 셋 중 하나라도 끝나면 세션이 끝난 것이다.
    /// </summary>
    private async Task PumpUntilBrokenAsync(Stream stream, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Volatile.Write(ref _lastReceivedTicks, Environment.TickCount64);

        Task receiver = ReceiverLoopAsync(stream, session.Token);
        Task sender = SenderLoopAsync(stream, session.Token);
        Task watchdog = HeartbeatLoopAsync(stream, session.Token);

        await Task.WhenAny(receiver, sender, watchdog).ConfigureAwait(false);

        await session.CancelAsync().ConfigureAwait(false);

        // 나머지 둘이 정리될 때까지 기다린다 — 소켓을 닫기 전에 쓰기가 끝나야 한다.
        await Task.WhenAll(
            Swallow(receiver), Swallow(sender), Swallow(watchdog)).ConfigureAwait(false);

        // <b>종료 지시는 저하가 아니다.</b> 여기서 무조건 Degraded 로 보내면
        // 정상 종료가 "링크가 끊겼다" 로 기록되고, docs/20 §6.3 의 전이표와도 어긋난다
        // (임의 → DisposeAsync → Disconnected).
        if (!ct.IsCancellationRequested)
        {
            SetState(LinkState.Degraded);
        }

        static async Task Swallow(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 세션 종료 과정의 예외는 이미 상태로 표현됐다.
            }
        }
    }

    /// <summary>
    /// 하트비트를 보내고 무수신을 감시한다. docs/20 §5.6 — 1초 주기, 3초 무수신이면 끝낸다.
    ///
    /// <b>시계는 <see cref="Environment.TickCount64"/> 다.</b> 이 경로는 게임 시간이 아니라
    /// 벽시계 영역이라 <c>Tick</c> 을 쓸 수 없고, 리플레이 대상도 아니다 (docs/20 §5.6).
    /// </summary>
    private async Task HeartbeatLoopAsync(Stream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_options.HeartbeatInterval, ct).ConfigureAwait(false);

            long silent = Environment.TickCount64 - Volatile.Read(ref _lastReceivedTicks);

            if (silent > (long)_options.HeartbeatTimeout.TotalMilliseconds)
            {
                return;   // 무수신 → 세션 종료 → 재접속
            }

            byte[] beat = MemoryPackSerializer.Serialize(new WireHeartbeat
            {
                Tick = 0,
                Sequence = Interlocked.Read(ref _lastSequence),
            });

            await WriteFrameAsync(stream, LinkMessageKind.Heartbeat, beat, ct).ConfigureAwait(false);
        }
    }

    private async Task<Stream> ConnectSocketAsync(CancellationToken ct)
    {
        var client = new TcpClient
        {
            // Nagle 을 끈다 (docs/02 §7-4). 10Hz 배치를 40ms 지연시키면 틱 예산이 무의미해진다.
            NoDelay = true,
        };

        await client.ConnectAsync(_options.Host, _options.Port, ct).ConfigureAwait(false);

        _client = client;

        return client.GetStream();
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

        (LinkMessageKind kind, byte[] payload, byte frameVersion) =
            await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);

        if (kind != LinkMessageKind.Hello)
        {
            await RejectAsync(stream, LinkRejectCode.ProtocolVersion, LinkByeCode.ProtocolViolation, ct)
                .ConfigureAwait(false);

            return false;
        }

        return frameVersion >= 2
            ? await HandshakeV2Async(stream, payload, ct).ConfigureAwait(false)
            : await HandshakeV1Async(stream, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// v1 핸드셰이크. 협상이 없다 — 네 값이 전부 같아야 한다 (docs/20 §5.5).
    /// </summary>
    private async Task<bool> HandshakeV1Async(Stream stream, byte[] payload, CancellationToken ct)
    {
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

        NegotiatedVersion = 1;
        NegotiatedContractMinor = 0;
        NegotiatedFeatures = 0;
        NegotiationDetail = "protocol 1 (v1 게임서버 — 협상 없음)";
        StartTick = hello.StartTick;
        StartGameMinuteOfDay = -1;
        SessionEpoch = 0;
        ContentHashWarning = false;

        SetState(LinkState.Connected);

        return true;
    }

    /// <summary>
    /// v2 핸드셰이크 (B-01). <b>범위를 받아 교집합의 최댓값을 고른다.</b>
    ///
    /// 구조 해시는 완전 일치를 요구하고 내용 해시는 경고로 수락한다 (B-04) —
    /// 게임서버·NPC 서버가 다른 파이프라인으로 배포되는 상용에서 밸런스 한쪽만 갱신되는 것은
    /// 정상 운영의 일부이지 장애가 아니다.
    /// </summary>
    private async Task<bool> HandshakeV2Async(Stream stream, byte[] payload, CancellationToken ct)
    {
        WireHelloV2 hello = MemoryPackSerializer.Deserialize<WireHelloV2>(payload);

        NegotiationResult negotiation = VersionNegotiation.Negotiate(
            hello.ProtocolVersion,
            hello.MinProtocolVersion,
            hello.ContractMajor,
            hello.ContractMinor,
            hello.Features,
            _options.RequireAuth);

        if (!negotiation.Accepted)
        {
            NegotiationDetail = negotiation.Detail;

            await RejectV2Async(stream, negotiation.Reject, ct).ConfigureAwait(false);
            return false;
        }

        // 인증을 먼저 본다 (A-06). 해시·배속은 "같은 데이터를 보고 있는가" 이고,
        // 인증은 "네가 누구인가" 다 — 신원을 모르는 상대에게 우리 마스터데이터 해시를
        // 되돌려 주는 것부터가 정보 누출이다.
        LinkRejectCode authReject = ValidateAuth(in hello);

        if (authReject != LinkRejectCode.None)
        {
            NegotiationDetail = $"{negotiation.Detail} · 인증 실패";

            await RejectV2Async(stream, authReject, ct).ConfigureAwait(false);
            return false;
        }

        LinkRejectCode reject = ValidateV2(in hello, out bool contentWarning);

        if (reject != LinkRejectCode.None)
        {
            NegotiationDetail = $"{negotiation.Detail} · 거절 {reject}";

            await RejectV2Async(stream, reject, ct).ConfigureAwait(false);
            return false;
        }

        NegotiatedVersion = negotiation.ProtocolVersion;
        NegotiatedContractMinor = negotiation.ContractMinor;
        NegotiatedFeatures = negotiation.Features;
        NegotiationDetail = negotiation.Detail + (contentWarning ? " · 내용 해시 경고" : string.Empty);

        StartTick = hello.StartTick;
        StartGameMinuteOfDay = hello.StartGameMinuteOfDay;
        SessionEpoch = hello.SessionEpoch;
        ContentHashWarning = contentWarning;

        await WriteFrameAsync(
            stream,
            LinkMessageKind.HelloAck,
            AckV2Payload(accepted: true, LinkRejectCode.None, contentWarning, hello.Nonce),
            ct,
            (byte)negotiation.ProtocolVersion).ConfigureAwait(false);

        _stream ??= stream;
        RejectCode = LinkRejectCode.None;

        SetState(LinkState.Connected);

        return true;
    }

    /// <summary>
    /// 인증 태그를 검산한다 (A-06).
    ///
    /// 비밀이 설정되지 않았으면 검사하지 않는다 — v1 게임서버·개발 회차와 붙어야 한다.
    /// <b>반드시 인증하려면 <see cref="TcpLinkOptions.RequireAuth"/> 를 켠다</b>,
    /// 그러면 협상 단계가 <c>Auth</c> 기능 비트부터 요구한다.
    /// </summary>
    public LinkRejectCode ValidateAuth(in WireHelloV2 hello)
    {
        if (_options.Secret.Length == 0)
        {
            return LinkRejectCode.None;
        }

        if (hello.Nonce.IsZero)
        {
            return LinkRejectCode.AuthFailed;
        }

        // 태그가 맞아도 같은 nonce 를 두 번 받으면 거절이다. 태그는 "그때 이 비밀을 아는
        // 누군가가 만들었다" 만 증명하지 "지금 만들었다" 를 증명하지 않는다.
        if (!_nonces.TryRemember(in hello.Nonce))
        {
            return LinkRejectCode.AuthFailed;
        }

        WireHash expected = LinkAuth.ComputeHello(_options.Secret, in hello);

        return LinkAuth.Verify(in expected, in hello.Auth)
            ? LinkRejectCode.None
            : LinkRejectCode.AuthFailed;
    }

    /// <summary>
    /// v2 핸드셰이크 값 검증 (B-01 · B-04).
    /// </summary>
    /// <param name="hello">게임서버가 보낸 것.</param>
    /// <param name="contentWarning">내용 해시가 달라 경고로 수락했으면 true.</param>
    public LinkRejectCode ValidateV2(in WireHelloV2 hello, out bool contentWarning)
    {
        contentWarning = false;

        if (hello.TimeScale != _options.TimeScale)
        {
            return LinkRejectCode.TimeScaleMismatch;
        }

        if (_options.MasterDataStructural == default)
        {
            // 분할 해시를 설정하지 않은 호출부는 <b>v1 규칙으로 돈다</b> — 전체 내용 해시가
            // 완전히 같아야 한다.
            //
            // 구조 해시 자리에 내용 해시를 넣어 비교하면 언제나 어긋나고, 반대로 검사를
            // 건너뛰면 보장이 조용히 사라진다. 둘 다 나쁘다. <b>설정하지 않았으면 예전처럼 엄격하다</b>
            // 가 유일하게 안전한 기본값이다.
            if (hello.MasterDataContent != _options.MasterData)
            {
                return LinkRejectCode.MasterDataMismatch;
            }
        }
        else if (hello.MasterDataStructural != _options.MasterDataStructural)
        {
            // 구조 해시는 완전 일치다. 게임서버가 POI 좌표·id 를 다르게 알면 NPC 가 엉뚱한 곳으로 간다.
            return LinkRejectCode.MasterDataMismatch;
        }

        if (hello.Roster != _options.Roster)
        {
            return LinkRejectCode.RosterMismatch;
        }

        if (_options.StrictNpcCount && hello.NpcCount != _options.NpcCount)
        {
            return LinkRejectCode.RosterMismatch;
        }

        // 내용 해시는 경고다. 한쪽이 밸런스를 먼저 받은 상태는 정상 운영이다 (B-04).
        WireHash content = _options.MasterDataContent == default
            ? _options.MasterData
            : _options.MasterDataContent;

        contentWarning = hello.MasterDataContent != content;

        return LinkRejectCode.None;
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

    /// <summary>
    /// 이번 배치를 <b>게시</b>한다. docs/20 §6.1·§6.2 · N8.
    ///
    /// <para>
    /// <b>소켓을 만지지 않는다.</b> 링을 비워 배치 슬롯 하나에 옮기고 게시 첨자를
    /// <see cref="Volatile"/> 로 올린 뒤 곧바로 돌아온다 — 실제 직렬화와 송신은 센더 태스크가 한다.
    /// 틱 루프에서 커널 송신 버퍼가 차기를 기다리면 그 순간 틱이 밀린다 (docs/20 §3.3).
    /// </para>
    ///
    /// <para>
    /// <b>한 번의 Flush = 한 개의 <c>CommandBatch</c> 프레임이다</b> (N8).
    /// 명령이 0건이어도 배치를 게시한다 — 배치 경계가 와이어에 그대로 보존되어야 하고,
    /// 빈 프레임 8바이트는 그 대가로 싸다.
    /// </para>
    ///
    /// <para>
    /// <b>할당 0.</b> 배치 슬롯은 기동 시 전부 잡아 둔다.
    /// </para>
    /// </summary>
    public ValueTask FlushAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_outbound.TryBeginWrite(out NpcCommand[]? slot))
        {
            // 센더가 밀렸다. <b>명령을 링에 그대로 둔다</b> — 다음 Flush 가 싣는다.
            //
            // 배치를 통째로 버리지 않는 것이 중요하다. 그러면 링의 우선순위 역압을 우회해
            // Critical 까지 같이 버리게 되고, 그건 docs/02 §1 이 금지한 것이다.
            // 링이 넘치면 그때 Cosmetic 부터 버리는 것은 PriorityCommandRing 이 한다.
            _flushesSkipped++;

            return ValueTask.CompletedTask;
        }

        int count = 0;

        while (count < slot.Length && _ring.TryDequeue(out NpcCommand command))
        {
            slot[count++] = command;
        }

        // 링에 남은 것이 있으면 이 배치에 못 실은 것이다. 다음 Flush 로 넘어간다.
        _outbound.CommitWrite(count);

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 리시버 태스크를 띄운다. <b>이 태스크가 채널의 유일한 기록자다</b> (docs/20 §6.1).
    ///
    /// <c>PipeReader</c> → 프레임 분해 → <see cref="WireEvent"/>[] → <see cref="GameEvent"/> → 채널.
    /// 틱 루프는 <see cref="Events"/> 에서 읽기만 한다.
    /// </summary>
    public Task StartReceiverAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _receiver = Task.Run(() => ReceiverLoopAsync(stream, ct), CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task ReceiverLoopAsync(Stream stream, CancellationToken ct)
    {
        PipeReader reader = PipeReader.Create(stream);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (FrameCodec.TryReadFrame(
                    ref buffer, out LinkMessageKind kind, out ReadOnlySequence<byte> payload,
                    out byte version))
                {
                    // 무엇이든 받았으면 살아 있는 것이다 — 하트비트도 포함이다.
                    Volatile.Write(ref _lastReceivedTicks, Environment.TickCount64);

                    Deliver(kind, payload, version);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    // EOF. 상대가 끊었다 → Degraded (docs/20 §6.3). 재접속은 T6-09 다.
                    SetState(LinkState.Degraded);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 종료 지시다. 상태를 건드리지 않는다.
        }
        catch (Exception)
        {
            // 프레임이 깨졌거나(InvalidDataException) 소켓이 죽었다.
            SetState(LinkState.Degraded);
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 프레임 하나를 처리한다. 리시버 태스크 전용.
    ///
    /// <b>배치의 배치를 프레임의 <c>Ver</c> 로 고른다</b> (B-02). 협상 결과가 아니라
    /// 프레임이 말하는 버전을 믿는다 — 협상 직후의 경계에서 두 버전이 섞여 도착할 수 있고,
    /// 그때 협상값으로 읽으면 배치가 어긋나 스트림 전체가 쓰레기가 된다.
    /// </summary>
    private void Deliver(LinkMessageKind kind, ReadOnlySequence<byte> payload, byte version)
    {
        if (kind != LinkMessageKind.EventBatch)
        {
            // Heartbeat·Bye 는 T6-09 가 다룬다. 모르는 종류는 조용히 버린다 —
            // 프레임 경계는 이미 맞았으므로 스트림은 멀쩡하다.
            return;
        }

        byte[] bytes = payload.ToArray();

        GameEvent[]? decoded = version >= 2
            ? Decode(MemoryPackSerializer.Deserialize<WireEventV2[]>(bytes))
            : Decode(MemoryPackSerializer.Deserialize<WireEvent[]>(bytes));

        if (decoded is null)
        {
            return;
        }

        foreach (GameEvent ev in decoded)
        {
            // N6 — 시퀀스 불연속을 센다. 건너뛴 개수만큼 더한다.
            //
            // 게임서버 프로세스가 사는 동안 시퀀스는 순증하고 재접속해도 리셋하지 않는다
            // (docs/20 §5.6). 리셋되면 EventApplier 가 이후 이벤트를 전부 중복으로 버리므로
            // 여기서 세는 갭이 그 사고의 첫 신호다.
            if (_lastSequence >= 0 && ev.Sequence > _lastSequence + 1)
            {
                _eventGaps += ev.Sequence - _lastSequence - 1;
            }

            if (ev.Sequence > _lastSequence)
            {
                _lastSequence = ev.Sequence;
            }

            if (_events.Writer.TryWrite(ev))
            {
                _eventsReceived++;
            }
        }
    }

    /// <summary>v1 이벤트 배치를 도메인으로. 확장 슬롯은 전부 0 이 된다.</summary>
    private static GameEvent[]? Decode(WireEvent[]? wire)
    {
        if (wire is null)
        {
            return null;
        }

        var events = new GameEvent[wire.Length];

        for (int i = 0; i < wire.Length; i++)
        {
            events[i] = wire[i].To();
        }

        return events;
    }

    /// <summary>v2 이벤트 배치를 도메인으로 (B-02).</summary>
    private static GameEvent[]? Decode(WireEventV2[]? wire)
    {
        if (wire is null)
        {
            return null;
        }

        var events = new GameEvent[wire.Length];

        for (int i = 0; i < wire.Length; i++)
        {
            events[i] = wire[i].To();
        }

        return events;
    }

    /// <summary>
    /// 센더 태스크를 띄운다. <b>틱 루프와 다른 스레드다</b> (docs/20 §6.1).
    /// 게시된 배치가 없으면 1ms 쉰다 — 10Hz 배치에 1ms 폴링은 무시할 수 있고,
    /// <c>SemaphoreSlim</c> 을 틱 루프 쪽에서 건드리지 않아도 된다 (CLAUDE.md §2.1).
    /// </summary>
    public Task StartSenderAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        _sender = Task.Run(() => SenderLoopAsync(stream, ct), CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task SenderLoopAsync(Stream stream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!_outbound.TryBeginRead(out NpcCommand[]? batch, out int count))
            {
                try
                {
                    await Task.Delay(1, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            // B-02 — 확장 슬롯이 협상됐으면 v2 배치로 나간다. 아니면 v1 그대로이고
            // Instance·Faction·ExtA·ExtB 는 조용히 버려진다(그것이 v1 의 의미다).
            bool ext = ExtSlotsNegotiated;
            byte[] payload;

            if (ext)
            {
                for (int i = 0; i < count; i++)
                {
                    _stagingV2[i] = WireCommandV2.From(in batch[i]);
                }

                payload = MemoryPackSerializer.Serialize(_stagingV2.AsSpan(0, count).ToArray());
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    _staging[i] = WireCommand.From(in batch[i]);
                }

                payload = MemoryPackSerializer.Serialize(_staging.AsSpan(0, count).ToArray());
            }

            try
            {
                await WriteFrameAsync(
                    stream, LinkMessageKind.CommandBatch, payload, ct, ext ? (byte)2 : FrameCodec.Version)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // 송신 실패 → Degraded. 재접속은 T6-09 다.
                SetState(LinkState.Degraded);
                return;
            }

            _flushed += count;
            _framesSent++;

            _outbound.CommitRead();
        }
    }

    /// <summary>
    /// 정상 종료를 알린다 (A-02). <c>Connected</c> 일 때만 실제로 보낸다.
    ///
    /// <b>보내지 않으면 게임서버는 우리가 왜 사라졌는지 모른다.</b> 하트비트 타임아웃으로만
    /// 알게 되므로 3초 동안 "죽은 건지 느린 건지" 를 구별하지 못한다.
    ///
    /// 실패해도 던지지 않는다 — 이미 닫힌 소켓에 쓰는 것은 종료 경로에서 정상적인 사건이다.
    /// </summary>
    /// <returns>실제로 보냈으면 true.</returns>
    public async Task<bool> SendByeAsync(LinkByeCode code, TimeSpan timeout, CancellationToken ct)
    {
        if (_state != LinkState.Connected || _stream is not { } stream)
        {
            return false;
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);

            deadline.CancelAfter(timeout);

            await WriteFrameAsync(stream, LinkMessageKind.Bye, ByePayload(code), deadline.Token)
                .ConfigureAwait(false);

            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException
                                       or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            // 이미 닫힌 스트림에 쓰는 것은 종료 경로에서 정상적인 사건이다.
            // InvalidOperationException 은 파이프가 완료된 경우다(인메모리 이중 스트림).
            return false;
        }
    }

    /// <summary>소켓을 닫고 상태를 <see cref="LinkState.Disconnected"/> 로 되돌린다.</summary>
    public async ValueTask DisposeAsync()
    {
        // 정상 종료를 먼저 알린다 (A-02). 상태가 Connected 가 아니면 아무것도 하지 않는다.
        await SendByeAsync(LinkByeCode.Shutdown, TimeSpan.FromSeconds(1), CancellationToken.None)
            .ConfigureAwait(false);

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

    private byte[] AckV2Payload(
        bool accepted, LinkRejectCode reject, bool contentWarning, WireNonce nonce = default)
    {
        var ack = new WireHelloAckV2
        {
            ProtocolVersion = NegotiatedVersion == 0 ? FrameCodec.MaxVersion : NegotiatedVersion,
            ContractMajor = ContractVersion.Major,
            ContractMinor = NegotiatedContractMinor,
            Features = NegotiatedFeatures,
            TimeScale = _options.TimeScale,
            NpcCount = _options.NpcCount,
            MasterDataStructural = _options.MasterDataStructural == default
                ? _options.MasterData
                : _options.MasterDataStructural,
            MasterDataContent = _options.MasterDataContent == default
                ? _options.MasterData
                : _options.MasterDataContent,
            Roster = _options.Roster,
            Accepted = accepted ? (byte)1 : (byte)0,
            RejectCode = (byte)reject,
            ContentHashWarning = contentWarning ? (byte)1 : (byte)0,
        };

        // 상호 인증 (A-06). 게임서버가 낸 nonce 를 되돌려 서명해야 "지금 이 세션에 대한 응답"
        // 임이 증명된다 — 그러지 않으면 예전 응답을 그대로 재생할 수 있다.
        if (_options.Secret.Length > 0 && !nonce.IsZero)
        {
            ack.Auth = LinkAuth.ComputeAck(_options.Secret, in ack, in nonce);
        }

        return MemoryPackSerializer.Serialize(ack);
    }

    /// <summary>v2 거절. 응답도 v2 배치로 보낸다 — 상대가 v2 로 말을 걸었기 때문이다.</summary>
    private async Task RejectV2Async(Stream stream, LinkRejectCode reject, CancellationToken ct)
    {
        RejectCode = reject;

        // 거절에는 인증 태그를 싣지 않는다. 상대의 신원을 모르는데 우리 비밀로 서명하면
        // 그것이 곧 오라클이 된다.
        await WriteFrameAsync(
            stream,
            LinkMessageKind.HelloAck,
            AckV2Payload(accepted: false, reject, contentWarning: false),
            ct,
            FrameCodec.MaxVersion).ConfigureAwait(false);

        await WriteFrameAsync(
            stream, LinkMessageKind.Bye, ByePayload(LinkByeCode.HandshakeRejected), ct)
            .ConfigureAwait(false);

        // 재시도하지 않는다. 사람이 고쳐야 하는 상태다 (docs/20 §6.3).
        SetState(LinkState.Faulted);
    }

    private static byte[] ByePayload(LinkByeCode code) =>
        MemoryPackSerializer.Serialize(new WireBye { Code = (byte)code });

    private static Task WriteFrameAsync(
        Stream stream, LinkMessageKind kind, byte[] payload, CancellationToken ct) =>
        WriteFrameAsync(stream, kind, payload, ct, FrameCodec.Version);

    private static async Task WriteFrameAsync(
        Stream stream, LinkMessageKind kind, byte[] payload, CancellationToken ct, byte version)
    {
        var writer = new ArrayBufferWriter<byte>(FrameCodec.HeaderSize + payload.Length);

        FrameCodec.WriteHeader(writer, kind, payload.Length, version);
        writer.Write(payload);

        await stream.WriteAsync(writer.WrittenMemory, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 프레임 하나를 읽는다. <b>핸드셰이크 전용</b>이라 단순 동기 읽기다 —
    /// 상시 수신은 <c>PipeReader</c> 로 간다 (T6-08 · docs/20 §6.1).
    /// </summary>
    private static async Task<(LinkMessageKind Kind, byte[] Payload, byte Version)> ReadFrameAsync(
        Stream stream, CancellationToken ct)
    {
        var header = new byte[FrameCodec.HeaderSize];

        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        // 버전·길이 상한은 코덱이 본다. 헤더만으로는 페이로드가 안 왔으니 반환값은 false 다 —
        // 여기서 필요한 것은 그 검사가 도는 것이고, 어기면 InvalidDataException 이 나온다.
        var probe = new ReadOnlySequence<byte>(header);

        FrameCodec.TryReadFrame(ref probe, out _, out _, out byte version);

        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        var kind = (LinkMessageKind)header[4];
        var payload = new byte[length];

        if (length > 0)
        {
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        }

        return (kind, payload, version);
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

    /// <summary>
    /// 틱 루프(생산자 하나) → 센더 태스크(소비자 하나) 배치 인계. <b>SPSC 다.</b>
    ///
    /// <para>
    /// 슬롯마다 명령 배열을 <b>기동 시 전부 잡아 둔다.</b> 그래서 <c>FlushAsync</c> 경로에
    /// 할당이 없다 (docs/20 §6.2) — 하는 일은 링에서 배치로 복사하고 꼬리를 올리는 것뿐이다.
    /// </para>
    ///
    /// <para>
    /// <b>첨자 소유가 갈려 있다.</b> 생산자는 <c>_tail</c> 만, 소비자는 <c>_head</c> 만 쓴다.
    /// 서로의 것은 <see cref="Volatile"/> 로 읽기만 한다 — 이것이 락 없이 안전한 근거이고,
    /// <c>PriorityCommandRing</c> 을 그대로 넘길 수 없었던 이유이기도 하다(그쪽은 <c>_count</c> 를 공유한다).
    /// </para>
    /// </summary>
    private sealed class BatchQueue
    {
        private readonly NpcCommand[][] _slots;
        private readonly int[] _counts;

        private long _tail;   // 생산자 전용
        private long _head;   // 소비자 전용

        public BatchQueue(int slots, int perSlot)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perSlot);

            _slots = new NpcCommand[slots][];
            _counts = new int[slots];

            for (int i = 0; i < slots; i++)
            {
                _slots[i] = new NpcCommand[perSlot];
            }
        }

        /// <summary><b>생산자.</b> 쓸 슬롯을 잡는다. 큐가 차 있으면 false.</summary>
        public bool TryBeginWrite(out NpcCommand[] slot)
        {
            if (_tail - Volatile.Read(ref _head) >= _slots.Length)
            {
                slot = [];
                return false;
            }

            slot = _slots[(int)(_tail % _slots.Length)];
            return true;
        }

        /// <summary><b>생산자.</b> 쓴 건수를 확정하고 게시한다.</summary>
        public void CommitWrite(int count)
        {
            _counts[(int)(_tail % _slots.Length)] = count;

            // 건수 쓰기가 먼저 보이고 그 다음 꼬리가 보여야 한다.
            Volatile.Write(ref _tail, _tail + 1);
        }

        /// <summary><b>소비자.</b> 읽을 배치를 잡는다. 게시된 것이 없으면 false.</summary>
        public bool TryBeginRead(out NpcCommand[] batch, out int count)
        {
            if (_head >= Volatile.Read(ref _tail))
            {
                batch = [];
                count = 0;
                return false;
            }

            int index = (int)(_head % _slots.Length);

            batch = _slots[index];
            count = _counts[index];

            return true;
        }

        /// <summary><b>소비자.</b> 다 썼다고 알린다. 그 슬롯이 생산자에게 돌아간다.</summary>
        public void CommitRead() => Volatile.Write(ref _head, _head + 1);

        /// <summary>
        /// 전부 버린다. <b>세션이 끝난 뒤에만 부른다</b> — 그때는 센더가 이미 멈춰 있어
        /// 생산자·소비자가 동시에 만지지 않는다.
        /// </summary>
        public void Reset()
        {
            Volatile.Write(ref _head, 0);
            Volatile.Write(ref _tail, 0);
            Array.Clear(_counts);
        }
    }
}
