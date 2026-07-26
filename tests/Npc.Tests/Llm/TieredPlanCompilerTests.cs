using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.Llm;

namespace Npc.Tests.Llm;

/// <summary>docs/14 §4 — 티어 선택 규칙표 6개 케이스와 페일오버.</summary>
public sealed class TieredPlanCompilerTests
{
    /// <summary>어느 티어로 불렸는지만 세는 가짜 컴파일러.</summary>
    private sealed class StubCompiler(string model, bool throws = false, string? error = null) : IPlanCompiler
    {
        public int Calls { get; private set; }

        public ValueTask<PlanCompileResult> CompileAsync(PlanRequest request, CancellationToken cancellationToken)
        {
            Calls++;

            if (throws)
            {
                throw new HttpRequestException($"{model} 가 죽었다");
            }

            return ValueTask.FromResult(new PlanCompileResult(
                null,
                ValidationResult.Ok,
                CompileStats.None with
                {
                    Model = model,
                    PromptTokens = 13_488,
                    CompletionTokens = 300,
                    Error = error,
                },
                "{}"));
        }
    }

    /// <summary>예산을 무한으로 열어 둔 판정기. 티어 선택만 보고 싶을 때 쓴다.</summary>
    private sealed class OpenBudget : IReplanBudget
    {
        public List<Tier> Acquired { get; } = [];

        public Tier Acquire(Tier requested, int estimatedTokens, Tick now)
        {
            Acquired.Add(requested);
            return requested;
        }

        public bool TryAcquire(Tier tier, int estimatedTokens, Tick now) => tier != Tier.None;

        public bool Peek(Tier tier, int estimatedTokens, Tick now) => tier != Tier.None;

        public double EstimateCost(Tier tier, int tokens) => 0;

        public void Settle(Tier tier, int actualTokens, double actualCostUsd)
        {
        }
    }

    /// <summary>요청한 티어를 정해진 티어로 바꿔주는 판정기. 강등·거절을 흉내낸다.</summary>
    private sealed class FixedBudget(Tier granted) : IReplanBudget
    {
        public Tier Acquire(Tier requested, int estimatedTokens, Tick now) => granted;

        public bool TryAcquire(Tier tier, int estimatedTokens, Tick now) => tier == granted;

        public bool Peek(Tier tier, int estimatedTokens, Tick now) => tier == granted;

        public double EstimateCost(Tier tier, int tokens) => 0;

        public void Settle(Tier tier, int actualTokens, double actualCostUsd)
        {
        }
    }

    private static PlanRequest Archetype() => new(
        new BucketKey(new ArchetypeId(3), TimeOfDay.Morning, RegionState.Peace, Climate.Fair),
        WorldFlags.IsDay,
        PlanQuality.Archetype);

    private static PlanRequest Individual() => new(
        new BucketKey(new ArchetypeId(3), TimeOfDay.Morning, RegionState.Peace, Climate.Fair),
        WorldFlags.IsDay,
        PlanQuality.Individual,
        new NpcSnapshot([], []));

    private static TieredPlanCompiler New(
        IPlanCompiler local,
        IPlanCompiler external,
        IReplanBudget budget,
        Func<int>? depth = null) =>
        new(local, external, budget, () => new Tick(0)) { LocalQueueDepth = depth };

