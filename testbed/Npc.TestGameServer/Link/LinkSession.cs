using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Threading.Channels;
using MemoryPack;
using Npc.Contracts;
using Npc.MasterData;
using Npc.Sim;
using Npc.Wire;
using Npc.Wire.V2;

namespace Npc.TestGameServer.Link;

/// <summary>
/// NPC 서버 하나와의 링크 세션. docs/20 §5.5 · §7.1.
///
/// <para>
/// <b>방향에 주의한다.</b> 핸드셰이크를 <b>시작하는 쪽은 게임서버</b>다 —
/// <see cref="WireHello"/> 를 보내고 <see cref="WireHelloAck"/> 를 받는다.
/// 검증(버전·타임스케일·마스터데이터·로스터)은 NPC 서버가 하고, 우리는 그 판정을 받는다.
/// 접속은 NPC 서버가 걸어 오지만 핸드셰이크의 첫 말은 우리가 한다.
/// </para>
///
/// <para>
/// <b>스레드 계약이 셋으로 갈린다.</b> 이 구분을 지우면 <see cref="SimWorld"/> 의 이벤트 채널이
/// 깨진다 — 그 채널은 <c>SingleWriter = true</c> 라 기록자가 하나여야 한다.
/// <list type="bullet">
///   <item><see cref="HandshakeAsync"/> — <b>accept 태스크</b>에서 부른다. 월드를 만지지 않는다.</item>
///   <item><see cref="ResyncAsync"/> — <b>틱 스레드</b>에서 부른다. 월드에 이벤트를 낸다.</item>
///   <item>수신 루프 — <b>리시버 태스크</b>다. 월드에 직접 쓰지 않고
///     <see cref="CommandInbox"/> 에만 넣는다. 그 링을 비우는 것은 틱 스레드다.</item>
/// </list>
/// </para>
/// </summary>
public sealed class LinkSession : IAsyncDisposable
{
    /// <summary>재동기화 <c>EventBatch</c> 한 프레임에 담는 이벤트 상한. docs/20 §5.5.</summary>
    public const int MaxEventsPerFrame = 256;

    /// <summary>
    /// 틱 9단계가 한 프레임에 싣는 이벤트 상한. docs/20 §7.2.
    ///
    /// <b>넘치면 버리는 것이 아니라 다음 틱으로 넘긴다.</b> 이벤트는 명령과 달리 유실을
    /// 가정하지 않는다 — 하나가 사라지면 NPC 서버의 상관 ID 가 영원히 안 닫힌다.
    /// 상한이 있는 이유는 프레임 1MiB 제한(docs/20 §5.1)이다: 1,024 × 64B = 64KiB 로 여유가 크다.
    /// </summary>
    public const int MaxEventsPerTick = 1_024;

    /// <summary>하트비트 주기(틱). 10Hz 기준 1초다. docs/20 §5.6.</summary>
    public const int HeartbeatTicks = GameWorld.TickRate;

    private readonly Stream _stream;
    private readonly GameWorld _world;
    private readonly MasterDataSet _data;
    private readonly GameServerOptions _options;

    /// <summary>
    /// 와이어 변환 스테이징. 프레임을 만들 때만 쓴다.
    ///
    /// <b>둘 중 큰 상한으로 잡는다.</b> 재동기화는 256 씩 쪼개고 틱 송신은 1,024 까지 싣는데,
    /// 둘 다 틱 스레드라 배열 하나를 나눠 써도 겹치지 않는다.
    /// </summary>
    private readonly WireEvent[] _staging = new WireEvent[MaxEventsPerTick];

    /// <summary>
    /// 세션이 살아 있는가. 0 이면 죽은 것이다.
    ///
    /// <b>accept 태스크·리시버 태스크·틱 스레드가 함께 본다.</b> 리시버가 EOF 를 만나면
    /// 여기를 내려야 리스너가 자리를 비우고 재접속을 받는다 (docs/20 §5.6).
    /// </summary>
    private int _active = 1;

    /// <summary>마지막으로 무엇이든 받은 벽시계 시각(ms). 하트비트 감시가 본다.</summary>
    private long _lastReceivedMillis = Environment.TickCount64;

    private Task? _receiver;

