using System.Globalization;

namespace Npc.Host.Commands;

/// <summary>
/// <c>Npc.Host healthcheck --url &lt;url&gt;</c> (A-09).
///
/// <b>컨테이너 <c>HEALTHCHECK</c> 진입점이다.</b> <c>aspnet</c> 런타임 이미지에는 curl 도 wget 도
/// 없다 — 넣으면 이미지가 커지고 공격면이 늘어난다. 이미 있는 .NET 으로 친다.
///
/// 200 이면 종료 코드 0, 그 밖은 1. 본문은 그대로 표준 출력으로 낸다 —
/// <c>docker inspect</c> 의 헬스 로그에 실패 사유가 남는다.
/// </summary>
public static class HealthCheckCommand
{
    /// <summary>서브커맨드 이름.</summary>
    public const string Name = "healthcheck";

    /// <summary>기본 타임아웃(초).</summary>
    public const int DefaultTimeoutSeconds = 3;

    /// <summary>돌린다.</summary>
    /// <param name="args">서브커맨드 뒤의 인자.</param>
    /// <param name="output">결과를 적을 곳.</param>
    /// <returns>200 이면 0, 아니면 1. 인자가 틀렸으면 2.</returns>
    public static int Run(string[] args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);

        string? url = null;
        int timeout = DefaultTimeoutSeconds;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--url" when i + 1 < args.Length:
                    url = args[++i];
                    break;

                case "--timeout-s" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout)
                        || timeout <= 0)
                    {
                        output.WriteLine("--timeout-s 는 양의 정수다.");
                        return 2;
                    }

                    break;

                default:
                    output.WriteLine($"사용법: {Name} --url <url> [--timeout-s N]");
                    return 2;
            }
        }

        if (url is null)
        {
            output.WriteLine($"사용법: {Name} --url <url> [--timeout-s N]");
            return 2;
        }

        return Probe(url, TimeSpan.FromSeconds(timeout), output);
    }

    private static int Probe(string url, TimeSpan timeout, TextWriter output)
    {
        using var client = new HttpClient { Timeout = timeout };

        try
        {
            using HttpResponseMessage response = client.GetAsync(url).GetAwaiter().GetResult();

            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            output.WriteLine($"{(int)response.StatusCode} {body}");

            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            output.WriteLine($"헬스체크 실패: {e.Message}");
            return 1;
        }
    }
}
