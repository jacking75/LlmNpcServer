using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Npc.Narrative;

/// <summary>
/// 필드 하나의 설명 (T02).
/// </summary>
/// <param name="File">마스터데이터 파일 이름.</param>
/// <param name="Path">항목 안에서의 경로. 배열 첨자는 빼고 <c>/traits/courage</c> 처럼 쓴다.</param>
/// <param name="Title">화면에 보이는 한국어 이름.</param>
/// <param name="What">이 값이 무엇인가.</param>
/// <param name="Why">어디에 쓰이는가 — <b>이것이 초보자에게 가장 중요한 줄이다.</b></param>
/// <param name="Caution">틀리면 무슨 일이 나는가. 없으면 빈 문자열.</param>
/// <param name="Related">근거 검증 코드·문서.</param>
public readonly record struct FieldHelp(
    string File,
    string Path,
    string Title,
    string What,
    string Why,
    string Caution,
    ImmutableArray<string> Related);

/// <summary>
/// 필드 사전 (T02). <b>"이 칸이 무슨 뜻인가" 가 코드에 없었다.</b>
///
/// <para>
/// 마스터데이터의 필드 이름이 곧 UI 였다 — <c>allowed_actions</c>·<c>population_weight</c> 를
/// 그대로 라벨로 쓰면, 그 이름을 이미 아는 사람만 쓸 수 있는 도구가 된다.
/// <c>docs/schema/*.base.schema.json</c> 의 <c>description</c> 은 일부 필드에만 있다.
/// </para>
///
/// <para>
/// <b>잎(Narrative)에 둔다.</b> CLI·MCP·Studio 가 같은 사전을 본다. 빠진 필드가 생기면
/// <c>FieldGuide_CoversEverySchemaProperty</c> 가 스키마를 훑어 알려 준다 — 새 필드를 넣고
/// 설명을 빼먹으면 그 칸은 초보자에게 "뜻을 모르는 입력란" 이 된다.
/// </para>
/// </summary>
public static class FieldGuide
{
    /// <summary>키는 <c>파일명 + "#" + 경로</c> 다.</summary>
    public static readonly FrozenDictionary<string, FieldHelp> Fields = Build();

    /// <summary>화면 용어 → 한 줄 뜻 (부록 B). 처음 보는 사람이 먼저 읽는다.</summary>
    public static readonly FrozenDictionary<string, string> Glossary = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["직업"] = "아키타입(archetype). NPC 의 종류다 — 정의 하나를 수백 명이 함께 쓴다.",
        ["직군"] = "직업을 묶은 일곱 갈래. 생산·채집·상업·치안·종교·주민·특수. 지도 인형 색이 이것이다.",
        ["NPC"] = "인스턴스(npc_instances). 직업으로 생성된 개체 한 명.",
        ["개별 설정"] = "오버라이드(npc_overrides). 한 명만 다르게 한다 — 명단을 다시 만들어도 남는다.",
        ["장소"] = "POI(pois). 집·일터·시장·선술집 같은 지점.",
        ["지역"] = "존(zones). 장소들의 묶음. 이웃으로 이어진다.",
        ["행동"] = "액션(actions). 원자 동작 37개. LLM 은 이것만 조합한다.",
        ["하루 일과"] = "폴백 플랜(fallback_plans). LLM 이 전부 실패해도 도는 기본 하루.",
        ["돌발 반응"] = "인터럽트(interrupts). 사건에 즉시 하는 행동. 규칙으로만 정한다.",
        ["상태"] = "월드 플래그(world_flags). \"집에 있다\" 같은 켜짐/꺼짐 64칸.",
        ["미리 구운 계획"] = "프리베이크(planstore). LLM 이 미리 만들어 둔 하루. 직업 × 시간대 × 상황.",
        ["상황(버킷)"] = "BucketKey. 시간대 6 × 지역 상태 4 × 기후 3 = 직업당 72칸.",
        ["기대 경로"] = "정의만으로 계산한 하루. 실제 서버는 여기서 흔들린다 — 약속이 아니다.",
        ["파급"] = "무엇을 바꾸면 무엇을 다시 해야 하는가.",
        ["연습장"] = "원본 사본. 망쳐도 원본이 그대로다.",
        ["정원"] = "장소가 동시에 받을 수 있는 인원. 정원 합이 그 직업 인구보다 커야 한다(V10).",
        ["파생물"] = "도구가 만든 파일(거리표·NPC 명단). 손으로 고치지 않고 다시 만든다.",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>이 필드의 설명. 없으면 null.</summary>
    public static FieldHelp? Of(string file, string path)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(path);

