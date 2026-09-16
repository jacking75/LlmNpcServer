# NPC Studio 개선 계획 — 초보자가 JSON 을 열지 않고 NPC 를 읽고 · 예측하고 · 만들게 한다

작성 2026-09-16 (2차 개정 같은 날) · 대상 `tools/Npc.Studio` · 상태 **계획** (다음 세션부터 이 문서로 구현한다)

> **이 문서가 Studio 작업의 유일한 지시서다.** `PRODUCTION_ROADMAP.md` F-02 는 "무엇을" 만 적었고,
> 이 문서는 **초보자 관점에서 "왜 · 어떻게"** 를 태스크 단위로 적는다.
>
> **2차 개정에서 더한 것.** ① **동작 예측** — 정의만으로 "이 NPC 가 하루를 어떻게 보내고, 위험하면 어떻게
> 하는지" 를 지도 위 SVG 인형으로 재생한다 (F 단계, T22~T29). ② 초보자 여정을 다시 훑어 빠져 있던
> 연습장·따라하기·전역 검색·건강 진단·저장 전 요약을 더했다 (T30~T34). ③ 이 모든 예측이 **데이터만으로
> 가능하다는 근거**를 코드에서 확인해 부록 D 에 수식·규칙으로 적었다.

---

## 태스크 체크리스트

**하나를 끝내면 그 즉시 여기 `[ ]` → `[x]` 로 바꾸고, §5 의 해당 태스크 제목 옆에도 `✅` 를 붙인다.**
행 순서가 권장 구현 순서다 (ID 는 단계별로 붙였고 순서와 다르다). 의존 열의 태스크가 끝나기 전에는 시작하지 않는다.

| # | 완료 | ID | 태스크 | 단계 | 우선 | 크기 | 의존 |
|---|---|---|---|---|---|---|---|
| 1 | [ ] | **T01** | 화면을 URL 라우팅·컴포넌트로 쪼갠다 (`Home.razor` 508줄 해체) | A 기반 | P0 | M | — |
| 2 | [ ] | **T02** | 사전 — `FieldGuide`(필드 뜻) · `Lexicon.Actions`(행동 한국어) · `Lexicon.Group`(직군) | A 기반 | P0 | M | — |
| 3 | [ ] | **T03** | 시작 화면 — 마을 한눈에 · 할 일 카드 · 해야 할 일 배지 | A 안내 | P0 | S | T01 |
| 4 | [ ] | **T04** | 화면마다 안내문 + 도움말 서랍 + 필드 ⓘ 툴팁 | A 안내 | P0 | S | T01 T02 |
| 5 | [ ] | **T30** | 연습장(샌드박스) — 원본을 건드리지 않고 실험한다 | A 안내 | P0 | S | T01 |
| 6 | [ ] | **T05** | 아키타입 **개요 보기** — JSON 대신 섹션 카드 (`ArchetypeFacts`) | B 읽기 | P0 | L | T02 T04 |
| 7 | [ ] | **T06** | 폴백 하루 구조화 (`PlanExplain.Trace`) + `DayTimeline` | B 읽기 | P0 | M | T02 |
| 8 | [ ] | **T07** | 개별 NPC 카드 초보자화 + 지역 지도 `ZoneMap` | B 읽기 | P0 | M | T01 T02 |
| 9 | [ ] | **T22** | **동작 예측 엔진 `DayForecast`** — 정의 → 24시간 위치·행동·상태 (Narrative) | F 예측 | P0 | L | T06 |
| 10 | [ ] | **T23** | **상황 반응 예측 `ReactionForecast`** — "위협이 오면?" + 왜 그 규칙인가 | F 예측 | P0 | M | T02 |
| 11 | [ ] | **T24** | **동작 미리보기 화면** — SVG NPC 인형 · 지도 재생 · 시간 스크러버 · 상황 버튼 | F 예측 | P0 | L | T07 T22 T23 |
| 12 | [ ] | **T15** | 검증 오류를 사람 말로 + 문제 필드로 바로가기 | D 안전 | P0 | M | T01 T02 |
| 13 | [ ] | **T16** | 파급 패널 — 저장 뒤 "이제 무엇을 해야 하나" (`ImpactAnalyzer`) | D 안전 | P0 | S | T01 |
| 14 | [ ] | **T10** | 아키타입 **폼 편집기** — 필드별 위젯, 무변경 저장은 바이트 동일 | C 편집 | P0 | L | T05 |
| 15 | [ ] | **T25** | 편집 ↔ 예측 연동 — 값을 바꾸면 400ms 뒤 하루가 다시 그려진다 | F 예측 | P0 | S | T10 T24 |
| 16 | [ ] | **T34** | 저장 전 사람 말 요약 — "무엇이 어떻게 바뀌나" (`DefinitionDiff`) | C 편집 | P1 | M | T10 T23 |
| 17 | [ ] | **T11** | 폴백 하루 **편집기** — 스텝 추가·순서·인자 폼 + 실시간 판정·예측 | C 편집 | P0 | L | T06 T10 T22 |
| 18 | [ ] | **T12** | 새 직업 **마법사** 5단계 (직군 틀 · 인구 재배분 3안 · 일터 정원 · 하루 · 파급) | C 편집 | P0 | L | T10 T11 T16 |
| 19 | [ ] | **T17** | 파생물 재생성 버튼 (`gen_npcs` · `gen_poi_distances`) | D 운영 | P1 | M | T16 |
| 20 | [ ] | **T08** | 장소·지역 탐색 화면 (읽기) | B 읽기 | P1 | M | T01 T02 T07 |
| 21 | [ ] | **T13** | 장소(POI) 추가 폼 — 5장(양봉장) 을 Studio 로 | C 편집 | P1 | M | T08 T16 |
| 22 | [ ] | **T26** | 마을 전체 지도 — 지역 타일 12개 · 인구 밀도 · 클릭 이동 | F 예측 | P1 | M | T07 T08 |
| 23 | [ ] | **T14** | 개별 NPC 폼 개선 — 필드 설명 · 지도에서 순찰로 찍기 · 값 제안 | C 편집 | P1 | S | T07 T04 |
| 24 | [ ] | **T09** | 돌발 반응(인터럽트) 화면 (읽기) + 반응 예측 연결 | B 읽기 | P1 | S | T01 T23 |
| 25 | [ ] | **T29** | 건강 진단 `ArchetypeLint` — 검증은 통과하지만 이상한 정의를 잡는다 | F 예측 | P1 | M | T05 T22 |
| 26 | [ ] | **T33** | 인구 분포 차트 + "이 직업은 어느 지역에 몇 명 살게 되나" | B 읽기 | P1 | S | T05 T08 |
| 27 | [ ] | **T31** | 따라하기 체크리스트 — 첫 10분 안내가 스스로 체크된다 | A 안내 | P1 | S | T04 T24 |
| 28 | [ ] | **T32** | 전역 검색 — 직업·NPC·장소를 한국어/ID 로 한 칸에서 | B 읽기 | P1 | S | T01 T02 |
| 29 | [ ] | **T27** | 버킷별 계획 — 미리 구운 플랜(`planstore`) 을 상황 격자로 보고 예측한다 | F 예측 | P1 | M | T22 T24 |
| 30 | [ ] | **T28** | 라이브 관찰 — 실행 중인 서버의 NPC 를 같은 지도에 (예측 vs 실제) | F 예측 | P2 | M | T24 T26 |
| 31 | [ ] | **T21** | 액션 카탈로그 읽기 화면 (9장 대비, 편집은 고급 모드 유지) | E 확장 | P2 | S | T02 T08 |
| 32 | [ ] | **T18** | 되돌리기 — 저장 전 백업과 파일 단위 복원 | D 안전 | P2 | S | T16 |
| 33 | [ ] | **T19** | 매뉴얼·README 를 초보자 시나리오 중심으로 다시 쓴다 | D 문서 | P1 | M | T03~T24 |
| 34 | [ ] | **T20** | 테스트 정비 — 드리프트·시나리오 종단·불필요 테스트 제거 | D 테스트 | P1 | M | 전부 |

**판정 시나리오** (§2) — 전부 통과하면 이 계획은 끝난다.

- [ ] **S1 읽기** 대장장이를 열어 "어디서 살고, 무엇을 하고, 위험하면 어떻게 하는지" 를 3분 안에 말할 수 있다
- [ ] **S2 새 직업** "양봉가" 를 만들고 (일터 · 인구 · 하루 일과) 검증 통과 → 파생물 재생성 → `--npcs 5000` 기동에서 뜬다 (7장)
- [ ] **S3 개별 NPC** 위병 #2326 의 순찰로를 지도에서 3곳 찍어 바꾸고 저장한다
- [ ] **S4 장소** 양봉장 POI 둘을 만들고 거리표를 다시 굽는다 (5장의 장소 부분)
- [ ] **S5 예측** 대장장이 개요에서 "하루 재생" 을 눌러 집 → 대장간 → 선술집 → 집 이 지도 위에서 움직이는 것을 보고, "위협 등장" 을 눌러 **물러나기**(`retreat_on_threat`) 로 성문에 가는 것을 본다. 용기를 30 으로 내리면 **도망**(`flee_on_threat`) 으로 바뀐다
- [ ] **S6 상황별** 수련사제의 "오후 · 경계 · 폭풍" 버킷을 고르면 미리 구운 플랜(신전으로 뛰어가 기도) 이 재생된다

---

## 0. 이 문서를 쓰는 규칙

- **한 태스크 = 커밋 하나 이상.** 파일군이 갈리면(Narrative / Studio / docs) 커밋을 나눈다. 메시지 `studio: …` · `narrative: …` · `docs: …`.
- 태스크를 끝내면 **① 위 표 체크 ② §5 제목에 ✅ ③ `working_log.md` 항목**. 셋 다 한 커밋에.
- "구현" 은 **현재 코드 기준**이다. 코드가 그 사이 바뀌었으면 코드가 맞고, 이 문서를 고친다.
- 판단이 갈리는 곳은 `> 확인:` 이다. 구현 전에 그 파일을 열어 결정하고, 결정을 이 문서에 적는다.
- CLAUDE.md §2·§3·§5.1 이 우선한다. **Studio 는 `Core`·`MasterData`·`Narrative` 만 참조한다.** `Host`·`Runtime`·`Planning`·`Llm`·`Sim`·`Cli` 를 끌어오지 않는다 — 필요한 계산은 코어(`Narrative`·`MasterData/Authoring`) 로 **내린다**. 실행 중 서버는 HTTP 로만 본다 (T28).

---

## 1. 진단 — 지금 왜 어려운가

### 1.1 증상 → 원인 → 태스크

| # | 증상 (초보자가 겪는 것) | 원인 (코드에서) | 고치는 태스크 |
|---|---|---|---|
| 1 | 열자마자 `blacksmith` 의 **JSON 원문**이 나온다 | `Home.razor:304` 첫 아키타입 자동 선택 + 기본 탭 `json` | T03 T05 |
| 2 | `allowed_actions`·`duty_hours`·`population_weight` … **필드 이름이 곧 UI** 다 | "필드 → 사람 말" 사전이 코드에 없다. `docs/schema` 의 `description` 은 일부 필드뿐 | T02 T04 |
| 3 | 설명 카드가 **검수자 언어**다 ("근거 `archetypes.json` · V5", 액션이 영어 id) | `ArchetypeCard` 는 검수 카드라 그것이 맞다. Studio 가 그것을 유일한 설명으로 쓰는 것이 문제. `Lexicon` 에 액션 표기가 없다 | T02 T05 |
| 4 | 이 툴로 **무엇을 할 수 있는지** 화면 어디에도 없다 | 상단 `매뉴얼` 링크뿐 | T03 T04 T31 T19 |
| 5 | **새 직업 만들기**가 3칸이다. 그 다음 할 일은 매뉴얼에 숨어 있다 | `CreateArchetype(id, from, weight)` 복제 트랜잭션만. 재배분 3안도 안 보여 준다 | T12 T16 T17 |
| 6 | 하루 일과·장소·돌발 반응은 **파일 전체 원문 편집**뿐이다 | 폼이 아키타입·오버라이드 두 곳뿐 | T06 T08 T09 T11 T13 |
| 7 | 검증 오류가 `V10 · pois.json` + JSON Pointer 로 나온다 | Pointer → 화면 링크가 없다 | T15 |
| 8 | 저장 뒤 **무엇을 더 해야 하는지** 알려 주지 않는다 | `ImpactAnalyzer`·`DerivedArtifacts` 를 Studio 가 안 부른다 | T16 T17 |
| 9 | 개별 NPC 폼의 값이 **무슨 뜻인지** 없다 | `npc_overrides.json` 의 `_comment` 에만 있다 | T02 T14 |
| 10 | 순찰로를 **좌표 숫자**로 고른다 | POI 에 `pos` 가 있는데 그리지 않는다 | T07 T14 |
| 11 | 화면 상태가 문자열 모드라 **URL 이 없다** | 단일 컴포넌트 508줄 | T01 |
| **12** | **정의를 다 읽어도 "그래서 이 NPC 가 어떻게 움직이지?" 를 모른다.** 표와 카드는 정적이다 | 예측을 만드는 코드가 없다. 그런데 재료는 전부 있다 — 소요 시간 모델(`ActionDef.Duration`), 심볼 바인딩 규칙(`Sim`·`PoiBinder`), 인터럽트 매칭(`InterruptRules.TryMatch`), 스텝 상태 전이(`PlanExplain`), 미리 구운 플랜(`planstore`, Core 만으로 컴파일 가능) | **T22~T29** |
| 13 | 실수하면 원본이 망가질까 봐 **손대지 못한다** | 실습서는 `lab/` 사본을 쓰라고 하는데(`samples/lab.ps1`) Studio 에는 그 개념이 없다 | T30 T18 |
| 14 | 검증은 통과하는데 **이상한 정의**를 만든다 (설명이 `TODO:`, 인구가 0명으로 반올림, 사는 지역에 선술집이 없어 `$tavern` 스텝이 매번 실패) | V1~V13 은 기동 가능성만 본다. 품질 경고가 없다 | T29 |
| 15 | 바꾸고 저장하기 전에 **무엇이 어떻게 달라지는지** 모른다 | 파일 단위 파급(`ImpactAnalyzer`) 만 있고 값 단위 설명이 없다 | T34 |

