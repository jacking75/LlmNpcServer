# NPC Studio 보강 계획 (2차) — 실수해도 망가지지 않고, 막혀도 빠져나오고, 어디서나 다음 할 일이 보인다

작성 2026-09-17 · 대상 `tools/Npc.Studio` (+ `src/Npc.Narrative` · `src/Npc.MasterData` 일부) · 상태 **완료 (2026-09-17)** · 태스크 28건

> **이 문서가 Studio 작업의 지시서다.** 1차 계획 [`STUDIO_IMPROVEMENT_PLAN.md`](STUDIO_IMPROVEMENT_PLAN.md) (34건 · 2026-09-16 완료) 은
> 기록이다. 1차가 **"읽고 · 예측하고 · 만든다"** 를 만들었다면, 2차는 그 위에 **"실수를 막는 장치"** 를 얹는다.
>
> **근거.** 2026-09-17 에 Studio 코드 전체(`Components/**` 32 · `Services/*` 9 · `wwwroot/*`)와 매뉴얼 · 테스트 · CODEMAP ·
> ROADMAP 을 대조해 찾은 것이다. 파일:줄 은 그 시점 기준이다. 실행하지 않고 코드로만 판정한 항목은 "실행 확인" 이라 적었다 —
> 구현 전에 브라우저에서 한 번 재현한다.
>
> **1차 계획이 "완료" 라고 적었지만 코드에 없는 것이 일곱 가지 있다** (§1.3). 그것도 여기서 끝낸다.

---

## 태스크 체크리스트

**하나를 끝내면 그 즉시 여기 `[ ]` → `[x]` 로 바꾸고, §3 의 해당 태스크 제목 옆에도 `✅` 를 붙인다.**
행 순서가 권장 구현 순서다. 의존 열의 태스크가 끝나기 전에는 시작하지 않는다. **H 는 보강(Hardening) 이다.**

| # | 완료 | ID | 태스크 | 묶음 | 우선 | 크기 | 의존 |
|---|---|---|---|---|---|---|---|
| 1 | [x] | **H10** | 검증 패널을 사람 말로 + "고치러 가기" 가 **편집 탭의 그 필드**로 간다 (링크 버그 포함) | B 탈출구 | P0 | M | — |
| 2 | [x] | **H07** | 막힌 상태(BLOCKED) 에 탈출구 셋 — 되돌리기 · 연습장 조작 · 명령줄. 거리표 파일이 없어도 화면이 뜬다 | B 탈출구 | P0 | M | — |
| 3 | [x] | **H08** | 예외가 화면에 보인다 — 오류 UI · 핸들러 보호 · 한글 메시지 | B 탈출구 | P0 | M | — |
| 4 | [x] | **H01** | 편집 중 이탈 가드 — 탭 전환 · 사이드바 · 새로고침 · 탭 닫기 · 마법사 | A 실수 방지 | P0 | S | — |
| 5 | [x] | **H02** | 파괴적 행동은 "무엇이 어떻게 되는지" 를 문장으로 확인한다 (공통 `ConfirmDialog`) | A 실수 방지 | P0 | M | — |
| 6 | [x] | **H03** | 인구 재배분 "이 안으로" 가 **즉시 저장하지 않는다** — 초안에 반영하고 한 트랜잭션으로 | A 실수 방지 | P0 | M | H02 |
| 7 | [x] | **H04** | 연습장 안전 — 같은 이름 덮어쓰기 금지 · 원본 잠금 · 탭 간 배지 일치 · 적용 뒤 복귀 | C 조용한 파손 | P0 | M | H02 |
| 8 | [x] | **H15** | 표시 이름이 조용히 버려지지 않는다 — 연습장 적용에 `localization/` 포함 · V14 를 Studio 검증에 | C 조용한 파손 | P0 | S | H04 |
| 9 | [x] | **H11** | 장소 id 중복 · 형식 · 정원을 저장 전에 거절한다 | C 조용한 파손 | P0 | S | — |
| 10 | [x] | **H12** | `code`·`bit` 재배치 금지를 **코드로** 강제한다 (지금은 문구뿐) | C 조용한 파손 | P0 | S | — |
| 11 | [x] | **H13** | 디스크가 그 사이 바뀌었으면 저장을 거절한다 (다른 탭 · VS Code · 생성기) | C 조용한 파손 | P0 | M | — |
| 12 | [x] | **H14** | 여러 파일 쓰기의 원자성 + **트랜잭션 단위 되돌리기** | C 조용한 파손 | P0 | M | — |
| 13 | [x] | **H09** | 저장 결과를 **그 자리에서** — 성공이면 파급, 실패면 필드 옆 오류. 좁은 창에서도 보인다 | A 실수 방지 | P0 | M | H10 |
| 14 | [x] | **H05** | 마법사 단계 검증 — id 즉시 · 재배분 재계산 · 정원 차단 · 5단계 파일 목록 · 실패 시 그 단계로 | A 실수 방지 | P0 | M | H01 H02 |
| 15 | [x] | **H06** | 마법사 4단계 **하루 일과 편집기** (1차 T12 ④ — 계획엔 완료, 코드엔 안내문 두 줄) | D 약속 이행 | P0 | L | H05 |
| 16 | [x] | **H16** | 파생물 재생성을 견고하게 — 교착 · 중복 실행 · 진행 표시 · **재생성 뒤 오버라이드가 엉뚱한 NPC 에 붙는 것** 경고 | C 조용한 파손 | P1 | M | H02 H08 |
| 17 | [x] | **H18** | 장소 폼 — 지도 클릭이 **실제로** 좌표를 찍는다 · 후보 점 미리보기 · 정원 판정 · 선택 위젯 | D 약속 이행 | P1 | M | H11 |
| 18 | [x] | **H22** | 자유 문자열을 선택으로 — 제작품 · 목표 · 소지품 · 인구(명) · 스텝 인자 · 허용 안 된 값 표시 | E 직관 | P1 | M | — |
| 19 | [x] | **H23** | NPC 화면 — 편집 중인 순찰로가 지도에 바로 보인다 · 4개 초과 사유 · 한국어 지역 · 정확 번호 검색 | E 직관 | P1 | S | H01 |
| 20 | [x] | **H21** | 말 · 버튼 · 위치 통일 — 저장 동사 하나 · "되돌리기" 두 뜻 분리 · 토스트 어휘 · 브레드크럼 | E 직관 | P1 | S | — |
| 21 | [x] | **H19** | 전역 검색 — `Ctrl+K` 가 **실제로** 동작한다 · 색인 신선도 · 키보드 | D 약속 이행 | P1 | S | — |
| 22 | [x] | **H20** | 문서가 약속한 나머지 — 상황별 탭 숨김 · 새 직업 직군 안내 · 반응 애니메이션 문구 | D 약속 이행 | P1 | S | — |
| 23 | [x] | **H25** | 체감 성능 — 마스터데이터 로드 캐시 · 렌더마다 재계산하는 getter 제거 · 로딩 표시 | E 직관 | P1 | M | — |
| 24 | [x] | **H26** | 읽기 전용 · 기동 인자 — 폼 비활성 + 배너 · 모르는 인자 거부 · 경로 폴백 알림 · 비루프백 경고 | A 실수 방지 | P1 | S | — |
| 25 | [x] | **H24** | 접근성 · 키보드 · 모션 — aria · 색+글자 · hover 전용 툴팁 대체 · 모달 포커스 · 스크러버 드래그 | E 직관 | P1 | M | — |
| 26 | [x] | **H17** | 개별 설정 저장의 서식 보존 · 세력은 설명으로 · 대화 프로필 후보를 데이터에서 | C 조용한 파손 | P2 | S | — |
| 27 | [x] | **H27** | 매뉴얼 · README · CODEMAP · ROADMAP 동기화 + "문제 해결" 10항목 추가 | F 문서 | P1 | M | H01~H23 |
| 28 | [x] | **H28** | 테스트 정비 — 동어반복 삭제 · 회귀 고정 · 새 불변식 · ⓘ 전수 검사를 모든 폼으로 · `FieldGuide` 빈틈 | F 테스트 | P1 | M | 전부 |

**판정 시나리오** (§2) — 전부 통과하면 이 계획은 끝난다. **브라우저에서 직접 밟아 판정한다.**

> **X1~X9 를 실제로 밟았다 (2026-09-17).** 체크는 "코드에 있다" 가 아니라 **"화면에서 봤다"** 다.
> 밟는 과정에서 결함 넷이 더 나왔고 전부 고쳤다 — §7 에 적었다. X9 의 마지막 문장만
> 도달 불가로 끝났는데, 그 이유도 §7 에 적었다.

- [x] **X1 이탈** 편집 탭에서 용기를 바꾸고 "하루 일과" 탭 · 사이드바 · 새로고침 · 탭 닫기를 각각 시도한다 → 넷 다 확인이 뜨고, 취소하면 초안이 그대로다
- [x] **X2 연습장** 같은 이름으로 두 번 만든다 → 덮어쓰지 않고 묻는다. 바꾼 파일이 있는 연습장을 버린다 → "N개 파일의 변경이 사라진다" 확인. 탭 둘을 열고 한 탭에서 연습장을 만든다 → 다른 탭 상단에 배너가 뜬다
- [x] **X3 막힘** 연습장에서 장소를 추가한다 → 화면이 통째로 막히지 않고 상단 배너가 뜬다 → "원본에 적용" → "거리표를 다시 만들어라" → 다시 만들기 → 열린다. 저장소 밖 폴더에서 같은 일을 하면 "방금 저장을 되돌리기" 가 있다
- [x] **X4 바로가기** `/issues` 와 개요 ⑩ 건강 진단의 "고치러 가기" 를 누른다 → **편집 탭**의 그 필드로 스크롤되고 강조된다 (지금은 JSON 탭으로 떨어진다)
- [x] **X5 중복** 이미 있는 장소 id 로 저장 → 거절 문장. 마법사 1단계에 이미 있는 직업 id → "다음" 이 막히고 이유가 보인다
- [x] **X6 재배분** 편집 탭에서 인구를 올리고 "이 안으로" → 파일이 바뀌지 않는다. 저장 모달에 "다른 직업 N개의 인구가 바뀐다" 가 있다 → 저장 → `archetypes.json` 한 트랜잭션
- [x] **X7 외부 편집** Studio 에서 대장장이를 열어 둔 채 VS Code 로 `archetypes.json` 을 고친다 → Studio 에서 저장 → "디스크가 바뀌었다 — 다시 읽어라" 로 거절. 새로고침 → 편집 탭이 새 값을 보여 준다
- [x] **X8 오류** `--server localhost:1` 로 띄우고 관찰 시작 → 회로가 죽지 않고 "주소가 잘못됐다" 가 뜬다. 창을 1000px 로 줄이고 V5 에 걸리는 저장 → 오류가 **화면 안에** 보인다
- [x] **X9 마법사** 4단계에서 스텝을 하나 지우고 예측이 다시 그려지는 것을 본다 → 5단계에 바뀌는 파일 6개와 허가되는 장소가 적혀 있다 → 만들기 실패를 일부러 내면(정원 부족) 3단계로 가는 링크가 있다

---

## 0. 이 문서를 쓰는 규칙

1차 계획 §0 과 같다. 요점만 다시 적는다.

- **한 태스크 = 커밋 하나 이상.** 파일군이 갈리면(Narrative / MasterData / Studio / docs / tests) 커밋을 나눈다. **1차의 회귀 4건은 화면 전부를 한 커밋(`6de8aab`)에 넣은 데서 나왔다** — 반복하지 않는다.
- 태스크를 끝내면 **① 위 표 체크 ② §3 제목에 ✅ ③ `working_log.md` 항목**. 셋 다 한 커밋에.
- 결함을 고치는 태스크는 **회귀 테스트를 같은 커밋에** 넣는다. 1차에서 테스트 없이 고친 것 둘(`1fd9812` · `4f83fdc`)은 H28 이 거둔다.
- 판단이 갈리는 곳은 `> 확인:` 이다. 구현 전에 그 파일을 열어 결정하고, 결정을 이 문서에 적는다.
- CLAUDE.md §2·§3·§5.1 이 우선한다. **Studio 는 `Core`·`MasterData`·`Narrative` 만 참조한다.** ROADMAP 의존표에 적힌 `Sim` 은 오기다 (H27 이 고친다).

---

## 1. 진단 — 지금 무엇이 위험한가

### 1.1 심각도 기준

| 등급 | 뜻 |
|---|---|
| **High** | 데이터를 잃거나 조용히 틀리게 만들거나, 초보자가 빠져나올 수 없다 |
| **Medium** | 실수를 유도하거나, 무엇이 잘못됐는지 알 수 없다 |
| **Low** | 어색하거나 느리다. 데이터는 안전하다 |

### 1.2 증상 → 원인 → 태스크

**A. 실수를 막는 장치가 없다**

