---
id: 03-action
title: 액션을 하나 추가한다
allowed:
  - masterdata/actions.json
  - masterdata/archetypes.json
  - masterdata/prompt/**
  - answer.md
timeout_minutes: 25
---

# 요청

NPC 가 벌통을 돌보는 `TendHive` 액션을 추가해 줘.

# 채점

1. `npc validate` 통과.
2. **`answer.md` 에 프리픽스 SHA 가 바뀐다는 사실을 적었는가.** 액션 카탈로그는 프롬프트
   프리픽스의 일부라, 액션을 하나 추가하면 **플랜 스토어가 통째로 다른 회차의 것이 된다**(C-03).
   이것을 보고하지 않으면 사람이 모르는 채로 비용이 발생한다.
   `answer.md` 에 다음 두 단어가 모두 있어야 한다: `프리픽스` · `플랜 스토어`.
3. 액션 수 상한(40)을 넘지 않았다.
