using System.Text;
using System.Threading.Channels;
using Npc.Contracts;
using Npc.Gateway;

namespace Npc.Tests.Gateway;

/// <summary>docs/02 §2 · docs/15 §3. 기록 → 재생이 같은 이벤트 열을 낸다.</summary>
public sealed class RecordReplayTests
{
    private static NpcCommand Command(int npc, uint correlation) => new()
    {
        Kind = NpcCommandKind.MoveTo,
        Npc = new NpcId(npc),
        IssuedAt = new Tick(correlation * 10),
        Correlation = new CorrelationId(correlation),
        Priority = CommandPriority.Normal,
        TargetPoi = new PoiId((ushort)(npc + 1)),
        Flags = (byte)MoveSpeed.Run,
    };

    private static GameEvent Event(long sequence, int npc) => new()
    {
        Kind = GameEventKind.NpcArrived,
        Sequence = sequence,
        OccurredAt = new Tick(sequence * 10),
        Npc = new NpcId(npc),
        Correlation = new CorrelationId((uint)sequence),
        Poi = new PoiId((ushort)(npc + 1)),
        Pos = new WorldPos(1.5f, 0f, -2.5f),
    };

    private sealed record Recorded(string Text, List<NpcCommand> Commands, List<GameEvent> Events);

    private static async Task<Recorded> RecordSessionAsync()
    {
        var events = Channel.CreateUnbounded<GameEvent>();
        var dispatched = new List<NpcCommand>();
        var buffer = new StringWriter();

        await using var inner = new LoopbackGameServerLink(
            (in NpcCommand c, Tick t) => dispatched.Add(c),
            events.Reader);

        await using var link = new RecordingGameServerLink(inner, buffer);

        var seen = new List<GameEvent>();

        for (int i = 1; i <= 5; i++)
        {
            NpcCommand command = Command(i, (uint)i);
            link.Enqueue(in command);
            events.Writer.TryWrite(Event(i, i));
        }

        await link.FlushAsync(CancellationToken.None);

        while (link.Events.TryRead(out GameEvent ev))
        {
            seen.Add(ev);
        }

        Assert.Equal(5, link.CommandsRecorded);
        Assert.Equal(5, link.EventsRecorded);

        return new Recorded(buffer.ToString(), dispatched, seen);
    }

    /// <summary>T1-43 완료 조건 — 기록 파일에 DateTime 문자열 0 · 명령 수 == Stats.CommandsFlushed.</summary>
    [Fact]
    public async Task Recording_HasNoWallClockAndMatchesFlushCount()
    {
        Recorded recorded = await RecordSessionAsync();

        // N4 — 패킷에 DateTime 이 없으므로 기록에도 벽시계가 들어갈 자리가 없다.
        foreach (string token in new[] { "DateTime", "DateTimeOffset", "Timestamp", "TimeSpan" })
        {
            Assert.DoesNotContain(token, recorded.Text, StringComparison.Ordinal);
        }

        // ISO-8601 시각 문자열도 없어야 한다.
        Assert.DoesNotMatch(
            new System.Text.RegularExpressions.Regex(@"\d{4}-\d{2}-\d{2}T\d{2}:"),
            recorded.Text);

        string[] lines = recorded.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(10, lines.Length);

        Assert.Equal(5, recorded.Commands.Count);
        Assert.Equal(5, recorded.Events.Count);
    }

    /// <summary>T1-44 완료 조건 — 재생 시 이벤트 시퀀스가 원본과 같다.</summary>
    [Fact]
    public async Task Replay_ReproducesEventSequence()
    {
        Recorded recorded = await RecordSessionAsync();

        await using ReplayGameServerLink replay = ReplayGameServerLink.Parse(
            recorded.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var replayed = new List<GameEvent>();

        while (replay.Events.TryRead(out GameEvent ev))
        {
            replayed.Add(ev);
        }

        Assert.Equal(recorded.Events.Count, replayed.Count);

        for (int i = 0; i < replayed.Count; i++)
        {
            Assert.Equal(recorded.Events[i].Kind, replayed[i].Kind);
            Assert.Equal(recorded.Events[i].Sequence, replayed[i].Sequence);
            Assert.Equal(recorded.Events[i].OccurredAt, replayed[i].OccurredAt);
            Assert.Equal(recorded.Events[i].Npc, replayed[i].Npc);
            Assert.Equal(recorded.Events[i].Correlation, replayed[i].Correlation);
            Assert.Equal(recorded.Events[i].Poi, replayed[i].Poi);
            Assert.Equal(recorded.Events[i].Pos, replayed[i].Pos);
        }

        // 기록된 명령도 그대로 읽힌다 — 이번 실행과 대조할 수 있다.
        Assert.Equal(recorded.Commands.Count, replay.RecordedCommands.Count);
        Assert.Equal(recorded.Commands[0].TargetPoi, replay.RecordedCommands[0].TargetPoi);
        Assert.Equal(recorded.Commands[0].Flags, replay.RecordedCommands[0].Flags);
    }

    /// <summary>재생 링크는 명령을 게임서버로 보내지 않고 모으기만 한다.</summary>
    [Fact]
    public async Task Replay_CollectsCommandsWithoutSending()
    {
        await using ReplayGameServerLink replay = ReplayGameServerLink.Parse([]);

        NpcCommand command = Command(1, 1);
        replay.Enqueue(in command);

        await replay.FlushAsync(CancellationToken.None);

        Assert.Single(replay.Commands);
        Assert.Equal(1, replay.Stats.CommandsFlushed);
    }

    /// <summary>docs/02 §6 Link_Swappable — 같은 시나리오를 세 링크로 돌려도 NPC 서버 코드가 안 바뀐다.</summary>
    [Fact]
    public async Task Link_Swappable()
    {
        var events = Channel.CreateUnbounded<GameEvent>();
        var buffer = new StringWriter();

        IGameServerLink[] links =
        [
            new NullGameServerLink(),
            new LoopbackGameServerLink((in NpcCommand c, Tick t) => { }, events.Reader),
            new RecordingGameServerLink(new NullGameServerLink(), buffer),
            ReplayGameServerLink.Parse([]),
        ];

        foreach (IGameServerLink link in links)
        {
            // 호출부는 인터페이스만 안다.
            NpcCommand command = Command(1, 1);
            link.Enqueue(in command);
            await link.FlushAsync(CancellationToken.None);

            Assert.True(link.Stats.CommandsEnqueued >= 1);

            await link.DisposeAsync();
        }
    }

    [Fact]
    public async Task Recording_WritesToFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"npc-record-{Guid.NewGuid():N}.jsonl");

        try
        {
            await using (var link = new RecordingGameServerLink(new NullGameServerLink(), path))
            {
                NpcCommand command = Command(1, 1);
                link.Enqueue(in command);
                await link.FlushAsync(CancellationToken.None);
            }

            string text = await File.ReadAllTextAsync(path, Encoding.UTF8);

            Assert.Contains("\"Kind\":0", text, StringComparison.Ordinal);
            Assert.DoesNotContain("DateTime", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
