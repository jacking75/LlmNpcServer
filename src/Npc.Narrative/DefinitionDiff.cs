using System.Collections.Immutable;
using System.Globalization;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Narrative;

/// <summary>
/// "내가 뭘 바꾼 거지" 를 사람 말로 (T34).
///
/// <para>
/// <b>파일 파급(<c>ImpactAnalyzer</c>)은 이 물음의 답이 아니다.</b> "archetypes.json 이 바뀐다"
/// 는 무엇이 어떻게 달라지는지 말해 주지 않는다 — 초보자가 저장을 누르려면
/// "용기 55 → 30, 그래서 위협에 물러나기 대신 도망" 이 필요하다.
/// </para>
///
/// <para><b>결정론이다.</b> 같은 두 정의면 같은 요약이 나온다.</para>
/// </summary>
public static class DefinitionDiff
{
    /// <summary>두 정의의 차이를 문장으로. 같으면 빈 배열.</summary>
    /// <param name="data">마스터데이터. 반응 비교와 정원 판정에 쓴다.</param>
    /// <param name="before">바꾸기 전.</param>
    /// <param name="after">바꾼 뒤.</param>
    public static ImmutableArray<string> Describe(MasterDataSet data, ArchetypeDef before, ArchetypeDef after)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var lines = ImmutableArray.CreateBuilder<string>(8);

        if (!string.Equals(before.Description, after.Description, StringComparison.Ordinal))
        {
            lines.Add($"설명이 바뀐다 — **LLM 프롬프트에 그대로 실린다** ({before.Description.Length}자 → {after.Description.Length}자)");
        }

        Actions(data, before, after, lines);
        Traits(data, before, after, lines);

        if (!before.DutyHours.SequenceEqual(after.DutyHours))
        {
            lines.Add(
                $"근무 시간대 {Times(before.DutyHours)} → **{Times(after.DutyHours)}**"
                + (after.DutyHours.IsEmpty && after.AllowedActions.Any(a => IsDutyAction(data, a))
                    ? " — 경계 근무·순찰을 허용하는데 근무 시간대가 없다 ✗ (V12)"
                    : string.Empty));
        }

        if (!string.Equals(before.WorkplacePoiType, after.WorkplacePoiType, StringComparison.Ordinal))
        {
            lines.Add(Workplace(data, before, after));
        }

        if (!string.Equals(before.HomePoiType, after.HomePoiType, StringComparison.Ordinal))
        {
            lines.Add($"사는 곳 {Lexicon.Place(before.HomePoiType)} → **{Lexicon.Place(after.HomePoiType)}**");
        }

        if (Math.Abs(before.PopulationWeight - after.PopulationWeight) > 1e-9)
        {
            int was = (int)Math.Round(before.PopulationWeight * ArchetypeCard.PopulationBase);
            int now = (int)Math.Round(after.PopulationWeight * ArchetypeCard.PopulationBase);
            double sum = data.Archetypes.Archetypes
                .Where(a => !string.Equals(a.Id, after.Id, StringComparison.Ordinal))
                .Sum(a => a.PopulationWeight) + after.PopulationWeight;

            lines.Add(
                $"인구 {was}명 → **{now}명** (합 {sum.ToString("0.####", CultureInfo.InvariantCulture)}"
                + (Math.Abs(sum - 1.0) <= 0.001 ? " ✓)" : " ✗ 1.0 이어야 한다)"));
        }

        if (before.CombatCapable != after.CombatCapable)
        {
            lines.Add(after.CombatCapable
                ? "**싸울 수 있게** 된다 — 위협에 맞서는 규칙이 후보에 든다"
                : "**싸울 수 없게** 된다 — 위협에 맞서는 규칙이 후보에서 빠진다");
        }

        if (!before.PrimaryRecipes.SequenceEqual(after.PrimaryRecipes, StringComparer.Ordinal))
        {
            lines.Add($"주력 제작품 {Items(before.PrimaryRecipes)} → **{Items(after.PrimaryRecipes)}**");
        }

        if (!before.DefaultGoals.SequenceEqual(after.DefaultGoals, StringComparer.Ordinal))
        {
            lines.Add($"기본 목표 {Items(before.DefaultGoals)} → **{Items(after.DefaultGoals)}**");
        }

        if (!before.InitialInventory.SequenceEqual(after.InitialInventory))
        {
            lines.Add($"시작 소지품이 바뀐다 — 첫 스텝의 전제가 달라질 수 있다");
        }

