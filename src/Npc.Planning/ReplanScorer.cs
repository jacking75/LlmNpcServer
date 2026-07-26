using System.Numerics;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;

namespace Npc.Planning;

/// <summary>
/// 재계획 우선순위 가중치. docs/14 §2.
///
/// <b><see cref="W1"/>(플레이어 근접도)을 크게 잡는 것이 핵심이다.</b>
/// 상위 계획 §2.2 의 "관측되지 않는 연산" 문제 — 아무도 보고 있지 않은 NPC 에 LLM 예산을 쓰는 것 —
/// 의 해결책이 이 한 계수다. LOD 3(비활성) NPC 는 근접도가 0 이라 인터럽트가 없는 한
/// 사실상 재계획되지 않는다.
/// </summary>
/// <param name="W1">플레이어 근접도 계수.</param>
/// <param name="W2">플랜 노후 계수.</param>
/// <param name="W3">전제 이탈 계수.</param>
/// <param name="W4">인터럽트 긴급도 계수.</param>
/// <param name="StaleTicks">이 틱만큼 지나면 노후 항이 1.0 이 된다.</param>
public readonly record struct Weights(float W1, float W2, float W3, float W4, int StaleTicks)
{
    /// <summary>노후 항이 포화하는 기본 틱 수. 배속 60 에서 게임시간 6시간이다.</summary>
    public const int DefaultStaleTicks = 36_000;

    /// <summary>
    /// 운영 기본값. <b>T4-17 의 A/B 결과가 이 값을 정한다</b> —
    /// 감으로 튜닝하면 재현이 안 된다 (docs/14 §10).
    /// </summary>
    public static readonly Weights Default = Baseline;

    /// <summary>A 세트 — 근접 우선. 플레이어 근처만 똑똑하다 (docs/14 §2 표).</summary>
    public static Weights ProximityFirst => new(5.0f, 0.2f, 1.5f, 4.0f, DefaultStaleTicks);

    /// <summary>B 세트 — 기본 (docs/14 §2 표).</summary>
    public static Weights Baseline => new(3.0f, 0.5f, 2.0f, 4.0f, DefaultStaleTicks);

    /// <summary>C 세트 — 이탈 우선. 플랜이 깨진 NPC 를 먼저 본다 (docs/14 §2 표).</summary>
    public static Weights DeviationFirst => new(2.0f, 0.3f, 4.0f, 4.0f, DefaultStaleTicks);

    /// <summary>D 세트 — 균등. 대조군 (docs/14 §2 표).</summary>
    public static Weights Uniform => new(1.0f, 1.0f, 1.0f, 1.0f, DefaultStaleTicks);

    /// <summary>docs/14 §2 표의 4세트. T4-17 의 A/B 가 이 순서로 돈다.</summary>
    public static (string Name, Weights Weights)[] AbSets =>
    [
        ("A-proximity", ProximityFirst),
        ("B-baseline", Baseline),
        ("C-deviation", DeviationFirst),
        ("D-uniform", Uniform),
    ];
}

/// <summary>
/// 점수 계산에 필요한 NPC 한 마리의 상태. docs/14 §2.
///
/// <b><c>NpcStore</c> 를 받지 않는다.</b> docs/14 §2 초안의
/// <c>Score(int i, NpcStore s, ...)</c> 는 <c>Npc.Planning → Npc.Runtime</c> 참조를 요구하는데
/// CLAUDE.md §3 이 그 간선을 금지한다 (<c>Npc.Runtime → Npc.Planning</c> 이 이미 있어 순환이 된다).
/// <c>IPlanVocabulary</c>(T1-23)·<c>IDryRunValidator</c>(T2-13) 와 같은 방법으로,
/// 계산에 실제로 쓰이는 네 값만 값 타입으로 받는다 — 복사 17바이트이고 할당은 0 이다.
/// </summary>
/// <param name="Lod">인지 LOD 등급 0~3. <c>NpcStore.Lod[i]</c>.</param>
/// <param name="Flags">현재 월드 플래그. <c>NpcStore.Flags[i]</c>.</param>
/// <param name="PlanAssignedTick">지금 플랜을 배정한 틱. <c>NpcStore.PlanAssignedTick[i]</c>.</param>
/// <param name="PendingUrgency">인터럽트가 남긴 긴급도 0~100. <c>NpcStore.PendingUrgency[i]</c>.</param>
public readonly record struct NpcReplanState(
    byte Lod,
    WorldFlags Flags,
    long PlanAssignedTick,
    byte PendingUrgency);

