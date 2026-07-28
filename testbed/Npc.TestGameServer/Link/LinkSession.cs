using System.Buffers;
using System.Buffers.Binary;
using MemoryPack;
using Npc.Contracts;
using Npc.MasterData;
using Npc.Sim;
using Npc.Wire;

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
/// <b>스레드 계약이 둘로 갈린다.</b> 이 구분을 지우면 <see cref="SimWorld"/> 의 이벤트 채널이
/// 깨진다 — 그 채널은 <c>SingleWriter = true</c> 라 기록자가 하나여야 한다.
/// <list type="bullet">
///   <item><see cref="HandshakeAsync"/> — <b>accept 태스크</b>에서 부른다. 월드를 만지지 않는다.</item>
///   <item><see cref="ResyncAsync"/> — <b>틱 스레드</b>에서 부른다. 월드에 이벤트를 낸다.</item>
/// </list>
/// </para>
/// </summary>
public sealed class LinkSession : IAsyncDisposable
{
    /// <summary>재동기화 <c>EventBatch</c> 한 프레임에 담는 이벤트 상한. docs/20 §5.5.</summary>
    public const int MaxEventsPerFrame = 256;

    private readonly Stream _stream;
    private readonly GameWorld _world;
    private readonly MasterDataSet _data;
    private readonly GameServerOptions _options;

    /// <summary>와이어 변환 스테이징. 프레임을 만들 때만 쓴다.</summary>
    private readonly WireEvent[] _staging = new WireEvent[MaxEventsPerFrame];

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
    public bool IsActive { get; private set; } = true;

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
    /// 핸드셰이크. <b>accept 태스크에서 부른다.</b> docs/20 §5.5.
    ///
    /// <c>Hello</c> 송신 → <c>HelloAck</c> 수신 → 수락 여부 판정.
    /// 거절이면 <c>Bye(HandshakeRejected)</c> 를 보내고 세션을 닫는다.
    /// </summary>
    /// <returns>수락되면 true.</returns>
    public async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        byte[] hello = MemoryPackSerializer.Serialize(new WireHello
        {
            ProtocolVersion = FrameCodec.Version,
            TickRate = GameWorld.TickRate,
            TimeScale = _options.TimeScale,
            NpcCount = _world.Roster.Count,
            StartTick = _world.TicksProcessed,
            MasterData = WireHash.FromHex(_data.ContentHash),
            Roster = WireHash.FromHex(_world.Roster.Hash),
        });

        await WriteFrameAsync(_stream, LinkMessageKind.Hello, hello, ct).ConfigureAwait(false);

        (LinkMessageKind kind, byte[] payload) = await ReadFrameAsync(_stream, ct).ConfigureAwait(false);

        if (kind != LinkMessageKind.HelloAck)
        {
            // 첫 말이 HelloAck 이 아니면 상대가 이 프로토콜을 쓰지 않는 것이다.
            RejectCode = LinkRejectCode.ProtocolVersion;
            await CloseAsync(LinkByeCode.ProtocolViolation, ct).ConfigureAwait(false);

            return false;
        }

        WireHelloAck ack = MemoryPackSerializer.Deserialize<WireHelloAck>(payload);

        if (ack.Accepted != 1)
        {
            RejectCode = (LinkRejectCode)ack.RejectCode;
            await CloseAsync(LinkByeCode.HandshakeRejected, ct).ConfigureAwait(false);

            return false;
        }

        RejectCode = LinkRejectCode.None;
        IsAccepted = true;

        return true;
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
        var now = new Tick(_world.TicksProcessed);

        while (world.Events.TryRead(out _))
        {
            // 세션 전 이벤트는 버린다.
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
    /// 이벤트 채널을 비워 <c>EventBatch</c> 프레임으로 내보낸다.
    /// 프레임당 <see cref="MaxEventsPerFrame"/> 건이고, 넘으면 프레임을 더 만든다.
    /// </summary>
    private async Task SendPendingEventsAsync(CancellationToken ct)
    {
        SimWorld world = _world.World;
        int count = 0;

        while (world.Events.TryRead(out GameEvent ev))
        {
            if (ev.Kind == GameEventKind.NpcSpawned)
            {
                SpawnsSent++;
            }

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

    /// <summary><c>Bye</c> 를 보내고 세션을 닫는다. 실패해도 조용히 넘긴다 — 이미 끊긴 소켓일 수 있다.</summary>
    public async Task CloseAsync(LinkByeCode code, CancellationToken ct)
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;

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
        IsActive = false;

        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 프레임 입출력

    /// <summary><c>Bye</c> 한 장. 세션을 만들기 전에도 보낼 수 있어야 해서 정적이다 (두 번째 접속 거절).</summary>
    public static Task SendByeAsync(Stream stream, LinkByeCode code, CancellationToken ct) =>
        WriteFrameAsync(
            stream,
            LinkMessageKind.Bye,
            MemoryPackSerializer.Serialize(new WireBye { Code = (byte)code }),
            ct);

    public static async Task WriteFrameAsync(
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
    /// 상시 수신은 <c>PipeReader</c> 로 간다 (T6-17).
    /// </summary>
    public static async Task<(LinkMessageKind Kind, byte[] Payload)> ReadFrameAsync(
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
}
