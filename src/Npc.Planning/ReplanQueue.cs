namespace Npc.Planning;

/// <summary>
/// 재계획 대기열. docs/14 §2.
///
/// <b>라운드로빈이 아니라 점수순이다.</b> 고정 크기 최대 힙이고 <b>할당이 0</b> 이다 —
/// 기동 시 배열 셋을 잡고 그 뒤로는 첨자 연산만 한다.
///
/// <b>같은 NPC 를 두 번 넣지 않는다.</b> 중복을 허용하면 큐가 즉시 포화된다 (CLAUDE.md §7).
/// 이미 들어 있으면 <see cref="_heapPos"/> 로 검출해 점수만 올린다.
///
/// <b>용량에 상한을 둔다</b>(<see cref="DefaultCapacity"/>). 큐가 무한히 자라면 오래된 요청이 쌓여
/// "이미 상황이 바뀐 NPC" 를 재계획하게 된다. 넘치면 최하위부터 버린다 —
/// 버려진 NPC 는 기존 플랜을 계속 쓰므로 안전하다 (docs/14 §2).
///
/// <b>동점은 npc 첨자 오름차순으로 가른다.</b> 힙 구조에 순서를 맡기면 같은 점수의 처리 순서가
/// 삽입 이력에 따라 달라져 리플레이가 깨진다 (CLAUDE.md §2.3).
///
/// <para>
/// <b>반환값 규약.</b> <c>TryEnqueue</c> 는 <b>새 항목이 큐에 들어갔을 때만</b> true 다.
/// 중복(점수 갱신)은 false 이고 <see cref="Deduplicated"/> 가 올라간다 — docs/14 §2 의 코드 조각은
/// 중복에도 true 를 돌려주지만, 그러면 호출부가 "몇 마리가 새로 대기하게 됐나" 를 셀 수 없다.
/// </para>
/// </summary>
public sealed class ReplanQueue
{
    /// <summary>기본 용량. docs/14 §2 의 4096.</summary>
    public const int DefaultCapacity = 4_096;

    /// <summary>인터럽트 점수의 기준값. 일반 점수는 가중치 합이라 이보다 훨씬 작다 (docs/14 §2).</summary>
    public const float UrgentBase = 1_000f;

    private readonly int[] _heap;        // 힙 위치 → npc 첨자
    private readonly float[] _score;     // npc 첨자 → 현재 점수
    private readonly int[] _heapPos;     // npc 첨자 → 힙 위치. -1 = 큐에 없음
    private readonly int _capacity;
    private int _count;
    private int _urgentCount;

    /// <summary>큐를 만든다. 기동 시 1회. 이후 재할당하지 않는다.</summary>
    /// <param name="npcCapacity">NPC 수.</param>
    /// <param name="queueCapacity">
    /// 동시에 대기할 수 있는 최대 NPC 수. 0 이면 <c>min(npcCapacity, 4096)</c> 이다 —
    /// docs/14 §2 가 4096 을 상한으로 정했고, NPC 가 그보다 적으면 더 잡을 이유가 없다.
    /// </param>
    public ReplanQueue(int npcCapacity, int queueCapacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(npcCapacity);

        _capacity = queueCapacity > 0 ? queueCapacity : Math.Min(npcCapacity, DefaultCapacity);
        MaxUrgentSlots = Math.Max(1, _capacity / 2);
        _heap = new int[_capacity];
        _score = new float[npcCapacity];
        _heapPos = new int[npcCapacity];

        Array.Fill(_heapPos, -1);
    }

    /// <summary>대기 중인 수.</summary>
    public int Count => _count;

    /// <summary>큐 용량.</summary>
    public int Capacity => _capacity;

    /// <summary>큐가 가득 차서 버린 요청 수. 대시보드가 본다.</summary>
    public long Dropped { get; private set; }

    /// <summary>중복이라 점수만 갱신한 횟수.</summary>
    public long Deduplicated { get; private set; }

    /// <summary>큐에 새로 들어간 요청 수. 대시보드의 "초당 유입" 이 이 값의 차분이다 (docs/11 §10).</summary>
    public long TotalEnqueued { get; private set; }

    /// <summary>포화 상태에서 최하위를 밀어내고 들어간 횟수. 이게 크면 예산이 모자란다.</summary>
    public long Evicted { get; private set; }

