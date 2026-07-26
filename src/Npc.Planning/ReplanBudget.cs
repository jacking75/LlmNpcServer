using Npc.Contracts;
using Npc.Core;

namespace Npc.Planning;

/// <summary>
/// 재계획 예산 상한 4개. docs/14 §3.
///
/// <b>기본값은 전부 실측에서 나왔다.</b> 각 필드의 출처는 <see cref="Measured"/> 에 있다 —
/// 추정치를 기본값으로 두면 다음 사람이 그것을 실측으로 오해한다.
/// </summary>
/// <param name="T1RequestsPerSecond">T1(로컬) 초당 요청 상한. GPU 용량이 상한이다.</param>
/// <param name="T2RequestsPerSecond">T2(외부) 초당 요청 상한. rate limit 이 상한이다.</param>
/// <param name="DailyTokenCap">하루 토큰 하드 캡. T1·T2 합산.</param>
/// <param name="DailyCostCapUsd">하루 비용 하드 캡(USD). T2 만 비용이 있다.</param>
/// <param name="T2CostPerMillionTokens">T2 백만 토큰당 단가(USD). 추정 소비를 비용으로 환산한다.</param>
public readonly record struct ReplanBudgetLimits(
    double T1RequestsPerSecond,
    double T2RequestsPerSecond,
    long DailyTokenCap,
    double DailyCostCapUsd,
    double T2CostPerMillionTokens)
{
    /// <summary>
    /// 실측 기반 기본값. docs/14 §3 표와 같은 값이다.
    ///
    /// <list type="table">
    ///   <item>
    ///     <term>T1 0.195 req/s</term>
    ///     <description>
    ///       <c>W1_perf.csv</c> · <c>llamacpp-qwen3-8b</c> · <c>Cache=on</c> 30건의 <c>TotalMs</c>
    ///       중앙값 <b>5,139.4 ms</b> → 1 / 5.1394 = 0.1946. 워커 1개 기준이고,
    ///       워커 N 개면 <see cref="ForWorkers"/> 로 곱한다.
    ///       <b>같은 파일의 4B 는 3,701.3 ms(0.270 req/s)</b> 다 — 로컬 모델이 아직 확정되지 않아
    ///       (T0-12 미완) 느린 쪽으로 잡는다.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>T2 2.5 req/s</term>
    ///     <description>
    ///       <c>W8_prebake.md §4</c> 파일럿 288버킷 — <b>115.0 s 에 288건 = 2.504 req/s</b>,
    ///       최대 동시 24 에서 <b>429 가 0회</b>. 상향 여지가 있다고 적혀 있으나
    ///       실측으로 확인된 값은 이것뿐이다 (<c>W6_compile_stats.md §6</c>).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>일일 토큰 15M</term>
    ///     <description>
    ///       docs/14 §3 의 정책값. <b>실측 단가로 환산하면 T2 약 649건/일</b> 이다 —
    ///       파일럿에서 6,655,438 tok / 288건 = <b>23,109 tok/건</b> (프리픽스 13,488 × 재시도 포함).
    ///       §3 의 원래 근거였던 "시나리오 B 20,000건/일의 2배" 는 요청당 375토큰을 가정한 것이라
    ///       실측과 60배 어긋난다. <b>20,000건은 T1(로컬·무비용)이 받는다</b> — T2 로 가는 것은
    ///       런타임 버킷 미스뿐이고, 히트율 98% 목표에서는 그 수가 세 자리다.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>일일 비용 $2</term>
    ///     <description>
    ///       비용 방어 정책값. 실측 단가 <b>$0.5119 / 288건 = $0.001777/건</b>
    ///       (<c>W8_prebake.md §4</c>, Poe → Gemini 2.5 Flash Lite) 로 환산하면 약 1,125건/일 이라
    ///       토큰 캡(649건)이 먼저 걸린다 — 두 캡이 서로 어긋나지 않는다.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>단가 $0.0769/M tok</term>
    ///     <description>
    ///       <b>$0.5119 / 6,655,438 tok</b> (<c>W8_prebake.md §4</c>) — 요청 하나가 $0.001777 이다.
    ///       공개 단가(Gemini 2.5 Flash Lite 입력 $0.10/M)보다 낮은 것은 캐시 적중분 42.6% 가
    ///       할인가로 계산되기 때문이다. Poe 과금은 포인트라 USD 자체가 추정이다.
    ///       <b>모델을 바꾸면 이 값을 같이 바꾼다.</b>
    ///     </description>
    ///   </item>
    /// </list>
    /// </summary>
    public static ReplanBudgetLimits Measured { get; } = new(
        T1RequestsPerSecond: 0.195,
        T2RequestsPerSecond: 2.5,
        DailyTokenCap: 15_000_000,
        DailyCostCapUsd: 2.0,
        T2CostPerMillionTokens: 0.0769);

    /// <summary>워커 수만큼 T1 상한을 곱한다. 기본 워커 2개면 0.39 req/s 다 (docs/14 §4).</summary>
    public ReplanBudgetLimits ForWorkers(int t1Workers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(t1Workers);

        return this with { T1RequestsPerSecond = T1RequestsPerSecond * t1Workers };
    }
}

