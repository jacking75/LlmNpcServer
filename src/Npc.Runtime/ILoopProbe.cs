namespace Npc.Runtime;

/// <summary>
/// 틱 루프 생존 신호. A-03 헬스체크의 liveness 근거다.
///
/// <b>이것은 게임 로직이 아니라 호스트 계측이다.</b> 루프는 "한 번 더 돌았다" 만 알리고,
/// 그것을 벽시계로 환산하는 것은 호스트다 (CLAUDE.md §2.3 — 게임 로직에 벽시계 금지).
/// 구현체는 <c>Volatile.Write</c> 한 번이어야 한다 — 틱 루프에서 불리므로 할당이 있으면 안 된다.
/// </summary>
public interface ILoopProbe
{
    /// <summary>루프가 한 바퀴 돌았다. 틱마다가 아니라 <b>이벤트 대기에서 깨어날 때마다</b> 불린다.</summary>
    void Beat();
}
