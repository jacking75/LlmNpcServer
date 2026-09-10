using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Npc.Contracts;
using Npc.Core;
using Npc.Gateway;
using Npc.Host;
using Npc.Host.Api;
using Npc.Host.Commands;
using Npc.Host.Metrics;
using Npc.Host.Observability;
using Npc.Host.Persistence;
using Npc.Host.Replan;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;
using Npc.Sim;
using Npc.Wire;

// ASP.NET 의 Microsoft.Extensions.Hosting.HostOptions 와 이름이 겹친다. 우리 것을 쓴다.
using HostOptions = Npc.Host.HostOptions;

// 조립 루트. docs/11 §11 · README §주요 실행 옵션.
//
// 여기가 유일하게 "전부를 아는" 곳이다. Npc.Runtime 은 Npc.Llm 도 Npc.Sim 도 모른다.
// 링크 구현체는 --link 로만 갈린다 — 아래 어느 코드도 구현체를 이름으로 알지 못한다.

if (args.Length > 0 && args[0] == ValidateCommand.Name)
{
    return ValidateCommand.Run(args[1..], Console.Out);
}

// 컨테이너 HEALTHCHECK 진입점 (A-09). aspnet 이미지에 curl 이 없어서 .NET 으로 친다.
if (args.Length > 0 && args[0] == HealthCheckCommand.Name)
{
    return HealthCheckCommand.Run(args[1..], Console.Out);
}

// 설정은 세 겹이다 (A-04): CLI > 환경변수(NPC_*) > 설정 파일(npc.settings.json) > 기본값.
// 배포 시스템이 ConfigMap·시크릿으로 넘길 길이 없으면 옵션 30개가 전부 손으로 친 명령줄이 된다.
if (!HostOptions.TryParseLayered(args, env: null, out HostOptions options, out string? parseError))
{
    Console.Error.WriteLine(parseError);
    Console.Error.WriteLine();
    Console.Error.WriteLine(HostOptions.Usage);
    return 2;
}

if (options.Help)
{
    Console.Out.WriteLine(HostOptions.Usage);
    return 0;
}

// 경로는 여기서 한 번에 푼다. 아래 코드는 절대경로만 다룬다.
// 경로 오타는 사용자 입력 문제이므로 미처리 예외로 스택트레이스를 쏟지 않는다.
if (!options.TryResolvePaths(out HostOptions resolved, out string? pathError))
{
    Console.Error.WriteLine(pathError);
    return 2;
}

options = resolved;

// 와일드카드 바인드는 관리 토큰이 있을 때만 연다 (A-04). 토큰 없이 0.0.0.0 을 열면
// 킬스위치·리로드를 누구나 부를 수 있는 포트가 생긴다.
if (!options.TryValidateBind(Environment.GetEnvironmentVariable("NPC_ADMIN_TOKEN"), out string? bindError))
{
    Console.Error.WriteLine(bindError);
    return 2;
}

if (options.LoadedConfigPath is { } configPath)
{
    Console.Out.WriteLine($"config: {configPath} (profile {options.Profile.ToString().ToLowerInvariant()})");
}

using var lifetime = new CancellationTokenSource();

// SIGTERM·SIGINT·SIGQUIT 을 한 취소로 모은다 (A-02).
// docker stop · k8s 종료 · Windows 서비스 정지는 전부 SIGTERM 이다 — 예전에는 SIGINT 만
// 처리해 컨테이너에서 드레인 없이 즉사했다.
using IDisposable signals = HostShutdown.RegisterSignals(lifetime, Console.Out);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    lifetime.Cancel();
};

// --days 기본값 1 은 실험용이다. 배속 60 이면 실시간 24분 뒤 exit 0 으로 조용히 사라진다 (A-02).
if (options.Profile == HostProfile.Dev && !options.DaysSpecified && options.Days > 0)
{
    Console.Out.WriteLine(
        $"warn: --days {options.Days} 는 실험용 기본값이다. "
        + "서비스로 띄울 때는 --profile service (--days 0 강제) 를 쓴다.");
}

await using var host = NpcHost.Create(options, Console.Out);

// ── 링크 결함 감시 (A-03) ────────────────────────────────────────
// Faulted 는 사람이 고쳐야 하는 상태다. 그대로 두면 소켓 태스크는 끝나고 틱 루프는 이벤트를
// 기다리며 영원히 블록되는데, 프로세스는 살아 있어 오케스트레이터가 재시작하지 않는다.
int exitCode = 0;

using var faultPolicy = new LinkFaultPolicy(
    options.OnLinkFault,
    options.FaultGraceSeconds,
    () => host.LinkRejectReason,
    (code, reason) =>
    {
        exitCode = code;
        Console.Error.WriteLine($"link-fault: {reason} (exit {code})");
        lifetime.Cancel();
    });

faultPolicy.Attach(host.Link);

host.Link.StateChanged += state => Console.Out.WriteLine($"link: {state}");

if (options.NoDashboard && options.HealthPort is null)
{
    int headless = await host.RunAsync(lifetime.Token, stopWeb: null, Console.Out);

    host.Report(Console.Out);
    return exitCode != 0 ? exitCode : headless;
}

// ContentRoot 기본값은 <b>작업 폴더</b>다. 그러면 wwwroot/dashboard.html 을 찾는 자리가
// "어디서 실행했는가"에 따라 달라져, 같은 빌드가 dotnet run 에서는 뜨고
// dll 을 직접 실행하면 /dashboard 가 404 가 된다. 실행 파일 폴더로 고정한다 —
// appsettings.json 도 출력 폴더에 있으므로 같이 안정된다.
WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    ContentRootPath = AppContext.BaseDirectory,
});

// --health-port 를 주면 프로브 전용 리스너를 하나 더 연다 (A-03).
// 대시보드·질의는 인증 뒤로 가고(A-06) 프로브만 무인증으로 남아야 하므로 포트를 가를 수 있다.
var bindings = new List<string>(2);

if (!options.NoDashboard)
{
    bindings.Add($"http://{options.Bind}:{options.Port}");
}

if (options.HealthPort is { } healthPort && healthPort != options.Port)
{
    bindings.Add($"http://{options.Bind}:{healthPort}");
}
else if (options.NoDashboard)
{
    bindings.Add($"http://{options.Bind}:{options.HealthPort ?? options.Port}");
}

builder.WebHost.UseUrls([.. bindings]);

// 로그는 형식이 갈린다 (A-05). 운영은 JSON 한 줄, 개발은 평문이다.
// ASP.NET 자체 로그는 여전히 Warning 이상만 — Information 을 켜면 요청마다 두 줄이 나온다.
builder.Logging.AddNpcLogging(options.LogFormat, LogLevel.Warning);

// 계측기 13종을 실제로 수집한다 (A-05). 아무도 수집하지 않으면 배선 비용을 내지 않는다.
builder.Services.AddNpcTelemetry(
    instanceId: $"{Environment.MachineName}:{options.Port}",
    otlpEndpoint: options.OtlpEndpoint is { } otlp ? new Uri(otlp) : null,
    prometheus: options.Prometheus);

WebApplication app = builder.Build();

// Prometheus 스크레이프 (A-05). 기존 /metrics JSON 은 그대로 둔다 —
// 대시보드가 그것을 쓰고, 브라우저 하나 띄우자고 수집기를 세울 이유가 없다.
if (options.Prometheus)
{
    app.MapPrometheusScrapingEndpoint(Telemetry.PrometheusRoute);
    Console.Out.WriteLine($"prometheus: http://{options.Bind}:{options.Port}{Telemetry.PrometheusRoute}");
}

// ── 헬스체크 (A-03) ─────────────────────────────────────────────
// --no-dashboard 여도 --health-port 를 주면 이 세 라우트만 뜬다.
app.MapGet(HealthEndpoints.LiveRoute, () => Probe(host.Live()));
app.MapGet(HealthEndpoints.ReadyRoute, () => Probe(host.Ready()));
app.MapGet(HealthEndpoints.StartupRoute, () => Probe(host.Startup()));

if (options.NoDashboard)
{
    await app.StartAsync(CancellationToken.None);
    Console.Out.WriteLine($"health: http://{options.Bind}:{options.HealthPort}{HealthEndpoints.LiveRoute}");

    int probeOnly = await host.RunAsync(lifetime.Token, StopWeb, Console.Out);

    host.Report(Console.Out);
    return exitCode != 0 ? exitCode : probeOnly;
}

