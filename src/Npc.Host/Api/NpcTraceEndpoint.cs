using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Host.Api;

/// <summary>NPC 추적의 스텝 한 줄. docs/14 §8.</summary>
/// <param name="Index">플랜 안의 몇 번째 스텝인가.</param>
/// <param name="Action">액션 id.</param>
/// <param name="Poi">POI 심볼 이름. 없으면 빈 문자열.</param>
/// <param name="Count">수량 인자.</param>
/// <param name="TimeoutSeconds">이 스텝의 timeout_s.</param>
/// <param name="Current">지금 실행 중인 스텝인가.</param>
public readonly record struct TraceStep(
    int Index,
    string Action,
    string Poi,
    int Count,
    int TimeoutSeconds,
    bool Current);

/// <summary>NPC 추적의 최근 사건 한 줄.</summary>
/// <param name="Kind">이벤트 종류.</param>
/// <param name="At">언제(틱).</param>
/// <param name="AgoTicks">몇 틱 전인가.</param>
/// <param name="Subject">누가/무엇이. Kind 에 따라 뜻이 다르다 (docs/11 §3).</param>
/// <param name="Salience">중요도.</param>
public readonly record struct TraceEvent(
    string Kind,
    long At,
    long AgoTicks,
    int Subject,
    byte Salience);

/// <summary>
/// NPC 한 마리의 현재 상태. docs/14 §8 "NPC 추적" 패널.
///
/// <b>데모에서 제일 설득력 있는 패널이다</b> — 대장장이 한 마리를 찍어놓고 하루가 흘러가는 걸
/// 보여주는 것이 숫자 표보다 강하다.
/// </summary>
/// <param name="Found">그 첨자에 NPC 가 있는가.</param>
/// <param name="Npc">NPC 첨자.</param>
/// <param name="Tick">스냅샷을 뜬 틱.</param>
/// <param name="Archetype">아키타입 id.</param>
/// <param name="Zone">현재 존 id.</param>
/// <param name="Poi">현재 POI id.</param>
/// <param name="HomePoi">자택 POI id.</param>
/// <param name="WorkPoi">일터 POI id. 없으면 빈 문자열.</param>
/// <param name="Lod">인지 LOD 등급.</param>
/// <param name="Hp">HP.</param>
/// <param name="Stamina">스태미나.</param>
/// <param name="StepStatus">스텝 실행 상태.</param>
/// <param name="PlanId">현재 플랜 id. 음수면 개별 풀 슬롯이다 (docs/13 §2).</param>
/// <param name="PlanKind">플랜 출처 — <c>bucket</c>·<c>individual</c>·<c>fallback</c>.</param>
/// <param name="PlanGoal">플랜의 goal.</param>
/// <param name="PlanBucket">플랜이 만들어진 버킷 표기.</param>
/// <param name="PlanLoop">플랜이 순환하는가.</param>
/// <param name="PlanAgeTicks">이 플랜을 쓴 지 몇 틱 됐나.</param>
/// <param name="PendingPlanId">워커가 걸어 둔 새 플랜. 0 이면 없음.</param>
/// <param name="PendingUrgency">인터럽트가 남긴 긴급도.</param>
/// <param name="Flags">참인 월드 플래그 이름들.</param>
/// <param name="Inventory">0 이 아닌 인벤토리 항목.</param>
/// <param name="Steps">플랜의 스텝.</param>
/// <param name="Recent">최근 사건 (RingBuffer8).</param>
/// <param name="PatrolRoute">순찰 지점 이름 (D-04). 비어 있으면 순찰로가 없다.</param>
/// <param name="Faction">이 NPC 의 세력 id (D-04). 빈 문자열이면 미지정.</param>
/// <param name="AggroRadiusM">경계 반경 m (D-04). 0 이면 아키타입 기본값.</param>
/// <param name="ScheduleOffsetMin">시간대 전환 오프셋, 게임 분 (D-04).</param>
public readonly record struct NpcTrace(
    bool Found,
    int Npc,
    long Tick,
    string Archetype,
    string Zone,
    string Poi,
    string HomePoi,
    string WorkPoi,
    byte Lod,
    short Hp,
    short Stamina,
    string StepStatus,
    int PlanId,
    string PlanKind,
    string PlanGoal,
    string PlanBucket,
    bool PlanLoop,
    long PlanAgeTicks,
    int PendingPlanId,
    byte PendingUrgency,
    string[] Flags,
    string[] Inventory,
    TraceStep[] Steps,
    TraceEvent[] Recent,
    string[] PatrolRoute,
    string Faction,
    ushort AggroRadiusM,
    short ScheduleOffsetMin);