    /// <summary>
    /// 인터럽트 항목이 차지할 수 있는 최대 칸 수 = 용량의 50% (docs/14 §10).
    ///
    /// <b>상한이 없으면 일반 재계획이 영원히 안 된다.</b> 인터럽트 점수는 1000 이상이라
    /// 일반 항목(상한 9.5)을 전부 밀어낸다 — 공성 한 번에 마을 전체가 인터럽트를 쏘면
    /// 큐 4096칸이 통째로 인터럽트로 차고, 그때부터 이탈 판정과 스텝 실패는 처리되지 않는다.
    /// </summary>
    public int MaxUrgentSlots { get; }

    /// <summary>지금 큐에 있는 인터럽트 항목 수.</summary>
    public int UrgentCount => _urgentCount;

    /// <summary>인터럽트 슬롯 상한에 걸려 거절한 수. 공성 중에 크게 오르는 것이 정상이다.</summary>
    public long UrgentDropped { get; private set; }

    /// <summary>이 점수가 인터럽트인가.</summary>
    public static bool IsUrgent(float score) => score >= UrgentBase;

    /// <summary>
    /// 재계획 요청. 이미 들어 있으면 점수만 올리고 false 를 돌려준다.
    /// 포화 상태면 최하위와 비교해 더 급한 쪽만 남긴다. <b>할당 0.</b>
    /// </summary>
    /// <param name="npc">NPC 첨자.</param>
    /// <param name="score">우선순위. 클수록 먼저 나온다.</param>
    /// <returns>새 항목이 큐에 들어갔으면 true.</returns>
    public bool TryEnqueue(int npc, float score)
    {
        if ((uint)npc >= (uint)_heapPos.Length)
        {
            return false;
        }

        if (_heapPos[npc] >= 0)
        {
            // 이미 대기 중이다. 더 급한 요청이면 점수만 올린다.
            UpdateScore(npc, MathF.Max(_score[npc], score));
            Deduplicated++;
            return false;
        }

        // 인터럽트 슬롯 상한 (docs/14 §10). 새로 들어오는 인터럽트만 막는다 —
        // 이미 큐에 있는 항목의 점수 승격은 칸을 더 쓰지 않으므로 일반 항목을 굶기지 않는다.
        if (IsUrgent(score) && _urgentCount >= MaxUrgentSlots)
        {
            UrgentDropped++;
            Dropped++;
            return false;
        }

        if (_count == _capacity)
        {
            int lowest = LowestSlot();

            if (!Precedes(npc, score, _heap[lowest]))
            {
                Dropped++;
                return false;   // 최하위보다 급하지 않으면 거절한다
            }

            RemoveAt(lowest);
            Evicted++;
        }

        Insert(npc, score);
        TotalEnqueued++;
        return true;
    }

    /// <summary>
    /// 인터럽트가 넣는 요청. docs/14 §2 — 일반 점수 범위를 넘어서게 <see cref="UrgentBase"/> 를 얹는다.
    /// </summary>
    public bool TryEnqueueUrgent(int npc, float urgency) => TryEnqueue(npc, UrgentBase + urgency);

    /// <summary>가장 급한 요청을 꺼낸다. 비어 있으면 -1.</summary>
    public int DequeueMax() => TryDequeueMax(out int npc) ? npc : -1;

    /// <summary>가장 급한 요청을 꺼낸다. docs/14 §4 의 워커가 부르는 이름이다.</summary>
    public bool TryDequeueMax(out int npc) => TryDequeue(out npc, out _);

    /// <summary>가장 급한 요청을 점수와 함께 꺼낸다.</summary>
    public bool TryDequeue(out int npc, out float score)
    {
        if (_count == 0)
        {
            npc = -1;
            score = 0;
            return false;
        }

        npc = _heap[0];
        score = _score[npc];
        RemoveAt(0);
        return true;
    }

    /// <summary>이 NPC 가 대기 중인가.</summary>
    public bool Contains(int npc) => (uint)npc < (uint)_heapPos.Length && _heapPos[npc] >= 0;

    /// <summary>대기 중인 요청의 점수. 없으면 -1.</summary>
    public float ScoreOf(int npc) =>
        (uint)npc < (uint)_heapPos.Length && _heapPos[npc] >= 0 ? _score[npc] : -1f;

