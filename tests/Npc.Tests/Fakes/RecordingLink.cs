using System.Threading.Channels;
using Npc.Contracts;

namespace Npc.Tests.Fakes;

/// <summary>테스트용 링크. 발행된 명령을 모아두고 이벤트를 밀어넣을 수 있다.</summary>
internal sealed class RecordingLink : IGameServerLink
{
    private readonly Channel<GameEvent> _events = Channel.CreateUnbounded<GameEvent>();
    private long _enqueued;
    private long _flushed;

    /// <summary>발행된 명령. 순서대로.</summary>
    public List<NpcCommand> Commands { get; } = [];

    /// <summary>플러시 호출 횟수.</summary>
    public int Flushes { get; private set; }

    public ChannelReader<GameEvent> Events => _events.Reader;

    public LinkState State => LinkState.Connected;

    public LinkStats Stats => new(_enqueued, _flushed, 0, 0, 0, Commands.Count - (int)_flushed);

    public event Action<LinkState>? StateChanged;

    public void Enqueue(in NpcCommand command)
    {
        Commands.Add(command);
        _enqueued++;
    }

    public ValueTask FlushAsync(CancellationToken ct)
    {
        Flushes++;
        _flushed = _enqueued;
        return ValueTask.CompletedTask;
    }

    /// <summary>이벤트를 밀어넣는다.</summary>
    public void Push(in GameEvent ev) => _events.Writer.TryWrite(ev);

    /// <summary>이벤트 채널을 닫는다. 틱 루프가 끝나게 한다.</summary>
    public void Complete() => _events.Writer.TryComplete();

    /// <summary>상태 전이를 흉내낸다. StateChanged 구독자를 확인하는 테스트용.</summary>
    public void RaiseStateChanged(LinkState state) => StateChanged?.Invoke(state);

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>명령을 세기만 하는 링크. 할당 측정에 쓴다 — List 증가가 측정을 오염시키지 않는다.</summary>
internal sealed class NullSinkLink : IGameServerLink
{
    private readonly Channel<GameEvent> _events = Channel.CreateUnbounded<GameEvent>();

    public long Count { get; private set; }

    public ChannelReader<GameEvent> Events => _events.Reader;

    public LinkState State => LinkState.Connected;

    public LinkStats Stats => new(Count, Count, 0, 0, 0, 0);

    public event Action<LinkState>? StateChanged;

    public void Enqueue(in NpcCommand command) => Count++;

    public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>상태 전이를 흉내낸다.</summary>
    public void RaiseStateChanged(LinkState state) => StateChanged?.Invoke(state);

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
