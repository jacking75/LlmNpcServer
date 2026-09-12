using Npc.Prebake;

namespace Npc.Tests.Prebake;

/// <summary>AIMD 동시성 제어. docs/13 §4 · T3-12.</summary>
public sealed class AdaptiveConcurrencyTests
{
    /// <summary>T3-12 완료 조건 — throttle 을 만나면 절반으로 접는다 (multiplicative decrease).</summary>
    [Fact]
    public void Aimd_HalvesOnThrottle()
    {
        var aimd = new AdaptiveConcurrency(start: 16, max: 32);

        Assert.Equal(16, aimd.Current);
        Assert.Equal(0, aimd.FirstThrottleConcurrency);

        aimd.OnThrottled();
        Assert.Equal(8, aimd.Current);
        Assert.Equal(16, aimd.FirstThrottleConcurrency);   // 처음 난 시점의 동시성을 기억한다
        Assert.Equal(1, aimd.ThrottleCount);

        aimd.OnThrottled();
        Assert.Equal(4, aimd.Current);
        Assert.Equal(16, aimd.FirstThrottleConcurrency);   // "처음" 이므로 갱신되지 않는다

        aimd.OnThrottled();
        Assert.Equal(2, aimd.Current);

        aimd.OnThrottled();
        Assert.Equal(1, aimd.Current);

        // 하한 아래로는 안 내려간다. 0 이 되면 아무것도 못 던진다.
        aimd.OnThrottled();
        Assert.Equal(1, aimd.Current);
        Assert.Equal(5, aimd.ThrottleCount);
    }

    /// <summary>T3-12 완료 조건 — 성공이 연속으로 쌓이면 1 올린다 (additive increase).</summary>
    [Fact]
    public void Aimd_GrowsOnStreak()
    {
        var aimd = new AdaptiveConcurrency(start: 8, max: 12, successStreak: 16);

        // 15연속으로는 안 오른다.
        for (int i = 0; i < 15; i++)
        {
            aimd.OnSuccess();
        }

        Assert.Equal(8, aimd.Current);
        Assert.Equal(15, aimd.Streak);

        // 16번째에 오르고 연속이 0 으로 돌아간다.
        aimd.OnSuccess();
        Assert.Equal(9, aimd.Current);
        Assert.Equal(0, aimd.Streak);
        Assert.Equal(9, aimd.Peak);

        // 한 번에 1 씩만 오른다 — 32연속이면 2 만 오른다.
        aimd.OnSuccess(32);
        Assert.Equal(11, aimd.Current);

        // 상한을 넘지 않는다.
        aimd.OnSuccess(16 * 10);
        Assert.Equal(12, aimd.Current);
        Assert.Equal(12, aimd.Peak);
    }

    /// <summary>throttle 은 연속 성공을 0 으로 되돌린다 — 접은 직후에 바로 올라가면 다시 튕긴다.</summary>
    [Fact]
    public void Aimd_ThrottleResetsStreak()
    {
        var aimd = new AdaptiveConcurrency(start: 8, max: 32, successStreak: 16);

        aimd.OnSuccess(15);
        Assert.Equal(15, aimd.Streak);

        aimd.OnThrottled();
        Assert.Equal(0, aimd.Streak);
        Assert.Equal(4, aimd.Current);

        // 다시 15연속으로는 안 오른다.
        aimd.OnSuccess(15);
        Assert.Equal(4, aimd.Current);
    }

    /// <summary>
    /// T3-12 완료 조건 — 초기값의 근거.
    ///
    /// 규칙은 "429 최초 발생 동시성의 절반, 그 실행 전이면 8" 이다.
    /// T2-19 첫 회차와 T3-11 실측에서 429 가 나지 않았으므로 절반 규칙이 발동할 값이 없다.
    /// </summary>
    [Fact]
    public void Aimd_RecommendsNextStartFromMeasurement()
    {
        // 429 를 본 적이 없으면 도달한 최대치를 권한다 — 더 올려도 된다는 뜻이다.
        var clean = new AdaptiveConcurrency(start: 8, max: 32);
        clean.OnSuccess(16 * 10);

        Assert.Equal(0, clean.FirstThrottleConcurrency);
        Assert.Equal(18, clean.Peak);
        Assert.Equal(18, clean.RecommendedStart);

        // 429 를 봤으면 그 시점 동시성의 절반이다.
        var throttled = new AdaptiveConcurrency(start: 20, max: 32);
        throttled.OnThrottled();

        Assert.Equal(20, throttled.FirstThrottleConcurrency);
        Assert.Equal(10, throttled.RecommendedStart);

        // 인자 없는 생성자는 기본 초기값에서 출발한다. 상위 계획의 "동시 32" 는 추정치라 쓰지 않는다.
        Assert.Equal(AdaptiveConcurrency.DefaultStart, new AdaptiveConcurrency().Start);
    }

    /// <summary>초기값은 하한·상한 사이로 잡힌다.</summary>
    [Fact]
    public void Aimd_ClampsStart()
    {
        Assert.Equal(4, new AdaptiveConcurrency(start: 100, max: 4).Current);
        Assert.Equal(2, new AdaptiveConcurrency(start: 1, max: 8, min: 2).Current);

        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveConcurrency(start: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AdaptiveConcurrency(min: 4, max: 2));
    }
}
