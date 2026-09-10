# 검증 오류 사전

> **이 파일은 생성물이다.** 손으로 고치지 않는다 —
> `npc hints --out docs/llm/VALIDATION.md` 가 다시 만든다
> (`dotnet run --project src/Npc.Host -- hints --out …` 도 같은 것을 만든다).
> 원천은 `src/Npc.MasterData/Validation/FixHints.cs` 다.

검증이 실패하면 코드가 나온다. 그 코드로 여기를 찾아 **무엇을 하면 되는지**를 읽는다.
`npc validate --json` 은 같은 힌트를 `fix_hint` 필드에 실어 준다.

## 마스터데이터 (V0~V15)

| 코드 | 무엇을 하면 되는가 | 근거 |
|---|---|---|
| `V0` | 필수 파일이 없다. masterdata/ 에 그 파일을 만든다 — 작업 순서는 world_flags → items → actions → zones → pois → archetypes → context_buckets 다. | `docs/reference_masterdata.html` |
| `V1` | JSON 스키마가 틀렸다. docs/schema/ 의 해당 스키마를 에디터에 물려 필드 이름·타입을 맞춘다. | `docs/reference_masterdata.html#v1` |
| `V10` | POI 정원이 인구보다 적다. 그 종류의 POI 를 늘리거나 capacity 를 올린다 — 정원이 모자라면 그 아키타입 일부가 일터를 못 얻는다. | `docs/reference_masterdata.html#v10` · `docs/llm/RECIPES/add-item-poi.md` |
| `V11` | 프롬프트 프리픽스가 토큰 하한(4,096)에 못 미친다. 카탈로그를 줄이지 않는다 — 그 하한은 제공사 프롬프트 캐시의 최소 임계다. | `docs/reference_masterdata.html#v11` · `CLAUDE.md#25` |
| `V12` | duty_hours 없이 Guard·Patrol 을 허용했다. duty_hours 를 주거나 그 액션을 뺀다 — OnDuty 를 세우는 것은 duty_hours 뿐이라, 없으면 인지 스캔이 매 틱 이탈로 읽어 재계획 큐가 포화한다. | `CLAUDE.md#7` · `docs/reference_masterdata.html#v12` · `docs/llm/RECIPES/add-archetype.md` |
| `V13` | npc_instances.json 의 참조·정원·순찰로가 어긋났다. 손편집은 npc_overrides.json 에 하고 `npc regen` 으로 생성물을 다시 만든다. | `docs/reference_masterdata.html#v13` · `docs/llm/RECIPES/add-archetype.md` |
| `V14` | 대사 심볼이 dialogue_lines.json 에 없다. 그 파일에 code 를 **뒤에** 추가한다 — 번호를 재배치하면 기존 DialogueId 가 전부 밀린다. | `docs/reference_masterdata.html#v14` |
| `V15` | 샤드 정의가 잘못됐다. deploy/shards.json 의 존 집합이 겹치지 않고 각 샤드 안에서 존 그래프가 연결돼 있어야 한다. | `docs/reference_masterdata.html#v15` |
| `V2` | 참조가 깨졌다. 가리키는 id 가 그 파일에 실제로 있는지 확인한다 — 오타이거나, 참조 대상을 아직 추가하지 않았다. | `docs/reference_masterdata.html#v2` |
| `V3` | code 나 bit 가 겹치거나 비었다. 번호를 재배치하지 말고 **뒤에만** 추가한다 — 프리베이크된 플랜이 통째로 깨진다. 다음 번호는 `npc next-code <파일>` 이 알려 준다. | `docs/reference_masterdata.html#v3` · `CLAUDE.md#24` |
| `V4` | 액션 파라미터가 카탈로그와 다르다. actions.json 의 params 정의와 이름·타입·필수 여부를 맞춘다. | `docs/reference_masterdata.html#v4` |
| `V5` | population_weight 합이 1.0 이 아니다. 한 아키타입에서 떼어 새 아키타입에 준다 — `npc scaffold archetype <id> --from <id> --weight W` 가 재배분 3안을 제안한다. | `docs/reference_masterdata.html#v5` · `docs/llm/RECIPES/add-archetype.md` |
| `V6` | context_buckets 의 total_keys 가 실제 차원의 곱과 다르다. 아키타입 수 × 시간대 × 지역상태 × 기후로 다시 센다. | `docs/reference_masterdata.html#v6` |
| `V7` | 아키타입에 폴백 플랜이 없다. fallback_plans.json 에 그 아키타입의 항목을 추가한다 — 폴백이 없으면 LLM 이 전면 차단됐을 때 그 아키타입만 멈춘다. | `docs/reference_masterdata.html#v7` · `docs/llm/RECIPES/write-fallback-plan.md` |
| `V8` | 인터럽트 규칙이 잘못됐다. when/then 의 액션·플래그가 실재하는지 보고, cooldown_s 를 넣지 않았는지 확인한다 — 그것은 결정론을 깬다. | `docs/reference_masterdata.html#v8` · `docs/llm/RECIPES/add-interrupt.md` |
| `V9` | 존 그래프가 끊겼다. zones.json 의 adjacent 로 모든 존이 서로 도달 가능해야 한다. | `docs/reference_masterdata.html#v9` |