| # | 증상 (초보자가 겪는 것) | 원인 (코드에서) | 등급 | 태스크 |
|---|---|---|---|---|
| A1 | 편집 탭에서 슬라이더를 만지다 "하루 일과" 탭을 누르면 **초안이 사라진다.** 사이드바 · 뒤로가기 · 새로고침 · 탭 닫기 전부 마찬가지 | `Archetypes.razor:88-162` 가 `@switch` 로 자식을 언마운트. `NavigationLock`·`RegisterLocationChangingHandler`·`beforeunload` 가 Studio 전체에 0건. `ArchetypeForm.razor:220`·`FallbackForm.razor:183` 의 `Dirty` 는 어디에도 물려 있지 않다 | High | H01 |
| A2 | NPC 개별 설정 · 원문 JSON · 새 장소 폼은 **바뀐 것을 추적조차 안 한다.** 다른 NPC 로 가면 말없이 덮인다 | `Npcs.razor:111, 244-246` 저장 버튼이 `ReadOnly` 만 봄. `Files.razor:64`, `Archetypes.razor:147`, `PlaceForm.razor` 동일 | High | H01 |
| A3 | 마법사 5단계 입력이 새로고침 · "취소" 링크 한 번에 전부 날아간다 | `ArchetypeNew.razor:187-199` 상태가 전부 컴포넌트 필드. `:178` 취소는 확인 없는 링크 | Medium | H01 H05 |
| A4 | **"연습장 버리기" 가 확인 없이 폴더를 지운다.** "원본에 적용" 도 무엇이 옮겨지는지 안 보여 준다 | `Home.razor:92, 156-162` → `StudioWorkspace.cs:101-120` `Directory.Delete(recursive)`. `Home.razor:90, 166-175` 확인 없음. 둘이 같은 줄에 `원본으로 돌아가기` 와 나란히 | High | H02 H04 |
| A5 | "개별 설정 삭제" · "이 상태로 되돌리기" · "다시 만들기(5,000명 명단 재생성)" · JSON 저장이 전부 한 번 클릭 | `Npcs.razor:112, 299-307` · `Files.razor:45, 87-95` · `ImpactPanel.razor:45, 107-112` · `Archetypes.razor:144`. 1차 T17 은 "확인 모달에 정확한 명령 · 예상 시간" 을 요구했다 | Medium | H02 |
| A6 | 편집 탭 "인구 재배분 → 이 안으로" 를 누르면 **다른 직업들의 인구가 즉시 파일에 써지고**, 이어 폼이 다시 읽혀 **저장 안 한 다른 편집이 전부 사라진다.** 저장 요약 모달도 건너뛴다 | `ArchetypeForm.razor:407-416` → `StudioWorkspace.cs:1075-1100` `ApplyRebalance` 가 `ValidateAndWrite` → 성공 시 `Load()`. 게다가 현재 직업 줄은 `skip` 이라 합이 1.0 이 아니어서 V5 에 걸릴 공산이 크다 (실행 확인) | High | H03 |
| A7 | 마법사에서 id 중복 · 형식 오류를 **5단계 끝에서** 예외 토스트로 안다. 인구를 바꿔도 재배분 안이 옛 값이라 V5 로 거절된다. 정원 부족은 "✗ V10 에 걸린다" 고 알려만 주고 "다음" 을 막지 않는다 | `ArchetypeNew.razor:213-218` `CanAdvance` 는 빈 값만. `:74` `_population` 이 `@bind` 뿐이라 `Propose()` 재실행 없음. `:288` 유효 안이 없으면 `default` 전략이 조용히 선택. `:217` 2단계는 `_ => true` | High | H05 |
| A8 | 마법사 5단계 요약에 **바뀌는 파일이 없다.** 실제로는 6개(`archetypes`·`fallback_plans`·`context_buckets`·`pois` 작업 허가·`ko-KR`·`en-US`) | `ArchetypeNew.razor:146-163`. 매뉴얼 §6 은 "네 파일" 이라 적혀 있어 그것도 틀렸다 | Medium | H05 H27 |
| A9 | 저장이 성공해도 **"이제 뭘 해야 하나" 가 그 화면에 없다** — 시작 화면에 가야 파급이 보인다는 것을 모른다. 실패하면 토스트 한 줄, 오류는 오른쪽 패널에만 | `ImpactPanel` 은 `Home.razor:79` · 차단 패널 · `ArchetypeForm.razor:192`(저장 **전** 모달) 에만. `StudioWorkspace.cs:1536` "검증 실패로 저장하지 않았다." | High | H09 |
| A10 | **창을 1100px 이하로 줄이면 검증 결과가 아예 안 보인다** — 저장 실패 사유가 사라진다 | `wwwroot/app.css` 의 `@media(max-width:1100px){.inspector{display:none}}` | High | H09 |
| A11 | 저장에 실패한 **초안의** 오류가 전역 "검증 ✗ N건" 으로 올라가 "내 파일이 망가졌나" 로 읽힌다 | `StudioSession.cs:74` `Issues = result.Issues` 가 실패해도 실행. `Home.razor:67` · 상단 카운트가 그것을 보여 줌 | Medium | H09 |
| A12 | 읽기 전용인데 폼은 다 채워지고 마지막 저장만 안 된다 | 모든 입력이 `disabled` 없이 그려짐. `ArchetypeNew.razor:176` 5단계에서만 비활성 | Low | H26 |
| A13 | `--readonly`(오타) 로 띄우면 **편집 모드로** 뜬다. 오타 경로면 저장소의 `masterdata/` 가 조용히 열린다 | `StudioOptions.cs:39-69` 모르는 인자 무시. `:87-114` 경로 폴백 | Medium | H26 |

**B. 막다른 길 · 조용한 죽음**

| # | 증상 | 원인 | 등급 | 태스크 |
|---|---|---|---|---|
| B1 | **연습장에서 장소를 추가하면 빠져나올 길이 없다.** 화면이 통째로 "읽을 수 없다" 로 바뀌고, 다시 만들기는 연습장이라 비활성, "원본에 적용 / 버리기" 버튼은 시작 화면에 있는데 그 화면도 막혔다. 재시작뿐이다 | `AppendPoi` 가 `loaderGate:false`(`StudioWorkspace.cs:879`) → 카탈로그 `Blocked` → `StudioLayout.razor:45-62` 가 **모든 페이지** 의 `@Body` 를 차단 패널로 교체. `GeneratorRunner.cs:49-54` 연습장 거부. 설령 적용해도 `ApplyToOrigin` 은 `loaderGate:true` 라 원본 거리표와 어긋나 실패 | High | H07 |
| B2 | 저장소 밖 폴더(`--masterdata`)에서 장소를 추가해도 같다 — 생성기 버튼이 없고 되돌리기(`/files`)도 차단 뒤에 있다 | `ImpactPanel.razor:47` `Generator.Available == false`. `Files.razor` 는 `@Body` | High | H07 |
| B3 | **`poi_distances.bin` 이 없으면 Studio 가 아예 안 뜬다** (갓 클론한 저장소 · 생성물 없는 외부 폴더). 빈 화면 | `StudioWorkspace.cs:275` 가 `InvalidDataException`·`JsonException` 만 잡음. `FileNotFoundException` 은 `StudioSession` 생성자(`:22`)에서 터져 DI 실패. `:282-286` 의 검증기 · 명단 로드도 `try` 밖 | High | H07 |
| B4 | 차단 사유가 무엇이든 "파생물이 입력과 어긋난다" 다. VS Code 로 괄호 하나 지운 경우도 같은 문구, 버튼도 없다 | `StudioLayout.razor:53-56` 고정 문구. `Blocked` 문자열로 분기하지 않음 | Medium | H07 |
| B5 | **예외가 나면 회로가 소리 없이 죽는다** — 버튼이 아무 반응이 없고 새로고침이 유일한 복구인데 초보자는 모른다 | `App.razor` 에 `blazor-error-ui`·`ErrorBoundary` 없음. `try/catch` 없는 핸들러: `Home.razor:148,156,164` · `ImpactPanel.razor:107` · `StudioLayout.razor:178` · `Npcs.razor:235`(명단 없으면 throw) · `Places.razor:166` · `Live.razor:110-120`(잘못된 `--server` 값이면 루프 사망) | High | H08 |
| B6 | 오류 문구의 절반이 영어다 — "'}' is invalid after…", "The process cannot access the file…", "(Parameter 'draft')" | `StudioWorkspace.cs:737, 1744` `JsonDocument.Parse` 원문. `ArgumentException` 접미. 검증기(`MasterDataValidator.cs:135-191`) 는 `catch` 없이 모양 오류를 던짐 | Medium | H08 |
| B7 | 삼킨 예외 — 파급 패널이 **사라지고**, 저장 모달이 "바뀐 것이 없다" 라고 거짓말한다 | `ImpactPanel.razor:98` `_impact = null` · `ArchetypeForm.razor:429` `return []` · `GlobalSearch.razor:43` · `BucketGrid.razor:154` | Medium | H08 |
| B8 | 백업이 안 만들어져도 **끝까지 모른다** — 되돌릴 수 있다고 믿고 저장한다 | `StudioWorkspace.cs:1706` 백업 실패를 삼킴. `/files` 는 "아직 백업이 없다. 한 번 저장하면 생긴다." | Medium | H14 |
| B9 | 생성기가 첫 실행에 수십 초인데 진행 표시가 없다. `dotnet` 이 PATH 에 없으면 회로가 죽는다. 두 탭에서 누르면 두 번 돈다. 빌드 오류가 길면 **교착**한다 | `GeneratorRunner.cs:86-98` `Win32Exception` 미처리. `:79` 세마포어가 줄 세움. `:100-103` stdout 을 다 읽은 **뒤** stderr → 파이프 버퍼 교착. 타임아웃 · 취소 없음 | Medium | H16 |

**C. 데이터를 조용히 망치는 길**

| # | 증상 | 원인 | 등급 | 태스크 |
|---|---|---|---|---|
| C1 | **같은 id 의 장소가 두 개 생긴다.** 아무도 안 잡는다 — 이후 편집이 첫 것과 마지막 것을 엇갈려 가리킨다 | 검증 V1 은 `code` 중복만. 로더 `MasterDataLoader.cs:321` `byId[dto.Id] = def` 는 마지막이 이김. `AppendPoi` 는 로더 게이트도 껐다. id 제안(`StudioPlaces.cs:169-182`)은 "같은 subtype 개수+1" 이라 하나라도 지웠으면 바로 충돌 | High | H11 |
| C2 | **사이드바가 "ID 와 code 재배치를 허용하지 않는다" 고 적혀 있지만 강제하는 코드가 없다.** 원문 편집기에서 두 직업의 `code` 를 맞바꾸면 V1 도 로더도 통과 → 명단의 NPC 직업이 뒤바뀌고 거리표 첨자가 어긋나고 프리베이크 파일명이 다른 직업을 가리킨다 | `StudioLayout.razor:110` 문구. `SaveArchetype`(`:713`)은 `id` 만 잠금(`EnsureItemId`). `SaveFile`(`:732`) 은 아무것도 안 잠금 | High | H12 |
| C3 | **탭 A 가 연습장을 만들면 탭 B 의 저장도 연습장으로 간다.** 반대로 A 가 버리면 B 는 연습장이라 믿고 **원본에** 쓴다. B 의 배지는 새로고침 전까지 옛 값 | `Program.cs:25,34` 워크스페이스 싱글턴의 `_directory` 는 전역, 배지는 회로별 `Catalog.IsSandbox` | High | H04 |
| C4 | **같은 이름으로 연습장을 다시 만들면 기존 연습장이 확인 없이 삭제된다.** 기본 이름이 `MMdd` 라 같은 날 두 번 누르면 아침 작업이 사라진다. 한글 이름은 전부 `-` 가 되어 `studio----` | `StudioWorkspace.cs:69, 75-78`. `Home.razor:126` | High | H04 |
| C5 | **마법사로 만든 직업의 한국어 · 영어 이름이 연습장에서 원본으로 옮겨지지 않는다.** 오류도 없다 | `ApplyToOrigin`(`:198-220`) 이 `s_editableFiles` 12개만 비교 — `localization/*.json` 제외. V14 는 `MasterDataValidator` 가 돌리지 않으므로(V1~V13) 잡히지 않음. `:1354` 주석 "V14 가 잡는다" 는 사실이 아님 | High | H15 |
| C6 | 화면은 "V1~V15 위반이 없다" 고 말하는데 검증기는 **V1~V13** 만 돈다 | `Home.razor:63` · `StudioLayout.razor:88` 문구 vs `MasterDataValidator.cs:166-178` | Medium | H15 |
| C7 | 다른 탭 · VS Code · 생성기가 파일을 바꿔도 **마지막 저장이 조용히 이긴다.** 상단 "새로고침" 을 눌러도 편집 화면은 옛 값이다 | 낙관적 동시성 없음. 원문 편집(`SaveFile`·`SaveArchetype`)은 로드 시점 텍스트로 통째 교체. 폼(`ApplyForm` `:1141-1236`)은 디스크 vs 폼을 비교하므로 **외부 변경을 옛 값으로 되돌려 쓴다.** 페이지 캐시 `_loaded`(`Archetypes.razor:212` 등)가 `Session.Reload()` 를 모름 | High | H13 |
| C8 | 마법사(3~6 파일) 쓰기 도중 두 번째 파일에서 IO 오류가 나면 **첫 파일만 써진 채** 남는다 (VS Code · 백신이 파일을 잡고 있을 때) | `StudioWorkspace.cs:1555-1562` 파일별 순차 `AtomicWrite`, 롤백 없음. 실패한 `.studio.tmp` 가 `masterdata/` 에 남음(`:1602-1607`) | High | H14 |
| C9 | **트랜잭션을 되돌릴 수 없다.** 마법사 뒤 `archetypes.json` 만 되돌리면 V7, `fallback_plans.json` 만 먼저 되돌려도 V7 — 어느 순서로도 단일 파일 복원은 통과하지 못한다 | `Restore`(`:1666-1679`) 가 파일 하나 단위. 백업 폴더는 이미 같은 stamp 로 묶여 있다(`:1682-1710`) — 구조는 있다 | High | H14 |
| C10 | `localization/ko-KR.json` 백업이 `ko-KR.json` 으로 저장돼 목록엔 뜨는데 복원하면 "편집할 수 없는 파일" 로 거절 | `:1560, 1649, 1670` `Path.GetFileName` 이 폴더를 버림 | Medium | H14 |
| C11 | 명단을 다시 만들면 **같은 번호가 다른 직업 · 다른 지역의 NPC 가 될 수 있어** 순찰로 개별 설정이 엉뚱한 NPC 에 조용히 붙는다 | 오버라이드는 생성된 번호로 묶임. `ImpactPanel.razor:56` 안내 "손편집은 overrides 에 남는다" 는 맞지만 **누구의** 것으로 남는지는 보장 못 함 | Medium | H16 |
| C12 | 생성기가 3.3MB 명단을 쓰는 도중 다른 탭이 저장하면 반쯤 쓰인 파일이 복사되어 "로더가 후보를 거절" — 원인이 안 보이는 가짜 실패 | `GeneratorRunner` 가 워크스페이스 `_gate` 를 잡지 않음 | Low | H13 H16 |
| C13 | 개별 설정 저장은 다른 경로와 달리 **파일을 통째로 재직렬화**해 첫 diff 가 크다. 세력 · 대화 프로필은 자유 문자열 | `StudioWorkspace.cs:643-699` (실행 확인). `Npcs.razor:167, 171-176` 대화 프로필 후보 3개가 UI 하드코딩 | Low | H17 |

**D. 1차 계획은 "완료" 인데 화면엔 없는 것** (§1.3 에 표로)

**E. 말 · 위치 · 형식이 제각각이라 헷갈린다**

