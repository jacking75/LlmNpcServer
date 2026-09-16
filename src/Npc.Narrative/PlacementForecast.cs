using System.Collections.Immutable;
using Npc.Contracts;
using Npc.MasterData;

namespace Npc.Narrative;

/// <summary>
/// 명단을 다시 만들면 이 직업이 어느 지역에 몇 명 살게 되는가 (T33).
///
/// <para>
/// <b>새 직업은 명단이 없다.</b> 만들고 나서 "그래서 몇 명이 어디에 생기나" 를 모르면
/// 파생물을 재생성해 보기 전까지 아무것도 확인할 수 없다. <c>gen_npcs</c> 는 일터 가까운 집을
/// 먼저 주므로, <b>일터 subtype 의 지역별 정원 비례</b>가 그 배치의 근사다.
/// </para>
///
/// <para>일터가 없는 직업은 지역 정원 비례로 나눈다. 합은 <b>언제나</b> 인구와 같다 (최대잔여법).</para>
/// </summary>
public static class PlacementForecast
{
    /// <summary>지역별 예상 인원 (인원 내림차순, 같으면 zone code 오름차순).</summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="def">아키타입 정의.</param>
    /// <param name="population">나눌 인구.</param>
    public static ImmutableArray<(ZoneId Zone, int Count)> ByZone(
        MasterDataSet data, ArchetypeDef def, int population)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(def);

        if (population <= 0)
        {
            return [];
        }

        var weights = new Dictionary<ZoneId, double>();

        if (def.WorkplacePoiType is { Length: > 0 } subtype)
        {
            foreach (PoiId poi in data.Pois.OfSubtype(subtype))
            {
                PoiDef site = data.Pois[poi];

                if (!site.CanEnter(def.Code))
                {
                    continue;
                }

                weights[site.Zone] = weights.GetValueOrDefault(site.Zone) + site.Capacity;
            }
        }

        if (weights.Count == 0)
        {
            foreach (ZoneDef zone in data.Zones.Zones)
            {
                if (zone.Capacity > 0)
                {
                    weights[zone.Code] = zone.Capacity;
                }
            }
        }

        if (weights.Count == 0)
        {
            return [];
        }

        double total = weights.Values.Sum();

        // Dictionary 순회에 기대지 않는다 (CLAUDE.md §2.3) — code 오름차순으로 고정한다.
        ImmutableArray<ZoneId> zones = [.. weights.Keys.OrderBy(z => z.Value)];
        var exact = new double[zones.Length];
        var counts = new int[zones.Length];
        int assigned = 0;

        for (int i = 0; i < zones.Length; i++)
        {
            exact[i] = weights[zones[i]] / total * population;
            counts[i] = (int)Math.Floor(exact[i]);
            assigned += counts[i];
        }

        // 최대잔여법. 합이 정확히 인구가 되어야 한다 — 반올림으로 한 명이 사라지면 표가 거짓말을 한다.
        foreach (int index in Enumerable.Range(0, zones.Length)
            .OrderByDescending(i => exact[i] - counts[i])
            .ThenBy(i => zones[i].Value)
            .Take(Math.Max(0, population - assigned)))
        {
            counts[index]++;
        }

        return
        [
            .. Enumerable.Range(0, zones.Length)
                .Where(i => counts[i] > 0)
                .OrderByDescending(i => counts[i])
                .ThenBy(i => zones[i].Value)
                .Select(i => (zones[i], counts[i])),
        ];
    }
}
