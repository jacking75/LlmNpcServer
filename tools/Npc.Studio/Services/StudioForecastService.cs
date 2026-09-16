using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.Narrative;

namespace Npc.Studio.Services;

/// <summary>
/// 화면 하나가 재생할 하루 (T24).
/// </summary>
/// <param name="Forecast">예측.</param>
/// <param name="ZonePois">지도에 그릴 같은 지역 장소.</param>
/// <param name="ViewBox">SVG viewBox 문자열.</param>
/// <param name="Keyframes">정적 JS 가 읽는 키프레임 JSON.</param>
/// <param name="Reactions">상황 프리셋별 반응.</param>
/// <param name="Group">직군 (인형 색).</param>
/// <param name="SubjectLabel">"#1234 기준" 또는 "명단 재생성 전 예상".</param>
/// <param name="Alternatives">같은 직업의 다른 개체 (앞 20명).</param>
/// <param name="PoiIds">POI code → 문자열 id. 화면이 MasterDataSet 을 들지 않게 한다.</param>
/// <param name="ItemNames">아이템 code → 한국어 이름.</param>
public sealed record StudioForecast(
    Forecast Forecast,
    ImmutableArray<StudioPoiChoice> ZonePois,
    string ViewBox,
    string Keyframes,
    ImmutableArray<(Situation Situation, Reaction Reaction)> Reactions,
    Lexicon.ArchetypeGroup Group,
    string SubjectLabel,
    ImmutableArray<int> Alternatives,
    ImmutableDictionary<int, string> PoiIds,
    ImmutableDictionary<int, string> ItemNames)
{
    /// <summary>직군 색.</summary>
    public string Color => Lexicon.ColorOf(Group);

    /// <summary>POI code → 문자열 id. 모르면 빈 문자열.</summary>
    public string PoiId(PoiId poi) => PoiIds.GetValueOrDefault(poi.Value, string.Empty);

    /// <summary>아이템 code → 한국어 이름.</summary>
    public string ItemName(ItemId item) => ItemNames.GetValueOrDefault(item.Value, string.Empty);
}

/// <summary>
/// 예측을 화면이 쓸 모양으로 바꾼다 (T24).
///
/// <b>재생은 클라이언트에서 한다.</b> Blazor Server 가 프레임마다 SignalR 을 타면
/// 20명이 동시에 보기만 해도 회선이 죽는다 — 서버는 키프레임을 한 번 주고,
/// <c>wwwroot/behavior-preview.js</c> 가 <c>requestAnimationFrame</c> 으로 움직인다.
/// </summary>
public sealed class StudioForecastService(StudioWorkspace workspace)
{
    /// <summary>지도 여백 비율. POI 가 테두리에 붙으면 인형이 잘린다.</summary>
    private const float MapPadding = 0.12f;

    /// <summary>아키타입(대표 개체) 또는 지정한 개체의 하루.</summary>
    /// <param name="archetypeId">직업 id.</param>
    /// <param name="npcId">개체 번호. 없으면 대표 개체.</param>
    /// <param name="bucket">버킷. 없으면 폴백 플랜의 버킷.</param>
    /// <param name="draft">저장 전 초안 정의 (T25). 없으면 저장된 정의.</param>
    public StudioForecast? Load(
        string archetypeId, int? npcId = null, BucketKey? bucket = null, ArchetypeDef? draft = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(archetypeId);

        return workspace.WithData((data, instances) =>
        {
            if (!data.Archetypes.TryGet(archetypeId, out ArchetypeDef stored))
            {
                return null;
            }

            ArchetypeDef def = draft ?? stored;

            ForecastSubject subject = npcId is { } id && instances is not null && Find(instances, id) is { } npc
                ? DayForecast.Of(data, npc) with { Archetype = def }
                : DayForecast.Representative(data, instances, stored.Code) with { Archetype = def };

            if (data.Fallbacks?.For(stored.Code) is not { } plan)
            {
                return null;
            }

            BucketKey key = bucket ?? plan.Bucket;
            Forecast forecast = DayForecast.Run(data, plan, subject, key);

            return Build(data, instances, forecast, subject, def);
        });
    }

