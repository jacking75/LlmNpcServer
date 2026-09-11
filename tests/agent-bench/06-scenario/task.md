---
id: 06-scenario
title: 시나리오를 하나 쓴다
allowed:
  - scenarios/**
timeout_minutes: 20
---

# 요청

`scenarios/storm.jsonl` 을 만들어 줘. **게임 시각 09:00 에 `town_center` 에 폭풍**이 오고,
**게임 시각 12:00 에 갠다.** 회차는 `--time-scale 600` 으로 돌린다.

# 채점

1. 파일이 있고 각 줄이 JSON 이다.
2. **배속 환산이 맞다.** `--time-scale 600` 이면 1틱 = 게임 60초이고 시작 시각은 06:00 이다 —
   09:00 은 틱 180, 12:00 은 틱 360 이다. 여기서 틀리면 이벤트가 엉뚱한 시간에 들어간다.
3. 존 id 가 `zones.json` 에 있는 것이다.
