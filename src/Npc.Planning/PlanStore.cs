using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Planning;

/// <summary>
/// 플랜 스토어. docs/13 §2.
///
/// <b>표가 둘이다.</b> 버킷으로도 찾고 <c>PlanId</c> 로도 찾는다 —
/// 런타임(<c>PlanExecutor</c>·<c>CognitionScheduler</c>)이 NPC 마다 들고 있는 것은
/// 버킷이 아니라 <c>NpcStore.PlanId</c> 이기 때문이다. 버킷 배열 하나로는 그 조회가 성립하지 않는다.
///
/// <b>락이 없다.</b> 읽기는 <see cref="Volatile"/> 읽기뿐이고 쓰기는 원자 참조 교체 + <see cref="Interlocked"/> 다.
/// 읽는 쪽이 조금 낡은 플랜을 봐도 다음 틱에 새 것을 본다. 틱 루프에서 락을 잡으면 그 자체가 병목이다.
///
/// <see cref="Resolve(BucketKey)"/> 는 <b>절대 null 을 반환하지 않는다</b> —
/// 버킷 미스면 아키타입 폴백, 그것도 없으면 최후 플랜을 준다.
/// 시나리오 C(LLM 전면 차단)가 통과하는 이유가 이것이다 (CLAUDE.md §2.6).
/// </summary>
public sealed class PlanStore
{
    /// <summary>어떤 버킷·아키타입에도 폴백이 없을 때 쓰는 최후 플랜의 id. 곧 "없음"이기도 하다.</summary>
    public const int IdlePlanId = 0;

    /// <summary>레지스트리 청크 하나의 크기(2의 거듭제곱).</summary>
    private const int ChunkBits = 9;
    private const int ChunkSize = 1 << ChunkBits;
    private const int ChunkMask = ChunkSize - 1;

    /// <summary>
    /// 청크 디렉터리 길이. 상한 = 128 × 512 = 65,536 플랜.
    ///
    /// 버킷 플랜은 2,880 상한이고 개별 플랜은 <see cref="IndividualPlanPool"/>(512 링)이 회수하므로
    /// 정상 운영에서 여기 닿지 않는다. 닿으면 회수가 새는 것이라 조용히 넘기지 않고 던진다.
    /// </summary>
    private const int MaxChunks = 128;

    /// <summary>레지스트리 상한. 넘으면 <see cref="Register"/> 가 던진다.</summary>
    public const int MaxPlans = MaxChunks * ChunkSize;

    // ── [1] 버킷 → PlanId. 2,880 고정 배열. 해시맵 불필요 — BucketKey.ToIndex() 가 O(1) ──
    private readonly int[] _byBucket = new int[BucketKey.TotalKeys];          // 0 = 미생성

    // PlanOrigin 을 그대로 담지 않는 이유는 Volatile.Write 에 enum 오버로드가 없기 때문이다.
    private readonly int[] _origin = new int[BucketKey.TotalKeys];
    private readonly long[] _hits = new long[BucketKey.TotalKeys];
    private readonly long[] _misses = new long[BucketKey.TotalKeys];
    private readonly int[] _byArchetype = new int[BucketKey.ArchetypeCount];  // 아키타입 폴백의 PlanId

    // ── [2] PlanId → 플랜. 런타임의 조회 경로. 첨자 두 번, 할당 0 ──
    //
    // List<T> 를 쓰지 않는 이유는 하나다 — 내부 배열 교체와 Count 갱신 사이에 창이 열려서
    // 읽는 쪽이 범위 밖을 볼 수 있다. 청크 배열은 자란다고 기존 청크를 건드리지 않는다.
    private readonly CompiledPlan[]?[] _chunks = new CompiledPlan[]?[MaxChunks];
    private readonly CompiledPlan _idle;
    private int _count;   // Interlocked 로만 올린다

    private PlanStore(CompiledPlan idle)
    {
        _idle = idle with { Id = new PlanId(IdlePlanId) };
        _chunks[0] = new CompiledPlan[ChunkSize];
        _chunks[0]![IdlePlanId] = _idle;
        _count = 1;
    }

