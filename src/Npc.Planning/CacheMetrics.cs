using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;

namespace Npc.Planning;

/// <summary>
/// 아키타입 하나의 캐시 성적. docs/13 §6 — <b>전체 평균만 보면 특정 아키타입의 재앙적 미스를 놓친다.</b>
/// </summary>
/// <param name="Archetype">아키타입 code.</param>
/// <param name="Hits">버킷 히트.</param>
/// <param name="Misses">폴백으로 해소된 미스.</param>
/// <param name="ColdBuckets">이 아키타입의 72버킷 중 미생성 수.</param>
public readonly record struct ArchetypeCacheStats(ArchetypeId Archetype, long Hits, long Misses, int ColdBuckets)
{
    /// <summary>조회 수.</summary>
    public long Lookups => Hits + Misses;

    /// <summary>히트율. 조회가 없으면 0 이다 — 안 쓰인 아키타입을 0% 로 벌하지 않으려면 <see cref="Lookups"/> 를 같이 본다.</summary>
    public double HitRate => Lookups == 0 ? 0 : (double)Hits / Lookups;
}

/// <summary>
/// 캐시 계측 한 장. docs/13 §6 의 4개 지표 + 아키타입별 분해.
/// </summary>
/// <param name="HitRate">전체 히트율. 게이트는 시나리오 A 에서 ≥ 0.98 이다.</param>
/// <param name="Hits">버킷 히트 누계.</param>
/// <param name="Misses">미스 누계.</param>
/// <param name="FilledBuckets">채워진 버킷 수.</param>
/// <param name="ColdBuckets">미생성 버킷 수. 0 이 아니면 <c>--resume</c> 으로 마저 생성한다.</param>
/// <param name="PinnedBuckets">사람이 고정한 버킷 수.</param>
/// <param name="IndividualTurnover">개별 플랜 풀 회전율. 크면 개별 재계획이 과다하다.</param>
/// <param name="IndividualLive">지금 살아 있는 개별 플랜 수.</param>
/// <param name="TopMisses">미스 상위 버킷. 프리베이크 우선순위 튜닝의 입력이다.</param>
/// <param name="WorstArchetypes">히트율 최하위 아키타입 (조회가 있는 것만).</param>
public readonly record struct CacheStats(
    double HitRate,
    long Hits,
    long Misses,
    int FilledBuckets,
    int ColdBuckets,
    int PinnedBuckets,
    double IndividualTurnover,
    int IndividualLive,
    ImmutableArray<BucketMissCount> TopMisses,
    ImmutableArray<ArchetypeCacheStats> WorstArchetypes);

/// <summary>
/// 플랜 캐시 계측. docs/13 §6.
///
/// <b>카운터는 여기(정확히는 <see cref="PlanStore"/>)가 들고 있고 계측기는 호스트가 만든다.</b>
/// <c>NpcMeter</c> 는 <c>Npc.Host</c> 의 <c>internal</c> 타입이라 <c>Npc.Planning</c> 에서 직접 못 쓴다 —
/// 이쪽은 순수 집계만 하고, 호스트가 스냅샷 시점에 <c>ObservableGauge</c> 로 읽어 간다.
///
/// <see cref="Snapshot"/> 은 정렬·LINQ 를 쓴다. <b>틱 루프에서 부르지 않는다</b> —
/// 대시보드 폴링(기본 1초)과 게이트 러너만 부른다.
/// </summary>
public sealed class CacheMetrics
{
    /// <summary>상위 목록 기본 길이. docs/13 §6 의 <c>Take(10)</c>.</summary>
    public const int DefaultTop = 10;

    /// <summary>한 아키타입이 갖는 버킷 수. 6 × 4 × 3.</summary>
    public const int BucketsPerArchetype =
        BucketKey.TimeOfDayCount * BucketKey.RegionStateCount * BucketKey.ClimateCount;

    private readonly PlanStore _plans;
    private readonly IndividualPlanPool? _individual;
    private readonly long[] _hits = new long[BucketKey.ArchetypeCount];
    private readonly long[] _misses = new long[BucketKey.ArchetypeCount];

    /// <summary>계측을 건다. 기동 시 1회.</summary>
    /// <param name="plans">버킷 히트/미스 카운터를 들고 있는 스토어.</param>
    /// <param name="individual">개별 플랜 풀. null 이면 회전율이 0 이다 (P4 에서 결선한다).</param>
    public CacheMetrics(PlanStore plans, IndividualPlanPool? individual = null)
    {
        ArgumentNullException.ThrowIfNull(plans);

        _plans = plans;
        _individual = individual;
    }