| # | 증상 | 원인 | 등급 | 태스크 |
|---|---|---|---|---|
| E1 | 저장 버튼이 다섯 가지 — "저장하기" · "검증하고 저장" · "검증하고 만들기" · "저장" · "이 안으로"(사실은 저장) · "원본에 적용" | `ArchetypeForm.razor:16,65,194` · `FallbackForm.razor:20` · `Npcs.razor:111` · `Files.razor:58` · `ArchetypeNew.razor:176` · `PlaceForm.razor:74` · `Home.razor:90` | Medium | H21 |
| E2 | **"되돌리기" 가 두 뜻이다** — 폼 초안 버리기(`ArchetypeForm:17`) 와 백업 복원(`Files:26,45`) | 같은 낱말 | Medium | H21 |
| E3 | 직업/아키타입 · 장소/POI · 하루 일과/폴백 · 개별 설정/오버라이드 가 화면과 토스트에서 섞인다 ("오버라이드를 저장했다", "아키타입 'x' 이 이미 있다") | `StudioWorkspace.cs:696, 1284` 등 토스트가 개발자 어휘. UI 용어표가 없음 | Medium | H21 |
| E4 | 상세 화면에 "← 목록" · 브레드크럼이 없다. 못 찾음 화면에도 돌아갈 링크가 없다. "+ 새 직업 만들기" 가 매뉴얼 버튼 모양 | `Archetypes.razor:19,61` · `Npcs.razor:64` · `Places.razor:40` | Low | H21 |
| E5 | 주력 제작품 · 기본 목표 · 시작 소지품(`아이템 개수` 구문) · 장소 세부 유형 · 일할 수 있는 직업 · 얻을 수 있는 것 · 대화 프로필이 **자유 문자열**이다. 오타는 저장 때 V3 로, 세부 유형 오타는 **아무 직업도 못 쓰는 새 유형**으로 조용히 남는다 | `ArchetypeForm.razor:127,134,141` · `PlaceForm.razor:16,62,67` · `Npcs.razor:171`. `_choices.Recipes/Items` · 직업 목록이 이미 있다. `ParseInventory`(`:465-484`) 는 실패를 무언으로 되돌림 | Medium | H22 H18 |
| E6 | 인구가 마법사는 "명", 편집 탭은 "비율 0.0123" | `ArchetypeForm.razor:37` `step=0.0001` | Low | H22 |
| E7 | 하루 일과 편집기에서 **기존 플랜이 이제 못 쓰는 행동**을 쓰면(V8) 드롭다운에 일치 옵션이 없어 브라우저가 첫 항목을 보여 주고 모델은 옛 값 — 판정 ✗ 와 화면이 어긋난다. 필수 인자 표시가 없고 행동을 바꾸면 인자가 말없이 비워진다 | `FallbackForm.razor:42-47` (`LoadStepChoices` 가 허용 행동만, `StudioWorkspace.cs:975`). `StudioParamInfo.Required` 를 UI 가 안 씀. `:256-260` | Medium | H22 |
| E8 | NPC 화면에서 **지도를 클릭해 순찰로에 더해도 선이 안 바뀐다** — 저장 후에야 보인다. 4개 초과 · 중복 클릭은 조용히 무시 | `Npcs.razor:84` `Route="_overview.PatrolRoute"`(저장된 값) vs `:210` 편집 중 `Route`. `:316-321` | Medium | H23 |
| E9 | 장소 유형 select 가 `Workplace`·`Tavern` 영어 enum. NPC 목록의 지역이 `market_east` 같은 id. 세력이 id | `PlaceForm.razor:25-28` · `Npcs.razor:35-40` · `:167`. `StudioPlaces.cs:260` 에 한국어 표가 이미 있다 | Low | H18 H23 H17 |
| E10 | 버킷 격자 72칸이 **색만** 있고 핀(`#1f7a4d`)·생성(`#2f9e6a`) 이 거의 같은 초록. 허용 안 된 행동 칩은 `opacity:.4` 로 대비 2:1 | `BucketGrid.razor:79-81` 내용 없는 버튼. `studio-v2.css:82` | Medium | H24 |
| E11 | 지도 점 · 타임라인 칸 · 격자 셀 · 주민 점의 설명이 `<title>` hover 전용 — 터치 · 키보드로 못 본다. 모달에 Escape · 포커스 트랩 없음. 미리보기 재생을 키보드로 못 한다. 재생 중 스크러버를 잡으면 손잡이가 튄다 | `ZoneMap.razor:16-20` · `DayTimeline.razor:8-10` · `ArchetypeForm.razor:175-198` · `behavior-preview.js:38` `dragging` 을 세우는 리스너가 없음 | Medium | H24 |
| E12 | **슬라이더가 버벅인다** — 초보자는 "죽었다" 로 읽는다 | `Workspace.WithData`(`:252-262`) 가 호출마다 마스터데이터 + 5,000명 명단을 다시 파싱. `ArchetypeForm.razor:234-271` `Chips`·`WorkplaceVerdict` 가 **getter** 라 `oninput` 마다 여러 번. `Interrupts.razor:136-154` 는 규칙마다 2회 | Medium | H25 |
| E13 | "진단 중…" 이 절대 안 보인다 | `Issues.razor:92-106` `_busy` 가 동기 계산 안에서 set/reset | Low | H25 |

**F. 문서 · 테스트가 코드와 어긋난다**

| # | 증상 | 원인 | 등급 | 태스크 |
|---|---|---|---|---|
| F1 | 매뉴얼이 없는 동작을 설명한다 — "지도를 클릭해도 된다", "`--planstore` 없으면 탭을 숨긴다", "떠날 때 확인", "Ctrl+K", "네 파일이 한 트랜잭션", "이미 있는 ID 면 바로 막는다", "4단계에서 스텝마다 ✓/✗" | `docs/npc_studio_manual.html` §5 · §7 · §9.1 · §1.3 · §6 | Medium | H27 |
| F2 | 매뉴얼 "문제 해결" 에 막힌 상태 · 생성기 실패 · planstore 없음 · 서버 미연결 · 연습장 적용 실패 · 되돌리기 실패 · 재배분 전부 ✗ 가 없다. "탭이 없다" 는 틀린 진단 | §10 표 | Medium | H27 |
| F3 | `CODEMAP.md` 에 `Narrative` 신설 6파일 · CLI `forecast`·`lint` · Studio 화면 지도가 없다. "화면은 `Components/Home.razor`" | `CODEMAP.md:36-37, 227` | Medium | H27 |
| F4 | `PRODUCTION_ROADMAP.md` F-02 가 미체크 · "남은 것" 이 낡음 · 의존에 `Sim` · 완료 조건 "9장 완주" 가 1차 계획(T21 읽기 전용) 과 충돌 | `:24, 95, 144, 162, 164, 250, 298` | Medium | H27 |
| F5 | **`Forecast_DurationsMatchSimWithoutJitter` 는 아무것도 못 잡는다** — `expected` 를 구한 뒤 `expected` 가 `expected±지터` 안인지 단언한다. `SimWorld.DurationSeconds` 를 부르지 않는다 | `tests/Npc.Tests/Narrative/ForecastTests.cs:32-62`. 1차 T22 는 "비교 자체를 없앴다" 고 결정했는데 T20 목록엔 남겼다 | Medium | H28 |
| F6 | "모든 편집 화면의 모든 필드에 ⓘ" 를 강제하는 테스트가 **아키타입 폼 하나뿐**. 실제로 없는 칸: 장소 폼 위치 Z · 닫는 시간, 스텝 인자 이름, 마법사 이름 · 직군 | `StudioFormTests.cs:94` 만. `PlaceForm.razor:45,56` · `FallbackForm.razor:53` · `ArchetypeNew.razor:35-36,47` | Low | H28 |
| F7 | `FieldGuide` 빈틈 — `items.json` `/recipes/outputs/*` · `world_flags.json` `/reserved_bits`·`/flags` 없음(스키마 `$ref`·최상위 배열이라 드리프트 테스트가 못 봄). `Caution` 이 빈 항목 61/140 (인터럽트는 거의 전부) | `FieldGuideTests.cs:119-150` 규칙의 구멍. `FieldGuide_HasNoEmptyText` 가 Caution 을 안 봄 | Low | H28 |
| F8 | 회귀 수정 둘이 테스트 없이 들어갔다 — 순찰로 없는 NPC NRE(`1fd9812`) · `/issues` 첫 렌더 NRE(`4f83fdc`) | `StudioViewTests.cs:32` 는 순찰로 있는 #2326 만 | Low | H28 |
| F9 | 편집 가능 파일 목록이 두 곳에 손으로 적혀 있다 | `Files.razor:70-75` vs `StudioWorkspace.cs:21-26` | Low | H28 |

### 1.3 1차 계획에서 "완료" 인데 코드에 없는 것

| 1차 태스크 | 약속 | 실제 | 2차 |
|---|---|---|---|
| **T12 ④** | "원본 폴백 초안 → T11 편집기 · 오른쪽에 예측 재생" | `ArchetypeNew.razor:133-144` **안내문 두 줄.** 편집기 · 판정 · 예측 없음. 진행 표시(`:185` "하루 일과") 와 체크리스트("하루 일과를 짠다") 는 무언가 하는 단계처럼 읽힌다 | **H06** |
| **T13** | "위치(`ZoneMap` 클릭 또는 숫자)" | `PlaceForm.razor:70` `PoiClicked="_ => { }"`. `ZoneMap` 은 기존 점 클릭만 받는다. `:46` "아래 지도를 클릭해도 된다" 는 **거짓** | **H18** |
| **T15 §3** | "`IssuePanel` — 제목 · 파일 칩 · 고치러 가기. 저장 실패 토스트에 첫 오류 링크" | `Shared/IssuePanel.razor` 파일 자체가 없다. 우측 패널(`StudioLayout.razor:93-101`)은 `V10 · pois.json` + JSON Pointer 원문. 토스트는 `result.Message` 뿐 | **H10** |
| **T15 §4** | "폼: `?field=` 로 스크롤 · 강조 + 인라인 오류" | `field` 를 읽는 코드 0건. 게다가 `LintList.razor:18` 이 `?tab=edit` 뒤에 `?field=` 를 또 붙여 `QueryTab == "edit?field=…"` → `Archetypes.razor:88` switch 의 `default` = **고급 JSON 탭**으로 떨어진다 | **H10** |
| **T10 §5** | "저장하지 않은 변경이 있으면 떠날 때 확인" | `RegisterLocationChangingHandler` 0건 | **H01** |
| **T17** | "확인 모달에 정확한 명령 · 예상 시간" | `ImpactPanel.razor:45` 즉시 실행 | **H02** |
| **T32** | "`Ctrl+K`" | `GlobalSearch.razor:6` placeholder 문구뿐. JS 없음 (`App.razor` 스크립트는 `behavior-preview.js` 하나) | **H19** |
| T24 | "상황 겹치기 토글" · "2초 뛰기 애니메이션" | 없음 · `BehaviorPreview.razor:262-267` 순간이동 (주석 "실제 서버라면 뛰어간다") | H20 (문구를 사실로) |
| T26 | `ZoneLayout.Compute` 를 `Narrative` 에 | `StudioPlaces.Tiles()`(`StudioPlaces.cs:153`) 에 있음. 결정론은 지켜진다 | 두지 않는다 — 이동은 가치가 없다. H27 이 1차 문서를 고친다 |
| T27 | "`--planstore` 없으면 탭을 숨긴다" | 탭은 항상 그려지고(`Archetypes.razor:83`) 안 열면 빈 상태 | H20 |

---

## 2. 목표 — 완료 조건

### 2.1 판정 시나리오 X1~X9

체크리스트 아래 목록이 전문이다. 1차의 S1~S6 은 **여전히 통과해야 한다** — 이 계획은 기능을 빼지 않는다.

### 2.2 수치 목표

- 파괴적 행동(삭제 · 덮어쓰기 · 재생성 · 복원 · 적용) **100%** 에 확인 모달. 모달 문장은 부록 A 규격.
- 편집 화면 **100%** 에 Dirty + 이탈 가드. 모든 폼의 모든 필드에 ⓘ (H28 드리프트 테스트가 **네 폼 전부**를 본다).
- 검증 오류 100% 가 **편집 탭의 필드**로 가는 링크를 가진다 (파일 단위 폴백은 고급 파일만).
- 회로가 죽는 경로 **0** — 모든 핸들러가 예외를 토스트 또는 인라인으로 낸다. 죽더라도 `blazor-error-ui` 가 "새로고침" 을 말한다.
- 막힌 상태에서 **클릭 두 번 안에** 원본 · 되돌리기 · 명령줄 중 하나에 닿는다.
- 무변경 저장 바이트 동일 · 예측 결정론 — 1차 그대로.

---

## 3. 설계 원칙 (1차 §3 에 더한다)

| 원칙 | 이유 · 근거 |
|---|---|
| **파괴적 행동은 결과를 문장으로 확인한다.** "확인?" 이 아니라 "연습장 0917 의 파일 3개(archetypes · pois · ko-KR)가 삭제된다" | 초보자는 버튼 이름으로 결과를 상상하지 못한다. 문장은 코드가 만든다 (`SandboxChanges` · `ImpactAnalyzer`) |
| **초안은 화면이 아니라 세션이 든다** | 탭 · 링크 · 새로고침이 초안을 죽이면 "일단 눌러 본다" 가 안 된다 |
| **막힌 상태에도 탈출구가 셋이다** — 되돌리기 · 연습장 조작 · 명령줄 | 하나는 늘 막혀 있다 (연습장이면 생성기, 저장소 밖이면 생성기 없음) |
| **예외는 화면에 나온다.** 삼키지 않는다. 한국어다 | 조용히 죽은 버튼은 "고장" 이고, 영어 스택은 "내 잘못이 아니다" 로 읽힌다 |
| **저장 결과는 그 자리에서.** 성공이면 파급, 실패면 필드 옆 | 시작 화면에 가야 보이는 안내는 없는 안내다 |
| **자유 문자열 대신 선택.** 데이터에 후보가 있으면 그것으로 | 오타는 저장 때가 아니라 입력 때 막는다. 세부 유형 오타는 검증도 못 잡는다 |
| **진실은 디스크 해시다.** 로드 시점 해시 ≠ 저장 시점 해시면 거절 | 마지막 저장이 조용히 이기는 것은 "안전한 편집" 이 아니다 |
| **문구가 약속한 것은 코드가 지킨다** — "재배치 금지" · "V1~V15" · "떠날 때 확인" | 사이드바 안전장치 목록이 거짓이면 나머지도 못 믿는다 |
| **사실이 아닌 문구는 지운다** — "지도를 클릭해도 된다", "Ctrl+K" | 구현할 수 없으면 문구를 뺀다. 둘 다 두지 않는다 |

---

## 4. 태스크 상세

각 태스크는 **왜 → 구현 → 테스트 → 완료 판정**. 파일 경로는 저장소 루트 기준. 테스트는 CLAUDE.md §5.1 기준 —
"이 테스트를 지우면 어떤 현실적인 결함을 놓치는가" 에 답이 있는 것만.

---

### H10 ✅ · 검증 패널을 사람 말로 + "고치러 가기" 가 편집 탭의 그 필드로 간다

**왜.** 1차 T15 의 핵심 두 가지가 코드에 없다 (§1.3). 그리고 지금 있는 링크는 **초보자를 고급 JSON 탭에 떨어뜨린다** — 가장 먼저 고칠 버그다.

**구현.**

1. **링크 버그.** `Components/Shared/LintList.razor:18` — `Link` 에 이미 `?` 가 있으면 `&field=`. 헬퍼 `StudioView.WithQuery(url, key, value)` 를 두고 `IssueLocator`(`Services/IssueGuide.cs:130-150`) 도 같은 헬퍼로.
2. **`?field=` 소비.** `Archetypes.razor` 에 `[SupplyParameterFromQuery(Name="field")] string? QueryField`. `ArchetypeForm`·`FallbackForm` 이 `[Parameter] string? FocusField` 를 받아 `OnAfterRenderAsync` 에서 JS `studio.focusField(id)` → `scrollIntoView` + `.field-flash` 2초. 필드 `id` 는 `JsonKeys` 의 JSON 키 그대로(`traits/courage` 같은 하위 경로는 `TraitBars` 의 슬라이더 `id`).
   - 정적 JS 하나 `wwwroot/studio.js` 를 신설한다 (H01 `beforeunload` · H19 `Ctrl+K` 도 여기). **빌드 없음, 30줄 안팎.**