### 1.2 초보자 여정으로 다시 본 빈틈

사람이 이 도구로 NPC 데이터를 다루는 여정은 다섯 단계다. 1차 계획은 **③ 예측** 이 통째로 비어 있었다.

| 단계 | 사람이 묻는 것 | 답하는 태스크 |
|---|---|---|
| ① 알기 | 이게 뭔가, 뭘 할 수 있나, 망가뜨려도 되나 | T03 T04 T30 T31 T32 |
| ② 보기 | 이 직업·이 NPC·이 장소는 무엇인가 | T05 T06 T07 T08 T09 T33 |
| **③ 예측** | **그래서 이 NPC 는 하루를 어떻게 보내고, 위험하면 어떻게 하나. 저 값을 바꾸면 뭐가 달라지나** | **T22 T23 T24 T25 T26 T27 T28 T29** |
| ④ 만들기 | 새 직업·장소·개별 설정을 어떻게 만드나 | T10 T11 T12 T13 T14 T34 |
| ⑤ 확인·되돌리기 | 잘못됐나, 무엇을 더 해야 하나, 되돌릴 수 있나 | T15 T16 T17 T18 |

**이 계획 밖 (명시).** LLM 제안 생성(F-07 — Studio 는 `Npc.Llm` 을 참조하지 않는다; `npc author` CLI 경로) · 대화 문구(대화 서비스 D-01 몫 — Studio 는 주제 id 만 안다) · 액션·플래그·버킷 편집(프롬프트·번호 체계를 흔드는 고급 작업. 읽기 화면 T21 과 JSON 고급 모드만) · git 커밋 생성(로드맵 F-02 에 있지만 초보자 목표와 무관. `npc diff` 안내로 대신한다).

---

## 2. 목표 — 완료 조건

### 2.1 판정 시나리오 (전부 **JSON 원문을 한 번도 보지 않고**)

| 시나리오 | 사람이 하는 일 | 성공 판정 |
|---|---|---|
| **S1 읽기** | 시작 → 직업 "대장장이" → 개요 | 집·일터·하루·성향·위험 시 반응·인구를 읽고 설명할 수 있다. 영어 id 는 부제로만 |
| **S2 새 직업** (7장) | 마법사: 이름 "양봉가" → 직군 "채집" · 닮은 직업 "목동" → 인구 20명 (재배분 안 고르기) → 일터 "양봉장" (정원 확인) → 하루 일과 6스텝 → 확인 | 검증 통과 · 파급 패널이 "NPC 명단 재생성 · 프리베이크 필요" · 재생성 버튼 → `--npcs 5000` 기동에서 양봉가가 뜬다 |
| **S3 개별 NPC** | NPC #2326 → 지도에서 위병소·장터·집 클릭 → 저장 | `npc_overrides.json` 의 2326 항목이 바뀌고 검증 통과 |
| **S4 장소** (5장) | 장소 → "새 장소" → 양봉장 2곳 → 저장 → "거리표 다시 만들기" | `pois.json` 에 code 244·245 · `poi_distances.bin` 재생성 · `derived.lock.json` 갱신 |
| **S5 예측** | 대장장이 개요 → "하루 재생" → "위협 등장" → 편집 탭에서 용기 55 → 30 | 인형이 집 → 대장간(일하기) → 선술집 → 집(잠) 을 지도 위에서 움직인다 · 위협에서 **물러나기**로 성문에 간다 · 용기 30 이면 **도망**으로 바뀌고 "왜" 패널이 `flee_on_threat: 용기 30 < 40 ✓` 를 보여 준다 |
| **S6 상황별** | 수련사제 → 버킷 격자에서 "오후 · 경계 · 폭풍" | `planstore/pinned/acolyte@Afternoon.Alert.Storm.json` 의 플랜(신전으로 뛰어가 기도·휴식·귀가·잠) 이 재생된다. 없는 버킷은 "폴백으로 돈다" 표시 |

### 2.2 수치 목표

- 시작 화면에서 S1 까지 클릭 **4회 이내**, S5 의 첫 재생까지 **3회 이내**.
- 모든 편집 화면의 모든 필드에 **ⓘ 설명** (T20 드리프트 테스트가 강제).
- 검증 오류 100% 가 **화면 링크**를 가진다.
- 무변경 저장은 **바이트 동일**.
- 예측은 **결정론** — 같은 정의면 같은 타임라인 (골든 스냅샷 가능). 소요 시간은 `Npc.Sim` 의 모델과 **지터를 뺀 값이 일치**한다 (드리프트 테스트).

---

## 3. 설계 원칙

| 원칙 | 이유 · 근거 |
|---|---|
| **설명·예측은 코드에서 생성한다.** 화면에 문장을 박아 두지 않는다 | `Npc.Narrative` 가 이미 "정의 → 사람 말" 이고 골든이 있다. 예측도 설명이다 — 같은 곳에 둔다 |
| **사전은 한 곳** — `Npc.Narrative` 의 `Lexicon`·(신설)`FieldGuide`·`Glossary` | 잎이라 CLI·MCP·Studio 가 같은 사전을 쓴다. 드리프트 테스트로 빠진 것을 잡는다 |
| **표시 이름 ≠ 식별자** | 로컬라이즈 표(`ko-KR.json` 의 `npc.*`·`zone.*`·`item.*`) 를 먼저, 없는 것(액션·POI 유형·직군) 은 `Lexicon` |
| **예측은 결정론이고 시각·난수·LLM 을 쓰지 않는다** | `Narrative` 의 규약("LLM·시각·난수 없음") 그대로. 소요 시간 모델은 `Npc.Sim` 이 기준이고 지터만 뺀다 — **드리프트 테스트**가 둘을 묶는다 |
| **예측은 "기대 경로" 이지 약속이 아니다** — 화면에 그렇게 적는다 | 실제 서버는 지터(±%)·게임서버 경로 계산·LLM 재계획이 더해진다. 소켓 경로는 결정론도 아니다 (CLAUDE.md §2.3) |
| **저장 경로는 하나** — `StudioWorkspace.ValidateAndWrite` | 임시 사본 → V1~V13 → 로더 → 원자 저장 |
| **서식 보존은 `JsonSurgeon`** | 통째로 다시 직렬화하면 diff 를 못 읽는다 |
| **마스터데이터를 하드코딩하지 않는다** | 액션·POI 유형·시간대·세력·아키타입 수 전부 데이터에서 |
| **Studio 참조는 `Core`·`MasterData`·`Narrative`** | 필요한 계산은 코어로 내린다. 실행 중 서버는 HTTP |
| **생성물은 읽기 전용** | 재생성은 버튼(T17), 편집은 절대 아니다 |
| **재생은 클라이언트에서** | Blazor Server 가 프레임마다 SignalR 을 타면 안 된다. 서버는 키프레임을 한 번 주고, 정적 JS(빌드 없음) 가 움직인다 |
| **테스트는 §5.1 기준** | "그려진다" 를 단언하지 않는다. 드리프트·경계·불변식·검사기 자체 시험·시나리오 종단만 |

---

## 4. 정보 구조 — 개선 후 화면 지도

```
/                        시작 (T03)   마을 지도 축소판(T26) · 할 일 3장 · 해야 할 일 배지(T16) · 연습장 배지(T30)
/archetypes              직업 목록    직군별 묶음 · 한국어 · 인구 막대(T33) · 검색
/archetypes/{id}         직업 상세    탭: 개요(T05) · 하루 미리보기(T24) · 편집(T10 + T25) · 하루 일과(T06/T11) · 상황별(T27) · 전문가 카드
/archetypes/new          새 직업 마법사 (T12)
/npcs                    NPC 목록     직업 → 지역 트리 · 검색 · 오버라이드 필터
/npcs/{id}               NPC 상세     누구인가(T07) · 지도 · 하루 미리보기(실제 집·일터, T24) · 개별 설정 폼(T14)
/map                     마을 전체 지도 (T26) — 지역 타일 · 인구 · 클릭 → /places/{zone}
/places                  지역·장소    지역 카드 → 장소 목록 → 상세 (T08) · 새 장소(T13)
/interrupts              돌발 반응    규칙 표(사람 말) · 걸리는 직업 · "이 직업이면?" 예측 (T09 + T23)
/actions                 행동 카탈로그 (읽기, T21)
/live                    라이브 관찰 (T28, 서버 연결 시)
/files/{name}            고급 · JSON 파일 (기존 원문 편집기 — "고급" 으로 격하)
/issues                  검증 결과 전체 (T15) + 건강 진단(T29)
```

공통 컴포넌트 (`Components/Shared/`):

| 컴포넌트 | 역할 | 태스크 |
|---|---|---|
| `StudioLayout.razor` | 상단 바 + 좌측 내비 + 우측 `IssuePanel` + `HelpDrawer` + 전역 검색(T32) | T01 |
| `HelpDrawer.razor` | 우측 서랍. "이 화면에서 할 수 있는 것" + 용어 + 따라하기(T31) | T04 |
| `FieldHelp.razor` | `<FieldHelp File="archetypes.json" Path="/duty_hours" />` → ⓘ 툴팁 | T04 |
| `IssuePanel.razor` | 오류 요약 (사람 말 제목 + 바로가기) + 건강 진단 | T15 T29 |
| `ImpactPanel.razor` | 저장 뒤 파급 카드 + 해야 할 일 | T16 |
| `ZoneMap.razor` | 존 안 POI 를 SVG 로. 강조·순찰로·클릭·**NPC 인형 레이어** | T07 T24 |
| `NpcFigure.razor` | SVG NPC 인형 (직군 색 · 상태 배지 · 말풍선) — 부록 E | T24 |
| `BehaviorPreview.razor` | 지도 + 스크러버 + 현재 스텝 카드 + 상황 버튼 + "왜" 패널 | T24 |
| `DayTimeline.razor` | 시간대 6칸 띠 + 근무 + 스텝 배치 (예측 시간으로) | T06 T22 |
| `TraitBars.razor` | 성향 4개 막대(읽기) / 슬라이더(편집) | T05 T10 |
| `ActionMatrix.razor` | 카테고리 × 액션 칩(읽기) / 체크(편집) | T05 T10 |
| `BucketGrid.razor` | 시간대 6 × (지역 4 × 기후 3) 격자, 셀 = 플랜 상태 | T27 |
| `MarkdownView.razor` | 기존. 전문가 카드에만 | — |

세션 상태: `Services/StudioSession.cs` (Scoped, 회로당 1개) — 카탈로그 캐시 · 검증 결과 · 바꾼 파일 · 따라하기 진행 · 토스트. `StudioWorkspace` 는 Singleton. 정적 JS 하나: `wwwroot/behavior-preview.js` (T24).

---

## 5. 태스크 상세

각 태스크는 **왜 → 효과 → 구현 → 테스트 → 완료 판정**. 파일 경로는 저장소 루트 기준.

---

### T01 · 화면을 URL 라우팅·컴포넌트로 쪼갠다

**왜.** `Home.razor` 한 파일이 세 모드·두 탭·마법사·폼을 문자열 상태로 든다. 화면을 더하면 1,500줄이 된다. URL 이 없어 오류 → 필드 바로가기(T15) 를 만들 수 없고 뒤로 가기가 안 된다.

**효과.** 이후 모든 태스크가 각자 파일에서 돈다. 링크 공유·뒤로 가기·새로고침 유지.

**구현.**

1. `Components/Layout/StudioLayout.razor` — 상단 바(경로 · 읽기 전용/연습장 배지 · 전체 검증 · 새로고침 · 도움말) 와 좌측 내비(§4). `App.razor` 의 `<Routes>` 기본 레이아웃으로.
2. `Components/Pages/`: `Home.razor` `@page "/"` · `Archetypes.razor` `@page "/archetypes"` `@page "/archetypes/{Id}"` · `ArchetypeNew.razor` `@page "/archetypes/new"` · `Npcs.razor` `@page "/npcs"` `@page "/npcs/{Id:int}"` · `Files.razor` `@page "/files"` `@page "/files/{Name}"` · `Map`·`Places`·`Interrupts`·`Actions`·`Live`·`Issues` 는 빈 껍데기.
3. `Services/StudioSession.cs` (Scoped):
   ```csharp
   public sealed class StudioSession(StudioWorkspace workspace)
   {
       public StudioCatalog Catalog { get; private set; } = workspace.LoadCatalog();
       public ImmutableArray<StudioIssue> Issues { get; private set; }
       public ImmutableArray<string> ChangedFiles { get; private set; } = [];
       public event Action? Changed;
       public void Reload() { Catalog = workspace.LoadCatalog(); Issues = Catalog.Issues; Changed?.Invoke(); }
       public void Apply(StudioSaveResult result) { Issues = result.Issues; if (result.Saved) ChangedFiles = [.. ChangedFiles.Union(result.Files)]; Changed?.Invoke(); }
       public void Toast(string message, bool error) { … }
   }
   ```
   `Program.cs`: `builder.Services.AddScoped<StudioSession>();`
