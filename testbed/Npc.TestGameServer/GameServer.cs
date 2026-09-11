using System.Diagnostics;
using System.Globalization;
using Npc.Contracts;
using Npc.MasterData;
using Npc.Memory;
using Npc.TestBed.Protocol;
using Npc.TestGameServer.Client;
using Npc.TestGameServer.Link;
using Npc.TestGameServer.World;
using Npc.Wire;

namespace Npc.TestGameServer;

/// <summary>
/// 게임서버 대역 한 벌. 조립과 10Hz 루프. docs/20 §7.1 · §7.2 · §7.5.
///
/// <para>
/// <b>부품은 T6-14~T6-25 가 만들었고, 이 클래스는 그것들을 잇는다.</b> 이 자리가 없던 동안
/// <see cref="MirrorLog"/>·<see cref="SnapshotBuilder"/>·<see cref="ControlHandler"/> 는
/// 테스트만 부르는 코드였다.
/// </para>
///
/// <para>
/// <b>NPC 서버를 기다리지 않는다.</b> 락스텝이 없는 것이 이 테스트 베드의 핵심이다
/// (docs/20 §1) — 링크가 없는 동안에도 세계는 흐르고, 그때 쌓인 이벤트는 배수해 버린다.
/// 안 그러면 <c>SimWorld</c> 의 무제한 채널이 프로세스 수명 내내 자란다.
/// </para>
///
/// <para>
/// <b>틱 스레드는 하나다.</b> accept 루프 둘(<see cref="LinkListener"/>·<see cref="ClientListener"/>)과
/// 세션별 리시버 태스크가 따로 돌지만, 그들은 링에만 넣는다 — 월드와 소켓 쓰기는 전부 여기다.
/// </para>
/// </summary>
public sealed class GameServer : IAsyncDisposable
{
    /// <summary><c>--headless</c> 일 때 통계를 찍는 주기(틱). 5초다.</summary>
    public const int StatsPeriodTicks = GameWorld.TickRate * 5;

    /// <summary>존 상태를 다시 보내는 최대 간격(틱). 5초다 (docs/20 §8.1). 변화가 있으면 즉시 나간다.</summary>
    public const int ZoneStatesPeriodTicks = GameWorld.TickRate * 5;

    /// <summary>링크 상태 주기(틱). 1초다 (docs/20 §8.1).</summary>
    public const int LinkStatusPeriodTicks = GameWorld.TickRate;

    private readonly GameServerOptions _options;
    private readonly MasterDataSet _data;
    private readonly GameWorld _world;
    private readonly PlayerRegistry _players;
    private readonly MirrorLog _mirror;
    private readonly ControlHandler _controls;
    private readonly SnapshotBuilder _snapshots;
    private readonly LinkListener _link;
    private readonly ClientListener _clients;
    private readonly TextWriter _log;
    private readonly TickMeter _meter = new();

    /// <summary>지금까지 붙었던 세션의 누계. 세션이 갈려도 종료 요약이 총계를 말하게 한다.</summary>
    private long _commandsIn;
    private long _eventsOut;

    /// <summary>지금 세션에서 이미 누계에 옮긴 지점. 두 번 세지 않게 하는 기준선이다.</summary>
    private long _commandBase;
    private long _eventBase;

    /// <summary>이벤트 관측자를 이미 붙인 세션. 재접속하면 새 세션에 다시 붙인다.</summary>
    private LinkSession? _observed;

    /// <summary>
    /// 존별 지역 상태·기후. 첨자는 존 code 다.
    ///
    /// <b>게임서버가 이것을 들고 있어야 하는 이유.</b> <c>SimWorld</c> 는 존 상태를 저장하지 않는다 —
    /// <c>ZoneStateChanged</c> 는 그냥 지나가는 이벤트다. 클라이언트에 <c>ZoneStates</c> 를
    /// 보내려면 지나가는 것을 누군가 붙잡아야 하고, 그 자리가 여기다.
    /// </summary>
    private readonly byte[] _zoneRegion;
    private readonly byte[] _zoneClimate;

