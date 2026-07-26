# Phase 2 (W5–6) — 플랜 컴파일러 · 검증기

> **목적: LLM이 유효한 플랜을 만들게 한다.** 아직 캐시도 티어링도 없다. 단건 생성만.
>
> **게이트: 검증 통과율 ≥ 90%** (4단 전체 기준)

---

## 1. 주차 배분

| 주 | 내용 |
|---|---|
| W5 | `Npc.Llm` 프롬프트 조립 + 단건 생성 + 검증기 1·2단 |
| W6 | 검증기 3·4단(정합성·드라이런) + 실패 처리 정책 + 통과율 측정/개선 |

---

## 2. `Npc.Llm` 구조

```
Npc.Llm/
  IPlanCompiler.cs          플랜 생성 계약
  LlmPlanCompiler.cs        본체 (`PlanCompiler` 는 쓸 수 없다 — Npc.Core.Plan 에 이미 있다)
  PromptPrefix.cs           고정 프리픽스 (기동 시 1회 조립, 불변)
  PlanRequestSuffix.cs      가변 서픽스 조립
  SchemaProvider.cs         actions.json → JSON Schema 생성
  ChatClientFactory.cs      IChatClient 생성 (W9에 티어 라우터로 확장)
  TieredPlanCompiler.cs     [W9~] 3-티어 라우터 (docs/14 §4)
  CircuitBreaker.cs         [W9~] T2 연속 실패 차단 (docs/14 §10)
```

> 일일 하드 캡은 여기가 아니라 **`Npc.Planning/ReplanBudget.cs`** 다 (`docs/14 §3`).
> 예산은 "누가 재계획을 받을 것인가"의 문제이고 그 판정은 큐 쪽에서 난다 — 호출 직전에 막으면 이미 큐에서 뽑은 뒤라 되돌릴 곳이 없다.

```csharp
public interface IPlanCompiler
{
    ValueTask<PlanCompileResult> CompileAsync(PlanRequest req, CancellationToken ct);
}

public readonly record struct PlanRequest(
    BucketKey       Bucket,
    WorldFlags      Flags,
    PlanQuality     Quality,          // Archetype | Individual — [W9~] 티어 선택에 사용
    NpcSnapshot?    Individual,       // null이면 아키타입 플랜(캐시 대상)
    ValidationResult? PreviousFailure); // 재시도 시 피드백

public readonly record struct PlanCompileResult(
    CompiledPlan?    Plan,
    ValidationResult Validation,
    CompileStats     Stats);

public readonly record struct CompileStats(
    int PromptTokens, int CachedTokens, int CompletionTokens,
    double LatencyMs, double CostUsd, string Model, int Attempt);
```

**`CompileStats`를 모든 호출에서 기록한다.** W7의 프리베이크 manifest와 W12 보고서의 원자료가 여기서 나온다.

---

## 3. 서픽스 조립 — 300토큰 예산을 지킨다

프리픽스는 고정되어 캐시된다. 서픽스는 매번 prefill되므로 **짧을수록 곧바로 지연이 준다.** dotLLM은 prefill이 느리므로 특히 그렇다.

> **프리픽스 크기는 W5에 재서 확정한다.** `docs/01 §10.1`의 4,200~4,500 은 액션 12·플래그 16 짜리 축소판으로 잡은 값이다(W1 실측 4,409 tok).
> 정식은 액션 37·플래그 42 라 그 단가로 외삽하면 약 8,000 tok 이다. **캐시에 필요한 것은 하한 4,096 뿐이므로 상한을 맞추려고 카탈로그를 줄이지 않는다** — T2-04에서 실측하고 §10.1을 갱신한다.
> 여기서 지키는 300 토큰은 **서픽스** 예산이고, 이건 프리픽스 크기와 무관하게 그대로다.

```csharp
// Npc.Llm/PlanRequestSuffix.cs
public static string Build(in PlanRequest r, MasterDataSet md)
{
    // 압축 원칙:
    //  - 키를 짧게 (archetype → a, time_of_day → t)  … 는 하지 않는다. 모델 이해도가 떨어진다.
    //  - 대신 값을 열거형 문자열로만. 설명 문장 금지.
    //  - 플래그는 참인 것만 나열. 거짓은 생략.
    //  - 인벤토리는 0이 아닌 항목만.
    //  - recent는 salience 상위 3개만.
}
```

