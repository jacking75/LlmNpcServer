using System.Buffers;
using Npc.Wire;

namespace Npc.TestBed.Protocol;

/// <summary>
/// 게임서버 ↔ 클라이언트 프레임 종류. docs/20 §8.
///
/// <para>
/// <b>서버 쪽은 1~63, 클라 쪽은 64~127 로 갈라 둔다.</b> 로그에서 방향을 헷갈리지 않기
/// 위한 것이다 — 한 줄만 보고도 누가 보낸 것인지 안다. 링크 프로토콜의
/// <see cref="LinkMessageKind"/> 와 값이 겹쳐도 상관없다. 다른 소켓이다.
/// </para>
///
/// <para><b>번호를 재배치하지 않는다.</b> 기록된 세션 덤프가 통째로 깨진다.</para>
/// </summary>
public enum ClientMessageKind : byte
{
    /// <summary>없음. 유효한 프레임에는 나오지 않는다.</summary>
    None = 0,

    // ── 서버 → 클라이언트 (1~63) ────────────────────────────────

    /// <summary>접속 1회. 월드 경계와 배정된 <c>PlayerId</c> 가 들어 있다.</summary>
    SrvHello = 1,

    /// <summary>2틱(5Hz). AOI 안 엔티티.</summary>
    Snapshot = 2,

    /// <summary>변화 시 + 5초마다.</summary>
    ZoneStates = 3,

    /// <summary>2틱. <c>MirrorLog</c> 의 명령 줄.</summary>
    CommandLog = 4,

    /// <summary>2틱. <c>MirrorLog</c> 의 이벤트 줄.</summary>
    EventLog = 5,

    /// <summary>1초. NPC 서버 링크의 상태.</summary>
    LinkStatus = 6,

    /// <summary><see cref="Ping"/> 응답.</summary>
    Pong = 7,

    // ── 클라이언트 → 서버 (64~127) ──────────────────────────────

    /// <summary>접속 1회. <b>문자열이 없다</b> (docs/20 §3.5).</summary>
    CliHello = 64,

    /// <summary>10Hz. 키가 눌린 동안만.</summary>
    Input = 65,

    /// <summary>대화. 거리 ≤ 30m.</summary>
    Interact = 66,

    /// <summary>공격. 거리 ≤ 30m.</summary>
    Attack = 67,

    /// <summary>로그 필터 + 인스펙터 대상.</summary>
    Select = 68,

    /// <summary>시나리오 제어. docs/20 §8.3.</summary>
    Control = 69,

    /// <summary>1초.</summary>
    Ping = 70,
}

/// <summary>
/// 프레임 코덱. <b><see cref="FrameCodec"/> 를 그대로 쓴다</b> — <c>Kind</c> 만 바꿔 끼운다.
/// docs/20 §8.
///
/// <para>
/// 헤더 8바이트·버전 검사·1MiB 상한이 전부 저쪽 것이다. 길이 접두 프로토콜에서
/// 그 검사를 두 벌로 만들면 한쪽만 고쳐지는 날이 오고, 그날 잘못된 4바이트 하나로
/// 프로세스가 죽는다 (docs/20 §5.1).
/// </para>
/// </summary>
public static class ClientProtocol
{
    /// <summary>
    /// 클라이언트 프로토콜 버전. <see cref="SrvHello"/>·<see cref="CliHello"/> 에 실린다.
    ///
    /// <b>프레임 헤더의 <see cref="FrameCodec.Version"/> 과 다른 것이다.</b> 저쪽은 "프레임을
    /// 어떻게 자르는가", 이쪽은 "그 안에 무엇이 들었는가" 다. 헤더 모양을 그대로 둔 채
    /// 메시지를 늘릴 수 있어야 하므로 둘을 묶지 않는다.
    /// </summary>
    public const int Version = 1;

    /// <summary>서버 → 클라이언트 Kind 의 상한. 이 값 이하가 서버 쪽이다.</summary>
    public const byte MaxServerKind = 63;

    /// <summary>
    /// 프레임 헤더 크기(바이트). <see cref="FrameCodec.HeaderSize"/> 를 그대로 다시 내놓는다.
    ///
    /// <b>여기 있는 이유는 의존 방향이다.</b> 클라이언트가 프레임을 만들려면 이 값이 필요한데,
    /// 그것 하나 때문에 <c>Npc.Wire</c> 를 알아야 한다면 이 프로젝트를 따로 둔 뜻이 없어진다
    /// (docs/20 §4). 값의 출처는 여전히 하나다.
    /// </summary>
    public const int HeaderSize = FrameCodec.HeaderSize;

    /// <summary>헤더를 쓴다. 페이로드는 호출부가 이어서 쓴다.</summary>
    public static void WriteHeader(IBufferWriter<byte> writer, ClientMessageKind kind, int payloadLength) =>
        FrameCodec.WriteHeader(writer, (LinkMessageKind)(byte)kind, payloadLength);

    /// <summary>
    /// 완전한 프레임 하나를 떼어낸다. 아직 다 안 왔으면 false 이고 버퍼는 그대로다.
    /// </summary>
    /// <exception cref="InvalidDataException">버전이 다르거나 길이가 상한을 넘는다.</exception>
    public static bool TryReadFrame(
        ref ReadOnlySequence<byte> buffer,
        out ClientMessageKind kind,
        out ReadOnlySequence<byte> payload)
    {
        bool complete = FrameCodec.TryReadFrame(ref buffer, out LinkMessageKind raw, out payload);

        kind = (ClientMessageKind)(byte)raw;

        return complete;
    }

    /// <summary>이 종류가 서버 → 클라이언트인가. 로그에 방향을 찍을 때 쓴다.</summary>
    public static bool IsServerBound(ClientMessageKind kind) =>
        kind != ClientMessageKind.None && (byte)kind <= MaxServerKind;
}
