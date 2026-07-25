using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Runtime;

/// <summary>
/// 시간대 전환 스파이크 완화. docs/14 §5.
///
/// <c>GameTimeChanged</c> 한 번에 5,000마리가 동시에 버킷 플랜을 갈아탄다.
/// LLM 호출이 아니라 순수 배열 쓰기지만, 5,000회를 한 틱에 하면 스파이크가 생긴다.
/// NPC 별로 <c>(Hash(npcId) % 601) - 300</c> 틱(게임시간 ±5분)만큼 흩어 실행한다.
///
/// <b>지터는 결정론이다.</b> <c>Random</c> 을 쓰면 리플레이가 깨진다 (CLAUDE.md §2.3).
///
/// 전환 시각은 경계에서야 알 수 있으므로 실제 예약은 <c>경계 + 300 + 지터</c> —
/// 즉 <c>[경계, 경계+600]</c> 구간이다. 흩어지는 폭(601틱)은 사양과 같고 중심만 뒤로 밀린다.
///
/// 예약은 카운팅 정렬로 오프셋별로 묶어 둔다. 그래서 <see cref="Tick"/> 는
/// 전환이 없는 평시에 아무 일도 하지 않고, 전환 중에도 그 오프셋의 몫만 훑는다. <b>할당 0.</b>
/// </summary>
public sealed class BucketTransition
{
    /// <summary>지터 폭(틱). ±300 = 게임시간 ±5분 (docs/14 §5).</summary>
    public const int JitterSpread = 300;

    /// <summary>지터가 흩어지는 구간 길이(틱).</summary>
    public const int JitterWindow = (JitterSpread * 2) + 1;

    /// <summary>해시 솔트. 다른 지터와 같은 수열이 나오지 않게 한다.</summary>
    private const int Salt = 0x5457;   // 'TW'

    private readonly NpcStore _store;
    private readonly PlanStore _plans;
    private readonly BucketSpace _buckets;

    private readonly WorldFlags[] _regionFlags = new WorldFlags[BucketKey.RegionStateCount];
    private readonly WorldFlags[] _climateFlags = new WorldFlags[BucketKey.ClimateCount];

    private readonly int[] _ordered;                        // 오프셋 순으로 묶인 NPC 첨자
    private readonly int[] _start = new int[JitterWindow + 1];
    private readonly int[] _cursor = new int[JitterWindow];

    private long _boundary = -1;
    private TimeOfDay _target;

    /// <summary>전환기를 만든다. 기동 시 1회.</summary>
    public BucketTransition(NpcStore store, PlanStore plans, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(data);

        _store = store;
        _plans = plans;
        _buckets = data.Buckets;
        _ordered = new int[store.Count];

        for (int i = 0; i < _regionFlags.Length; i++)
        {
            _regionFlags[i] = _buckets.FlagsOf((RegionState)i);
        }

        for (int i = 0; i < _climateFlags.Length; i++)
        {
            _climateFlags[i] = _buckets.FlagsOf((Climate)i);
        }
    }

    /// <summary>전환을 겪은 횟수.</summary>
    public long Transitions { get; private set; }

    /// <summary>예약한 스왑 수.</summary>
    public long Scheduled { get; private set; }

    /// <summary>실제로 스왑을 건 수. 같은 플랜이면 걸지 않는다.</summary>
    public long Swapped { get; private set; }

    /// <summary>마지막 틱에 처리한 예약 수. 대시보드가 스파이크를 볼 때 쓴다.</summary>
    public int LastBatch { get; private set; }

    /// <summary>전환이 진행 중인가.</summary>
    public bool InProgress => _boundary >= 0;

    /// <summary>docs/14 §5 의 지터. <c>[-300, +300]</c>.</summary>
    public static int JitterTicks(NpcId npc) => PlanHash.Jitter(npc, Salt, JitterSpread);

    /// <summary>이 NPC 가 실제로 갈아탈 틱.</summary>
    public static long DueTick(long boundaryTick, NpcId npc) =>
        boundaryTick + JitterSpread + JitterTicks(npc);

