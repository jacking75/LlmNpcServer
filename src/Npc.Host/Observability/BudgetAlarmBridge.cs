using Npc.Planning;

namespace Npc.Host.Observability;

/// <summary>
/// 예산 경보를 <see cref="IAlarmSink"/> 로 옮긴다 (A-05 · C-02).
///
/// <b>예전에는 콘솔 한 줄이었고 <c>RateLimited</c> 는 로그조차 없었다.</b> 캡 소진·강등·거절을
/// 대시보드를 새로고침하기 전에는 아무도 몰랐다.
///
/// <para>
/// <b>임계 경보를 여기서 만든다.</b> <see cref="ReplanBudget"/> 은 캡을 <b>넘었을 때</b>만 알린다 —
/// 80%·95% 는 "아직 넘지 않았지만 곧 넘는다" 이고, 그것이 사람이 조치할 수 있는 유일한 시점이다.
/// 각 임계는 하루에 한 번만 운다.
/// </para>
///
/// <b>워커 스레드에서 불린다.</b> 블록하면 재계획이 밀린다 — 싱크는 큐에 넣고 즉시 돌아온다.
/// </summary>
public sealed class BudgetAlarmBridge
{
    /// <summary>경보를 울릴 소진율. 오름차순이어야 한다.</summary>
    public static ReadOnlySpan<double> Thresholds => [0.80, 0.95, 1.00];

    private readonly IAlarmSink _sink;
    private readonly ReplanBudgetLimits _limits;
    private readonly object _gate = new();
    private int _tokenLevel;
    private int _costLevel;
    private long _day = -1;

    /// <summary>다리를 만든다.</summary>
    public BudgetAlarmBridge(IAlarmSink sink, ReplanBudgetLimits limits)
    {
        ArgumentNullException.ThrowIfNull(sink);

        _sink = sink;
        _limits = limits;
    }

    /// <summary>지금까지 울린 임계 경보 수. 테스트가 읽는다.</summary>
    public long ThresholdAlarms { get; private set; }

    /// <summary><see cref="ReplanBudget.Alarm"/> 에 그대로 꽂는다.</summary>
    public void OnBudgetAlarm(ReplanBudgetAlarm alarm)
    {
        (AlarmKind kind, AlarmSeverity severity, string message) = alarm.Kind switch
        {
            ReplanBudgetAlarmKind.TokenCapExceeded => (
                AlarmKind.TokenBudget, AlarmSeverity.Critical,
                $"일일 토큰 캡 초과 — {alarm.TokensToday:N0} / {_limits.DailyTokenCap:N0} tok"),

            ReplanBudgetAlarmKind.CostCapExceeded => (
                AlarmKind.CostBudget, AlarmSeverity.Critical,
                $"일일 비용 캡 초과 — ${alarm.CostToday:0.####} / ${_limits.DailyCostCapUsd:0.##}"),

            ReplanBudgetAlarmKind.TierDowngraded => (
                AlarmKind.TierDowngraded, AlarmSeverity.Warning,
                $"티어 강등 {alarm.Requested} → {alarm.Granted}. 플랜 품질이 떨어진다"),

            ReplanBudgetAlarmKind.Rejected => (
                AlarmKind.ReplanRejected, AlarmSeverity.Warning,
                "재계획 거절 — 예산이 없다. NPC 는 기존 플랜을 계속 쓴다"),

            // 레이트 리밋은 정상 동작의 일부다. 그래도 <b>남긴다</b> —
            // 예전에는 로그조차 없어서 "왜 처리율이 안 오르나" 를 추적할 방법이 없었다.
            _ => (AlarmKind.RateLimited, AlarmSeverity.Info,
                $"레이트 리밋 {alarm.Requested} → {alarm.Granted}"),
        };

        _sink.Raise(new AlarmPayload(kind, severity, alarm.Kind.ToString(), message, alarm.TokensToday));
    }

    /// <summary>
    /// 소진율을 보고 임계 경보를 낸다. 예산을 쓴 뒤에 부른다.
    /// </summary>
    /// <param name="tokensToday">오늘 쓴 토큰.</param>
    /// <param name="costToday">오늘 쓴 비용.</param>
    /// <param name="day">지금 몇 번째 날인가. 바뀌면 임계가 초기화된다.</param>
    public void Observe(long tokensToday, double costToday, long day)
    {
        lock (_gate)
        {
            if (day != _day)
            {
                _day = day;
                _tokenLevel = 0;
                _costLevel = 0;
            }

            if (_limits.DailyTokenCap > 0)
            {
                Check(
                    (double)tokensToday / _limits.DailyTokenCap,
                    ref _tokenLevel,
                    AlarmKind.TokenBudget,
                    $"{tokensToday:N0} / {_limits.DailyTokenCap:N0} tok");
            }

            if (_limits.DailyCostCapUsd > 0)
            {
                Check(
                    costToday / _limits.DailyCostCapUsd,
                    ref _costLevel,
                    AlarmKind.CostBudget,
                    $"${costToday:0.####} / ${_limits.DailyCostCapUsd:0.##}");
            }
        }
    }

    private void Check(double ratio, ref int level, AlarmKind kind, string detail)
    {
        while (level < Thresholds.Length && ratio >= Thresholds[level])
        {
            double threshold = Thresholds[level];

            level++;
            ThresholdAlarms++;

            _sink.Raise(new AlarmPayload(
                kind,
                threshold >= 1.0 ? AlarmSeverity.Critical : AlarmSeverity.Warning,
                $"{kind}:{threshold:0.00}",
                $"일일 예산 {threshold:P0} 소진 — {detail}",
                ratio));
        }
    }
}
