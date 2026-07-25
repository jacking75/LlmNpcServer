namespace Npc.Planning;

/// <summary>
/// 재계획 대기열. docs/14 §2 (정식은 P4).
///
/// <b>P1 은 FIFO + 중복 제거만 한다.</b> 우선순위 힙은 T4-01 에서 교체한다.
/// 인터페이스를 바꾸지 않고 교체할 수 있도록 <see cref="TryEnqueue"/> 가 점수를 이미 받는다.
///
/// <b>같은 NPC 를 두 번 넣지 않는다.</b> 중복을 허용하면 큐가 즉시 포화된다 (CLAUDE.md §7).
/// 이미 들어 있으면 점수만 갱신한다.
/// </summary>
public sealed class ReplanQueue
{
    private readonly int[] _ring;
    private readonly int[] _score;      // 첨자 = npc. -1 = 큐에 없음
    private readonly int _capacity;
    private int _head;
    private int _tail;
    private int _count;

    /// <summary>큐를 만든다. 기동 시 1회. 이후 재할당하지 않는다.</summary>
    /// <param name="npcCapacity">NPC 수.</param>
    /// <param name="queueCapacity">동시에 대기할 수 있는 최대 NPC 수. 기본은 NPC 수와 같다.</param>
    public ReplanQueue(int npcCapacity, int queueCapacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(npcCapacity);

        _capacity = queueCapacity > 0 ? queueCapacity : npcCapacity;
        _ring = new int[_capacity];
        _score = new int[npcCapacity];

        Array.Fill(_score, -1);
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

    /// <summary>
    /// 재계획 요청. 이미 들어 있으면 점수만 올리고 false 를 돌려준다.
    /// 할당 0.
    /// </summary>
    /// <param name="npc">NPC 첨자.</param>
    /// <param name="score">긴급도. P1 은 순서에 쓰지 않지만 P4 의 힙이 그대로 쓴다.</param>
    public bool TryEnqueue(int npc, int score)
    {
        if ((uint)npc >= (uint)_score.Length)
        {
            return false;
        }

        if (_score[npc] >= 0)
        {
            // 이미 대기 중이다. 더 급한 요청이면 점수만 올린다.
            if (score > _score[npc])
            {
                _score[npc] = score;
            }

            Deduplicated++;
            return false;
        }

        if (_count >= _capacity)
        {
            Dropped++;
            return false;
        }

        _ring[_tail] = npc;
        _tail = (_tail + 1) % _capacity;
        _count++;
        _score[npc] = score;
        TotalEnqueued++;
        return true;
    }

    /// <summary>가장 오래된 요청을 꺼낸다.</summary>
    public bool TryDequeue(out int npc, out int score)
    {
        if (_count == 0)
        {
            npc = -1;
            score = 0;
            return false;
        }

        npc = _ring[_head];
        _head = (_head + 1) % _capacity;
        _count--;

        score = _score[npc];
        _score[npc] = -1;
        return true;
    }

    /// <summary>이 NPC 가 대기 중인가.</summary>
    public bool Contains(int npc) => (uint)npc < (uint)_score.Length && _score[npc] >= 0;

    /// <summary>대기 중인 요청의 점수. 없으면 -1.</summary>
    public int ScoreOf(int npc) => (uint)npc < (uint)_score.Length ? _score[npc] : -1;

    /// <summary>전부 비운다.</summary>
    public void Clear()
    {
        Array.Fill(_score, -1);
        _head = 0;
        _tail = 0;
        _count = 0;
    }
}
