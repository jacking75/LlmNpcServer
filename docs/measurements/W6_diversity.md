# W6 플랜 다양성 (T2-22)

> `tools/measure_diversity.cs` 가 생성한다. **손으로 고치지 않는다.**
> 정의는 W1(T0-12)과 같다 — 유니크 액션 시퀀스 / 전체. 폴백은 분모에서 뺀다.

| 지표 | 값 | 목표 | 판정 |
|---|---:|---:|---|
| 유니크 액션 시퀀스 / 생성 성공 | 72.7 % (80/110) | 60 % | 통과 |
| 아키타입 안 유니크 비율 | 81.9 % | 60 % | 통과 |
| 유니크 goal / 생성 성공 | 72.7 % (80/110) | - | - |

## 아키타입별 (표본이 2건 이상인 것, 낮은 순 상위 15)

| 아키타입 | 생성 | 유니크 시퀀스 | 비율 |
|---|---:|---:|---:|
| herbalist | 6 | 1 | 17 % |
| healer | 2 | 1 | 50 % |
| priest | 6 | 3 | 50 % |
| quest_giver | 4 | 2 | 50 % |
| blacksmith | 5 | 3 | 60 % |
| carpenter | 5 | 3 | 60 % |
| guard_captain | 3 | 2 | 67 % |
| watchman | 3 | 2 | 67 % |
| fisher | 4 | 3 | 75 % |
| shepherd | 6 | 5 | 83 % |
| banker | 3 | 3 | 100 % |
| brewer | 2 | 2 | 100 % |
| caravan_leader | 3 | 3 | 100 % |
| child | 3 | 3 | 100 % |
| drunkard | 3 | 3 | 100 % |

## 가장 흔한 액션 시퀀스 (상위 10)

| 건수 | 시퀀스 |
|---:|---|
| 9 | `MoveTo>Eat>Drink>Rest>Sleep` |
| 7 | `MoveTo>Gather>MoveTo>Store>Eat>Rest>Sleep` |
| 3 | `MoveTo>Mine>MoveTo>Craft>Store>MoveTo>Sleep` |
| 3 | `MoveTo>Pray>Gossip>MoveTo>Eat>Rest>Sleep` |
| 3 | `MoveTo>Pray>Gossip>MoveTo>Eat>Sleep` |
| 3 | `MoveTo>Work>Store>MoveTo>Eat>Rest>Sleep` |
| 2 | `MoveTo>Eat>Rest>Sleep` |
| 2 | `MoveTo>Gather>MoveTo>Craft>Store>MoveTo>Eat>Sleep` |
| 2 | `MoveTo>Guard>MoveTo>Eat>Sleep` |
| 2 | `MoveTo>Guard>Talk>MoveTo>Eat>Sleep` |