app.MapGet("/", () => Results.Redirect("/dashboard"));
app.MapGet("/status", () => host.Snapshot());
app.MapGet("/metrics", () => host.Metrics.Snapshot());

// NPC 추적 (T4-21). SoA 배열을 읽기만 하고 값을 복사해 나간다 — 틱 루프를 막지 않는다.
app.MapGet(NpcTraceEndpoint.Route, (int id) => host.Trace(id));

// 버킷 히트맵 원자료 (T4-22). W12 보고서 §6 "발견" 의 원자료라 CSV 로도 뽑을 수 있게 둔다 —
// /metrics 의 JSON 은 대시보드용이고, 이쪽은 T5-18 이 파일로 받아 가는 경로다.
app.MapGet("/heatmap.csv", () => Results.Text(
    host.HeatmapCsv(), "text/csv; charset=utf-8"));

// 대시보드 1차. docs/11 §10. 단일 HTML 이고 /metrics 를 폴링한다.
app.MapGet("/dashboard", () =>
{
    IFileInfo file = app.Environment.WebRootFileProvider.GetFileInfo("dashboard.html");

    return file.Exists
        ? Results.File(file.CreateReadStream(), "text/html; charset=utf-8")
        : Results.NotFound($"dashboard.html 이 없다: {app.Environment.WebRootPath}");
});

// 데모용 제어 (T6-13 · docs/20 §11.4). --dev-control 일 때만 <b>등록 자체를 한다</b> —
// 플래그 없이 기동하면 라우트가 없으므로 /control/* 은 404 다.
// 조건부 401/403 이 아니라 조건부 등록인 이유: 상태를 바꾸는 HTTP 를 기본으로 열어 두면
// 대시보드가 열려 있는 동안 누구든 티어를 끊을 수 있다.
//
// <b>이벤트 주입은 없다.</b> 그건 게임서버의 일이고, 여기서 같이 내면 세계가 두 번 밀린다.
if (options.DevControl)
{
    app.MapPost("/control/killswitch", (string? target) =>
    {
        if (!KillSwitchState.TryParse(target, out KillSwitchTarget parsed))
        {
            return Results.BadRequest(
                $"target 이 {KillSwitchState.TargetNames} 중 하나여야 한다: {target ?? "(없음)"}");
        }

        host.Switches.Fire(parsed);

        // 멱등이다 (N7). 두 번 눌러도 같은 상태라 200 을 그대로 준다.
        return Results.Ok(new { target = parsed.ToString(), fired = true });
    });

    Console.Out.WriteLine("dev-control: POST /control/killswitch?target=T2|T1|PlanStore");
}

await app.StartAsync(CancellationToken.None);
Console.Out.WriteLine($"dashboard: http://{options.Bind}:{options.Port}/dashboard");

int shutdownCode = await host.RunAsync(lifetime.Token, StopWeb, Console.Out);

host.Report(Console.Out);

return exitCode != 0 ? exitCode : shutdownCode;

// 종료 시퀀스 5단계 — 웹 호스트를 세운다 (A-02).
async Task<string> StopWeb(TimeSpan budget)
{
    using var deadline = new CancellationTokenSource(budget);

    await app.StopAsync(deadline.Token);
    return "웹 호스트 정지";
}

// 프로브 응답. fail 만 503 이고 나머지는 200 이다 — degraded 에 503 을 주면
// 오케스트레이터가 "돌고는 있지만 완전하지 않은" 인스턴스를 로테이션에서 빼 버린다.
static IResult Probe(HealthReport report) =>
    Results.Json(report, statusCode: HealthEndpoints.StatusCode(report));

/// <summary>
/// 조립된 NPC 서버 한 벌. docs/11 §11.
///
/// <b>Sim 은 틱 루프와 다른 스레드에서 돈다.</b> 게임서버 대역의 이벤트 채널은 단일 기록자라
/// 틱 루프가 <c>FlushAsync</c> 중에 직접 <c>ApplyCommand</c> 를 부르면 기록자가 둘이 된다.
/// 명령은 <see cref="CommandRing"/> 을 거쳐 드라이버 쪽에서 적용한다 — 실제 게임서버가
/// 자기 스레드에서 수신 패킷을 처리하는 것과 같은 모양이고, 덤으로 할당이 0 이다.
/// </summary>
internal sealed class NpcHost : IAsyncDisposable
{
    /// <summary>드라이버가 틱 루프보다 앞설 수 있는 틱 수. 1 이면 사실상 락스텝이다.</summary>
    private const int MaxLeadTicks = 1;

    private readonly HostOptions _options;
    private readonly IGameServerLink _link;
    private readonly NpcServerLoop _loop;
    private readonly GameClock _clock;
    private readonly PlanExecutor _executor;
    private readonly CognitionScheduler _cognition;
    private readonly InterruptMatcher _interrupts;
    private readonly ReplanQueue _replanQueue;
    private readonly NpcStore _store;
    private readonly PlanStore _plans;
    private readonly MasterDataSet _data;
    private readonly NpcMeter _meter;
    private readonly SimDriver? _driver;
    private readonly NullGameServerLink? _nullLink;

    /// <summary>
    /// 소켓 링크. <c>--link tcp</c>(와 그것을 감싼 <c>--link record</c>)일 때만 있다.
    ///
    /// <b>이것을 따로 들고 있어야 하는 이유.</b> 접속·재접속 루프는 <c>IGameServerLink</c> 에
    /// 없는 동작이라(계약이 <c>Enqueue</c>·<c>FlushAsync</c>·<c>Events</c> 뿐이다 — docs/02 §1)
    /// 누군가는 구체 타입으로 <c>RunAsync</c> 를 돌려야 한다. <c>--link record</c> 는
    /// 데코레이터라 <see cref="_link"/> 로는 안쪽 소켓에 닿을 수 없다.
    /// </summary>
    private readonly TcpGameServerLink? _tcp;
    private readonly TierWiring _tiers;
    private readonly KillSwitchState _switches;
    private readonly long _totalTicks;
    private readonly SnapshotWriter? _snapshots;
    private readonly TickSyncWatchdog _tickSync;
    private readonly IAlarmSink _alarms;

    private NpcHost(
        HostOptions options,
        IGameServerLink link,
        NpcServerLoop loop,
        GameClock clock,
        PlanExecutor executor,
        CognitionScheduler cognition,
        InterruptMatcher interrupts,
        ReplanQueue replanQueue,
        NpcStore store,
        PlanStore plans,
        MasterDataSet data,
        NpcMeter meter,
        SimDriver? driver,
        NullGameServerLink? nullLink,
        TcpGameServerLink? tcp,
        long totalTicks,
        NpcRoster roster,
        TierWiring tiers,
        KillSwitchState switches,
        SnapshotWriter? snapshots,
        IAlarmSink alarms)
    {
        _options = options;
        _link = link;
        _loop = loop;
        _clock = clock;
        _executor = executor;
        _cognition = cognition;
        _interrupts = interrupts;
        _replanQueue = replanQueue;
        _store = store;
        _plans = plans;
        _data = data;
        _meter = meter;
        _tcp = tcp;
        _driver = driver;
        _nullLink = nullLink;
        _totalTicks = totalTicks;
        _tiers = tiers;
        _switches = switches;
        _snapshots = snapshots;
        _alarms = alarms;
        Roster = roster;

        // TickSync 워치독 (A-10). 게임서버가 틱을 멈추면 NPC 서버는 조용히 얼어붙는다.
        _tickSync = new TickSyncWatchdog(
            clock,
            options.TickSyncStallSeconds,
            message => alarms.Raise(new AlarmPayload(
                AlarmKind.TickSyncStalled, AlarmSeverity.Critical, "tick-sync", message)));

        // 게임서버가 알려준 시각을 시계에 예약한다 (A-10). 반영은 틱 경계에서 한다.
        //
        // <b>게임서버 값이 스냅샷보다 세다.</b> 세계를 미는 것은 게임서버이고,
        // NPC 서버가 다른 시각을 들고 있으면 스케줄 전체가 어긋난다.
        if (tcp is { } socket)
        {
            socket.StateChanged += state =>
            {
                if (state == Npc.Contracts.LinkState.Connected)
                {
                    clock.RequestOrigin(socket.StartTick, socket.StartGameMinuteOfDay);
                }

                // 상태 전이는 그 자체가 사건이다 (A-05). 쿨다운이 같은 상태의 반복을 접는다.
                alarms.Raise(new AlarmPayload(
                    AlarmKind.LinkState,
                    state is Npc.Contracts.LinkState.Faulted or Npc.Contracts.LinkState.Degraded
                        ? AlarmSeverity.Critical
                        : AlarmSeverity.Info,
                    state.ToString(),
                    $"링크 상태 {state}" + (socket.RejectCode != LinkRejectCode.None
                        ? $" — {socket.RejectCode}"
                        : string.Empty)));
            };
        }
    }

