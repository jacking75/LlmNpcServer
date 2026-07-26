using Npc.Core.Plan;

namespace Npc.Planning;

/// <summary>
/// 개별 오버라이드 플랜 풀. docs/13 §2.
///
/// 버킷 플랜과 별개로, 특정 NPC 에게만 붙는 1회성 플랜이 있다.
/// <b>링 버퍼 512개이고 꽉 차면 LRU 로 회수한다.</b> 512 는 "동시에 특별 대우를 받는 NPC 수"의
/// 상한이고, 이게 곧 docs/14 의 재계획 예산과 맞물린다.
///
/// <para><b>id 인코딩.</b> <c>NpcStore.PlanId</c> 한 칸이 두 표를 가리킨다:</para>
/// <list type="bullet">
///   <item><c>&gt; 0</c> — <see cref="PlanStore"/> 레지스트리 id. <b>버킷 인덱스가 아니다</b></item>
///   <item><c>== 0</c> — 최후 플랜 (<see cref="PlanStore.IdlePlanId"/>)</item>
///   <item><c>&lt; 0</c> — 이 풀의 슬롯. <c>~value</c> 가 슬롯 번호다</item>
/// </list>
/// <para>
/// <c>~slot</c> 이라 슬롯 0 이 <c>-1</c> 이 된다 — <c>NpcStore.PendingPlanId</c> 도
/// <c>0 = 없음</c> 이므로 개별 슬롯은 <b>반드시</b> 음수여야 하고, <c>-slot</c> 이면 슬롯 0 이 겹친다.
/// </para>
///
/// <para>
/// <b>락이 없다.</b> 슬롯 확보는 <see cref="Interlocked.CompareExchange(ref int, int, int)"/> 뿐이고
/// 읽기는 <see cref="Volatile"/> 읽기뿐이다. 읽는 쪽은 틱 루프다 (CLAUDE.md §2.1).
/// </para>
///
/// <para>
/// 개별 플랜은 <b>수명이 짧다.</b> 완료되거나 다음 버킷 전환 시 버킷 플랜으로 되돌아간다 —
/// 회수된 슬롯을 예전 주인이 다시 읽으면 <see cref="TryGet"/> 이 거짓을 돌려주고,
/// 호출부는 버킷 플랜으로 되돌아간다.
/// </para>
/// </summary>
public sealed class IndividualPlanPool
{
    /// <summary>슬롯 수. docs/13 §2 의 512.</summary>
    public const int Capacity = 512;

    /// <summary>빈 슬롯의 주인 값.</summary>
    private const int Free = -1;

    private readonly CompiledPlan?[] _plans = new CompiledPlan?[Capacity];
    private readonly int[] _owner = new int[Capacity];   // npc 첨자. Free = 빈 칸
    private readonly long[] _touched = new long[Capacity];

    private long _assigned;
    private long _evicted;
    private int _cursor;

    /// <summary>풀을 만든다. 기동 시 1회. 이후 재할당하지 않는다.</summary>
    public IndividualPlanPool() => Array.Fill(_owner, Free);

    /// <summary>배정 누계. 회전율의 분자다.</summary>
    public long Assigned => Volatile.Read(ref _assigned);

    /// <summary>LRU 로 밀려난 누계. 이게 크면 개별 재계획이 과다하다 (docs/13 §6).</summary>
    public long Evicted => Volatile.Read(ref _evicted);

    /// <summary>
    /// 회전율 — 링을 몇 바퀴 돌았나. <c>Evicted / Capacity</c> 다.
    /// docs/13 §6 이 "히트율 98% 가 안 나오는 원인" 표에서 보라고 한 값이다.
    /// </summary>
    public double TurnoverRate => (double)Evicted / Capacity;

    /// <summary>지금 살아 있는 개별 플랜 수.</summary>
    public int Live
    {
        get
        {
            int live = 0;

            for (int i = 0; i < Capacity; i++)
            {
                if (Volatile.Read(ref _owner[i]) != Free)
                {
                    live++;
                }
            }

            return live;
        }
    }

    /// <summary>슬롯 → <c>NpcStore.PlanId</c> 값. 슬롯 0 은 -1 이다.</summary>
    public static int PlanIdOf(int slot) => ~slot;

    /// <summary><c>NpcStore.PlanId</c> 값 → 슬롯. 음수 id 에만 의미가 있다.</summary>
    public static int SlotOf(int planId) => ~planId;

    /// <summary>이 id 가 개별 플랜을 가리키는가.</summary>
    public static bool IsIndividual(int planId) => planId < 0;

    /// <summary>
    /// 플랜을 이 NPC 에게 배정하고 <c>NpcStore.PlanId</c> 에 넣을 음수 id 를 돌려준다.
    /// 빈 슬롯이 없으면 가장 오래 안 쓴 슬롯을 회수한다.
    /// </summary>
    /// <param name="plan">배정할 플랜.</param>
    /// <param name="npc">NpcStore 첨자.</param>
    /// <param name="tick">현재 틱. LRU 기준이다 — <c>DateTime</c> 을 쓰지 않는다 (CLAUDE.md §2.3).</param>
    public int Assign(CompiledPlan plan, int npc, long tick)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(npc);

