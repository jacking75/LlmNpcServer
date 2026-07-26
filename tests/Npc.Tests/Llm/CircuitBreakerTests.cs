using Npc.Contracts;
using Npc.Llm;

namespace Npc.Tests.Llm;

/// <summary>docs/14 §10 — T2 연속 실패 5회 → 60초 차단 → 반개방.</summary>
public sealed class CircuitBreakerTests
{
    /// <summary>T4-09 완료 조건 — 연속 5회 실패에 차단으로 넘어간다.</summary>
    [Fact]
    public void Breaker_OpensAfter5Failures()
    {
        var breaker = new CircuitBreaker();

        Assert.Equal(5, breaker.FailureThreshold);
        Assert.Equal(60, breaker.CooldownSeconds);
        Assert.Equal(600, breaker.CooldownTicks);   // 실시간 10Hz

        // 4회까지는 닫혀 있다 — 한두 번의 5xx 로 외부 티어를 버리지 않는다.
        for (int i = 1; i <= 4; i++)
        {
            breaker.RecordFailure(new Tick(i));
            Assert.Equal(CircuitState.Closed, breaker.StateAt(new Tick(i)));
            Assert.True(breaker.TryEnter(new Tick(i)));
        }

        Assert.Equal(4, breaker.ConsecutiveFailures);

        // 5회에서 열린다.
        breaker.RecordFailure(new Tick(5));
        Assert.Equal(CircuitState.Open, breaker.StateAt(new Tick(5)));
        Assert.Equal(1, breaker.Opens);

        // 차단 중에는 시도조차 하지 않는다.
        Assert.False(breaker.TryEnter(new Tick(6)));
        Assert.False(breaker.TryEnter(new Tick(500)));
        Assert.Equal(2, breaker.ShortCircuits);

        // 중간에 성공이 끼면 연속 카운터가 0 으로 돌아간다.
        var intermittent = new CircuitBreaker();

        for (int i = 0; i < 4; i++)
        {
            intermittent.RecordFailure(new Tick(i));
        }

        intermittent.RecordSuccess();
        Assert.Equal(0, intermittent.ConsecutiveFailures);

        for (int i = 0; i < 4; i++)
        {
            intermittent.RecordFailure(new Tick(10 + i));
        }

        Assert.Equal(CircuitState.Closed, intermittent.StateAt(new Tick(20)));
        Assert.Equal(0, intermittent.Opens);
    }

    /// <summary>T4-09 완료 조건 — 차단 시간이 지나면 반개방이 되어 한 건을 탐색한다.</summary>
    [Fact]
    public void Breaker_HalfOpensAfterTimeout()
    {
        var breaker = new CircuitBreaker(failureThreshold: 2, cooldownSeconds: 10);

        breaker.RecordFailure(new Tick(100));
        breaker.RecordFailure(new Tick(100));
        Assert.Equal(CircuitState.Open, breaker.StateAt(new Tick(100)));

        // 10초 = 100틱. 그 전에는 여전히 차단이다.
        Assert.Equal(CircuitState.Open, breaker.StateAt(new Tick(199)));
        Assert.False(breaker.TryEnter(new Tick(199)));

        // 경과하면 반개방 — 탐색 요청 하나가 나간다.
        Assert.Equal(CircuitState.HalfOpen, breaker.StateAt(new Tick(200)));
        Assert.True(breaker.TryEnter(new Tick(200)));
        Assert.Equal(1, breaker.Probes);

        // 탐색이 실패하면 즉시 재차단이다. 임계를 다시 채우게 두면 죽은 엔드포인트에
        // 매 쿨다운마다 (임계 - 1)건이 새어 나간다.
        breaker.RecordFailure(new Tick(200));
        Assert.Equal(CircuitState.Open, breaker.StateAt(new Tick(200)));
        Assert.Equal(2, breaker.Opens);
        Assert.False(breaker.TryEnter(new Tick(250)));

        // 다시 경과 → 반개방 → 이번엔 성공 → 닫힘.
        Assert.Equal(CircuitState.HalfOpen, breaker.StateAt(new Tick(300)));
        Assert.True(breaker.TryEnter(new Tick(300)));
        breaker.RecordSuccess();

        Assert.Equal(CircuitState.Closed, breaker.StateAt(new Tick(300)));
        Assert.Equal(0, breaker.ConsecutiveFailures);
        Assert.True(breaker.TryEnter(new Tick(301)));
        Assert.Equal(2, breaker.Opens);
    }

    /// <summary>Reset 은 강제로 닫는다. 운영 수동 복구용.</summary>
    [Fact]
    public void Breaker_ResetCloses()
    {
        var breaker = new CircuitBreaker(failureThreshold: 1);

        breaker.RecordFailure(new Tick(0));
        Assert.Equal(CircuitState.Open, breaker.StateAt(new Tick(0)));

        breaker.Reset();
        Assert.Equal(CircuitState.Closed, breaker.StateAt(new Tick(0)));
    }
}
