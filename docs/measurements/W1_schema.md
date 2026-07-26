# W1 강제 디코딩 검증 (T0-09 · 게이트 G0-1)

> `spike schemacheck` 이 `spike/out/schemacheck.jsonl` 전체에서 다시 만든다.
> 표는 손으로 고치지 않는다. 해석은 맨 아래 "판단" 절에 사람이 쓴다.

- 프리픽스 4409 토큰 · SHA `71383e927e2f6977`
- 서픽스 최대 20 종 (버킷 288 에서 결정론 샘플링, 전부 서로 다름)
- 검증: 검증기 1·2단 (T0-08). 3·4단은 W6 범위.
- **유효 JSON** = 1단(파싱+스키마) 통과. 게이트 G0-1 의 기준.
- **검증통과** = 1·2단 전부 통과. **관용통과** = 액션에 없는 인자를 눈감아 준 것.
- `forced` = `response_format=json_schema`, `prompt` = 형식 지정 없이 프롬프트만.

| 엔진 | 스키마 | 디코딩 | 유효 JSON | 검증통과 | 관용통과 | 평균 ms | out tok/req |
|---|---|---|---|---|---|---|---|
| `gemini-3.1-flash-lite` | bare | forced | **20/20** | 3 | 3 | 3380 | 290 |
| `gemini-3.1-flash-lite` | full | forced | **99/100** | 0 | 94 | 3751 | 727 |
| `gemini-3.1-flash-lite` | full | prompt | **98/100** | 93 | 93 | 2566 | 369 |
| `gemini-3.5-flash-lite` | full | prompt | **94/100** | 88 | 88 | 10897 | 204 |
| `llamacpp-gemma3-4b` | full | prompt | **95/100** | 62 | 62 | 3892 | 237 |
| `llamacpp-qwen3-8b` | full | prompt | **100/100** | 84 | 85 | 5119 | 210 |

### `gemini-3.1-flash-lite` [bare/forced] 실패 분해

| 코드 | 건수 |
|---|---|
| `V2.MISSING_REQUIRED_ARG` | 17 |

예시:

```
blacksmith@Dawn.Peace.Fair: V2.MISSING_REQUIRED_ARG@0 MoveTo.poi
farmer@Morning.War.Cold: V2.MISSING_REQUIRED_ARG@0 Gather.resource
merchant@Afternoon.Peace.Storm: V2.MISSING_REQUIRED_ARG@0 Talk.npc
town_guard@Evening.Disaster.Fair: V2.MISSING_REQUIRED_ARG@1 MoveTo.poi
farmer@Dawn.Alert.Cold: V2.MISSING_REQUIRED_ARG@0 MoveTo.poi
merchant@Morning.Disaster.Storm: V2.MISSING_REQUIRED_ARG@1 Talk.npc
town_guard@Afternoon.War.Fair: V2.MISSING_REQUIRED_ARG@1 MoveTo.poi
merchant@Dawn.War.Storm: V2.MISSING_REQUIRED_ARG@0 MoveTo.poi
```

### `gemini-3.1-flash-lite` [full/forced] 실패 분해

| 코드 | 건수 |
|---|---|
| `V2.UNKNOWN_ARG` | 99 |
| `CALL_FAILED` | 1 |

예시:

```
blacksmith@Dawn.Peace.Fair: V2.UNKNOWN_ARG@0 MoveTo.recipe
farmer@Morning.War.Cold: V2.UNKNOWN_ARG@0 Gather.poi
merchant@Afternoon.Peace.Storm: V2.UNKNOWN_ARG@0 Talk.poi
town_guard@Evening.Disaster.Fair: V2.UNKNOWN_ARG@0 Eat.poi
farmer@Dawn.Alert.Cold: V2.UNKNOWN_ARG@0 MoveTo.recipe
merchant@Morning.Disaster.Storm: V2.UNKNOWN_ARG@0 MoveTo.recipe
town_guard@Afternoon.War.Fair: V2.UNKNOWN_ARG@0 Eat.poi
blacksmith@Night.Peace.Cold: V2.UNKNOWN_ARG@0 Eat.poi
```

### `gemini-3.1-flash-lite` [full/prompt] 실패 분해

| 코드 | 건수 |
|---|---|
| `V2.ACTION_NOT_ALLOWED` | 5 |
| `V1.STEP_COUNT` | 1 |
| `V1.EXTRA_FIELD` | 1 |

문제된 스키마 요소:

| 요소 | 건수 |
|---|---|
| `minItems/maxItems` | 1 |
| `additionalProperties (npc)` | 1 |

예시:

