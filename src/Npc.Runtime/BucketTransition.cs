using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Runtime;

/// <summary>
/// 버킷 전환 스파이크 완화. docs/14 §5.
///
/// 전환 계기 한 번에 대상 NPC 가 동시에 버킷 플랜을 갈아탄다.
/// LLM 호출이 아니라 순수 배열 쓰기지만, 5,000회를 한 틱에 하면 스파이크가 생긴다.
/// NPC 별로 <c>Hash(npcId)</c> 지터만큼 흩어 실행한다.
///
/// <b>지터는 결정론이다.</b> <c>Random</c> 을 쓰면 리플레이가 깨진다 (CLAUDE.md §2.3).
///
/// 전환 시각은 경계에서야 알 수 있으므로 실제 예약은 <c>경계 + spread + 지터</c> —
/// 즉 <c>[경계, 경계 + 2×spread]</c> 구간이다. 흩어지는 폭은 사양과 같고 중심만 뒤로 밀린다.
///
/// <b>폭이 계기마다 다르다</b> — 시간대 전환은 <see cref="JitterSpread"/>(±300, 실시간 60초 창),
/// 존 이벤트는 <see cref="ZoneJitterSpread"/>(±14, 2.9초 창)다. 이유는 그 상수 주석에 있다.
///
/// <para><b>계기가 셋이다</b> (T4-13):</para>
/// <list type="bullet">
///   <item><c>GameClock.TimeOfDayChanged</c> — 전원</item>
///   <item><c>ZoneStateChanged</c> — <b>그 존만</b></item>
///   <item><c>WeatherChanged</c> — <b>그 존만</b></item>
/// </list>
/// <para>
/// 뒤의 둘은 P1 에 없었다. 플래그는 이미 바뀌었는데 다음 시간대 경계까지 낡은 플랜을 쓰고 있었다.
/// <b>존 이벤트는 그 존만 예약한다</b> — 전원을 다시 세면 완화하려던 스파이크가 그대로 돌아온다.
/// </para>
///
/// <para><b>예약 구조.</b> NPC 당 예약 시각 하나(<c>_due</c>)와 <c>due % 601</c> 슬롯의 단일 연결 리스트다.
/// 재예약은 같은 노드를 옮기므로 NPC 는 리스트에 최대 한 번 들어간다 — 슬롯 하나의 평균 길이는
/// 5,000 / 601 ≈ 8 이라 옮기는 비용도 그 정도다. <b>할당 0</b> 이고, 평시 <see cref="Apply"/> 는
/// 빈 슬롯 하나를 보는 것뿐이다.</para>
/// </summary>
public sealed class BucketTransition
{
    /// <summary>시간대 전환의 지터 폭(틱). ±300 (docs/14 §5).</summary>
    public const int JitterSpread = 300;

    /// <summary>지터가 흩어지는 구간 길이(틱).</summary>
    public const int JitterWindow = (JitterSpread * 2) + 1;

    /// <summary>
    /// 존 이벤트의 지터 폭(틱). ±14 → 29틱 창 = <b>실시간 2.9초</b>.
    ///
    /// <b>시간대 전환과 폭이 다른 것이 핵심이다.</b> §5 의 ±300 은 5,000마리가 한꺼번에
    /// 갈아타는 <b>정기</b> 전환의 스파이크를 없애려는 값이고, 그 창은 실시간 60초다.
    /// 그런데 <c>docs/14 §7</c> 은 공성에서 "<b>3초 내</b> 마을 전체 플랜 스왑 완료" 를 요구한다 —
    /// 같은 폭으로는 두 요구가 양립하지 않는다.
    ///
    /// 존 이벤트는 (1) 대상이 그 존뿐이라 인원이 훨씬 적고 (2) 긴급하다. 그래서 좁게 흩는다.
    /// 존 인원 500마리 기준 틱당 약 17건이고, 그 정도는 스파이크가 아니다.
    /// </summary>
    public const int ZoneJitterSpread = 14;

    /// <summary>존 전환이 흩어지는 구간 길이(틱). 실시간 2.9초.</summary>
    public const int ZoneJitterWindow = (ZoneJitterSpread * 2) + 1;

    /// <summary>해시 솔트. 다른 지터와 같은 수열이 나오지 않게 한다.</summary>
    private const int Salt = 0x5457;   // 'TW'

