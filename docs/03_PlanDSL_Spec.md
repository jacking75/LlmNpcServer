# 03. 플랜 DSL 사양 및 검증기

> LLM이 생성하는 유일한 산출물의 형식. **자연어는 한 글자도 없다.**
> 이 스키마는 (1) 강제 디코딩의 입력, (2) 검증기의 기준, (3) 런타임 실행기의 입력 — 세 곳에서 동시에 쓰인다.

---

## 1. 플랜 문서 예시

```json
{
  "schema": 1,
  "goal": "restock_and_forge",
  "reasoning": "ore depleted; siege raises weapon demand",
  "steps": [
    { "action": "MoveTo",  "args": { "poi": "$nearest_field", "speed": "run" }, "timeout_s": 300 },
    { "action": "Mine",    "args": { "resource": "iron_ore", "count": 8 }, "timeout_s": 900 },
    { "action": "MoveTo",  "args": { "poi": "$workplace" }, "timeout_s": 300 },
    { "action": "Craft",   "args": { "recipe": "iron_sword", "count": 3 }, "timeout_s": 1800 },
    { "action": "Store",   "args": { "item": "iron_sword", "count": 3 } },
    { "action": "MoveTo",  "args": { "poi": "$home" }, "timeout_s": 300 },
    { "action": "Sleep",   "args": { "until_time": "Morning" } }
  ],
  "on_step_fail": "fallback",
  "loop": true
}
```

**포함되지 않는 것** (의도적 배제):

| 배제 항목 | 이유 |
|---|---|
| `npc_id` | 플랜은 (아키타입 × 버킷) 단위로 재사용된다. 개체에 묶이면 캐시가 무의미해진다 |
| 인터럽트 정의 | `interrupts.json`의 결정론 규칙이 담당한다 (§01 문서 §7) |
| 절대 POI id (`smithy_01`) | 심볼(`$workplace`)만 허용. 개체별 바인딩은 런타임이 한다 |
| 조건 분기 / 반복문 | 플랜은 **선형 시퀀스**다. 분기가 필요하면 재계획한다 |
| 대사 텍스트 | `Speak`는 `dialogue` id만 받는다 |
| 좌표 | 좌표는 게임서버 소관 |

`reasoning`은 **디버깅·검수 전용**이며 런타임이 무시한다. 검수자가 "왜 이 플랜인가"를 읽을 수 있게 하려고 남긴다. 40토큰으로 상한을 둔다.

---

