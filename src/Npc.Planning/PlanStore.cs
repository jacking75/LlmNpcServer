using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Planning;

/// <summary>
/// 플랜 스토어. docs/13 §2 (정식은 P3).
///
/// <b>P1 에서는 폴백만 반환한다.</b> 프리베이크도 캐시도 아직 없다.
/// 그래도 <see cref="Resolve"/> 는 <b>절대 null 을 반환하지 않는다</b> —
/// 시나리오 C(LLM 전면 차단)가 통과하는 이유가 이것이다 (CLAUDE.md §2.6).
///
/// 조회는 버킷 인덱스 첨자 한 번이다. 해시맵이 필요 없다 (docs/01 §6).
/// </summary>
public sealed class PlanStore
{
    /// <summary>어떤 버킷·아키타입에도 폴백이 없을 때 쓰는 최후 플랜의 id.</summary>
    public const int IdlePlanId = 0;

    private readonly List<CompiledPlan> _plans;
    private readonly int[] _byBucket;        // 첨자 = BucketKey.ToIndex(). 0 = 없음
    private readonly int[] _byArchetype;     // 첨자 = ArchetypeId. 0 = 없음

    private PlanStore(CompiledPlan idle)
    {
        _plans = [idle];
        _byBucket = new int[BucketKey.TotalKeys];
        _byArchetype = new int[BucketKey.ArchetypeCount];
    }

    /// <summary>등록된 플랜 수 (최후 플랜 포함).</summary>
    public int Count => _plans.Count;

    /// <summary>버킷 슬롯 중 채워진 수. P3 의 히트율 계산이 쓴다.</summary>
    public int FilledBuckets
    {
        get
        {
            int filled = 0;
            foreach (int planId in _byBucket)
            {
                if (planId != IdlePlanId)
                {
                    filled++;
                }
            }

            return filled;
        }
    }

    /// <summary>아키타입 폴백 중 채워진 수.</summary>
    public int FilledFallbacks
    {
        get
        {
            int filled = 0;
            foreach (int planId in _byArchetype)
            {
                if (planId != IdlePlanId)
                {
                    filled++;
                }
            }

            return filled;
        }
    }

    /// <summary>
    /// 최후 플랜만 가진 스토어를 만든다.
    /// 최후 플랜은 전제조건이 하나도 없는 액션으로만 이뤄져 어떤 상황에서도 실행된다.
    /// </summary>
    public static PlanStore CreateIdleOnly(MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new PlanStore(BuildIdlePlan(data));
    }

    /// <summary>플랜을 등록하고 id 를 돌려준다.</summary>
    public PlanId Register(CompiledPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var id = new PlanId(_plans.Count);
        _plans.Add(plan with { Id = id });
        return id;
    }

    /// <summary>아키타입 폴백을 건다.</summary>
    public void SetFallback(ArchetypeId archetype, PlanId plan)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(archetype.Value, _byArchetype.Length);

        _byArchetype[archetype.Value] = plan.Value;
    }

    /// <summary>버킷 플랜을 건다. P3 의 프리베이크가 쓴다.</summary>
    public void SetBucket(BucketKey key, PlanId plan) => _byBucket[key.ToIndex()] = plan.Value;

    /// <summary>
    /// 버킷 → 플랜. <b>절대 null 이 아니다.</b>
    /// 버킷 미스면 아키타입 폴백, 그것도 없으면 최후 플랜을 준다.
    /// </summary>
    public CompiledPlan Resolve(BucketKey key)
    {
        int planId = _byBucket[key.ToIndex()];

        if (planId == IdlePlanId && key.A.Value < _byArchetype.Length)
        {
            planId = _byArchetype[key.A.Value];
        }

        return _plans[planId];
    }

    /// <summary>이 버킷이 캐시에 있는가. 히트율 측정용.</summary>
    public bool HasBucket(BucketKey key) => _byBucket[key.ToIndex()] != IdlePlanId;

    /// <summary>PlanId 로 조회. 첨자 한 번. 할당 0.</summary>
    public CompiledPlan this[PlanId id] =>
        (uint)id.Value < (uint)_plans.Count ? _plans[id.Value] : _plans[IdlePlanId];

    /// <summary>PlanId 로 조회 (int).</summary>
    public CompiledPlan this[int planId] =>
        (uint)planId < (uint)_plans.Count ? _plans[planId] : _plans[IdlePlanId];

    /// <summary>
    /// 최후 플랜. 전제조건이 없는 액션만 쓴다 —
    /// 어떤 상태에서도 실행되어야 NPC 가 멈추지 않는다.
    /// </summary>
    private static CompiledPlan BuildIdlePlan(MasterDataSet data)
    {
        var steps = ImmutableArray.CreateBuilder<CompiledStep>(3);
        var flagSets = ImmutableArray.CreateBuilder<StepFlags>(3);

        AddStep(data, steps, flagSets, "Wait", count: 60);
        AddStep(data, steps, flagSets, "Emote", count: 0);
        AddStep(data, steps, flagSets, "Rest", count: 600);

        return new CompiledPlan
        {
            Id = new PlanId(IdlePlanId),
            Bucket = default,
            Version = 1,
            Goal = "idle_fallback",
            Loop = true,
            OnFail = StepFailPolicy.Skip,
            RequiredFlags = WorldFlags.None,
            ForbiddenFlags = WorldFlags.None,
            Steps = steps.ToImmutable(),
            StepFlagSets = flagSets.ToImmutable(),
            Origin = PlanOrigin.Fallback,
        };
    }

    private static void AddStep(
        MasterDataSet data,
        ImmutableArray<CompiledStep>.Builder steps,
        ImmutableArray<StepFlags>.Builder flagSets,
        string actionId,
        int count)
    {
        if (!data.TryGetAction(actionId, out ActionId action))
        {
            throw new InvalidDataException($"최후 플랜이 쓰는 액션 '{actionId}' 가 카탈로그에 없다.");
        }

        StepFlags flags = data.FlagsOf(action);

        // 최후 플랜은 전제 없이 돌아야 한다. 카탈로그가 바뀌어 전제가 생기면 기동에서 잡는다.
        if (flags.Requires != WorldFlags.None || flags.RequiresAny != WorldFlags.None)
        {
            throw new InvalidDataException(
                $"최후 플랜이 전제조건 있는 액션 '{actionId}' 를 쓴다: {WorldFlagTable.Format(flags.Requires | flags.RequiresAny)}");
        }

        flagSets.Add(flags);
        steps.Add(new CompiledStep(
            action,
            PoiSymbol.None,
            default,
            (ushort)count,
            (ushort)data.DefaultTimeoutSeconds(action),
            0,
            NpcRefCodes.None,
            (ushort)(steps.Count)));
    }
}
