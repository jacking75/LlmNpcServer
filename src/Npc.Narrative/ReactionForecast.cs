using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Narrative;

/// <summary>사람이 고르는 상황 (T23). 플래그 몇 개와 사건 하나.</summary>
/// <param name="Id">프리셋 id. 화면 버튼 키다.</param>
/// <param name="Label">버튼에 적는 말.</param>
/// <param name="Flags">이 상황에서 서 있는 플래그.</param>
/// <param name="Event">이 상황이 만드는 사건. 없으면 상태만 본다.</param>
public readonly record struct Situation(string Id, string Label, WorldFlags Flags, GameEventKind? Event);

/// <summary>규칙 하나가 왜 걸렸는지·왜 안 걸렸는지 (T23).</summary>
/// <param name="Rule">규칙.</param>
/// <param name="Matched">걸렸는가.</param>
/// <param name="Failed">못 맞춘 조건들. 걸렸으면 비어 있다.</param>
public readonly record struct RuleCheck(InterruptRule Rule, bool Matched, ImmutableArray<string> Failed);

/// <summary>상황 하나에 대한 반응 (T23).</summary>
/// <param name="Matched">걸린 규칙. 없으면 null.</param>
/// <param name="Sentence">사람이 읽는 한 줄.</param>
/// <param name="TargetPoi">달려가는 곳. 없으면 <c>default</c>.</param>
/// <param name="Urgency">그 뒤 재계획 긴급도.</param>
/// <param name="Checks">모든 규칙의 판정 (우선순위 순).</param>
public readonly record struct Reaction(
    InterruptRule? Matched,
    string Sentence,
    PoiId TargetPoi,
    int Urgency,
    ImmutableArray<RuleCheck> Checks);

/// <summary>
/// "위협이 오면 이 NPC 는?" (T23). <b>초보자가 가장 궁금해하는 것</b>이고,
/// 답은 <see cref="InterruptRules"/> 가 이미 알고 있다.
///
/// <para>
/// <b>더 중요한 것은 "왜 그 규칙인가 · 왜 다른 규칙은 아닌가" 다.</b> 용기 55 를 30 으로 내리면
/// 물러나기가 도망으로 바뀌는데, 그 이유가 화면에 없으면 값과 행동의 인과를 배울 수 없다.
/// <see cref="Reaction.Checks"/> 가 모든 규칙을 우선순위 순으로 돌며 실패 조건을 문장으로 낸다.
/// </para>
/// </summary>
public static class ReactionForecast
{
    /// <summary>
    /// 화면 버튼이 되는 상황들. <b>플래그 이름이 코드에 박히지만 그것은 마스터데이터 값이 아니라
    /// <see cref="WorldFlags"/> 열거형이다</b> — 없는 플래그를 쓰면 컴파일이 깨진다.
    /// </summary>
    public static ImmutableArray<Situation> Presets { get; } =
    [
        new("threat", "위협 등장", WorldFlags.ThreatNearby, null),
        new("combat", "전투 시작", WorldFlags.InCombat, GameEventKind.CombatStarted),
        new("injured", "부상", WorldFlags.InCombat | WorldFlags.IsInjured, null),
        new("hostile", "적대 플레이어", WorldFlags.HostilePlayerNearby, null),
        new("talked", "플레이어가 말을 건다", WorldFlags.None, GameEventKind.PlayerInteracted),
        new("damage", "피해 입음", WorldFlags.None, GameEventKind.DamageTaken),
        new("alone", "혼자 위협 (아군 없음)", WorldFlags.ThreatNearby, null),
    ];

    /// <summary>
    /// 이 상황에서 이 직업이 무엇을 하는가.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="def">아키타입 정의. 저장 전 초안이어도 된다.</param>
    /// <param name="situation">상황.</param>
    /// <param name="subject">개체. 목표 장소 바인딩에 쓴다.</param>
    /// <param name="from">지금 위치.</param>
    public static Reaction Run(
        MasterDataSet data,
        ArchetypeDef def,
        in Situation situation,
        in ForecastSubject subject,
        PoiId from)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(def);

