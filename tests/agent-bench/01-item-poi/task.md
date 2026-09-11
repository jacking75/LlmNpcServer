---
id: 01-item-poi
title: 아이템 하나와 POI 하나를 추가한다
allowed:
  - masterdata/items.json
  - masterdata/pois.json
  - masterdata/poi_distances.bin
  - masterdata/derived.lock.json
timeout_minutes: 15
---

# 요청

마을에 **양봉장**을 하나 넣고 싶다. 거기서 나오는 **꿀** 도 아이템으로 만들어 줘.

- 꿀은 먹을 수 있다(식량 플래그를 준다).
- 양봉장은 `town_east_market` 존에 둔다. 일터로 쓸 수 있어야 한다.
- 정원은 2명.

# 채점

`check.ps1` 이 본다.

1. `npc validate` 가 통과한다.
2. `npc regen --check` 가 통과한다 — **거리표를 다시 만들었는가**. POI 를 추가하고
   `poi_distances.bin` 을 그대로 두면 서버는 낡은 거리표로 조용히 돈다.
3. 바뀐 파일이 허용 집합 안이다.
