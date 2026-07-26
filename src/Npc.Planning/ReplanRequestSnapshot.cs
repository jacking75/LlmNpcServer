using System.Numerics;
using Npc.Contracts;
using Npc.Core;

namespace Npc.Planning;

/// <summary>
/// 큐에 넣을 때의 상태 스냅샷 하나. docs/14 §10 "재계획 결과가 이미 낡음".
/// </summary>
/// <param name="Flags">큐 삽입 시점의 월드 플래그.</param>
/// <param name="QueuedTick">큐에 넣은 틱. 대기 시간 통계의 분모다.</param>
/// <param name="Score">삽입 시점의 점수. 재삽입할 때 그대로 쓴다.</param>
public readonly record struct ReplanRequestSnapshot(WorldFlags Flags, long QueuedTick, float Score)
{
    /// <summary>기록된 적이 없는 스냅샷.</summary>
    public static ReplanRequestSnapshot None { get; } = new(WorldFlags.None, -1, 0);

    /// <summary>기록된 적이 있는가.</summary>
    public bool Exists => QueuedTick >= 0;
}

/// <summary>
/// 재계획 요청의 상태 스냅샷 표. docs/14 §10.
///
/// <b>문제.</b> 요청이 큐에서 대기하는 동안 상황이 바뀐다. T1 로컬 지연이 실측 3.7~5.1s
/// (`W1_perf.csv`) 이고 10Hz 틱이므로 <b>한 요청이 대기+생성 사이에 37~51틱을 보낸다.</b>
/// 그 사이 밤이 되고 전투가 끝나고 존이 War 로 바뀌면, 돌아온 플랜은 이미 존재하지 않는 상황의 것이다.
/// 그것을 그대로 스왑하면 다음 인지 스캔이 즉시 이탈로 판정해 또 재계획을 요청한다 — LLM 예산이
/// 왕복만 하고 아무 진전이 없다.
///
/// <b>해법.</b> 큐에 넣을 때 플래그를 찍어 두고, 워커가 꺼낼 때 지금 플래그와 비교한다.
/// 어긋난 비트가 <see cref="MaxDrift"/> 를 넘으면 <b>폐기하고 지금 상태로 재삽입한다.</b>
/// 폐기는 손실이 아니다 — 아직 LLM 을 부르지 않았으므로 버리는 것은 낡은 컨텍스트뿐이다.
///
/// <b>배열 셋뿐이고 할당이 0 이다.</b> 쓰기는 틱 루프(인지 스캔·인터럽트), 읽기는 워커 스레드라
/// <see cref="Volatile"/> 로만 주고받는다 (CLAUDE.md §2.1).
/// </summary>
public sealed class ReplanSnapshots
{
    /// <summary>
    /// 낡음으로 판정하는 어긋난 비트 수. <b>이 값을 넘으면</b> 폐기다.
    ///
    /// 4 는 "평범한 진행" 과 "상황 전환" 을 가르는 선이다 — NPC 가 걸어서 POI 를 옮기면
    /// 장소 플래그가 2비트(이전 것 해제 + 새 것 설정), 시간대가 바뀌면 또 2비트 바뀐다.
    /// 그 둘이 겹친 정도까지는 같은 상황으로 본다. 5비트 이상은 전투·존 상태·인벤토리 중
    /// 무언가가 더 바뀐 것이다.
    /// </summary>
    public const int DefaultMaxDrift = 4;

    private readonly WorldFlags[] _flags;
    private readonly long[] _tick;
    private readonly float[] _score;

    private long _captured;
    private long _discarded;
    private long _accepted;

    /// <summary>표를 만든다. 기동 시 1회. 이후 재할당하지 않는다.</summary>
    /// <param name="npcCapacity">NPC 수.</param>
    /// <param name="maxDrift">낡음 판정 임계. 0 이면 <see cref="DefaultMaxDrift"/>.</param>
    public ReplanSnapshots(int npcCapacity, int maxDrift = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(npcCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDrift);

        MaxDrift = maxDrift > 0 ? maxDrift : DefaultMaxDrift;
        _flags = new WorldFlags[npcCapacity];
        _tick = new long[npcCapacity];
        _score = new float[npcCapacity];

        Array.Fill(_tick, -1);
    }

    /// <summary>낡음 판정 임계 (어긋난 비트 수).</summary>
    public int MaxDrift { get; }

    /// <summary>찍은 스냅샷 수.</summary>
    public long Captured => Volatile.Read(ref _captured);

