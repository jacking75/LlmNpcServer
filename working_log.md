# 작업 로그

## 2026-09-11 10:25 KST · F-08 에디터 지원

E-02 스키마를 만들어 놓고 에디터가 그것을 찾을 길이 없었다.

- `.vscode/settings.json` — 스키마 매핑 10종 · 생성물 검색 제외(3.3MB `npc_instances.json`
  이 검색에 섞이면 진짜 소스를 못 찾는다) · LF 고정.
- `.vscode/tasks.json` — `build.ps1`(기본 빌드 작업) · 검증 · 파생물 검사 · 스키마 재생성 ·
  아키타입 카드 · 변경 파급. **파이프라인과 같은 것을 돌린다** — 갈리면 "내 기계에서는 됐다"
  를 환경 차이로 좁힐 수 없다.
- `.vscode/npc.code-snippets` — 아키타입(일반·근무직) · 액션 · POI · 아이템 · 레시피 ·
  인터럽트 · 폴백 · 플랜 스텝 · 플래그 · 존 10종. **빠뜨리기 쉬운 필드를 자리와 함께 낸다** —
  `fallback_plan`(V7) · `timeout_s`(0 이면 안 된다) · `duty_hours`(V12).
  **번호는 넣지 않는다** — `npc next-code` 가 답한다.
- `gen_npcs.cs` 가 `$schema` 를 쓰게 했다. 생성물이라 사람이 편집할 일은 없지만,
  "모든 마스터데이터 파일이 자기 스키마를 가리킨다" 가 예외 없는 규칙이라야 빠뜨린 파일을
  테스트가 잡는다.
- 테스트 8건 추가 — **매핑이 실제 파일을 가리키는지**까지 본다. 경로가 틀리면 그 파일만
  조용히 자동완성이 죽고 에디터는 오류를 내지 않는다.
- 전체 1,399건 통과 · 경고 0.

## 2026-09-11 09:40 KST · E-02 JSON Schema 발행

LLM 이 마스터데이터를 만들 때 필드 이름·타입·허용 값을 **추측**했다. 사람도 에디터
자동완성이 없었다. 스키마를 손으로 쓰면 로더와 어긋나므로 **로더의 DTO 에서 뽑는다**.

- **신규** `src/Npc.MasterData/Schema/SchemaCatalog.cs` — `JsonSchemaExporter` 로 구조를 뽑고
  **허용 값(enum)은 지금 마스터데이터에서** 채운다. 파일 10종 × (동적·정적) = **20개**.
  `npc schema` · `Npc.Host schema` 둘 다 같은 것을 낸다.
- **`required` 를 싣지 않는다.** DTO 생성자 인자가 전부 required 로 나오는데 실제 파일은
  선택 필드를 생략한다(`duty_hours`) — 스키마가 필수 판정을 흉내 내면 멀쩡한 파일에 빨간
  줄이 그어지고 사람이 스키마를 꺼 버린다. 필수는 로더와 V1~V13 이 안다.
- **허용 값은 (파일, 필드) 쌍으로 정한다.** 같은 이름이 파일마다 다른 뜻이다 —
  `pois.type` 은 POI 종류이고 `actions.params.*.type` 은 파라미터 타입이다.
  이름만 보고 붙였다가 액션 33종이 전부 떨어졌다.
- 로더가 무시하는 필드(`version`·`_comment`·`$schema`·`exclusive_groups`·`key_format` …)를
  스키마가 허용한다. 거절하면 파일에 `$schema` 를 못 붙이고 에디터가 스키마를 못 찾는다.
- **드리프트 방지 둘.** ① `SchemaCatalogTests` — 커밋된 마스터데이터가 스키마를 통과하고,
  고의 오류(타입 불일치·없는 액션)는 걸리며, **교차 제약(V5)은 못 잡는다는 것까지** 단언한다.
  ② `SchemaDtoTests` — `world_flags`·`fallback_plans` 는 로더 DTO 가 없어 스키마 전용 DTO 를
  뒀는데, 실제 파일을 훑어 **DTO 가 모르는 필드가 있으면 깨진다**.
- 마스터데이터 10종에 `"$schema"` 를 붙였다. content hash 가 바뀌어 파생물 2건이 낡았고,
  **F-04 의 신선도 검사가 그것을 그대로 잡았다** — 재생성했다.
- `JsonSchemaExporter` 는 원시 타입까지 메타데이터를 요구해 소스 생성 컨텍스트로는 안 된다.
  경고를 억제하지 않고 `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` 로 **표시**했다 —
  실수로 서버 기동 경로에 들어오면 빌드가 알려 준다.
- 테스트 38건 추가. 전체 1,391건 통과 · 경고 0.

## 2026-09-10 20:31 KST · README 기능 목록·도구·LLM 지시 절 · 검증 JSON 을 snake_case 로

README 에 "이 서버가 무엇을 할 수 있는가" 와 "LLM 에게 어떻게 시키는가" 가 없었다.
도입을 검토하는 사람도, 붙이려는 에이전트도 코드를 읽어야 알 수 있었다.

- **기능 목록** — 런타임·플랜·연동·운영·콘텐츠 5개 표. **동작하고 테스트가 있는 것만** 적었다.
- **도구** — `npc` 12개 명령을 `--json` 지원 여부까지. 그 밖의 도구와 라이브러리 3종.
  `Npc.Cli` 에 `PackAsTool` 을 붙여 `dotnet tool install` 이 실제로 되게 했고 pack 을 확인했다.
- **MMO 개발에 LLM 을 붙일 때** — 런타임 LLM(①)과 개발 에이전트(②)를 먼저 가른다.
  ①은 배선이 끝나 지시할 것이 없다. ②에 줄 **시스템 프롬프트 전문**, 자주 시키는 일 6종,
  기계 판독 출력 예시, 게임서버 팀에게 줄 것, **아직 없는 것**(E-01·E-02·E-03·F-02·D-01) —
  없는 도구를 전제로 지시하면 에이전트가 그것을 부르다 헤맨다.

**문서를 쓰다 코드의 결함 셋을 찾았다.**

- 검증 JSON 이 `camelCase`(`fixHint`)인데 로드맵과 모든 문서는 `fix_hint` 였다.
  **마스터데이터 JSON 이 전부 snake_case** 이므로 코드를 고쳤다 —
  검증 출력만 다르면 LLM 이 두 표기를 오간다.
- `FixHints` 의 `related` 가 `docs/llm/RECIPES/*.md` 를 가리켰다. **그 파일들은 없다**(E-01).
  404 로 가는 링크는 없는 것보다 나쁘다 — 빼고, 있는 문서만 남겼다.
- `Npc.Contracts` 를 "316줄" 이라 적어 둔 곳이 셋(README·CODEMAP). **실제 5파일 472줄**이다.
  `Npc.Gateway` "링크 구현 6종" 도 실제는 5종이었다.
- `HintsCommand.Render` 를 `Npc.MasterData/Validation/FixHintDocument.cs` 로 옮기고
  `npc hints --out` 을 추가했다. 껍질이 둘인데 각자 markdown 을 만들면 같은 사전에서
  다른 문서가 나온다.

전체 1,353건 통과 · 경고 0 · `dotnet format` 통과.

## 2026-09-10 19:12 KST · CI 워크플로 제거 · 이번 세션 결과물을 문서에 반영

**CI 워크플로 파일을 만들지 않기로 했다.** 이 저장소는 CI 제공자를 고르지 않았고, 고르지 않은
채 `.github/workflows/*.yml` 을 두면 **"CI 가 있다" 는 거짓 신호**가 된다 — 아무도 돌리지 않는
파이프라인이 녹색으로 보이는 것이 없는 것보다 나쁘다.

- `.github/` 삭제. 커밋된 적이 없어 이력에는 남지 않는다.
- **`build.ps1` 이 파이프라인 그 자체가 됐다.** 빌드·스타일·테스트에 더해
  `npc validate`(V1~V13 + 로더 + 파생물)와 `npc regen --check`(낡으면 비0)를 돌린다.
  `-SkipData` 로 코드만 고쳤을 때는 건너뛴다. 사내 CI 든 Actions 든 이 한 줄을 부르면 된다.
- `DeployArtifactTests` 를 뒤집었다 — 워크플로 파일의 **존재**를 요구하던 것이
  이제 `.github/` 가 **없음**을 요구한다. 결정이 테스트로 지켜진다.
- 로드맵 A-09 의 CI 항목에 취소선과 결정 근거를 달고, G-03 을 "성능 회귀 **판정**" 으로
  고쳐 A-09 의존을 끊었다 — 워크플로가 아니라 판정 명령으로 만든다.

**이번 세션 결과물(E-04·F-05·F-03·F-04·F-01)을 문서 전반에 반영했다.**

- `CLAUDE.md` — §3 의존 그래프에 `Npc.Narrative` 와 **도구 잎 4종**, §1 에 `npc` 명령,
  §2.4 에 파생물 잠금, §7 실수 표에 4줄(V12 가 막는다 · `next-code` · `regen --check` · `JsonSurgeon`).
- `README.md` — 배포 표에서 워크플로 3줄 삭제 + 파이프라인 명령 3줄, 구조 트리에 `Npc.Cli`.
- `CODEMAP.md` — 작업별 지도에 CLI 줄, 도구 표 신설, 컨테이너 줄에서 CI 제거.
- `docs/book/ch02` — V12·V13 행, `FixHints` 절, **`derived.lock.json` 절 신설**,
  무효화 표에 `ImpactAnalyzer`·`npc diff` 카드.
- `docs/book/ch04` — `CompiledStep` **14 B → 16 B**, `NpcRef` `byte` → `ushort`.
  64종이 조용한 상한이었다는 사실을 캡션에 남겼다.
- `docs/book/ch11`·`ch13` — `npc` 명령, 파일 지도에 `Authoring/`·`Narrative/` 11줄, 용어 3개.
- `docs/startup_flow` — `validate` 출력 갱신(구조 해시·`WARN 파생물`), 로더 단계에 신선도.
- `docs/tutorial/appendix` — 도구 표에 `npc`, V12·V13 행, `hints`·`healthcheck` 서브커맨드.
- `docs/index`·`LLM_NPC_Server_Plan.md` — 문서 지도에 `VALIDATION.md`·로드맵,
  검증 목록에 V12·V13, **"아키타입 수는 코드가 모른다"** 절 신설.
- `V1~V11` → `V1~V13` 을 저장소 전역 36곳에서 고쳤다. **`working_log.md` 는 되돌렸다** —
  과거 기록은 그때의 사실이고, 고치면 V13 이 그때 있었다고 주장하게 된다.

빌드 경고 0 · 테스트 1,353건 통과 · `dotnet format` 통과.

## 2026-09-10 16:14 KST · F-01 `npc` CLI (`tools/Npc.Cli`)

서브커맨드가 `validate` 하나였다. F-03·F-04 의 라이브러리를 사람과 LLM 이 터미널에서 쓸
껍질이 없었다.

- **신규** `tools/Npc.Cli` (`AssemblyName` = `npc`). `Npc.Narrative` · `Npc.MasterData.Authoring` ·
  검증 4단(`Npc.Core`·`Npc.Sim`)을 부른다. **CLI 에 로직을 두지 않았다** — MCP(E-03)와
  Studio(F-02)가 같은 함수를 부르므로 여기만의 규칙을 만들면 세 껍질이 다르게 답한다.
- `validate`(+`--json`) · `explain archetype|action|poi|item|flag|interrupt` ·
  `card archetype|npc|roster` · `timeline archetype` · `hints` ·
  `next-code` · `scaffold archetype` · `diff` · `regen`(+`--check`) ·
  `plan validate|explain|narrate` · `buckets` · `pin`.
- **F-04 완료 조건을 여기서 확인했다.** `npc scaffold archetype beekeeper --from shepherd
  --weight 0.004` 가 재배분 3안·파급표·남은 일을 내고, `--apply` 가 서식을 보존해 고친다 —
  diff 가 2줄 + 새 블록이고 가중치 합 1.0, 아키타입 41, 한글 escape 없음.
- **버그 둘을 실측으로 잡았다.** ① 베낀 조각의 들여쓰기가 두 겹이 됐다(원문 들여쓰기를
  먼저 벗겨야 했다). ② `desc` 가 `\uXXXX` 로 escape 됐다 — 파일의 다른 줄은 다 한글이라
  도구가 넣은 줄만 표기가 달랐다.
- `plan validate` 는 **플랜 스토어 봉투와 문서 둘 다** 받는다. 봉투의 `bucket` 을 읽으므로
  `--bucket` 없이 `planstore/plans/*.json` 을 바로 줄 수 있다.
- 아직 없는 명령(`repair`·`review`·`serve`)은 "모르는 명령" 이 아니라 **"아직 없다 — 어느
  태스크를 기다린다"** 로 답한다. 오타와 미구현을 구별하지 못하면 사람이 헤맨다.
- `ValidationJson` 을 `Npc.Host/Commands` → `Npc.MasterData/Validation` 으로 옮겼다.
  두 껍질이 같은 JSON 을 내야 LLM 이 같은 것을 읽는다.
- **인자 파서는 손으로 썼다.** 로드맵은 `System.CommandLine` 을 적었지만 2.0 이 아직
  프리릴리스이고, 이 저장소의 다른 파서(`HostOptions`·`PrebakeOptions`)가 모두 수제다 —
  도구 하나 때문에 프리릴리스를 중앙 패키지 목록에 넣지 않았다.
