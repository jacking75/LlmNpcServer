using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Npc.TestClient.Net;

/// <summary>플랜의 스텝 한 줄. <c>Npc.Host.Api.TraceStep</c> 과 같은 모양이다.</summary>
public sealed class TraceStepDto
{
    /// <summary>플랜 안의 몇 번째 스텝인가.</summary>
    public int Index { get; set; }

    /// <summary>액션 id.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>POI 심볼 이름. 없으면 빈 문자열.</summary>
    public string Poi { get; set; } = string.Empty;

    /// <summary>수량 인자.</summary>
    public int Count { get; set; }

    /// <summary>이 스텝의 <c>timeout_s</c>.</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>지금 실행 중인 스텝인가.</summary>
    public bool Current { get; set; }
}

/// <summary>최근 사건 한 줄.</summary>
public sealed class TraceEventDto
{
    /// <summary>이벤트 종류.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>언제(틱).</summary>
    public long At { get; set; }

    /// <summary>몇 틱 전인가.</summary>
    public long AgoTicks { get; set; }

    /// <summary>누가/무엇이. Kind 에 따라 뜻이 다르다.</summary>
    public int Subject { get; set; }

    /// <summary>중요도.</summary>
    public byte Salience { get; set; }
}

/// <summary>
/// <c>GET /npc/{id}</c> 응답. <c>Npc.Host.Api.NpcTrace</c> 와 같은 모양이다.
///
/// <b>클라이언트가 그 타입을 참조하지 않고 다시 적는다.</b> <c>Npc.Host</c> 를 참조하면
/// 테스트 클라이언트가 NPC 서버 전체(그리고 LLM 까지)를 끌고 오게 된다 — 단방향 잎이
/// 아니게 되는 것이고, docs/20 §4 의 의존 그래프가 무너진다.
/// <b>모르는 필드는 무시된다</b>(<c>System.Text.Json</c> 기본) — 서버가 필드를 더해도 안 깨진다.
/// </summary>
public sealed class NpcTraceDto
{
    /// <summary>그 첨자에 NPC 가 있는가.</summary>
    public bool Found { get; set; }

    /// <summary>NPC 첨자.</summary>
    public int Npc { get; set; }

    /// <summary>스냅샷을 뜬 틱.</summary>
    public long Tick { get; set; }

    /// <summary>아키타입 id.</summary>
    public string Archetype { get; set; } = string.Empty;

    /// <summary>현재 존 id.</summary>
    public string Zone { get; set; } = string.Empty;

    /// <summary>현재 POI id.</summary>
    public string Poi { get; set; } = string.Empty;

    /// <summary>자택 POI id.</summary>
    public string HomePoi { get; set; } = string.Empty;

    /// <summary>일터 POI id.</summary>
    public string WorkPoi { get; set; } = string.Empty;

    /// <summary>인지 LOD 등급.</summary>
    public byte Lod { get; set; }

    /// <summary>HP.</summary>
    public short Hp { get; set; }

    /// <summary>스태미나.</summary>
    public short Stamina { get; set; }

    /// <summary>스텝 실행 상태.</summary>
    public string StepStatus { get; set; } = string.Empty;

    /// <summary>현재 플랜 id.</summary>
    public int PlanId { get; set; }

    /// <summary>플랜 출처 — <c>bucket</c>·<c>individual</c>·<c>fallback</c>.</summary>
    public string PlanKind { get; set; } = string.Empty;

    /// <summary>플랜의 goal.</summary>
    public string PlanGoal { get; set; } = string.Empty;

    /// <summary>플랜이 만들어진 버킷 표기.</summary>
    public string PlanBucket { get; set; } = string.Empty;

    /// <summary>플랜이 순환하는가.</summary>
    public bool PlanLoop { get; set; }

    /// <summary>이 플랜을 쓴 지 몇 틱 됐나.</summary>
    public long PlanAgeTicks { get; set; }

    /// <summary>워커가 걸어 둔 새 플랜. 0 이면 없음.</summary>
    public int PendingPlanId { get; set; }

    /// <summary>인터럽트가 남긴 긴급도.</summary>
    public byte PendingUrgency { get; set; }

    /// <summary>참인 월드 플래그 이름들.</summary>
    public string[] Flags { get; set; } = [];

    /// <summary>0 이 아닌 인벤토리 항목.</summary>
    public string[] Inventory { get; set; } = [];

