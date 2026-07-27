# W11 골든 회귀 — 실측

> `dotnet test --filter Category=Golden` 이 만든다. 손으로 고치지 않는다.

| 측정일 | 2026-07-27 KST |
|---|---|
| 엔진 | `openrouter-gemini-2.5-flash-lite` (google/gemini-2.5-flash-lite) |
| 픽스처 | 50건 |
| 회차 | 3회 (3회 중 2회 이상 통과 = 합격) |
| 동시 요청 | 8 |

## 1. 판정

| 항목 | 값 | 기준 | 판정 |
|---|---|---|---|
| 단언 합격률 | 93.6 % (206/220) | ≥ 90 % | 통과 |
| 1회차 단언 통과 | 204/220 (92.7 %) | - | - |
| 1회차 전단언 통과 픽스처 | 37/50 (74.0 %) | - | - |
| 2회차 단언 통과 | 196/220 (89.1 %) | - | - |
| 2회차 전단언 통과 픽스처 | 36/50 (72.0 %) | - | - |
| 3회차 단언 통과 | 201/220 (91.4 %) | - | - |
| 3회차 전단언 통과 픽스처 | 36/50 (72.0 %) | - | - |

## 2. 비용

| 항목 | 값 |
|---|---|
| 호출 수 (재시도 포함) | 216 |
| 입력 토큰 | 3,012,661 |
| 그중 캐시 적중 | 1,223,508 |
| 출력 토큰 | 70,381 |
| 비용 | $0.2193 |
| 평균 지연 | 1774 ms |
| 호출 실패 | 0 |

## 3. 합격하지 못한 단언

```
  G-002 validates(DryRun) (0/3) V2.ACTION_NOT_ALLOWED@step1 'Defend' 은 이 아키타입의 allowed_actions 에 없다.
  G-003 validates(DryRun) (1/3) V3.PRECONDITION_UNMET@step0 Mine requires AtField but no preceding step grants it. Insert a MoveTo step with poi $nearest_field before it. State before this step: HasFood|HasWater|HasTool|HasCoin|IsEvening|RegionUnderAttack|WeatherHarsh.
  G-003 contains_action(Craft) (1/3) 'Craft' 이 없다. 실제: MoveTo>Repair>Store>Eat>MoveTo>Sleep
  G-003 produces_item_of(product) (1/3) 아무것도 만들지 않는다. 실제: MoveTo>Repair>Store>Eat>MoveTo>Sleep
  G-010 validates(DryRun) (1/3) V3.PRECONDITION_UNMET@step3 Craft requires HasRawMaterial but no preceding step grants it. Gather/Mine/Farm/Fish it first, or Withdraw a raw material at your home or workplace. State before this step: AtWorkplace|HasFood|HasWater|HasTool|HasProduct|HasCoin|IsDay|RegionPeaceful.
  G-011 contains_action(Craft) (1/3) 'Craft' 이 없다. 실제: MoveTo>Gather>MoveTo>Work>Store>MoveTo>Sleep
  G-013 validates(DryRun) (1/3) V3.PRECONDITION_UNMET@step3 Craft requires HasRawMaterial but no preceding step grants it. Gather/Mine/Farm/Fish it first, or Withdraw a raw material at your home or workplace. State before this step: AtWorkplace|HasFood|HasWater|HasTool|HasProduct|HasCoin|IsEvening|RegionPeaceful.
  G-025 validates(DryRun) (0/3) V4.DEADLOCK@step0 The plan stops at step 0: MoveTo cannot run: requires None.
  G-029 validates(DryRun) (0/3) V3.PRECONDITION_UNMET@step4 Cook requires HasRawMaterial but no preceding step grants it. Gather/Mine/Farm/Fish it first, or Withdraw a raw material at your home or workplace. State before this step: AtWorkplace|HasFood|HasWater|HasTool|HasProduct|HasCoin|IsEvening|RegionPeaceful.
  G-032 validates(DryRun) (0/3) 컴파일 실패 step1: Observe 의 인자를 해석할 수 없다: 'target' 이 허용된 npc_ref 심볼이 아니다: nearest:horse
  G-032 step_count_between(3..10) (0/3) 컴파일 실패 step1: Observe 의 인자를 해석할 수 없다: 'target' 이 허용된 npc_ref 심볼이 아니다: nearest:horse
  G-032 ends_with_any(Sleep/Rest) (0/3) 컴파일 실패 step1: Observe 의 인자를 해석할 수 없다: 'target' 이 허용된 npc_ref 심볼이 아니다: nearest:horse
  G-033 validates(DryRun) (1/3) V4.INFINITE_LOOP@step-1 loop is true but one cycle takes only 0.9 game hours. A daily routine must fill about 24 hours - end it with Sleep until the next morning.
  G-044 validates(DryRun) (0/3) V2.ACTION_NOT_ALLOWED@step3 'Equip' 은 이 아키타입의 allowed_actions 에 없다.
```

## 4. 단언 종류별 합격률

| kind | 합격 | 전체 | 비율 |
|---|---|---|---|
| acquires_before_use | 3 | 3 | 100.0 % |
| avoids_flag_while | 2 | 2 | 100.0 % |
| contains_action | 20 | 22 | 90.9 % |
| differs_from | 16 | 16 | 100.0 % |
| ends_with_any | 49 | 50 | 98.0 % |
| not_contains | 17 | 17 | 100.0 % |
| produces_item_of | 9 | 10 | 90.0 % |
| step_count_between | 49 | 50 | 98.0 % |
| validates | 41 | 50 | 82.0 % |
