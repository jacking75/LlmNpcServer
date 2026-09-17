using System.Collections.Immutable;
using System.Text.Json;

namespace Npc.Studio.Services;

/// <summary>
/// 실행 중인 서버가 말하는 NPC 하나 (T28).
/// <b>필드 이름의 정답은 <c>docs/openapi.json</c> 의 <c>NpcSummary</c> 다</b> —
/// 여기서 지어내면 화면이 조용히 빈 값을 그린다.
/// </summary>
/// <param name="Id">NPC 번호 (<c>npc</c>).</param>
/// <param name="Slot">NpcStore 슬롯.</param>
/// <param name="Archetype">직업 id.</param>
/// <param name="Zone">지금 있는 지역.</param>
/// <param name="Poi">지금 있는 장소.</param>
/// <param name="Action">지금 하는 행동.</param>
/// <param name="Lod">인지 LOD 밴드.</param>
/// <param name="StepStatus">스텝 상태 (<c>Ready</c>·<c>Waiting</c>…).</param>
/// <param name="PlanKind">지금 도는 플랜의 종류.</param>
public sealed record LiveNpc(
    int Id,
    int Slot,
    string Archetype,
    string Zone,
    string Poi,
    string Action,
    int Lod,
    string StepStatus,
    string PlanKind);

/// <summary>서버 상태 한 줄 (T28).</summary>
/// <param name="Connected">붙었는가.</param>
/// <param name="Message">사람이 읽는 상태.</param>
/// <param name="Npcs">받은 NPC.</param>
public sealed record LiveSnapshot(bool Connected, string Message, ImmutableArray<LiveNpc> Npcs);

/// <summary>
/// 실행 중인 NPC 서버를 본다 (T28).
///
/// <para>
/// <b><c>Npc.Host</c> 를 참조하지 않는다</b> (CLAUDE.md §3). B-08 대시보드 엔드포인트를
/// <see cref="HttpClient"/> 로 부를 뿐이고, 응답 모양의 정답은 <c>docs/openapi.json</c> 이다.
/// </para>
///
/// <para>예측은 기대 경로다. 실제를 같은 지도에 놓아야 "예측이 맞는가" 가 보인다.</para>
/// </summary>
public sealed class LiveClient(StudioOptions options) : IDisposable
{
    private readonly HttpClient _http = Create(options);

    /// <summary>서버 주소. 비면 이 화면을 쓰지 않는다.</summary>
    public string Server => options.Server;

    /// <summary>
    /// 서버가 쓸 수 있게 설정돼 있는가 (H08).
    ///
    /// <b>주소를 여기서 검사한다</b> — <c>--server localhost:1</c> 처럼 스킴이 없으면
    /// <c>HttpClient</c> 생성자가 던지고, 그 예외가 관찰 루프 안에서 나면 회로가 죽는다.
    /// </summary>
    public bool Configured => _http.BaseAddress is not null;

    /// <summary>주소가 왜 쓸 수 없는가. 쓸 수 있으면 빈 문자열.</summary>
    public string ConfigProblem =>
        options.Server.Length == 0
            ? "서버 주소가 없다 — `--server http://127.0.0.1:<port>` 로 준다."
            : _http.BaseAddress is null
                ? $"서버 주소가 잘못됐다: `{options.Server}` — `http://` 나 `https://` 로 시작해야 한다."
                : string.Empty;

    /// <summary>지금 NPC 목록을 받아 온다.</summary>
    /// <param name="token">취소 토큰.</param>
    public async Task<LiveSnapshot> SnapshotAsync(CancellationToken token = default)
    {
        if (!Configured)
        {
            return new LiveSnapshot(false, ConfigProblem, []);
        }

        try
        {
            using HttpResponseMessage response = await _http.GetAsync(new Uri("npcs", UriKind.Relative), token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                string reason = (int)response.StatusCode switch
                {
                    401 or 403 => $"서버가 거절했다({(int)response.StatusCode}) — `--token` 을 확인한다.",
                    404 => "서버에 `/npcs` 가 없다 — 대시보드가 켜져 있는지 본다.",
                    _ => $"서버가 {(int)response.StatusCode} 로 답했다.",
                };

                return new LiveSnapshot(false, reason, []);
            }

            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            return new LiveSnapshot(true, $"{options.Server} 에 붙어 있다.", Parse(body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException or InvalidOperationException or OperationCanceledException)
        {
            return new LiveSnapshot(
                false, $"서버 {options.Server} 에 연결할 수 없다 — 떠 있는지 · 포트가 맞는지 본다.", []);
        }
    }

    /// <summary>
    /// 대시보드 응답을 읽는다. <b>모양이 조금 달라도 읽는다</b> — 배열이 최상위일 수도,
    /// <c>npcs</c> 아래에 있을 수도 있다 (<c>docs/openapi.json</c> 이 정답이다).
    /// </summary>
    /// <param name="json">응답 본문.</param>
    public static ImmutableArray<LiveNpc> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        JsonElement array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("npcs", out JsonElement npcs) ? npcs : default;

        if (array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = ImmutableArray.CreateBuilder<LiveNpc>();

        foreach (JsonElement item in array.EnumerateArray())
        {
            list.Add(new LiveNpc(
                Int(item, "npc"),
                Int(item, "slot"),
                Text(item, "archetype"),
                Text(item, "zone"),
                Text(item, "poi"),
                Text(item, "action"),
                Int(item, "lod"),
                Text(item, "stepStatus"),
                Text(item, "planKind")));
        }

        return list.ToImmutable();
    }

    private static HttpClient Create(StudioOptions options)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        // 스킴 없는 주소(`localhost:1`)는 절대 URI 가 아니다 — 던지지 않고 "설정 안 됨" 으로 둔다.
        if (options.Server.Length > 0
            && Uri.TryCreate(options.Server.TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseAddress)
            && (baseAddress.Scheme == Uri.UriSchemeHttp || baseAddress.Scheme == Uri.UriSchemeHttps))
        {
            http.BaseAddress = baseAddress;
        }

        if (options.Token.Length > 0)
        {
            http.DefaultRequestHeaders.Add("X-Npc-Token", options.Token);
        }

        return http;
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString()
            : string.Empty;

    private static int Int(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int number) ? number : 0;

    /// <summary>HTTP 클라이언트를 놓는다.</summary>
    public void Dispose() => _http.Dispose();
}