```
farmer@Night.War.Fair: V2.ACTION_NOT_ALLOWED@1 farmer 는 Craft 를 쓸 수 없다
farmer@Dawn.War.Fair: V2.ACTION_NOT_ALLOWED@3 farmer 는 Craft 를 쓸 수 없다
blacksmith@Night.Alert.Fair: V1.STEP_COUNT@-1 2 steps
farmer@Night.War.Cold: V2.ACTION_NOT_ALLOWED@1 farmer 는 Craft 를 쓸 수 없다
merchant@Afternoon.War.Fair: V1.EXTRA_FIELD@0 npc
town_guard@Morning.Alert.Cold: V2.ACTION_NOT_ALLOWED@5 town_guard 는 Work 를 쓸 수 없다
farmer@Noon.Peace.Fair: V2.ACTION_NOT_ALLOWED@1 farmer 는 Craft 를 쓸 수 없다
```

### `gemini-3.5-flash-lite` [full/prompt] 실패 분해

| 코드 | 건수 |
|---|---|
| `V1.STEP_COUNT` | 6 |
| `V2.ACTION_NOT_ALLOWED` | 5 |
| `V2.TYPE_MISMATCH` | 1 |

문제된 스키마 요소:

| 요소 | 건수 |
|---|---|
| `minItems/maxItems` | 6 |

예시:

```
blacksmith@Noon.War.Cold: V2.ACTION_NOT_ALLOWED@3 blacksmith 는 Guard 를 쓸 수 없다
merchant@Night.Disaster.Fair: V1.STEP_COUNT@-1 2 steps
farmer@Night.War.Fair: V1.STEP_COUNT@-1 2 steps
farmer@Dawn.War.Fair: V2.ACTION_NOT_ALLOWED@1 farmer 는 Craft 를 쓸 수 없다
blacksmith@Night.Alert.Fair: V1.STEP_COUNT@-1 2 steps
blacksmith@Morning.War.Fair: V2.ACTION_NOT_ALLOWED@3 blacksmith 는 Guard 를 쓸 수 없다
blacksmith@Night.Alert.Cold: V1.STEP_COUNT@-1 2 steps
farmer@Dawn.War.Cold: V2.ACTION_NOT_ALLOWED@2 farmer 는 Craft 를 쓸 수 없다
```

### `llamacpp-gemma3-4b` [full/prompt] 실패 분해

| 코드 | 건수 |
|---|---|
| `V2.UNKNOWN_ACTION` | 24 |
| `V2.ACTION_NOT_ALLOWED` | 7 |
| `V1.PARSE` | 4 |
| `V1.SCHEMA` | 1 |
| `V2.UNKNOWN_ARG` | 1 |
| `V2.UNKNOWN_ITEM` | 1 |

문제된 스키마 요소:

| 요소 | 건수 |
|---|---|
| `required` | 1 |
| `maximum` | 1 |

예시:

```
merchant@Afternoon.Peace.Storm: V2.UNKNOWN_ACTION@1 Buy
merchant@Morning.Disaster.Storm: V2.UNKNOWN_ACTION@1 Buy
merchant@Dawn.War.Storm: V2.UNKNOWN_ACTION@1 Buy
town_guard@Morning.Peace.Fair: V2.ACTION_NOT_ALLOWED@1 town_guard 는 Work 를 쓸 수 없다
merchant@Night.Disaster.Fair: V2.UNKNOWN_ACTION@1 Buy
farmer@Noon.Disaster.Storm: V2.UNKNOWN_ACTION@3 Idle
merchant@Evening.War.Fair: V2.UNKNOWN_ACTION@2 Buy
merchant@Afternoon.Alert.Fair: V2.UNKNOWN_ACTION@1 Buy
```

### `llamacpp-qwen3-8b` [full/prompt] 실패 분해

| 코드 | 건수 |
|---|---|
| `V2.ACTION_NOT_ALLOWED` | 11 |
| `V2.UNKNOWN_ITEM` | 4 |
| `V2.UNKNOWN_ARG` | 1 |

예시:

```
blacksmith@Afternoon.Disaster.Cold: V2.ACTION_NOT_ALLOWED@4 blacksmith 는 Guard 를 쓸 수 없다
blacksmith@Noon.War.Cold: V2.ACTION_NOT_ALLOWED@4 blacksmith 는 Guard 를 쓸 수 없다
merchant@Night.Disaster.Fair: V2.UNKNOWN_ARG@1 Trade.topic
farmer@Morning.War.Storm: V2.ACTION_NOT_ALLOWED@5 farmer 는 Guard 를 쓸 수 없다
merchant@Dawn.Disaster.Fair: V2.ACTION_NOT_ALLOWED@3 merchant 는 Gather 를 쓸 수 없다
blacksmith@Noon.War.Storm: V2.ACTION_NOT_ALLOWED@0 blacksmith 는 Guard 를 쓸 수 없다
merchant@Night.Disaster.Cold: V2.UNKNOWN_ITEM@1 Trade.item: iron_ore
merchant@Evening.War.Cold: V2.ACTION_NOT_ALLOWED@3 merchant 는 Gather 를 쓸 수 없다
```
