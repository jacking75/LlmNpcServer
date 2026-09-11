using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using MemoryPack;
using Npc.Contracts;
using Npc.TestBed.Protocol;
using Npc.TestGameServer.World;
using Npc.Wire;

namespace Npc.TestGameServer.Client;

/// <summary>
/// 월드가 차지하는 사각형. <see cref="SrvHello"/> 에 실려 클라이언트의 <c>Home</c> 이 된다.
///
/// <b>클라이언트가 두 번 계산하지 않는다.</b> 같은 마스터데이터로 각자 재면 언젠가 어긋나고,
/// 그때 화면만 조금 이상해져서 원인을 찾기 어렵다 (docs/20 §9.3).
/// </summary>
/// <param name="MinX">최소 X.</param>
/// <param name="MinZ">최소 Z.</param>
/// <param name="MaxX">최대 X.</param>
/// <param name="MaxZ">최대 Z.</param>
public readonly record struct WorldBounds(float MinX, float MinZ, float MaxX, float MaxZ);

/// <summary>
/// 테스트 클라이언트 하나와의 세션. docs/20 §8.1 · §8.2.
///
/// <para>
/// <b>스레드 계약이 <see cref="Link.LinkSession"/> 과 같은 모양이다.</b> 이 구분을 지우면
/// <see cref="Npc.Sim.SimWorld"/> 의 이벤트 채널(<c>SingleWriter</c>)과 소켓 쓰기가 동시에 깨진다.
/// <list type="bullet">
///   <item><see cref="HandshakeAsync"/> — <b>accept 태스크</b>. <c>CliHello</c> 를 읽고 버전만 본다.
///     월드도 <see cref="PlayerRegistry"/> 도 만지지 않는다.</item>
///   <item><see cref="JoinAsync"/> — <b>틱 스레드</b>. 플레이어를 등록하고 <c>SrvHello</c> 를 보낸다.</item>
///   <item>수신 루프 — <b>리시버 태스크</b>. <b>아무것도 적용하지 않고</b> 링에만 넣는다.</item>
///   <item><see cref="ApplyAsync"/> — <b>틱 스레드</b>. 링을 비워 실제로 적용하고 응답을 보낸다.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>소켓에 쓰는 것은 틱 스레드 하나뿐이다.</b> 리시버가 <c>Pong</c> 을 직접 보내면 스냅샷
/// 프레임과 바이트가 섞여 스트림이 깨진다 — 프레임 하나가 아니라 연결이 죽는다.
/// 그래서 <c>Ping</c> 조차 링을 지나 틱 경계에서 답한다.
/// </para>
/// </summary>
public sealed class ClientSession : IAsyncDisposable
{
    /// <summary>
    /// 미적용 입력 링의 칸 수.
    ///
    /// 입력은 10Hz 이고 틱마다 전량이 빠지므로 한 틱에 한두 개가 정상이다.
    /// 256 은 클라이언트가 폭주해도 게임서버가 죽지 않게 하는 상한이지 용량 설계가 아니다.
    /// </summary>
    public const int InboxCapacity = 256;

    /// <summary>한 배치에서 <see cref="MirrorLog"/> 로부터 읽는 줄 수 상한.</summary>
    public const int MaxLogRead = 256;

    /// <summary>
    /// 선택 NPC 가 <b>아닌</b> NPC 의 줄은 배치당 이만큼까지다. docs/20 §7.4.
    ///
    /// 전량을 보내면 NPC 500 에서 초당 수백 건이 된다. 선택한 NPC 의 줄은 이 상한을 안 받는다 —
    /// 그 NPC 를 설명하는 것이 이 화면의 목적이기 때문이다 (docs/20 §9.5).
    /// </summary>
    public const int MaxOtherLogsPerBatch = 32;

    private const int Mask = InboxCapacity - 1;

    private readonly Stream _stream;
    private readonly GameWorld _world;
    private readonly PlayerRegistry _players;
    private readonly WorldBounds _bounds;
    private readonly int _timeScale;

    /// <summary>리시버(생산자) → 틱 스레드(소비자) SPSC 링. <see cref="Link.CommandInbox"/> 와 같은 모양이다.</summary>
    private readonly ClientAction[] _inbox = new ClientAction[InboxCapacity];

