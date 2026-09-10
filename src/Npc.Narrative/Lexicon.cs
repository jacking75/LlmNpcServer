using System.Collections.Frozen;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Narrative;

/// <summary>
/// id → 한국어 표기. docs/15 §6 의 서술은 사람이 읽는 것이라 id 를 그대로 쓸 수 없다.
///
/// <b>이것은 표시 계층이다.</b> 행동을 정하는 값(액션·플래그·아키타입 code)은 여전히
/// <c>masterdata/</c> 가 단일 원천이고(CLAUDE.md §2.4), 여기 있는 것은 그 id 를 어떻게
/// 읽어 줄지뿐이다. 마스터데이터에는 <c>name_key</c>(로컬라이즈 키)만 있고 실제 문구가 없다.
///
/// 빠진 id 가 생기면 <c>Lexicon_CoversEveryId</c> 가 깨진다 — 조용히 영어 id 가 새어 나가면
/// 블라인드 평가 자료의 두 군이 다르게 읽힐 수 있다.
/// </summary>
public static class Lexicon
{
    /// <summary>아키타입 표기. 추가하면 <c>Lexicon_CoversEveryId</c> 가 빠진 것을 알려 준다.</summary>
    public static readonly FrozenDictionary<string, string> Archetypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["blacksmith"] = "대장장이",
        ["carpenter"] = "목수",
        ["tailor"] = "재봉사",
        ["alchemist"] = "연금술사",
        ["baker"] = "제빵사",
        ["brewer"] = "양조사",
        ["jeweler"] = "보석세공사",
        ["tanner"] = "무두장이",
        ["mason"] = "석공",
        ["scribe"] = "필경사",
        ["miner"] = "광부",
        ["farmer"] = "농부",
        ["fisher"] = "어부",
        ["hunter"] = "사냥꾼",
        ["woodcutter"] = "나무꾼",
        ["herbalist"] = "약초꾼",
        ["shepherd"] = "목동",
        ["merchant"] = "상인",
        ["innkeeper"] = "여관주인",
        ["stablemaster"] = "마구간지기",
        ["banker"] = "은행가",
        ["peddler"] = "행상",
        ["guard_captain"] = "경비대장",
        ["town_guard"] = "위병",
        ["gate_guard"] = "성문지기",
        ["patrol_scout"] = "정찰병",
        ["watchman"] = "야경꾼",
        ["priest"] = "사제",
        ["acolyte"] = "수련사제",
        ["scholar"] = "학자",
        ["healer"] = "치유사",
        ["villager"] = "주민",
        ["child"] = "아이",
        ["elder"] = "노인",
        ["beggar"] = "걸인",
        ["drunkard"] = "주정뱅이",
        ["noble"] = "귀족",
        ["quest_giver"] = "의뢰인",
        ["wandering_bard"] = "떠돌이 음유시인",
        ["caravan_leader"] = "대상 우두머리",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>POI subtype 27종. 서술에는 개체 id 가 아니라 이 이름이 나간다.</summary>
    public static readonly FrozenDictionary<string, string> Places = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["house"] = "집",
        ["smithy"] = "대장간",
        ["carpenter_shop"] = "목공소",
        ["tailor_shop"] = "재봉소",
        ["alchemy_lab"] = "연금술 공방",
        ["bakery"] = "빵집",
        ["brewery"] = "양조장",
        ["jeweler_shop"] = "보석세공소",
        ["tannery"] = "무두질 작업장",
        ["masonry"] = "석공소",
        ["scriptorium"] = "필사실",
        ["infirmary"] = "치료소",
        ["mine"] = "광산",
        ["farm"] = "농장",
        ["fishing_spot"] = "낚시터",
        ["hunting_ground"] = "사냥터",
        ["logging_camp"] = "벌목장",
        ["herb_patch"] = "약초밭",
        ["pasture"] = "목초지",
        ["market_stall"] = "시장 좌판",
        ["tavern"] = "선술집",
        ["temple"] = "신전",
        ["wayshrine"] = "길가 사당",
        ["guardhouse"] = "위병소",
        ["gatehouse"] = "성문",
        ["stable"] = "마구간",
        ["bank"] = "은행",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>아이템 82종.</summary>
    public static readonly FrozenDictionary<string, string> Items = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["iron_ore"] = "철광석",
        ["coal"] = "석탄",
        ["copper_ore"] = "구리광석",
        ["silver_ore"] = "은광석",
        ["gold_ore"] = "금광석",
        ["gemstone_rough"] = "원석",
        ["stone"] = "돌",
        ["clay"] = "점토",
        ["timber"] = "원목",
        ["plank"] = "널빤지",
        ["wool"] = "양털",
        ["hide"] = "생가죽",
        ["leather"] = "가죽",
        ["flax"] = "아마",
        ["linen"] = "아마천",
        ["herb"] = "약초",
        ["mushroom"] = "버섯",
        ["wheat"] = "밀",
        ["flour"] = "밀가루",
        ["iron_sword"] = "철검",
        ["iron_tool"] = "철제 연장",
        ["horseshoe"] = "편자",
        ["wooden_chair"] = "나무 의자",
        ["cart_wheel"] = "수레바퀴",
        ["linen_shirt"] = "아마 셔츠",
        ["wool_cloak"] = "양모 망토",
        ["leather_belt"] = "가죽 허리띠",
        ["leather_boots"] = "가죽 장화",
        ["waterskin"] = "물통",
        ["healing_potion"] = "치유 물약",
        ["antidote"] = "해독제",
        ["tonic"] = "강장제",
        ["silver_ring"] = "은반지",
        ["gold_necklace"] = "금목걸이",
        ["gemstone_cut"] = "세공 보석",
        ["stone_block"] = "석재",
        ["brick"] = "벽돌",
        ["parchment"] = "양피지",
        ["book"] = "책",
        ["bread"] = "빵",
        ["water"] = "물",
        ["pastry"] = "과자",
        ["pie"] = "파이",
        ["cheese"] = "치즈",
        ["dried_meat"] = "말린 고기",
        ["vegetable"] = "채소",
        ["grape"] = "포도",
        ["cooked_fish"] = "구운 생선",
        ["stew"] = "스튜",
        ["apple"] = "사과",
        ["ale"] = "에일",
        ["wine"] = "포도주",
        ["cider"] = "사과주",
        ["milk"] = "우유",
        ["tea"] = "차",
        ["raw_fish"] = "생선",
        ["game_meat"] = "사냥감 고기",
        ["feather"] = "깃털",
        ["egg"] = "달걀",
        ["smith_hammer"] = "대장 망치",
        ["pickaxe"] = "곡괭이",
        ["hoe"] = "괭이",
        ["fishing_rod"] = "낚싯대",
        ["hunting_bow"] = "사냥활",
        ["axe"] = "도끼",
        ["saw"] = "톱",
        ["chisel"] = "끌",
        ["needle"] = "바늘",
        ["loom_shuttle"] = "북",
        ["mortar_pestle"] = "절구",
        ["quill"] = "깃펜",
        ["merchant_scale"] = "저울",
        ["lantern"] = "등불",
        ["rope"] = "밧줄",
        ["shepherd_crook"] = "목동 지팡이",
        ["trowel"] = "흙손",
        ["guard_spear"] = "위병 창",
        ["guard_sword"] = "위병 검",
        ["coin"] = "동전",
        ["torch"] = "횃불",
        ["statue"] = "석상",
        ["map"] = "지도",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>성향 이름.</summary>
    public static string Trait(TraitKind kind) => kind switch
    {
        TraitKind.Diligence => "근면",
        TraitKind.Sociability => "사교",
        TraitKind.Courage => "용기",
        TraitKind.Greed => "탐욕",
        _ => kind.ToString(),
    };