3. **탭이 URL 을 가진다.** `Archetypes.razor:247` `SetTab` 이 `Nav.NavigateTo($"/archetypes/{Id}?tab={tab}", replace: true)`. `OnParametersSet` 이 `QueryTab` 변화를 반영한다 (`_loaded == Id` 가드 때문에 지금은 `/issues` 링크가 같은 직업에선 무시된다 — `_tab = null` 을 `QueryTab` 이 바뀌었을 때도 한다). 뒤로가기가 탭을 되돌린다.
4. **`Shared/IssuePanel.razor` 신설** — `IssueLocator.Views(Session.Issues)` 로 제목(`IssueGuide`) · 파일 칩 · "고치러 가기" · 접으면 원문. `StudioLayout.razor:92-102` 가 이것을 쓴다. `.Take(30)` 은 "외 N건 → /issues" 로.
5. **건강 진단 배지.** 패널 머리에 노란 "주의 N" (1차 T29 §2 "IssuePanel 노란 배지"). 진단은 카탈로그 로드 때 한 번 돌려 `StudioCatalog.LintCount` 에 싣는다 (H25 캐시가 있어야 싸다 — 그 전엔 `/issues` 에서 돌린 결과를 세션에 저장).
6. **인라인 오류.** 저장 실패 시 `result.Issues` 를 `IssueLocator` 로 필드에 매핑해 그 필드 아래 `.field-error` 로. 매핑 안 되는 것은 폼 상단 목록. (H09 와 경계: H10 은 링크 · 매핑, H09 는 배치 · 좁은 창 · 토스트.)

**테스트.** `IssueLocator_AppendsFieldWithAmpersand` (회귀 — 무슨 사고였는지 주석: "고치러 가기가 JSON 탭으로 떨어졌다") · `IssueLocator_MapsEveryFormFieldCode` — `FixHints.Codes` 중 아키타입 · 폴백 · 오버라이드 · POI 코드가 전부 필드 링크를 갖는다.

**완료 판정.** X4. `/issues` 에서 링크 → 편집 탭 · 스크롤 · 강조. 우측 패널이 `/issues` 와 같은 제목을 보여 준다.

---

### H07 ✅ · 막힌 상태에 탈출구 셋 — 되돌리기 · 연습장 조작 · 명령줄

**왜.** B1~B4. 연습장에서 장소를 추가하면 **재시작이 유일한 탈출구**다. 거리표 파일이 없으면 Studio 가 뜨지도 않는다.

**구현.**

1. **`LoadCatalog` 가 죽지 않는다.** `StudioWorkspace.cs:271-286` — `catch` 를 `IOException` 까지 넓히고, `MasterDataValidator.Validate` · `NpcInstanceTable.Load` 도 `try` 안으로. `Blocked` 에 **사유 종류** `BlockedKind { StaleDistances, MissingArtifact, JsonSyntax, LoaderReject, InstancesStale }` 를 싣는다 — 예외 타입과 메시지(`poi_distances.bin` 포함 여부 · `LineNumber`)로 분류한다.
2. **차단 패널이 사유별로 말한다.** `StudioLayout.razor:45-62`:
   - `StaleDistances`/`MissingArtifact` → "장소가 N개인데 거리표는 M개다 / 거리표가 없다 — 다시 만든다" + 다시 만들기 버튼(원본 · 저장소 안) **또는** 명령줄 `dotnet run tools/gen_poi_distances.cs` 를 복사 버튼과 함께.
   - `JsonSyntax` → "`zones.json` 12행이 JSON 이 아니다" + **그 파일의 원문 편집기를 패널 안에서 연다** (`Files.razor` 의 편집기 부분을 `Shared/JsonEditor.razor` 로 뽑는다) + 최근 백업 복원.
   - `LoaderReject` → 로더 메시지 + 원문 편집기 + 복원.
   - 모든 사유에 **"방금 저장을 되돌리기"** — 가장 최근 트랜잭션 백업(H14) 을 `Restore`. 그리고 **연습장이면** "원본에 적용 · 원본으로 돌아가기 · 연습장 버리기" 세 버튼(H02 확인 포함).
3. **연습장 → 원본 적용이 거리표 때문에 실패하지 않는다.** `ApplyToOrigin`(`:160-196`) — 후보에 `pois.json` 이 있으면 `loaderGate: false` 로 적용하고 결과 메시지에 "거리표를 다시 만들어야 한다" 를 붙인다. 적용 성공 시 `_directory` 를 원본으로 돌린다(지금은 연습장에 남아 파급 패널이 "연습장에서는 못 만든다" 를 방금 적용한 사람에게 보여 준다 — `StudioWorkspace.cs:180-193`, `ImpactPanel.razor:50-53`).
4. **거리표 낡음을 차단이 아닌 배너로 격하할지.**
   > **결정: 더했다.** `MasterDataLoadOptions.SkipDistances` 를 로더에 두고, Studio 는 엄격 로드가
   > **거리표 때문에** 실패했을 때만 그 옵션으로 다시 읽는다(`LoadTolerant`). 기본값이 지금 동작이라
   > `Npc.Host` 기동 경로는 인자를 주지 않으므로 영향이 0 이다 — 거리표가 낡으면 예전처럼 던진다.
   > **이유.** 장소를 더한 사람이 바로 다음에 눌러야 하는 것이 "다시 만들기" 인데, 그 버튼이 있는
   > 화면까지 같이 죽으면 재시작이 유일한 탈출구가 된다. 대신 거리가 전부 0 이 되므로
   > `MasterDataSet.DistancesAvailable` 을 싣고 상단 배너가 "소요 예측을 믿을 수 없다" 를 말한다.
   > 차단 패널은 JSON 오류 · 로더 거절에만 남겼다.
5. **상단 배지 링크.** `StudioLayout.razor:23` 연습장 배지가 `/` 로 가는데 차단 중엔 같은 패널이다 — 배지 클릭이 패널 안 연습장 절로 스크롤.

**테스트.** `LoadCatalog_BlocksInsteadOfThrowing_WhenDistancesMissing` (파일 삭제 → `IsBlocked`, `Kind == MissingArtifact`) · `ApplyToOrigin_WithNewPoi_SucceedsAndReportsStaleDistances` (X3 종단) · `LoadCatalog_ClassifiesJsonSyntaxError`.

**완료 판정.** X3. 세 상황(연습장 · 저장소 밖 · 거리표 없음)에서 각각 클릭 두 번 안에 탈출.

---

### H08 ✅ · 예외가 화면에 보인다

**왜.** B5~B7. 회로가 죽으면 버튼이 조용히 멈춘다. 초보자는 새로고침을 모른다.

**구현.**

1. `Components/App.razor` — 표준 `<div id="blazor-error-ui">` 를 **한국어로** ("문제가 생겼다. 저장한 것은 안전하다. 새로고침한다 [새로고침]"). `Routes.razor` 의 `<RouteView>` 를 `<ErrorBoundary>` 로 감싸고 `ErrorContent` 가 "이 화면을 그리지 못했다: {메시지}" + "다시 시도" (`Recover()`). 재연결 UI(`components-reconnect-modal`) 도 한국어.
2. **`try/catch` 없는 핸들러 전부** — `Home.razor:148,156,164` · `ImpactPanel.razor:107` · `StudioLayout.razor:178` · `Npcs.razor:235`(명단 없으면 빈 목록 + "명단이 없다 — 다시 만들기" 안내) · `Places.razor:166` · `Live.razor:110-120`(`catch (Exception)` 으로 루프 보호, `_running=false`). 공통 헬퍼 `Session.Try(Action, string what)` 로 통일: 예외 → `Notify($"{what}하지 못했다: {Humanize(ex)}", true)`.
3. **한글화 `StudioErrors.Humanize(Exception)`** (`Services/`): `JsonException` → "JSON 문법 오류 — {N}행 {M}열 근처" · `IOException`/`UnauthorizedAccessException` → "파일을 쓸 수 없다 — 다른 프로그램이 열고 있거나 권한이 없다: {파일}" · `Win32Exception`(dotnet 없음) → "`dotnet` 을 찾지 못했다 — PATH 를 확인한다" · `ArgumentException` → 메시지에서 ` (Parameter '…')` 제거 (근본은 `InvalidDataException` 으로 바꾼다: `StudioWorkspace.cs:409,429,478,597,656,1284,1289,1759`). 부록 C 가 대응표.
4. **검증기 모양 오류.** `ValidateAndWrite`(`:1531`) 가 검증기 예외를 잡아 `StudioIssue("V0", file, "", "파일 모양이 스키마와 다르다: …")` 로. `IssueGuide` 에 `V0` · `LOAD` 제목 등록 (`IssueGuide_CoversEveryFixHintCode` 가 `FixHints.Codes` 만 봐서 `LOAD` 가 빠져 있었다).
5. **삼킨 예외를 보이게** — `ImpactPanel.razor:98` "파급을 계산하지 못했다: …" 카드 · `ArchetypeForm.razor:429` 모달에 "요약을 만들지 못했다 — 그래도 저장은 된다" · `GlobalSearch.razor:43` · `BucketGrid.razor:154` (`Broken` 상태, H20).
6. **`LiveClient`** — 기동 시 `Uri.TryCreate(..., Absolute)` + `http/https` 검사, 아니면 `Configured=false` + 사유. 401/403 → "토큰을 확인한다".

**테스트.** `Humanize_TranslatesJsonAndIoExceptions` (대표 2건) · `LiveClient_RejectsSchemelessServerUrl`.

**완료 판정.** X8 앞부분. 개발자 도구 없이 모든 오류가 화면에 한국어로.

---

### H01 ✅ · 편집 중 이탈 가드

**왜.** A1~A3. "일단 눌러 본다" 의 전제다. 1차 T10 §5 가 약속했다.

**구현.**

1. `Services/StudioSession.cs` 에 **초안 등록부** — `RegisterDirty(object owner, Func<bool> isDirty, string what)` / `Unregister(owner)`. `AnyDirty` · `DirtyWhat`. 폼(`ArchetypeForm` · `FallbackForm` · `Npcs` 오버라이드 · `Files` · `Archetypes` JSON 탭 · `PlaceForm` · `ArchetypeNew`) 이 `OnInitialized` 에 등록, `Dispose` 에 해제.
2. **내부 이동** — `StudioLayout.razor` 에 `<NavigationLock ConfirmExternalNavigation="@Session.AnyDirty" OnBeforeInternalNavigation="Guard" />`. `Guard` 는 `AnyDirty` 면 `ConfirmDialog`(H02) "{what} 에 저장하지 않은 변경이 있다. 버리고 이동할까?" → 취소면 `context.PreventNavigation()`. `ConfirmExternalNavigation` 이 `beforeunload` 를 건다 (Blazor 내장 — `studio.js` 불필요).
3. **탭 전환** — `Archetypes.razor:247` `SetTab` 이 `Session.AnyDirty` 면 같은 확인. 확인 없이 전환하려면 자식을 `hidden` 으로 유지하는 방법도 있으나 **폼 상태를 세션에 두는 쪽이 새로고침까지 살린다** — 아래 4.
4. **초안 보존** — `ArchetypeForm`·`FallbackForm`·오버라이드 초안을 `Session.Drafts[key]` 에 둔다(회로 메모리). 같은 직업으로 돌아오면 "저장하지 않은 초안이 있다 — 이어서 / 버리기". 마법사는 `ProtectedSessionStorage`(브라우저 탭 세션) 에 5단계 입력을 JSON 으로 — 새로고침에도 남는다.
5. **NPC · Files · JSON 탭 · PlaceForm 에 Dirty** — 원본 스냅샷 + 비교. 저장 버튼 `disabled=!Dirty`.
6. **상단 "새로고침"** — `AnyDirty` 면 확인. 그리고 페이지 캐시(`_loaded`)를 무효화하는 **세대 번호** `Session.Generation` (H13 과 공유).

**테스트.** 화면 로직이라 없음. `StudioSession_DirtyRegistry_TracksOwners` 는 상수 되읽기라 쓰지 않는다.

**완료 판정.** X1 — 넷 다 확인이 뜨고 취소하면 초안 유지. 마법사 새로고침 뒤 입력이 남아 있다.

---

### H02 ✅ · 파괴적 행동은 결과를 문장으로 확인한다

**왜.** A4 · A5. 확인이 없거나("연습장 버리기"), 있어도 무엇이 되는지 말하지 않는다.

**구현.**

1. **`Shared/ConfirmDialog.razor`** — `Title` · `Lines`(문장 목록) · `ConfirmLabel` · `Danger`(빨간 버튼) · `Task<bool> ShowAsync()`. 포커스 트랩 · `Escape`=취소 · `Enter`=확인 안 함(위험 버튼은 클릭만). 모달 제목 옆에 **연습장/읽기 전용 배지 반복** (모달 `z-index:60` 이 상단 바 `10` 을 덮어 배지가 안 보인다 — `studio-v2.css:183`).
2. **적용 대상과 문장** (부록 A):
   - 연습장 버리기 — "연습장 `0917` 의 파일 {N}개가 원본과 다르다: {목록}. 버리면 그 변경이 사라진다. (백업 폴더에는 남는다)" — `SandboxChanges()` 를 반드시 먼저 돈다.
   - 원본에 적용 — "{N}개 파일이 원본을 덮어쓴다: {목록}" + `ImpactPanel` 미리보기(H09 의 `PendingFiles`) + "원본에서 다시 검증한다".
   - 개별 설정 삭제 — "#2326 의 순찰로 3곳 · 세력 · 오프셋이 지워지고 직업 기본값으로 돌아간다".
   - 백업 복원 — "`archetypes.json` 을 09-17 00:12 상태로 되돌린다. 지금과 다른 줄 {N}개" (줄 diff 개수는 `File.ReadAllLines` 비교).
   - 다시 만들기 — "`dotnet run tools/gen_npcs.cs` 를 돌린다. 5,000명 명단이 다시 만들어진다(약 30초 · 첫 실행은 빌드로 더 걸린다). 개별 설정은 남지만 **번호가 가리키는 NPC 가 바뀔 수 있다**(H16)".
   - JSON 원문 저장 — "`world_flags.json` 은 번호 체계 파일이다. 바뀐 줄 {N}개. 추가는 뒤에만" (H12 가 재배치는 거절).
   - 마법사 취소 · 하루 일과 스텝 삭제(스텝 3 이상일 때만) — 짧은 확인.
3. **시각 · 위치** — `.danger` 는 채움 빨강 + 행 우측 끝. `Home.razor:88-93` 의 네 버튼을 "비교 · 적용" / "돌아가기 · 버리기" 두 줄로.
4. **이중 실행** — 모든 저장 핸들러에 `_busy` 가드 (지금 `Npcs.razor:282` · `Files.razor:118` · `FallbackForm.razor:316` 은 두 번 눌리면 두 번 돈다).

**테스트.** 없음 (화면). H04 · H14 의 서비스 테스트가 문장의 근거(`SandboxChanges` · diff 개수)를 지킨다.

**완료 판정.** 부록 A 의 모든 행동에 모달. X2 두 번째 항목.

---

### H03 ✅ · 인구 재배분이 즉시 저장하지 않는다

**왜.** A6. **폼 안의 버튼 하나가 다른 편집을 지우고 파일을 바꾼다.** 1차 T10 §4 "여러 항목 `SetInArrayItem`" 의 구현이 트랜잭션 경계를 잘못 잡았다.

**구현.**