```jsonc
// 아키타입 플랜 요청 (캐시 대상) — 약 150토큰
{
  "archetype": "blacksmith",
  "time_of_day": "Evening",
  "region_state": "War",
  "climate": "Cold",
  "flags": ["AtWorkplace","HasTool","IsHungry","RegionUnderAttack","WeatherHarsh"],
  "traits": { "diligence": 80, "sociability": 40, "courage": 55, "greed": 50 },
  "goals": ["restock_ore","fulfill_orders","maintain_shop"]
}
```

> **`recent` 는 강타입으로만 만든다** (T2-06). 아래 예시의 `"ResourceDepleted@mine"` 처럼 보이는 자유 문자열은 실제로는 `GameEventKind` + `PoiSymbol` 조합에서 렌더된다(`"NpcActionFailed@$nearest_field"`). 플레이어가 쓴 문자열이 프롬프트에 새는 길을 아예 두지 않는다 (`../CLAUDE.md §2.5`).
>
> **`allowed_actions` 는 서픽스에 싣지 않는다** (T2-04). §8 의 `V2.ACTION_NOT_ALLOWED` 처방 중 "프리픽스에 아키타입별 표 추가" 쪽을 골랐다 — 아키타입당 20여 개 id 를 매 요청에 실으면 아래 개별 요청이 300 토큰을 넘긴다.
>
> **인벤토리는 상위 8종까지만 싣는다** (T2-06). 아이템이 82종이라 만재 인벤을 그대로 실으면 그것만으로 예산을 넘긴다. `ItemId` 오름차순(원자재 → 완제품 → 식량 …)으로 자른다.

```jsonc
// 개별 NPC 재계획 요청 — 약 250토큰
{
  "archetype": "blacksmith",
  "time_of_day": "Evening",
  "region_state": "War",
  "climate": "Cold",
  "flags": ["AtWorkplace","IsHungry","IsExhausted","RegionUnderAttack","ResourceDepleted"],
  "traits": { "diligence": 80, "sociability": 40, "courage": 55, "greed": 50 },
  "goals": ["restock_ore","fulfill_orders"],
  "inventory": { "coal": 3, "coin": 45 },
  "recent": ["ResourceDepleted@mine", "PlayerInteracted", "DamageTaken"],
  "last_plan_outcome": "failed_at_step_1"
}
```

### 서픽스 토큰 예산 테스트

```csharp
[Fact] public void Suffix_NeverExceeds300Tokens()
{
    foreach (var bucket in AllBuckets())              // 2,880
        foreach (var stress in StressSnapshots())      // 인벤 만재, recent 만재 등
            Assert.True(Tokenize(Build(...)).Length <= 300);
}
```

**이 테스트가 없으면 서픽스는 반드시 자란다.** 필드가 하나씩 추가되면서 어느 순간 800토큰이 되고, prefill 시간이 3배가 된다.

---

## 4. 스키마 생성기

`actions.json`에서 JSON Schema를 생성한다. 손으로 유지하지 않는다.

```csharp
// Npc.Llm/SchemaProvider.cs
public sealed class SchemaProvider
{
    public string PlanSchemaJson { get; }     // 강제 디코딩용
    public string CatalogMarkdown { get; }    // 프리픽스용 액션 카탈로그 렌더링
    public string Sha256 { get; }
}
```

W1(G0-1)에서 문제된 스키마 요소는 여기서 제거한다. 아래 "실측" 열은 `docs/measurements/W1_schema.md`의 건수다.

| 요소 | 위험 | 실측 | 대안 |
|---|---|---|---|
| `oneOf` / `anyOf` | FSM 상태 폭발. 로컬 강제 디코딩이 느려지거나 실패 | (미사용) | `args`를 `type: object`로 두고 검증기 2단이 처리 |
| 깊은 중첩 (3단 이상) | 모델이 닫는 괄호를 놓침 | (미사용) | 평탄화 |
| `pattern` (정규식) | 제공사별 지원 편차 | (미사용) | 검증기 2단으로 이관 |
| `minItems`/`maxItems` | 모델이 종종 어긴다 | **7건** (`V1.STEP_COUNT`) | **뺀다.** 검증기 1단이 이미 스텝 수를 본다 |
| `additionalProperties: false` | 환각 필드 차단 | **1건** (`V1.EXTRA_FIELD`) | **뺀다.** 검증기 1단이 이미 잡는다 |
| `required` · `maximum` | 로컬 모델이 어긴다 | 각 **1건** | **뺀다.** 검증기 2단이 인자를 본다 |

