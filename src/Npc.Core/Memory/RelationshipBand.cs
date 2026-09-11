namespace Npc.Core;

/// <summary>
/// 플레이어와의 관계를 3단으로 압축한 값 (D-03).
///
/// <para>
/// <b>서픽스에 들어가는 것은 이 enum 하나다.</b> 호감도 원값(-100~100)을 실으면 모델이
/// 그 숫자를 플랜에 되쓰려 하고, 상호작용 횟수·마지막 시각까지 실으면 300토큰 예산이
/// 금방 없어진다 (CLAUDE.md §2.5). 3단이면 4토큰이다.
/// </para>
///
/// <para>
/// <b><see cref="Npc.Core"/> 에 두는 이유.</b> 밴드를 읽는 쪽은 <c>Npc.Llm</c>(서픽스)이고
/// 만드는 쪽은 <c>Npc.Memory</c>(저장소)다. 둘 중 하나에 두면 다른 하나가 그것을 참조해야 하고,
/// 그러면 CLAUDE.md §3 의 의존 그래프에 없는 간선이 생긴다.
/// </para>
/// </summary>
public enum RelationshipBand : byte
{
    /// <summary>기록이 없다. <b>서픽스에 싣지 않는다</b> — "모른다" 를 적는 데 토큰을 쓰지 않는다.</summary>
    Unknown = 0,

    /// <summary>적대.</summary>
    Hostile = 1,

    /// <summary>중립. 기록은 있지만 어느 쪽도 아니다.</summary>
    Neutral = 2,

    /// <summary>우호.</summary>
    Friendly = 3,
}

/// <summary>호감도 → 밴드. <b>경계값은 한 곳에서만 정한다</b> (D-03).</summary>
public static class RelationshipBands
{
    /// <summary>이 값 이하면 <see cref="RelationshipBand.Hostile"/>.</summary>
    public const int HostileAtOrBelow = -25;

    /// <summary>이 값 이상이면 <see cref="RelationshipBand.Friendly"/>.</summary>
    public const int FriendlyAtOrAbove = 25;

    /// <summary>호감도를 밴드로. 기록이 없을 때는 부르지 않는다 — 그때는 <c>Unknown</c> 이다.</summary>
    /// <param name="affinity">-100~100.</param>
    public static RelationshipBand Of(int affinity) => affinity switch
    {
        <= HostileAtOrBelow => RelationshipBand.Hostile,
        >= FriendlyAtOrAbove => RelationshipBand.Friendly,
        _ => RelationshipBand.Neutral,
    };

    /// <summary>서픽스에 쓰는 소문자 표기. <see cref="RelationshipBand.Unknown"/> 은 빈 문자열.</summary>
    /// <param name="band">밴드.</param>
    public static string ToText(RelationshipBand band) => band switch
    {
        RelationshipBand.Hostile => "hostile",
        RelationshipBand.Neutral => "neutral",
        RelationshipBand.Friendly => "friendly",
        _ => string.Empty,
    };
}