    /// <summary>
    /// T4-07 완료 조건 — docs/14 §4 표의 6개 케이스.
    /// <b>재사용 횟수에 비례해 품질에 투자한다</b> (CLAUDE.md §8).
    /// </summary>
    [Fact]
    public void Tier_SelectsPerSpecTable()
    {
        var budget = new OpenBudget();
        var t1 = new StubCompiler("local");
        var t2 = new StubCompiler("external");

        // 1행 · 2행 — 아키타입 플랜(프리베이크 · 런타임 미스). 둘은 같은 요청이라 같은 티어다.
        TieredPlanCompiler router = New(t1, t2, budget);
        Assert.Equal(Tier.T2, router.SelectTier(Archetype()));

        // 3행 — 개별 NPC 재계획 (LOD 0)
        Assert.Equal(Tier.T1, router.SelectTier(Individual()));

        // 4행 — 개별 NPC 재계획 (LOD 1+). LOD 는 티어를 가르지 않는다. 1회용이면 T1 이다
        // (LOD 는 재계획 *우선순위*를 가른다 — ReplanScorer 의 근접 항).
        Assert.Equal(Tier.T1, router.SelectTier(Individual()));

        // 5행 — T1 큐 깊이 > 64 → 스필오버
        int depth = 0;
        TieredPlanCompiler spilling = New(t1, t2, budget, () => depth);

        depth = 64;
        Assert.Equal(Tier.T1, spilling.SelectTier(Individual()));   // 임계는 "초과" 다

        depth = 65;
        Assert.Equal(Tier.T2, spilling.SelectTier(Individual()));
        Assert.Equal(TieredPlanCompiler.DefaultSpilloverThreshold, spilling.SpilloverThreshold);

        // 아키타입 플랜은 큐 깊이와 무관하게 T2 다.
        Assert.Equal(Tier.T2, spilling.SelectTier(Archetype()));

        // 6행 — 예산 초과 → T1 강등 → 거절. 강등 판정은 IReplanBudget 의 몫이다.
        Assert.Equal(Tier.T1, New(t1, t2, new FixedBudget(Tier.T1)).SelectTier(Individual()));
    }

    /// <summary>선택한 티어의 컴파일러만 부른다.</summary>
    [Fact]
    public async Task Tier_RoutesToSelectedCompiler()
    {
        var t1 = new StubCompiler("local");
        var t2 = new StubCompiler("external");
        var budget = new OpenBudget();
        TieredPlanCompiler router = New(t1, t2, budget);

        await router.CompileAsync(Archetype(), CancellationToken.None);
        Assert.Equal(0, t1.Calls);
        Assert.Equal(1, t2.Calls);
        Assert.Equal(1, router.T2Calls);

        await router.CompileAsync(Individual(), CancellationToken.None);
        Assert.Equal(1, t1.Calls);
        Assert.Equal(1, t2.Calls);
        Assert.Equal(1, router.T1Calls);

        Assert.Equal([Tier.T2, Tier.T1], budget.Acquired);
    }

    /// <summary>예산이 강등하면 라우터는 그 결과를 따른다 — 캡을 우회하지 않는다 (CLAUDE.md §2.7).</summary>
    [Fact]
    public async Task Tier_HonoursBudgetDowngrade()
    {
        var t1 = new StubCompiler("local");
        var t2 = new StubCompiler("external");

        // 아키타입 플랜이라 T2 를 원하지만 예산이 T1 만 준다.
        TieredPlanCompiler router = New(t1, t2, new FixedBudget(Tier.T1));

        await router.CompileAsync(Archetype(), CancellationToken.None);

        Assert.Equal(1, t1.Calls);
        Assert.Equal(0, t2.Calls);
    }

    /// <summary>예산이 없으면 거절한다. 예외를 던지지 않고 결과로 돌려준다.</summary>
    [Fact]
    public async Task Tier_RejectsWhenBudgetExhausted()
    {
        var t1 = new StubCompiler("local");
        var t2 = new StubCompiler("external");
        TieredPlanCompiler router = New(t1, t2, new FixedBudget(Tier.None));

        PlanCompileResult result =
            await router.CompileAsync(Archetype(), CancellationToken.None);

        Assert.Null(result.Plan);
        Assert.Equal("T4.BUDGET_EXHAUSTED", result.Validation.Code);
        Assert.Equal("budget_exhausted", result.Stats.Error);
        Assert.False(result.Stats.Reached);
        Assert.Equal(1, router.Rejected);
        Assert.Equal(0, t1.Calls);
        Assert.Equal(0, t2.Calls);
    }