    /// <summary>낡아서 폐기한 요청 수. <b>이게 크면 큐 대기 시간이 너무 길다</b> (docs/14 §10).</summary>
    public long Discarded => Volatile.Read(ref _discarded);

    /// <summary>워커가 받아들인 요청 수.</summary>
    public long Accepted => Volatile.Read(ref _accepted);

    /// <summary>폐기율. 분모는 워커가 꺼낸 수다.</summary>
    public double DiscardRate
    {
        get
        {
            long total = Accepted + Discarded;

            return total == 0 ? 0 : (double)Discarded / total;
        }
    }

    /// <summary>두 플래그 집합이 몇 비트 어긋나는가. 비트 연산 한 번 + PopCount 한 번.</summary>
    public static int Drift(WorldFlags a, WorldFlags b) => BitOperations.PopCount((ulong)(a ^ b));

    /// <summary>
    /// 큐에 넣으면서 상태를 찍는다. <b>틱 루프에서 불린다</b> — 할당 0.
    /// </summary>
    public void Capture(int npc, WorldFlags flags, Tick now, float score)
    {
        if ((uint)npc >= (uint)_tick.Length)
        {
            return;
        }

        _flags[npc] = flags;
        _score[npc] = score;

        // 틱을 마지막에 쓴다. 워커가 반쯤 채워진 스냅샷을 보고 판정하면 안 된다.
        Volatile.Write(ref _tick[npc], now.Value);
        Interlocked.Increment(ref _captured);
    }

    /// <summary>기록된 스냅샷. 없으면 <see cref="ReplanRequestSnapshot.None"/>.</summary>
    public ReplanRequestSnapshot Of(int npc)
    {
        if ((uint)npc >= (uint)_tick.Length || Volatile.Read(ref _tick[npc]) < 0)
        {
            return ReplanRequestSnapshot.None;
        }

        return new ReplanRequestSnapshot(_flags[npc], _tick[npc], _score[npc]);
    }

    /// <summary>지금 플래그가 스냅샷과 크게 다른가. 스냅샷이 없으면 낡은 것으로 보지 않는다.</summary>
    public bool IsStale(int npc, WorldFlags current)
    {
        ReplanRequestSnapshot snapshot = Of(npc);

        return snapshot.Exists && Drift(snapshot.Flags, current) > MaxDrift;
    }

    /// <summary>
    /// 워커가 큐에서 꺼낸 요청을 받아들일지 판정한다. <b>워커 스레드에서 불린다.</b>
    ///
    /// 낡았으면 false 이고, 호출부는 <see cref="Requeue"/> 로 지금 상태를 다시 넣는다 —
    /// 그냥 버리면 그 NPC 는 다음 인지 스캔까지(LOD 2 면 100틱) 이탈 상태로 방치된다.
    /// </summary>
    /// <param name="npc">NPC 첨자.</param>
    /// <param name="current">지금 플래그.</param>
    /// <param name="snapshot">찍혀 있던 스냅샷.</param>
    /// <returns>받아들여도 되면 true.</returns>
    public bool TryAccept(int npc, WorldFlags current, out ReplanRequestSnapshot snapshot)
    {
        snapshot = Of(npc);

        if (snapshot.Exists && Drift(snapshot.Flags, current) > MaxDrift)
        {
            Interlocked.Increment(ref _discarded);
            Clear(npc);
            return false;
        }

        Interlocked.Increment(ref _accepted);
        Clear(npc);
        return true;
    }

    /// <summary>
    /// 낡은 요청을 지금 상태로 다시 넣는다. 점수는 스냅샷의 것을 이어받는다 —
    /// 새로 계산하려면 <c>ReplanScorer</c> 가 필요하고 그것은 워커의 몫이 아니다.
    /// </summary>
    /// <returns>큐에 들어갔으면 true. 포화라 거절됐으면 false.</returns>
    public bool Requeue(int npc, WorldFlags current, Tick now, float score, ReplanQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);

        Capture(npc, current, now, score);

        return queue.TryEnqueue(npc, score);
    }

    /// <summary>스냅샷을 지운다. 요청이 소진됐다는 뜻이다.</summary>
    public void Clear(int npc)
    {
        if ((uint)npc < (uint)_tick.Length)
        {
            Volatile.Write(ref _tick[npc], -1);
        }
    }

    /// <summary>전부 지운다. 측정 구간을 가를 때만 쓴다.</summary>
    public void ClearAll() => Array.Fill(_tick, -1);
}
