using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;

namespace Npc.Memory;

/// <summary>
/// NPC 서버가 기억 저장소에 하는 일 전부 (D-03). <b>읽기만 있다.</b>
///
/// <para>
/// <b>쓰기 주체는 우리가 아니다.</b> 무슨 일이 있었는지 아는 것은 게임서버(거래·퀘스트·전투)와
/// 대화 서비스(D-01)다. NPC 서버가 쓰기 시작하면 같은 사실을 두 곳이 기록하게 되고,
/// 두 기록이 어긋나는 날이 온다.
/// </para>
///
/// <para>
/// <b>틱 루프에서 부르지 않는다.</b> 전부 <c>async</c> 이고 호출자는 재계획 워커다 —
/// <c>Npc.Runtime</c> 이 이 프로젝트를 참조하지 않는 것이 그 규칙의 강제 수단이다.
/// </para>
/// </summary>
public interface IMemoryReader
{
    /// <summary>
    /// 이 NPC 가 이 플레이어를 어떻게 보는가. 기록이 없으면 <see cref="RelationshipBand.Unknown"/>.
    ///
    /// <b>밴드만 돌려준다.</b> 호감도 원값을 서픽스 조립기에 흘리면 언젠가 그것이 실린다.
    /// </summary>
    /// <param name="npc">NPC 전역 id.</param>
    /// <param name="player">플레이어 id.</param>
    /// <param name="ct">취소 토큰.</param>
    ValueTask<RelationshipBand> BandAsync(int npc, int player, CancellationToken ct = default);

    /// <summary>관계 한 줄. 없으면 null.</summary>
    /// <param name="npc">NPC 전역 id.</param>
    /// <param name="player">플레이어 id.</param>
    /// <param name="ct">취소 토큰.</param>
    ValueTask<Relationship?> RelationshipAsync(int npc, int player, CancellationToken ct = default);

    /// <summary>이 NPC 의 기억. <b>salience 내림차순, 동점이면 최신순</b>이다.</summary>
    /// <param name="npc">NPC 전역 id.</param>
    /// <param name="limit">최대 개수.</param>
    /// <param name="ct">취소 토큰.</param>
    ValueTask<ImmutableArray<EpisodeSummary>> EpisodesAsync(int npc, int limit, CancellationToken ct = default);

    /// <summary>세력이 플레이어를 보는 점수. 없으면 null.</summary>
    /// <param name="faction">세력 code.</param>
    /// <param name="player">플레이어 id.</param>
    /// <param name="ct">취소 토큰.</param>
    ValueTask<Reputation?> ReputationAsync(FactionId faction, int player, CancellationToken ct = default);
}

/// <summary>
/// 쓰기까지 하는 저장소 (D-03). <b>게임서버·대화 서비스·도구가 이쪽을 든다.</b>
///
/// <para>
/// NPC 서버 호스트는 <see cref="IMemoryReader"/> 로만 주입받는다 — 타입이 곧 권한이다.
/// </para>
/// </summary>
public interface IMemoryStore : IMemoryReader
{
    /// <summary>관계를 얹는다. 없으면 만든다.</summary>
    /// <param name="npc">NPC 전역 id.</param>
    /// <param name="player">플레이어 id.</param>
    /// <param name="delta">호감도 증감.</param>
    /// <param name="tick">지금 틱.</param>
    /// <param name="tags">이번에 세울 비트.</param>
    /// <param name="ct">취소 토큰.</param>
    ValueTask<Relationship> RecordInteractionAsync(
        int npc,
        int player,
        int delta,
        Tick tick,
        RelationshipTags tags = RelationshipTags.None,
        CancellationToken ct = default);

    /// <summary>
    /// 기억 한 줄을 붙인다. <see cref="EpisodeSummary.Sequence"/> 는 저장소가 매긴다.
    ///
    /// <para><b><paramref name="summaryKey"/> 는 로컬라이즈 키다</b> — 자연어를 넣지 않는다.</para>
    /// </summary>
    /// <param name="npc">NPC 전역 id.</param>
    /// <param name="kind">종류.</param>
    /// <param name="subject">상대.</param>
    /// <param name="subjectIsPlayer">상대가 플레이어인가.</param>
    /// <param name="tick">지금 틱.</param>
    /// <param name="salience">중요도.</param>
    /// <param name="summaryKey">로컬라이즈 키.</param>
    /// <param name="ct">취소 토큰.</param>
    ValueTask<EpisodeSummary> AppendEpisodeAsync(
        int npc,
        EpisodeKind kind,
        int subject,
        bool subjectIsPlayer,
        Tick tick,
        byte salience,
        string summaryKey,
        CancellationToken ct = default);

    /// <summary>세력 평판을 얹는다.</summary>
    /// <param name="faction">세력 code.</param>
    /// <param name="player">플레이어 id.</param>
    /// <param name="delta">점수 증감.</param>
    /// <param name="tick">지금 틱.</param>
    /// <param name="ct">취소 토큰.</param>
    ValueTask<Reputation> RecordReputationAsync(
        FactionId faction, int player, int delta, Tick tick, CancellationToken ct = default);

    /// <summary>
    /// 이 플레이어에 관한 모든 기록을 지운다 (개인정보 요건).
    ///
    /// <para>
    /// <b>탈퇴 처리의 종착지다.</b> 관계·기억·평판 셋 다 지운다 — 하나라도 남으면
    /// "지웠다" 고 말할 수 없다. 지운 줄 수를 돌려주는 이유는 그 수가 감사 기록이 되기 때문이다.
    /// </para>
    /// </summary>
    /// <param name="player">플레이어 id.</param>
    /// <param name="ct">취소 토큰.</param>
    ValueTask<int> ForgetPlayerAsync(int player, CancellationToken ct = default);

    /// <summary>
    /// 오래된 기록을 지운다. <b>기준은 벽시계가 아니라 틱이다</b> (CLAUDE.md §2.3).
    /// </summary>
    /// <param name="olderThanTick">이 틱보다 오래된 것을 지운다.</param>
    /// <param name="ct">취소 토큰.</param>
    /// <returns>지운 줄 수.</returns>
    ValueTask<int> PruneAsync(Tick olderThanTick, CancellationToken ct = default);

    /// <summary>디스크에 확정한다. 메모리 구현에서는 아무것도 하지 않는다.</summary>
    /// <param name="ct">취소 토큰.</param>
    ValueTask FlushAsync(CancellationToken ct = default);
}