        return Fields.TryGetValue(file + "#" + path, out FieldHelp help) ? help : null;
    }

    /// <summary>이 파일의 모든 필드 (경로 오름차순).</summary>
    public static ImmutableArray<FieldHelp> ForFile(string file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return
        [
            .. Fields.Values
                .Where(f => string.Equals(f.File, file, StringComparison.Ordinal))
                .OrderBy(f => f.Path, StringComparer.Ordinal),
        ];
    }

    private static FrozenDictionary<string, FieldHelp> Build()
    {
        var b = new Dictionary<string, FieldHelp>(StringComparer.Ordinal);

        Archetypes(b);
        Overrides(b);
        Pois(b);
        Zones(b);
        Fallbacks(b);
        Interrupts(b);
        Factions(b);
        Items(b);
        Actions(b);
        Advanced(b);

        return b.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static void Add(
        Dictionary<string, FieldHelp> map,
        string file,
        string path,
        string title,
        string what,
        string why,
        string caution = "",
        params string[] related) =>
        map[file + "#" + path] = new FieldHelp(file, path, title, what, why, caution, [.. related]);

    // ------------------------------------------------------------------ archetypes.json

    private static void Archetypes(Dictionary<string, FieldHelp> b)
    {
        const string F = "archetypes.json";

        Add(b, F, "/id", "직업 ID",
            "다른 파일이 이 직업을 가리킬 때 쓰는 영문 이름이다.",
            "하루 일과·장소 허가·NPC 명단이 이 이름으로 연결된다.",
            "만든 뒤에는 바꿀 수 없다 — 가리키던 곳이 전부 끊어진다.", "V3");
        Add(b, F, "/code", "직업 번호",
            "0 부터 붙는 번호다.",
            "미리 구운 플랜 2,880개가 이 번호 위에 있다.",
            "절대 바꾸지 않는다. 추가는 맨 뒤에만 한다.", "V1");
        Add(b, F, "/name_key", "표시 이름 키",
            "`npc.<id>` 형식의 로컬라이즈 키다.",
            "실제 문구는 `localization/ko-KR.json` 이 가진다.",
            "키를 지우면 V14 로 잡힌다.", "V14");
        Add(b, F, "/desc", "설명",
            "이 직업이 어떤 존재인지 한두 문장으로 쓴다.",
            "**LLM 에게 그대로 전달된다.** 하루 계획의 분위기가 여기서 나온다.",
            "`TODO:` 로 두면 NPC 가 이상하게 행동한다. 플레이어가 쓴 문장은 넣지 않는다.");
        Add(b, F, "/allowed_actions", "할 수 있는 행동",
            "이 직업이 쓸 수 있는 행동 목록이다.",
            "LLM 은 이 안에서만 계획을 짠다. 돌발 반응도 여기 없는 행동은 건너뛴다.",
            "경계 근무·순찰을 넣으려면 근무 시간대가 필요하다. 하루 일과가 쓰는 행동을 빼면 저장이 거절된다.",
            "V4", "V8", "V12");
        Add(b, F, "/home_poi_type", "사는 곳 유형",
            "집으로 배정받을 장소 유형이다.",
            "명단을 만들 때 빈 자리를 찾아 배정한다.",
            "보통 `house` 다.", "V10");
        Add(b, F, "/workplace_poi_type", "일터 유형",
            "일하러 갈 장소의 세부 유형이다. 없으면 비운다.",
            "하루 일과의 `$workplace` 가 이것으로 바인딩된다.",
            "그 유형 장소의 정원 합이 이 직업 인구보다 커야 한다.", "V10");
        Add(b, F, "/primary_recipes", "주력 제작품",
            "이 직업이 주로 만드는 것이다.",
            "하루 일과와 LLM 이 이것을 우선한다.",
            "`items.json` 에 같은 이름의 레시피가 있어야 한다.", "V3");
        Add(b, F, "/traits", "성격",
            "근면·사교·용기·탐욕 네 가지를 0~100 으로 준다.",
            "LLM 프롬프트에 실리고, 돌발 반응 규칙이 이 값으로 갈린다.",
            "바꾸면 걸리는 돌발 반응이 바뀐다 — 화면이 미리 보여 준다.");
        Add(b, F, "/traits/diligence", "근면",
            "얼마나 부지런한가. 0~100.",
            "일과의 밀도와 LLM 의 문장에 반영된다.");
        Add(b, F, "/traits/sociability", "사교",
            "얼마나 사람을 찾는가. 0~100.",
            "대화·잡담·공연이 계획에 들어갈 확률에 반영된다.");
        Add(b, F, "/traits/courage", "용기",
            "얼마나 위험을 견디는가. 0~100.",
            "**돌발 반응이 여기서 갈린다** — 40 미만이면 도망, 이상이면 맞서거나 물러난다.",
            "40 경계를 넘으면 위협에 대한 반응이 통째로 바뀐다.");
        Add(b, F, "/traits/greed", "탐욕",
            "얼마나 이득을 좇는가. 0~100.",
            "거래·수집 성향에 반영된다.");
        Add(b, F, "/default_goals", "기본 목표",
            "늘 신경 쓰는 것 두세 개다.",
            "LLM 프롬프트의 가변 부분(서픽스)에 실린다.",
            "많으면 300 토큰 예산을 먹는다.");
        Add(b, F, "/initial_inventory", "시작 소지품",
            "이 직업이 가지고 시작하는 아이템이다.",
            "첫 스텝의 전제(도구가 있나)가 여기서 선다.",
            "도구가 없으면 \"일하기\" 가 막힌다.");
        Add(b, F, "/initial_inventory/item", "소지품 아이템",
            "`items.json` 의 아이템 id 다.",
            "그 아이템의 `grants` 가 시작 플래그로 선다.",
            "없는 id 면 기동이 막힌다.", "V3");
        Add(b, F, "/initial_inventory/count", "소지품 개수",
            "몇 개를 들고 시작하는가.",
            "먹기·마시기처럼 소비하는 행동이 이 수를 쓴다.");
        Add(b, F, "/duty_hours", "근무 시간대",
            "근무하는 시간대를 고른다.",
            "이 때만 \"근무 중\" 이 서고 경계 근무·순찰을 할 수 있다.",
            "비우고 경계 근무·순찰을 허용하면 재계획이 폭주한다 — 기동이 막힌다.", "V12");
        Add(b, F, "/fallback_plan", "하루 일과 ID",
            "LLM 없이도 도는 기본 하루의 id 다.",
            "LLM 이 전부 실패해도 NPC 가 사는 이유다.",
            "직업마다 하나씩 있어야 한다.", "V7");
        Add(b, F, "/combat_capable", "싸울 수 있나",
            "무기를 들고 맞설 수 있는 직업인가.",
            "위협을 만났을 때 맞설지·물러날지·도움을 부를지가 갈린다.");
        Add(b, F, "/population_weight", "인구 비율",
            "전체 NPC 중 이 직업의 비율이다 (0.012 ≈ 5,000명 중 60명).",
            "명단을 만들 때 이 비율로 나눈다.",
            "**전체 합이 정확히 1.0** 이어야 한다. 늘리려면 다른 직업에서 뗀다.", "V5");
    }

    // ------------------------------------------------------------------ npc_overrides.json

    private static void Overrides(Dictionary<string, FieldHelp> b)
    {
        const string F = "npc_overrides.json";

        Add(b, F, "/id", "NPC 번호",
            "1~5,000 중 이 설정을 적용할 번호다.",
            "이 번호의 NPC 한 명에게만 적용된다.",
            "명단을 다시 만들어도 이 파일은 남는다.", "V13");
        Add(b, F, "/patrol_route", "순찰로",
            "차례로 들를 장소다. 최대 4곳.",
            "하루 일과의 `$patrol_route` 가 첫 지점으로 바인딩된다.",
            "**같은 지역**이어야 하고 들어갈 수 있는 곳이어야 한다.", "V13");
        Add(b, F, "/aggro_radius_m", "경계 반경",
            "몇 m 안에서 반응하는가. 0~200.",
            "게임서버가 쓴다 — NPC 서버는 저장해 넘길 뿐이다.",
            "비우면 아키타입 기본값이다.", "V13");
        Add(b, F, "/faction", "세력",
            "`factions.json` 의 세력 id 다.",
            "나가는 명령에 찍혀 게임서버가 적·아군을 판정한다.",
            "적대 판정 자체는 NPC 서버가 하지 않는다.", "V13");
        Add(b, F, "/dialogue_profile", "대화 프로필",
            "대화 서비스가 읽는 말투 id 다.",
            "NPC 서버는 저장만 한다.",
            "자유 문자열이라 오타를 잡아 주지 않는다.");
        Add(b, F, "/schedule_offset_min", "일정 오프셋",
            "시간대가 바뀔 때 n분 빠르게/늦게 움직인다. -120~120.",
            "같은 직업이 한꺼번에 움직이는 것을 막는 분산에 더해진다.",
            "시간대 폭보다 크면 그 시간대 계획을 아예 못 받는다.", "V13");
    }

    // ------------------------------------------------------------------ pois.json

    private static void Pois(Dictionary<string, FieldHelp> b)
    {
        const string F = "pois.json";

        Add(b, F, "/id", "장소 ID",
            "`유형_번호_지역번호` 형식이다 (`house_012_04`).",
            "순찰로·NPC 명단이 이 이름으로 가리킨다.",
            "바꾸지 않는다.");
        Add(b, F, "/code", "장소 번호",
            "거리표의 첨자다.",
            "`poi_distances.bin` 이 이 번호 순서로 만들어진다.",
            "절대 바꾸지 않는다. 추가는 맨 뒤에만.", "V1");
        Add(b, F, "/zone", "지역",
            "이 장소가 속한 지역 id 다.",
            "순찰로와 `$nearest_*` 는 같은 지역 안에서 고른다.",
            "", "V3");
        Add(b, F, "/type", "유형",
            "집·일터·시장·선술집·신전·성문·농경지·야외 8종 중 하나다.",
            "`$market`·`$tavern` 같은 심볼이 이 유형으로 장소를 찾는다.");
        Add(b, F, "/subtype", "세부 유형",
            "대장간·빵집·광산처럼 더 좁은 종류다.",
            "직업의 일터 유형이 가리키는 것이 이 값이다.",
            "새 세부 유형을 쓰면 그 유형의 장소를 만들어야 한다.", "V10");
        Add(b, F, "/pos", "위치",
            "**지역 중심 기준** 좌표다.",
            "거리표가 이 좌표로 만들어진다.",
            "바꾸면 거리표를 다시 만들어야 한다.");
        Add(b, F, "/pos/x", "위치 X", "가로 좌표다.", "거리 계산과 지도 그림이 쓴다.");
        Add(b, F, "/pos/y", "위치 Y", "높이 좌표다.", "거리 계산에는 쓰지 않는다 — 게임서버가 쓴다.");
        Add(b, F, "/pos/z", "위치 Z", "세로 좌표다.", "거리 계산과 지도 그림이 쓴다.");
        Add(b, F, "/capacity", "정원",
            "동시에 배정될 수 있는 인원이다.",
            "명단 생성이 이 수만큼만 배정한다.",
            "같은 세부 유형의 정원 합이 그 직업 인구를 감당해야 한다.", "V10");
        Add(b, F, "/open_hours", "여는 시간",
            "시간대 from~to 로 여닫는다.",
            "닫힌 시간에 오면 그 스텝이 실패한다.");
        Add(b, F, "/open_hours/from", "여는 시간대", "언제부터 여는가.", "이 시간대부터 들어갈 수 있다.");
        Add(b, F, "/open_hours/to", "닫는 시간대", "언제까지 여는가.", "이 시간대 전까지 들어갈 수 있다.");
        Add(b, F, "/grants", "도착하면 서는 상태",
            "이 장소에 있는 동안 서는 상태 플래그다 (집 → `AtHome`).",
            "다음 스텝의 전제가 이것으로 충족된다.",
            "같은 유형의 다른 장소를 베끼는 것이 안전하다.");
        Add(b, F, "/allowed_archetypes", "일할 수 있는 직업",
            "여기서 일할 수 있는 직업 목록이다. 비우면 제한 없음.",
            "정원이 남아도 여기 없으면 일할 수 없다.",
            "출입 허가가 아니라 **근무 허가**다 — 시장·선술집은 누구나 드나든다.", "V10");
        Add(b, F, "/resources", "얻을 수 있는 것",
            "채집·채굴로 얻는 아이템이다.",
            "채집 계열 행동이 이 목록에서 고른다.",
            "`items.json` 에 있어야 한다.", "V3");
    }

    // ------------------------------------------------------------------ zones.json

    private static void Zones(Dictionary<string, FieldHelp> b)
    {
        const string F = "zones.json";

        Add(b, F, "/id", "지역 ID", "장소·NPC 가 가리키는 영문 이름이다.", "장소의 `zone` 이 이 값이다.", "바꾸지 않는다.", "V3");
        Add(b, F, "/code", "지역 번호", "0 부터 붙는 번호다.", "샤드 마스크와 장소 id 의 끝 두 자리가 이 번호다.", "바꾸지 않는다.", "V1");
        Add(b, F, "/name_key", "표시 이름 키", "`zone.<id>` 형식의 로컬라이즈 키다.", "실제 문구는 `localization/ko-KR.json` 이 가진다.", "", "V14");
        Add(b, F, "/adjacent", "이웃 지역",
            "걸어서 이어지는 지역이다.",
            "지역 간 이동과 마을 지도의 선이 이것을 본다.",
            "한쪽만 적으면 한 방향으로만 이어진 마을이 된다.");
        Add(b, F, "/default_region_state", "기본 지역 상태",
            "평온·경계·공성 중·재해 중 기본값이다.",
            "버킷의 지역 상태 차원이 여기서 시작한다.");
        Add(b, F, "/default_climate", "기본 기후",
            "맑음·추위·폭풍 중 기본값이다.",
            "버킷의 기후 차원이 여기서 시작한다.");
        Add(b, F, "/capacity", "지역 정원",
            "이 지역이 감당하는 인원이다.",
            "명단 생성이 지역별 인구를 나눌 때 본다.");
    }

    // ------------------------------------------------------------------ fallback_plans.json

    private static void Fallbacks(Dictionary<string, FieldHelp> b)
    {
        const string F = "fallback_plans.json";

        Add(b, F, "/id", "일과 ID", "`fb_<직업>` 형식이다.", "직업의 하루 일과 ID 와 같아야 한다.", "", "V7");
        Add(b, F, "/archetype", "직업", "누구의 하루인가.", "이 직업의 허용 행동만 쓸 수 있다.", "", "V8");
        Add(b, F, "/goal", "하루 목표", "한 단어로 적는 하루의 목적이다.", "설명과 화면 표시에 쓴다.");
        Add(b, F, "/loop", "반복",
            "마지막 스텝 뒤에 첫 스텝으로 돌아가는가.",
            "폴백은 언제나 `true` 다 — 하루가 끝없이 돌아야 한다.",
            "마지막 상태가 첫 스텝의 전제를 만족해야 한다(고리가 닫힌다).", "V3");
        Add(b, F, "/on_step_fail", "스텝 실패 시",
            "한 스텝이 실패했을 때 무엇을 하는가.",
            "폴백은 언제나 `skip` 이다 — 다음 스텝으로 넘어간다.",
            "`replan` 으로 두면 LLM 이 죽었을 때 갈 곳이 없다.");
        Add(b, F, "/steps", "스텝 목록", "차례로 하는 행동이다.", "위에서 아래로 실행한다.", "장소를 요구하는 행동 앞에는 이동이 와야 한다.");
        Add(b, F, "/steps/action", "행동", "이 스텝에서 하는 행동이다.", "그 직업이 할 수 있는 행동만 쓸 수 있다.", "", "V8");
        Add(b, F, "/steps/args", "대상",
            "어디로·무엇을·몇 개인가.",
            "장소는 `$home`·`$workplace` 같은 심볼로 쓴다 — 개체마다 다른 실제 장소로 바인딩된다.",
            "장소를 직접 id 로 쓰면 수천 NPC 가 한 곳으로 몰린다.", "V2");
        Add(b, F, "/steps/timeout_s", "최대 시간(초)",
            "이 안에 못 끝내면 실패로 보고 다음으로 간다.",
            "명령이 유실돼도 NPC 가 굳지 않는 이유다.",
            "**상한이지 예상값이 아니다.** 예측 소요보다 짧으면 매번 실패한다. 0 은 쓸 수 없다.");
    }

    // ------------------------------------------------------------------ interrupts.json

    private static void Interrupts(Dictionary<string, FieldHelp> b)
    {
        const string F = "interrupts.json";

        Add(b, F, "/id", "규칙 ID", "규칙 하나의 이름이다.", "같은 우선순위끼리는 이 이름 순으로 갈린다.");
        Add(b, F, "/priority", "우선순위", "클수록 먼저 본다.", "여러 규칙이 맞으면 가장 큰 것 하나만 발동한다.", "같은 값이면 id 오름차순이라 이름이 판정을 바꾼다.");
        Add(b, F, "/when", "언제", "발동 조건이다. 안의 항목이 전부 AND 로 묶인다.", "하나라도 안 맞으면 다음 규칙을 본다.");
        Add(b, F, "/when/any_flag", "이 상태 중 하나", "나열한 상태 중 하나라도 서 있으면 참이다.", "\"위협이 가깝다 또는 교전 중\" 같은 조건을 만든다.");
        Add(b, F, "/when/all_flag", "이 상태 전부", "나열한 상태가 전부 서 있어야 참이다.", "\"교전 중이고 부상\" 같은 좁은 조건을 만든다.");
        Add(b, F, "/when/none_flag", "이 상태 없음", "나열한 상태가 하나도 없어야 참이다.", "\"아직 안전한 곳이 아닐 때만\" 같은 조건을 만든다.");
        Add(b, F, "/when/event", "이 사건일 때만", "이 종류의 사건을 받았을 때만 본다.", "생략하면 사건을 가리지 않고 상태만 본다.");
        Add(b, F, "/when/archetype_trait", "성격 조건", "`\"<40\"` 같은 비교식이다.", "**용기 40 이 도망과 물러나기를 가른다.**", "경계를 넘기면 그 직업의 반응이 통째로 바뀐다.");
        Add(b, F, "/when/combat_capable", "전투 가능 여부", "싸울 수 있는 직업만/못 하는 직업만 고른다.", "맞서는 규칙과 도망가는 규칙을 가른다.");
        Add(b, F, "/then", "무엇을", "발동했을 때 즉시 하는 것이다.", "LLM 을 기다리지 않는다 — 반응 속도가 이 기능의 전부다.");
        Add(b, F, "/then/action", "즉시 하는 행동", "발동 즉시 실행할 행동이다.", "그 직업의 허용 행동에 없으면 규칙 전체를 건너뛴다.", "허용 행동에서 빼면 그 직업만 조용히 반응하지 않게 된다.");
        Add(b, F, "/then/params", "행동 인자", "행동의 대상이다 (`$nearest_safe` 등).", "안전지대·위협 대상 같은 심볼을 실제 대상으로 바인딩한다.");
        Add(b, F, "/replan", "그 뒤 재계획", "즉시 반응 뒤 새 계획을 받는 방법이다.", "먼저 도망치고 계획은 나중에 받는다.");
        Add(b, F, "/replan/urgency", "재계획 긴급도", "0~100. 클수록 먼저 새 계획을 받는다.", "재계획 큐의 점수가 된다.");
    }

    // ------------------------------------------------------------------ factions.json

    private static void Factions(Dictionary<string, FieldHelp> b)
    {
        const string F = "factions.json";

        Add(b, F, "/id", "세력 ID", "세력의 영문 이름이다.", "개별 NPC 설정의 세력이 이것을 가리킨다.");
        Add(b, F, "/code", "세력 번호", "0 은 \"미지정\" 이다.", "나가는 명령에 이 번호가 찍힌다.", "바꾸지 않는다.");
        Add(b, F, "/desc", "설명", "이 세력이 무엇인지 한 줄이다.", "화면 표시용이다.");
        Add(b, F, "/hostile_to", "적대 세력", "서로 적으로 보는 세력이다.", "**게임서버가 쓴다** — NPC 서버는 판정하지 않는다.");
    }

    // ------------------------------------------------------------------ items.json

    private static void Items(Dictionary<string, FieldHelp> b)
    {
        const string F = "items.json";

        Add(b, F, "/id", "아이템 ID", "아이템의 영문 이름이다.", "소지품·레시피·채집 자원이 이 이름으로 가리킨다.", "바꾸지 않는다.", "V3");
        Add(b, F, "/code", "아이템 번호", "인벤토리 배열의 첨자다.", "런타임이 이 번호로 개수를 센다.", "절대 바꾸지 않는다.", "V1");
        Add(b, F, "/category", "분류", "원자재·완성품·식량 같은 갈래다.", "화면 묶음과 프롬프트 카탈로그에 쓴다.");
        Add(b, F, "/grants", "이걸 가지면 서는 상태",
            "이 아이템을 가진 동안 서는 상태 플래그다 (빵 → `HasFood`).",
            "\"먹을 것이 있다\" 같은 전제가 여기서 선다.",
            "바꾸면 그 아이템에 기대던 스텝의 전제가 깨진다.");
        Add(b, F, "/stack", "최대 개수", "한 번에 들 수 있는 수다.", "인벤토리 가득 참 판정이 이것을 본다.");
        Add(b, F, "/recipes", "제작법", "무엇으로 무엇을 만드는가.", "제작 행동이 이 목록에서 고른다.");
        Add(b, F, "/recipes/id", "제작법 ID", "만들어지는 결과물의 이름이다.", "직업의 주력 제작품이 이것을 가리킨다.", "", "V3");
        Add(b, F, "/recipes/workplace_type", "제작 장소", "어느 일터에서 만들 수 있는가.", "그 일터에 있어야 제작이 된다.");
        Add(b, F, "/recipes/inputs", "재료", "무엇을 얼마나 쓰는가.", "재료가 모자라면 제작 스텝이 실패한다.");
        Add(b, F, "/recipes/inputs/item", "재료 아이템", "쓸 아이템 id 다.", "채집·꺼내기로 미리 확보해야 한다.", "", "V3");
        Add(b, F, "/recipes/inputs/count", "재료 개수", "몇 개 쓰는가.", "하루 일과의 수지 계산이 이것을 센다.", "", "V3");
        Add(b, F, "/recipes/outputs", "산출물", "무엇이 몇 개 나오는가.", "완성품이 인벤토리에 들어간다.");
        Add(b, F, "/recipes/duration_s", "제작 시간(초)", "한 번 만드는 데 걸리는 시간이다.", "하루 예측의 제작 스텝 길이가 이것이다.");
    }

    // ------------------------------------------------------------------ actions.json (읽기 전용)

    private static void Actions(Dictionary<string, FieldHelp> b)
    {
        const string F = "actions.json";
        const string Read = "**읽기 전용으로 본다.** 행동을 고치면 프롬프트 프리픽스가 바뀌어 미리 구운 플랜이 전량 무효가 된다.";

        Add(b, F, "/id", "행동 ID", "행동의 영문 이름이다.", "하루 일과·돌발 반응이 이 이름으로 가리킨다.", Read);
        Add(b, F, "/code", "행동 번호", "허용 행동 비트셋의 자리다.", "직업의 허용 행동이 이 번호로 저장된다.", "절대 바꾸지 않는다. 상한 40칸 중 37칸을 쓴다.", "V1");
        Add(b, F, "/category", "분류", "이동·노동·사교·생활·전투·소지품·기타.", "화면 묶음과 카탈로그 순서에 쓴다.");
        Add(b, F, "/desc", "설명", "이 행동이 무엇인지 설명한다.", "**프롬프트 카탈로그에 그대로 실린다.**", Read);
        Add(b, F, "/params", "인자 정의", "이 행동이 받는 값들이다.", "하루 일과의 대상 칸이 이 정의대로 만들어진다.", Read);
        Add(b, F, "/requires", "전제 상태(전부)", "이 상태가 전부 서 있어야 한다.", "안 서 있으면 그 스텝은 실패한다.");
        Add(b, F, "/requires_any", "전제 상태(하나)", "나열한 것 중 하나는 서 있어야 한다.", "\"식량 또는 물\" 같은 전제를 만든다.");
        Add(b, F, "/forbids", "금지 상태", "이 상태가 서 있으면 못 한다.", "\"가방이 가득 차면 줍지 못한다\" 같은 규칙이다.");
        Add(b, F, "/grants", "세우는 상태", "이 행동이 끝나면 서는 상태다.", "다음 스텝의 전제가 여기서 충족된다.");
        Add(b, F, "/clears", "내리는 상태", "이 행동이 끝나면 내려가는 상태다.", "\"먹으면 배고픔이 내려간다\" 같은 효과다.");
        Add(b, F, "/duration", "소요 시간 모델", "고정·거리 비례·인자·시간대까지 중 하나다.", "**하루 예측의 스텝 길이가 여기서 나온다.**");
        Add(b, F, "/duration/kind", "소요 방식", "`fixed`·`distance`·`param`·`until_time`.", "거리 비례면 집·일터 사이 거리가 그대로 시간이 된다.");
        Add(b, F, "/duration/base_s", "기본 시간(초)", "기본 소요다.", "거리 비례에서는 여기에 거리분이 더해진다.");
        Add(b, F, "/duration/per_meter_s", "미터당 시간(초)", "1m 이동에 걸리는 시간이다.", "이동 스텝의 길이를 정한다.");
        Add(b, F, "/duration/param", "시간 인자 이름", "어느 인자에서 시간을 읽는가.", "`duration_s`·`until_time` 같은 인자를 가리킨다.");
        Add(b, F, "/cost", "비용", "계획 점수용 가중치다.", "LLM 계획의 상대 선호에 쓴다.");
        Add(b, F, "/default_timeout_s", "기본 최대 시간(초)", "하루 일과가 값을 안 주면 쓰는 상한이다.", "명령이 유실돼도 진행이 재개되는 근거다.");
        Add(b, F, "/emits", "발행 명령", "게임서버로 나가는 명령이다.", "이 행동이 실제로 무엇을 시키는가.", Read);
        Add(b, F, "/emits/command", "명령 종류", "게임서버가 받는 명령 이름이다.", "계약(N1~N8)의 명령 종류 중 하나다.", Read);
        Add(b, F, "/emits/priority", "명령 우선순위", "같은 틱에 여러 명령이 나갈 때의 순서다.", "즉시 반응이 평소 명령보다 먼저 나간다.");
        Add(b, F, "/emits/map", "명령 필드 배선", "명령의 각 칸을 무엇으로 채우는가.", "심볼·인자·개체 값이 여기서 실제 값이 된다.", Read);
        Add(b, F, "/completes_on", "완료 사건", "어떤 사건이 오면 끝난 것으로 보는가.", "도착으로 끝나는 행동은 장소 상태를 세운다.");
        Add(b, F, "/fails_on", "실패 사건", "어떤 사건이 오면 실패로 보는가.", "실패하면 하루 일과의 실패 처리로 넘어간다.");
    }

    // ------------------------------------------------------------------ 고급 파일 (한 줄씩)

    private static void Advanced(Dictionary<string, FieldHelp> b)
    {
        const string Wf = "world_flags.json";
        const string Advanced = "**고급이다.** 번호(bit)를 재배치하면 미리 구운 플랜 2,880개가 통째로 깨진다 — 추가는 맨 뒤에만 한다.";

        Add(b, Wf, "/bit", "비트 번호", "64칸 중 이 상태가 쓰는 자리다.", "런타임이 이 자리로 상태를 읽는다.", Advanced, "V1");
        Add(b, Wf, "/id", "상태 ID", "상태의 영문 이름이다.", "행동의 전제·효과가 이 이름으로 가리킨다.", Advanced);
        Add(b, Wf, "/group", "묶음", "같이 다루는 상태들의 이름이다.", "배타 묶음 검사가 이것을 본다.", Advanced);
        Add(b, Wf, "/desc", "설명", "이 상태가 무엇인지 한 줄이다.", "**프롬프트 카탈로그에 실린다.**", Advanced);
        Add(b, Wf, "/exclusive_groups", "배타 묶음", "동시에 설 수 없는 상태 묶음이다.", "\"집에 있으면서 일터에 있다\" 를 막는다.", Advanced);

        const string Cb = "context_buckets.json";
        const string CbNote = "**고급이다.** 버킷 차원을 바꾸면 미리 구운 플랜의 좌표계가 바뀐다.";

        Add(b, Cb, "/dimensions", "버킷 차원", "시간대·지역 상태·기후의 값과 시각 범위다.", "하루 예측의 시작 시각과 시작 상태가 여기서 나온다.", CbNote);
        Add(b, Cb, "/total_keys", "전체 버킷 수", "직업 수 × 72 다.", "선언값과 실제가 어긋나면 기동이 막힌다.", "직업을 추가하면 같이 올린다.", "V6");
        Add(b, Cb, "/prebake_priority", "프리베이크 우선순위", "어느 지역 상태를 먼저 구울 것인가.", "중단돼도 많이 쓰이는 버킷이 먼저 채워진다.", CbNote);
        Add(b, Cb, "/prebake_priority/region_state", "대상 지역 상태", "우선순위를 매길 지역 상태다.", "평온이 가장 높아야 평시가 캐시에 맞는다.", CbNote);
        Add(b, Cb, "/prebake_priority/weight", "가중치", "클수록 먼저 굽는다.", "런타임의 버킷 역추론도 이 값을 본다.", CbNote);
        Add(b, Cb, "/key_format", "키 형식", "버킷 키를 글자로 쓰는 법이다.", "`직업@시간대.지역.기후` 형식이다.", CbNote);
        Add(b, Cb, "/key_example", "키 예시", "형식의 실제 예다.", "플랜 파일 이름이 이 형식이다.", CbNote);
        Add(b, Cb, "/index_formula", "첨자 수식", "키를 번호로 바꾸는 식이다.", "플랜 스토어가 이 번호로 자리를 잡는다.", CbNote);

        const string Dl = "dialogue_lines.json";
        const string DlNote = "대화 **문구**는 여기 없다 — 주제 번호만 있고 문구는 대화 서비스가 고른다.";

        Add(b, Dl, "/id", "대사 주제 ID", "대화 주제의 영문 이름이다.", "대화 행동의 주제 인자가 이것을 가리킨다.", DlNote);
        Add(b, Dl, "/code", "대사 주제 번호", "패킷에 실리는 번호다.", "패킷은 문자열을 싣지 않는다(N3).", "바꾸지 않는다.");
        Add(b, Dl, "/tags", "태그", "주제를 묶는 이름표다.", "대화 서비스가 문구를 고를 때 본다.", DlNote);
        Add(b, Dl, "/priority", "우선순위", "여러 주제가 맞을 때의 순서다.", "큰 것이 먼저다.", DlNote);

        const string Ni = "npc_instances.json";
        const string NiNote = "**생성물이다.** 손으로 고치지 않는다 — 다시 만들면 사라진다. 개별 차이는 `npc_overrides.json` 에 적는다.";

        Add(b, Ni, "/seed", "생성 시드", "명단을 만든 난수 씨앗이다.", "같은 시드면 같은 명단이 나온다.", NiNote);
        Add(b, Ni, "/id", "NPC 번호", "1부터 붙는 개체 번호다.", "개별 설정이 이 번호로 얹힌다.", NiNote);
        Add(b, Ni, "/archetype", "직업", "이 개체의 직업이다.", "정의는 전부 직업이 가진다.", NiNote);
        Add(b, Ni, "/zone", "지역", "집이 있는 지역이다.", "순찰로는 이 지역 안이어야 한다.", NiNote);
        Add(b, Ni, "/home_poi", "집", "배정받은 집이다.", "`$home` 이 이것으로 바인딩된다.", NiNote);
        Add(b, Ni, "/workplace_poi", "일터", "배정받은 일터다. 없을 수 있다.", "`$workplace` 가 이것으로 바인딩된다.", NiNote);
        Add(b, Ni, "/spawn_pos", "스폰 좌표", "처음 등장하는 위치다.", "게임서버가 이 좌표에 세운다.", NiNote);
        Add(b, Ni, "/spawn_pos/x", "스폰 X", "가로 좌표다.", "게임서버가 쓴다.", NiNote);
        Add(b, Ni, "/spawn_pos/y", "스폰 Y", "높이 좌표다.", "게임서버가 쓴다.", NiNote);
        Add(b, Ni, "/spawn_pos/z", "스폰 Z", "세로 좌표다.", "게임서버가 쓴다.", NiNote);
    }
}
