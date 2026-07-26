using Npc.Contracts;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Runtime;

/// <summary>
/// 이벤트를 인터럽트 규칙에 매칭해 즉시 반응을 만든다. docs/01 §7 · docs/11 §5.
///
/// <b>LLM 이 개입하지 않는다.</b> 인터럽트는 반응 속도가 생명이라 결정론 규칙으로만 돈다
/// (CLAUDE.md §8 — 반응 속도가 필요하면 규칙).
///
/// 순서는 "먼저 도망치고, 새 계획은 나중에 받는다"다. <c>then</c> 액션을 그 틱에 발행하고,
/// <c>replan.urgency</c> 는 재계획 큐에 점수로 넣는다.
///
/// <b>발동은 엣지 트리거다.</b> 규칙 조건은 대부분 플래그라 한 번 참이 되면 한동안 참으로 남는다
/// (예: <c>go_home_at_night</c> 은 집에 닿을 때까지 참이다). 들어오는 이벤트마다 다시 쏘면
/// 강제 액션 → 완료 이벤트 → 다시 강제의 되먹임이 생겨 명령 수가 폭주한다.
/// 그래서 NPC 별로 마지막에 발동한 규칙을 기억하고, <b>같은 규칙이 이어서 걸리면 넘긴다</b>.
/// 조건이 한 번 풀렸다가 다시 참이 되면 그때 다시 쏜다. docs/01 §7.
/// </summary>
public sealed class InterruptMatcher
{
    private readonly MasterDataSet _data;
    private readonly NpcStore _store;
    private readonly InterruptRule?[] _armed;   // NPC 별 마지막으로 발동한 규칙

    /// <summary>매처를 만든다. 기동 시 1회.</summary>
    public InterruptMatcher(MasterDataSet data, NpcStore store)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(store);

        _data = data;
        _store = store;
        _armed = new InterruptRule?[store.Count];
    }

    /// <summary>매칭된 횟수.</summary>
    public long Matched { get; private set; }

    /// <summary>즉시 발행한 액션 수.</summary>
    public long Forced { get; private set; }

    /// <summary>같은 규칙이 이어서 걸려 넘긴 횟수. 되먹임 방지가 실제로 일하는 양이다.</summary>
    public long Suppressed { get; private set; }

    /// <summary>
    /// 이벤트에 걸리는 규칙을 찾는다. 없으면 false.
    /// 할당 0 — 틱 루프의 이벤트 배수 구간에서 돈다.
    /// </summary>
    public bool TryMatch(in GameEvent ev, out InterruptRule rule)
    {
        int npc = ev.Npc.Value;

        if ((uint)npc >= (uint)_store.Count)
        {
            rule = null!;
            return false;
        }

        ArchetypeDef archetype = _data.Archetypes[new ArchetypeId(_store.ArchetypeCode[npc])];

        if (!_data.Interrupts.TryMatch(in ev, _store.Flags[npc], archetype, out rule))
        {
            return false;
        }

        Matched++;
        return true;
    }

    /// <summary>
    /// 규칙의 <c>then</c> 을 실행 가능한 스텝으로 만든다.
    /// 위협의 정체는 규칙이 아니라 이벤트가 알려주므로 <paramref name="ev"/> 를 같이 본다.
    /// </summary>
    public CompiledStep ToStep(InterruptRule rule, in GameEvent ev)
    {
        ArgumentNullException.ThrowIfNull(rule);

        int seconds = rule.Amount > 0 ? rule.Amount : _data.DefaultTimeoutSeconds(rule.Action);

        return new CompiledStep(
            rule.Action,
            rule.Poi,
            default,
            (ushort)Math.Clamp(rule.Amount, 0, ushort.MaxValue),
            (ushort)Math.Clamp(seconds, PlanDocument.MinTimeoutSeconds, PlanDocument.MaxTimeoutSeconds),
            0,
            NpcRefCodes.None,
            0);

        // NpcRef 는 None 이다. 대상 NPC 는 EmitContext.Target 으로 들어간다 (CommandEmitter 참조).
    }

    /// <summary>
    /// 이벤트 하나를 처리한다. 규칙이 걸리면 즉시 액션을 발행하고 재계획 큐에 점수를 넣는다.
    /// 틱 루프의 이벤트 배수 구간이 부른다 (docs/11 §5).
    /// </summary>
    /// <returns>인터럽트가 걸렸으면 true.</returns>
    public bool Handle(in GameEvent ev, Tick tick, PlanExecutor executor, IGameServerLink link, ReplanQueue? queue)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(link);

        int npc = ev.Npc.Value;

        if (!TryMatch(in ev, out InterruptRule rule))
        {
            // 아무 규칙도 안 걸린다 = 상황이 풀렸다. 다음 진입을 다시 받을 수 있게 무장을 푼다.
            if ((uint)npc < (uint)_armed.Length)
            {
                _armed[npc] = null;
            }

            return false;
        }

        if (ReferenceEquals(_armed[npc], rule))
        {
            Suppressed++;
            return false;   // 같은 규칙이 계속 참이다. 이미 반응했다
        }

        _armed[npc] = rule;

        CompiledStep step = ToStep(rule, in ev);

        executor.ForceAction(npc, in step, tick, link, TargetOf(rule, in ev));
        Forced++;

        // 후속 재계획. 즉시 반응이 먼저고 계획은 나중이다.
        // 인터럽트는 일반 점수 범위를 넘어서게 넣는다 (docs/14 §2 의 1000 + urgency).
        queue?.TryEnqueueUrgent(npc, rule.Urgency);

        return true;
    }

    /// <summary><c>$threat</c> 대상. 위협의 정체는 이벤트에 있다.</summary>
    private static NpcId TargetOf(InterruptRule rule, in GameEvent ev) =>
        rule.TargetsThreat ? ev.OtherNpc : default;
}