    /// <summary>미리 구운 플랜 하나로 재생한다 (T27).</summary>
    /// <param name="archetypeId">직업 id.</param>
    /// <param name="plan">컴파일된 플랜.</param>
    /// <param name="bucket">그 플랜의 버킷.</param>
    /// <param name="npcId">개체 번호. 없으면 대표 개체.</param>
    public StudioForecast? LoadPlan(string archetypeId, CompiledPlan plan, BucketKey bucket, int? npcId = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(archetypeId);
        ArgumentNullException.ThrowIfNull(plan);

        return workspace.WithData((data, instances) =>
        {
            if (!data.Archetypes.TryGet(archetypeId, out ArchetypeDef def))
            {
                return null;
            }

            ForecastSubject subject = npcId is { } id && instances is not null && Find(instances, id) is { } npc
                ? DayForecast.Of(data, npc)
                : DayForecast.Representative(data, instances, def.Code);

            return Build(data, instances, DayForecast.Run(data, plan, subject, bucket), subject, def);
        });
    }

    private StudioForecast Build(
        MasterDataSet data,
        NpcInstanceTable? instances,
        Forecast forecast,
        in ForecastSubject subject,
        ArchetypeDef def)
    {
        ImmutableArray<StudioPoiChoice> pois = PoisOf(data, subject);
        var reactions = ImmutableArray.CreateBuilder<(Situation, Reaction)>(ReactionForecast.Presets.Length);

        foreach (Situation situation in ReactionForecast.Presets)
        {
            reactions.Add((situation, ReactionForecast.Run(data, def, situation, subject, subject.Home)));
        }

        return new StudioForecast(
            forecast,
            pois,
            ViewBoxOf(pois),
            KeyframesOf(data, forecast),
            reactions.ToImmutable(),
            Lexicon.Group(def.Id),
            subject.IsReal ? $"#{subject.NpcId} 기준" : "가상 개체 — 명단 재생성 전 예상",
            Alternatives(instances, def.Code),
            PoiIdsOf(data, forecast, reactions),
            ItemNamesOf(data, forecast));
    }

    /// <summary>예측·반응이 가리키는 POI 의 문자열 id. 지도가 이것으로 점을 찾는다.</summary>
    private static ImmutableDictionary<int, string> PoiIdsOf(
        MasterDataSet data, Forecast forecast, ImmutableArray<(Situation, Reaction)>.Builder reactions)
    {
        var map = ImmutableDictionary.CreateBuilder<int, string>();

        void Add(PoiId poi)
        {
            if (poi.Value != 0 && !map.ContainsKey(poi.Value))
            {
                map[poi.Value] = data.Pois[poi].Id;
            }
        }

        Add(forecast.Subject.Home);
        Add(forecast.Subject.Workplace);

        foreach (ForecastSegment segment in forecast.Segments)
        {
            Add(segment.From);
            Add(segment.To);
        }

        foreach ((Situation _, Reaction reaction) in reactions)
        {
            Add(reaction.TargetPoi);
        }

        return map.ToImmutable();
    }

    /// <summary>예측이 다루는 아이템 이름.</summary>
    private static ImmutableDictionary<int, string> ItemNamesOf(MasterDataSet data, Forecast forecast)
    {
        var map = ImmutableDictionary.CreateBuilder<int, string>();

        foreach (ForecastSegment segment in forecast.Segments)
        {
            foreach ((ItemId item, int _) in segment.Inventory)
            {
                if (item.Value != 0 && !map.ContainsKey(item.Value))
                {
                    map[item.Value] = Lexicon.Item(data.ItemName(item));
                }
            }
        }

        return map.ToImmutable();
    }

    private static NpcInstanceDef? Find(NpcInstanceTable instances, int id)
    {
        int index = id - 1;

        return (uint)index < (uint)instances.Count && instances[index].Id == id ? instances[index] : null;
    }

    private static ImmutableArray<int> Alternatives(NpcInstanceTable? instances, ArchetypeId archetype) =>
        instances is null
            ? []
            : [.. instances.Instances.Where(n => n.Archetype == archetype).Take(20).Select(n => n.Id)];