- 테스트 25건 추가. 전체 1,356건 통과 · 경고 0.
- **미달**: 튜토리얼 5·7·9장의 패치 스크립트를 `npc` 명령으로 전면 대체하는 개정은 하지
  않았다. 7장 스크립트에 대체 명령을 주석으로 달아 두었다.

## 2026-09-10 15:22 KST · F-04 편집 안전장치 · 검증 V12/V13

`code`/`bit` 를 손으로 정하고, 가중치 합을 손으로 맞추고, 거리표·인스턴스 재생성을 기억해야
했다. 편집 스크립트는 서식 보존을 위해 문자열 치환을 썼다.

- **신규** `src/Npc.MasterData/Authoring/` 5종.
  `CodeAllocator` — **예약 구간을 먼저 채운다.** "최대 + 1" 로 두면 `world_flags` 의 22~23 처럼
  비워 둔 구간을 영영 못 쓴다. **재배치 API 를 두지 않았다.**
  `WeightRebalancer` — 3안 + 결과 인구표. 반올림 잔차를 가장 많이 떼는 줄이 흡수해 V5 를 지킨다.
  `JsonSurgeon` — `Utf8JsonReader` 토큰 오프셋으로 최소 범위만 치환. **무변경 편집은 바이트 동일.**
  UTF-8 바이트와 char 오프셋을 구분한다 — 한글 설명이 든 파일에서 밀리면 파일이 깨진다.
  `DerivedArtifacts` — `masterdata/derived.lock.json`. **기록이 없으면 낡은 것으로 본다.**
  `ImpactAnalyzer` — 무효화 범위·프리픽스·구조 해시·재생성 목록·재배포 여부.
- `InvalidationScope` 를 `Npc.Planning` → `Npc.MasterData.Authoring` 으로 옮겼다.
  `ImpactAnalyzer` 가 이것을 돌려주는데 `MasterData → Planning` 참조는 §3 이 금지한다 —
  그리고 "어느 파일이 바뀌면 무엇이 무효인가" 는 애초에 마스터데이터의 성질이다.
  `PlanStoreValidator.ScopeOf` 는 표로 넘긴다. **테스트가 두 판정의 일치를 강제한다.**
- **V12** — `duty_hours` 없이 `OnDuty` 요구 액션 허용 금지. CLAUDE.md §7 의 함정을 규칙으로
  옮겼다. 어느 액션이 `OnDuty` 를 요구하는지는 `actions.json` 이 정한다 — 이름을 박지 않았다.
- **V13** — `npc_instances.json` 의 참조·id 중복·개체 단위 정원. 파일이 없으면 건너뛴다.
  위반은 첫 5건만 낸다 — 5,000줄이 전부 깨지면 목록이 아니라 소음이다.
- 생성기 둘이 `#:project` 로 `Npc.MasterData` 를 참조해 `DerivedArtifacts.Record` 를 부른다.
  형식을 두 벌 관리하지 않는다. 재생성 결과는 바이트 동일이었다(결정론 확인).
- 로더가 `StaleArtifacts` 를 실어 주고 호스트 기동 로그·`validate` 가 `WARN` 으로 낸다.
  **기동을 막지 않는다** — 낡은 파생물로도 개발 중에는 돌려 봐야 하고 판정은 사람이 한다.
- 테스트 34건 추가. 전체 1,331건 통과 · 경고 0.
- **완료 조건 미달**: `npc scaffold archetype …` 한 줄로 7장 실습을 대체하는 것은 F-01 의
  `npc` CLI 가 올 자리다. 라이브러리는 다 있고 껍질만 없다.

## 2026-09-10 14:35 KST · F-03 설명 생성기 `src/Npc.Narrative`

`Npc.Narrate` 는 **명령 로그**를 일지로 바꿨다. 정의(아키타입·플랜·인터럽트·인스턴스) 자체를
설명하는 것은 없었다 — 검수자가 `review.ps1` 에서 보는 것은 "허용 22종" 이라는 **개수**였고,
어떤 22종인지·무엇이 빠졌는지·그 성향이면 어느 인터럽트에 걸리는지는 파일 여섯 개를 대조해야 했다.

- **신규 프로젝트** `src/Npc.Narrative` — `Core`·`MasterData` 만 참조. LLM·시각·난수 없음.
  `ArchetypeCard`·`PlanExplain`·`InterruptExplain`·`InstanceCard`·`Md`.
  `Lexicon` 은 `tools/Npc.Narrate` 에서 **이동**했다.
- 아키타입 카드: 인구/정원(V10 계산) · 근무 시간과 `OnDuty` 함의 · 성향 · 레시피 재료와 소요 ·
  초기 소지품이 세우는 시작 플래그 · 카테고리별 허용/미허용 액션 · **걸릴 수 있는 인터럽트** ·
  폴백 하루 트레이스 · 버킷 첨자 구간.
- **플랜 트레이스가 3단 검증기와 같은 판정을 낸다.** 액션 `grants` + 도착 장소 플래그 +
  수령 아이템 `grants` 세 가지를 다 더해야 일치한다 — 처음에 도착 grants 를 빼먹어서 멀쩡한
  폴백이 전부 반려로 그려졌다. `PlanExplain_AgreesWithCoherenceValidator` 가 폴백 40 ×
  버킷 72 = **2,880건**에서 같은 스텝·같은 코드를 요구한다.
  자원 수지만 스텝 표에 그리지 않고 별도 표로 내며, 그 예외를 테스트가 고정한다.
- `Npc.Narrate card archetype <id>` · `card npc <n>` · `card roster <id>` ·
  `explain interrupts` · `explain fallback <id>`. **F-01 의 `npc` CLI 가 올 자리**이고
  같은 함수를 부르므로 CLI 가 와도 출력이 바뀌지 않는다.
- 아키텍처 테스트 허용 그래프에 `Npc.Narrative → Core, MasterData` 를 넣었다.
- 테스트 13건 추가. 전체 1,297건 통과 · 경고 0.

## 2026-09-10 13:41 KST · F-05 `ArchetypeCount` 컴파일 상수 제거

아키타입 하나 추가에 C# 상수 수정과 재빌드가 따라왔다. **파일이 41 인데 바이너리가 40 이면
41번째의 버킷 72칸이 조용히 사라진다** — 런타임에 티가 나지 않는 종류의 오류다.

- `BucketKey.ArchetypeCount`·`TotalKeys` 를 없앴다. 남은 것은 열거형이 정하는
  `PerArchetype`(72) 뿐이다. **`ToIndex()` 식에는 원래 아키타입 수가 없었다** —
  그래서 뒤에 추가해도 기존 첨자가 그대로고 프리베이크 플랜이 첨자 때문에 깨지지 않는다.
- 이미 있던 `BucketSpace`(`MasterDataSet.Buckets`)가 `ArchetypeCount`·`TotalKeys`·
  `FromIndex`·`Contains` 를 갖는다. 새 타입을 만들지 않았다.
- 배열을 기동 시 잡는다: `PlanStore`(4종 + 폴백) · `CacheMetrics` · `NpcMeter` 히트맵·
  행 이름 · `BucketReplanSource`. 틱 루프는 첨자로만 읽으므로 조회 비용은 그대로다.
- **`NpcRef` 를 6비트 → 12비트로 넓혔다.** 64종이 조용한 상한이었다 — 65번째 아키타입은
  `nearest:` 에서 code 를 잃고 엉뚱한 NPC 를 가리켰을 것이다. `CompiledStep` 14 → 16B,
  크기를 정확히 16 으로 동결했다. 상한 밖 code 는 자르지 않고 어휘 검증에서 떨어뜨린다.
- 테스트는 개수를 `TestPaths.ArchetypeCount` 로 읽는다. **신규**
  `BucketSpaceGrowthTests` — masterdata 를 복사해 41번째를 넣고 코드를 한 줄도 고치지 않은
  채 로드·검증·플랜 스토어·계측이 따라오는지, 기존 40개의 첨자와 code 가 불변인지 본다.
- 7장 실습에서 코드 수정 단계를 뺐다(⑥ 삭제). 남은 대가는 **프리픽스 해시 변경 →
  플랜 스토어 전량 무효**다 — 그쪽은 F-05 가 없앤 문제가 아니라 그대로 남는다.
- 전체 1,284건 통과 · 경고 0.

## 2026-09-10 13:02 KST · 할당 0 측정의 계층 JIT 잡음 제거

`RingBuffer8_AddDoesNotAllocate` 와 `Lod_UpdateDoesNotAllocate` 가 병렬 회차에서 간헐적으로
4KB 대를 보고했다. 둘 다 이미 워밍업이 있었다 — 원인은 코드가 아니라 **계기**였다.

- 계층 JIT 은 메서드를 나중에 다시 컴파일한다. 그 재컴파일과 PGO 계측이 **측정 창 안에**
  떨어지면 아무것도 할당하지 않는 코드도 수 KB 를 남긴다.
- **신규** `tests/Npc.Tests/AllocationProbe.cs` — `MinimumBytes(action)`. 여러 창을 재고
  **최솟값**을 쓴다. 실제로 할당하는 코드는 모든 창에서 할당하고, JIT 재컴파일은 많아야
  한두 창에만 나타난다. 그 차이가 신호와 잡음을 가른다.
- **창을 늘려 통과시키는 것이 아니다.** 같은 판정을 안정적으로 내게 하는 것이다 — 0 을 한 번
  보면 즉시 끊는다. G-03 의 성능 회귀 CI 는 흔들리는 계기 위에 세울 수 없다.
- 흔들린 2건에만 적용했다. 나머지 측정 지점은 흔들린 적이 없어 그대로 둔다.
- 전체 1,278건 통과 · 경고 0.

## 2026-09-10 12:58 KST · E-04 기계가 읽는 검증 출력 · 수정 힌트 사전

검증이 실패하면 코드만 나왔다. `V5` 를 받은 LLM 은 무엇을 고쳐야 하는지 모른다 —
사람은 소스를 열어 보지만 LLM 은 그럴 수 없고, 그러면 온보딩 자동화가 거기서 멈춘다.

- **신규** `src/Npc.MasterData/Validation/FixHints.cs` — 검증 코드 41건의 **"무엇을 하면
  되는가"** 사전. V0~V15 · 플랜 4단(V1.~V4.) · 핸드셰이크 거절. 관련 파일도 같이 준다.
- **신규** `src/Npc.Host/Commands/ValidationJson.cs` — `validate --format json`.
  `code`·`message`·`file`·`path`·`fix_hint`·`related` + 두 해시. **로더까지 돌린다** —
  규칙만 통과하고 참조가 깨진 상태를 `ok: true` 로 내면 호출부가 그 위에 작업을 쌓는다.
- **신규** `src/Npc.Host/Commands/HintsCommand.cs` — `hints --out docs/llm/VALIDATION.md`.
  **문서는 생성물이다.** 두 벌 관리하면 어긋나고, 어긋난 문서는 없는 것보다 나쁘다.
- `MasterDataViolation` 에 `File`·`Path` 를 더하고 `FixHint`·`Related` 를 사전에서 끌어 쓴다.
  텍스트 출력도 힌트를 한 줄 덧붙인다.
- 테스트 12건 추가 — 소스에서 코드 문자열을 긁어 사전과 대조하므로 **힌트 없는 새 규칙을
  추가하면 깨진다.** 생성 문서 최신 여부와 렌더 결정론도 단언한다. 전체 1,278건 통과 · 경고 0.

## 2026-09-10 12:26 KST · C-07 스필오버 서브 쿼터 · 버킷/개체 예산 분리

개별 재계획이 T1 큐 64 를 넘겨 T2 로 흐르면 하루 예산(약 649건)이 몇 분 만에 소진됐다.
그러면 정작 수천 NPC 가 공유하는 **버킷 미스 보충**이 굶는다.

- **신규** `src/Npc.Core/Planning/ReplanAccount.cs` — `ReplanAccount`(Bucket/Individual) ·
  `SpilloverQuota`. **`Npc.Core` 에 둔다** — `Npc.Llm` 이 읽는데 의존 그래프상
  `Npc.Llm → Npc.Planning` 이 없다(`IReplanBudget` 이 거기 있는 것과 같은 이유).
- 개체가 서브 쿼터(기본 20%)를 넘으면 **거절이 아니라 T1 대기**다. 개별 재계획은 급하지 않고
  그 NPC 는 기존 플랜을 계속 쓰면 된다.
- **버킷은 제한하지 않는다.** 서브 쿼터는 개체에만 건다 — 버킷을 제한하면 서브 쿼터를 둔
  이유가 사라진다.
- `--budget-individual-share`. `/metrics` 비용 패널에 `spilloverDeferred`·
  `individualTokensToday`·`individualTokenCap` 과 C-02 의 `wallClockSpentUsd` 를 실었다.
- 테스트 7건 추가. 전체 1,266건 통과 · 경고 0.

## 2026-09-10 12:17 KST · C-06 문자열 격리 테스트 강제 · `reasoning` 정화 · 모더레이션 훅

플레이어 문자열 금지는 타입 설계로만 지켜졌고 회귀 테스트가 없었다. `PlanRequest` 에
`string` 필드를 하나 넣는 변경을 막는 장치가 없었는데, 그런 변경은 리뷰에서 놓치기 쉽다.

- **신규** `tests/.../PromptIsolationTests.cs` — 서픽스에 실리는 타입 4종에 문자열 필드가
  없음을 리플렉션으로 단언하고, **아키타입 40 × 시간대 6 의 서픽스 전문**에 허용 문자 밖이
  없음을 본다. 한글·이모지·따옴표는 전부 허용 밖이라 플레이어 문자열이 들어오면 걸린다.