    /// <summary>액션 카테고리. 카드의 액션 묶음 제목이다.</summary>
    public static string Of(ActionCategory category) => category switch
    {
        ActionCategory.Movement => "이동",
        ActionCategory.Labor => "노동",
        ActionCategory.Social => "사교",
        ActionCategory.Needs => "생활",
        ActionCategory.Combat => "전투",
        ActionCategory.Items => "소지품",
        ActionCategory.Misc => "기타",
        _ => category.ToString(),
    };

    /// <summary>POI 심볼. <c>$workplace</c> 가 아니라 "일터" 로 읽는다.</summary>
    public static string Of(PoiSymbol symbol) => symbol switch
    {
        PoiSymbol.None => "—",
        PoiSymbol.Home => "집",
        PoiSymbol.Workplace => "일터",
        PoiSymbol.Market => "시장",
        PoiSymbol.Tavern => "선술집",
        PoiSymbol.Temple => "신전",
        PoiSymbol.Gate => "성문",
        PoiSymbol.NearestField => "가까운 밭",
        PoiSymbol.NearestSafe => "가까운 안전지대",
        PoiSymbol.NearestShelter => "가까운 대피소",
        _ => PoiSymbols.ToText(symbol),
    };

    /// <summary>지역 상태.</summary>
    public static string Of(RegionState state) => state switch
    {
        RegionState.Peace => "평온",
        RegionState.Alert => "경계",
        RegionState.War => "공성 중",
        RegionState.Disaster => "재해",
        _ => state.ToString(),
    };