    /// <summary>등록된 플랜 수 (최후 플랜 포함).</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>버킷 슬롯 중 채워진 수. 히트율 계산이 쓴다.</summary>
    public int FilledBuckets
    {
        get
        {
            int filled = 0;

            for (int i = 0; i < _byBucket.Length; i++)
            {
                if (Volatile.Read(ref _byBucket[i]) != IdlePlanId)
                {
                    filled++;
                }
            }

            return filled;
        }
    }

    /// <summary>미생성 버킷 수. docs/13 §6 의 <c>cold_buckets</c>.</summary>
    public int ColdBuckets => BucketKey.TotalKeys - FilledBuckets;

    /// <summary>아키타입 폴백 중 채워진 수.</summary>
    public int FilledFallbacks
    {
        get
        {
            int filled = 0;

            for (int i = 0; i < _byArchetype.Length; i++)
            {
                if (Volatile.Read(ref _byArchetype[i]) != IdlePlanId)
                {
                    filled++;
                }
            }

            return filled;
        }
    }

    /// <summary>버킷 히트 누계.</summary>
    public long Hits => Sum(_hits);

    /// <summary>버킷 미스 누계. 폴백으로 해소된 조회다.</summary>
    public long Misses => Sum(_misses);

    /// <summary>히트율. 조회가 없으면 0.</summary>
    public double HitRate
    {
        get
        {
            long hits = Hits;
            long total = hits + Misses;

            return total == 0 ? 0 : (double)hits / total;
        }
    }

    /// <summary>
    /// 최후 플랜만 가진 스토어를 만든다.
    /// 최후 플랜은 전제조건이 하나도 없는 액션으로만 이뤄져 어떤 상황에서도 실행된다.
    /// </summary>
    public static PlanStore CreateIdleOnly(MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new PlanStore(BuildIdlePlan(data));
    }

    /// <summary>플랜을 등록하고 id 를 돌려준다. 여러 워커가 동시에 불러도 된다.</summary>
    public PlanId Register(CompiledPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        int id = Interlocked.Increment(ref _count) - 1;

        if (id >= MaxPlans)
        {
            Interlocked.Decrement(ref _count);

            throw new InvalidOperationException(
                $"플랜 레지스트리 상한 {MaxPlans} 을 넘었다. 개별 플랜 회수가 새고 있다 (docs/13 §2).");
        }

        CompiledPlan[] chunk = ChunkFor(id >> ChunkBits);

        // 슬롯을 채운 뒤에 보이게 한다. 앞선 Interlocked.Increment 로 id 는 이미 우리 것이고,
        // 아직 안 채워진 슬롯을 읽는 쪽은 최후 플랜을 본다 (인덱서 참조).
        Volatile.Write(ref chunk[id & ChunkMask], plan with { Id = new PlanId(id) });

        return new PlanId(id);
    }

    /// <summary>아키타입 폴백을 건다.</summary>
    public void SetFallback(ArchetypeId archetype, PlanId plan)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(archetype.Value, _byArchetype.Length);

