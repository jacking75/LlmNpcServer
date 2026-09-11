using Npc.Contracts;

namespace Npc.Core.Plan;

/// <summary>
/// 플랜이 POI 를 가리키는 유일한 방법. docs/03 §2.
///
/// <b>절대 POI id(<c>smithy_01</c>)를 쓰지 않는다.</b> 플랜은 (아키타입 × 버킷) 단위로 재사용되므로
/// 개체에 묶이면 캐시가 무의미해진다. 개체별 바인딩은 런타임(<c>PoiBinder</c>)이 한다.
/// </summary>
public enum PoiSymbol : byte
{
    /// <summary>POI 인자가 없는 스텝.</summary>
    None = 0,

    /// <summary>NPC 인스턴스의 home_poi.</summary>
    Home = 1,

    /// <summary>NPC 인스턴스의 workplace_poi.</summary>
    Workplace = 2,

    /// <summary>같은 존에서 가장 가까운 market.</summary>
    Market = 3,

    /// <summary>같은 존에서 가장 가까운 tavern.</summary>
    Tavern = 4,

    /// <summary>같은 존에서 가장 가까운 temple.</summary>
    Temple = 5,

    /// <summary>같은 존에서 가장 가까운 gate.</summary>
    Gate = 6,

    /// <summary>아키타입이 접근 가능한 가장 가까운 field.</summary>
    NearestField = 7,

    /// <summary>가장 가까운 안전 지점 (gate 또는 home).</summary>
    NearestSafe = 8,

    /// <summary>실내로 판정되는 가장 가까운 POI.</summary>
    NearestShelter = 9,

    /// <summary>
    /// 이 NPC 의 순찰 지점 (D-04). <c>npc_overrides.json</c> 의 <c>patrol_route</c> 다.
    ///
    /// <para>
    /// <b>버킷 플랜은 수천 NPC 가 공유한다.</b> 그래서 플랜에는 순찰로가 아니라 이 심볼만 있고,
    /// 개체 차이는 바인딩에서 난다 — 프롬프트 서픽스 300토큰 예산과 무관하다 (CLAUDE.md §2.5).
    /// </para>
    ///
    /// <para>순찰로가 없는 NPC 는 일터로, 일터도 없으면 집으로 떨어진다.</para>
    /// </summary>
    PatrolRoute = 10,
}

/// <summary>POI 심볼의 문자열 표기와 파싱. docs/03 §2 의 허용 목록.</summary>
public static class PoiSymbols
{
    /// <summary><see cref="PoiSymbol"/> 첨자별 문자열 표기. None 은 빈 문자열.</summary>
    public static readonly string[] Names =
    [
        "",
        "$home",
        "$workplace",
        "$market",
        "$tavern",
        "$temple",
        "$gate",
        "$nearest_field",
        "$nearest_safe",
        "$nearest_shelter",
        "$patrol_route",
    ];

    /// <summary>정의된 심볼 수 (None 제외).</summary>
    public const int Count = 10;

    /// <summary>
    /// 플랜 DSL 의 문자열을 심볼로. 허용 목록에 없으면 false —
    /// 검증기 2단의 <c>V2.UNKNOWN_POI</c> 가 이 결과를 쓴다.
    /// </summary>
    public static bool TryParse(string? text, out PoiSymbol symbol)
    {
        switch (text)
        {
            case "$home": symbol = PoiSymbol.Home; return true;
            case "$workplace": symbol = PoiSymbol.Workplace; return true;
            case "$market": symbol = PoiSymbol.Market; return true;
            case "$tavern": symbol = PoiSymbol.Tavern; return true;
            case "$temple": symbol = PoiSymbol.Temple; return true;
            case "$gate": symbol = PoiSymbol.Gate; return true;
            case "$nearest_field": symbol = PoiSymbol.NearestField; return true;
            case "$nearest_safe": symbol = PoiSymbol.NearestSafe; return true;
            case "$nearest_shelter": symbol = PoiSymbol.NearestShelter; return true;
            case "$patrol_route": symbol = PoiSymbol.PatrolRoute; return true;
            default: symbol = PoiSymbol.None; return false;
        }
    }

    /// <summary>심볼의 문자열 표기.</summary>
    public static string ToText(PoiSymbol symbol) =>
        (uint)symbol < (uint)Names.Length ? Names[(int)symbol] : string.Empty;
}

/// <summary>
/// 결정론 해시. CLAUDE.md §2.3 — 게임 로직에 시드 없는 <c>Random</c> 을 쓰지 않는다.
/// 지터·분산은 전부 <c>(npcId, ...)</c> 해시로 만든다. 그래야 리플레이가 100% 일치한다.
/// </summary>
public static class PlanHash
{
    /// <summary>32비트 혼합 (SplitMix32). 같은 입력이면 언제나 같은 값.</summary>
    public static uint Mix(uint value)
    {
        value += 0x9E37_79B9u;
        value = (value ^ (value >> 16)) * 0x21F0_AAADu;
        value = (value ^ (value >> 15)) * 0x735A_2D97u;
        return value ^ (value >> 15);
    }

    /// <summary>두 값을 섞는다.</summary>
    public static uint Mix(int a, int b) => Mix(unchecked((uint)a * 0x85EB_CA6Bu) ^ Mix(unchecked((uint)b)));

    /// <summary>NPC 별 결정론 지터. <c>[-span, +span]</c> 범위의 정수를 준다.</summary>
    public static int Jitter(NpcId npc, int salt, int span)
    {
        if (span <= 0)
        {
            return 0;
        }

        int period = (span * 2) + 1;
        return (int)(Mix(npc.Value, salt) % (uint)period) - span;
    }
}