    /// <summary>기후.</summary>
    public static string Of(Climate climate) => climate switch
    {
        Climate.Fair => "맑음",
        Climate.Cold => "추위",
        Climate.Storm => "폭풍",
        _ => climate.ToString(),
    };

    /// <summary>시간대.</summary>
    public static string Of(TimeOfDay time) => time switch
    {
        TimeOfDay.Dawn => "새벽",
        TimeOfDay.Morning => "아침",
        TimeOfDay.Noon => "한낮",
        TimeOfDay.Afternoon => "오후",
        TimeOfDay.Evening => "저녁",
        TimeOfDay.Night => "밤",
        _ => time.ToString(),
    };

    /// <summary>
    /// 이 장소에서 무언가를 얻는 행위를 뭐라고 부르는가.
    /// 같은 <c>Interact</c> 명령이라도 광산이면 채굴, 농장이면 수확이다.
    /// </summary>
    public static string HarvestVerb(string subtype) => subtype switch
    {
        "mine" => "캐냄",
        "farm" => "거둠",
        "fishing_spot" => "낚음",
        "hunting_ground" => "잡음",
        "logging_camp" => "베어 냄",
        "herb_patch" => "캐 모음",
        "pasture" => "거둠",
        _ => "모음",
    };

    /// <summary>
    /// 조사를 붙인다. <c>"물을(를) 씀"</c> 같은 표기가 남으면 사람이 읽는 글이 아니다 —
    /// 블라인드 평가 자료의 읽는 맛이 두 군에서 같아야 하므로 여기서 다듬는다.
    /// </summary>
    /// <param name="noun">명사.</param>
    /// <param name="withFinal">받침이 있을 때 붙일 조사 (을·이·은·과).</param>
    /// <param name="withoutFinal">받침이 없을 때 붙일 조사 (를·가·는·와).</param>
    public static string With(string noun, string withFinal, string withoutFinal) =>
        noun + (HasFinalConsonant(noun) ? withFinal : withoutFinal);

    /// <summary><c>~(으)로</c>. ㄹ 받침은 "로" 를 쓴다.</summary>
    public static string To(string noun) =>
        noun + (HasFinalConsonant(noun) && !EndsWithRieul(noun) ? "으로" : "로");

    /// <summary>마지막 글자에 받침이 있는가. 한글이 아니면 있는 것으로 본다(영어 id 대비).</summary>
    private static bool HasFinalConsonant(string noun)
    {
        if (noun.Length == 0)
        {
            return true;
        }

        char last = noun[^1];

        return last is < '가' or > '힣' || (last - '가') % 28 != 0;
    }

    private static bool EndsWithRieul(string noun) =>
        noun.Length > 0 && noun[^1] is >= '가' and <= '힣' && (noun[^1] - '가') % 28 == 8;

    /// <summary>아키타입 id → 한국어. 모르면 id 그대로.</summary>
    public static string Archetype(string id) => Archetypes.GetValueOrDefault(id, id);

    /// <summary>POI subtype → 한국어. 모르면 subtype 그대로.</summary>
    public static string Place(string subtype) => Places.GetValueOrDefault(subtype, subtype);

    /// <summary>아이템 id → 한국어. 모르면 id 그대로.</summary>
    public static string Item(string id) => Items.GetValueOrDefault(id, id);
}