    /// <summary>
    /// 채널에서 꺼낸 이벤트 총수 — 보낸 것 + 재동기화에서 버린 것.
    ///
    /// <b><c>ChannelReader.Count</c> 를 쓰지 않는 이유.</b> <see cref="SimWorld"/> 의 채널은
    /// <c>SingleReader = true</c> 라 <c>SingleConsumerUnboundedChannel</c> 로 만들어지고,
    /// 그 구현은 <c>Count</c> 를 지원하지 않는다(<c>NotSupportedException</c>).
    /// 세션이 그 채널의 유일한 독자이므로 발행 수에서 꺼낸 수를 빼면 정확히 같은 값이 나온다.
    /// </summary>
    private long _drained;

    /// <summary>세션을 만든다. 소켓은 <see cref="LinkListener"/> 가 이미 열어 뒀다.</summary>
    public LinkSession(Stream stream, GameWorld world, MasterDataSet data, GameServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);

        _stream = stream;
        _world = world;
        _data = data;
        _options = options;
    }

    /// <summary>세션이 살아 있는가. 리스너가 두 번째 접속을 거절할지 이 값으로 가른다.</summary>
    public bool IsActive => Volatile.Read(ref _active) != 0;

    /// <summary>핸드셰이크가 수락됐는가.</summary>
    public bool IsAccepted { get; private set; }

    /// <summary>
    /// NPC 서버가 보낸 거절 사유. 수락됐으면 <see cref="LinkRejectCode.None"/>.
    ///
    /// <b>우회하지 않는다.</b> 마스터데이터가 다른 두 프로세스를 붙이면 POI code 가 어긋나
    /// NPC 가 엉뚱한 곳으로 가고, 원인을 찾는 데 하루가 든다 (docs/20 §5.5).
    /// </summary>
    public LinkRejectCode RejectCode { get; private set; }

    /// <summary>아직 재동기화를 보내지 않았는가. 틱 루프가 이 값을 보고 <see cref="ResyncAsync"/> 를 부른다.</summary>
    public bool NeedsResync { get; private set; } = true;

    /// <summary>보낸 <c>NpcSpawned</c> 수. 재접속하면 다시 로스터 수만큼 늘어난다.</summary>
    public long SpawnsSent { get; private set; }

    /// <summary>보낸 이벤트 수.</summary>
    public long EventsSent { get; private set; }

    /// <summary>보낸 <c>EventBatch</c> 프레임 수.</summary>
    public long FramesSent { get; private set; }

    /// <summary>
    /// 받은 명령 수. <b>링이 차서 버린 것도 센다</b> — 이 값과
    /// <c>GameWorld.Inbox.Accepted</c> 의 차이가 곧 유실분이다.
    /// </summary>
    public long CommandsReceived { get; private set; }

    /// <summary>받은 <c>CommandBatch</c> 프레임 수. NPC 서버의 <c>FlushAsync</c> 횟수와 같아야 한다 (N8).</summary>
    public long CommandFramesReceived { get; private set; }

    /// <summary>
    /// 상한에 걸려 다음 틱으로 넘긴 이벤트 수의 누계.
    ///
    /// <b>유실이 아니다.</b> 채널에 그대로 남아 다음 <see cref="FlushEventsAsync"/> 에 실린다.
    /// 계속 밀리면 같은 이벤트가 여러 번 세어지는데, 그것이 이 값의 쓸모다 — 적체 압력을 잰다.
    /// </summary>
    public long EventsDeferred { get; private set; }

    /// <summary>보낸 하트비트 수.</summary>
    public long HeartbeatsSent { get; private set; }

    /// <summary>
    /// 세션 밖에서 배수된 이벤트 수. 링크가 없던 구간에 <c>GameServer</c> 가 버린 분이다.
    ///
    /// <b>이 값을 안 빼면 <see cref="EventsPending"/> 이 그만큼 부풀어</b>
    /// <see cref="EventsDeferred"/> 가 매 틱 헛되이 늘어난다 — 적체가 없는데 적체 압력이
    /// 보이는 것이 가장 나쁜 종류의 계기다. <c>GameServer</c> 가 세션을 붙일 때 한 번 넣는다.
    /// </summary>
    public long ExternallyDrained { get; set; }

    /// <summary>아직 채널에 남아 다음 틱을 기다리는 이벤트 수.</summary>
    public long EventsPending => _world.World.EventsEmitted - _drained - ExternallyDrained;

    /// <summary>마지막 수신 이후 지난 벽시계 시간(ms). 하트비트 감시가 본다 (docs/20 §5.6).</summary>
    public long SilentMillis => Environment.TickCount64 - Volatile.Read(ref _lastReceivedMillis);

    /// <summary>수신 루프. 세션이 끝나면 완료된다. 안 띄웠으면 이미 완료다.</summary>
    public Task Receiver => _receiver ?? Task.CompletedTask;

    /// <summary>
    /// 채널에서 꺼낸 이벤트를 하나씩 알린다. <see cref="World.MirrorLog"/> 가 여기 붙는다
    /// (docs/20 §7.4 — 로그 패널의 원천).
    ///
    /// <para>
    /// <b>세션이 유일한 독자라서 여기가 유일한 자리다.</b> <c>SimWorld.Events</c> 는
    /// <c>SingleReader</c> 채널이라 미러가 따로 읽을 수 없다 — 읽으면 그만큼 링크로 안 나간다.
    /// </para>
    /// </summary>
    public EventSink? EventObserver { get; set; }

    /// <summary>
    /// 핸드셰이크. <b>accept 태스크에서 부른다.</b> docs/20 §5.5.
    ///
    /// <c>Hello</c> 송신 → <c>HelloAck</c> 수신 → 수락 여부 판정.
    /// 거절이면 <c>Bye(HandshakeRejected)</c> 를 보내고 세션을 닫는다.
    /// </summary>
    /// <returns>수락되면 true.</returns>
    public async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        // v2 로 말을 건다 (B-01). 범위 [1, 2] 를 보내므로 v1 NPC 서버와도 붙는다 —
        // 상대가 v1 이면 v1 배치의 HelloAck 가 돌아오고, 그것을 프레임 버전으로 가른다.
        byte[] hello = _options.ProtocolVersion >= 2
            ? MemoryPackSerializer.Serialize(new WireHelloV2
            {
                ProtocolVersion = FrameCodec.MaxVersion,
                MinProtocolVersion = FrameCodec.MinVersion,
                ContractMajor = ContractVersion.Major,
                ContractMinor = ContractVersion.Minor,
                Features = _options.Features,
                TickRate = GameWorld.TickRate,
                TimeScale = _options.TimeScale,
                NpcCount = _world.Roster.Count,
                StartTick = _world.Now.Value,
                StartGameMinuteOfDay = (ushort)_world.GameMinuteOfDay,
                ShardId = 0,
                ZoneMask = 0,
                SessionEpoch = _options.SessionEpoch,
                MasterDataStructural = WireHash.FromHex(_data.StructuralHash),
                MasterDataContent = WireHash.FromHex(_data.ContentHash),
                Roster = WireHash.FromHex(_world.Roster.Hash),
            })
            : MemoryPackSerializer.Serialize(new WireHello
            {
                ProtocolVersion = FrameCodec.Version,
                TickRate = GameWorld.TickRate,
                TimeScale = _options.TimeScale,
                NpcCount = _world.Roster.Count,
                StartTick = _world.Now.Value,
                MasterData = WireHash.FromHex(_data.ContentHash),
                Roster = WireHash.FromHex(_world.Roster.Hash),
            });

        byte helloVersion = _options.ProtocolVersion >= 2 ? FrameCodec.MaxVersion : FrameCodec.Version;

        await WriteFrameAsync(_stream, LinkMessageKind.Hello, hello, ct, helloVersion).ConfigureAwait(false);

        (LinkMessageKind kind, byte[] payload, byte ackVersion) =
            await ReadFrameWithVersionAsync(_stream, ct).ConfigureAwait(false);

        if (kind != LinkMessageKind.HelloAck)
        {
            // 첫 말이 HelloAck 이 아니면 상대가 이 프로토콜을 쓰지 않는 것이다.
            RejectCode = LinkRejectCode.ProtocolVersion;
            await CloseAsync(LinkByeCode.ProtocolViolation, ct).ConfigureAwait(false);

            return false;
        }

        byte accepted;
        byte rejectCode;

        if (ackVersion >= 2)
        {
            WireHelloAckV2 ack = MemoryPackSerializer.Deserialize<WireHelloAckV2>(payload);

            accepted = ack.Accepted;
            rejectCode = ack.RejectCode;
            NegotiatedVersion = ack.ProtocolVersion;
            NegotiatedFeatures = ack.Features;
            ContentHashWarning = ack.ContentHashWarning == 1;
        }
        else
        {
            WireHelloAck ack = MemoryPackSerializer.Deserialize<WireHelloAck>(payload);

            accepted = ack.Accepted;
            rejectCode = ack.RejectCode;
            NegotiatedVersion = ack.ProtocolVersion;
            NegotiatedFeatures = 0;
            ContentHashWarning = false;
        }

        if (accepted != 1)
        {
            RejectCode = (LinkRejectCode)rejectCode;
            await CloseAsync(LinkByeCode.HandshakeRejected, ct).ConfigureAwait(false);

            return false;
        }

        RejectCode = LinkRejectCode.None;
        IsAccepted = true;

        // 수락된 순간부터 명령이 올 수 있다. 리시버를 여기서 띄우는 이유는 의존 방향이다 —
        // 리스너가 띄우게 하면 "핸드셰이크가 끝났다" 와 "수신을 시작했다" 사이에 틈이 생기고,
        // 그 틈에 도착한 CommandBatch 는 커널 버퍼에 남아 있다가 순서만 늦게 반영된다.
        StartReceiver(ct);

        return true;
    }

    /// <summary>
    /// 수신 루프를 띄운다. <b>이 태스크가 <see cref="CommandInbox"/> 의 유일한 생산자다</b>
    /// (docs/20 §7.1 — SPSC). 두 번 불러도 하나만 돈다.
    /// </summary>
    public void StartReceiver(CancellationToken ct) =>
        _receiver ??= Task.Run(() => ReceiveLoopAsync(ct), CancellationToken.None);

    /// <summary>
    /// <c>PipeReader</c> → 프레임 → <see cref="CommandInbox"/>. docs/20 §7.2 의 1단계 재료를 만든다.
    ///
    /// <para>
    /// <b>여기서 <c>SimWorld</c> 를 만지지 않는다.</b> 명령을 적용하는 것은 틱 스레드다 —
    /// 소켓 태스크가 월드를 직접 고치면 틱 중간에 상태가 바뀌어 같은 시나리오가 매번 다르게 흐른다.
    /// </para>
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        PipeReader reader = PipeReader.Create(_stream);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadResult result = await reader.ReadAsync(ct).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;

                while (FrameCodec.TryReadFrame(
                    ref buffer, out LinkMessageKind kind, out ReadOnlySequence<byte> payload))
                {
                    // 무엇이든 받았으면 살아 있는 것이다 — 하트비트도 포함이다.
                    Volatile.Write(ref _lastReceivedMillis, Environment.TickCount64);

                    Deliver(kind, payload);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;   // EOF. NPC 서버가 끊었다
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 종료 지시다.
        }
        catch (Exception)
        {
            // 프레임이 깨졌거나(InvalidDataException) 소켓이 죽었다.
            // 게임서버는 계속 돈다 — 링크가 없는 동안에도 세계는 흐른다 (docs/20 §7.2).
        }
        finally
        {
            // 자리를 비운다. 리스너가 이 값을 보고 다음 접속을 받는다.
            Volatile.Write(ref _active, 0);

            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>프레임 하나를 처리한다. 리시버 태스크 전용.</summary>
    private void Deliver(LinkMessageKind kind, ReadOnlySequence<byte> payload)
    {
        switch (kind)
        {
            case LinkMessageKind.CommandBatch:
                Receive(payload);
                return;

            case LinkMessageKind.Bye:
                // NPC 서버가 종료를 알렸다. 자리를 비워 다음 접속을 받는다 (A-02).
                //
                // <b>사유를 기록한다.</b> Shutdown 과 ProtocolViolation 은 운영에서 전혀 다른
                // 사건인데, 예전에는 둘 다 "연결이 끊겼다" 로만 보였다.
                LastByeCode = ReadByeCode(in payload);
                ByesReceived++;
                Volatile.Write(ref _active, 0);
                return;

            default:
                // Heartbeat 는 도착한 것만으로 의미가 있다 — 시각은 위에서 이미 갱신했다.
                // 모르는 종류는 조용히 버린다. 프레임 경계는 맞았으므로 스트림은 멀쩡하다.
                return;
        }
    }

    /// <summary>
    /// <c>CommandBatch</c> 한 장을 링에 붓는다.
    ///
    /// <para>
    /// <b>프레임 안 순서를 그대로 넣는다.</b> 링은 FIFO 라 틱 루프가 같은 순서로 꺼낸다 —
    /// 뒤집히면 <c>Stop</c> 뒤에 <c>MoveTo</c> 가 적용되는 식으로 NPC 가 엉뚱하게 움직이고,
    /// 증상이 "가끔 이상하게 행동한다" 로만 나타난다.
    /// </para>
    ///
    /// <para>
    /// <b>링이 차면 버린다.</b> 예외를 던지지 않는다 — 명령은 유실된다고 가정하는 것이
    /// 이 링크의 계약이고(docs/02 §1), NPC 서버는 <c>timeout_s</c> 만료로 스스로 재개한다.
    /// </para>
    /// </summary>
    private void Receive(ReadOnlySequence<byte> payload)
    {
        WireCommand[]? commands = MemoryPackSerializer.Deserialize<WireCommand[]>(payload);

        CommandFramesReceived++;

        if (commands is null)
        {
            return;   // 빈 배치도 프레임 하나다 (N8). 셌으니 할 일이 없다
        }

        foreach (WireCommand wire in commands)
        {
            NpcCommand command = wire.To();

            CommandsReceived++;
            _world.Inbox.TryEnqueue(in command);
        }
    }

    /// <summary>
    /// 세션 시작 재동기화. <b>틱 스레드에서 부른다</b> — 월드에 이벤트를 내기 때문이다.
    /// docs/20 §5.5 · §5.6.
    ///
    /// <list type="number">
    ///   <item>세션 전에 쌓인 이벤트를 <b>버린다.</b> 아무도 듣지 않던 구간의 이벤트이고,
    ///     그 구간의 결과는 아래에서 현재 상태로 다시 보낸다.</item>
    ///   <item>로스터 전원의 <c>NpcSpawned</c>. 순서는 로스터 순서다.</item>
    ///   <item>존 상태 초기화 — <c>ZoneStateChanged</c>·<c>WeatherChanged</c>.</item>
    /// </list>
    ///
    /// <para>
    /// <b>시퀀스를 되감지 않는다</b> (N6 · docs/20 §5.6). 재접속해도 이어지는 새 시퀀스를 달고
    /// 나가므로 <c>EventApplier</c> 가 멱등하게 반영한다 — 되감으면 그쪽이 전부 중복으로 버려
    /// NPC 가 영원히 멈춘다.
    /// </para>
    /// </summary>
    public async Task ResyncAsync(CancellationToken ct)
    {
        SimWorld world = _world.World;
        Tick now = _world.Now;

        while (world.Events.TryRead(out _))
        {
            // 세션 전 이벤트는 버린다.
            _drained++;
        }

        for (int i = 0; i < _world.Roster.Count; i++)
        {
            world.Emit(new GameEvent
            {
                Kind = GameEventKind.NpcSpawned,
                Sequence = 0,
                OccurredAt = now,
                Npc = new NpcId(i),
                Poi = world.PoiOf(i),
                Zone = world.ZoneOf(i),
                Pos = world.PositionOf(i),
            });
        }

        // 존 상태 초기화. 마스터데이터의 기본값을 그대로 보낸다 —
        // NPC 서버의 ZoneStateTable 은 전 존을 Peace·Fair 로 시작하므로(docs/14 §5),
        // 이 두 줄이 없으면 zones.json 이 Alert·Cold 로 선언한 존이 조용히 어긋난다.
        foreach (ZoneDef zone in _data.Zones.Zones)
        {
            world.Emit(new GameEvent
            {
                Kind = GameEventKind.ZoneStateChanged,
                Sequence = 0,
                OccurredAt = now,
                Zone = zone.Code,
                Code = (byte)zone.DefaultRegionState,
            });

            world.Emit(new GameEvent
            {
                Kind = GameEventKind.WeatherChanged,
                Sequence = 0,
                OccurredAt = now,
                Zone = zone.Code,
                Code = (byte)zone.DefaultClimate,
            });
        }

        NeedsResync = false;

        await SendPendingEventsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 틱 9단계. 이벤트 채널을 비워 <b>한 개의</b> <c>EventBatch</c> 프레임으로 내보내고,
    /// 1초마다 하트비트를 얹는다. docs/20 §7.2 · §5.6. <b>틱 스레드에서 부른다.</b>
    ///
    /// <para>
    /// <b>이벤트가 없으면 프레임도 없다.</b> 명령 쪽과 다른 판단이다 — 명령은 배치 경계 자체가
    /// 계약이라 빈 프레임도 보내야 하지만(N8), 이벤트는 각자 <c>Sequence</c> 를 달고 있어
    /// 빈 프레임이 아무것도 전달하지 않는다. 실제로는 <c>SimWorld.Tick</c> 이 매 틱
    /// <c>TickSync</c> 를 내므로 정상 회차에서 빈 틱은 없다.
    /// </para>
    ///
    /// <para>
    /// <b>상한을 넘긴 분은 버리지 않고 채널에 남긴다</b> (<see cref="MaxEventsPerTick"/>).
    /// 이벤트를 하나라도 버리면 NPC 서버의 상관 ID 가 영원히 안 닫히고,
    /// 그 NPC 는 <c>timeout_s</c> 가 지나야 겨우 재개한다.
    /// </para>
    /// </summary>
    public async Task FlushEventsAsync(Tick now, CancellationToken ct)
    {
        ChannelReader<GameEvent> events = _world.World.Events;
        int count = 0;

        while (count < MaxEventsPerTick && events.TryRead(out GameEvent ev))
        {
            _drained++;

            if (ev.Kind == GameEventKind.NpcSpawned)
            {
                SpawnsSent++;
            }

            EventObserver?.Invoke(in ev);

            _staging[count++] = WireEvent.From(in ev);
        }

        if (count > 0)
        {
            await SendBatchAsync(count, ct).ConfigureAwait(false);
        }

        // 남은 것은 다음 틱 몫이다. 채널은 틱 스레드만 읽고 쓰므로 이 수는 정확하다.
        EventsDeferred += EventsPending;

        if (now.Value % HeartbeatTicks == 0)
        {
            await SendHeartbeatAsync(now, ct).ConfigureAwait(false);
        }
    }

    /// <summary>하트비트 한 장. 마지막 시퀀스를 실어 수신 측이 갭을 스스로 볼 수 있게 한다.</summary>
    private async Task SendHeartbeatAsync(Tick now, CancellationToken ct)
    {
        byte[] beat = MemoryPackSerializer.Serialize(new WireHeartbeat
        {
            Tick = now.Value,
            Sequence = _world.World.Sequence,
        });

        await WriteFrameAsync(_stream, LinkMessageKind.Heartbeat, beat, ct).ConfigureAwait(false);

        HeartbeatsSent++;
    }

    /// <summary>
    /// 이벤트 채널을 비워 <c>EventBatch</c> 프레임으로 내보낸다.
    /// 프레임당 <see cref="MaxEventsPerFrame"/> 건이고, 넘으면 프레임을 더 만든다.
    /// </summary>
    private async Task SendPendingEventsAsync(CancellationToken ct)
    {
        SimWorld world = _world.World;
        int count = 0;

        while (world.Events.TryRead(out GameEvent ev))
        {
            _drained++;

            if (ev.Kind == GameEventKind.NpcSpawned)
            {
                SpawnsSent++;
            }

            EventObserver?.Invoke(in ev);

            _staging[count++] = WireEvent.From(in ev);

            if (count == MaxEventsPerFrame)
            {
                await SendBatchAsync(count, ct).ConfigureAwait(false);
                count = 0;
            }
        }

        if (count > 0)
        {
            await SendBatchAsync(count, ct).ConfigureAwait(false);
        }
    }

    private async Task SendBatchAsync(int count, CancellationToken ct)
    {
        byte[] payload = MemoryPackSerializer.Serialize(_staging.AsSpan(0, count).ToArray());

        await WriteFrameAsync(_stream, LinkMessageKind.EventBatch, payload, ct).ConfigureAwait(false);

        EventsSent += count;
        FramesSent++;
    }

    /// <summary>협상된 프로토콜 버전 (B-01). 핸드셰이크 전에는 0.</summary>
    public int NegotiatedVersion { get; private set; }

    /// <summary>협상된 기능 비트 (B-01).</summary>
    public ulong NegotiatedFeatures { get; private set; }

    /// <summary>NPC 서버가 내용 해시 불일치를 경고로 수락했는가 (B-04).</summary>
    public bool ContentHashWarning { get; private set; }

    /// <summary>마지막으로 받은 <c>Bye</c> 사유. 아직 없으면 null (A-02).</summary>
    public LinkByeCode? LastByeCode { get; private set; }

    /// <summary>받은 <c>Bye</c> 수. 테스트가 읽는다.</summary>
    public long ByesReceived { get; private set; }

    /// <summary><c>Bye</c> 를 보내고 세션을 닫는다. 실패해도 조용히 넘긴다 — 이미 끊긴 소켓일 수 있다.</summary>
    public async Task CloseAsync(LinkByeCode code, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
        {
            return;
        }

        try
        {
            await SendByeAsync(_stream, code, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 상대가 이미 끊었다. 종료 통보는 최선 노력이다.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _active, 0);

        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 프레임 입출력

    /// <summary><c>Bye</c> 페이로드에서 사유를 읽는다. 깨졌으면 null.</summary>
    private static LinkByeCode? ReadByeCode(in System.Buffers.ReadOnlySequence<byte> payload)
    {
        try
        {
            WireBye bye = MemoryPackSerializer.Deserialize<WireBye>(payload);

            return Enum.IsDefined((LinkByeCode)bye.Code) ? (LinkByeCode)bye.Code : null;
        }
        catch (MemoryPackSerializationException)
        {
            return null;
        }
    }

    /// <summary><c>Bye</c> 한 장. 세션을 만들기 전에도 보낼 수 있어야 해서 정적이다 (두 번째 접속 거절).</summary>
    public static Task SendByeAsync(Stream stream, LinkByeCode code, CancellationToken ct) =>
        WriteFrameAsync(
            stream,
            LinkMessageKind.Bye,
            MemoryPackSerializer.Serialize(new WireBye { Code = (byte)code }),
            ct);

    public static Task WriteFrameAsync(
        Stream stream, LinkMessageKind kind, byte[] payload, CancellationToken ct) =>
        WriteFrameAsync(stream, kind, payload, ct, FrameCodec.Version);

    /// <summary>버전을 명시해 프레임 하나를 쓴다 (B-01 협상 경로).</summary>
    public static async Task WriteFrameAsync(
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
    /// 상시 수신은 <see cref="StartReceiver"/> 의 <c>PipeReader</c> 로 간다.
    /// </summary>
    public static async Task<(LinkMessageKind Kind, byte[] Payload)> ReadFrameAsync(
        Stream stream, CancellationToken ct)
    {
        (LinkMessageKind kind, byte[] payload, _) =
            await ReadFrameWithVersionAsync(stream, ct).ConfigureAwait(false);

        return (kind, payload);
    }

    /// <summary>
    /// 위와 같되 프레임의 와이어 버전을 같이 돌려준다 (B-01).
    ///
    /// <c>WireHelloAck</c> 와 <c>WireHelloAckV2</c> 는 배치가 달라, 페이로드를 읽기 전에
    /// 어느 쪽인지 알아야 한다.
    /// </summary>
    public static async Task<(LinkMessageKind Kind, byte[] Payload, byte Version)>
        ReadFrameWithVersionAsync(Stream stream, CancellationToken ct)
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

    /// <summary>
    /// <see cref="EventObserver"/> 의 모양. <c>in</c> 인 이유는 <see cref="GameWorld.CommandSink"/> 와 같다 —
    /// 64바이트 구조체를 틱마다 수백 번 복사하지 않는다.
    /// </summary>
    public delegate void EventSink(in GameEvent ev);
}
