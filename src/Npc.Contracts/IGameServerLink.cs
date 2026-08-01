using System.Threading.Channels;

namespace Npc.Contracts;

/// <summary>링크의 접속 상태. docs/02 §2.</summary>
public enum LinkState
{
    Disconnected,
    Connecting,
    Connected,
    Degraded,
    Faulted,
}

/// <summary>링크 통계. 대시보드의 "링크" 패널이 이 값을 그린다. docs/11 §10.</summary>
public readonly record struct LinkStats(
    long CommandsEnqueued,
    long CommandsFlushed,
    long CommandsDropped,
    long EventsReceived,
    long EventGapsDetected,
    int PendingCommands);

/// <summary>
/// NPC 서버 ↔ 게임서버 사이의 유일한 경계. docs/02 §2.
///
/// 이것은 "게임서버의 인터페이스"가 아니라 <b>게임서버로 향하는 NPC 서버의 아웃바운드 포트</b>다.
/// 이름의 "GameServer" 는 상대방을 가리킨다.
/// 구현체는 인프로세스 루프백 / 널 / 기록·재생 데코레이터 / <b>TCP</b>(<c>Npc.Gateway</c>, P6).
/// TCP 링크는 이 파일을 <b>한 줄도 바꾸지 않고</b> 붙었다 — 그것이 P6 게이트 G6-1 이 잰 것이다
/// (docs/20 §1 · §5·§6).
///
/// N1: 명령은 fire-and-forget 이다. <b>반환값 있는 전송 메서드를 여기에 추가하지 않는다.</b>
///     결과는 <see cref="Events"/> 로만 돌아온다. RPC 왕복(await SendAndWait)을 만드는 순간
///     네트워크 RTT 가 틱 루프를 블록한다.
/// N8: 배치가 기본이다. 단건 전송은 배치 크기 1의 특수 케이스다 —
///     <see cref="Enqueue"/> 로 쌓고 <see cref="FlushAsync"/> 로 한 번에 내보낸다.
/// </summary>
public interface IGameServerLink : IAsyncDisposable
{
    /// <summary>
    /// NPC 서버 → 게임서버. 배치 큐에 쌓기만 한다. 반환값 없음 (N1).
    /// 역압으로 드롭될 수 있다 — 드롭은 <see cref="Stats"/>.CommandsDropped 로만 관측한다.
    /// </summary>
    void Enqueue(in NpcCommand command);

    /// <summary>쌓인 배치를 내보낸다. 틱 루프 안에서 유일하게 허용되는 await 대상이다 (CLAUDE.md §2.1).</summary>
    ValueTask FlushAsync(CancellationToken ct);

    /// <summary>게임서버 → NPC 서버.</summary>
    ChannelReader<GameEvent> Events { get; }

    /// <summary>현재 접속 상태.</summary>
    LinkState State { get; }

    /// <summary>누적 통계.</summary>
    LinkStats Stats { get; }

    /// <summary>접속 상태 전이 통보.</summary>
    event Action<LinkState>? StateChanged;
}
