using System.Collections.Immutable;
using System.Text;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;

namespace Npc.Narrative;

/// <summary>
/// 누구의 하루인가 (T22). 아키타입 단위로 볼 때는 대표 개체를 넣는다.
/// </summary>
/// <param name="Archetype">직업 정의.</param>
/// <param name="Zone">사는 지역.</param>
/// <param name="Home">집 POI.</param>
/// <param name="Workplace">일터 POI. 없으면 <c>default</c>.</param>
/// <param name="PatrolRoute">순찰 지점.</param>
/// <param name="ScheduleOffsetMinutes">시간대 전환에 더할 분.</param>
/// <param name="NpcId">개체 번호. 가상 개체면 0 이다.</param>
public readonly record struct ForecastSubject(
    ArchetypeDef Archetype,
    ZoneId Zone,
    PoiId Home,
    PoiId Workplace,
    ImmutableArray<PoiId> PatrolRoute,
    int ScheduleOffsetMinutes,
    int NpcId = 0)
{
    /// <summary>명단에서 온 개체인가. 거짓이면 "명단 재생성 전 예상" 이다.</summary>
    public bool IsReal => NpcId > 0;
}

/// <summary>타임라인 한 조각이 무엇인가. 인형 배지가 이것으로 갈린다 (부록 E).</summary>
public enum SegmentKind
{
    /// <summary>걷거나 뛴다.</summary>
    Move,

    /// <summary>무언가를 한다.</summary>
    Act,

    /// <summary>잔다.</summary>
    Sleep,

    /// <summary>기다린다·쉰다.</summary>
    Wait,

    /// <summary>전제를 못 채워 건너뛴다. 0초다.</summary>
    Skipped,

    /// <summary>여기서 새 계획을 요청한다. 예측은 멈춘다.</summary>
    Replan,
}

/// <summary>
/// 타임라인 한 조각 (T22). 시각은 <b>하루 시작 기준 경과 게임 초</b>다.
/// </summary>
/// <param name="StepIndex">플랜의 스텝 번호.</param>
/// <param name="Lap">몇 바퀴째인가. 0부터.</param>
/// <param name="Kind">무엇을 하는 중인가.</param>
/// <param name="Action">액션 code.</param>
/// <param name="ActionId">액션 id.</param>
/// <param name="StartSeconds">시작 경과 초.</param>
/// <param name="EndSeconds">끝 경과 초.</param>
/// <param name="From">출발 POI.</param>
/// <param name="To">도착·머무는 POI.</param>
/// <param name="Caption">사람이 읽는 한 줄.</param>
/// <param name="FlagsAfter">이 스텝 뒤 상태.</param>
/// <param name="Code">실패 코드. 통과면 빈 문자열.</param>
/// <param name="Reason">실패 이유.</param>
/// <param name="Inventory">이 스텝의 소지품 증감.</param>
public readonly record struct ForecastSegment(
    int StepIndex,
    int Lap,
    SegmentKind Kind,
    ActionId Action,
    string ActionId,
    int StartSeconds,
    int EndSeconds,
    PoiId From,
    PoiId To,
    string Caption,
    WorldFlags FlagsAfter,
    string Code,
    string Reason,
    ImmutableArray<(ItemId Item, int Delta)> Inventory)
{
    /// <summary>걸리는 시간(초).</summary>
    public int Seconds => EndSeconds - StartSeconds;
}

/// <summary>하루 예측 한 건 (T22).</summary>
/// <param name="Subject">누구의 하루인가.</param>
/// <param name="Bucket">어느 상황인가.</param>
/// <param name="Plan">돌린 플랜.</param>
/// <param name="StartClockSeconds">하루 시작 시각 (게임 초, 0~86399).</param>
/// <param name="Segments">타임라인.</param>
/// <param name="Loop">고리 판정.</param>
/// <param name="StoppedForReplan">재계획으로 멈췄는가.</param>
public sealed record Forecast(
    ForecastSubject Subject,
    BucketKey Bucket,
    CompiledPlan Plan,
    int StartClockSeconds,
    ImmutableArray<ForecastSegment> Segments,
    LoopVerdict Loop,
    bool StoppedForReplan)
{
    /// <summary>예측이 덮은 시간(초). 24시간을 다 채웠으면 86,400 이다.</summary>
    public int CoveredSeconds => Segments.IsEmpty ? 0 : Segments[^1].EndSeconds;
}

