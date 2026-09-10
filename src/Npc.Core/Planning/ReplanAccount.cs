using Npc.Contracts;

// <b>Npc.Core 에 둔다.</b> Npc.Llm 이 이것을 읽는데, 의존 그래프상 Npc.Llm 은
// Npc.Planning 을 참조하지 않는다 (CLAUDE.md §3) — IReplanBudget 이 여기 있는 것과 같은 이유다.
namespace Npc.Core;

/// <summary>
/// 예산 계정 (C-07).
///
/// <b>스필오버가 버킷 예산을 먹어 치우던 것을 막는다.</b> 개별 재계획이 T1 큐 64 를 넘겨 T2 로
/// 흐르면, 하루 예산(≈649건)이 몇 분 만에 소진된다 — 그러면 정작 필요한 <b>버킷 미스 보충</b>이
/// 굶는다. 버킷 플랜은 수천 NPC 가 공유하고 개별 플랜은 한 마리가 쓴다.
/// </summary>
public enum ReplanAccount
{
    /// <summary>
    /// 버킷 미스 보충. <b>우선이다</b> — 하나가 수천 NPC 에게 쓰인다.
    /// </summary>
    Bucket = 0,

    /// <summary>
    /// 개별 재계획 스필오버. 서브 쿼터 안에서만 T2 를 쓴다.
    ///
    /// 넘으면 <b>거절이 아니라 T1 대기</b>다 — 개별 재계획은 급하지 않고, 그 NPC 는
    /// 기존 플랜을 계속 쓰면 된다.
    /// </summary>
    Individual = 1,
}

/// <summary>
/// 개체 스필오버 서브 쿼터 (C-07).
///
/// <b>계정을 나누기만 하고 예산 자체는 건드리지 않는다</b> (CLAUDE.md §2.7).
/// 총 캡은 <c>ReplanBudget</c> 이 그대로 지키고, 여기서는 그 안에서 개체 몫의 상한을 본다.
/// </summary>
public sealed class SpilloverQuota
{
    /// <summary>개체 몫의 기본 비율. 20% 다.</summary>
    public const double DefaultShare = 0.20;

    /// <summary>
    /// 하루에 해당하는 틱 수. 실시간 24시간 × 10Hz.
    /// <c>ReplanBudget.TicksPerDay</c> 와 같아야 한다 — 두 계정의 하루가 갈리면
    /// 서브 쿼터가 총 캡과 다른 주기로 돈다.
    /// </summary>
    public const long TicksPerDay = 24L * 3600 * Tick.PerSecond;

    private readonly double _share;
    private long _day = -1;
    private long _bucketTokens;
    private long _individualTokens;
    private readonly Lock _gate = new();

    /// <summary>쿼터를 만든다.</summary>
    /// <param name="share">개체 몫 비율 0~1. 0 이면 개체는 T2 를 쓰지 않는다.</param>
    /// <param name="dailyTokenCap">일일 토큰 캡. <c>ReplanBudget</c> 과 같은 값이어야 한다.</param>
    public SpilloverQuota(double share, long dailyTokenCap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(share);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(share, 1.0);
        ArgumentOutOfRangeException.ThrowIfNegative(dailyTokenCap);

        _share = share;
        DailyTokenCap = dailyTokenCap;
    }

    /// <summary>일일 토큰 캡.</summary>
    public long DailyTokenCap { get; }

    /// <summary>개체가 오늘 쓸 수 있는 T2 토큰 상한.</summary>
    public long IndividualCap => (long)(DailyTokenCap * _share);

    /// <summary>서브 쿼터를 넘겨 T1 으로 돌린 횟수. 계측·경보가 읽는다.</summary>
    public long IndividualDeferred { get; private set; }

    /// <summary>오늘 개체가 쓴 T2 토큰.</summary>
    public long IndividualTokensToday
    {
        get
        {
            lock (_gate)
            {
                return _individualTokens;
            }
        }
    }

    /// <summary>오늘 버킷이 쓴 T2 토큰.</summary>
    public long BucketTokensToday
    {
        get
        {
            lock (_gate)
            {
                return _bucketTokens;
            }
        }
    }

    /// <summary>
    /// 이 계정이 지금 T2 를 써도 되는가.
    /// </summary>
    /// <param name="account">계정.</param>
    /// <param name="estimatedTokens">예상 토큰.</param>
    /// <param name="tick">지금 틱. 하루 경계 판정에 쓴다 — <c>DateTime</c> 을 보면 리플레이가 깨진다.</param>
    /// <returns>허용이면 true. 개체가 서브 쿼터를 넘으면 false (T1 대기).</returns>
    public bool TryUseT2(ReplanAccount account, int estimatedTokens, Tick tick)
    {
        lock (_gate)
        {
            Rollover(tick);

            if (account == ReplanAccount.Bucket)
            {
                // 버킷은 총 캡만 본다. 서브 쿼터는 개체에만 걸린다 —
                // 버킷을 제한하면 정작 수천 NPC 가 쓰는 플랜이 굶는다.
                _bucketTokens += estimatedTokens;
                return true;
            }

            if (_individualTokens + estimatedTokens > IndividualCap)
            {
                IndividualDeferred++;
                return false;
            }

            _individualTokens += estimatedTokens;
            return true;
        }
    }

    /// <summary>하루가 바뀌었으면 계정을 되돌린다. 락 안에서만 부른다.</summary>
    private void Rollover(Tick tick)
    {
        long day = tick.Value / TicksPerDay;

        if (day == _day)
        {
            return;
        }

        _day = day;
        _bucketTokens = 0;
        _individualTokens = 0;
    }
}
