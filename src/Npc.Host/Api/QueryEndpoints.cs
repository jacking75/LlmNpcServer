using System.Globalization;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Host.Api;

/// <summary>
/// 벌크 조회 한 줄 (B-08). <b>전부 id·enum 이다</b> — 플레이어가 쓴 문자열은 없다 (§2.5).
/// </summary>
/// <param name="Npc">NPC 첨자.</param>
/// <param name="Archetype">아키타입 id.</param>
/// <param name="Zone">존 id.</param>
/// <param name="Poi">현재 POI id.</param>
/// <param name="Action">지금 스텝의 액션 id. 없으면 빈 문자열.</param>
/// <param name="Lod">인지 LOD 등급.</param>
/// <param name="StepStatus">스텝 실행 상태.</param>
/// <param name="PlanKind">플랜 출처 — <c>bucket</c>·<c>individual</c>·<c>fallback</c>.</param>
public readonly record struct NpcSummary(
    int Npc,
    string Archetype,
    string Zone,
    string Poi,
    string Action,
    byte Lod,
    string StepStatus,
    string PlanKind);

/// <summary>
/// <c>GET /npcs</c> 의 한 쪽 (B-08).
/// </summary>
/// <param name="Tick">스냅샷을 뜬 틱.</param>
/// <param name="Total">슬롯 총수.</param>
/// <param name="Matched">필터에 걸린 수. <b>이 쪽에 실린 수가 아니다.</b></param>
/// <param name="Returned">이 쪽에 실린 수.</param>
/// <param name="NextCursor">다음 쪽의 <c>cursor</c>. 마지막 쪽이면 null.</param>
/// <param name="Npcs">요약들.</param>
public readonly record struct NpcListPage(
    long Tick,
    int Total,
    int Matched,
    int Returned,
    int? NextCursor,
    NpcSummary[] Npcs);

/// <summary>
/// <c>GET /npc/{id}/context</c> — 대화 서비스(D-01)가 읽을 최소 맥락 (B-08).
///
/// <b>여기에 자연어를 담지 않는다.</b> 전부 id·enum·숫자다 — 이 응답이 그대로
/// 프롬프트에 실릴 수 있고, 플레이어가 쓴 문자열이 섞이면 그 순간 인젝션 경로가 열린다 (§2.5).
/// </summary>
/// <param name="Found">그 첨자에 NPC 가 있는가.</param>
/// <param name="Npc">NPC 첨자.</param>
/// <param name="Tick">스냅샷을 뜬 틱.</param>
/// <param name="Archetype">아키타입 id.</param>
/// <param name="Zone">존 id.</param>
/// <param name="Poi">현재 POI id.</param>
/// <param name="Goal">플랜의 goal.</param>
/// <param name="Action">지금 스텝의 액션 id.</param>
/// <param name="StepIndex">지금 스텝 첨자.</param>
/// <param name="StepCount">플랜의 스텝 수.</param>
/// <param name="StepStatus">스텝 실행 상태.</param>
/// <param name="Flags">참인 월드 플래그 이름들.</param>
/// <param name="Inventory">0 이 아닌 인벤토리 (id → 수량).</param>
/// <param name="Recent">최근 사건 (RingBuffer8).</param>
public readonly record struct NpcContext(
    bool Found,
    int Npc,
    long Tick,
    string Archetype,
    string Zone,
    string Poi,
    string Goal,
    string Action,
    int StepIndex,
    int StepCount,
    string StepStatus,
    string[] Flags,
    string[] Inventory,
    TraceEvent[] Recent);

/// <summary>버킷 한 줄 (B-08).</summary>
/// <param name="Bucket">버킷 표기.</param>
/// <param name="State"><c>filled</c>·<c>pinned</c>·<c>fallback</c>·<c>missing</c>.</param>
public readonly record struct BucketRow(string Bucket, string State);

/// <summary>
/// <c>GET /buckets</c> — 플랜 스토어 상태 (B-08. F-02 Studio 가 쓴다).
/// </summary>
/// <param name="Total">버킷 총수.</param>
/// <param name="Filled">플랜이 올라온 버킷 수.</param>
/// <param name="Pinned">사람이 고친 것.</param>
/// <param name="Missing">비어 있는 것 — 아키타입 폴백으로 해소된다.</param>
/// <param name="Returned">이 응답에 실린 줄 수.</param>
/// <param name="Buckets">줄들.</param>
public readonly record struct BucketReport(
    int Total,
    int Filled,
    int Pinned,
    int Missing,
    int Returned,
    BucketRow[] Buckets);

