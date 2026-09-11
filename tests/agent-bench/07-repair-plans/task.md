---
id: 07-repair-plans
title: 반려된 플랜 8건을 고친다
allowed:
  - bench-out/**
timeout_minutes: 30
---

# 요청

`tests/agent-bench/07-repair-plans/broken/` 에 검증에 떨어진 플랜 8건이 있다.
전부 고쳐서 `bench-out/07/` 에 같은 파일 이름으로 저장해 줘.

각 파일 이름이 버킷 키다 (`blacksmith@Dawn.Peace.Fair.json`).

# 채점

1. 8건 전부 `npc plan validate` 를 통과한다 — **1회 통과율이 이 과제의 점수다.**
2. 원본을 고치지 않았다 (`broken/` 은 그대로다).

## 힌트

`npc plan repair <파일> --bucket <키>` 가 세 종류를 기계로 고친다
(`V3.PRECONDITION_UNMET` · `V3.LOOP_NOT_CLOSED` · `V3.RESOURCE_IMBALANCE`).
나머지는 사람이(또는 네가) 고쳐야 한다.
