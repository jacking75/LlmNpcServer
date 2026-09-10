using System.Globalization;
using Npc.Contracts;

namespace Npc.Conformance.Checks;

/// <summary>
/// C2 — <c>TickSync</c> (B-07).
///
/// <para>
/// <b>NPC 서버의 시계는 이것 하나로만 움직인다.</b> 틱이 멈추면 NPC 는 마지막 스텝에서
/// 굳고, 되감기면 타임아웃 합성이 어긋난다. <b>크래시가 아니라 정지</b>라 감시하지 않으면
/// 모른다.
/// </para>
///
/// <para>
/// <b>속도(10Hz)는 벽시계가 있어야 판정한다.</b> 테스트가 틱을 직접 미는 회차에서는
/// 실제 게임서버의 페이싱을 볼 수 없으므로 <b>미판정</b>이다 — 통과로 세면 거짓이 된다.
/// </para>
/// </summary>
public sealed class TickSyncCheck : IConformanceCheck
{
    /// <summary>규약 틱 레이트(Hz).</summary>
    public const double ExpectedHz = 10.0;

    /// <summary>허용 오차. ±10 %.</summary>
    public const double Tolerance = 0.10;

    /// <summary>속도를 판정하려면 이만큼은 관찰해야 한다(ms).</summary>
    public const long MinWallClockMillis = 2_000;

    /// <inheritdoc/>
    public string Id => "C2.ticksync";

    /// <inheritdoc/>
    public string Title => "TickSync — 단조 · 갭 0 · 10Hz";

    /// <inheritdoc/>
    public CheckResult Run(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        Observed[] ticks = [.. observation.Of(GameEventKind.TickSync)];

        if (ticks.Length < 2)
        {
            return CheckResult.NotChecked(
                Id, Title, $"TickSync 가 {ticks.Length}건뿐이라 판정할 수 없다");
        }

        var violations = new List<string>();
        long previous = ticks[0].Event.OccurredAt.Value;
        long gaps = 0;

        for (int i = 1; i < ticks.Length; i++)
        {
            long now = ticks[i].Event.OccurredAt.Value;

            if (now <= previous)
            {
                violations.Add($"틱이 되감겼다: {previous} → {now}");
            }
            else if (now != previous + 1)
            {
                gaps += now - previous - 1;
            }

            previous = now;
        }

        if (gaps > 0)
        {
            violations.Add($"틱 갭 {gaps}건. TickSync 는 매 틱 나가야 한다");
        }

        long span = ticks[^1].Event.OccurredAt.Value - ticks[0].Event.OccurredAt.Value;
        string rate;

        if (observation.WallClockMillis < MinWallClockMillis)
        {
            rate = observation.WallClockMillis == 0
                ? "속도 미판정 (구동 회차 — 벽시계가 없다)"
                : $"속도 미판정 (관찰 {observation.WallClockMillis}ms < {MinWallClockMillis}ms)";
        }
        else
        {
            double hz = span * 1000.0 / observation.WallClockMillis;
            double drift = Math.Abs(hz - ExpectedHz) / ExpectedHz;

            rate = string.Create(CultureInfo.InvariantCulture, $"{hz:F2} Hz");

            if (drift > Tolerance)
            {
                violations.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"틱 레이트가 {hz:F2} Hz 다. 규약 {ExpectedHz} Hz ±{Tolerance:P0}"));
            }
        }

        string detail = string.Create(
            CultureInfo.InvariantCulture,
            $"TickSync {ticks.Length}건 · 틱 {ticks[0].Event.OccurredAt.Value}~{ticks[^1].Event.OccurredAt.Value} · "
            + $"갭 {gaps} · {rate}");

        return violations.Count == 0
            ? CheckResult.Pass(Id, Title, detail)
            : CheckResult.Fail(Id, Title, detail, violations);
    }
}
