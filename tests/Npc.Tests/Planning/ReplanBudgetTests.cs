using Npc.Contracts;
using Npc.Core;
using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>docs/14 §3 — T1 초당 · T2 초당 · 일일 토큰 · 일일 비용 4개 상한.</summary>
public sealed class ReplanBudgetTests
{
    /// <summary>T4-05 완료 조건 — 버킷이 설정한 속도대로 다시 찬다.</summary>
    [Fact]
    public void Budget_RefillsAtRate()
    {
        // 초당 2건, 버스트 2. 틱은 실시간 10Hz 이므로 10틱 = 1초다.
        var bucket = new TokenBucket(ratePerSecond: 2.0, burst: 2.0);

        Assert.True(bucket.TryTake(new Tick(0)));
        Assert.True(bucket.TryTake(new Tick(0)));
        Assert.False(bucket.TryTake(new Tick(0)));   // 버스트 소진

        // 0.5초(5틱) → 1건분이 찬다.
        Assert.Equal(1.0, bucket.Available(new Tick(5)), 6);
        Assert.True(bucket.TryTake(new Tick(5)));
        Assert.False(bucket.TryTake(new Tick(5)));

        // 10초 지나도 버스트를 넘지 않는다.
        Assert.Equal(2.0, bucket.Available(new Tick(105)), 6);

        Assert.Equal(3, bucket.Granted);
        Assert.Equal(2, bucket.Denied);

        // 시계가 되감겨도 토큰이 줄지 않는다 — 이벤트 재주입(N7)에 리미터가 잠기면 안 된다.
        Assert.Equal(2.0, bucket.Available(new Tick(0)), 6);
    }

    /// <summary>버스트를 안 주면 초당 상한과 1 중 큰 쪽이다 — 1 미만이면 아무것도 못 나간다.</summary>
    [Fact]
    public void Budget_BurstDefaultsToAtLeastOne()
    {
        var slow = new TokenBucket(ratePerSecond: 0.195);

        Assert.Equal(1.0, slow.Burst);
        Assert.True(slow.TryTake(new Tick(0)));
        Assert.False(slow.TryTake(new Tick(0)));

        // 1 / 0.195 = 5.13초 = 51.3틱 뒤에 한 건.
        Assert.False(slow.CanTake(new Tick(50)));
        Assert.True(slow.CanTake(new Tick(52)));
    }

    /// <summary>T4-05 완료 조건 — 상한 4개가 각각 요청을 막는다.</summary>
    [Fact]
    public void Budget_BlocksWhenExhausted()
    {
        // ── (1) T1 초당 요청 ──
        // 버스트는 초당 상한의 4초치다 (0.195 req/s 에서 버스트 1 이면 인터럽트가 몰린 순간
        // 한 건만 나가고 나머지가 5초씩 기다린다). rate 1.0 → 버스트 4.0.
        var t1 = new ReplanBudget(ReplanBudgetLimits.Measured with
        {
            T1RequestsPerSecond = 1.0,
            T2RequestsPerSecond = 1.0,
        });

        for (int i = 0; i < 4; i++)
        {
            Assert.True(t1.TryAcquire(Tier.T1, 1_000, new Tick(0)), $"버스트 {i + 1}번째");
        }

        Assert.False(t1.TryAcquire(Tier.T1, 1_000, new Tick(0)));
        Assert.True(t1.TryAcquire(Tier.T1, 1_000, new Tick(20)));   // 2초 뒤
        Assert.True(t1.RateLimited > 0);

        // T2 는 별도 리미터다 — T1 이 막혔다고 T2 까지 막히면 스필오버(T4-08)가 성립하지 않는다.
        Assert.True(t1.TryAcquire(Tier.T2, 1_000, new Tick(20)));

        // ── (2) 일일 토큰 캡 ──
        var tokens = new ReplanBudget(ReplanBudgetLimits.Measured with
        {
            T1RequestsPerSecond = 1_000,
            T2RequestsPerSecond = 1_000,
            DailyTokenCap = 50_000,
        });

        Assert.True(tokens.TryAcquire(Tier.T1, 30_000, new Tick(0)));
        Assert.Equal(30_000, tokens.TokensToday);
        Assert.Equal(0.6, tokens.TokenCapUsage, 6);

        Assert.False(tokens.TryAcquire(Tier.T1, 30_000, new Tick(0)));   // 60,000 > 50,000
        Assert.True(tokens.TryAcquire(Tier.T1, 20_000, new Tick(0)));    // 딱 맞으면 통과
        Assert.Equal(1.0, tokens.TokenCapUsage, 6);
        Assert.False(tokens.TryAcquire(Tier.T1, 1, new Tick(0)));

        // 하루가 지나면 일일 누계만 되돌아간다. 총 누계는 남는다.
        Assert.True(tokens.TryAcquire(Tier.T1, 1_000, new Tick(ReplanBudget.TicksPerDay)));
        Assert.Equal(1_000, tokens.TokensToday);
        Assert.Equal(51_000, tokens.TokensTotal);

        // ── (3) 일일 비용 캡 ── T1 은 무비용이라 비용 캡에 걸리지 않는다.
        var cost = new ReplanBudget(ReplanBudgetLimits.Measured with
        {
            T1RequestsPerSecond = 1_000,
            T2RequestsPerSecond = 1_000,
            DailyTokenCap = 0,
            DailyCostCapUsd = 0.10,
            T2CostPerMillionTokens = 100.0,
        });

        // 1M tok = $100 → $0.10 은 1,000 tok 이다.
        Assert.True(cost.TryAcquire(Tier.T2, 900, new Tick(0)));
        Assert.Equal(0.09, cost.CostToday, 6);
        Assert.False(cost.TryAcquire(Tier.T2, 200, new Tick(0)));
        Assert.True(cost.TryAcquire(Tier.T1, 10_000_000, new Tick(0)));
        Assert.Equal(0.09, cost.CostToday, 6);
        Assert.Equal(0.9, cost.CostCapUsage, 6);
    }