- **신규** `src/Npc.Llm/ReasoningSanitizer.cs` (`IContentModerator`) — 제어문자·URL·이메일 제거,
  길이 절단, 금칙어. 걸리면 `reasoning` 을 **통째로** 비운다(한 글자만 가리면 원문을 짐작할 수 있다).
- **플랜 자체는 건드리지 않는다.** 정화가 스텝을 바꾸면 검증을 지난 뒤에 플랜을 고치는 것이고,
  그러면 "검증된 플랜" 이라는 말이 거짓이 된다.
- **신규** `masterdata/prompt/blocklist.txt`(비어 있음). 프리픽스에 실리지 않아 플랜 스토어를
  무효화하지 않는다.
- 테스트 21건 추가. 전체 1,259건 통과 · 경고 0.

## 2026-09-10 12:08 KST · C-01 제공사 페일오버 체인 · C-02 벽시계 청구 캡

있던 페일오버는 티어 간(T2→T1)뿐이었다. T2 제공사 하나가 죽으면 브레이커가 60초 열리고
전부 T1(로컬 GPU, 0.195 req/s)로 몰렸다. GPU 가 없으면 캐시+폴백만 남았다.

- **신규** `src/Npc.Llm/FailoverChatClient.cs` — 체인 순서대로 시도, **엔진마다 자기 브레이커**.
  실패를 둘로 가른다: 전송 실패(429·5xx·타임아웃)는 다음 엔진, 400·스키마 거절은 그대로 던진다.
  다른 제공사에 보내도 같은 프롬프트라 같은 결과가 나오고 비용만 두 배가 된다.
- `RetryPolicy` 를 `tools/Npc.Prebake` 에서 `src/Npc.Llm` 으로 옮겨 공용화했다.
- `appsettings.Llm.json` 에 `chains.t1`·`chains.t2`. 키가 없는 엔진은 체인에서 자동으로 빠진다 —
  시도해 봐야 인증 오류로 실패하고 그 실패가 브레이커를 열어 뒤 엔진까지 늦춘다.
- **신규** `src/Npc.Host/Replan/BillingGuard.cs` (C-02) — `ReplanBudget` 의 틱 기준 하루는
  결정론을 위해 그대로 두고, **벽시계 캡을 하나 더** 둔다. 배속 회차에서 실제 청구일 하루에
  캡이 여러 번 리셋되던 것을 막는다. 넘으면 T2 킬스위치, **해제는 사람이** `/admin/killswitch` 로.
- 테스트 20건 추가. 전체 1,238건 통과 · 경고 0.

## 2026-09-10 11:56 KST · A-11 킬스위치 가역화 · `/admin/*` · 감사 로그

킬스위치를 한 번 켜면 꺼지지 않아 오조작 복구가 재기동뿐이었다. 재기동은 상태 전손이고,
그것이 킬스위치를 누르기 무섭게 만들었다.

- `KillSwitchState.Clear` · `Fired`. 읽는 쪽은 매 호출 `IsDisabled` 를 보므로 해제가 즉시 반영된다.
  **시나리오 파일은 여전히 켜기만 한다** — 대본이 중간에 되돌리면 시나리오 C 가 무엇을 쟀는지
  알 수 없게 된다.
- **신규** `src/Npc.Host/Api/AdminEndpoints.cs`(`/admin/killswitch?target&state&reason` ·
  `/admin/snapshot`) · `Api/AuditLog.cs`.
- 감사는 구조화 로그와 `state/audit.jsonl` 두 곳에 남긴다. **시각은 게임 틱**이다 —
  리플레이·스냅샷과 같은 좌표계여야 맞춰 볼 수 있다. 실패한 호출도 남는다.
- 인증은 앞의 미들웨어(A-06)가 본다. 여기서 다시 보지 않는다 — 두 곳에서 보면 한쪽만
  고쳐지는 날이 온다.
- `--dev-control` 은 남긴다(로드맵은 제거를 적었다). 문서 8곳과 뷰어 버튼이 쓰고 있고,
  데모에 토큰을 요구하면 사람들이 사소한 토큰을 만들어 그대로 스테이징에 들고 간다.
- `/status.killSwitches` 에 지금 끊긴 대상. 테스트 8건 추가, 전체 1,218건 통과.

## 2026-09-10 11:44 KST · A-06 링크 보안 — 상호 인증 · TLS/mTLS · 관리 API 토큰

링크 포트에 붙기만 하면 누구나 NPC 명령 스트림을 관측하고 이벤트를 위조할 수 있었다.
관리 API 에 인증이 없어 `0.0.0.0` 바인드가 구조적으로 불가능했다.

- **신규** `src/Npc.Wire/V2/LinkAuth.cs` — HMAC-SHA256 상호 인증 · nonce 재사용 거절(`NonceCache`).
  태그 재료에 협상 필드·해시 3종·nonce 를 전부 넣는다. 테스트가 필드별로 바꿔치기해 확인한다.
- **신규** `src/Npc.Gateway/TlsStreamFactory.cs` — `--link-tls off|tls|mtls`.
  `TcpGameServerLink` 본문을 건드리지 않고 연결 생성기 이음매에 `SslStream` 을 끼운다.
- **신규** `src/Npc.Host/Api/AdminAuth.cs` — `Authorization: Bearer <NPC_ADMIN_TOKEN>`.
  고정 시간 비교 · 분당 5회 실패 뒤 429. **보호 목록이 아니라 허용 목록의 반대**다 —
  프로브·수집기만 빼고 나머지를 전부 막는다.
- 비밀은 환경변수로만 온다(`NPC_LINK_SECRET`·`NPC_LINK_CERT_PASSWORD`·`NPC_ADMIN_TOKEN`).
  인자는 `ps` 에 보이고 파일은 이미지에 굽힌다.
- 거절에는 인증 태그를 싣지 않는다 — 신원을 모르는 상대에게 서명해 주면 그것이 오라클이다.
- `docs/reference_link.html` §09 에 인증 4단계·암호화 절을 넣고 §13 "구현 필요" 를
  "구현됨(v2)" 로 바꿨다.
- 테스트 43건 추가. 전체 1,210건 통과 · 경고 0.

## 2026-09-10 11:30 KST · A-05 관측성 — OpenTelemetry · Prometheus · 구조화 로깅 · 경보 싱크

계측기 13종이 `Meter` 에 만들어져 있는데 아무도 수집하지 않았다. 로그는 평문 한국어 한 줄이라
수집기가 파싱할 수 없었고, 경보는 대시보드 색깔로만 존재했다.

- **신규** `src/Npc.Host/Observability/` — `Alarms.cs`(싱크·쿨다운·컴포지트·웹훅) ·
  `BudgetAlarmBridge.cs`(80/95/100% 임계) · `Telemetry.cs`(OTel·Prometheus·로그 형식).
- 같은 `(종류, 열쇠)` 는 `--alarm-cooldown-s`(기본 300) 안에 한 번만. 없으면 링크가 흔들릴 때
  초당 수십 건이 나가고 진짜 경보가 묻힌다.
- 웹훅은 큐에 넣고 즉시 돌아온다. 큐가 차면 **오래된 것부터** 버린다 — 최신 경보가 살아남는다.
- `RateLimited` 도 남긴다. 예전에는 로그조차 없어 "왜 처리율이 안 오르나" 를 추적할 수 없었다.
- 틱 히스토그램 경계를 `[0.1, 0.5, 1, 2, 5, 10, 20, 50]`ms 로 잡았다 — 기본 경계는 초 단위라
  0.8ms 짜리 틱이 전부 첫 칸에 몰려 p99 를 볼 수 없다.
- **`src/Npc.Runtime/BannedSymbols.txt`** — `Stopwatch`·LINQ·`ILogger` 확장을 **빌드가 막는다**.
  부하 테스트는 야간에 잡지만 그때는 이미 커밋이 올라간 뒤다.
- 옵션 5개 — `--otlp-endpoint` · `--prometheus` · `--alarm-webhook` · `--alarm-cooldown-s` ·
  `--log-format`. `--profile service` 는 Prometheus 와 JSON 로그를 켠다.
- `docs/reference_metrics.html` §13.5 에 메트릭 이름 사전 21종과 경보 종류 표를 넣었다.
- 테스트 12건 추가. 전체 1,167건 통과 · 경고 0.

## 2026-09-10 11:12 KST · A-10 게임 시각 복원 · `TickSync` 워치독

재기동 시 게임 시각이 새벽 6시로 돌아가 NPC 스케줄 전체가 게임서버와 어긋났다.
게임서버가 `TickSync` 를 멈추면 NPC 서버가 조용히 얼어붙는데 그것을 알리는 장치가 없었다.

- `GameClock.RequestOrigin` / `TryApplyPendingOrigin` — 소켓 스레드가 예약하고 틱 경계에서
  반영한다. 락 없이 `Volatile` 만 쓴다. **게임서버 값이 스냅샷보다 세다.**
- 값은 v2 핸드셰이크의 `StartTick`·`StartGameMinuteOfDay` 다 (B-01). v1 은 `-1` 이고
  없는 것을 있는 척하지 않는다.
- 원점을 `[0, 하루)` 로 정규화한다 — 그러지 않으면 게임 초가 음수가 되어 시각이 뒤집힌다.
- **신규** `src/Npc.Host/TickSyncWatchdog.cs` — 틱이 `--tick-sync-stall-s`(기본 5) 동안
  안 늘면 경보, 다시 돌면 복구 경보. 한 사건에 한 번만 운다.
- 계측기 `npc.clock.ticks_since_sync`·`npc.clock.stalled`·`npc.clock.game_hour`,
  `/status` 에 `ticksBehind`·`tickSyncStalled`.
- 테스트 11건 추가. 전체 1,155건 통과 · 경고 0.

## 2026-09-10 11:04 KST · B-04 핸드셰이크 해시 분할 · 부분 호환 정책

마스터데이터가 한 글자만 달라도 링크가 안 붙었다. 상용에서는 게임서버·NPC 서버가 다른
파이프라인으로 배포되고 한쪽만 핫픽스되는 일이 잦은데, 그때마다 NPC 전체가 멈췄다.

- **신규** `src/Npc.MasterData/StructuralHash.cs` — id↔code · id↔bit · POI 좌표·존 ·
  버킷 차원만 담는다. **파일 바이트가 아니라 로드된 표에서** 뽑는다(같은 파일 안에서 구조와
  내용을 가르는 방법은 그것뿐이다).
- 구조 해시 불일치는 거절, 내용 해시 불일치는 **경고 후 수락**. `HelloAck.ContentHashWarning`
  과 `/status` 에 남는다. `code`·`bit` 재배치는 여전히 거절이다.
- 분할 해시를 설정하지 않은 호출부는 **v1 규칙(전체 일치)으로 돈다** — 설정하지 않았을 때
  보장이 조용히 사라지지 않게 한다.
- 테스트 7건 추가(desc 변경은 구조 불변 · POI 좌표·code 변경은 구조 변경 · 서식 변경 무영향 ·
  내용 불일치 수락 종단 · 구조 불일치 거절 종단). 전체 1,144건 통과.

## 2026-09-10 10:50 KST · B-01 계약 버전 · 와이어 버전 협상 · 기능 비트

계약(의미)에 버전이 없었고 와이어의 `Ver` 1바이트는 협상 없이 즉시 절단이었다.
`NpcCommandKind` 에 항목 하나를 추가해도 알릴 방법이 없어 롤링 배포가 구조적으로 막혀 있었다.

- **신규** `src/Npc.Contracts/ContractVersion.cs` — Major/Minor 규칙과 `LinkFeatures` 6비트.
- **신규** `src/Npc.Wire/V2/` — `LinkMessagesV2.cs`(v2 핸드셰이크) · `VersionNegotiation.cs`.
  기존 와이어 DTO 는 `V1/` 폴더로 옮기고 "수정하지 않는다" 를 파일 머리에 못 박았다.
  네임스페이스는 그대로다 — 동결은 폴더가 아니라 `Wire_LayoutIsFrozen` 이 강제한다.
- `FrameCodec` 이 버전 <b>범위</b> `[1, 2]` 를 받는다. 프레임 버전으로 v1/v2 핸드셰이크를 가른다.
- 협상: 교집합의 **최댓값**. Major 불일치는 `ContractMismatch`, Minor 는 낮은 쪽 기준.
  기능 비트는 교집합. v1 게임서버는 그대로 protocol 1 로 붙는다.
- v2 `WireHello` 에 A-06(nonce·auth) · A-08(shard·zoneMask) · A-10(게임 시각) ·
  B-04(구조/내용 해시) · G-02(세션 에포크) 자리를 미리 뚫었다. 레이아웃을 다섯 번 동결하지 않는다.
- `ContractVersionTests` 가 열거형 멤버 수 스냅샷을 들고 있다 — 늘었는데 Minor 를 안 올리면 깨진다.
- `docs/reference_link.html` §08·§09 에 협상·호환성 매트릭스·기능 비트 표를 넣었다.
- 테스트 18건 추가. 전체 1,137건 통과 · 경고 0.

## 2026-09-10 02:36 KST · A-09 배포 — Dockerfile · compose · k8s · CI 파이프라인

테스트 1,119건을 자동으로 돌리는 곳이 없었다. 이미지·파이프라인·시크릿 주입·롤아웃이 전부 없었다.

- **신규** `deploy/` — `Dockerfile`(멀티스테이지·비루트·`HEALTHCHECK`) ·
  `Dockerfile.testgameserver` · `compose.yaml`(대역+서버+Prometheus+Grafana) ·
  `k8s/deployment.yaml`(프로브 3종·시크릿·PVC·유예 30초) · `prometheus.yml` · `grafana/npc-server.json`.