1. `StudioArchetypeForm` 에 `ImmutableArray<WeightChange> Rebalance` 를 더한다 (기본 빈 배열). "이 안으로" 는 `_form with { Rebalance = proposal.Changes }` — **디스크에 쓰지 않는다.** `Sum` 계산이 `Rebalance` 를 반영한다.
2. `SaveArchetypeForm` 이 `Rebalance` 의 줄들을 같은 `archetypes.json` 후보에 `SetInArrayItem` 으로 얹어 **한 트랜잭션**. `ApplyRebalance(proposal, skip)` 공개 메서드는 지운다 (마법사는 이미 `CreateArchetype` 안에서 처리한다).
3. `DefinitionDiff.Describe` 가 다중 직업 변경을 문장으로 — "인구 60 → 80명 · 농부 −10 · 광부 −10 (합 1.0 ✓)". 저장 모달에 표로.
4. "되돌리기(초안 취소)" 가 `Rebalance` 도 비운다. 인구를 다시 바꾸면 `Rebalance` 를 비우고 "재배분 방법 보기" 를 다시 보이게 (낡은 안이 적용되는 것을 막는다 — A7 과 같은 결함).

**테스트.** `SaveArchetypeForm_WithRebalance_WritesAllRowsInOneTransaction` (합 1.0 · 파일 한 번 쓰기 · 백업 stamp 하나) · `ApplyRebalance` 삭제 커밋 메시지에 "즉시 저장 경로를 없앴다".

**완료 판정.** X6.

---

### H04 ✅ · 연습장 안전

**왜.** C3 · C4 · A4. "망쳐도 된다" 가 탭 두 개면 거짓이고, 같은 이름이면 아침 작업이 사라진다.

**구현.**

1. **이름 충돌** — `OpenSandbox`(`StudioWorkspace.cs:65-88`) 가 대상 폴더가 있으면 `SandboxExistsException(name, changedFiles)` 를 던지고, 화면은 "이미 있다 — 이어서 열기 / 새로 만들기(기존 삭제, 확인)" 를 묻는다. `ResumeSandbox(name)` 신설. 한글 이름은 `studio-<safe>-<sha8(원래 이름)>` 로 충돌을 없애고 표시는 원래 이름.
2. **기존 연습장 목록** — 시작 화면 연습장 절에 `lab/studio-*` 목록(이름 · 만든 시각 · 원본과 다른 파일 수) + "열기". `Workspace.Sandboxes()`.
3. **원본 잠금** — 만들 때 원본 파일 해시를 `lab/studio-<name>/origin.lock.json` 에 기록. `ApplyToOrigin` 이 지금 원본 해시와 다르면 "연습장을 만든 뒤 원본이 바뀐 파일: {목록}. 적용하면 그 변경이 덮인다" 를 확인 모달(H02)에 싣는다.
4. **탭 간 일치** —
   > **결정: 전역 + 알림.** `StudioWorkspace.DirectoryChanged` 를 모든 세션이 구독해 `Reload()` 하고
   > 상단 배너로 "다른 탭이 연습장을 열었다 — 이 탭도 그 폴더를 본다" 를 말한다.
   > **이유.** 도구는 한 사람이 쓴다. 탭마다 다른 폴더를 보는 쪽이 오히려 위험하다 —
   > 원본인 줄 알고 연습장에 쓰게 된다. 회로 단위로 옮기면 호출부 ~40곳을 고쳐야 하고,
   > 그 값은 "두 폴더를 동시에 본다" 인데 그것을 원하는 상황이 없다.
   > 세션이 알던 폴더와 지금 폴더가 다르면 배너가 먼저 뜨고, 저장 경로는 **파일 지문**(H13)이
   > 지킨다 — 폴더가 바뀌면 지문도 어긋나므로 같은 거절로 수렴한다.
5. **적용 뒤 복귀** — 적용 성공 시 원본으로 돌아가며 "연습장은 남아 있다 — 버리려면 …" 토스트 (H07 3 과 같이).
6. **버리기** — 삭제 대신 `lab/.trash/<name>-<시각>` 로 이동. 30일 뒤 정리(H14 백업 정리와 같은 루틴).
7. **복사 범위** — `CopyFrom`(`:1579-1600`) 을 재귀 복사로 (`prompt/` 포함). 지금은 최상위 + `localization/` 만이라 앞으로 검증이 `prompt/` 를 읽으면 판정이 갈린다.

**테스트.** `OpenSandbox_RefusesToOverwriteExisting` (회귀 — "같은 날 두 번 만들면 아침 작업이 사라졌다") · `ApplyToOrigin_WarnsWhenOriginChangedSinceOpen` · `Save_RejectsWhenDirectoryChangedUnderSession`.

**완료 판정.** X2.

---

### H15 ✅ · 표시 이름이 조용히 버려지지 않는다

**왜.** C5 · C6. 마법사에서 넣은 한국어 이름이 연습장에서 원본으로 안 간다. 화면은 V1~V15 라고 하는데 V14 는 안 돈다.

**구현.**

1. `s_editableFiles`(`StudioWorkspace.cs:21-26`) 와 별도로 **비교 · 적용 · 백업 대상** `s_transactionFiles` = 편집 파일 + `localization/*.json`. `SandboxChanges` · `ApplyToOrigin` · `Backup` 이 그것을 쓴다. 키는 `localization/ko-KR.json` 상대 경로 그대로.
2. **V14 를 Studio 검증에** — `Validate()`(`:1493`) 와 `ValidateAndWrite` 뒤에 `LocalizationTable.Missing(data)` 를 `StudioIssue("V14", "localization/ko-KR.json", key, "표시 이름이 없다")` 로 합친다. `IssueGuide` 에 `V14` 제목 · `IssueLocator` 가 `npc.<id>` 키를 `/archetypes/{id}?tab=edit&field=name` 으로.
3. 문구 — `Home.razor:63` · `StudioLayout.razor:88` "V1~V15" 를 실제 도는 범위로. V15 가 무엇인지 확인해 돌릴 수 있으면 돌린다.
   > **결정: 검증기는 건드리지 않고 Studio 가 합쳐서 본다.**
   > V14 는 **표시 계층**이라 기동을 막지 않는 경고이고(그것이 `MasterDataValidator` 밖에 있는 이유다),
   > V15 는 샤드 정의라 `ShardTable.Load` 가 기동 경로에서 던진다 — 마스터데이터 폴더만으로는 돌릴 수 없다.
   > 검증기에 V14 를 넣으면 **기동 판정이 바뀐다**(경고 → 실패). 그래서 `ValidateLocked` 가
   > `MasterDataValidator` 결과에 `LocalizationTable.Missing` 을 더해 화면에만 합친다.
   > 화면 문구의 "V1~V15" 는 실제 도는 범위를 적지 않으므로 지웠다 — 숫자 범위 대신 "전체 검증" 이다.
4. 마법사 — 로케일 파일이 없으면 만들어 넣는다. 영어 이름을 비우면 5단계 요약에 "영어 이름: beekeeper (id 그대로)" 를 보여 준다.

**테스트.** `ApplyToOrigin_CarriesLocalizationFiles` (회귀 — "양봉가 이름이 원본에 안 갔다") · `Validate_ReportsMissingDisplayName`.

**완료 판정.** 연습장에서 마법사 → 적용 → 원본 `ko-KR.json` 에 `npc.beekeeper`. 이름을 지운 직업이 검증 ✗ 로 보인다.

---

### H11 ✅ · 장소 id 중복 · 형식 · 정원을 저장 전에 거절한다

**왜.** C1. 같은 id 의 장소 둘은 검증도 로더도 통과하고, 이후 편집이 엇갈린다.

**구현.**

1. `AppendPoi`(`StudioWorkspace.cs:831-889`) — `data.Pois.TryGet(id)` 면 거절 "장소 id `apiary_001_04` 가 이미 있다". `ValidateNewId` 규칙(영문 소문자 · 숫자 · 밑줄 · 숫자 시작 금지) 을 POI 에도. 정원 ≥ 1 서버 검사.
2. **검증기 V1 에 `id` 중복** — `src/Npc.MasterData/Validation/MasterDataValidator.cs` V1 이 `pois` 의 `id` 도 본다 (zones · archetypes 는 로더가 던진다 — pois 만 빠져 있다). `FixHints` 에 문구. **이것은 마스터데이터 검증기 변경이라 `docs/reference_masterdata.html` V1 설명도 같은 커밋에서.**
3. `SuggestId`(`StudioPlaces.cs:169-182`) — "개수+1" 대신 **쓰이지 않은 최소 번호**.
4. `PlaceForm` — id 입력 `@oninput` 에 형식 · 중복 즉시 표시, 저장 버튼 비활성 + 사유. 새 `subtype` 이면 `poi.<subtype>` 로케일 키를 같은 트랜잭션에 넣는다 (`LocalizationTable.cs:136` 가 그 키를 본다).

**테스트.** `AddPoi_RejectsDuplicateId` (대조군 — 검사기 자체 시험) · `Validator_V1_FlagsDuplicatePoiId` (마스터데이터 테스트 쪽).

**완료 판정.** X5 앞부분.

---

### H12 ✅ · `code` · `bit` 재배치 금지를 코드로

**왜.** C2. CLAUDE.md §2.4 "절대 재배치하지 않는다" 를 사이드바가 약속하지만 강제하는 곳이 없다. 원문 편집기가 열려 있는 한 한 줄 실수로 프리베이크 2,880건과 명단이 조용히 어긋난다.

**구현.**

1. `ValidateAndWrite` 안에 **번호 고정 검사** `CodePinning.Check(before, after)` (`src/Npc.MasterData/Authoring/CodePinning.cs` 신설 — 편집 도구라 `Authoring/` 이 맞다): 파일별 `(id → code|bit)` 쌍을 뽑아 **기존 쌍의 값이 바뀌었거나 id 가 사라졌으면** `StudioIssue("PIN", file, path, "id 'x' 의 code 3 → 7 — 번호는 재배치할 수 없다")`. 대상: `archetypes`(code) · `pois`(code) · `zones`(code) · `actions`(code) · `world_flags`(bit) · `items`(code 가 있으면) · `factions`(code). 추가는 허용.
2. 삭제는 **경고로 두되 저장은 허용할지** —
   > **결정: 거절한다.** `CodePinning.Check` 가 "id 가 사라졌다" 도 위반으로 낸다.
   > **이유.** 지우면 프리베이크 2,880건의 파일 이름과 명단이 가리키는 번호가 빈다 —
   > 그 파급은 저장 화면에서 보여 줄 수 있는 크기가 아니다. `npc` CLI 로 파급을 보고 한다.
   > **예외는 백업 복원 하나다**(`pinCheck: false`) — 되돌리기는 정의상 그 사이에 추가된 id 를
   > 없애므로, 여기서 막으면 되돌릴 수 없게 된다.
3. `StudioLayout.razor:104-112` 안전장치 목록을 사실로 — "번호 재배치는 저장이 거절된다".

**테스트.** `ValidateAndWrite_RejectsCodeReassignment` (대조군: 두 직업의 code 를 맞바꾼 후보) · `ValidateAndWrite_AllowsAppendedCode`.

**완료 판정.** 원문 편집기에서 code 를 맞바꿔 저장 → 거절 문장에 어느 id 가 어떻게 바뀌었는지.

---

### H13 ✅ · 디스크가 그 사이 바뀌었으면 저장을 거절한다

**왜.** C7 · C12 · A1(새로고침). 도구 밖의 편집(VS Code · 다른 탭 · 생성기)을 Studio 가 모른다.

**구현.**

1. **로드 시 해시** — `StudioArchetypeDocument` · `StudioArchetypeForm` · `StudioFallbackForm` · `StudioNpcOverrideEditor` · 원문 로드가 **파일 SHA-256** (`FileStamp`) 을 같이 든다. 저장 메서드가 `FileStamp` 를 받아 `ValidateAndWrite` 직전에 디스크와 비교 → 다르면 `StudioSaveResult(false, "저장 뒤 파일이 바뀌었다 — 다시 읽고 고친다 (바뀐 파일: …)")` + 새로고침 버튼.
2. **폼 3-way** — `ApplyForm`(`:1141-1236`) 이 `before` 를 **디스크가 아니라 폼의 `_original` 스냅샷**에서 잡는다. 디스크 ≠ 원본 스냅샷이면 1 의 거절. (지금은 외부 변경을 옛 값으로 되돌려 쓴다.)
3. **세대 번호** — `Session.Generation` 이 `Reload()` · `Apply()` 마다 오른다. 페이지 `_loaded` 가드가 `(Id, Generation)` 쌍을 본다 → 상단 새로고침 뒤 편집 화면이 새 값을 보여 준다 (Dirty 면 H01 확인 먼저).
4. **파일 감시** — `FileSystemWatcher` 로 `masterdata/*.json` 변경을 잡아 `Workspace.ExternalChange` 이벤트 → 모든 세션에 배너 "밖에서 `archetypes.json` 이 바뀌었다 — 새로고침". 폴링보다 싸고, 없어도 1 이 지킨다.
5. **생성기 실행 중 저장 거부** — `GeneratorRunner.Busy` 를 워크스페이스가 보고 `ValidateAndWrite` 가 "명단을 만드는 중이다 — 끝나면 저장한다" 로 거절. `ChangedFiles` 를 워크스페이스 전역으로 옮겨 탭마다 "해야 할 일" 이 같게.

**테스트.** `Save_RejectsWhenFileChangedSinceLoad` (VS Code 흉내: 로드 → 파일 수정 → 저장 → 거절 · 파일 불변) · `SaveArchetypeForm_DoesNotRevertExternalEdit` (회귀 대조군).

**완료 판정.** X7.

---

### H14 ✅ · 여러 파일 쓰기의 원자성 + 트랜잭션 되돌리기

**왜.** C8~C10 · B8. 마법사가 반만 써질 수 있고, 써진 뒤엔 되돌릴 수 없다.

**구현.**

1. **쓰기 순서** — `ValidateAndWrite`(`:1555-1562`): ① 대상 전부 백업(같은 stamp) ② 전부 `.studio.tmp` 로 쓰기 ③ 전부 `Move` — ③ 에서 하나라도 실패하면 이미 옮긴 것을 백업으로 되감고 `.tmp` 를 지운다 (`finally`). 완전한 원자성은 파일 시스템이 안 주지만 **되감기까지** 는 준다.
2. **백업 실패는 저장 실패다** — `:1706` 에서 삼키지 않는다. 기동 시 백업 폴더 쓰기 검사 → 못 쓰면 상단 배지 "되돌리기를 만들 수 없다: {경로}" + 저장은 확인 뒤에만.
3. **트랜잭션 단위** — `Backups()` 가 stamp 폴더를 **한 항목**으로 (파일 N개 · 시각 · 어떤 작업이었는지 — `StudioSaveResult` 에 `Label`("새 직업 beekeeper" · "장소 apiary_001_04 추가" · "대장장이 편집") 을 실어 `manifest.json` 으로 stamp 폴더에). `Restore(backup)` 이 묶음 전부를 `ValidateAndWrite` 에 한 번에 넘긴다. 파일 하나만 되돌리는 것은 접이식 고급.
4. **경로 보존** — `Backup(path, file)` 이 `localization/ko-KR.json` 을 하위 폴더 그대로 (`:1560,1649,1670` 의 `GetFileName` 제거). `Restore` 가 그 경로로.
5. **정리** — 30일 + **파일별 최근 5개는 날짜와 무관하게 보존**. 연습장 백업(`FolderKey`) 은 연습장 버릴 때 같이.
6. "방금 저장을 되돌리기" 를 저장 성공 토스트에 링크로 (H09) — 가장 흔한 되돌리기다.

