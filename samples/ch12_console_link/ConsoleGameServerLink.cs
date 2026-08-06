using System.Threading.Channels;
using Npc.Contracts;

namespace Npc.Gateway;

/// <summary>
/// 활용 실습서 12장 — 가장 작은 <see cref="IGameServerLink"/> 구현.
///
/// 하는 일은 셋뿐이다.
///   ① 받은 명령을 콘솔에 한 줄씩 찍는다
///   ② 그 명령에 대응하는 완료 이벤트를 <b>즉시 합성</b>해 돌려준다
///   ③ 안쪽 링크가 내는 이벤트(TickSync)를 그대로 흘려보낸다
///
/// 게임서버가 없어도 NPC 가 계속 진행한다. 이동에 걸리는 시간도, 실패도, 좌표도 없다 —
/// "명령을 보내면 곧바로 성공한다" 는 가장 단순한 세계다.
///
/// <b>데코레이터인 이유.</b> 세계 시간을 미는 것은 링크가 아니라 호스트의 펌프다.
/// 펌프는 <see cref="NullGameServerLink.PushTick"/> 으로 <c>TickSync</c> 를 넣는데,
/// 우리가 자기 채널만 들고 있으면 그 틱이 아무에게도 안 간다. 그래서 안쪽을 감싸고
/// 안쪽 이벤트를 우리 채널로 옮긴다 — <c>RecordingGameServerLink</c> 와 같은 모양이다.
///
/// N1: 반환값 있는 전송 메서드를 만들지 않는다. 결과는 <see cref="Events"/> 로만 돌아온다.
/// N6: 모든 이벤트에 순증 <c>Sequence</c> 를 붙인다. 합성분과 전달분이 <b>한 줄기</b>여야 한다.
/// N8: 배치가 기본이다. <see cref="Enqueue"/> 로 쌓고 <see cref="FlushAsync"/> 로 내보낸다.
/// </summary>
public sealed class ConsoleGameServerLink : IGameServerLink
{
    private readonly Channel<GameEvent> _events =
        Channel.CreateUnbounded<GameEvent>(new UnboundedChannelOptions { SingleReader = true });

    // 배치 큐. FlushAsync 전까지 여기 쌓인다 (N8).
    private readonly List<NpcCommand> _pending = new(256);

    private readonly NullGameServerLink _inner;
    private readonly TextWriter _out;
    private readonly bool _quiet;
    private readonly Task _forward;

    private long _sequence;
    private long _enqueued;
    private long _flushed;

    /// <param name="inner">틱을 밀어 줄 안쪽 링크. 호스트가 <c>PushTick</c> 을 부른다.</param>
    /// <param name="output">명령을 찍을 곳. null 이면 <see cref="Console.Out"/>.</param>
    /// <param name="quiet">true 면 찍지 않고 세기만 한다. 부하 회차용.</param>
    public ConsoleGameServerLink(NullGameServerLink inner, TextWriter? output = null, bool quiet = false)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _out = output ?? Console.Out;
        _quiet = quiet;