- **신규** `.github/workflows/` — `ci.yml`(리눅스·윈도 매트릭스 · 빌드·스타일·테스트·검증·이미지) ·
  `nightly.yml`(Load·FaultInjection·실측 diff) · `release.yml`(태그 → 버전 주입·이미지·SBOM).
- **신규** `Npc.Host healthcheck --url` 서브커맨드 — `aspnet` 이미지에 curl 이 없다.
- `/status.version` = 어셈블리 버전 + 마스터데이터 해시 앞 8자리.
- `tests/Npc.Tests/Deploy/DeployArtifactTests.cs` — compose·k8s 의 `NPC_*` 가 옵션 표에 있는지,
  Dockerfile 이 생성물·dotLLM 을 굽지 않는지, CI 필터가 `build.ps1` 과 같은지 강제한다.
- 테스트 19건 추가. 전체 1,119건 통과 · 경고 0.

## 2026-09-10 02:28 KST · A-02 SIGTERM · 정상 종료 · `Bye(Shutdown)` · 서비스 프로파일

`docker stop`·k8s 종료·Windows 서비스 정지는 전부 SIGTERM 인데 SIGINT 만 처리해 컨테이너에서
드레인 없이 즉사했다. 게임서버는 NPC 서버가 왜 사라졌는지 몰랐다.

- **신규** `src/Npc.Host/HostShutdown.cs` — 신호 등록(SIGTERM·SIGINT·SIGQUIT)과 6단계 시퀀스.
  틱 루프 → 마지막 스냅샷 → 워커 → Flush·`Bye(Shutdown)`·소켓 → 웹 호스트 → 종료 코드.
- `TcpGameServerLink.SendByeAsync` 추가. `DisposeAsync` 가 `Connected` 면 먼저 보낸다.
  게임서버 대역은 사유(`LastByeCode`)를 기록한다 — 하트비트 타임아웃과 구별된다.
- `SnapshotWriter.WriteFinal` — 틱 루프가 멈춘 뒤 직접 복사해 마지막 한 장을 쓴다.
  주기 루프와 즉시 요청이 겹치지 않게 세마포어로 직렬화했다(빈 파일을 남기던 경합).
- `--shutdown-timeout-s`(기본 15). 넘기면 종료 코드 2 — "정상 종료" 와 "드레인 실패" 를 가른다.
- `--days` 를 명시하지 않고 dev 프로파일로 띄우면 기동 첫 줄에 경고를 낸다.
- 테스트 6건 추가. 전체 1,100건 통과 · 경고 0.

## 2026-09-10 02:08 KST · A-01 NPC 상태 스냅샷 · 복구

재기동·크래시·롤아웃마다 NPC 5,000 이 집 좌표로 돌아가 폴백 플랜을 처음부터 돌던 것을 없앴다.
게임 시계도 새벽 6시로 되감기지 않는다.

- **신규** `src/Npc.Runtime/NpcStoreSnapshot.cs`(`ShadowBuffer`·`SnapshotPort`) ·
  `src/Npc.Host/Persistence/{Crc32,SnapshotFile,SnapshotWriter,SnapshotRestorer}.cs`.
- 틱 루프는 틱 끝에서 `Array.Copy` 만 한다 — 복사 틱의 할당 0(테스트로 강제). 파일은 별도 스레드가 쓴다.
- 형식은 자체 이진 + CRC32 꼬리. `.tmp` → 원자 교체. 파일에 벽시계를 넣지 않는다.
- 복원 조건은 형식 버전·마스터데이터 해시·로스터 해시·NPC 수·인벤토리 칸 수 전부 일치.
  CRC 가 깨지면 이전 스냅샷으로 물러난다. 프리픽스가 다르면 개별 플랜만 버린다.
- `Waiting` 스텝은 `Ready` 로 되돌려 재발행하고, 상관 ID 는 65,536 만큼 건너뛴다.
- 옵션 4개 — `--snapshot-dir` · `--snapshot-interval-s` · `--snapshot-keep` · `--restore`.
  개발 기본은 꺼짐, `--profile service` 는 60초 주기로 켠다.
- `/status` 에 `lastSnapshotTick`·`restoredFromTick`·`snapshotFailures`, 계측기 `npc.snapshot.*` 4종.
- 테스트 20건 추가(왕복·복원 조건·결정론 연속성·할당 0·재기동 종단). 전체 1,094건 통과.

## 2026-09-10 01:52 KST · A-04 설정 소스 통합 · 바인드 주소 · 프로파일

옵션 30개를 배포 시스템(환경변수·ConfigMap·시크릿)으로 넘길 길이 없었다.
`localhost` 고정 바인드는 컨테이너에서 외부 도달이 안 됐다.

- **신규** `src/Npc.Host/Config/HostOptionsSource.cs`(옵션 표 · 환경변수·파일 → 합성 argv) ·
  `Config/ConfigPaths.cs`(탐색 기준 통일).
- 우선순위 **CLI > 환경변수(`NPC_*`) > 설정 파일(`npc.settings.json`) > 기본값**.
  파서는 하나 그대로다 — 합성 argv 를 앞에 붙이는 방식이라 "뒤가 이긴다" 만으로 성립한다.
- `--bind`(기본 `127.0.0.1`) · `--config` · `--profile dev|service`. 와일드카드 바인드는
  `NPC_ADMIN_TOKEN` 이 있을 때만 열린다. `service` 는 `--days 0` 을 강제한다.
- 설정 파일의 모르는 키는 기동 실패. `profiles.<이름>` 절이 최상위를 덮는다.
- `appsettings.Llm.json` 탐색을 `ConfigPaths` 로 옮기고 읽은 경로를 기동 로그에 적는다.
- 테스트 11건 추가. 전체 1,075건 통과 · 경고 0.

## 2026-09-10 01:31 KST · A-03 헬스체크 · `LinkState` 노출 · `Faulted` 좀비 제거

오케스트레이터가 재시작시킬 근거를 만들었다. 지금까지는 핸드셰이크가 거절되면 소켓 태스크가 조용히
끝나고 틱 루프는 이벤트를 기다리며 영원히 블록됐는데 `/status` 는 200 이었다.

- **신규** `src/Npc.Runtime/ILoopProbe.cs`(루프 하트비트 · `Volatile.Write` 하나) ·
  `src/Npc.Host/Api/HealthEndpoints.cs`(`/healthz/live`·`ready`·`startup`) ·
  `src/Npc.Host/LinkFaultPolicy.cs`(`Faulted` → 유예 뒤 종료 코드 3).
- `NpcServerLoop` 이 이벤트 대기에서 깨어날 때마다 `Probe.Beat()`. 벽시계 환산은 호스트가 한다.
- `/status` 에 `linkState`·`linkReject`, `/metrics` 의 링크 패널에 `state` 를 실었다.
- 옵션 5개 — `--on-link-fault exit|wait` · `--fault-grace-s` · `--live-stall-s` ·
  `--ready-tick-stall-s` · `--health-port`(`--no-dashboard` 와 함께 쓰면 프로브만 뜬다).
- 테스트 15건 추가. 전체 1,064건 통과 · 경고 0.

## 2026-09-10 00:06 KST · 상용 투입 로드맵 문서 작성 — `PRODUCTION_ROADMAP.md`

상용 온라인 게임 서버 투입 관점에서 저장소 전체를 조사해 결손을 진단하고 태스크 50건을 정의했다.

- **신규** `PRODUCTION_ROADMAP.md`(2,052줄) — 상단 체크리스트(트랙 A 운영 · B 계약 · C LLM 운영 · D 대화·기억 ·
  E LLM 온보딩 · F NPC 정의 툴 · G 품질·판정), 마일스톤·의존 그래프, 영역별 현재 상태 진단(근거 `파일:줄`),
  태스크별 상세(왜 → 현재 → 설계 → 구현 절차 → 문서 변경 → 테스트 → 완료 조건 → 절대 규칙 충돌 확인 → 크기·의존).
- 진단 요지 — 게임 로직·LLM 통합은 테스트로 눌려 있으나 **운영 층(영속성·SIGTERM·헬스체크·인증/TLS·핫 리로드·
  샤딩·배포)**, **LLM 운영 배관(제공사 페일오버·알람·프롬프트 버저닝·평가 파이프라인)**, **오써링 도구(GUI·스키마·
  code 할당·파생물 감지·검수)** 가 통째로 없다.
- LLM 이 이 서버를 잘 쓰게 하는 방법(§8): `docs/llm/` 온보딩 팩 · JSON Schema 발행 · MCP 서버 · 기계가 읽는 검증 출력 ·
  요청 템플릿 5종 · 에이전트 벤치마크. NPC 정의 툴(§9): `npc` CLI · NPC Studio · 설명 생성기(카드) · 편집 안전장치 · 검수 v2.
- `CLAUDE.md` 문서 지도와 `README.md` 문서 표에 링크를 추가했다. 코드 변경 없음.

## 2026-08-07 00:36 KST · 활용 실습서 완성 — 3~6부(10~20장) · 부록 · 예제 11종

남은 전부를 썼다. 예제를 실제로 만들어 돌리고 그 출력을 실었다.

- **신규** `docs/tutorial/ch10~ch20.html` · `appendix.html`(A 옵션 · B 엔드포인트 · C 오류 사전 ·
  D 되돌리기 · E 저장소 대조표). 표지와 `tutorial.js` 를 전 21장 + 부록으로 갱신했다.
- **신규 예제 11종** — `ch10_scenario/`(대본 2 + 틱 환산기 + 대조 실행) ·
  `ch11_chaos/`(유실 스윕 · 킬스위치) · `ch12_console_link/`(80줄 링크 + 배선/원복) ·
  `ch13_mini_gs/`(250줄 TCP 게임서버, 자체 csproj) · `ch14_sniffer/`(파이썬 스니퍼 + LAYOUT.md) ·
  `ch15_replay/` · `ch16_tier/` · `ch17_prebake/` · `ch18_review/` · `ch19_load/` ·
  `ch20_tests/`(xUnit 4개 + 체크리스트 12항목).

**실측으로 확보한 것**

- 대본 유무 대조 — plague/siege 에서 명령이 3~4천 건 줄지만 틱 p99·이월은 그대로.
- 명령 유실 스윕 — drop 0.5 에서 진행률 56.9 %, 타임아웃 13.0 %→46.6 %, 크래시 0.
- 킬스위치 3단 — 전부 끊은 뒤에도 스텝 22→1,830.
- `--link console` — 런타임 4개 프로젝트 diff 0줄, `timeouts 0` 으로 하루 완주.
- **미니 TCP 게임서버가 실제로 붙었다** — 핸드셰이크 수락, 명령 2,364건, `timeouts 0`,
  `--gap-at` 로 시퀀스를 건너뛰면 `eventGapsDetected=1`.
- 파이썬 스니퍼 실측 — Hello/HelloAck 각 96 B, CommandBatch 179프레임 평균 139 B.
- 결정론 3종 비교 전부 일치(A/B 루프백, C/D 기록·재생, A/C).
- 부하 — NPC 5,000 에서 p99 2.672 ms · `bytes/tick` 0 · **스캔/틱이 2,000부터 150 고정**.
- 반려 플랜 766건 집계 — Coherence 60.1 %, `V3.PRECONDITION_UNMET` 47.9 %, 스텝 1번이 271건,
  실패에 든 비용 $0.5743.
- manifest 실측 — 통과 523 / 시도 714(73.2 %), 통과 1건당 실효 단가 $0.001336.

**안 한 것** — 5부의 **유료 LLM 호출**은 실행하지 않았다(키는 환경에 있다).
대신 저장소의 실측 산출물(`planstore/manifest.json` · `rejected/` 766건)을 읽었고,
책에 "안 돌렸다" 고 명시했다.

12장 실습은 `lab/ch12-console-link` 브랜치에서 검증 후 되돌렸다.
main 은 빌드 경고 0 · 오류 0, 테스트 1,051개 전부 통과.

## 2026-08-06 20:39 KST · 활용 실습서 2부 집필 (4~9장) + 예제 스크립트 6종

콘텐츠를 직접 만드는 부다. 모든 예제를 실제로 돌려 검증했고, 그 과정에서 **목차 단계의 추정
세 가지가 틀린 것**을 확인해 사실대로 고쳤다.

- **신규** `docs/tutorial/ch04~ch09.html` — 마스터데이터 지도/검증기 · 아이템·POI ·
  플랜 DSL · 새 직업 · 인터럽트 · 새 액션. 표지와 `tutorial.js` 를 갱신했다.
- **신규 예제 6종** — `ch04_break/break.ps1`(패치 9종) · `ch05_apiary/patch.ps1` ·
  `ch06_plan/`(플랜 + 변형 4종) · `ch07_beekeeper/`(apply·revert) ·
  `ch08_interrupt/`(규칙 + 시나리오) · `ch09_action/apply.ps1`.
- `samples/lab.ps1` 이 실습장에 `NpcServer.sln` 표식을 둔다 — `tools/gen_*.cs` 가
  `--masterdata` 를 안 받고 저장소 루트를 위로 찾기 때문이다. 이제 생성기가 사본을 고친다.
- **`samples/check.ps1` 수정** — `--masterdata` 를 절대 경로로 바꿔서 넘긴다.
  상대 경로면 `validate` 는 사본을, 서버는 원본을 보는 상태가 되고 아무 경고도 안 난다.

**목차와 달라진 것 셋** (전부 실행해 보고 확인)