        var checks = ImmutableArray.CreateBuilder<RuleCheck>(data.Interrupts.Count);
        InterruptRule? matched = null;

        foreach (InterruptRule rule in InterruptExplain.Ordered(data))
        {
            ImmutableArray<string> failed = Explain(data, rule, situation, def);
            bool ok = failed.IsEmpty;

            checks.Add(new RuleCheck(rule, ok && matched is null, failed));

            matched ??= ok ? rule : null;
        }

        if (matched is not { } hit)
        {
            return new Reaction(
                null,
                "아무 규칙에도 안 걸린다 — 하던 일을 계속한다.",
                default,
                0,
                checks.ToImmutable());
        }

        PoiId target = default;

        if (hit.Poi != PoiSymbol.None)
        {
            _ = DayForecast.TryBind(data, hit.Poi, subject, from, out target);
        }

        string where = target.Value != 0
            ? $" → {Lexicon.PlaceName(data.Pois[target])}"
            : hit.Poi != PoiSymbol.None ? $" → {Lexicon.Of(hit.Poi)}" : string.Empty;

        return new Reaction(
            hit,
            Lexicon.Action(data.ActionName(hit.Action)) + where,
            target,
            hit.Urgency,
            checks.ToImmutable());
    }

    /// <summary>
    /// 이 규칙이 왜 안 걸렸는가. <b>빈 배열이면 걸린다.</b>
    ///
    /// 판정 순서와 조건은 <see cref="InterruptRules.TryMatch"/> 와 같아야 한다 —
    /// 여기서 "걸린다" 고 했는데 런타임이 안 걸면 화면이 거짓말을 한 것이다.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="rule">규칙.</param>
    /// <param name="situation">상황.</param>
    /// <param name="def">아키타입.</param>
    public static ImmutableArray<string> Explain(
        MasterDataSet data, InterruptRule rule, in Situation situation, ArchetypeDef def)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(def);

        var failed = ImmutableArray.CreateBuilder<string>(4);
        WorldFlags flags = situation.Flags;

        if (rule.Event is { } kind && kind != situation.Event)
        {
            failed.Add($"이 상황은 `{kind}` 사건이 아니다");
        }

        if (rule.AnyFlag != WorldFlags.None && (rule.AnyFlag & flags) == 0)
        {
            failed.Add($"`{WorldFlagTable.Format(rule.AnyFlag)}` 중 하나도 서 있지 않다");
        }

        if ((rule.AllFlag & ~flags) != 0)
        {
            failed.Add($"`{WorldFlagTable.Format(rule.AllFlag & ~flags)}` 가 서 있지 않다");
        }

        if ((rule.NoneFlag & flags) != 0)
        {
            failed.Add($"`{WorldFlagTable.Format(rule.NoneFlag & flags)}` 가 서 있다");
        }

        if (rule.CombatCapable is { } combat && combat != def.CombatCapable)
        {
            failed.Add(combat ? "전투 가능한 직업이 아니다" : "전투 가능한 직업이라 해당 없다");
        }

        foreach (TraitCondition condition in rule.Traits)
        {
            if (!condition.Matches(def.Traits))
            {
                failed.Add(
                    $"{Lexicon.Trait(condition.Kind)} {def.Traits[condition.Kind]} 라서 "
                    + $"'{Compare(condition)}' 에 안 맞는다");
            }
        }

        if (!def.Allows(rule.Action))
        {
            failed.Add($"{Lexicon.Action(data.ActionName(rule.Action))} 이(가) 허용 행동에 없다");
        }

        return failed.ToImmutable();
    }

    private static string Compare(TraitCondition condition) => condition.Comparison switch
    {
        TraitComparison.Less => "< " + condition.Value,
        TraitComparison.LessOrEqual => "≤ " + condition.Value,
        TraitComparison.Greater => "> " + condition.Value,
        TraitComparison.GreaterOrEqual => "≥ " + condition.Value,
        TraitComparison.Equal => "= " + condition.Value,
        _ => condition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