**테스트.** `Restore_RevertsWholeTransaction` (마법사 → 되돌리기 → 6개 파일 바이트 복원 · 검증 초록) · `ValidateAndWrite_RollsBackWhenSecondMoveFails` (두 번째 파일을 잠근 채 — Windows 에서 `FileShare.None` 으로 열어 두면 재현된다) · `Backup_PreservesSubdirectory`.

**완료 판정.** 마법사로 만든 직업을 클릭 두 번으로 되돌린다. `.studio.tmp` 가 남지 않는다.

---

### H09 ✅ · 저장 결과를 그 자리에서

**왜.** A9~A11. 성공은 시작 화면에 가야 보이고, 실패는 좁은 창에서 아예 안 보인다.

**구현.**

1. **성공** — `Session.Apply` 성공 시 레이아웃이 **결과 카드**를 띄운다 (토스트 대신): "저장했다 · {Label}" + `ImpactPanel` 축소판(해야 할 일 1~3줄 · 다시 만들기 버튼) + "방금 저장 되돌리기" + 닫기. 10초 뒤 접히되 사라지지 않는다("최근 저장" 으로 상단 바에 남는다).
2. **실패** — 페이지 안 `IssueList`(H10 의 `IssueLocator.Views`) 를 **폼 위**에, 필드 매핑되는 것은 필드 아래. 토스트에 첫 오류 링크. 오른쪽 패널에도 그대로.
3. **초안 오류를 전역에 섞지 않는다** — `StudioSession.Apply`(`:74`) 가 실패 시 `Issues` 를 바꾸지 않고 `LastRejected` 에 둔다. 우측 패널은 `Issues`(디스크) + 접이식 "방금 거절된 초안의 문제 N건".
4. **좁은 창** — `app.css` 1100px 규칙을 `display:none` 에서 **접이식 + 머리만 남김**(오류 개수 배지)으로. 800px 이하는 상단 바에 배지만.
5. **모달의 파급이 미래를 본다** — `ImpactPanel` 에 `[Parameter] ImmutableArray<string> PendingFiles` → `ImpactAnalyzer.Of(dir, Changed ∪ Pending)`. 저장 모달 · 마법사 5단계 · 원본 적용 확인이 쓴다.
6. **토스트** — `role="status"` `aria-live="polite"` · 닫기 × · 성공 6초 자동 소멸 · 오류는 남음 · 큐(두 개 연속이면 둘 다).
7. 저장 버튼 옆 사유 — `_previewError`/`_preview.Error` 가 있으면 버튼 비활성 + "초안이 컴파일되지 않아 저장할 수 없다".

**테스트.** `Apply_KeepsDiskIssuesWhenDraftRejected` (세션 로직).

**완료 판정.** X8 뒷부분. 어느 화면에서 저장해도 그 화면을 떠나지 않고 다음 할 일이 보인다.

---

### H05 ✅ · 마법사 단계 검증

**왜.** A7 · A8 · A3. 5단계 끝에서 거절당하면 어느 단계로 돌아가야 하는지 모른다.

**구현.**

1. **1단계** — id `@oninput` 에 `ValidateNewId` + `Session.Catalog.Archetypes` 중복 즉시. 도움말에 "영문자로 시작" 추가(`ArchetypeNew.razor:32` 가 빠뜨렸다). `CanAdvance` 가 그것을 본다. 한국어 이름 · 설명 필수는 그대로, 영어 이름 비면 "id 를 쓴다" 미리 표시.
2. **2단계** — `_population` · `_from` 에 `@bind:after="Propose"`. 유효 안이 없으면 "다음" 차단 + "인구 {N}명을 뗄 곳이 없다 — 인구를 줄이거나 닮은 직업을 바꾼다". `_strategy` 가 `default` 로 떨어지지 않게 `FirstOrDefault` 대신 명시.
3. **3단계** — 정원 < 인구 · 세부 유형 없음이면 **차단** + 사유. "장소를 먼저 만든다" 링크는 마법사 초안(H01 4)을 남긴 채 이동하고, 돌아오면 이어진다. (인라인 새 장소는 H06 뒤로 — 같은 트랜잭션에 `pois.json` 을 더하는 것은 H18 의 폼을 재사용해야 한다.)
4. **5단계 요약** — 바뀌는 파일 6개(`CreateArchetype` 이 실제로 만질 목록을 `PreviewCreateArchetype(draft)` 로 미리 받는다) · 허가되는 장소 목록(`Permit`) · 재배분 표(누가 몇 명) · 영어 이름 대체 · **`ImpactPanel PendingFiles`**. "만든 뒤 해야 할 일" 은 `ImpactAnalyzer` 가 만든다 (지금은 손으로 쓴 세 줄).
5. **실패** — `result.Issues` 를 단계에 매핑(V5→2 · V10→3 · V8/V3→4 · id→1) 해 "2단계로 돌아가기" 링크. 예외는 H08 한글화.
6. **초안 보존 · 취소 확인** — H01 4 · H02.
7. 읽기 전용이면 1단계 위 배너 "읽기 전용 — 만들 수 없다" (H26).

**테스트.** `CreateArchetype_PreviewListsEveryFileItWillTouch` (드리프트 — 미리보기 목록 = 실제 쓴 파일 목록) · `WizardState` 순수 클래스가 생기면 `WizardState_BlocksWhenNoValidRebalance`.

**완료 판정.** X5 뒷부분 · X9 뒷부분.

---

### H06 ✅ · 마법사 4단계 하루 일과 편집기

**왜.** §1.3 첫 줄. 1차 S2 판정 "하루 일과 6스텝" 은 마법사 밖에서 했다. 초보자는 만든 뒤 "하루 일과 탭" 을 찾아가야 하고, 원본 직업의 레시피가 그대로 들어온 것을 모른다.

**구현.**

1. `FallbackForm` 을 **초안 모드**로 — `[Parameter] StudioFallbackForm? Draft` + `[Parameter] EventCallback<StudioFallbackForm> DraftChanged` + `[Parameter] string ArchetypeIdForChoices`(허용 행동 · 심볼 바인딩은 **초안 직업**으로 — `LoadStepChoices` 가 `ArchetypeDef` 를 직접 받는 오버로드). 파일 없이 `PreviewFallbackForm(form, draftDef)` 로 판정 · 예측.
2. 마법사 4단계 — `_from` 의 폴백을 초안으로 복제해 편집기에 넣는다. 오른쪽 `BehaviorPreview` 는 **가상 개체**(1차 D.7) 로. 원본 레시피 · 아이템을 쓰는 스텝에 노란 표시 "닮은 직업의 것이다 — 새 직업에 맞나?".
3. `StudioNewArchetype` 에 `Steps` 를 더하고 `CreateArchetype` 이 복제 대신 그것을 쓴다. 비어 있으면 지금처럼 복제.
4. `TutorialChecklist` "하루 일과를 짠다" 가 **스텝을 한 번이라도 바꿨을 때** 찍히게 (`MarkStep("wizard-plan-edited")`).

**테스트.** `CreateArchetype_UsesEditedSteps` (종단 — 스텝 하나를 바꿔 만든 뒤 `fallback_plans.json` 에 그 스텝) · `PreviewFallback_WorksWithoutFile` (초안 직업으로 판정).

**완료 판정.** X9 앞부분. S2 를 마법사 안에서만 끝낼 수 있다.

---

### H16 ✅ · 파생물 재생성을 견고하게 + 오버라이드 보호

**왜.** B9 · C11 · C12.

**구현.**

1. `GeneratorRunner.RunAsync`(`:79-115`) — stdout · stderr 를 `Task.WhenAll` 로 동시에 읽는다(교착 제거). `CancellationToken` + 10분 타임아웃 → `process.Kill(entireProcessTree: true)`. `_gate.Wait(0)` 실패면 "이미 돌고 있다" 거절. `Busy`·`Output` 변경을 `event Action? Changed` 로 → `ImpactPanel` 이 구독해 **진행 중 출력이 흐른다** + 취소 버튼. 기동 시 `dotnet --version` 으로 `Available` 판정.
2. `ImpactPanel.Regenerate` — `try/catch` (H08) · 확인 모달(H02).
3. **오버라이드 보호** — 재생성 전 `npc_overrides.json` 의 각 NPC 에 대해 (직업 · 지역 · 집) 스냅샷 → 재생성 뒤 비교 → 달라진 항목을 결과 카드에 "#2326 이 위병(동쪽 장터) 에서 농부(남쪽 들판) 가 됐다 — 순찰로 개별 설정이 맞지 않는다" + 그 NPC 링크. 같은 지역이 아니면 V13 이 잡지만 같은 지역 다른 직업은 못 잡는다.
   > **결정: 스냅샷 비교로 판정한다** (코드를 읽어 추론하지 않는다).
   > `gen_npcs.cs` 는 직업 code 순 결정론 배치라 인구가 안 바뀌면 번호가 유지되지만,
   > 그 성질은 생성기의 구현 세부이고 바뀌면 조용히 틀린다. 재생성 **전후로** 개별 설정이 붙은
   > 번호의 (직업 · 지역)을 찍어 견주고(`OverrideTargets` · `OverrideDrift`), 달라진 것만 카드에 적는다.
4. 생성기 실행 중 워크스페이스 저장 거부는 H13 5.

**테스트.** `GeneratorRunner_RefusesConcurrentRun` · `GeneratorRunner_RefusesSandboxInsideRepo` (지금 테스트는 저장소 **밖**만 본다 — `lab/…/masterdata` 경로로 `CanRunFor == false`) · `OverrideDrift_ReportsReassignedNpc` (스냅샷 비교 함수의 대조군).

**완료 판정.** 첫 실행(빌드 포함)에서 출력이 흐르고 취소가 된다. 재생성 뒤 엉뚱해진 오버라이드가 카드에 뜬다.

---

### H18 ✅ · 장소 폼 — 지도 클릭이 실제로 좌표를 찍는다

**왜.** §1.3 T13. 화면이 "클릭해도 된다" 고 말하는데 아무 일도 안 한다. 그 외 E5 · E9 의 장소 부분.

**구현.**

1. `ZoneMap.razor` — `[Parameter] EventCallback<(float X, float Z)> MapClicked`. SVG 배경 `<rect>` 에 `@onclick` → `MouseEventArgs.OffsetX/Y` 를 viewBox 좌표로 환산 (`getBoundingClientRect` 가 필요하면 `studio.js` 의 `studio.svgPoint(el, clientX, clientY)` 한 함수). `[Parameter] (float X, float Z)? Candidate` 를 받으면 **점선 원 + "새 장소"** 라벨로 그린다.
2. `PlaceForm` — `MapClicked` → `_x/_z`, `Candidate` 로 미리보기. 문구 유지.
3. **정원 판정** — `WorkplaceVerdict` 와 같은 문장을 여기서도: "`apiary` 정원 합 {지금+새} vs 이 유형을 일터로 쓰는 직업 인구 {N}" (없으면 "이 유형을 쓰는 직업이 아직 없다").
4. **선택 위젯** — 세부 유형 `datalist` + 목록에 없으면 "새 유형이다 — 확인" 칩 · 유형 select 한국어(`StudioPlaces.cs:260` 표) · 일할 수 있는 직업 체크 목록(직군별) · 얻을 수 있는 것 아이템 다중 선택 · 여는/닫는 시간 `from ≤ to` 또는 자정 넘김 설명.
5. id 를 손본 뒤 세부 유형을 바꿔도 덮어쓰지 않는다 (`_idTouched`).
6. `Places.razor` `?poi=` 로 오면 그 카드로 `scrollIntoView`.
7. 기존 장소 **편집 · 삭제는 넣지 않는다** — 삭제는 번호 체계(H12) 문제고, 편집(정원 · 시간 · 허가) 은 다음 계획. 카드에 "편집은 고급 · JSON" 링크만.

**테스트.** 없음 (화면). H11 이 서비스 쪽을 지킨다.

**완료 판정.** 지도 클릭 → 좌표 칸이 바뀌고 점선 원이 그 자리에. 세부 유형 오타에 "새 유형" 확인이 뜬다.

---

### H22 ✅ · 자유 문자열을 선택으로

**왜.** E5~E7. 후보가 데이터에 있는데 타이핑을 시킨다.

**구현.**

1. `ArchetypeForm` — 주력 제작품 · 기본 목표 · 시작 소지품을 **칩 + 추가 드롭다운** (`_choices.Recipes` · `_choices.Items`). 소지품은 `아이템 · 개수` 표. `ParseInventory` 삭제. 기본 목표는 자유 문자열이 맞지만 300토큰 예산 카운터를 붙인다(`Suffix` 예산은 1차 §2.5).
2. **인구 입력을 "명" 으로** — 주 입력 `명`(정수), 보조로 비율 표시. `population_weight` 는 `명/5000` 반올림 4자리 — 마지막 항목에 잔차를 몰아 합 1.0 정확히(H03 재배분과 같이).
3. `TraitBars` — 숫자 직접 입력 + 인터럽트 임계값(용기 40 등 `interrupts.json` 의 `archetype_trait` 에서 읽어) 눈금 표시.
4. 숫자 `min/max` 를 **서버에서도** 클램프 · 거절 (`Npcs.razor:164,178` · `PlaceForm:40` · `ArchetypeNew:74`).
5. `FallbackForm` — 현재 값이 목록에 없으면 `<option disabled selected>(허용 안 됨) Craft</option>` 을 앞에 넣어 화면과 모델을 맞춘다. 필수 인자에 `*` + 비면 인라인 "필수". 행동을 바꾸면 "인자를 다시 고른다" 안내. `loop` · `on_step_fail` 은 읽기 전용이 맞다(폴백은 항상 `true`/`skip`) — 칩에 그 이유를 ⓘ 로. 타임아웃 상한(예: 24h) · 인자 이름에 ⓘ (`FieldGuide` 에 `fallback_plans.json#/steps/args/<name>` 항목 — H28 7).
6. `.step-editor-row` 5열 고정(`studio-v2.css:180`) → 인자 칸 `flex-wrap`.

**테스트.** `SaveArchetypeForm_WithoutChanges_IsByteIdentical` 이 그대로 지킨다 (위젯이 바뀌어도 서식은 같아야 한다).

**완료 판정.** 편집 폼에 자유 문자열 칸이 설명 · 기본 목표 · 대화 프로필뿐이다.

---

### H23 ✅ · NPC 화면

**왜.** E8 · E9 · A2.

**구현.** `Npcs.razor:84` `Route="Route"`(편집 중 값). 4개 초과 · 중복 클릭에 토스트 "최대 4곳이다 / 이미 있다" + 지도 점 흐리기. Dirty (H01). 목록 지역 `Lexicon.Zone` · 집/일터 `Lexicon.PlaceName`. 번호 검색은 `#` 접두 또는 정확 일치 우선. 검색 시 `<Virtualize>` 또는 결과 상한 200 + "더 좁힌다". 예측이 없으면 절을 숨기지 말고 "하루를 그릴 수 없다 — {이유}". `DeleteOverride` 가 실패하면 폼 값을 되돌린다(지금은 화면만 비워진 채 남는다).

**테스트.** 없음.

**완료 판정.** 지도를 세 번 클릭하면 선이 즉시 세 점을 잇는다.

---

### H21 ✅ · 말 · 버튼 · 위치 통일

**왜.** E1~E4.

**구현.**

