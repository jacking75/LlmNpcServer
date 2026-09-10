using Npc.Wire;

namespace Npc.Gateway;

/// <summary>
/// TCP 링크 설정. docs/20 §5.5 · §6.3 · §7.5.
///
/// <para>
/// <b>핸드셰이크에서 검증할 값 넷이 여기 들어 있다</b> — 프로토콜 버전 · 타임스케일 ·
/// 마스터데이터 해시 · 로스터 해시. 게임서버가 보낸 <see cref="WireHello"/> 와 하나라도 다르면
/// 거절하고 <see cref="Npc.Contracts.LinkState.Faulted"/> 로 간다.
/// </para>
///
/// <para>
/// <b>우회 옵션(<c>--force</c> 류)을 만들지 않는다</b> (docs/20 §5.5).
/// 마스터데이터가 다른 두 프로세스를 붙이면 POI code 가 어긋나 NPC 가 엉뚱한 곳으로 가고,
/// 원인을 찾는 데 하루가 든다. <b>연결 시점에 죽이는 편이 싸다.</b>
/// </para>
/// </summary>
public sealed record TcpLinkOptions
{
    /// <summary>게임서버 호스트.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>게임서버 포트. docs/20 §7.5 의 기본값.</summary>
    public int Port { get; init; } = 7010;

    /// <summary>내 타임스케일. <c>GameClock.TimeScale</c> 이다.</summary>
    public int TimeScale { get; init; }

    /// <summary>내가 들고 있는 NPC 수.</summary>
    public int NpcCount { get; init; }

    /// <summary>내 마스터데이터 콘텐츠 해시.</summary>
    public WireHash MasterData { get; init; }

    /// <summary>
    /// 내 마스터데이터 <b>구조</b> 해시 (B-04). id·code·bit·좌표 집합.
    ///
    /// 비어 있으면 <see cref="MasterData"/> 를 쓴다 — v1 회차와 같은 동작이다.
    /// <b>불일치는 거절이다.</b> 게임서버가 POI 좌표를 다르게 알면 NPC 가 엉뚱한 곳으로 간다.
    /// </summary>
    public WireHash MasterDataStructural { get; init; }

    /// <summary>
    /// 내 마스터데이터 <b>내용</b> 해시 (B-04). desc·traits·인터럽트 등 나머지.
    ///
    /// <b>불일치는 경고 후 수락이다.</b> 게임서버와 NPC 서버가 다른 파이프라인으로 배포되는
    /// 상용에서 한쪽이 밸런스를 먼저 받은 상태는 정상 운영의 일부다.
    /// </summary>
    public WireHash MasterDataContent { get; init; }

    /// <summary>
    /// 인증을 반드시 요구하는가 (A-06). 기본은 요구하지 않는다 — v1 게임서버와 붙어야 한다.
    ///
    /// 켜면 게임서버가 <c>Auth</c> 기능을 안 켠 회차를 <c>AuthFailed</c> 로 거절한다.
    /// </summary>
    public bool RequireAuth { get; init; }

    /// <summary>
    /// 링크 HMAC 비밀 (A-06). 32바이트. 비어 있으면 인증하지 않는다.
    ///
    /// 값은 환경변수 <c>NPC_LINK_SECRET</c> 에서만 온다 — 파일에도 인자에도 두지 않는다.
    /// 인자는 <c>ps</c> 에 보이고 파일은 이미지에 굽힌다.
    /// </summary>
    public byte[] Secret { get; init; } = [];

    /// <summary>링크 암호화 모드 (A-06). 기본 <see cref="LinkTlsMode.Off"/>.</summary>
    public LinkTlsMode Tls { get; init; } = LinkTlsMode.Off;

    /// <summary>TLS SNI 이름. null 이면 <see cref="Host"/> 를 쓴다.</summary>
    public string? TlsHost { get; init; }

    /// <summary>클라이언트 인증서(pfx) 경로. <c>mtls</c> 에서만 쓴다.</summary>
    public string? ClientCertificatePath { get; init; }

    /// <summary>클라이언트 인증서 비밀번호. 환경변수 <c>NPC_LINK_CERT_PASSWORD</c> 에서 온다.</summary>
    public string? ClientCertificatePassword { get; init; }

    /// <summary>내 NPC 로스터 해시 (docs/20 §10.2).</summary>
    public WireHash Roster { get; init; }

    /// <summary>명령 링 용량.</summary>
    public int Capacity { get; init; } = PriorityCommandRing.DefaultCapacity;

    /// <summary>
    /// 센더에게 넘길 배치 슬롯 수. 슬롯마다 <see cref="Capacity"/> 만큼의 명령 배열을
    /// 기동 시 잡으므로 크게 잡으면 메모리를 그만큼 먹는다.
    ///
    /// <b>4 면 0.4초치다</b> (10Hz 기준). 센더가 그보다 오래 밀리면 배치를 버리는 편이 낫다 —
    /// 쌓아 두면 지연만 늘고 그 명령은 이미 낡았다.
    /// </summary>
    public int OutboundBatches { get; init; } = 4;

    /// <summary>
    /// 핸드셰이크 상한. 게임서버가 <see cref="WireHello"/> 를 이 안에 보내지 않으면 실패로 본다.
    /// <b>무한 대기하지 않는다</b> — 그러면 기동이 조용히 멈춘다.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <b>NPC 수를 핸드셰이크에서 대조할지.</b> 기본은 대조하지 않는다 —
    /// 로스터 해시가 이미 그 집합을 담고 있고(docs/20 §10.2), <c>--npcs</c> 로 일부만 돌리는
    /// 측정 회차에서 수만 다른 것은 정상이다. 로스터 해시가 같으면 같은 NPC 를 보고 있는 것이다.
    /// </summary>
    public bool StrictNpcCount { get; init; }

    /// <summary>하트비트 주기. docs/20 §5.6 의 1초.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>이만큼 아무것도 못 받으면 <c>Degraded</c> 다. docs/20 §5.6 의 3초.</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>재접속 백오프 첫 값. docs/20 §5.6 — 250ms → 500 → 1s → 2s → 4s.</summary>
    public TimeSpan ReconnectBackoff { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>재접속 백오프 상한.</summary>
    public TimeSpan MaxReconnectBackoff { get; init; } = TimeSpan.FromSeconds(4);
}
