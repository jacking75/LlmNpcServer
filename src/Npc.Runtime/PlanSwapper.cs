using System.Threading;
using Npc.Contracts;

namespace Npc.Runtime;

/// <summary>
/// 플랜 스왑. docs/03 §6 스왑 규약.
///
/// 규약:
/// <list type="number">
///   <item>재계획 워커가 <c>PendingPlanId[i]</c> 에 새 PlanId 를 <see cref="Volatile.Write(ref int, int)"/> 한다.</item>
///   <item>실행기가 <b>스텝 경계에서만</b> 확인하고 스왑한다. StepIndex 는 0 으로 돌아간다.</item>
///   <item>인터럽트 시에는 즉시 스왑을 허용한다 — 진행 중 명령은 상관 ID 로 무시된다.</item>
/// </list>
///
/// <b>스텝 중간에 바꾸면 상관 ID 가 꼬인다.</b> 직전 스텝의 응답이 새 스텝의 완료로 오인된다.
///
/// 워커는 별도 스레드다. <c>lock</c> 을 쓰지 않고 <see cref="Volatile"/> 읽기/쓰기와
/// 원자 교환만으로 주고받는다 (CLAUDE.md §2.1).
/// </summary>
public sealed class PlanSwapper
{
    private readonly NpcStore _store;

    /// <summary>스왑기를 만든다. 기동 시 1회.</summary>
    public PlanSwapper(NpcStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <summary>워커가 요청한 스왑 수.</summary>
    public long Requested { get; private set; }

    /// <summary>실제로 적용된 스왑 수.</summary>
    public long Applied { get; private set; }

    /// <summary>스텝이 진행 중이라 다음 경계로 미룬 횟수.</summary>
    public long Deferred { get; private set; }

    /// <summary>인터럽트로 즉시 적용한 스왑 수.</summary>
    public long Immediate { get; private set; }

    /// <summary>
    /// 재계획 워커가 새 플랜을 건다. <b>다른 스레드에서 불린다.</b>
    /// 틱 루프의 상태를 직접 고치지 않고 여기에만 쓴다 (CLAUDE.md §7).
    /// </summary>
    public void Request(int npc, PlanId plan)
    {
        if ((uint)npc >= (uint)_store.Count)
        {
            return;
        }

        Volatile.Write(ref _store.PendingPlanId[npc], plan.Value);
        Requested++;
    }

    /// <summary>대기 중인 스왑이 있는가.</summary>
    public bool HasPending(int npc) => Volatile.Read(ref _store.PendingPlanId[npc]) != 0;

    /// <summary>
    /// 스텝 경계에서 실행기가 부른다. 대기 중인 플랜이 있으면 꺼내고 슬롯을 비운다.
    /// 원자 교환이라 워커와 경쟁해도 플랜을 잃거나 두 번 적용하지 않는다.
    /// </summary>
    public bool TryTake(int npc, out PlanId plan)
    {
        int pending = Interlocked.Exchange(ref _store.PendingPlanId[npc], 0);

        if (pending == 0)
        {
            plan = default;
            return false;
        }

        plan = new PlanId(pending);
        Applied++;
        return true;
    }

    /// <summary>
    /// 스텝 경계에 있지 않아 미룬 것들을 처리한다. 틱 루프가 실행기 뒤에 부른다 (docs/11 §5).
    /// 대기 중이지만 명령을 기다리고 있는 NPC 는 건드리지 않는다.
    /// </summary>
    /// <returns>이번 틱에 적용한 스왑 수.</returns>
    public int ApplyPendingSwaps(PlanExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);

        int applied = 0;

        for (int i = 0; i < _store.Count; i++)
        {
            if (Volatile.Read(ref _store.PendingPlanId[i]) == 0)
            {
                continue;
            }

            if ((StepStatus)_store.StepStatus[i] == StepStatus.Waiting)
            {
                // 스텝이 진행 중이다. 다음 경계에서 실행기가 가져간다.
                Deferred++;
                continue;
            }

            if (TryTake(i, out PlanId plan))
            {
                executor.AssignPlan(i, plan);
                applied++;
            }
        }

        return applied;
    }

    /// <summary>
    /// 인터럽트가 요구하는 즉시 스왑. 스텝 경계를 기다리지 않는다.
    /// 진행 중 명령의 상관 ID 는 <see cref="PlanExecutor.AssignPlan"/> 이 무효화한다.
    /// </summary>
    public void SwapNow(int npc, PlanId plan, PlanExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);

        Volatile.Write(ref _store.PendingPlanId[npc], 0);
        executor.AssignPlan(npc, plan);
        Immediate++;
    }
}
