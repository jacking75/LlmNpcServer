using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Runtime;

/// <summary>
/// 심볼 바인딩에 필요한 개체 정보. docs/03 §2.
/// 값 타입이라 바인딩 경로에 할당이 없다.
/// </summary>
/// <param name="Npc">NPC. 지터의 씨앗이다.</param>
/// <param name="Archetype">아키타입. field POI 접근 허가를 본다.</param>
/// <param name="Home">인스턴스의 home_poi.</param>
/// <param name="Workplace">인스턴스의 workplace_poi. 없으면 default.</param>
/// <param name="Current">현재 위치 POI. 거리 계산의 기준이다.</param>
/// <param name="Patrol">
/// 이번 스텝의 순찰 지점 (D-04). 0 이면 이 NPC 에게 순찰로가 없다.
///
/// <b>지점 고르기는 여기서 하지 않는다.</b> 몇 번째 지점인가는 스텝 번호가 정하고,
/// 그것을 아는 것은 발행기다 (<c>PlanExecutor.ContextOf</c>) — 바인더는 받은 값을 쓴다.
/// </param>
public readonly record struct PoiBindContext(
    NpcId Npc,
    ArchetypeId Archetype,
    PoiId Home,
    PoiId Workplace,
    PoiId Current,
    PoiId Patrol = default);

/// <summary>
/// 플랜의 POI 심볼을 개체별 실제 POI 로 바인딩한다. docs/03 §2 · docs/11 §12.
///
/// <b>가장 가까운 하나를 그대로 고르지 않는다.</b> 그러면 같은 버킷의 NPC 수백 마리가
/// 전부 같은 POI 로 몰린다(docs/11 §12 의 첫 번째 증상). 가까운 후보 몇 개 중에서
/// <c>npcId</c> 해시로 고른다 — 난수가 아니라 해시라서 리플레이가 일치한다.
/// </summary>
public sealed class PoiBinder
{
    /// <summary>지터가 고르는 후보 수. 가까운 순으로 이만큼 추린 뒤 해시로 하나를 뽑는다.</summary>
    public const int CandidateCount = 4;

    /// <summary>실내로 판정하는 POI 타입. <c>$nearest_shelter</c> 가 쓴다.</summary>
    private static readonly PoiType[] s_shelterTypes =
        [PoiType.Home, PoiType.Tavern, PoiType.Temple, PoiType.Workplace, PoiType.Market];

    /// <summary>안전 지점으로 판정하는 POI 타입. <c>$nearest_safe</c> 가 쓴다.</summary>
    private static readonly PoiType[] s_safeTypes = [PoiType.Gate, PoiType.Home];

    private readonly PoiTable _pois;

    /// <summary>POI 표를 물고 있는 바인더. 기동 시 1회 만들고 이후 불변이다.</summary>
    public PoiBinder(PoiTable pois) => _pois = pois;

    /// <summary>
    /// 이 샤드가 맡는 존 비트마스크 (A-08). <b>0 = 전체</b>라 단일 샤드는 오늘과 같다.
    ///
    /// <para>
    /// <b>1단계에서 NPC 는 자기 샤드의 존 안에서만 산다.</b> 마스크가 없으면
    /// <c>$nearest_market</c> 이 옆 샤드의 시장을 고르고, 그 NPC 는 우리가 이벤트를 받지 못하는
    /// 곳으로 걸어가 <c>timeout_s</c> 가 만료될 때까지 멈춘다 — 증상은 "가끔 NPC 가 굳는다" 다.
    /// </para>
    ///
    /// <para>
    /// 기동 시 한 번 쓰고 이후 읽기만 한다. 후보 판정은 비트 연산 하나라 <b>할당 0</b> 이다.
    /// </para>
    /// </summary>
    public ulong ZoneMask { get; init; }

