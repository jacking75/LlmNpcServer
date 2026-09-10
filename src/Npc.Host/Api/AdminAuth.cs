using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Npc.Host.Api;

/// <summary>
/// 관리·질의 API 인증 (A-06).
///
/// <b>관리 API 에 인증이 없어 <c>0.0.0.0</c> 바인드가 불가능했다.</b> 그래서 대시보드는
/// <c>localhost</c> 에 묶여 있었고, 컨테이너에서는 아예 도달할 수 없었다.
///
/// <para>
/// <c>Authorization: Bearer &lt;NPC_ADMIN_TOKEN&gt;</c> 하나다. 토큰 비교는
/// <see cref="CryptographicOperations.FixedTimeEquals"/> — 앞에서부터 비교하면
/// 타이밍으로 토큰을 한 글자씩 알아낼 수 있다.
/// </para>
///
/// <para>
/// <b><c>/healthz/*</c> 와 <c>/metrics/prometheus</c> 는 무인증이다.</b> 오케스트레이터와
/// 수집기가 토큰을 들고 다니게 하면 그 토큰이 사방에 퍼진다 — 그 둘은 상태를 바꾸지 않는다.
/// </para>
/// </summary>
public sealed class AdminAuth
{
    /// <summary>토큰 환경변수 이름.</summary>
    public const string TokenEnvironmentVariable = "NPC_ADMIN_TOKEN";

    /// <summary>분당 허용 실패 수. 넘으면 429.</summary>
    public const int FailuresPerMinute = 5;

    private readonly byte[] _token;
    private readonly ConcurrentDictionary<string, Window> _failures = new(StringComparer.Ordinal);
    private readonly Func<long> _now;

    /// <summary>인증기를 만든다.</summary>
    /// <param name="token">기대하는 토큰. null·빈 문자열이면 인증을 요구하지 않는다.</param>
    /// <param name="now">지금 시각(ms). 테스트가 가짜를 넣는다.</param>
    public AdminAuth(string? token, Func<long>? now = null)
    {
        _token = string.IsNullOrEmpty(token) ? [] : Encoding.UTF8.GetBytes(token);
        _now = now ?? (() => Environment.TickCount64);
    }

    /// <summary>토큰이 설정됐는가. 아니면 <c>/admin/*</c> 라우트를 아예 등록하지 않는다.</summary>
    public bool Enabled => _token.Length > 0;

    /// <summary>거절한 요청 수. 계측이 읽는다.</summary>
    public long Rejected { get; private set; }

    /// <summary>
    /// <c>Authorization</c> 헤더를 본다.
    /// </summary>
    /// <param name="header">헤더 값. 없으면 null.</param>
    /// <param name="client">호출자 식별자(원격 IP). 레이트 리밋 열쇠다.</param>
    /// <returns>200 통과면 <c>null</c>, 아니면 응답할 상태 코드.</returns>
    public int? Check(string? header, string client)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!Enabled)
        {
            // 토큰이 없으면 이 미들웨어는 아무것도 막지 않는다.
            // 대신 --bind 0.0.0.0 이 기동 단계에서 거절된다 (A-04).
            return null;
        }

        if (IsThrottled(client))
        {
            Rejected++;
            return StatusCodes.Status429TooManyRequests;
        }

        if (!TryReadBearer(header, out byte[] presented))
        {
            RecordFailure(client);
            Rejected++;
            return StatusCodes.Status401Unauthorized;
        }

        if (!CryptographicOperations.FixedTimeEquals(_token, presented))
        {
            RecordFailure(client);
            Rejected++;
            return StatusCodes.Status401Unauthorized;
        }

        return null;
    }

    /// <summary>환경변수에서 토큰을 읽는다.</summary>
    public static string? TokenFromEnvironment() =>
        Environment.GetEnvironmentVariable(TokenEnvironmentVariable);

    /// <summary>
    /// 이 경로가 토큰을 요구하는가 (A-06).
    ///
    /// <b>허용 목록이 아니라 보호 목록이다.</b> 새 라우트가 늘었을 때 기본이 "무인증" 이면
    /// 그것을 알아채는 계기가 없다 — 그래서 프로브·수집기만 빼고 나머지를 전부 막는다.
    /// </summary>
    public static bool IsProtected(PathString path)
    {
        string value = path.Value ?? "/";

        // 오케스트레이터와 수집기는 상태를 바꾸지 않는다. 토큰을 들고 다니게 하면
        // 그 토큰이 사방에 퍼진다.
        if (value.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/metrics/prometheus", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return value.StartsWith("/control", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/npc", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/status", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/heatmap", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/buckets", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/stream", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadBearer(string? header, out byte[] token)
    {
        token = [];

        if (header is null)
        {
            return false;
        }

        const string Prefix = "Bearer ";

        if (!header.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string value = header[Prefix.Length..].Trim();

        if (value.Length == 0)
        {
            return false;
        }

        token = Encoding.UTF8.GetBytes(value);
        return true;
    }

    private bool IsThrottled(string client)
    {
        if (!_failures.TryGetValue(client, out Window window))
        {
            return false;
        }

        return _now() - window.StartMs < 60_000 && window.Count >= FailuresPerMinute;
    }

    private void RecordFailure(string client)
    {
        long now = _now();

        _failures.AddOrUpdate(
            client,
            _ => new Window(now, 1),
            (_, existing) => now - existing.StartMs >= 60_000
                ? new Window(now, 1)
                : new Window(existing.StartMs, existing.Count + 1));
    }

    private readonly record struct Window(long StartMs, int Count);
}