4. `Home.razor` 의 `@code` 를 페이지별로 나눈다. **동작은 바꾸지 않는다.** `App.razor` 의 인라인 `<style>` 두 줄은 `app.css` 로.
5. 페이지 파라미터가 바뀔 때 `OnParametersSet` 에서 다시 읽는다 (`OnInitialized` 는 첫 진입만).

**테스트.** 화면 테스트 없음. `StudioWorkspaceTests` 초록. 수동: 모든 URL 직접 열기 · 상세에서 새로고침.

**완료 판정.** `Home.razor` 가 시작 화면만 · 모든 화면이 URL 을 가진다 · 빌드 경고 0 · 기존 기능 전부 그대로.

---

### T02 · 사전 — `FieldGuide` · `Lexicon.Actions` · `Lexicon.Group`

**왜.** "필드가 무슨 뜻인가" 가 코드에 없다. 액션 한국어 표기가 없어 카드가 `Craft · Drink` 를 낸다. 직군(생산·채집·상업·치안·종교·주민·특수) 은 `reference_masterdata.html` §06 표에만 있어 인형 색·마법사 틀·목록 묶음에 쓸 수 없다.

**구현.**

1. `src/Npc.Narrative/Lexicon.cs`:
   ```csharp
   public static readonly FrozenDictionary<string, string> Actions = …;   // 37개 전부. actions.json 의 desc 첫 문장 기준
   public static string Action(string id) => Actions.GetValueOrDefault(id, id);

   /// <summary>직군. 인형 색·목록 묶음·마법사 틀이 쓴다. 모르는 id 는 Other.</summary>
   public enum ArchetypeGroup { Craft, Gather, Trade, Guard, Faith, Folk, Special, Other }
   public static readonly FrozenDictionary<string, ArchetypeGroup> Groups = …;   // reference_masterdata §06 표 그대로 40개
   public static ArchetypeGroup Group(string id) => Groups.GetValueOrDefault(id, ArchetypeGroup.Other);
   public static string Of(ArchetypeGroup g) => g switch { Craft => "생산", Gather => "채집", Trade => "상업", Guard => "치안", Faith => "종교·학문", Folk => "주민", Special => "특수", _ => "기타" };
   ```
   > 확인: `ArchetypeCard.Actions()` 가 `Lexicon.Action()` 을 쓰게 바꿀지. 바꾸면 골든·블라인드 평가 자료의 전제가 바뀐다. **권장: 카드는 그대로, Studio 만 쓴다.**
2. `src/Npc.Narrative/FieldGuide.cs`:
   ```csharp
   public readonly record struct FieldHelp(string File, string Path, string Title, string What, string Why, string Caution, ImmutableArray<string> Related);
   public static class FieldGuide
   {
       public static readonly FrozenDictionary<string, FieldHelp> Fields;   // key = File + "#" + Path
       public static FieldHelp? Of(string file, string path);
       public static readonly FrozenDictionary<string, string> Glossary;    // 부록 B
   }
   ```
   **부록 A 가 초안이다.** 대상: `archetypes.json`(15) · `npc_overrides.json`(6) · `pois.json`(11) · `zones.json`(6) · `fallback_plans.json`(6+3) · `interrupts.json`(9) · `factions.json`(4) · `items.json` · `actions.json`(읽기) · 고급 파일은 한 줄.
3. 지역·직업 이름: `Lexicon.Zone(MasterDataSet, id)`·`Lexicon.ArchetypeName(MasterDataSet, id)` — `data.Locales` 의 `ko-KR` 에서 `zone.<id>`·`npc.<id>`, 없으면 `Lexicon.Archetypes`, 그래도 없으면 id.
   > 확인: `LocalizationTable` 의 조회 API (`src/Npc.MasterData/LocalizationTable.cs`).
4. 장소 표시 이름 `Lexicon.PlaceName(PoiDef, zoneName)` → "집 #12 (동쪽 장터)" — `house_012_04` 의 숫자를 뽑는다. 규칙 밖 id 는 `Lexicon.Place(subtype) + " " + id`.

**테스트** (`tests/Npc.Tests/Narrative/`): `Lexicon_CoversEveryAction` · `Lexicon_CoversEveryArchetypeGroup` · `FieldGuide_CoversEverySchemaProperty`(`docs/schema/*.base.schema.json` 재귀, `_comment`·`$schema`·`version` 제외) · `FieldGuide_HasNoEmptyText`.

---

### T03 · 시작 화면

**왜.** 첫 화면이 "무엇을 할 수 있는 툴인가" 를 말해야 한다.

**구현.** `Pages/Home.razor`:
1. **마을 한눈에** — 4 타일(직업 · NPC · 장소 · 지역) + 한 줄 정의("직업은 NPC 의 종류, NPC 는 그 종류로 생성된 개체, 장소는 사는 곳·일하는 곳"). `StudioCatalog` 에 `PoiCount`·`ZoneCount`·`InterruptCount` 를 더한다. T26 이 끝나면 타일 옆에 **마을 지도 축소판**.
2. **무엇을 하시겠습니까** — 카드 4장: 직업을 살펴본다 → `/archetypes/{첫}` · **NPC 가 어떻게 움직이는지 본다** → `/archetypes/{첫}?tab=forecast` · 새 직업을 만든다 → `/archetypes/new` · NPC 한 명을 고친다 → `/npcs`. 각 2줄 설명 + "약 n분".
3. **검증 상태** · **해야 할 일**(T16) · **연습장**(T30: "원본입니다 — 실험은 연습장에서" 버튼) · **처음이라면**(용어 5개, `FieldGuide.Glossary`).

**테스트.** `LoadCatalog_*` 에 `PoiCount > 0` 한 줄.

**완료 판정.** 시작 → S1 클릭 2회, S5 첫 재생 클릭 2회.

---

### T04 · 화면마다 안내문 + 도움말 서랍 + 필드 ⓘ 툴팁

**구현.**
1. `Shared/FieldHelp.razor` — `FieldGuide.Of(File, Path)` 로 ⓘ + CSS 팝오버 (JS 없음, hover/focus).
2. `Shared/HelpDrawer.razor` — 우측 서랍. 페이지가 `[CascadingParameter] StudioLayout` 로 `SetHelp(RenderFragment)`. 내용: 할 수 있는 것 3~5줄 · 다음 단계 · 관련 용어 · (T31) 따라하기.
3. 각 페이지 상단 `page-lead` 한 줄.
4. 기존 오버라이드 폼 필드에 `FieldHelp` 부착.

**완료 판정.** 모든 입력 칸 옆에 ⓘ, 모든 페이지에 lead 와 서랍.

---

### T30 · 연습장(샌드박스)

**왜.** 초보자는 원본을 망칠까 봐 손대지 못한다. 실습서는 `lab/<이름>/masterdata` 사본을 쓴다(`samples/lab.ps1`, `lab/` 은 gitignore). Studio 에 같은 개념이 있어야 "일단 눌러 본다" 가 된다.

**구현.**
1. `StudioWorkspace` 가 **현재 디렉터리를 바꿀 수 있게** — `options.MasterData` 대신 `_directory` 필드(`_gate` 안에서만 교체). `StudioOptions` 는 초기값.
2. `OpenSandbox(string name)` — `lab/studio-<name>/masterdata` 로 전부 복사(기존 `CopyMasterData` + `localization/`), `_directory` 교체, `StudioSession.Reload()`. `--masterdata` 가 저장소 밖이면 `%LOCALAPPDATA%\NpcStudio\lab\` 에.
3. 상단 배지 "연습장 · studio-0916" · 버튼 "원본과 비교"(파일별 바이트 비교 → 바뀐 파일 목록 + 파급) · "연습장 버리기"(확인 후 삭제, 원본으로 복귀) · "원본에 적용"(바뀐 파일만 원본에 `ValidateAndWrite` — 원본에서 다시 검증한다).
4. 시작 화면과 저장 버튼 옆에 "원본입니다" 경고 + "연습장에서 하기".
5. 파생물 재생성(T17) 은 연습장 디렉터리를 `--out`·입력으로 준다 — `gen_npcs.cs` 가 `--out` 을 받는다; 입력 디렉터리 인자가 있는지 `> 확인`.

**테스트.** `Sandbox_CopiesAndIsolatesWrites` — 연습장에서 저장해도 원본 바이트 불변.

---

### T05 · 아키타입 개요 보기

**왜.** S1 의 핵심. "대장장이가 어떤 존재인가" 를 섹션 카드로 읽는다.

**구현.**
1. `ArchetypeCard` 의 private 계산을 `public static ArchetypeFacts Facts(MasterDataSet, ArchetypeDef, int population)` 로 뽑고 `Render` 는 `Facts` 를 문자열로 만든다. **카드 출력 바이트 불변** (`Card_IsDeterministic`·골든).
   ```csharp
   public sealed record ArchetypeFacts(
       ArchetypeDef Def, int Population, int PopulationBase,
       string HomeType, string? WorkplaceType, int WorkplaceSites, int WorkplaceCapacity,
       ImmutableArray<TimeOfDay> DutyHours, bool DutyMissingButNeeded,
       ImmutableArray<(ActionCategory Category, ImmutableArray<string> Ids)> AllowedByCategory, ImmutableArray<string> Denied,
       ImmutableArray<(string Recipe, string Inputs, int Seconds, bool Exists)> Recipes,
       ImmutableArray<(string Item, int Count)> Inventory, WorldFlags StartFlags,
       ImmutableArray<InterruptRule> Interrupts, CompiledPlan? Fallback);
   ```
2. `/archetypes/{id}` 기본 탭 **개요** — 섹션: ① 머리(한국어 이름 · 직군 칩 · id · code · `desc` 강조 "LLM 에게 그대로 전달된다") ② 얼마나 있나(인구 막대, T33 지역 분포) ③ 어디서 살고 일하나(정원 vs 인구 ✓/✗) ④ 언제 일하나(`DayTimeline` 근무) ⑤ 성격(`TraitBars` + "용기 55 → 위협을 만나면 물러난다" — `Interrupts` 매칭에서) ⑥ 할 수 있는 행동(`ActionMatrix` 한국어) ⑦ **하루 미리보기 축소판**(T24 의 지도 + 재생 버튼 → 탭 이동) ⑧ 위험할 때(인터럽트 표) ⑨ 시작 소지품 ⑩ 건강 진단(T29). `code`·`name_key`·`fallback_plan`·버킷은 접이식 고급.
3. **전문가 카드** 탭 — 기존 md.

**테스트.** `Facts_AgreesWithCard`. 기존 골든 유지.

---

### T06 · 폴백 하루 구조화 (`PlanExplain.Trace`) + `DayTimeline`

**왜.** md 표는 화면이 열을 못 쓴다. 예측 엔진(T22)·편집기(T11) 가 스텝별 판정·이유·상태를 구조로 필요로 한다.

**구현.** `src/Npc.Narrative/PlanExplain.cs`:
```csharp
public readonly record struct StepTrace(int Index, ActionId Action, string ActionId, string Target, string Requirement,
    string Code, string Reason, WorldFlags Before, WorldFlags After, int TimeoutSeconds);
public readonly record struct LoopVerdict(bool Closed, string Message);
public static ImmutableArray<StepTrace> Trace(MasterDataSet data, CompiledPlan plan, BucketKey bucket, ArchetypeId archetype);
public static LoopVerdict LoopOf(CompiledPlan plan, WorldFlags finalState);
```
`Steps()` 는 `Trace()` 로 같은 md 를 만든다 (**출력 불변**). `FirstFailure()` 는 `Trace()` 의 첫 `Code != ""`.
`Shared/DayTimeline.razor` — 입력 `ImmutableArray<StepTrace>` 또는 (T22 이후) `ImmutableArray<ForecastSegment>` — T22 전에는 타임아웃 합 비례, T22 후에는 **예측 시간**으로 폭을 잡는다.

**테스트.** 기존 `PlanExplain_*` 세 개 유지가 전부. 커밋 메시지에 "출력 불변".

---

### T07 · 개별 NPC 카드 초보자화 + 지역 지도 `ZoneMap`

**구현.**
1. `LoadNpcOverview(int id)` → `StudioNpcOverview(NpcInstanceDef Npc, string ArchetypeName, string ZoneName, PoiDef Home, PoiDef? Workplace, float CommuteMeters, bool CanEnterWorkplace, ImmutableArray<StudioPoiChoice> ZonePois, StudioNpcOverrideEditor Override)`. 거리·출입은 `InstanceCard` 의 계산을 `InstanceFacts` 로 뽑아 쓴다.
2. **누구인가** 문장 — `InstanceCard.Sentence(...)`: "#2326 은 **동쪽 장터**의 **위병**이다. 집 #12 에 살고 위병소 #1 에서 일한다 (걸어서 약 120 m). 순찰로 3곳 · 세력 마을 경비대 · 일정이 15분 늦다."
3. `Shared/ZoneMap.razor`:
   - 입력: `Pois`, `Home`, `Workplace`, `Route`, `OnPoiClick`, `Interactive`, **`ChildContent`(인형 레이어, T24)**.
   - `<svg viewBox>` 범위 = POI `X`·`Z` 최소·최대 + 10%. 점 색은 유형별(집 회색 · 일터 파랑 · 시장 노랑 · 선술집 주황 · 신전 보라 · 성문 갈색 · 농경지 초록 · 야외 연두). 집·일터 큰 테두리. 순찰로 `<polyline>` + 순번.
   - **좌표는 지역 안에서만 실제다.** `pois.json` 의 `pos` 는 "존 중심 기준" 배치라 지역끼리 합칠 절대 좌표가 없다 — 지도는 항상 **한 지역**을 그린다 (T26 은 타일로 배치한다).
4. `/npcs/{id}`: 문장 → 지도 → 표 → (T24) 하루 미리보기 → 개별 설정 폼. 전문가 카드 접이식.

**테스트.** `LoadNpcOverview_ComputesCommuteLikeInstanceCard`.

---

### T22 · 동작 예측 엔진 `DayForecast` ★

**왜.** 정의를 다 읽어도 "그래서 어떻게 움직이지" 를 모른다 (§1.1 #12). 재료는 전부 코어에 있다 — 이 태스크는 그것을 **한 타임라인으로 엮는 것**이다. 화면(T24) 보다 먼저, 화면 없이 테스트한다.

**효과.** 아키타입·개별 NPC·폴백 편집·마법사·버킷 격자 — 다섯 화면이 같은 엔진을 쓴다. CLI `npc forecast` 도 공짜다.

**구현.** `src/Npc.Narrative/DayForecast.cs` (부록 D 가 수식·규칙 전문이다):

```csharp
/// <summary>누구의 하루인가. 아키타입 단위면 대표 개체를 넣는다.</summary>
public readonly record struct ForecastSubject(
    ArchetypeDef Archetype, ZoneId Zone, PoiId Home, PoiId Workplace,
    ImmutableArray<PoiId> PatrolRoute, int ScheduleOffsetMinutes);

