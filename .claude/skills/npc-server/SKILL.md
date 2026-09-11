---
name: npc-server
description: LlmNpcServer 저장소에서 MMO NPC 콘텐츠(마스터데이터·행동 플랜)를 만들고 고친다. 아키타입·액션·POI·아이템·인터럽트 추가, 폴백 플랜 작성, 검증 실패 진단, 게임서버 연동 계획, NPC 행동 진단에 쓴다.
---

# LlmNpcServer 콘텐츠 작업

## 언제 쓰나

- "NPC 에 새 직업/액션/장소를 추가해 줘"
- "이 플랜이 왜 반려됐는지 알려 줘"
- "NPC 1247 이 왜 저기 있는지 설명해 줘"
- "우리 게임서버에 붙일 계획을 세워 줘"
- 마스터데이터 검증이 실패했을 때

## MCP 가 붙어 있으면 **부른다** <span>E-03</span>

호스트에 `npc-server` MCP 가 붙어 있으면 아래 명령 대신 툴을 부른다 — **같은 코어를 부르므로
답이 같고**, 파일을 직접 읽는 것보다 정확하다(파생 정보가 같이 나온다).

| 하려는 일 | 툴 |
|---|---|
| 지금 상태 확인 | `masterdata_validate` · `masterdata_explain` |
| 번호 정하기 | `masterdata_next_code` |
| 새 정의 초안 | `masterdata_scaffold` → (쓰기) `masterdata_apply` |
| 파급 확인 | `masterdata_diff` · `masterdata_regen_check` |
| 플랜 검증·수선·설명 | `plan_validate` · `plan_repair` · `plan_narrate` |
| 버킷 상태 | `bucket_status` |
| 돌고 있는 서버 | `server_status` · `server_metrics` · `server_npc` · `server_npcs` |
| 규칙·문서 찾기 | `docs_search` · 리소스 `npc://context`·`npc://rules`·`npc://codemap` |

**고친 뒤에는 반드시 `masterdata_validate` 를 부른다.** 순서는 아래 "작업 순서" 와 같다.

**쓰기 툴은 보통 없다.** `masterdata_apply`·`server_admin`·`prebake_run` 은 서버를
`--allow-write` 로 띄웠을 때만 목록에 뜬다. 없으면 초안을 내고 **사람에게 적용을 넘긴다**.

## 먼저 읽는다

1. `docs/llm/CONTEXT.md` — 3,000토큰 압축 컨텍스트. **이것부터 읽는다**
2. `CODEMAP.md` — 무엇을 하려면 어디를 여는가
3. 해당 작업의 `docs/llm/RECIPES/*.md`

`src/` 96파일 21,000줄을 훑지 않는다. CODEMAP 에서 줄을 찾아 거기 적힌 파일만 연다.

## 작업 순서

1. **지금 상태를 먼저 본다.** 추측하지 않는다.
   ```
   npc card archetype <id>      # 인구·정원·근무 시간·허용 액션·걸릴 인터럽트·폴백 하루
   npc explain action <id>      # 전제·효과·누가 쓸 수 있는가
   npc timeline archetype <id>  # 24시간 띠
   ```
2. **레시피를 연다.** `docs/llm/RECIPES/` 에 작업별 절차가 있다.
3. **번호가 필요하면 물어본다.** `npc next-code archetypes|items|pois|actions|zones|flags`.
   눈으로 세지 않는다 — 예약 구간이 있고 중복은 조용히 깨진다.
4. **고친다.** JSON 서식(들여쓰기·키 순서)을 보존한다.
   `.vscode/npc.code-snippets` 에 뼈대가 있다.
5. **검증한다.**
   ```
   npc validate --json          # 위반마다 fix_hint 가 붙는다
   ```
6. **파급을 보고한다.**
   ```
   npc diff                     # 무효화 범위 · 프리픽스 · 구조 해시 · 재생성 목록
   npc regen --check            # 파생물이 낡았으면 비0
   ```
7. **사람에게 판정을 넘긴다.** 프리베이크(비용)·`pinned/` 수정·`code` 재배치는 사람이 한다.

## 절대 하지 않는다

- `code`·`bit` 번호 **재배치** — 프리베이크 플랜 2,880개가 통째로 깨진다. 추가는 뒤에만
- 액션·플래그·아키타입을 **C# 에 하드코딩** — `masterdata/` 가 단일 원천이다
- **플레이어가 쓴 문자열**(캐릭터명·채팅·길드명)을 프롬프트에 — 인젝션이다
- **일일 토큰 캡 우회** — 초과 시 T2 → T1 강등 → 거절이 정답이다
- `#pragma warning disable` — `TreatWarningsAsErrors` 다. 고친다
- **생성물 직접 편집** — `prompt/` · `npc_instances.json` · `poi_distances.bin` ·
  `docs/schema/` · `docs/llm/VALIDATION.md`
- **검증을 건너뛴 플랜**을 런타임에 올리기
- 틱 루프에 `await`·LINQ·힙 할당·`lock`·`DateTime` (`CLAUDE.md` §2.1)

## 판정은 도구가 한다

"괜찮아 보인다" 라고 말하지 않는다. 종료 코드로 답한다.

| 명령 | 0 의 뜻 |
|---|---|
| `npc validate` | V1~V13 + 로더 + 참조 무결성 통과 |
| `npc plan validate <파일>` | 검증 4단 전부 통과 |
| `npc regen --check` | 파생물 최신 |
| `.\build.ps1` | 빌드·스타일·테스트·데이터 전부 |

## 모르면 멈춘다

근거가 불충분하거나 문서와 코드가 어긋나면 **임의로 코드에 맞추지 말고 멈추고 보고한다.**
문서와 코드가 어긋나면 둘 중 하나가 버그다.
