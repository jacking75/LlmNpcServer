using System.Globalization;

namespace Npc.Conformance.Checks;

/// <summary>
/// C3 — 이벤트 시퀀스 (N6 · B-07).
///
/// <para>
/// <b>재접속해도 리셋하지 않는다.</b> 게임서버 프로세스가 사는 동안 순증해야 한다 —
/// 되감으면 <c>EventApplier</c> 가 이후 이벤트를 전부 중복으로 버리고
/// <b>NPC 가 영원히 멈춘다.</b> 이것이 이 검사가 있는 유일한 이유다.
/// </para>
/// </summary>
public sealed class SequenceCheck : IConformanceCheck
{
    /// <inheritdoc/>
    public string Id => "C3.sequence";

    /// <inheritdoc/>
    public string Title => "시퀀스 — 단조 · 갭 0 · 리셋 없음";

    /// <inheritdoc/>
    public CheckResult Run(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.Events.Length < 2)
        {
            return CheckResult.NotChecked(
                Id, Title, $"이벤트가 {observation.Events.Length}건뿐이라 판정할 수 없다");
        }

        var violations = new List<string>();
        long previous = observation.Events[0].Event.Sequence;
        long gaps = 0;
        long resets = 0;

        for (int i = 1; i < observation.Events.Length; i++)
        {
            long now = observation.Events[i].Event.Sequence;

            if (now <= previous)
            {
                resets++;

                if (violations.Count < CheckResult.MaxViolations)
                {
                    violations.Add(
                        $"{i}번째 이벤트에서 시퀀스가 되감겼다: {previous} → {now} "
                        + $"({observation.Events[i].Event.Kind})");
                }
            }
            else if (now != previous + 1)
            {
                gaps += now - previous - 1;
            }

            previous = now;
        }

        if (gaps > 0)
        {
            violations.Add($"시퀀스 갭 {gaps}건. 유실이면 NPC 가 그만큼 뒤처진다");
        }

        if (observation.EventGaps > 0)
        {
            violations.Add($"링크가 센 갭 {observation.EventGaps}건");
        }

        string detail = string.Create(
            CultureInfo.InvariantCulture,
            $"이벤트 {observation.Events.Length}건 · "
            + $"시퀀스 {observation.Events[0].Event.Sequence}~{previous} · "
            + $"갭 {gaps} · 되감김 {resets}");

        return violations.Count == 0
            ? CheckResult.Pass(Id, Title, detail)
            : CheckResult.Fail(Id, Title, detail, violations);
    }
}