### 실측이 뒤집은 것 — 강제 디코딩을 기본으로 두지 않는다

W1(T0-09)의 결론은 "스키마를 고치면 된다"가 아니었다. **스키마를 강제할수록 나빠졌다.**

| 스키마 | 디코딩 | 유효 JSON | 검증통과 |
|---|---|---|---|
| full | forced | 99/100 | **0** — `V2.UNKNOWN_ARG` 99 |
| bare | forced | 20/20 | **3** — `V2.MISSING_REQUIRED_ARG` 17 |
| full | **prompt** | 98/100 | **93** |

강제 디코딩은 문법을 지키면서 **인자를 지어낸다**(`MoveTo.recipe`, `Eat.poi`). 형식이 맞으니 파서는 통과시키고, 어휘 검증에서 전부 걸린다.
따라서 스키마는 **프롬프트에 실리는 문서**로만 쓰고, `response_format`으로 강제할지는 설정으로 빼서 T2-19 첫 회차에 두 모드를 다 재고 정한다.
이것이 `../CLAUDE.md §2.6`("강제 디코딩을 신뢰하지 않는다")의 실측 근거다.

---

## 5. 검증기 구현

§03 문서 §3의 4단을 구현한다. 구현 순서와 주의점만 여기 적는다.

### W5: 1·2단

단순하다. 하루면 된다. 다만 **에러 코드를 세분화**해야 W6의 통과율 개선에서 어디를 고칠지 보인다.

```csharp
_metrics.ValidationFailures.Add(1,
    new("stage", result.FailedAt.ToString()),
    new("code",  result.Code),
    new("archetype", bucket.A.ToString()));
```

### W6: 3단 (정합성) — 여기가 실질적으로 가장 많이 잡는다

```csharp
// Npc.Core/Validation/CoherenceValidator.cs
public ValidationResult Validate(PlanDocument doc, ValidationContext ctx)
{
    var state = ctx.InitialFlags;          // 버킷에서 유도
    var invSim = ctx.InitialInventory.ToDictionary();
    PoiSymbol at = ctx.StartPoi;

    for (int i = 0; i < doc.Steps.Length; i++)
    {
        var s = doc.Steps[i];
        var def = _catalog[s.Action];

        if ((def.Requires & ~state) != 0)
            return Fail("V3.PRECONDITION_UNMET", i, Explain(def.Requires & ~state));
        if ((def.Forbids & state) != 0)
            return Fail("V3.FORBIDDEN_FLAG", i, Explain(def.Forbids & state));

        // POI 바인딩 가능성
        if (def.NeedsPoi && !ctx.CanBind(s.Poi, out _))
            return Fail("V3.UNREACHABLE_POI", i, s.Poi.ToString());

        // 자원 수지
        if (def.Consumes is { } c && !invSim.TryConsume(c, s.Count))
            return Fail("V3.RESOURCE_IMBALANCE", i, c.ToString());
        if (def.Produces is { } p) invSim.Add(p, s.Count);

        state = (state & ~def.Clears) | def.Grants;
        if (def.NeedsPoi) at = s.Poi;
    }

    if (doc.Loop && (_catalog[doc.Steps[0].Action].Requires & ~state) != 0)
        return Fail("V3.LOOP_NOT_CLOSED", -1, "");

    return ValidationResult.Ok;
}
```

**주의: `Explain()`이 중요하다.** 실패 이유를 "HasRawMaterial 플래그가 없는데 Craft를 시도함" 같은 자연어로 만들어서 재시도 프롬프트에 넣는다. 비트마스크 숫자를 그대로 넣으면 모델이 못 고친다.

### W6: 4단 (드라이런)

`Npc.Sim`을 NPC 1마리 · 시간 1000배속으로 인스턴스화한다. W2–4에서 만든 Sim을 그대로 재사용한다.

**이 검증기만 `Npc.Core`에 두지 않는다.** `../CLAUDE.md §3`의 의존 그래프에서 `Npc.Core`는 `Npc.Contracts`만 참조하는데 `SimWorld`는 `Npc.Sim`에 있다. 1~3단과 달리 4단은 세계를 굴려야 하므로 자리를 옮긴다 — **계약은 `Npc.Core`, 구현은 `Npc.Sim`.** `IPlanVocabulary`(docs/03 §5)를 가른 것과 같은 이유이고 같은 방법이다.

