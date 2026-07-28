using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Host.Replan;

/// <summary>
/// 개별 NPC 재계획 공급원. docs/14 §2·§4.
///
/// <see cref="ReplanQueue"/>(점수순 힙)에서 가장 급한 NPC 를 꺼내 <see cref="PlanRequest"/> 를 만든다.
/// 품질은 항상 <see cref="PlanQuality.Individual"/> 이라 티어 라우터가 T1(로컬·저지연·무비용)로 보낸다 —
/// 1회용 플랜에 T2 예산을 쓰지 않는다 (CLAUDE.md §8).
///
/// <b>낡은 요청은 꺼내는 자리에서 폐기한다</b> (T4-04). 큐 대기 + 생성이 실측 37~51틱이라
/// 그 사이 상황이 바뀐 요청을 그대로 처리하면 LLM 예산이 왕복만 하고 진전이 없다.
///
/// <b>반영은 <c>PendingPlanId</c> 한 칸에 <c>Volatile.Write</c> 뿐이다</b> (T4-12).
/// 실제 스왑은 실행기가 스텝 경계에서 한다 (docs/03 §6).
/// </summary>
internal sealed class IndividualReplanSource : IReplanSource
{
    private readonly NpcStore _store;
    private readonly ReplanQueue _queue;
    private readonly ReplanHandoff _handoff;
    private readonly ReplanSnapshots _snapshots;
    private readonly IndividualPlanPool _pool;
    private readonly PlanSwapper _swapper;
    private readonly MasterDataSet _data;
    private readonly Func<TimeOfDay> _timeOfDay;

    private long _discarded;

    /// <summary>공급원을 만든다. 기동 시 1회.</summary>
    public IndividualReplanSource(
        NpcStore store,
        ReplanQueue queue,
        ReplanHandoff handoff,
        ReplanSnapshots snapshots,
        IndividualPlanPool pool,
        PlanSwapper swapper,
        MasterDataSet data,
        Func<TimeOfDay> timeOfDay)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(handoff);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(swapper);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(timeOfDay);

        _store = store;
        _queue = queue;
        _handoff = handoff;
        _snapshots = snapshots;
        _pool = pool;
        _swapper = swapper;
        _data = data;
        _timeOfDay = timeOfDay;
    }

    /// <summary>
    /// 존 상태 표. 붙이면 버킷 키의 지역상태·기후가 정확해진다 (T4-13).
    /// 없으면 플래그에서 추정하므로 <c>Alert</c>·<c>Disaster</c>·<c>Cold</c> 버킷을 찾지 못한다.
    /// </summary>
    public ZoneStateTable? ZoneStates { get; init; }

    /// <inheritdoc />
    public string Name => "individual";

    /// <summary>
    /// 대기 깊이. <b>힙과 통로를 합친다</b> — 스필오버 판정(docs/14 §4)이 보는 것은
    /// "지금 T1 에 얼마나 밀려 있나" 이고, 통로에 놓인 것도 아직 처리 전이다.
    ///
    /// <c>_queue.Count</c> 를 워커 스레드에서 읽는 것은 안전하다. 한 필드 읽기이고
    /// 한 틱 낡은 값을 봐도 임계(64) 판정이 흔들릴 뿐 메모리 안전과 무관하다.
    /// </summary>
    public int Depth => _queue.Count + _handoff.PendingCount;

    /// <summary>낡아서 폐기하고 재삽입한 요청 수 (T4-04).</summary>
    public long Discarded => Interlocked.Read(ref _discarded);

    /// <inheritdoc />
    public bool TryTake(Tick now, out ReplanJob job)
    {
        // <b>힙(ReplanQueue)을 여기서 만지지 않는다.</b> 워커 스레드에서 힙을 꺼내거나 되넣으면
        // 틱 루프의 TryEnqueue 와 경합해 _heap[_count] 가 범위를 벗어난다 (ReplanHandoff 주석).
        // 통로에서만 가져오고, 되돌릴 것도 통로에 놓는다.
        while (_handoff.TryClaim(out int npc, out float score))
        {
            // WorldFlags 는 8바이트 enum 이라 x64 에서 원자적으로 읽힌다.
            // Volatile.Read<T> 는 참조 형식만 받으므로 배열을 그대로 읽는다 — 틱 루프가 쓰고
            // 우리는 읽기만 하며, 한 틱 낡은 값을 봐도 스냅샷 판정이 받아준다.
            WorldFlags flags = _store.Flags[npc];

            // 큐에 넣을 때와 상황이 크게 달라졌으면 폐기하고 지금 상태로 다시 넣는다 (T4-04).
            // 힙에 되넣는 것은 다음 틱의 DrainReturns 가 한다 — 한 틱 늦지만 경합이 없다.
            if (!_snapshots.TryAccept(npc, flags, out ReplanRequestSnapshot snapshot))
            {
                Interlocked.Increment(ref _discarded);
                _snapshots.Capture(npc, flags, now, score);
                _handoff.TryReturn(npc, score);
                continue;
            }

            job = new ReplanJob(
                npc,
                BucketOf(npc, flags),
                flags,
                PlanQuality.Individual,
                score,
                snapshot.Exists ? snapshot.QueuedTick : -1);

            return true;
        }

        job = default;
        return false;
    }

    /// <summary>
    /// 이 NPC 의 버킷 키. 아키타입과 시간대는 밖에서 오고, 지역상태·기후는 존 상태 표가 있으면
    /// 그쪽이 정확하다 — 플래그만으로는 <c>Alert</c>·<c>Disaster</c>·<c>Cold</c> 를 가를 수 없다.
    /// </summary>
    private BucketKey BucketOf(int npc, WorldFlags flags)
    {
        var archetype = new ArchetypeId(_store.ArchetypeCode[npc]);
        TimeOfDay time = _timeOfDay();

        if (ZoneStates is not { } states)
        {
            return _data.Buckets.KeyOf(archetype, time, flags);
        }

        var zone = new ZoneId(_store.ZoneCode[npc]);

        return new BucketKey(archetype, time, states.RegionOf(zone), states.ClimateOf(zone));
    }

    /// <inheritdoc />
    public void Apply(in ReplanJob job, CompiledPlan plan, Tick now)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // 개별 풀에 대여한다. 꽉 차면 LRU 로 회수된다 (docs/13 §2) —
        // PlanStore 레지스트리에 1회용 플랜을 쌓으면 65,536 상한에 닿는다.
        int planId = _pool.Assign(plan, job.Npc, now.Value);

        // 워커가 건드리는 런타임 상태는 이 한 칸뿐이다 (CLAUDE.md §2.1 · §7).
        _swapper.Request(job.Npc, new PlanId(planId));
    }

    /// <inheritdoc />
    public void Abandon(in ReplanJob job, Tick now)
    {
        _ = now;

        // 아무것도 하지 않는다. 그 NPC 는 기존 플랜을 계속 쓰고, 여전히 이탈 상태면
        // 다음 인지 스캔이 다시 큐에 넣는다. 여기서 재삽입하면 실패가 무한 루프가 된다.
        _snapshots.Clear(job.Npc);
    }
}