    private const int None = -1;

    private readonly NpcStore _store;
    private readonly PlanStore _plans;
    private readonly BucketSpace _buckets;

    private readonly long[] _due;            // npc → 예약 틱. None(-1) = 예약 없음
    private readonly TimeOfDay[] _dueTime;   // npc → 그때 쓸 시간대
    private readonly int[] _slotOf;          // npc → 지금 들어 있는 슬롯. None = 없음
    private readonly int[] _next;            // npc → 같은 슬롯의 다음 npc. None = 끝
    private readonly int[] _head = new int[JitterWindow];

    private int _pending;

    /// <summary>전환기를 만든다. 기동 시 1회.</summary>
    public BucketTransition(NpcStore store, PlanStore plans, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(data);

        _store = store;
        _plans = plans;
        _buckets = data.Buckets;

        _due = new long[store.Count];
        _dueTime = new TimeOfDay[store.Count];
        _slotOf = new int[store.Count];
        _next = new int[store.Count];

        Array.Fill(_due, None);
        Array.Fill(_slotOf, None);
        Array.Fill(_next, None);
        Array.Fill(_head, None);
    }

    /// <summary>
    /// 존 상태 표. 붙이면 버킷 키의 지역상태·기후가 <b>정확해진다</b> —
    /// 없으면 플래그에서 추정하므로 <c>Alert</c>·<c>Disaster</c>·<c>Cold</c> 를 구분하지 못한다
    /// (<see cref="BucketSpace.RegionStateOf"/>).
    /// </summary>
    public ZoneStateTable? ZoneStates { get; init; }

    /// <summary>전환을 겪은 횟수 (계기 수).</summary>
    public long Transitions { get; private set; }

    /// <summary>예약한 스왑 수.</summary>
    public long Scheduled { get; private set; }

    /// <summary>실제로 스왑을 건 수. 같은 플랜이면 걸지 않는다.</summary>
    public long Swapped { get; private set; }

    /// <summary>시간대 전환으로 예약한 횟수.</summary>
    public long TimeOfDayTransitions { get; private set; }

    /// <summary>존 이벤트로 예약한 횟수 (지역상태 + 기후).</summary>
    public long ZoneTransitions { get; private set; }

    /// <summary>마지막 틱에 처리한 예약 수. 대시보드가 스파이크를 볼 때 쓴다.</summary>
    public int LastBatch { get; private set; }

    /// <summary>예약이 남아 있는가.</summary>
    public bool InProgress => _pending > 0;

    /// <summary>예약 대기 중인 NPC 수.</summary>
    public int Pending => _pending;

    /// <summary>docs/14 §5 의 지터. <c>[-300, +300]</c>.</summary>
    public static int JitterTicks(NpcId npc) => PlanHash.Jitter(npc, Salt, JitterSpread);

    /// <summary>주어진 폭의 지터. <c>[-spread, +spread]</c>.</summary>
    public static int JitterTicks(NpcId npc, int spread) => PlanHash.Jitter(npc, Salt, spread);

    /// <summary>이 NPC 가 실제로 갈아탈 틱 (시간대 전환).</summary>
    public static long DueTick(long boundaryTick, NpcId npc) =>
        DueTick(boundaryTick, npc, JitterSpread);

    /// <summary>
    /// 이 NPC 가 실제로 갈아탈 틱. 예약은 <c>[경계, 경계 + 2×spread]</c> 구간이다 —
    /// 전환 시각은 경계에서야 알 수 있으므로 중심을 <c>+spread</c> 만큼 뒤로 민다.
    /// </summary>
    public static long DueTick(long boundaryTick, NpcId npc, int spread) =>
        boundaryTick + spread + JitterTicks(npc, spread);

    /// <summary>
    /// 한 틱. 시간대가 바뀌면 전원을 예약하고, 이번 틱 몫의 예약을 실행한다.
    /// 틱 루프가 매 틱 부른다 — 평시 비용은 분기 하나와 빈 슬롯 조회 하나다.
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