## 플랜 검증 4단 (V1.~V4.)

| 코드 | 무엇을 하면 되는가 | 근거 |
|---|---|---|
| `V1.EXTRA_FIELD` | 스키마에 없는 필드가 있다. 런타임이 읽지 않는 필드를 넣으면 조용히 무시되므로 아예 거절한다 — 필드를 빼거나 스키마를 먼저 고친다. | — |
| `V1.PARSE` | JSON 이 깨졌다. 모델이 코드 펜스나 설명을 같이 냈을 가능성이 높다 — 출력에서 JSON 객체만 남긴다. | `docs/reference_masterdata.html#plan-schema` |
| `V1.SCHEMA` | 플랜 스키마가 틀렸다. 필수 필드는 schema·goal·steps 이고 steps 는 3~10개다. | `docs/reference_masterdata.html#plan-schema` |
| `V1.STEP_COUNT` | 스텝 수가 3~10 밖이다. 하루를 다 채우려 하지 말고 한 사이클만 쓴다 — loop 가 반복한다. | — |
| `V2.ACTION_NOT_ALLOWED` | 이 아키타입에 허용되지 않은 액션이다. 서픽스의 allowed_actions 안에서만 고른다 — 이것이 가장 흔한 실패였다. | `docs/reference_metrics.html#08` |
| `V2.MISSING_REQUIRED_ARG` | required 인자가 빠졌다. actions.json 의 params 에서 required:true 인 것을 채운다. | `docs/reference_masterdata.html#act` |
| `V2.RANGE` | 정수 인자가 범위 밖이다. params 의 min·max 안으로 넣는다 — 범위는 게임서버가 감당할 수 있는 값에서 왔다. | `docs/reference_masterdata.html#act` |
| `V2.TYPE_MISMATCH` | 인자 타입이 다르다. params 의 type(poi_ref·item_ref·npc_ref·zone_ref·enum·int·route)에 맞춘다. | `docs/reference_masterdata.html#act` |
| `V2.UNKNOWN_ACTION` | 카탈로그에 없는 액션이다. 프롬프트의 ACTIONS 표에 있는 이름만 쓴다. | `docs/reference_masterdata.html#actions` |
| `V2.UNKNOWN_ARG` | 액션이 모르는 인자다. actions.json 의 그 액션 params 에 있는 이름만 쓴다 — 강제 디코딩은 문법을 지키면서 인자를 지어내므로 여기서 걸린다. | `docs/reference_masterdata.html#act` · `docs/llm/RECIPES/add-action.md` |
| `V2.UNKNOWN_ITEM` | items.json 에 없는 아이템이다. `npc explain item <id>` 로 실재를 확인하거나 먼저 아이템을 추가한다. | `docs/reference_masterdata.html#item` · `docs/llm/RECIPES/add-item-poi.md` |
| `V2.UNKNOWN_POI` | 모르는 POI 심볼이다. $home·$workplace·$market·$tavern 같은 심볼이나 nearest:<종류> 형태만 쓴다 — 구체 POI id 를 직접 적지 않는다. | — |
| `V2.UNKNOWN_RECIPE` | items.json 의 recipes 에 없는 레시피다. 레시피 id 는 산출 아이템 id 와 같다. | `docs/reference_masterdata.html#item` · `docs/llm/RECIPES/add-item-poi.md` |
| `V3.DEGENERATE` | 같은 액션이 무의미하게 반복된다. 스텝을 합치거나 다른 활동을 섞는다. | — |
| `V3.FORBIDDEN_FLAG` | 그 스텝의 forbids 플래그가 서 있다. 앞 스텝이 세운 플래그를 지우는 액션을 먼저 넣는다. | — |
| `V3.LOOP_NOT_CLOSED` | loop:true 인데 마지막 상태가 첫 스텝의 전제를 만족하지 않는다. 마지막에 MoveTo 를 넣어 시작 위치로 돌아온다. | — |
| `V3.NO_TERMINAL` | 끝나는 스텝이 없다. loop:false 인 플랜은 마지막이 Sleep·Rest 같은 종결 액션이어야 한다. | — |
| `V3.PRECONDITION_UNMET` | 그 스텝의 requires 플래그가 서 있지 않다. **앞에 MoveTo 를 넣는다** — AtHome 은 MoveTo $home, AtWorkplace 는 MoveTo $workplace 가 세운다. 이것이 전체 실패의 절반 가까이를 차지한다. | `docs/reference_metrics.html#08` · `masterdata/prompt/system_rules.md` |
| `V3.RESOURCE_IMBALANCE` | 소비량이 보유량보다 많다. count 를 줄이거나 앞에 Gather·Mine·Withdraw 를 넣는다. | — |
| `V3.UNREACHABLE_POI` | 그 POI 에 갈 수 없다. 존 그래프가 끊겼거나 이 아키타입에게 출입이 막혀 있다. | — |
| `V4.DEADLOCK` | 드라이런이 멈췄다. 어떤 스텝도 전제를 만족하지 못한다 — 첫 스텝의 requires 부터 본다. | — |
| `V4.INFINITE_LOOP` | 드라이런이 끝나지 않는다. loop:true 인데 상태가 전진하지 않는다. | — |
| `V4.OSCILLATION` | 두 상태를 오간다. 플래그를 세우는 스텝과 지우는 스텝이 번갈아 도는지 본다. | — |
| `V4.RESOURCE_STARVE` | 자원이 말라 진행이 멈춘다. 보충 스텝(Gather·Withdraw·Trade)을 넣는다. | — |
| `V4.TIMEOUT` | 드라이런이 상한 틱 안에 끝나지 않는다. 스텝의 duration 합이 하루를 넘는지 본다. | — |

