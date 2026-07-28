# Phase 3 (W7–8) — 플랜 캐시 · 프리베이크

> **목적: 2,880개 플랜을 만들어 놓고 런타임 LLM 호출을 없앤다.**
> 여기서 만들어지는 `planstore/`가 이 프로젝트의 **실질적 핵심 산출물**이다.
>
> **게이트: 2,880키 콜드 필 완주 + 캐시 히트율 ≥ 98%**

---

## 1. 주차 배분

| 주 | 내용 |
|---|---|
| W7 | `PlanStore` + `BucketKey` 인덱싱 + 캐시 무효화 규칙 + pinned/rejected 관리 |
| W8 | `Npc.Prebake` CLI (동시성·백오프·재개) + 전량 프리베이크 + 히트율 계측 |

---

## 2. `PlanStore`

**표가 둘이다.** 버킷으로도 찾고 `PlanId`로도 찾는다 — 런타임(`PlanExecutor`·`CognitionScheduler`)이 NPC마다 들고 있는 것은 버킷이 아니라 `NpcStore.PlanId` 이기 때문이다. 버킷 하나에 배열 하나로는 그 조회가 안 된다.

```csharp
// Npc.Planning/PlanStore.cs
public sealed class PlanStore
{
    // [1] 버킷 → PlanId. 2,880 고정 배열. 해시맵 불필요 — BucketKey.ToIndex()가 O(1)
    private readonly int[]        _byBucket    = new int[2880];   // 0 = 미생성
    private readonly int[]        _origin      = new int[2880];   // PlanOrigin. Volatile.Write 에 enum 오버로드가 없다
    private readonly long[]       _hits        = new long[2880];
    private readonly long[]       _misses      = new long[2880];

    // [2] PlanId → 플랜. 런타임의 조회 경로. 첨자 두 번, 할당 0
    //   List<T> 가 아니라 청크 배열이다 — 내부 배열 교체와 Count 갱신 사이에 창이 열려
    //   읽는 쪽이 범위 밖을 볼 수 있다. 청크는 자란다고 기존 청크를 건드리지 않는다
    private readonly CompiledPlan[]?[] _chunks;                   // [0][0] = 최후 플랜
    private readonly int[]             _byArchetype;              // 아키타입 폴백의 PlanId

    public CompiledPlan Resolve(BucketKey key, out PlanOrigin origin)
    {
        int idx = key.ToIndex();
        int id  = Volatile.Read(ref _byBucket[idx]);
        if (id != IdlePlanId) { Interlocked.Increment(ref _hits[idx]); origin = _origin[idx]; return _plans[id]; }

        Interlocked.Increment(ref _misses[idx]);
        origin = PlanOrigin.Fallback;
        return _plans[_byArchetype[key.A.Value]];   // 미스여도 항상 유효한 플랜을 반환한다
    }

    public CompiledPlan this[PlanId id] { get; }   // 런타임이 매 틱 쓴다

    // 재계획 워커·프리베이크만 호출. 원자 교체.
    // docs 초안의 Publish 와 같은 것이다 — 이름은 P1 스텁이 이미 쓰던 SetBucket 으로 통일했다.
    public PlanId SetBucket(BucketKey key, CompiledPlan plan)
    {
        int idx = key.ToIndex();

        // 사람이 고정한 플랜은 덮지 않는다. 이미 걸려 있는 것을 그대로 돌려준다
        if ((PlanOrigin)_origin[idx] == PlanOrigin.Pinned) return new PlanId(_byBucket[idx]);

        PlanId id = Register(plan);
        Volatile.Write(ref _byBucket[idx], id.Value);
        Volatile.Write(ref _origin[idx], (int)plan.Origin);
        return id;
    }
}
```

**`Resolve`는 절대 null을 반환하지 않는다.** 미스여도 폴백을 준다. 이게 시나리오 C(LLM 전면 차단)가 통과하는 이유다.