/// <summary>
/// <c>GET /npc/{id}</c>. docs/14 §8 · T4-21.
///
/// <b>틱 루프를 막지 않는다.</b> SoA 배열을 <b>읽기만</b> 하고 값을 복사해 나간다 —
/// 락도 없고 쓰기도 없다. 한 틱 낡은 값을 볼 수 있지만 추적 패널에는 그것으로 충분하다
/// (다음 폴링에 새 값이 온다). 락을 잡으면 그 자체가 틱 예산을 먹는다 (CLAUDE.md §2.1).
///
/// <b>개별 플랜은 LRU 를 갱신하지 않고 본다</b>(<c>TryPeekFor</c>) — 조회만으로 회수 순서가
/// 바뀌면 대시보드를 열어 둔 것만으로 게임 상태가 달라진다 (T4-12).
/// </summary>
internal static class NpcTraceEndpoint
{
    /// <summary>라우트 경로.</summary>
    public const string Route = "/npc/{id:int}";

    /// <summary>한 NPC 의 상태를 뜬다. 없는 첨자면 <c>Found=false</c>.</summary>
    public static NpcTrace Snapshot(
        int npc, NpcStore store, PlanStore plans, MasterDataSet data, Tick now)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(data);

        if ((uint)npc >= (uint)store.Count)
        {
            return new NpcTrace { Found = false, Npc = npc, Tick = now.Value };
        }

        int planId = Volatile.Read(ref store.PlanId[npc]);
        bool live = plans.TryPeekFor(npc, planId, out CompiledPlan plan);
        var status = (Runtime.StepStatus)store.StepStatus[npc];
        int step = plan.Steps.IsEmpty ? -1 : Math.Min(store.StepIndex[npc], plan.Steps.Length - 1);

        return new NpcTrace(
            Found: true,
            Npc: npc,
            Tick: now.Value,
            Archetype: data.Archetypes[new ArchetypeId(store.ArchetypeCode[npc])].Id,
            Zone: ZoneNameOf(data, store.ZoneCode[npc]),
            Poi: PoiNameOf(data, store.CurrentPoi[npc]),
            HomePoi: PoiNameOf(data, store.HomePoi[npc]),
            WorkPoi: PoiNameOf(data, store.WorkPoi[npc]),
            Lod: store.Lod[npc],
            Hp: store.Hp[npc],
            Stamina: store.Stamina[npc],
            StepStatus: status.ToString(),
            PlanId: planId,
            PlanKind: PlanKindOf(planId, live, plan),
            PlanGoal: plan.Goal,
            PlanBucket: plan.Bucket.A.Value == 0 && plan.Bucket.T == default
                ? string.Empty
                : plan.Bucket.Format(data.Archetypes[plan.Bucket.A].Id),
            PlanLoop: plan.Loop,
            PlanAgeTicks: Math.Max(0, now.Value - store.PlanAssignedTick[npc]),
            PendingPlanId: Volatile.Read(ref store.PendingPlanId[npc]),
            PendingUrgency: store.PendingUrgency[npc],
            Flags: WorldFlagTable.Format(store.Flags[npc]).Split(
                '|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Inventory: InventoryOf(store, data, npc),
            Steps: StepsOf(plan, data, step),
            Recent: RecentOf(store, npc, now),

            // D-04 — 개체별 행동 파라미터. 여기가 유일한 조회 창구다.
            PatrolRoute: PatrolOf(store, data, npc),
            Faction: data.Factions?.NameOf(new FactionId(store.Faction[npc])) ?? string.Empty,
            AggroRadiusM: store.AggroRadiusM[npc],
            ScheduleOffsetMin: store.ScheduleOffsetMin[npc]);
    }

