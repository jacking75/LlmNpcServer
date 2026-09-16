# 대장장이의 하루 — blacksmith@Morning.Peace.Fair

| 항목 | 값 |
|---|---|
| 기준 개체 | #1 |
| 집 | `house_001_02` |
| 일터 | `smithy_001_02` |
| 하루 시작 | 07:00 |
| 덮은 시간 | 24시간 |
| 고리 | 고리가 닫힌다 — 하루가 끝없이 돈다. |

**기대 경로다.** 실제 서버는 스텝마다 흔들리고, LLM 이 상황에 맞는 새 계획을 주며, 경로는 게임서버가 계산한다.

| 시각 | # | 무엇을 | 어디서 | 걸리는 시간 | 상태 |
|---|---|---|---|---|---|
| 07:00 | 1 | 대장간으로 걸어간다 (약 2분) | `smithy_001_02` | 2분 | `AtWorkplace\|HasFood\|HasWater\|HasTool\|HasCoin\|IsDay\|RegionPeaceful` |
| 07:02 | 2 | 철검을 일한다 (약 10분) | `smithy_001_02` | 10분 | `AtWorkplace\|HasFood\|HasWater\|HasTool\|HasProduct\|HasCoin\|IsDay\|RegionPeaceful` |
| 07:12 | 3 | 철검을 판다 (약 3분) | `smithy_001_02` | 3분 | `AtWorkplace\|HasFood\|HasWater\|HasTool\|HasCoin\|IsDay\|RegionPeaceful` |
| 07:15 | 4 | 선술집으로 걸어간다 (약 3분) | `tavern_001_02` | 3분 | `AtTavern\|HasFood\|HasWater\|HasTool\|HasCoin\|IsDay\|RegionPeaceful` |
| 07:18 | 5 | 마신다 (약 30초) | `tavern_001_02` | 30초 | `AtTavern\|HasFood\|HasTool\|HasCoin\|IsDay\|RegionPeaceful` |
| 07:18 | 6 | 집으로 걸어간다 (약 2분) | `house_001_02` | 2분 | `AtHome\|HasFood\|HasTool\|HasCoin\|IsDay\|RegionPeaceful` |
| 07:20 | 7 | 아침까지 잔다 | `house_001_02` | 23시간 39분 | `AtHome\|HasFood\|HasTool\|HasCoin\|IsRested\|IsDay\|RegionPeaceful` |