**락이 없다.** `int` 쓰기가 원자적이고, 읽는 쪽이 조금 낡은 플랜을 봐도 다음 틱에 새 것을 본다. 틱 루프에서 락을 잡으면 그 자체가 병목이 된다.

> P1 스텁(T1-39)이 이미 이 두 표를 갖고 있고 `Register`·`SetBucket`·`SetFallback`·`Resolve`·`HasBucket`·`this[PlanId]`를 노출한다.
> P3는 **내부만 바꾼다.** 위 코드의 `Publish` 는 `SetBucket` 과 같은 것이니 이름을 하나로 통일하고 호출부를 맞춘다.
> `_plans` 가 계속 자라는 것을 막는 회수는 개별 플랜 풀(아래)이 맡는다 — 버킷 플랜은 2,880 상한이라 두지 않아도 된다.

### 개별 오버라이드

버킷 플랜과 별개로, 특정 NPC에게만 붙는 1회성 플랜이 있다.

```csharp
// NpcStore.PlanId 가 음수면 개별 플랜 슬롯을 가리킨다
//   >  0  : PlanStore 레지스트리 id  ← 버킷 인덱스가 아니다
//   == 0  : 최후 플랜 (PlanStore.IdlePlanId)
//   <  0  : 개별 플랜 풀 인덱스 (~value)  ← 슬롯 0 이 -1 이라 0 과 겹치지 않는다
private readonly IndividualPlanPool _individual;   // 링 버퍼. 최대 512개, LRU 회수
```

**`PlanId`는 버킷 인덱스가 아니다.** 같은 플랜을 여러 버킷이 가리킬 수 있고(인접 버킷 재사용 — `docs/12 §7`), 개별 플랜은 버킷이 아예 없다.
`NpcStore.PendingPlanId` 도 `0 = 없음` 이므로 워커가 넘기는 개별 슬롯은 **반드시 음수**여야 한다 (`docs/14 §4`의 `~slot`).

개별 플랜은 **수명이 짧다.** 완료되거나 다음 버킷 전환 시 버킷 플랜으로 되돌아간다. 512개 상한은 "동시에 특별 대우를 받는 NPC 수"의 상한이고, 이게 곧 §14의 재계획 예산과 맞물린다.

---

## 3. 캐시 무효화

```csharp
// Npc.Planning/PlanStoreValidator.cs
public enum InvalidationScope { None, Partial, Full }

// prefixHash 를 인자로 받는다 — MasterDataSet 은 PromptPrefix 를 들고 있지 않다.
// 프리픽스는 Npc.Llm 에 있고 Npc.MasterData → Npc.Llm 참조는 CLAUDE.md §3 이 금지한다.
public static InvalidationScope Compare(Manifest? old, MasterDataSet cur, string prefixHash)
{
    if (old is null) return InvalidationScope.Full;                              // 스토어가 없다
    bool prefixChanged = old.PrefixHash != prefixHash;

    if (!prefixChanged && old.MasterdataHash == cur.ContentHash) return InvalidationScope.None;
    if (prefixChanged) return InvalidationScope.Full;                            // 프롬프트가 바뀜

    // 바뀐 파일들의 범위 중 가장 큰 것. 표는 아래.
    return ChangedFiles(old, cur).Max(ScopeOf);
}
```

| 변경 파일 | 범위 | 이유 |
|---|---|---|
| `world_flags.json` | **Full** | 비트 의미가 바뀌면 모든 플랜의 requires/grants가 무의미 |
| `actions.json` | **Full** | 카탈로그가 프롬프트 프리픽스에 실린다 |
| `archetypes.json` | **Full** | 아키타입 코드가 버킷 인덱스에 들어간다 |
| `context_buckets.json` | **Full** | 인덱싱 자체가 바뀜 |
| `prompt/**` | **Full** | 프리픽스 해시 변경 |
| `pois.json` (추가만) | Partial | 기존 플랜은 유효. 새 POI를 쓰는 플랜만 재생성 |
| `items.json` (추가만) | Partial | 동일 |
| `zones.json` (추가만) | Partial | 플랜에 존 id가 안 들어가지만 3단의 장소 판정이 존을 본다 |
| `poi_distances.bin` | **None** | 실행 시점의 이동 시간만 바뀐다. 플랜의 유효성과 무관 |
| `npc_instances.json` | **None** | 플랜은 개체에 안 묶인다 |
| `fallback_plans.json` | **None** | 폴백은 별도 저장 |
| `interrupts.json` | **None** | 플랜에 인터럽트가 없다 (§03 §1) |
| 표에 없는 파일 | **Full** | 모르는 입력이 바뀌었으면 안전한 쪽으로 |

