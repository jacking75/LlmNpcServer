namespace Npc.Planning;

/// <summary>
/// 틱 루프 ↔ 재계획 워커 사이의 인계 통로. docs/14 §2·§4.
///
/// <para>
/// <b>왜 필요한가.</b> <see cref="ReplanQueue"/> 는 고정 크기 힙이고 <b>동기화가 없다</b> —
/// 배열 셋(<c>_heap</c>·<c>_score</c>·<c>_heapPos</c>)과 <c>_count</c> 를 그대로 만진다.
/// 그런데 워커가 그 힙을 직접 꺼내 쓰면 틱 루프의 <c>TryEnqueue</c> 와 경합한다 —
/// 용량 검사와 삽입 사이 창에서 <c>_count</c> 가 용량을 넘어
/// <c>_heap[_count]</c> 가 범위를 벗어난다 (2026-07-28 실측: 8회 중 2회 <c>IndexOutOfRangeException</c>).
/// </para>
///
/// <para>
/// <b>고치는 방법은 힙에 락을 거는 것이 아니다.</b> CLAUDE.md §2.1 이 틱 루프의
/// <c>lock</c>·<c>Monitor</c>·<c>SemaphoreSlim</c> 을 금지한다 — 워커 8개가 다투는 락을
/// 틱 루프가 같이 잡으면 그 순간 p99 가 무너진다. 그래서 <b>힙은 틱 루프만 만지게 하고</b>
/// 워커와는 이 통로로만 주고받는다.
/// </para>
///
/// <code>
///   틱 루프                              워커 N개
///   ────────                             ────────
///   ReplanQueue(힙)  ──Pump──▶  Pending  ──TryClaim──▶  LLM 호출
///        ▲                                                  │
///        └────DrainReturns────  Returns  ◀──TryReturn───────┘  (낡은 요청)
/// </code>
///
/// <para>
/// <b>낡은 요청을 워커가 힙에 되넣지 않는다.</b> 그것이 원래 경합의 두 번째 경로였다
/// (<c>ReplanSnapshots.Requeue</c> → <c>ReplanQueue.TryEnqueue</c>).
/// 워커는 <see cref="TryReturn"/> 으로 되돌려만 놓고, 힙에 다시 넣는 것은
/// 다음 틱의 <see cref="DrainReturns"/> 가 한다 — T4-04 의 "폐기하고 지금 상태로 다시 넣는다" 는
/// 그대로 지켜지되 한 틱 늦어진다.
/// </para>
///
/// <para>
/// <b>두 링 다 할당이 0 이다.</b> 기동 시 배열을 잡고 그 뒤로는 첨자와
/// <see cref="Interlocked"/> 연산만 쓴다 (CLAUDE.md §2.1).
/// </para>
/// </summary>
public sealed class ReplanHandoff
{
    /// <summary>한 틱에 힙에서 통로로 넘기는 최대 건수.</summary>
    /// <remarks>
    /// 워커가 실측 3~5초에 한 건을 처리하므로(<c>W1_perf.csv</c>) 10Hz 틱에서 넘칠 이유가 없다.
    /// 상한을 두는 것은 <b>틱 예산 방어</b>다 — 큐가 포화(4,096)일 때 한 틱에 전부 옮기면
    /// 그 틱만 O(4096 log n) 이 된다.
    /// </remarks>
    public const int DefaultPumpPerTick = 64;

    private readonly Ring _pending;
    private readonly Ring _returns;

    /// <summary>통로를 만든다. 기동 시 1회.</summary>
    /// <param name="capacity">
    /// 각 링의 칸 수. 2의 거듭제곱으로 올림한다.
    /// 워커 수 × 여유면 충분하다 — 대기 행렬의 본체는 힙이지 이 통로가 아니다.
    /// </param>
    public ReplanHandoff(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _pending = new Ring(capacity);
        _returns = new Ring(capacity);
    }

    /// <summary>워커가 가져갈 수 있게 놓인 건수. 스필오버 판정(docs/14 §4)이 읽는다.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>되돌아온 건수. 다음 <see cref="DrainReturns"/> 가 힙으로 옮긴다.</summary>
    public int ReturnCount => _returns.Count;

    /// <summary>통로가 꽉 차서 힙에 남겨 둔 건수 (누계).</summary>
    public long PumpBlocked { get; private set; }

    /// <summary>되돌리지 못하고 버린 건수 (누계). 그 NPC 는 다음 인지 스캔이 다시 잡는다.</summary>
    public long ReturnDropped => Interlocked.Read(ref _returnDropped);

    private long _returnDropped;

