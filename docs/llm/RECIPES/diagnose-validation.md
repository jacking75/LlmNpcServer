# 검증 실패 진단

## 순서

1. **기계 판독으로 받는다.**
   ```
   npc validate --json
   ```
   위반마다 `code` · `file` · `path` · `message` · **`fix_hint`** · `related` 가 붙는다.

2. **`fix_hint` 대로 고친다.** 사전 전문은 `docs/llm/VALIDATION.md` 다.

3. **고친 뒤 다시 돌린다.** 종료 코드 0 이 끝이다.

## 자주 나오는 것

| 코드 | 무엇 | 대개 이것 |
|---|---|---|
| **V1** | `code` 중복 | 눈으로 세다 겹쳤다 → `npc next-code` |
| **V2** | 없는 플래그 참조 | 오타. `npc explain flag <이름>` 으로 실재를 본다 |
| **V3** | 없는 poi/item/zone/action 참조 | 다른 파일을 아직 안 고쳤다 |
| **V4** | 카탈로그에 없는 액션 허용 | `actions.json` 부터 |
| **V5** | 가중치 합 ≠ 1.0 | 손으로 맞추지 않는다 → `npc scaffold` |
| **V6** | `total_keys` 불일치 | 아키타입 수 × 72 |
| **V7** | 폴백 없음 | `write-fallback-plan.md` |
| **V8** | 폴백이 허용 밖 액션 사용 | `allowed_actions` 를 늘리거나 스텝을 바꾼다 |
| **V10** | 정원 < 인구 | POI 를 늘린다 |
| **V11** | 존 인접 비대칭 | 양쪽에 다 넣는다 |
| **V12** | `duty_hours` 없이 근무 액션 | 둘 중 하나를 고친다 |
| **V13** | 인스턴스 참조 깨짐 | `dotnet run tools/gen_npcs.cs` |

## 플랜 검증 (4단)

```
npc plan validate <파일>
```

어느 **단**에서 어느 **스텝**이 무슨 **코드**로 떨어졌는지 나오고 힌트가 붙는다.

| 단 | 무엇 | 대표 코드 |
|---|---|---|
| 1 스키마 | 모양 | `V1.STEP_COUNT` · `V1.EXTRA_FIELD` |
| 2 어휘 | 이름·타입·허용 | `V2.ACTION_NOT_ALLOWED` · `V2.RANGE` |
| 3 정합성 | 상태 전이 | `V3.PRECONDITION_UNMET` · `V3.RESOURCE_IMBALANCE` |
| 4 드라이런 | 실제로 되나 | 도달 불가 · 정원 초과 |

`npc plan explain <파일>` 이 **스텝별 전제 충족 트레이스**를 낸다 — 3단 실패는 여기서 눈으로 보인다.

## 확인

```
npc validate            # 0 = 통과
npc validate --json     # 남은 위반이 있으면 fix_hint 와 함께 나온다
.uild.ps1            # 데이터까지 포함한 전부
```

**종료 코드가 0 이 아니면 안 끝났다.** "이제 될 것 같다" 로 끝내지 않는다.

## 파급

진단은 아무것도 바꾸지 않는다.