**이 표가 개발 속도를 좌우한다.** POI를 하나 추가할 때마다 전량 재생성하면 W8 이후 작업이 지옥이 된다.

> **"추가만"은 코드가 검증하지 않는다.** 파일 해시 하나로는 추가와 수정을 가를 수 없다.
> 대신 세 가지가 그것을 받쳐 준다 — (1) `code`·`bit` 번호는 절대 재배치하지 않고 추가는 뒤에만 한다 (`CLAUDE.md §2.4`),
> (2) 플랜에는 절대 POI id 가 들어가지 않는다 (`$home`·`$workplace` 심볼만),
> (3) `items.json` 의 레시피는 프리픽스에 실려 있어 내용이 바뀌면 **프리픽스 해시 검사에서 먼저 걸린다.**
> 그래서 `items.json`의 Partial 행은 프리픽스에 실리지 않는 필드(`stack` 등)만 바뀐 경우에 닿는다.

---

## 4. `Npc.Prebake` CLI

```
Npc.Prebake.exe
  --masterdata ./masterdata
  --out        ./planstore
  --tier       T2                 # T1(로컬) | T2(외부)
  --model      gpt-5-nano
  --concurrency 8                 # AIMD 초기값. ⚠ W1은 외부 API 동시성을 재지 않았다 (T2-19에서 실측)
  --dryrun-sample 1.0             # 프리베이크는 전수 드라이런
  --resume                        # 기존 planstore에서 이어서
  --only "blacksmith@*"           # 부분 재생성 (glob)
  --budget-usd 5.00               # 하드 캡. 초과 시 중단
```

### 실행 흐름

```
1. 마스터데이터 로드 + V1~V11 검증
2. 기존 manifest와 비교 → 무효화 범위 판정
3. 생성 대상 버킷 목록 산출 (Full=2880, Partial=변경분, Resume=미생성분)
4. prebake_priority 순으로 정렬 (Peace 계열 먼저 — 실제로 많이 쓰인다)
5. 프리픽스 워밍업 1회 (캐시 write 유발) ← 반드시 먼저. 안 하면 32개가 동시에 write 비용 지불
6. Channel + N워커로 동시 처리
     - 429 → AIMD 감속 (concurrency /= 2, 지수 백오프)
     - 성공 지속 → concurrency += 1 (상한까지)
7. 각 결과를 4단 검증 → plans/ 또는 rejected/ 에 기록
8. manifest.json 작성
```

### 프리픽스 워밍업이 중요하다

```csharp
// 캐시 write를 1회만 지불하기 위해, 동시 요청 전에 단건을 먼저 던진다
await client.GetResponseAsync([prefixMsg, warmupSuffix], opts);
await Task.Delay(200);   // 캐시 반영 대기
// 이제 동시 N 시작 (N = AdaptiveConcurrency 의 현재값)
```

**로컬 티어에서는 이 실험이 성립하지 않는다.** dotLLM 0.1.0-preview.3 은 `cached_tokens` 를 항상 0으로 보고한다(`W1_env.md §4.4`). 워밍업 효과 실측은 외부 API 로만 한다.

