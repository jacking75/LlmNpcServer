using System.Text.Json;
using System.Threading.Channels;
using Npc.Contracts;

namespace Npc.Gateway;

/// <summary>
/// 기록된 jsonl 을 이벤트 소스로 재생한다. docs/15 §3.
///
/// 명령은 <b>버린다</b> — 재생은 "그때 게임서버가 뭘 보냈는가"를 그대로 다시 흘리는 것이고,
/// NPC 서버가 이번에 무엇을 보내는지는 비교 대상이지 입력이 아니다.
/// 새로 발행된 명령은 <see cref="Commands"/> 에 모아 원본과 대조할 수 있게 한다.
///
/// 기록에 벽시계가 없으므로(N4) 몇 번을 돌려도 같은 이벤트 열이 나온다.
/// </summary>
public sealed class ReplayGameServerLink : IGameServerLink
{
    private readonly Channel<GameEvent> _events;
    private long _enqueued;
    private long _flushed;

    private ReplayGameServerLink(Channel<GameEvent> events, List<NpcCommand> recorded)
    {
        _events = events;
        RecordedCommands = recorded;
    }

    /// <summary>재생한 이벤트 수.</summary>
    public int EventCount { get; private init; }

    /// <summary>기록 파일에 들어 있던 명령. 이번 실행과 대조할 때 쓴다.</summary>
    public IReadOnlyList<NpcCommand> RecordedCommands { get; }

    /// <summary>이번 실행에서 NPC 서버가 낸 명령.</summary>
    public List<NpcCommand> Commands { get; } = [];

    /// <summary>기록된 이벤트 스트림.</summary>
    public ChannelReader<GameEvent> Events => _events.Reader;

    /// <summary>재생 중에는 연결된 것으로 본다.</summary>
    public LinkState State => LinkState.Connected;

    /// <inheritdoc />
    public LinkStats Stats => new(_enqueued, _flushed, 0, EventCount, 0, (int)(_enqueued - _flushed));

    /// <summary>재생 중에는 상태가 바뀌지 않는다.</summary>
    public event Action<LinkState>? StateChanged;

    /// <summary>기록 파일에서 만든다.</summary>
    public static ReplayGameServerLink Load(string path) => Parse(File.ReadLines(path));

    /// <summary>기록 줄들에서 만든다.</summary>
    public static ReplayGameServerLink Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        Channel<GameEvent> events = Channel.CreateUnbounded<GameEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        var commands = new List<NpcCommand>();
        int count = 0;

        foreach (string line in lines)
        {
            string text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            LinkRecord? record = JsonSerializer.Deserialize(text, LinkRecordJsonContext.Default.LinkRecord);

            if (record is null)
            {
                continue;
            }

            if (record.Kind == RecordKind.Event && record.Event is { } ev)
            {
                events.Writer.TryWrite(ev);
                count++;
            }
            else if (record.Kind == RecordKind.Command && record.Command is { } command)
            {
                commands.Add(command);
            }
        }

        events.Writer.TryComplete();

        return new ReplayGameServerLink(events, commands) { EventCount = count };
    }

    /// <summary>이번 실행의 명령을 모은다. 게임서버로 나가지는 않는다.</summary>
    public void Enqueue(in NpcCommand command)
    {
        Commands.Add(command);
        _enqueued++;
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken ct)
    {
        _flushed = _enqueued;
        return ValueTask.CompletedTask;
    }

    /// <summary>상태 전이 통보. 재생에서는 쓰이지 않는다.</summary>
    public void RaiseStateChanged(LinkState state) => StateChanged?.Invoke(state);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
