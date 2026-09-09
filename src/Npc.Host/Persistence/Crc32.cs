namespace Npc.Host.Persistence;

/// <summary>
/// CRC-32 (IEEE 802.3). 스냅샷 꼬리에 붙는다 (A-01).
///
/// <b>암호 해시가 아니다.</b> 여기서 잡으려는 것은 공격이 아니라 <b>반쯤 쓰인 파일</b>과
/// 디스크 비트 썩음이다. SHA-256 을 쓰면 1.6MB 를 쓸 때마다 그 비용을 낸다.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    /// <summary>한 번에 계산한다.</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Update(0xFFFFFFFFu, data));

    /// <summary>이어서 계산한다. 시작값은 <c>0xFFFFFFFF</c> 다.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>누적값을 최종값으로.</summary>
    public static uint Finish(uint crc) => crc ^ 0xFFFFFFFFu;

    private static uint[] BuildTable()
    {
        var table = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            uint value = i;

            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}

/// <summary>
/// 지나가는 바이트의 CRC 를 누적하는 쓰기 전용 스트림 래퍼 (A-01).
///
/// 파일을 두 번 읽지 않으려고 둔다 — 1.6MB 를 쓰고 다시 읽어 해시하면 I/O 가 두 배다.
/// </summary>
public sealed class Crc32Stream : Stream
{
    private readonly Stream _inner;
    private uint _crc = 0xFFFFFFFFu;

    /// <summary>안쪽 스트림을 감싼다. <b>Dispose 해도 안쪽은 닫지 않는다.</b></summary>
    public Crc32Stream(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
    }

    /// <summary>지금까지의 CRC 최종값.</summary>
    public uint Value => Crc32.Finish(_crc);

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        _crc = Crc32.Update(_crc, buffer.AsSpan(offset, count));
        _inner.Write(buffer, offset, count);
    }

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _crc = Crc32.Update(_crc, buffer);
        _inner.Write(buffer);
    }

    /// <inheritdoc />
    public override void WriteByte(byte value)
    {
        Span<byte> one = [value];

        _crc = Crc32.Update(_crc, one);
        _inner.WriteByte(value);
    }
}
