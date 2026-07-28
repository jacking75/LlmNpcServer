using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using MemoryPack;
using Npc.Contracts;
using Npc.Gateway;
using Npc.Wire;

namespace Npc.Tests.Gateway;

/// <summary>docs/02 §2. Null 링크와 Tcp 골격.</summary>
public sealed class NullAndTcpLinkTests
{
    private static NpcCommand Command(int npc) => new()
    {
        Kind = NpcCommandKind.MoveTo,
        Npc = new NpcId(npc),
        IssuedAt = new Tick(1),
        Correlation = new CorrelationId(1),
        Priority = CommandPriority.Normal,
    };

    /// <summary>T1-41 완료 조건 — 명령 폐기 · 이벤트 없음 · Stats.CommandsEnqueued 증가.</summary>
    [Fact]
    public async Task NullLink_DiscardsCommandsAndCountsThem()
    {
        await using var link = new NullGameServerLink();

        for (int i = 0; i < 100; i++)
        {
            NpcCommand command = Command(i);
            link.Enqueue(in command);
        }

        Assert.Equal(100, link.Stats.CommandsEnqueued);
        Assert.Equal(0, link.Stats.CommandsFlushed);
        Assert.Equal(100, link.Stats.PendingCommands);

        await link.FlushAsync(CancellationToken.None);

        Assert.Equal(100, link.Stats.CommandsFlushed);
        Assert.Equal(0, link.Stats.PendingCommands);
        Assert.Equal(0, link.Stats.CommandsDropped);

        // 이벤트는 오지 않는다.
        Assert.False(link.Events.TryRead(out _));
        Assert.Equal(LinkState.Connected, link.State);
    }