    /// <summary>마지막 스냅샷에 실린 NPC. 로그 필터의 "AOI 안" 이 이것이다 (docs/20 §7.4).</summary>
    private readonly bool[] _inAoi;

    private readonly LoggedCommand[] _readCommands = new LoggedCommand[MaxLogRead];
    private readonly LoggedEvent[] _readEvents = new LoggedEvent[MaxLogRead];

    /// <summary>
    /// 거른 결과. <b>읽기 버퍼와 갈라 둔다</b> — 훑는 자리가 감기므로 제자리 압축이 안 된다.
    ///
    /// 여기서 미러의 <see cref="LoggedCommand"/> 가 와이어의 <see cref="LogCommand"/> 로 바뀐다.
    /// 미러는 게임서버 내부 표현이고 이쪽은 프로토콜이라 둘을 같은 타입으로 묶지 않는다 —
    /// <c>Npc.Wire</c> 가 <c>Npc.Contracts</c> 와 갈려 있는 것과 같은 이유다 (docs/20 §3.1).
    /// </summary>
    private readonly LogCommand[] _outCommands = new LogCommand[MaxLogRead];
    private readonly LogEvent[] _outEvents = new LogEvent[MaxLogRead];

    /// <summary>선별한 줄을 틱 순으로 되돌리는 정렬 키. 비교자를 만들지 않으려고 키 배열을 쓴다.</summary>
    private readonly long[] _logKeys = new long[MaxLogRead];

    private long _head;   // 소비자 (틱 스레드)
    private long _tail;   // 생산자 (리시버)

    private int _active = 1;
    private Task? _receiver;

    /// <summary>이 세션이 미러를 어디까지 읽었는가. 첫 배치 때 만든다.</summary>
    private MirrorLog.Cursor? _cursor;

    /// <summary>라운드로빈 시작 자리. 같은 NPC 의 줄만 계속 실리지 않게 한다 (docs/20 §7.4).</summary>
    private int _commandRotate;
    private int _eventRotate;

    /// <summary>세션을 만든다. 소켓은 <see cref="ClientListener"/> 가 이미 열어 뒀다.</summary>
    public ClientSession(
        Stream stream, GameWorld world, PlayerRegistry players, WorldBounds bounds, int timeScale)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(players);

