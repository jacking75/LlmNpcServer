using System.Threading.Channels;
using Npc.Contracts;

namespace Npc.Gateway;

/// <summary>
/// <b>[범위 밖] 실제 게임서버 TCP 링크.</b> docs/02 §2, §7.
///
/// 이 클래스는 <b>구현하지 않는다.</b> 인터페이스가 네트워크 전환에 견디는지 확인하기 위한 골격이다.
/// 컴파일되고, 모든 전송 메서드가 <see cref="NotSupportedException"/> 을 던진다.
///
/// 구현 시 필요한 것 (docs/02 §7):
/// <list type="number">
///   <item><b>프레이밍</b> — 길이 접두 4바이트 + payload. 배치는 <c>[count][cmd][cmd]…</c></item>
///   <item><b>직렬화</b> — MemoryPack 또는 소스 생성 기반. 리플렉션 금지.
///         패킷이 전부 값 타입이라 <c>MemoryMarshal</c> 로 blittable 처리도 가능하다 (N2).</item>
///   <item><b>재접속 + 시퀀스 재동기화</b> — <see cref="LinkState.Degraded"/> 동안 명령은
///         링 버퍼에 쌓고, 재연결 시 시퀀스를 맞춘다 (N6).</item>
///   <item><b>Nagle 비활성 + 배치 코얼레싱</b> — 5,000 NPC × 초당 명령을 단건으로 보내면 죽는다 (N8).</item>
///   <item><b>흐름 제어</b> — 게임서버가 처리 못 하면 링크가 역압을 올린다.
///         지금 <c>BoundedChannel</c> 이 있는 자리에 그대로 들어간다.</item>
/// </list>
///
/// <b>이 5가지 중 NPC 서버 코드를 건드려야 하는 것은 없다.</b> 그것이 이번 인터페이스 설계의 검증 기준이다.
/// </summary>
public sealed class TcpGameServerLink : IGameServerLink
{
    private readonly Channel<GameEvent> _events = Channel.CreateUnbounded<GameEvent>();

    /// <summary>범위 밖. 구현되지 않았다.</summary>
    public ChannelReader<GameEvent> Events => _events.Reader;

    /// <summary>연결되지 않은 상태로 고정.</summary>
    public LinkState State => LinkState.Disconnected;

    /// <summary>전부 0.</summary>
    public LinkStats Stats => default;

    /// <summary>범위 밖. 발생하지 않는다.</summary>
    public event Action<LinkState>? StateChanged;

    /// <summary>범위 밖.</summary>
    public void Enqueue(in NpcCommand command) =>
        throw new NotSupportedException("TODO: 범위 밖 — docs/02 §7 의 5항목을 먼저 구현해야 한다.");

    /// <summary>범위 밖.</summary>
    public ValueTask FlushAsync(CancellationToken ct) =>
        throw new NotSupportedException("TODO: 범위 밖 — docs/02 §7 의 5항목을 먼저 구현해야 한다.");

    /// <summary>범위 밖.</summary>
    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        StateChanged?.Invoke(LinkState.Disconnected);
        return ValueTask.CompletedTask;
    }
}
