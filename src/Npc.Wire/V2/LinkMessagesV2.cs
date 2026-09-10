using MemoryPack;

namespace Npc.Wire.V2;

/// <summary>
/// v2 핸드셰이크. 게임서버 → NPC 서버 (B-01).
///
/// <b>v1 과 다른 점은 협상이 있다는 것 하나다.</b> v1 은 <c>ProtocolVersion</c> 이 다르면
/// 즉시 절단이었다. v2 는 게임서버가 받아 줄 <b>범위</b>를 보내고, NPC 서버가 교집합의
/// 최댓값을 골라 <see cref="WireHelloAckV2.ProtocolVersion"/> 으로 돌려준다.
/// 교집합이 비면 그때 끊는다.
///
/// <para>
/// <b>필드를 미리 다 뚫어 둔다.</b> 인증(A-06)·샤드(A-08)·게임 시각(A-10)·해시 분할(B-04)·
/// 세션 에포크(G-02)가 쓸 자리를 지금 잡는다. MemoryPack 은 선언 순서로 직렬화하므로
/// 나중에 하나씩 끼워 넣으면 그때마다 레이아웃이 바뀌고, 그것을 다섯 번 동결하는 것은
/// 동결이 아니다. <b>쓰지 않는 필드는 0 이고, 0 이 "그 기능 없음" 을 뜻한다.</b>
/// </para>
///
/// N3 준수: 문자열이 없다. 해시·nonce·인증은 전부 고정 길이 값 타입이다.
/// N4 준수: 시간은 <c>Tick</c>(long)과 분 단위 정수뿐이다.
/// </summary>
[MemoryPackable]
public partial struct WireHelloV2
{
    /// <summary>게임서버가 쓰고 싶은 최대 프로토콜 버전.</summary>
    public int ProtocolVersion;

    /// <summary>게임서버가 받아 줄 최소 프로토콜 버전. 협상 범위의 아래쪽이다.</summary>
    public int MinProtocolVersion;

    /// <summary>게임서버가 구현한 계약 주 버전. 다르면 <c>ContractMismatch</c> 다.</summary>
    public ushort ContractMajor;

    /// <summary>게임서버가 구현한 계약 부 버전. 낮은 쪽 기준으로 돈다.</summary>
    public ushort ContractMinor;

    /// <summary>게임서버가 지원하는 기능 비트. <c>Npc.Contracts.LinkFeatures</c>.</summary>
    public ulong Features;

    /// <summary>틱 레이트(Hz). 10 이다.</summary>
    public int TickRate;

    /// <summary>게임서버의 타임스케일. NPC 서버와 같아야 한다.</summary>
    public int TimeScale;

    /// <summary>NPC 수. 로스터 크기와 같아야 한다.</summary>
    public int NpcCount;

    /// <summary>게임서버의 현재 틱 (N4).</summary>
    public long StartTick;

    /// <summary>
    /// 게임 안 하루의 몇 분째인가 (0~1439). A-10.
    ///
    /// <b>재기동 시 게임 시각이 새벽 6시로 돌아가는 것을 막는다.</b> 0 이면 "모른다" 가 아니라
    /// 자정이므로, 게임서버가 이 값을 안 준다는 것은 <c>Features</c> 로 표현한다.
    /// </summary>
    public ushort StartGameMinuteOfDay;

    /// <summary>샤드 식별자 (A-08). 0 = 단일 샤드.</summary>
    public ushort ShardId;

    /// <summary>이 샤드가 맡는 존 비트마스크 (A-08). 0 = 전체.</summary>
    public ulong ZoneMask;

    /// <summary>
    /// 세션 에포크 (G-02). 게임서버 프로세스가 다시 뜨면 증가한다.
    ///
    /// <b>시퀀스 리셋을 감지하는 유일한 수단이다.</b> 에포크가 그대로인데 시퀀스가 작아지면
    /// 그것은 규약 위반이지만, 에포크가 바뀌었으면 정상적인 재기동이므로 기준을 새로 잡는다.
    /// </summary>
    public uint SessionEpoch;