    /// <summary>존 상태가 바뀌었다. 다음 방송에 실린다.</summary>
    private bool _zonesDirty = true;

    /// <summary>마지막으로 존 상태를 보낸 틱.</summary>
    private long _zonesSentAt = long.MinValue;

    /// <summary>NPC 서버가 마지막으로 알린 틱. 명령의 <c>IssuedAt</c> 에서 딴다.</summary>
    private long _npcServerTick;

    private GameServer(
        GameServerOptions options,
        MasterDataSet data,
        GameWorld world,
        PlayerRegistry players,
        MirrorLog mirror,
        ControlHandler controls,
        SnapshotBuilder snapshots,
        LinkListener link,
        ClientListener clients,
        TextWriter log)
    {
        _options = options;
        _data = data;
        _world = world;
        _players = players;
        _mirror = mirror;
        _controls = controls;
        _snapshots = snapshots;
        _link = link;
        _clients = clients;
        _log = log;

        int max = 0;

        foreach (ZoneDef zone in data.Zones.Zones)
        {
            max = Math.Max(max, zone.Code.Value);
        }

        _zoneRegion = new byte[max + 1];
        _zoneClimate = new byte[max + 1];

        // 마스터데이터의 기본값에서 시작한다. 이것을 안 하면 zones.json 이 Alert·Cold 로
        // 선언한 존이 첫 방송에서 Peace·Fair 로 보인다 (docs/20 §5.5 의 2026-07-28 보정과 같은 사고다).
        foreach (ZoneDef zone in data.Zones.Zones)
        {
            _zoneRegion[zone.Code.Value] = (byte)zone.DefaultRegionState;
            _zoneClimate[zone.Code.Value] = (byte)zone.DefaultClimate;
        }

        world.CommandObserver = ObserveCommand;
        clients.Controls = _controls;
    }

    /// <summary>월드. 테스트가 상태를 들여다보는 통로다.</summary>
    public GameWorld World => _world;

    /// <summary>마스터데이터.</summary>
    public MasterDataSet Data => _data;

    /// <summary>플레이어 등록기.</summary>
    public PlayerRegistry Players => _players;

    /// <summary>명령·이벤트 미러. 클라이언트 로그 패널의 원천이다 (docs/20 §7.4).</summary>
    public MirrorLog Mirror => _mirror;

    /// <summary>제어 처리기. 클라이언트의 <c>Control</c> 이 여기로 간다 (docs/20 §8.3).</summary>
    public ControlHandler Controls => _controls;

    /// <summary>스냅샷 빌더.</summary>
    public SnapshotBuilder Snapshots => _snapshots;

    /// <summary>NPC 서버 링크 수신기.</summary>
    public LinkListener Link => _link;

    /// <summary>클라이언트 수신기.</summary>
    public ClientListener Clients => _clients;

    /// <summary>링크 포트. <see cref="Start"/> 뒤부터 유효하다.</summary>
    public int LinkPort => _link.Port;

    /// <summary>클라이언트 포트. <see cref="Start"/> 뒤부터 유효하다.</summary>
    public int ClientPort => _clients.Port;

    /// <summary>
    /// 지금 틱 번호. <c>SkipTime</c> 때문에 <see cref="GameWorld.TicksProcessed"/> 보다 클 수 있다.
    /// </summary>
    public long Tick => _world.Now.Value;

    /// <summary><c>SkipTime</c> 으로 건너뛴 틱 수의 누계 (docs/20 §8.3).</summary>
    public long TicksSkipped { get; private set; }

    /// <summary>링크가 없는 동안 배수해 버린 이벤트 수. 미러에는 남는다.</summary>
    public long OrphanEvents { get; private set; }

    /// <summary>틱 처리 시간 p99(ms). 예산은 10ms 다 (docs/20 §14.1).</summary>
    public double TickP99Millis => _meter.Percentile(0.99);