1. **저장 동사 하나** — 모든 저장이 "검증하고 저장". 만들기는 "검증하고 만들기". 모달 확인 버튼은 "저장" 그대로. "원본에 적용" 유지(다른 행동이다).
2. **"되돌리기" 분리** — 폼은 **"초안 취소"**, 백업은 **"백업에서 복원"**. 매뉴얼 §8.3 도.
3. **UI 용어표** `src/Npc.Narrative/Lexicon.Ui` — 직업 · 장소 · 하루 일과 · 개별 설정 · 돌발 반응 · 미리 구운 계획 · 연습장. 토스트(`StudioWorkspace.cs` 메시지 ~30곳)가 그것을 쓴다. `Lexicon_Ui_HasNoDeveloperWords` — 토스트 문자열에 "아키타입" · "오버라이드" · "POI" · "폴백" 이 없다 (드리프트).
4. 버튼 위치 · 순서 — 편집 화면은 패널 머리 우측 `[검증하고 저장][초안 취소]`, 만들기 화면은 하단 `[이전][다음/만들기] … [취소]`, 위험 버튼은 별도 줄 우측.
5. 브레드크럼 `시작 › 직업 › 대장장이` (`Shared/Breadcrumb.razor`) 를 상세 화면 머리에. 못 찾음 화면에 "목록으로". "+ 새 직업 만들기" 를 `primary`.
6. `page-lead` 가 없는 hero (`Places:44-49` · `Files:57`) 에도.

**테스트.** 3 의 드리프트 하나.

**완료 판정.** 부록 B 표와 화면이 일치한다.

---

### H19 ✅ · 전역 검색

**왜.** §1.3 T32. `Ctrl+K` 가 문구뿐이다.

**구현.** `studio.js` — `keydown` `Ctrl/Cmd+K` → `#global-search` focus. Blazor 쪽 `@onkeydown` — `Enter` 첫 결과 이동 · `↑↓` 선택 · `Escape` 닫기. `@onblur` 180ms 지연 대신 `focusout` 에서 `relatedTarget` 이 결과 안이면 유지. `Session.Changed` 에 `SearchIndex()` 재생성(저장 · 연습장 · 차단 해제 뒤 새 직업이 검색된다). "찾은 것이 없다" 는 링크가 아닌 `<span>`. NPC 번호는 `InstanceCount` 안일 때만 제안.

**테스트.** 없음.

**완료 판정.** 어느 화면에서든 `Ctrl+K` → 입력 → `Enter` 로 이동.

---

### H20 ✅ · 문서가 약속한 나머지

**왜.** §1.3 T24 · T27 · 1차 §7 "직군 기타".

**구현.**

1. `--planstore` 가 없으면 상황별 탭을 **숨긴다** (`Archetypes.razor:83` `@if (Store.Available)`). `StudioOptions.cs:10` 주석과 매뉴얼이 이미 그렇게 말한다. 대신 개요 어딘가에 "미리 구운 플랜이 없다 — 17장" 한 줄.
2. `PlanStoreReader` — 읽었는데 컴파일 안 되는 파일을 `BucketState.Broken` 으로 격자에 (지금은 "없음" 과 구분 안 됨).
3. **새 직업 직군** — 마법사 1단계 직군 칩 아래 "직군은 닮은 직업 후보를 좁히는 데만 쓴다. 만든 직업은 '기타' 로 표시된다" 를 적는다. 근본 해결(스키마에 `group` 필드) 은 밖에 둔다(§5).
   > **결정: 더하지 않는다** (이 계획 밖).
   > 직군은 지금 `Lexicon.Groups` 표가 든다. 스키마에 `group` 을 더하면 `ContentHash` 가 바뀌어
   > **미리 구운 계획이 전량 무효**가 되는데, 그 대가로 얻는 것은 새 직업이 '기타' 로 묶이지 않는 것뿐이다.
   > 지금은 마법사 1단계가 "직군은 저장되지 않는다" 를 적어 사실을 말한다.
4. 반응 애니메이션 — `BehaviorPreview.razor:262-267` 은 순간이동이다. 2초 뛰기를 넣거나(`behaviorPreview.dash(id, x, z, 2000)` — JS 로 보간, 쉽다) 1차 문서 T24 의 문구를 "이동" 으로 고친다. **권장: 넣는다** — 30줄이다.
5. 1차 계획 §5 T24 · T26 · T27 · T32 의 ✅ 옆에 "→ 2차 H20/H19 에서 마무리" 주석 (기록의 정확성).

**테스트.** `PlanStoreReader_MarksUnreadablePlanAsBroken` (대조군: 깨진 JSON 하나).

**완료 판정.** 매뉴얼 §7 · §9.1 의 문장이 사실이다.

---

### H25 ✅ · 체감 성능

**왜.** E12 · E13. 느린 것은 초보자에게 "고장" 이다.

**구현.**

1. `StudioWorkspace` 에 **로드 캐시** — `(directory, 파일 해시 합)` 키로 `MasterDataSet` + `NpcInstanceTable` 을 한 벌 캐시. `ValidateAndWrite` 성공 · 디렉터리 전환 · 생성기 완료 · `FileSystemWatcher`(H13 4) 에서 무효화. `WithData` · `LoadCatalog` · 모든 `Load*` 가 캐시를 읽는다. 락은 그대로(전환 도중 반쪽 읽기 방지).
2. 렌더마다 도는 getter 를 `OnParametersSet`/`Set` 디바운스 뒤 1회 계산으로 — `ArchetypeForm.Chips`·`WorkplaceVerdict`·`DutyVerdict` · `ArchetypeNew.WorkplaceVerdict` · `Npcs.Duty` · `Interrupts.Then/Archetypes` · `ArchetypeOverview:185-189`.
3. 로딩 표시 — `Issues.RunLint` 를 `Task.Run` + `await` 로 "진단 중…" 이 보이게. 저장 중 버튼 스피너.

**테스트.** 없음. 부하 측정은 `bytesPerTick` 이 아니라(틱 루프가 아니다) 슬라이더 `oninput` 당 `MasterDataLoader.Load` 호출 0회 — 카운터를 `WithData` 에 두고 수동 확인.

**완료 판정.** 슬라이더가 끊기지 않는다. `Interrupts` 상황 버튼이 0.2초 안에.

---

### H26 ✅ · 읽기 전용 · 기동 인자

**왜.** A12 · A13.

**구현.** 읽기 전용이면 편집 화면 상단 배너 + 폼 전체 `<fieldset disabled>` + 마법사 1단계에서 차단. `StudioOptions.Parse` — 모르는 인자 · 값 누락 · 범위 밖 포트는 `--help` 출력 후 종료(코드 2). 경로 폴백(`ResolveMasterData` 가 저장소 `masterdata/` 로 떨어진 경우) 은 콘솔 + 상단 배지 "요청한 경로가 없어 `…\masterdata` 를 열었다". 비루프백 `--bind` + 편집 모드면 콘솔 경고 + 상단 배지 "LAN 에 열려 있다".

**테스트.** `StudioOptions_RejectsUnknownArgument` (대조군).

**완료 판정.** `--readonly`(오타) 로 띄우면 뜨지 않고 도움말이 나온다.

---

### H24 ✅ · 접근성 · 키보드 · 모션

**왜.** E10 · E11. 마우스 hover 가 유일한 경로인 정보가 많다.

**구현.**

1. `aria-label` — 아이콘 버튼 전부(`StudioLayout:119` · `PlaceForm:7` · `Npcs:136-138` · `FallbackForm:109-111` · `BucketGrid:79-81`). 스텝 select 에 `aria-label="스텝 N 행동"`. `TraitBars` range 에 라벨. 토스트 `role=status`(H09).
2. **색 + 글자** — 버킷 격자 셀에 글자(핀 `P` · 생성 `G` · 반려 `R`) 또는 무늬, 핀/생성 색 대비. `.chip.off` 를 `opacity` 대신 테두리 점선 + 취소선.
3. **hover 전용 툴팁 대체** — 지도 점 · 타임라인 칸 · 격자 셀을 `<button>`/`tabindex=0` 으로, 포커스 시 같은 설명을 `aria-describedby` 로. `FieldInfo` 는 터치에서 탭으로 열고 다시 탭으로 닫힘.
4. **모달** — 포커스 트랩 · `Escape` · 열릴 때 첫 버튼에 포커스 (H02 의 `ConfirmDialog` 와 `ArchetypeForm` 저장 모달 · 도움말 서랍).
5. **미리보기** — `Space` 재생/멈춤 · `←→` 1시간 · `Shift+←→` 세그먼트. 스크러버 `pointerdown/up` 으로 `view.dragging` (지금 `behavior-preview.js:38` 은 세우는 곳이 없다) · `input` 이벤트로 seek. `prefers-reduced-motion` 이면 재생 대신 스텝 이동. rAF 루프는 뷰가 0개면 멈춘다. `invokeMethodAsync(...).catch(()=>{})`.
6. `NpcFigure` `transform` 을 JS 만 만지게 — 서버 값은 첫 렌더에만 (세그먼트 경계 점프 제거).
7. 10~11px 글자를 12px 이상으로.

**테스트.** 없음.

**완료 판정.** 마우스 없이 X1~X9 를 밟을 수 있다.

---

### H17 ✅ · 개별 설정 저장의 서식 · 세력 · 대화 프로필

**왜.** C13 · E9. P2 — 데이터는 안전하지만 diff 가 크고 후보가 하드코딩이다.

**구현.** `SaveNpcOverride` 를 `JsonSurgeon` 항목 치환으로(무변경 바이트 동일 테스트 추가). 세력 select 에 `factions.json` 의 `desc`. 대화 프로필 후보를 **기존 오버라이드에 쓰인 값** + `dialogue_lines.json` 의 프로필 키(있으면)에서 — `Npcs.razor:172-176` 하드코딩 삭제.

**테스트.** `SaveNpcOverride_WithoutChanges_IsByteIdentical`.

---

### H27 ✅ · 매뉴얼 · README · CODEMAP · ROADMAP 동기화

**왜.** F1~F4. 사람이 문서를 믿고 "지도를 클릭" 한다.

**구현.**

1. `docs/npc_studio_manual.html` — §1.2 (a) 17건 · (b) 12건 반영. **"10. 문제 해결" 에 10항목**: 막힌 상태(사유별) · 생성기 실패(출력 읽는 법 · dotnet 없음) · planstore 없음(탭이 숨는다) · "이 플랜을 컴파일하지 못했다" · 서버 미연결(관찰 시작 · 401) · 연습장 적용 실패(원본이 바뀜 · 거리표) · 백업에서 복원 실패 · "하루를 그릴 수 없다 (V7)" · 재배분 전부 ✗ · 읽기 전용. §8.3 "되돌리기" → "백업에서 복원". 하루 일과 편집기 절 신설. NPC 화면의 "POI ID 직접 입력" 을 되살린다(17:57 항목에서 만들고 T19 에서 사라졌다).
2. `README.md` Studio 절 — 인자 표에 `--planstore` · `--server` · `--token` · `--backup-root` · `--bind`.
3. `CODEMAP.md` — Studio 줄을 `Pages/`·`Shared/`·`Layout/`·`Services/` 지도로. `Narrative` 신설 6파일과 "NPC 가 어떻게 움직일지 예측한다" 작업 줄. CLI `forecast` · `lint`. "V1~V13" 을 실제로.
4. `PRODUCTION_ROADMAP.md` F-02 — `[x]` 로 닫고 "남은 것" 을 "LLM 제안 리뷰(F-07) · git 커밋(계획 밖)" 으로. `:298` 의존에서 `Sim` 삭제. `:162` bUnit 언급 삭제. `:250` "5·7·9장 완주" 는 9장이 읽기 전용이므로 "5·7장 완주 + 9장 읽기" 로 —
   > **결정: 완료 조건을 "5·7장 완주 + 9장 읽기" 로 고쳤다.**
   > 액션을 더하면 프롬프트 프리픽스가 바뀌어 미리 구운 계획이 전량 무효가 된다 —
   > 그 파급은 폼으로 가릴 것이 아니라 사람이 보고 결정할 것이다. 1차 계획 §1.2 의 "액션 편집은 밖" 을
   > ROADMAP 이 따라왔다.
5. `docs/index.html` · `CLAUDE.md` 문서 지도에 `docs/npc_studio_manual.html` 줄. 1차 계획 §1.3 표의 ✅ 옆 주석(H20 5).

**테스트.** 매뉴얼의 `<span class="ui">` 라벨이 화면 문자열에 있는지 보는 드리프트 테스트 `Manual_UiLabelsExistInRazor` — 한 번 써 두면 다음 재작성에서 F1 이 재발하지 않는다.

**완료 판정.** 매뉴얼의 모든 절차를 초보자가 그대로 따라 끝낼 수 있다 (S1~S6 · X1~X9 를 매뉴얼만 보고).

---

### H28 ✅ · 테스트 정비

**왜.** F5~F9. 못 잡는 테스트는 이자이고, 잡아야 할 것은 빠져 있다.

**구현.**

1. **지운다** — `Forecast_DurationsMatchSimWithoutJitter` (동어반복). 커밋 메시지: "소요 시간 드리프트는 `ActionDuration` 한 벌이 구조로 막는다 — 테스트는 자기 자신을 비교하고 있었다". 1차 문서 T20 목록에서도 뺀다.
2. **회귀 고정** — `LoadNpcOverview_WorksWithoutPatrolRoute` (`1fd9812`) · `Issues_FirstRender_HandlesDefaultLintArray` 는 화면이라 대신 `StudioWorkspace.Lint*` 반환이 `default` 가 아님을 보장 · `IssueLocator_AppendsFieldWithAmpersand` (H10).
3. **새 불변식** — H04 · H11 · H12 · H13 · H14 · H15 · H16 의 테스트 (각 태스크 절에). 읽기 전용이 **모든 쓰기 경로**(`SaveFile` · `AppendPoi` · `Restore` · `CreateArchetype` · `ApplyToOrigin` · `RunAsync`) 를 거절 — `[Theory]` 하나로.
4. **ⓘ 전수** — `*Form_EveryFieldHasFieldGuide` 를 `FallbackForm` · `PlaceForm` · 오버라이드 · 마법사로. 각 폼이 `JsonKeys` 같은 표를 노출한다. 실제 빠진 칸(장소 Z · 닫는 시간 · 스텝 인자 · 마법사 이름 · 직군)에 ⓘ.
5. **`FieldGuide` 빈틈** — `items.json#/recipes/outputs/item`·`/count` · `world_flags.json#/reserved_bits`·`/flags`·`/exclusive_groups/id` · `fallback_plans.json#/steps/args/<13 인자>` 항목 추가. 드리프트 테스트가 `$ref` 를 따라가고 최상위 배열을 건너뛰지 않게 (`FieldGuideTests.cs:119-150`). **편집 폼에 실리는 필드는 `Caution` 필수** — `FieldGuide_EditableFieldsHaveCaution`.
6. `Files.razor:70-75` `EditableFiles` 삭제 → `StudioWorkspace.EditableFiles` 하나.
7. `StudioViewTests` 의 `#2326` 고정은 그대로 — 골든 성격이다.

**완료 판정.** `dotnet test "--filter Category!=Golden&Category!=Gate&Category!=Load"` 초록. 지운 테스트 1 · 더한 테스트 ~20.