    /// <summary><c>TickSync</c> 워치독 (A-10). 계측·테스트가 읽는다.</summary>
    public TickSyncWatchdog TickSync => _tickSync;

    /// <summary>경보 싱크 (A-05). 호스트 안의 모든 경보가 여기로 모인다.</summary>
    public IAlarmSink Alarms => _alarms;

    /// <summary>이 호스트가 돌리는 NPC 수. <b><c>--npcs</c> 가 아니라 로스터가 정한다</b> — <c>--zone</c> 이 줄인다.</summary>
    public int Npcs => Roster.Count;

    /// <summary>
    /// 이 호스트가 보는 NPC 집합. <b>해시가 핸드셰이크에 실린다</b> (docs/20 §5.5).
    ///
    /// 밖에서 읽을 수 있어야 하는 이유는 진단이다 — 연결이 <c>RosterMismatch</c> 로 거절될 때
    /// 양쪽 해시를 나란히 놓고 볼 수 없으면 원인을 존 필터까지 되짚기 어렵다.
    /// </summary>
    public NpcRoster Roster { get; }

    /// <summary>틱 루프. 게이트 러너가 통계를 읽는다.</summary>
    public NpcServerLoop Loop => _loop;

    /// <summary>
    /// 조립된 링크. 결정론 테스트가 재생 결과(<c>ReplayGameServerLink.Commands</c>)를 읽는다 —
    /// 재생은 명령을 파일로 내보내지 않고 메모리에만 모으기 때문이다.
    /// </summary>
    public IGameServerLink Link => _link;

    /// <summary>게임서버 대역. Loopback·Record 일 때만 있다.</summary>
    public SimDriver? Driver => _driver;

    /// <summary>계측. /metrics 와 게이트 러너가 읽는다.</summary>
    public NpcMeter Metrics => _meter;

    /// <summary>재계획 티어 한 벌. 부하 하네스가 워커·예산 통계를 읽는다 (T4-15).</summary>
    public TierWiring Tiers => _tiers;

    /// <summary>
    /// 킬스위치 상태. <c>--dev-control</c> 의 <c>POST /control/killswitch</c> 가 여기에 쓴다.
    /// <b>한 번 켜지면 꺼지지 않는다</b> — 되돌리려면 재기동한다.
    /// </summary>
    public KillSwitchState Switches => _switches;

    /// <summary>NPC 상태. 측정 하네스가 읽는다 — <b>쓰지 않는다</b> (T4-17·T4-21).</summary>
    public NpcStore Store => _store;

    /// <summary>재계획 큐. 가중치 A/B 가 유입·처리량을 읽는다 (T4-17).</summary>
    public ReplanQueue ReplanQueue => _replanQueue;

    /// <summary>인지 스캐너. 어느 가중치로 돌았는지 리포트에 적을 때 읽는다.</summary>
    public CognitionScheduler Cognition => _cognition;

    /// <summary>게임 시계.</summary>
    public GameClock Clock => _clock;

    /// <summary>
    /// 틱 루프 생존 계측 (A-03). 헬스 엔드포인트가 읽는다.
    ///
    /// 조립 시점에 루프에 붙이므로 프로세스 수명 내내 같은 인스턴스다.
    /// </summary>
    public HealthProbe Probe { get; } = new();

    /// <summary>
    /// 링크가 <c>Connected</c> 여야 하는 회차인가 (A-03).
    ///
    /// 루프백·널·재생은 소켓이 없으므로 readiness 에서 링크를 보지 않는다 —
    /// 그 회차에서 링크를 요구하면 프로브가 영원히 503 이다.
    /// </summary>
    public bool LinkRequired => _tcp is not null;

    /// <summary>핸드셰이크 거절 사유. 없으면 null. <c>/healthz/ready</c> 본문에 실린다.</summary>
    public string? LinkRejectReason =>
        _tcp is { RejectCode: not LinkRejectCode.None } tcp ? tcp.RejectCode.ToString() : null;

    /// <summary>
    /// 로드가 끝났는가. 조립이 끝난 시점에 참이다 — <see cref="Create"/> 가 마스터데이터와
    /// 플랜 스토어를 동기로 읽고 나서야 인스턴스를 돌려주기 때문이다.
    /// </summary>
    public bool Loaded => true;

    /// <summary>스냅샷 복원 판정 (A-01). 기동 중에 채워진다.</summary>
    public RestoreResult Restore { get; init; }

    /// <summary>스냅샷 복원 판정을 사람 말로.</summary>
    public string RestoreDetail => Restore.Detail.Length == 0 ? "복원 대상 없음 (시드로 기동)" : Restore.Detail;

    /// <summary>주기 스냅샷 쓰기 (A-01). 꺼져 있으면 null.</summary>
    public SnapshotWriter? Snapshots => _snapshots;

    /// <summary>마지막 종료 시퀀스 (A-02). 테스트가 단계 순서를 읽는다.</summary>
    public HostShutdown? LastShutdown { get; private set; }

    /// <summary>경보 웹훅 (A-05). 없으면 null. 종료 시 큐를 비운다.</summary>
    internal WebhookAlarmSink? Webhook { get; init; }

    /// <summary>
    /// 릴리스 버전 (A-09). <c>MAJOR.MINOR.PATCH+메타</c>.
    ///
    /// 어셈블리 정보 버전은 빌드가 정하고(태그 → <c>VersionPrefix</c>), 마스터데이터
    /// <c>content_hash</c> 앞 8자리를 메타로 붙인다 — <b>같은 바이너리라도 다른 콘텐츠면
    /// 다른 버전이다</b>. 장애 대응에서 "어느 데이터로 떠 있나" 가 먼저 필요하다.
    /// </summary>
    public string Version =>
        $"{HostVersion.Assembly}+md.{Head(_data.ContentHash)}";

    private static string Head(string hash) => hash.Length <= 8 ? hash : hash[..8];

    /// <summary>liveness 판정.</summary>
    public HealthReport Live() =>
        HealthEndpoints.Live(Probe, _options.LiveStallSeconds * 1_000L);

    /// <summary>readiness 판정. 틱 진행 관측을 겸한다.</summary>
    public HealthReport Ready()
    {
        Probe.Observe(_clock.Current.Value);

        return HealthEndpoints.Ready(
            Probe,
            Loaded,
            _link.State,
            LinkRequired,
            _options.ReadyTickStallSeconds * 1_000L,
            LinkRejectReason);
    }

    /// <summary>startup 판정.</summary>
    public HealthReport Startup() => HealthEndpoints.Startup(Loaded, restoreDecided: true, RestoreDetail);

    /// <summary>
    /// TCP 링크를 만든다. docs/20 §10.3.
    ///
    /// <b>핸드셰이크에 실을 값 넷을 여기서 채운다</b> — 프로토콜 버전은 링크가, 나머지 셋은
    /// 우리가 안다. 게임서버가 하나라도 다른 값을 보내면 연결이 거절되고
    /// <c>Faulted</c> 로 간다 (docs/20 §5.5). <b>우회 옵션은 없다.</b>
    /// </summary>
    private static TcpGameServerLink TcpLink(HostOptions options, MasterDataSet data, NpcRoster roster) =>
        new(new TcpLinkOptions
        {
            Host = options.GameServerHost,
            Port = options.GameServerPort,
            TimeScale = options.TimeScale,
            NpcCount = roster.Count,
            MasterData = WireHash.FromHex(data.ContentHash),
            MasterDataStructural = WireHash.FromHex(data.StructuralHash),
            MasterDataContent = WireHash.FromHex(data.ContentHash),
            Roster = WireHash.FromHex(roster.Hash),
        });

