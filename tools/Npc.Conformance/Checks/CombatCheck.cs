using System.Globalization;
using Npc.Contracts;

namespace Npc.Conformance.Checks;

/// <summary>
/// C5 — 상호작용 · 전투 (B-07).
///
/// <para>
/// <b><c>DamageTaken</c> 은 <c>CombatStarted</c> 뒤에만 온다.</b> NPC 서버의 인터럽트는
/// <c>ThreatNearby</c> 를 <c>CombatStarted</c> 로 세우므로, 순서가 뒤집히면 피해는 들어오는데
/// 위협 플래그가 안 서서 <b>반격하지 않는 NPC</b> 가 된다.
/// </para>
///
/// <para>
/// <b><c>CombatEnded</c> 뒤의 <c>DamageTaken</c> 도 위반이다.</b> 전투가 끝났다고 알린 뒤
/// 피해를 보내면 NPC 서버는 그 피해를 "전투 밖의 사고" 로 읽는다.
/// </para>
/// </summary>
public sealed class CombatCheck : IConformanceCheck
{
    /// <inheritdoc/>
    public string Id => "C5.combat";

    /// <inheritdoc/>
    public string Title => "전투 — DamageTaken 은 CombatStarted 뒤에만";

    /// <inheritdoc/>
    public CheckResult Run(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        int damage = observation.Count(GameEventKind.DamageTaken);
        int started = observation.Count(GameEventKind.CombatStarted);
        int ended = observation.Count(GameEventKind.CombatEnded);
        int interacted = observation.Count(GameEventKind.PlayerInteracted);

        if (damage + started + ended == 0)
        {
            return CheckResult.NotChecked(
                Id, Title, "전투 이벤트가 0건이다 — 이 회차에 전투가 없었다");
        }

        var violations = new List<string>();
        var inCombat = new HashSet<int>();

        foreach (Observed observed in observation.Events)
        {
            GameEvent ev = observed.Event;

            switch (ev.Kind)
            {
                case GameEventKind.CombatStarted:
                    inCombat.Add(ev.Npc.Value);
                    break;

                case GameEventKind.CombatEnded:
                    inCombat.Remove(ev.Npc.Value);
                    break;

                case GameEventKind.DamageTaken:
                    if (!inCombat.Contains(ev.Npc.Value))
                    {
                        violations.Add(
                            $"NPC {ev.Npc.Value}: CombatStarted 없이 DamageTaken (틱 {ev.OccurredAt.Value})");
                    }

                    if (ev.Amount <= 0)
                    {
                        violations.Add(
                            $"NPC {ev.Npc.Value}: DamageTaken 의 피해량이 {ev.Amount} 다 (틱 {ev.OccurredAt.Value})");
                    }

                    break;

                default:
                    break;
            }
        }

        string detail = string.Create(
            CultureInfo.InvariantCulture,
            $"CombatStarted {started} · CombatEnded {ended} · DamageTaken {damage} · "
            + $"PlayerInteracted {interacted} · 관찰 끝에 전투 중인 NPC {inCombat.Count}명");

        return violations.Count == 0
            ? CheckResult.Pass(Id, Title, detail)
            : CheckResult.Fail(Id, Title, detail, violations);
    }
}