/// <summary>
/// 정의에서 하루를 계산한다 (T22). <b>정의를 다 읽어도 "그래서 어떻게 움직이지" 를 모른다</b> 가
/// 이 도구의 가장 큰 빈틈이었다 — 재료는 전부 코어에 있고, 이 클래스가 그것을 한 타임라인으로 엮는다.
///
/// <para>
/// <b>기대 경로이지 약속이 아니다.</b> 실제 서버는 스텝마다 ±<c>SimWorld.JitterPercent</c> 로
/// 흔들리고, LLM 이 상황에 맞는 새 계획을 주며, 경로는 게임서버가 계산한다. 소켓 경로는
/// 결정론도 아니다 (CLAUDE.md §2.3). <b>화면은 그렇게 적어야 한다.</b>
/// </para>
///
/// <para>
/// <b>결정론이다.</b> 시각·난수·LLM 을 쓰지 않는다 — <c>Npc.Narrative</c> 의 규약 그대로다.
/// 소요 시간은 <see cref="ActionDuration"/>, 심볼 바인딩은 <see cref="PoiTable.NearestEnterable(PoiType, PoiId, ArchetypeId)"/>,
/// 스텝 판정은 <see cref="PlanExplain.Trace"/> — 전부 다른 곳과 <b>같은 함수</b>를 쓴다.
/// </para>
/// </summary>
public static class DayForecast
{
    /// <summary>게임 하루.</summary>
    public const int DaySeconds = 24 * 3600;

    /// <summary>한 예측이 도는 최대 바퀴 수. 이보다 오래 돌면 하루를 못 채운 것이다.</summary>
    public const int MaxLaps = 64;

    /// <summary><c>$nearest_safe</c> 가 보는 타입 순서. Sim 과 같다 — 성문, 없으면 집.</summary>
    private static readonly PoiType[] s_safeTypes = [PoiType.Gate, PoiType.Home];

    /// <summary>이 플랜으로 하루를 돌린다.</summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="plan">돌릴 플랜.</param>
    /// <param name="subject">누구의 하루인가.</param>
    /// <param name="bucket">시작 시각·시작 상태를 정하는 버킷.</param>
    public static Forecast Run(
        MasterDataSet data, CompiledPlan plan, in ForecastSubject subject, BucketKey bucket)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(plan);

        ImmutableArray<StepTrace> trace = PlanExplain.Trace(data, plan, bucket, subject.Archetype.Code);
        WorldFlags finalState = trace.IsEmpty ? data.InitialFlags(bucket) : trace[^1].After;
        LoopVerdict loop = PlanExplain.LoopOf(plan, finalState);

        (int fromHour, int _) = data.Buckets.GameHoursOf(bucket.T);
        int startClock = Wrap((fromHour * 3600) + (subject.ScheduleOffsetMinutes * 60));

        var segments = ImmutableArray.CreateBuilder<ForecastSegment>(plan.Steps.Length * 2);

        PoiId current = subject.Home.Value != 0
            ? subject.Home
            : data.Pois.FirstEnterable(PoiType.Home, subject.Archetype.Code);

        int elapsed = 0;
        bool stopped = false;

