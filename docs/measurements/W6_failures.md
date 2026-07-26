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

## 2. 상위 실패 원인 Top 5 (마지막 차수)

| 순위 | stage | code | 건수 | 비중 | 처방 (docs/12 §8) |
|---:|---|---|---:|---:|---|
| 1 | Coherence | `V3.PRECONDITION_UNMET` | 7 | 11.7 % | requires/grants 관계 미이해. 액션 카탈로그에 플래그 표, few-shot 에 수정 예시. |
| 2 | Vocabulary | `V2.RANGE` | 3 | 5.0 % | 정수 범위 위반. 카탈로그의 range 표기를 강조한다. |
| 3 | Vocabulary | `V2.TYPE_MISMATCH` | 1 | 1.7 % | 인자 타입 불일치. 열거값 목록을 카탈로그에 명시한다. |
| 4 | Coherence | `V3.DEGENERATE` | 1 | 1.7 % | 같은 액션 3연속. few-shot 에 다양한 모양을 넣는다. |
| 5 | DryRun | `V4.INFINITE_LOOP` | 1 | 1.7 % | 사이클이 하루보다 짧다. Sleep until 로 하루를 채우게 한다. |

## 3. 단계별 (마지막 차수)

| stage | 건수 |
|---|---:|
| Coherence | 8 |
| Vocabulary | 4 |
| DryRun | 1 |

## 4. 아키타입별 (마지막 차수, 실패 많은 순 상위 15)

| 아키타입 | 시도 | 통과 | 통과율 | 가장 흔한 실패 |
|---|---:|---:|---:|---|
| healer | 2 | 0 | 0 % | V3.PRECONDITION_UNMET (2) |
| hunter | 1 | 0 | 0 % | V2.RANGE (1) |
| scribe | 2 | 0 | 0 % | V2.RANGE (1) |
| stablemaster | 1 | 0 | 0 % | V2.TYPE_MISMATCH (1) |
| tailor | 1 | 0 | 0 % | V2.RANGE (1) |
| wandering_bard | 1 | 0 | 0 % | V3.PRECONDITION_UNMET (1) |
| alchemist | 2 | 1 | 50 % | V3.PRECONDITION_UNMET (1) |
| brewer | 2 | 1 | 50 % | V3.PRECONDITION_UNMET (1) |
| carpenter | 2 | 1 | 50 % | V3.PRECONDITION_UNMET (1) |
| innkeeper | 2 | 1 | 50 % | V3.DEGENERATE (1) |
| jeweler | 2 | 1 | 50 % | V4.INFINITE_LOOP (1) |
| acolyte | 1 | 1 | 100 % | - |
| baker | 1 | 1 | 100 % | - |
| banker | 2 | 2 | 100 % | - |
| beggar | 1 | 1 | 100 % | - |

## 5. (stage, code, archetype) 3축 (마지막 차수, 상위 20)

| stage | code | 아키타입 | 건수 |
|---|---|---|---:|
| Coherence | `V3.PRECONDITION_UNMET` | healer | 2 |
| Vocabulary | `V2.RANGE` | hunter | 1 |
| Vocabulary | `V2.RANGE` | scribe | 1 |
| Vocabulary | `V2.RANGE` | tailor | 1 |
| Vocabulary | `V2.TYPE_MISMATCH` | stablemaster | 1 |
| Coherence | `V3.DEGENERATE` | innkeeper | 1 |
| Coherence | `V3.PRECONDITION_UNMET` | alchemist | 1 |
| Coherence | `V3.PRECONDITION_UNMET` | brewer | 1 |
| Coherence | `V3.PRECONDITION_UNMET` | carpenter | 1 |
| Coherence | `V3.PRECONDITION_UNMET` | scribe | 1 |
| Coherence | `V3.PRECONDITION_UNMET` | wandering_bard | 1 |
| DryRun | `V4.INFINITE_LOOP` | jeweler | 1 |

## 6. 판단

*여기는 사람이 쓴다. 무엇을 고쳤고 다음 차수에서 무엇을 기대하는가.*