## 2. JSON Schema (강제 디코딩 입력)

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "NpcPlan",
  "type": "object",
  "properties": {
    "schema":    { "const": 1 },
    "goal":      { "type": "string" },
    "reasoning": { "type": "string", "maxLength": 200 },
    "loop":      { "type": "boolean" },
    "on_step_fail": { "type": "string",
                      "enum": ["fallback", "retry_once", "skip", "replan"] },
    "steps": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "action":    { "type": "string",
                         "enum": ["MoveTo","Follow","Wander","Flee","Patrol",
                                  "Work","Gather","Mine","Farm","Fish","Craft","Cook","Repair",
                                  "Talk","Trade","Greet","Gossip","Pray","Perform",
                                  "Eat","Drink","Sleep","Rest","Bathe",
                                  "Attack","Defend","Guard","CallForHelp","Retreat",
                                  "PickUp","Drop","Store","Withdraw","Equip",
                                  "Wait","Observe","Emote"] },
          "args":      { "type": "object" },
          "timeout_s": { "type": "integer" }
        }
      }
    }
  }
}
```

> `action` 열거값은 **`actions.json`에서 자동 생성**한다. 손으로 유지하면 반드시 어긋난다.
> `args`는 스키마 단계에서 `object`로만 두고, 액션별 파라미터 검증은 검증기 2단이 한다. JSON Schema의 `oneOf`로 37개 액션 분기를 만들면 강제 디코딩 FSM이 폭발한다.
> 구현은 `Npc.Llm/SchemaProvider.cs` 이고, `Full` 프로파일은 `args`를 전 액션 파라미터의 **평탄화 합집합**으로 채운다.

### 왜 제약이 이렇게 적은가 — W1 실측 (T0-09 · T0-13)

**스키마를 강제할수록 나빠졌다.** `response_format=json_schema` 는 유효 JSON 99/100 을 내면서 **어휘 검증 통과 0/100** 이었다. 실패 99건이 전부 `V2.UNKNOWN_ARG` 다 — 문법은 완벽한데 존재하지 않는 인자를 지어낸다(`MoveTo.recipe` · `Eat.poi`). 같은 모델의 `prompt` 모드는 93/100 이다 (`measurements/W1_schema.md`).

그래서 **강제 디코딩을 기본에서 뺐고**(`appsettings.Llm.json` 의 `force_json_schema: false`), 이 스키마의 주 용도는 **프롬프트 프리픽스에 실리는 문서**다. 아래 요소는 W1 에서 실제로 실패를 만든 건수만큼 제거했다 — **전부 검증기 1·2단이 다시 잡으므로 안전망은 줄지 않는다.**

| 제거한 요소 | W1 실패 건수 | 대신 잡는 곳 |
|---|---|---|
| `minItems` / `maxItems` | 7 | `V1.STEP_COUNT` |
| `additionalProperties: false` | 1 | `V1.EXTRA_FIELD` |
| `required` | 1 | `V2.MISSING_REQUIRED_ARG` |
| `maximum` / `minimum` | 1 | `V2.RANGE` |
| `pattern` (goal) | — 제공사별 지원 편차 | `V1.SCHEMA` |
| `oneOf` / `anyOf` | — FSM 폭발 | 검증기 2단 |

> **측정 지표를 잘못 고르면 100% 실패를 통과로 읽는다.** 게이트 G0-1 의 기준인 "유효 JSON" 으로 보면 `forced` 가 이겼고, 쓸 수 있는 플랜 수로 보면 0 대 93 으로 졌다. `docs/10 §5` 의 위험 신호("유효율 90% 미만")로도 이 실패는 잡히지 않는다.

### POI 심볼 (허용 목록)

| 심볼 | 바인딩 |
|---|---|
| `$home` | NPC 인스턴스의 `home_poi` |
| `$workplace` | NPC 인스턴스의 `workplace_poi` |
| `$market` / `$tavern` / `$temple` / `$gate` | 같은 존에서 가장 가까운 해당 타입 POI |
| `$nearest_field` | 아키타입이 접근 가능한 가장 가까운 `field` POI |
| `$nearest_safe` | 가장 가까운 `RegionPeaceful` 존의 `gate`/`home` |
| `$nearest_shelter` | 실내 판정된 가장 가까운 POI |

바인딩 실패(예: `$nearest_field`가 없는 존) 시 → 검증기 3단에서 잡거나, 런타임에서 `ActionFailed(Unreachable)` → 폴백.

---

## 3. 4단 검증기

```csharp
// Npc.Core/Validation/PlanValidator.cs
public sealed class PlanValidator
{
    public ValidationResult Validate(PlanDocument doc, ValidationContext ctx);
}

public readonly record struct ValidationResult(
    ValidationStage FailedAt,       // None = 통과
    string Code,                    // "V2.UNKNOWN_POI" 등
    int    StepIndex,               // -1 = 문서 전체
    string Detail);