---

## 5. 구현 순서와 커밋 단위

| 묶음 | 태스크 | 왜 이 순서인가 |
|---|---|---|
| 1 탈출구 | H10 → H07 → H08 | **버그 하나(링크)와 막다른 길 둘** 을 먼저. 나머지 작업 중 화면이 죽으면 H08 이 알려 준다 |
| 2 실수 방지 | H01 → H02 → H03 → H04 → H15 | 이탈 가드와 확인 모달은 이후 모든 폼의 전제. H03·H04 는 H02 의 첫 사용자 |
| 3 조용한 파손 | H11 → H12 → H13 → H14 | 서비스 층. 화면 없이 테스트로 닫는다 |
| 4 그 자리에서 | H09 → H05 → H06 | 저장 결과 표시가 있어야 마법사 5단계 · 4단계가 완성된다 |
| 5 견고 · 약속 | H16 → H18 → H22 → H23 → H21 → H19 → H20 | P1. 각각 독립 — 병렬 가능 |
| 6 체감 | H25 → H26 → H24 → H17 | 성능은 늦게 — 앞 태스크가 getter 를 옮기고 나면 남는 것이 적다 |
| 7 문서 · 테스트 | H27 → H28 → X1~X9 판정 | 회귀 테스트는 각 태스크 커밋에 이미 들어 있다. H28 은 남은 정비 |

각 커밋 전: `dotnet build -c Release` 경고 0 · `dotnet test "--filter Category!=Golden&Category!=Gate&Category!=Load"` · `dotnet format --verify-no-changes` · Studio 를 띄워 바뀐 화면을 브라우저에서 직접 눌러 본다 (포트 25056).
**`Narrative` · `MasterData` 를 건드린 커밋은 골든(`Card_*` · `PlanExplain_*` · `forecast_blacksmith.md`) 이 초록인지 반드시.** H11 2 · H12 1 은 검증기 변경이라 `docs/reference_masterdata.html` 을 같은 커밋에서.

---

## 6. 밖에 두는 것

| 무엇 | 왜 밖인가 |
|---|---|
| `archetypes.json` 에 `group` 필드 (직군 저장) | 스키마 변경. `reference_masterdata.html` §06 결정이 먼저다. H20 3 의 `> 확인` 에 결정만 적는다 |
| 기존 장소 · 직업 **삭제** UI | 번호 체계(H12) 와 프리베이크 · 명단 파급이 커서 `npc` CLI 로 파급을 보고 하는 것이 맞다. Studio 는 거절 + 안내 |
| 기존 장소 **편집** 폼 (정원 · 시간 · 허가) | 다음 계획. 지금은 고급 JSON |
| 출하 데이터 진단 19건 (L7 18 · L6 1) | 1차 §7 그대로 — `masterdata/` 값 판단이고 `ContentHash` 를 바꾼다 |
| 연습장 전환의 회로 단위화 | H04 4 의 `> 확인` 에서 "전역 + 알림" 을 권장했다. 회로 단위는 호출부 40곳 개편이라 근거가 더 생기면 |
| 로더 `SkipDistances` 옵션이 `Npc.Host` 기동 경로에 미치는 영향 | H07 4 의 `> 확인`. 기본값이 지금 동작이면 영향 0 이어야 하나, 검토 없이 넣지 않는다 |
| bUnit · 화면 스냅샷 테스트 | 1차 T20 결정 유지. 화면은 X1~X9 를 손으로 |
| LLM 제안 · 대화 문구 · 액션/플래그/버킷 편집 · git 커밋 | 1차 §1.2 그대로 |

---

## 7. 완료 기록 (2026-09-17)

28건 전부 구현하고 X1~X9 를 브라우저로 밟았다. 커밋은 아홉이다 — `masterdata`(로더·검증기·안전장치) ·
`narrative`(사전·용어) · `studio` 서비스 · `studio` 화면 · `tests` ·
`studio`(브라우저 판정 1차) · `studio`(브라우저 판정 2차) · `masterdata`(위반의 파일 이름) · `docs`.

**구현하면서 드러난 것.**

| 무엇 | 어떻게 됐나 |
|---|---|
| `Assert.Equal(ImmutableArray, ImmutableArray)` | xUnit 이 `EqualityComparer<T>.Default` 로 떨어져 **배열 내용이 아니라 참조**를 본다. 같은 목록인데 실패했다 — 문자열로 이어 붙여 견준다 |
| `JsonSurgeon.ItemRange` | 키를 **문자열만** 매칭했다. `npc_overrides.json` 의 `id` 는 숫자라 항목을 못 찾았다 — 원문 그대로 견주도록 넓혔다 |
| `Path.Combine` 으로 만든 로케일 키 | Windows 에서 역슬래시가 되어 미리보기 목록과 실제 쓴 목록이 **글자만** 달랐다. 키는 언제나 슬래시다 |
| 개별 설정 저장 | 통째로 다시 쓰면 `patrol_route` 의 한 줄 배열이 여러 줄이 된다 — 바뀐 필드만 고치도록 바꿔야 "무변경 = 바이트 동일" 이 성립한다 |
| `FieldGuide_EditableFieldsHaveCaution` | 새로 쓴 이 테스트가 **실제로 빠진 칸 10개**를 찾았다 (성향 3 · 전투 가능 · 하루 목표 · 장소 유형 · 좌표 2 · 시간 2) |
| 드리프트 테스트의 구멍 | `$ref` 를 안 따라가 `items.json` 의 `recipes/outputs` 가, 원시값 배열을 벗겨 `reserved_bits` 가 검사에서 빠져 있었다 |

**X1~X9 를 브라우저로 밟으면서 더 나온 것.** 전부 화면에서만 보이는 것들이라 테스트가 못 잡았다.

| 무엇 | 어떻게 됐나 |
|---|---|
| `default` 인 `ImmutableArray` 에 패턴 매칭 | `_drift is { Length: > 0 }` 이 `NullReferenceException` 이다. 새 장소를 저장하면 회로가 죽었다 — `IsDefaultOrEmpty` 로 바꿨다. (거꾸로 H08 의 오류 화면이 제대로 떴다는 증거이기도 하다) |
| 새 세부 유형의 표시 이름 | H18 4 가 요구한 "`poi.<subtype>` 를 같은 트랜잭션에" 가 빠져 있었다. 장소만 생기고 V14 두 건이 남았다 — 폼에서 이름을 받아 로케일 파일에 같이 넣는다 |
| V14 의 수정 힌트 | `FixHints` 의 V14 는 **대사 심볼**(D-02) 문장이라 화면이 "`dialogue_lines.json` 을 고쳐라" 라고 말했다. 고칠 파일은 로케일 파일이다 — Studio 가 제 문장을 쓴다 |
| 파일 감시가 자기 저장을 잡았다 | 저장할 때마다 "밖에서 바뀐 파일" 배너가 떴다. **거짓 경보는 진짜 경보까지 안 읽게 만든다** — 방금 쓴 지문과 같으면 알리지 않는다 |
| 사는 곳 유형 선택이 비어 있었다 | 아키타입은 POI **type**(`"home"`)을 쓰는데 후보를 **subtype**(`"house"`)에서 만들었다. 마스터데이터가 사실의 출처이므로 `FieldGuide` 문장을 고쳤다 |
| 시간 의존 테스트의 빨간불 | `Handoff_SurvivesConcurrentWorkersAndTicks` · `Worker_T2RunsEightConcurrently` 가 전체 실행에서만 30초 예산을 넘겼다. 원인은 앞 회차가 남긴 `dotnet` 테스트 호스트 19개였다 — 정리하니 전부 통과. **코드 결함이 아니다** |

**X1~X9 를 끝까지 밟으면서 또 나온 것 넷.** 앞의 여섯과 마찬가지로 테스트가 못 잡는 자리였다.

| 무엇 | 어떻게 됐나 |
|---|---|
| 새로고침이 편집 폼을 다시 읽지 않았다 (X7) | H13 3 의 세대 가드는 맞았는데 **아무도 깨우지 않았다** — 라우팅된 페이지는 매개변수가 그대로면 다시 그리지 않아 `OnParametersSet` 이 안 돈다. 폼과 페이지가 `Session.Changed` 를 직접 듣는다 |
| "버리고 다시 읽기" 가 초안을 안 버렸다 (X7) | `Reload()` 가 `Session.Drafts` 를 안 비워, 폼이 디스크 대신 보관본을 되살렸다. **확인 모달이 한 말이 거짓이었다** |
| 다른 탭 배너가 뜬 적이 없다 (X2) | `OnDirectoryChanged` 가 문구를 세운 **뒤에** `Reload()` 를 불렀고, `Reload()` 가 그 문구를 비웠다. 순서를 뒤집었다 |
| 모든 검증 위반이 "파일 미상" (X8) | 검증기 41곳이 전부 `File` 을 비워 둬 화면의 "원문으로" 바로가기가 죽어 있었다. 메시지가 이미 `"pois.json: …"` 로 시작하므로 레코드가 앞머리에서 읽는다 (E-04 의 원래 의도) |

**X9 의 마지막 문장은 다르게 끝났다.** "만들기 실패를 일부러 내면(정원 부족) 3단계로 가는 링크" 를
확인하려 했으나, **3단계가 저장 전에 V10 을 잡고 "다음" 을 막는다** — 그 실패에 도달할 수 없다.
실패 카드와 `StepOf` 링크는 코드에 있고 다른 코드(V5·V8·V2·V3)로는 열린다. H05 의 "5단계 끝에서
알면 늦다" 가 한 단계 더 앞당겨진 것이라 고치지 않았다.

**밖으로 남긴 것** — §6 그대로다. 여기에 하나 더: `NpcFigure` 의 `transform` 을 JS 만 만지게 하는 것은
돌발 반응 이동(H20)에서만 했다. 세그먼트 경계의 미세한 점프는 남아 있고, 그것은 키프레임 보간의 성질이라
고치려면 예측 쪽을 건드려야 한다.

---

## 부록 A · 확인 모달 문장 규격 (`ConfirmDialog`)

세 줄이다. **① 무엇이 ② 어떻게 되고 ③ 되돌릴 수 있는가.** 숫자는 코드가 센다. "정말?" 은 쓰지 않는다.

| 행동 | ① | ② | ③ | 버튼 |
|---|---|---|---|---|
| 연습장 버리기 | 연습장 `0917` · 원본과 다른 파일 {N}개: {목록} | 그 변경이 사라진다 | `lab/.trash/` 에 30일 남는다 | **버리기**(빨강) · 취소 |
| 원본에 적용 | {N}개 파일: {목록} | 원본을 덮어쓴다. 원본에서 다시 검증한다 | 원본은 백업에서 복원할 수 있다 | **적용** · 취소 |
| 원본에 적용 (원본이 바뀐 경우) | + "연습장을 만든 뒤 원본이 바뀐 파일: {목록}" | 그 변경이 덮인다 | — | **그래도 적용**(빨강) · 취소 |
| 개별 설정 삭제 | #2326 의 순찰로 3곳 · 세력 · 오프셋 | 직업 기본값으로 돌아간다 | 백업에서 복원할 수 있다 | **삭제**(빨강) · 취소 |
| 백업에서 복원 | `archetypes.json` 을 09-17 00:12 상태로 · 지금과 다른 줄 {N}개 | 그 뒤의 저장 {M}건이 되돌아간다 | 복원도 백업을 남긴다 | **복원** · 취소 |
| 다시 만들기 | `dotnet run tools/gen_npcs.cs` · 약 30초(첫 실행은 빌드로 더) | 5,000명 명단이 다시 만들어진다. 개별 설정의 번호가 가리키는 NPC 가 바뀔 수 있다 | 이전 명단은 백업에 남는다 | **다시 만들기** · 취소 |
| JSON 원문 저장 (번호 파일) | `world_flags.json` · 바뀐 줄 {N}개 | 번호 체계 파일이다 — 추가는 뒤에만 | 백업 | **검증하고 저장** · 취소 |
| 초안 버리고 이동 | {화면} 에 저장하지 않은 변경 | 사라진다 | — | **버리고 이동**(빨강) · 머무르기 |
| 마법사 취소 | 5단계 입력 | 사라진다 | — | **취소하기**(빨강) · 계속 |

## 부록 B · UI 용어 통일표 (`Lexicon.Ui`)

| 화면 낱말 | 쓰지 않는 말 | 코드 이름 (부제 · ⓘ · 고급에서만) |
|---|---|---|
| 직업 | 아키타입 | `archetype` |
| NPC | 인스턴스 · 개체(단독) | `npc_instances` |
| 개별 설정 | 오버라이드 | `npc_overrides` |
| 장소 | POI | `pois` |
| 지역 | 존 | `zones` |
| 하루 일과 | 폴백 · 폴백 플랜 | `fallback_plans` |
| 돌발 반응 | 인터럽트 | `interrupts` |
| 미리 구운 계획 | 프리베이크 · planstore | `planstore` |
| 상황(버킷) | 버킷(단독) | `BucketKey` |
| 연습장 | 샌드박스 · lab | `lab/` |
| 초안 취소 | 되돌리기 | — |
| 백업에서 복원 | 되돌리기 · 언두 | `backup/` |
| 검증하고 저장 | 저장하기 · 적용 | `ValidateAndWrite` |
| 다시 만들기 | 재생성 · regen | `gen_*.cs` |

## 부록 C · 오류 메시지 한글화 대응표 (`StudioErrors.Humanize`)

| 예외 | 지금 화면 | 바꾼 문장 |
|---|---|---|
| `JsonException` | `'}' is invalid after a single JSON value. … LineNumber: 12 \| BytePositionInLine: 3` | JSON 문법 오류 — 13행 4열 근처. 괄호 · 쉼표를 본다 |
| `IOException` (공유 위반) | `The process cannot access the file 'C:\…\archetypes.json' because it is being used by another process.` | `archetypes.json` 을 쓸 수 없다 — 다른 프로그램(편집기 · 백신)이 열고 있다. 닫고 다시 저장한다 |
| `UnauthorizedAccessException` | `Access to the path '…' is denied.` | `…` 에 쓸 권한이 없다 |
| `DirectoryNotFoundException` | `Could not find a part of the path '…'.` | 폴더가 없다: `…` |
| `Win32Exception` (dotnet) | `An error occurred trying to start process 'dotnet' …` | `dotnet` 을 찾지 못했다 — PATH 를 확인한다. 명령을 터미널에서 직접 돌릴 수 있다: `…` |
| `ArgumentException` (우리 것) | `아키타입 'x' 이 이미 있다. (Parameter 'draft')` | 직업 `x` 가 이미 있다 — `InvalidDataException` 으로 바꿔 접미를 없앤다 |
| 검증기 모양 예외 (`InvalidOperationException` 등) | `The requested operation requires an element of type 'Array', but the target element has type 'Object'.` | `archetypes.json` 의 모양이 스키마와 다르다 — `archetypes` 는 배열이어야 한다 (V0) |
| `HttpRequestException` / `TaskCanceledException` (라이브) | `No connection could be made …` | 서버 `http://127.0.0.1:25055` 에 연결할 수 없다 — 떠 있는지 · 포트가 맞는지 본다 |
| 401 / 403 | `서버가 401 로 답했다.` | 서버가 거절했다(401) — `--token` 을 확인한다 |
