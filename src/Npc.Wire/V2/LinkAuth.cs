using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Npc.Wire.V2;

/// <summary>
/// 링크 상호 인증 (A-06).
///
/// <b>핸드셰이크가 검증하는 것은 "같은 데이터를 보고 있는가" 였지 "네가 누구인가" 가 아니었다.</b>
/// 링크 포트에 붙기만 하면 누구나 NPC 명령 스트림을 관측하고 이벤트를 위조할 수 있었다.
///
/// <para>
/// HMAC-SHA256 이다. 비밀은 환경변수 <c>NPC_LINK_SECRET</c>(hex)로만 온다 — 파일에도 인자에도
/// 두지 않는다. 인자는 <c>ps</c> 에 보이고 파일은 이미지에 굽힌다.
/// </para>
///
/// <para>
/// <b>양쪽이 서로를 검증한다.</b> 게임서버가 nonce 와 <c>Hello</c> 태그를 보내고, NPC 서버가
/// 같은 비밀로 검산한 뒤 <c>HelloAck</c> 태그를 만들어 되돌린다. 한쪽만 검증하면
/// "우리에게 붙는 가짜 게임서버" 는 막아도 "가짜 NPC 서버가 게임서버에 붙는 것" 은 못 막는다.
/// </para>
///
/// <para>
/// N3 준수: 태그는 <see cref="WireHash"/>(32B), nonce 는 <see cref="WireNonce"/>(16B)다.
/// 둘 다 값 타입이고 문자열이 아니다.
/// </para>
/// </summary>
public static class LinkAuth
{
    /// <summary>비밀의 바이트 길이. 32바이트(hex 64자)를 요구한다.</summary>
    public const int SecretBytes = 32;

    /// <summary>세션당 기억할 nonce 수. 재사용 공격을 막는다.</summary>
    public const int NonceCacheSize = 256;

    /// <summary>
    /// 환경변수 형식의 hex 비밀을 바이트로 푼다.
    /// </summary>
    /// <param name="hex">64자 hex. 공백은 무시한다.</param>
    /// <param name="secret">푼 비밀.</param>
    /// <param name="error">실패 사유.</param>
    public static bool TryParseSecret(string? hex, out byte[] secret, out string? error)
    {
        secret = [];
        error = null;

        if (string.IsNullOrWhiteSpace(hex))
        {
            error = "링크 비밀이 비어 있다.";
            return false;
        }

        string trimmed = hex.Trim();

        if (trimmed.Length != SecretBytes * 2)
        {
            error = $"링크 비밀은 hex {SecretBytes * 2}자여야 한다: {trimmed.Length}자를 받았다.";
            return false;
        }

        try
        {
            secret = Convert.FromHexString(trimmed);
            return true;
        }
        catch (FormatException)
        {
            error = "링크 비밀이 hex 가 아니다.";
            return false;
        }
    }

    /// <summary>
    /// <c>Hello</c> 태그를 계산한다. 게임서버가 만들고 NPC 서버가 검산한다.
    ///
    /// <b>재료에 <c>Auth</c> 자신은 넣지 않는다</b> — 순환이다. 그 밖의 협상·검증 대상 필드는
    /// 전부 넣는다: 하나라도 빼면 그 필드를 중간에서 바꿔치기할 수 있다.
    /// </summary>
    public static WireHash ComputeHello(ReadOnlySpan<byte> secret, in WireHelloV2 hello)
    {
        Span<byte> material = stackalloc byte[128];
        int written = 0;

        Write32(material, ref written, (uint)hello.ProtocolVersion);
        Write32(material, ref written, (uint)hello.MinProtocolVersion);
        Write16(material, ref written, hello.ContractMajor);
        Write16(material, ref written, hello.ContractMinor);
        Write64(material, ref written, hello.Features);
        Write32(material, ref written, (uint)hello.TickRate);
        Write32(material, ref written, (uint)hello.TimeScale);
        Write32(material, ref written, (uint)hello.NpcCount);
        Write64(material, ref written, (ulong)hello.StartTick);
        Write16(material, ref written, hello.StartGameMinuteOfDay);
        Write16(material, ref written, hello.ShardId);
        Write64(material, ref written, hello.ZoneMask);
        Write32(material, ref written, hello.SessionEpoch);

        return Compute(
            secret,
            material[..written],
            hello.MasterDataStructural,
            hello.MasterDataContent,
            hello.Roster,
            hello.Nonce);
    }

