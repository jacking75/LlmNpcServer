using System.Buffers;
using System.Buffers.Binary;

namespace Npc.Wire;

/// <summary>
/// 프레임 헤더를 쓰고 읽는다. docs/20 §5.1.
///
/// <code>
///  0      1      2      3      4      5      6      7      8 ...
/// +------+------+------+------+------+------+------+------+---------------+
/// |      PayloadLength (u32)   | Kind | Ver  |   Reserved  |    Payload    |
/// +------+------+------+------+------+------+------+------+---------------+
///         little-endian          u8     u8      u16 = 0     MemoryPack
/// </code>
///
/// <para>
/// <b>헤더는 손으로 쓴다.</b> MemoryPack 안에 길이를 또 넣지 않는다 —
/// 진실의 출처를 둘로 만들면 언젠가 어긋나고, 어긋나는 순간 스트림이 깨진다.
/// </para>
///
/// <para>
/// <b>길이 상한 검사를 빼지 않는다.</b> 길이 접두 프로토콜에서 그 검사가 없으면
/// 잘못된 4바이트 하나로 프로세스가 죽는다 — 상대가 악의적이지 않아도
/// 프레임 경계가 한 번 밀리면 바로 그 상황이 된다 (docs/20 §5.1).
/// </para>
/// </summary>
public static class FrameCodec
{
    /// <summary>헤더 크기. 고정 8바이트.</summary>
    public const int HeaderSize = 8;

    /// <summary>지금 쓰는 와이어 버전. 다르면 <c>Bye(ProtocolViolation)</c> 후 끊는다.</summary>
    public const byte Version = 1;

    /// <summary>페이로드 상한. 1 MiB.</summary>
    public const int MaxPayloadLength = 1 << 20;

    /// <summary>헤더를 쓴다. 페이로드는 호출부가 이어서 쓴다.</summary>
    /// <param name="writer">쓸 곳.</param>
    /// <param name="kind">프레임 종류.</param>
    /// <param name="payloadLength">헤더를 뺀 페이로드 바이트 수.</param>
    /// <exception cref="ArgumentOutOfRangeException">길이가 음수이거나 상한을 넘는다.</exception>
    public static void WriteHeader(IBufferWriter<byte> writer, LinkMessageKind kind, int payloadLength)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadLength, MaxPayloadLength);

        Span<byte> header = writer.GetSpan(HeaderSize);

        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payloadLength);
        header[4] = (byte)kind;
        header[5] = Version;
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);   // Reserved

        writer.Advance(HeaderSize);
    }

    /// <summary>
    /// <paramref name="buffer"/> 앞에서 <b>완전한 프레임 하나</b>를 떼어낸다.
    ///
    /// <para>
    /// 아직 다 안 왔으면 false 를 돌려주고 <paramref name="buffer"/> 를 건드리지 않는다 —
    /// 호출부는 더 읽고 다시 부른다. TCP 는 경계를 지켜 주지 않으므로 이 재시도가 정상 경로다.
    /// </para>
    /// </summary>
    /// <param name="buffer">읽을 곳. 성공하면 <b>떼어낸 프레임 뒤로 밀려 있다.</b></param>
    /// <param name="kind">프레임 종류.</param>
    /// <param name="payload">페이로드. <paramref name="buffer"/> 위를 가리킨다.</param>
    /// <returns>완전한 프레임을 떼어냈으면 true.</returns>
    /// <exception cref="InvalidDataException">
    /// 버전이 다르거나 길이가 상한을 넘는다. <b>연결은 끊어야 하지만 프로세스는 산다</b> —
    /// 호출부가 잡아서 <c>Bye(ProtocolViolation)</c> 를 보내고 소켓만 닫는다.
    /// </exception>
    public static bool TryReadFrame(
        ref ReadOnlySequence<byte> buffer,
        out LinkMessageKind kind,
        out ReadOnlySequence<byte> payload)
    {
        kind = LinkMessageKind.None;
        payload = default;

        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderSize];

        buffer.Slice(0, HeaderSize).CopyTo(header);

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        byte version = header[5];

        // 버전을 길이보다 먼저 본다 — 버전이 다르면 길이 해석 자체를 믿을 수 없다.
        if (version != Version)
        {
            throw new InvalidDataException(
                $"와이어 버전이 다르다: 받은 {version}, 기대 {Version}. 연결을 끊는다 (docs/20 §5.1).");
        }

        if (length > MaxPayloadLength)
        {
            throw new InvalidDataException(
                $"페이로드가 상한을 넘는다: {length}B > {MaxPayloadLength}B. "
                + "프레임 경계가 밀렸을 가능성이 높다. 연결을 끊는다 (docs/20 §5.1).");
        }

        long total = HeaderSize + length;

        if (buffer.Length < total)
        {
            return false;   // 아직 다 안 왔다. 버퍼를 건드리지 않는다
        }

        kind = (LinkMessageKind)header[4];
        payload = buffer.Slice(HeaderSize, length);
        buffer = buffer.Slice(total);

        return true;
    }
}
