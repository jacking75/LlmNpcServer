using System.Globalization;
using MemoryPack;

// v1 링크 메시지. <b>이 파일은 수정하지 않는다</b> (B-01).
//
// 네임스페이스는 Npc.Wire 그대로다 — 폴더만 V1/ 로 옮겼다. 이름을 바꾸면 참조 1,000곳이
// 흔들리는데, "동결" 은 폴더가 아니라 테스트(Wire_LayoutIsFrozen)가 강제한다.
// 새 필드는 V2/ 에만 넣는다.
namespace Npc.Wire;

/// <summary>프레임 종류. docs/20 §5.2 의 표 그대로다.</summary>
public enum LinkMessageKind : byte
{
    /// <summary>없음. 유효한 프레임에는 나오지 않는다.</summary>
    None = 0,

    /// <summary>게임서버 → NPC 서버. 핸드셰이크 시작.</summary>
    Hello = 1,

    /// <summary>NPC 서버 → 게임서버. 수락/거절.</summary>
    HelloAck = 2,

    /// <summary>NPC 서버 → 게임서버. 한 번의 <c>FlushAsync</c> = 한 프레임 (N8).</summary>
    CommandBatch = 3,

    /// <summary>게임서버 → NPC 서버.</summary>
    EventBatch = 4,

    /// <summary>양방향.</summary>
    Heartbeat = 5,

    /// <summary>양방향. 정상 종료·프로토콜 위반 통보.</summary>
    Bye = 6,
}

/// <summary>핸드셰이크 거절 사유. docs/20 §5.4.</summary>
public enum LinkRejectCode : byte
{
    /// <summary>거절하지 않았다.</summary>
    None = 0,

    /// <summary>프로토콜 버전이 다르다.</summary>
    ProtocolVersion,

    /// <summary>마스터데이터 콘텐츠 해시가 다르다.</summary>
    MasterDataMismatch,

    /// <summary>NPC 로스터 해시가 다르다.</summary>
    RosterMismatch,

    /// <summary>타임스케일이 다르다.</summary>
    TimeScaleMismatch,

    /// <summary>계약 주 버전이 다르다 (B-01). <b>추가는 뒤에만</b> — 번호를 재배치하지 않는다.</summary>
    ContractMismatch,

    /// <summary>인증에 실패했다 (A-06).</summary>
    AuthFailed,
}

/// <summary>연결 종료 사유. docs/20 §5.4.</summary>
public enum LinkByeCode : byte
{
    /// <summary>정상 종료.</summary>
    Shutdown = 0,

    /// <summary>프레임이 규약을 어겼다.</summary>
    ProtocolViolation,

    /// <summary>핸드셰이크에서 거절됐다.</summary>
    HandshakeRejected,

    /// <summary>하트비트가 끊겼다.</summary>
    Timeout,
}

