using System.Collections.Immutable;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Cli.Review;

/// <summary>검수 표본 하나 (F-06).</summary>
/// <param name="Bucket">버킷 키.</param>
/// <param name="Name">사람이 읽는 버킷 이름.</param>
/// <param name="Origin">이 버킷이 지금 무엇으로 채워져 있나.</param>
/// <param name="Plan">검수 대상 플랜.</param>
public readonly record struct ReviewSample(
    BucketKey Bucket, string Name, PlanOrigin Origin, CompiledPlan Plan);

/// <summary>
/// 검수 표본 추출 (F-06).
///
/// <para>
/// <b>층화 추출이다.</b> <c>plans/</c> 만 뽑으면 <b>폴백으로 대체된 버킷이 표본에 안 잡힌다</b> —
/// 그런데 그 버킷들이야말로 "왜 생성이 실패했나" 를 말해 주는 쪽이다. 생성·폴백·수선의
/// 비율을 모집단 그대로 유지한 채 뽑는다.
/// </para>
///
/// <para>
/// <b>시드 고정이다.</b> 검수 대상이 실행마다 바뀌면 재현이 안 되고, 두 사람이 같은 회차를
/// 검수했는지 확인할 방법이 없어진다. 난수를 쓰지 않고 <c>(시드, 버킷 이름)</c> 해시로 정렬한다.
/// </para>
/// </summary>
public static class ReviewSampler
{
    /// <summary>기본 표본 수. docs/13 §5 의 40건.</summary>
    public const int DefaultSample = 40;

    /// <summary>기본 시드. 같은 시드면 같은 표본이 나온다.</summary>
    public const int DefaultSeed = 20260726;

    /// <summary>
    /// 표본을 뽑는다.
    /// </summary>
    /// <param name="store">로드된 플랜 스토어.</param>
    /// <param name="data">마스터데이터.</param>
    /// <param name="sample">뽑을 수.</param>
    /// <param name="seed">시드.</param>
    /// <param name="archetype">아키타입으로 좁힌다. null 이면 전부.</param>
    /// <param name="planStoreDirectory">
    /// 플랜 스토어 폴더. <c>rejected/</c> 를 읽어 <b>폴백으로 대체된 버킷</b>을 찾는다.
    /// null 이면 그 층을 뺀다.
    /// </param>
    public static ImmutableArray<ReviewSample> Take(
        PlanStore store,
        MasterDataSet data,
        int sample = DefaultSample,
        int seed = DefaultSeed,
        string? archetype = null,
        string? planStoreDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sample);

        ImmutableArray<ReviewSample> population = Population(store, data, archetype, planStoreDirectory);

        if (population.Length <= sample)
        {
            return population;
        }

        // 층별로 나누고, 층 크기 비율대로 배분한다. 배분은 내림 + 큰 나머지 순 —
        // 반올림으로 하면 합이 표본 수와 어긋난다.
        ImmutableArray<PlanOrigin> layers = [.. population.Select(s => s.Origin).Distinct().Order()];

        var quota = new Dictionary<PlanOrigin, int>(layers.Length);
        var remainder = new List<(PlanOrigin Layer, double Fraction)>(layers.Length);
        int assigned = 0;

        foreach (PlanOrigin layer in layers)
        {
            int size = population.Count(s => s.Origin == layer);
            double exact = (double)size * sample / population.Length;
            int floor = (int)exact;

            quota[layer] = floor;
            assigned += floor;
            remainder.Add((layer, exact - floor));
        }

        foreach ((PlanOrigin layer, double _) in remainder
            .OrderByDescending(r => r.Fraction)
            .ThenBy(r => (int)r.Layer))
        {
            if (assigned >= sample)
            {
                break;
            }

            quota[layer]++;
            assigned++;
        }

        var taken = ImmutableArray.CreateBuilder<ReviewSample>(sample);

