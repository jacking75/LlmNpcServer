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

/// <summary>
/// 받은 명령을 곧바로 완료로 되돌려주는 링크. 건강한 게임서버 대역이다.
///
/// <b>응답하지 않는 링크로는 플랜 전환을 관측할 수 없다.</b> 스텝이 <c>timeout_s</c> 만에
/// 합성 실패로 끝나고 <c>on_step_fail: fallback</c> 이 아키타입 폴백으로 되돌리기 때문에,
/// 버킷 플랜이 걸려도 몇 틱 뒤에는 다시 폴백이 되어 있다 (T4-23 에서 실제로 겪었다).
///
/// <see cref="Drain"/> 를 호출한 시점에 쌓인 명령을 <c>NpcActionCompleted</c> 로 바꿔 되돌린다 —
/// 상관 ID 를 그대로 실어 주므로 <c>CorrelationTable</c> 판정도 통과한다 (docs/02 §3.4).
/// </summary>
internal sealed class CompletingLink : IGameServerLink
{
    private readonly Channel<GameEvent> _events = Channel.CreateUnbounded<GameEvent>();
    private readonly List<NpcCommand> _pending = [];
    private long _enqueued;
    private long _flushed;
    private long _sequence;

    /// <summary>발행된 명령. 순서대로.</summary>
    public List<NpcCommand> Commands { get; } = [];

    /// <inheritdoc />
    public ChannelReader<GameEvent> Events => _events.Reader;

    /// <inheritdoc />
    public LinkState State => LinkState.Connected;

    /// <inheritdoc />
    public LinkStats Stats => new(_enqueued, _flushed, 0, 0, 0, _pending.Count);

    /// <inheritdoc />
    public event Action<LinkState>? StateChanged;

    /// <inheritdoc />
    public void Enqueue(in NpcCommand command)
    {
        Commands.Add(command);
        _pending.Add(command);
        _enqueued++;
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken ct)
    {
        _flushed = _enqueued;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 쌓인 명령을 완료 이벤트로 되돌린다. 틱 루프의 배수 구간 직전에 부른다.
    /// </summary>
    /// <returns>되돌린 명령 수.</returns>
    public int Drain(Tick now)
    {
        int drained = _pending.Count;

        foreach (NpcCommand command in _pending)
        {
            _events.Writer.TryWrite(new GameEvent
            {
                Kind = GameEventKind.NpcActionCompleted,
                Sequence = Interlocked.Increment(ref _sequence),
                OccurredAt = now,
                Npc = command.Npc,
                Correlation = command.Correlation,
            });
        }

        _pending.Clear();
        return drained;
    }

    /// <summary>
    /// 다음 이벤트 시퀀스. <b>주입 이벤트도 이 번호를 써야 한다</b> —
    /// <c>EventApplier</c> 는 시퀀스가 되돌아가면 재전송으로 보고 버린다 (N7).
    /// </summary>
    public long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>상태 전이를 흉내낸다.</summary>
    public void RaiseStateChanged(LinkState state) => StateChanged?.Invoke(state);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
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