        // 안쪽 이벤트를 우리 채널로 옮긴다. 시퀀스는 여기서 다시 매긴다 —
        // 합성분과 섞이므로 한 줄기여야 갭 검출(N6)이 성립한다.
        _forward = Task.Run(ForwardAsync);
    }

    /// <inheritdoc />
    public ChannelReader<GameEvent> Events => _events.Reader;

    /// <inheritdoc />
    public LinkState State => LinkState.Connected;

    /// <inheritdoc />
    public LinkStats Stats => new(
        CommandsEnqueued: Interlocked.Read(ref _enqueued),
        CommandsFlushed: Interlocked.Read(ref _flushed),
        CommandsDropped: 0,
        EventsReceived: Interlocked.Read(ref _sequence),
        EventGapsDetected: 0,
        PendingCommands: _pending.Count);

    /// <inheritdoc />
    public event Action<LinkState>? StateChanged;

    /// <inheritdoc />
    public void Enqueue(in NpcCommand command)
    {
        // 반환값이 없다 (N1). 이 링크는 실패하지 않지만, 실패해도 알릴 방법이 없다 —
        // 그것이 fire-and-forget 의 뜻이다. 드롭은 Stats 로만 관측한다.
        _pending.Add(command);
        _enqueued++;
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken ct)
    {
        foreach (NpcCommand command in _pending)
        {
            if (!_quiet)
            {
                _out.WriteLine(Format(command));
            }

            Respond(command);
            _flushed++;
        }

        _pending.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 명령 하나에 대한 응답 이벤트를 만든다.
    ///
    /// 진짜 게임서버라면 이동에 시간이 걸리고 실패도 한다. 여기서는 전부 즉시 성공이다 —
    /// <b>그래서 이 링크로 돌리면 스텝이 매우 빠르게 넘어간다.</b>
    /// </summary>
    private void Respond(in NpcCommand command)
    {
        GameEventKind kind = command.Kind switch
        {
            // 이동은 "도착" 으로 답한다. 플랜 실행기가 이걸 기다린다.
            NpcCommandKind.MoveTo => GameEventKind.NpcArrived,
            NpcCommandKind.Spawn => GameEventKind.NpcSpawned,
            NpcCommandKind.Despawn => GameEventKind.NpcDespawned,

            // 나머지는 전부 "완료".
            _ => GameEventKind.NpcActionCompleted,
        };

        _events.Writer.TryWrite(new GameEvent
        {
            Kind = kind,
            Sequence = Interlocked.Increment(ref _sequence),   // N6
            OccurredAt = command.IssuedAt,
            Npc = command.Npc,
            Correlation = command.Correlation,                 // N5 — 어느 명령의 답인가
            Poi = command.TargetPoi,
            Pos = command.TargetPos,
        });
    }

    /// <summary>안쪽 링크의 이벤트를 우리 채널로 옮긴다. 시퀀스만 다시 매긴다.</summary>
    private async Task ForwardAsync()
    {
        try
        {
            await foreach (GameEvent e in _inner.Events.ReadAllAsync().ConfigureAwait(false))
            {
                _events.Writer.TryWrite(e with { Sequence = Interlocked.Increment(ref _sequence) });
            }
        }
        catch (OperationCanceledException)
        {
            // 종료 경로다.
        }
        finally
        {
            _events.Writer.TryComplete();
        }
    }

    /// <summary>명령 한 줄. 패킷에 문자열이 없으므로(N3) 전부 숫자 id 로 나온다.</summary>
    private static string Format(in NpcCommand c)
    {
        string payload = c.Kind switch
        {
            NpcCommandKind.MoveTo => $"poi={c.TargetPoi.Value} speed={(MoveSpeed)c.Flags}",
            NpcCommandKind.Interact => $"poi={c.TargetPoi.Value} item={c.Item.Value} n={c.Amount}",
            NpcCommandKind.Speak => $"to={c.TargetNpc.Value} dialogue={c.Dialogue.Value}",
            NpcCommandKind.PlayAnimation => $"anim={c.Animation.Value} n={c.Amount}",
            NpcCommandKind.SetVisualState => $"visual={c.Visual}",
            NpcCommandKind.InventoryChange => $"item={c.Item.Value} delta={c.Amount}",
            NpcCommandKind.CombatAction => $"kind={(CombatActionKind)c.Flags} target={c.TargetNpc.Value}",
            NpcCommandKind.Spawn => $"archetype={c.Archetype.Value} zone={c.Zone.Value}",
            _ => string.Empty,
        };

        return $"[t={c.IssuedAt.Value,6}] npc {c.Npc.Value,5} {c.Kind,-16} {payload}  "
            + $"(corr {c.Correlation.Value}, {c.Priority})";
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _forward.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 종료 중이다.
        }

        _events.Writer.TryComplete();
        StateChanged?.Invoke(LinkState.Disconnected);
    }
}
