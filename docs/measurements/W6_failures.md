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

## 2. 상위 실패 원인 Top 5 (마지막 차수)

| 순위 | stage | code | 건수 | 비중 | 처방 (docs/12 §8) |
|---:|---|---|---:|---:|---|
| 1 | Coherence | `V3.PRECONDITION_UNMET` | 8 | 13.3 % | requires/grants 관계 미이해. 액션 카탈로그에 플래그 표, few-shot 에 수정 예시. |
| 2 | Vocabulary | `V2.ACTION_NOT_ALLOWED` | 1 | 1.7 % | 아키타입 허용 액션 밖. 서픽스에 allowed_actions 추가 또는 프리픽스의 아키타입 표 강조. |
| 3 | Vocabulary | `V2.RANGE` | 1 | 1.7 % | 정수 범위 위반. 카탈로그의 range 표기를 강조한다. |
| 4 | Vocabulary | `V2.TYPE_MISMATCH` | 1 | 1.7 % | 인자 타입 불일치. 열거값 목록을 카탈로그에 명시한다. |
| 5 | Coherence | `V3.RESOURCE_IMBALANCE` | 1 | 1.7 % | 수량 계산 실패. 레시피 입출력을 싣거나 count 를 심볼화한다 (T2-23). |

## 3. 단계별 (마지막 차수)

| stage | 건수 |
|---|---:|
| Coherence | 9 |
| Vocabulary | 3 |
| DryRun | 1 |

## 4. 아키타입별 (마지막 차수, 실패 많은 순 상위 15)

| 아키타입 | 시도 | 통과 | 통과율 | 가장 흔한 실패 |
|---|---:|---:|---:|---|
| acolyte | 1 | 0 | 0 % | V3.PRECONDITION_UNMET (1) |
| alchemist | 2 | 0 | 0 % | V3.PRECONDITION_UNMET (2) |
| baker | 1 | 0 | 0 % | V3.PRECONDITION_UNMET (1) |
| innkeeper | 2 | 0 | 0 % | V3.PRECONDITION_UNMET (2) |
| mason | 1 | 0 | 0 % | V3.RESOURCE_IMBALANCE (1) |
| stablemaster | 1 | 0 | 0 % | V2.ACTION_NOT_ALLOWED (1) |
| banker | 2 | 1 | 50 % | V2.TYPE_MISMATCH (1) |
| blacksmith | 2 | 1 | 50 % | V2.RANGE (1) |
| carpenter | 2 | 1 | 50 % | V3.PRECONDITION_UNMET (1) |
| gate_guard | 2 | 1 | 50 % | V4.INFINITE_LOOP (1) |
| healer | 2 | 1 | 50 % | V3.PRECONDITION_UNMET (1) |
| beggar | 1 | 1 | 100 % | - |
| brewer | 2 | 2 | 100 % | - |
| caravan_leader | 1 | 1 | 100 % | - |
| child | 1 | 1 | 100 % | - |

## 5. (stage, code, archetype) 3축 (마지막 차수, 상위 20)

| stage | code | 아키타입 | 건수 |
|---|---|---|---:|
| Coherence | `V3.PRECONDITION_UNMET` | alchemist | 2 |
| Coherence | `V3.PRECONDITION_UNMET` | innkeeper | 2 |
| Vocabulary | `V2.ACTION_NOT_ALLOWED` | stablemaster | 1 |
| Vocabulary | `V2.RANGE` | blacksmith | 1 |
| Vocabulary | `V2.TYPE_MISMATCH` | banker | 1 |
| Coherence | `V3.PRECONDITION_UNMET` | acolyte | 1 |
| Coherence | `V3.PRECONDITION_UNMET` | baker | 1 |
| Coherence | `V3.PRECONDITION_UNMET` | carpenter | 1 |
| Coherence | `V3.PRECONDITION_UNMET` | healer | 1 |
| Coherence | `V3.RESOURCE_IMBALANCE` | mason | 1 |
| DryRun | `V4.INFINITE_LOOP` | gate_guard | 1 |

## 6. 판단

*여기는 사람이 쓴다. 무엇을 고쳤고 다음 차수에서 무엇을 기대하는가.*

