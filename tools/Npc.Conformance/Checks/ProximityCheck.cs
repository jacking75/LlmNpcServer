using System.Globalization;
using Npc.Contracts;

namespace Npc.Conformance.Checks;

/// <summary>
/// C4 — 플레이어 근접 (B-07). <b>가장 중요한 규약이다.</b>
///
/// <para>
/// 근접 계수는 재계획 점수에서 가장 큰 항이다. 경계에 선 NPC 가 판정마다
/// <c>Enter</c>/<c>Leave</c> 를 번갈아 내면 <b>재계획 큐가 그것만으로 포화한다</b> —
/// 크래시도 오류 로그도 없고, "NPC 가 멍청해졌다" 로만 나타난다.
/// </para>
///
/// <para>보는 것 넷.</para>
/// <list type="number">
///   <item>NPC 당 동시 1건 — 이미 관측 중인 NPC 에 <c>Enter</c> 를 또 내면 위반</item>
///   <item>히스테리시스 — <c>Enter</c> 는 ≤200m, <c>Leave</c> 는 ≥220m</item>
///   <item>에지 트리거 — 상태가 안 바뀐 NPC 에 이벤트를 내면 위반</item>
///   <item>판정 주기 — 같은 NPC 의 연속 이벤트가 5틱보다 촘촘하면 매 틱 재고 있다는 뜻</item>
/// </list>
/// </summary>
public sealed class ProximityCheck : IConformanceCheck
{
    /// <summary>이 거리(m) 이하면 <c>Enter</c>.</summary>
    public const int EnterRange = 200;

    /// <summary>이 거리(m) 이상이면 <c>Leave</c>. 차이 20m 이 히스테리시스다.</summary>
    public const int LeaveRange = 220;

    /// <summary>판정 주기(틱). 5틱 = 0.5초.</summary>
    public const int PeriodTicks = 5;

    /// <inheritdoc/>
    public string Id => "C4.proximity";

    /// <inheritdoc/>
    public string Title => "근접 — 동시 1건 · 히스테리시스 · 에지 트리거";

    /// <inheritdoc/>
    public CheckResult Run(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        Observed[] events = [.. observation.Of(GameEventKind.PlayerProximity)];

        if (events.Length == 0)
        {
            return CheckResult.NotChecked(
                Id, Title, "PlayerProximity 가 0건이다 — 플레이어가 없었거나 아무도 범위에 안 들어왔다");
        }

        var violations = new List<string>();
        var observing = new Dictionary<int, PlayerId>();   // npc → 관측 중인 플레이어
        var lastTick = new Dictionary<int, long>();

        int enters = 0;
        int leaves = 0;

        foreach (Observed observed in events)
        {
            GameEvent ev = observed.Event;
            int npc = ev.Npc.Value;
            var change = (ProximityChange)ev.Code;
            bool open = observing.ContainsKey(npc);

            if (change == ProximityChange.Enter)
            {
                enters++;

                // 에지 트리거 + 동시 1건. 이미 관측 중인데 Enter 가 또 오면 둘 다 위반이다.
                if (open)
                {
                    Add(violations, $"NPC {npc}: 관측 중인데 Enter 가 또 왔다 (틱 {ev.OccurredAt.Value})");
                }

                if (ev.Amount > EnterRange)
                {
                    Add(violations,
                        $"NPC {npc}: Enter 거리 {ev.Amount}m > {EnterRange}m (틱 {ev.OccurredAt.Value})");
                }

                observing[npc] = ev.Player;
            }
            else
            {
                leaves++;

                if (!open)
                {
                    Add(violations, $"NPC {npc}: 관측 중이 아닌데 Leave 가 왔다 (틱 {ev.OccurredAt.Value})");
                }

                // 거리 0 은 "거리를 안 실었다" 는 뜻이라 히스테리시스를 판정하지 않는다.
                if (ev.Amount != 0 && ev.Amount < LeaveRange)
                {
                    Add(violations,
                        $"NPC {npc}: Leave 거리 {ev.Amount}m < {LeaveRange}m — 히스테리시스가 없다 "
                        + $"(틱 {ev.OccurredAt.Value})");
                }

                observing.Remove(npc);
            }

            if (lastTick.TryGetValue(npc, out long previous))
            {
                long delta = ev.OccurredAt.Value - previous;

                if (delta is > 0 and < PeriodTicks)
                {
                    Add(violations,
                        $"NPC {npc}: 근접 이벤트가 {delta}틱 만에 또 왔다 — 판정 주기는 {PeriodTicks}틱이다");
                }
            }

            lastTick[npc] = ev.OccurredAt.Value;
        }

        string detail = string.Create(
            CultureInfo.InvariantCulture,
            $"PlayerProximity {events.Length}건 (Enter {enters} · Leave {leaves}) · "
            + $"관찰 끝에 열려 있는 NPC {observing.Count}명");

        return violations.Count == 0
            ? CheckResult.Pass(Id, Title, detail)
            : CheckResult.Fail(Id, Title, detail, violations);
    }

    /// <summary>위반을 담되 <b>세는 것은 전부</b> 센다. 상한은 보고서에서만 적용한다.</summary>
    private static void Add(List<string> violations, string message) => violations.Add(message);
}