- `baker`·`bakery` 는 **이미 있다** → 5·7장 소재를 `honey`·`apiary`·`beekeeper` 로 교체.
- `Pray` 액션도 **이미 있다**(code 18) → 9장 소재를 `Tend`(code 38) 로 교체.
- 6장의 "거리표 미생성 = 조용한 오배정" 추정은 틀렸다 → 로더가 첫 줄에서 잡는다.

**실측으로 확보한 것** — 검증 패치 9종의 실제 메시지(v3·v5 는 규칙이 둘씩 걸린다) ·
POI 추가 시 `events` 32,411→32,556 이면서 `commands` 는 동일 · 플랜 스텝 상한 3~10 ·
`ArchetypeCount` 40→41 시 **테스트 18개가 깨진다**(40·2880·"폴백 40" 하드코딩) ·
인터럽트 규칙 하나로 발동 200→575 이면서 틱 p99 는 그대로 · 액션 하나에 프리픽스
13,488→13,602 토큰 · SHA 변경.

7장 실습은 `lab/ch07-beekeeper` 브랜치에서 검증한 뒤 되돌렸다. main 은 빌드 경고 0 · 오류 0.

## 2026-08-06 19:47 KST · 활용 실습서 0부·1부 집필 (0~3장) + 예제 스크립트

목차 확정 후 첫 두 부를 썼다. **예제를 실제로 만들어 돌려 보고 그 출력을 그대로 실었다** —
책에 있는 수치·로그·오류 메시지 중 지어낸 것은 없다.

- **신규** `docs/tutorial/ch00~ch03.html` · `tutorial.css` · `tutorial.js`(사이드바·페이저·진행 막대),
  표지 `index.html` 을 공용 자산으로 재작성하고 집필 현황을 표시했다.
- **신규 예제 7개** — `samples/lab.ps1`(실습장 생성·비교·삭제) · `check.ps1`(확인 3단) ·
  `ch01_first_run/run.ps1`·`variants.ps1`·`variants.md` · `ch02_watch/watch-npc.ps1`(NPC 추적기)·
  `read-metrics.md` · `ch03_demo/handshake-fail.ps1`. 전부 실행 검증했다.
- 실측으로 확보한 것: 대조 실험 8회(NPC 5,000에서 p99 2.368ms·스캔/틱 150·bytesPerTick 0),
  `--drop-rate 0.5` 에서 타임아웃 합성이 1,381→2,744로 대신 오르는 것,
  핸드셰이크 불일치 시 `link down`·`cmd 0`, 인구 가중치를 어긋내면 V5·V10 이 연쇄로 걸리는 것.
- `.gitignore` 에 `lab/` 추가(실습장은 재현 가능한 사본), `README.md`·`CLAUDE.md` 문서 지도에 연결.
- **알아 둘 것**: 마스터데이터 JSON 문법이 깨지면 검증기 전에 파서가 죽어
  `JsonReaderException` 스택 트레이스가 그대로 나온다. 기동은 정상적으로 실패하지만
  메시지가 거칠다 — 0장에 사실대로 적어 뒀다.

## 2026-08-06 18:39 KST · 활용 실습서 목차 초안 작성

제품을 앞에 두고 "무엇부터 손대는가"에 답하는 문서가 없어, 손으로 만들며 배우는 실습서의
목차를 먼저 잡았다. 세부 집필 전 검토용 초안이다.

- **신규** `docs/tutorial/index.html` — 6부 21장 목차. 장마다 **만들 예제 파일**과
  **동작 확인 3단(검증 → 실행 → 관측)**, 난이도·소요 시간·전제조건을 명시했다.
- 난이도 순서를 설계했다 — 1부는 코드 0줄, 2부는 JSON만, C# 은 12장부터, LLM 은 16장부터.
  1~15장이 LLM 없이 끝나는 것 자체가 "LLM 은 크리티컬 패스에 없다"의 실습이다.
- 실습 규약을 고정했다 — 원본 `masterdata/` 불변, 사본은 `lab/`, 샘플은 `NpcServer.sln` 에
  넣지 않아 본체 CI 영향 0. `src/` 를 건드리는 7·9장은 브랜치 + 원복 절차를 장 안에 싣는다.

## 2026-08-06 18:11 KST · CODEMAP.md — 작업별 코드 지도 신설

매번 전체 코드를 훑는 비효율을 없애려고 "무엇을 하려면 어디를 여는가"를 한 장으로 정리했다.

- **신규** `CODEMAP.md` — 작업별 지도 7분류 40여 줄(마스터데이터 · 런타임 · 연동 · 플랜 생성 ·
  캐시/재계획 · 호스트 · 시뮬), 이름으로 못 찾을 때 타는 **추적 경로 3개**(명령이 나가는 길 ·
  이벤트가 들어오는 길 · 플랜이 만들어지는 길), 프로젝트별 진입 파일, 안 봐도 되는 것.
- `CLAUDE.md` §4 확인 순서 맨 앞에 "CODEMAP 에서 해당 줄을 찾고 거기 적힌 파일만 연다"와
  "그 파일의 테스트를 먼저 읽는다"를 넣었다. 문서 지도에도 ★ 로 올렸다.
- 지도가 가리키는 경로 70개가 실제로 존재하는지 스크립트로 검증했다.

## 2026-08-06 17:58 KST · 사양 문서를 제품 레퍼런스 HTML 로 전환

R&D 단계의 md 사양·진행 원장·실측 보고서를 걷어내고, 남겨야 할 내용을 HTML 레퍼런스 3종으로 옮겼다.

- **신규** `docs/reference_link.html`(게임서버 연동 계약 — N1~N8·패킷·와이어·발행 규약) ·
  `reference_masterdata.html`(플래그 42·액션 37·아키타입 40·버킷 2,880·V1~V11) ·
  `reference_metrics.html`(성능·비용·품질·수용 기준 판정·미측정 목록).
- **삭제 33개** — `docs/00`·`01`·`02`·`20`, `TASKS.md`, `docs/measurements/*.md` 23개,
  그리고 삭제 문서를 읽던 R&D 평가 테스트(`tests/BlindEval/` 2파일, `Phase5GateTests` 5메서드).
  실측 원자료(jsonl·csv)와 제품 테스트는 남겼다.
- `CLAUDE.md` 를 제품 단계 기준으로 개정(태스크 단위 작업 규약 삭제·문서 지도 교체),
  `README.md`·`LLM_NPC_Server_Plan.md`·HTML 16개의 깨진 참조를 전부 새 레퍼런스로 돌렸다.
- 빌드 경고 0 · CI 기본 테스트 1,051개 전부 통과. 직전 커밋부터 깨져 있던
  `Gate_ReportHasAllEightSections`(삭제된 `RnD_Report.md` 참조)도 같이 정리됐다.

## 2026-08-06 16:28 KST · LlmNpcServer FAQ HTML 문서 작성

- 기존 MMORPG NPC 서버 대비 이점·적합한 적용 범위·현재 한계를 FAQ로 통합했다.
- 행동 플랜에 필요한 마스터데이터와 순찰·적대 플레이어 공격 설계를 단계별로 정리했다.
- 고정 대사와 실시간 LLM 자유대화의 범위를 구분하고, 후속 FAQ를 추가할 수 있는 검색·목차 구조를 마련했다.

## 2026-07-28 16:37 KST · P6 — 테스트 게임서버 착수 (T6-14·T6-15·T6-16)

`docs/20` D 절의 앞 셋. **게임서버 대역이 소켓을 갖고 NPC 서버와 실제로 붙었다.**

- **T6-14** `testbed/Npc.TestGameServer` Exe 골격과 §7.5 전 옵션 파싱. `Npc.Host` 를 참조하지
  않으므로(단방향 잎) 경로 해석까지 `HostOptions` 를 본떠 다시 썼다. 포트 하한을 0 으로 뒀다 —
  테스트는 자동 할당 포트로 연다.
- **T6-15** `GameWorld` — `Npc.Sim` 하위 시뮬 조립과 §7.2 틱 순서. `PlayerBots` 를 쓰지 않고
  그 자리를 `PlayerRegistry`(T6-19)에 비워 뒀다. 하위 시뮬이 봉인 클래스라 가로챌 수 없어서
  단계 관측자로 순서를 단언한다.
- **T6-16** `LinkListener`·`LinkSession` — 핸드셰이크·동시 세션 1개·로스터 재동기화.
  테스트는 상대역을 흉내내지 않고 **진짜 `TcpGameServerLink` 를 진짜 소켓으로** 붙인다.

**사양 보정 하나.** §5.5 가 존 상태 초기화에 `ZoneStateChanged` 만 적어 두었는데
`zones.json` 은 12개 존 중 2개를 `Cold` 로 선언한다. NPC 서버는 전 존을 `Fair` 로 시작하므로
기후가 조용히 어긋난다 — 버킷 키의 한 축이라 "가끔 이상하게 행동한다" 로만 나타난다.
`WeatherChanged × Z` 를 같이 보내고 §5.5 에 근거를 적었다.

빌드 경고 0 · CI 992건 통과(기존 실패 1건은 이 작업과 무관한 `ClockAuditTests` 다).

**다음에 할 것으로 기록해 둔 것 둘** (사용자 결정: 지금 하지 않는다. `TASKS.md` §3 참조)

1. **T6-12 의 남은 일** — `--zone` 이 파싱만 되고 `NpcRoster.Select` 에 전달되지 않는다.
   지금 `--zone` 을 주면 게임서버와 로스터 해시가 어긋나 핸드셰이크에서 거절된다.
   조립 순서(할당이 로스터 선택보다 먼저다)를 바꿔야 해서 한 줄 수정이 아니다. T6-35 전에.
2. **`ClockAuditTests.Whitelist_HasAReason`** — `IlScanner` 가 IL 바이트를 토큰으로 오해해
   `BadImageFormatException`. P6 이전부터 있던 것이고(main 에서 재현 확인) 결정론 감사 자체는
   멀쩡하다. CI 가 상시 빨간 것이 문제다.

## 2026-07-28 16:01 KST · P6 — 킬스위치 전달 경로 (T6-13)

`Npc.Runtime` 을 **한 줄도 고치지 않았다.** 태스크 지시서와 `docs/20` §11.4 는
`NpcServerLoop` 의 틱 구간에 훅 한 줄을 넣는 것으로 적혀 있었고 "P6에서 본체를 건드리는
유일한 태스크"라는 경고까지 달려 있었는데, `ITickObserver` 라는 이음매가 이미 있었다 —
`KillSwitchSchedule` 이 그 인터페이스를 구현하고 원래 관측자(`NpcMeter`)를 감싼다.
**P6 합격 기준 1(본체 diff 0줄)이 T6-01~T6-13 전 구간에서 유지된다.**

구멍 하나를 같이 메웠다. `KillSwitchState` 가 `SimDriver` 가 있을 때만 만들어져서
`--link tcp` 에서는 끊을 대상 자체가 없었다. 대역과 무관하게 만들되, 대역이 도는 모드에서는
시나리오 러너와 **같은 상태를 공유**한다 — `Fire` 는 멱등이라(N7) 양쪽이 같은 줄을 봐도 결과가 같다.

`--dev-control` 은 `docs/20` §10.3 에 있으니 원래 T6-12 의 몫이었는데 그때 빠뜨렸다.
여기서 넣었다. 막는 방식은 **조건부 등록**이다 — 조건부 401/403 은 "핸들러가 있고 거절한다"라
실수 하나로 열린다. 플래그가 없으면 라우트가 없다. 실기동으로 확인했다: 플래그 없이 404,
플래그와 함께 200(`{"target":"T2","fired":true}`), 잘못된 `target` 은 400, 대시보드는 양쪽 200.

같은 커밋에서 `docs/20` §11.4 의 낡은 서술 둘을 고쳤다 — 런타임 훅 이야기와,
§15 리스크표에 남아 있던 "T5-21 미착수라 `--tier t2` 킬스위치가 안 먹는다"(이미 해소됐다).

빌드 경고 0 · 964건 통과.

## 2026-07-28 15:30 KST · P6 — 사양 동기화와 로스터 추출 (T6-10·T6-11)

**① T6-10 — 링크 사양 문서 갱신**

`docs/02` 를 코드와 맞췄다. §7 의 "범위 밖 5항목"이 **전부 구현됐다**로 바뀌었고,
그러면서 **사양이 실제와 달랐던 곳 셋**을 같이 적었다 — 프레임 헤더는 4바이트가 아니라
8바이트, `SocketAsyncEventArgs` 가 아니라 `PipeReader`, `BoundedChannel` 이 아니라 우선순위 링.
남은 것은 이기종 엔디언·인증·샤딩 셋으로 다시 적었다.

**일부러 고치지 않은 것 하나를 등재했다.** `Npc.Contracts/IGameServerLink.cs:29` 가 아직
"(미래) TCP"라고 적고 있다. `docs/20 §1` 합격 기준 1 이 그 프로젝트의 diff 를 금지하므로
**주석 한 줄이라도 건드리면 "본체 무변경" 주장이 흐려진다** — 그 주장이 P6 의 존재 이유다.
G6-1 을 판정한 뒤에 고친다.

**② T6-11 — `NpcRoster` 추출**

선택 규칙과 해시를 한 곳에 모았다. 규칙이 갈리면 **첨자 7번이 서로 다른 NPC** 가 되고,
증상은 "가끔 이상하게 행동한다"로 나타나 원인을 찾기 어렵다.

**선택 공식은 한 글자도 바꾸지 않았다.** 추출은 리팩터링이지 재설계가 아니다 —
결과가 한 마리라도 달라지면 프리베이크·리플레이 산출물의 첨자가 전부 어긋난다.
`Roster_SelectionMatchesLegacyFormula` 가 1·7·500·5000 에서 원소 단위로 못 박는다.