public enum ValidationStage { None, Schema, Vocabulary, Coherence, DryRun }
```

### 1단 — 스키마 (`Schema`)

강제 디코딩을 신뢰하지 않고 재검증한다. 외부 API의 `strict` 지원 수준이 제공사마다 다르기 때문이다.

| 코드 | 검사 |
|---|---|
| `V1.PARSE` | JSON 파싱 |
| `V1.SCHEMA` | JSON Schema 검증 |
| `V1.STEP_COUNT` | 3 ≤ steps ≤ 10 |
| `V1.EXTRA_FIELD` | `additionalProperties: false` 위반 |

### 2단 — 어휘 (`Vocabulary`)

| 코드 | 검사 |
|---|---|
| `V2.UNKNOWN_ACTION` | `action`이 카탈로그에 존재 |
| `V2.ACTION_NOT_ALLOWED` | 해당 아키타입의 `allowed_actions`에 포함 |
| `V2.UNKNOWN_ARG` | `args` 키가 액션의 `params`에 정의됨 |
| `V2.MISSING_REQUIRED_ARG` | `required: true` 파라미터 누락 |
| `V2.TYPE_MISMATCH` | 파라미터 타입 불일치 |
| `V2.UNKNOWN_POI` | POI 심볼이 허용 목록에 있음 |
| `V2.UNKNOWN_ITEM` / `V2.UNKNOWN_RECIPE` | `items.json` 참조 무결성 |
| `V2.RANGE` | int 파라미터가 min/max 범위 내 |

### 3단 — 정합성 (`Coherence`)

GOAP 스타일 정적 검사. **여기가 실질적으로 가장 많이 잡는다.**

```csharp
// 의사코드
WorldFlags state = ctx.InitialFlags;   // 버킷이 함의하는 초기 상태
foreach (var (step, i) in doc.Steps.Index())
{
    var def = catalog[step.Action];
    if ((def.Requires & ~state) != 0) return Fail("V3.PRECONDITION_UNMET", i);
    if (def.RequiresAny != 0 && (def.RequiresAny & state) == 0)
        return Fail("V3.PRECONDITION_UNMET", i);          // OR 전제 (docs/01 §2.1 requires_any)
    if ((def.Forbids  &  state) != 0) return Fail("V3.FORBIDDEN_FLAG", i);
    state = (state & ~def.Clears) | def.Grants;
}
if (doc.Loop && !CanReenterFrom(state, doc.Steps[0])) return Fail("V3.LOOP_NOT_CLOSED", -1);
```

| 코드 | 검사 |
|---|---|
| `V3.PRECONDITION_UNMET` | i번 스텝의 `requires`가 그 시점 상태에서 미충족 |
| `V3.FORBIDDEN_FLAG` | i번 스텝의 `forbids`가 성립 |
| `V3.LOOP_NOT_CLOSED` | `loop: true`인데 마지막 상태에서 첫 스텝으로 못 돌아감 |
| `V3.UNREACHABLE_POI` | POI 심볼이 이 아키타입/존에서 바인딩 불가 |
| `V3.RESOURCE_IMBALANCE` | 소비량 > 생산량 (예: 광석 3개로 검 3자루) |
| `V3.NO_TERMINAL` | `loop: false`인데 마지막이 `Sleep`/`Rest`가 아님 |
| `V3.DEGENERATE` | 동일 액션이 3회 이상 연속 |

> `ctx.InitialFlags`는 **버킷 키 + 아키타입 기본 인벤토리**에서 유도한다.
>
> - 버킷 쪽: `*.Night.*` → `IsNight`, `*.War.*` → `RegionUnderAttack`. 이 매핑은 `context_buckets.json`의 `world_flag`에 정의되어 있다.
> - 아키타입 쪽: `archetypes.json`의 `initial_inventory`가 `items.json`의 `grants`를 통해 세우는 플래그(`HasTool`, `HasFood`, `HasWater`, `HasCoin`, …). 이게 없으면 `Work`(requires `HasTool`)를 쓰는 플랜이 전부 3단에서 걸린다.
>
> 장소 플래그(`AtWorkplace` 등)는 초기 상태에 없다. `MoveTo`처럼 **`NpcArrived`로 완료되는 액션**이 도착한 POI 심볼에 해당하는 장소 플래그를 세운다 — `docs/01 §2.2`가 `MoveTo`의 grants를 "(POI별)"이라고 쓴 부분이 이것이다.

**`V3.RESOURCE_IMBALANCE`는 자기모순만 잡는다.** 플랜이 스스로 모으기도 하고 쓰기도 한 자원만 본다. 아예 모으지 않는 자원(석탄 등)은 인벤토리·보관함에서 온다고 가정한다 — 3단은 창고 잔량을 알 수 없다.

### 4단 — 드라이런 (`DryRun`)

`Npc.Sim`의 축소 인스턴스(NPC 1마리, 시간 1000배속)에서 플랜을 1사이클 실행한다.

| 코드 | 검사 |
|---|---|
| `V4.DEADLOCK` | 어떤 스텝에서 진행이 멈춤 |
| `V4.TIMEOUT` | 전체 사이클이 게임 시간 36시간 초과 |
| `V4.INFINITE_LOOP` | `loop: true`인데 사이클이 24게임시간 미만 (너무 빨리 돔) |
| `V4.RESOURCE_STARVE` | 사이클 중 필수 자원이 0이 되고 회복 경로 없음 |
| `V4.OSCILLATION` | 같은 두 POI를 4회 이상 왕복 |

드라이런은 비싸다(~50ms). 프리베이크에서는 전수 실행하고, 런타임 개별 재계획에서는 **샘플링(10%)** 만 한다.

---

## 4. 실패 처리 정책

```
검증 실패
  ├─ 1회 재시도 (temperature +0.2, 실패 코드를 프롬프트에 피드백)
  │    ↓ 또 실패
  ├─ 같은 아키타입의 인접 버킷 플랜 재사용
  │    (예: *.War.Cold 실패 → *.War.Fair → *.Alert.Cold 순)
  │    ↓ 없음
  └─ fallback_plans.json 의 아키타입 폴백