    /// <summary>
    /// 한 틱. 시간대가 바뀌면 전원을 예약하고, 이번 틱 몫의 예약을 실행한다.
    /// 틱 루프가 매 틱 부른다 — 평시 비용은 분기 하나다.
    /// </summary>
    public void Tick(GameClock clock, PlanSwapper swapper)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(swapper);

        if (clock.TimeOfDayChanged)
        {
            Schedule(clock.Current, clock.TimeOfDay);
        }

        Apply(clock.Current, swapper);
    }

    /// <summary>전원을 지터에 따라 예약한다. 카운팅 정렬이라 O(N).</summary>
    public void Schedule(Tick boundary, TimeOfDay target)
    {
        Array.Clear(_start);

        int count = _store.Count;

        for (int npc = 0; npc < count; npc++)
        {
            _start[OffsetOf(npc) + 1]++;
        }

        for (int off = 0; off < JitterWindow; off++)
        {
            _start[off + 1] += _start[off];
            _cursor[off] = _start[off];
        }

        for (int npc = 0; npc < count; npc++)
        {
            _ordered[_cursor[OffsetOf(npc)]++] = npc;
        }

        _boundary = boundary.Value;
        _target = target;

        Transitions++;
        Scheduled += count;
    }

    /// <summary>이번 틱 몫의 예약을 실행한다.</summary>
    /// <returns>이번 틱에 처리한 예약 수.</returns>
    public int Apply(Tick now, PlanSwapper swapper)
    {
        ArgumentNullException.ThrowIfNull(swapper);

        LastBatch = 0;

        if (_boundary < 0)
        {
            return 0;
        }

        long offset = now.Value - _boundary;

        if (offset < 0)
        {
            return 0;
        }

        if (offset >= JitterWindow)
        {
            _boundary = -1;   // 전환 끝
            return 0;
        }

        int from = _start[offset];
        int to = _start[offset + 1];

        for (int k = from; k < to; k++)
        {
            RequestSwap(_ordered[k], swapper);
        }

        if (offset == JitterWindow - 1)
        {
            _boundary = -1;
        }

        LastBatch = to - from;
        return LastBatch;
    }

    private static int OffsetOf(int npc) => JitterSpread + JitterTicks(new NpcId(npc));

    private void RequestSwap(int npc, PlanSwapper swapper)
    {
        if ((StepStatus)_store.StepStatus[npc] == StepStatus.Unspawned)
        {
            return;
        }

        WorldFlags flags = _store.Flags[npc];

        var key = new BucketKey(
            new ArchetypeId(_store.ArchetypeCode[npc]), _target, RegionOf(flags), ClimateOf(flags));

        CompiledPlan plan = _plans.Resolve(key);

        if (plan.Id.Value == _store.PlanId[npc])
        {
            return;   // 같은 플랜이다. 상관 ID 를 흔들 이유가 없다
        }

        // 스텝 경계에서 갈아탄다 (docs/03 §6). 스텝 중간에 바꾸면 직전 응답이 오인된다.
        swapper.Request(npc, plan.Id);
        Swapped++;
    }

    /// <summary>플래그에서 지역 상태를 읽는다. 겹치면 심각한 쪽이 이긴다.</summary>
    private RegionState RegionOf(WorldFlags flags)
    {
        for (int i = _regionFlags.Length - 1; i > 0; i--)
        {
            if (_regionFlags[i] != WorldFlags.None && (flags & _regionFlags[i]) == _regionFlags[i])
            {
                return (RegionState)i;
            }
        }

        return RegionState.Peace;
    }

    /// <summary>플래그에서 기후를 읽는다.</summary>
    private Climate ClimateOf(WorldFlags flags)
    {
        for (int i = _climateFlags.Length - 1; i > 0; i--)
        {
            if (_climateFlags[i] != WorldFlags.None && (flags & _climateFlags[i]) == _climateFlags[i])
            {
                return (Climate)i;
            }
        }

        return Climate.Fair;
    }
}