> **실측 결과 — 이 엔진 계열에서 워밍업은 비용을 줄이지 않는다** (`W8_prebake.md §1`, T3-11).
>
> 위 문단의 근거는 "N개 요청이 전부 cache miss로 시작해서 Anthropic 기준 write 할증(1.25~2배)을 N번 낸다"였다.
> Gemini 2.5 Flash Lite 실측에서 **그 전제가 둘 다 없었다** — 파동 안에서 먼저 도착한 요청이 캐시를 만들어
> 나머지가 물려받고(동시 32에서 워밍업 없이 68.6% 적중), 암시적 캐싱에는 write 할증이 없다.
> 동시 8에서 비용 차이는 평균 +1.3%(분산 ±23%), 동시 32에서는 **워밍업이 더 비쌌다**(−10.8%, −17.3%).
>
> **그래도 기본으로 켜 둔다.** 비용 논거가 아니라 (1) Anthropic 명시적 캐싱에서는 전제가 성립하고,
> (2) 2,880건을 던지기 전에 키·엔드포인트·프리픽스가 통하는지 요청 하나로 확인하는 예비 점검이며,
> (3) 요청 1개는 2,880건의 0.03% 라서다.
> Anthropic 계열 실측은 `cache_control` 블록을 실어야 해서 OpenAI 호환 경유로는 할 수 없다.

### AIMD 동시성 제어

```csharp
// tools/Npc.Prebake/AdaptiveConcurrency.cs
sealed class AdaptiveConcurrency
{
    int _current;                 // 초기값 = --concurrency
    readonly int _max, _min = 1;

    public void OnSuccess() { if (++_streak >= 16) { _current = Math.Min(_max, _current + 1); _streak = 0; } }
    public void OnThrottled() { _current = Math.Max(_min, _current / 2); _streak = 0; }
}
```

`SemaphoreSlim`의 카운트를 동적으로 조절하는 대신, 워커가 각자 `_current`를 확인하고 초과분은 대기하는 방식이 단순하다.

### 예산 하드 캡

```csharp
if (_spentUsd + estimate > _budgetUsd)
{
    _log.LogError("예산 초과. 생성 {Done}/{Total} 에서 중단. --resume 으로 이어서 실행 가능", done, total);
    break;   // manifest는 부분 상태로 기록
}
```

버그로 재계획 루프가 도는 사고(리스크 R8)의 1차 방어선이다.

---

## 5. 산출물 검수

프리베이크가 끝나면 **사람이 본다.** 이게 오써링 자동화 검증의 핵심 절차다.

```
tools/review.ps1 --store ./planstore --sample 40
  → 40개 플랜을 무작위 추출해 사이드바이사이드로 출력
  → 검수자가 각각 [채택 / 수정 후 채택 / 폐기] 판정
  → docs/measurements/review_W8.jsonl 에 기록
```

```jsonc
{"bucket":"blacksmith@Evening.War.Cold","verdict":"accept","minutes":1.5}
{"bucket":"farmer@Night.Peace.Storm","verdict":"edit","minutes":6,"note":"폭풍인데 밭에 감"}
{"bucket":"priest@Dawn.Disaster.Fair","verdict":"reject","minutes":2,"note":"기도만 8스텝"}
```

**이 파일이 W12 보고서의 핵심 수치다.**

```
오써링 절감률 = 1 − (LLM 생성 + 검수 시간) / (수작성 시간)
              = 1 − (3분 + 2880 × 평균검수시간) / (2880 × 15분)
```

검수 시간이 평균 2분이면 절감률 약 87%. 검수 시간이 8분까지 오르면 47%. **여기서 나오는 숫자가 R&D의 결론이다.**

수정 후 채택된 플랜은 `pinned/`로 옮긴다. 이후 프리베이크가 덮어쓰지 않는다.

---

## 6. 히트율 계측

```csharp
// Npc.Planning/CacheMetrics.cs
_meter.CreateObservableGauge("npc.plan.cache.hit_ratio",
    () => (double)_hits.Sum() / (_hits.Sum() + _misses.Sum()));

_meter.CreateObservableGauge("npc.plan.cache.cold_buckets",
    () => _byBucket.Count(id => id == IdlePlanId));  // 미생성 버킷 수

// 미스 상위 버킷 — 프리베이크 우선순위 튜닝의 입력
_meter.CreateObservableGauge("npc.plan.cache.top_miss",
    () => _misses.Index().OrderByDescending(x => x.Item).Take(10)...);
```