```

재시도 프롬프트에 실패 코드를 넣는 것은 효과가 크다. 단 **1회만** 한다 — 2회 이상은 성공률이 거의 안 오르고 토큰만 태운다 (W5–6에서 실측해서 확정).

```csharp
// 재시도 시 서픽스에 추가되는 블록 (프리픽스는 절대 안 건드린다 — 캐시 유지)
"previous_attempt_failed": {
  "stage": "Coherence",
  "code": "V3.PRECONDITION_UNMET",
  "step": 3,
  "detail": "Craft requires HasRawMaterial but no preceding step grants it"
}
```

---

## 5. 런타임 표현 — 컴파일된 플랜

JSON은 저장·검수용이고, 런타임은 컴파일된 구조를 쓴다.

```csharp
// Npc.Core/CompiledPlan.cs
public sealed record CompiledPlan   // record 인 이유: PlanStore 가 등록 시점에 Id 를 with 로 박는다
{
    public PlanId     Id            { get; init; }
    public BucketKey  Bucket        { get; init; }
    public int        Version       { get; init; }
    public bool       Loop          { get; init; }
    public StepFailPolicy OnFail    { get; init; }
    public WorldFlags RequiredFlags { get; init; }   // 플랜 진입 조건 — 이탈 판정용 (아래 계산식)
    public WorldFlags ForbiddenFlags{ get; init; }
    public ImmutableArray<CompiledStep> Steps { get; init; }
    public ImmutableArray<StepFlags>    StepFlagSets { get; init; }   // 스텝별 플래그 전이

    public string SourceJson { get; init; }          // 검수·리플레이용 원본 보존
    public PlanOrigin Origin { get; init; }          // Prebaked | Runtime | Fallback | Pinned
}

public readonly record struct CompiledStep(
    ActionId  Action,
    PoiSymbol Poi,           // 심볼. 실행 시점에 개체 바인딩
    ItemId    Item,
    ushort    Count,
    ushort    TimeoutSeconds,
    byte      ArgFlags,      // speed 등급 등 enum 파라미터의 ordinal
    byte      NpcRef,        // npc_ref 심볼 (상위 2비트 종류 + 하위 6비트 페이로드)
    ushort    FlagSetIndex); // → plan.StepFlagSets[i]

public readonly record struct StepFlags(
    WorldFlags Requires,
    WorldFlags RequiresAny,
    WorldFlags Forbids,
    WorldFlags Grants,
    WorldFlags Clears);
