using System.Threading.Channels;
using Npc.Contracts;

namespace Npc.Gateway;

/// <summary>
/// 명령을 받아 처리하는 쪽. <c>Npc.Sim</c> 의 <c>SimWorld.ApplyCommand</c> 가 여기 들어간다.
///
/// 인터페이스가 아니라 델리게이트인 이유는 의존 방향 때문이다 —
/// <c>Npc.Gateway</c> 는 <c>Npc.Contracts</c> 만 참조하므로 <c>SimWorld</c> 를 알 수 없다 (CLAUDE.md §3).
/// 조립은 <c>Npc.Host</c> 가 한다.
/// </summary>
public delegate void CommandSink(in NpcCommand command, Tick now);

/// <summary>
/// 인프로세스 루프백 링크. docs/02 §2, §5.
///
/// <b>역압(backpressure)이 이 클래스의 존재 이유다.</b> 5,000 NPC × 초당 명령을 그대로 흘리면
/// 실제 네트워크에서 죽는다. 큐가 포화되면 <see cref="CommandPriority.Cosmetic"/> 부터 버리고
/// <see cref="CommandPriority.Critical"/> 은 무손실로 지킨다 (docs/02 §1).
///
/// 우선순위별 링 버퍼를 사전 할당하므로 <see cref="Enqueue"/> 는 할당이 0 이다.
/// </summary>
public sealed class LoopbackGameServerLink : IGameServerLink
{
    /// <summary>기본 큐 용량 (명령 수).</summary>
    public const int DefaultCapacity = 4_096;

    private const int PriorityCount = 3;

    private readonly CommandSink _sink;
    private readonly ChannelReader<GameEvent> _events;
    private readonly NpcCommand[][] _rings;
    private readonly int[] _head;
    private readonly int[] _tail;
    private readonly int[] _count;
    private readonly int _capacity;

    private long _enqueued;
    private long _flushed;
    private long _dropped;

    /// <summary>링크를 만든다. 기동 시 1회.</summary>
    /// <param name="sink">명령을 받을 쪽 (SimWorld.ApplyCommand).</param>
    /// <param name="events">그쪽이 내는 이벤트 스트림.</param>
    /// <param name="capacity">큐에 담을 수 있는 총 명령 수.</param>
    public LoopbackGameServerLink(CommandSink sink, ChannelReader<GameEvent> events, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _sink = sink;
        _events = events;
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

    /// <summary>게임서버 → NPC 서버.</summary>
    public ChannelReader<GameEvent> Events => _events;

    /// <summary>인프로세스라 항상 연결이다.</summary>
    public LinkState State => LinkState.Connected;

    /// <summary>통계.</summary>
    public LinkStats Stats => new(_enqueued, _flushed, _dropped, 0, 0, Pending);

    /// <summary>큐에 남아 있는 명령 수.</summary>
    public int Pending => _count[0] + _count[1] + _count[2];

    /// <summary>큐 용량.</summary>
    public int Capacity => _capacity;

    /// <summary>인프로세스라 상태가 바뀌지 않는다.</summary>
    public event Action<LinkState>? StateChanged;

    /// <summary>
    /// 명령을 큐에 넣는다. 할당 0.
    /// 포화되면 자기보다 낮은 우선순위를 밀어내고, 밀어낼 것이 없으면
    /// Critical 이 아닌 한 자기 자신을 버린다.
    /// </summary>
    public void Enqueue(in NpcCommand command)
    {
        _enqueued++;

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
    /// 쌓인 명령을 우선순위 순으로 내보낸다. 틱 루프에서 유일하게 허용되는 await 대상이다.
    /// 인프로세스라 실제로는 동기 완료다.
    /// </summary>
    public ValueTask FlushAsync(CancellationToken ct)
    {
        for (int priority = 0; priority < PriorityCount; priority++)
        {
            while (_count[priority] > 0)
            {
                ct.ThrowIfCancellationRequested();

                NpcCommand command = Pop(priority);
                _sink(in command, command.IssuedAt);
                _flushed++;
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>상태 전이 통보. 인프로세스라 테스트에서만 쓴다.</summary>
    public void RaiseStateChanged(LinkState state) => StateChanged?.Invoke(state);

    /// <summary>큐를 비운다.</summary>
    public ValueTask DisposeAsync()
    {
        Array.Clear(_count);
        return ValueTask.CompletedTask;
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