```csharp
// Npc.Core/Validation/IDryRunValidator.cs   ← 호출자(Npc.Llm)가 보는 것
public interface IDryRunValidator
{
    ValidationResult Validate(CompiledPlan plan, ValidationContext ctx);
}
```

```csharp
// Npc.Sim/Validation/DryRunValidator.cs     ← SimWorld 를 아는 쪽
public ValidationResult Validate(CompiledPlan plan, ValidationContext ctx)
{
    using var sim = SimWorld.CreateMinimal(ctx.MasterData, ctx.Bucket, seed: ctx.Seed);
    var npc = sim.Spawn(ctx.Bucket.A);
    sim.AssignPlan(npc, plan);

    var trace = sim.RunCycles(maxGameHours: 36, cycles: plan.Loop ? 2 : 1);

    if (trace.StalledAtStep >= 0)     return Fail("V4.DEADLOCK", trace.StalledAtStep, "");
    if (trace.GameHours > 36)         return Fail("V4.TIMEOUT", -1, $"{trace.GameHours}h");
    if (plan.Loop && trace.CycleHours < 24) return Fail("V4.INFINITE_LOOP", -1, $"{trace.CycleHours}h");
    if (trace.StarvedResource is { } r)    return Fail("V4.RESOURCE_STARVE", -1, r);
    if (trace.MaxPoiOscillation >= 4)      return Fail("V4.OSCILLATION", -1, "");
    return ValidationResult.Ok;
}
```

**드라이런은 결정론적이어야 한다.** `seed`를 버킷 키에서 유도해서, 같은 플랜은 항상 같은 판정을 받게 한다. 안 그러면 골든 테스트가 불안정해진다. `SimWorld`는 이미 `SimOptions.Seed`로 난수가 고정돼 있으므로 그 경로를 쓰고 새 `Random`을 만들지 않는다.

**비용**: 약 50ms/건 **(추정)**. 프리베이크(2,880건)에서 전수 실행하면 144초가 추가되지만 병렬화 가능하다. 런타임 개별 재계획에서는 10% 샘플링만 한다. T2-13에서 실측하면 이 수를 갱신하고, T3-15의 "≤ 60초" 목표도 같이 다시 본다.

---

## 6. 실패 처리 파이프라인

```csharp
// Npc.Llm/LlmPlanCompiler.cs
public async ValueTask<PlanCompileResult> CompileAsync(PlanRequest req, CancellationToken ct)
{
    // 시도 1
    var r1 = await GenerateAndValidate(req, attempt: 1, ct);
    if (r1.Validation.Ok) return r1;

    // 시도 2 — 실패 코드를 서픽스에 피드백. 프리픽스는 절대 안 건드린다 (캐시 유지)
    var r2 = await GenerateAndValidate(
        req with { PreviousFailure = r1.Validation }, attempt: 2, ct);
    if (r2.Validation.Ok) return r2;

    // 인접 버킷 재사용
    foreach (var nb in NeighborBuckets(req.Bucket))          // §7
        if (_store.TryGet(nb, out var plan))
            return Reused(plan, r2.Stats);

    // 아키타입 폴백
    return Fallback(_md.Fallbacks[req.Bucket.A], r2.Stats);
}
```

### 재시도는 1회만

W6에서 **재시도 횟수별 성공률을 실측**해서 확정한다.

| 시도 | 예상 누적 성공률 | 판단 |
|---|---|---|
| 1회 | 78% | |
| 2회 | 91% | ← 여기서 멈춘다 |
| 3회 | 93% | +2%p에 토큰 50% 추가. 손해 |

실측 결과가 다르면 그에 맞춘다. **단, 3회 이상은 어떤 결과가 나와도 하지 않는다** — 지연이 선형으로 늘어나서 런타임 재계획이 무의미해진다.

### 재시도 시 temperature

```
시도 1: temperature 0.4
시도 2: temperature 0.6   ← 같은 실수를 반복하지 않게
```

---

## 7. 인접 버킷 정의

