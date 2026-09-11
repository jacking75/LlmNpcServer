using System.ComponentModel;
using System.Globalization;
using System.Net.Http.Headers;
using ModelContextProtocol.Server;

namespace Npc.Mcp.Tools;

/// <summary>
/// 돌고 있는 서버에 묻는 툴 (E-03 · B-08).
///
/// <para>
/// <b>응답을 가공하지 않는다.</b> 서버가 낸 JSON 을 그대로 돌려준다 — 여기서 요약하면
/// 모델이 보는 것과 사람이 대시보드에서 보는 것이 달라지고, 장애 대응에서 그 차이가 문제가 된다.
/// </para>
///
/// <para>
/// <b>토큰은 인자로 받는다.</b> 이 프로세스가 환경변수에서 읽어 두면 MCP 를 붙인 모든
/// 세션이 관리 권한을 갖게 된다 — 부르는 쪽이 명시하게 한다.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class ServerTools
{
    /// <summary>기본 서버 주소. 호스트의 기본 바인드와 같다.</summary>
    public const string DefaultUrl = "http://127.0.0.1:5080";

    /// <summary>요청 제한 시간(초). 서버가 죽어 있을 때 모델을 오래 기다리게 하지 않는다.</summary>
    public const int TimeoutSeconds = 5;

    /// <summary>기동 요약 · 버전 · 계약 · 해시.</summary>
    /// <param name="url">서버 주소.</param>
    [McpServerTool(Name = "server_status")]
    [Description(
        "돌고 있는 NPC 서버의 기동 요약을 읽는다 — 버전·계약·마스터데이터 해시·링크 상태. "
        + "'어느 데이터로 떠 있나' 가 장애 대응의 첫 질문이다.")]
    public static Task<string> StatusAsync(
        [Description("서버 주소. 기본 http://127.0.0.1:5080")] string? url = null) =>
        GetAsync(url, "/status", null);

    /// <summary>메트릭 스냅샷.</summary>
    /// <param name="url">서버 주소.</param>
    [McpServerTool(Name = "server_metrics")]
    [Description(
        "메트릭 스냅샷을 읽는다 — 틱 p99·틱당 바이트·재계획 큐·캐시 히트율·비용. "
        + "틱 루프를 건드린 뒤에는 bytesPerTick 이 0 인지 여기서 본다.")]
    public static Task<string> MetricsAsync(
        [Description("서버 주소")] string? url = null) => GetAsync(url, "/metrics", null);

    /// <summary>NPC 한 마리의 추적.</summary>
    /// <param name="id">전역 NPC id.</param>
    /// <param name="url">서버 주소.</param>
    /// <param name="context">재계획 맥락까지 볼 것인가.</param>
    [McpServerTool(Name = "server_npc")]
    [Description(
        "NPC 한 마리를 추적한다 — 지금 플랜·스텝·플래그·인벤토리·최근 사건. "
        + "id 는 npc_instances.json 의 전역 id 다(슬롯 번호가 아니다).")]
    public static Task<string> NpcAsync(
        [Description("전역 NPC id")] int id,
        [Description("서버 주소")] string? url = null,
        [Description("true 면 재계획 맥락(/context)까지 읽는다")] bool context = false) =>
        GetAsync(
            url,
            context
                ? "/npc/" + id.ToString(CultureInfo.InvariantCulture) + "/context"
                : "/npc/" + id.ToString(CultureInfo.InvariantCulture),
            null);

    /// <summary>NPC 목록.</summary>
    /// <param name="url">서버 주소.</param>
    /// <param name="limit">최대 개수.</param>
    /// <param name="archetype">아키타입 필터.</param>
    /// <param name="zone">존 필터.</param>
    [McpServerTool(Name = "server_npcs")]
    [Description("NPC 목록을 읽는다. 아키타입·존으로 좁힐 수 있다.")]
    public static Task<string> NpcsAsync(
        [Description("서버 주소")] string? url = null,
        [Description("최대 개수. 기본 20")] int limit = 20,
        [Description("아키타입 id 필터")] string? archetype = null,
        [Description("존 id 필터")] string? zone = null)
    {
        var query = new List<string> { "limit=" + limit.ToString(CultureInfo.InvariantCulture) };

        if (!string.IsNullOrEmpty(archetype))
        {
            query.Add("archetype=" + Uri.EscapeDataString(archetype));
        }

        if (!string.IsNullOrEmpty(zone))
        {
            query.Add("zone=" + Uri.EscapeDataString(zone));
        }

        return GetAsync(url, "/npcs?" + string.Join('&', query), null);
    }

    /// <summary>
    /// GET 하나. <b>실패를 예외로 바꾸지 않는다</b> — 모델에게는 "왜 안 됐나" 가 답이다.
    /// </summary>
    internal static async Task<string> GetAsync(string? url, string path, string? token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };

        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        string target = (string.IsNullOrEmpty(url) ? DefaultUrl : url.TrimEnd('/')) + path;

        try
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri(target))
                .ConfigureAwait(false);

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? body
                : $"[HTTP {(int)response.StatusCode}] {target}\n{body}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return
                $"[연결 실패] {target}\n{ex.Message}\n"
                + "서버가 떠 있는지 본다: dotnet run -c Release --project src/Npc.Host -- --loopback --no-llm";
        }
    }
}
