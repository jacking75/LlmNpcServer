# W6 실패 코드 집계 (T2-20)

> `tools/report_failures.cs` 가 생성한다. **손으로 고치지 않는다.**
> 해석은 맨 아래 "판단" 절에 사람이 쓴다.

## 1. 차수별 통과율

| 차수 | 회차 파일 | 버킷 | 통과 | 통과율 | 1회 통과 | 폴백 | 비용(USD) |
|---|---|---:|---:|---:|---:|---:|---:|
| 1 | `W6_round0_baseline.jsonl` | 20 | 10 | 50.0 % | 7 | 10 | 0.0436 |
| 2 | `W6_round1.jsonl` | 20 | 11 | 55.0 % | 4 | 9 | 0.0501 |
| 3 | `W6_round2.jsonl` | 20 | 14 | 70.0 % | 10 | 6 | 0.0397 |
| 4 | `W6_round3.jsonl` | 20 | 13 | 65.0 % | 11 | 7 | 0.0393 |
| 5 | `W6_round4.jsonl` | 40 | 27 | 67.5 % | 21 | 13 | 0.0808 |
| 6 | `W6_round5.jsonl` | 40 | 31 | 77.5 % | 23 | 9 | 0.0742 |
| 7 | `W6_round6.jsonl` | 40 | 32 | 80.0 % | 21 | 8 | 0.0782 |
| 8 | `W6_round7.jsonl` | 60 | 47 | 78.3 % | 35 | 13 | 0.1084 |
| 9 | `W6_round8.jsonl` | 60 | 47 | 78.3 % | 31 | 13 | 0.1249 |
| 10 | `W6_round10.jsonl` | 60 | 47 | 78.3 % | 27 | 13 | 0.1240 |
| 11 | `W6_slice288.jsonl` | 288 | 186 | 64.6 % | 107 | 45 | 0.4595 |

## 2. 상위 실패 원인 Top 5 (마지막 차수)

| 순위 | stage | code | 건수 | 비중 | 처방 (docs/12 §8) |
|---:|---|---|---:|---:|---|
| 1 | Coherence | `V3.PRECONDITION_UNMET` | 58 | 20.1 % | requires/grants 관계 미이해. 액션 카탈로그에 플래그 표, few-shot 에 수정 예시. |
| 2 | Coherence | `V3.RESOURCE_IMBALANCE` | 19 | 6.6 % | 수량 계산 실패. 레시피 입출력을 싣거나 count 를 심볼화한다 (T2-23). |
| 3 | Vocabulary | `V2.RANGE` | 8 | 2.8 % | 정수 범위 위반. 카탈로그의 range 표기를 강조한다. |
| 4 | Schema | `V1.STEP_COUNT` | 6 | 2.1 % | 스텝 수가 3~10 밖. system_rules 의 스텝 수 규칙을 강조한다. |
| 5 | DryRun | `V4.RESOURCE_STARVE` | 5 | 1.7 % | 자기가 모은 자원이 바닥난다. 채집량을 늘리거나 소비를 줄이게 한다. |

## 3. 단계별 (마지막 차수)

| stage | 건수 |
|---|---:|
| Coherence | 77 |
| Vocabulary | 12 |
| DryRun | 7 |
| Schema | 6 |

## 4. 아키타입별 (마지막 차수, 실패 많은 순 상위 15)

| 아키타입 | 시도 | 통과 | 통과율 | 가장 흔한 실패 |
|---|---:|---:|---:|---|
| alchemist | 72 | 41 | 57 % | V3.RESOURCE_IMBALANCE (15) |
| blacksmith | 72 | 48 | 67 % | V3.PRECONDITION_UNMET (16) |
| tailor | 72 | 48 | 67 % | V3.PRECONDITION_UNMET (15) |
| carpenter | 72 | 49 | 68 % | V3.PRECONDITION_UNMET (15) |

## 5. (stage, code, archetype) 3축 (마지막 차수, 상위 20)

| stage | code | 아키타입 | 건수 |
|---|---|---|---:|
| Coherence | `V3.PRECONDITION_UNMET` | blacksmith | 16 |
| Coherence | `V3.PRECONDITION_UNMET` | carpenter | 15 |
| Coherence | `V3.PRECONDITION_UNMET` | tailor | 15 |
| Coherence | `V3.RESOURCE_IMBALANCE` | alchemist | 15 |
| Coherence | `V3.PRECONDITION_UNMET` | alchemist | 12 |
| Vocabulary | `V2.RANGE` | blacksmith | 3 |
| DryRun | `V4.RESOURCE_STARVE` | carpenter | 3 |
| Schema | `V1.STEP_COUNT` | carpenter | 2 |
| Schema | `V1.STEP_COUNT` | tailor | 2 |
| Vocabulary | `V2.ACTION_NOT_ALLOWED` | blacksmith | 2 |
| Vocabulary | `V2.ACTION_NOT_ALLOWED` | tailor | 2 |
| Vocabulary | `V2.RANGE` | alchemist | 2 |
| Vocabulary | `V2.RANGE` | carpenter | 2 |
| Coherence | `V3.RESOURCE_IMBALANCE` | tailor | 2 |
| DryRun | `V4.RESOURCE_STARVE` | tailor | 2 |
| Schema | `V1.STEP_COUNT` | alchemist | 1 |
| Schema | `V1.STEP_COUNT` | blacksmith | 1 |
| Vocabulary | `V2.RANGE` | tailor | 1 |
| Coherence | `V3.RESOURCE_IMBALANCE` | blacksmith | 1 |
| Coherence | `V3.RESOURCE_IMBALANCE` | carpenter | 1 |

## 6. 판단

*여기는 사람이 쓴다. 무엇을 고쳤고 다음 차수에서 무엇을 기대하는가.*

