using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Npc.Contracts;

namespace Npc.Gateway;

/// <summary>기록 줄의 종류.</summary>
public enum RecordKind
{
    /// <summary>NPC 서버 → 게임서버 명령.</summary>
    Command,

    /// <summary>게임서버 → NPC 서버 이벤트.</summary>
    Event,
}

/// <summary>기록 파일의 한 줄. docs/15 §3.</summary>
/// <param name="Kind">명령인가 이벤트인가.</param>
/// <param name="Command">명령. Kind 가 Event 면 null.</param>
/// <param name="Event">이벤트. Kind 가 Command 면 null.</param>
public sealed record LinkRecord(RecordKind Kind, NpcCommand? Command, GameEvent? Event);

/// <summary>기록 파일의 소스 생성 직렬화 컨텍스트. 런타임 리플렉션 0.</summary>
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LinkRecord))]
public sealed partial class LinkRecordJsonContext : JsonSerializerContext;

/// <summary>
/// 기록 데코레이터. docs/02 §2 · docs/15 §3.
///
/// 안쪽 링크를 그대로 통과시키면서 명령과 이벤트를 jsonl 로 남긴다.
/// <b>시각은 <see cref="Tick"/> 만 기록한다</b> — 패킷에 <c>DateTime</c> 이 없으므로(N4)
/// 기록 파일에도 벽시계가 들어갈 자리가 없다. 그래서 리플레이가 100% 일치한다.
/// </summary>
public sealed class RecordingGameServerLink : IGameServerLink
{
    private readonly IGameServerLink _inner;
    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly LoggingReader _events;

    /// <summary>파일에 기록한다.</summary>
    public RecordingGameServerLink(IGameServerLink inner, string path)
        : this(inner, new StreamWriter(path, append: false), ownsWriter: true)
    {
    }

    /// <summary>임의의 writer 에 기록한다. 테스트가 쓴다.</summary>
    public RecordingGameServerLink(IGameServerLink inner, TextWriter writer, bool ownsWriter = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(writer);

        _inner = inner;
        _writer = writer;
        _ownsWriter = ownsWriter;
        _events = new LoggingReader(inner.Events, this);
    }

    /// <summary>기록한 명령 수.</summary>
    public long CommandsRecorded { get; private set; }

    /// <summary>기록한 이벤트 수.</summary>
    public long EventsRecorded { get; private set; }

    /// <summary>읽을 때 기록하는 이벤트 스트림.</summary>
    public ChannelReader<GameEvent> Events => _events;

    /// <inheritdoc />
    public LinkState State => _inner.State;

    /// <inheritdoc />
    public LinkStats Stats => _inner.Stats;

    /// <inheritdoc />
    public event Action<LinkState>? StateChanged
    {
        add => _inner.StateChanged += value;
        remove => _inner.StateChanged -= value;
    }

    /// <summary>명령을 기록하고 안쪽으로 넘긴다.</summary>
    public void Enqueue(in NpcCommand command)
    {
        Write(new LinkRecord(RecordKind.Command, command, null));
        CommandsRecorded++;
        _inner.Enqueue(in command);
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken ct)
    {
        _writer.Flush();
        return _inner.FlushAsync(ct);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _writer.Flush();

        if (_ownsWriter)
        {
            _writer.Dispose();
        }

        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    private void Write(LinkRecord record) =>
        _writer.WriteLine(JsonSerializer.Serialize(record, LinkRecordJsonContext.Default.LinkRecord));

    private void RecordEvent(in GameEvent ev)
    {
        Write(new LinkRecord(RecordKind.Event, null, ev));
        EventsRecorded++;
    }

    /// <summary>읽히는 이벤트를 기록하는 리더. 별도 스레드를 두지 않아 순서가 결정론적이다.</summary>
    private sealed class LoggingReader(ChannelReader<GameEvent> inner, RecordingGameServerLink owner)
        : ChannelReader<GameEvent>
    {
        public override bool TryRead(out GameEvent item)
        {
            if (!inner.TryRead(out item))
            {
                return false;
            }

            owner.RecordEvent(in item);
            return true;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken ct = default) =>
            inner.WaitToReadAsync(ct);

        public override bool CanCount => inner.CanCount;

        public override int Count => inner.Count;

        public override Task Completion => inner.Completion;
    }
}