    /// <summary>심볼 → 실제 POI. 바인딩에 실패하면 false (런타임은 ActionFailed(Unreachable) 로 간다).</summary>
    public bool TryBind(PoiSymbol symbol, in PoiBindContext ctx, out PoiId poi)
    {
        switch (symbol)
        {
            case PoiSymbol.None:
                poi = default;
                return false;

            case PoiSymbol.Home:
                poi = ctx.Home;
                return poi.Value != 0;

            case PoiSymbol.Workplace:
                poi = ctx.Workplace;
                return poi.Value != 0;

            case PoiSymbol.Market:
                return TryNearestOfType(PoiType.Market, ctx, symbol, out poi);

            case PoiSymbol.Tavern:
                return TryNearestOfType(PoiType.Tavern, ctx, symbol, out poi);

            case PoiSymbol.Temple:
                return TryNearestOfType(PoiType.Temple, ctx, symbol, out poi);

            case PoiSymbol.Gate:
                return TryNearestOfType(PoiType.Gate, ctx, symbol, out poi);

            case PoiSymbol.NearestField:
                return TryNearestOfType(PoiType.Field, ctx, symbol, out poi);

            case PoiSymbol.NearestSafe:
                return TryNearestOfTypes(s_safeTypes, ctx, symbol, out poi);

            case PoiSymbol.NearestShelter:
                return TryNearestOfTypes(s_shelterTypes, ctx, symbol, out poi);

            // D-04 — 순찰로가 없는 NPC 가 이 심볼을 쓴 플랜을 받을 수 있다. 버킷 플랜은
            // 아키타입 단위라 "이 경비병에게는 순찰로가 있고 저 경비병에게는 없다" 를 모른다.
            // 그때 실패로 두면 위병 플랜 전체가 그 스텝에서 멈추므로 일터로 떨어뜨린다.
            case PoiSymbol.PatrolRoute:
                poi = ctx.Patrol.Value != 0 ? ctx.Patrol
                    : ctx.Workplace.Value != 0 ? ctx.Workplace
                    : ctx.Home;
                return poi.Value != 0;

            default:
                poi = default;
                return false;
        }
    }

    private bool TryNearestOfType(PoiType type, in PoiBindContext ctx, PoiSymbol symbol, out PoiId poi)
    {
        Span<PoiId> candidates = stackalloc PoiId[CandidateCount];
        Span<float> distances = stackalloc float[CandidateCount];
        int found = 0;

        Collect(_pois.OfType(type), ctx, candidates, distances, ref found);

        return Pick(candidates, found, ctx.Npc, symbol, out poi);
    }

    private bool TryNearestOfTypes(PoiType[] types, in PoiBindContext ctx, PoiSymbol symbol, out PoiId poi)
    {
        Span<PoiId> candidates = stackalloc PoiId[CandidateCount];
        Span<float> distances = stackalloc float[CandidateCount];
        int found = 0;

        foreach (PoiType type in types)
        {
            Collect(_pois.OfType(type), ctx, candidates, distances, ref found);
        }

        return Pick(candidates, found, ctx.Npc, symbol, out poi);
    }

    /// <summary>가까운 순으로 최대 <see cref="CandidateCount"/> 개를 추린다. 할당 0.</summary>
    private void Collect(
        ImmutableArray<PoiId> source,
        in PoiBindContext ctx,
        Span<PoiId> candidates,
        Span<float> distances,
        ref int found)
    {
        foreach (PoiId id in source)
        {
            PoiDef def = _pois[id];

            // 일터·채집지는 근무 허가를 본다. 시장·선술집·신전·성문은 공공장소라 누구나 간다.
            if (!def.CanEnter(ctx.Archetype))
            {
                continue;
            }

            // A-08 — 샤드 밖 POI 는 후보가 아니다. 마스크가 0 이면 전체라 이 검사는 언제나 통과다.
            if (!ShardTable.Covers(ZoneMask, def.Zone))
            {
                continue;
            }

            float distance = _pois.Distance(ctx.Current, id);

            if (float.IsInfinity(distance))
            {
                continue;
            }

            // 삽입 정렬. 후보가 4개뿐이라 이게 가장 싸다.
            int slot = found < CandidateCount ? found : CandidateCount - 1;

            if (found == CandidateCount && distance >= distances[slot])
            {
                continue;
            }

            while (slot > 0 && distances[slot - 1] > distance)
            {
                distances[slot] = distances[slot - 1];
                candidates[slot] = candidates[slot - 1];
                slot--;
            }

            distances[slot] = distance;
            candidates[slot] = id;

            if (found < CandidateCount)
            {
                found++;
            }
        }
    }

    /// <summary>후보 중 하나를 npcId 해시로 고른다. 난수를 쓰지 않으므로 리플레이가 일치한다.</summary>
    private static bool Pick(ReadOnlySpan<PoiId> candidates, int found, NpcId npc, PoiSymbol symbol, out PoiId poi)
    {
        if (found == 0)
        {
            poi = default;
            return false;
        }

        int index = (int)(PlanHash.Mix(npc.Value, (int)symbol) % (uint)found);
        poi = candidates[index];
        return true;
    }
}
