using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Tests.Llm;

/// <summary>
/// T5-08 — 킬스위치가 실제 티어를 끊는가. docs/14 §4 · docs/15 §4.
///
/// <b>LLM 을 부르지 않는다.</b> 티어 자리에 어느 티어가 불렸는지만 기록하는 가짜를 넣는다 —
/// 여기서 확인할 것은 라우팅이지 생성 품질이 아니다.
/// </summary>
public sealed class TieredPlanCompilerTests
{
    /// <summary>어느 티어가 불렸는지만 남기는 가짜 컴파일러.</summary>
    private sealed class Spy(string name, Exception? throws = null) : IPlanCompiler
    {
        public int Calls { get; private set; }

        public ValueTask<PlanCompileResult> CompileAsync(PlanRequest request, CancellationToken cancellationToken)
        {
            Calls++;

            if (throws is not null)
            {
                throw throws;
            }

            return ValueTask.FromResult(new PlanCompileResult(
                null, ValidationResult.Ok, CompileStats.None with { Model = name }, name));
        }
    }

    private static PlanRequest Archetype() =>
        new(new BucketKey(default, TimeOfDay.Morning, RegionState.Peace, Climate.Fair), WorldFlags.None);

    private static PlanRequest Individual() => Archetype() with { Quality = PlanQuality.Individual };

    // ---------------------------------------------------------------- 티어 선택 (docs/14 §4 표)

    [Fact]
    public void Tier_ArchetypeGoesToT2_AndIndividualToT1()
    {
        var router = new TieredPlanCompiler(new Spy("t2"), new Spy("t1"));

        // 수천 NPC 가 재사용하는 플랜에만 품질을 산다 (CLAUDE.md §8).
        Assert.Equal(PlanTier.T2, router.SelectTier(Archetype()));
        Assert.Equal(PlanTier.T1, router.SelectTier(Individual()));
    }

    // ---------------------------------------------------------------- 3종 차단 (T5-08 완료 조건)

    [Fact]
    public async Task KillSwitch_T2_FailsOverToT1()
    {
        var t2 = new Spy("t2");
        var t1 = new Spy("t1");
        var switches = new KillSwitchState();
        var router = new TieredPlanCompiler(t2, t1, switches);

        switches.Fire(KillSwitchTarget.T2);

        Assert.False(router.Available(PlanTier.T2));
        Assert.Equal(PlanTier.T1, router.SelectTier(Archetype()));

        PlanCompileResult result = await router.CompileAsync(Archetype(), CancellationToken.None);

        Assert.Equal("t1", result.ResponseText);
        Assert.Equal(0, t2.Calls);
        Assert.Equal(1, t1.Calls);
    }

    [Fact]
    public async Task KillSwitch_T1AndT2_RejectsWithoutThrowing()
    {
        var t2 = new Spy("t2");
        var t1 = new Spy("t1");
        var switches = new KillSwitchState();
        var router = new TieredPlanCompiler(t2, t1, switches);

        switches.Fire(KillSwitchTarget.T2);
        switches.Fire(KillSwitchTarget.T1);

        Assert.Equal(PlanTier.None, router.SelectTier(Archetype()));
        Assert.Equal(PlanTier.None, router.SelectTier(Individual()));

        // 거절도 결과이지 예외가 아니다 — 워커가 터지면 그게 크래시다.
        PlanCompileResult result = await router.CompileAsync(Individual(), CancellationToken.None);

        Assert.Null(result.Plan);
        Assert.Equal("V0.NO_TIER", result.Validation.Code);
        Assert.Equal(1, router.Rejections);
        Assert.Equal(0, t1.Calls);
        Assert.Equal(0, t2.Calls);
    }