/// <summary>
/// SHA-256 해시 32바이트를 <c>ulong</c> 4개로 싣는다. docs/20 §5.4.
///
/// <b>hex 문자열로 싣지 않는다</b> — N3(패킷에 <c>string</c> 금지)이 와이어에서도 그대로다
/// (docs/20 §3.4). <c>ulong</c> 4개면 정확히 32바이트라 손실도 없고, hex 로 실으면
/// 64바이트에 파싱까지 붙는다. 사람이 읽어야 할 때만 <see cref="ToHex"/> 로 되돌린다.
/// </summary>
/// <param name="A">바이트 0~7 (빅엔디언 해석).</param>
/// <param name="B">바이트 8~15.</param>
/// <param name="C">바이트 16~23.</param>
/// <param name="D">바이트 24~31.</param>
[MemoryPackable]
public readonly partial record struct WireHash(ulong A, ulong B, ulong C, ulong D)
{
    /// <summary>hex 표기 길이. SHA-256 32바이트 = 64자.</summary>
    public const int HexLength = 64;

    /// <summary>아무것도 아닌 해시. 비교에 쓰면 항상 불일치다.</summary>
    public static WireHash None => default;

    /// <summary>
    /// 소문자 hex 64자에서 만든다. <c>sha256:</c> 접두는 붙어 있어도 된다.
    /// </summary>
    /// <exception cref="FormatException">64자 hex 가 아니다.</exception>
    public static WireHash FromHex(string sha256Hex)
    {
        ArgumentNullException.ThrowIfNull(sha256Hex);

        ReadOnlySpan<char> hex = sha256Hex.AsSpan();

        if (hex.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            hex = hex[7..];
        }

        if (hex.Length != HexLength)
        {
            throw new FormatException($"SHA-256 hex 는 {HexLength}자여야 한다. 받은 길이 {hex.Length}.");
        }

        return new WireHash(Word(hex[..16]), Word(hex[16..32]), Word(hex[32..48]), Word(hex[48..]));

        static ulong Word(ReadOnlySpan<char> part) =>
            ulong.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 소문자 hex 64자로 되돌린다. <b>로그·비교 출력용이고 와이어에는 나가지 않는다.</b>
    /// </summary>
    public string ToHex() => string.Create(
        HexLength,
        this,
        static (span, hash) =>
        {
            Write(span[..16], hash.A);
            Write(span[16..32], hash.B);
            Write(span[32..48], hash.C);
            Write(span[48..], hash.D);

            static void Write(Span<char> target, ulong value)
            {
                bool ok = value.TryFormat(target, out int written, "x16", CultureInfo.InvariantCulture);

                // x16 은 항상 16자를 채운다. 실패는 있을 수 없다.
                if (!ok || written != 16)
                {
                    throw new InvalidOperationException("hex 변환이 16자를 채우지 못했다.");
                }
            }
        });
}

/// <summary>
/// 핸드셰이크 시작. 게임서버 → NPC 서버. docs/20 §5.4·§5.5.
///
/// <b>여기 실린 넷이 전부 검증 대상이다</b> — 프로토콜 버전 · 타임스케일 ·
/// 마스터데이터 해시 · 로스터 해시. 하나라도 다르면 거절하고 <b>우회 옵션을 만들지 않는다</b>
/// (docs/20 §6.3). 마스터데이터가 어긋난 채로 붙으면 POI id 가 서로 다른 곳을 가리킨다.
/// </summary>
[MemoryPackable]
public partial struct WireHello
{
    /// <summary>와이어 프로토콜 버전. 지금은 1 이다.</summary>
    public int ProtocolVersion;

    /// <summary>틱 레이트(Hz). 10 이다.</summary>
    public int TickRate;

    /// <summary>게임서버의 타임스케일. NPC 서버와 같아야 한다.</summary>
    public int TimeScale;

    /// <summary>NPC 수. 로스터 크기와 같아야 한다.</summary>
    public int NpcCount;

    /// <summary>게임서버의 현재 틱 (N4).</summary>
    public long StartTick;

    /// <summary>마스터데이터 콘텐츠 해시.</summary>
    public WireHash MasterData;

    /// <summary>NPC 로스터 해시 (docs/20 §10.2).</summary>
    public WireHash Roster;
}

/// <summary>핸드셰이크 응답. NPC 서버 → 게임서버. docs/20 §5.4.</summary>
[MemoryPackable]
public partial struct WireHelloAck
{
    /// <summary>NPC 서버가 쓰는 프로토콜 버전.</summary>
    public int ProtocolVersion;

    /// <summary>NPC 서버의 타임스케일.</summary>
    public int TimeScale;

    /// <summary>NPC 서버가 들고 있는 NPC 수.</summary>
    public int NpcCount;

    /// <summary>NPC 서버의 마스터데이터 해시.</summary>
    public WireHash MasterData;

    /// <summary>NPC 서버의 로스터 해시.</summary>
    public WireHash Roster;

    /// <summary>수락했으면 1, 거절이면 0. <c>bool</c> 대신 바이트로 둔다 — 크기가 고정된다.</summary>
    public byte Accepted;

    /// <summary>거절 사유. <see cref="LinkRejectCode"/>.</summary>
    public byte RejectCode;
}

/// <summary>
/// 하트비트. 양방향. docs/20 §5.4.
///
/// <b><c>DateTime</c> 이 없다</b> (N4). 살아 있는지는 틱과 시퀀스로만 말한다.
/// </summary>
[MemoryPackable]
public partial struct WireHeartbeat
{
    /// <summary>보내는 쪽의 현재 틱.</summary>
    public long Tick;

    /// <summary>보내는 쪽이 마지막으로 발행한 이벤트 시퀀스 (N6). 명령 쪽은 0 이다.</summary>
    public long Sequence;
}

/// <summary>연결 종료 통보. 양방향. docs/20 §5.4.</summary>
[MemoryPackable]
public partial struct WireBye
{
    /// <summary>종료 사유. <see cref="LinkByeCode"/>.</summary>
    public byte Code;
}
