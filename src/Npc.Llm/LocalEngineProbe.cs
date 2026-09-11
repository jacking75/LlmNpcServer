using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Npc.Contracts;

namespace Npc.Llm;

/// <summary>
/// 헬스 확인 한 번의 결과.
/// </summary>
/// <param name="Ok">응답이 왔고 모델 목록이 비어 있지 않은가.</param>
/// <param name="ModelId">응답이 말한 모델 id. 모르면 빈 문자열.</param>
/// <param name="Detail">사람이 읽는 한 줄. 실패면 <b>왜</b>가 여기 있다.</param>
public readonly record struct ProbeReading(bool Ok, string ModelId, string Detail)
{
    /// <summary>실패 하나.</summary>
    /// <param name="why">사유.</param>
    public static ProbeReading Down(string why) => new(false, string.Empty, why);
}

/// <summary>
/// 로컬 추론 프로세스 감독 (C-08).
///
/// <para>
/// <b>dotLLM·llama.cpp 는 별도 프로세스다</b> (GPLv3 경계 · CLAUDE.md §2.7). 죽으면 T1 이
/// 사라지는데 아무도 재시작하지 않고, NPC 서버 쪽에서는 <b>요청이 타임아웃될 때까지</b>
/// 알 방법이 없었다. 요청당 5초짜리 지연이 워커 수만큼 쌓이는 동안 재계획 큐는 계속 찬다.
/// </para>
///
/// <para>
/// <b>고치는 것이 아니라 알리고 비켜 가는 것이다.</b> 프로세스를 되살리는 것은 오케스트레이터의
/// 일이다(<c>deploy/compose.yaml</c> 의 <c>restart</c>) — 여기서 하는 일은 <b>죽었다는 사실을
/// 60초 안에 알고</b>, 그동안 T1 요청을 T2 로 흘려 보내는 것뿐이다.
/// </para>
///
/// <para>
/// <b>HTTP 를 직접 알지 않는다.</b> 읽기 동작을 주입받으므로 가짜로 테스트할 수 있다 —
/// "죽으면 어떻게 되나" 를 확인하려고 진짜 프로세스를 죽일 수는 없다.
/// </para>
/// </summary>
public sealed class LocalEngineProbe : IAsyncDisposable
{
    /// <summary>확인 주기(초). 완료 조건이 "60초 안에" 라 그보다 촘촘해야 한다.</summary>
    public const int DefaultIntervalSeconds = 30;

    /// <summary>한 번의 확인에 주는 시간(초). 넘으면 죽은 것으로 본다.</summary>
    public const int DefaultTimeoutSeconds = 5;

    private readonly Func<CancellationToken, Task<ProbeReading>> _read;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();

    private Task? _loop;
    private int _healthy = 1;

    /// <summary>프로브를 만든다. <b>여기서 돌기 시작하지 않는다</b> — <see cref="Start"/> 다.</summary>
    /// <param name="engineId">감시할 엔진 id. 알람 메시지에 들어간다.</param>
    /// <param name="read">헬스 확인 동작. 테스트는 가짜를 넣는다.</param>
    /// <param name="intervalSeconds">주기(초). 0 이면 기본값.</param>
    public LocalEngineProbe(
        string engineId,
        Func<CancellationToken, Task<ProbeReading>> read,
        int intervalSeconds = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(engineId);
        ArgumentNullException.ThrowIfNull(read);

        EngineId = engineId;
        _read = read;
        _interval = TimeSpan.FromSeconds(
            intervalSeconds > 0 ? intervalSeconds : DefaultIntervalSeconds);
    }

    /// <summary>감시 중인 엔진 id.</summary>
    public string EngineId { get; }

    /// <summary>
    /// 기대하는 모델 파일 해시 (C-08). 비어 있으면 대조하지 않는다.
    ///
    /// <b>불일치는 경고이지 차단이 아니다.</b> 모델을 바꾼 것이 의도된 배포일 수 있고,
    /// 여기서 막으면 사람이 이 검사를 꺼 버린다 — 대신 <see cref="HashMismatch"/> 가 서고
    /// 알람이 한 번 나간다.
    /// </summary>
    public string ExpectedModelSha256 { get; init; } = string.Empty;

    /// <summary>지금 살아 있다고 보는가. <b>첫 확인 전에는 살아 있다고 본다</b> — 죽었다는 증거가 없다.</summary>
    public bool Healthy => Volatile.Read(ref _healthy) != 0;

    /// <summary>확인 횟수.</summary>
    public long Checks { get; private set; }

    /// <summary>실패 확인 횟수.</summary>
    public long Failures { get; private set; }

    /// <summary>살아 있음 → 죽음 전이 횟수. <b>이 값이 오르면 사이드카가 흔들린다.</b></summary>
    public long Outages { get; private set; }

