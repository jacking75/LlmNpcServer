using Npc.Core;
using Npc.Host.Observability;

namespace Npc.Host.Replan;

/// <summary>
/// 벽시계 청구 주기 캡 (C-02).
///
/// <b><see cref="Npc.Planning.ReplanBudget"/> 의 하루는 <c>Tick</c> 기준이다</b> — 실시간 10Hz 로
/// 864,000틱. 결정론을 위해 그렇게 두었고 그대로 유지한다: 리플레이가 같은 강등 결정을 내야 한다.
///
/// <para>
/// 문제는 <c>--max-speed</c>·<c>--time-scale</c> 회차다. 배속이 붙으면 틱 기준 하루가 실시간
/// 몇 분이라 <b>실제 청구일 하루에 캡이 여러 번 리셋된다</b>. 제공사 청구서는 벽시계로 오는데
/// 우리 캡은 그것과 무관하게 돌아간 셈이다.
/// </para>
///
/// <para>
/// 그래서 <b>호스트 쪽에 벽시계 캡을 하나 더 둔다.</b> 예산을 우회하는 것이 아니라 더하는 것이다
/// (CLAUDE.md §2.7). 넘으면 T2 킬스위치를 걸고, 해제는 A-11 의 <c>/admin/killswitch</c> 로 한다 —
/// 자동 해제를 만들면 "왜 다시 돈이 나갔나" 에 답할 수 없다.
/// </para>
///
/// <para>
/// <b>게임 로직 밖이다.</b> 결정은 킬스위치 상태로 나타나고, 리플레이에는 그 상태가 기록으로
/// 재현된다 (A-07 의 리로드 기록과 같은 방식).
/// </para>
/// </summary>
public sealed class BillingGuard
{
    private readonly KillSwitchState _switches;
    private readonly IAlarmSink _alarms;
    private readonly double _capUsd;
    private readonly int _resetHourUtc;
    private readonly Func<DateTimeOffset> _now;
    private readonly Lock _gate = new();

    private DateOnly _day;
    private double _spentToday;
    private double _baseline;

    /// <summary>감시기를 만든다.</summary>
    /// <param name="switches">킬스위치 상태. 넘으면 T2 를 건다.</param>
    /// <param name="alarms">경보 싱크.</param>
    /// <param name="capUsd">벽시계 하루 캡(USD). 0 이면 끈다.</param>
    /// <param name="resetHourUtc">하루가 바뀌는 UTC 시각(0~23). 청구 주기에 맞춘다.</param>
    /// <param name="now">지금 시각. 테스트가 가짜를 넣는다.</param>
    public BillingGuard(
        KillSwitchState switches,
        IAlarmSink alarms,
        double capUsd,
        int resetHourUtc = 0,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(switches);
        ArgumentNullException.ThrowIfNull(alarms);
        ArgumentOutOfRangeException.ThrowIfNegative(capUsd);
        ArgumentOutOfRangeException.ThrowIfNegative(resetHourUtc);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(resetHourUtc, 23);

        _switches = switches;
        _alarms = alarms;
        _capUsd = capUsd;
        _resetHourUtc = resetHourUtc;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _day = BillingDay(_now());
    }

    /// <summary>켜져 있는가. 캡이 0 이면 아무것도 하지 않는다.</summary>
    public bool Enabled => _capUsd > 0;

    /// <summary>벽시계 오늘 지출(USD). 대시보드 비용 패널이 읽는다.</summary>
    public double SpentToday
    {
        get
        {
            lock (_gate)
            {
                return _spentToday;
            }
        }
    }

    /// <summary>캡(USD).</summary>
    public double CapUsd => _capUsd;

    /// <summary>캡을 넘겨 차단한 횟수.</summary>
    public long Blocks { get; private set; }

    /// <summary>
    /// 누계 지출을 보고 판단한다. 주기적으로 부른다 (누계는 프로세스 기동 이후 값이다).
    /// </summary>
    /// <param name="cumulativeUsd"><c>CompileStats</c> 의 누적 비용.</param>
    /// <returns>이번 호출에서 차단했으면 true.</returns>
    public bool Observe(double cumulativeUsd)
    {
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            DateOnly today = BillingDay(_now());

            if (today != _day)
            {
                // 청구일이 바뀌었다. 오늘 지출의 기준점을 지금 누계로 옮긴다.
                //
                // <b>킬스위치는 자동으로 풀지 않는다.</b> 사람이 /admin/killswitch 로 되살린다 —
                // 자동 해제를 만들면 "왜 다시 돈이 나갔나" 에 답할 수 없다.
                _day = today;
                _baseline = cumulativeUsd;
                _spentToday = 0;

                _alarms.Raise(new AlarmPayload(
                    AlarmKind.CostBudget, AlarmSeverity.Info, "billing.rollover",
                    $"청구일이 바뀌었다 ({today:yyyy-MM-dd} UTC{_resetHourUtc:+00;-00}). 벽시계 지출을 0 으로 되돌린다"));

                return false;
            }

            _spentToday = Math.Max(0, cumulativeUsd - _baseline);

            if (_spentToday < _capUsd || _switches.IsDisabled(KillSwitchTarget.T2))
            {
                return false;
            }

            _switches.Fire(KillSwitchTarget.T2);
            Blocks++;

            _alarms.Raise(new AlarmPayload(
                AlarmKind.CostBudget, AlarmSeverity.Critical, "billing.cap",
                $"벽시계 하루 캡 초과 — ${_spentToday:0.####} / ${_capUsd:0.##}. T2 를 끊었다. "
                + "해제는 POST /admin/killswitch?target=T2&state=off 다",
                _spentToday));

            return true;
        }
    }

    /// <summary>이 시각이 속한 청구일. 리셋 시각만큼 당겨서 날짜를 센다.</summary>
    private DateOnly BillingDay(DateTimeOffset now) =>
        DateOnly.FromDateTime(now.UtcDateTime.AddHours(-_resetHourUtc));
}
