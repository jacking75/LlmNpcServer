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

    // ── [1] 버킷 → PlanId. 고정 배열. 해시맵 불필요 — BucketKey.ToIndex() 가 O(1) ──
    //
    // 길이는 masterdata 가 정한다 (F-05). 기동 시 한 번 잡고 이후 바뀌지 않는다 —
    // 틱 루프는 이 배열을 첨자로만 읽는다 (CLAUDE.md §2.1).
    private readonly BucketSpace _space;
    private readonly int[] _byBucket;          // 0 = 미생성

    // PlanOrigin 을 그대로 담지 않는 이유는 Volatile.Write 에 enum 오버로드가 없기 때문이다.
    private readonly int[] _origin;
    private readonly long[] _hits;
    private readonly long[] _misses;
    private readonly int[] _byArchetype;       // 아키타입 폴백의 PlanId

    // ── [2] PlanId → 플랜. 런타임의 조회 경로. 첨자 두 번, 할당 0 ──
    //
    // List<T> 를 쓰지 않는 이유는 하나다 — 내부 배열 교체와 Count 갱신 사이에 창이 열려서
    // 읽는 쪽이 범위 밖을 볼 수 있다. 청크 배열은 자란다고 기존 청크를 건드리지 않는다.
    private readonly CompiledPlan[]?[] _chunks = new CompiledPlan[]?[MaxChunks];
    private readonly CompiledPlan _idle;
    private int _count;   // Interlocked 로만 올린다
    private long _individualHits;
    private long _individualLost;

    private PlanStore(CompiledPlan idle, BucketSpace space)
    {
        _space = space;
        _byBucket = new int[space.TotalKeys];
        _origin = new int[space.TotalKeys];
        _hits = new long[space.TotalKeys];
        _misses = new long[space.TotalKeys];
        _byArchetype = new int[space.ArchetypeCount];

        _idle = idle with { Id = new PlanId(IdlePlanId) };
        _chunks[0] = new CompiledPlan[ChunkSize];
        _chunks[0]![IdlePlanId] = _idle;
        _count = 1;
    }

    /// <summary>이 스토어가 다루는 버킷 키 공간. 배열 길이의 근거다 (F-05).</summary>
    public BucketSpace Space => _space;

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
    public int ColdBuckets => _byBucket.Length - FilledBuckets;

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

        return new PlanStore(BuildIdlePlan(data), data.Buckets);
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
    ///
    /// <b><see cref="PlanOrigin.Pinned"/> 은 덮어쓰지 않는다</b> (docs/13 §2·§5) —
    /// 사람이 검수·수정한 플랜이 재프리베이크로 날아가면 검수 작업이 통째로 사라진다.
    /// </summary>
    /// <returns>실제로 걸린 PlanId. pinned 라 거절됐으면 이미 걸려 있던 것.</returns>
    public PlanId SetBucket(BucketKey key, PlanId plan)
    {
        int index = key.ToIndex();

        if (IsPinned(index))
        {
            return new PlanId(Volatile.Read(ref _byBucket[index]));
        }

        Volatile.Write(ref _byBucket[index], plan.Value);
        Volatile.Write(ref _origin[index], (int)this[plan].Origin);

        return plan;
    }

    /// <summary>
    /// 플랜을 등록하고 그 버킷에 건다. 호출부가 매번 두 줄을 쓰지 않게 하는 편의 오버로드다.
    /// pinned 버킷이면 <b>등록조차 하지 않는다</b> — 레지스트리에 쓰레기를 남기지 않는다.
    /// </summary>
    public PlanId SetBucket(BucketKey key, CompiledPlan plan)
    {
        int index = key.ToIndex();

        if (IsPinned(index))
        {
            return new PlanId(Volatile.Read(ref _byBucket[index]));
        }

        return SetBucket(key, Register(plan));
    }

    /// <summary>
    /// pinned 를 무시하고 강제로 건다. <b>pinned 플랜 자체를 올릴 때만</b> 쓴다 —
    /// <c>planstore/pinned/</c> 로드(T3-07)와 승격 도구(T3-19)가 유일한 호출자다.
    /// </summary>
    public PlanId Pin(BucketKey key, CompiledPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        PlanId id = Register(plan with { Origin = PlanOrigin.Pinned });
        int index = key.ToIndex();

        Volatile.Write(ref _byBucket[index], id.Value);
        Volatile.Write(ref _origin[index], (int)PlanOrigin.Pinned);

        return id;
    }

    /// <summary>이 버킷이 사람이 고정한 플랜인가.</summary>
    public bool IsPinned(BucketKey key) => IsPinned(key.ToIndex());

    /// <summary>
    /// 다른 스토어의 버킷 표를 <b>이 스토어로 옮긴다</b> (A-07 핫 리로드).
    ///
    /// <para>
    /// <b>순서가 전부다.</b> 새 플랜을 먼저 등록해 새 <c>PlanId</c> 를 만든 뒤에야
    /// <c>_byBucket</c> 을 그 쪽으로 돌린다 — 반대로 하면 그 사이에 읽는 틱이
    /// <b>아직 채워지지 않은 슬롯</b>을 가리키는 id 를 본다.
    /// </para>
    ///
    /// <para>
    /// <b>옛 플랜을 지우지 않는다.</b> 지금 그 플랜을 쓰고 있는 NPC 가 있고, 스텝 경계에
    /// 도달해야 새 것으로 넘어간다 (§2.6). 레지스트리가 조금 자라지만 리로드는 드물다.
    /// </para>
    ///
    /// <para>
    /// <b>디스크가 진실이다.</b> <paramref name="fresh"/> 에 없는 버킷은 미생성으로 되돌린다 —
    /// 파일을 지웠는데 옛 플랜이 남으면 "지운 것이 계속 돈다".
    /// </para>
    ///
    /// <para><b>개별 플랜 풀은 건드리지 않는다</b> — 프리픽스가 같으면 그 플랜은 여전히 유효하다.</para>
    /// </summary>
    /// <param name="fresh">디스크에서 새로 읽은 스토어.</param>
    /// <returns>버킷 표에서 바뀐 칸 수.</returns>
    public int Adopt(PlanStore fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);

        if (fresh._byBucket.Length != _byBucket.Length
            || fresh._byArchetype.Length != _byArchetype.Length)
        {
            throw new ArgumentException(
                $"버킷 공간이 다르다: {fresh._byBucket.Length} vs {_byBucket.Length}. "
                + "구조가 바뀌었으면 리로드가 아니라 재기동이다.",
                nameof(fresh));
        }

        int changed = 0;

        for (int index = 0; index < _byBucket.Length; index++)
        {
            int freshId = Volatile.Read(ref fresh._byBucket[index]);
            var freshOrigin = (PlanOrigin)Volatile.Read(ref fresh._origin[index]);

            if (freshId == IdlePlanId)
            {
                if (Volatile.Read(ref _byBucket[index]) != IdlePlanId)
                {
                    Volatile.Write(ref _byBucket[index], IdlePlanId);
                    Volatile.Write(ref _origin[index], (int)PlanOrigin.Fallback);
                    changed++;
                }

                continue;
            }

            CompiledPlan freshPlan = fresh[freshId];
            int liveId = Volatile.Read(ref _byBucket[index]);

            // <b>같은 내용이면 등록하지 않는다.</b> 레지스트리는 65,536칸이고 회수가 없다 —
            // 2,880 버킷을 매 리로드마다 새로 등록하면 22회에 찬다. 대부분의 리로드는
            // 몇 칸만 바뀌므로, 바뀐 것만 세는 쪽이 보고도 정직해진다.
            if (liveId != IdlePlanId
                && freshPlan.SameContentAs(this[liveId])
                && (PlanOrigin)Volatile.Read(ref _origin[index]) == freshOrigin)
            {
                continue;
            }

            // 먼저 등록해 새 id 를 만든다. 그 다음에야 버킷을 돌린다.
            PlanId id = Register(freshPlan);

            Volatile.Write(ref _byBucket[index], id.Value);
            Volatile.Write(ref _origin[index], (int)freshOrigin);

            changed++;
        }

        for (int archetype = 0; archetype < _byArchetype.Length; archetype++)
        {
            int freshId = Volatile.Read(ref fresh._byArchetype[archetype]);

            if (freshId == IdlePlanId)
            {
                continue;   // 폴백은 지우지 않는다 — 없으면 NPC 가 설 자리가 없다
            }

            CompiledPlan freshPlan = fresh[freshId];
            int liveId = Volatile.Read(ref _byArchetype[archetype]);

            if (liveId != IdlePlanId && freshPlan.SameContentAs(this[liveId]))
            {
                continue;
            }

            PlanId id = Register(freshPlan);

            Volatile.Write(ref _byArchetype[archetype], id.Value);
        }

        return changed;
    }

    /// <summary>pinned 버킷 수. manifest 의 <c>counts.pinned</c> 다 (docs/03 §7).</summary>
    public int PinnedBuckets
    {
        get
        {
            int pinned = 0;

            for (int i = 0; i < _origin.Length; i++)
            {
                if (IsPinned(i))
                {
                    pinned++;
                }
            }

            return pinned;
        }
    }

    /// <summary>
    /// 킬스위치. <c>PlanStore</c> 가 끊기면 버킷 표를 보지 않고 폴백으로만 답한다 (docs/15 §4).
    ///
    /// <b>끊겨도 <see cref="Resolve"/> 는 여전히 null 을 반환하지 않는다</b> —
    /// 시나리오 C 3단계가 "폴백 40개만으로 5,000 NPC 가 도는가"를 보는 것이고,
    /// 그 성질이 여기서 깨지면 NPC 가 멈춘다 (CLAUDE.md §2.6).
    /// 기본값은 아무것도 끊기지 않은 <see cref="KillSwitchState.None"/> 이다.
    /// </summary>
    public KillSwitchState Switches { get; set; } = KillSwitchState.None;

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
        int planId = Switches.IsDisabled(KillSwitchTarget.PlanStore)
            ? IdlePlanId
            : Volatile.Read(ref _byBucket[index]);

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

    /// <summary>
    /// 버킷 플랜을 <b>세지 않고</b> 본다. 미생성이면 null.
    /// 저장(T3-07)·manifest 집계처럼 히트율에 실려서는 안 되는 경로가 쓴다.
    /// </summary>
    public CompiledPlan? PeekBucket(BucketKey key)
    {
        int planId = Volatile.Read(ref _byBucket[key.ToIndex()]);

        return planId == IdlePlanId ? null : this[planId];
    }

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

        for (int i = 0; i < _hits.Length; i++)
        {
            int archetype = i / BucketKey.PerArchetype;

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

    /// <summary>
    /// 개별 오버라이드 플랜 풀. <b>기동 시 1회만 붙인다.</b> docs/13 §2 의 세 번째 표다.
    /// 붙이지 않으면 음수 id 는 최후 플랜으로 읽힌다 — 즉 워커가 만든 개별 플랜이 조용히 사라진다.
    /// </summary>
    public IndividualPlanPool? Individual { get; set; }

    /// <summary>개별 플랜을 실제로 찾아 쓴 횟수.</summary>
    public long IndividualHits => Volatile.Read(ref _individualHits);

    /// <summary>슬롯이 회수돼 개별 플랜을 잃은 횟수. 크면 풀 회전율이 과하다 (docs/13 §6).</summary>
    public long IndividualLost => Volatile.Read(ref _individualLost);

    /// <summary>
    /// 이 NPC 가 지금 실행해야 할 플랜. docs/13 §2 의 id 인코딩을 여기서 푼다.
    ///
    /// <list type="bullet">
    ///   <item><c>&gt;= 0</c> — 레지스트리 id. 그대로 조회한다</item>
    ///   <item><c>&lt; 0</c> — 개별 풀 슬롯. 주인이 맞으면 그 플랜, LRU 로 회수됐으면 <b>false</b></item>
    /// </list>
    ///
    /// <b>false 를 받은 호출부는 버킷 플랜으로 되돌아가야 한다</b>
    /// (<see cref="IndividualPlanPool"/> 주석). 여기서 아키타입 폴백을 고를 수 없는 이유는
    /// 스토어가 NPC 의 아키타입을 모르기 때문이다 — 그것은 런타임의 <c>NpcStore</c> 에 있다.
    /// </summary>
    /// <param name="npc">NpcStore 첨자. 개별 슬롯의 주인 확인에 쓴다.</param>
    /// <param name="planId"><c>NpcStore.PlanId</c> 값.</param>
    /// <param name="tick">현재 틱. 개별 풀의 LRU 기준을 갱신한다.</param>
    /// <param name="plan">찾은 플랜. false 여도 <b>절대 null 이 아니다</b> (최후 플랜).</param>
    public bool TryFor(int npc, int planId, long tick, out CompiledPlan plan)
    {
        if (!IndividualPlanPool.IsIndividual(planId))
        {
            plan = this[planId];
            return true;
        }

        if (Individual is { } pool && pool.TryGet(planId, npc, tick, out plan))
        {
            Interlocked.Increment(ref _individualHits);
            return true;
        }

        Interlocked.Increment(ref _individualLost);
        plan = _idle;
        return false;
    }

    /// <summary>
    /// <see cref="TryFor"/> 와 같지만 <b>개별 풀의 LRU 를 갱신하지 않는다.</b>
    /// 계측·대시보드처럼 상태를 바꾸면 안 되는 경로가 쓴다 (<see cref="IndividualPlanPool.TryPeek"/>).
    /// </summary>
    public bool TryPeekFor(int npc, int planId, out CompiledPlan plan)
    {
        if (!IndividualPlanPool.IsIndividual(planId))
        {
            plan = this[planId];
            return true;
        }

        if (Individual is { } pool && pool.TryPeek(planId, npc, out plan))
        {
            return true;
        }

        plan = _idle;
        return false;
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

    private bool IsPinned(int bucketIndex) =>
        (PlanOrigin)Volatile.Read(ref _origin[bucketIndex]) == PlanOrigin.Pinned
        && Volatile.Read(ref _byBucket[bucketIndex]) != IdlePlanId;

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