`Meter` 계측기는 OpenTelemetry 수집용이고, **대시보드는 `GET /metrics` 의 JSON 한 장만 읽는다**(`docs/11 §10`).
그 JSON을 만드는 `NpcMeter` 는 `Npc.Host` 의 `internal` 타입이라 `Npc.Planning` 에서 직접 못 쓴다 —
카운터는 `CacheMetrics` 가 들고 있고 `NpcMeter` 가 스냅샷 시점에 읽어 간다. 패널은 T4-18이 그린다.

### 히트율 98%가 안 나오는 원인

| 원인 | 확인 | 대응 |
|---|---|---|
| 미생성 버킷이 남아있음 | `cold_buckets > 0` | `--resume`으로 마저 생성 |
| 개별 재계획이 과다 | 개별 플랜 풀 회전율 | §14의 우선순위 큐 가중치 조정. `w2`(노후도) 하향 |
| 버킷 전환이 너무 잦음 | `GameTimeChanged` 빈도 | 시간대 6단계가 과하면 4단계로 축소 검토 |
| 특정 아키타입만 미스 | `top_miss` 확인 | 그 아키타입의 검증 실패율 확인 (W6 이슈일 가능성) |

---

## 7. 게이트 확인

자동화는 `tests/Npc.Tests/Gates/Phase3GateTests.cs` 이고 판정 결과는 `docs/measurements/P3_gate.md` 다.

**항목마다 근거가 어디서 오는지가 다르다.** 기제(mechanism)만 보면 되는 것은 상시 테스트가 잡지만,
실측 산출물이 있어야 판정되는 것은 `Category=Gate` 로 갈라 두었다 — 산출물이 없으면 **실패한다.**
없는 것을 통과로 세면 게이트가 거짓이 된다 (`CLAUDE.md §5`).

| # | 항목 | 근거 | 상시 |
|---|---|---|---|
| 1 | ~~2,880개 버킷 중 생성 완료 ≥ 95%~~ → **미생성 버킷이 폴백으로 안전하게 해소된다** | 폴백 해소 (기제) | ○ |
| 2 | 프리베이크 wall-clock ≤ 5분 | `manifest.json` | — |
| 3 | 프리베이크 실비용 ≤ $5 | `manifest.json` | — |
| 4 | 프롬프트 캐시 적중률 — **엔진 계열별** (암시적 ≥ 40% · 명시적 ≥ 95%) | `manifest.json` | — |
| 5 | 시나리오 A(7게임일)에서 **캐시 히트율 ≥ 98%** | 95% 채운 스토어로 실제 7게임일 주행 | ○ |
| 6 | `pinned/` 플랜이 재프리베이크로 덮어써지지 않는다 | 파일 계층까지 한 바퀴 | ○ |
| 7 | POI 1개 추가 시 Partial 무효화가 동작한다 (전량 재생성 안 함) | 무효화 판정 + 대상 산출 | ○ |
| 8 | `--budget-usd` 초과 시 중단하고 `--resume`으로 이어진다 | 예산 가드 + 대상 산출 | ○ |
| 9 | 검수 40건 샘플의 채택률(accept + edit) ≥ 80% | `docs/measurements/review_W8.jsonl` (**사람이 만든다**) | — |

> **항목 2 의 기준은 실측으로 갱신한다.** "동시 32 → 3분" 은 상위 계획의 추정이고 W1 은 외부 동시성을
> 재지 않았다. 기준을 맞추려고 동시성을 올려 429 를 맞지 않는다 — 실측이 5분을 넘으면 그 값을 적고
> `../TASKS.md §3` 에 남긴다.