    /// <summary>플랜의 스텝.</summary>
    public TraceStepDto[] Steps { get; set; } = [];

    /// <summary>최근 사건.</summary>
    public TraceEventDto[] Recent { get; set; } = [];
}

/// <summary>틱 패널의 일부. 링크 탭이 쓴다.</summary>
public sealed class TickPanelDto
{
    /// <summary>틱 p99(ms).</summary>
    public double P99Ms { get; set; }

    /// <summary>틱 p50(ms).</summary>
    public double P50Ms { get; set; }

    /// <summary>예산 초과 횟수.</summary>
    public long Overruns { get; set; }

    /// <summary>처리한 틱 수.</summary>
    public long Ticks { get; set; }

    /// <summary>Gen0 수집 횟수.</summary>
    public int Gen0Collections { get; set; }

    /// <summary>게임 일수.</summary>
    public long GameDay { get; set; }

    /// <summary>게임 시각.</summary>
    public int GameHour { get; set; }

    /// <summary>시간대 이름.</summary>
    public string TimeOfDay { get; set; } = string.Empty;
}

/// <summary>재계획 패널의 일부.</summary>
public sealed class ReplanPanelDto
{
    /// <summary>큐 깊이.</summary>
    public int QueueDepth { get; set; }

    /// <summary>이탈 판정 누계.</summary>
    public long Deviations { get; set; }

    /// <summary>인터럽트가 즉시 발행한 액션 수.</summary>
    public long InterruptsForced { get; set; }
}

/// <summary><c>GET /metrics</c> 응답 중 링크 탭이 쓰는 부분.</summary>
public sealed class MetricsDto
{
    /// <summary>틱 패널.</summary>
    public TickPanelDto? Tick { get; set; }

    /// <summary>재계획 패널.</summary>
    public ReplanPanelDto? Replan { get; set; }

    /// <summary>LLM 호출 수. 킬스위치가 듣는지 여기서 본다 (docs/20 §11.3).</summary>
    public long LlmCalls { get; set; }
}

/// <summary>
/// NPC 서버의 디버그 HTTP 를 읽는 소스 생성 컨텍스트.
///
/// <b>ASP.NET Core 최소 API 는 camelCase 로 낸다</b>(<c>JsonSerializerDefaults.Web</c>).
/// 대소문자 무시까지 켜 두면 서버가 정책을 바꿔도 이쪽이 안 깨진다.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NpcTraceDto))]
[JsonSerializable(typeof(MetricsDto))]
public sealed partial class NpcServerJson : JsonSerializerContext;

/// <summary>
/// NPC 서버 HTTP 폴링. docs/20 §2 · §9.5.
///
/// <para>
/// <b>왜 클라이언트가 NPC 서버를 직접 보는가.</b> 플랜 id·스텝 번호·월드 플래그는
/// <b>게임서버가 알 수 없는 정보</b>다. 링크에는 그런 필드가 없고(N1·N3) 있어서도 안 된다 —
/// 게임서버를 경유해 중계하면 실제로는 존재하지 않는 결합을 테스트 베드가 만들어 낸다
/// (docs/20 §2).
/// </para>
///
/// <para>
/// <b>실패는 조용히 넘긴다.</b> NPC 서버가 죽어 있어도 클라이언트는 계속 돈다 —
/// 패널이 "미연결" 로 보일 뿐이다 (docs/20 §15).
/// </para>
/// </summary>
public sealed class NpcServerHttp : IDisposable
{
    /// <summary>폴링 주기(ms). docs/20 §9.5.</summary>
    public const int PollIntervalMillis = 1_000;

    /// <summary>한 번의 요청에 기다리는 시간(ms). 짧게 잡는다 — 화면이 이것 때문에 멎으면 안 된다.</summary>
    public const int TimeoutMillis = 700;

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    private NpcTraceDto? _trace;
    private MetricsDto? _metrics;
    private int _target = -1;
    private int _reachable;

