using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Narrative;

/// <summary>건강 진단 한 건 (T29).</summary>
/// <param name="Code">규칙 코드 (<c>L1_TODO_DESC</c>).</param>
/// <param name="Title">한 줄 제목.</param>
/// <param name="Detail">무엇이 어떻게 이상한가.</param>
/// <param name="Fix">무엇을 하면 되는가.</param>
/// <param name="Field">고칠 필드 경로 (<c>/desc</c>). 없으면 빈 문자열.</param>
public readonly record struct LintFinding(string Code, string Title, string Detail, string Fix, string Field);

/// <summary>
/// 검증은 통과하는데 이상한 정의를 잡는다 (T29).
///
/// <para>
/// <b>V1~V13 은 "기동이 되는가" 만 본다.</b> 초보자는 그것을 통과하고도 설명이 <c>TODO:</c> 인
/// 직업, 인구가 0명으로 반올림되는 직업, 사는 지역에 선술집이 없어 <c>$tavern</c> 스텝이
/// 매번 실패하는 하루 일과를 만든다. 전부 <b>차단이 아니라 주의</b>다 — 의도한 것일 수 있다.
/// </para>
///
/// <para><b>전부 결정론이다.</b> 같은 정의면 같은 진단이 나온다.</para>
/// </summary>
public static class ArchetypeLint
{
    /// <summary>성향이 이 범위에 전부 들면 "밋밋하다" 로 본다.</summary>
    public const int BlandLow = 45;

    /// <summary>밋밋함 판정의 위쪽 경계.</summary>
    public const int BlandHigh = 55;

    /// <summary>허용 행동이 이보다 적으면 하루를 짜기 어렵다.</summary>
    public const int FewActions = 6;

    /// <summary>
    /// 일터 정원 여유가 이 비율 미만이면 한 명만 늘려도 V10 에 걸린다.
    ///
    /// <b>2% 다.</b> 이 마스터데이터는 정원을 인구에 맞춰 설계해서 대부분 4~7% 로 돈다 —
    /// 10% 로 두면 40개 전부가 경고가 되고, 전부가 경고면 아무도 읽지 않는다.
    /// </summary>
    public const double CapacityMargin = 0.02;

    /// <summary>이 아키타입의 진단. 코드 오름차순이다.</summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="def">아키타입 정의. 저장 전 초안이어도 된다.</param>
    /// <param name="instances">명단. 없으면 거주 지역을 일터에서 추정한다.</param>
    public static ImmutableArray<LintFinding> Run(
        MasterDataSet data, ArchetypeDef def, NpcInstanceTable? instances)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(def);

        ArchetypeFacts facts = ArchetypeCard.Facts(data, def);
        var findings = ImmutableArray.CreateBuilder<LintFinding>(8);

        if (def.Description.TrimStart().StartsWith("TODO", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new LintFinding(
                "L1_TODO_DESC",
                "설명이 아직 TODO 다",
                "이 문장은 LLM 프롬프트에 그대로 실린다. TODO 로 두면 NPC 가 이상하게 행동한다.",
                "이 직업이 어떤 존재인지 한두 문장으로 쓴다.",
                "/desc"));
        }

        if (facts.Population == 0)
        {
            findings.Add(new LintFinding(
                "L2_ZERO_POPULATION",
                "인구가 0명이다",
                $"가중치 {def.PopulationWeight:0.####} × {facts.PopulationBase} 가 반올림되어 0명이 된다. "
                + "명단을 다시 만들어도 한 마리도 생기지 않는다.",
                $"가중치를 최소 {1.0 / facts.PopulationBase:0.####} 이상으로 올린다.",
                "/population_weight"));
        }