    /// <summary>
    /// <c>PlanStore</c> 차단은 라우터가 아니라 플랜 스토어가 본다.
    /// 끊겨도 <c>Resolve</c> 는 <b>여전히 null 을 반환하지 않는다</b> — 폴백으로 답한다.
    /// </summary>
    [Fact]
    public void KillSwitch_PlanStore_FallsBackWithoutReturningNull()
    {
        MasterDataSet data = LlmPlanCompilerTests.Data;
        PlanStore plans = PlanStore.CreateIdleOnly(data);

        // 폴백 40개를 채우고, 버킷 하나에 다른 플랜을 올린다.
        foreach (FallbackPlanEntry entry in data.Fallbacks!.Plans)
        {
            plans.SetFallback(entry.Archetype, plans.Register(entry.Plan));
        }

        BucketKey bucket = LlmPlanCompilerTests.BlacksmithMorning();
        CompiledPlan prebaked = data.Fallbacks.Plans[1].Plan with
        {
            Bucket = bucket,
            Origin = PlanOrigin.Prebaked,
        };

        plans.SetBucket(bucket, prebaked);

        var switches = new KillSwitchState();
        plans.Switches = switches;

        CompiledPlan before = plans.Resolve(bucket, out PlanOrigin originBefore);

        switches.Fire(KillSwitchTarget.PlanStore);

        CompiledPlan after = plans.Resolve(bucket, out PlanOrigin originAfter);

        Assert.NotEqual(PlanOrigin.Fallback, originBefore);
        Assert.Equal(PlanOrigin.Fallback, originAfter);
        Assert.NotSame(before, after);

        // 절대 null 이 아니다 (CLAUDE.md §2.6) — 시나리오 C 3단계가 여기에 달려 있다.
        Assert.NotNull(after);
    }

    // ---------------------------------------------------------------- 페일오버 · 미구성

    [Fact]
    public async Task Tier_T2FailureFallsOverToT1()
    {
        var t2 = new Spy("t2", new HttpRequestException("503"));
        var t1 = new Spy("t1");
        var router = new TieredPlanCompiler(t2, t1);

        PlanCompileResult result = await router.CompileAsync(Archetype(), CancellationToken.None);

        Assert.Equal("t1", result.ResponseText);
        Assert.Equal(1, router.Failovers);
    }

    [Fact]
    public async Task Tier_MissingT2IsTreatedAsUnavailable()
    {
        var t1 = new Spy("t1");
        var router = new TieredPlanCompiler(null, t1);

        Assert.Equal(PlanTier.T1, router.SelectTier(Archetype()));

        PlanCompileResult result = await router.CompileAsync(Archetype(), CancellationToken.None);

        Assert.Equal("t1", result.ResponseText);
    }

    [Fact]
    public async Task Tier_NoCompilersAtAllRejects()
    {
        var router = new TieredPlanCompiler(null, null);

        PlanCompileResult result = await router.CompileAsync(Archetype(), CancellationToken.None);

        Assert.Equal("V0.NO_TIER", result.Validation.Code);
        Assert.NotNull(result.Stats.Error);
    }

    [Fact]
    public void KillSwitch_IsIdempotent()
    {
        var switches = new KillSwitchState();

        switches.Fire(KillSwitchTarget.T2);
        switches.Fire(KillSwitchTarget.T2);

        Assert.True(switches.IsDisabled(KillSwitchTarget.T2));
        Assert.True(switches.AnyFired);

        switches.Reset();

        Assert.False(switches.AnyFired);
    }

    [Fact]
    public void KillSwitch_ParsesOnlyTheThreeTargets()
    {
        Assert.True(KillSwitchState.TryParse("T1", out KillSwitchTarget t1));
        Assert.Equal(KillSwitchTarget.T1, t1);

        Assert.True(KillSwitchState.TryParse("planstore", out KillSwitchTarget store));
        Assert.Equal(KillSwitchTarget.PlanStore, store);

        Assert.False(KillSwitchState.TryParse("T3", out _));
        Assert.False(KillSwitchState.TryParse(null, out _));
        Assert.False(KillSwitchState.TryParse("  ", out _));

        // 열거형 ordinal 을 그대로 넣는 것도 막는다 — "2" 가 PlanStore 가 되면 오타를 못 잡는다.
        Assert.False(KillSwitchState.TryParse("2", out _));
    }
}
