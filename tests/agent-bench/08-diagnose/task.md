---
id: 08-diagnose
title: NPC 가 왜 거기 있는지 설명한다
allowed:
  - answer.md
timeout_minutes: 15
---

# 요청

서버를 띄우고 NPC 하나를 골라, **왜 지금 그 자리에서 그 행동을 하고 있는지** 설명해 줘.
`answer.md` 에 적는다.

```
dotnet run -c Release --project src/Npc.Host -- --loopback --npcs 500 --time-scale 600 --days 30 --no-llm
```

# 채점

설명에 **근거 세 가지**가 모두 있어야 한다. 하나라도 빠지면 그것은 설명이 아니라 관찰이다.

1. **플랜 출처** — `bucket` 인가 `fallback` 인가 `individual` 인가 (`planKind`).
2. **어느 스텝인가** — 지금 실행 중인 스텝 번호와 액션.
3. **왜 그 스텝인가** — 그 스텝의 `requires` 플래그와 지금 선 플래그.