    /// <summary>
    /// <b>틱 루프 전용.</b> 되돌아온 요청을 힙에 다시 넣는다.
    /// <see cref="Pump"/> 보다 먼저 불러야 이번 틱에 다시 나갈 기회를 얻는다.
    /// </summary>
    /// <param name="queue">재계획 힙. 틱 루프가 소유한다.</param>
    /// <returns>힙에 되넣은 건수.</returns>
    public int DrainReturns(ReplanQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        int moved = 0;

        while (_returns.TryDequeue(out int npc, out float score))
        {
            queue.TryEnqueue(npc, score);
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// <b>틱 루프 전용.</b> 힙에서 급한 순으로 꺼내 통로에 놓는다.
    ///
    /// <b>통로가 차면 거기서 멈춘다</b> — 힙에 남겨 두는 편이 낫다. 힙은 점수순이라
    /// 다음 틱에 더 급한 것이 앞설 수 있지만, 통로는 FIFO 라 한 번 놓인 것의 순서는 굳는다.
    /// </summary>
    /// <param name="queue">재계획 힙.</param>
    /// <param name="max">이번 틱에 옮길 상한. 0 이하면 <see cref="DefaultPumpPerTick"/>.</param>
    /// <returns>옮긴 건수.</returns>
    public int Pump(ReplanQueue queue, int max = 0)
    {
        ArgumentNullException.ThrowIfNull(queue);

        int limit = max > 0 ? max : DefaultPumpPerTick;
        int moved = 0;

        while (moved < limit && queue.Count > 0)
        {
            if (_pending.IsFull)
            {
                PumpBlocked++;
                break;
            }

            if (!queue.TryDequeue(out int npc, out float score))
            {
                break;
            }

            if (!_pending.TryEnqueue(npc, score))
            {
                // 통로가 방금 찼다. 힙으로 되돌린다 — 요청을 잃지 않는다.
                queue.TryEnqueue(npc, score);
                PumpBlocked++;
                break;
            }

            moved++;
        }

        return moved;
    }

    /// <summary><b>워커 전용.</b> 일감 하나를 가져간다. 없으면 false.</summary>
    public bool TryClaim(out int npc, out float score) => _pending.TryDequeue(out npc, out score);

    /// <summary>
    /// <b>워커 전용.</b> 낡아서 처리하지 못한 요청을 돌려놓는다.
    /// 실패하면(통로 포화) 버린다 — 그 NPC 는 다음 인지 스캔이 다시 잡는다.
    /// </summary>
    public bool TryReturn(int npc, float score)
    {
        if (_returns.TryEnqueue(npc, score))
        {
            return true;
        }

        Interlocked.Increment(ref _returnDropped);
        return false;
    }

    /// <summary>전부 비운다. 테스트가 쓴다.</summary>
    public void Clear()
    {
        while (_pending.TryDequeue(out _, out _))
        {
        }

        while (_returns.TryDequeue(out _, out _))
        {
        }
    }

    /// <summary>
    /// 고정 크기 락프리 MPMC 링 (Vyukov 방식).
    ///
    /// 칸마다 <c>Sequence</c> 를 두어 "쓸 차례인가 / 읽을 차례인가" 를 가른다.
    /// 머리·꼬리 첨자만 <see cref="Interlocked.CompareExchange(ref long, long, long)"/> 로 다투므로
    /// 락이 없고 할당도 없다.
    ///
    /// <b>왜 단순 head/tail 링이 아닌가.</b> 소비자가 CAS 로 자리를 잡은 뒤 값을 읽으면,
    /// 그 사이 생산자가 같은 칸을 덮어쓸 수 있다. 칸별 시퀀스가 그 창을 닫는다.
    /// </summary>
    private sealed class Ring
    {
        private readonly Cell[] _cells;
        private readonly int _mask;

        private long _enqueuePos;
        private long _dequeuePos;

        public Ring(int capacity)
        {
            int size = 1;

            while (size < capacity)
            {
                size <<= 1;
            }

            _cells = new Cell[size];
            _mask = size - 1;

            for (int i = 0; i < size; i++)
            {
                _cells[i].Sequence = i;
            }
        }

        /// <summary>대략의 적재량. 계측용이라 정확한 순간값이 아니어도 된다.</summary>
        public int Count
        {
            get
            {
                long count = Volatile.Read(ref _enqueuePos) - Volatile.Read(ref _dequeuePos);

                return count <= 0 ? 0 : (int)Math.Min(count, _mask + 1);
            }
        }

        /// <summary>대략 꽉 찼는가. <see cref="Count"/> 와 같은 성격이다.</summary>
        public bool IsFull => Count > _mask;

        public bool TryEnqueue(int npc, float score)
        {
            long pos = Volatile.Read(ref _enqueuePos);

            while (true)
            {
                ref Cell cell = ref _cells[pos & _mask];
                long difference = Volatile.Read(ref cell.Sequence) - pos;

                if (difference == 0)
                {
                    if (Interlocked.CompareExchange(ref _enqueuePos, pos + 1, pos) == pos)
                    {
                        cell.Npc = npc;
                        cell.Score = score;

                        // 값 쓰기가 먼저 보이고 그 다음 시퀀스가 보여야 한다.
                        Volatile.Write(ref cell.Sequence, pos + 1);
                        return true;
                    }
                }
                else if (difference < 0)
                {
                    return false;   // 꽉 찼다
                }
                else
                {
                    pos = Volatile.Read(ref _enqueuePos);
                    continue;
                }

                pos = Volatile.Read(ref _enqueuePos);
            }
        }

        public bool TryDequeue(out int npc, out float score)
        {
            long pos = Volatile.Read(ref _dequeuePos);

            while (true)
            {
                ref Cell cell = ref _cells[pos & _mask];
                long difference = Volatile.Read(ref cell.Sequence) - (pos + 1);

                if (difference == 0)
                {
                    if (Interlocked.CompareExchange(ref _dequeuePos, pos + 1, pos) == pos)
                    {
                        npc = cell.Npc;
                        score = cell.Score;

                        // 이 칸을 한 바퀴 뒤의 쓰기에 넘긴다.
                        Volatile.Write(ref cell.Sequence, pos + _mask + 1);
                        return true;
                    }
                }
                else if (difference < 0)
                {
                    npc = -1;
                    score = 0;
                    return false;   // 비었다
                }
                else
                {
                    pos = Volatile.Read(ref _dequeuePos);
                    continue;
                }

                pos = Volatile.Read(ref _dequeuePos);
            }
        }

        /// <summary>칸 하나. <c>Sequence</c> 가 이 칸의 소유권을 정한다.</summary>
        private struct Cell
        {
            public long Sequence;
            public int Npc;
            public float Score;
        }
    }
}