해시 설계에서 둘을 판단했다 — **순서를 포함하고**(집합이 아니라 배열의 해시여야 첨자가 맞다),
**좌표는 뺀다**(스폰 위치 조정 때문에 거절되면 우회 옵션을 만들고 싶어지고, 그게 검사를
무력화하는 길이다).

완료 조건의 핵심인 **"기존 결정론 테스트 무수정 통과"** 를 확인했다 — 11건 통과,
`tests/` diff 는 신규 파일 하나뿐이다.

---

**P6 진행: 11/36.** A·B 완료 · C 착수. 다음은 T6-12(`--link tcp` 배선).
`.\build.ps1` 3단계 통과 · **950건**.

## 2026-07-28 14:40 KST · P6 B 그룹 완료 — TCP 수신·재접속 (T6-08·T6-09)

**① T6-08 수신 경로**

`PipeReader` → 프레임 분해 → `WireEvent[]` → `GameEvent` → 채널. 채널의 기록자는
리시버 태스크 하나뿐이다. `LinkStats` 여섯 필드를 전부 채웠다.

**N6 갭은 "있었다"가 아니라 "몇 개가 없었나"를 센다.** 게임서버가 시퀀스를 리셋하면
`EventApplier` 가 이후 이벤트를 전부 중복으로 버려 NPC 가 영원히 멈추므로,
이 값이 그 사고의 첫 신호다.

**② T6-09 재접속·하트비트**

`RunAsync` 하나가 링크 수명 전체를 돈다. 하트비트 1초 · 무수신 3초 → `Degraded` →
백오프 재접속(250ms~4s). 재접속 시 링에 남은 명령은 버리고 드롭에 계상한다.
`Faulted` 는 재시도하지 않는다.

**연결 생성기를 생성자 인자로 뺐다.** 재접속은 "연결을 새로 만든다"가 본질이라
그 자리를 밖에서 갈아끼울 수 있어야 소켓·포트·타이밍 없이 재현된다.

**테스트가 결함을 하나 잡았다** — `PumpUntilBrokenAsync` 가 세션 종료를 무조건 `Degraded` 로
보고하고 있었다. **종료 지시는 저하가 아니다**(`docs/20 §6.3` 은 그것을 `Disconnected` 로 정한다).
그대로 뒀으면 정상 종료가 "링크가 끊겼다"로 기록돼 대시보드와 로그가 거짓이 됐을 것이다.

---

**P6 진행: 9/36. A(와이어 프로토콜)·B(TCP 링크) 완료.**
다음은 T6-10(문서 갱신) → C(로스터·호스트 배선).

불변식 확인 — T6-01~T6-09 아홉 커밋 전부 `Npc.Runtime`·`Npc.Planning`·`Npc.Core`·
`Npc.Contracts`·`masterdata` 변경 **0줄**이다. **`docs/20 §1` 합격 기준 1 이 여기까지 지켜졌다.**

`.\build.ps1` 3단계 통과 · **942건** · TCP 링크 15건 3회 연속.

## 2026-07-28 14:26 KST · P6 T6-07 — TCP 송신 경로

**사양 §6.1 을 그대로 따르지 않았고, 그 이유를 사양에 적었다.**

§6.1 그림은 센더 태스크가 `publishedTail` 까지 `PriorityCommandRing` 에서 직접 Pop 한다.
**그런데 그 링은 삽입과 꺼냄이 `_count` 를 함께 만져 SPSC 로 안전하지 않다** —
`ReplanQueue` 에서 이미 같은 실수를 한 번 했다(결정 16).

그래서 링은 틱 루프 단독 소유로 두고 경계를 **배치 단위**로 옮겼다. `BatchQueue` 는
생산자/소비자 첨자가 갈린 SPSC 이고 슬롯 배열을 기동 시 전부 잡는다. 대가는 Flush 당
배치 하나만큼의 복사(O(N))이고, 직렬화·소켓 쓰기는 여전히 다른 스레드다.

**중간에 정책을 한 번 뒤집었다.** 처음에는 배치 큐가 차면 배치를 통째로 버리게 짰는데,
그러면 **링의 우선순위 역압을 우회해 `Critical` 까지 같이 버린다** — `docs/02 §1` 이 금지한 것이다.
지금은 Flush 를 건너뛰고 명령을 링에 남긴다. 링이 넘치면 그때 `Cosmetic` 부터 버리는 것은
`PriorityCommandRing` 이 한다. 건너뛴 횟수는 `FlushesSkipped` 에 남는다.

이 버그는 `TcpLink_OneFlushIsOneFrame` 이 "프레임 4/5"로 잡아 줬다 —
실패 메시지에 상태·드롭·대기를 실어 두지 않았으면 타임아웃만 보고 원인을 못 찾았을 것이다.

**테스트 3건 전부 완료 조건 그대로다.** `OneFlushIsOneFrame` 은 Flush 사이에 센더가
따라잡을 틈을 준다 — 실서비스는 10Hz 라 100ms 가 있고, 몰아치면 배치 큐가 차서
그 상태로 프레임 수를 재면 N8 이 아니라 큐 깊이를 재게 된다.

**P6 진행: 7/36.** 다음은 T6-08 수신 경로. 불변식(본체 4개 프로젝트 · masterdata 무변경) 유지.
`.\build.ps1` 3단계 통과 · **937건**.

## 2026-07-28 14:12 KST · P6 — 와이어 프로토콜 완료와 TCP 핸드셰이크 (T6-05·T6-06)

**① T6-05 — `PriorityCommandRing` 추출**

`LoopbackGameServerLink` 의 우선순위 링과 역압 정책을 클래스로 뽑았다. **동작은 한 줄도
바뀌지 않았고 테스트 파일 diff 가 0줄**이다 — 완료 조건이 "기존 테스트 무수정 통과" 였다.

우선순위마다 용량만큼의 링을 잡는 것이 `Critical` 무손실의 근거다(다른 우선순위가 가득해도
자리가 남는다). 그 성질을 클래스 주석에 남겼다 — 원래 코드에는 없던 설명이다.

**② T6-06 — TCP 연결·핸드셰이크**

골격을 구현으로 바꿨다. 넷을 검증한다 — 프로토콜 버전 · 타임스케일 · 마스터데이터 해시 ·
로스터 해시. **우회 옵션을 만들지 않았다**: 마스터데이터가 다른 두 프로세스를 붙이면
POI code 가 어긋나 원인을 찾는 데 하루가 든다.

`HandshakeAsync(Stream, ct)` 를 따로 노출했다. **소켓을 만들지 않는 진입점이 있어야
네 가지 불일치를 각각 재현할 수 있다** — 검증 규칙이 되돌리기가 가장 비싼 부분이다.

`Faulted` 는 재시도하지 않는다. 사람이 고쳐야 하는 상태이고, 무한 재시도로 덮으면
로그만 차고 원인이 묻힌다. 접속 실패(상대가 아직 안 떴다)는 성격이 달라 `Connecting` 유지다.

**아키텍처 테스트가 새 간선 `Npc.Gateway → Npc.Wire` 를 잡았다.** 사양(`docs/20 §4`)이
허용하는 간선이라 기대 그래프와 `CLAUDE.md §3` 을 같은 커밋에서 고쳤다 — T6-10 이
`docs/02` 까지 정리하기로 되어 있지만 문서와 코드가 어긋난 채로 두지 않는다.

---

**P6 진행: 6/36.** A(와이어 프로토콜) 완료, B(TCP 링크) 착수. 다음은 T6-07 송신 경로.
**불변식 확인** — T6-01~T6-06 여섯 커밋 전부 `Npc.Runtime`·`Npc.Planning`·`Npc.Core`·
`Npc.Contracts`·`masterdata` 변경 **0줄**이다.

`.\build.ps1` 3단계 통과 · **935건**.

## 2026-07-28 13:55 KST · 할당 측정 정리(결정 18)와 P6 와이어 프로토콜 (T6-03·T6-04)

**① 결정 18 — 할당 측정 두 곳**

- `CognitionSchedulerTests` 를 `AllocationCollection` 에 넣어 직렬화했다. `Cognition_ScanDoesNotAllocate`
  가 전체 스위트에서 5회 중 1회 실패했는데, 원인은 **계층형 JIT** 이다 — 1,000틱 워밍업을 해도
  다른 테스트와 병렬로 돌면 CPU 경합으로 tier-1 승격이 측정 창 안으로 밀린다. 이후 5회 연속 통과.
  **다만 원래 빈도가 1/5 라 5회 통과는 증명이 아니다.**
- `Runtime_TickWindowAllocatesNothingInHost` 에 남아 있던 "누계 0" 단언을 결정 17 과 같은
  "정상 상태 0" 으로 맞췄다. **게이트만 고치고 같은 단언을 쓰는 이 테스트를 남겨 두면
  야간 회차에서 같은 이유로 실패한다.**

**② T6-03 — 제어 메시지와 `WireHash`**

`WireHash` 는 hex 가 아니라 `ulong` 4개다 (N3). 정확히 SHA-256 32바이트라 손실이 없고,
hex 로 실으면 64바이트에 파싱까지 붙는다. `FromHex` 가 길이를 확인하고 던지는 것이 중요하다 —
**조용히 잘라 쓰면 해시 비교가 거짓으로 통과하고, 그러면 마스터데이터가 어긋난 게임서버가 붙는다.**

**③ T6-04 — 프레임 코덱**

8바이트 고정 헤더. 헤더는 손으로 쓴다 — MemoryPack 안에 길이를 또 넣으면 진실의 출처가 둘이 된다.
**버전을 길이보다 먼저 본다**(버전이 다르면 길이 해석을 믿을 수 없다). 길이 상한 1MiB 검사를
빼지 않는다 — 없으면 잘못된 4바이트 하나로 프로세스가 죽는다.

테스트 7건. `Frame_ReadsAcrossSegments` 가 특히 중요하다 — 단일 배열만 가정하면
단위 테스트는 통과하고 `PipeReader` 를 물리는 실서비스에서 깨진다.

---

**P6 합격 기준 1 확인** — T6-01~T6-04 네 커밋 전부 `Npc.Runtime`·`Npc.Planning`·`Npc.Core`·
`Npc.Contracts`·`masterdata` 변경 **0줄**이다 (커밋별로 확인했다).

`.\build.ps1` 3단계 통과 · **929건**.

## 2026-07-28 13:35 KST · 틱 할당 판정 정정 — 누수가 아니라 콜드 스타트였다 (결정 17)

**추적 결과: 틱당 누수가 아니다.**

| 회차 | 할당이 있었던 틱 | 누계 |
|---|---|---|
| 게임 1일 (1,440틱) | 1 · 148 · 396 | 264 B |
| **게임 3일 (4,320틱)** | **1 · 148 · 396** (같다) | **264 B** (같다) |

회차를 3배 늘려도 늘지 않고, 마지막 할당 뒤 **3,924틱이 전부 0** 이다.
단계별로는 틱 1 이 `CognitionScheduler.Scan`(88 B), 148·396 이 `PlanExecutor.Step`(24·152 B)인데
그 안의 개별 호출(`TryFor`·`NeedsReplan`·`Score`·`Capture`)에는 할당이 없다 —
**그 경로가 틱 창 안에서 처음 실행될 때의 JIT·정적 초기화 비용**이다.
195 → 718버킷에서 프리베이크 플랜이 새 액션 분기를 처음 밟은 것이 차이였다.

**판정을 고쳤다.**

```
전:  AllocatedInTicks == 0                   → 콜드 스타트까지 세어 JIT 런타임에서 달성 불가
후:  LastAllocatingTick * 2 < TicksProcessed → 회차 후반부에 할당이 없다
     BytesPerTick == 0                        (그대로)
```

**임의의 워밍업 상수를 두지 않았다** — 실측(396틱)에 맞춰 정하면 게이트를 실측에 맞추는 것이다.
"후반부"는 회차 길이에 따라 자동으로 정해지고 **진짜 누수는 반드시 후반부에도 걸린다.**

**P4 게이트 9/10** (남은 것은 항목 5 GPU 하나).
빌드 경고 0·오류 0 · `dotnet format` 통과 · 916건 4회 연속 통과.

**부수 등재** — `Cognition_ScanDoesNotAllocate` 가 전체 스위트에서 **5회 중 1회** 실패한다
(단독은 3/3 통과). 같은 계열이다 — 할당 측정이 계층형 JIT 에 민감하다. 처방 3안을 원장에 올렸다.

## 2026-07-28 12:58 KST · `ReplanQueue` 데이터 레이스 수정 (결정 16)

**힙을 틱 루프 단독 소유로 만들고 워커와는 `ReplanHandoff` 로만 주고받게 했다.**

```
  틱 루프 (단일 스레드)                워커 N개
  DrainReturns ◀── Returns 링 ◀────── TryReturn   (낡은 요청)
  Scan → TryEnqueue
  Pump         ──▶ Pending 링 ───────▶ TryClaim    (일감)
```

**락으로 고치지 않았다** — `CLAUDE.md §2.1` 이 틱 루프의 `lock` 을 금지한다. 워커 8개가 다투는
락을 틱 루프가 같이 잡으면 그 순간 p99 가 무너진다. 칸별 시퀀스를 쓰는 **락프리 MPMC 링** 둘로
갈랐고 **할당은 0** 이다.