        Volatile.Write(ref _byArchetype[archetype.Value], plan.Value);
    }

    /// <summary>
    /// 버킷 플랜을 건다. 프리베이크·재계획 워커만 부른다. 원자 교체다.
    ///
    /// docs/13 §2 의 <c>Publish</c> 와 같은 것이다 — 이름은 P1 스텁이 이미 쓰던 이 쪽으로 통일했다.
    /// </summary>
    public void SetBucket(BucketKey key, PlanId plan)
    {
        int index = key.ToIndex();

        Volatile.Write(ref _byBucket[index], plan.Value);
        Volatile.Write(ref _origin[index], (int)this[plan].Origin);
    }

    /// <summary>플랜을 등록하고 그 버킷에 건다. 호출부가 매번 두 줄을 쓰지 않게 하는 편의 오버로드다.</summary>
    public PlanId SetBucket(BucketKey key, CompiledPlan plan)
    {
        PlanId id = Register(plan);

        SetBucket(key, id);
        return id;
    }

    /// <summary>
    /// 버킷 → 플랜. <b>절대 null 이 아니다.</b>
    /// 버킷 미스면 아키타입 폴백, 그것도 없으면 최후 플랜을 준다.
    /// </summary>
    public CompiledPlan Resolve(BucketKey key) => Resolve(key, out _);

    /// <summary>
    /// 버킷 → 플랜 + 출처. docs/13 §2 의 시그니처.
    /// 미스는 <see cref="PlanOrigin.Fallback"/> 이다 — 폴백으로 해소했다는 뜻이다.
    /// </summary>
    public CompiledPlan Resolve(BucketKey key, out PlanOrigin origin)
    {
        int index = key.ToIndex();
        int planId = Volatile.Read(ref _byBucket[index]);

        if (planId != IdlePlanId)
        {
            Interlocked.Increment(ref _hits[index]);
            origin = (PlanOrigin)Volatile.Read(ref _origin[index]);
            return this[planId];
        }

        Interlocked.Increment(ref _misses[index]);
        origin = PlanOrigin.Fallback;

        // 미스여도 항상 유효한 플랜을 반환한다.
        int fallback = (uint)key.A.Value < (uint)_byArchetype.Length
            ? Volatile.Read(ref _byArchetype[key.A.Value])
            : IdlePlanId;

        return this[fallback];
    }

    /// <summary>이 버킷이 캐시에 있는가. 히트/미스를 세지 않는다.</summary>
    public bool HasBucket(BucketKey key) => Volatile.Read(ref _byBucket[key.ToIndex()]) != IdlePlanId;

    /// <summary>이 버킷 플랜의 출처. 미생성이면 <see cref="PlanOrigin.Fallback"/>.</summary>
    public PlanOrigin OriginOf(BucketKey key)
    {
        int index = key.ToIndex();

        return Volatile.Read(ref _byBucket[index]) == IdlePlanId
            ? PlanOrigin.Fallback
            : (PlanOrigin)Volatile.Read(ref _origin[index]);
    }

    /// <summary>이 버킷의 히트 수.</summary>
    public long HitsOf(BucketKey key) => Volatile.Read(ref _hits[key.ToIndex()]);

    /// <summary>이 버킷의 미스 수.</summary>
    public long MissesOf(BucketKey key) => Volatile.Read(ref _misses[key.ToIndex()]);

    /// <summary>
    /// 미스 상위 버킷. 프리베이크 우선순위 튜닝의 입력이다 (docs/13 §6).
    /// 미스 내림차순, 같으면 버킷 인덱스 오름차순 — 순서가 흔들리면 대시보드가 깜빡인다.
    /// </summary>
    public ImmutableArray<BucketMissCount> TopMisses(int take)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);

        var all = new List<BucketMissCount>(64);

        for (int i = 0; i < _misses.Length; i++)
        {
            long misses = Volatile.Read(ref _misses[i]);

            if (misses > 0)
            {
                all.Add(new BucketMissCount(BucketKey.FromIndex(i), misses));
            }
        }

        all.Sort((a, b) => a.Misses != b.Misses
            ? b.Misses.CompareTo(a.Misses)
            : a.Bucket.ToIndex().CompareTo(b.Bucket.ToIndex()));

        return [.. all.Take(take)];
    }

    /// <summary>아키타입별 (히트, 미스). 첨자 = 아키타입 code. 전체 평균만 보면 재앙적 미스를 놓친다.</summary>
    public void HitsByArchetype(Span<long> hits, Span<long> misses)
    {
        hits.Clear();
        misses.Clear();

        for (int i = 0; i < BucketKey.TotalKeys; i++)
        {
            int archetype = i / (BucketKey.TimeOfDayCount * BucketKey.RegionStateCount * BucketKey.ClimateCount);

            if ((uint)archetype >= (uint)hits.Length)
            {
                continue;
            }

            hits[archetype] += Volatile.Read(ref _hits[i]);
            misses[archetype] += Volatile.Read(ref _misses[i]);
        }
    }

    /// <summary>히트/미스 카운터를 0 으로. 측정 구간을 가를 때만 쓴다.</summary>
    public void ResetCounters()
    {
        Array.Clear(_hits);
        Array.Clear(_misses);
    }

    /// <summary>PlanId 로 조회. 첨자 두 번. 할당 0.</summary>
    public CompiledPlan this[PlanId id] => this[id.Value];

    /// <summary>PlanId 로 조회 (int). 등록되지 않은 id 는 최후 플랜이다.</summary>
    public CompiledPlan this[int planId]
    {
        get
        {
            if ((uint)planId >= (uint)Volatile.Read(ref _count))
            {
                return _idle;
            }

            CompiledPlan[]? chunk = Volatile.Read(ref _chunks[planId >> ChunkBits]);

            if (chunk is null)
            {
                return _idle;
            }

            // 예약만 되고 아직 안 채워진 슬롯이 있을 수 있다 (Register 가 두 단계다).
            return Volatile.Read(ref chunk[planId & ChunkMask]) ?? _idle;
        }
    }

    private static long Sum(long[] counters)
    {
        long total = 0;

        for (int i = 0; i < counters.Length; i++)
        {
            total += Volatile.Read(ref counters[i]);
        }

        return total;
    }

    private CompiledPlan[] ChunkFor(int chunkIndex)
    {
        CompiledPlan[]? chunk = Volatile.Read(ref _chunks[chunkIndex]);

        if (chunk is not null)
        {
            return chunk;
        }

        var fresh = new CompiledPlan[ChunkSize];

        // 둘이 동시에 만들면 먼저 꽂은 쪽이 이긴다. 진 쪽의 배열은 버린다.
        return Interlocked.CompareExchange(ref _chunks[chunkIndex], fresh, null) ?? fresh;
    }

    /// <summary>
    /// 최후 플랜. 전제조건이 없는 액션만 쓴다 —
    /// 어떤 상태에서도 실행되어야 NPC 가 멈추지 않는다.
    /// </summary>
    private static CompiledPlan BuildIdlePlan(MasterDataSet data)
    {
        var steps = ImmutableArray.CreateBuilder<CompiledStep>(3);
        var flagSets = ImmutableArray.CreateBuilder<StepFlags>(3);

        AddStep(data, steps, flagSets, "Wait", count: 60);
        AddStep(data, steps, flagSets, "Emote", count: 0);
        AddStep(data, steps, flagSets, "Rest", count: 600);

        return new CompiledPlan
        {
            Id = new PlanId(IdlePlanId),
            Bucket = default,
            Version = 1,
            Goal = "idle_fallback",
            Loop = true,
            OnFail = StepFailPolicy.Skip,
            RequiredFlags = WorldFlags.None,
            ForbiddenFlags = WorldFlags.None,
            Steps = steps.ToImmutable(),
            StepFlagSets = flagSets.ToImmutable(),
            Origin = PlanOrigin.Fallback,
        };
    }

    private static void AddStep(
        MasterDataSet data,
        ImmutableArray<CompiledStep>.Builder steps,
        ImmutableArray<StepFlags>.Builder flagSets,
        string actionId,
        int count)
    {
        if (!data.TryGetAction(actionId, out ActionId action))
        {
            throw new InvalidDataException($"최후 플랜이 쓰는 액션 '{actionId}' 가 카탈로그에 없다.");
        }

        StepFlags flags = data.FlagsOf(action);

        // 최후 플랜은 전제 없이 돌아야 한다. 카탈로그가 바뀌어 전제가 생기면 기동에서 잡는다.
        if (flags.Requires != WorldFlags.None || flags.RequiresAny != WorldFlags.None)
        {
            throw new InvalidDataException(
                $"최후 플랜이 전제조건 있는 액션 '{actionId}' 를 쓴다: {WorldFlagTable.Format(flags.Requires | flags.RequiresAny)}");
        }

        flagSets.Add(flags);
        steps.Add(new CompiledStep(
            action,
            PoiSymbol.None,
            default,
            (ushort)count,
            (ushort)data.DefaultTimeoutSeconds(action),
            0,
            NpcRefCodes.None,
            (ushort)(steps.Count)));
    }
}

/// <summary>버킷 하나의 미스 수. docs/13 §6 의 <c>top_miss</c>.</summary>
/// <param name="Bucket">어느 버킷인가.</param>
/// <param name="Misses">미스 수.</param>
public readonly record struct BucketMissCount(BucketKey Bucket, long Misses);
