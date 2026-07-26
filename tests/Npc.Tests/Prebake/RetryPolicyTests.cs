using Npc.Prebake;

namespace Npc.Tests.Prebake;

/// <summary>429 백오프. docs/13 §4 · T3-13.</summary>
public sealed class RetryPolicyTests
{
    /// <summary>
    /// T3-13 완료 조건 — 지수 백오프 + 지터이고 <b>지터가 결정론</b>이다.
    ///
    /// <c>Random</c> 을 쓰면 같은 입력이 같은 회차를 내지 못한다 (CLAUDE.md §2.3).
    /// </summary>
    [Fact]
    public void Backoff_IsExponentialWithJitter()
    {
        const int Base = 5_000;
        const int Bucket = 1_234;

        int first = RetryPolicy.DelayMs(Bucket, 1, Base);
        int second = RetryPolicy.DelayMs(Bucket, 2, Base);
        int third = RetryPolicy.DelayMs(Bucket, 3, Base);

        // 지수 — 지터 ±25% 를 감안해도 시도마다 커진다.
        Assert.InRange(first, Base * 75 / 100, Base * 125 / 100);
        Assert.InRange(second, Base * 2 * 75 / 100, Base * 2 * 125 / 100);
        Assert.InRange(third, Base * 4 * 75 / 100, Base * 4 * 125 / 100);
        Assert.True(first < second && second < third);

        // 지터가 실제로 붙는다 — 순수 지수라면 정확히 배수여야 한다.
        bool anyJitter =
            first != Base
            || second != Base * 2
            || RetryPolicy.DelayMs(Bucket + 1, 1, Base) != Base;

        Assert.True(anyJitter, "지터가 붙지 않았다.");

        // 결정론 — 같은 (버킷, 시도)면 언제나 같은 값이다.
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(first, RetryPolicy.DelayMs(Bucket, 1, Base));
            Assert.Equal(third, RetryPolicy.DelayMs(Bucket, 3, Base));
        }

        // 버킷이 다르면 지터도 다르다 — 여러 버킷이 같은 순간에 다시 몰리지 않는다.
        int[] delays = [.. Enumerable.Range(0, 64).Select(b => RetryPolicy.DelayMs(b, 1, Base))];

        Assert.True(delays.Distinct().Count() > 32, $"지터가 흩어지지 않았다 (유니크 {delays.Distinct().Count()}/64).");

        // 상한 — 지수가 무한정 자라지 않는다.
        Assert.InRange(
            RetryPolicy.DelayMs(Bucket, 20, Base, maxDelayMs: 60_000),
            60_000 * 75 / 100,
            60_000 * 125 / 100);

        // 기본 시간이 0 이면 기다리지 않는다 (테스트 경로).
        Assert.Equal(0, RetryPolicy.DelayMs(Bucket, 3, baseDelayMs: 0));
    }

    /// <summary>지터는 <c>[-25%, +25%]</c> 안이고 평균이 0 에 가깝다.</summary>
    [Fact]
    public void Backoff_JitterStaysInBand()
    {
        const int Delay = 8_000;
        int span = Delay * RetryPolicy.JitterPercent / 100;

        long sum = 0;

        for (int bucket = 0; bucket < 2_880; bucket++)
        {
            int jitter = RetryPolicy.JitterMs(bucket, attempt: 1, Delay);

            Assert.InRange(jitter, -span, span);
            sum += jitter;
        }

        // 한쪽으로 쏠리면 백오프가 실제로는 지수보다 길거나 짧아진다.
        Assert.InRange(sum / 2_880.0, -span * 0.1, span * 0.1);
    }

    /// <summary>제공사마다 429 문장이 다르다. 코드·문구를 다 본다.</summary>
    [Fact]
    public void Backoff_DetectsRateLimitWording()
    {
        Assert.True(RetryPolicy.IsRateLimited("Service request failed. Status: 429 (Too Many Requests)"));
        Assert.True(RetryPolicy.IsRateLimited("HTTP 429"));
        Assert.True(RetryPolicy.IsRateLimited("too many requests"));
        Assert.True(RetryPolicy.IsRateLimited("You exceeded your current rate limit"));

        Assert.False(RetryPolicy.IsRateLimited(null));
        Assert.False(RetryPolicy.IsRateLimited("Status: 500 (Internal Server Error)"));
        Assert.False(RetryPolicy.IsRateLimited("timeout"));
    }

    /// <summary>재시도 상한.</summary>
    [Fact]
    public void Backoff_StopsAtMaxRetries()
    {
        Assert.True(RetryPolicy.ShouldRetry(0));
        Assert.True(RetryPolicy.ShouldRetry(2));
        Assert.False(RetryPolicy.ShouldRetry(3));
        Assert.False(RetryPolicy.ShouldRetry(0, maxRetries: 0));
        Assert.Equal(3, RetryPolicy.MaxRetries);
    }

    /// <summary>잘못된 인자는 거절한다.</summary>
    [Fact]
    public void Backoff_RejectsBadArgs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.DelayMs(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.DelayMs(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetryPolicy.DelayMs(0, 1, baseDelayMs: -1));
    }
}