```

`CompiledStep`은 **32바이트 이하 값 타입**으로 유지한다 (실측 14바이트). 5,000 NPC × 최대 10스텝이 캐시에 잘 들어가야 한다.

> **플래그를 `CompiledStep` 안에 두지 않는 이유.** `WorldFlags` 5종이면 그것만 40바이트다. 32바이트 상한과 양립할 수 없다. 스텝별 플래그는 `CompiledPlan.StepFlagSets` 병렬 배열에 두고 `FlagSetIndex`로 찾는다. 실행기 핫패스는 플래그를 보지 않으므로(플랜 단위 `RequiredFlags`만 본다) 이 간접 참조는 스캔 성능에 영향이 없다.

### `RequiredFlags` 사전계산이 핵심이다

인지 스캔(§11.3)에서 NPC 5,000마리를 훑을 때 스텝을 순회하면 안 된다. 플랜 단위로 미리 계산해둔 `RequiredFlags`/`ForbiddenFlags`와 현재 상태를 **비트 연산 한 번**으로 비교한다.

**단순 OR 이 아니다.** 앞선 스텝이 세워주는 플래그는 진입 조건이 아니다. 전부 OR 하면 `Mine`이 세워줄 `HasRawMaterial`을 `Craft` 때문에 요구하게 되고, 인지 스캔이 매 틱 "이탈"이라고 답한다.

```csharp
WorldFlags granted = 0, cleared = 0, required = 0, forbidden = 0;
foreach (var step in steps)
{
    required  |= step.Requires & ~granted;
    forbidden |= step.Forbids  & ~cleared;
    granted = (granted & ~step.Clears) | step.Grants;
    cleared = (cleared & ~step.Grants) | step.Clears;
}
```

```csharp
// 틱당 115회 실행되는 핫패스
static bool NeedsReplan(in WorldFlags cur, CompiledPlan p)
    => (p.RequiredFlags & ~cur) != 0 || (p.ForbiddenFlags & cur) != 0;
```

---

## 6. 플랜 실행기

```csharp
// Npc.Runtime/PlanExecutor.cs
public sealed class PlanExecutor
{
    public void Step(Tick tick, IGameServerLink link)
    {
        for (int i = 0; i < _active.Length; i++)          // SoA 순회
        {
            ref var st = ref _state[i];
            if (st.Status == StepStatus.Waiting)
            {
                if (tick.Value - st.IssuedTick < st.TimeoutTicks) continue;
                OnStepFailed(i, ActionFailReason.Timeout);  // 명령 유실 방어 (§02 N-규칙)
                continue;
            }
            if (st.Status == StepStatus.Ready)
            {
                var step = _plans[st.PlanId].Steps[st.StepIndex];
                var corr = _correlations.Next(i);
                foreach (var cmd in Emit(i, step, corr, tick))
                    link.Enqueue(in cmd);                   // 배치 큐로만
                st.Status = StepStatus.Waiting;
                st.IssuedTick = tick.Value;
            }
        }
    }
}
```

**플랜 스왑은 원자적이어야 한다.** 재계획 워커가 새 플랜을 완성하면, 실행 중인 스텝이 끝나는 시점(또는 인터럽트 시점)에 `PlanId`만 교체한다. 스텝 중간에 바꾸면 상관 ID가 꼬인다.

```csharp
// 스왑 규약
// 1) 워커가 _pendingPlan[i] 에 새 PlanId 를 Volatile.Write
// 2) 실행기가 스텝 경계에서 _pendingPlan[i] 를 확인하고 스왑, StepIndex = 0
// 3) 인터럽트 발생 시에는 즉시 스왑 허용 (진행 중 명령은 상관 ID로 무시)
```

---

## 7. 플랜 스토어 파일 포맷

```
planstore/
  manifest.json                    // 마스터데이터 ContentHash, 생성 시각(외부 주입), 모델/버전
  manifest_history.jsonl           // 회차별 요약 한 줄씩. 덮어쓰기만 하면 지난 회차 숫자를 못 본다
  plans/
    blacksmith@Dawn.Peace.Fair.json
    blacksmith@Dawn.Peace.Cold.json
    ...                            // 2,880개
  pinned/
    blacksmith@Evening.War.Cold.json   // 사람이 수정한 플랜. prebake가 덮어쓰지 않는다
  rejected/
    <bucket>.<attempt>.json            // 검증 실패한 산출물 + 실패 코드. 품질 분석용
