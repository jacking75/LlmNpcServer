using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Npc.Host.Api;

/// <summary>
/// SSE 연결 수 상한 (B-08).
///
/// <para>
/// <b>상한이 없으면 GM 도구를 여러 개 띄운 것만으로</b> 응답 조립이 틱마다 수십 번 돈다 —
/// 조회는 다른 스레드지만 CPU 는 같이 쓴다. 틱 예산은 CPU 를 혼자 쓴다는 전제가 아니다.
/// </para>
/// </summary>
internal sealed class StreamLimiter(int limit)
{
    private int _open;

    /// <summary>동시에 열어 줄 연결 수. 0 이면 스트림을 열지 않는다.</summary>
    public int Limit { get; } = Math.Max(0, limit);

    /// <summary>지금 열려 있는 수.</summary>
    public int Open => Volatile.Read(ref _open);

    /// <summary>한 자리 잡는다. 자리가 없으면 false.</summary>
    public bool TryEnter()
    {
        if (Limit == 0)
        {
            return false;
        }

        if (Interlocked.Increment(ref _open) <= Limit)
        {
            return true;
        }

        Interlocked.Decrement(ref _open);

        return false;
    }

    /// <summary>자리를 놓는다.</summary>
    public void Exit() => Interlocked.Decrement(ref _open);
}

/// <summary>
/// <c>GET /stream/npcs?ids=1,2,3</c> — 변경분 SSE (B-08).
///
/// <para>
/// <b>1Hz 이고 바뀐 것만 보낸다.</b> 매 틱 보내면 10Hz × NPC 수가 되고, 안 바뀐 것까지
/// 보내면 그 대부분이 같은 값이다 — 둘 다 대역폭이 아니라 <b>응답 조립 CPU</b> 가 문제다.
/// </para>
///
/// <para>
/// <b>틱 루프와 같은 스레드가 아니다.</b> SoA 를 읽기만 하므로 한 틱 낡은 값을 볼 수 있고,
/// 진단에는 그것으로 충분하다.
/// </para>
/// </summary>
internal static class QueryStream
{
    /// <summary>보내는 주기(ms). 1Hz.</summary>
    public const int PeriodMillis = 1_000;

    /// <summary>한 연결이 볼 수 있는 NPC 수 상한.</summary>
    public const int MaxIds = 64;

    /// <summary>
    /// 직렬화 옵션. <b>ASP.NET 의 기본과 같은 camelCase 로 맞춘다</b> —
    /// <c>/npcs</c> 와 <c>/stream/npcs</c> 가 같은 모양이어야 클라이언트가 코드를 나눠 쓴다.
    /// </summary>
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 스트림을 돌린다. 클라이언트가 끊거나 <paramref name="ct"/> 가 취소될 때까지.
    /// </summary>
    /// <param name="http">응답.</param>
    /// <param name="host">호스트.</param>
    /// <param name="ids">쉼표로 이은 전역 NPC id. 비어 있으면 아무것도 안 보낸다.</param>
    /// <param name="ct">취소.</param>
    public static async Task RunAsync(HttpContext http, NpcHost host, string? ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(host);

        // A-08 — ids 는 전역 NPC id 다. 슬롯 번호가 아니다.
        int[] watched = QueryEndpoints.ParseIds(ids, host.GlobalIdSpace, MaxIds);

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";

        if (watched.Length == 0)
        {
            // 빈 요청도 연결은 연다 — 클라이언트가 ids 를 나중에 붙여 재연결하는 흐름이 정상이다.
            await WriteAsync(http, "error", "{\"error\":\"ids 가 비었다\"}", ct).ConfigureAwait(false);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, http.RequestAborted);

        // 직전에 보낸 것. 바뀐 것만 보내려면 비교 대상이 있어야 한다.
        var last = new string?[watched.Length];

        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                for (int i = 0; i < watched.Length; i++)
                {
                    string json = JsonSerializer.Serialize(host.Summary(watched[i]), s_json);

                    if (string.Equals(last[i], json, StringComparison.Ordinal))
                    {
                        continue;   // 안 바뀌었다
                    }

                    last[i] = json;

                    await WriteAsync(http, "npc", json, linked.Token).ConfigureAwait(false);
                }

                await Task.Delay(PeriodMillis, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 클라이언트가 끊었거나 종료 지시다. 둘 다 정상이다.
        }
    }

    private static async Task WriteAsync(HttpContext http, string kind, string json, CancellationToken ct)
    {
        var line = new StringBuilder(json.Length + 32);

        line.Append(CultureInfo.InvariantCulture, $"event: {kind}\n");
        line.Append(CultureInfo.InvariantCulture, $"data: {json}\n\n");

        await http.Response.WriteAsync(line.ToString(), ct).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
