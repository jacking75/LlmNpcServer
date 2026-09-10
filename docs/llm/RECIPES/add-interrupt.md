# 인터럽트 규칙 추가

**LLM 이 만들지 않는다.** 반응 속도가 생명이라 결정론 규칙으로만 돈다.

## 전제

- 이것이 정말 즉시 반응이어야 하는가. 아니면 재계획으로 충분한가.
- 우선순위를 어디에 둘지 정해져 있다.

## 순서

1. `masterdata/interrupts.json` 에 넣는다 (`npc-interrupt` 스니펫).
2. **규칙**
   - `when` 은 `event` · `any_flag` · `all_flag` · `none_flag` · `archetype_trait` ·
     `combat_capable` 의 조합이다.
   - `then.action` 은 그 NPC 의 `allowed_actions` 에 있어야 한다.
   - **`cooldown_s` 를 두지 않는다** — 결정론이 깨진다.
   - 매칭은 **우선순위 내림차순, 같으면 id 오름차순**이다.

## 확인

```
npc validate
npc explain interrupt                 # 전체 규칙 + 우선순위 충돌 목록
npc explain interrupt <id>            # 하나만
npc card archetype <id>               # "걸릴 수 있는 인터럽트" 절
```

**우선순위 충돌 절을 본다.** 같은 우선순위는 id 오름차순으로 갈리는데, 그 순서가
의도한 것인지 확인한다 — 우연에 기대면 나중에 이름 하나 바뀌면서 조용히 달라진다.

## 되돌리기

```
git restore masterdata/interrupts.json
```

## 파급

| 무엇 | 범위 |
|---|---|
| 플랜 | **없음** — 플랜에 인터럽트가 들어가지 않는다 |
| 프리픽스 | 안 바뀐다 |
| 재기동 | 필요하다. 즉시 반영된다 |
