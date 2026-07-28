using Npc.Contracts;

namespace Npc.Gateway;

/// <summary>
/// 우선순위별 명령 링과 역압 정책. docs/02 §1 · docs/20 §5.7.
///
/// <para>
/// <b>정책을 복사하지 않고 추출한 것이다</b> (T6-05). <see cref="LoopbackGameServerLink"/> 와
/// <see cref="TcpGameServerLink"/> 가 같은 인스턴스 타입을 쓴다 — 두 벌이 되면 반드시 갈라지고,
/// <c>Link_Backpressure</c> 는 한쪽만 보므로 갈라진 것을 아무도 모른다.
/// </para>
///
/// <para>
/// 규칙은 셋이다 (docs/02 §1).
/// <list type="bullet">
///   <item>큐가 차면 <b>자기보다 낮은 우선순위</b>(<c>Cosmetic</c> → <c>Normal</c>)부터 밀어낸다</item>
///   <item><see cref="CommandPriority.Critical"/> 은 <b>무손실</b>이다</item>
///   <item>드롭은 <see cref="Dropped"/> 에만 나타난다. <b>예외를 던지지 않는다</b></item>
/// </list>
/// </para>
///
/// <para>
/// <b>할당이 0 이다.</b> 기동 시 우선순위마다 링을 하나씩 잡고 그 뒤로는 첨자 연산만 한다
/// (CLAUDE.md §2.1 — 틱 루프에서 <see cref="Enqueue"/> 가 불린다).
/// </para>
///
/// <para>
/// <b>스레드 안전하지 않다.</b> 틱 루프 한 스레드가 <see cref="Enqueue"/> 하고
/// 같은 스레드가 꺼낸다. TCP 링크에서 센더 태스크가 소비하는 경로는 T6-07 이 별도로 배선한다.
/// </para>
/// </summary>
public sealed class PriorityCommandRing
{
    /// <summary>기본 용량 (명령 수).</summary>
    public const int DefaultCapacity = 4_096;

    /// <summary><see cref="CommandPriority"/> 의 값 수.</summary>
    public const int PriorityCount = 3;

    private readonly NpcCommand[][] _rings;
    private readonly int[] _head;
    private readonly int[] _tail;
    private readonly int[] _count;
    private readonly int _capacity;

    private long _dropped;

    /// <summary>링을 만든다. 기동 시 1회.</summary>
    /// <param name="capacity">
    /// 담을 수 있는 총 명령 수. <b>우선순위마다 이 크기의 링을 잡는다</b> —
    /// 그래야 <c>Critical</c> 이 무손실일 수 있다 (다른 우선순위가 가득해도 자리가 남는다).
    /// </param>
    public PriorityCommandRing(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
        _rings = new NpcCommand[PriorityCount][];

        for (int p = 0; p < PriorityCount; p++)
        {
            _rings[p] = new NpcCommand[capacity];
        }

        _head = new int[PriorityCount];
        _tail = new int[PriorityCount];
        _count = new int[PriorityCount];
    }

    /// <summary>담을 수 있는 총 명령 수.</summary>
    public int Capacity => _capacity;

    /// <summary>지금 들어 있는 명령 수.</summary>
    public int Pending => _count[0] + _count[1] + _count[2];

    /// <summary>역압으로 버린 누계. <c>LinkStats.CommandsDropped</c> 가 이 값이다.</summary>
    public long Dropped => _dropped;

    /// <summary>
    /// 명령을 넣는다. <b>할당 0.</b>
    ///
    /// 포화되면 자기보다 낮은 우선순위를 밀어내고, 밀어낼 것이 없으면
    /// <see cref="CommandPriority.Critical"/> 이 아닌 한 자기 자신을 버린다.
    /// </summary>
    public void Enqueue(in NpcCommand command)
    {
        int priority = (int)command.Priority;

        if ((uint)priority >= PriorityCount)
        {
            priority = (int)CommandPriority.Normal;
        }

        if (Pending < _capacity)
        {
            Push(priority, in command);
            return;
        }

        // 포화. 자기보다 낮은 우선순위(=큰 값)부터 밀어낸다.
        for (int lower = PriorityCount - 1; lower > priority; lower--)
        {
            if (_count[lower] > 0)
            {
                Pop(lower);
                _dropped++;
                Push(priority, in command);
                return;
            }
        }

        // Critical 은 무손실이다. 링이 용량만큼 있으므로 자리는 있다.
        if (priority == (int)CommandPriority.Critical && _count[priority] < _capacity)
        {
            Push(priority, in command);
            return;
        }

        _dropped++;
    }

    /// <summary>
    /// 가장 급한 명령 하나를 꺼낸다. 비어 있으면 false.
    /// <b>우선순위 순</b>이다 — <c>Critical</c> 이 전부 나간 뒤에 <c>Normal</c> 이 나간다.
    /// </summary>
    public bool TryDequeue(out NpcCommand command)
    {
        for (int priority = 0; priority < PriorityCount; priority++)
        {
            if (_count[priority] > 0)
            {
                command = Pop(priority);
                return true;
            }
        }

        command = default;
        return false;
    }

    /// <summary>전부 버린다. 재접속 시 링에 남은 명령을 버리는 경로가 쓴다 (docs/20 §6.3).</summary>
    /// <param name="countAsDropped">버린 것을 <see cref="Dropped"/> 에 더할지.</param>
    /// <returns>버린 명령 수.</returns>
    public int Clear(bool countAsDropped)
    {
        int discarded = Pending;

        Array.Clear(_count);
        Array.Clear(_head);
        Array.Clear(_tail);

        if (countAsDropped)
        {
            _dropped += discarded;
        }

        return discarded;
    }

    private void Push(int priority, in NpcCommand command)
    {
        _rings[priority][_tail[priority]] = command;
        _tail[priority] = _tail[priority] + 1 >= _capacity ? 0 : _tail[priority] + 1;
        _count[priority]++;
    }

    private NpcCommand Pop(int priority)
    {
        NpcCommand command = _rings[priority][_head[priority]];
        _head[priority] = _head[priority] + 1 >= _capacity ? 0 : _head[priority] + 1;
        _count[priority]--;
        return command;
    }
}
