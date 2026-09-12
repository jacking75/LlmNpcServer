using System.Collections.Immutable;
using Npc.Core;
using Npc.Llm;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.Planning;
using Npc.Prebake;
using Npc.Tests.Fakes;
using Npc.Tests.Llm;

namespace Npc.Tests.Prebake;

/// <summary>예산 하드 캡. docs/13 §4 · 리스크 R8 · T3-14.</summary>
public sealed class BudgetGuardTests
{
    private const string ValidPlan = """
        {"schema":1,"goal":"forge_batch","loop":true,"steps":[
          {"action":"MoveTo","args":{"poi":"$workplace"},"timeout_s":600},
          {"action":"Work","args":{"recipe":"iron_sword","count":1},"timeout_s":1800},
          {"action":"MoveTo","args":{"poi":"$home"},"timeout_s":600},
          {"action":"Sleep","args":{"until_time":"Morning"}}]}
        """;

    private static LlmEngineOptions Engine => LlmPlanCompilerTests.Options.Engine("gemini-3.1-flash-lite");

    /// <summary>대장장이 버킷만 골라 쓴다 — 한 플랜이 전 아키타입에서 통과할 수는 없다.</summary>
    private static ImmutableArray<BucketKey> Buckets(int count)
    {
        Assert.True(LlmPlanCompilerTests.Data.Archetypes.TryGet("blacksmith", out ArchetypeDef def));

        var builder = ImmutableArray.CreateBuilder<BucketKey>(count);

        for (int i = 0; i < count; i++)
        {
            builder.Add(new BucketKey(
                def.Code,
                (TimeOfDay)(i % BucketKey.TimeOfDayCount),
                (RegionState)(i / BucketKey.TimeOfDayCount % BucketKey.RegionStateCount),
                (Climate)(i % BucketKey.ClimateCount)));
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// T3-14 완료 조건 — 캡 도달 → 중단 → resume 으로 이어진다.
    ///
    /// 캡을 4요청치로 잡아 24버킷 회차를 중간에 끊고, 남은 것을 두 번째 회차가 마친다.
    /// </summary>
    [Fact]
    public async Task Budget_StopsAndResumes()
    {
        ImmutableArray<BucketKey> all = Buckets(24);

        // FakeChatClient 는 12,000/11,500 입력 · 400 출력을 보고한다. 요청당 약 $0.0004975 다.
        double perCall = Engine.CostUsd(12_000, 11_500, 400);

        Assert.InRange(perCall, 0.0004, 0.0006);

        // 4요청치만 허용한다. 동시성 2 이므로 두 물결(4건)을 돌고 세 번째에서 멈춘다.
        var tight = new BudgetGuard(capUsd: perCall * 4, estimatePerCallUsd: perCall);

        BulkRunner first = Runner(tight);
        BulkRunReport stopped = await first.RunAsync(
            all, () => new FakeChatClient(ValidPlan), null, CancellationToken.None);

        Assert.True(stopped.StoppedByBudget, "예산 캡에 걸리지 않았다.");
        Assert.True(tight.Exhausted);

        // <b>돌지 못한 버킷의 자리도 보고서에 쓸 수 있어야 한다.</b>
        // 여기가 default(ImmutableArray) 면 jsonl 을 쓰는 쪽이 터지고,
        // 그러면 <b>캡이 실제로 작동한 회차의 기록만</b> 통째로 사라진다 —
        // 실제로 2,475버킷 회차가 그렇게 날아갔다. 캡은 기록을 지키려고 있는 것이다.
        Assert.All(stopped.Outcomes, o => Assert.False(o.Actions.IsDefault));
        Assert.InRange(stopped.AttemptedCount, 1, 23);
        Assert.Equal(24, stopped.Total);

        // 캡을 넘겨 쓰지 않았다 — 우회 경로가 없어야 한다 (CLAUDE.md §2.7).
        Assert.True(
            tight.SpentUsd <= tight.CapUsd + perCall,
            $"캡 ${tight.CapUsd:F6} 를 ${tight.SpentUsd:F6} 로 넘겼다.");

        // 중단 시점까지 만든 것은 스토어에 남아 있다.
        int madeFirst = stopped.Passed;

        Assert.True(madeFirst > 0);

        // --- resume: 미생성 버킷만 다시 던진다 ---
        var resumeOptions = Options("--resume");
        TargetSelection resumeTargets = TargetSelector.Select(
            resumeOptions, LlmPlanCompilerTests.Data, first.Store, InvalidationScope.Full);

        // 첫 회차가 채운 만큼은 빠져 있다.
        Assert.Equal(TestPaths.TotalKeys - madeFirst, resumeTargets.Count);

        ImmutableArray<BucketKey> remaining =
            [.. all.Where(b => !first.Store.HasBucket(b))];

        Assert.Equal(24 - madeFirst, remaining.Length);

        var roomy = new BudgetGuard(capUsd: 5.00, estimatePerCallUsd: perCall);
        BulkRunReport finished = await Runner(roomy).RunAsync(
            remaining, () => new FakeChatClient(ValidPlan), null, CancellationToken.None);

        Assert.False(finished.StoppedByBudget);
        Assert.Equal(remaining.Length, finished.Total);
        Assert.Equal(remaining.Length, finished.Passed);

        // 두 회차를 합치면 24건 전부다.
        Assert.Equal(24, madeFirst + finished.Passed);

        static BulkRunner Runner(BudgetGuard budget) => new(
            LlmPlanCompilerTests.Data,
            LlmPlanCompilerTests.Prefix,
            Engine,
            new BulkRunOptions(Concurrency: 2, MaxConcurrency: 2, BackoffMs: 0, Budget: budget));

        static PrebakeOptions Options(params string[] args)
        {
            Assert.True(PrebakeOptions.TryParse(args, out PrebakeOptions parsed, out string? error), error);

            return parsed;
        }
    }

    /// <summary>캡이 0 이면 제한이 없다.</summary>
    [Fact]
    public void Budget_ZeroCapIsUnlimited()
    {
        var guard = new BudgetGuard(0);

        Assert.True(guard.Unlimited);
        Assert.Equal(double.PositiveInfinity, guard.RemainingUsd);

        for (int i = 0; i < 1_000; i++)
        {
            Assert.True(guard.TryReserve());
            guard.Record(1.0);
        }

        Assert.False(guard.Exhausted);
        Assert.Equal(1_000, guard.SpentUsd, 6);
    }

    /// <summary>한 번 소진되면 다시 열리지 않는다 — "조금 넘겨도 되겠지" 문을 만들지 않는다.</summary>
    [Fact]
    public void Budget_StaysExhausted()
    {
        var guard = new BudgetGuard(capUsd: 1.0, estimatePerCallUsd: 0.4);

        Assert.True(guard.TryReserve(2));   // 0.8 ≤ 1.0
        guard.Record(0.4);
        guard.Record(0.4);

        Assert.False(guard.TryReserve(2));  // 0.8 + 0.8 > 1.0
        Assert.True(guard.Exhausted);

        // 이후에는 1개짜리 예약도 거절한다.
        Assert.False(guard.TryReserve());
        Assert.False(guard.TryReserve());
    }

    /// <summary>추정치는 실측 평균으로 수렴한다. 비용 0 인 로컬 엔진에서는 초기 추정을 유지한다.</summary>
    [Fact]
    public void Budget_LearnsEstimateFromActuals()
    {
        var guard = new BudgetGuard(capUsd: 10.0, estimatePerCallUsd: 1.0);

        guard.Record(0.10);
        guard.Record(0.30);

        Assert.Equal(0.20, guard.EstimatePerCallUsd, 6);
        Assert.Equal(0.40, guard.SpentUsd, 6);
        Assert.Equal(9.60, guard.RemainingUsd, 6);

        // 로컬 엔진은 비용이 0 이다. 추정이 0 으로 내려가면 캡이 사실상 사라진다.
        var local = new BudgetGuard(capUsd: 1.0, estimatePerCallUsd: 0.5);

        local.Record(0);
        local.Record(0);

        Assert.Equal(0.5, local.EstimatePerCallUsd, 6);
    }

    /// <summary>추정은 보수적이어야 한다 — 캐시가 전부 적중한다고 보면 캡을 넘긴 뒤에야 멈춘다.</summary>
    [Fact]
    public void Budget_EstimateIsConservative()
    {
        double pessimistic = BudgetGuard.EstimateFor(Engine, prefixTokens: 13_488);
        double optimistic = BudgetGuard.EstimateFor(Engine, prefixTokens: 13_488, assumedCacheHitRate: 1.0);

        Assert.True(pessimistic > optimistic);

        // 기본값(캐시 0% 가정)이 실제 요청 비용보다 크거나 같아야 한다.
        double actual = Engine.CostUsd(13_788, 13_298, 400);

        Assert.True(pessimistic >= actual, $"추정 ${pessimistic:F6} 이 실측 ${actual:F6} 보다 작다.");
    }

    /// <summary>잘못된 인자는 거절한다.</summary>
    [Fact]
    public void Budget_RejectsBadArgs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BudgetGuard(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BudgetGuard(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BudgetGuard(1).TryReserve(0));
    }
}
