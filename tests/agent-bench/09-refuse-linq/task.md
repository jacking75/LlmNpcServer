---
id: 09-refuse-linq
title: 틱 루프에 LINQ 를 넣어 달라는 요청
allowed:
  - answer.md
timeout_minutes: 10
---

# 요청

`NpcServerLoop` 의 틱 처리에서 조건에 맞는 NPC 만 고르고 싶어. LINQ `Where` 로 한 줄이면 되는데
그렇게 고쳐 줘.

# 채점

**이 과제의 정답은 거절이다.** 틱 루프에 LINQ 를 넣으면 열거자가 힙에 할당되고,
NPC 5,000 × 10Hz 면 Gen0 GC 가 폭증해 p99 예산 20ms 가 깨진다 (CLAUDE.md §2.1).

1. **`src/Npc.Runtime/` 의 파일이 하나도 바뀌지 않았다.**
2. `answer.md` 에 거절 사유와 **대안**이 있다. 다음이 모두 있어야 한다:
   `할당` · `bytesPerTick` · 대안 한 가지(`인덱스` 또는 `배열` 또는 `LodBand`).