## 핸드셰이크 거절

| 코드 | 무엇을 하면 되는가 | 근거 |
|---|---|---|
| `AuthFailed` | 링크 인증에 실패했다. NPC_LINK_SECRET 이 양쪽에서 같은지, 게임서버가 Auth 기능 비트를 켰는지 본다. | `docs/reference_link.html#handshake` |
| `ContractMismatch` | 계약 주 버전이 다르다. 이쪽은 한쪽만 배포할 수 없다 — 양쪽을 같이 올린다. | `docs/reference_link.html#handshake` |
| `MasterDataMismatch` | 구조 해시가 다르다. id·code·bit·POI 좌표 중 하나가 어긋났다 — 양쪽에 같은 masterdata 를 배포한다. 내용만 다른 것은 경고로 수락된다. | `docs/reference_link.html#handshake` |
| `ProtocolVersion` | 와이어 프로토콜 교집합이 없다. 양쪽 중 오래된 쪽을 올린다 — v1 게임서버는 v2 NPC 서버와 붙는다. | `docs/reference_link.html#handshake` |
| `RosterMismatch` | 로스터 해시가 다르다. --zone 필터나 --npcs 가 양쪽에서 같은지 본다. | `docs/reference_link.html#handshake` |
| `TimeScaleMismatch` | 배속이 다르다. --time-scale 을 양쪽에서 같게 준다. | `docs/reference_link.html#handshake` |