/// <summary>
/// 읽기 전용 질의 API (B-08).
///
/// <para>
/// <b>링크가 아니라 HTTP 다.</b> N1(명령은 fire-and-forget, 반환값 없음)은 그대로다 —
/// 게임서버가 NPC 서버에 물어볼 수단이 구조적으로 없는 것은 <b>옳은 설계</b>이고,
/// 그것을 흔들지 않으려고 질의는 링크 밖에 둔다.
/// </para>
///
/// <para>
/// <b>런타임 게임 로직이 이 API 에 의존하면 안 된다.</b> 도구·대화 서비스·운영 진단 전용이다 —
/// 게임서버가 매 틱 여기에 물어 행동을 정하기 시작하면 그것은 링크를 우회한 동기 호출이 되고,
/// 틱 예산과 장애 격리가 동시에 무너진다.
/// </para>
///
/// <para>
/// <b>틱 루프를 막지 않는다.</b> SoA 배열을 읽기만 하고 값을 복사해 나간다 — 락도 쓰기도 없다.
/// 한 틱 낡은 값을 볼 수 있지만 조회에는 그것으로 충분하다. 개별 플랜은
/// <c>TryPeekFor</c> 로 본다 — <b>조회만으로 LRU 회수 순서가 바뀌면</b> 대시보드를 열어 둔 것만으로
/// 게임 상태가 달라진다 (T4-12).
/// </para>
/// </summary>
internal static class QueryEndpoints
{
    /// <summary>벌크 목록 라우트.</summary>
    public const string ListRoute = "/npcs";

    /// <summary>대화·진단용 맥락 라우트.</summary>
    public const string ContextRoute = "/npc/{id:int}/context";

    /// <summary>플랜 스토어 상태 라우트.</summary>
    public const string BucketsRoute = "/buckets";

    /// <summary>변경 스트림 라우트 (SSE).</summary>
    public const string StreamRoute = "/stream/npcs";

    /// <summary>한 쪽의 기본 크기.</summary>
    public const int DefaultLimit = 100;

    /// <summary>
    /// 한 쪽의 상한. <b>5,000 을 한 번에 주지 않는다</b> — 응답 조립이 그만큼 길어지고,
    /// 그 시간 동안 SoA 를 읽으므로 값이 앞뒤로 한 틱씩 어긋난다.
    /// </summary>
    public const int MaxLimit = 1_000;

    /// <summary><c>/buckets</c> 가 한 번에 싣는 줄 수 상한.</summary>
    public const int MaxBucketRows = 3_000;

    /// <summary>
    /// 벌크 목록. 필터는 전부 <b>선택</b>이고, 빈 값이면 걸지 않는다.
    /// </summary>
    /// <param name="store">상태 저장소.</param>
    /// <param name="plans">플랜 스토어.</param>
    /// <param name="data">마스터데이터.</param>
    /// <param name="now">현재 틱.</param>
    /// <param name="zone">존 id 필터.</param>
    /// <param name="archetype">아키타입 id 필터.</param>
    /// <param name="flag">월드 플래그 이름 필터 — 그 플래그가 선 NPC 만.</param>
    /// <param name="status">스텝 상태 필터.</param>
    /// <param name="limit">한 쪽 크기.</param>
    /// <param name="cursor">이 슬롯부터 본다.</param>
    public static NpcListPage List(
        NpcStore store,
        PlanStore plans,
        MasterDataSet data,
        Tick now,
        string? zone = null,
        string? archetype = null,
        string? flag = null,
        string? status = null,
        int limit = DefaultLimit,
        int cursor = 0)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(data);

        int take = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
        int from = Math.Max(0, cursor);

        ushort zoneCode = CodeOfZone(data, zone);
        ushort archetypeCode = CodeOfArchetype(data, archetype);
        WorldFlags wanted = FlagOf(flag);
        StepStatus? wantedStatus = StatusOf(status);

        var rows = new List<NpcSummary>(take);
        int matched = 0;
        int? next = null;