```csharp
// Npc.Planning/BucketNeighbors.cs
// 거리가 가까운 순으로 최대 5개
public static IEnumerable<BucketKey> NeighborBuckets(BucketKey k)
{
    yield return k with { C = Climate.Fair };                    // 기후만 완화
    yield return k with { R = Relax(k.R) };                      // War→Alert, Alert→Peace
    yield return k with { C = Climate.Fair, R = Relax(k.R) };
    yield return k with { T = AdjacentTime(k.T) };               // 인접 시간대
    yield return k with { R = RegionState.Peace, C = Climate.Fair };  // 최후
}
```

**아키타입은 절대 바꾸지 않는다.** 대장장이 플랜을 농부에게 주면 액션 허용 목록부터 어긋난다. 재사용된 플랜도 검증기 2·3단을 다시 통과시킨다.

---

## 8. 통과율 개선 루프 (W6 후반)

```
1. 2,880 버킷 전량을 1회씩 생성 (외부 API. **동시성은 8에서 시작해 AIMD 로 올리며 이 회차에서 실측한다** — "동시 32 → 3분"은 상위 계획의 추정이고 W1 은 외부 동시성을 재지 않았다)
2. 실패를 (stage, code, archetype) 으로 집계
3. 상위 3개 실패 원인을 고친다
4. 반복
```

### 실패 원인별 처방

| 실패 코드 | 흔한 원인 | 처방 |
|---|---|---|
| `V2.ACTION_NOT_ALLOWED` | 프롬프트에 아키타입별 허용 액션이 안 실림 | 서픽스에 `allowed_actions` 추가 (토큰 예산 주의) 또는 프리픽스에 아키타입별 표 추가 |
| `V3.PRECONDITION_UNMET` | 모델이 `requires`/`grants` 관계를 이해 못 함 | 프리픽스의 액션 카탈로그에 requires/grants를 **표 형태**로 명시. few-shot에 정합성 실패→수정 예시 추가 |
| `V3.LOOP_NOT_CLOSED` | 마지막이 Sleep이 아님 | system_rules에 "must end in Sleep or Rest when loop=true" 강조 |
| `V3.RESOURCE_IMBALANCE` | 재료 수량 계산 실패 | 서픽스에 레시피 입출력 명시. 또는 `count`를 모델이 정하지 않고 런타임이 계산하게 스키마 변경 |
| `V4.OSCILLATION` | POI 왕복 | few-shot에 "인접 작업을 묶어라" 예시 |
| `V1.SCHEMA` | 강제 디코딩 실패 | 스키마 단순화 (§4) 또는 모델 상향 |

> **`count`를 모델에서 빼는 것을 진지하게 검토한다.** 수량 계산은 LLM이 가장 못하는 영역이고, 런타임이 인벤토리를 보고 결정하는 편이 정확하다. `Craft(recipe, count: "max")` 같은 심볼만 허용하면 `V3.RESOURCE_IMBALANCE`가 통째로 사라진다.

---

## 9. 게이트 확인

- [ ] 2,880 버킷 전량 생성 시 **검증 통과율 ≥ 90%** (재시도 1회 포함)
- [ ] 실패 2,880건 중 폴백으로 떨어지는 비율 ≤ 5%
- [ ] 서픽스 토큰 ≤ 300 (전 버킷·스트레스 케이스)
- [ ] 프리픽스 SHA가 전 요청에서 1종
- [ ] 프롬프트 캐시 적중률 ≥ 95% (외부 API)
- [ ] 드라이런이 결정론적 (같은 플랜 100회 → 동일 판정)
- [ ] 생성된 플랜을 사람이 열어봤을 때 **버킷별로 실제로 다르다** (§W1 M6의 "다양성"을 정량화: 서로 다른 액션 시퀀스 비율 ≥ 60%)

---

## 10. 측정 산출물

```
docs/measurements/W6_compile_stats.md
  - 통과율: 전체 / 아키타입별 / 버킷 차원별
  - 실패 코드 분포 (개선 전/후)
  - 재시도 횟수별 누적 성공률
  - 모델별 통과율 (로컬 vs 외부)
  - 요청당 평균 지연·토큰·비용
  - 플랜 다양성 지표 (유니크 액션 시퀀스 / 전체)
```

**"플랜 다양성 지표"가 W12 블라인드 평가의 예고편이다.** 여기서 60% 미만이 나오면 W12에서 "구분 못 함"이 나올 가능성이 매우 높으므로, 그 시점에 가치 축을 오써링 자동화로 전환할 준비를 시작한다.
