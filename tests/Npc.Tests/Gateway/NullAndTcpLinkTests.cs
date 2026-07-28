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
