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
    public const int DefaultCapacity = PriorityCommandRing.DefaultCapacity;

    private readonly CommandSink _sink;
    private readonly ChannelReader<GameEvent> _events;

    /// <summary>
    /// 우선순위 링과 역압 정책. <b>TCP 링크와 같은 타입을 쓴다</b> (T6-05 · docs/20 §5.7) —
    /// 정책을 복사하면 반드시 갈라진다.
    /// </summary>
    private readonly PriorityCommandRing _ring;

    private long _enqueued;
    private long _flushed;

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
        _ring = new PriorityCommandRing(capacity);
    }

    /// <summary>게임서버 → NPC 서버.</summary>
    public ChannelReader<GameEvent> Events => _events;

    /// <summary>인프로세스라 항상 연결이다.</summary>
    public LinkState State => LinkState.Connected;

    /// <summary>통계.</summary>
    public LinkStats Stats => new(_enqueued, _flushed, _ring.Dropped, 0, 0, Pending);

    /// <summary>큐에 남아 있는 명령 수.</summary>
    public int Pending => _ring.Pending;

    /// <summary>큐 용량.</summary>
    public int Capacity => _ring.Capacity;

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
        _ring.Enqueue(in command);
    }

    /// <summary>
    /// 쌓인 명령을 우선순위 순으로 내보낸다. 틱 루프에서 유일하게 허용되는 await 대상이다.
    /// 인프로세스라 실제로는 동기 완료다.
    /// </summary>
    public ValueTask FlushAsync(CancellationToken ct)
    {
        while (_ring.TryDequeue(out NpcCommand command))
        {
            ct.ThrowIfCancellationRequested();

            _sink(in command, command.IssuedAt);
            _flushed++;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>상태 전이 통보. 인프로세스라 테스트에서만 쓴다.</summary>
    public void RaiseStateChanged(LinkState state) => StateChanged?.Invoke(state);

    /// <summary>큐를 비운다.</summary>
    public ValueTask DisposeAsync()
    {
        // 버린 것을 드롭에 세지 않는다 — 종료지 역압이 아니다.
        _ring.Clear(countAsDropped: false);

        return ValueTask.CompletedTask;
    }
}