    /// <summary>추정과 실측의 차이를 정산한다. 정산이 없으면 실제 비용이 캡을 넘는다.</summary>
    [Fact]
    public void Budget_SettlesActualUsage()
    {
        var budget = new ReplanBudget(ReplanBudgetLimits.Measured with
        {
            T2RequestsPerSecond = 1_000,
            DailyTokenCap = 100_000,
            DailyCostCapUsd = 100,
        });

        Assert.True(budget.TryAcquire(Tier.T2, 20_000, new Tick(0)));
        Assert.Equal(20_000, budget.TokensToday);

        // 실제로는 재시도가 붙어 5,000 토큰 더 썼다.
        budget.Settle(Tier.T2, 5_000, budget.EstimateCost(Tier.T2, 5_000));
        Assert.Equal(25_000, budget.TokensToday);

        // 추정보다 적게 썼으면 되돌려 준다.
        budget.Settle(Tier.T2, -3_000, -budget.EstimateCost(Tier.T2, 3_000));
        Assert.Equal(22_000, budget.TokensToday);
    }

    /// <summary>
    /// 기본 상한 4개가 실측치와 일치한다. 이 테스트가 깨지면 <c>docs/14 §3</c> 표도 같이 고쳐야 한다.
    /// </summary>
    [Fact]
    public void Budget_DefaultsComeFromMeasurements()
    {
        ReplanBudgetLimits m = ReplanBudgetLimits.Measured;

        // W1_perf.csv · llamacpp-qwen3-8b · Cache=on 중앙값 5,139.4ms → 1/5.1394 = 0.1946
        Assert.Equal(0.195, m.T1RequestsPerSecond, 3);
        Assert.Equal(1.0 / 5.1394, m.T1RequestsPerSecond, 3);

        // W8_prebake.md §4 — 288건 / 115.0s = 2.504 req/s
        Assert.Equal(2.5, m.T2RequestsPerSecond, 3);
        Assert.Equal(288 / 115.0, m.T2RequestsPerSecond, 1);

        Assert.Equal(15_000_000, m.DailyTokenCap);
        Assert.Equal(2.0, m.DailyCostCapUsd);

        // W8_prebake.md §4 — $0.5119 / 6,655,438 tok = $0.0769/M tok
        Assert.Equal(0.0769, m.T2CostPerMillionTokens, 4);
        Assert.Equal(0.5119 / 6.655438, m.T2CostPerMillionTokens, 4);

        // 실측 단가로 환산한 일일 건수. 토큰 캡이 비용 캡보다 먼저 걸려야 두 캡이 어긋나지 않는다.
        const int TokensPerRequest = 23_109;   // 6,655,438 / 288
        double byTokens = m.DailyTokenCap / (double)TokensPerRequest;
        double byCost = m.DailyCostCapUsd / (TokensPerRequest / 1_000_000.0 * m.T2CostPerMillionTokens);

        Assert.True(byTokens < byCost, $"토큰 캡 {byTokens:F0}건 · 비용 캡 {byCost:F0}건 — 순서가 뒤집혔다.");
        Assert.InRange(byTokens, 600, 700);

        // 워커 수만큼 T1 상한이 곱해진다 (docs/14 §4 — 기본 워커 2).
        Assert.Equal(0.39, m.ForWorkers(2).T1RequestsPerSecond, 3);
    }

    /// <summary><see cref="Tier.None"/> 은 언제나 거절이다. 우회 경로를 만들지 않는다 (CLAUDE.md §2.7).</summary>
    [Fact]
    public void Budget_NoneTierIsAlwaysRejected()
    {
        var budget = new ReplanBudget();

        Assert.False(budget.TryAcquire(Tier.None, 0, new Tick(0)));
        Assert.False(budget.Peek(Tier.None, 0, new Tick(0)));
        Assert.Equal(Tier.None, budget.Acquire(Tier.None, 0, new Tick(0)));
    }

    /// <summary><see cref="ReplanBudget.Peek"/> 는 소비하지 않는다.</summary>
    [Fact]
    public void Budget_PeekDoesNotConsume()
    {
        var budget = new ReplanBudget(ReplanBudgetLimits.Measured with { T1RequestsPerSecond = 0.25 });

        Assert.Equal(1.0, budget.T1Limiter.Burst);   // max(1, 0.25 × 4)

        Assert.True(budget.Peek(Tier.T1, 1_000, new Tick(0)));
        Assert.True(budget.Peek(Tier.T1, 1_000, new Tick(0)));
        Assert.Equal(0, budget.TokensToday);

        Assert.True(budget.TryAcquire(Tier.T1, 1_000, new Tick(0)));
        Assert.Equal(1_000, budget.TokensToday);
        Assert.False(budget.Peek(Tier.T1, 1_000, new Tick(0)));
    }
}