```

```jsonc
// manifest.json
{
  "schema": 1,
  "masterdata_hash": "sha256:9f3a...",
  "prefix_hash": "c41b...",
  "generated_at": "",                  // 외부 주입. 비우면 결정론적 산출물이 된다
  "partial": false,                    // --budget-usd 캡에 걸려 중단됐는가 (--resume 이 본다)
  "generated_by": { "tier": "T2", "model": "gpt-5-nano", "temperature": 0.4,
                    "concurrency": 8, "peak_concurrency": 18,
                    "first_rate_limit_concurrency": 0 },
  "counts": { "total": 2880,           // 버킷 공간 크기. 고정
              "target": 264,           // 이번 회차가 만들기로 선언한 수 (--only 면 그 부분집합)
              "generated": 2841,       // 스토어 전체. 이번 회차 것만이 아니다
              "pinned": 12, "fallback": 27, "reused": 0 },
  "validation": { "pass": 2841, "fail_schema": 3, "fail_vocab": 11,
                  "fail_coherence": 22, "fail_dryrun": 3, "fail_call": 0 },
  "cost_usd": 0.23,                    // 이번 회차. target 버킷을 만드는 데 든 값
  "wall_clock_s": 187,                 // 이번 회차
  "cache_hit_rate": 0.96,              // 입력 토큰 기준. 하한은 엔진 계열별 (docs/13 §7)
  "file_hashes": [                     // 부분 무효화 판정의 입력 (docs/13 §3)
    { "file": "actions.json", "sha256": "..." }
  ]
}
```

`manifest.json`이 곧 R&D 보고서의 원자료다. 프리베이크를 돌 때마다 보존한다.

### `counts` 의 세 수는 세는 대상이 다르다 (2026-07-28 결정 13·15-A)

| 필드 | 무엇 | 증분 `--only` 회차에서 |
|---|---|---|
| `total` | 버킷 공간 크기. 항상 2,880 | 고정 |
| `target` | **이번 회차가 만들기로 선언한 수** | 264 · 714 처럼 회차마다 다르다 |
| `generated` | **스토어 전체**에서 LLM 이 채운 수 | 누적된다 (264회차 뒤 195 → 714회차 뒤 718) |

**이 셋을 구분하지 않으면 게이트가 거짓이 된다.** 실제로 그랬다 —
`generated` 가 러너의 스토어(이번 회차 몫)를 세는 바람에 디스크에 718개가 있는데
manifest 는 523 이라고 적었고, `Phase3GateTests` 가 그 값을 읽어 스토어를 실제보다 작게 봤다.

**`cost_usd`·`wall_clock_s` 는 `target` 없이 읽으면 안 된다.**
"97초" 는 264버킷의 97초이지 2,880버킷의 97초가 아니다. P3 게이트 항목 2·3 의 실패 메시지가
항상 `[대상 N버킷]` 을 같이 찍는 이유다.

> `target` 이 **0 이면 2026-07-28 이전 스키마**다. 그 manifest 로는 항목 2·3·4 를 판정하지 않는다.

**`generated_at`은 외부에서 주입한다.** 만드는 쪽이 `DateTime.Now`를 부르면 같은 입력이 같은 파일을 내지 못한다 (CLAUDE.md §2.3).
`file_hashes`가 없으면 부분 무효화를 판정할 수 없어 POI 하나 추가에도 2,880건 전량 재생성이 된다.
`concurrency` 3필드는 "동시 32 / 5분"의 근거를 실측으로 남기기 위한 것이다 (docs/13 T3-12).

---

## 8. 테스트

| 테스트 | 내용 |
|---|---|
| `Schema_GeneratedFromCatalog` | JSON Schema의 `action` 열거값이 `actions.json`과 일치 |
| `Validator_CatchesEachCode` | V1~V4의 모든 실패 코드마다 최소 1개의 유발 픽스처 |
| `Validator_AcceptsAllFallbacks` | `fallback_plans.json` 40개 전부 4단 통과 |
| `CompiledStep_SizeIsBounded` | `Unsafe.SizeOf<CompiledStep>() <= 32` (플래그는 `StepFlagSets`로 분리) |
| `Plan_RoundTrip` | JSON → Compiled → JSON 왕복 시 의미 동일 |
| `Executor_AtomicSwap` | 스텝 실행 중 스왑 요청 → 스텝 경계에서만 교체, 상관 ID 누수 없음 |
| `Executor_TimeoutSynthesis` | 명령을 드롭 → timeout 후 합성 실패 → 플랜 진행 재개 |
| `Validator_Fuzz` | 랜덤 변형 플랜 10,000건 → 크래시 없이 전부 판정 |