/// <summary>
/// 재계획 우선순위 점수. docs/14 §2.
///
/// 네 항의 가중합이다 — <b>근접 · 노후 · 이탈 · 긴급.</b> 각 항은 [0, 1] 로 정규화되므로
/// 점수 상한은 <c>W1+W2+W3+W4</c>(기본 9.5) 이고, 인터럽트가 넣는
/// <see cref="ReplanQueue.UrgentBase"/>(1000) 와 겹치지 않는다.
///
/// <b>할당이 0 이고 부동소수 연산만 한다.</b> 인지 스캔 안에서 틱당 최대 150회 불린다.
/// </summary>
public static class ReplanScorer
{
    /// <summary>긴급도 최댓값. <c>interrupts.json</c> 의 <c>replan.urgency</c> 상한이다 (docs/01 §7).</summary>
    public const float MaxUrgency = 100f;

    /// <summary>
    /// 이탈 항을 1.0 으로 만드는 비트 수. 플래그가 8개 어긋나면 "완전히 다른 상황" 으로 본다 —
    /// 42비트 전부로 나누면 실제 이탈(보통 1~3비트)이 0.05 밑으로 깔려 항이 죽는다.
    /// </summary>
    public const float DeviationScale = 8f;

    /// <summary>
    /// 점수 하나. docs/14 §2 의 <c>Score</c>.
    /// </summary>
    /// <param name="npc">NPC 상태.</param>
    /// <param name="plan">지금 실행 중인 플랜. 이탈 항의 기준이다.</param>
    /// <param name="now">현재 틱. <see cref="DateTime"/> 을 쓰지 않는다 (CLAUDE.md §2.3).</param>
    /// <param name="weights">가중치.</param>
    public static float Score(in NpcReplanState npc, CompiledPlan plan, Tick now, in Weights weights)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return (weights.W1 * Proximity(npc.Lod))
            + (weights.W2 * Staleness(npc.PlanAssignedTick, now, weights.StaleTicks))
            + (weights.W3 * Deviation(plan.RequiredFlags, plan.ForbiddenFlags, npc.Flags))
            + (weights.W4 * Urgency(npc.PendingUrgency));
    }

    /// <summary>
    /// 근접 항. <b>LOD 3 은 0 이다</b> — 아무도 보지 않는 NPC 에 예산을 쓰지 않는다 (docs/14 §2).
    /// </summary>
    public static float Proximity(byte lod) => lod switch
    {
        0 => 1.0f,
        1 => 0.5f,
        2 => 0.1f,
        _ => 0.0f,
    };

    /// <summary>노후 항. <paramref name="staleTicks"/> 를 넘으면 1.0 에서 포화한다.</summary>
    public static float Staleness(long planAssignedTick, Tick now, int staleTicks)
    {
        if (staleTicks <= 0)
        {
            return 0f;
        }

        long age = now.Value - planAssignedTick;

        return age <= 0 ? 0f : MathF.Min(1f, age / (float)staleTicks);
    }

    /// <summary>
    /// 이탈 항. <b>비트 연산 한 번 + PopCount 한 번</b> 이다 — 스텝을 순회하지 않는다 (docs/03 §5).
    /// 모자란 전제와 금지 플래그를 같은 무게로 센다.
    /// </summary>
    public static float Deviation(WorldFlags required, WorldFlags forbidden, WorldFlags current)
    {
        ulong missing = (ulong)(required & ~current);
        ulong violated = (ulong)(forbidden & current);

        return MathF.Min(1f, BitOperations.PopCount(missing | violated) / DeviationScale);
    }

    /// <summary>긴급 항. 인터럽트가 <c>NpcStore.PendingUrgency</c> 에 남긴 값이다.</summary>
    public static float Urgency(byte pendingUrgency) =>
        pendingUrgency == 0 ? 0f : MathF.Min(1f, pendingUrgency / MaxUrgency);
}