        return lines.ToImmutable();
    }

    /// <summary>
    /// 하루 일과 두 벌의 차이 (T34). 스텝 하나가 바뀌면 <b>그 뒤 스텝의 전제가 깨지는지</b>도 본다.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="archetype">누구의 하루인가.</param>
    /// <param name="before">바꾸기 전 플랜.</param>
    /// <param name="after">바꾼 뒤 플랜.</param>
    public static ImmutableArray<string> Describe(
        MasterDataSet data, ArchetypeDef archetype, CompiledPlan before, CompiledPlan after)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(archetype);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var lines = ImmutableArray.CreateBuilder<string>(8);

        if (!string.Equals(before.Goal, after.Goal, StringComparison.Ordinal))
        {
            lines.Add($"하루 목표 `{before.Goal}` → **`{after.Goal}`**");
        }

        if (before.Steps.Length != after.Steps.Length)
        {
            lines.Add($"스텝 {before.Steps.Length}개 → **{after.Steps.Length}개**");
        }

        for (int i = 0; i < Math.Min(before.Steps.Length, after.Steps.Length); i++)
        {
            if (before.Steps[i].Action != after.Steps[i].Action)
            {
                lines.Add(
                    $"스텝 {i + 1} 이 {Lexicon.Action(data.ActionName(before.Steps[i].Action))} → "
                    + $"**{Lexicon.Action(data.ActionName(after.Steps[i].Action))}** 로 바뀐다");
            }
            else if (before.Steps[i].Poi != after.Steps[i].Poi)
            {
                lines.Add(
                    $"스텝 {i + 1} 의 대상이 {Lexicon.Of(before.Steps[i].Poi)} → "
                    + $"**{Lexicon.Of(after.Steps[i].Poi)}** 로 바뀐다");
            }
            else if (before.Steps[i].TimeoutSeconds != after.Steps[i].TimeoutSeconds)
            {
                lines.Add(
                    $"스텝 {i + 1} 의 최대 시간이 {before.Steps[i].TimeoutSeconds / 60}분 → "
                    + $"**{after.Steps[i].TimeoutSeconds / 60}분**");
            }
        }

        ImmutableArray<StepTrace> trace = PlanExplain.Trace(data, after, after.Bucket, archetype.Code);

        foreach (StepTrace step in trace)
        {
            if (!step.Ok)
            {
                lines.Add($"✗ 스텝 {step.Index + 1} 의 전제가 깨진다 — {step.Reason} (`{step.Code}`)");
                break;
            }
        }

        LoopVerdict loop = PlanExplain.LoopOf(after, trace.IsEmpty ? data.InitialFlags(after.Bucket) : trace[^1].After);

        if (!loop.Closed)
        {
            lines.Add("✗ " + loop.Message);
        }

        return lines.ToImmutable();
    }

    /// <summary>
    /// 허용 행동의 증감. <b>"성향이 바뀌면 반응이 바뀐다" 와 같은 이유로</b> 여기도 결과를 같이 적는다 —
    /// 인터럽트의 <c>then.action</c> 이 빠지면 그 규칙이 조용히 후보에서 사라진다.
    /// </summary>
    private static void Actions(
        MasterDataSet data, ArchetypeDef before, ArchetypeDef after, ImmutableArray<string>.Builder lines)
    {
        ImmutableArray<string> added =
        [
            .. after.AllowedActions.Except(before.AllowedActions).Select(a => Lexicon.Action(data.ActionName(a))).Order(StringComparer.Ordinal),
        ];

        ImmutableArray<string> removed =
        [
            .. before.AllowedActions.Except(after.AllowedActions).Select(a => Lexicon.Action(data.ActionName(a))).Order(StringComparer.Ordinal),
        ];

        if (added.IsEmpty && removed.IsEmpty)
        {
            return;
        }

        string text = "허용 행동";

        if (!added.IsEmpty) text += " **+" + string.Join(" +", added) + "**";
        if (!removed.IsEmpty) text += " **−" + string.Join(" −", removed) + "**";

        lines.Add(text);
    }

    /// <summary>
    /// 성향 변화와 <b>그 결과</b>. 용기 55 → 30 이 "물러나기 → 도망" 으로 읽혀야
    /// 값과 행동의 인과를 배운다.
    /// </summary>
    private static void Traits(
        MasterDataSet data, ArchetypeDef before, ArchetypeDef after, ImmutableArray<string>.Builder lines)
    {
        foreach (TraitKind kind in Enum.GetValues<TraitKind>())
        {
            if (before.Traits[kind] == after.Traits[kind])
            {
                continue;
            }

            string text = $"{Lexicon.Trait(kind)} {before.Traits[kind]} → **{after.Traits[kind]}**";
            Situation threat = ReactionForecast.Presets.First(p => p.Id == "threat");
            ForecastSubject subject = DayForecast.Virtual(data, before.Code);

            Reaction was = ReactionForecast.Run(data, before, threat, subject, subject.Home);
            Reaction now = ReactionForecast.Run(data, after, threat, subject, subject.Home);

            if (!string.Equals(was.Matched?.Id, now.Matched?.Id, StringComparison.Ordinal))
            {
                text += $" — 위협에 {was.Sentence} 대신 **{now.Sentence}**"
                    + (now.Matched is { } rule ? $" (`{rule.Id}`)" : string.Empty);
            }

            lines.Add(text);
        }
    }

    private static string Workplace(MasterDataSet data, ArchetypeDef before, ArchetypeDef after)
    {
        string was = before.WorkplacePoiType is { Length: > 0 } b ? Lexicon.Place(b) : "없음";
        string now = after.WorkplacePoiType is { Length: > 0 } a ? Lexicon.Place(a) : "없음";
        string text = $"일터 {was} → **{now}**";

        if (after.WorkplacePoiType is not { Length: > 0 } type)
        {
            return text;
        }

        int population = (int)Math.Round(after.PopulationWeight * ArchetypeCard.PopulationBase);
        int capacity = data.Pois.OfSubtype(type).Sum(p => data.Pois[p].Capacity);

        return text + (capacity >= population
            ? $" (정원 {capacity} ≥ 인구 {population} ✓)"
            : $" (정원 {capacity} < 인구 {population} ✗)");
    }

    private static bool IsDutyAction(MasterDataSet data, ActionId action) =>
        data.ActionName(action) is "Guard" or "Patrol";

    private static string Times(ImmutableArray<TimeOfDay> times) =>
        times.IsEmpty ? "없음" : string.Join("·", times.Select(Lexicon.Of));

    private static string Items(ImmutableArray<string> values) =>
        values.IsEmpty ? "없음" : string.Join("·", values);
}
