using Npc.Contracts;
using Npc.Runtime;

namespace Npc.Host.Api;

/// <summary>프로브 판정. 200 / 200(경고) / 503 세 값이다.</summary>
public enum HealthStatus
{
    /// <summary>정상. 200.</summary>
    Ok,

    /// <summary>돌고는 있으나 완전하지 않다. 200 이되 본문이 사유를 적는다.</summary>
    Degraded,

    /// <summary>못 쓴다. 503.</summary>
    Fail,
}

/// <summary>검사 한 줄. 이름·판정·근거.</summary>
/// <param name="Name">검사 이름.</param>
/// <param name="Status">판정.</param>
/// <param name="Detail">사람이 읽는 근거.</param>
public readonly record struct HealthCheck(string Name, string Status, string Detail);

/// <summary>프로브 응답 한 장.</summary>
/// <param name="Status">전체 판정.</param>
/// <param name="Checks">개별 검사.</param>
public sealed record HealthReport(string Status, IReadOnlyList<HealthCheck> Checks);

/// <summary>
/// 틱 루프 생존 계측 (A-03).
///
/// <b>벽시계를 여기서만 본다.</b> 루프는 <see cref="Beat"/> 를 부를 뿐이고 그것을 초로
/// 환산하는 것은 호스트다 — 게임 로직에 <c>DateTime</c>·<c>Stopwatch</c> 가 들어가지 않는다
/// (CLAUDE.md §2.3). <c>Environment.TickCount64</c> 는 단조 증가라 시각 변경에 흔들리지 않는다.
/// </summary>
public sealed class HealthProbe : ILoopProbe
{
    private long _lastBeatMs = Environment.TickCount64;
    private long _lastSyncTick;
    private long _lastSyncObservedMs = Environment.TickCount64;

    /// <summary>마지막 하트비트로부터 지난 밀리초.</summary>
    public long SinceBeatMs => Environment.TickCount64 - Volatile.Read(ref _lastBeatMs);

    /// <summary>마지막 <c>TickSync</c> 진행으로부터 지난 밀리초.</summary>
    public long SinceTickAdvanceMs => Environment.TickCount64 - Volatile.Read(ref _lastSyncObservedMs);

    /// <summary>마지막으로 관측한 틱.</summary>
    public long LastTick => Volatile.Read(ref _lastSyncTick);

    /// <inheritdoc />
    public void Beat() => Volatile.Write(ref _lastBeatMs, Environment.TickCount64);

    /// <summary>
    /// 틱이 진행했는지 본다. 프로브 요청 스레드에서 부른다 — 틱 루프가 아니다.
    /// 진행이 있었으면 관측 시각을 갱신한다.
    /// </summary>
    public void Observe(long currentTick)
    {
        if (currentTick > Volatile.Read(ref _lastSyncTick))
        {
            Volatile.Write(ref _lastSyncTick, currentTick);
            Volatile.Write(ref _lastSyncObservedMs, Environment.TickCount64);
        }
    }
}

/// <summary>
/// 헬스체크 세 종 (A-03 · PRODUCTION_ROADMAP §4).
///
/// <b>오케스트레이터가 재시작시킬 근거</b>가 여기 있다. <c>/status</c> 는 링크가 죽어도 200 이라
/// liveness 로 쓸 수 없다.
///
/// <list type="table">
///   <item><term>live</term><description>프로세스가 응답하고 틱 루프 스레드가 살아 있다</description></item>
///   <item><term>ready</term><description>로드 완료 + 링크 Connected + 틱이 진행 중</description></item>
///   <item><term>startup</term><description>로드·복원 판정이 끝났다</description></item>
/// </list>
/// </summary>
public static class HealthEndpoints
{
    /// <summary>liveness 경로.</summary>
    public const string LiveRoute = "/healthz/live";

    /// <summary>readiness 경로.</summary>
    public const string ReadyRoute = "/healthz/ready";

    /// <summary>startup 경로.</summary>
    public const string StartupRoute = "/healthz/startup";

    /// <summary>
    /// liveness. 틱 루프가 <paramref name="stallMs"/> 안에 한 번이라도 깨어났는가.
    ///
    /// <b>이벤트를 기다리는 중도 살아 있음으로 센다</b> — 링크가 조용한 것과 루프가 죽은 것은
    /// 다른 사건이고, 전자는 readiness 가 잡는다.
    /// </summary>
    public static HealthReport Live(HealthProbe probe, long stallMs)
    {
        ArgumentNullException.ThrowIfNull(probe);

        long since = probe.SinceBeatMs;
        bool ok = since <= stallMs;

        return new HealthReport(
            ok ? "ok" : "fail",
            [new HealthCheck("loop", ok ? "ok" : "fail", $"마지막 하트비트 {since}ms 전 (상한 {stallMs}ms)")]);
    }

    /// <summary>readiness. 로드 완료 · 링크 Connected · 틱 진행.</summary>
    /// <param name="probe">루프 프로브.</param>
    /// <param name="loaded">마스터데이터·플랜 스토어 로드가 끝났는가.</param>
    /// <param name="state">링크 상태.</param>
    /// <param name="linkRequired">링크가 필요한 회차인가. 루프백·널은 false 다.</param>
    /// <param name="tickStallMs">틱이 멈춰도 되는 상한(ms).</param>
    /// <param name="rejectReason">핸드셰이크 거절 사유. 없으면 null.</param>
    public static HealthReport Ready(
        HealthProbe probe,
        bool loaded,
        LinkState state,
        bool linkRequired,
        long tickStallMs,
        string? rejectReason)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var checks = new List<HealthCheck>(3)
        {
            new("planstore", loaded ? "ok" : "fail", loaded ? "loaded" : "로드 중"),
        };

        bool linkOk = !linkRequired || state == LinkState.Connected;

        checks.Add(new HealthCheck(
            "link",
            linkOk ? "ok" : "fail",
            rejectReason is null ? state.ToString() : $"{state} — {rejectReason}"));

        long sinceTick = probe.SinceTickAdvanceMs;
        bool tickOk = sinceTick <= tickStallMs;

        checks.Add(new HealthCheck(
            "tickSync",
            tickOk ? "ok" : "fail",
            $"tick {probe.LastTick} · 마지막 진행 {sinceTick}ms 전 (상한 {tickStallMs}ms)"));

        bool all = loaded && linkOk && tickOk;

        return new HealthReport(all ? "ok" : "fail", checks);
    }

    /// <summary>startup. 로드와 복원 판정이 끝났는가.</summary>
    /// <param name="loaded">로드 완료.</param>
    /// <param name="restoreDecided">스냅샷 복원 판정 완료 (A-01).</param>
    /// <param name="restoreDetail">복원 결과 설명.</param>
    public static HealthReport Startup(bool loaded, bool restoreDecided, string restoreDetail)
    {
        bool ok = loaded && restoreDecided;

        return new HealthReport(
            ok ? "ok" : "fail",
            [
                new HealthCheck("planstore", loaded ? "ok" : "fail", loaded ? "loaded" : "로드 중"),
                new HealthCheck("restore", restoreDecided ? "ok" : "fail", restoreDetail),
            ]);
    }

    /// <summary>판정을 HTTP 상태 코드로. <c>fail</c> 만 503 이다.</summary>
    public static int StatusCode(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return report.Status == "fail" ? 503 : 200;
    }
}
