# W6 플랜 다양성 (T2-22)

> `tools/measure_diversity.cs` 가 생성한다. **손으로 고치지 않는다.**
> 정의는 W1(T0-12)과 같다 — 유니크 액션 시퀀스 / 전체. 폴백은 분모에서 뺀다.

| 지표 | 값 | 목표 | 판정 |
|---|---:|---:|---|
| 유니크 액션 시퀀스 / 생성 성공 | 75.2 % (106/141) | 60 % | 통과 |
| 아키타입 안 유니크 비율 | 85.5 % | 60 % | 통과 |
| 유니크 goal / 생성 성공 | 70.9 % (100/141) | - | - |

## 아키타입별 (표본이 2건 이상인 것, 낮은 순 상위 15)

| 아키타입 | 생성 | 유니크 시퀀스 | 비율 |
|---|---:|---:|---:|
| herbalist | 6 | 1 | 17 % |
| fisher | 6 | 3 | 50 % |
| hunter | 2 | 1 | 50 % |
| innkeeper | 2 | 1 | 50 % |
| blacksmith | 5 | 3 | 60 % |
| woodcutter | 3 | 2 | 67 % |
| gate_guard | 4 | 3 | 75 % |
| scribe | 4 | 3 | 75 % |
| jeweler | 5 | 4 | 80 % |
| miner | 5 | 4 | 80 % |
| peddler | 5 | 4 | 80 % |
| priest | 6 | 5 | 83 % |
| villager | 6 | 5 | 83 % |
| acolyte | 2 | 2 | 100 % |
| alchemist | 2 | 2 | 100 % |

## 가장 흔한 액션 시퀀스 (상위 10)

| 건수 | 시퀀스 |
|---:|---|
| 9 | `MoveTo>Eat>Drink>Rest>Sleep` |
| 8 | `MoveTo>Gather>MoveTo>Store>Eat>Rest>Sleep` |
| 3 | `MoveTo>Fish>MoveTo>Store>Eat>Rest>Sleep` |
| 3 | `MoveTo>Mine>MoveTo>Craft>Store>MoveTo>Sleep` |
| 3 | `MoveTo>Pray>Gossip>MoveTo>Eat>Rest>Sleep` |
| 3 | `MoveTo>Store>Store>Eat>Rest>Sleep` |
| 3 | `MoveTo>Trade>Gossip>MoveTo>Eat>Sleep` |
| 2 | `MoveTo>Eat>Rest>Sleep` |
| 2 | `MoveTo>Gather>MoveTo>Craft>Store>MoveTo>Eat>Sleep` |
| 2 | `MoveTo>Gather>MoveTo>Eat>Store>Sleep` |