    /// <summary>
    /// 게임서버 이벤트를 본다. 존 상태·기후가 <b>실제로 바뀌었을 때만</b> 그 존을 예약한다.
    ///
    /// <c>NpcServerLoop.DrainEvents</c> 가 부른다 — 모든 이벤트가 지나는 곳이 거기 하나뿐이고,
    /// 링크 구현체마다 넣으면 구현체를 바꿀 때 검출이 사라진다 (docs/11 §10 과 같은 논거).
    /// </summary>
    /// <returns>예약이 걸렸으면 true.</returns>
    public bool Observe(in GameEvent ev, GameClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        switch (ev.Kind)
        {
            case GameEventKind.ZoneStateChanged:
                // 표가 없으면 바뀐 것인지 알 수 없다. 그때는 매번 예약한다 —
                // 같은 플랜이면 RequestSwap 이 스스로 걸지 않으므로 비용은 조회 한 번이다.
                if (ZoneStates is { } states && !states.Set(ev.Zone, (RegionState)ev.Code))
                {
                    return false;
                }

                ScheduleZone(NextTick(clock), clock.TimeOfDay, ev.Zone);
                return true;

            case GameEventKind.WeatherChanged:
                if (ZoneStates is { } weather && !weather.Set(ev.Zone, (Climate)ev.Code))
                {
                    return false;
                }

                ScheduleZone(NextTick(clock), clock.TimeOfDay, ev.Zone);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// 이벤트로 예약할 때의 경계 틱. <b>다음 틱이다.</b>
    ///
    /// <see cref="Observe"/> 는 <c>DrainEvents</c> 에서 불리고, 그 뒤에 시계가 한 틱 나아가
    /// <see cref="Apply"/> 가 처음 돈다. 지금 틱으로 예약하면 지터가 최솟값(-spread)인 NPC 의
    /// 예약 시각이 <b>이미 지나간 틱</b>이 되어 다음 한 바퀴(601틱)까지 처리되지 않는다.
    /// </summary>
    private static Tick NextTick(GameClock clock) => new(clock.Current.Value + 1);

    /// <summary>전원을 지터에 따라 예약한다. O(N).</summary>
    public void Schedule(Tick boundary, TimeOfDay target)
    {
        int count = _store.Count;

        for (int npc = 0; npc < count; npc++)
        {
            Reserve(npc, boundary.Value, target, JitterSpread);
        }

        Transitions++;
        TimeOfDayTransitions++;
        Scheduled += count;
    }

    /// <summary>
    /// 한 존의 NPC 만 예약한다. O(그 존 인원).
    ///
    /// <b>전원을 다시 세지 않는다</b> — 공성 한 번에 존 이벤트가 여러 발 오는데
    /// 매번 5,000마리를 훑으면 완화하려던 스파이크가 그대로 돌아온다 (docs/14 §5).
    /// </summary>
    /// <returns>예약한 NPC 수.</returns>
    public int ScheduleZone(Tick boundary, TimeOfDay target, ZoneId zone)
    {
        int scheduled = 0;

        for (int npc = 0; npc < _store.Count; npc++)
        {
            if (_store.ZoneCode[npc] != zone.Value)
            {
                continue;
            }

            // 존 이벤트는 좁게 흩는다 — §7 이 "3초 내 마을 전체 스왑" 을 요구한다.
            Reserve(npc, boundary.Value, target, ZoneJitterSpread);
            scheduled++;
        }

        Transitions++;
        ZoneTransitions++;
        Scheduled += scheduled;
        return scheduled;
    }

    /// <summary>이번 틱 몫의 예약을 실행한다.</summary>
    /// <returns>이번 틱에 처리한 예약 수.</returns>
    public int Apply(Tick now, PlanSwapper swapper)
    {
        ArgumentNullException.ThrowIfNull(swapper);

        LastBatch = 0;

        if (_pending == 0)
        {
            return 0;
        }

        int slot = SlotOf(now.Value);
        int npc = _head[slot];
        int previous = None;
        int fired = 0;

        while (npc != None)
        {
            int next = _next[npc];

            if (_due[npc] == now.Value)
            {
                Unlink(slot, previous, npc, next);
                RequestSwap(npc, _dueTime[npc], swapper);
                fired++;
            }
            else
            {
                previous = npc;   // 다음 바퀴의 예약이다. 두고 간다
            }

            npc = next;
        }

        LastBatch = fired;
        return fired;
    }

    /// <summary>예약을 전부 버린다. 측정 구간을 가를 때만 쓴다.</summary>
    public void Clear()
    {
        Array.Fill(_due, None);
        Array.Fill(_slotOf, None);
        Array.Fill(_next, None);
        Array.Fill(_head, None);
        _pending = 0;
    }

    // ---------------------------------------------------------------- 내부

    private static int SlotOf(long tick) => (int)(((tick % JitterWindow) + JitterWindow) % JitterWindow);

    /// <summary>
    /// 이 NPC 의 예약을 걸거나 옮긴다. 이미 예약이 있으면 <b>덮어쓴다</b> —
    /// 두 계기가 겹치면 나중 것이 이긴다. 어느 쪽이든 갈아탈 버킷은 지금 상태에서 다시 계산된다.
    /// </summary>
    private void Reserve(int npc, long boundary, TimeOfDay target, int spread)
    {
        long due = DueTick(boundary, new NpcId(npc), spread);
        int slot = SlotOf(due);
        int current = _slotOf[npc];

        _due[npc] = due;
        _dueTime[npc] = target;

        if (current == slot)
        {
            return;   // 같은 슬롯이다. 노드를 옮길 필요가 없다
        }

        if (current != None)
        {
            Detach(current, npc);
        }
        else
        {
            _pending++;
        }

        _next[npc] = _head[slot];
        _head[slot] = npc;
        _slotOf[npc] = slot;
    }

    /// <summary>슬롯 리스트에서 이 NPC 를 뺀다. 슬롯 평균 길이가 8 이라 선형 탐색으로 충분하다.</summary>
    private void Detach(int slot, int npc)
    {
        int at = _head[slot];
        int previous = None;

        while (at != None && at != npc)
        {
            previous = at;
            at = _next[at];
        }

        if (at == None)
        {
            return;
        }

        if (previous == None)
        {
            _head[slot] = _next[at];
        }
        else
        {
            _next[previous] = _next[at];
        }

        _next[at] = None;
    }

    /// <summary><see cref="Apply"/> 순회 중의 제거. 앞 노드를 이미 알고 있어 O(1).</summary>
    private void Unlink(int slot, int previous, int npc, int next)
    {
        if (previous == None)
        {
            _head[slot] = next;
        }
        else
        {
            _next[previous] = next;
        }

        _next[npc] = None;
        _slotOf[npc] = None;
        _due[npc] = None;
        _pending--;
    }

    private void RequestSwap(int npc, TimeOfDay target, PlanSwapper swapper)
    {
        if ((StepStatus)_store.StepStatus[npc] == StepStatus.Unspawned)
        {
            return;
        }

        var key = new BucketKey(
            new ArchetypeId(_store.ArchetypeCode[npc]), target, RegionOf(npc), ClimateOf(npc));

        CompiledPlan plan = _plans.Resolve(key);

        if (plan.Id.Value == _store.PlanId[npc])
        {
            return;   // 같은 플랜이다. 상관 ID 를 흔들 이유가 없다
        }

        // 스텝 경계에서 갈아탄다 (docs/03 §6). 스텝 중간에 바꾸면 직전 응답이 오인된다.
        swapper.Request(npc, plan.Id);
        Swapped++;
    }

    /// <summary>
    /// 이 NPC 가 있는 존의 지역 상태. 표가 있으면 정확하고, 없으면 플래그에서 추정한다.
    ///
    /// ⚠ 추정 경로는 <c>Alert</c>·<c>Disaster</c> 를 낼 수 없다 — 플래그가 <c>Peace</c>·<c>War</c> 와
    /// 같기 때문이다. P1 은 이 추정만 갖고 있었고, ordinal 내림차순으로 골라
    /// <b>평시 NPC 전원을 <c>Alert</c> 로 판정</b>하고 있었다 (프리베이크는 <c>Peace</c> 를 먼저 채운다).
    /// </summary>
    private RegionState RegionOf(int npc) =>
        ZoneStates is { } states
            ? states.RegionOf(new ZoneId(_store.ZoneCode[npc]))
            : _buckets.RegionStateOf(_store.Flags[npc]);

    /// <summary>이 NPC 가 있는 존의 기후. 표가 없으면 플래그에서 추정한다(<c>Cold</c> 는 낼 수 없다).</summary>
    private Climate ClimateOf(int npc) =>
        ZoneStates is { } states
            ? states.ClimateOf(new ZoneId(_store.ZoneCode[npc]))
            : _buckets.ClimateOf(_store.Flags[npc]);
}