        _stream = stream;
        _world = world;
        _players = players;
        _bounds = bounds;
        _timeScale = timeScale;
        _inAoi = new bool[world.World.Capacity];
    }

    /// <summary>
    /// 제어 처리기. <see cref="ClientListener"/> 가 붙여 준다. null 이면 <c>Control</c> 을 버린다.
    ///
    /// <b>세션이 직접 만들지 않는다.</b> <see cref="ControlHandler"/> 는 <c>SimWorld.Handler</c> 앞에
    /// 서는 프로세스 단위 물건이라 세션마다 하나씩 있으면 관문이 겹겹이 쌓인다.
    /// </summary>
    public ControlHandler? Controls { get; set; }

    /// <summary>세션이 살아 있는가. 리스너가 이 값을 보고 자리를 회수한다.</summary>
    public bool IsActive => Volatile.Read(ref _active) != 0;

    /// <summary>핸드셰이크의 <c>CliHello</c> 까지 끝났는가.</summary>
    public bool IsGreeted { get; private set; }

    /// <summary>아직 <see cref="JoinAsync"/> 를 안 불렀는가. 틱 루프가 이 값을 본다.</summary>
    public bool NeedsJoin { get; private set; } = true;

    /// <summary>아직 존 상태를 한 번도 안 받았는가. 첫 장은 주기를 기다리지 않는다 (docs/20 §8.1).</summary>
    public bool NeedsZoneStates { get; private set; } = true;

    /// <summary>배정된 플레이어. 아직 참여 전이거나 정원 초과면 <c>PlayerId(0)</c> 다.</summary>
    public PlayerId Player { get; private set; }

    /// <summary>
    /// 선택된 NPC 첨자. 없으면 -1.
    ///
    /// 로그 필터와 인스펙터의 대상이다 (docs/20 §8.2 · §9.5). 스냅샷 빌더(T6-24)가 읽는다.
    /// </summary>
    public int SelectedNpc { get; private set; } = -1;

    /// <summary>클라이언트가 보고한 빌드 번호. 진단용이다.</summary>
    public int ClientVersion { get; private set; }

    /// <summary>받은 <c>Input</c> 수.</summary>
    public long InputsReceived { get; private set; }

    /// <summary>링이 차서 버린 입력 수. 0 이 아니면 클라이언트가 규약보다 빠르게 보낸 것이다.</summary>
    public long ActionsDropped { get; private set; }

    /// <summary>보낸 <c>Pong</c> 수.</summary>
    public long PongsSent { get; private set; }

    /// <summary>보낸 프레임 수.</summary>
    public long FramesSent { get; private set; }

    /// <summary>보낸 <c>Snapshot</c> 수.</summary>
    public long SnapshotsSent { get; private set; }

    /// <summary>보낸 로그 줄 수 — 명령·이벤트 합계.</summary>
    public long LogLinesSent { get; private set; }

    /// <summary>배치 상한(<see cref="MaxOtherLogsPerBatch"/>)에 걸려 뺀 줄 수의 누계.</summary>
    public long LogLinesCulled { get; private set; }

    /// <summary>적용한 <c>Control</c> 수. <see cref="ControlHandler"/> 가 거절한 것은 안 센다.</summary>
    public long ControlsApplied { get; private set; }

    /// <summary>수신 루프. 세션이 끝나면 완료된다.</summary>
    public Task Receiver => _receiver ?? Task.CompletedTask;

    /// <summary>
    /// <c>CliHello</c> 를 읽고 버전을 본다. <b>accept 태스크에서 부른다.</b>
    ///
    /// <b>여기서 플레이어를 등록하지 않는다.</b> <see cref="PlayerRegistry"/> 는 틱 스레드
    /// 단독 소유다 — 소켓 태스크가 슬롯을 잡으면 틱 스레드가 절반만 초기화된 플레이어를 본다.
    /// </summary>
    /// <returns>버전이 맞으면 true.</returns>
    public async Task<bool> HandshakeAsync(CancellationToken ct)
    {
        (ClientMessageKind kind, byte[] payload) = await ReadFrameAsync(_stream, ct).ConfigureAwait(false);

        if (kind != ClientMessageKind.CliHello)
        {
            return false;   // 첫 말이 CliHello 가 아니면 이 프로토콜을 쓰지 않는 것이다
        }

        CliHello hello = MemoryPackSerializer.Deserialize<CliHello>(payload);

        if (hello.ProtocolVersion != ClientProtocol.Version)
        {
            return false;
        }

        ClientVersion = hello.ClientVersion;
        IsGreeted = true;

        return true;
    }

    /// <summary>
    /// 플레이어를 등록하고 <c>SrvHello</c> 를 보낸다. <b>틱 스레드에서 부른다.</b>
    ///
    /// <para>
    /// <b>정원이 없으면 <c>PlayerId = 0</c> 을 실어 보내고 닫는다.</b> §8 에 거절 메시지가
    /// 따로 없고, 0 은 <see cref="PlayerRegistry"/> 가 이미 "없음" 으로 쓰는 값이다 —
    /// 조용히 소켓을 닫으면 클라이언트가 "서버가 죽었나" 와 구별하지 못한다.
    /// </para>
    /// </summary>
    public async Task JoinAsync(Tick now, CancellationToken ct)
    {
        NeedsJoin = false;
        Player = _players.Add();

        await SendAsync(
            ClientMessageKind.SrvHello,
            new SrvHello
            {
                ProtocolVersion = ClientProtocol.Version,
                TickRate = GameWorld.TickRate,
                TimeScale = _timeScale,
                PlayerId = Player.Value,
                NpcCount = _world.Roster.Count,
                MinX = _bounds.MinX,
                MinZ = _bounds.MinZ,
                MaxX = _bounds.MaxX,
                MaxZ = _bounds.MaxZ,
                MasterData = WireHash.FromHex(_world.World.Data.ContentHash),
            },
            ct).ConfigureAwait(false);

        if (Player.Value == 0)
        {
            Volatile.Write(ref _active, 0);
            return;
        }

        StartReceiver(ct);
    }

    /// <summary>
    /// 링을 비워 적용하고 응답을 보낸다. <b>틱 스레드에서 부른다</b> — 틱 3단계다 (docs/20 §7.2).
    /// </summary>
    public async Task ApplyAsync(Tick now, CancellationToken ct)
    {
        while (TryDequeue(out ClientAction action))
        {
            switch (action.Kind)
            {
                case ClientMessageKind.Input:
                    _players.SetInput(Player, action.DirX, action.DirZ, action.Run != 0);
                    break;

                case ClientMessageKind.Interact:
                    // 사거리(30m) 판정은 등록기가 한다. 밖이면 조용히 무시되고 카운터만 오른다.
                    _players.TryInteract(Player, action.Npc, now);
                    break;

                case ClientMessageKind.Attack:
                    _players.TryAttack(Player, action.Npc, action.Amount, now);
                    break;

                case ClientMessageKind.Select:
                    // 음수는 선택 해제다. 범위 밖 첨자도 -1 로 접는다 —
                    // 스냅샷 빌더가 그 값으로 배열을 찌르지 않게 한다.
                    SelectedNpc = (uint)action.Npc < (uint)_world.World.Capacity ? action.Npc : -1;
                    break;

                case ClientMessageKind.Ping:
                    await SendAsync(
                        ClientMessageKind.Pong,
                        new Pong { ClientStamp = action.Stamp, ServerTick = now.Value },
                        ct).ConfigureAwait(false);

                    PongsSent++;
                    break;

                case ClientMessageKind.Control:
                    // 모르는 종류·틀린 인자는 처리기가 세고 넘긴다 — 낡은 클라이언트 하나가
                    // 게임서버를 죽이면 안 된다 (docs/20 §8.3).
                    if (Controls is { } controls && controls.Apply(
                        new Control
                        {
                            Kind = action.ControlKind,
                            ZoneCode = action.Zone,
                            Code = action.Code,
                            Amount = action.Amount,
                        },
                        now))
                    {
                        ControlsApplied++;
                    }

                    break;

                default:
                    // 모르는 종류는 이미 리시버가 걸렀다. 여기 오면 우리 실수다.
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- 10단계 방송

    /// <summary>
    /// 스냅샷 한 장. <b>틱 스레드에서 부른다</b> — 틱 10단계다 (docs/20 §7.2).
    ///
    /// 실린 NPC 를 기억해 둔다. 로그 필터의 "AOI 안" 이 곧 이 집합이다 (docs/20 §7.4) —
    /// 거리를 두 번 재지 않는다.
    /// </summary>
    public async Task SendSnapshotAsync(Snapshot snapshot, CancellationToken ct)
    {
        Array.Clear(_inAoi);

        EntityState[] entities = snapshot.Entities ?? [];
        int count = Math.Min(snapshot.EntityCount, entities.Length);

        for (int i = 0; i < count; i++)
        {
            ref EntityState entity = ref entities[i];

            if (entity.Kind == (byte)EntityKind.Npc && (uint)entity.Id < (uint)_inAoi.Length)
            {
                _inAoi[entity.Id] = true;
            }
        }

        await SendAsync(ClientMessageKind.Snapshot, snapshot, ct).ConfigureAwait(false);

        SnapshotsSent++;
    }

    /// <summary>
    /// 명령·이벤트 로그 한 배치. <b>틱 스레드에서 부른다.</b> docs/20 §7.4 · §8.1.
    ///
    /// <b><see cref="SendSnapshotAsync"/> 뒤에 부른다</b> — AOI 집합이 그 안에서 갱신된다.
    /// </summary>
    public async Task SendLogsAsync(MirrorLog mirror, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mirror);

        _cursor ??= mirror.NewCursor();

        int read = mirror.ReadCommands(_cursor, _readCommands);
        int kept = FilterCommands(read);

        if (kept > 0)
        {
            await SendAsync(
                ClientMessageKind.CommandLog,
                new CommandLog { Commands = _outCommands.AsSpan(0, kept).ToArray() },
                ct).ConfigureAwait(false);

            LogLinesSent += kept;
        }

        read = mirror.ReadEvents(_cursor, _readEvents);
        kept = FilterEvents(read);

        if (kept > 0)
        {
            await SendAsync(
                ClientMessageKind.EventLog,
                new EventLog { Events = _outEvents.AsSpan(0, kept).ToArray() },
                ct).ConfigureAwait(false);

            LogLinesSent += kept;
        }
    }

    /// <summary>
    /// 존 상태 한 장. 변화 시 + 5초마다다 (docs/20 §8.1).
    ///
    /// <b>첫 장은 주기를 기다리지 않는다</b> (<see cref="NeedsZoneStates"/>). 방금 붙은 세션이
    /// 다음 주기까지 존 색을 모르면, 사람은 그 5초 동안 회색 지도를 보고 "제어가 안 먹는다" 로 읽는다.
    /// </summary>
    public async Task SendZoneStatesAsync(ZoneStates zones, CancellationToken ct)
    {
        await SendAsync(ClientMessageKind.ZoneStates, zones, ct).ConfigureAwait(false);

        NeedsZoneStates = false;
    }

    /// <summary>NPC 서버 링크의 상태 한 장. 1초마다다 (docs/20 §8.1).</summary>
    public Task SendLinkStatusAsync(LinkStatus status, CancellationToken ct) =>
        SendAsync(ClientMessageKind.LinkStatus, status, ct);

    /// <summary>
    /// 세션을 끝낸다. <b>플레이어 제거는 여기서 하지 않는다</b> —
    /// <see cref="PlayerRegistry"/> 는 틱 스레드 것이라 <see cref="ClientListener"/> 가 회수한다.
    /// </summary>
    public void Close() => Volatile.Write(ref _active, 0);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _active, 0);

        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 정원 초과를 알리고 끊는다. <b>세션을 만들기 전에도 보낼 수 있어야 해서 정적이다.</b>
    /// <c>PlayerId = 0</c> 이 "자리가 없다" 는 뜻이다.
    /// </summary>
    public static async Task RejectAsync(
        Stream stream, WorldBounds bounds, int timeScale, int npcCount, CancellationToken ct)
    {
        try
        {
            await WriteFrameAsync(
                stream,
                ClientMessageKind.SrvHello,
                MemoryPackSerializer.Serialize(new SrvHello
                {
                    ProtocolVersion = ClientProtocol.Version,
                    TickRate = GameWorld.TickRate,
                    TimeScale = timeScale,
                    PlayerId = 0,
                    NpcCount = npcCount,
                    MinX = bounds.MinX,
                    MinZ = bounds.MinZ,
                    MaxX = bounds.MaxX,
                    MaxZ = bounds.MaxZ,
                }),
                ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 상대가 이미 끊었다. 거절 통보는 최선 노력이다.
        }
    }

    // ---------------------------------------------------------------- 로그 필터

    /// <summary>
    /// 명령 줄을 §7.4 규약대로 거른다 — 선택 NPC 는 전부, 그 외는 AOI 안에서 배치당
    /// <see cref="MaxOtherLogsPerBatch"/> 건까지.
    ///
    /// <para>
    /// <b>라운드로빈은 훑기 시작 자리를 미는 것으로 한다.</b> 미러는 도착 순이라 시작 자리를
    /// 옮기면 다음 배치에서 다른 NPC 가 실린다 — 늘 앞의 32건만 실으면 수다스러운 NPC 하나가
    /// 나머지를 굶긴다. 자리를 옮긴 만큼 순서가 접히므로 마지막에 틱 순으로 되돌린다.
    /// </para>
    /// </summary>
    /// <returns><see cref="_outCommands"/> 앞쪽에 담긴 줄 수.</returns>
    private int FilterCommands(int read)
    {
        if (read <= 0)
        {
            return 0;
        }

        int kept = 0;
        int others = 0;

        for (int step = 0; step < read; step++)
        {
            ref LoggedCommand line = ref _readCommands[(_commandRotate + step) % read];

            if (line.Npc != SelectedNpc)
            {
                if (!InAoi(line.Npc))
                {
                    continue;
                }

                if (others >= MaxOtherLogsPerBatch)
                {
                    LogLinesCulled++;
                    continue;
                }

                others++;
            }

            _logKeys[kept] = line.Tick;
            _outCommands[kept++] = new LogCommand
            {
                Tick = line.Tick,
                Npc = line.Npc,
                Correlation = line.Correlation,
                TargetPoi = line.TargetPoi,
                Kind = line.Kind,
            };
        }

        _commandRotate = (_commandRotate + Math.Max(1, others)) % read;

        Array.Sort(_logKeys, _outCommands, 0, kept);

        return kept;
    }

    /// <summary>이벤트 줄. <see cref="FilterCommands"/> 와 같은 규약이다.</summary>
    /// <returns><see cref="_outEvents"/> 앞쪽에 담긴 줄 수.</returns>
    private int FilterEvents(int read)
    {
        if (read <= 0)
        {
            return 0;
        }

        int kept = 0;
        int others = 0;

        for (int step = 0; step < read; step++)
        {
            ref LoggedEvent line = ref _readEvents[(_eventRotate + step) % read];

            if (line.Npc != SelectedNpc)
            {
                if (!InAoi(line.Npc))
                {
                    continue;
                }

                if (others >= MaxOtherLogsPerBatch)
                {
                    LogLinesCulled++;
                    continue;
                }

                others++;
            }

            _logKeys[kept] = line.Tick;
            _outEvents[kept++] = new LogEvent
            {
                Tick = line.Tick,
                Npc = line.Npc,
                Amount = line.Amount,
                Kind = line.Kind,
                Code = line.Code,
            };
        }

        _eventRotate = (_eventRotate + Math.Max(1, others)) % read;

        Array.Sort(_logKeys, _outEvents, 0, kept);

        return kept;
    }

    /// <summary>
    /// 이 NPC 가 마지막 스냅샷에 실렸는가.
    ///
    /// <b>스냅샷을 아직 한 번도 안 보냈으면 전부 false 다</b> — 그래서 첫 배치는 선택 NPC 의 줄만
    /// 나간다. 5Hz 라 한 번 늦는 것뿐이고, 거리를 두 번 재는 것보다 낫다.
    /// </summary>
    private bool InAoi(int npc) => (uint)npc < (uint)_inAoi.Length && _inAoi[npc];

    // ---------------------------------------------------------------- 수신

    /// <summary>수신 루프를 띄운다. <b>이 태스크가 링의 유일한 생산자다</b> (SPSC).</summary>
    private void StartReceiver(CancellationToken ct) =>
        _receiver ??= Task.Run(() => ReceiveLoopAsync(ct), CancellationToken.None);

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        PipeReader reader = PipeReader.Create(_stream);

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
                    break;   // EOF. 클라이언트가 끊었다
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 종료 지시다.
        }
        catch (Exception)
        {
            // 프레임이 깨졌거나 소켓이 죽었다. <b>게임서버는 계속 돈다</b> —
            // 창 하나가 닫힌 것이 세계를 멈출 이유가 아니다 (docs/20 §7.2).
        }
        finally
        {
            Volatile.Write(ref _active, 0);

            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>프레임 하나를 링에 넣는다. 리시버 태스크 전용. <b>여기서 적용하지 않는다.</b></summary>
    private void Receive(ClientMessageKind kind, ReadOnlySequence<byte> payload)
    {
        switch (kind)
        {
            case ClientMessageKind.Input:
            {
                Input input = MemoryPackSerializer.Deserialize<Input>(payload);

                InputsReceived++;
                Enqueue(new ClientAction
                {
                    Kind = kind,
                    DirX = input.DirX,
                    DirZ = input.DirZ,
                    Run = input.Run,
                });
                return;
            }

            case ClientMessageKind.Interact:
                Enqueue(new ClientAction
                {
                    Kind = kind,
                    Npc = MemoryPackSerializer.Deserialize<Interact>(payload).NpcId,
                });
                return;

            case ClientMessageKind.Attack:
            {
                Attack attack = MemoryPackSerializer.Deserialize<Attack>(payload);

                Enqueue(new ClientAction { Kind = kind, Npc = attack.NpcId, Amount = attack.Amount });
                return;
            }

            case ClientMessageKind.Select:
                Enqueue(new ClientAction
                {
                    Kind = kind,
                    Npc = MemoryPackSerializer.Deserialize<Select>(payload).NpcId,
                });
                return;

            case ClientMessageKind.Ping:
                Enqueue(new ClientAction
                {
                    Kind = kind,
                    Stamp = MemoryPackSerializer.Deserialize<Ping>(payload).ClientStamp,
                });
                return;

            case ClientMessageKind.Control:
            {
                Control control = MemoryPackSerializer.Deserialize<Control>(payload);

                Enqueue(new ClientAction
                {
                    Kind = kind,
                    ControlKind = control.Kind,
                    Zone = control.ZoneCode,
                    Code = control.Code,
                    Amount = control.Amount,
                });
                return;
            }

            default:
                // 서버 → 클라 종류가 거꾸로 왔다. 무시한다.
                return;
        }
    }

    private void Enqueue(in ClientAction action)
    {
        long tail = _tail;

        if (tail - Volatile.Read(ref _head) >= InboxCapacity)
        {
            ActionsDropped++;
            return;
        }

        _inbox[tail & Mask] = action;
        Volatile.Write(ref _tail, tail + 1);
    }

    private bool TryDequeue(out ClientAction action)
    {
        long head = _head;

        if (head >= Volatile.Read(ref _tail))
        {
            action = default;
            return false;
        }

        action = _inbox[head & Mask];
        Volatile.Write(ref _head, head + 1);

        return true;
    }

    // ---------------------------------------------------------------- 프레임 입출력

    private async Task SendAsync<T>(ClientMessageKind kind, T message, CancellationToken ct)
    {
        await WriteFrameAsync(_stream, kind, MemoryPackSerializer.Serialize(message), ct)
            .ConfigureAwait(false);

        FramesSent++;
    }

    /// <summary>프레임 한 장. <b>틱 스레드에서만 부른다</b> — 기록자가 둘이면 바이트가 섞인다.</summary>
    public static async Task WriteFrameAsync(
        Stream stream, ClientMessageKind kind, byte[] payload, CancellationToken ct)
    {
        var writer = new ArrayBufferWriter<byte>(FrameCodec.HeaderSize + payload.Length);

        ClientProtocol.WriteHeader(writer, kind, payload.Length);
        writer.Write(payload);

        await stream.WriteAsync(writer.WrittenMemory, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 프레임 하나를 읽는다. <b>핸드셰이크 전용</b>이라 단순 동기 읽기다 —
    /// 상시 수신은 <c>PipeReader</c> 로 간다.
    /// </summary>
    public static async Task<(ClientMessageKind Kind, byte[] Payload)> ReadFrameAsync(
        Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[FrameCodec.HeaderSize];

        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);

        // 버전·길이 상한은 코덱이 본다. 헤더만으로는 페이로드가 안 왔으니 반환값은 false 다 —
        // 여기서 필요한 것은 그 검사가 도는 것이고, 어기면 InvalidDataException 이 나온다.
        var probe = new ReadOnlySequence<byte>(header);

        ClientProtocol.TryReadFrame(ref probe, out _, out _);

        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        var kind = (ClientMessageKind)header[4];
        var payload = new byte[length];

        if (length > 0)
        {
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        }

        return (kind, payload);
    }

    /// <summary>
    /// 미적용 클라이언트 동작 한 건.
    ///
    /// <b>메시지마다 타입을 두지 않는다.</b> 링은 사전 할당 배열이라 원소가 하나의 값 타입이어야
    /// 하고, 종류가 다섯뿐이라 판별 공용체 한 개로 충분하다 — <c>NpcCommand</c> 와 같은 방식이다.
    /// </summary>
    private struct ClientAction
    {
        public ClientMessageKind Kind;
        public int Npc;
        public int Amount;
        public float DirX;
        public float DirZ;
        public byte Run;
        public long Stamp;

        /// <summary><c>Control</c> 전용 — <c>ControlKind</c>. <see cref="Kind"/> 는 늘 <c>Control</c> 이다.</summary>
        public byte ControlKind;

        /// <summary><c>Control</c> 전용 — 대상 존.</summary>
        public ushort Zone;

        /// <summary><c>Control</c> 전용 — Kind 별 소형 코드.</summary>
        public byte Code;
    }
}
