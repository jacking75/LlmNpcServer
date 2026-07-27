using System.Threading.Channels;
using Npc.Contracts;

namespace Npc.Gateway;

/// <summary>
/// 명령을 버리고 이벤트를 주지 않는 링크. docs/02 §2.
///
/// 쓰임새는 둘이다.
/// <list type="bullet">
///   <item>단위 테스트 — 게임서버 없이 런타임만 돌린다.</item>
///   <item>성능 측정 기준선 — 링크 비용을 0 으로 두고 틱 루프만 잰다.</item>
/// </list>
///
/// 게이트 체크리스트의 "IGameServerLink 를 Null 로 바꿔도 코드 변경 없이 기동한다"가 이것이다.
/// </summary>
public sealed class NullGameServerLink : IGameServerLink
{
    private readonly Channel<GameEvent> _events = Channel.CreateUnbounded<GameEvent>();
    private long _enqueued;
    private long _flushed;

    /// <summary>
    /// 이벤트 시퀀스. <b>명령 카운터와 따로 센다</b> (N6).
    /// 하나로 쓰면 두 <c>TickSync</c> 사이에 나간 명령 수만큼 시퀀스가 건너뛰고,
    /// 틱 루프의 갭 검출이 매 틱 경보를 올린다 (실측 1,440틱에 445건).
    /// </summary>
    private long _sequence;

    /// <summary>이벤트는 오지 않는다. 채널은 열려 있지만 아무도 쓰지 않는다.</summary>
    public ChannelReader<GameEvent> Events => _events.Reader;

    /// <summary>항상 연결된 것으로 본다.</summary>
    public LinkState State => LinkState.Connected;

    /// <summary>통계. 명령은 세기만 하고 버린다.</summary>
    public LinkStats Stats => new(
        CommandsEnqueued: Interlocked.Read(ref _enqueued),
        CommandsFlushed: Interlocked.Read(ref _flushed),
        CommandsDropped: 0,
        EventsReceived: Interlocked.Read(ref _sequence),
        EventGapsDetected: 0,
        PendingCommands: (int)(Interlocked.Read(ref _enqueued) - Interlocked.Read(ref _flushed)));

    /// <summary>상태가 바뀌지 않으므로 발생하지 않는다.</summary>
    public event Action<LinkState>? StateChanged;

    /// <summary>명령을 버린다. 카운터만 올린다.</summary>
    public void Enqueue(in NpcCommand command) => Interlocked.Increment(ref _enqueued);

    /// <summary>보낼 것이 없다. 카운터만 맞춘다.</summary>
    public ValueTask FlushAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _flushed, Interlocked.Read(ref _enqueued));
        return ValueTask.CompletedTask;
    }

    /// <summary>테스트가 틱을 굴릴 수 있도록 TickSync 를 넣어준다.</summary>
    public void PushTick(Tick tick) => _events.Writer.TryWrite(new GameEvent
    {
        Kind = GameEventKind.TickSync,
        Sequence = Interlocked.Increment(ref _sequence),
        OccurredAt = tick,
    });

    /// <summary>상태 전이 통보. 구독자가 없으면 아무 일도 없다.</summary>
    public void RaiseStateChanged(LinkState state) => StateChanged?.Invoke(state);

    /// <summary>채널을 닫는다.</summary>
    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
