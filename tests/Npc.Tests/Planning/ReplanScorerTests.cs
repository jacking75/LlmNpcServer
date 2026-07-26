using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Planning;

/// <summary>docs/14 §2 — 근접 · 노후 · 이탈 · 긴급의 가중합.</summary>
public sealed class ReplanScorerTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>전제도 금지도 없는 플랜. 이탈 항이 항상 0 이다.</summary>
    private static CompiledPlan NeutralPlan() => PlanStore.CreateIdleOnly(s_data)[PlanStore.IdlePlanId];

    private static CompiledPlan PlanWith(WorldFlags required, WorldFlags forbidden) =>
        NeutralPlan() with { RequiredFlags = required, ForbiddenFlags = forbidden };

    /// <summary>
    /// T4-02 완료 조건 — LOD 3 은 긴급도가 없으면 0 점이다.
    ///
    /// 상위 계획 §2.2 의 "관측되지 않는 연산" 방어가 이 한 줄이다 —
    /// 아무도 보고 있지 않은 NPC 는 LLM 예산을 받지 않는다.
    /// </summary>
    [Fact]
    public void Scorer_Lod3ScoresZeroWithoutUrgency()
    {
        var now = new Tick(1_000);
        CompiledPlan plan = NeutralPlan();

        // 플랜이 방금 배정됐고 이탈도 긴급도도 없다 — 남는 항은 근접뿐이다.
        var idle = new NpcReplanState(NpcStore.InactiveLod, WorldFlags.None, now.Value, 0);

        Assert.Equal(0f, ReplanScorer.Score(in idle, plan, now, Weights.Default));
        Assert.Equal(0f, ReplanScorer.Proximity(NpcStore.InactiveLod));

        // 같은 조건의 LOD 0 은 W1 만큼 받는다.
        var watched = idle with { Lod = 0 };
        Assert.Equal(Weights.Default.W1, ReplanScorer.Score(in watched, plan, now, Weights.Default), 5);

        // 등급이 낮아질수록 점수가 준다 (docs/14 §2 의 1.0 / 0.5 / 0.1 / 0.0).
        Assert.True(ReplanScorer.Proximity(0) > ReplanScorer.Proximity(1));
        Assert.True(ReplanScorer.Proximity(1) > ReplanScorer.Proximity(2));
        Assert.True(ReplanScorer.Proximity(2) > ReplanScorer.Proximity(3));
    }

    /// <summary>
    /// LOD 3 이라도 인터럽트가 긴급도를 남기면 점수를 받는다 (docs/14 §2).
    ///
    /// <b>인터럽트의 절대 우선순위는 점수 함수가 아니라 <see cref="ReplanQueue.TryEnqueueUrgent"/>
    /// (1000 + urgency)가 보장한다.</b> 여기서 보는 것은 그 항목이 큐에서 밀려났을 때
    /// 다음 인지 스캔이 여전히 그 NPC 를 우대하는가다.
    /// </summary>
    [Fact]
    public void Scorer_Lod3StillScoresWithUrgency()
    {
        var now = new Tick(1_000);
        CompiledPlan plan = NeutralPlan();

        var urgent = new NpcReplanState(NpcStore.InactiveLod, WorldFlags.None, now.Value, 100);
        var idle = new NpcReplanState(NpcStore.InactiveLod, WorldFlags.None, now.Value, 0);

        // 비활성 NPC 는 긴급도만으로 점수를 받는다 — 근접 항이 0 이기 때문이다.
        Assert.Equal(Weights.Default.W4, ReplanScorer.Score(in urgent, plan, now, Weights.Default), 5);
        Assert.Equal(0f, ReplanScorer.Score(in idle, plan, now, Weights.Default));

        // 같은 비활성이라도 긴급도가 있으면 이긴다. 동일존(LOD 1)보다도 앞선다.
        var sameZone = new NpcReplanState(1, WorldFlags.None, now.Value, 0);

        Assert.True(
            ReplanScorer.Score(in urgent, plan, now, Weights.Default)
            > ReplanScorer.Score(in sameZone, plan, now, Weights.Default));

        // ⚠ 시야내(LOD 0)와는 세트에 따라 순서가 갈린다.
        //    A-proximity 는 W1(5.0) > W4(4.0) 이라 근접이 이기고, B-baseline 은 W1(3.0) < W4(4.0) 이라
        //    긴급이 이긴다. T4-17 A/B 가 A 를 골랐으므로 지금 기본값에서는 근접이 앞선다 —
        //    인터럽트는 이미 ForceAction 으로 즉시 반응했고 큐에도 1000+ 로 들어가 있다.
        var watched = new NpcReplanState(0, WorldFlags.None, now.Value, 0);

        Assert.Equal(
            Weights.Default.W1 > Weights.Default.W4,
            ReplanScorer.Score(in watched, plan, now, Weights.Default)
            > ReplanScorer.Score(in urgent, plan, now, Weights.Default));

        Assert.True(
            ReplanScorer.Score(in urgent, plan, now, Weights.Baseline)
            > ReplanScorer.Score(in watched, plan, now, Weights.Baseline));
    }

    /// <summary>T4-02 완료 조건 — 이탈 항은 어긋난 비트 수(PopCount)에 비례한다.</summary>
    [Fact]
    public void Scorer_DeviationUsesPopCount()
    {
        // 모자란 전제 1개 → 1/8.
        Assert.Equal(
            1f / ReplanScorer.DeviationScale,
            ReplanScorer.Deviation(WorldFlags.AtWorkplace, WorldFlags.None, WorldFlags.None),
            5);

        // 3개 → 3/8.
        WorldFlags three = WorldFlags.AtWorkplace | WorldFlags.HasTool | WorldFlags.IsRested;
        Assert.Equal(
            3f / ReplanScorer.DeviationScale,
            ReplanScorer.Deviation(three, WorldFlags.None, WorldFlags.None),
            5);

        // 이미 서 있는 전제는 세지 않는다.
        Assert.Equal(0f, ReplanScorer.Deviation(three, WorldFlags.None, three));

        // 금지 플래그도 같은 무게로 센다.
        Assert.Equal(
            2f / ReplanScorer.DeviationScale,
            ReplanScorer.Deviation(
                WorldFlags.None,
                WorldFlags.InCombat | WorldFlags.IsInjured,
                WorldFlags.InCombat | WorldFlags.IsInjured | WorldFlags.AtHome),
            5);

        // 모자란 전제와 금지 위반은 합산된다.
        Assert.Equal(
            2f / ReplanScorer.DeviationScale,
            ReplanScorer.Deviation(WorldFlags.AtWorkplace, WorldFlags.InCombat, WorldFlags.InCombat),
            5);

        // 8비트를 넘으면 1.0 에서 포화한다.
        Assert.Equal(1f, ReplanScorer.Deviation((WorldFlags)0x3FFFF, WorldFlags.None, WorldFlags.None));

        // 점수 전체에서도 이탈이 많은 쪽이 크다.
        var now = new Tick(500);
        var clean = new NpcReplanState(1, three, now.Value, 0);
        var broken = new NpcReplanState(1, WorldFlags.None, now.Value, 0);

        Assert.True(
            ReplanScorer.Score(in broken, PlanWith(three, WorldFlags.None), now, Weights.Default)
            > ReplanScorer.Score(in clean, PlanWith(three, WorldFlags.None), now, Weights.Default));
    }

    /// <summary>노후 항은 StaleTicks 에서 포화한다. 음수 나이는 0 이다.</summary>
    [Fact]
    public void Scorer_StalenessSaturatesAtStaleTicks()
    {
        int stale = Weights.DefaultStaleTicks;

        Assert.Equal(0f, ReplanScorer.Staleness(0, new Tick(0), stale));
        Assert.Equal(0.5f, ReplanScorer.Staleness(0, new Tick(stale / 2), stale), 5);
        Assert.Equal(1f, ReplanScorer.Staleness(0, new Tick(stale), stale));
        Assert.Equal(1f, ReplanScorer.Staleness(0, new Tick(stale * 10), stale));

        // 배정 틱이 미래면(리플레이 경계) 0 으로 클램프한다.
        Assert.Equal(0f, ReplanScorer.Staleness(100, new Tick(50), stale));
        Assert.Equal(0f, ReplanScorer.Staleness(0, new Tick(100), 0));
    }

    /// <summary>docs/14 §2 표의 A/B 4세트가 그대로 있어야 T4-17 이 돈다.</summary>
    [Fact]
    public void Scorer_AbSetsMatchSpecTable()
    {
        Assert.Equal(4, Weights.AbSets.Length);

        Assert.Equal(new Weights(5.0f, 0.2f, 1.5f, 4.0f, 36_000), Weights.ProximityFirst);
        Assert.Equal(new Weights(3.0f, 0.5f, 2.0f, 4.0f, 36_000), Weights.Baseline);
        Assert.Equal(new Weights(2.0f, 0.3f, 4.0f, 4.0f, 36_000), Weights.DeviationFirst);
        Assert.Equal(new Weights(1.0f, 1.0f, 1.0f, 1.0f, 36_000), Weights.Uniform);

        // 점수 상한이 인터럽트 기준값과 겹치면 우선순위가 뒤섞인다.
        foreach ((string name, Weights w) in Weights.AbSets)
        {
            Assert.True(
                w.W1 + w.W2 + w.W3 + w.W4 < ReplanQueue.UrgentBase,
                $"{name} 의 점수 상한이 인터럽트 기준값을 넘는다.");
        }
    }

    /// <summary>
    /// T4-02 완료 조건 — 핫 배열 크기가 그대로다.
    /// <c>PlanAssignedTick</c>·<c>PendingUrgency</c> 를 핫에 넣으면 docs/11 §3 의 L2 목표가 깨진다.
    /// </summary>
    [Fact]
    public void NpcStore_HotBytesUnchanged()
    {
        // Flags 8 + PlanId 4 + StepIndex 1 + StepStatus 1 + StepIssuedTick 8 + Lod 1 = 23
        Assert.Equal(23, NpcStore.HotBytesPerNpc);

        var store = new NpcStore();
        store.Allocate(5_000, s_data.Items.MaxCode + 1);

        Assert.Equal(23L * 5_000, store.HotBytes);
        Assert.True(store.HotBytes <= 128 * 1024, "핫 배열이 L2(128KB) 를 넘는다.");

        // 새 배열은 콜드 영역이라 길이만 확인한다.
        Assert.Equal(5_000, store.PlanAssignedTick.Length);
        Assert.Equal(5_000, store.PendingUrgency.Length);
    }

    /// <summary>점수 계산 경로의 할당이 0 이다 — 인지 스캔 안에서 틱당 150회 불린다.</summary>
    [Fact]
    public void Scorer_DoesNotAllocate()
    {
        CompiledPlan plan = PlanWith(WorldFlags.AtWorkplace | WorldFlags.HasTool, WorldFlags.InCombat);
        var state = new NpcReplanState(1, WorldFlags.InCombat, 0, 40);
        float sink = 0;

        for (int i = 0; i < 20_000; i++)
        {
            sink += ReplanScorer.Score(in state, plan, new Tick(i), Weights.Default);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 20_000; i++)
        {
            sink += ReplanScorer.Score(in state, plan, new Tick(i), Weights.Default);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(sink > 0);
    }
}