    /// <summary>전체 히트율.</summary>
    public double HitRate => _plans.HitRate;

    /// <summary>버킷 히트 누계.</summary>
    public long Hits => _plans.Hits;

    /// <summary>미스 누계.</summary>
    public long Misses => _plans.Misses;

    /// <summary>미생성 버킷 수.</summary>
    public int ColdBuckets => _plans.ColdBuckets;

    /// <summary>채워진 버킷 수.</summary>
    public int FilledBuckets => _plans.FilledBuckets;

    /// <summary>사람이 고정한 버킷 수.</summary>
    public int PinnedBuckets => _plans.PinnedBuckets;

    /// <summary>개별 플랜 풀 회전율. 풀이 없으면 0.</summary>
    public double IndividualTurnover => _individual?.TurnoverRate ?? 0;

    /// <summary>지금 살아 있는 개별 플랜 수. 풀이 없으면 0.</summary>
    public int IndividualLive => _individual?.Live ?? 0;

    /// <summary>미스 상위 버킷.</summary>
    public ImmutableArray<BucketMissCount> TopMisses(int take = DefaultTop) => _plans.TopMisses(take);

    /// <summary>이 아키타입의 캐시 성적.</summary>
    public ArchetypeCacheStats StatsOf(ArchetypeId archetype)
    {
        _plans.HitsByArchetype(_hits, _misses);

        return new ArchetypeCacheStats(
            archetype,
            _hits[archetype.Value],
            _misses[archetype.Value],
            ColdBucketsOf(archetype));
    }

    /// <summary>아키타입 code 오름차순의 전체 분해.</summary>
    public ImmutableArray<ArchetypeCacheStats> ByArchetype()
    {
        _plans.HitsByArchetype(_hits, _misses);

        var builder = ImmutableArray.CreateBuilder<ArchetypeCacheStats>(BucketKey.ArchetypeCount);

        for (int code = 0; code < BucketKey.ArchetypeCount; code++)
        {
            var archetype = new ArchetypeId((ushort)code);

            builder.Add(new ArchetypeCacheStats(
                archetype, _hits[code], _misses[code], ColdBucketsOf(archetype)));
        }

        return builder.ToImmutable();
    }

    /// <summary>이 아키타입의 72버킷 중 미생성 수.</summary>
    public int ColdBucketsOf(ArchetypeId archetype)
    {
        int cold = 0;
        int start = archetype.Value * BucketsPerArchetype;

        for (int i = 0; i < BucketsPerArchetype; i++)
        {
            if (!_plans.HasBucket(BucketKey.FromIndex(start + i)))
            {
                cold++;
            }
        }

        return cold;
    }

    /// <summary>계측 한 장. 대시보드와 게이트 러너가 이것만 읽는다.</summary>
    public CacheStats Snapshot(int take = DefaultTop) => new(
        HitRate: HitRate,
        Hits: Hits,
        Misses: Misses,
        FilledBuckets: FilledBuckets,
        ColdBuckets: ColdBuckets,
        PinnedBuckets: PinnedBuckets,
        IndividualTurnover: IndividualTurnover,
        IndividualLive: IndividualLive,
        TopMisses: TopMisses(take),
        WorstArchetypes: WorstArchetypes(take));

    /// <summary>
    /// 히트율 최하위 아키타입. <b>조회가 한 번이라도 있었던 것만 센다</b> —
    /// 안 쓰인 아키타입을 0% 로 올리면 진짜 문제가 목록에서 밀린다.
    /// </summary>
    public ImmutableArray<ArchetypeCacheStats> WorstArchetypes(int take = DefaultTop)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var used = new List<ArchetypeCacheStats>(BucketKey.ArchetypeCount);

        foreach (ArchetypeCacheStats stats in ByArchetype())
        {
            if (stats.Lookups > 0)
            {
                used.Add(stats);
            }
        }

        // 히트율 오름차순, 같으면 조회 수 내림차순, 그래도 같으면 code 오름차순.
        used.Sort((a, b) =>
        {
            int byRate = a.HitRate.CompareTo(b.HitRate);
            if (byRate != 0)
            {
                return byRate;
            }

            int byLookups = b.Lookups.CompareTo(a.Lookups);
            return byLookups != 0 ? byLookups : a.Archetype.Value.CompareTo(b.Archetype.Value);
        });

        return [.. used.Take(take)];
    }
}