    /// <summary>이 NPC 의 순찰 지점 이름들 (D-04). 순찰로가 없으면 빈 배열.</summary>
    private static string[] PatrolOf(NpcStore store, MasterDataSet data, int npc)
    {
        int count = store.PatrolCount[npc];

        if (count == 0)
        {
            return [];
        }

        Span<ushort> route = store.PatrolRouteOf(npc);
        var names = new string[count];

        for (int i = 0; i < count; i++)
        {
            names[i] = PoiNameOf(data, route[i]);
        }

        return names;
    }

    /// <summary>
    /// 플랜 출처. 음수 id 는 개별 풀이고, 그 슬롯이 회수됐으면 폴백으로 되돌아간 상태다.
    /// </summary>
    private static string PlanKindOf(int planId, bool live, CompiledPlan plan)
    {
        if (IndividualPlanPool.IsIndividual(planId))
        {
            return live ? "individual" : "individual(회수됨)";
        }

        return plan.Origin == PlanOrigin.Fallback ? "fallback" : "bucket";
    }

    private static string ZoneNameOf(MasterDataSet data, ushort code)
    {
        if (code == 0)
        {
            return string.Empty;
        }

        foreach (ZoneDef zone in data.Zones.Zones)
        {
            if (zone.Code.Value == code)
            {
                return zone.Id;
            }
        }

        return code.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string PoiNameOf(MasterDataSet data, ushort code) =>
        code == 0 ? string.Empty : data.Pois[new PoiId(code)].Id;

    private static string[] InventoryOf(NpcStore store, MasterDataSet data, int npc)
    {
        var items = new List<string>(8);
        ReadOnlySpan<int> inventory = store.ReadInventoryOf(npc);

        for (int slot = 0; slot < inventory.Length; slot++)
        {
            if (inventory[slot] > 0)
            {
                items.Add($"{data.Items[new ItemId((ushort)slot)].Id} × {inventory[slot]}");
            }
        }

        return [.. items];
    }

    private static TraceStep[] StepsOf(CompiledPlan plan, MasterDataSet data, int current)
    {
        var steps = new TraceStep[plan.Steps.Length];

        for (int i = 0; i < plan.Steps.Length; i++)
        {
            CompiledStep step = plan.Steps[i];

            steps[i] = new TraceStep(
                i,
                data.ActionName(step.Action),
                step.Poi == PoiSymbol.None ? string.Empty : step.Poi.ToString(),
                step.Count,
                step.TimeoutSeconds,
                i == current);
        }

        return steps;
    }

    private static TraceEvent[] RecentOf(NpcStore store, int npc, Tick now)
    {
        // 구조체 링 버퍼는 값 복사로 읽는다 — 틱 루프가 그 사이에 밀어 넣어도 우리 사본은 온전하다.
        RingBuffer8<Runtime.RecentEvent> ring = store.Recent[npc];
        var events = new TraceEvent[ring.Count];

        for (int i = 0; i < ring.Count; i++)
        {
            Runtime.RecentEvent recent = ring[i];

            events[i] = new TraceEvent(
                recent.Kind.ToString(),
                recent.At.Value,
                Math.Max(0, now.Value - recent.At.Value),
                recent.Subject,
                recent.Salience);
        }

        // 최근 것이 위로. 삽입 순서가 아니라 시각 기준이다 — 링은 salience 로 밀어내므로
        // 슬롯 순서가 곧 시간 순서가 아니다 (docs/11 §3).
        Array.Sort(events, (a, b) => b.At.CompareTo(a.At));

        return events;
    }
}