        foreach (PlanOrigin layer in layers)
        {
            taken.AddRange(population
                .Where(s => s.Origin == layer)
                .OrderBy(s => Rank(seed, s.Name))
                .ThenBy(s => s.Name, StringComparer.Ordinal)
                .Take(quota[layer]));
        }

        // 출력 순서도 고정한다 — 층별로 모아 두면 검수자가 "생성만 40건" 을 본 뒤 지친다.
        return [.. taken.OrderBy(s => Rank(seed, s.Name)).ThenBy(s => s.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// 모집단. 채워진 버킷 + <b>폴백으로 대체된 버킷</b>이다.
    ///
    /// <para>
    /// <b>"대체됐다" 는 "생성을 시도했는데 떨어졌다" 는 뜻이다.</b> 한 번도 시도하지 않은
    /// 버킷(프리베이크 대상 밖)까지 넣으면 모집단의 4분의 3이 폴백이 되고, 검수자는 같은
    /// 손글씨 플랜 40개를 계속 보게 된다 — 그것은 검수가 아니다. 시도 여부는
    /// <c>rejected/</c> 에 그 버킷의 반려 기록이 있는가로 판정한다.
    /// </para>
    /// </summary>
    /// <param name="store">플랜 스토어.</param>
    /// <param name="data">마스터데이터.</param>
    /// <param name="archetype">아키타입 필터.</param>
    /// <param name="planStoreDirectory">플랜 스토어 폴더. null 이면 폴백 층을 뺀다.</param>
    public static ImmutableArray<ReviewSample> Population(
        PlanStore store,
        MasterDataSet data,
        string? archetype = null,
        string? planStoreDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(data);

        HashSet<string> attempted = AttemptedButRejected(planStoreDirectory);
        var samples = ImmutableArray.CreateBuilder<ReviewSample>();

        foreach (ArchetypeDef def in data.Archetypes.Archetypes)
        {
            if (archetype is not null && !string.Equals(archetype, def.Id, StringComparison.Ordinal))
            {
                continue;
            }

            for (int slot = 0; slot < BucketKey.PerArchetype; slot++)
            {
                var bucket = new BucketKey(
                    def.Code,
                    (TimeOfDay)(slot / (BucketKey.RegionStateCount * BucketKey.ClimateCount)),
                    (RegionState)(slot / BucketKey.ClimateCount % BucketKey.RegionStateCount),
                    (Climate)(slot % BucketKey.ClimateCount));

                string name = bucket.Format(def.Id);

                if (!store.HasBucket(bucket) && !attempted.Contains(name))
                {
                    continue;
                }

                CompiledPlan plan = store.Resolve(bucket, out PlanOrigin origin);

                samples.Add(new ReviewSample(bucket, name, origin, plan));
            }
        }

        return samples.ToImmutable();
    }

    /// <summary>
    /// 반려 기록이 있는 버킷 이름. 파일 이름이 <c>{버킷}.{시도}.json</c> 이다.
    /// </summary>
    private static HashSet<string> AttemptedButRejected(string? planStoreDirectory)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        if (planStoreDirectory is null)
        {
            return names;
        }

        string directory = Path.Combine(planStoreDirectory, PlanStoreIo.FolderOf(PlanLayer.Rejected));

        if (!Directory.Exists(directory))
        {
            return names;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            int dot = stem.LastIndexOf('.');

            if (dot > 0)
            {
                names.Add(stem[..dot]);
            }
        }

        return names;
    }

    /// <summary>
    /// 시드와 이름으로 정하는 순서. <b>난수가 아니다</b> — 같은 시드면 같은 순서가 나온다
    /// (CLAUDE.md §2.3 과 같은 원칙: 표본이 회차마다 바뀌면 재현이 안 된다).
    /// </summary>
    public static uint Rank(int seed, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // FNV-1a. 짧고 결정론이고 외부 의존이 없다.
        uint hash = 2166136261u ^ unchecked((uint)seed);

        foreach (char c in name)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }
}