public enum SegmentKind { Move, Act, Sleep, Wait, Skipped, Replan }

/// <summary>타임라인 한 조각. 초 단위 게임 시간.</summary>
public readonly record struct ForecastSegment(
    int StepIndex, SegmentKind Kind, ActionId Action, string ActionId,
    int StartSeconds, int EndSeconds,            // 하루 시작(버킷 시간대 시각) 기준 경과
    PoiId From, PoiId To,                        // Move 만 둘 다. 그 외 To = 머무는 곳
    string Caption,                              // "대장간으로 걸어간다 (약 3분)" — Lexicon 으로 조립
    WorldFlags FlagsAfter, string Code, string Reason,   // Trace 에서
    ImmutableArray<(ItemId Item, int Delta)> Inventory); // 이 스텝의 획득·소비

public sealed record Forecast(
    ForecastSubject Subject, BucketKey Bucket, CompiledPlan Plan,
    int StartClockSeconds,                       // 버킷 시간대의 시작 시각(게임 초, 0~86399)
    ImmutableArray<ForecastSegment> Segments,    // 24시간(86,400초) 또는 플랜이 멈추는 곳까지
    LoopVerdict Loop, bool StoppedForReplan);

public static class DayForecast
{
    public const int DaySeconds = 24 * 3600;
    public static Forecast Run(MasterDataSet data, CompiledPlan plan, in ForecastSubject subject, BucketKey bucket);
    public static Forecast RunFallback(MasterDataSet data, in ForecastSubject subject, BucketKey bucket);   // data.Fallbacks.For(archetype)
    public static ForecastSubject Representative(MasterDataSet data, NpcInstanceTable instances, ArchetypeId archetype);   // 그 아키타입의 첫 개체 (code 순 배치라 결정론)
    public static ForecastSubject Of(MasterDataSet data, in NpcInstanceDef npc);
    public static (PoiId Poi, float X, float Z, int SegmentIndex) PositionAt(MasterDataSet data, Forecast forecast, int seconds);   // 이동 중이면 선형 보간
    public static string RenderMarkdown(MasterDataSet data, Forecast forecast);   // CLI · 골든용 표
}
```
- **소요 시간**: `ActionDef.Duration.Kind` 별 — Fixed `BaseSeconds` · Distance `Base + 거리(m) × PerMeterSeconds` (`speed=run` 이면 `× 0.6`) · Param `Count > 0 ? Count : Base` · UntilTime 목표 시간대 시작 시각까지 (지났으면 다음 날). **지터는 넣지 않는다.** `Npc.Sim/SimWorld.Minimal.cs:DurationSeconds` 가 기준이고 이것은 그 기대값이다.
- **심볼 → POI**: `$home`·`$workplace` 개체 값 · `$market`·`$tavern`·`$temple`·`$gate`·`$nearest_field` 같은 존에서 출입 가능한 그 유형 중 **현재 위치에서 가장 가까운 것** · `$nearest_safe` 성문 → 없으면 집 · `$nearest_shelter` 집 · `$patrol_route` 첫 순찰 지점 → 없으면 일터 → 집. `PoiBinder`(Runtime) 와 `Sim` 이 같은 규칙이다. 후보가 없으면 그 스텝은 `Code = "V3.UNREACHABLE_POI"` 로 `Skipped`.
   > 확인: 규칙을 `Npc.MasterData` 의 `PoiTable.NearestEnterable(PoiType[] types, ZoneId zone, PoiId from, ArchetypeId who)` 로 내리고 `Sim` 이 그것을 쓰게 바꿀지 (Runtime `PoiBinder` 는 틱 루프 할당 0 규약이라 그대로 둔다). 내리지 않으면 드리프트 테스트로만 묶는다.
- **상태·판정**: `PlanExplain.Trace` 의 `After`·`Code`·`Reason` 을 그대로 쓴다. ✗ 스텝은 `on_step_fail` 에 따라 — `skip` 이면 0초 `Skipped`, `fallback`·`replan` 이면 거기서 `StoppedForReplan = true` 로 멈추고 캡션 "여기서 새 계획을 요청한다".
- **고리**: `loop` 면 마지막 뒤 첫 스텝으로, 24시간을 채울 때까지. `LoopVerdict.Closed == false` 면 두 바퀴째 첫 스텝을 `Replan` 으로 표시.
- **인벤토리**: `PlanExplain.Budget` 의 획득·소비 규칙을 스텝 단위로.
- **캡션**: `Lexicon.Action` + 대상(`Lexicon.Place`/`Item`) + 시간("약 n분") — 부록 D.3 문장 틀.
- `RenderMarkdown` 은 `npc forecast archetype <id> [--bucket …]` 이 그대로 낸다 (`tools/Npc.Cli/InspectCommands.cs` 에 명령 추가 — 선택이지만 권장. CLI 와 Studio 가 같은 함수).

**테스트** (`tests/Npc.Tests/Narrative/`; 테스트 프로젝트는 `Sim` 을 참조할 수 있다):
- `Forecast_DurationsMatchSimWithoutJitter` — 대장장이 폴백을 `SimWorld` 로 돌린 스텝 소요와 예측 소요가 `±JitterPercent` 안에 든다 (`SimWorld.JitterPercent` 가 상수면 그 값으로 비교, 옵션이면 0 으로 놓고 **정확히** 같아야 한다). `> 확인: JitterPercent 의 위치`.
- `Forecast_BindsSymbolsLikeSim` — 개체 20명 × 심볼 9종에 대해 Sim 의 `TryBindPoi`(비공개면 `InternalsVisibleTo`) 와 같은 POI.
- `Forecast_CoversWholeDayWhenLoopClosed` — 닫힌 폴백은 마지막 세그먼트 `EndSeconds == 86400`.
- `Forecast_StopsAtReplanForOpenLoop` — 대조군: 고리가 안 닫히는 초안 → `StoppedForReplan`.
- `Forecast_IsDeterministic` — 두 번 돌려 `RenderMarkdown` 동일. 골든은 `tests/golden/forecast_blacksmith.md` 하나(회귀용).

**완료 판정.** `npc forecast archetype blacksmith` 가 24시간 표를 낸다. 드리프트 테스트 초록.

---

### T23 · 상황 반응 예측 `ReactionForecast` ★

**왜.** "위협이 오면 이 NPC 는?" 이 초보자가 가장 궁금해하는 것이다. `InterruptRules.TryMatch` 가 답을 안다. 초보자에게 더 중요한 것은 **"왜 그 규칙인가 · 왜 다른 규칙은 아닌가"** 다.

**구현.** `src/Npc.Narrative/ReactionForecast.cs`:
```csharp
/// <summary>사람이 고르는 상황. 플래그 몇 개와 사건 하나.</summary>
public readonly record struct Situation(WorldFlags Flags, GameEventKind? Event);

public readonly record struct RuleCheck(InterruptRule Rule, bool Matched, ImmutableArray<string> Failed);
//  Failed 예: "용기 55 ≥ 40 이라 '< 40' 에 안 맞는다" · "공격 이 허용 행동에 없다" · "전투 가능 이 아니다" · "IsInjured 가 안 서 있다"

public readonly record struct Reaction(
    InterruptRule? Matched, string Sentence,          // "물러난다 → 가까운 안전지대(성문 #1)" / "아무 규칙에도 안 걸린다 — 하던 일을 계속한다"
    PoiId TargetPoi, int Urgency, ImmutableArray<RuleCheck> Checks);