/// <summary>
/// 재계획 예산. docs/14 §3 · CLAUDE.md §2.7.
///
/// <b>상한이 4개다.</b> T1 초당 요청 · T2 초당 요청 · 일일 토큰 · 일일 비용.
/// 앞의 둘은 <see cref="TokenBucket"/> 이고 뒤의 둘은 하루 단위 누계다.
///
/// <b>캡을 우회하는 경로를 만들지 않는다</b> (CLAUDE.md §2.7). 초과 시 T2 → T1 강등이고
/// T1 마저 소진되면 거절이다 — 거절된 NPC 는 기존 플랜을 계속 쓰므로 안전하다.
///
/// <b>하루 경계는 <see cref="Tick"/> 으로 판정한다.</b> 실시간 10Hz 이므로 하루 = 864,000 틱이다.
/// <see cref="System.DateTime"/> 을 보면 리플레이가 깨진다 (CLAUDE.md §2.3).
/// </summary>
public sealed class ReplanBudget : IReplanBudget
{
    /// <summary>하루에 해당하는 틱 수. 실시간 24시간 × 10Hz.</summary>
    public const long TicksPerDay = 24L * 3600 * Tick.PerSecond;

    private readonly ReplanBudgetLimits _limits;
    private readonly TokenBucket _t1;
    private readonly TokenBucket _t2;
    private readonly object _dayGate = new();

    private long _day = -1;
    private long _tokensToday;
    private double _costToday;
    private long _tokensTotal;
    private double _costTotal;

    /// <summary>예산을 만든다. 기동 시 1회.</summary>
    /// <param name="limits">상한 4개. 기본은 <see cref="ReplanBudgetLimits.Measured"/>.</param>
    /// <param name="startTick">기준 틱.</param>
    public ReplanBudget(ReplanBudgetLimits? limits = null, Tick startTick = default)
    {
        _limits = limits ?? ReplanBudgetLimits.Measured;

        // 버스트는 초당 상한의 4초치로 잡는다. T1 이 0.195 req/s 라 버스트가 1 이면
        // 인터럽트가 몰린 순간 한 건만 나가고 나머지는 5초씩 기다린다.
        _t1 = new TokenBucket(_limits.T1RequestsPerSecond, BurstOf(_limits.T1RequestsPerSecond), startTick.Value);
        _t2 = new TokenBucket(_limits.T2RequestsPerSecond, BurstOf(_limits.T2RequestsPerSecond), startTick.Value);
        _day = startTick.Value / TicksPerDay;
    }

    /// <summary>상한 4개.</summary>
    public ReplanBudgetLimits Limits => _limits;

    /// <summary>오늘 쓴 토큰.</summary>
    public long TokensToday => Interlocked.Read(ref _tokensToday);

    /// <summary>오늘 쓴 비용(USD).</summary>
    public double CostToday
    {
        get
        {
            lock (_dayGate)
            {
                return _costToday;
            }
        }
    }

    /// <summary>기동 이후 누계 토큰. 대시보드 비용 패널(T4-20)이 읽는다.</summary>
    public long TokensTotal => Interlocked.Read(ref _tokensTotal);

    /// <summary>기동 이후 누계 비용(USD).</summary>
    public double CostTotal
    {
        get
        {
            lock (_dayGate)
            {
                return _costTotal;
            }
        }
    }

    /// <summary>일일 토큰 캡 소진율 0~1.</summary>
    public double TokenCapUsage =>
        _limits.DailyTokenCap <= 0 ? 0 : Math.Min(1.0, (double)TokensToday / _limits.DailyTokenCap);