        int slot = Claim(npc);

        Volatile.Write(ref _touched[slot], tick);
        Volatile.Write(ref _plans[slot], plan);
        Interlocked.Increment(ref _assigned);

        return PlanIdOf(slot);
    }

    /// <summary>
    /// 개별 플랜을 꺼낸다. 슬롯이 회수됐거나 주인이 바뀌었으면 거짓 —
    /// 호출부는 버킷 플랜으로 되돌아간다.
    /// </summary>
    /// <param name="planId">음수 id.</param>
    /// <param name="npc">주인이어야 하는 NPC.</param>
    /// <param name="tick">현재 틱. LRU 기준을 갱신한다.</param>
    /// <param name="plan">찾은 플랜.</param>
    public bool TryGet(int planId, int npc, long tick, out CompiledPlan plan)
    {
        int slot = SlotOf(planId);

        if (!IsIndividual(planId) || (uint)slot >= Capacity)
        {
            plan = null!;
            return false;
        }

        if (Volatile.Read(ref _owner[slot]) != npc)
        {
            plan = null!;
            return false;   // 회수됐다. 예전 주인은 버킷 플랜으로 돌아간다
        }

        CompiledPlan? found = Volatile.Read(ref _plans[slot]);

        if (found is null)
        {
            plan = null!;
            return false;
        }

        Volatile.Write(ref _touched[slot], tick);
        plan = found;
        return true;
    }

    /// <summary>
    /// 개별 플랜을 <b>LRU 를 갱신하지 않고</b> 본다.
    ///
    /// 계측·대시보드 경로가 쓴다. <see cref="TryGet"/> 를 쓰면 <c>/metrics</c> 를 폴링하는 것만으로
    /// LRU 순서가 바뀌어 <b>어느 NPC 가 슬롯을 잃는지가 대시보드 열람 여부에 달리게 된다</b> —
    /// 리플레이 100% 일치(W11 게이트)를 깨는 길이다 (CLAUDE.md §2.3).
    /// </summary>
    public bool TryPeek(int planId, int npc, out CompiledPlan plan)
    {
        int slot = SlotOf(planId);

        if (!IsIndividual(planId) || (uint)slot >= Capacity || Volatile.Read(ref _owner[slot]) != npc)
        {
            plan = null!;
            return false;
        }

        CompiledPlan? found = Volatile.Read(ref _plans[slot]);

        plan = found!;
        return found is not null;
    }

    /// <summary>플랜이 끝났다. 슬롯을 비운다. 주인이 아니면 아무 일도 하지 않는다.</summary>
    public bool Release(int planId, int npc)
    {
        int slot = SlotOf(planId);

        if (!IsIndividual(planId) || (uint)slot >= Capacity)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _owner[slot], Free, npc) != npc)
        {
            return false;
        }

        Volatile.Write(ref _plans[slot], null);
        return true;
    }

    /// <summary>이 슬롯의 주인. 빈 칸이면 -1. 테스트·메트릭용.</summary>
    public int OwnerOf(int planId)
    {
        int slot = SlotOf(planId);

        return !IsIndividual(planId) || (uint)slot >= Capacity ? Free : Volatile.Read(ref _owner[slot]);
    }

    /// <summary>
    /// 슬롯 하나를 확보한다. 빈 칸이 먼저고, 없으면 LRU 회수다.
    /// 커서에서 시작해 한 바퀴만 돈다 — 매번 0 부터 훑으면 앞쪽 슬롯만 재사용된다.
    /// </summary>
    private int Claim(int npc)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int start = Volatile.Read(ref _cursor);

            for (int i = 0; i < Capacity; i++)
            {
                int slot = (start + i) % Capacity;

                if (Interlocked.CompareExchange(ref _owner[slot], npc, Free) == Free)
                {
                    Volatile.Write(ref _cursor, (slot + 1) % Capacity);
                    return slot;
                }
            }

            // 꽉 찼다 — 가장 오래 안 쓴 칸을 뺏는다.
            int victim = 0;
            long oldest = long.MaxValue;

            for (int slot = 0; slot < Capacity; slot++)
            {
                long touched = Volatile.Read(ref _touched[slot]);

                if (touched < oldest)
                {
                    oldest = touched;
                    victim = slot;
                }
            }

            int previous = Volatile.Read(ref _owner[victim]);

            // 그 사이 다른 워커가 같은 칸을 가져갔으면 다시 고른다.
            if (previous != Free && Interlocked.CompareExchange(ref _owner[victim], npc, previous) == previous)
            {
                Interlocked.Increment(ref _evicted);
                Volatile.Write(ref _cursor, (victim + 1) % Capacity);
                return victim;
            }
        }

        throw new InvalidOperationException(
            $"개별 플랜 슬롯을 {Capacity} 칸에서 확보하지 못했다. 배정 경쟁이 비정상이다 (docs/13 §2).");
    }
}
