using System.Buffers;
using System.Buffers.Binary;
using Npc.Wire;

namespace Npc.Tests.Wire;

/// <summary>
/// 프레임 코덱. docs/20 §5.1.
///
/// <b>TCP 는 경계를 지켜 주지 않는다.</b> 한 번의 읽기에 프레임이 반만 오거나 세 개가 붙어 올 수 있고,
/// 그 둘을 다 다루지 못하면 스트림이 조용히 깨진다. 이 파일이 그 두 경우를 못 박는다.
/// </summary>
[Trait("Category", "Wire")]
public sealed class FrameCodecTests
{
    /// <summary>한 프레임을 만든다. 페이로드는 첫 바이트로 구별한다.</summary>
    private static byte[] Frame(LinkMessageKind kind, params byte[] payload)
    {
        var writer = new ArrayBufferWriter<byte>();

        FrameCodec.WriteHeader(writer, kind, payload.Length);
        writer.Write(payload);

        return writer.WrittenSpan.ToArray();
    }

    /// <summary>헤더가 사양의 바이트 배치 그대로다. 여기가 틀리면 상대가 못 읽는다.</summary>
    [Fact]
    public void Frame_HeaderLayoutMatchesSpec()
    {
        byte[] frame = Frame(LinkMessageKind.EventBatch, 0xAA, 0xBB, 0xCC);

        Assert.Equal(FrameCodec.HeaderSize + 3, frame.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(frame));
        Assert.Equal((byte)LinkMessageKind.EventBatch, frame[4]);
        Assert.Equal(FrameCodec.Version, frame[5]);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(6)));   // Reserved
    }

    /// <summary>
    /// <b>1바이트씩 넣어도 복원한다</b> (T6-04 완료 조건). 다 오기 전에는 false 이고
    /// 버퍼를 건드리지 않는다 — 호출부는 더 읽고 다시 부른다.
    /// </summary>
    [Fact]
    public void Frame_SplitAcrossReads()
    {
        byte[] frame = Frame(LinkMessageKind.Heartbeat, 1, 2, 3, 4, 5, 6, 7, 8);

        for (int prefix = 0; prefix < frame.Length; prefix++)
        {
            var partial = new ReadOnlySequence<byte>(frame, 0, prefix);

            Assert.False(
                FrameCodec.TryReadFrame(ref partial, out _, out _),
                $"{prefix}바이트만 왔는데 프레임을 떼어냈다.");

            // 실패했으면 버퍼가 그대로여야 한다 — 소비했다고 착각하면 스트림이 밀린다.
            Assert.Equal(prefix, partial.Length);
        }

        var full = new ReadOnlySequence<byte>(frame);

        Assert.True(FrameCodec.TryReadFrame(ref full, out LinkMessageKind kind, out ReadOnlySequence<byte> payload));
        Assert.Equal(LinkMessageKind.Heartbeat, kind);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, payload.ToArray());
        Assert.Equal(0, full.Length);
    }

    /// <summary>한 버퍼에 여러 프레임이 붙어 와도 하나씩 떼어낸다.</summary>
    [Fact]
    public void Frame_MultipleFramesInOneBuffer()
    {
        byte[] a = Frame(LinkMessageKind.Hello, 0x01);
        byte[] b = Frame(LinkMessageKind.CommandBatch, 0x02, 0x03);
        byte[] c = Frame(LinkMessageKind.Bye);

        var joined = new byte[a.Length + b.Length + c.Length];

        a.CopyTo(joined, 0);
        b.CopyTo(joined, a.Length);
        c.CopyTo(joined, a.Length + b.Length);

        var buffer = new ReadOnlySequence<byte>(joined);

        Assert.True(FrameCodec.TryReadFrame(ref buffer, out LinkMessageKind k1, out ReadOnlySequence<byte> p1));
        Assert.Equal(LinkMessageKind.Hello, k1);
        Assert.Equal(new byte[] { 0x01 }, p1.ToArray());

        Assert.True(FrameCodec.TryReadFrame(ref buffer, out LinkMessageKind k2, out ReadOnlySequence<byte> p2));
        Assert.Equal(LinkMessageKind.CommandBatch, k2);
        Assert.Equal(new byte[] { 0x02, 0x03 }, p2.ToArray());

        Assert.True(FrameCodec.TryReadFrame(ref buffer, out LinkMessageKind k3, out ReadOnlySequence<byte> p3));
        Assert.Equal(LinkMessageKind.Bye, k3);
        Assert.Equal(0, p3.Length);

        // 셋을 다 떼어내면 남는 것이 없다.
        Assert.Equal(0, buffer.Length);
        Assert.False(FrameCodec.TryReadFrame(ref buffer, out _, out _));
    }

    /// <summary>
    /// 여러 조각(<c>ReadOnlySequence</c> 의 다중 세그먼트)에 걸쳐 있어도 읽는다.
    /// <c>PipeReader</c> 가 주는 버퍼는 대개 이 모양이다 — 단일 배열만 가정하면 실서비스에서 깨진다.
    /// </summary>
    [Fact]
    public void Frame_ReadsAcrossSegments()
    {
        byte[] frame = Frame(LinkMessageKind.EventBatch, 9, 8, 7, 6, 5);

        // 헤더 한가운데에서 자른다 — 가장 아픈 자리다.
        ReadOnlySequence<byte> split = Segmented(frame, 3);

        Assert.True(FrameCodec.TryReadFrame(ref split, out LinkMessageKind kind, out ReadOnlySequence<byte> payload));
        Assert.Equal(LinkMessageKind.EventBatch, kind);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5 }, payload.ToArray());
    }

    /// <summary>
    /// 길이가 상한을 넘으면 던진다. <b>프로세스는 살아야 한다</b> —
    /// 호출부가 잡아서 <c>Bye</c> 를 보내고 소켓만 닫는다 (T6-04 완료 조건).
    /// </summary>
    [Fact]
    public void Frame_RejectsOversizePayload()
    {
        var header = new byte[FrameCodec.HeaderSize];

        BinaryPrimitives.WriteUInt32LittleEndian(header, FrameCodec.MaxPayloadLength + 1u);
        header[4] = (byte)LinkMessageKind.CommandBatch;
        header[5] = FrameCodec.Version;

        var buffer = new ReadOnlySequence<byte>(header);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () =>
            {
                ReadOnlySequence<byte> local = buffer;

                FrameCodec.TryReadFrame(ref local, out _, out _);
            });

        Assert.Contains("상한", ex.Message, StringComparison.Ordinal);

        // 쓰는 쪽도 막는다 — 상한을 넘는 프레임을 애초에 만들지 않는다.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FrameCodec.WriteHeader(
                new ArrayBufferWriter<byte>(), LinkMessageKind.CommandBatch, FrameCodec.MaxPayloadLength + 1));
    }

    /// <summary>버전이 다르면 던진다. 길이 해석보다 <b>먼저</b> 본다.</summary>
    [Fact]
    public void Frame_RejectsWrongVersion()
    {
        var header = new byte[FrameCodec.HeaderSize];

        // 길이도 같이 망가뜨린다. 버전을 먼저 보지 않으면 길이 쪽 메시지가 나온다.
        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);
        header[4] = (byte)LinkMessageKind.Hello;
        header[5] = FrameCodec.Version + 1;

        var buffer = new ReadOnlySequence<byte>(header);

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () =>
            {
                ReadOnlySequence<byte> local = buffer;

                FrameCodec.TryReadFrame(ref local, out _, out _);
            });

        Assert.Contains("버전", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>페이로드가 빈 프레임도 정상이다 — <c>Bye</c> 는 1바이트, 하트비트도 작다.</summary>
    [Fact]
    public void Frame_HandlesEmptyPayload()
    {
        byte[] frame = Frame(LinkMessageKind.Bye);
        var buffer = new ReadOnlySequence<byte>(frame);

        Assert.Equal(FrameCodec.HeaderSize, frame.Length);
        Assert.True(FrameCodec.TryReadFrame(ref buffer, out LinkMessageKind kind, out ReadOnlySequence<byte> payload));
        Assert.Equal(LinkMessageKind.Bye, kind);
        Assert.Equal(0, payload.Length);
    }

    /// <summary><paramref name="at"/> 에서 두 세그먼트로 자른 시퀀스.</summary>
    private static ReadOnlySequence<byte> Segmented(byte[] data, int at)
    {
        var first = new Segment(data.AsMemory(0, at));
        Segment last = first.Append(data.AsMemory(at));

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };

            Next = next;

            return next;
        }
    }
}