> **항목 5 는 95% 채운 스토어로 잰다.** 전량 채운 스토어로 재면 히트율은 정의상 100% 이고
> 아무것도 증명하지 못한다. 비워 두는 5% 는 `prebake_priority` 가 뒤로 미룬 꼬리이므로,
> 이 항목은 "우선순위 정렬이 실제로 덜 쓰이는 버킷을 뒤로 보내는가"까지 같이 잰다.

### 2026-07-28 개정 — 항목 1 의 완료율과 항목 4 의 기준

**항목 1 에서 "생성 완료 ≥ 95%" 를 뺐다. 폴백 해소만 남긴다.**

분모가 2,880 이라서다. `RnD_Report` 권고 2 로 **도달 집합(264)만 만드는 방침**을 택했으므로
완료율은 정의상 6.8~24.9% 에 머물고 **영원히 미달**이다. 분모를 "선언된 대상 집합"으로 바꿔도
73.9% 라 여전히 미달인데, 그 미달의 원인은 **P2 통과율**이고 P3 의 결선 문제가 아니다 —
즉 분모를 바꾸면 이 항목이 `docs/12 §9` 와 **같은 것을 두 번 재게 된다.**

남기는 것은 기제 하나다 — **미생성 버킷이 있어도 `Resolve` 가 null 을 주지 않는다.**
그것이 이 항목이 원래 지키려던 것이고, 완료율은 그 대리 지표였을 뿐이다.

**항목 4 의 하한을 엔진 계열로 나눈다.**

| 캐싱 방식 | 하한 | 예 |
|---|---|---|
| **명시적** (`cache_control` 를 요청에 싣는다) | **≥ 95%** | Anthropic |
| **암시적** (제공사가 알아서 판정) | **≥ 40%** | Gemini 계열 |

95% 는 **명시적 캐싱을 전제한 값**이었다. 암시적 캐싱은 요청 단위로 전부 적중하거나 전부
미적중하고 그 판정이 비결정적이라(`W8_prebake.md §2`) 우리가 프리픽스를 아무리 고정해도
95% 에 닿을 수 없다. 실측은 **42.6% → 45.4% → 69.2%** 로 회차 규모에 따라 움직인다.

40% 는 **손익 기준**이다 — 적중분이 1/10 단가이므로 적중률 40% 면 비용이 약 1/3 준다
(`cost_actual.md §3` 실측: 적중률 40.6% → 절감 33.4%). 그 아래로 내려가면
"프리픽스를 고정한 값어치가 없다" 고 말할 수 있다.

> **판정 자체는 여전히 `RequireFullRun` 뒤에 있다.** 전량 회차를 돌리지 않기로 했으므로
> (2026-07-28 결정) 항목 2·3·4 는 판정되지 않은 채로 남는다. `TASKS.md §3` 참조.

---

## 8. 이 단계에서 흔한 실수

| 실수 | 결과 | 방지 |
|---|---|---|
| 프리픽스 워밍업 생략 | Anthropic 명시적 캐싱에서는 write 할증을 동시성 수만큼 낸다. **Gemini 암시적 캐싱에서는 손해가 없다** — `W8_prebake.md §1` 실측 | §4 |
| `manifest.json`에 생성 시각을 `DateTime.Now`로 | 결정론 깨짐, 리플레이 불가 | 시각은 외부에서 인자로 주입 |
| 검증 실패분을 조용히 버림 | 품질 개선의 원자료 소실 | `rejected/`에 실패 코드와 함께 전량 보존 |
| 히트율을 전체 평균으로만 봄 | 특정 아키타입의 재앙적 미스를 놓침 | 버킷별·아키타입별 분해 |
| `pinned/`를 git에 안 올림 | 사람의 수정 작업이 날아감 | `planstore/pinned/`는 반드시 버전 관리. `plans/`는 `.gitignore` |
| 드라이런을 프리베이크에서 생략 | 데드락 플랜이 런타임에 배포됨 | 프리베이크는 전수 드라이런 (`--dryrun-sample 1.0`) |
