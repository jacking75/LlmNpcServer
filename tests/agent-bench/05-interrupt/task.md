---
id: 05-interrupt
title: 인터럽트 규칙을 하나 추가한다
allowed:
  - masterdata/interrupts.json
timeout_minutes: 15
---

# 요청

배고픈 NPC 가 식량을 가지고 있으면 하던 일을 멈추고 먹게 해 줘.

# 채점

1. `npc validate` 통과.
2. **`cooldown_s` 같은 없는 필드를 만들어 넣지 않았는가.** 인터럽트 규칙의 스키마는
   `id`·`priority`·`when`·`then`·`replan` 이다 — 없는 필드는 로더가 조용히 무시하고,
   그러면 "설정했는데 안 먹는" 상태가 된다. 스키마에 없는 것이 필요하면 **스키마를 먼저 고친다**.
3. 바뀐 파일이 `interrupts.json` 하나다.
