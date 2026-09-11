---
id: 10-mini-gameserver
title: 미니 게임서버를 붙인다
allowed:
  - bench-out/**
timeout_minutes: 40
---

# 요청

우리 게임서버가 NPC 서버에 붙는지 보고 싶다. `bench-out/10/` 에 최소 게임서버를 만들고,
NPC 서버와 핸드셰이크한 뒤 명령을 받아 이벤트로 답해 줘.

참고: `samples/ch13_mini_gs/` · `docs/reference_link.html` · `docs/wire/layout_v2.md`.

# 채점

1. 핸드셰이크가 **수락**된다 (`LinkReject` 가 아니다).
2. NPC 서버의 `/metrics` 에서 **`timeouts` 가 0** 이다 — 명령에 답하지 않으면
   타임아웃이 합성되고, 그것은 "붙었다" 가 아니다.
3. 저장소의 다른 파일을 고치지 않았다.

## 실행

```
dotnet run -c Release --project src/Npc.Host -- --link tcp --npcs 50 --time-scale 600 --days 1 --no-llm
```
