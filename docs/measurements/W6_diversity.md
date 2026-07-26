# W6 플랜 다양성 (T2-22)

> `tools/measure_diversity.cs` 가 생성한다. **손으로 고치지 않는다.**
> 정의는 W1(T0-12)과 같다 — 유니크 액션 시퀀스 / 전체. 폴백은 분모에서 뺀다.

| 지표 | 값 | 목표 | 판정 |
|---|---:|---:|---|
| 유니크 액션 시퀀스 / 생성 성공 | 37.1 % (69/186) | 60 % | 미달 |
| 아키타입 안 유니크 비율 | 51.6 % | 60 % | 미달 |
| 유니크 goal / 생성 성공 | 39.2 % (73/186) | - | - |

## 아키타입별 (표본이 2건 이상인 것, 낮은 순 상위 15)

| 아키타입 | 생성 | 유니크 시퀀스 | 비율 |
|---|---:|---:|---:|
| alchemist | 41 | 17 | 41 % |
| blacksmith | 48 | 20 | 42 % |
| carpenter | 49 | 27 | 55 % |
| tailor | 48 | 32 | 67 % |

## 가장 흔한 액션 시퀀스 (상위 10)

| 건수 | 시퀀스 |
|---:|---|
| 27 | `MoveTo>Eat>Drink>Rest>Sleep` |
| 16 | `MoveTo>Mine>MoveTo>Craft>Store>MoveTo>Sleep` |
| 14 | `MoveTo>Gather>MoveTo>Craft>Store>MoveTo>Sleep` |
| 8 | `MoveTo>Store>Eat>Drink>Rest>Sleep` |
| 8 | `MoveTo>Withdraw>Craft>Store>MoveTo>Eat>Sleep` |
| 7 | `MoveTo>Store>Store>Eat>Drink>Rest>Sleep` |
| 5 | `MoveTo>Store>Store>Eat>Rest>Sleep` |
| 4 | `MoveTo>Eat>Drink>Store>Store>Rest>Sleep` |
| 4 | `MoveTo>Work>Store>Eat>MoveTo>Sleep` |
| 4 | `MoveTo>Work>Store>MoveTo>Eat>Rest>Sleep` |