    /// <summary>일일 비용 캡 소진율 0~1.</summary>
    public double CostCapUsage =>
        _limits.DailyCostCapUsd <= 0 ? 0 : Math.Min(1.0, CostToday / _limits.DailyCostCapUsd);

    /// <summary>T1 레이트리미터. 대시보드가 잔량을 본다.</summary>
    public TokenBucket T1Limiter => _t1;

    /// <summary>T2 레이트리미터.</summary>
    public TokenBucket T2Limiter => _t2;

    /// <summary>레이트리미터에 걸려 막힌 횟수 (티어별 합).</summary>
    public long RateLimited => _t1.Denied + _t2.Denied;

    /// <inheritdoc />
    public bool Peek(Tier tier, int estimatedTokens, Tick now)
    {
        if (tier == Tier.None)
        {
            return false;
        }

        RollDay(now);

        return WithinDailyCaps(tier, estimatedTokens)
            && Limiter(tier).CanTake(now);
    }

    /// <inheritdoc />
    public bool TryAcquire(Tier tier, int estimatedTokens, Tick now)
    {
        if (tier == Tier.None)
        {
            return false;
        }

        RollDay(now);

        if (!WithinDailyCaps(tier, estimatedTokens))
        {
            return false;
        }

        if (!Limiter(tier).TryTake(now))
        {
            return false;
        }

        // 추정분을 미리 깎는다. 실측은 Settle 이 정산한다 — 추정만 하고 정산을 안 하면
        // 캡이 실제 사용량과 어긋난다.
        Charge(estimatedTokens, EstimateCost(tier, estimatedTokens));
        return true;
    }

    /// <inheritdoc />
    public Tier Acquire(Tier requested, int estimatedTokens, Tick now) =>
        TryAcquire(requested, estimatedTokens, now) ? requested : Tier.None;

    /// <inheritdoc />
    public void Settle(Tier tier, int actualTokens, double actualCostUsd)
    {
        // 추정과 실측의 차이만 반영한다. 부호가 음수면 되돌려 준다.
        _ = tier;
        Charge(actualTokens, actualCostUsd);
    }

    /// <summary>
    /// 추정 토큰을 비용으로 환산한다. T1(로컬)은 항상 0 이다 — 전기값은 이 캡의 관심사가 아니다.
    /// </summary>
    public double EstimateCost(Tier tier, int tokens) =>
        tier == Tier.T2 ? tokens / 1_000_000.0 * _limits.T2CostPerMillionTokens : 0.0;

    /// <summary>일일 누계를 0 으로. 측정 구간을 가를 때만 쓴다.</summary>
    public void ResetDay(Tick now)
    {
        lock (_dayGate)
        {
            _day = now.Value / TicksPerDay;
            Interlocked.Exchange(ref _tokensToday, 0);
            _costToday = 0;
        }
    }

    // ---------------------------------------------------------------- 내부

    private TokenBucket Limiter(Tier tier) => tier == Tier.T2 ? _t2 : _t1;

    private static double BurstOf(double ratePerSecond) => Math.Max(1.0, ratePerSecond * 4);

    private bool WithinDailyCaps(Tier tier, int estimatedTokens)
    {
        lock (_dayGate)
        {
            if (_limits.DailyTokenCap > 0 && _tokensToday + estimatedTokens > _limits.DailyTokenCap)
            {
                return false;
            }

            double cost = EstimateCost(tier, estimatedTokens);

            return !(_limits.DailyCostCapUsd > 0 && _costToday + cost > _limits.DailyCostCapUsd);
        }
    }

    private void Charge(long tokens, double costUsd)
    {
        lock (_dayGate)
        {
            _tokensToday += tokens;
            _costToday += costUsd;
            _tokensTotal += tokens;
            _costTotal += costUsd;
        }
    }

    /// <summary>하루가 지났으면 일일 누계를 되돌린다. 누계 총량은 남긴다.</summary>
    private void RollDay(Tick now)
    {
        long day = now.Value / TicksPerDay;

        lock (_dayGate)
        {
            if (day == _day)
            {
                return;
            }

            _day = day;
            Interlocked.Exchange(ref _tokensToday, 0);
            _costToday = 0;
        }
    }
}
