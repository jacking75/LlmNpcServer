using System.Collections.Immutable;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.Planning;

namespace Npc.Prebake;

/// <summary>어떻게 대상을 골랐나. 로그와 manifest 가 이 이름을 쓴다.</summary>
public enum TargetMode
{
    /// <summary>전량 2,880. 무효화 범위가 Full 이거나 스토어가 없다.</summary>
    Full,

    /// <summary>변경분만. 기존 프리베이크 플랜은 유효하다.</summary>
    Partial,

    /// <summary><c>--resume</c>. 미생성 버킷만.</summary>
    Resume,

    /// <summary><c>--only</c> glob.</summary>
    Only,

    /// <summary>할 것이 없다. 무효화 범위가 None 이다.</summary>
    None,
}

/// <summary>대상 산출 결과.</summary>
/// <param name="Buckets">생성 순서대로의 대상. <see cref="TargetSelector.SortByPriority"/> 순이다.</param>
/// <param name="Mode">어떻게 골랐나.</param>
/// <param name="Scope">무효화 판정 결과.</param>
/// <param name="SkippedPinned">사람이 고정해서 건너뛴 수.</param>
public readonly record struct TargetSelection(
    ImmutableArray<BucketKey> Buckets,
    TargetMode Mode,
    InvalidationScope Scope,
    int SkippedPinned)
{
    /// <summary>대상 수.</summary>
    public int Count => Buckets.Length;
}

/// <summary>
/// 생성 대상 버킷 산출. docs/13 §4 의 3단계.
///
/// <list type="bullet">
///   <item><b>Full</b> — 2,880 전량. 무효화가 Full 이거나 스토어가 없다</item>
///   <item><b>Partial</b> — 변경분. <b>미생성 버킷 + 폴백·재사용으로 메운 버킷</b>이다.
///     Partial 의 정의상 기존 <c>Prebaked</c> 플랜은 유효하므로 다시 만들지 않는다.
///     폴백으로 떨어진 버킷은 마스터데이터가 늘어나면 이번엔 성공할 수 있으니 다시 던진다</item>
///   <item><b>Resume</b> — 미생성 버킷만. 폴백으로 메운 것도 "있는 것"으로 본다</item>
///   <item><b>Only</b> — glob 에 맞는 것</item>
/// </list>
///
/// <b>pinned 버킷은 어느 모드에서도 대상이 아니다.</b> 사람이 고친 것을 다시 만들 이유가 없고,
/// <c>PlanStore.SetBucket</c> 이 어차피 거절한다 (T3-02).
/// </summary>
public static class TargetSelector
{
    /// <summary>전 버킷. 인덱스 순서. 개수는 masterdata 가 정한다 (F-05).</summary>
    public static ImmutableArray<BucketKey> All(BucketSpace space)
    {
        ArgumentNullException.ThrowIfNull(space);

        var builder = ImmutableArray.CreateBuilder<BucketKey>(space.TotalKeys);

        for (int index = 0; index < space.TotalKeys; index++)
        {
            builder.Add(BucketKey.FromIndex(index));
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// 대상을 고른다.
    /// </summary>
    /// <param name="options">CLI 옵션.</param>
    /// <param name="data">마스터데이터. 아키타입 id 로 glob 을 맞춘다.</param>
    /// <param name="existing">
    /// 기존 스토어. <c>--resume</c>·Partial 이 여기를 본다.
    /// null 이면 아무것도 없는 것으로 본다.
    /// </param>
    /// <param name="scope">무효화 판정(T3-06) 결과.</param>
    public static TargetSelection Select(
        PrebakeOptions options,
        MasterDataSet data,
        PlanStore? existing,
        InvalidationScope scope)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(data);

        TargetMode mode = ModeOf(options, scope);

        IEnumerable<BucketKey> candidates = mode switch
        {
            TargetMode.None => [],
            TargetMode.Resume => Missing(data.Buckets, existing),
            TargetMode.Partial => Changed(data.Buckets, existing),
            _ => All(data.Buckets),   // Full · Only
        };

        var chosen = new List<BucketKey>(data.Buckets.TotalKeys);
        int skippedPinned = 0;

        foreach (BucketKey bucket in candidates)
        {
            if (existing is not null && existing.IsPinned(bucket))
            {
                skippedPinned++;
                continue;
            }

            if (!options.IncludesBucket(bucket.Format(data.Archetypes[bucket.A].Id)))
            {
                continue;
            }

            chosen.Add(bucket);
        }

        // 측정 회차용 축소. 전량 회차에서는 셋 다 기본값이라 아무 일도 하지 않는다.
        chosen = Narrow(data.Buckets, chosen, options);

        return new TargetSelection(SortByPriority(chosen, data), mode, scope, skippedPinned);
    }

    /// <summary>
    /// <c>prebake_priority</c> 순 정렬. docs/01 §6 · docs/13 §4.
    ///
    /// <b>Peace 계열이 먼저다.</b> 중단되어도(예산 캡·429·Ctrl-C) 실제로 많이 쓰이는 버킷이
    /// 먼저 채워져 있어야 한다. 가중치는 <c>context_buckets.json</c> 에서 온다 —
    /// 코드에 하드코딩하지 않는다 (CLAUDE.md §2.4).
    ///
    /// 같은 가중치면 버킷 인덱스 오름차순이다. 순서가 흔들리면 회차 간 diff 가 의미를 잃는다.
    /// </summary>
    public static ImmutableArray<BucketKey> SortByPriority(IEnumerable<BucketKey> buckets, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(data);

        var sorted = new List<BucketKey>(buckets);

        sorted.Sort((a, b) =>
        {
            int byWeight = data.Buckets.PrebakePriorityOf(b.R).CompareTo(data.Buckets.PrebakePriorityOf(a.R));

            return byWeight != 0 ? byWeight : a.ToIndex().CompareTo(b.ToIndex());
        });

        return [.. sorted];
    }

    /// <summary>어느 모드로 도는가. <c>--only</c> 가 가장 세다.</summary>
    public static TargetMode ModeOf(PrebakeOptions options, InvalidationScope scope)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Only.IsEmpty)
        {
            return TargetMode.Only;
        }

