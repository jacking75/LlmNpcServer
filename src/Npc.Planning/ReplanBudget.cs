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
/// <param name="DailyTokenCap">
/// 하루 토큰 하드 캡. <b>T2 만 센다</b> — docs/14 §3 이 "T1(로컬)은 GPU 용량, T2(외부)는
/// rate limit + 비용이 상한" 이라 했고, 토큰 캡은 비용의 대리 지표이지 GPU 시간의 지표가 아니다.
/// T1 토큰까지 세면 캡 초과 시 T1 도 같이 막혀 CLAUDE.md §2.7 의 "T2 → T1 강등" 이 성립하지 않는다.
/// </param>
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

/// <summary>예산 경보의 종류. docs/14 §3 · 리스크 R8.</summary>
public enum ReplanBudgetAlarmKind
{
    /// <summary>일일 토큰 하드 캡 초과.</summary>
    TokenCapExceeded = 0,

    /// <summary>일일 비용 하드 캡 초과.</summary>
    CostCapExceeded = 1,

    /// <summary>초당 요청 상한에 걸렸다. 일시적이라 경보 등급이 낮다.</summary>
    RateLimited = 2,

    /// <summary>T2 → T1 자동 강등. 품질이 떨어지지만 동작은 계속된다.</summary>
    TierDowngraded = 3,

    /// <summary>거절. T1 마저 소진됐다. NPC 는 기존 플랜을 계속 쓴다.</summary>
    Rejected = 4,
}

/// <summary>예산 경보 하나. 호스트가 로거·대시보드로 올린다.</summary>
/// <param name="Kind">무슨 경보인가.</param>
/// <param name="Requested">요청한 티어.</param>
/// <param name="Granted">실제로 준 티어. <see cref="Tier.None"/> 이면 거절이다.</param>
/// <param name="TokensToday">오늘 쓴 T2 토큰.</param>
/// <param name="CostToday">오늘 쓴 비용(USD).</param>
/// <param name="At">발생 틱.</param>
public readonly record struct ReplanBudgetAlarm(
    ReplanBudgetAlarmKind Kind,
    Tier Requested,
    Tier Granted,
    long TokensToday,
    double CostToday,
    Tick At);