    /// <summary>
    /// 마스터데이터 <b>구조</b> 해시 (B-04). id·code·bit·좌표 집합.
    /// 불일치는 거절이다 — 게임서버가 POI 좌표를 다르게 알면 위험하다.
    /// </summary>
    public WireHash MasterDataStructural;

    /// <summary>
    /// 마스터데이터 <b>내용</b> 해시 (B-04). desc·traits·인터럽트 등 나머지.
    /// 불일치는 경고 후 수락이다 — 한쪽이 밸런스를 먼저 받은 정상 상태다.
    /// </summary>
    public WireHash MasterDataContent;

    /// <summary>NPC 로스터 해시.</summary>
    public WireHash Roster;

    /// <summary>인증 nonce (A-06). 게임서버가 OS 난수로 만든다. 0 이면 인증 없음.</summary>
    public WireNonce Nonce;

    /// <summary>HMAC-SHA256 인증 태그 (A-06). 0 이면 인증 없음.</summary>
    public WireHash Auth;
}

/// <summary>
/// v2 핸드셰이크 응답. NPC 서버 → 게임서버 (B-01).
///
/// <see cref="ProtocolVersion"/> 이 <b>협상 결과</b>다 — 이후 모든 프레임의 <c>Ver</c> 가 이 값이다.
/// </summary>
[MemoryPackable]
public partial struct WireHelloAckV2
{
    /// <summary>협상된 프로토콜 버전. 이후 프레임의 <c>Ver</c> 다.</summary>
    public int ProtocolVersion;

    /// <summary>NPC 서버의 계약 주 버전.</summary>
    public ushort ContractMajor;

    /// <summary>협상된 계약 부 버전 (양쪽의 낮은 값).</summary>
    public ushort ContractMinor;

    /// <summary>협상된 기능 비트 (양쪽의 교집합).</summary>
    public ulong Features;

    /// <summary>NPC 서버의 타임스케일.</summary>
    public int TimeScale;

    /// <summary>NPC 서버가 들고 있는 NPC 수.</summary>
    public int NpcCount;

    /// <summary>NPC 서버의 구조 해시.</summary>
    public WireHash MasterDataStructural;

    /// <summary>NPC 서버의 내용 해시. 게임서버와 달라도 수락한다 (B-04).</summary>
    public WireHash MasterDataContent;

    /// <summary>NPC 서버의 로스터 해시.</summary>
    public WireHash Roster;

    /// <summary>상호 인증 태그 (A-06). 0 이면 인증 없음.</summary>
    public WireHash Auth;

    /// <summary>수락했으면 1, 거절이면 0.</summary>
    public byte Accepted;

    /// <summary>거절 사유. <c>LinkRejectCode</c>.</summary>
    public byte RejectCode;

    /// <summary>내용 해시가 달라 경고로 수락했으면 1 (B-04).</summary>
    public byte ContentHashWarning;
}

/// <summary>
/// 인증 nonce 16바이트 (A-06).
///
/// <b>문자열이 아니다</b> (N3). <c>ulong</c> 둘이면 정확히 16바이트다.
/// </summary>
/// <param name="A">바이트 0~7.</param>
/// <param name="B">바이트 8~15.</param>
[MemoryPackable]
public readonly partial record struct WireNonce(ulong A, ulong B)
{
    /// <summary>0. "인증 없음" 을 뜻한다.</summary>
    public static WireNonce Zero => default;

    /// <summary>비어 있는가.</summary>
    public bool IsZero => A == 0 && B == 0;

    /// <summary>16바이트로 푼다. 빅엔디언.</summary>
    public void WriteTo(Span<byte> destination)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(destination, A);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(destination[8..], B);
    }

    /// <summary>16바이트에서 읽는다.</summary>
    public static WireNonce From(ReadOnlySpan<byte> source) => new(
        System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(source),
        System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(source[8..]));
}