        if (IsBland(def.Traits))
        {
            findings.Add(new LintFinding(
                "L3_BLAND_TRAITS",
                "성격이 밋밋하다",
                $"네 성향이 전부 {BlandLow}~{BlandHigh} 다. 돌발 반응이 다른 직업과 똑같이 갈리고 "
                + "LLM 이 이 직업을 구별할 단서가 없다.",
                "직업다운 성향 하나를 크게 벌린다 — 예: 위병은 용기를 높이고, 상인은 탐욕을 높인다.",
                "/traits"));
        }

        if (facts.AllowedCount < FewActions)
        {
            findings.Add(new LintFinding(
                "L4_FEW_ACTIONS",
                $"할 수 있는 행동이 {facts.AllowedCount}개뿐이다",
                "하루를 채울 조합이 부족하다. LLM 이 만들 수 있는 계획도 그만큼 단조로워진다.",
                $"최소 {FewActions}개 이상 — 이동·생활(먹기·마시기·잠) 과 본업 행동을 넣는다.",
                "/allowed_actions"));
        }

        ImmutableArray<ZoneId> zones = ZonesOf(data, def, instances);

        foreach (PoiSymbol symbol in UnbindableSymbols(data, def, zones))
        {
            string place = Lexicon.Of(symbol);

            findings.Add(new LintFinding(
                "L5_UNBINDABLE_IN_ZONE",
                $"{Lexicon.With(place, "을", "를")} 붙일 수 없다",
                $"하루 일과가 {place} 를 쓰는데, 이 직업이 사는 지역의 집에서 출발해 갈 수 있는 그런 장소가 "
                + "마을 어디에도 없다. 그 스텝은 매번 `V3.UNREACHABLE_POI` 로 실패한다.",
                $"그 유형의 장소를 만들거나, 하루 일과에서 {place} 스텝을 뺀다.",
                string.Empty));
        }

        if (facts.Fallback is { } plan)
        {
            LintFallback(data, def, plan, instances, findings);
        }

        if (facts.WorkplaceType is not null && facts.WorkplaceFits && facts.CapacityMargin < CapacityMargin)
        {
            findings.Add(new LintFinding(
                "L8_CAPACITY_MARGIN",
                "일터 정원 여유가 거의 없다",
                $"정원 합 {facts.WorkplaceCapacity} 에 인구 {facts.Population} 이다 "
                + $"(여유 {facts.CapacityMargin * 100:0.#}%). 인구를 조금만 늘려도 V10 에 걸린다.",
                "그 유형의 장소를 더 만들거나 정원을 올린다.",
                "/population_weight"));
        }