    /// <summary>docs/14 §4 — T2 가 예외를 던지면 T1 으로 페일오버한다.</summary>
    [Fact]
    public async Task Tier_FailsOverToLocalOnExternalException()
    {
        var t1 = new StubCompiler("local");
        var t2 = new StubCompiler("external", throws: true);
        TieredPlanCompiler router = New(t1, t2, new OpenBudget());

        PlanCompileResult result =
            await router.CompileAsync(Archetype(), CancellationToken.None);

        Assert.Equal("local", result.Stats.Model);
        Assert.Equal(1, t1.Calls);
        Assert.Equal(1, router.Failovers);
    }

    /// <summary>호출이 서버에 닿지 못한 것(Stats.Error)도 페일오버다.</summary>
    [Fact]
    public async Task Tier_FailsOverWhenExternalDidNotReach()
    {
        var t1 = new StubCompiler("local");
        var t2 = new StubCompiler("external", error: "timeout");
        TieredPlanCompiler router = New(t1, t2, new OpenBudget());

        PlanCompileResult result =
            await router.CompileAsync(Archetype(), CancellationToken.None);

        Assert.Equal("local", result.Stats.Model);
        Assert.Equal(1, router.Failovers);
    }

    /// <summary>
    /// 검증 실패는 페일오버가 아니다 — 모델 품질 문제라 로컬로 옮겨도 나아지지 않고
    /// 예산만 두 번 나간다.
    /// </summary>
    [Fact]
    public async Task Tier_DoesNotFailOverOnValidationFailure()
    {
        var t1 = new StubCompiler("local");
        var t2 = new StubCompiler("external");
        TieredPlanCompiler router = New(t1, t2, new OpenBudget());

        PlanCompileResult result =
            await router.CompileAsync(Archetype(), CancellationToken.None);

        Assert.Equal("external", result.Stats.Model);
        Assert.Equal(0, t1.Calls);
        Assert.Equal(0, router.Failovers);
    }

    /// <summary>
    /// T4-08 완료 조건 — T1 큐 깊이가 임계를 넘으면 개별 재계획도 T2 로 흘린다 (docs/14 §4).
    ///
    /// T1 지연이 실측 5.1s 이므로 64건이 밀려 있으면 마지막 건은 5분 뒤다.
    /// 그 시점에는 스냅샷 낡음 판정(T4-04)이 거의 확실히 폐기한다 — 헛일이 될 요청이다.
    /// </summary>
    [Fact]
    public async Task Tier_SpillsOverOnLocalBacklog()
    {
        var t1 = new StubCompiler("local");
        var t2 = new StubCompiler("external");
        int depth = 0;
        TieredPlanCompiler router = New(t1, t2, new OpenBudget(), () => depth);

        // 큐가 비어 있으면 개별 재계획은 T1 이다.
        await router.CompileAsync(Individual(), CancellationToken.None);
        Assert.Equal(1, t1.Calls);
        Assert.Equal(0, t2.Calls);
        Assert.Equal(0, router.Spillovers);
        Assert.False(router.IsSpilling);

        // 임계(64)는 "초과" 다 — 딱 64 면 아직 T1 이다.
        depth = 64;
        await router.CompileAsync(Individual(), CancellationToken.None);
        Assert.Equal(2, t1.Calls);
        Assert.Equal(0, router.Spillovers);

        // 65 부터 T2 로 흘린다.
        depth = 65;
        Assert.True(router.IsSpilling);
        await router.CompileAsync(Individual(), CancellationToken.None);
        Assert.Equal(2, t1.Calls);
        Assert.Equal(1, t2.Calls);
        Assert.Equal(1, router.Spillovers);

        // 아키타입 요청은 원래 T2 라 스필오버로 세지 않는다.
        await router.CompileAsync(Archetype(), CancellationToken.None);
        Assert.Equal(2, t2.Calls);
        Assert.Equal(1, router.Spillovers);

        // 큐가 빠지면 다시 T1 이다.
        depth = 0;
        await router.CompileAsync(Individual(), CancellationToken.None);
        Assert.Equal(3, t1.Calls);
        Assert.Equal(1, router.Spillovers);
    }

