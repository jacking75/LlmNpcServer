using System.Collections.Immutable;
using System.Text;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Narrative;

/// <summary>
/// 인터럽트 설명 (F-03). 규칙을 <b>"언제 · 누가 · 무엇을 · 그 다음"</b> 문장으로 바꾼다.
///
/// <b>우선순위 충돌을 같이 낸다.</b> 같은 priority 두 규칙은 id 오름차순으로 갈리는데,
/// 그 순서는 마스터데이터를 쓰는 사람이 의도한 것이 아니라 우연히 그렇게 된 것일 수 있다 —
/// 우연에 기대는 판정은 나중에 이름 하나 바뀌면 조용히 달라진다.
/// </summary>
public static class InterruptExplain
{
    /// <summary>규칙 전체를 한 장으로.</summary>
    public static string Render(MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var sb = new StringBuilder(4 * 1024);

        sb.Append("# 인터럽트 규칙 ").Append(Md.N(data.Interrupts.Count)).AppendLine("개");
        sb.AppendLine();
        sb.AppendLine(
            "**LLM 이 만들지 않는다.** 인터럽트는 반응 속도가 생명이라 결정론 규칙으로만 돈다. "
            + "매칭은 우선순위 내림차순, 같으면 id 오름차순이다.");
        sb.AppendLine();

        Md.TableHead(sb, "우선순위", "규칙", "언제", "무엇을", "재계획 긴급도");

        foreach (InterruptRule rule in Ordered(data))
        {
            Md.Row(
                sb,
                Md.N(rule.Priority),
                Md.Code(rule.Id),
                Md.Cell(When(rule)),
                Md.Cell(Then(data, rule)),
                Md.N(rule.Urgency));
        }

        sb.AppendLine();
        Conflicts(sb, data);

        return sb.ToString();
    }

    /// <summary>매칭 순서. <see cref="InterruptRules.TryMatch"/> 와 같은 정렬이다.</summary>
    public static ImmutableArray<InterruptRule> Ordered(MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return
        [
            .. data.Interrupts.Rules
                .OrderByDescending(r => r.Priority)
                .ThenBy(r => r.Id, StringComparer.Ordinal),
        ];
    }

    /// <summary>발동 조건을 한 문장으로.</summary>
    public static string When(InterruptRule rule)
    {
        var parts = new List<string>(6);

        if (rule.Event is { } kind)
        {
            parts.Add($"`{kind}` 이벤트");
        }

        if (rule.AnyFlag != WorldFlags.None)
        {
            parts.Add($"`{WorldFlagTable.Format(rule.AnyFlag)}` 중 하나");
        }

        if (rule.AllFlag != WorldFlags.None)
        {
            parts.Add($"`{WorldFlagTable.Format(rule.AllFlag)}` 전부");
        }

        if (rule.NoneFlag != WorldFlags.None)
        {
            parts.Add($"`{WorldFlagTable.Format(rule.NoneFlag)}` 없음");
        }

        foreach (TraitCondition trait in rule.Traits)
        {
            parts.Add($"{Lexicon.Trait(trait.Kind)} {Symbol(trait.Comparison)} {Md.N(trait.Value)}");
        }

        if (rule.CombatCapable is { } combat)
        {
            parts.Add(combat ? "전투 가능" : "전투 불가");
        }

        return parts.Count == 0 ? "조건 없음 — 항상 걸린다" : string.Join(" · ", parts);
    }

    /// <summary>발동 결과를 한 문장으로.</summary>
    public static string Then(MasterDataSet data, InterruptRule rule)
    {
        ArgumentNullException.ThrowIfNull(data);

        var parts = new List<string>(4) { "`" + data.ActionName(rule.Action) + "`" };

        if (rule.Poi != PoiSymbol.None)
        {
            parts.Add(Lexicon.To(Lexicon.Of(rule.Poi)));
        }

        if (rule.TargetsThreat)
        {
            parts.Add("위협 대상에게");
        }

        if (rule.Amount != 0)
        {
            parts.Add("×" + Md.N(rule.Amount));
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// 같은 우선순위끼리 묶어 보여 준다. <b>충돌이 없으면 없다고 적는다</b> —
    /// 빈 절을 지우면 "확인했는데 없었다" 와 "확인하지 않았다" 를 구별할 수 없다.
    /// </summary>
    private static void Conflicts(StringBuilder sb, MasterDataSet data)
    {
        sb.AppendLine("## 우선순위 충돌");
        sb.AppendLine();

        ImmutableArray<IGrouping<int, InterruptRule>> groups =
        [
            .. Ordered(data).GroupBy(r => r.Priority).Where(g => g.Count() > 1),
        ];

        if (groups.IsEmpty)
        {
            sb.AppendLine("없다. 모든 규칙의 우선순위가 서로 다르다.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("같은 우선순위는 **id 오름차순**으로 갈린다. 의도한 순서인지 확인한다.");
        sb.AppendLine();

        foreach (IGrouping<int, InterruptRule> group in groups)
        {
            sb.Append("- 우선순위 ").Append(Md.N(group.Key)).Append(": ")
              .AppendLine(Md.Codes(group.Select(r => r.Id)));
        }

        sb.AppendLine();
    }

    private static string Symbol(TraitComparison comparison) => comparison switch
    {
        TraitComparison.Less => "<",
        TraitComparison.LessOrEqual => "≤",
        TraitComparison.Greater => ">",
        TraitComparison.GreaterOrEqual => "≥",
        TraitComparison.Equal => "=",
        _ => comparison.ToString(),
    };
}