**결과**: 재현 테스트 `Handoff_SurvivesConcurrentWorkersAndTicks` **10/10 통과**
(고치기 전 8회 중 2회 실패). 스트레스 테스트 6건을 새로 넣었다 —
생산자 1 · 소비자 8 로 20,000건을 태우고 **하나도 잃거나 겹치지 않음**을 단언한다.

**바뀐 동작 하나** — 낡은 요청의 재삽입이 **한 틱 늦어진다.** 예전에는 워커가 힙에 바로 되넣어
같은 `TryTake` 안에서 다시 집었다. T4-04 의 의미("폐기하고 지금 상태로 다시 넣는다")는 그대로다.
`Worker_DiscardsStaleJobAndRequeues` 를 새 의미대로 다시 썼다.

**근본 원인도 메웠다** — `docs/14 §4` 에 스레드 모델이 없어 워커가 힙을 직접 꺼내는 코드 조각이
사양에 그대로 실려 있었다. 누가 무엇을 만지는지를 표로 명시하고 §10 위험 대장에도 올렸다.

---

**부수 발견 — `Gate_NoAllocationInTickLoop` 이 실패한다. 결정 16 과 무관하다.**

틱당 **240~264 B** 할당. HEAD 에서도 같은 값이라 이번 수정 탓이 아니고,
**플랜 스토어를 채운 뒤 생겼다** — 세션 초반 195버킷에서는 통과했고 718버킷이 된 뒤 실패한다.
즉 **버킷이 실제로 히트하는 경로에 할당이 있다.** `P4_gate.md` 항목 9 의 "통과" 는
빈 스토어 기준이었다는 뜻이라 게이트 판정을 다시 해야 한다. `TASKS.md §3` 에 올렸다.

빌드 경고 0·오류 0 · `dotnet format` 통과 · **916건 3회 연속 통과**.

## 2026-07-28 12:26 KST · manifest 스키마와 게이트 산출물 의존 정리 (결정 13·15-A·14)

**① 13 — `counts.generated` 를 "스토어 총계" 로 확정**
원인은 `BulkRunner` 가 빈 스토어로 시작하는 것이었다. 디스크에 718개가 있는데 manifest 는
523(이번 회차 몫)이라 적었고 게이트가 그 값을 읽었다. 저장 뒤 디스크에서 다시 읽어 센다.

**② 15-A — 판정선을 "전량" 에서 "선언한 범위 완주" 로**
`RequireFullRun`(생성/2,880 ≥ 95 %) → `RequireCompleteRun`(`target > 0 && !partial`).
manifest 에 `counts.target` 을 추가했다. **방어는 사라지지 않고 자리를 옮겼다** — 측정치에
`[대상 N버킷]` 꼬리표를 강제한다. "97초" 가 264버킷의 값이라는 문맥을 잃으면 게이트가 다시 거짓이 된다.

**③ 14 — `SiegeTests` 4건을 `Category=Gate` 로**
**버킷을 합성해 채우는 안은 택하지 않았다.** 그러면 "War 에서 플랜이 달라졌다" 가 런타임이 아니라
픽스처의 성질이 되어 테스트가 공허해진다 — 합성 플랜의 goal 만 바꿔도 통과한다.
어느 넷인지는 빈 스토어(`a8ddd61`) 실측으로 갈랐다. CI 기본 914 → 910건.

---

**부수 발견 — `ReplanQueue` 가 스레드 안전하지 않다. 실제 결함이다.**

`Handoff_SurvivesConcurrentWorkersAndTicks` 가 `ReplanQueue.Insert` 에서
`IndexOutOfRangeException` 으로 죽는다 (8회 중 2회).

- 틱 루프가 `TryEnqueue` 를, 워커 8개가 `TryDequeue`(`IndividualReplanSource`)를 **동시에** 부른다
- `ReplanQueue` 에 동기화가 **하나도 없다** — `lock`·`Interlocked`·`Volatile` 0개
- 용량 검사와 `Insert` 사이 창에서 `_count` 가 `_capacity` 를 넘어 `_heap[_count]` 가 범위를 벗어난다

**지금까지 안 터진 이유는 전 회차가 `--tier none` 이라 워커가 뜨지 않아서다.**
보고서의 "런타임 LLM 근거 없음" 과 같은 뿌리이고, **결정 5(티어 켠 회차)의 선행이다.**
`docs/14 §2` 가 스레드 모델을 명시하지 않은 것이 뿌리라 설계 판단이 필요하다 — `TASKS.md §3` 에 올렸다.

## 2026-07-28 12:03 KST · 게이트 기준 개정 4건과 CI 필터 수정 (사용자 결정 1-A·2-A·3-A·4-A·12)

**① 1-A — P2 통과율 게이트를 판정에서 뺐다** (`docs/12 §9`)
통과율 뒤의 걱정("플랜이 없으면 NPC 가 멈춘다")은 `PlanStore.Resolve` 가 구조적으로 막고
시나리오 C 가 실측했다. 품질은 골든 93.6 %, 비용은 런타임 히트율 98.67 % 가 각자 판정한다.
**새 하한을 만들지 않았다** — 실측 73~78 % 에 맞추면 게이트를 실측에 맞추는 것이다.
측정·보고는 그대로 한다.

**② 2-A — P3 항목 1 의 완료율 판정 제거** (`docs/13 §7`)
분모 2,880 이 도달 집합 방침에서 정의상 영구 미달이었다. 폴백 해소(기제)만 남겼다.

**③ 3-A — 캐시 적중률 하한을 엔진 계열별로** (암시적 ≥ 40 % · 명시적 ≥ 95 %)
40 % 는 손익 기준이다 — 적중분이 1/10 단가라 40 % 면 비용 약 1/3 절감.
**다만 오늘은 효과가 없다** — 실측 69.3 % 가 새 하한을 넘는데 `RequireFullRun` 이 앞에서 막는다.

**④ 4-A — G0-1 을 "어휘 검증 통과율 ≥ 90/100" 으로.** 그 기준으로 **93/100 통과**.
`forced` 가 유효 JSON 99/100 에 어휘 검증 0/100 이었다 — 유효 JSON 으로 재면 100 % 실패를
통과로 읽는다. **P0 게이트 1/4 → 2/4.**

**⑤ 12 — `Category=Load` 를 CI 기본에서 뺐다**
`CLAUDE.md §5` 는 Load 를 "야간" 으로 정해 뒀는데 필터가 `Golden` 만(`build.ps1`) /
`Golden`·`Gate` 만(문서) 뺐다 — **문서와 코드가 어긋난 것**이라 사실상 버그다.
`build.ps1` 에 빠져 있던 `Gate` 도 같이 고쳤다.

| | 전 | 후 |
|---|---|---|
| CI 기본 소요 | 8분 37초 | **57초** |
| 테스트 | 934건 (1건 간헐 실패) | **914건 전부 통과** |
| 측정 파일 | 매번 덮어써짐 | **무변동** |

`.\build.ps1` 3단계 전부 통과 (build · format · test).

**남은 것 — 항목 2·3·4 는 여전히 미판정이다.** `RequireFullRun` 이 `생성/2880 ≥ 95%` 를
요구하는데 전량 회차를 안 하기로 했다(결정 8). **기준이 틀렸던 것과 회차 범위가 부족한 것은
다른 문제**이고 이번에 고친 것은 앞쪽뿐이다. 선택지 4개를 `P3_gate.md §2.6` 에 정리했다.

## 2026-07-28 11:40 KST · 공성 확장 회차(+714)와 P6 착수 (T6-01·T6-02)

**① 공성 확장 프리베이크 (+714, 누계 718버킷)**

도달 집합 A(264)만으로는 `SiegeTests` 2건이 "War 인데 아무 플랜도 갈아타지 않았다"로
멈췄다. `siege.jsonl` 이 흔드는 존·상태를 더해 **978** 을 유도했고(보고서와 재차 일치)
추가분 714만 돌렸다.

- **$0.6990 · 177.6초 · 생성 523 (73.2 %) · 429 0회** (최대 동시성 32)
- 누계 **718버킷 · $1.0717**
- **P4 게이트 9/10** — 항목 2(98.67 %)·항목 6(`SiegeTests` 7/7) 둘 다 통과.
  남은 것은 항목 5(GPU) 하나이고 T1 을 켠 회차가 필요하다.

캐시 적중률이 45.4 % → **69.2 %** 로 올랐다. 회차가 길어 프리픽스가 더 오래 살아 있었고
AIMD 가 상한 32 까지 갔다. 429 는 여전히 0 이라 **32 도 아직 천장이 아니다.**

**② P6 테스트 베드 착수 — T6-01 · T6-02**

- `Npc.Wire` 프로젝트 생성. `Npc.Contracts` + MemoryPack 1.21.4 만 참조한다.
- `WireCommand`(56B) · `WireEvent`(64B) 와 `From`/`To` 매핑. 테스트 7건.
- 핵심은 `Wire_MirrorsContractMembers` 다 — `Npc.Contracts` 에 `[MemoryPackable]` 을
  못 붙여 DTO 를 갈랐고, 가른 대가인 **드리프트**를 리플렉션으로 잡는다.

**세션 상한(3태스크)에 걸려 T6-03 부터는 다음 세션으로 넘긴다.**

빌드 경고 0·오류 0 · `dotnet format` 통과 · 934건 중 933건 통과.

부수 등재 2건 — `Load_RunsSelectedMatrix` 가 전체 스위트에서 간헐 실패한다(단독은 통과).
그리고 증분 `--only` 회차에서 **manifest 가 스토어 총계가 아니라 그 회차만 적는다**
(실제 718, manifest 523). `RequireFullRun` 이 그 값을 읽어 게이트가 스토어를 작게 본다.

## 2026-07-28 11:10 KST · 도달 집합 프리베이크 회차 (264 버킷)

`RnD_Report` 권고 2 를 실행했다. 전량 2,880 대신 **도달 집합 264** 만 만들었다.
도달 집합은 보고서 값을 옮기지 않고 `npc_instances.json` × `zones.json` 에서 **다시 유도**했고
`bucket_usage.md` 와 완전히 일치했다 (44쌍 × 6시간대 = 264).

**결과: $0.3727 · 96.9초 · 생성 195 (73.9 %) · 429 0회.** 예상 $0.47보다 21 % 싸다.

**핵심 — 버킷을 줄였는데 히트율이 올랐다.**

| 스토어 | 버킷 | 시나리오 A 히트율 |
|---|---|---|
| 이전 (시나리오 B 최소분) | 254 | 76.08 % |
| **도달 집합** | **195** | **98.67 %** |

**P4 게이트 항목 2 가 미달 → 통과**로 바뀌었다. 최초 판정이 "히트율은 채움 비율에 묶여
2,880 을 다 채워야 한다"고 진단했는데 **그 진단이 틀렸다** — 묶여 있는 것은 채운 버킷이
실제로 조회되는가다.

**대신 시나리오 B 가 내려갔다.** 도달 집합 A 에는 War 버킷이 없어 `SiegeTests` 가 7/7 → 5/7 이다.
한 스토어로 A·B 를 동시에 만족시키지 못한다 — 둘 다 닫으려면 978버킷(추가 약 $1.0)이 필요하다.

**P3 게이트 항목 1~4 는 열리지 않았다.** `RequireFullRun` 이 분모 2,880 으로 완료율 6.8 % 를
보고 판정을 거부한다. **게이트를 고쳐서 통과시키지 않았다** — 분모를 바꿀지는 사람이 정할 일이라
`TASKS.md §3` 에 결정 대기로 올렸다.

## 2026-07-28 10:53 KST · T0-13 — W1 결과 정리와 상위 문서 갱신

P0 의 마지막 태스크. `docs/measurements/W1_results.md` 를 새로 쓰고 M1~M6 을 `W1_perf.csv`
180행 **재집계**(엔진 × 캐시 중앙값)로 채웠다. 값을 옮겨 적지 않고 원자료에서 다시 냈다.

- **M1 미달** 5.14s (8B) — 목표 1.75s 의 2.9배. **원인은 prefill 이 아니라 decode 다**
  (가정 100 tok/s · 실측 44). prefill 은 캐시가 걸리면 목표 0.25s 에 붙는다.
- **M3 통과** prefill 85~88 % 감소 — 이 설계의 유일하게 검증된 승리다.
- **M4·M5 는 "미측정" 으로 적었다.** 외부 API 를 한 번도 부르지 않았다. 없는 것을 채우면
  이 표를 근거로 쓴 문서가 전부 거짓이 된다.
- **M6 판정 불가** — 8B 미채점이라 G0-4 에 비교 대상이 없다.

**`off_tail` 을 쓰면 G0-3 을 오판했을 것이다.** 무효화 주석을 프리픽스 **뒤**에 붙이면
접두가 일치해 캐시가 그대로 적중한다(242ms vs 적중 237ms). 감소율 2 % 가 나왔을 것이다.

갱신: 상위 계획 §2.1·§10.1·§10.2(추정 지우지 않고 실측 **병기** + §10.2.1 신설) ·
`CLAUDE.md §9`(6행 → 10행 전부 실측) · `docs/03 §2`.

**`docs/03 §2` 는 문서만 낡아 있었다** — `SchemaProvider` 는 이미 `minItems`·`required` 등을
뺀 상태인데 사양서만 초안 그대로였다. 프리픽스는 코드 생성물을 싣지 이 문서를 싣지 않으므로
**프리픽스 해시는 바뀌지 않는다**(프리픽스·스키마 테스트 31건 통과로 확인).

빌드 경고 0·오류 0 · 코드 변경 없음(문서만).

## 2026-07-28 10:39 KST · T5-21 — 메꾼 티어가 킬스위치를 물려받는다