        for (int npc = 0; npc < store.Count; npc++)
        {
            if (!Matches(store, npc, zoneCode, archetypeCode, wanted, wantedStatus))
            {
                continue;
            }

            matched++;

            if (npc < from)
            {
                continue;
            }

            if (rows.Count == take)
            {
                // 다음 쪽이 있다. 커서는 슬롯 첨자라 스토어가 바뀌어도 뜻이 흔들리지 않는다.
                next ??= npc;
                continue;
            }

            rows.Add(SummaryOf(store, plans, data, npc));
        }

        return new NpcListPage(now.Value, store.Count, matched, rows.Count, next, [.. rows]);
    }

    /// <summary>대화·진단용 맥락. 없는 첨자면 <c>Found=false</c>.</summary>
    /// <param name="npc">NPC 첨자.</param>
    /// <param name="store">상태 저장소.</param>
    /// <param name="plans">플랜 스토어.</param>
    /// <param name="data">마스터데이터.</param>
    /// <param name="now">현재 틱.</param>
    public static NpcContext Context(
        int npc, NpcStore store, PlanStore plans, MasterDataSet data, Tick now)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(data);

        NpcTrace trace = NpcTraceEndpoint.Snapshot(npc, store, plans, data, now);

        if (!trace.Found)
        {
            return new NpcContext { Found = false, Npc = npc, Tick = now.Value };
        }

        int step = store.StepIndex[npc];

        return new NpcContext(
            Found: true,
            Npc: npc,
            Tick: now.Value,
            Archetype: trace.Archetype,
            Zone: trace.Zone,
            Poi: trace.Poi,
            Goal: trace.PlanGoal,
            Action: ActionOf(store, plans, data, npc),
            StepIndex: Math.Min(step, Math.Max(0, trace.Steps.Length - 1)),
            StepCount: trace.Steps.Length,
            StepStatus: trace.StepStatus,
            Flags: trace.Flags,
            Inventory: trace.Inventory,
            Recent: trace.Recent);
    }

    /// <summary>
    /// 플랜 스토어 상태.
    /// </summary>
    /// <param name="plans">플랜 스토어.</param>
    /// <param name="data">마스터데이터.</param>
    /// <param name="state">
    /// <c>missing</c>·<c>pinned</c>·<c>fallback</c>·<c>filled</c>. 빈 값이면 집계만 준다 —
    /// <b>2,880줄을 기본으로 뱉지 않는다.</b>
    /// </param>
    public static BucketReport Buckets(PlanStore plans, MasterDataSet data, string? state = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(data);

        var rows = new List<BucketRow>(state is null ? 0 : 256);
        int filled = 0;
        int pinned = 0;
        int missing = 0;

        for (int index = 0; index < data.Buckets.TotalKeys; index++)
        {
            BucketKey bucket = data.Buckets.FromIndex(index);
            string row = StateOf(plans, bucket);

            switch (row)
            {
                case "pinned":
                    pinned++;
                    filled++;
                    break;

                case "missing":
                    missing++;
                    break;

                default:
                    filled++;
                    break;
            }

            if (state is { Length: > 0 }
                && string.Equals(row, state, StringComparison.OrdinalIgnoreCase)
                && rows.Count < MaxBucketRows)
            {
                rows.Add(new BucketRow(bucket.Format(data.Archetypes[bucket.A].Id), row));
            }
        }

        return new BucketReport(
            data.Buckets.TotalKeys, filled, pinned, missing, rows.Count, [.. rows]);
    }

    /// <summary>
    /// <c>ids=1,2,3</c> 를 슬롯 배열로. <b>모르는 값은 버린다</b> — 스트림은 진단용이라
    /// 오타 하나로 연결이 끊기는 것보다 조용히 빠지는 편이 낫다.
    /// </summary>
    /// <param name="ids">쉼표로 이은 첨자들.</param>
    /// <param name="count">슬롯 총수.</param>
    /// <param name="max">받아 줄 최대 개수.</param>
    public static int[] ParseIds(string? ids, int count, int max)
    {
        if (string.IsNullOrWhiteSpace(ids))
        {
            return [];
        }

        var parsed = new List<int>(16);

        foreach (string part in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (parsed.Count == max)
            {
                break;
            }

            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
                && (uint)id < (uint)count
                && !parsed.Contains(id))
            {
                parsed.Add(id);
            }
        }

        return [.. parsed];
    }

    /// <summary>한 NPC 의 요약.</summary>
    private static NpcSummary SummaryOf(
        NpcStore store, PlanStore plans, MasterDataSet data, int npc)
    {
        int planId = Volatile.Read(ref store.PlanId[npc]);
        bool live = plans.TryPeekFor(npc, planId, out CompiledPlan plan);

        return new NpcSummary(
            Npc: npc,
            Archetype: data.Archetypes[new ArchetypeId(store.ArchetypeCode[npc])].Id,
            Zone: NameOfZone(data, store.ZoneCode[npc]),
            Poi: store.CurrentPoi[npc] == 0 ? string.Empty : data.Pois[new PoiId(store.CurrentPoi[npc])].Id,
            Action: ActionOf(plan, data, store.StepIndex[npc]),
            Lod: store.Lod[npc],
            StepStatus: ((StepStatus)store.StepStatus[npc]).ToString(),
            PlanKind: IndividualPlanPool.IsIndividual(planId)
                ? live ? "individual" : "individual(회수됨)"
                : plan.Origin == PlanOrigin.Fallback ? "fallback" : "bucket");
    }

    private static string ActionOf(NpcStore store, PlanStore plans, MasterDataSet data, int npc)
    {
        plans.TryPeekFor(npc, Volatile.Read(ref store.PlanId[npc]), out CompiledPlan plan);

        return ActionOf(plan, data, store.StepIndex[npc]);
    }

    private static string ActionOf(in CompiledPlan plan, MasterDataSet data, int stepIndex)
    {
        if (plan.Steps.IsEmpty)
        {
            return string.Empty;
        }

        int step = Math.Clamp(stepIndex, 0, plan.Steps.Length - 1);

        return data.Actions[plan.Steps[step].Action].Id;
    }

    private static bool Matches(
        NpcStore store,
        int npc,
        ushort zone,
        ushort archetype,
        WorldFlags flag,
        StepStatus? status)
    {
        if (zone != 0 && store.ZoneCode[npc] != zone)
        {
            return false;
        }

        if (archetype != ushort.MaxValue && store.ArchetypeCode[npc] != archetype)
        {
            return false;
        }

        if (flag != WorldFlags.None && (store.Flags[npc] & flag) == 0)
        {
            return false;
        }

        return status is null || (StepStatus)store.StepStatus[npc] == status;
    }

    /// <summary>모르는 존 이름은 0 이다 — 필터가 안 걸린다. 404 로 만들지 않는다.</summary>
    private static ushort CodeOfZone(MasterDataSet data, string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone))
        {
            return 0;
        }

        return data.Zones.TryGet(zone, out ZoneDef def) ? def.Code.Value : ushort.MaxValue;
    }

    /// <summary><see cref="ushort.MaxValue"/> 가 "필터 없음" 이다 — 아키타입 code 0 이 유효하다.</summary>
    private static ushort CodeOfArchetype(MasterDataSet data, string? archetype)
    {
        if (string.IsNullOrWhiteSpace(archetype))
        {
            return ushort.MaxValue;
        }

        return data.Archetypes.TryGet(archetype, out ArchetypeDef def)
            ? def.Code.Value
            : (ushort)(ushort.MaxValue - 1);   // 아무것도 안 걸리는 값
    }

    private static WorldFlags FlagOf(string? flag) =>
        !string.IsNullOrWhiteSpace(flag) && WorldFlagTable.TryParse(flag, out WorldFlags value)
            ? value
            : WorldFlags.None;

    private static StepStatus? StatusOf(string? status) =>
        !string.IsNullOrWhiteSpace(status) && Enum.TryParse(status, ignoreCase: true, out StepStatus value)
            ? value
            : null;

    private static string NameOfZone(MasterDataSet data, ushort code)
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

        return code.ToString(CultureInfo.InvariantCulture);
    }

    private static string StateOf(PlanStore plans, BucketKey bucket)
    {
        if (!plans.HasBucket(bucket))
        {
            return "missing";
        }

        return plans.IsPinned(bucket) ? "pinned" : "filled";
    }
}