    [Fact]
    public async Task NullLink_EnqueueDoesNotAllocate()
    {
        await using var link = new NullGameServerLink();
        NpcCommand command = Command(1);

        for (int i = 0; i < 30_000; i++)
        {
            link.Enqueue(in command);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            link.Enqueue(in command);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task NullLink_StateChangedIsObservable()
    {
        await using var link = new NullGameServerLink();
        LinkState? seen = null;

        link.StateChanged += s => seen = s;
        link.RaiseStateChanged(LinkState.Degraded);

        Assert.Equal(LinkState.Degraded, seen);
    }

    /// <summary>두 링크 모두 IGameServerLink 계약을 만족한다 — 교체에 코드 변경이 없다.</summary>
    [Fact]
    public void Link_ImplementationsAreInterchangeable()
    {
        IGameServerLink[] links = [new NullGameServerLink(), new TcpGameServerLink(new TcpLinkOptions())];

        foreach (IGameServerLink link in links)
        {
            Assert.NotNull(link.Events);
            Assert.True(Enum.IsDefined(link.State));
        }
    }

    // ---------------------------------------------------------------- T6-06 핸드셰이크

    /// <summary>내 쪽 설정. 게임서버가 이것과 같은 값을 보내야 수락된다.</summary>
    private static TcpLinkOptions Mine() => new()
    {
        TimeScale = 600,
        NpcCount = 5_000,
        MasterData = WireHash.FromHex(new string('a', 64)),
        Roster = WireHash.FromHex(new string('b', 64)),
        HandshakeTimeout = TimeSpan.FromSeconds(5),
    };

    /// <summary>게임서버가 보내는 정상 Hello.</summary>
    private static WireHello Hello(TcpLinkOptions mine) => new()
    {
        ProtocolVersion = FrameCodec.Version,
        TickRate = 10,
        TimeScale = mine.TimeScale,
        NpcCount = mine.NpcCount,
        StartTick = 1,
        MasterData = mine.MasterData,
        Roster = mine.Roster,
    };

    /// <summary>넷이 다 맞으면 수락하고 Connected 로 간다 (T6-06 완료 조건).</summary>
    [Fact]
    public async Task TcpLink_HandshakeAcceptsMatch()
    {
        TcpLinkOptions mine = Mine();

        await using var link = new TcpGameServerLink(mine);

        var states = new List<LinkState>();

        link.StateChanged += states.Add;

        (bool accepted, WireHelloAck ack) = await RunHandshakeAsync(link, Hello(mine));

        Assert.True(accepted);
        Assert.Equal(1, ack.Accepted);
        Assert.Equal((byte)LinkRejectCode.None, ack.RejectCode);
        Assert.Equal(LinkState.Connected, link.State);
        Assert.Equal(LinkRejectCode.None, link.RejectCode);

        // docs/20 §6.3 — Disconnected → Connecting → Connected
        Assert.Equal([LinkState.Connecting, LinkState.Connected], states);
    }

    /// <summary>
    /// 넷 중 하나라도 다르면 <b>각각 해당 거절 코드로</b> 거절하고 Faulted 다 (T6-06 완료 조건).
    ///
    /// <b>우회 옵션이 없다.</b> 마스터데이터가 다른 두 프로세스를 붙이면 POI code 가 어긋나
    /// NPC 가 엉뚱한 곳으로 가고, 원인을 찾는 데 하루가 든다 (docs/20 §5.5).
    /// </summary>
    [Theory]
    [InlineData("version", LinkRejectCode.ProtocolVersion)]
    [InlineData("timescale", LinkRejectCode.TimeScaleMismatch)]
    [InlineData("masterdata", LinkRejectCode.MasterDataMismatch)]
    [InlineData("roster", LinkRejectCode.RosterMismatch)]
    public async Task TcpLink_HandshakeRejectsMismatch(string what, LinkRejectCode expected)
    {
        TcpLinkOptions mine = Mine();
        WireHello hello = Hello(mine);

        hello = what switch
        {
            "version" => hello with { ProtocolVersion = FrameCodec.Version + 1 },
            "timescale" => hello with { TimeScale = mine.TimeScale + 1 },
            "masterdata" => hello with { MasterData = WireHash.FromHex(new string('c', 64)) },
            "roster" => hello with { Roster = WireHash.FromHex(new string('d', 64)) },
            _ => throw new ArgumentOutOfRangeException(nameof(what)),
        };

        await using var link = new TcpGameServerLink(mine);

        (bool accepted, WireHelloAck ack) = await RunHandshakeAsync(link, hello);

        Assert.False(accepted);
        Assert.Equal(0, ack.Accepted);
        Assert.Equal((byte)expected, ack.RejectCode);
        Assert.Equal(expected, link.RejectCode);

        // Faulted 는 사람이 고쳐야 하는 상태다 — 재시도하지 않는다 (docs/20 §6.3).
        Assert.Equal(LinkState.Faulted, link.State);
    }

    /// <summary>검증 순서가 사양대로다 — 버전이 틀리면 다른 것도 틀렸어도 버전으로 거절한다.</summary>
    [Fact]
    public void TcpLink_ValidateReportsFirstMismatch()
    {
        TcpLinkOptions mine = Mine();
        var link = new TcpGameServerLink(mine);

        WireHello broken = Hello(mine) with
        {
            ProtocolVersion = 99,
            TimeScale = 1,
            MasterData = WireHash.None,
            Roster = WireHash.None,
        };

        Assert.Equal(LinkRejectCode.ProtocolVersion, link.Validate(in broken));

        WireHello good = Hello(mine);

        Assert.Equal(LinkRejectCode.None, link.Validate(in good));
    }

    /// <summary>NPC 수는 기본으로 보지 않는다 — 로스터 해시가 이미 그 집합을 담고 있다.</summary>
    [Fact]
    public void TcpLink_NpcCountIsNotCheckedByDefault()
    {
        TcpLinkOptions mine = Mine();
        WireHello fewer = Hello(mine) with { NpcCount = 500 };

        Assert.Equal(LinkRejectCode.None, new TcpGameServerLink(mine).Validate(in fewer));

        // 켜면 본다.
        TcpLinkOptions strict = mine with { StrictNpcCount = true };

        Assert.Equal(LinkRejectCode.RosterMismatch, new TcpGameServerLink(strict).Validate(in fewer));
    }

    // ---------------------------------------------------------------- T6-07 송신 경로

    /// <summary>
    /// <b>한 번의 Flush = 한 개의 <c>CommandBatch</c> 프레임</b> (N8 · T6-07 완료 조건).
    ///
    /// 배치 경계가 와이어에 그대로 보존되는지를 <b>수신 측이 센 프레임 수</b>로 본다.
    /// </summary>
    [Fact]
    public async Task TcpLink_OneFlushIsOneFrame()
    {
        const int Flushes = 5;

        TcpLinkOptions mine = Mine();

        await using var link = new TcpGameServerLink(mine);

        var pipe = new Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await link.StartSenderAsync(pipe.Writer.AsStream(), cts.Token);

        // 실서비스는 10Hz 라 Flush 사이에 100ms 가 있다. 센더가 따라잡을 틈을 주고 재는 것이
        // N8 이 말하는 상황이다 — 몰아치면 배치 큐가 차서 Flush 가 건너뛴다(그건 아래 테스트가 본다).
        for (int f = 0; f < Flushes; f++)
        {
            for (int i = 0; i < 3; i++)
            {
                NpcCommand c = Command(f * 10 + i);

                link.Enqueue(in c);
            }

            await link.FlushAsync(cts.Token);

            for (int spin = 0; spin < 400 && link.FramesSent <= f; spin++)
            {
                await Task.Delay(5, cts.Token);
            }
        }

        Assert.True(
            link.FramesSent == Flushes,
            $"프레임 {link.FramesSent}/{Flushes} · 상태 {link.State} · "
            + $"건너뜀 {link.FlushesSkipped} · 대기 {link.Stats.PendingCommands}");

        Assert.Equal(0, link.FlushesSkipped);

        await pipe.Writer.CompleteAsync();

        (int frames, int commands) = await CountFramesAsync(pipe.Reader);

        Assert.Equal(Flushes, frames);
        Assert.Equal(Flushes * 3, commands);
        Assert.Equal(Flushes * 3, link.Stats.CommandsFlushed);
    }

    /// <summary>
    /// 역압은 <c>Cosmetic</c> 부터 버리고 <c>Critical</c> 은 지킨다 (T6-07 완료 조건).
    /// <b>정책은 <see cref="PriorityCommandRing"/> 하나에서 온다</b> — 루프백과 같은 코드다.
    /// </summary>
    [Fact]
    public async Task TcpLink_BackpressureDropsCosmeticFirst()
    {
        TcpLinkOptions mine = Mine() with { Capacity = 10 };

        await using var link = new TcpGameServerLink(mine);

        for (int i = 0; i < 10; i++)
        {
            NpcCommand cosmetic = Command(CommandPriority.Cosmetic, i);

            link.Enqueue(in cosmetic);
        }

        Assert.Equal(0, link.Stats.CommandsDropped);

        for (int i = 0; i < 5; i++)
        {
            NpcCommand critical = Command(CommandPriority.Critical, 100 + i);

            link.Enqueue(in critical);
        }

        Assert.Equal(10, link.Stats.PendingCommands);
        Assert.Equal(5, link.Stats.CommandsDropped);
    }

    /// <summary>
    /// <b><c>FlushAsync</c> 경로에 할당이 0 이다</b> (T6-07 완료 조건 · docs/20 §6.2).
    ///
    /// 틱 루프 안에서 불리므로 여기에 할당이 생기면 Gen0 이 돌고 틱 p99 가 무너진다.
    /// 배치 슬롯을 기동 시 전부 잡아 두는 것이 그 근거다.
    /// </summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task TcpLink_FlushDoesNotAllocate()
    {
        const int Iterations = 10_000;

        TcpLinkOptions mine = Mine();

        await using var link = new TcpGameServerLink(mine);

        // 센더를 띄우지 않는다 — 배치 큐가 차면 Flush 는 링을 비우고 곧바로 돌아온다.
        // 두 경로(빈 자리 있음 / 가득) 다 할당이 없어야 한다.
        for (int i = 0; i < 1_000; i++)
        {
            NpcCommand warm = Command(i);

            link.Enqueue(in warm);
            await link.FlushAsync(CancellationToken.None);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < Iterations; i++)
        {
            NpcCommand c = Command(i);

            link.Enqueue(in c);
            await link.FlushAsync(CancellationToken.None);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---------------------------------------------------------------- T6-08 수신 경로

    /// <summary>
    /// 받은 이벤트가 <b>순서대로</b> 채널에 들어간다 (T6-08 완료 조건).
    /// <c>LinkStats</c> 의 여섯 필드가 전부 갱신되는 것도 여기서 본다 (docs/20 §6.4).
    /// </summary>
    [Fact]
    public async Task TcpLink_DeliversEventsInOrder()
    {
        TcpLinkOptions mine = Mine();

        await using var link = new TcpGameServerLink(mine);

        var pipe = new Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await link.StartReceiverAsync(pipe.Reader.AsStream(), cts.Token);

        // 게임서버가 두 배치로 나눠 보낸다. 시퀀스는 연속이다.
        await WriteEventBatchAsync(pipe.Writer, Events(1, 4));
        await WriteEventBatchAsync(pipe.Writer, Events(5, 3));

        var got = new List<GameEvent>();

        while (got.Count < 7 && !cts.IsCancellationRequested)
        {
            if (link.Events.TryRead(out GameEvent ev))
            {
                got.Add(ev);
                continue;
            }

            await Task.Delay(5, cts.Token);
        }

        Assert.Equal(7, got.Count);
        Assert.Equal(Enumerable.Range(1, 7).Select(i => (long)i), got.Select(e => e.Sequence));

        // 명령 쪽도 채워 여섯 필드를 전부 확인한다.
        NpcCommand cosmetic = Command(CommandPriority.Cosmetic, 1);

        link.Enqueue(in cosmetic);

        LinkStats stats = link.Stats;

        Assert.Equal(1, stats.CommandsEnqueued);
        Assert.Equal(7, stats.EventsReceived);
        Assert.Equal(0, stats.EventGapsDetected);
        Assert.Equal(1, stats.PendingCommands);
        Assert.Equal(0, stats.CommandsFlushed);
        Assert.Equal(0, stats.CommandsDropped);
    }

    /// <summary>
    /// 시퀀스를 건너뛴 배치를 주면 <c>EventGapsDetected</c> 가 오른다 (N6 · T6-08 완료 조건).
    ///
    /// <b>갭은 사고의 첫 신호다.</b> 게임서버가 시퀀스를 리셋하면 <c>EventApplier</c> 가
    /// 이후 이벤트를 전부 중복으로 버려 NPC 가 영원히 멈춘다 (docs/20 §5.6).
    /// </summary>
    [Fact]
    public async Task TcpLink_CountsSequenceGaps()
    {
        TcpLinkOptions mine = Mine();

        await using var link = new TcpGameServerLink(mine);

        var pipe = new Pipe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await link.StartReceiverAsync(pipe.Reader.AsStream(), cts.Token);

        await WriteEventBatchAsync(pipe.Writer, Events(1, 3));    // 1 2 3
        await WriteEventBatchAsync(pipe.Writer, Events(7, 2));    // 7 8  → 4·5·6 이 없다

        while (link.Stats.EventsReceived < 5 && !cts.IsCancellationRequested)
        {
            await Task.Delay(5, cts.Token);
        }

        Assert.Equal(5, link.Stats.EventsReceived);

        // 건너뛴 개수만큼 센다 — "갭이 있었다" 가 아니라 "몇 개가 없었나" 다.
        Assert.Equal(3, link.Stats.EventGapsDetected);
    }

    /// <summary>연속한 시퀀스를 단 <see cref="GameEvent"/> 묶음.</summary>
    private static GameEvent[] Events(long from, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new GameEvent
        {
            Kind = GameEventKind.TickSync,
            Sequence = from + i,
            OccurredAt = new Tick(from + i),
            Npc = new NpcId((int)(from + i)),
        })];

    /// <summary>게임서버 흉내 — <c>EventBatch</c> 프레임 하나를 밀어 넣는다.</summary>
    private static async Task WriteEventBatchAsync(PipeWriter writer, GameEvent[] events)
    {
        var wire = new WireEvent[events.Length];

        for (int i = 0; i < events.Length; i++)
        {
            wire[i] = WireEvent.From(in events[i]);
        }

        byte[] payload = MemoryPackSerializer.Serialize(wire);
        var buffer = new ArrayBufferWriter<byte>();

        FrameCodec.WriteHeader(buffer, LinkMessageKind.EventBatch, payload.Length);
        buffer.Write(payload);

        await writer.WriteAsync(buffer.WrittenMemory);
    }

    /// <summary>스트림에서 <c>CommandBatch</c> 프레임 수와 그 안의 명령 수를 센다.</summary>
    private static async Task<(int Frames, int Commands)> CountFramesAsync(PipeReader reader)
    {
        int frames = 0;
        int commands = 0;

        while (true)
        {
            ReadResult result = await reader.ReadAsync();
            ReadOnlySequence<byte> buffer = result.Buffer;

            while (FrameCodec.TryReadFrame(ref buffer, out LinkMessageKind kind, out ReadOnlySequence<byte> payload))
            {
                Assert.Equal(LinkMessageKind.CommandBatch, kind);

                frames++;
                commands += MemoryPackSerializer.Deserialize<WireCommand[]>(payload.ToArray())!.Length;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (result.IsCompleted && buffer.IsEmpty)
            {
                break;
            }
        }

        return (frames, commands);
    }

    private static NpcCommand Command(CommandPriority priority, int npc) => new()
    {
        Kind = NpcCommandKind.MoveTo,
        Npc = new NpcId(npc),
        IssuedAt = new Tick(1),
        Correlation = new CorrelationId((uint)npc),
        Priority = priority,
    };

    /// <summary>
    /// 게임서버 흉내. Hello 를 밀어 넣고 링크가 쓴 <c>HelloAck</c> 를 되읽는다.
    ///
    /// <b>소켓을 쓰지 않는다.</b> <c>HandshakeAsync</c> 가 <c>Stream</c> 을 받는 이유가 이것이다 —
    /// 네 가지 불일치를 각각 재현하는 데 포트도 타이밍도 필요하지 않다.
    /// </summary>
    private static async Task<(bool Accepted, WireHelloAck Ack)> RunHandshakeAsync(
        TcpGameServerLink link, WireHello hello)
    {
        var toLink = new Pipe();
        var fromLink = new Pipe();

        byte[] payload = MemoryPackSerializer.Serialize(hello);
        var writer = new ArrayBufferWriter<byte>();

        FrameCodec.WriteHeader(writer, LinkMessageKind.Hello, payload.Length);
        writer.Write(payload);

        await toLink.Writer.WriteAsync(writer.WrittenMemory);
        await toLink.Writer.CompleteAsync();

        var duplex = new DuplexStream(toLink.Reader.AsStream(), fromLink.Writer.AsStream());

        bool accepted = await link.HandshakeAsync(duplex, CancellationToken.None);

        await fromLink.Writer.CompleteAsync();

        Stream back = fromLink.Reader.AsStream();
        var header = new byte[FrameCodec.HeaderSize];

        await back.ReadExactlyAsync(header);

        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
        var body = new byte[length];

        await back.ReadExactlyAsync(body);

        Assert.Equal((byte)LinkMessageKind.HelloAck, header[4]);

        return (accepted, MemoryPackSerializer.Deserialize<WireHelloAck>(body));
    }

    /// <summary>읽기와 쓰기를 다른 스트림에 붙인 이중 스트림. 테스트 전용.</summary>
    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => write.Flush();

        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);

        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            read.ReadAsync(buffer, ct);

        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            write.WriteAsync(buffer, ct);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