P5 의 마지막 미착수 태스크. `TierWiring` 이 한쪽 티어의 엔진이 없을 때 없는 쪽을 있는 쪽으로
메꾸는데, 메꾼 자리가 원 티어의 차단을 물려받지 않아 `--tier t2` 에서 T2 를 끊어도
같은 외부 컴파일러가 T1 이름으로 계속 불렸다 — `KillSwitchTarget` 주석이 금지한 "안 끊긴 채로 통과".

`TieredPlanCompiler` 에 `HasT1`·`HasT2`(기본 `true`)를 두고 `Available` 을 사양(`docs/15 §E`)의
두 식으로 바꿨다. `TierWiring` 만 `t1/t2 is not null` 을 넘긴다 — 기본값이 `true` 라
기존 호출부와 킬스위치 3종 테스트가 무수정으로 통과한다. 테스트 3건 추가.

**부수 발견 2건** — 둘 다 `TASKS.md §3` 에 등재했다.
- `SiegeTests` 4건이 프리베이크 산출물(`planstore/plans/`, `.gitignore` 대상) 없이는 실패한다.
  T5-21 이전부터 그렇고 커밋 `a8ddd61` 에서 재현했다.
- **`dotnet test` 가 `docs/measurements/W10_load.csv` 를 덮어쓴다.** 이번 회차에서 실제로
  `cache_hit_rate` 0.71 → 0.0000, `cold_buckets` 2,626 → 2,880 으로 덮였다. P4 게이트 항목 2 의
  근거라 되돌렸다.

빌드 경고 0·오류 0 · `dotnet format` 통과 · 927건 중 923건 통과(실패 4건은 위 SiegeTests).

## 2026-07-27 20:12 KST · 수정 4건을 태스크 단위 커밋으로 분리

`Program.cs` 가 세 수정에 걸쳐 있어 HEAD 로 되돌린 뒤 한 묶음씩 다시 얹어 커밋했다.
단계마다 빌드하고 해당 동작을 실제로 확인했다.

```
f82be31 host: 링크 큐를 한 틱치로 잡고 드롭을 종료 요약에 낸다
67419c8 host: --scenario 와 --trace 도 저장소 루트를 찾도록 경로 해석을 한곳으로 모은다
aaf7e45 host: 대시보드 ContentRoot 를 실행 파일 폴더로 고정한다
20ec98b test: 링크 교체 게이트를 오버런 수 대신 p99 로 판정한다
```

`docs/index.html` 은 스테이징된 채로 두었다 — 이번 수정과 무관한 1,900줄 보강이 같은 파일에
섞여 있어 나눠 담을 수 없다. `README.md`·`working_log.md` 도 커밋하지 않았다.

최종 확인: 빌드 경고 0·오류 0 · `dotnet format` 통과 · 테스트 795건 전부 통과 ·
작업 트리가 커밋 전 최종본과 내용 동일.

## 2026-07-27 19:35 KST · 기동 리뷰에서 찾은 결함 3건 수정

**① 링크 큐 사이징** — `SimDriver.Create` 가 `LoopbackGameServerLink` 용량을
`npcs × CommandEmitter.MaxCommandsPerStep`(하한 4,096)으로 준다. 기본값 4,096 은
NPC 5,000 의 한 틱치보다 작아 틱 0 에 904건이 버려졌다. 소비자(`FlushAsync`)가 매 틱 전량을
비우므로 여기서 넘치는 것은 역압(docs/02 §1)이 아니라 사이징 실수다.
종료 요약에 `drops link N sim N` 을 추가했다 — 헤드리스로 돌리면 대시보드를 못 본다.

**② 경로 해석 비대칭** — `HostOptions.TryResolvePaths()` 를 추가해 기동 직후 한 번에
`masterdata`·`planstore`·`scenario`·`trace` 를 전부 절대경로로 푼다. `--masterdata` 만
저장소 루트를 위로 찾고 `--scenario`·`--trace` 는 안 찾던 비대칭이 원인이었다 —
문서에 적힌 `--scenario ./scenarios/siege.jsonl` 이 미처리 예외로 죽었다.
새로 만드는 `--trace` 파일도 저장소 루트 기준으로 고정한다. 못 찾으면 한 줄 에러 + 종료 코드 2다.

**③ 대시보드 404** — `WebApplicationOptions.ContentRootPath` 를 `AppContext.BaseDirectory` 로
고정하고, csproj 에 `wwwroot` 를 빌드 출력으로도 복사하게 했다. ContentRoot 기본값이 작업 폴더라
같은 빌드가 `dotnet run` 에서는 뜨고 dll 직접 실행에서는 404 였다.

검증: 빌드 경고 0·오류 0 · `dotnet format` 통과 · 테스트 795건 전부 통과 ·
NPC 5,000 에서 `drops link 0 sim 0` · 기록 2회 SHA-256 동일(643,415줄) ·
이벤트/명령 수가 수정 전과 동일(344,928 / 298,487)이라 시뮬레이션 동작은 안 바뀌었다 ·
`dotnet run` · dll 직접 실행 · publish 산출물(다른 cwd) 셋 다 `/dashboard` 200.
`docs/index.html` 의 출력 예시와 경로 FAQ 를 코드에 맞춰 고쳤다.

`LinkSwapTests.Link_Swappable` 의 `Assert.Equal(0, Overruns)` 는 **기존부터 불안정**했다 —
아래 항목에서 따로 고쳤다.

## 2026-07-27 19:50 KST · `LinkSwapTests` 의 벽시계 단언을 p99 로 교체

부하를 걸어 재현했더니 실패하는 링크는 항상 **record** 였고,
1,440틱 중 **딱 한 틱**이 45~103ms 로 튀는데 **p99 는 1.0ms** 였다.
기록 데코레이터가 틱 안에서 파일에 쓰기 때문에 OS 레벨에서 한 번 멈춘 것이고,
NPC 서버의 결함이 아니다. `Assert.Equal(0, Overruns)` 는 max 기준의 <b>단일 표본</b> 단언이라
이런 스톨을 그대로 실패로 만든다.

문서가 정한 예산은 <b>p99 ≤ 20ms</b>(docs/11 §9)이고 `Phase1GateTests`·`Phase5GateTests`·
`BlackoutTests` 도 이미 p99 를 단언한다. `LinkSwapTests` 만 p99 없이 오버런 수만 봤다.
그것을 `P99Ms <= TickBudgetMs` 로 바꾸고 이유를 주석에 남겼다.
docs/15 §5 의 게이트 기준에 성능은 없다 — 이 테스트가 보는 것은 교체 가능성이다.

검증: 같은 부하(NPC 5,000 × 3~4 프로세스)에서 이 테스트 5/5 통과(수정 전 1/3),
**전체 795건도 부하 상태에서 전부 통과**했다. 나머지 5곳의 `Overruns == 0` 단언
(`Phase1Gate`·`Phase5Gate`·`Blackout`·`TickAllocation`·`LinkFault`)은 같은 부하에서
멀쩡했으므로 손대지 않았다.

## 2026-07-27 18:44 KST · 빌드 후 기동 상태 리뷰 — 실행 검증

빌드(경고 0·오류 0) · 마스터데이터 V1~V11 · 테스트 795건 전부 통과. NPC 5,000 부하에서
p99 0.117ms · overruns 0 · bytes/tick 0, 기록 2회의 트레이스 643,415줄이 SHA-256 동일(결정론).
장애 주입 · 시나리오 B/C · 링크 4종 · 대시보드 · Narrate 모두 정상 동작.

실행해 보고 결함 3건을 찾았다(수정은 하지 않음).
(1) NPC 5,000 첫 틱에 명령 **904건 드롭** — `LoopbackGameServerLink` 용량 4,096 < 5,000.
    4,096마리 이하는 드롭 0. Sim 쪽 `CommandRing` 은 65,536 이라 두 버퍼 사이징이 어긋나 있다.
    콘솔 종료 요약에 드롭 수가 없어 대시보드를 열어야만 보인다.
(2) `--scenario` · `--trace` 상대경로가 저장소 루트 기준으로 해석되지 않는다.
    `--masterdata`/`--planstore` 만 `Resolve*()` 로 위로 올라간다. 문서에 적힌 시나리오 명령이
    **미처리 예외로 죽는다**(exit 82, 스택트레이스 노출).
(3) `wwwroot/dashboard.html` 이 빌드 출력에 복사되지 않아 dll 직접 실행·publish 시 `/dashboard` 404.
    (2)와 합쳐 상대경로와 대시보드가 동시에 되는 실행 방법이 없다.

## 2026-07-27 18:20 KST · `docs/index.html` 보강 — 그림·애니메이션·코드 안내 3개 절 추가

14개 절 → 17개 절. 새로 넣은 것은 **4 동작(움직여 보기)** · **5 구현 코드 안내** · **6 코드 분석 가이드**다.
3장에는 시스템 전경도와 의존 계층도를 SVG 로 새로 그렸다. 애니메이션은 SVG 는 SMIL,
HTML 은 자체 타이머이고 사이드바 버튼 하나로 전부 멈춘다(`prefers-reduced-motion` 이면 처음부터 꺼진다).
인터랙티브 4종 — 틱 파이프라인 9단계, 스텝 상태기계, 인지 LOD 스캔(240 NPC·150 상한 재현),
버킷 키 계산기. LOD 데모와 계산기는 `CognitionScheduler.Scan` · `LodBandSet.SliceOf` ·
`BucketKey.ToIndex()` 의 계산을 그대로 옮긴 것이라 실제 코드와 같은 값을 낸다.

5·6장은 코드를 읽고 썼다. 프로젝트별 파일 수·줄 수는 실측(소스 18,392 · 테스트 22,188),
인용한 코드 6개는 저장소에서 그대로 옮겼고, 규칙 ↔ 강제 테스트 대응표 12행은 해당 테스트 파일을
열어 확인했다. 추적 시나리오 5개(명령 송출 · 이벤트 반영 · 재계획 · LLM 생성 · 마스터데이터→코드)와
30분/반나절/하루 읽기 코스를 넣었다.

Chrome 으로 렌더 검증했다 — 콘솔 오류 0, 앵커 깨짐 0, 가로 오버플로 0, 다크/라이트 양쪽 확인.
검증 중 사실관계 3건을 정정했다: `scan/tick 58` 은 NPC 5,000 이 아니라 500 실행의 값,
재계획 대기 구간의 틱 수, `Ids.cs` 의 강타입 ID 개수.

## 2026-07-27 14:09 KST · `docs/index.html` — 누구나 읽는 프로젝트 안내서

아키텍처·용도·빌드·실행을 한 장에 담은 단일 HTML 문서를 `docs/` 에 추가했다.
외부 CDN 의존이 없어 파일을 그대로 열면 되고, 다크/라이트 테마·목차 하이라이트·코드 복사만
자체 스크립트로 붙였다. 14개 절 — 개요, 핵심 아이디어, 아키텍처(4계층·의존 규칙·N1~N8·결정론),
용어 사전, 용도, 빌드, 실행 레시피 7종, LLM 설정, 도구, 테스트, 실측 결과, 문서 지도,
기여 규칙, 트러블슈팅.

문서에 적은 명령은 전부 실제로 돌려 확인했다 — `dotnet build -c Release`(경고 0·오류 0),
마스터데이터 검증(V1~V11 통과), NPC 500 스모크(p99 0.247ms · gen0 0). 붙인 출력은 그 실행 결과다.
수치·판정은 `docs/RnD_Report.md` 와 `docs/measurements/` 를 원본으로 삼았고 새로 추정하지 않았다.

## 2026-07-27 12:40 KST · P5 (W11–12) 전량 구현 — T5-01 ~ T5-20

`docs/15_Phase5_W11-12_TASKS.md` 의 20개 태스크를 전부 구현하고 태스크마다 커밋했다.
골든 회귀(50건 × 3회, 단언 합격률 93.6%) · 결정론 리플레이 · 장애 주입 5종 · 링크 4종 교체 ·
블라인드 평가 자료 40건 · 통계 처리기 · 오써링/비용/버킷 정산 · R&D 보고서 · 최종 게이트 판정.

착수 전 베이스라인 실패 3건(CRLF 체크아웃 2 · 미설정 API 키 1)을 먼저 복구했고,
진행 중 실제 버그 4건을 찾아 고쳤다 — 기록이 실행마다 달라지던 틱 경계 문제,
Null 링크의 이벤트 시퀀스 오용, 티어 라우터가 외부 타임아웃을 흘려보내던 catch 필터,
서술기의 인스턴스 매핑. 틱 경계 수정의 부수 효과로 전체 테스트가 1분 39초 → 57초가 됐다.

게이트는 6/9 통과 · 1 부분 · 2 미달이고 **미달은 전부 "아직 안 돌렸다"** 다 —
블라인드 평가(사람 12명)와 수작성 실측(T1-54 재실행 · T3-18)이 남았다.

마무리로 셋을 더 했다. (1) `docs/15 §1` 이 요구하는 **블라인드 예비 실시**를 LLM 심사원
6명 × 40건으로 돌렸다 — **Q1 정답률 50.8 %(구분 불가)** 로 §6 첫 사분면이다. 사람 자료와
파일을 분리하고 "정식이 아니다" 를 파일 스스로 말하게 했다. (2) "Poe 우선" 을 일회성
선택이 아니라 `appsettings.Llm.json` 의 `preferred` 순서로 고정했다 — 키를 넣으면 다음
회차부터 자동으로 Poe 를 쓴다. (3) 블라인드 자료를 `bundle.md` 한 장으로 묶었다.

LLM 사용 비용은 총 $0.52 (OpenRouter).