    private static ImmutableArray<StudioPoiChoice> PoisOf(MasterDataSet data, in ForecastSubject subject)
    {
        PoiId home = subject.Home;
        PoiId workplace = subject.Workplace;

        return
        [
            .. data.Pois.InZone(subject.Zone)
                .Select(id => data.Pois[id])
                .OrderBy(p => p.Type)
                .ThenBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => new StudioPoiChoice(
                    p.Id,
                    Label(p.Type),
                    p.Subtype,
                    p.Capacity,
                    p.Pos.X,
                    p.Pos.Z,
                    p.Code == home,
                    p.Code == workplace)
                {
                    Kind = p.Type,
                    Label = Lexicon.PlaceName(p),
                }),
        ];
    }

    /// <summary>
    /// 지도 범위. POI 좌표의 최소·최대에 여백을 더한다 — <b>좌표는 지역 안에서만 실제다</b>
    /// (지역끼리 합칠 절대 좌표가 없다). 그래서 지도는 언제나 한 지역만 그린다.
    /// </summary>
    public static string ViewBoxOf(ImmutableArray<StudioPoiChoice> pois)
    {
        if (pois.IsDefaultOrEmpty)
        {
            return "0 0 100 100";
        }

        float minX = pois.Min(p => p.X);
        float maxX = pois.Max(p => p.X);
        float minZ = pois.Min(p => p.Z);
        float maxZ = pois.Max(p => p.Z);

        float width = Math.Max(maxX - minX, 1);
        float height = Math.Max(maxZ - minZ, 1);
        float pad = Math.Max(width, height) * MapPadding;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{minX - pad:0.##} {minZ - pad:0.##} {width + (pad * 2):0.##} {height + (pad * 2):0.##}");
    }

    /// <summary>
    /// 키프레임. 세그먼트 경계마다 하나씩, 이동은 시작·끝 둘이다 — 그 사이는 JS 가 보간한다.
    /// </summary>
    private static string KeyframesOf(MasterDataSet data, Forecast forecast)
    {
        var sb = new StringBuilder(4 * 1024);

        sb.Append("{\"duration\":").Append(Math.Max(1, forecast.CoveredSeconds))
          .Append(",\"startClock\":").Append(forecast.StartClockSeconds)
          .Append(",\"frames\":[");

        for (int i = 0; i < forecast.Segments.Length; i++)
        {
            ForecastSegment segment = forecast.Segments[i];

            Frame(sb, data, forecast, segment, i, segment.StartSeconds, start: true);

            if (segment.Kind == SegmentKind.Move && segment.Seconds > 0)
            {
                Frame(sb, data, forecast, segment, i, segment.EndSeconds, start: false);
            }
        }

        sb.Append("]}");

        return sb.ToString();
    }

    private static void Frame(
        StringBuilder sb,
        MasterDataSet data,
        Forecast forecast,
        in ForecastSegment segment,
        int index,
        int seconds,
        bool start)
    {
        PoiId at = start && segment.Kind == SegmentKind.Move ? segment.From : segment.To;

        if (at.Value == 0)
        {
            at = segment.To.Value != 0 ? segment.To : forecast.Subject.Home;
        }

        if (at.Value == 0)
        {
            return;
        }

        WorldPos pos = data.Pois[at].Pos;

        if (sb[^1] != '[')
        {
            sb.Append(',');
        }

        sb.Append(CultureInfo.InvariantCulture, $"{{\"t\":{seconds},\"x\":{pos.X:0.##},\"z\":{pos.Z:0.##}")
          .Append(",\"seg\":").Append(index)
          .Append(",\"state\":\"").Append(segment.Kind)
          .Append("\",\"caption\":").Append(JsonSerializer.Serialize(segment.Caption, JsonSurgeonText.Options))
          .Append('}');
    }

    private static string Label(PoiType type) => type switch
    {
        PoiType.Home => "집",
        PoiType.Workplace => "일터",
        PoiType.Market => "시장",
        PoiType.Tavern => "선술집",
        PoiType.Temple => "신전",
        PoiType.Gate => "성문",
        PoiType.Field => "농경지",
        PoiType.Wilderness => "야외",
        _ => type.ToString(),
    };
}