public static class ReactionForecast
{
    public static ImmutableArray<Situation> Presets { get; }   // 위협 등장 · 전투 시작 · 부상 · 적대 플레이어 · 플레이어 말 걸기 · 피해 입음 · 혼자 위협(아군 없음)
    public static Reaction Run(MasterDataSet data, ArchetypeDef def, in Situation situation, in ForecastSubject subject, PoiId from);
}
```
- 매칭은 `data.Interrupts.TryMatch(ev, flags, def, out rule)` — `GameEvent` 는 `Kind` 만 보므로 `default with { Kind = … }` 로 만든다. 사건이 없으면 `TryMatchState`.
   > 확인: `InterruptRules` 에 `TryMatch(GameEventKind kind, …)` 오버로드를 더할지 (Contracts 타입을 안 만들어도 되게). 권장: 더한다 — `MasterData` 안이라 의존 변화 없음.
- `Checks` 는 **모든 규칙**을 우선순위 순으로 돌며 `MatchesState` 의 각 조건을 따로 판정한다 (private 이면 `InterruptRules.Explain(rule, flags, def)` 를 public 으로 추가). 허용 액션 검사도 포함.
- `TargetPoi` 는 `rule.Poi` 심볼을 T22 의 바인딩으로. `Sentence` 는 `InterruptExplain.Then` + 대상 이름.
- **프리셋** 은 플래그 이름이 코드에 박히지만(`ThreatNearby` 등) 이것은 마스터데이터 값이 아니라 `WorldFlags` 열거형이다 — 허용. 없는 플래그면 컴파일이 깨진다.

**테스트.** `Reaction_ExplainsWhyEachRuleFailed` — 대장장이 + 위협: `Matched == retreat_on_threat` · `flee_on_threat.Failed` 에 "용기" · `fight_on_threat.Failed` 에 "공격" (허용 밖). `Reaction_ChangesWithCourage` — 용기 30 인 초안이면 `flee_on_threat`. (검사기 자체 시험 — 대조군이 있다.)

---

### T24 · 동작 미리보기 화면 ★

**왜.** T22·T23 을 **보게** 한다. 지도 위 인형이 하루를 걸어 다니고, 버튼 하나로 위협에 반응한다. 이것이 S5 다.

**효과.** 정의 → 행동의 인과가 눈에 보인다. 값을 바꾸면(T25) 인형이 다르게 움직이므로 "이 필드가 뭘 하는가" 를 설명 없이 배운다.

**구현.**

1. `Shared/NpcFigure.razor` — 부록 E 규격. 입력: `ArchetypeGroup Group`, `SegmentKind State`, `string Caption`, `float X, Z`, `bool Selected`. `<g transform="translate(x z)">` 안에 머리·몸·상태 배지·말풍선.
2. `Shared/BehaviorPreview.razor` — 입력: `Forecast`, `ImmutableArray<StudioPoiChoice> ZonePois`, `Func<Situation, Reaction> React`, `EventCallback<int> OnSegmentSelected`.
   - 위: `ZoneMap` + `NpcFigure` 레이어 + 경로 `<polyline>`(하루 동안 지나는 길, 연하게) + 반응 시 목표 POI 강조.
   - 아래: **시간 스크러버** — 24시간 띠(`DayTimeline` 예측 모드: 세그먼트를 액션 카테고리 색으로) + 현재 시각 표시 + 재생/일시정지 + 배속(×60 · ×600 · ×3600).
   - 오른쪽: **현재 스텝 카드**(순번 · 캡션 · 남은 시간 · 상태 칩 `WorldFlagTable.Format` · 소지품 변화) + 전체 스텝 목록(클릭 → 그 시각으로).
   - **상황 버튼** 줄(`ReactionForecast.Presets`) + "상황 겹치기" 토글. 누르면 `React` → 인형이 목표 POI 로 **뛰는** 반응 애니메이션(2초 고정) + 상태 배지 + **"왜" 패널**: 규칙 표(우선순위 순), 걸린 규칙 초록, 나머지는 실패 조건 문장. "돌아가기" 로 재개.
   - 상단 lead: "**기대 경로**다. 실제 서버는 스텝마다 ±% 흔들리고, LLM 이 상황에 맞는 새 계획을 주며, 경로는 게임서버가 계산한다."
3. **재생은 클라이언트에서** — `wwwroot/behavior-preview.js` (정적 파일, 빌드 없음):
   - Blazor 가 `JSRuntime.InvokeVoidAsync("behaviorPreview.load", elementId, keyframesJson)`. 키프레임: `[{t: 초, x, z, seg: 세그먼트 첨자, state}]` — 세그먼트 경계마다 1개 + Move 는 시작·끝 2개.
   - JS 가 `requestAnimationFrame` 으로 시각을 진행하며 `<g id="npc-figure">` 의 `transform` 과 시계 라벨·스크러버 위치·현재 세그먼트 강조(`data-seg`) 를 바꾼다. 세그먼트가 바뀔 때만 `DotNetObjectReference.invokeMethodAsync("OnSegment", seg)` — **프레임마다 서버 왕복 없음**.
   - Blazor 재렌더가 인형을 되돌리지 않도록 인형 `<g>` 는 `@key` 고정 + `BehaviorPreview.ShouldRender` 가 재생 중이면 지도 부분을 다시 그리지 않는다 (재생 컨트롤·카드는 자식 컴포넌트).
   - 스크러버 드래그도 JS 가 처리하고 놓을 때만 서버에 시각을 보낸다.
4. 배치: `/archetypes/{id}?tab=forecast` (대표 개체 — "#1234 기준" 표시, 다른 개체 고르기 드롭다운) · `/npcs/{id}` (실제 개체 · 순찰로 · 일정 오프셋 반영) · 개요 탭 §7 축소판(재생 버튼만) · T11 편집기 옆 · T27 버킷 셀 클릭.
5. 버킷 선택기(시간대 · 지역 상태 · 기후) — 폴백은 버킷과 무관하지만 **시작 시각·초기 플래그**가 버킷에서 나온다. T27 전에는 폴백만.

**테스트.** 없음(화면). JS 는 손으로: 재생 · 배속 · 드래그 · 상황 버튼 · 탭 전환 후 복귀.

**완료 판정.** S5 통과. 재생 중 브라우저 개발자 도구 네트워크 탭에 SignalR 트래픽이 세그먼트 전환 때만 있다.

---

### T15 · 검증 오류를 사람 말로 + 바로가기

**구현.**
1. `Services/IssueGuide.cs` — 코드 → 초보자 제목 (V1~V15 + `LOAD`). `IssueGuide_CoversEveryFixHintCode` 로 `FixHints.Codes` 전수 강제.
2. `Services/IssueLocator.cs` — `StudioIssue` → `StudioLink?`: `/archetypes/3/population_weight` → 첨자 3 의 `id` → `/archetypes/{id}?field=population_weight`; `pois.json` → `/places/{zone}?poi=`; `npc_overrides.json` → `/npcs/{id}`; `fallback_plans.json` → `/archetypes/{id}?tab=fallback`; 그 외 `/files/{file}`.
3. `IssuePanel` — 제목 · 파일 칩 · "고치러 가기" · 접으면 원문. 저장 실패 토스트에 첫 오류 링크.
4. 폼: `?field=` 로 스크롤·강조 + 인라인 오류.
5. `/issues` 전체 목록 + (T29) 건강 진단 절.

**테스트.** `IssueLocator_ResolvesArrayIndexToId` · `IssueLocator_FallsBackToFileView`.

---

### T16 · 파급 패널

**구현.**
1. `StudioSaveResult` 에 `ImmutableArray<string> Files`. 세션이 쌓는다.
2. `Shared/ImpactPanel.razor` — `ImpactAnalyzer.Of(dir, session.ChangedFiles)` → 카드: 구조 해시(게임서버 재배포) · 프리픽스(플랜 전량 무효 → 프리베이크 명령 표시만) · Partial(`--resume`) · 파생물 낡음(T17 버튼) · 재기동 필요(폴백·인터럽트).
3. 시작 화면 배지 = `DerivedArtifacts.Stale(dir)` + 세션 파급. **git 을 부르지 않는다.**

---

### T10 · 아키타입 폼 편집기

**구현.**
1. `Services/StudioForms.cs`:
   ```csharp
   public sealed record StudioArchetypeForm(string Id, string Desc, ImmutableArray<string> AllowedActions, string HomePoiType, string? WorkplacePoiType,
       ImmutableArray<string> PrimaryRecipes, int Diligence, int Sociability, int Courage, int Greed, ImmutableArray<string> DefaultGoals,
       ImmutableArray<StudioInventoryLine> InitialInventory, ImmutableArray<TimeOfDay> DutyHours, bool CombatCapable, double PopulationWeight);
   public static readonly ImmutableDictionary<string, string> JsonKeys;   // 속성 → JSON 키. T20 드리프트가 이 표를 본다
   ```
2. **저장** `SaveArchetypeForm(form)` — 항목 텍스트를 `ItemRange` 로 뽑고, `LoadArchetypeForm(id)` 와 비교해 **바뀐 필드만** `JsonSurgeon.SetTopLevel`. 배열·객체는 `StudioJsonFormat.Array/Object(raw, indent)` 로 파일 서식(원소마다 줄바꿈, 항목 들여쓰기 + 2) 을 재현. 그 뒤 기존 `SaveArchetype(id, itemJson)`.
   > 확인: `JsonSurgeon.SetTopLevel` 이 없는 속성을 추가하는가 (`duty_hours` 는 대장장이에 없다). 못 하면 "마지막 속성 뒤에 삽입" 을 더하고 테스트.
3. **미리보기** `PreviewArchetypeForm(form)` → 기존 `PreviewArchetype(id, itemJson)` + **T25 예측**.
4. 위젯 — `desc` textarea(글자 수 · TODO 경고) · `population_weight` 숫자 + 인구 환산 + 합 미리보기(V5) + "인구 재배분" 버튼(`WeightRebalancer.Propose` 3안 표 → 여러 항목 `SetInArrayItem`) · `home/workplace_poi_type` 드롭다운(subtype 한국어, 정원 vs 인구 즉시) · `duty_hours` 6 토글(Guard/Patrol 경고 V12) · `traits` 슬라이더 4개 + "이 값이면 걸리는 돌발 반응"(T23) · `allowed_actions` `ActionMatrix` 체크(폴백이 쓰는 행동 해제 시 경고 V8) · `primary_recipes` 다중 선택 · `default_goals` 태그 · `initial_inventory` 표 · `combat_capable` 토글(반응 변화 문장) · `id`·`code`·`name_key`·`fallback_plan` 읽기 전용 칩.
5. "JSON 으로 보기" 토글(고급) — 같은 초안. **저장하지 않은 변경** 이 있으면 떠날 때 확인(`NavigationManager.RegisterLocationChangingHandler`).

**테스트.** `SaveArchetypeForm_WithoutChanges_IsByteIdentical`(전 아키타입) · `SaveArchetypeForm_AddsDutyHoursWhenAbsent` · `SaveArchetypeForm_RejectsV5` · `ArchetypeForm_EveryFieldHasFieldGuide`(`JsonKeys` 전수).

**완료 판정.** JSON 탭 없이 `desc`·`duty_hours` 를 바꿔 저장, git diff 가 그 두 줄.

---

### T25 · 편집 ↔ 예측 연동

**왜.** "이 값이 뭘 하는가" 를 가장 빨리 가르치는 것은 바꿔 보는 것이다.

**구현.** 편집 탭 오른쪽 패널을 (기존 실시간 설명 md 대신) **`BehaviorPreview` 축소판 + 반응 요약**으로. 입력 400ms 뒤 `PreviewArchetypeForm` → 초안 `ArchetypeDef` → `DayForecast.RunFallback(data, Representative, bucket)` + `ReactionForecast.Run(위협 프리셋)` 다시 계산 → JS `behaviorPreview.load` 로 키프레임 교체(재생 위치 유지). 성향 슬라이더를 끌면 반응 문장이 즉시 바뀐다. 실시간 설명 md 는 "전문가" 접이식으로 내린다.

**테스트.** 없음 (T22·T23 이 지킨다).

---

### T34 · 저장 전 사람 말 요약 (`DefinitionDiff`)

**왜.** 저장 버튼 앞에서 "내가 뭘 바꾼 거지" 를 확인해야 초보자가 저장을 누른다. 파일 파급(T16) 은 답이 아니다.

**구현.** `src/Npc.Narrative/DefinitionDiff.cs` — `Describe(MasterDataSet data, ArchetypeDef before, ArchetypeDef after) → ImmutableArray<string>`:
"허용 행동 +순찰 −잡담" · "용기 55 → 30 — 위협에 **물러나기** 대신 **도망**(`flee_on_threat`)" (T23 두 번 돌려 비교) · "근무 시간대 없음 → 아침·한낮" · "인구 60명 → 20명 (다른 직업에서 뺀 것 없음 → 합 0.992 ✗)" · "일터 대장간 → 양봉장 (정원 40 < 인구 60 ✗)". 폴백용 `Describe(before, after plan)` 도 — "스텝 3 이 거래 → 채집으로, 이 뒤 4번 스텝의 전제가 깨진다".
저장 모달: 요약 + 파급(T16) + "저장" / "취소". `npc diff --semantic` 이 값 단위도 내게 확장하면 CLI 도 같이 좋아진다 (선택).

**테스트.** `DefinitionDiff_MentionsReactionChange` — 용기 55 → 30 의 요약에 `flee_on_threat` 이 들어 있다.

---

### T11 · 폴백 하루 편집기

**구현.**
1. `StudioPlanStep(string Action, ImmutableDictionary<string,string> Args, int TimeoutSeconds)` · `StudioFallbackForm(Id, Archetype, Goal, Steps, Loop, OnStepFail)`. 읽기는 `JsonNode`, 쓰기는 텍스트(`JsonSurgeon`).
2. **실시간 판정·예측** `PreviewFallback(form)` — 후보 파일 → `PlanTable.Parse(candidate, data)` → `For(code)` → `PlanExplain.Trace` + `DayForecast.Run` → 스텝 카드 ✓/✗ + 지도 재생(T24). 400ms 디바운스.
3. 스텝 위젯 — 액션 드롭다운(허용만, 한국어) · 인자는 `ActionDef.Params` 로 (`PoiRef` 심볼 드롭다운 — **이 지역에서 바인딩 가능한 것만 활성**, 불가능하면 회색 + 이유 · `ItemRef` 아이템/레시피 · `Enum` `EnumValues` · `Int` `Min~Max` · 그 외 텍스트) · 타임아웃(분 환산, 기본 `DefaultTimeoutSeconds`, **예측 소요보다 짧으면 경고** "이동에 약 4분 걸리는데 상한이 3분이다") · ↑↓× · 추가. `loop`·`on_step_fail` 읽기 전용 칩.
4. 저장 `SaveFallbackForm` — 키 순서 `action → args → timeout_s` 고정.
5. 위치: `/archetypes/{id}?tab=fallback`. 마법사 ④ 가 같은 컴포넌트.

**테스트.** `SaveFallbackForm_WithoutChanges_IsByteIdentical` · `PreviewFallback_FlagsUnmetPrecondition`(대조군) · `SaveFallbackForm_RejectsDisallowedAction`.

---

### T12 · 새 직업 마법사

**구현.** `/archetypes/new` 5단계:

| 단계 | 입력 | 즉시 검사 · 안내 |
|---|---|---|
| ① 이름·직군 | id · 한국어 이름(`ko-KR`·`en-US` 의 `npc.<id>` 둘 다 — V14) · `desc`(필수, TODO 금지) · **직군 틀**(`Lexicon.Group` 별 카드: 생산/채집/상업/치안/종교/주민/특수 — 고르면 ② 의 후보를 그 직군으로 좁히고 기본 행동·근무 시간대 틀을 제안) | 중복 · 빈 설명 |
| ② 닮은 직업·인구 | 복제 원본(직군 안에서) · 가중치 → 인구 · `WeightRebalancer.Propose` 3안 표 (기본 Largest) · **"이 직업은 어느 지역에 몇 명 살게 되나" 예측**(T33 의 계산: 일터 subtype 의 지역별 정원) | V5 |
| ③ 일터 | 기존 유형(정원 vs 인구) **또는** 새 장소(T13 인라인, 같은 트랜잭션) | V10 |
| ④ 하루 일과 | 원본 폴백 초안 → T11 편집기. 원본 레시피·아이템 노란 표시 · **오른쪽에 예측 재생**(T24) | V8 · ✓ |
| ⑤ 확인 | 요약 · **T34 요약** · 파급표 · 생성 뒤 할 일(T17 버튼 · 프리베이크 명령 · 재배포) | — |

서비스 `CreateArchetype(StudioNewArchetype draft)` — 트랜잭션: `archetypes.json`(새 항목 + 재배분 줄들) · `fallback_plans.json` · `context_buckets.json` · `localization/ko-KR.json`·`en-US.json` · 선택 `pois.json`.
> 확인: `ValidateAndWrite` 가 `localization/…` 상대 경로 후보를 받도록 손본다 (지금은 복사만 한다).

**테스트.** `CreateArchetype_Wizard_EndToEnd`(양봉가). 기존 `CreateArchetype_AddsFallback…` 은 이것으로 대체.

---

### T17 · 파생물 재생성 버튼

**구현.** `Services/GeneratorRunner.cs` (Singleton) — 저장소 루트(`masterdata/..`) 에 `tools/gen_npcs.cs` 가 있을 때만 활성. `dotnet run <status.Generator>` (`DerivedArtifacts.Known` 의 값 그대로) · `WorkingDirectory = root` · stdout 스트리밍 · `SemaphoreSlim(1,1)` · 끝나면 `DerivedArtifacts.Check` + `Reload`. 확인 모달에 정확한 명령·예상 시간·"npc_instances.json 이 다시 만들어진다 — 손편집은 npc_overrides.json 에만 남는다". 연습장(T30) 이면 그 디렉터리를 인자로. 읽기 전용 비활성.

**테스트.** `GeneratorRunner_RefusesOutsideRepo`.

---

### T08 · 장소·지역 탐색 화면

**구현.** `LoadPlaces()` → 지역 요약(Id · 이름 · 장소 n · 정원 · 거주 · 근무 · 인접) · `LoadZone(id)` → POI 목록(`OpenFrom/To`·`Grants`·`AllowedArchetypes`·`Residents`·`Workers`). `/places` 지역 카드 → `/places/{zone}` `ZoneMap` + 목록(유형 필터) → 상세(한국어 유형 · 정원 · 사는/일하는 NPC · 여는 시간 · 일할 수 있는 직업 · 자원). **이 지역에 사는 NPC 인형**(T24 인형을 정적 배치, 집 위치에 직군 색 점) 으로 "어디에 누가 사는가" 를 보여 준다.

**테스트.** `LoadPlaces_CountsResidentsFromInstances` — 합 5,000.

---

### T13 · 장소(POI) 추가 폼

**구현.** `/places/{zone}` "새 장소": id 제안(`{subtype}_{NNN}_{zoneCode:00}`) · 유형/세부 유형 · 정원 · 위치(`ZoneMap` 클릭 또는 숫자) · 여는 시간 · `grants`(같은 subtype 복사) · `allowed_archetypes` · `resources`. `code = CodeAllocator.Next(dir, "pois.json")` · `AppendToArray` → `ValidateAndWrite`. 저장 후 파급: Partial + **거리표 낡음** → T17 버튼.
> 확인: `zones.json` `capacity` 를 같이 올릴지 — 5장 표본은 안 올린다. `gen_npcs.cs` 가 읽는지 보고 결정.

**테스트.** `AddPoi_AppendsWithNextCodeAndMarksDistancesStale`.

---

### T26 · 마을 전체 지도

**왜.** "이 데이터가 마을 하나다" 를 한 장으로. 시작 화면의 얼굴이고, 지역 → 장소 → NPC 로 내려가는 입구다.

**구현.** `/map` + 시작 화면 축소판:
- **절대 좌표가 없다** (T07). 지역 12개를 **타일**로 — 배치는 `zones.json` 의 `adjacent` 그래프에서 결정론 레이아웃(간단: code 순 3×4 격자, 인접은 곡선으로; 더 낫게: `town_center` 를 가운데 두고 BFS 홉 수로 동심 배치 — 결정론이면 어느 쪽이든 된다). 타일 안은 `ZoneMap` 축소판.
- 타일 라벨: 한국어 이름 · 인구(거주) · 대표 직군 색 띠(그 지역 거주자의 직군 분포). hover 에 장소 수·정원.
- 클릭 → `/places/{zone}`. 직업 상세에서 "이 직업이 사는 지역" 을 타일 강조로.
- 라이브(T28) 가 붙으면 타일 안에 실제 NPC 점.

**테스트.** `ZoneLayout_IsDeterministic` — 같은 `zones.json` 이면 같은 좌표 (레이아웃 함수는 `Narrative` 에 `ZoneLayout.Compute(ZoneTable)` 로 — 순수 함수).

---

### T14 · 개별 NPC 폼 개선

**구현.** 필드 설명(경계 반경 "게임서버가 쓴다, NPC 서버는 저장만" · 대화 프로필 "대화 서비스가 읽는다" · 일정 오프셋 "같은 직업이 일제히 움직이는 것을 막는 분산에 더한다") · 순찰로는 `ZoneMap Interactive` 클릭 순서 = 방문 순서(목록은 보조) · 세력은 `desc` 표시 · 대화 프로필 datalist 제안 · 저장 뒤 **T24 재생**에 `$patrol_route` 첫 지점이 반영된 것을 보여 준다.

---

### T09 · 돌발 반응 화면 + 반응 예측 연결

**구현.** `/interrupts` — `InterruptExplain.Ordered` 표(우선순위 · 규칙 · 언제 · 무엇을 · 긴급도 · **걸리는 직업**) · 충돌 경고 · lead("LLM 이 만들지 않는다 …"). 오른쪽 **"이 직업이면?"** — 직업 드롭다운 + 상황 프리셋 → `ReactionForecast.Run` 의 `Checks` 표 (규칙마다 ✓ / 실패 조건). 편집은 JSON 고급 모드.

---

### T29 · 건강 진단 `ArchetypeLint`

**왜.** V1~V13 은 "기동이 되는가" 만 본다. 초보자는 검증을 통과하고도 이상한 정의를 만든다.

**구현.** `src/Npc.Narrative/ArchetypeLint.cs` — `Run(MasterDataSet data, ArchetypeDef def, NpcInstanceTable? instances) → ImmutableArray<LintFinding(string Code, string Title, string Detail, string Fix, string Field)>`. 규칙(전부 결정론, 차단 아님):
- `L1_TODO_DESC` 설명이 `TODO` 로 시작 · `L2_ZERO_POPULATION` `round(weight × 5000) == 0` · `L3_BLAND_TRAITS` 네 성향이 전부 45~55 · `L4_FEW_ACTIONS` 허용 행동 < 6 · `L5_UNBINDABLE_IN_ZONE` 폴백이 쓰는 심볼(`$tavern` 등) 을 이 직업이 사는 지역 중 하나라도 바인딩 못 한다 (거주 지역은 `instances` 에서; 없으면 일터 subtype 의 지역으로) · `L6_DUTY_SLEEP` 근무 시간대에 폴백이 `Sleep` 이다 (`npc timeline` 이 보던 것) · `L7_TIMEOUT_TOO_SHORT` 예측 소요(T22) > `timeout_s` 인 스텝 · `L8_CAPACITY_MARGIN` 일터 정원 여유 < 10% · `L9_LOOP_OPEN` `LoopVerdict.Closed == false` · `L10_NO_REST` 24시간 예측에 `IsRested` 를 세우는 스텝이 없다.
- Studio: 개요 ⑩ 절 + `/issues` 의 "주의" 절 + `IssuePanel` 노란 배지. `npc card` 에도 절을 더할지는 `> 확인` (카드 골든이 바뀐다 — 권장: `npc lint archetype <id>` 별도 명령).

**테스트.** `Lint_FindsPlantedProblems` — 대조군: TODO 설명 · 용기 50/50/50/50 · 고리 안 닫힘 초안 → 각 코드가 나온다. `Lint_IsQuietOnShippedData` — 현재 40개 아키타입에서 L1·L2·L9 는 0건 (다른 코드는 있어도 된다 — 있으면 실제 발견이다. 결과를 이 문서에 적는다).

---

### T33 · 인구 분포 차트 + 지역 배치 예측

**구현.** `/archetypes` 목록 상단에 인구 막대(40개, 직군 색, 클릭 → 상세). 개요 ② 절: "지역별 몇 명" — 있는 직업은 `instances` 집계, **새 직업(명단 재생성 전)** 은 일터 subtype 의 지역별 정원 비례로 **예측**("명단을 다시 만들면 대략 이렇게 배치된다 — `gen_npcs` 는 일터 가까운 집을 먼저 준다"). `Narrative` 에 `PlacementForecast.ByZone(data, def, population)`.

**테스트.** `PlacementForecast_SumsToPopulation`.

---

### T31 · 따라하기 체크리스트

**구현.** `HelpDrawer` 의 "첫 10분": ① 대장장이를 연다 ② 하루 재생을 누른다 ③ 위협 등장을 누른다 ④ 편집 탭에서 용기를 30 으로 내리고 반응이 바뀌는 것을 본다 ⑤ 저장하지 않고 닫는다 ⑥ 연습장을 만든다 ⑦ 연습장에서 저장해 본다. 각 항목은 `StudioSession` 의 이벤트(페이지 진입 · 재생 시작 · 상황 버튼 · 폼 값 변경 · 연습장 생성 · 저장) 로 **자동 체크**. 진행은 `localStorage` 가 아니라 세션(회로) 에만 — 새로고침하면 초기화돼도 된다. 두 번째 코스 "새 직업 10분" 은 S2 절차.

---

### T32 · 전역 검색

**구현.** 상단 바 입력 한 칸 — 직업(한국어·id) · NPC(번호) · 장소(id·한국어 유형·지역) · 규칙(id) 을 한 목록에. `StudioWorkspace.SearchIndex()` 를 카탈로그 로드 때 한 번 만든다(`ImmutableArray<(string Kind, string Key, string Label, string Url)>`). `Ctrl+K`.

---

### T27 · 버킷별 계획 (`planstore`)

**왜.** 실제 서버에서 NPC 행동의 **98.67%** 는 미리 구운 플랜(캐시 히트) 에서 나온다. 폴백은 최후 보루다. "이 직업이 폭풍 치는 전쟁 중 오후에 무엇을 하나" 의 참 답은 `planstore` 에 있다 — 그리고 그것이 **LLM 이 준 하루**다.

**구현.**
1. `StudioOptions --planstore <dir>` (기본 `./planstore`, CLI 와 같다). 없으면 탭을 숨기고 "미리 구운 플랜이 없다 — 17장(프리베이크)" 안내.
2. `Services/PlanStoreReader.cs` — **`Npc.Planning` 을 참조하지 않는다.** 파일을 직접 읽는다: `pinned/*.json` 과 `plans/*.json`(C-03 이면 `<sha8>/plans/`) — 파일 형식은 `{bucket, archetype, origin, plan:{…}}`. `plan` 을 `SchemaValidator.Validate(json, out PlanDocument)`(Core) → `PlanCompiler.Compile(document, bucket, id, data /* IPlanVocabulary */, origin)`(Core) → `CompiledPlan`. 버킷 문자열 `acolyte@Afternoon.Alert.Storm` 은 `BucketKey.Format` 의 역 — Core 에 `TryParse` 가 없으면 추가한다.
   > 확인: `planstore/manifest.json` 의 필드(생성·핀·폴백 대체·반려 상태) — `Npc.Planning/PlanStore.cs` 의 매니페스트 기록 코드를 읽고, 셀 색은 매니페스트를 근거로 한다 (카드가 그러듯 "생성·핀·반려 내역은 manifest 가 근거").
3. `Shared/BucketGrid.razor` — 행 시간대 6 × 열 (지역 상태 4 × 기후 3) = 72칸. 색: 핀(진초록) · 생성(초록) · 폴백 대체(회색) · 반려(빨강) · 없음(빈칸). hover 에 goal. 클릭 → T24 재생(그 플랜, 그 버킷의 시작 시각·초기 플래그). 상단 요약 "72 중 생성 51 · 핀 3 · 없음 18".
4. 스텝 카드에 `reasoning`·`narrate` 가 파일에 있으면 접이식으로 (LLM 이 왜 그렇게 짰는지).

**테스트.** `PlanStoreReader_CompilesPinnedPlans` — `planstore/pinned/*.json` 전부 컴파일되고 `Trace` 에 ✗ 가 없다 (핀은 사람이 검수한 것이다 — ✗ 가 있으면 실제 발견이다).

---

### T28 · 라이브 관찰 (예측 vs 실제)

**왜.** 예측은 기대 경로다. 실제를 같은 지도에 놓으면 "예측이 맞는가" 가 보이고, 실습서 3장(눈으로 본다) 이 웹에서 된다.

**구현.**
1. `StudioOptions --server http://127.0.0.1:<port> [--token …]` (A-06 토큰). **`Npc.Host` 를 참조하지 않는다** — `HttpClient` 로 B-08 대시보드 엔드포인트(`/npcs` · `/npc/{id}` · `/metrics` · `/buckets`). 응답 모양은 `docs/openapi.json` 이 정답이다.
2. `/live` — 연결 상태 · `/npcs` 를 1초마다 폴링 → T26 타일에 실제 NPC 점(현재 존·POI·액션) · NPC 클릭 → `/npc/{id}` 상세(현재 플랜·스텝·플래그) 를 T24 화면에 **예측 인형(연하게) + 실제 인형(진하게)** 로 겹친다. "지금 스텝이 예측 3번 · 실제 3번 ✓" / "실제는 재계획 중".
3. 재생(리플레이 파일) — `replays/` 를 읽어 같은 화면에 돌리는 것은 `> 확인`(형식이 `Npc.Host` 전용이면 미룬다).

**테스트.** `LiveClient_ParsesNpcsSample` — `docs/openapi.json` 의 예시 응답을 파싱한다 (드리프트).

---

### T21 · 액션 카탈로그 읽기 화면

`/actions` — 카테고리별 카드(한국어 · id · `desc` · 전제/세움 플래그 · 기본 타임아웃 · 소요 시간 모델(Fixed/거리/인자/시간대) · 인자 · 허용 직업 n). lead "40개 상한 · 37개. 추가는 프롬프트를 바꾸는 고급 작업(9장)". 편집은 `/files/actions.json`.

---

### T18 · 되돌리기

`AtomicWrite` 직전 원본을 `%LOCALAPPDATA%\NpcStudio\backup\<시각>\<file>` 로 (**`masterdata/` 안에 두지 않는다** — `CopyMasterData`·`ContentHash` 가 폴더 전체를 본다). 변경 이력 → "이 파일 되돌리기" 는 `ValidateAndWrite` 로 (되돌린 상태도 검증). 30일 지난 백업 삭제. 연습장(T30) 이 있으면 되돌리기 필요가 줄지만 원본 편집 때 여전히 필요하다.

**테스트.** `Undo_RestoresPreviousBytes`.

---

### T19 · 매뉴얼·README 재작성

`docs/npc_studio_manual.html`: 1 처음이라면(용어 · 화면 지도) · 2 따라하기 S1 · **3 따라하기 S5 예측**(재생 · 상황 · 값 바꿔 보기 · "기대 경로" 의 뜻) · 4 S3 · 5 S4 · 6 S2 · 7 S6 상황별 · 8 저장은 어떻게 안전한가 · 연습장 · 파급 · 9 고급(JSON · 전문가 카드 · CLI 대응표 — `card`·`explain`·`forecast`·`lint`·`validate`·`regen`) · 10 문제 해결. README Studio 절은 시나리오 6줄. 튜토리얼 2·3·5·7장 끝에 "Studio 로 보기" 상자 한 줄(선택).

---

### T20 · 테스트 정비

**남기는 것:** 드리프트(`Lexicon_CoversEveryAction/Group` · `FieldGuide_CoversEverySchemaProperty` · `IssueGuide_CoversEveryFixHintCode` · `*Form_EveryFieldHasFieldGuide` · `Forecast_DurationsMatchSimWithoutJitter` · `Forecast_BindsSymbolsLikeSim` · `LiveClient_ParsesNpcsSample`) · 불변식(`*_WithoutChanges_IsByteIdentical` · `Forecast_IsDeterministic` · 골든 `forecast_blacksmith.md`) · 검사기 자체 시험(`PreviewFallback_FlagsUnmetPrecondition` · `Reaction_ExplainsWhyEachRuleFailed` · `Lint_FindsPlantedProblems`) · 종단(`CreateArchetype_Wizard_EndToEnd` · `AddPoi_*` · `Sandbox_*` · `PlanStoreReader_CompilesPinnedPlans`).
**지우는 것:** `CreateArchetype_AddsFallbackAndKeepsValidationGreen`(종단이 덮는다). `LoadCatalog_*` 의 개수 단언은 `> 0` 로.
**bUnit 은 넣지 않는다.** 마법사 단계 상태 기계가 필요하면 `WizardState` 순수 클래스로.

---

## 6. 구현 순서와 커밋 단위

| 묶음 | 태스크 | 커밋 예시 |
|---|---|---|
| 1 기반 | T01 → T02 → T03 → T04 → T30 | `studio: 화면을 URL 라우팅과 페이지 컴포넌트로 분리했다` · `narrative: 액션·직군 표기와 필드 사전 FieldGuide 를 추가했다` · `studio: 시작 화면·도움말 서랍·연습장을 추가했다` |
| 2 읽기 | T05 → T06 → T07 | `narrative: ArchetypeCard 계산을 ArchetypeFacts 로 뽑았다 (출력 불변)` · `narrative: PlanExplain.Trace 로 스텝 판정을 구조화했다 (출력 불변)` · `studio: NPC 지도와 개요 보기` |
| 3 **예측** | T22 → T23 → T24 | `narrative: DayForecast — 정의에서 24시간 동작을 예측한다` · `narrative: ReactionForecast — 상황별 인터럽트 반응과 이유` · `studio: 동작 미리보기 — SVG 인형 · 지도 재생 · 상황 버튼` |
| 4 안전 | T15 → T16 | `studio: 검증 오류에 사람 말 제목과 바로가기` · `studio: 저장 뒤 파급 패널` |
| 5 편집 | T10 → T25 → T34 → T11 | `studio: 아키타입 폼 편집기 — 무변경 저장은 바이트 동일` · `studio: 편집 중 실시간 동작 예측` · `narrative: DefinitionDiff — 변경을 사람 말로` · `studio: 폴백 하루 편집기` |
| 6 생성 | T12 → T17 → T08 → T13 → T26 | `studio: 새 직업 마법사 5단계` · `studio: 파생물 재생성 버튼` · `studio: 지역·장소 탐색과 새 장소 폼` · `studio: 마을 전체 지도` |
| 7 마무리 | T14 → T09 → T29 → T33 → T31 → T32 | `narrative: ArchetypeLint 건강 진단` … |
| 8 확장 | T27 → T28 → T21 → T18 | `studio: 버킷별 미리 구운 계획 격자` · `studio: 라이브 관찰` |
| 9 문서·테스트 | T19 → T20 → S1~S6 판정 | `docs: Studio 매뉴얼을 시나리오 중심으로` · `tests: Studio·Narrative 드리프트·종단 테스트 정비` |

각 커밋 전: `dotnet build -c Release` 경고 0 · `dotnet test "--filter Category!=Golden&Category!=Gate&Category!=Load"` · `dotnet format --verify-no-changes` · Studio 를 띄워 바뀐 화면을 브라우저에서 직접 눌러 본다 (포트 25056). Narrative 를 건드린 커밋은 골든(`Card_*`·`PlanExplain_*`) 이 초록인지 반드시.

---

## 7. 다음 세션 시작 절차

1. 체크리스트에서 **첫 미완료 태스크**. 의존이 끝났는지 본다.
2. `CODEMAP.md` §1 Studio 줄과 이 문서 §5 의 해당 "구현" 을 읽는다. 예측 태스크(T22~T29) 는 **부록 D·E** 도.
3. 손대는 파일의 테스트(`tests/Npc.Tests/Studio/`, `tests/Npc.Tests/Narrative/`) 를 먼저 읽는다.
4. `> 확인:` 항목이 있으면 그 파일을 열어 결정하고 **이 문서에 적는다**.
5. 구현 → 테스트 → 브라우저 확인 → 커밋 → 체크 → `working_log.md`.
6. 한 태스크가 끝나면 멈추고 보고한다. 다음으로 넘어가기 전에 사용자 확인.

---

## 부록 A · 필드 사전 초안 (`FieldGuide`)

문장은 초보자용이다. 검증 코드는 `Related` 에만. **T02 의 입력이다** — 코드로 옮긴 뒤 이 부록은 지워도 된다.

### `archetypes.json` — 직업(아키타입)

| 필드 | 제목 | 무엇 | 왜 · 어디에 쓰이나 | 주의 | Related |
|---|---|---|---|---|---|
| `id` | 직업 ID | 다른 파일이 이 직업을 가리킬 때 쓰는 영문 이름 | 폴백·장소 허가·NPC 명단이 이 이름으로 연결된다 | 만든 뒤 바꿀 수 없다 | V3 |
| `code` | 직업 번호 | 0 부터 붙는 번호 | 미리 구운 플랜 2,880개가 이 번호 위에 있다 | **절대 바꾸지 않는다** | V1 |
| `name_key` | 표시 이름 키 | `npc.<id>` | `localization/ko-KR.json` 이 실제 문구를 가진다 | 키를 지우면 V14 | V14 |
| `desc` | 설명 | 이 직업이 어떤 존재인지 한두 문장 | **LLM 에게 그대로 전달된다.** 하루 계획의 분위기가 여기서 나온다 | `TODO:` 로 두면 이상하게 행동한다. 플레이어가 쓴 문장을 넣지 않는다 | — |
| `allowed_actions` | 할 수 있는 행동 | 쓸 수 있는 행동 목록 | LLM 은 이 안에서만 계획을 짠다. 돌발 반응도 여기 없는 행동은 건너뛴다 | 경계 근무·순찰을 넣으려면 근무 시간대가 필요. 하루 일과가 쓰는 행동을 빼면 저장 거절 | V4 V8 V12 |
| `home_poi_type` | 사는 곳 유형 | 집으로 배정받을 장소 유형 | 명단을 만들 때 빈 자리를 배정한다 | 보통 `house` | V10 |
| `workplace_poi_type` | 일터 유형 | 일하러 갈 장소 유형. 없으면 `null` | 하루 일과의 `$workplace` 가 이것 | 그 유형 장소 정원 합 ≥ 인구 | V10 |
| `primary_recipes` | 주력 제작품 | 주로 만드는 것 | 하루 일과·LLM 이 우선한다 | `items.json` 에 레시피가 있어야 | V3 |
| `traits` | 성격 | 근면·사교·용기·탐욕 0~100 | LLM 에 전달되고, 돌발 반응 규칙이 이 값으로 갈린다 | 바꾸면 걸리는 돌발 반응이 바뀐다 — 화면이 미리 보여 준다 | — |
| `default_goals` | 기본 목표 | 늘 신경 쓰는 것 2~3개 | LLM 프롬프트 가변 부분 | 많으면 300 토큰 예산을 먹는다 | — |
| `initial_inventory` | 시작 소지품 | 가지고 시작하는 아이템 | 첫 스텝의 전제(도구가 있나) | 도구가 없으면 "일하기" 가 막힌다 | — |
| `duty_hours` | 근무 시간대 | 근무하는 시간대 | 이 때만 "근무 중" 이 서고 경계 근무·순찰이 가능 | 비우고 경계 근무·순찰을 허용하면 재계획이 폭주한다 | V12 |
| `fallback_plan` | 하루 일과 ID | LLM 없이도 도는 기본 하루 | LLM 이 전부 실패해도 사는 이유 | 직업마다 하나 | V7 |
| `combat_capable` | 싸울 수 있나 | 무기를 들고 맞설 수 있나 | 위협 시 맞설지·물러날지·도움을 부를지가 갈린다 | — | — |
| `population_weight` | 인구 비율 | 전체 중 비율 (0.012 ≈ 60명/5,000) | 명단을 만들 때 이 비율로 나눈다 | **합이 정확히 1.0**. 늘리면 다른 직업에서 뺀다 | V5 |

### `npc_overrides.json` — 개별 NPC 설정

| 필드 | 제목 | 무엇 | 왜 | 주의 | Related |
|---|---|---|---|---|---|
| `id` | NPC 번호 | 1~5,000 | 이 번호에만 적용 | 명단을 다시 만들어도 유지 | V13 |
| `patrol_route` | 순찰로 | 차례로 들를 장소 (최대 4) | `$patrol_route` 가 첫 지점으로 | **같은 지역** · 들어갈 수 있는 곳 | V13 |
| `aggro_radius_m` | 경계 반경 | 몇 m 안에서 반응하나 | 게임서버가 쓴다. NPC 서버는 저장해 넘길 뿐 | 0~200 | V13 |
| `faction` | 세력 | `factions.json` 의 세력 | 명령에 찍혀 게임서버가 적·아군 판정 | — | V13 |
| `dialogue_profile` | 대화 프로필 | 대화 서비스가 읽는 말투 | NPC 서버는 저장만 | 자유 문자열 | — |
| `schedule_offset_min` | 일정 오프셋 | 시간대가 바뀔 때 n분 빠르게/늦게 | 같은 직업이 한꺼번에 움직이는 것을 막는 분산에 더해진다 | -120~120 | V13 |

### `pois.json` — 장소

| 필드 | 제목 | 무엇 | 주의 |
|---|---|---|---|
| `id` | 장소 ID | `유형_번호_지역번호` | 바꾸지 않는다 |
| `code` | 장소 번호 | 거리표 첨자 | **절대 바꾸지 않는다** |
| `zone` | 지역 | 속한 지역 | 순찰로·`$nearest_*` 는 같은 지역 안 |
| `type` / `subtype` | 유형 / 세부 유형 | 집·일터·시장… / 대장간·빵집… | 직업의 일터 유형은 `subtype` |
| `pos` | 위치 | **지역 중심 기준** 좌표(x, z) | 거리표가 이것으로 — 바꾸면 거리표 재생성 |
| `capacity` | 정원 | 동시에 배정될 수 있는 수 | 정원 합이 인구를 감당해야 (V10) |
| `open_hours` | 여는 시간 | 시간대 from~to | 닫힌 때 오면 스텝 실패 |
| `grants` | 도착하면 서는 상태 | 예: 집 → `AtHome` | 같은 유형 장소를 베낀다 |
| `allowed_archetypes` | 일할 수 있는 직업 | 비우면 제한 없음 | 정원이 남아도 여기 없으면 못 일한다 |
| `resources` | 얻을 수 있는 것 | 채집·채굴 아이템 | `items.json` 에 있어야 (V3) |

### `fallback_plans.json` — 하루 일과 (폴백)

| 필드 | 제목 | 무엇 | 주의 |
|---|---|---|---|
| `id` | 일과 ID | `fb_<직업>` | 직업의 `fallback_plan` 과 같아야 (V7) |
| `archetype` | 직업 | 누구의 하루 | — |
| `goal` | 하루 목표 | 한 단어 | 설명용 |
| `steps[].action` | 행동 | 할 수 있는 행동만 | V8 |
| `steps[].args` | 대상 | 어디로·무엇을·몇 개 | 장소를 요구하는 행동 앞에는 이동이 온다 |
| `steps[].timeout_s` | 최대 시간(초) | 못 끝내면 실패로 보고 다음으로 | 0 불가. 상한이지 예상값이 아니다 — 예측 소요보다 짧으면 경고 |
| `loop` | 반복 | 마지막 뒤 처음으로 | 폴백은 항상 `true` — 고리가 닫혀야 |
| `on_step_fail` | 스텝 실패 시 | `skip` | 폴백은 항상 `skip` |

### `interrupts.json` — 돌발 반응 (읽기)

`priority` 높을수록 먼저 · `when`(AND): `any_flag` 하나라도 · `all_flag` 전부 · `none_flag` 없이 · `event` 이 사건일 때만 · `archetype_trait` 성격 조건 · `combat_capable` · `then.action` 즉시 하는 행동(할 수 있어야) · `replan.urgency` 그 뒤 새 계획을 얼마나 급히(0~100).

### `factions.json` · 고급 파일

`id`·`code`(0 은 "미지정")·`desc`·`hostile_to`(게임서버가 쓴다). 고급 — `world_flags.json`·`actions.json`·`context_buckets.json`·`items.json`·생성물은 한 줄 "손대지 않는다 — 이유".

---

## 부록 B · 용어 사전 초안 (`Glossary`)

| 용어 (화면) | 코드 이름 | 한 줄 |
|---|---|---|
| 직업 | 아키타입 `archetype` | NPC 의 종류. 정의 하나를 여러 명이 공유 |
| 직군 | `ArchetypeGroup` | 생산·채집·상업·치안·종교·주민·특수. 인형 색이 이것 |
| NPC | 인스턴스 `npc_instances` | 직업으로 생성된 개체 한 명 |
| 개별 설정 | 오버라이드 `npc_overrides` | 한 명만 다르게. 명단을 다시 만들어도 남는다 |
| 장소 | POI `pois` | 집·일터·시장… |
| 지역 | 존 `zones` | 장소들의 묶음. 이웃으로 이어진다 |
| 행동 | 액션 `actions` | 원자 동작 37개. LLM 은 이것만 조합 |
| 하루 일과 | 폴백 플랜 `fallback_plans` | LLM 없이도 도는 기본 하루 |
| 돌발 반응 | 인터럽트 `interrupts` | 사건에 즉시 하는 행동. 규칙으로만 |
| 상태 | 월드 플래그 `world_flags` | "집에 있다" 같은 on/off 64칸 |
| 미리 구운 계획 | 프리베이크 `planstore` | LLM 이 미리 만든 하루. 직업 × 시간대 × 상황 |
| 상황(버킷) | `BucketKey` | 시간대 6 × 지역 상태 4 × 기후 3 = 직업당 72 |
| 기대 경로 | `DayForecast` | 정의만으로 계산한 하루. 실제는 흔들린다 |
| 파급 | `ImpactAnalyzer` | 무엇을 바꾸면 무엇을 다시 해야 하나 |
| 연습장 | `lab/` | 원본 사본. 망쳐도 된다 |

---

## 부록 C · 참고 파일 지도

| 무엇 | 어디 |
|---|---|
| 현재 Studio | `tools/Npc.Studio/Components/Home.razor` (T01 이후 `Pages/`·`Shared/`) · `Services/StudioWorkspace.cs`(`ValidateAndWrite` 가 유일한 쓰기) |
| 설명·예측 | `src/Npc.Narrative/` — `ArchetypeCard`·`InstanceCard`·`PlanExplain`·`InterruptExplain`·`Lexicon` · 신설 `FieldGuide`·`DayForecast`·`ReactionForecast`·`DefinitionDiff`·`ArchetypeLint`·`PlacementForecast`·`ZoneLayout` |
| 안전 편집 | `src/Npc.MasterData/Authoring/` — `JsonSurgeon`·`CodeAllocator`·`WeightRebalancer`·`ImpactAnalyzer`·`DerivedArtifacts` |
| 검증·힌트 | `src/Npc.MasterData/Validation/MasterDataValidator.cs`·`FixHints.cs` |
| **소요 시간 모델 (기준)** | `src/Npc.Sim/SimWorld.Minimal.cs` `DurationSeconds`·`Travel` · `MovementSim` (`RunSpeedFactor = 0.6`) · `ActionDef.Duration`(`DurationDef(Kind, BaseSeconds, PerMeterSeconds, Param)`) |
| **심볼 바인딩 (기준)** | `src/Npc.Runtime/PoiBinder.cs` `TryBind` · `src/Npc.Sim/SimWorld.Minimal.cs` 심볼 switch · `MasterDataSet.CanBindSymbol` |
| **인터럽트 매칭** | `src/Npc.MasterData/InterruptRules.cs` `TryMatch`·`TryMatchState` |
| **플랜 컴파일 (Core)** | `src/Npc.Core/Validation/SchemaValidator.cs` `Validate(json, out PlanDocument)` · `src/Npc.Core/Plan/CompiledPlan.cs` `PlanCompiler.Compile` · `PlanTable.Parse`(폴백) |
| 미리 구운 플랜 | `planstore/pinned/*.json`·`planstore/plans/` — `{bucket, archetype, origin, plan:{goal, steps, on_step_fail, loop}}` · `manifest.json` |
| 시간대·초기 플래그 | `MasterDataSet.Buckets` — `GameHoursOf(TimeOfDay)`·`InitialFlags(BucketKey)`·`FlagsOf(...)` |
| 스키마 | `docs/schema/*.base.schema.json` (생성물) · `docs/reference_masterdata.html` |
| 로컬라이즈 | `masterdata/localization/ko-KR.json` (`npc.*`·`zone.*`·`item.*`·`dialogue.*` — 액션은 없다) |
| 서버 API | `docs/openapi.json` (B-08 대시보드 — T28 은 이것만 본다) |
| 실습장 | `samples/lab.ps1` (`lab/<이름>/masterdata`, gitignore) |
| 절차의 정답 | `docs/llm/RECIPES/add-archetype.md`·`write-fallback-plan.md`·`add-item-poi.md` · `samples/ch05_apiary/patch.ps1`·`ch07_beekeeper/apply.ps1` |
| 기존 테스트 | `tests/Npc.Tests/Studio/StudioWorkspaceTests.cs` · `tests/Npc.Tests/Narrative/NarrativeTests.cs` |

---

## 부록 D · 동작 예측 모델 (T22·T23 의 수식과 규칙)

**전부 코드에서 확인한 것이다.** 기준은 `Npc.Sim`(게임서버 대역) 과 `Npc.Runtime/PoiBinder` 다. 예측은 그 **기대값**이고 드리프트 테스트가 둘을 묶는다.

### D.1 시계

- 게임 하루 = 86,400 게임 초. 시간대 6개의 시각 범위는 `data.Buckets.GameHoursOf(time)` (예: 아침 06~10). 예측의 `StartClockSeconds` = 버킷 시간대의 시작 시각 × 3600 + `ScheduleOffsetMinutes × 60`.
- 세그먼트 시각은 하루 시작 기준 경과 초. 실제 시각 = `(StartClock + 경과) mod 86400`.

### D.2 스텝 소요 (초) — `SimWorld.DurationSeconds` 와 같다, 지터만 뺀다

| `Duration.Kind` | 소요 | 비고 |
|---|---|---|
| `Fixed` | `BaseSeconds` | |
| `Distance` | `BaseSeconds + 거리(m) × PerMeterSeconds` · `speed=run` 이면 `PerMeterSeconds × 0.6` | 거리는 `data.Pois.Distance(from, to)` (`poi_distances.bin`). `from == to` 또는 `Infinity` 면 `BaseSeconds`. 속도 인자는 `CompiledStep.ArgFlags` 의 열거 인자 (`ActionCatalog.cs` ~660줄 인코딩 그대로 읽는다) |
| `Param` | `Count > 0 ? Count : BaseSeconds` | `duration_s` 는 컴파일 시 `Count` 에 실린다 |
| `UntilTime` | 목표 시간대 시작 시각까지. 이미 지났으면 다음 날 | 지터 없음 — Sim 도 여기엔 지터를 안 준다 |
| 그 외 | `BaseSeconds` | |

`0 이하 → 1`. **Sim 은 이 값에 `±JitterPercent` 결정론 지터를 더한다** — 예측은 안 더한다. 화면은 "기대 경로" 라고 적는다.

### D.3 심볼 → POI (같은 지역, 출입 가능, 가장 가까운 것)

| 심볼 | 규칙 | 근거 |
|---|---|---|
| `$home` · `$workplace` | 개체 값 | `PoiBinder`·Sim |
| `$market` `$tavern` `$temple` `$gate` `$nearest_field` | 같은 존 · `CanEnter(archetype)` · 현재 위치에서 최단 (같은 유형 `PoiType`) | `PoiBinder.TryNearestOfType` |
| `$nearest_safe` | 성문 → 없으면 집 | Sim: `Gate`, 없으면 `FirstOfType(Home)`. Runtime: `s_safeTypes` (`> 확인`: 순서가 같은지) |
| `$nearest_shelter` | 집 | `CanBindSymbol` |
| `$patrol_route` | 첫 순찰 지점 → 일터 → 집 | `PoiBinder` D-04 주석 |
| 없음 | 이동 없음 — 현재 위치 유지 | |

바인딩 실패 = `V3.UNREACHABLE_POI` → `Skipped`(폴백) / `Replan`(그 외).

### D.4 상태·판정·고리

- 시작 플래그 `data.InitialFlags(bucket)` + 아키타입 기본 인벤토리의 `grants` (3단 검증기와 같다 — `PlanExplain.Trace` 가 이미 그렇게 한다).
- 스텝 뒤 상태 = `Trace[i].After`. 판정 `Code`·`Reason` 도 그대로.
- `on_step_fail`: `skip` → 0초 `Skipped` · `fallback`/`replan`/`retry_once` → `Replan` 로 멈춤 (`StoppedForReplan`).
- `loop` → 마지막 뒤 첫 스텝. `LoopVerdict.Closed == false` 면 두 바퀴째 첫 스텝을 `Replan`. 24시간에서 자른다.

### D.5 캡션 문장 틀 (`Lexicon` 으로 조립)

| 상황 | 문장 |
|---|---|
| Move | "{대상 장소}로 걸어간다/뛰어간다 (약 {분}분)" — 대상은 `Lexicon.PlaceName` |
| Act + 아이템 | "{아이템}을(를) {수량}개 {행동}한다 (약 {분}분)" — 조사는 `Lexicon.With` |
| Act | "{행동}한다 (약 {분}분)" |
| UntilTime | "{시간대}까지 {행동}한다" |
| Skipped | "건너뛴다 — {Reason}" |
| Replan | "여기서 새 계획을 요청한다 — {Reason}" |

### D.6 상황 반응 (T23)

- 입력 `Situation(Flags, Event?)`. 프리셋: 위협 등장 `ThreatNearby` · 전투 시작 `InCombat` + `CombatStarted` · 부상 `InCombat|IsInjured` · 적대 플레이어 `HostilePlayerNearby` · 플레이어 말 걸기 `PlayerInteracted` 사건 · 피해 입음 `DamageTaken` 사건 · 혼자 위협 `ThreatNearby` (아군 없음).
- 매칭 = `data.Interrupts.TryMatch(ev, flags | 현재 세그먼트의 FlagsAfter, def, out rule)`. 사건 없으면 `TryMatchState`.
- `Checks` = 모든 규칙을 우선순위 순으로: 사건 불일치 · `any/all/none_flag` · 성향 비교(값과 함께) · `combat_capable` · **행동 허용 여부** 를 각각 문장으로.
- 반응 애니메이션: `rule.Poi` 를 D.3 으로 바인딩해 그리로 **뛴다**(Run 속도). `Attack` 이면 제자리 + 전투 배지. `Wait` 면 제자리 + 대기 배지. `CallForHelp` 면 말풍선.

### D.7 대표 개체

아키타입 화면은 개체가 없으면 지도를 못 그린다. `Representative(data, instances, archetype)` = 그 아키타입의 **첫 개체**(`gen_npcs` 는 code 순 배치라 결정론). 화면에 "#1234 기준 — 다른 NPC 보기" 드롭다운. 명단이 없으면(새 직업, 재생성 전) 일터 subtype 의 첫 POI 와 같은 존의 첫 집으로 **가상 개체**를 만들고 "명단 재생성 전 예상" 표시.

---

## 부록 E · NPC SVG 인형 규격 (`NpcFigure`)

**모습이 아니라 상태가 중요하다.** 한눈에 "누구(직군) · 무엇을 하는 중(상태) · 어디로(방향)" 가 읽히면 된다. 전부 인라인 SVG, 이미지 파일 없음, 이모지 없음.

```
viewBox 0 0 24 32 (지도 위에서는 scale 로 크기 조절. 지도 최소 변의 1/25)
├ 머리   <circle cx=12 cy=7 r=5>           fill = 직군 색 (연한 톤)
├ 몸     <path d="M6 13 h12 v12 h-12 z" rx=3>  fill = 직군 색
├ 얼굴   <circle r=1> ×2                     방향 = 이동 벡터 쪽으로 x 오프셋 ±1
├ 상태 배지 <g transform="translate(18 2)">   지름 8 원 + 기호 (아래 표)
├ 말풍선 <g class="bubble">                   캡션 (재생 중 현재 세그먼트) — 16px 이하, 최대 18자, 넘치면 …
└ 선택 테두리 <circle r=13 stroke-dasharray>   Selected 일 때만
```

| 직군 | 색 (`--npc-craft` …) | | 상태 | 배지 기호 (SVG path/text) | 배지 색 |
|---|---|---|---|---|---|
| 생산 | 주황 `#e8873a` | | Move (걷기) | 작은 화살표 `→` path | 파랑 |
| 채집 | 초록 `#5aa85a` | | Move (뛰기) | 겹화살표 `»` | 진파랑 |
| 상업 | 노랑 `#d9b23c` | | Act 일하기/만들기 | 망치 path (4 선분) | 주황 |
| 치안 | 파랑 `#4a78c2` | | Act 거래/대화 | 말풍선 윤곽 | 노랑 |
| 종교·학문 | 보라 `#8a6bc9` | | Act 식사/마시기 | 잔 윤곽 | 갈색 |
| 주민 | 회색 `#8d97a5` | | Sleep | 텍스트 `z` | 남색 |
| 특수 | 분홍 `#d46a9a` | | Wait/Rest | 텍스트 `…` | 회색 |
| 기타 | 검정 `#444` | | 반응 — 도망/물러나기 | 텍스트 `!` | 빨강 |
| | | | 반응 — 공격/경계 | 교차 선분 `×` | 진빨강 |
| | | | 반응 — 도움 요청 | 텍스트 `?` 말풍선 | 빨강 |
| | | | Skipped/Replan | 텍스트 `✕`(path) 반투명 | 회색 |

- 색은 CSS 변수로 (`app.css`) — 다크 배경 기준. 지도 POI 점과 겹치지 않도록 인형은 POI 점 위 8px 에 앉힌다.
- 여러 인형(T08 지역 거주자·T28 라이브) 은 `<use href="#npc-sprite-{group}">` 스프라이트로 — `<defs>` 에 직군 8종 × 상태 없음 1벌만 두고 상태 배지는 따로 겹친다.
- 접근성: `<title>` 에 "#2326 위병 · 대장간으로 걸어간다".
- 애니메이션은 `transform` 만 바꾼다 (JS `requestAnimationFrame`, T24). 반응 애니메이션 2초 고정 · 뛰기 배지.