    /// <summary>
    /// 옵션대로 전부 조립한다. 기동 시 1회.
    ///
    /// <para>
    /// <b>로스터를 <c>Npc.Host</c> 와 같은 순서로 뽑는다</b> (docs/20 §10.2) — 존 필터를 먼저 풀고,
    /// 모르는 존 id 와 0 마리는 <b>기동 실패</b>다. 조용히 넘기면 사고가 핸드셰이크 거절로만
    /// 나타나 "새로 만든 게임서버가 이상하다" 로 오해하기 쉽다.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">모르는 존 id 이거나 뽑힌 NPC 가 0 마리다.</exception>
    public static GameServer Create(GameServerOptions options, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        string masterDataDir = options.ResolveMasterData();
        MasterDataSet data = MasterDataLoader.Load(masterDataDir);
        NpcInstanceTable instances = NpcInstanceTable.Load(
            Path.Combine(masterDataDir, "npc_instances.json"), data);

        // A-08 — 샤드가 있으면 존 목록·비트마스크가 여기서 온다.
        ShardDef? shard = ShardOf(options, data);
        ZoneId[] zoneFilter = ZoneFilter(options, data, shard);

        if (shard is { } shardDef)
        {
            options = options with { ZoneMask = shardDef.Mask };

            log.WriteLine(
                $"shard {shardDef.Shard}: 존 {shardDef.Zones.Length}개 · mask 0x{shardDef.Mask:x}");
        }

        NpcRoster roster = NpcRoster.Select(instances, options.Npcs, zoneFilter);

        if (roster.Count == 0)
        {
            throw new ArgumentException(
                $"--zone {string.Join(",", options.Zones)} 에 해당하는 NPC 가 npc_instances.json 에 없다.",
                nameof(options));
        }

        if (roster.Count < options.Npcs)
        {
            string pool = zoneFilter.Length == 0
                ? $"npc_instances.json 에 {instances.Count} 마리뿐이라"
                : $"--zone {string.Join(",", options.Zones)} 안에 {roster.Count} 마리뿐이라";

            log.WriteLine($"warn: {pool} {roster.Count} 로 줄였다.");
        }

        GameWorld world = GameWorld.Create(options, data, roster, instances);
        var mirror = new MirrorLog();
        // D-03 — 대역이 기억을 쓴다. NPC 서버에 같은 폴더를 주면 그 결과가 밴드로 읽힌다.
        FileMemoryStore? memory = options.MemoryDir is { Length: > 0 } memoryDir
            ? FileMemoryStore.Open(memoryDir)
            : null;

        if (memory is not null)
        {
            log.WriteLine($"memory: {memory.Path} · 관계 {memory.RelationshipCount}");
        }

        var players = new PlayerRegistry(world, data, options) { Memory = memory };

        world.Players = players.Tick;

        // ControlHandler 는 GameWorld.Create 뒤에 만든다 — SimWorld.Handler 앞에 서기 때문이다.
        var controls = new ControlHandler(world, data, players);
        var snapshots = new SnapshotBuilder(world, data, players, options.TimeScale);

        return new GameServer(
            options,
            data,
            world,
            players,
            mirror,
            controls,
            snapshots,
            new LinkListener(world, data, options),
            new ClientListener(world, data, players, options),
            log);
    }

    /// <summary>두 수신기를 바인드한다. <see cref="LinkPort"/>·<see cref="ClientPort"/> 는 이 뒤부터 유효하다.</summary>
    public void Start()
    {
        _link.Start();
        _clients.Start();
    }

    /// <summary>
    /// accept 루프 둘을 함께 돈다. 취소될 때까지 끝나지 않는다.
    ///
    /// <b><see cref="Start"/> 를 먼저 부른다.</b> 테스트는 이 메서드를 안 부르고
    /// 소켓 없이 <see cref="TickAsync"/> 만 돌릴 수도 있다.
    /// </summary>
    public Task AcceptAsync(CancellationToken ct) => Task.WhenAll(
        Task.Run(() => _link.RunAsync(ct), CancellationToken.None),
        Task.Run(() => _clients.RunAsync(ct), CancellationToken.None));

