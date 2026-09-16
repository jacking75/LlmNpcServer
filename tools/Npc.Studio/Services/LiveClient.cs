using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Npc.Studio.Services;

/// <summary>실행 중인 서버가 말하는 NPC 하나 (T28).</summary>
/// <param name="Id">NPC 번호.</param>
/// <param name="Archetype">직업 id.</param>
/// <param name="Zone">지금 있는 지역.</param>
/// <param name="Poi">지금 있는 장소.</param>
/// <param name="Action">지금 하는 행동.</param>
/// <param name="Step">플랜의 몇 번째 스텝인가.</param>
/// <param name="Flags">지금 서 있는 상태.</param>
public sealed record LiveNpc(
    int Id, string Archetype, string Zone, string Poi, string Action, int Step, string Flags);

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
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http = Create(options);

    /// <summary>서버 주소. 비면 이 화면을 쓰지 않는다.</summary>
    public string Server => options.Server;

    /// <summary>서버가 설정돼 있는가.</summary>
    public bool Configured => options.Server.Length > 0;

    /// <summary>지금 NPC 목록을 받아 온다.</summary>
    /// <param name="token">취소 토큰.</param>
    public async Task<LiveSnapshot> SnapshotAsync(CancellationToken token = default)
    {
        if (!Configured)
        {
            return new LiveSnapshot(false, "서버 주소가 없다 — --server http://127.0.0.1:<port> 로 준다.", []);
        }

        try
        {
            using HttpResponseMessage response = await _http.GetAsync(new Uri("npcs", UriKind.Relative), token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new LiveSnapshot(false, $"서버가 {(int)response.StatusCode} 로 답했다.", []);
            }

            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            return new LiveSnapshot(true, $"{options.Server} 에 붙어 있다.", Parse(body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new LiveSnapshot(false, "서버에 붙지 못했다 — " + ex.Message, []);
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
                Int(item, "id"),
                Text(item, "archetype"),
                Text(item, "zone"),
                Text(item, "poi"),
                Text(item, "action"),
                Int(item, "step"),
                Text(item, "flags")));
        }

        return list.ToImmutable();
    }

    private static HttpClient Create(StudioOptions options)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        if (options.Server.Length > 0)
        {
            http.BaseAddress = new Uri(options.Server.TrimEnd('/') + "/");
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
