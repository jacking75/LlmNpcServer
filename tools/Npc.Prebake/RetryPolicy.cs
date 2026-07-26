using Npc.Core.Plan;

namespace Npc.Prebake;

/// <summary>
/// 429 백오프. docs/13 §4.
///
/// <b>측정되지 않은 동시성을 그냥 던지면 초반에 다 튕긴다.</b> 지수 백오프로 물러나고,
/// 물러나는 시간에 지터를 섞어 여러 버킷이 동시에 다시 몰리지 않게 한다.
///
/// <para>
/// <b>지터는 결정론이다.</b> <c>Random</c> 을 쓰면 같은 입력이 같은 회차를 내지 못한다
/// (<c>../CLAUDE.md §2.3</c>). <c>(bucketIndex, attempt)</c> 해시를 쓴다 —
/// 런타임의 버킷 전환 지터가 <c>(npcId, salt)</c> 해시를 쓰는 것과 같은 방법이다.
/// </para>
/// </summary>
public static class RetryPolicy
{
    /// <summary>1회 물러날 때의 기본 시간(ms).</summary>
    public const int BaseDelayMs = 5_000;

    /// <summary>물러나는 시간의 상한(ms). 지수가 무한정 자라지 않게 한다.</summary>
    public const int MaxDelayMs = 60_000;

    /// <summary>같은 버킷을 다시 던지는 횟수 상한.</summary>
    public const int MaxRetries = 3;

    /// <summary>지터 폭. 기본 지연의 ±25% 다.</summary>
    public const int JitterPercent = 25;

    /// <summary>해시 솔트. 다른 지터와 같은 수열이 나오지 않게 한다.</summary>
    private const int Salt = 0x5245_5452;   // "RETR"

    /// <summary>
    /// 이 버킷·시도에서 물러날 시간(ms). 지수 + 결정론 지터다.
    ///
    /// <c>attempt</c> 1 이 첫 재시도다 — 기본 시간의 1배에서 시작해 시도마다 2배가 된다.
    /// </summary>
    /// <param name="bucketIndex">버킷 인덱스. 지터를 버킷마다 다르게 만든다.</param>
    /// <param name="attempt">몇 번째 재시도인가 (1부터).</param>
    /// <param name="baseDelayMs">1회 기본 시간.</param>
    /// <param name="maxDelayMs">상한.</param>
    public static int DelayMs(
        int bucketIndex,
        int attempt,
        int baseDelayMs = BaseDelayMs,
        int maxDelayMs = MaxDelayMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bucketIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        ArgumentOutOfRangeException.ThrowIfNegative(baseDelayMs);

        if (baseDelayMs == 0)
        {
            return 0;
        }

        // 지수 — 1배, 2배, 4배 … 시프트가 32를 넘지 않게 먼저 자른다.
        int shift = Math.Min(attempt - 1, 20);
        long exponential = (long)baseDelayMs << shift;
        int capped = (int)Math.Min(exponential, Math.Max(baseDelayMs, maxDelayMs));

        return capped + JitterMs(bucketIndex, attempt, capped);
    }

    /// <summary>
    /// 이 버킷·시도의 지터(ms). <c>[-25%, +25%]</c> 범위이고 <b>같은 입력이면 언제나 같은 값</b>이다.
    /// </summary>
    public static int JitterMs(int bucketIndex, int attempt, int delayMs)
    {
        int span = delayMs * JitterPercent / 100;

        if (span <= 0)
        {
            return 0;
        }

        int period = (span * 2) + 1;

        return (int)(PlanHash.Mix(bucketIndex, attempt ^ Salt) % (uint)period) - span;
    }

    /// <summary>이 오류가 429 인가. 제공사마다 문장이 달라 코드 문자열로 본다.</summary>
    public static bool IsRateLimited(string? error) =>
        error is not null
        && (error.Contains("429", StringComparison.Ordinal)
            || error.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
            || error.Contains("rate limit", StringComparison.OrdinalIgnoreCase));

    /// <summary>더 던져도 되는가.</summary>
    public static bool ShouldRetry(int attempt, int maxRetries = MaxRetries) => attempt < maxRetries;
}