        return [.. findings.OrderBy(f => f.Code, StringComparer.Ordinal)];
    }

    private static void LintFallback(
        MasterDataSet data,
        ArchetypeDef def,
        CompiledPlan plan,
        NpcInstanceTable? instances,
        ImmutableArray<LintFinding>.Builder findings)
    {
        ImmutableArray<StepTrace> trace = PlanExplain.Trace(data, plan, plan.Bucket, def.Code);
        WorldFlags finalState = trace.IsEmpty ? data.InitialFlags(plan.Bucket) : trace[^1].After;
        LoopVerdict loop = PlanExplain.LoopOf(plan, finalState);

        // 실제 개체로 본다 — 가상 개체는 일터 subtype 의 첫 장소를 쓰므로 통근 거리가 실제와 다르다.
        ForecastSubject subject = DayForecast.Representative(data, instances, def.Code);
        Forecast forecast = DayForecast.Run(data, plan, subject, plan.Bucket);

        // L6 — 근무 시간대에 깨어 있는 순간이 하나도 없다. `npc timeline` 이 눈으로 보던 것이다.
        if (!def.DutyHours.IsEmpty && !AwakeOnDuty(data, def, forecast))
        {
            findings.Add(new LintFinding(
                "L6_DUTY_SLEEP",
                "근무 시간대에 깨어 있지 않다",
                $"근무 시간대는 {string.Join(" · ", def.DutyHours.Select(Lexicon.Of))} 인데, "
                + "하루 일과가 깨어 있는 시간이 그 안에 하나도 없다. LLM 이 죽으면 이 직업은 "
                + "근무를 한 번도 하지 않는다.",
                "잠 스텝의 목표 시간대를 근무 시작 전으로 옮기거나, 근무 시간대를 하루 일과에 맞춘다.",
                "/duty_hours"));
        }

        // L7 — 예측 소요가 타임아웃보다 길다. 그 스텝은 매번 실패로 끝난다.
        foreach (ForecastSegment segment in forecast.Segments)
        {
            if (segment.Kind is SegmentKind.Skipped or SegmentKind.Replan || segment.Lap > 0)
            {
                continue;
            }

            // `until_time` 스텝(잠)은 게임 시각이 끝낸다 — 완료 이벤트가 `GameTimeChanged` 다.
            // 경과 시간으로 재면 "아침까지 자는데 상한이 2시간" 이 전부 경고가 되고, 그건 사실이 아니다.
            if (data.Actions[segment.Action].Duration.Kind == DurationKind.UntilTime)
            {
                continue;
            }

            int timeout = plan.Steps[segment.StepIndex].TimeoutSeconds;

            if (timeout > 0 && segment.Seconds > timeout)
            {
                findings.Add(new LintFinding(
                    "L7_TIMEOUT_TOO_SHORT",
                    $"{segment.StepIndex + 1}번 스텝의 상한이 예상보다 짧다",
                    $"{Lexicon.With(Lexicon.Action(segment.ActionId), "에", "에")} 약 {segment.Seconds / 60}분이 "
                    + $"걸리는데 상한은 {timeout / 60}분이다. 매번 실패로 끝난다.",
                    $"그 스텝의 최대 시간을 {(segment.Seconds / 60) + 1}분 이상으로 올린다.",
                    "/steps/timeout_s"));
                break;
            }
        }

        if (!loop.Closed)
        {
            findings.Add(new LintFinding(
                "L9_LOOP_OPEN",
                "하루가 한 바퀴만 돈다",
                loop.Message + " 두 바퀴째마다 재계획이 걸려 LLM 호출이 샌다.",
                "마지막 스텝이 첫 스텝의 전제를 세우게 한다 — 보통 잠으로 끝내고 집에서 시작한다.",
                "/steps"));
        }

        bool rests = false;

        for (int i = 0; i < plan.Steps.Length; i++)
        {
            if ((plan.FlagsOf(i).Grants & WorldFlags.IsRested) != 0)
            {
                rests = true;
                break;
            }
        }

        if (!rests)
        {
            findings.Add(new LintFinding(
                "L10_NO_REST",
                "하루에 쉬는 스텝이 없다",
                "잠도 휴식도 없다. 피로가 쌓이는 설계라면 의도한 것이지만, 보통은 빠뜨린 것이다.",
                "하루 끝에 잠(또는 휴식) 스텝을 넣는다.",
                "/steps"));
        }
    }

    /// <summary>
    /// 근무 시간대 안에 <b>깨어 있는</b> 순간이 하나라도 있는가.
    ///
    /// <b>"근무 시간대에 잔다" 로 물으면 안 된다</b> — 이 저장소의 폴백은 전부 30분 활동 +
    /// "아침까지 잠" 이라 모든 직업이 걸린다. 알고 싶은 것은 "근무를 한 번이라도 하는가" 다.
    /// </summary>
    private static bool AwakeOnDuty(MasterDataSet data, ArchetypeDef def, Forecast forecast)
    {
        foreach (ForecastSegment segment in forecast.Segments)
        {
            if (segment.Kind is SegmentKind.Sleep or SegmentKind.Skipped or SegmentKind.Replan)
            {
                continue;
            }

            // 분 단위로 훑는다 — 깨어 있는 구간이 30분뿐이라 시간 단위면 놓친다.
            for (int t = segment.StartSeconds; t <= segment.EndSeconds; t += 60)
            {
                int hour = (forecast.StartClockSeconds + t) / 3600 % 24;

                if (def.IsOnDuty(data.Buckets.TimeOfDayAt(hour)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>이 직업이 사는 지역. 명단이 있으면 세고, 없으면 일터 subtype 의 지역으로 본다.</summary>
    private static ImmutableArray<ZoneId> ZonesOf(
        MasterDataSet data, ArchetypeDef def, NpcInstanceTable? instances)
    {
        if (instances is not null)
        {
            ImmutableArray<ZoneId> lived =
            [
                .. instances.Instances
                    .Where(n => n.Archetype == def.Code)
                    .Select(n => n.Zone)
                    .Distinct()
                    .OrderBy(z => z.Value),
            ];

            if (!lived.IsEmpty)
            {
                return lived;
            }
        }

        return [.. PlacementForecast.ByZone(data, def, ArchetypeCard.PopulationBase).Select(z => z.Zone)];
    }

    /// <summary>
    /// 하루 일과가 쓰는 심볼 중, 사는 지역 어딘가에서 바인딩할 수 없는 것.
    /// <b>존 단위로 본다</b> — <c>CanBindSymbol</c> 은 마을 전체를 보므로 이 병을 못 잡는다.
    /// </summary>
    private static ImmutableArray<PoiSymbol> UnbindableSymbols(
        MasterDataSet data, ArchetypeDef def, ImmutableArray<ZoneId> zones)
    {
        if (zones.IsEmpty || data.Fallbacks?.For(def.Code) is not { } plan)
        {
            return [];
        }

        var bad = ImmutableArray.CreateBuilder<PoiSymbol>();
        var seen = new HashSet<PoiSymbol>();

        foreach (CompiledStep step in plan.Steps)
        {
            if (step.Poi is PoiSymbol.None or PoiSymbol.Home or PoiSymbol.Workplace
                or PoiSymbol.PatrolRoute or PoiSymbol.NearestShelter || !seen.Add(step.Poi))
            {
                continue;
            }

            if (TypeOf(step.Poi) is not { } type)
            {
                continue;
            }

            // 바인딩은 존을 가리지 않는다 (PoiBinder·Sim 둘 다 마을 전체에서 가장 가까운 것을 고른다).
            // 그래서 "이 존에 없다" 는 실패가 아니다 — 실패는 그 집에서 출발해 갈 곳이 아예 없을 때다.
            foreach (ZoneId zone in zones)
            {
                if (!Reachable(data, zone, type, def.Code))
                {
                    bad.Add(step.Poi);
                    break;
                }
            }
        }

        return bad.ToImmutable();
    }

    /// <summary>이 지역의 집에서 출발해 그 유형의 장소를 붙일 수 있는가. 존을 가리지 않는다.</summary>
    private static bool Reachable(MasterDataSet data, ZoneId zone, PoiType type, ArchetypeId who)
    {
        PoiId home = default;

        foreach (PoiId id in data.Pois.InZone(zone))
        {
            if (data.Pois[id].Type == PoiType.Home)
            {
                home = id;
                break;
            }
        }

        return data.Pois.NearestEnterable(type, home, who).Value != 0;
    }

    private static PoiType? TypeOf(PoiSymbol symbol) => symbol switch
    {
        PoiSymbol.Market => PoiType.Market,
        PoiSymbol.Tavern => PoiType.Tavern,
        PoiSymbol.Temple => PoiType.Temple,
        PoiSymbol.Gate or PoiSymbol.NearestSafe => PoiType.Gate,
        PoiSymbol.NearestField => PoiType.Field,
        _ => null,
    };

    private static bool IsBland(in TraitSet traits) =>
        traits.Diligence is >= BlandLow and <= BlandHigh
        && traits.Sociability is >= BlandLow and <= BlandHigh
        && traits.Courage is >= BlandLow and <= BlandHigh
        && traits.Greed is >= BlandLow and <= BlandHigh;
}