    /// <summary>
    /// <c>--zone</c> 을 존 code 로 푼다. docs/20 §10.2.
    ///
    /// <b>모르는 id 는 기동 실패다.</b> 조용히 무시하면 필터가 없는 것처럼 로스터가 커지고,
    /// 그 사고는 <b>핸드셰이크 거절로만</b> 나타난다 — 원인이 멀어져
    /// "새로 만든 게임서버가 이상하다" 로 오해하기 쉽다 (docs/20 §5.5).
    /// </summary>
    private static ZoneId[] ZoneFilter(HostOptions options, MasterDataSet data)
    {
        if (options.Zones.IsEmpty)
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

    /// <summary>옵션대로 전부 조립한다. 기동 시 1회.</summary>
    public static NpcHost Create(HostOptions options, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        string masterDataDir = options.ResolveMasterData();
        MasterDataSet data = MasterDataLoader.Load(masterDataDir);

        string instancePath = Path.Combine(masterDataDir, "npc_instances.json");
        NpcInstanceTable instances = NpcInstanceTable.Load(instancePath, data);

        // ── 로스터 ────────────────────────────────────────────────
        // 선택 규칙은 NpcRoster 한 곳에 있다 (T6-11 · docs/20 §10). 게임서버도 같은 함수를
        // 부르므로 첨자가 어긋나지 않는다 — 어긋나면 대장장이에게 밭을 갈라고 명령하게 된다.
        //
        // <b>할당보다 먼저 뽑는다.</b> --zone 이 걸리면 실제 마릿수가 --npcs 보다 작아지는데,
        // 그 값을 모른 채 NpcStore·CorrelationTable·ReplanQueue 를 잡으면 뒤쪽이 영원히
        // 비어 있는 배열이 되고, 무엇보다 로스터 해시가 게임서버와 어긋나 연결이 거절된다.
        ZoneId[] zoneFilter = ZoneFilter(options, data);
        NpcRoster roster = NpcRoster.Select(instances, options.Npcs, zoneFilter);
        int npcs = roster.Count;

        if (npcs == 0)
        {
            // 0 마리로 기동하면 아무 일도 안 하는 서버가 조용히 뜬다. 존 오타의 전형적 증상이라
            // 여기서 멈추는 편이 싸다 — 게임서버 쪽은 같은 필터로 같은 0 을 얻어 해시는 맞는다.
            throw new ArgumentException(
                $"--zone {string.Join(",", options.Zones)} 에 해당하는 NPC 가 npc_instances.json 에 없다.",
                nameof(options));
        }

        if (npcs < options.Npcs)
        {
            string pool = zoneFilter.Length == 0
                ? $"npc_instances.json 에 {instances.Count} 마리뿐이라"
                : $"--zone {string.Join(",", options.Zones)} 안에 {npcs} 마리뿐이라";

            log.WriteLine($"warn: {pool} {npcs} 로 줄였다.");
        }

        // ── 런타임 ────────────────────────────────────────────────
        var store = new NpcStore();
        store.Allocate(npcs, data.Items.MaxCode + 1);

        var clock = new GameClock(data.Buckets, options.TimeScale);
        var correlations = new CorrelationTable(npcs);
        var zoneStates = new ZoneStateTable(data);
        var lodUpdater = new LodUpdater(store) { ZoneStates = zoneStates };
        var applier = new EventApplier(data, store, clock, correlations, lodUpdater);
        var emitter = new CommandEmitter(data, new PoiBinder(data.Pois));
        var swapper = new PlanSwapper(store);
        var replanQueue = new ReplanQueue(npcs);
        var snapshots = new ReplanSnapshots(npcs);
        var individualPool = new IndividualPlanPool();

        PlanStore plans = BuildPlanStore(options, data, masterDataDir, log, out int[] fallbackOf);

        // 개별 재계획 플랜은 레지스트리가 아니라 512칸 링에서 온다 (docs/13 §2 · T4-12).
        plans.Individual = individualPool;

        var executor = new PlanExecutor(data, store, plans, correlations, emitter, options.TimeScale)
        {
            Swapper = swapper,
            ReplanQueue = replanQueue,
        };

        var bands = new LodBandSet(store);
        if (!Weights.TryParse(options.Weights, out Weights weights))
        {
            throw new ArgumentException(
                $"--weights '{options.Weights}' 를 모른다. "
                + $"있는 것: {string.Join(", ", Weights.AbSets.Select(s => s.Name))}",
                nameof(options));
        }

        var cognition = new CognitionScheduler(store, bands, plans, weights)
        {
            Snapshots = snapshots,
            ScanBudgetPerTick = options.ScanCap >= 0
                ? options.ScanCap
                : CognitionScheduler.MaxScansPerTick,
        };
        var interrupts = new InterruptMatcher(data, store) { Snapshots = snapshots };

        // ── 인구 배치 ─────────────────────────────────────────────
        // <b>먼저 시드한다.</b> 복원이 그 위에 덮는 형태여야 복원 대상이 아닌 값(집·일터 같은
        // 인스턴스 정의)이 항상 채워진다 — 스냅샷은 그것들도 담지만, 스냅샷이 없는 회차와
        // 코드 경로를 갈라 두면 "복원했을 때만 나는 버그" 가 생긴다.
        for (int i = 0; i < npcs; i++)
        {
            NpcInstanceDef def = roster.Npcs[i];

            applier.Seed(i, def.Home, def.Zone, def.Archetype, def.Home, def.Workplace);
            store.StepStatus[i] = (byte)StepStatus.Ready;
            executor.AssignPlan(i, new PlanId(fallbackOf[def.Archetype.Value]));
        }

        // ── 스냅샷 복원 (A-01) ────────────────────────────────────
        // 조건(형식 버전·마스터데이터 해시·로스터 해시·NPC 수)이 전부 맞을 때만 덮는다.
        var restorer = new SnapshotRestorer(store, clock, zoneStates, correlations, individualPool, data);

        RestoreResult restore = restorer.TryRestore(
            options.Restore,
            options.Restore == RestoreMode.File ? options.RestorePath! : options.ResolveSnapshotDir(),
            data.ContentHash,
            roster.Hash,
            PromptPrefix.Build(data, masterDataDir).Sha256,
            log);

        log.WriteLine($"restore: {restore.Detail}");

        bands.Rebalance();

        // ── 링크 · 게임서버 대역 ──────────────────────────────────
        SimDriver? driver = null;
        NullGameServerLink? nullLink = null;
        TcpGameServerLink? tcp = null;
        IGameServerLink link;

        switch (options.Link)
        {
            case LinkKind.Null:
                nullLink = new NullGameServerLink();
                link = nullLink;
                break;

            case LinkKind.Replay:
                link = ReplayGameServerLink.Load(options.TracePath!);
                break;

            case LinkKind.Tcp:
                // 대역을 만들지 않는다 — Replay 와 같은 경로다. 세계를 미는 것은 게임서버이고
                // 우리는 이벤트를 받아 명령을 낼 뿐이다 (docs/20 §10.3).
                link = tcp = TcpLink(options, data, roster);
                break;

            case LinkKind.Record:
            {
                // 데코레이터라 안쪽이 무엇이든 감싼다. --link record --gs-port ... 조합이면
                // 소켓으로 받은 이벤트 열을 그대로 기록해 나중에 --link replay 로 재생할 수 있다.
                IGameServerLink inner;

                if (options.UsesGameServer)
                {
                    inner = tcp = TcpLink(options, data, roster);
                }
                else
                {
                    driver = SimDriver.Create(options, data, roster.Npcs);
                    inner = driver.Link;
                }

                string path = options.TracePath ?? Path.Combine("artifacts", "link.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                link = new RecordingGameServerLink(inner, path);
                break;
            }

            case LinkKind.Loopback:
            default:
                driver = SimDriver.Create(options, data, roster.Npcs);
                link = driver.Link;
                break;
        }

        // 킬스위치를 실제 티어에 결선한다 (docs/15 §4).
        //
        // 같은 상태를 셋이 읽는다 — PlanStore·티어 라우터(TieredPlanCompiler)·재계획 워커.
        // 라우터 쪽은 아래 TierWiring.Build 에 넘긴다. --tier none 이면 라우터가 없으므로
        // 실제로 끊기는 것은 PlanStore 하나다.
        //
        // <b>대역이 없어도 만든다</b> (T6-13). 예전에는 driver 가 null 이면 상태 자체가 없어서
        // --link tcp 에서는 시나리오의 KillSwitch 줄도 --dev-control 도 끊을 대상이 없었다.
        // 대역이 있으면 그 시나리오가 쓰는 상태를 그대로 물려받아 <b>한 상태를 둘이 본다</b>.
        // 아무도 발동하지 않으면 전부 켜진 상태 그대로라 기존 동작과 같다.
        KillSwitchState switches = driver?.Scenario.Switches ?? new KillSwitchState();

        plans.Switches = switches;

        long totalTicks = options.Days == 0 ? 0 : clock.TicksForGameDays(options.Days);

        // 틱 루프 ↔ 워커 인계 통로 (docs/14 §4). 힙은 틱 루프만 만지고 워커는 이 통로만 본다 —
        // 워커가 힙을 직접 꺼내면 데이터 레이스다 (ReplanHandoff 주석).
        var handoff = new ReplanHandoff();

        var loop = new NpcServerLoop(
            link, clock, applier, interrupts, cognition, executor, swapper, bands, replanQueue)
        {
            StopAtTick = totalTicks,
            Transition = new BucketTransition(store, plans, data) { ZoneStates = zoneStates },
            Handoff = handoff,
        };

        // ── 경보 싱크 (A-05) ─────────────────────────────────────
        // 텍스트가 기본이고 웹훅은 있으면 덧붙인다. 쿨다운이 바깥을 감싼다 —
        // 억제는 싱크마다가 아니라 사건마다여야 한다.
        var textAlarms = new TextAlarmSink(log);
        IAlarmSink alarmTarget = textAlarms;

        WebhookAlarmSink? webhook = null;

        if (options.AlarmWebhook is { } webhookUrl)
        {
            webhook = new WebhookAlarmSink(new Uri(webhookUrl), textAlarms);
            alarmTarget = new CompositeAlarmSink(textAlarms, webhook);

            log.WriteLine($"alarm-webhook: {webhookUrl}");
        }

        var alarms = new CooldownAlarmSink(alarmTarget, options.AlarmCooldownSeconds);

        // ── 재계획 티어 (docs/14 §4). --tier 가 결정한다 ──
        TierWiring tiers = TierWiring.Build(
            options, data, masterDataDir, store, plans, replanQueue, handoff, snapshots,
            individualPool, swapper, zoneStates, clock, log, switches, alarms);

        var meter = new NpcMeter(
            store, bands, plans, replanQueue, cognition, interrupts, link, loop, clock, data,
            tiers.Stats, new CacheMetrics(plans, individualPool), tiers);

        // 킬스위치 스케줄이 계측기를 감싼다 (T6-13 · docs/20 §11.4).
        // --link tcp 에서 이벤트를 내는 것은 게임서버이고, 우리는 KillSwitch 줄만 본다.
        //
        // Npc.Runtime 에 훅을 넣지 않았다 — ITickObserver 라는 이음매가 이미 있었다.
        // 대역이 도는 모드에서는 ScenarioRunner 도 같은 줄을 보지만, Fire 는 멱등이라(N7)
        // 두 번 끊겨도 상태가 같다.
        var killSwitches = KillSwitchSchedule.Load(options.Scenario, data, switches, meter);

        loop.Observer = killSwitches;

        // ── 스냅샷 쓰기 (A-01) ───────────────────────────────────
        // 틱 루프는 그림자 버퍼로 복사만 한다. 파일은 별도 스레드가 쓴다.
        SnapshotWriter? snapshotWriter = null;

        if (options.SnapshotEnabled)
        {
            var port = new SnapshotPort
            {
                Buffer = new ShadowBuffer(npcs, store.InventoryStride, zoneStates.Capacity),
                Store = store,
                Zones = zoneStates,
                Correlations = correlations,
            };

            loop.Snapshots = port;

            string prefixHash = PromptPrefix.Build(data, masterDataDir).Sha256;
            string snapshotDir = options.ResolveSnapshotDir();

            snapshotWriter = new SnapshotWriter(
                port,
                snapshotDir,
                options.SnapshotIntervalSeconds,
                options.SnapshotKeep,
                () => new SnapshotHeader(
                    SnapshotFile.FormatVersion,
                    port.Buffer.Tick,
                    port.Buffer.SyncedTick,
                    npcs,
                    store.InventoryStride,
                    zoneStates.Capacity,
                    port.Buffer.NextCorrelation,
                    data.ContentHash,
                    roster.Hash,
                    prefixHash),
                () => IndividualPlanCapture.From(store, individualPool, port.Buffer.Tick),
                result =>
                {
                    meter.OnSnapshotWritten(result);

                    if (result.Error is { } error)
                    {
                        alarms.Raise(new AlarmPayload(
                            AlarmKind.SnapshotFailed, AlarmSeverity.Critical, "snapshot",
                            $"스냅샷 실패 — {error}. 상태 손실 창이 주기보다 커진다"));
                    }
                });

            meter.Snapshots = snapshotWriter;

            log.WriteLine(
                $"snapshot: {snapshotDir} · 주기 {options.SnapshotIntervalSeconds}s · 보존 {options.SnapshotKeep}");
        }

        var host = new NpcHost(
            options, link, loop, clock, executor, cognition, interrupts, replanQueue, store, plans, data,
            meter, driver, nullLink, tcp, totalTicks, roster, tiers, switches, snapshotWriter, alarms)
        {
            Restore = restore,
            Webhook = webhook,
        };

        meter.TickSync = host.TickSync;

        // 생존 신호는 루프가 조립된 뒤에 붙인다 (A-03). Volatile 쓰기 하나라 틱 예산에 영향이 없다.
        loop.Probe = host.Probe;

        log.WriteLine(
            $"contract {Npc.Contracts.ContractVersion.Text} · "
            + $"wire [{FrameCodec.MinVersion}, {FrameCodec.MaxVersion}]");

        log.WriteLine(
            $"npcs {npcs} · link {options.Link} · time-scale {options.TimeScale} · "
            + $"days {options.Days} ({(totalTicks == 0 ? "무제한" : totalTicks + " ticks")}) · "
            + $"버킷 {plans.FilledBuckets}/{BucketKey.TotalKeys} · {tiers.Describe()}");

        return host;
    }

    /// <summary>
    /// 드라이버와 틱 루프를 함께 돌린다. 둘 중 하나가 끝나면 <see cref="HostShutdown"/> 시퀀스를 돈다.
    /// 재계획 워커는 <b>루프 밖</b>에서 같이 돈다 (CLAUDE.md §2.1).
    /// </summary>
    /// <returns>종료 코드. 정상 0, 종료 타임아웃 초과 2 (A-02).</returns>
    public Task<int> RunAsync(CancellationToken ct) => RunAsync(ct, stopWeb: null, log: null);

    /// <summary>
    /// 위와 같되 웹 호스트 정지를 시퀀스 5단계로 끼운다 (A-02).
    /// </summary>
    /// <param name="ct">취소 토큰. 신호가 여기로 온다.</param>
    /// <param name="stopWeb">웹 호스트 정지. 없으면 건너뛴다.</param>
    /// <param name="log">종료 시퀀스 로그.</param>
    public async Task<int> RunAsync(
        CancellationToken ct, Func<TimeSpan, Task<string>>? stopWeb, TextWriter? log)
    {
        log ??= TextWriter.Null;

        using var workers = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await _tiers.StartAsync(workers.Token).ConfigureAwait(false);

        _snapshots?.Start();
        _tickSync.Start();

        // 소켓 링크는 스스로 붙지 않는다 — 접속·재접속 루프를 누군가 돌려야 한다.
        // <b>WhenAll 에 넣지 않는다</b>: 이 루프는 취소될 때까지 끝나지 않으므로
        // --days N 처럼 틱 루프가 먼저 끝나는 회차에서 종료를 막는다.
        Task socket = _tcp is null
            ? Task.CompletedTask
            : Task.Run(() => _tcp.RunAsync(workers.Token), CancellationToken.None);

        Task pump = Task.Run(() => PumpAsync(ct), CancellationToken.None);
        Task loop = _loop.RunAsync(ct);

        try
        {
            await Task.WhenAll(pump, loop).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 신호로 끝났다. 정상 종료 경로다.
        }

        // ── 정상 종료 시퀀스 (A-02) ─────────────────────────────
        // 순서가 규약이다. 워커를 먼저 세우면 마지막 스왑이 유실되고,
        // 스냅샷을 나중에 쓰면 틱 루프가 이미 멈춰 사본을 만들 수 없다.
        var shutdown = new HostShutdown();

        LastShutdown = shutdown;

        return await shutdown.RunAsync(
            TimeSpan.FromSeconds(_options.ShutdownTimeoutSeconds),
            stopTickLoop: _ => Task.FromResult(
                $"틱 {_loop.TicksProcessed} 처리 · 마지막 틱 {_clock.Current.Value}"),
            writeSnapshot: _snapshots is null
                ? null
                : (Func<TimeSpan, Task<string>>)(_ =>
                {
                    SnapshotWriteResult result = _snapshots.WriteFinal(
                        _clock.Current.Value, _clock.SyncedTick);

                    return Task.FromResult(result.Error is { } error
                        ? $"실패: {error}"
                        : $"tick {result.Tick} · {result.Bytes}B · {result.ElapsedMs:0}ms");
                }),
            stopWorkers: async _ =>
            {
                await workers.CancelAsync().ConfigureAwait(false);
                await _tiers.StopAsync().ConfigureAwait(false);

                return "재계획 워커 정지 (진행 중인 LLM 결과는 버린다)";
            },
            closeLink: async budget =>
            {
                string bye = "링크 없음";

                if (_tcp is { } tcp)
                {
                    await tcp.FlushAsync(CancellationToken.None).ConfigureAwait(false);

                    bool sent = await tcp
                        .SendByeAsync(LinkByeCode.Shutdown, Min(budget, TimeSpan.FromSeconds(2)), CancellationToken.None)
                        .ConfigureAwait(false);

                    bye = sent ? "Bye(Shutdown) 송신" : "Bye 미송신 (이미 끊김)";
                }

                try
                {
                    await socket.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 취소·소켓 종료로 끝난다. 종료 경로에서는 예외가 정상이다.
                }

                return bye;
            },
            stopWeb: stopWeb ?? (_ => Task.FromResult("웹 호스트 없음")),
            log).ConfigureAwait(false);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>
    /// NPC 한 마리의 추적 스냅샷 (T4-21). <b>틱 루프를 막지 않는다</b> — 읽기와 값 복사뿐이다.
    /// </summary>
    public NpcTrace Trace(int npc) =>
        NpcTraceEndpoint.Snapshot(npc, _store, _plans, _data, _clock.Current);

    /// <summary>
    /// 버킷 히트맵 원자료 CSV (T4-22). W12 보고서 §6 "발견" 의 원자료다 (T5-18 이 읽는다).
    ///
    /// 열은 <c>archetype,time_of_day,region_state,climate,queries,filled</c> 이고
    /// <b>조회가 0 인 버킷도 전부 낸다</b> — "쓰이지 않았다" 가 이 파일의 내용이다.
    /// </summary>
    public string HeatmapCsv()
    {
        var csv = new System.Text.StringBuilder(BucketKey.TotalKeys * 40);

        csv.AppendLine("archetype,time_of_day,region_state,climate,queries,filled");

        for (int i = 0; i < BucketKey.TotalKeys; i++)
        {
            BucketKey key = BucketKey.FromIndex(i);
            long queries = _plans.HitsOf(key) + _plans.MissesOf(key);

            csv.Append(_data.Archetypes[key.A].Id).Append(',')
               .Append(key.T).Append(',')
               .Append(key.R).Append(',')
               .Append(key.C).Append(',')
               .Append(queries).Append(',')
               .Append(_plans.HasBucket(key) ? '1' : '0')
               .AppendLine();
        }

        return csv.ToString();
    }

    /// <summary>대시보드·게이트가 읽는 현재 상태.</summary>
    public HostSnapshot Snapshot() => new(
        Version: Version,
        Tick: _clock.Current.Value,
        GameDay: _clock.GameDay,
        GameHour: _clock.GameHour,
        TimeOfDay: _clock.TimeOfDay.ToString(),
        Npcs: Npcs,
        TicksProcessed: _loop.TicksProcessed,
        EventsDrained: _loop.EventsDrained,
        CommandsEmitted: _executor.CommandsEmitted,
        StepsAdvanced: _executor.StepsAdvanced,
        TimeoutsSynthesized: _executor.TimeoutsSynthesized,
        ScanPerTick: _cognition.LastScanned,
        Deviations: _cognition.Deviations,
        InterruptsForced: _interrupts.Forced,
        ReplanQueued: _replanQueue.Count,
        EventBacklogs: _loop.EventBacklogs,
        LlmCalls: _tiers.Stats?.Calls ?? 0,
        LinkState: _link.State.ToString(),
        LinkReject: LinkRejectReason,
        LinkNegotiation: _tcp?.NegotiationDetail ?? "링크 없음",
        TicksBehind: _tickSync.TicksBehind,
        TickSyncStalled: _tickSync.Stalled,
        ContractVersion: Npc.Contracts.ContractVersion.Text,
        LastSnapshotTick: _snapshots?.LastTick ?? 0,
        SnapshotFailures: _snapshots?.Failures ?? 0,
        RestoredFromTick: Restore.Tick,
        Link: _link.Stats);

    /// <summary>종료 요약. 게이트 러너가 이 숫자를 본다.</summary>
    public void Report(TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(log);

        HostSnapshot s = Snapshot();
        MetricsSnapshot m = _meter.Snapshot();

        log.WriteLine(
            $"tick p50 {m.Tick.P50Ms:0.###}ms · p99 {m.Tick.P99Ms:0.###}ms · max {m.Tick.MaxMs:0.###}ms · "
            + $"overruns {m.Tick.Overruns} · gen0 {m.Tick.Gen0Collections} · "
            + $"bytes/tick {m.Tick.BytesPerTick} · heap {m.Tick.ManagedHeapMb}MB");

        // 드롭은 대시보드에만 있으면 안 된다 — 헤드리스로 돌린 사람은 못 본다.
        // link 는 발행 큐가 넘친 수, sim 은 게임서버 대역의 수신 링이 넘친 수다. 둘 다 0 이어야 정상이다.
        log.WriteLine(
            $"ticks {s.TicksProcessed} · game day {s.GameDay} · events {s.EventsDrained} · "
            + $"commands {s.CommandsEmitted} · steps {s.StepsAdvanced} · "
            + $"timeouts {s.TimeoutsSynthesized} · scan/tick {s.ScanPerTick} · "
            + $"interrupts {s.InterruptsForced} · replan-q {s.ReplanQueued} · "
            + $"drops link {s.Link.CommandsDropped} sim {_driver?.CommandsDropped ?? 0} · "
            + $"backlogs {s.EventBacklogs} · llm {s.LlmCalls}");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _meter.Dispose();
        _tickSync.Dispose();

        if (_snapshots is { } snapshots)
        {
            await snapshots.DisposeAsync().ConfigureAwait(false);
        }

        if (Webhook is { } webhook)
        {
            await webhook.DisposeAsync().ConfigureAwait(false);
        }

        await _tiers.DisposeAsync().ConfigureAwait(false);
        await _link.DisposeAsync().ConfigureAwait(false);

        if (_driver is not null)
        {
            await _driver.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 폴백 40개 + 프리베이크된 버킷 플랜을 스토어에 올린다. docs/13 §2.
    ///
    /// <b>폴백을 먼저 등록한다.</b> 버킷이 하나도 없어도 <c>Resolve</c> 가 유효한 플랜을 주는 것은
    /// 폴백 때문이고, 그것이 시나리오 C(LLM 전면 차단)가 통과하는 이유다 (CLAUDE.md §2.6).
    /// 버킷 로드는 그 위에 얹는다 — 미생성 버킷은 그대로 폴백으로 해소된다.
    ///
    /// 마스터데이터가 스토어보다 새로우면 <b>경고만</b> 하고 계속 간다. 기동을 막지 않는 이유는
    /// 낡은 플랜이라도 폴백보다는 나은 경우가 있고, 재생성 판단은 사람이 할 일이기 때문이다.
    /// </summary>
    private static PlanStore BuildPlanStore(
        HostOptions options, MasterDataSet data, string masterDataDir, TextWriter log, out int[] fallbackOf)
    {
        PlanStore plans = PlanStore.CreateIdleOnly(data);
        fallbackOf = new int[data.Archetypes.Count];

        if (data.Fallbacks is { } table)
        {
            foreach (FallbackPlanEntry entry in table.Plans)
            {
                PlanId id = plans.Register(entry.Plan);

                plans.SetFallback(entry.Archetype, id);
                fallbackOf[entry.Archetype.Value] = id.Value;
            }
        }

        string storeDir = options.ResolvePlanStore();

        if (!Directory.Exists(storeDir))
        {
            log.WriteLine($"planstore 없음 ({storeDir}) — 폴백 {plans.FilledFallbacks}개로 돈다.");
            return plans;
        }

        long started = Stopwatch.GetTimestamp();
        PlanStoreLoadReport report = PlanStoreIo.LoadAll(storeDir, plans, data);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;

        log.WriteLine(
            $"planstore {storeDir} · 버킷 {report.Total}/{BucketKey.TotalKeys} "
            + $"(pinned {report.Pinned}) · 폴백 {plans.FilledFallbacks} · {elapsed:0.###}s");

        if (report.Skipped + report.Failed > 0)
        {
            log.WriteLine($"warn: 플랜 {report.Skipped + report.Failed}건을 못 올렸다.");

            foreach (string error in report.Errors.Take(5))
            {
                log.WriteLine($"  {error}");
            }
        }

        if (plans.ColdBuckets > 0)
        {
            log.WriteLine($"warn: 미생성 버킷 {plans.ColdBuckets}건. 아키타입 폴백으로 해소된다.");
        }

        WarnIfStale(storeDir, data, masterDataDir, log);

        return plans;
    }

    /// <summary>
    /// 마스터데이터·프롬프트가 스토어보다 새로우면 경고한다. 판정은 T3-06 이 한다.
    ///
    /// 프리픽스는 여기서만 조립한다 — <c>Npc.Runtime</c> 은 <c>Npc.Llm</c> 을 모른다 (CLAUDE.md §3).
    /// </summary>
    private static void WarnIfStale(
        string storeDir, MasterDataSet data, string masterDataDir, TextWriter log)
    {
        Manifest? manifest = Manifest.LoadFrom(storeDir);

        if (manifest is null)
        {
            log.WriteLine("warn: manifest.json 이 없다. 스토어가 지금 마스터데이터로 만들어진 것인지 알 수 없다.");
            return;
        }

        InvalidationScope scope = PlanStoreValidator.Compare(
            manifest,
            data,
            PromptPrefix.Build(data, masterDataDir).Sha256,
            out ImmutableArray<string> changed);

        if (scope == InvalidationScope.None)
        {
            return;
        }

        log.WriteLine(
            $"warn: 플랜 스토어가 낡았다 — 무효화 {scope}"
            + (changed.IsEmpty ? string.Empty : $" (바뀐 것: {string.Join(", ", changed)})")
            + ". tools/Npc.Prebake 로 재생성한다.");
    }

    /// <summary>
    /// 세계를 한 틱씩 민다. 게임서버가 없을 때(Null) 는 TickSync 만,
    /// Loopback·Record 면 Sim 전체를 돌린다. Replay 는 파일이 이미 이벤트를 갖고 있어 아무것도 안 한다.
    /// </summary>
    private async Task PumpAsync(CancellationToken ct)
    {
        if (_driver is null && _nullLink is null)
        {
            return;   // Replay — 채널에 이미 다 들어 있다
        }

        // 벽시계는 호스트 쪽에만 있다. 게임 로직은 여전히 Tick 만 본다 (CLAUDE.md §2.3).
        var stopwatch = Stopwatch.StartNew();
        long tick = 0;

        while (!ct.IsCancellationRequested && (_totalTicks == 0 || tick < _totalTicks))
        {
            tick++;

            var now = new Tick(tick);

            // 틱 루프보다 앞서 나가면 안 된다. 앞서면 명령이 나오기도 전에 세계가 다음 틱으로
            // 넘어가 버려서 응답 이벤트가 하나도 돌아오지 않는다 (전부 타임아웃 합성이 된다).
            //
            // 기다리는 것을 <b>세계를 밀기 전으로</b> 옮겼다. 밀고 나서 기다리면 tick N+1 의
            // 이벤트가 이미 채널에 들어간 뒤라, 틱 루프가 N 을 돌리는 동안 N+1 이 섞여 들어온다.
            // 기준도 TicksProcessed 가 아니라 TicksCommitted 다 — 처리만 끝나고 명령이 아직
            // 링크 큐에 있는 순간에 세계를 밀면 어떤 명령이 반영되는지가 스레드 스케줄에 달린다.
            // 이 둘이 docs/15 §3 의 "리플레이 100% 일치"가 성립하기 위한 조건이다.
            var spin = new SpinWait();

            while (tick - _loop.TicksCommitted > MaxLeadTicks && !ct.IsCancellationRequested)
            {
                // sleep1Threshold: -1 — Thread.Sleep(1) 로 넘어가지 않게 한다.
                // 락스텝이라 매 틱 한 번은 기다리게 되는데, 여기서 1ms 를 자면
                // 1,440틱짜리 하루가 그것만으로 20초를 더 쓴다 (실측 1분 39초 → 9분 8초).
                spin.SpinOnce(sleep1Threshold: -1);
            }

            if (_driver is not null)
            {
                _driver.Tick(now);
            }
            else
            {
                _nullLink!.PushTick(now);
            }

            if (_options.MaxSpeed)
            {
                continue;   // 측정용 — 페이싱 없이 루프 속도에 맞춰 민다
            }

            long due = tick * 1_000 / NpcServerLoop.TickHz;
            long wait = due - stopwatch.ElapsedMilliseconds;

            if (wait > 0)
            {
                await Task.Delay((int)wait, ct).ConfigureAwait(false);
            }
        }

        // 채널을 닫아야 틱 루프의 WaitToReadAsync 가 풀린다.
        if (_driver is not null)
        {
            await _driver.CompleteAsync().ConfigureAwait(false);
        }
        else
        {
            await _nullLink!.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>대시보드·게이트가 읽는 상태 한 장. docs/11 §10.</summary>
/// <param name="Version">릴리스 버전 + 마스터데이터 해시 앞 8자리 (A-09).</param>
/// <param name="Tick">현재 틱.</param>
/// <param name="GameDay">게임 일수.</param>
/// <param name="GameHour">게임 시각(0~23).</param>
/// <param name="TimeOfDay">시간대 이름.</param>
/// <param name="Npcs">NPC 수.</param>
/// <param name="TicksProcessed">처리한 틱 수.</param>
/// <param name="EventsDrained">배수한 이벤트 수.</param>
/// <param name="CommandsEmitted">발행한 명령 수.</param>
/// <param name="StepsAdvanced">전진한 스텝 수.</param>
/// <param name="TimeoutsSynthesized">합성한 타임아웃 수.</param>
/// <param name="ScanPerTick">마지막 틱의 인지 스캔 수.</param>
/// <param name="Deviations">이탈로 판정해 재계획 큐에 넣은 수.</param>
/// <param name="InterruptsForced">인터럽트가 즉시 발행한 액션 수.</param>
/// <param name="ReplanQueued">재계획 큐 깊이.</param>
/// <param name="EventBacklogs">한 틱 상한에 걸려 다음 틱으로 넘긴 횟수.</param>
/// <param name="LlmCalls">LLM 호출 수. P1 에서는 항상 0 이다.</param>
/// <param name="LinkState">링크 접속 상태 (A-03). <c>/status</c> 가 링크와 무관하게 200 이던 결손을 메운다.</param>
/// <param name="LinkReject">핸드셰이크 거절 사유. 없으면 null.</param>
/// <param name="LinkNegotiation">협상 결과 (B-01). 프로토콜·계약·기능 비트.</param>
/// <param name="TicksBehind">게임서버가 알려준 틱과 우리가 처리한 틱의 차이 (A-10). 0 이 정상.</param>
/// <param name="TickSyncStalled"><c>TickSync</c> 가 임계를 넘겨 멈춰 있는가 (A-10).</param>
/// <param name="ContractVersion">이 프로세스가 구현한 계약 버전 (B-01).</param>
/// <param name="LastSnapshotTick">마지막으로 쓴 스냅샷의 틱 (A-01). 상태 손실 창의 하한이다.</param>
/// <param name="SnapshotFailures">스냅샷 실패 누계. 0 이 아니면 손실 창이 주기보다 크다.</param>
/// <param name="RestoredFromTick">복원한 스냅샷의 틱. 복원 안 했으면 0.</param>
/// <param name="Link">링크 통계.</param>
internal readonly record struct HostSnapshot(
    string Version,
    long Tick,
    long GameDay,
    int GameHour,
    string TimeOfDay,
    int Npcs,
    long TicksProcessed,
    long EventsDrained,
    long CommandsEmitted,
    long StepsAdvanced,
    long TimeoutsSynthesized,
    int ScanPerTick,
    long Deviations,
    long InterruptsForced,
    int ReplanQueued,
    long EventBacklogs,
    long LlmCalls,
    string LinkState,
    string? LinkReject,
    string LinkNegotiation,
    long TicksBehind,
    bool TickSyncStalled,
    string ContractVersion,
    long LastSnapshotTick,
    long SnapshotFailures,
    long RestoredFromTick,
    LinkStats Link);

/// <summary>게임서버 대역 한 벌. Sim 하위 시뮬을 조립하고 틱마다 민다.</summary>
internal sealed class SimDriver : IAsyncDisposable
{
    private readonly SimWorld _world;
    private readonly MovementSim _movement;
    private readonly InteractionSim _interaction;
    private readonly TransformEmitter _transforms;
    private readonly NeedsSim _needs;
    private readonly PlayerBots _bots;
    private readonly ScenarioRunner _scenario;
    private readonly CommandRing _inbox;

    private SimDriver(
        SimWorld world,
        MovementSim movement,
        InteractionSim interaction,
        TransformEmitter transforms,
        NeedsSim needs,
        PlayerBots bots,
        ScenarioRunner scenario,
        CommandRing inbox,
        LoopbackGameServerLink link)
    {
        _world = world;
        _movement = movement;
        _interaction = interaction;
        _transforms = transforms;
        _needs = needs;
        _bots = bots;
        _scenario = scenario;
        _inbox = inbox;
        Link = link;
    }

    /// <summary>NPC 서버가 물리는 링크.</summary>
    public LoopbackGameServerLink Link { get; }

    /// <summary>월드.</summary>
    public SimWorld World => _world;

    /// <summary>시나리오 러너. 호스트가 킬스위치 상태를 여기서 꺼내 결선한다 (docs/15 §4).</summary>
    public ScenarioRunner Scenario => _scenario;

    /// <summary>링이 가득 차 버린 명령 수. 0 이 아니면 Sim 이 밀린 것이다.</summary>
    public long CommandsDropped => _inbox.Dropped;

    /// <summary>조립한다.</summary>
    public static SimDriver Create(
        HostOptions options, MasterDataSet data, ImmutableArray<NpcInstanceDef> npcs)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(data);

        var world = new SimWorld(data, npcs.Length, new SimOptions(
            Seed: options.Seed,
            TimeScale: options.TimeScale,
            FailRate: options.FailRate,
            DropRate: options.DropRate,
            PlayerBots: options.PlayerBots));

        for (int i = 0; i < npcs.Length; i++)
        {
            world.Place(i, npcs[i].Archetype, npcs[i].Home);
        }

        var movement = new MovementSim(world);
        var interaction = new InteractionSim(world);
        var faults = new FaultInjector(
            world,
            (in NpcCommand c, Tick t) => movement.TryHandle(in c, t) || interaction.TryHandle(in c, t));

        world.Handler = faults.TryHandle;

        // 인구를 세계에 올린다. 스폰되지 않은 NPC 는 PlayerBots 도 MovementSim 도 건너뛴다.
        // 실제 게임서버라면 접속·로딩이 하는 일이고, 대역에서는 기동 시 한 번에 한다.
        for (int i = 0; i < npcs.Length; i++)
        {
            var spawn = new NpcCommand
            {
                Kind = NpcCommandKind.Spawn,
                Npc = new NpcId(i),
                IssuedAt = default,
                Correlation = default,
                Priority = CommandPriority.Critical,
                Archetype = npcs[i].Archetype,
                Zone = npcs[i].Zone,
                TargetPoi = npcs[i].Home,
            };

            world.ApplyCommand(in spawn, default);
        }

        var inbox = new CommandRing();

        // 링크 큐는 <b>한 틱치 명령</b>을 담아야 한다. 소비자(FlushAsync)가 매 틱 전량을 비우므로
        // 여기서 넘치는 것은 역압(docs/02 §1)이 아니라 사이징 실수다 —
        // 틱 0 에 전원이 첫 스텝을 내면 npcs × MaxCommandsPerStep 이 한 번에 들어온다.
        // 기본값 4,096 을 그대로 두면 NPC 5,000 에서 첫 틱에 904건이 버려지고
        // 그 NPC 들은 timeout_s 가 만료될 때까지 첫 걸음을 못 뗀다.
        int linkCapacity = Math.Max(
            LoopbackGameServerLink.DefaultCapacity,
            npcs.Length * CommandEmitter.MaxCommandsPerStep);

        return new SimDriver(
            world,
            movement,
            interaction,
            new TransformEmitter(world, movement),
            new NeedsSim(world),
            new PlayerBots(world),
            options.Scenario is { } path ? ScenarioRunner.Load(path, data) : ScenarioRunner.Empty,
            inbox,
            new LoopbackGameServerLink(inbox.Enqueue, world.Events, linkCapacity));
    }

    /// <summary>한 틱. 받은 명령을 적용하고 하위 시뮬을 민 뒤 TickSync 를 낸다.</summary>
    public void Tick(Tick now)
    {
        while (_inbox.TryDequeue(out NpcCommand command))
        {
            _world.ApplyCommand(in command, now);
        }

        _scenario.Tick(now, _world);
        _bots.Tick(now);
        _movement.Tick(now);
        _interaction.Tick(now);
        _transforms.Tick(now);
        _needs.Tick(now);
        _world.Tick(now);
    }

    /// <summary>이벤트 스트림을 닫는다. 틱 루프가 여기서 풀려 나온다.</summary>
    public ValueTask CompleteAsync() => _world.DisposeAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Link.DisposeAsync().ConfigureAwait(false);
        await _world.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 단일 생산자(틱 루프) · 단일 소비자(Sim 드라이버) 링 버퍼.
///
/// <c>ConcurrentQueue</c> 를 쓰지 않는 이유는 하나다 — <c>FlushAsync</c> 는 틱 루프 안에서
/// 불리므로 할당이 있으면 안 된다 (CLAUDE.md §2.1). 여기는 미리 잡은 배열뿐이다.
/// </summary>
internal sealed class CommandRing
{
    private const int Capacity = 1 << 16;   // 5,000 NPC 한 틱치를 넉넉히 담는다
    private const int Mask = Capacity - 1;

    private readonly NpcCommand[] _slots = new NpcCommand[Capacity];
    private long _head;   // 소비자
    private long _tail;   // 생산자

    /// <summary>버린 명령 수. 링이 가득 찼다면 Sim 이 밀린 것이다.</summary>
    public long Dropped { get; private set; }

    /// <summary>틱 루프에서 부른다. 할당 0.</summary>
    public void Enqueue(in NpcCommand command, Tick now)
    {
        _ = now;

        long tail = _tail;

        if (tail - Volatile.Read(ref _head) >= Capacity)
        {
            Dropped++;
            return;   // 명령은 유실될 수 있다 (docs/02 §1). 타임아웃 합성이 받아준다
        }

        _slots[tail & Mask] = command;
        Volatile.Write(ref _tail, tail + 1);
    }

    /// <summary>드라이버에서 부른다.</summary>
    public bool TryDequeue(out NpcCommand command)
    {
        long head = _head;

        if (head >= Volatile.Read(ref _tail))
        {
            command = default;
            return false;
        }

        command = _slots[head & Mask];
        Volatile.Write(ref _head, head + 1);
        return true;
    }
}