    /// <summary>폴러를 만든다. <b>여기서 요청하지 않는다</b> — <see cref="RunAsync"/> 다.</summary>
    public NpcServerHttp(string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(TimeoutMillis) };
    }

    /// <summary>마지막 요청이 성공했는가. false 면 패널이 "NPC 서버 미연결" 을 보인다.</summary>
    public bool Reachable => Volatile.Read(ref _reachable) != 0;

    /// <summary>가장 최근 추적. 아직 없거나 대상이 없으면 null.</summary>
    public NpcTraceDto? Trace => Volatile.Read(ref _trace);

    /// <summary>가장 최근 계측.</summary>
    public MetricsDto? Metrics => Volatile.Read(ref _metrics);

    /// <summary>성공한 요청 수.</summary>
    public long Polls { get; private set; }

    /// <summary>실패한 요청 수. 0 이 아닌 것 자체는 정상이다 — NPC 서버가 늦게 뜰 수 있다.</summary>
    public long Failures { get; private set; }

    /// <summary>
    /// 볼 NPC. -1 이면 추적을 쉰다.
    ///
    /// <b>바뀌면 곧바로 낡은 값을 버린다</b> — 안 그러면 다음 폴링까지 1초 동안
    /// 다른 NPC 의 플랜이 새 선택의 것인 양 화면에 남는다.
    /// </summary>
    public int Target
    {
        get => Volatile.Read(ref _target);
        set
        {
            if (Volatile.Read(ref _target) == value)
            {
                return;
            }

            Volatile.Write(ref _target, value);
            Volatile.Write(ref _trace, null);
        }
    }

    /// <summary>1초마다 폴링한다. 취소될 때까지 돈다. <b>예외를 밖으로 내보내지 않는다.</b></summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool ok = await PollAsync(ct).ConfigureAwait(false);

            Volatile.Write(ref _reachable, ok ? 1 : 0);

            try
            {
                await Task.Delay(PollIntervalMillis, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// <c>POST /control/killswitch?target=…</c>. docs/20 §11.4.
    ///
    /// <para>
    /// <b>이 경로는 <c>--dev-control</c> 일 때만 열린다.</b> 없으면 라우트 자체가 없어서 404 다 —
    /// 조건부 401/403 이 아니라 <b>조건부 등록</b>이기 때문이다. "핸들러가 있고 거절한다" 는
    /// 실수 하나로 열리지만, 라우트가 없으면 열릴 수가 없다.
    /// </para>
    ///
    /// <para>
    /// <b>킬스위치는 링크로 못 보낸다.</b> NPC 서버 안쪽 상태이고 <c>GameEvent</c> 에 그런 종류가
    /// 없다 — 만들면 N 규칙을 어긴다 (docs/20 §8.3). 그래서 디버그 HTTP 로 직접 간다.
    /// </para>
    /// </summary>
    /// <param name="target"><c>T2</c>·<c>T1</c>·<c>PlanStore</c>.</param>
    /// <returns>사람이 읽을 결과 한 줄.</returns>
    public async Task<string> KillSwitchAsync(string target, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await _http
                .PostAsync(new Uri($"{_baseUrl}/control/killswitch?target={target}"), null, ct)
                .ConfigureAwait(false);

            return response.StatusCode switch
            {
                System.Net.HttpStatusCode.NotFound => "미연결 — NPC 서버에 --dev-control 이 없다",
                System.Net.HttpStatusCode.BadRequest => $"거절 — 모르는 target: {target}",
                _ when response.IsSuccessStatusCode => $"{target} 끊음",
                _ => $"실패 — {(int)response.StatusCode}",
            };
        }
        catch (Exception)
        {
            return "미연결 — NPC 서버에 닿지 않는다";
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task<bool> PollAsync(CancellationToken ct)
    {
        try
        {
            MetricsDto? metrics = await _http.GetFromJsonAsync(
                $"{_baseUrl}/metrics", NpcServerJson.Default.MetricsDto, ct).ConfigureAwait(false);

            Volatile.Write(ref _metrics, metrics);

            int target = Target;

            if (target >= 0)
            {
                NpcTraceDto? trace = await _http.GetFromJsonAsync(
                    $"{_baseUrl}/npc/{target}", NpcServerJson.Default.NpcTraceDto, ct)
                    .ConfigureAwait(false);

                // 사이에 선택이 바뀌었으면 버린다. 늦게 온 응답이 새 선택을 덮으면
                // 화면이 한 박자 전 NPC 를 보여 준다.
                if (Target == target)
                {
                    Volatile.Write(ref _trace, trace);
                }
            }

            Polls++;

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // NPC 서버가 없거나 늦다. 조용히 넘긴다 (docs/20 §9.5).
            Failures++;

            return false;
        }
    }
}
