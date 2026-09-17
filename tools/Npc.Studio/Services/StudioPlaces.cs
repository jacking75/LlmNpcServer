using System.Collections.Immutable;
using System.Globalization;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.Narrative;

namespace Npc.Studio.Services;

/// <summary>지역 하나의 요약 (T08·T26).</summary>
/// <param name="Id">지역 id.</param>
/// <param name="Name">표시 이름.</param>
/// <param name="Code">지역 번호.</param>
/// <param name="PoiCount">장소 수.</param>
/// <param name="Capacity">장소 정원 합.</param>
/// <param name="Residents">여기 사는 NPC 수.</param>
/// <param name="Workers">여기서 일하는 NPC 수.</param>
/// <param name="Adjacent">이웃 지역 id.</param>
/// <param name="Groups">거주자의 직군 분포 (직군 → 인원).</param>
public sealed record StudioZone(
    string Id,
    string Name,
    int Code,
    int PoiCount,
    int Capacity,
    int Residents,
    int Workers,
    ImmutableArray<string> Adjacent,
    ImmutableArray<(Lexicon.ArchetypeGroup Group, int Count)> Groups);

/// <summary>장소 하나의 상세 (T08).</summary>
/// <param name="Poi">POI 선택기와 같은 모양.</param>
/// <param name="Code">장소 번호.</param>
/// <param name="OpenFrom">여는 시간대.</param>
/// <param name="OpenTo">닫는 시간대.</param>
/// <param name="Grants">도착하면 서는 상태.</param>
/// <param name="AllowedArchetypes">일할 수 있는 직업 (비면 제한 없음).</param>
/// <param name="Resources">얻을 수 있는 것.</param>
/// <param name="Residents">여기 사는 NPC 수.</param>
/// <param name="Workers">여기서 일하는 NPC 수.</param>
public sealed record StudioPlace(
    StudioPoiChoice Poi,
    int Code,
    string OpenFrom,
    string OpenTo,
    string Grants,
    ImmutableArray<string> AllowedArchetypes,
    ImmutableArray<string> Resources,
    int Residents,
    int Workers);

/// <summary>마을 지도 타일 하나 (T26).</summary>
/// <param name="Zone">지역 요약.</param>
/// <param name="Column">격자 열.</param>
/// <param name="Row">격자 행.</param>
public sealed record ZoneTile(StudioZone Zone, int Column, int Row);

/// <summary>새 장소 초안 (T13).</summary>
/// <param name="Id">장소 id.</param>
/// <param name="Zone">지역 id.</param>
/// <param name="Type">유형.</param>
/// <param name="Subtype">세부 유형.</param>
/// <param name="X">좌표 X.</param>
/// <param name="Z">좌표 Z.</param>
/// <param name="Capacity">정원.</param>
/// <param name="OpenFrom">여는 시간대.</param>
/// <param name="OpenTo">닫는 시간대.</param>
/// <param name="AllowedArchetypes">일할 수 있는 직업.</param>
/// <param name="Resources">얻을 수 있는 것.</param>
public sealed record StudioNewPlace(
    string Id,
    string Zone,
    string Type,
    string Subtype,
    float X,
    float Z,
    int Capacity,
    string OpenFrom,
    string OpenTo,
    ImmutableArray<string> AllowedArchetypes,
    ImmutableArray<string> Resources)
{
    /// <summary>
    /// 로케일 → 세부 유형의 표시 이름 (V14 · H18).
    ///
    /// <b>처음 보는 세부 유형일 때만 쓴다.</b> <c>poi.&lt;subtype&gt;</c> 키는 로케일마다 있어야 하고,
    /// 없으면 화면이 키를 그대로 보여 준다 — 장소를 만든 자리에서 같이 넣지 않으면
    /// 나중에 "표시 이름이 빠졌다" 만 남는다.
    /// </summary>
    public ImmutableArray<(string Locale, string Name)> Names { get; init; } = [];
}

/// <summary>
/// 지역·장소 읽기와 새 장소 추가 (T08·T13·T26).
///
/// <b>좌표는 지역 안에서만 실제다.</b> <c>pois.json</c> 의 <c>pos</c> 는 존 중심 기준 배치라
/// 지역끼리 합칠 절대 좌표가 없다 — 마을 지도는 <b>타일</b>로 배치한다 (T26).
/// </summary>
public sealed class StudioPlaces(StudioWorkspace workspace)
{
    /// <summary>지역 목록 (code 오름차순).</summary>
    public ImmutableArray<StudioZone> Zones() =>
        workspace.WithData<ImmutableArray<StudioZone>>((data, instances) =>
            [.. data.Zones.Zones.Select(zone => ZoneOf(data, instances, zone))]);

    /// <summary>지역 하나. 없으면 null.</summary>
    /// <param name="zoneId">지역 id.</param>
    public StudioZone? Zone(string zoneId) =>
        workspace.WithData((data, instances) =>
            data.Zones.TryGet(zoneId, out ZoneDef zone) ? ZoneOf(data, instances, zone) : null);

    /// <summary>이 지역의 장소 (유형·id 순).</summary>
    /// <param name="zoneId">지역 id.</param>
    public ImmutableArray<StudioPlace> Places(string zoneId) =>
        workspace.WithData((data, instances) =>
        {
            if (!data.Zones.TryGet(zoneId, out ZoneDef zone))
            {
                return ImmutableArray<StudioPlace>.Empty;
            }

            return
            [
                .. data.Pois.InZone(zone.Code)
                    .Select(id => data.Pois[id])
                    .OrderBy(p => p.Type)
                    .ThenBy(p => p.Id, StringComparer.Ordinal)
                    .Select(p => PlaceOf(data, instances, p)),
            ];
        });

