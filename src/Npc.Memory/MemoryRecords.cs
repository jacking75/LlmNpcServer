using Npc.Contracts;
using Npc.Core;

namespace Npc.Memory;

/// <summary>
/// 관계에 붙는 사실 비트 (D-03). <b>자연어가 아니라 비트다</b> —
/// 프롬프트 인젝션과 개인정보 두 문제를 동시에 피한다 (CLAUDE.md §2.5).
///
/// <para><b>비트 번호를 재배치하지 않는다.</b> 저장된 기록이 통째로 뜻을 바꾼다 (§2.4 와 같은 규칙).</para>
/// </summary>
[Flags]
public enum RelationshipTags : ulong
{
    /// <summary>없음.</summary>
    None = 0,

    /// <summary>거래한 적이 있다.</summary>
    Traded = 1UL << 0,

    /// <summary>도움을 받았다.</summary>
    Helped = 1UL << 1,

    /// <summary>공격당했다.</summary>
    Attacked = 1UL << 2,

    /// <summary>물건을 도둑맞았다.</summary>
    Robbed = 1UL << 3,

    /// <summary>퀘스트를 줬다.</summary>
    QuestGiven = 1UL << 4,

    /// <summary>퀘스트를 완료해 줬다.</summary>
    QuestCleared = 1UL << 5,

    /// <summary>대화한 적이 있다.</summary>
    Talked = 1UL << 6,
}

/// <summary>
/// 기억 한 줄의 종류 (D-03). <b>code 를 재배치하지 않는다.</b>
/// </summary>
public enum EpisodeKind : byte
{
    /// <summary>미지정.</summary>
    None = 0,

    /// <summary>거래.</summary>
    Trade = 1,

    /// <summary>전투.</summary>
    Combat = 2,

    /// <summary>대화.</summary>
    Dialogue = 3,

    /// <summary>퀘스트.</summary>
    Quest = 4,

    /// <summary>도움.</summary>
    Favor = 5,

    /// <summary>절도·기물 파손.</summary>
    Crime = 6,
}

/// <summary>
/// NPC 한 명과 플레이어 한 명의 관계 (D-03).
/// </summary>
/// <param name="Npc">NPC 전역 id (<c>npc_instances.json</c> 의 <c>id</c>).</param>
/// <param name="Player">플레이어 id.</param>
/// <param name="Affinity">
/// 호감도 -100~100. <b>서픽스에는 이 값이 아니라 <see cref="RelationshipBand"/> 가 실린다</b> —
/// 숫자를 실으면 모델이 그것을 플랜에 되쓰려 한다.
/// </param>
/// <param name="LastInteractionTick">마지막 상호작용 틱. <b>벽시계가 아니다</b> (CLAUDE.md §2.3).</param>
/// <param name="Interactions">상호작용 횟수. 65,535 에서 멈춘다 — 그 위는 세도 의미가 없다.</param>
/// <param name="Tags">무슨 일이 있었나. 비트다.</param>
public readonly record struct Relationship(
    int Npc,
    int Player,
    int Affinity,
    long LastInteractionTick,
    int Interactions,
    RelationshipTags Tags)
{
    /// <summary>호감도 상한.</summary>
    public const int MaxAffinity = 100;

    /// <summary>호감도 하한.</summary>
    public const int MinAffinity = -100;

    /// <summary><see cref="Interactions"/> 의 상한. <c>ushort</c> 로 저장한다.</summary>
    public const int MaxInteractions = ushort.MaxValue;

    /// <summary>3단 밴드. 서픽스에 실리는 것은 이것뿐이다.</summary>
    public RelationshipBand Band => RelationshipBands.Of(Affinity);

    /// <summary>
    /// 한 번의 상호작용을 얹는다. <b>범위는 여기서만 자른다</b> —
    /// 저장소마다 자르면 백엔드에 따라 다른 값이 남는다.
    /// </summary>
    /// <param name="delta">호감도 증감.</param>
    /// <param name="tick">지금 틱.</param>
    /// <param name="tags">이번에 세울 비트.</param>
    public Relationship Apply(int delta, Tick tick, RelationshipTags tags = RelationshipTags.None) => this with
    {
        Affinity = Math.Clamp(Affinity + delta, MinAffinity, MaxAffinity),
        LastInteractionTick = tick.Value,
        Interactions = Math.Min(Interactions + 1, MaxInteractions),
        Tags = Tags | tags,
    };
}

/// <summary>
/// 있었던 일 한 줄 (D-03).
///
/// <para>
/// <b>자연어 요약을 저장하지 않는다.</b> <see cref="SummaryKey"/> 는 로컬라이즈 키(D-02)이고
/// 문장은 표시 계층이 만든다 — 프롬프트 인젝션과 개인정보를 동시에 피하는 유일한 방법이다.
/// </para>
/// </summary>
/// <param name="Npc">NPC 전역 id.</param>
/// <param name="Sequence">이 NPC 안에서 순증. 정렬 키다.</param>
/// <param name="Kind">무슨 종류인가.</param>
/// <param name="Subject">상대 (플레이어 id 또는 NPC id — <see cref="SubjectIsPlayer"/> 가 가른다).</param>
/// <param name="SubjectIsPlayer">상대가 플레이어인가.</param>
/// <param name="Tick">언제. 벽시계가 아니다.</param>
/// <param name="Salience">중요도 0~255. 낮은 것부터 밀려난다.</param>
/// <param name="SummaryKey">로컬라이즈 키. <b>자연어가 아니다.</b></param>
public readonly record struct EpisodeSummary(
    int Npc,
    long Sequence,
    EpisodeKind Kind,
    int Subject,
    bool SubjectIsPlayer,
    long Tick,
    byte Salience,
    string SummaryKey);

/// <summary>
/// 세력 하나가 플레이어를 어떻게 보는가 (D-03).
/// </summary>
/// <param name="Faction">세력 code (D-04 <c>factions.json</c>).</param>
/// <param name="Player">플레이어 id.</param>
/// <param name="Score">평판 -100~100.</param>
/// <param name="UpdatedTick">마지막 갱신 틱.</param>
public readonly record struct Reputation(
    FactionId Faction,
    int Player,
    int Score,
    long UpdatedTick);