        if (options.Resume)
        {
            return TargetMode.Resume;
        }

        return scope switch
        {
            InvalidationScope.None => TargetMode.None,
            InvalidationScope.Partial => TargetMode.Partial,
            _ => TargetMode.Full,
        };
    }

    /// <summary>스토어에 없는 버킷. <c>--resume</c> 이 쓴다.</summary>
    private static IEnumerable<BucketKey> Missing(BucketSpace space, PlanStore? existing)
    {
        for (int index = 0; index < space.TotalKeys; index++)
        {
            var bucket = BucketKey.FromIndex(index);

            if (existing is null || !existing.HasBucket(bucket))
            {
                yield return bucket;
            }
        }
    }

    /// <summary>
    /// 미생성 + 폴백·재사용으로 메운 버킷. Partial 이 쓴다.
    /// <c>Prebaked</c>·<c>Pinned</c> 는 Partial 의 정의상 유효하므로 다시 만들지 않는다.
    /// </summary>
    private static IEnumerable<BucketKey> Changed(BucketSpace space, PlanStore? existing)
    {
        for (int index = 0; index < space.TotalKeys; index++)
        {
            var bucket = BucketKey.FromIndex(index);

            if (existing is null || !existing.HasBucket(bucket))
            {
                yield return bucket;
                continue;
            }

            if (existing.OriginOf(bucket) is not (PlanOrigin.Prebaked or PlanOrigin.Pinned))
            {
                yield return bucket;
            }
        }
    }

    /// <summary>측정 회차용 축소(<c>--stride</c>·<c>--archetypes</c>·<c>--limit</c>).</summary>
    private static List<BucketKey> Narrow(BucketSpace space, List<BucketKey> buckets, PrebakeOptions options)
    {
        if (options.Archetypes > 0)
        {
            // 연속 슬라이스. 인접 버킷 재사용을 실제로 태우려면 같은 아키타입이 연속이어야 한다.
            int limit = options.Archetypes * BucketKey.PerArchetype;

            buckets = [.. buckets.Where(b => b.ToIndex() < limit)];
        }
        else if (options.Stride > 1)
        {
            // 전체 키 수와 서로소인 stride 를 주면 순서가 전 아키타입을 훑는다. 난수를 쓰지 않는다.
            // 걸러내는 것이 아니라 <b>순서를 바꾸는</b> 것이다 — 뒤의 --limit 이 앞에서 자르면
            // 그 표본이 흩어져 있게 된다.
            var present = new HashSet<int>(buckets.Select(b => b.ToIndex()));
            var reordered = new List<BucketKey>(buckets.Count);

            for (int i = 0; i < space.TotalKeys && reordered.Count < buckets.Count; i++)
            {
                int index = i * options.Stride % space.TotalKeys;

                if (present.Remove(index))
                {
                    reordered.Add(BucketKey.FromIndex(index));
                }
            }

            buckets = reordered;
        }

        if (options.Limit > 0 && options.Limit < buckets.Count)
        {
            buckets = [.. buckets.Take(options.Limit)];
        }

        return buckets;
    }
}