    /// <summary>모델 해시가 기대와 달랐는가 (C-08). 경고이지 차단이 아니다.</summary>
    public bool HashMismatch { get; private set; }

    /// <summary>마지막 확인의 사유 한 줄. <c>/status</c> 가 보여 준다.</summary>
    public string LastDetail { get; private set; } = "아직 확인 전";

    /// <summary>
    /// 죽음·복귀를 알린다. 인자는 (살아 있는가, 사유).
    /// <b>전이에서만 부른다</b> — 매 확인마다 부르면 알람이 30초마다 온다.
    /// </summary>
    public Action<bool, string>? OnChange { get; init; }

    /// <summary>돌기 시작한다. 취소될 때까지 <see cref="_interval"/> 주기로 돈다.</summary>
    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);

    /// <summary>
    /// 한 번 확인하고 상태를 갱신한다. <b>테스트가 이것만 부른다</b> — 루프를 기다리지 않는다.
    /// </summary>
    /// <param name="cancellationToken">취소.</param>
    public async Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        ProbeReading reading;

        try
        {
            reading = await _read(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            reading = ProbeReading.Down(e.Message);
        }

        Checks++;
        LastDetail = reading.Detail;

        if (!reading.Ok)
        {
            Failures++;
        }

        // 해시 대조는 살아 있을 때만 뜻이 있다. 죽은 응답에서 읽은 빈 문자열로 경보하지 않는다.
        if (reading.Ok
            && ExpectedModelSha256.Length > 0
            && reading.ModelId.Length > 0
            && !reading.ModelId.Contains(ExpectedModelSha256, StringComparison.OrdinalIgnoreCase)
            && !HashMismatch)
        {
            HashMismatch = true;

            OnChange?.Invoke(
                true,
                $"{EngineId}: 모델이 기대와 다르다 — 기대 {Short(ExpectedModelSha256)} · "
                + $"응답 {reading.ModelId}. 플랜 품질이 조용히 바뀐다.");
        }

        bool wasHealthy = Healthy;

        Volatile.Write(ref _healthy, reading.Ok ? 1 : 0);

        if (wasHealthy == reading.Ok)
        {
            return reading.Ok;
        }

        if (!reading.Ok)
        {
            Outages++;
        }

        OnChange?.Invoke(
            reading.Ok,
            reading.Ok
                ? $"{EngineId}: 로컬 엔진이 돌아왔다."
                : $"{EngineId}: 로컬 엔진이 응답하지 않는다 — {reading.Detail}. "
                  + "T1 요청은 T2 로 우회한다.");

        return reading.Ok;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);

        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 종료 지시다. 정상이다.
            }
        }

        _stop.Dispose();
    }

    /// <summary>
    /// OpenAI 호환 <c>/v1/models</c> 를 읽는 기본 동작.
    ///
    /// <b>모델 목록이 비어 있으면 죽은 것으로 본다.</b> 200 을 주면서 빈 목록을 내는 서버가
    /// 있는데, 그 상태로는 어떤 요청도 성공하지 않는다.
    /// </summary>
    /// <param name="client">HTTP 클라이언트. 엔드포인트가 기준 주소다.</param>
    /// <param name="timeoutSeconds">한 번에 주는 시간(초). 0 이면 기본값.</param>
    public static Func<CancellationToken, Task<ProbeReading>> HttpReader(
        HttpClient client, int timeoutSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(client);

        TimeSpan timeout = TimeSpan.FromSeconds(
            timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds);

        return async ct =>
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);

            window.CancelAfter(timeout);

            try
            {
                using HttpResponseMessage response = await client
                    .GetAsync("models", window.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return ProbeReading.Down(
                        string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)response.StatusCode}"));
                }

                JsonElement body = await response.Content
                    .ReadFromJsonAsync<JsonElement>(window.Token)
                    .ConfigureAwait(false);

                return Read(body);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
            {
                return ProbeReading.Down(e is OperationCanceledException ? "응답 없음 (타임아웃)" : e.Message);
            }
        };
    }

    /// <summary><c>/v1/models</c> 응답 본문을 읽는다. <b>공개로 둔다</b> — 파싱을 따로 테스트한다.</summary>
    /// <param name="body">응답 본문.</param>
    public static ProbeReading Read(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0)
        {
            return ProbeReading.Down("모델 목록이 비어 있다");
        }

        JsonElement first = data[0];
        string id = first.ValueKind == JsonValueKind.Object
            && first.TryGetProperty("id", out JsonElement idValue)
            && idValue.ValueKind == JsonValueKind.String
            ? idValue.GetString() ?? string.Empty
            : string.Empty;

        return new ProbeReading(true, id, $"모델 {data.GetArrayLength()}종 · {id}");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await CheckAsync(ct).ConfigureAwait(false);

            try
            {
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];
}