    /// <summary>이 지역에 사는 NPC 의 집 좌표와 직군 (T08 — "어디에 누가 사는가").</summary>
    /// <param name="zoneId">지역 id.</param>
    /// <param name="limit">최대 인원. 5,000명을 다 그리면 지도가 검어진다.</param>
    public ImmutableArray<(int Id, float X, float Z, Lexicon.ArchetypeGroup Group)> Residents(
        string zoneId, int limit = 120) =>
        workspace.WithData((data, instances) =>
        {
            if (instances is null || !data.Zones.TryGet(zoneId, out ZoneDef zone))
            {
                return ImmutableArray<(int, float, float, Lexicon.ArchetypeGroup)>.Empty;
            }

            return
            [
                .. instances.Instances
                    .Where(n => n.Zone == zone.Code)
                    .Take(limit)
                    .Select(n => (
                        n.Id,
                        data.Pois[n.Home].Pos.X,
                        data.Pois[n.Home].Pos.Z,
                        Lexicon.Group(data.Archetypes[n.Archetype].Id))),
            ];
        });

    /// <summary>
    /// 마을 지도 타일 배치 (T26). <c>zones.json</c> 의 code 순 격자다 —
    /// <b>결정론이면 어느 배치든 된다</b>. 같은 파일이면 같은 자리다.
    /// </summary>
    /// <param name="columns">한 줄에 몇 개.</param>
    public ImmutableArray<ZoneTile> Tiles(int columns = 4)
    {
        ImmutableArray<StudioZone> zones = Zones();
        var tiles = ImmutableArray.CreateBuilder<ZoneTile>(zones.Length);

        for (int i = 0; i < zones.Length; i++)
        {
            tiles.Add(new ZoneTile(zones[i], i % columns, i / columns));
        }

        return tiles.ToImmutable();
    }

    /// <summary>새 장소의 id 제안 (T13). <c>{subtype}_{NNN}_{zoneCode:00}</c>.</summary>
    /// <param name="subtype">세부 유형.</param>
    /// <param name="zoneId">지역 id.</param>
    public string SuggestId(string subtype, string zoneId) =>
        workspace.WithData((data, _) =>
        {
            if (!data.Zones.TryGet(zoneId, out ZoneDef zone))
            {
                return subtype + "_001_00";
            }

            int next = 1 + data.Pois.Pois.Count(p =>
                string.Equals(p.Subtype, subtype, StringComparison.Ordinal) && p.Zone == zone.Code);

            return string.Create(
                CultureInfo.InvariantCulture, $"{subtype}_{next:000}_{zone.Code.Value:00}");
        });

    /// <summary>
    /// 새 장소를 넣는다 (T13). <c>code</c> 는 <see cref="CodeAllocator"/> 가 정한다 —
    /// <b>눈으로 세어 다음 번호를 정하면 중복·예약 구간 침범이 난다.</b>
    /// </summary>
    /// <param name="draft">초안.</param>
    public StudioSaveResult AddPlace(StudioNewPlace draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return workspace.AppendPoi(draft);
    }

    private static StudioZone ZoneOf(MasterDataSet data, NpcInstanceTable? instances, ZoneDef zone)
    {
        ImmutableArray<PoiId> pois = data.Pois.InZone(zone.Code);
        var groups = new Dictionary<Lexicon.ArchetypeGroup, int>();
        int residents = 0;
        int workers = 0;

        if (instances is not null)
        {
            foreach (NpcInstanceDef npc in instances.Instances)
            {
                if (npc.Zone == zone.Code)
                {
                    residents++;

                    Lexicon.ArchetypeGroup group = Lexicon.Group(data.Archetypes[npc.Archetype].Id);
                    groups[group] = groups.GetValueOrDefault(group) + 1;
                }

                if (npc.Workplace != default && data.Pois[npc.Workplace].Zone == zone.Code)
                {
                    workers++;
                }
            }
        }

        return new StudioZone(
            zone.Id,
            Lexicon.Zone(data, zone.Id),
            zone.Code.Value,
            pois.Length,
            pois.Sum(p => data.Pois[p].Capacity),
            residents,
            workers,
            [.. zone.Adjacent.Select(a => data.Zones[a].Id)],
            [.. groups.OrderByDescending(g => g.Value).ThenBy(g => (int)g.Key).Select(g => (g.Key, g.Value))]);
    }

    private static StudioPlace PlaceOf(MasterDataSet data, NpcInstanceTable? instances, PoiDef poi)
    {
        int residents = instances?.Instances.Count(n => n.Home == poi.Code) ?? 0;
        int workers = instances?.Instances.Count(n => n.Workplace == poi.Code) ?? 0;

        return new StudioPlace(
            new StudioPoiChoice(
                poi.Id, PoiTypeLabel(poi.Type), poi.Subtype, poi.Capacity, poi.Pos.X, poi.Pos.Z, false, false)
            {
                Kind = poi.Type,
                Label = Lexicon.PlaceName(poi),
            },
            poi.Code.Value,
            Lexicon.Of(poi.OpenFrom),
            Lexicon.Of(poi.OpenTo),
            WorldFlagTable.Format(poi.Grants),
            [
                .. data.Archetypes.Archetypes
                    .Where(a => poi.AllowedArchetypeMask != 0 && poi.Allows(a.Code))
                    .Select(a => Lexicon.Archetype(a.Id)),
            ],
            [.. poi.Resources.Select(r => Lexicon.Item(data.Items[r].Id))],
            residents,
            workers);
    }

    private static string PoiTypeLabel(PoiType type) => type switch
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