        for (int lap = 0; lap < MaxLaps && elapsed < DaySeconds && !stopped; lap++)
        {
            // 고리가 안 닫히면 두 바퀴째 첫 스텝에서 재계획이 걸린다 — 실제 서버가 그렇게 돈다.
            if (lap > 0 && !loop.Closed)
            {
                segments.Add(Replan(plan, trace, 0, lap, elapsed, current, loop.Message));
                stopped = true;
                break;
            }

            for (int i = 0; i < plan.Steps.Length && elapsed < DaySeconds; i++)
            {
                CompiledStep step = plan.Steps[i];
                StepTrace verdict = trace[i];
                ActionDef action = data.Actions[step.Action];

                PoiId target = current;
                bool bound = true;

                if (step.Poi != PoiSymbol.None)
                {
                    bound = TryBind(data, step.Poi, subject, current, out target);

                    if (!bound)
                    {
                        target = current;
                    }
                }

                string code = !bound ? "V3.UNREACHABLE_POI" : verdict.Code;
                string reason = !bound
                    ? $"이 개체에게 {Lexicon.Of(step.Poi)} 를 붙일 수 없다"
                    : verdict.Reason;

                if (code.Length > 0)
                {
                    if (plan.OnFail == StepFailPolicy.Skip)
                    {
                        segments.Add(new ForecastSegment(
                            i, lap, SegmentKind.Skipped, step.Action, action.Id,
                            elapsed, elapsed, current, current,
                            "건너뛴다 — " + Plain(reason),
                            verdict.After, code, reason, []));
                        continue;
                    }

                    segments.Add(Replan(plan, trace, i, lap, elapsed, current, reason));
                    stopped = true;
                    break;
                }

                int seconds = (int)Math.Round(
                    ActionDuration.Seconds(data, action, step, current, target, startClock + elapsed));
                int end = Math.Min(elapsed + seconds, DaySeconds);

                bool moves = data.CompletesOnArrival(step.Action) && target.Value != 0 && target != current;
                SegmentKind kind = KindOf(data, action, moves);

                segments.Add(new ForecastSegment(
                    i, lap, kind, step.Action, action.Id,
                    elapsed, end, current, target,
                    Caption(data, action, step, kind, target, end - elapsed),
                    verdict.After, string.Empty, string.Empty,
                    InventoryDelta(data, action, step)));

                elapsed = end;

                if (moves)
                {
                    current = target;
                }
            }

            if (!plan.Loop)
            {
                break;
            }
        }

