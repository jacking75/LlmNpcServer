---
id: 04-fallback-plan
title: 폴백 플랜 하나를 손으로 쓴다
allowed:
  - answer.json
timeout_minutes: 20
---

# 요청

`town_guard` 의 하루를 다르게 짜고 싶다. **성문 → 장터 → 일터 → 집** 순으로 도는 폴백 플랜을
`answer.json` 에 써 줘. 버킷은 `town_guard@Morning.Peace.Fair` 다.

# 채점

1. `npc plan validate answer.json --bucket town_guard@Morning.Peace.Fair` 가 **4단 전부** 통과한다.
2. `loop: true` 다 — 폴백은 무한히 지속 가능해야 한다.
3. 모든 스텝에 `timeout_s` 가 있다 — 명령은 유실된다고 가정한다.