    /// <summary>임계는 설정값이다. 공급자를 안 주면 스필오버 판정을 하지 않는다.</summary>
    [Fact]
    public void Tier_SpilloverThresholdIsConfigurable()
    {
        var t1 = new StubCompiler("local");
        var t2 = new StubCompiler("external");

        var tight = new TieredPlanCompiler(t1, t2, new OpenBudget(), () => new Tick(0))
        {
            LocalQueueDepth = () => 5,
            SpilloverThreshold = 4,
        };

        Assert.Equal(Tier.T2, tight.SelectTier(Individual()));
        Assert.True(tight.IsSpilling);

        // 공급자가 없으면 큐 깊이를 모르므로 늘 T1 이다.
        TieredPlanCompiler blind = New(t1, t2, new OpenBudget());
        Assert.Equal(Tier.T1, blind.SelectTier(Individual()));
        Assert.False(blind.IsSpilling);
    }

    /// <summary>
    /// T4-09 결선 — 브레이커가 차단 중이면 T2 를 시도조차 하지 않고 곧바로 T1 으로 간다.
    /// 죽은 엔드포인트에 T2 예산과 타임아웃 지연을 태우지 않는다 (docs/14 §10).
    /// </summary>
    [Fact]
    public async Task Tier_ShortCircuitsToLocalWhenBreakerOpen()
    {
        var t1 = new StubCompiler("local");
        var t2 = new StubCompiler("external", throws: true);
        var breaker = new CircuitBreaker(failureThreshold: 2, cooldownSeconds: 10);
        var budget = new OpenBudget();

        var router = new TieredPlanCompiler(t1, t2, budget, () => new Tick(0)) { Breaker = breaker };

        // 두 번 실패하면 브레이커가 열린다. 그 두 번은 페일오버로 T1 이 받는다.
        await router.CompileAsync(Archetype(), CancellationToken.None);
        await router.CompileAsync(Archetype(), CancellationToken.None);

        Assert.Equal(2, t2.Calls);
        Assert.Equal(2, t1.Calls);
        Assert.Equal(2, router.Failovers);
        Assert.Equal(CircuitState.Open, breaker.StateAt(new Tick(0)));

        // 이제는 T2 를 부르지 않는다. 예산 요청도 T1 으로만 나간다.
        await router.CompileAsync(Archetype(), CancellationToken.None);

        Assert.Equal(2, t2.Calls);          // 늘지 않았다
        Assert.Equal(3, t1.Calls);
        Assert.Equal(2, router.Failovers);  // 페일오버도 아니다 — 애초에 T1 으로 갔다
        Assert.Equal(1, breaker.ShortCircuits);
        Assert.Equal([Tier.T2, Tier.T1, Tier.T2, Tier.T1, Tier.T1], budget.Acquired);
    }

    /// <summary>브레이커가 없으면 늘 닫혀 있다고 본다 — 결선하지 않은 경로가 막히면 안 된다.</summary>
    [Fact]
    public async Task Tier_WorksWithoutBreaker()
    {
        var t1 = new StubCompiler("local");
        var t2 = new StubCompiler("external", throws: true);
        TieredPlanCompiler router = New(t1, t2, new OpenBudget());

        for (int i = 0; i < 10; i++)
        {
            await router.CompileAsync(Archetype(), CancellationToken.None);
        }

        Assert.Equal(10, t2.Calls);
        Assert.Equal(10, router.Failovers);
    }

    /// <summary>기본 토큰 추정치는 실측 23,109 tok/요청이다 (W8_prebake.md §4).</summary>
    [Fact]
    public void Tier_DefaultTokenEstimateComesFromMeasurement()
    {
        Assert.Equal(23_109, TieredPlanCompiler.DefaultEstimatedTokens);
        Assert.Equal(6_655_438 / 288, TieredPlanCompiler.DefaultEstimatedTokens);
    }
}
