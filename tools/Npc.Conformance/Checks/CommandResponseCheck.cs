using System.Globalization;
using Npc.Contracts;

namespace Npc.Conformance.Checks;

/// <summary>
/// C6 — 명령 응답 (B-07).
///
/// <para>
/// <b>명령은 유실된다고 가정한다</b> (CLAUDE.md §2.2). 그래서 응답이 안 와도 NPC 서버는
/// 타임아웃을 합성해 진행한다 — <b>그것이 이 검사가 필요한 이유다.</b> 응답을 안 보내는
/// 게임서버에 붙여도 겉보기에는 잘 돌고, 실제로는 모든 스텝이 <c>timeout_s</c> 를 다 기다린 뒤
/// 실패로 전진한다. NPC 가 굼떠지지만 오류는 하나도 안 난다.
/// </para>
///
/// <para><b>상관 ID 가 일치해야 한다</b> (N5). 응답은 왔는데 상관 ID 가 0 이면 그 스텝은 못 넘어간다.</para>
/// </summary>
public sealed class CommandResponseCheck : IConformanceCheck
{
    /// <summary>응답률이 이보다 낮으면 불합격. 유실은 정상 경로이므로 100 %를 요구하지 않는다.</summary>
    public const double MinResponseRate = 0.90;

    /// <inheritdoc/>
    public string Id => "C6.response";

    /// <inheritdoc/>
    public string Title => "명령 응답 — timeout_s 안에 · 상관 ID 일치";

    /// <inheritdoc/>
    public CheckResult Run(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (observation.Commands.Length == 0)
        {
            return CheckResult.NotChecked(Id, Title, "우리가 낸 명령이 0건이다");
        }

        // 상관 ID → 응답이 온 틱.
        var answered = new Dictionary<uint, long>();
        int unknown = 0;

        var issued = new Dictionary<uint, Issued>();

        foreach (Issued command in observation.Commands)
        {
            issued[command.Correlation.Value] = command;
        }

        foreach (Observed observed in observation.Events)
        {
            GameEvent ev = observed.Event;

            if (ev.Kind is not (GameEventKind.NpcArrived
                or GameEventKind.NpcActionCompleted
                or GameEventKind.NpcActionFailed))
            {
                continue;
            }

            uint correlation = ev.Correlation.Value;

            if (correlation == 0)
            {
                unknown++;
                continue;
            }

            if (!issued.ContainsKey(correlation))
            {
                unknown++;
                continue;
            }

            answered.TryAdd(correlation, ev.OccurredAt.Value);
        }

        var violations = new List<string>();
        int late = 0;

        foreach (Issued command in observation.Commands)
        {
            if (!answered.TryGetValue(command.Correlation.Value, out long tick))
            {
                continue;   // 유실이다. 아래 응답률에서 센다
            }

            long deadline = command.IssuedTick + command.TimeoutTicks;

            if (tick > deadline)
            {
                late++;

                if (violations.Count < CheckResult.MaxViolations)
                {
                    violations.Add(
                        $"NPC {command.Npc.Value} {command.Kind}: 응답이 틱 {tick} 에 왔다 "
                        + $"(마감 {deadline})");
                }
            }
        }

        double rate = (double)answered.Count / observation.Commands.Length;

        if (rate < MinResponseRate)
        {
            violations.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"응답률 {rate:P1} < {MinResponseRate:P0}. 명령을 받고도 이벤트를 안 내면 NPC 서버는 매 스텝 timeout_s 를 다 기다린다"));
        }

        if (unknown > 0)
        {
            violations.Add(
                $"모르는 상관 ID 로 온 응답 {unknown}건 — 명령에 실린 값을 그대로 되돌려야 한다 (N5)");
        }

        string detail = string.Create(
            CultureInfo.InvariantCulture,
            $"명령 {observation.Commands.Length}건 · 응답 {answered.Count}건 ({rate:P1}) · "
            + $"마감 초과 {late} · 모르는 상관 ID {unknown}");

        return violations.Count == 0
            ? CheckResult.Pass(Id, Title, detail)
            : CheckResult.Fail(Id, Title, detail, violations);
    }
}
