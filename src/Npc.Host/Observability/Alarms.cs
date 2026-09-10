using System.Collections.Concurrent;
using System.Text.Json;

namespace Npc.Host.Observability;

/// <summary>
/// 경보 종류 (A-05).
///
/// <b>이름을 그대로 쿨다운 키의 일부로 쓴다.</b> 종류가 늘면 여기 뒤에 추가한다 —
/// 번호에 의미가 없으므로 재배치해도 되지만, 로그·웹훅에 이름이 실리므로 이름은 안정적이어야 한다.
/// </summary>
public enum AlarmKind
{
    /// <summary>일일 토큰 예산 소진율이 임계를 넘었다 (C-02).</summary>
    TokenBudget,

    /// <summary>일일 비용 예산 소진율이 임계를 넘었다 (C-02).</summary>
    CostBudget,

    /// <summary>티어가 강등됐다 (T2 → T1).</summary>
    TierDowngraded,

    /// <summary>예산이 없어 재계획을 거절했다.</summary>
    ReplanRejected,

    /// <summary>레이트 리밋에 걸렸다. 예전에는 로그도 남지 않았다.</summary>
    RateLimited,

    /// <summary>서킷 브레이커가 열렸다.</summary>
    BreakerOpened,

    /// <summary>제공사 페일오버가 일어났다 (C-01).</summary>
    Failover,

    /// <summary>링크 상태가 바뀌었다 (A-03).</summary>
    LinkState,

    /// <summary>이벤트 시퀀스에 갭이 생겼다 (N6).</summary>
    EventGap,

    /// <summary>틱 루프에서 힙 할당이 생겼다 (CLAUDE.md §2.1).</summary>
    TickAllocation,

    /// <summary>틱 p99 가 예산을 넘었다.</summary>
    TickOverrun,

    /// <summary>스냅샷을 못 썼다 (A-01). 상태 손실 창이 주기보다 커진다.</summary>
    SnapshotFailed,

    /// <summary><c>TickSync</c> 가 멈췄다 (A-10).</summary>
    TickSyncStalled,

    /// <summary>로컬 추론 엔진이 응답하지 않는다 (C-08).</summary>
    LocalEngineDown,
}

/// <summary>경보 심각도. 라우팅과 색깔을 가른다.</summary>
public enum AlarmSeverity
{
    /// <summary>알아 두면 좋다. 지금 조치할 일은 없다.</summary>
    Info,

    /// <summary>지금은 돌지만 곧 문제가 된다.</summary>
    Warning,

    /// <summary>사람이 지금 봐야 한다.</summary>
    Critical,
}

/// <summary>
/// 경보 한 건.
/// </summary>
/// <param name="Kind">종류.</param>
/// <param name="Severity">심각도.</param>
/// <param name="Key">
/// 같은 사건을 묶는 열쇠. 쿨다운은 <c>(Kind, Key)</c> 단위다 —
/// 예를 들어 링크 상태는 상태 이름이, 페일오버는 엔진 id 가 열쇠다.
/// </param>
/// <param name="Message">사람이 읽는 한 줄.</param>
/// <param name="Value">숫자 값. 없으면 <c>null</c>.</param>
public readonly record struct AlarmPayload(
    AlarmKind Kind,
    AlarmSeverity Severity,
    string Key,
    string Message,
    double? Value = null);

/// <summary>
/// 경보 싱크 (A-05).
///
/// <b>경보가 대시보드 색깔로만 존재했다.</b> 예산 소진·강등·거절을 새로고침하기 전에는 아무도
/// 몰랐고, <c>RateLimited</c> 는 로그조차 남지 않았다.
/// </summary>
public interface IAlarmSink
{
    /// <summary>경보를 올린다. <b>던지지 않는다</b> — 경보가 프로세스를 죽이면 그것이 장애다.</summary>
    void Raise(in AlarmPayload payload);
}

/// <summary>아무것도 하지 않는 싱크. 테스트와 "경보 없음" 회차용.</summary>
public sealed class NullAlarmSink : IAlarmSink
{
    /// <summary>공유 인스턴스.</summary>
    public static NullAlarmSink Instance { get; } = new();

    /// <inheritdoc />
    public void Raise(in AlarmPayload payload)
    {
        // 일부러 비어 있다.
    }
}

/// <summary>
/// 중복 억제 데코레이터 (A-05).
///
/// 같은 <c>(Kind, Key)</c> 는 쿨다운 안에 한 번만 통과시킨다. <b>없으면 링크가 흔들릴 때
/// 초당 수십 건이 웹훅으로 나가고, 그 순간 진짜 경보가 묻힌다.</b>
///
/// 벽시계를 본다 — 경보는 게임 로직이 아니라 운영이고, 게임 틱으로 재면 배속에 따라
/// 쿨다운이 늘었다 줄었다 한다.
/// </summary>
public sealed class CooldownAlarmSink : IAlarmSink
{
    private readonly IAlarmSink _inner;
    private readonly long _cooldownMs;
    private readonly ConcurrentDictionary<(AlarmKind, string), long> _lastMs = new();
    private readonly Func<long> _now;

    /// <summary>데코레이터를 만든다.</summary>
    /// <param name="inner">안쪽 싱크.</param>
    /// <param name="cooldownSeconds">같은 열쇠의 재발화 간격. 0 이면 억제하지 않는다.</param>
    /// <param name="now">지금 시각(ms). 테스트가 가짜를 넣는다.</param>
    public CooldownAlarmSink(IAlarmSink inner, int cooldownSeconds, Func<long>? now = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(cooldownSeconds);

        _inner = inner;
        _cooldownMs = cooldownSeconds * 1_000L;
        _now = now ?? (() => Environment.TickCount64);
    }