        return new Forecast(subject, bucket, plan, startClock, segments.ToImmutable(), loop, stopped);
    }

    /// <summary>이 아키타입의 하루 일과(폴백)로 돌린다.</summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="subject">누구의 하루인가.</param>
    /// <param name="bucket">버킷.</param>
    public static Forecast? RunFallback(MasterDataSet data, in ForecastSubject subject, BucketKey bucket)
    {
        ArgumentNullException.ThrowIfNull(data);

        return data.Fallbacks?.For(subject.Archetype.Code) is { } plan
            ? Run(data, plan, subject, bucket)
            : null;
    }

    /// <summary>
    /// 이 아키타입의 대표 개체. <c>gen_npcs</c> 가 code 순으로 배치하므로 첫 개체는 결정론이다.
    /// 명단이 없거나 한 마리도 없으면 <b>가상 개체</b>를 만든다 — 새 직업은 명단 재생성 전이다.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="instances">명단. 없으면 null.</param>
    /// <param name="archetype">아키타입 code.</param>
    public static ForecastSubject Representative(
        MasterDataSet data, NpcInstanceTable? instances, ArchetypeId archetype)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (instances is not null)
        {
            foreach (NpcInstanceDef npc in instances.Instances)
            {
                if (npc.Archetype == archetype)
                {
                    return Of(data, npc);
                }
            }
        }

        return Virtual(data, archetype);
    }

    /// <summary>명단이 없을 때의 가상 개체. 일터 subtype 의 첫 장소와 같은 지역의 첫 집.</summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="archetype">아키타입 code.</param>
    public static ForecastSubject Virtual(MasterDataSet data, ArchetypeId archetype)
    {
        ArgumentNullException.ThrowIfNull(data);

        ArchetypeDef def = data.Archetypes[archetype];
        PoiId workplace = default;

        if (def.WorkplacePoiType is { Length: > 0 } subtype)
        {
            ImmutableArray<PoiId> sites = data.Pois.OfSubtype(subtype);
            workplace = sites.Length > 0 ? sites[0] : default;
        }

        ZoneId zone = workplace.Value != 0 ? data.Pois[workplace].Zone : data.Zones.Zones[0].Code;
        PoiId home = default;

        foreach (PoiId id in data.Pois.InZone(zone))
        {
            if (data.Pois[id].Type == PoiType.Home)
            {
                home = id;
                break;
            }
        }

        if (home.Value == 0)
        {
            home = data.Pois.FirstEnterable(PoiType.Home, archetype);
        }

        return new ForecastSubject(def, zone, home, workplace, [], 0);
    }

    /// <summary>실제 개체에서.</summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="npc">인스턴스.</param>
    public static ForecastSubject Of(MasterDataSet data, in NpcInstanceDef npc)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new ForecastSubject(
            data.Archetypes[npc.Archetype],
            npc.Zone,
            npc.Home,
            npc.Workplace,
            npc.PatrolRoute.IsDefault ? [] : npc.PatrolRoute,
            npc.ScheduleOffsetMinutes,
            npc.Id);
    }

    /// <summary>
    /// 이 시각의 위치. 이동 중이면 두 POI 사이를 선형 보간한다 — 지도 위 인형이 이 값으로 움직인다.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="forecast">예측.</param>
    /// <param name="seconds">하루 시작 기준 경과 초.</param>
    public static (PoiId Poi, float X, float Z, int SegmentIndex) PositionAt(
        MasterDataSet data, Forecast forecast, int seconds)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(forecast);

        if (forecast.Segments.IsEmpty)
        {
            PoiId home = forecast.Subject.Home;

            return home.Value == 0
                ? (default, 0, 0, -1)
                : (home, data.Pois[home].Pos.X, data.Pois[home].Pos.Z, -1);
        }

        int index = 0;

        for (int i = 0; i < forecast.Segments.Length; i++)
        {
            if (forecast.Segments[i].StartSeconds <= seconds)
            {
                index = i;
            }
            else
            {
                break;
            }
        }

        ForecastSegment segment = forecast.Segments[index];

        if (segment.To.Value == 0)
        {
            return (default, 0, 0, index);
        }

        WorldPos to = data.Pois[segment.To].Pos;

        if (segment.Kind != SegmentKind.Move || segment.From.Value == 0 || segment.Seconds <= 0)
        {
            return (segment.To, to.X, to.Z, index);
        }

        WorldPos from = data.Pois[segment.From].Pos;
        float k = Math.Clamp((seconds - segment.StartSeconds) / (float)segment.Seconds, 0, 1);

        return (k >= 1 ? segment.To : segment.From, from.X + ((to.X - from.X) * k), from.Z + ((to.Z - from.Z) * k), index);
    }

    /// <summary>CLI·골든이 쓰는 표. <c>npc forecast</c> 가 이것을 그대로 낸다.</summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="forecast">예측.</param>
    public static string RenderMarkdown(MasterDataSet data, Forecast forecast)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(forecast);

        ArchetypeDef def = forecast.Subject.Archetype;
        var sb = new StringBuilder(4 * 1024);

        sb.Append("# ").Append(Lexicon.Archetype(def.Id)).Append("의 하루 — ")
          .AppendLine(forecast.Bucket.Format(def.Id));
        sb.AppendLine();

        Md.TableHead(sb, "항목", "값");
        Md.Row(sb, "기준 개체", forecast.Subject.IsReal
            ? $"#{Md.N(forecast.Subject.NpcId)}"
            : "가상 개체 — 명단 재생성 전 예상");
        Md.Row(sb, "집", forecast.Subject.Home.Value == 0 ? "—" : Md.Code(data.Pois[forecast.Subject.Home].Id));
        Md.Row(sb, "일터", forecast.Subject.Workplace.Value == 0 ? "없음" : Md.Code(data.Pois[forecast.Subject.Workplace].Id));
        Md.Row(sb, "하루 시작", Clock(forecast.StartClockSeconds));
        Md.Row(sb, "덮은 시간", Md.Seconds(forecast.CoveredSeconds));
        Md.Row(sb, "고리", forecast.Loop.Message);
        sb.AppendLine();

        sb.AppendLine("**기대 경로다.** 실제 서버는 스텝마다 흔들리고, LLM 이 상황에 맞는 새 계획을 주며, "
            + "경로는 게임서버가 계산한다.");
        sb.AppendLine();

        Md.TableHead(sb, "시각", "#", "무엇을", "어디서", "걸리는 시간", "상태");

        foreach (ForecastSegment segment in forecast.Segments)
        {
            Md.Row(
                sb,
                Clock(Wrap(forecast.StartClockSeconds + segment.StartSeconds)),
                Md.N(segment.StepIndex + 1),
                Md.Cell(segment.Caption),
                segment.To.Value == 0 ? "—" : Md.Code(data.Pois[segment.To].Id),
                segment.Seconds == 0 ? "—" : Md.Seconds(segment.Seconds),
                Md.Code(WorldFlagTable.Format(segment.FlagsAfter)));
        }

        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// 심볼 → 실제 POI (부록 D.3). 개체 값은 개체에서, 나머지는 현재 위치에서 가장 가까운 것.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="symbol">심볼.</param>
    /// <param name="subject">개체.</param>
    /// <param name="current">현재 위치.</param>
    /// <param name="poi">바인딩 결과.</param>
    public static bool TryBind(
        MasterDataSet data, PoiSymbol symbol, in ForecastSubject subject, PoiId current, out PoiId poi)
    {
        ArgumentNullException.ThrowIfNull(data);

        ArchetypeId who = subject.Archetype.Code;

        poi = symbol switch
        {
            PoiSymbol.Home or PoiSymbol.NearestShelter => subject.Home,
            PoiSymbol.Workplace => subject.Workplace,
            PoiSymbol.Market => data.Pois.NearestEnterable(PoiType.Market, current, who),
            PoiSymbol.Tavern => data.Pois.NearestEnterable(PoiType.Tavern, current, who),
            PoiSymbol.Temple => data.Pois.NearestEnterable(PoiType.Temple, current, who),
            PoiSymbol.Gate => data.Pois.NearestEnterable(PoiType.Gate, current, who),
            PoiSymbol.NearestField => data.Pois.NearestEnterable(PoiType.Field, current, who),
            PoiSymbol.NearestSafe => data.Pois.NearestEnterable(s_safeTypes, current, who),
            PoiSymbol.PatrolRoute => subject.PatrolRoute.IsDefaultOrEmpty
                ? (subject.Workplace.Value != 0 ? subject.Workplace : subject.Home)
                : subject.PatrolRoute[0],
            _ => current,
        };

        // 집·대피소는 개체에 없으면 마을에서 찾는다 — Sim 이 그렇게 떨어진다.
        if (poi.Value == 0 && symbol is PoiSymbol.Home or PoiSymbol.NearestShelter)
        {
            poi = data.Pois.FirstEnterable(PoiType.Home, who);
        }

        return poi.Value != 0;
    }

    private static ForecastSegment Replan(
        CompiledPlan plan, ImmutableArray<StepTrace> trace, int index, int lap, int elapsed, PoiId current, string reason)
    {
        StepTrace step = trace[index];

        return new ForecastSegment(
            index, lap, SegmentKind.Replan, step.Action, step.ActionId,
            elapsed, elapsed, current, current,
            "여기서 새 계획을 요청한다 — " + Plain(reason),
            step.After, step.Code, reason, []);
    }

    /// <summary>
    /// 이 스텝이 무엇으로 보이는가. <b>액션 id 를 코드에 박지 않는다</b> — 정의에서 읽는다.
    /// </summary>
    private static SegmentKind KindOf(MasterDataSet data, ActionDef action, bool moves)
    {
        if (moves)
        {
            return SegmentKind.Move;
        }

        if ((action.Grants & WorldFlags.IsRested) != 0 && action.Duration.Kind == DurationKind.UntilTime)
        {
            return SegmentKind.Sleep;
        }

        if ((action.Grants & WorldFlags.IsRested) != 0 || action.Category == ActionCategory.Misc)
        {
            return SegmentKind.Wait;
        }

        _ = data;

        return SegmentKind.Act;
    }

    /// <summary>캡션 문장 틀 (부록 D.5).</summary>
    private static string Caption(
        MasterDataSet data, ActionDef action, in CompiledStep step, SegmentKind kind, PoiId target, int seconds)
    {
        string minutes = Minutes(seconds);
        string verb = Lexicon.ActionVerb(action.Id);

        if (kind == SegmentKind.Move)
        {
            // 장소 번호는 조사를 흔든다 ("대장간 #1으로"). 유형 이름으로 조사를 붙이고 번호는 지도가 보여 준다.
            string place = target.Value == 0 ? Lexicon.Of(step.Poi) : Lexicon.Place(data.Pois[target].Subtype);
            string how = ActionDuration.IsRunning(action, step) ? "뛰어간다" : "걸어간다";

            return $"{Lexicon.To(place)} {how} ({minutes})";
        }

        if (action.Duration.Kind == DurationKind.UntilTime)
        {
            ParamDef? param = action.Param(action.Duration.Param ?? string.Empty);

            if (param is not null && step.ArgFlags < param.EnumValues.Length
                && Enum.TryParse(param.EnumValues[step.ArgFlags], out TimeOfDay until))
            {
                return $"{Lexicon.Of(until)}까지 {verb}";
            }
        }

        if (step.Item != default)
        {
            string item = Lexicon.Item(data.ItemName(step.Item));
            string count = step.Count > 1 ? $" {step.Count}개" : string.Empty;

            return $"{Lexicon.With(item, "을", "를")}{count} {verb} ({minutes})";
        }

        return $"{verb} ({minutes})";
    }

    /// <summary>이 스텝의 소지품 증감. <c>PlanExplain</c> 의 수지 규칙과 같다.</summary>
    private static ImmutableArray<(ItemId Item, int Delta)> InventoryDelta(
        MasterDataSet data, ActionDef action, in CompiledStep step)
    {
        if (step.Item == default)
        {
            return [];
        }

        int count = step.Count == 0 ? 1 : step.Count;

        if (data.TryGetRecipeInputs(step.Item, out ImmutableArray<PlanRecipeInput> inputs))
        {
            var builder = ImmutableArray.CreateBuilder<(ItemId, int)>(inputs.Length + 1);

            builder.Add((step.Item, count));

            foreach (PlanRecipeInput input in inputs)
            {
                builder.Add((input.Item, -(input.Count * count)));
            }

            return builder.ToImmutable();
        }

        _ = action;

        return data.ProducesItem(step.Action) ? [(step.Item, count)] : [(step.Item, -count)];
    }

    /// <summary>마크다운 강조와 코드 표기를 뺀다. 캡션은 말풍선에 들어가므로 기호가 없어야 한다.</summary>
    private static string Plain(string text) => text.Replace("`", string.Empty, StringComparison.Ordinal);

    private static string Minutes(int seconds) =>
        seconds < 60 ? $"약 {seconds}초" : $"약 {(int)Math.Round(seconds / 60.0)}분";

    private static string Clock(int seconds)
    {
        int wrapped = Wrap(seconds);

        return $"{wrapped / 3600:00}:{wrapped % 3600 / 60:00}";
    }

    private static int Wrap(int seconds) => ((seconds % DaySeconds) + DaySeconds) % DaySeconds;
}
