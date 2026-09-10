using System.Diagnostics;
using Microsoft.Extensions.Logging.Console;
using Npc.Host.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Npc.Host.Observability;

/// <summary>로그 형식 (A-05).</summary>
public enum LogFormat
{
    /// <summary>사람이 읽는 평문. 개발 기본이다.</summary>
    Text,

    /// <summary>한 줄 JSON. 수집기가 파싱한다. 운영에서 쓴다.</summary>
    Json,
}

/// <summary>
/// 관측성 배선 (A-05).
///
/// <b>계측기 13종이 <c>Meter</c> 에 만들어져 있는데 아무도 수집하지 않았다.</b> 로그는 평문
/// 한국어 한 줄이라 수집기가 파싱할 수 없었고, 경보는 대시보드 색깔로만 존재했다.
///
/// <para>
/// <b>기존 <c>/metrics</c> JSON 은 그대로 둔다.</b> 대시보드가 그것을 쓰고, 브라우저 하나
/// 띄우자고 수집기를 세울 이유가 없다. Prometheus 는 <c>/metrics/prometheus</c> 로 따로 낸다.
/// </para>
///
/// <para>
/// <b>추적은 재계획 워커 경로에만 붙인다.</b> 틱 루프에 <c>Activity</c> 를 넣으면 틱마다 할당이
/// 생긴다 (CLAUDE.md §2.1).
/// </para>
/// </summary>
public static class Telemetry
{
    /// <summary>서비스 이름. 리소스 속성 <c>service.name</c>.</summary>
    public const string ServiceName = "npc-server";

    /// <summary>Prometheus 스크레이프 경로.</summary>
    public const string PrometheusRoute = "/metrics/prometheus";

    /// <summary>재계획 워커 추적원 이름. <b>틱 루프에는 붙이지 않는다.</b></summary>
    public const string ReplanActivitySource = "Npc.Replan";

    /// <summary>재계획 추적원. LLM 호출 하나가 하나의 span 이다.</summary>
    public static ActivitySource Replan { get; } = new(ReplanActivitySource);

    /// <summary>
    /// 틱 지속시간 히스토그램 경계(ms). <b>틱 예산 20ms 에 맞춰 잡는다</b> —
    /// 기본 경계는 초 단위라 0.8ms 짜리 틱이 전부 첫 칸에 몰려 p99 를 볼 수 없다.
    /// </summary>
    public static double[] TickDurationBoundaries { get; } = [0.1, 0.5, 1, 2, 5, 10, 20, 50];

    /// <summary>
    /// 메트릭·추적을 배선한다.
    /// </summary>
    /// <param name="services">서비스 모음.</param>
    /// <param name="instanceId">인스턴스 식별자. 샤드 id 가 있으면 그것 (A-08).</param>
    /// <param name="otlpEndpoint">OTLP 수집기 주소. null 이면 OTLP 를 켜지 않는다.</param>
    /// <param name="prometheus">Prometheus 엔드포인트를 열까.</param>
    public static void AddNpcTelemetry(
        this IServiceCollection services,
        string instanceId,
        Uri? otlpEndpoint,
        bool prometheus)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(instanceId);

        if (!prometheus && otlpEndpoint is null)
        {
            return;   // 아무도 수집하지 않는다. 배선 비용을 내지 않는다.
        }

        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: ServiceName,
                serviceVersion: HostVersion.Assembly,
                serviceInstanceId: instanceId))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(NpcMeter.MeterName)
                    .AddRuntimeInstrumentation()
                    .AddView(
                        "npc.tick.duration",
                        new ExplicitBucketHistogramConfiguration
                        {
                            Boundaries = TickDurationBoundaries,
                        });

                if (prometheus)
                {
                    metrics.AddPrometheusExporter();
                }

                if (otlpEndpoint is { } endpoint)
                {
                    metrics.AddOtlpExporter((exporter, _) => exporter.Endpoint = endpoint);
                }
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(ReplanActivitySource);

                if (otlpEndpoint is { } endpoint)
                {
                    tracing.AddOtlpExporter(exporter => exporter.Endpoint = endpoint);
                }
            });
    }

    /// <summary>
    /// 로깅을 배선한다.
    ///
    /// <b>운영은 JSON 한 줄이다.</b> 평문 한국어는 사람에게는 좋지만 수집기에서 필드로 쪼갤 수 없다.
    /// 개발 기본은 여전히 평문이다 — 실습 중에 JSON 을 읽고 싶은 사람은 없다.
    /// </summary>
    public static void AddNpcLogging(this ILoggingBuilder logging, LogFormat format, LogLevel minimum)
    {
        ArgumentNullException.ThrowIfNull(logging);

        logging.ClearProviders();
        logging.SetMinimumLevel(minimum);

        if (format == LogFormat.Json)
        {
            logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.UseUtcTimestamp = true;
                options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions { Indented = false };
            });

            return;
        }

        logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
            options.ColorBehavior = LoggerColorBehavior.Disabled;
        });
    }
}
