using System.Collections.Frozen;
using Npc.Contracts;
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
public static partial class Lexicon
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

    /// <summary>
    /// 직군. 아키타입 40개를 사람이 아는 일곱 묶음으로 나눈다
    /// (<c>docs/reference_masterdata.html</c> §06 의 표 그대로).
    ///
    /// <b>인형 색·목록 묶음·마법사 틀이 이것을 쓴다.</b> 표가 문서에만 있으면 화면이
    /// 그것을 쓸 수 없다 — 모르는 id 는 <see cref="ArchetypeGroup.Other"/> 다.
    /// </summary>
    public enum ArchetypeGroup
    {
        /// <summary>생산 — 재료를 받아 완성품을 만든다.</summary>
        Craft,

        /// <summary>채집 — 야외에서 원자재를 모은다.</summary>
        Gather,

        /// <summary>상업 — 사고팔고 재운다.</summary>
        Trade,

        /// <summary>치안 — 경계 근무와 순찰.</summary>
        Guard,

        /// <summary>종교·학문.</summary>
        Faith,

        /// <summary>주민 — 특정 직업이 없다.</summary>
        Folk,

        /// <summary>특수 — 의뢰·공연·대상.</summary>
        Special,

        /// <summary>표에 없는 id.</summary>
        Other,
    }

    /// <summary>아키타입 id → 직군. <c>reference_masterdata</c> §06 표 그대로다.</summary>
    public static readonly FrozenDictionary<string, ArchetypeGroup> Groups = new Dictionary<string, ArchetypeGroup>(StringComparer.Ordinal)
    {
        ["blacksmith"] = ArchetypeGroup.Craft,
        ["carpenter"] = ArchetypeGroup.Craft,
        ["tailor"] = ArchetypeGroup.Craft,
        ["alchemist"] = ArchetypeGroup.Craft,
        ["baker"] = ArchetypeGroup.Craft,
        ["brewer"] = ArchetypeGroup.Craft,
        ["jeweler"] = ArchetypeGroup.Craft,
        ["tanner"] = ArchetypeGroup.Craft,
        ["mason"] = ArchetypeGroup.Craft,
        ["scribe"] = ArchetypeGroup.Craft,
        ["miner"] = ArchetypeGroup.Gather,
        ["farmer"] = ArchetypeGroup.Gather,
        ["fisher"] = ArchetypeGroup.Gather,
        ["hunter"] = ArchetypeGroup.Gather,
        ["woodcutter"] = ArchetypeGroup.Gather,
        ["herbalist"] = ArchetypeGroup.Gather,
        ["shepherd"] = ArchetypeGroup.Gather,
        ["merchant"] = ArchetypeGroup.Trade,
        ["innkeeper"] = ArchetypeGroup.Trade,
        ["stablemaster"] = ArchetypeGroup.Trade,
        ["banker"] = ArchetypeGroup.Trade,
        ["peddler"] = ArchetypeGroup.Trade,
        ["guard_captain"] = ArchetypeGroup.Guard,
        ["town_guard"] = ArchetypeGroup.Guard,
        ["gate_guard"] = ArchetypeGroup.Guard,
        ["patrol_scout"] = ArchetypeGroup.Guard,
        ["watchman"] = ArchetypeGroup.Guard,
        ["priest"] = ArchetypeGroup.Faith,
        ["acolyte"] = ArchetypeGroup.Faith,
        ["scholar"] = ArchetypeGroup.Faith,
        ["healer"] = ArchetypeGroup.Faith,
        ["villager"] = ArchetypeGroup.Folk,
        ["child"] = ArchetypeGroup.Folk,
        ["elder"] = ArchetypeGroup.Folk,
        ["beggar"] = ArchetypeGroup.Folk,
        ["drunkard"] = ArchetypeGroup.Folk,
        ["noble"] = ArchetypeGroup.Folk,
        ["quest_giver"] = ArchetypeGroup.Special,
        ["wandering_bard"] = ArchetypeGroup.Special,
        ["caravan_leader"] = ArchetypeGroup.Special,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// 액션 37종의 한국어 표기. <c>actions.json</c> 의 <c>desc</c> 첫 문장을 줄인 것이다.
    ///
    /// <b>카드는 이것을 쓰지 않는다.</b> 아키타입 카드·플랜 설명은 검수 자료라 액션 id 를
    /// 그대로 적는다 — 골든과 블라인드 평가 자료의 전제가 그것이다. 여기는 초보자 화면(Studio)용이다.
    /// </summary>
    public static readonly FrozenDictionary<string, string> Actions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MoveTo"] = "이동",
        ["Follow"] = "따라가기",
        ["Wander"] = "돌아다니기",
        ["Flee"] = "도망",
        ["Patrol"] = "순찰",
        ["Work"] = "일하기",
        ["Gather"] = "채집",
        ["Mine"] = "채굴",
        ["Farm"] = "농사",
        ["Fish"] = "낚시",
        ["Craft"] = "제작",
        ["Cook"] = "요리",
        ["Repair"] = "수리",
        ["Talk"] = "대화",
        ["Trade"] = "거래",
        ["Greet"] = "인사",
        ["Gossip"] = "잡담",
        ["Pray"] = "기도",
        ["Perform"] = "공연",
        ["Eat"] = "식사",
        ["Drink"] = "마시기",
        ["Sleep"] = "잠",
        ["Rest"] = "휴식",
        ["Bathe"] = "씻기",
        ["Attack"] = "공격",
        ["Defend"] = "방어",
        ["Guard"] = "경계 근무",
        ["CallForHelp"] = "도움 요청",
        ["Retreat"] = "물러나기",
        ["PickUp"] = "줍기",
        ["Drop"] = "버리기",
        ["Store"] = "보관",
        ["Withdraw"] = "꺼내기",
        ["Equip"] = "장비",
        ["Wait"] = "기다리기",
        ["Observe"] = "관찰",
        ["Emote"] = "몸짓",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// 액션의 <b>서술형</b>. 캡션이 문장이 되려면 "일하기한다" 가 아니라 "일한다" 여야 한다.
    /// <see cref="Actions"/> 와 키가 같아야 한다 — <c>Lexicon_CoversEveryAction</c> 이 강제한다.
    /// </summary>
    public static readonly FrozenDictionary<string, string> ActionVerbs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MoveTo"] = "이동한다",
        ["Follow"] = "따라간다",
        ["Wander"] = "돌아다닌다",
        ["Flee"] = "달아난다",
        ["Patrol"] = "순찰한다",
        ["Work"] = "일한다",
        ["Gather"] = "모은다",
        ["Mine"] = "캔다",
        ["Farm"] = "농사짓는다",
        ["Fish"] = "낚는다",
        ["Craft"] = "만든다",
        ["Cook"] = "요리한다",
        ["Repair"] = "고친다",
        ["Talk"] = "대화한다",
        ["Trade"] = "판다",
        ["Greet"] = "인사한다",
        ["Gossip"] = "잡담한다",
        ["Pray"] = "기도한다",
        ["Perform"] = "공연한다",
        ["Eat"] = "먹는다",
        ["Drink"] = "마신다",
        ["Sleep"] = "잔다",
        ["Rest"] = "쉰다",
        ["Bathe"] = "씻는다",
        ["Attack"] = "공격한다",
        ["Defend"] = "사수한다",
        ["Guard"] = "경계를 선다",
        ["CallForHelp"] = "도움을 청한다",
        ["Retreat"] = "물러난다",
        ["PickUp"] = "줍는다",
        ["Drop"] = "버린다",
        ["Store"] = "보관한다",
        ["Withdraw"] = "꺼낸다",
        ["Equip"] = "장비한다",
        ["Wait"] = "기다린다",
        ["Observe"] = "지켜본다",
        ["Emote"] = "몸짓한다",
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

    /// <summary>
    /// 장소 유형 (H18). <b>화면에 <c>Workplace</c> 를 그대로 내지 않는다</b> —
    /// 그 낱말을 이미 아는 사람만 쓸 수 있는 도구가 된다.
    /// </summary>
    /// <param name="type">장소 유형.</param>
    public static string Of(PoiType type) => type switch
    {
        PoiType.Home => "집",
        PoiType.Workplace => "일터",
        PoiType.Market => "시장",
        PoiType.Tavern => "선술집",
        PoiType.Temple => "신전",
        PoiType.Gate => "성문",
        PoiType.Field => "농경지",
        PoiType.Wilderness => "야외",
        _ => type.ToString(),
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

    /// <summary>액션 id → 한국어. 모르면 id 그대로.</summary>
    public static string Action(string id) => Actions.GetValueOrDefault(id, id);

    /// <summary>액션 id → 서술형("일한다"). 모르면 표기 + "한다".</summary>
    public static string ActionVerb(string id) => ActionVerbs.GetValueOrDefault(id, Action(id) + "한다");

    /// <summary>아키타입 id → 직군. 모르는 id 는 <see cref="ArchetypeGroup.Other"/>.</summary>
    public static ArchetypeGroup Group(string id) => Groups.GetValueOrDefault(id, ArchetypeGroup.Other);

    /// <summary>직군 이름.</summary>
    public static string Of(ArchetypeGroup group) => group switch
    {
        ArchetypeGroup.Craft => "생산",
        ArchetypeGroup.Gather => "채집",
        ArchetypeGroup.Trade => "상업",
        ArchetypeGroup.Guard => "치안",
        ArchetypeGroup.Faith => "종교·학문",
        ArchetypeGroup.Folk => "주민",
        ArchetypeGroup.Special => "특수",
        _ => "기타",
    };

    /// <summary>
    /// 직군 색 (CSS hex). 부록 E 의 인형 색이고 지도 점·인구 막대가 같은 색을 쓴다 —
    /// 화면마다 다른 색이면 "파란 게 위병이다" 를 배울 수 없다.
    /// </summary>
    public static string ColorOf(ArchetypeGroup group) => group switch
    {
        ArchetypeGroup.Craft => "#e8873a",
        ArchetypeGroup.Gather => "#5aa85a",
        ArchetypeGroup.Trade => "#d9b23c",
        ArchetypeGroup.Guard => "#4a78c2",
        ArchetypeGroup.Faith => "#8a6bc9",
        ArchetypeGroup.Folk => "#8d97a5",
        ArchetypeGroup.Special => "#d46a9a",
        _ => "#444444",
    };

    /// <summary>
    /// 표시 이름은 로컬라이즈 표가 먼저다. <c>ko-KR</c> 의 <c>npc.&lt;id&gt;</c> → 사전 → id.
    /// </summary>
    public static string ArchetypeName(MasterDataSet data, string id)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Localized(data, "npc." + id) ?? Archetype(id);
    }

    /// <summary>지역 표시 이름. <c>zone.&lt;id&gt;</c> → id.</summary>
    public static string Zone(MasterDataSet data, string id)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Localized(data, "zone." + id) ?? id;
    }

    /// <summary>아이템 표시 이름. <c>item.&lt;id&gt;</c> → 사전 → id.</summary>
    public static string ItemName(MasterDataSet data, string id)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Localized(data, "item." + id) ?? Item(id);
    }

    /// <summary>
    /// 장소의 사람 이름. <c>house_012_04</c> → "집 #12". 지역 이름을 주면 "집 #12 (동쪽 장터)".
    /// 규칙 밖 id 는 <c>세부 유형 + id</c> 다.
    /// </summary>
    public static string PlaceName(PoiDef poi, string? zoneName = null)
    {
        ArgumentNullException.ThrowIfNull(poi);

        string label = Place(poi.Subtype);
        string number = Number(poi.Id, poi.Subtype);
        string head = number.Length == 0 ? label + " " + poi.Id : label + " #" + number;

        return string.IsNullOrEmpty(zoneName) ? head : $"{head} ({zoneName})";
    }

    /// <summary>같은 규칙을 마스터데이터에서 지역 이름까지 붙여 부른다.</summary>
    public static string PlaceName(MasterDataSet data, PoiId poi)
    {
        ArgumentNullException.ThrowIfNull(data);

        PoiDef def = data.Pois[poi];

        return PlaceName(def, Zone(data, data.Zones[def.Zone].Id));
    }

    /// <summary><c>house_012_04</c> 의 <c>012</c> → <c>12</c>. 규칙 밖이면 빈 문자열.</summary>
    private static string Number(string id, string subtype)
    {
        if (!id.StartsWith(subtype, StringComparison.Ordinal) || id.Length <= subtype.Length + 1)
        {
            return string.Empty;
        }

        string[] parts = id[(subtype.Length + 1)..].Split('_');

        return parts.Length >= 1 && int.TryParse(parts[0], out int n)
            ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string? Localized(MasterDataSet data, string key)
    {
        foreach (LocalizationTable table in data.Locales)
        {
            if (string.Equals(table.Locale, LocalizationTable.DefaultLocale, StringComparison.Ordinal)
                && table.Contains(key))
            {
                return table[key];
            }
        }

        return null;
    }
}
