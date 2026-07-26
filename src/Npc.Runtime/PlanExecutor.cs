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
    /// <summary>
    /// <c>on_step_fail = replan</c> 로 큐에 넣을 때의 점수.
    ///
    /// 인지 스캔의 이탈 판정(최대 40)보다는 급하고 인터럽트(<see cref="ReplanQueue.UrgentBase"/> 이상)보다는
    /// 느긋하다 — 플랜이 실제로 실패한 NPC 는 "곧 이탈할지도 모르는" NPC 보다 먼저 봐야 한다.
    /// </summary>
    public const float ReplanOnFailScore = 50f;

    private readonly NpcStore _store;
    private readonly PlanStore _plans;
    private readonly CorrelationTable _correlations;
    private readonly CommandEmitter _emitter;
    private readonly MasterDataSet _data;
    private readonly int _timeScale;
    private readonly NpcCommand[] _batch = new NpcCommand[CommandEmitter.MaxCommandsPerStep];

    /// <summary>
    /// 마지막으로 처리한 틱. <see cref="AssignPlan"/> 이 배정 시각을 남길 때 쓴다 (docs/14 §2).
    /// 실행기가 현재 시각을 스스로 알면 안 되므로 <see cref="Step"/> 이 들고 온 값만 기억한다 —
    /// <c>GameClock</c> 을 참조하지 않는 이유와 같다.
    /// </summary>
    private long _tick;

    /// <summary>실행기를 만든다. 기동 시 1회.</summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="store">NPC 상태.</param>
    /// <param name="plans">플랜 스토어.</param>
    /// <param name="correlations">상관 ID 표.</param>
    /// <param name="emitter">명령 발행기.</param>
    /// <param name="timeScale">
    /// 게임 시간 배속. <c>timeout_s</c>(게임 초)를 틱으로 바꿀 때 쓴다.
    /// GameClock 을 통째로 받지 않는 이유는 실행기가 현재 시각을 몰라야 하기 때문이다 —
    /// 틱은 인자로만 들어온다.
    /// </param>
    public PlanExecutor(
        MasterDataSet data,
        NpcStore store,
        PlanStore plans,
        CorrelationTable correlations,
        CommandEmitter emitter,
        int timeScale = 1)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(correlations);
        ArgumentNullException.ThrowIfNull(emitter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeScale);

        _data = data;
        _store = store;
        _plans = plans;
        _correlations = correlations;
        _emitter = emitter;
        _timeScale = timeScale;
    }

    /// <summary>발행한 명령 수.</summary>
    public long CommandsEmitted { get; private set; }

    /// <summary>전진한 스텝 수.</summary>
    public long StepsAdvanced { get; private set; }

    /// <summary>정책에 따라 폴백으로 전환한 횟수.</summary>
    public long FallbacksTaken { get; private set; }

    /// <summary>재계획을 요청받은 횟수 (on_step_fail = replan).</summary>
    public long ReplansRequested { get; private set; }

    /// <summary>명령 유실을 막으려고 로컬에서 합성한 ActionFailed(Timeout) 수.</summary>
    public long TimeoutsSynthesized { get; private set; }

    /// <summary>재계획 요청을 받아 갈 큐. 없으면 요청을 세기만 한다.</summary>
    public ReplanQueue? ReplanQueue { get; init; }

    /// <summary>
    /// 플랜 스왑기. 스텝 경계마다 대기 중인 플랜이 있는지 확인한다 (docs/03 §6).
    /// 없으면 스왑을 하지 않는다.
    /// </summary>
    public PlanSwapper? Swapper { get; init; }

    /// <summary>
    /// 한 틱. 전원을 훑으며 완료·실패를 처리하고 준비된 스텝의 명령을 낸다.
    /// </summary>
    public void Step(Tick tick, IGameServerLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        _tick = tick.Value;

        for (int i = 0; i < _store.Count; i++)
        {
            switch ((StepStatus)_store.StepStatus[i])
            {
                case StepStatus.Unspawned:
                case StepStatus.Done:
                    continue;

                case StepStatus.Waiting:
                    if (!SynthesizeTimeout(i, tick))
                    {
                        continue;
                    }

                    ApplyFailPolicy(i);
                    break;

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
    public void ForceAction(
        int npc, in CompiledStep step, Tick tick, IGameServerLink link, NpcId target = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        if ((StepStatus)_store.StepStatus[npc] == StepStatus.Unspawned)
        {
            return;
        }

        _tick = tick.Value;
        _correlations.Invalidate(npc);

        CorrelationId correlation = _correlations.Next(npc);
        EmitContext ctx = ContextOf(npc, tick, correlation) with { Target = target };
        int count = _emitter.Emit(step, ctx, _store.ReadInventoryOf(npc), _batch);

        for (int c = 0; c < count; c++)
        {
            link.Enqueue(in _batch[c]);
        }

        CommandsEmitted += count;

        // 인터럽트 액션이 끝나면 원래 플랜의 현재 스텝을 다시 낸다.
        _store.StepStatus[npc] = (byte)StepStatus.Waiting;
        _store.StepIssuedTick[npc] = tick.Value;
    }

    /// <summary>
    /// 플랜을 갈아끼운다. 스텝은 0 으로 되돌리고 상관 ID 를 무효화한다.
    ///
    /// 배정 시각과 긴급도도 여기서 정리한다 — 재계획 점수의 "노후"·"긴급" 항의 기준점이다
    /// (docs/14 §2). 시각은 마지막 <see cref="Step"/> 의 틱이다. 기동 시 인구 배치처럼
    /// 틱 루프 밖에서 부르면 0 이고, 그것이 곧 "게임 시작부터 이 플랜" 이라 맞다.
    /// </summary>
    public void AssignPlan(int npc, PlanId plan)
    {
        int previous = _store.PlanId[npc];

        _store.PlanId[npc] = plan.Value;
        _store.StepIndex[npc] = 0;
        _store.StepRetries[npc] = 0;
        _store.PlanAssignedTick[npc] = _tick;
        _store.PendingUrgency[npc] = 0;
        _correlations.Invalidate(npc);

        // 개별 플랜에서 벗어나면 슬롯을 즉시 놓아준다. LRU 회수만 믿으면 512칸이
        // 죽은 플랜으로 차서 새 재계획이 남의 슬롯을 뺏는다 (docs/13 §6 풀 회전율).
        if (previous != plan.Value && IndividualPlanPool.IsIndividual(previous))
        {
            _plans.Individual?.Release(previous, npc);
        }

        if ((StepStatus)_store.StepStatus[npc] != StepStatus.Unspawned)
        {
            _store.StepStatus[npc] = (byte)StepStatus.Ready;
        }
    }

    // ---------------------------------------------------------------- 내부

    /// <summary>
    /// 이 NPC 의 현재 플랜. 개별 풀 슬롯(음수 id)이 회수됐으면 아키타입 폴백으로 되돌린다.
    ///
    /// <b>되돌리는 것이 이 메서드의 요점이다.</b> 개별 플랜은 512칸 링이라 LRU 로 회수되는데
    /// (docs/13 §2), 예전 주인이 회수된 슬롯을 계속 읽으면 최후 플랜(Wait·Emote·Rest)에 갇힌다.
    /// </summary>
    private CompiledPlan PlanOf(int npc)
    {
        if (_plans.TryFor(npc, _store.PlanId[npc], _tick, out CompiledPlan plan))
        {
            return plan;
        }

        SwitchToFallback(npc);
        return _plans[_store.PlanId[npc]];
    }

    private void AdvanceStep(int npc)
    {
        _store.StepRetries[npc] = 0;
        StepsAdvanced++;

        // 스텝 경계다. 워커가 걸어둔 새 플랜이 있으면 여기서 갈아끼운다 (docs/03 §6).
        // 스텝 중간에 바꾸면 직전 스텝의 응답이 새 스텝의 완료로 오인된다.
        if (Swapper is not null && Swapper.TryTake(npc, out PlanId swapped))
        {
            AssignPlan(npc, swapped);
            return;
        }

        CompiledPlan plan = PlanOf(npc);
        int next = _store.StepIndex[npc] + 1;

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
        CompiledPlan plan = PlanOf(npc);

        switch (plan.OnFail)
        {
            case StepFailPolicy.RetryOnce when _store.StepRetries[npc] == 0:
                _store.StepRetries[npc] = 1;
                _store.StepStatus[npc] = (byte)StepStatus.Ready;
                return;

            case StepFailPolicy.Replan:
                ReplansRequested++;
                ReplanQueue?.TryEnqueue(npc, ReplanOnFailScore);
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
        CompiledPlan plan = PlanOf(npc);

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

    /// <summary>
    /// 명령 유실 방어. docs/02 §1 · docs/03 §6.
    ///
    /// <b>명령은 유실된다고 가정한다.</b> 게임서버가 응답 이벤트를 보내지 않아도 NPC 가
    /// 영원히 멈추면 안 되므로, timeout_s 가 지나면 로컬에서 ActionFailed(Timeout) 을 합성한다.
    /// 이 경로는 Npc.Sim --drop-rate 로 상시 테스트한다.
    /// </summary>
    /// <returns>타임아웃이 발생해 스텝이 실패로 바뀌었으면 true.</returns>
    private bool SynthesizeTimeout(int npc, Tick tick)
    {
        CompiledPlan plan = PlanOf(npc);

        if (plan.Steps.IsEmpty)
        {
            return false;
        }

        int index = Math.Min(_store.StepIndex[npc], plan.Steps.Length - 1);
        long budget = TimeoutTicks(plan.Steps[index].TimeoutSeconds);

        if (tick.Value - _store.StepIssuedTick[npc] < budget)
        {
            return false;
        }

        _correlations.Invalidate(npc);
        _store.StepStatus[npc] = (byte)StepStatus.Failed;
        _store.LastFailReason[npc] = (byte)ActionFailReason.Timeout;
        TimeoutsSynthesized++;
        return true;
    }

    /// <summary>게임 초 → 틱. 게임초 = 틱 × TimeScale ÷ 10 의 역이다 (docs/11 §5).</summary>
    public long TimeoutTicks(int timeoutSeconds)
    {
        long ticks = (long)timeoutSeconds * Tick.PerSecond / _timeScale;
        return ticks < 1 ? 1 : ticks;
    }
}
