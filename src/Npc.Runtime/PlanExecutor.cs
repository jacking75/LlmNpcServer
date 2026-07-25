using Npc.Contracts;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Runtime;

/// <summary>
/// 플랜 스텝을 실행하고 명령을 발행한다. docs/03 §6.
///
/// SoA 배열을 순서대로 훑는다. <b>루프 안에 <c>await</c> 도 LINQ 도 문자열 보간도 없다</b> (CLAUDE.md §2.1).
/// 명령 버퍼는 사전 할당이고, 발행 경로의 할당은 0 이다.
/// </summary>
public sealed class PlanExecutor
{
    private readonly NpcStore _store;
    private readonly PlanStore _plans;
    private readonly CorrelationTable _correlations;
    private readonly CommandEmitter _emitter;
    private readonly MasterDataSet _data;
    private readonly NpcCommand[] _batch = new NpcCommand[CommandEmitter.MaxCommandsPerStep];

    /// <summary>실행기를 만든다. 기동 시 1회.</summary>
    public PlanExecutor(
        MasterDataSet data,
        NpcStore store,
        PlanStore plans,
        CorrelationTable correlations,
        CommandEmitter emitter)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(correlations);
        ArgumentNullException.ThrowIfNull(emitter);

        _data = data;
        _store = store;
        _plans = plans;
        _correlations = correlations;
        _emitter = emitter;
    }

    /// <summary>발행한 명령 수.</summary>
    public long CommandsEmitted { get; private set; }

    /// <summary>전진한 스텝 수.</summary>
    public long StepsAdvanced { get; private set; }

    /// <summary>정책에 따라 폴백으로 전환한 횟수.</summary>
    public long FallbacksTaken { get; private set; }

    /// <summary>재계획을 요청받은 횟수 (on_step_fail = replan).</summary>
    public long ReplansRequested { get; private set; }

    /// <summary>재계획 요청을 받아 갈 큐. 없으면 요청을 세기만 한다.</summary>
    public ReplanQueue? ReplanQueue { get; init; }

    /// <summary>
    /// 한 틱. 전원을 훑으며 완료·실패를 처리하고 준비된 스텝의 명령을 낸다.
    /// </summary>
    public void Step(Tick tick, IGameServerLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        for (int i = 0; i < _store.Count; i++)
        {
            switch ((StepStatus)_store.StepStatus[i])
            {
                case StepStatus.Unspawned:
                case StepStatus.Done:
                    continue;

                case StepStatus.Waiting:
                    OnWaiting(i, tick);
                    continue;

                case StepStatus.Completed:
                    AdvanceStep(i);
                    break;

                case StepStatus.Failed:
                    ApplyFailPolicy(i);
                    break;

                case StepStatus.Ready:
                default:
                    break;
            }

            if ((StepStatus)_store.StepStatus[i] == StepStatus.Ready)
            {
                Emit(i, tick, link);
            }
        }
    }

    /// <summary>
    /// 인터럽트가 강제하는 즉시 액션. docs/01 §7.
    /// 진행 중인 명령은 상관 ID 무효화로 무시된다 (docs/03 §6).
    /// </summary>
    public void ForceAction(int npc, in CompiledStep step, Tick tick, IGameServerLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        _correlations.Invalidate(npc);

        CorrelationId correlation = _correlations.Next(npc);
        int count = _emitter.Emit(step, ContextOf(npc, tick, correlation), _store.ReadInventoryOf(npc), _batch);

        for (int c = 0; c < count; c++)
        {
            link.Enqueue(in _batch[c]);
        }

        CommandsEmitted += count;

        // 인터럽트 액션이 끝나면 원래 플랜의 현재 스텝을 다시 낸다.
        _store.StepStatus[npc] = (byte)StepStatus.Waiting;
        _store.StepIssuedTick[npc] = tick.Value;
    }

    /// <summary>플랜을 갈아끼운다. 스텝은 0 으로 되돌리고 상관 ID 를 무효화한다.</summary>
    public void AssignPlan(int npc, PlanId plan)
    {
        _store.PlanId[npc] = plan.Value;
        _store.StepIndex[npc] = 0;
        _store.StepRetries[npc] = 0;
        _correlations.Invalidate(npc);

        if ((StepStatus)_store.StepStatus[npc] != StepStatus.Unspawned)
        {
            _store.StepStatus[npc] = (byte)StepStatus.Ready;
        }
    }

    // ---------------------------------------------------------------- 내부

    /// <summary>대기 중인 스텝의 타임아웃을 본다. T1-33 에서 채운다.</summary>
    private void OnWaiting(int npc, Tick tick) => SynthesizeTimeout(npc, tick);

    private void AdvanceStep(int npc)
    {
        CompiledPlan plan = _plans[_store.PlanId[npc]];
        int next = _store.StepIndex[npc] + 1;

        _store.StepRetries[npc] = 0;
        StepsAdvanced++;

        if (next < plan.Steps.Length)
        {
            _store.StepIndex[npc] = (byte)next;
            _store.StepStatus[npc] = (byte)StepStatus.Ready;
            return;
        }

        if (plan.Loop)
        {
            _store.StepIndex[npc] = 0;
            _store.StepStatus[npc] = (byte)StepStatus.Ready;
            return;
        }

        _store.StepIndex[npc] = (byte)(plan.Steps.Length - 1);
        _store.StepStatus[npc] = (byte)StepStatus.Done;
    }

    private void ApplyFailPolicy(int npc)
    {
        CompiledPlan plan = _plans[_store.PlanId[npc]];

        switch (plan.OnFail)
        {
            case StepFailPolicy.RetryOnce when _store.StepRetries[npc] == 0:
                _store.StepRetries[npc] = 1;
                _store.StepStatus[npc] = (byte)StepStatus.Ready;
                return;

            case StepFailPolicy.Replan:
                ReplansRequested++;
                ReplanQueue?.TryEnqueue(npc, 50);
                AdvanceStep(npc);
                return;

            case StepFailPolicy.Skip:
                AdvanceStep(npc);
                return;

            case StepFailPolicy.RetryOnce:
            case StepFailPolicy.Fallback:
            default:
                SwitchToFallback(npc);
                return;
        }
    }

    /// <summary>아키타입 폴백으로 전환한다. PlanStore.Resolve 는 절대 null 이 아니다.</summary>
    private void SwitchToFallback(int npc)
    {
        var bucket = new Core.BucketKey(
            new ArchetypeId(_store.ArchetypeCode[npc]),
            Core.TimeOfDay.Morning,
            Core.RegionState.Peace,
            Core.Climate.Fair);

        CompiledPlan fallback = _plans.Resolve(bucket with { A = new ArchetypeId(_store.ArchetypeCode[npc]) });

        FallbacksTaken++;
        AssignPlan(npc, fallback.Id);
    }

    private void Emit(int npc, Tick tick, IGameServerLink link)
    {
        CompiledPlan plan = _plans[_store.PlanId[npc]];

        if (plan.Steps.IsEmpty)
        {
            _store.StepStatus[npc] = (byte)StepStatus.Done;
            return;
        }

        int index = Math.Min(_store.StepIndex[npc], plan.Steps.Length - 1);
        CompiledStep step = plan.Steps[index];

        CorrelationId correlation = _correlations.Next(npc);
        int count = _emitter.Emit(step, ContextOf(npc, tick, correlation), _store.ReadInventoryOf(npc), _batch);

        for (int c = 0; c < count; c++)
        {
            link.Enqueue(in _batch[c]);
        }

        CommandsEmitted += count;
        _store.StepStatus[npc] = (byte)StepStatus.Waiting;
        _store.StepIssuedTick[npc] = tick.Value;
    }

    private EmitContext ContextOf(int npc, Tick tick, CorrelationId correlation) => new(
        new NpcId(npc),
        new ArchetypeId(_store.ArchetypeCode[npc]),
        tick,
        correlation,
        new PoiId(_store.HomePoi[npc]),
        new PoiId(_store.WorkPoi[npc]),
        new PoiId(_store.CurrentPoi[npc]),
        new ZoneId(_store.ZoneCode[npc]));

    /// <summary>T1-33 에서 구현한다.</summary>
    private void SynthesizeTimeout(int npc, Tick tick)
    {
        _ = npc;
        _ = tick;
        _ = _data;
    }
}
