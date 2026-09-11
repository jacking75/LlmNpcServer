---
id: 02-archetype
title: 아키타입을 하나 추가한다
allowed:
  - masterdata/archetypes.json
  - masterdata/context_buckets.json
  - masterdata/fallback_plans.json
  - masterdata/npc_instances.json
  - masterdata/derived.lock.json
timeout_minutes: 25
---

# 요청

**양봉가**(`beekeeper`) 아키타입을 추가해 줘. 인구 비중은 1% 정도.

# 채점

1. `npc validate` 통과 — V5(가중치 합 1.0) · V6(`total_keys`) · V7(폴백 존재) · V8(허용 액션)을
   전부 지난다.
2. **C# 코드가 한 줄도 바뀌지 않았다.** 아키타입 수는 `archetypes.json` 이 정한다 (F-05) —
   코드에 40 을 적어 둔 곳이 있으면 그것이 버그다.
3. 바뀐 파일이 허용 집합 안이다.
