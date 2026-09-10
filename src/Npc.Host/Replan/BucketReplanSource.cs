using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Host.Replan;

/// <summary>
/// 아키타입 버킷 미스 공급원. docs/14 §4 표 2행 "아키타입 플랜 (런타임 미스)".
///
/// <b>일감을 따로 큐에 넣지 않는다.</b> <see cref="PlanStore"/> 가 이미 버킷별 미스를 세고 있으므로
/// (docs/13 §6 의 <c>top_miss</c>) 미생성 버킷 중 미스가 가장 많은 것을 집어 오면 된다.
/// 틱 루프에 새 배관을 넣지 않는 것이 이 설계의 요점이다 — 미스 집계는 <c>Resolve</c> 안의
/// <c>Interlocked.Increment</c> 하나이고 이미 거기 있다.
///
/// 품질은 항상 <see cref="PlanQuality.Archetype"/> 이라 티어 라우터가 T2(외부·고품질)로 보낸다 —
/// 한 버킷을 수천 NPC 가 공유하므로 품질에 투자할 값이 있다 (CLAUDE.md §8).
///
/// <b>시나리오 B 의 "캐시 미스 버킷만 LLM 호출" 이 이 공급원이다.</b> 존이 War 로 바뀌면
/// <c>*.War.*</c> 버킷을 찾는 조회가 몰리고, 그중 미생성인 것만 여기로 올라온다 —
/// 전량 재생성이 아니다 (docs/14 §7).
///
/// <b>워커 8개가 같은 버킷을 동시에 집지 않는다.</b> 버킷당 한 칸을 <see cref="Interlocked"/> 로
/// 선점하고, 실패한 버킷은 쿨다운을 걸어 되돌아오지 못하게 한다 — 안 걸면 만들 수 없는 버킷
/// 하나가 워커 8개를 영원히 점유한다.
/// </summary>
internal sealed class BucketReplanSource : IReplanSource
{
    /// <summary>실패한 버킷을 다시 시도하지 않는 시간(틱). 실시간 5분.</summary>
    public const long FailureCooldownTicks = 5L * 60 * Tick.PerSecond;

    private const int Idle = 0;
    private const int InFlight = 1;

    private readonly PlanStore _plans;
    private readonly BucketSpace _buckets;
    private readonly int[] _claim;
    private readonly long[] _cooldownUntil;

    private long _filled;
    private long _skipped;

    /// <summary>공급원을 만든다. 기동 시 1회.</summary>
    public BucketReplanSource(PlanStore plans, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(data);

        _plans = plans;
        _buckets = data.Buckets;

        // 길이는 masterdata 가 정한다 (F-05). 기동 시 한 번 잡는다.
        _claim = new int[_buckets.TotalKeys];
        _cooldownUntil = new long[_buckets.TotalKeys];

        Array.Fill(_cooldownUntil, long.MinValue);
    }

    /// <inheritdoc />
    public string Name => "bucket";

    /// <summary>
    /// 채울 것이 남은 버킷 수 — 미생성이고 조회된 적이 있고 워커가 붙어 있지 않은 것.
    /// 대시보드의 "콜드 버킷 대기" 다. <b>쿨다운 중인 것도 센다</b> — 대시보드는
    /// "만들 것이 남았다" 를 알아야 하고, 쿨다운은 지금 안 만든다는 뜻일 뿐이다.
    /// </summary>
    public int Depth => CountPending(long.MaxValue);

    /// <summary>런타임에 채운 버킷 수.</summary>
    public long Filled => Interlocked.Read(ref _filled);

    /// <summary>만들지 못해 쿨다운을 걸어 둔 횟수.</summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    /// <summary>지금 워커가 붙어 있는 버킷 수.</summary>
    public int InFlightCount
    {
        get
        {
            int count = 0;

            for (int i = 0; i < _claim.Length; i++)
            {
                if (Volatile.Read(ref _claim[i]) == InFlight)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public bool TryTake(Tick now, out ReplanJob job)
    {
        // 미스가 가장 많은 미생성 버킷을 고른다. 동점이면 인덱스 오름차순 — 순서가 흔들리면
        // 회차마다 다른 버킷이 채워져 캐시 히트율 비교가 불가능해진다 (CLAUDE.md §2.3).
        while (TryPickHottest(now, out int index))
        {
            if (Interlocked.CompareExchange(ref _claim[index], InFlight, Idle) != Idle)
            {
                continue;   // 다른 워커가 먼저 집었다
            }

            BucketKey key = BucketKey.FromIndex(index);

            // 선점 사이에 누군가 채웠으면 놓아준다.
            if (_plans.HasBucket(key))
            {
                Volatile.Write(ref _claim[index], Idle);
                continue;
            }

            job = new ReplanJob(
                Npc: -1,
                key,
                _buckets.InitialFlags(key),
                PlanQuality.Archetype,
                _plans.MissesOf(key));

            return true;
        }

        job = default;
        return false;
    }

    /// <inheritdoc />
    public void Apply(in ReplanJob job, CompiledPlan plan, Tick now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _ = now;

        int index = job.Bucket.ToIndex();

        // pinned 버킷은 SetBucket 이 스스로 거절한다 (docs/13 §2·§5) —
        // 사람이 검수한 플랜을 런타임 재계획이 덮어쓰면 검수 작업이 사라진다.
        _plans.SetBucket(job.Bucket, plan);

        Interlocked.Increment(ref _filled);
        Volatile.Write(ref _claim[index], Idle);
    }

    /// <inheritdoc />
    public void Abandon(in ReplanJob job, Tick now)
    {
        int index = job.Bucket.ToIndex();

        // 못 만든 버킷을 곧바로 다시 집으면 워커 8개가 그 하나에 매달린다.
        // 쿨다운 동안은 폴백이 조회를 받아 준다 — PlanStore.Resolve 는 절대 null 이 아니다.
        Volatile.Write(ref _cooldownUntil[index], now.Value + FailureCooldownTicks);
        Interlocked.Increment(ref _skipped);
        Volatile.Write(ref _claim[index], Idle);
    }

    private bool TryPickHottest(Tick now, out int index)
    {
        int best = -1;
        long bestMisses = 0;

        for (int i = 0; i < _claim.Length; i++)
        {
            if (Volatile.Read(ref _claim[i]) != Idle || Volatile.Read(ref _cooldownUntil[i]) > now.Value)
            {
                continue;
            }

            BucketKey key = BucketKey.FromIndex(i);
            long misses = _plans.MissesOf(key);

            // 조회된 적 없는 버킷은 만들지 않는다 — 2,880개를 런타임에 채우는 것은
            // 프리베이크의 일이고, 런타임은 "실제로 쓰이는데 없는 것" 만 만든다 (docs/13 §6).
            if (misses <= bestMisses || _plans.HasBucket(key))
            {
                continue;
            }

            best = i;
            bestMisses = misses;
        }

        index = best;
        return best >= 0;
    }

    private int CountPending(long nowValue)
    {
        int count = 0;

        for (int i = 0; i < _claim.Length; i++)
        {
            BucketKey key = BucketKey.FromIndex(i);

            if (Volatile.Read(ref _claim[i]) == Idle
                && Volatile.Read(ref _cooldownUntil[i]) <= nowValue
                && _plans.MissesOf(key) > 0
                && !_plans.HasBucket(key))
            {
                count++;
            }
        }

        return count;
    }
}