    /// <summary>
    /// 대기 중인 항목의 점수 분포. docs/14 §8 재계획 패널의 히스토그램.
    ///
    /// 마지막 칸은 <b>인터럽트 전용</b>이다 (<see cref="UrgentBase"/> 이상).
    /// 그 앞 칸들이 <c>[0, UrgentBase)</c> 를 균등 분할한다 — 일반 점수 상한은 가중치 합(약 9.5)이라
    /// 실제로는 앞쪽 한두 칸에 몰린다. 그 쏠림 자체가 봐야 할 그림이다.
    ///
    /// <b>할당 0.</b> 대시보드가 부르지만 큐 크기(≤ 4096)만 훑는다.
    /// </summary>
    /// <param name="buckets">채울 칸. 2칸 이상이어야 한다.</param>
    /// <returns>센 항목 수.</returns>
    public int ScoreHistogram(Span<int> buckets)
    {
        if (buckets.Length < 2)
        {
            return 0;
        }

        buckets.Clear();

        int normal = buckets.Length - 1;
        float width = UrgentBase / normal;

        for (int slot = 0; slot < _count; slot++)
        {
            float score = _score[_heap[slot]];

            int bucket = IsUrgent(score)
                ? normal
                : Math.Clamp((int)(score / width), 0, normal - 1);

            buckets[bucket]++;
        }

        return _count;
    }

    /// <summary>전부 비운다.</summary>
    public void Clear()
    {
        Array.Fill(_heapPos, -1);
        _count = 0;
        _urgentCount = 0;
    }

    // ---------------------------------------------------------------- 힙

    /// <summary>
    /// <paramref name="npc"/>(점수 <paramref name="score"/>) 가 <paramref name="other"/> 보다 먼저 나오는가.
    /// 동점은 첨자 오름차순 — 순서가 흔들리면 리플레이가 깨진다.
    /// </summary>
    private bool Precedes(int npc, float score, int other) =>
        score != _score[other] ? score > _score[other] : npc < other;

    private bool Precedes(int a, int b) => Precedes(a, _score[a], b);

    private void Insert(int npc, float score)
    {
        _score[npc] = score;
        _heap[_count] = npc;
        _heapPos[npc] = _count;
        _count++;

        if (IsUrgent(score))
        {
            _urgentCount++;
        }

        SiftUp(_count - 1);
    }

    private void UpdateScore(int npc, float score)
    {
        float previous = _score[npc];

        if (score == previous)
        {
            return;
        }

        if (IsUrgent(score) != IsUrgent(previous))
        {
            _urgentCount += IsUrgent(score) ? 1 : -1;
        }

        _score[npc] = score;
        int slot = _heapPos[npc];

        if (score > previous)
        {
            SiftUp(slot);
        }
        else
        {
            SiftDown(slot);
        }
    }

    /// <summary>힙에서 한 칸을 뺀다. 마지막 원소를 그 자리에 넣고 양방향으로 다시 앉힌다.</summary>
    private void RemoveAt(int slot)
    {
        int removed = _heap[slot];
        _heapPos[removed] = -1;

        if (IsUrgent(_score[removed]))
        {
            _urgentCount--;
        }

        int last = --_count;

        if (slot == last)
        {
            return;
        }

        int moved = _heap[last];
        _heap[slot] = moved;
        _heapPos[moved] = slot;

        SiftDown(slot);
        SiftUp(slot);
    }

    /// <summary>
    /// 최하위 항목의 힙 위치. 최대 힙에서 최솟값은 <b>반드시 잎</b> 이므로 잎만 훑는다.
    ///
    /// 잎은 절반이라 용량 4096 이면 비교 2048 회다. 비싸 보이지만 <b>포화 상태에서만</b> 돈다 —
    /// 포화는 이미 예산이 모자란 퇴화 구간이고, 그때 "아무 잎이나" 버리면 사양의
    /// "최하위 축출" 이 성립하지 않아 급한 요청이 낮은 점수에 밀린다.
    /// </summary>
    private int LowestSlot()
    {
        int first = (_count - 1) / 2;
        int lowest = first;

        for (int slot = first + 1; slot < _count; slot++)
        {
            if (Precedes(_heap[lowest], _heap[slot]))
            {
                lowest = slot;
            }
        }

        return lowest;
    }

    private void SiftUp(int slot)
    {
        int npc = _heap[slot];

        while (slot > 0)
        {
            int parent = (slot - 1) / 2;

            if (!Precedes(npc, _score[npc], _heap[parent]))
            {
                break;
            }

            Place(_heap[parent], slot);
            slot = parent;
        }

        Place(npc, slot);
    }

    private void SiftDown(int slot)
    {
        int npc = _heap[slot];

        while (true)
        {
            int left = (2 * slot) + 1;

            if (left >= _count)
            {
                break;
            }

            int right = left + 1;
            int best = right < _count && Precedes(_heap[right], _heap[left]) ? right : left;

            if (!Precedes(_heap[best], _score[_heap[best]], npc))
            {
                break;
            }

            Place(_heap[best], slot);
            slot = best;
        }

        Place(npc, slot);
    }

    private void Place(int npc, int slot)
    {
        _heap[slot] = npc;
        _heapPos[npc] = slot;
    }
}