/// <summary>
/// 재계획 예산. docs/14 §3 · CLAUDE.md §2.7 · 리스크 R8.
///
/// <b>상한이 4개다.</b> T1 초당 요청 · T2 초당 요청 · 일일 토큰 · 일일 비용.
/// 앞의 둘은 <see cref="TokenBucket"/> 이고 뒤의 둘은 하루 단위 누계다.
/// <b>뒤의 둘은 T2 만 센다</b> — 이유는 <see cref="ReplanBudgetLimits.DailyTokenCap"/> 참조.
///
/// <b>캡을 우회하는 경로를 만들지 않는다</b> (CLAUDE.md §2.7). 초과 시 T2 → T1 강등이고
/// T1 마저 소진되면 거절이다 — 거절된 NPC 는 기존 플랜을 계속 쓰므로 안전하다.
/// 강등·거절·캡 초과는 전부 <see cref="Alarm"/> 으로 나간다.
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
    private long _localTokensToday;
    private long _localTokensTotal;
    private long _downgrades;
    private long _rejections;
    private long _tokenCapHits;
    private long _costCapHits;

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

    /// <summary>
    /// 예산 경보. 호스트가 로거·대시보드로 올린다.
    /// <b>워커 스레드에서 불린다</b> — 구현이 블록하면 재계획이 밀린다.
    /// </summary>
    public Action<ReplanBudgetAlarm>? Alarm { get; init; }

    /// <summary>오늘 쓴 T2 토큰. 일일 캡의 대상이다.</summary>
    public long TokensToday => Interlocked.Read(ref _tokensToday);

    /// <summary>오늘 쓴 T1(로컬) 토큰. 캡의 대상이 아니고 관측용이다.</summary>
    public long LocalTokensToday => Interlocked.Read(ref _localTokensToday);

    /// <summary>기동 이후 T1 누계 토큰.</summary>
    public long LocalTokensTotal => Interlocked.Read(ref _localTokensTotal);

    /// <summary>T2 → T1 자동 강등 횟수. 리스크 R8 의 관측 지표다.</summary>
    public long Downgrades => Interlocked.Read(ref _downgrades);

    /// <summary>거절 횟수. 이게 0 이 아니면 그만큼의 NPC 가 기존 플랜을 계속 쓴다.</summary>
    public long Rejections => Interlocked.Read(ref _rejections);

    /// <summary>일일 토큰 캡에 막힌 횟수.</summary>
    public long TokenCapHits => Interlocked.Read(ref _tokenCapHits);

    /// <summary>일일 비용 캡에 막힌 횟수.</summary>
    public long CostCapHits => Interlocked.Read(ref _costCapHits);

    /// <summary>일일 캡 중 하나라도 소진됐는가. 대시보드 비용 패널이 붉게 칠하는 조건이다.</summary>
    public bool DailyCapExhausted => TokenCapHits > 0 || CostCapHits > 0;

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

    /// <summary>한 칸 아래 티어. T2 → T1 → 거절 (docs/14 §3·§4).</summary>
    public static Tier Downgrade(Tier tier) => tier == Tier.T2 ? Tier.T1 : Tier.None;

    /// <inheritdoc />
    public bool Peek(Tier tier, int estimatedTokens, Tick now)
    {
        if (tier == Tier.None)
        {
            return false;
        }

        RollDay(now);

        return DenyReasonFor(tier, estimatedTokens) == Deny.None && Limiter(tier).CanTake(now);
    }

    /// <inheritdoc />
    public bool TryAcquire(Tier tier, int estimatedTokens, Tick now) =>
        TryAcquireCore(tier, estimatedTokens, now, out _);

    /// <summary>
    /// 요청한 티어로 못 내면 한 칸 내려서라도 낸다. docs/14 §3 · CLAUDE.md §2.7.
    ///
    /// <b>캡을 우회하지 않는다.</b> T2 가 캡에 걸리면 T1(로컬·무비용)로 내려가고,
    /// T1 의 초당 요청마저 소진되면 거절이다. 거절은 실패가 아니다 —
    /// 그 NPC 는 기존 플랜을 계속 쓰고, 다음 인지 스캔에서 다시 큐에 들어온다.
    /// </summary>
    /// <returns>실제로 확보한 티어. <see cref="Tier.None"/> 이면 거절이다.</returns>
    public Tier Acquire(Tier requested, int estimatedTokens, Tick now)
    {
        if (requested == Tier.None)
        {
            return Tier.None;
        }

        if (TryAcquireCore(requested, estimatedTokens, now, out Deny reason))
        {
            return requested;
        }

        RaiseDeny(reason, requested, Tier.None, now);

        for (Tier next = Downgrade(requested); next != Tier.None; next = Downgrade(next))
        {
            if (TryAcquireCore(next, estimatedTokens, now, out Deny lower))
            {
                Interlocked.Increment(ref _downgrades);
                Raise(ReplanBudgetAlarmKind.TierDowngraded, requested, next, now);
                return next;
            }

            RaiseDeny(lower, next, Tier.None, now);
        }

        Interlocked.Increment(ref _rejections);
        Raise(ReplanBudgetAlarmKind.Rejected, requested, Tier.None, now);
        return Tier.None;
    }

    /// <inheritdoc />
    public void Settle(Tier tier, int actualTokens, double actualCostUsd) =>
        Charge(tier, actualTokens, actualCostUsd);

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
            Interlocked.Exchange(ref _localTokensToday, 0);
            _costToday = 0;
        }
    }

    // ---------------------------------------------------------------- 내부

    /// <summary>거절 사유. 어느 상한에 걸렸는지에 따라 경보 등급이 다르다.</summary>
    private enum Deny
    {
        None = 0,
        TokenCap = 1,
        CostCap = 2,
        RateLimit = 3,
    }

    private TokenBucket Limiter(Tier tier) => tier == Tier.T2 ? _t2 : _t1;

    private static double BurstOf(double ratePerSecond) => Math.Max(1.0, ratePerSecond * 4);

    private bool TryAcquireCore(Tier tier, int estimatedTokens, Tick now, out Deny reason)
    {
        if (tier == Tier.None)
        {
            reason = Deny.RateLimit;
            return false;
        }

        RollDay(now);

        reason = DenyReasonFor(tier, estimatedTokens);

        if (reason == Deny.TokenCap)
        {
            Interlocked.Increment(ref _tokenCapHits);
            return false;
        }

        if (reason == Deny.CostCap)
        {
            Interlocked.Increment(ref _costCapHits);
            return false;
        }

        if (!Limiter(tier).TryTake(now))
        {
            reason = Deny.RateLimit;
            return false;
        }

        // 추정분을 미리 깎는다. 실측은 Settle 이 정산한다 — 추정만 하고 정산을 안 하면
        // 캡이 실제 사용량과 어긋난다.
        Charge(tier, estimatedTokens, EstimateCost(tier, estimatedTokens));
        reason = Deny.None;
        return true;
    }

    /// <summary>
    /// 일일 캡 판정. <b>T1 은 언제나 통과한다</b> — 로컬 토큰은 무비용이고,
    /// 여기서 막으면 CLAUDE.md §2.7 의 "T2 → T1 강등" 이 성립하지 않는다.
    /// </summary>
    private Deny DenyReasonFor(Tier tier, int estimatedTokens)
    {
        if (tier != Tier.T2)
        {
            return Deny.None;
        }

        lock (_dayGate)
        {
            if (_limits.DailyTokenCap > 0 && _tokensToday + estimatedTokens > _limits.DailyTokenCap)
            {
                return Deny.TokenCap;
            }

            double cost = EstimateCost(tier, estimatedTokens);

            return _limits.DailyCostCapUsd > 0 && _costToday + cost > _limits.DailyCostCapUsd
                ? Deny.CostCap
                : Deny.None;
        }
    }

    private void Charge(Tier tier, long tokens, double costUsd)
    {
        if (tier != Tier.T2)
        {
            Interlocked.Add(ref _localTokensToday, tokens);
            Interlocked.Add(ref _localTokensTotal, tokens);
            return;
        }

        lock (_dayGate)
        {
            _tokensToday += tokens;
            _costToday += costUsd;
            _tokensTotal += tokens;
            _costTotal += costUsd;
        }
    }

    private void RaiseDeny(Deny reason, Tier requested, Tier granted, Tick now) => Raise(
        reason switch
        {
            Deny.TokenCap => ReplanBudgetAlarmKind.TokenCapExceeded,
            Deny.CostCap => ReplanBudgetAlarmKind.CostCapExceeded,
            _ => ReplanBudgetAlarmKind.RateLimited,
        },
        requested,
        granted,
        now);

    private void Raise(ReplanBudgetAlarmKind kind, Tier requested, Tier granted, Tick now) =>
        Alarm?.Invoke(new ReplanBudgetAlarm(kind, requested, granted, TokensToday, CostToday, now));

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
            Interlocked.Exchange(ref _localTokensToday, 0);
            _costToday = 0;
        }
    }
}
