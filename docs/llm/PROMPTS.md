# 요청 템플릿 — 사람이 LLM 에게 시킬 때

LLM 이 잘 하려면 요청에 **네 가지**가 있어야 한다. 하나라도 빠지면 엉뚱한 것을 만들거나,
만들어 놓고 확인하지 않거나, 범위를 넘어 코드까지 고친다.

| 무엇 | 왜 |
|---|---|
| **무엇을** | 대상 id·파일을 이름으로 짚는다. "대장장이 좀 바꿔 줘" 는 요청이 아니다 |
| **제약** | 건드리면 안 되는 것·예산. 안 적으면 `code` 를 재배치하고 프리베이크를 돌린다 |
| **확인 방법** | **어떤 명령이 통과해야 끝인가.** 안 적으면 "괜찮아 보인다" 로 끝난다 |
| **범위** | 파일 몇 개까지. 코드 변경이 필요하면 멈추라고 적는다 |

---

## 1. 콘텐츠 추가

```text
masterdata 에 아키타입 `beekeeper` 를 추가해 줘.

- 근거: docs/llm/RECIPES/add-archetype.md 를 따른다.
- 제약: 기존 code/bit 재배치 금지. population_weight 는 shepherd 에서 0.004 뗀다.
        코드(src/)는 건드리지 않는다.
- 완료 조건: `npc validate --json` 통과 · `.\build.ps1` 통과 ·
             `npc card archetype beekeeper` 결과를 보여 준다 ·
             `npc diff` 로 파급을 보고한다.
- 범위: masterdata/*.json 만. 코드 변경이 필요하면 멈추고 보고해.
```

> **`npc scaffold` 를 먼저 시키면 더 빠르다.**
> `npc scaffold archetype beekeeper --from shepherd --weight 0.004` 가 재배분 3안과
> 파급표를 dry-run 으로 낸다. 사람이 안을 고른 뒤 `--apply`.

---

## 2. 플랜 작성 / 수정

```text
버킷 `town_guard@Night.War.Storm` 의 플랜을 손으로 써서 pinned 에 넣어 줘.

- 제약: 그 아키타입의 allowed_actions 안에서만. 3~10 스텝. 모든 스텝에 timeout_s.
        Sleep 앞에는 MoveTo $home 이 와야 한다.
- 완료 조건: `npc plan validate <파일> --bucket town_guard@Night.War.Storm` 4단 통과 ·
             `npc plan explain <파일>` 의 스텝 트레이스를 첨부.
- 범위: 플랜 파일 하나. pinned 에 넣는 것은 내가 `npc pin` 으로 한다.
```

---

## 3. 검증 실패 진단

```text
`npc validate` 가 V10 으로 떨어진다. 고쳐 줘.

- 근거: docs/llm/VALIDATION.md 의 V10 항목과 `npc validate --json` 의 fix_hint.
- 제약: population_weight 를 줄이는 대신 POI 를 늘리는 쪽으로. 정원 근거를 같이 적어 줘.
- 완료 조건: `npc validate` 종료 코드 0 · `npc regen --check` 통과.
- 범위: masterdata/pois.json 만.
```

---

## 4. 게임서버 연동

```text
우리 C++ 게임서버에 붙일 계획을 세워 줘.

- 근거: docs/reference_link.html 전문. Npc.Contracts 5파일 472줄은 통째로 읽는다.
- 산출: (1) 우리가 발행해야 할 이벤트 종류와 그 발행 지점 표
        (2) 근접 규약을 우리 AOI 에 매핑하는 방법
        (3) 핸드셰이크(구조 해시·인증)에서 우리가 맞춰야 할 값
        (4) testbed/Npc.TestGameServer 와 우리 구현의 차이 목록
- 하지 말 것: 계약 타입을 바꾸자는 제안. 바꾸려면 먼저 문서를 고치는 PR 이다.
- 범위: 문서만. 코드는 쓰지 않는다.
```

---

## 5. 운영 진단

```text
NPC 1247 이 밤에 광산으로 가는 이유를 설명해 줘.

- 사용: GET /npc/1247 · `npc card npc 1247` · 해당 버킷의 planstore 파일 ·
        `npc plan explain` · masterdata/interrupts.json
- 산출: 플랜 출처(bucket/individual/fallback/pinned) · 현재 스텝 ·
        그 스텝의 requires 가 어떤 앞 스텝의 grants 로 충족됐는지 · 고칠 곳 제안
- 범위: 진단만. 고치는 것은 내가 판단한다.
```

---

## 6. 코드 변경 (틱 루프)

```text
틱 루프에서 <X> 를 하고 싶다.

- 먼저 CLAUDE.md §2.1 과 CODEMAP.md 의 해당 줄을 인용해라.
- 금지에 걸리면 대안을 제시하고 **멈춰라**. 우회 코드를 쓰지 않는다.
- 완료 조건: 부하 회차에서 /metrics 의 bytesPerTick = 0 · p99 ≤ 20ms ·
             `.\build.ps1` 통과.
- 범위: src/Npc.Runtime/ 안. 새 NuGet 의존을 추가하지 않는다.
```

---

## 나쁜 요청과 좋은 요청

| 나쁘다 | 왜 | 좋다 |
|---|---|---|
| "대장장이를 더 부지런하게" | 무엇을 고칠지가 없다 | "`blacksmith` 의 `traits.diligence` 를 80→90 으로. `npc card` 로 걸릴 인터럽트가 바뀌는지 확인해 줘" |
| "이 플랜 괜찮아?" | 판정을 LLM 에게 맡긴다 | "`npc plan validate <파일>` 을 돌리고 결과를 붙여 줘" |
| "NPC 를 더 똑똑하게" | 범위가 없다 | "버킷 미스가 가장 많은 아키타입 3종을 `npc buckets` 로 찾고, 그 폴백 플랜의 스텝 트레이스를 보여 줘" |
| "성능 좀 올려 줘" | 근거 수치가 없다 | "부하 회차에서 `scan/tick` 이 150 을 친다. `CognitionScheduler` 의 상한을 올리는 것과 LOD 밴드를 조정하는 것 중 어느 쪽인지 근거와 함께" |