    /// <summary>억제한 경보 수. 계측이 읽는다.</summary>
    public long Suppressed { get; private set; }

    /// <inheritdoc />
    public void Raise(in AlarmPayload payload)
    {
        if (_cooldownMs > 0)
        {
            long now = _now();
            (AlarmKind Kind, string Key) key = (payload.Kind, payload.Key);

            if (_lastMs.TryGetValue(key, out long last) && now - last < _cooldownMs)
            {
                Suppressed++;
                return;
            }

            _lastMs[key] = now;
        }

        _inner.Raise(in payload);
    }
}

/// <summary>여러 싱크에 같은 경보를 낸다. 하나가 실패해도 나머지는 받는다.</summary>
public sealed class CompositeAlarmSink : IAlarmSink
{
    private readonly IAlarmSink[] _sinks;

    /// <summary>합친다.</summary>
    public CompositeAlarmSink(params IAlarmSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);

        _sinks = sinks;
    }

    /// <inheritdoc />
    public void Raise(in AlarmPayload payload)
    {
        foreach (IAlarmSink sink in _sinks)
        {
            sink.Raise(in payload);
        }
    }
}

/// <summary>
/// 텍스트 싱크. 기본이다.
///
/// 형식을 고정한다 — <c>alarm kind=... sev=... key=... value=... msg=...</c>.
/// 수집기가 파싱할 수 있어야 하고, 사람도 읽을 수 있어야 한다.
/// </summary>
public sealed class TextAlarmSink : IAlarmSink
{
    private readonly TextWriter _writer;
    private readonly object _gate = new();

    /// <summary>싱크를 만든다.</summary>
    public TextAlarmSink(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _writer = writer;
    }

    /// <summary>올린 경보 수.</summary>
    public long Raised { get; private set; }

    /// <inheritdoc />
    public void Raise(in AlarmPayload payload)
    {
        string value = payload.Value is { } v
            ? v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            : "-";

        string line =
            $"alarm kind={payload.Kind} sev={payload.Severity} key={payload.Key} "
            + $"value={value} msg={payload.Message}";

        lock (_gate)
        {
            Raised++;
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }
}

/// <summary>
/// 웹훅 싱크 (A-05). Slack·Teams 호환 JSON 을 비동기로 보낸다.
///
/// <b>보내는 것이 경보를 막으면 안 된다.</b> 큐에 넣고 즉시 돌아온다 — 웹훅이 느리거나 죽어도
/// 호출부(틱 루프 바깥의 워커·호스트)가 멈추지 않는다. 큐가 차면 <b>가장 오래된 것을 버리고</b>
/// 버린 수를 센다. 최신 경보가 살아남는 편이 낫다.
/// </summary>
public sealed class WebhookAlarmSink : IAlarmSink, IAsyncDisposable
{
    /// <summary>큐 용량. 넘치면 오래된 것부터 버린다.</summary>
    public const int QueueCapacity = 256;

    private readonly Uri _url;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly IAlarmSink _fallback;
    private readonly System.Threading.Channels.Channel<AlarmPayload> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;

    /// <summary>싱크를 만든다.</summary>
    /// <param name="url">웹훅 주소.</param>
    /// <param name="fallback">전송에 실패했을 때 대신 적을 곳. 보통 텍스트 싱크다.</param>
    /// <param name="http">HTTP 클라이언트. null 이면 직접 만든다.</param>
    public WebhookAlarmSink(Uri url, IAlarmSink fallback, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(fallback);

        _url = url;
        _fallback = fallback;
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        _queue = System.Threading.Channels.Channel.CreateBounded<AlarmPayload>(
            new System.Threading.Channels.BoundedChannelOptions(QueueCapacity)
            {
                FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        _pump = Task.Run(() => PumpAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>보낸 수.</summary>
    public long Sent { get; private set; }

    /// <summary>보내지 못해 폴백으로 넘긴 수.</summary>
    public long Failed { get; private set; }

    /// <inheritdoc />
    public void Raise(in AlarmPayload payload)
    {
        // 실패해도 던지지 않는다. 경보가 프로세스를 죽이면 그것이 장애다.
        _ = _queue.Writer.TryWrite(payload);
    }

    /// <summary>Slack·Teams 가 둘 다 이해하는 최소 형태로 만든다.</summary>
    public static string Format(in AlarmPayload payload)
    {
        string value = payload.Value is { } v
            ? v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            : "-";

        return JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["text"] = $"[{payload.Severity}] {payload.Kind} ({payload.Key}) {payload.Message}",
            ["kind"] = payload.Kind.ToString(),
            ["severity"] = payload.Severity.ToString(),
            ["key"] = payload.Key,
            ["value"] = value,
        });
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();

        await _stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 종료로 끝난다.
        }

        _stop.Dispose();

        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (_queue.Reader.TryRead(out AlarmPayload payload))
            {
                await SendAsync(payload, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task SendAsync(AlarmPayload payload, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(
                Format(in payload), System.Text.Encoding.UTF8, "application/json");

            using HttpResponseMessage response =
                await _http.PostAsync(_url, content, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                Sent++;
                return;
            }

            Failed++;

            _fallback.Raise(payload with
            {
                Message = $"[웹훅 {(int)response.StatusCode}] {payload.Message}",
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Failed++;

            _fallback.Raise(payload with { Message = $"[웹훅 실패: {e.Message}] {payload.Message}" });
        }
    }
}