    /// <summary>
    /// <c>HelloAck</c> 태그를 계산한다. NPC 서버가 만들고 게임서버가 검산한다.
    ///
    /// <b>같은 nonce 를 쓴다.</b> 게임서버가 낸 난수를 되돌려 서명해야 "지금 이 세션에 대한 응답"
    /// 임이 증명된다 — 그러지 않으면 예전 응답을 그대로 재생할 수 있다.
    /// </summary>
    public static WireHash ComputeAck(
        ReadOnlySpan<byte> secret, in WireHelloAckV2 ack, in WireNonce nonce)
    {
        Span<byte> material = stackalloc byte[64];
        int written = 0;

        Write32(material, ref written, (uint)ack.ProtocolVersion);
        Write16(material, ref written, ack.ContractMajor);
        Write16(material, ref written, ack.ContractMinor);
        Write64(material, ref written, ack.Features);
        Write32(material, ref written, (uint)ack.TimeScale);
        Write32(material, ref written, (uint)ack.NpcCount);
        material[written++] = ack.Accepted;
        material[written++] = ack.RejectCode;

        return Compute(
            secret,
            material[..written],
            ack.MasterDataStructural,
            ack.MasterDataContent,
            ack.Roster,
            nonce);
    }

    /// <summary>
    /// 태그를 비교한다. <b>고정 시간 비교다</b> — 바이트마다 일찍 빠져나오면
    /// 타이밍으로 태그를 한 바이트씩 알아낼 수 있다.
    /// </summary>
    public static bool Verify(in WireHash expected, in WireHash actual)
    {
        Span<byte> a = stackalloc byte[32];
        Span<byte> b = stackalloc byte[32];

        WriteHash(a, expected);
        WriteHash(b, actual);

        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>OS 난수로 nonce 를 만든다. 게임서버가 세션마다 부른다.</summary>
    public static WireNonce NewNonce()
    {
        Span<byte> bytes = stackalloc byte[16];

        RandomNumberGenerator.Fill(bytes);

        return WireNonce.From(bytes);
    }

    private static WireHash Compute(
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> fields,
        in WireHash structural,
        in WireHash content,
        in WireHash roster,
        in WireNonce nonce)
    {
        Span<byte> material = stackalloc byte[fields.Length + (32 * 3) + 16];

        fields.CopyTo(material);

        int offset = fields.Length;

        WriteHash(material[offset..], structural);
        offset += 32;
        WriteHash(material[offset..], content);
        offset += 32;
        WriteHash(material[offset..], roster);
        offset += 32;
        nonce.WriteTo(material[offset..]);

        Span<byte> tag = stackalloc byte[32];

        HMACSHA256.HashData(secret, material, tag);

        return new WireHash(
            BinaryPrimitives.ReadUInt64BigEndian(tag),
            BinaryPrimitives.ReadUInt64BigEndian(tag[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(tag[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(tag[24..]));
    }

    private static void WriteHash(Span<byte> destination, in WireHash hash)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, hash.A);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], hash.B);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], hash.C);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], hash.D);
    }

    private static void Write16(Span<byte> destination, ref int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], value);
        offset += 2;
    }

    private static void Write32(Span<byte> destination, ref int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], value);
        offset += 4;
    }

    private static void Write64(Span<byte> destination, ref int offset, ulong value)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination[offset..], value);
        offset += 8;
    }
}

/// <summary>
/// 세션당 nonce 기억 (A-06).
///
/// <b>같은 nonce 를 두 번 받으면 거절한다.</b> 재생 공격은 태그가 맞아도 막아야 한다 —
/// 태그는 "그때 이 비밀을 아는 누군가가 만들었다" 만 증명하지 "지금 만들었다" 를 증명하지 않는다.
///
/// 링 버퍼다. <see cref="LinkAuth.NonceCacheSize"/> 개를 넘으면 가장 오래된 것을 잊는다 —
/// 세션 하나에 핸드셰이크가 그만큼 일어날 일이 없고, 무한히 기억하면 그것이 메모리 누수다.
/// </summary>
public sealed class NonceCache
{
    private readonly WireNonce[] _seen = new WireNonce[LinkAuth.NonceCacheSize];
    private readonly Lock _gate = new();
    private int _cursor;
    private int _count;

    /// <summary>처음 보는 nonce 면 기억하고 true. 이미 본 것이면 false.</summary>
    public bool TryRemember(in WireNonce nonce)
    {
        lock (_gate)
        {
            for (int i = 0; i < _count; i++)
            {
                if (_seen[i] == nonce)
                {
                    return false;
                }
            }

            _seen[_cursor] = nonce;
            _cursor = (_cursor + 1) % _seen.Length;

            if (_count < _seen.Length)
            {
                _count++;
            }

            return true;
        }
    }
}