    /// <summary>
    /// 조립 → 수신 시작 → 100ms 페이싱 루프. 취소될 때까지 돈다.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Start();

        Task accepts = AcceptAsync(ct);

        try
        {
            await LoopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl-C. 정상 종료다.
        }

        try
        {
            await accepts.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 수신기는 취소·소켓 종료로 끝난다. 종료 경로에서 그것을 사고로 세지 않는다.
        }
    }

    /// <summary>
    /// 한 틱. docs/20 §7.2 의 1~9단계다. 10단계(클라이언트 방송)는 T6-38 이 여기 붙는다.
    ///
    /// <para>
    /// <b><see cref="ClientListener.TickAsync"/> 가 <see cref="GameWorld.Tick"/> 앞에 있다.</b>
    /// §7.2 는 그것을 3단계(<c>PlayerRegistry.Tick</c>)와 같은 칸에 두는데, 그 자리는
    /// <see cref="GameWorld.Tick"/> <b>안</b>이고 거기서는 <c>await</c> 를 할 수 없다 —
    /// 참여 인사와 <c>Pong</c> 이 소켓 쓰기이기 때문이다. 바로 앞으로 옮기면 이번 틱에 도착한
    /// 입력이 이번 틱의 3단계 이동에 반영된다는 §7.2 의 뜻은 그대로 지켜진다.
    /// 순서가 실제로 바뀌는 것은 <b>플레이어 이벤트가 명령 응답보다 앞서 채널에 들어가는 것</b>
    /// 하나이고, 둘은 서로 다른 상관 ID 를 가지므로 NPC 서버 쪽 반영 결과가 달라지지 않는다.
    /// </para>
    /// </summary>
    public async Task TickAsync(Tick now, CancellationToken ct)
    {
        // 3단계의 클라이언트 몫 — 참여 인사 · 입력 적용 · 죽은 세션 회수.
        await _clients.TickAsync(now, ct).ConfigureAwait(false);

        // 1~8단계.
        _world.Tick(now);

        // 9단계.
        await FlushLinkAsync(now, ct).ConfigureAwait(false);

        // 10단계.
        await BroadcastAsync(now, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 종료 요약 한 줄. docs/20 §7.5.
    ///
    /// <b>세션이 갈려도 총계를 말한다</b> — 재접속한 회차에서 마지막 세션의 값만 찍으면
    /// "명령이 거의 안 왔다" 로 읽힌다.
    /// </summary>
    public string Summarize()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"ticks {_world.TicksProcessed} (tick {Tick}, skipped {TicksSkipped}) | " +
            $"commands in {_commandsIn} (applied {_world.CommandsApplied}, dropped {_world.Inbox.Dropped}) | " +
            $"events out {_eventsOut} (orphan {OrphanEvents}) | " +
            $"sessions {_link.SessionsAccepted} | clients {_clients.SessionsAccepted} | " +
            $"p99 tick {TickP99Millis:F2} ms");
    }

    /// <summary>진행 중 한 줄. <c>--headless</c> 회차에서 5초마다 찍는다.</summary>
    public string Status()
    {
        LinkSession? session = _link.Session;

        // D-03 — 저장소가 없으면 줄에서 아예 뺀다. 보간 문자열 안에서 조건을 접으면
        // string.Create 의 핸들러가 받지 못한다 (CS1620).
        string memory = _players.Memory is null
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture, $"memory {_players.MemoryWrites} | ");

        return string.Create(
            CultureInfo.InvariantCulture,
            $"tick {Tick} | link {(session is { IsActive: true } ? "up" : "down")} | " +
            $"cmd {_world.CommandsApplied} | ev {_world.World.EventsEmitted} | " +
            $"players {_players.Count} | clients {_clients.Count} | " +
            $"interact {_players.Interacts}/{_players.InteractsOutOfRange} | " +
            $"attack {_players.Attacks}/{_players.AttacksOutOfRange} | " +
            $"{memory}p99 {TickP99Millis:F2} ms");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _clients.DisposeAsync().ConfigureAwait(false);
        await _link.DisposeAsync().ConfigureAwait(false);
        await _world.DisposeAsync().ConfigureAwait(false);

        // D-03 — 기억을 확정한다. 여기서 안 쓰면 대역이 종료될 때 그 회차의 관계가 통째로 사라진다.
        if (_players.Memory is { } memory)
        {
            await memory.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- 루프

    /// <summary>
    /// 100ms 페이싱 루프.
    ///
    /// <para>
    /// <b>벽시계가 여기 있는 것은 규칙 위반이 아니다.</b> CLAUDE.md §2.3 이 금지하는 것은
    /// <b>게임 로직</b>의 벽시계다. 실시간에 맞춰 틱을 내보내는 일은 호스트의 몫이고,
    /// <c>Npc.Host</c> 의 펌프도 같은 자리에서 <see cref="Stopwatch"/> 를 쓴다.
    /// </para>
    ///
    /// <para>
    /// <b>매 틱 100ms 를 더하지 않는다.</b> 그러면 처리 시간이 누적 오차가 되어 게임 시각이
    /// 조금씩 뒤로 밀린다 — 기준은 언제나 시작 시각이다.
    /// </para>
    /// </summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();

        // 실제로 흐른 틱 수. SkipTime 으로 건너뛴 분은 여기 안 든다 —
        // 들면 건너뛴 만큼 벽시계를 기다리게 되어 "건너뛴다" 가 "멈춘다" 가 된다.
        long paced = 0;
        long tick = _world.Now.Value;

        while (!ct.IsCancellationRequested)
        {
            tick++;
            paced++;

            long began = Stopwatch.GetTimestamp();

            await TickAsync(new Tick(tick), ct).ConfigureAwait(false);

            _meter.Record(Stopwatch.GetTimestamp() - began);

            long skip = _controls.TakeTickSkip();

            if (skip > 0)
            {
                tick += skip;
                TicksSkipped += skip;
            }

            if (_options.Headless && paced % StatsPeriodTicks == 0)
            {
                _log.WriteLine(Status());
            }

            long wait = (paced * 1_000 / GameWorld.TickRate) - clock.ElapsedMilliseconds;

            if (wait > 0)
            {
                await Task.Delay((int)wait, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 9단계. 이벤트 채널을 비워 <c>EventBatch</c> 한 프레임으로 내보낸다.
    ///
    /// <para>
    /// <b>링크가 없으면 그냥 배수한다.</b> 채널이 무제한이라 안 비우면 프로세스 수명 내내 자라고,
    /// 그러다 NPC 서버가 붙는 순간 수십만 건이 한꺼번에 나간다. 어차피
    /// <see cref="LinkSession.ResyncAsync"/> 가 세션 전 이벤트를 버리므로 결과는 같다.
    /// 버리기 전에 미러에는 남긴다 — NPC 서버 없이 클라이언트만 띄운 회차에서도 로그 패널이 산다.
    /// </para>
    /// </summary>
    private async Task FlushLinkAsync(Tick now, CancellationToken ct)
    {
        LinkSession? session = _link.Session;

        if (session is not { IsActive: true, IsAccepted: true })
        {
            Accumulate();
            DrainOrphanEvents();

            return;
        }

        if (!ReferenceEquals(session, _observed))
        {
            Accumulate();

            session.EventObserver = ObserveEvent;

            // 링크가 없던 구간에 우리가 버린 분을 알려 준다. 안 알리면 세션의 EventsPending 이
            // 그만큼 부풀어 적체가 없는데 적체 압력이 보인다.
            session.ExternallyDrained = OrphanEvents;
            _observed = session;
        }

        try
        {
            if (session.NeedsResync)
            {
                await session.ResyncAsync(ct).ConfigureAwait(false);
            }

            await session.FlushEventsAsync(now, ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // 소켓이 죽었다. 자리를 비워 재접속을 받는다 — 세계는 계속 돈다 (docs/20 §7.2).
            await session.CloseAsync(LinkByeCode.Timeout, ct).ConfigureAwait(false);
        }

        Accumulate();
    }

    /// <summary>링크가 없는 구간의 이벤트를 미러에만 남기고 버린다.</summary>
    private void DrainOrphanEvents()
    {
        while (_world.World.Events.TryRead(out GameEvent ev))
        {
            ObserveEvent(in ev);
            OrphanEvents++;
        }
    }

    /// <summary>
    /// 10단계. 클라이언트 방송. docs/20 §7.2 · §8.1.
    ///
    /// <b>세션 하나가 던져도 나머지를 계속 돈다</b> — 창 하나가 닫힌 것이 세계를 멈출 이유가 아니다.
    /// 죽은 세션의 자리는 다음 틱의 3단계에서 회수된다.
    /// </summary>
    private async Task BroadcastAsync(Tick now, CancellationToken ct)
    {
        bool snapshotDue = SnapshotBuilder.DueAt(now);
        bool zonesDue = _zonesDirty || now.Value - _zonesSentAt >= ZoneStatesPeriodTicks;
        bool statusDue = now.Value % LinkStatusPeriodTicks == 0;

        if (!snapshotDue && !zonesDue && !statusDue)
        {
            return;
        }

        // 존 상태·링크 상태는 세션마다 같다. 한 번만 만든다.
        ZoneStates zones = default;
        bool zonesBuilt = false;
        LinkStatus status = statusDue ? BuildLinkStatus(now) : default;

        foreach (ClientSession session in _clients.Live)
        {
            try
            {
                if (snapshotDue)
                {
                    await session.SendSnapshotAsync(_snapshots.Build(session.Player, now), ct)
                        .ConfigureAwait(false);

                    // 로그는 스냅샷 뒤다 — AOI 집합이 그 안에서 갱신된다 (docs/20 §7.4).
                    await session.SendLogsAsync(_mirror, ct).ConfigureAwait(false);
                }

                // 방금 붙은 세션은 주기를 기다리지 않는다. 주기만 보면 최대 5초 동안
                // 회색 지도를 보게 되고, 사람은 그것을 "제어가 안 먹는다" 로 읽는다.
                if (zonesDue || session.NeedsZoneStates)
                {
                    if (!zonesBuilt)
                    {
                        zones = BuildZoneStates(now);
                        zonesBuilt = true;
                    }

                    await session.SendZoneStatesAsync(zones, ct).ConfigureAwait(false);
                }

                if (statusDue)
                {
                    await session.SendLinkStatusAsync(status, ct).ConfigureAwait(false);
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // 소켓이 죽었다. 다음 틱 3단계가 자리를 회수한다.
                session.Close();
            }
        }

        if (zonesDue)
        {
            _zonesSentAt = now.Value;
            _zonesDirty = false;
        }
    }

    /// <summary>지금 존 상태 한 장. 존 수만큼이라 짧다.</summary>
    private ZoneStates BuildZoneStates(Tick now)
    {
        var zones = new ZoneState[_data.Zones.Zones.Length];

        for (int i = 0; i < zones.Length; i++)
        {
            ZoneDef zone = _data.Zones.Zones[i];

            zones[i] = new ZoneState
            {
                ZoneCode = zone.Code.Value,
                RegionState = _zoneRegion[zone.Code.Value],
                Climate = _zoneClimate[zone.Code.Value],
            };
        }

        return new ZoneStates { Tick = now.Value, Zones = zones };
    }

    /// <summary>
    /// 링크 상태 한 장. docs/20 §8.1 · §9.2.
    ///
    /// <para>
    /// <b><c>NpcServerTick</c> 은 명령의 <c>IssuedAt</c> 에서 딴다.</b> 하트비트에는 없다 —
    /// <c>TcpGameServerLink</c> 는 런타임의 틱을 모르고(<c>IGameServerLink</c> 에 그런 것이 없다),
    /// 넣으려면 계약을 바꿔야 한다. 대신 <b>NPC 서버가 조용하면 이 값이 늙는다</b> —
    /// 상태바의 지연이 커지는 것으로 보이는데, 그것도 정보다.
    /// </para>
    ///
    /// <para>
    /// <b><c>Gaps</c> 는 우리가 낸 시퀀스 중 링크로 안 나간 수다.</b> 세션 전에 쌓여 버려진 분과
    /// 링크 없는 구간의 분이 여기 든다 — NPC 서버의 <c>EventGapsDetected</c> 가 셀 값과 같다.
    /// </para>
    /// </summary>
    private LinkStatus BuildLinkStatus(Tick now)
    {
        LinkSession? session = _link.Session;
        long pending = session is { IsActive: true } ? session.EventsPending : 0;

        return new LinkStatus
        {
            GsTick = now.Value,
            NpcServerTick = _npcServerTick,
            CommandsIn = _commandsIn,
            EventsOut = _eventsOut,
            Dropped = _world.Inbox.Dropped,
            Gaps = Math.Max(0, _world.World.EventsEmitted - _eventsOut - pending),
            Connected = (byte)(session is { IsActive: true, IsAccepted: true } ? 1 : 0),
        };
    }

    /// <summary>
    /// 1단계에서 적용되는 명령 하나. 미러에 남기고 NPC 서버의 틱을 딴다.
    /// </summary>
    private void ObserveCommand(in NpcCommand command, Tick now)
    {
        // A-08 — 뷰어는 슬롯으로 본다. 와이어의 전역 id 를 여기서 되돌린다.
        _mirror.Record(in command, _world.World.SlotOf(command.Npc.Value), now);

        if (command.IssuedAt.Value > _npcServerTick)
        {
            _npcServerTick = command.IssuedAt.Value;
        }
    }

    /// <summary>
    /// 나가는 이벤트 하나. 미러에 남기고 존 상태를 붙잡는다.
    ///
    /// <b>여기가 존 상태를 아는 유일한 자리다</b> — <c>SimWorld</c> 는 저장하지 않고,
    /// 이벤트는 링크 세션이 유일한 독자라 미러가 따로 읽을 수 없다.
    /// </summary>
    private void ObserveEvent(in GameEvent ev)
    {
        _mirror.Record(in ev, _world.World.SlotOf(ev.Npc.Value));

        int zone = ev.Zone.Value;

        if ((uint)zone >= (uint)_zoneRegion.Length)
        {
            return;
        }

        switch (ev.Kind)
        {
            case GameEventKind.ZoneStateChanged:
                _zoneRegion[zone] = ev.Code;
                _zonesDirty = true;
                break;

            case GameEventKind.WeatherChanged:
                _zoneClimate[zone] = ev.Code;
                _zonesDirty = true;
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// 지금 세션의 카운터를 누계에 옮긴다. 세션이 갈리기 <b>전</b>에 부른다.
    ///
    /// <b>두 번 세지 않는다</b> — 옮긴 만큼을 <see cref="_observed"/> 별 기준선으로 빼고 더한다.
    /// </summary>
    private void Accumulate()
    {
        if (_observed is not { } session)
        {
            return;
        }

        _commandsIn += session.CommandsReceived - _commandBase;
        _eventsOut += session.EventsSent - _eventBase;
        _commandBase = session.CommandsReceived;
        _eventBase = session.EventsSent;

        if (!session.IsActive)
        {
            _observed = null;
            _commandBase = 0;
            _eventBase = 0;
        }
    }

    // ---------------------------------------------------------------- 조립 보조

    /// <summary>
    /// <c>--zone</c> 을 존 code 로 푼다.
    ///
    /// <b>모르는 id 는 기동 실패다.</b> <c>Npc.Host</c> 와 같은 판단이다 (docs/20 §10.3) —
    /// 한쪽만 조용히 넘기면 로스터가 어긋나고, 증상은 핸드셰이크 거절 하나로만 나타난다.
    /// </summary>
    /// <summary>
    /// <c>--shard</c> 를 샤드 정의로 푼다 (A-08). 0 이면 null — 단일 샤드다.
    /// <b>모르는 샤드 번호는 기동 실패다.</b>
    /// </summary>
    private static ShardDef? ShardOf(GameServerOptions options, MasterDataSet data)
    {
        if (options.Shard == 0)
        {
            return null;
        }

        string path = options.ShardsPath;

        if (!File.Exists(path))
        {
            for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
                 dir is not null;
                 dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, options.ShardsPath);

                if (File.Exists(candidate))
                {
                    path = candidate;
                    break;
                }
            }
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"--shard {options.Shard} 인데 샤드 정의가 없다: {options.ShardsPath}", options.ShardsPath);
        }

        ShardTable table = ShardTable.Load(path, data);

        return table.TryGet((ushort)options.Shard)
            ?? throw new ArgumentException(
                $"--shard {options.Shard} 가 {path} 에 없다. "
                + $"있는 것: {string.Join(", ", table.Shards.Select(sd => sd.Shard))}",
                nameof(options));
    }

    private static ZoneId[] ZoneFilter(GameServerOptions options, MasterDataSet data, ShardDef? shard)
    {
        // A-08 — 샤드가 존 목록을 이미 담고 있다. --zone 은 무시한다.
        if (shard is { } def)
        {
            return [.. def.Zones];
        }

        if (options.Zones.IsDefaultOrEmpty)
        {
            return [];
        }

        var zones = new ZoneId[options.Zones.Length];

        for (int i = 0; i < zones.Length; i++)
        {
            string id = options.Zones[i];

            if (!data.Zones.TryGet(id, out ZoneDef zone))
            {
                throw new ArgumentException(
                    $"--zone '{id}' 를 zones.json 에서 못 찾았다. "
                    + $"있는 것: {string.Join(", ", data.Zones.Zones.Select(z => z.Id))}",
                    nameof(options));
            }

            zones[i] = zone.Code;
        }

        return zones;
    }

    /// <summary>
    /// 틱 처리 시간 분포. docs/20 §14.1 의 "게임서버 틱 p99 ≤ 10ms" 를 종료 요약이 말하게 한다.
    ///
    /// <b>표본을 배열에 쌓지 않는다.</b> 하루 회차면 수십만 개가 되고, 그것을 정렬하는 비용을
    /// 종료 경로에서 치를 이유가 없다. 0.05ms 폭 히스토그램이면 예산 판정에 충분하다.
    /// </summary>
    private sealed class TickMeter
    {
        private const int Buckets = 2_048;
        private const double MillisPerBucket = 0.05;

        /// <summary>마지막 칸은 넘침이다 — 102.4ms 를 넘는 틱은 예산 문제이지 분해능 문제가 아니다.</summary>
        private readonly long[] _counts = new long[Buckets + 1];

        private long _samples;

        public void Record(long elapsedTimestamps)
        {
            double millis = elapsedTimestamps * 1_000.0 / Stopwatch.Frequency;
            int bucket = (int)(millis / MillisPerBucket);

            _counts[Math.Clamp(bucket, 0, Buckets)]++;
            _samples++;
        }

        public double Percentile(double quantile)
        {
            if (_samples == 0)
            {
                return 0;
            }

            long target = (long)Math.Ceiling(quantile * _samples);
            long seen = 0;

            for (int i = 0; i <= Buckets; i++)
            {
                seen += _counts[i];

                if (seen >= target)
                {
                    // 칸의 위 경계를 답한다. 아래로 답하면 예산 초과를 통과로 읽는다.
                    return (i + 1) * MillisPerBucket;
                }
            }

            return (Buckets + 1) * MillisPerBucket;
        }
    }
}
