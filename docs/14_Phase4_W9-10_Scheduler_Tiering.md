# Phase 4 (W9–10) — 재계획 스케줄러 · 3-티어 라우팅 · 부하 테스트

> **목적: 5,000 NPC를 실제로 돌리고, LLM 예산을 "가장 필요한 NPC"에 집중시킨다.**
>
> **게이트: NPC 5,000에서 캐시 히트율 ≥ 98%, GPU 사용률 ≤ 60%, 틱 p99 ≤ 20ms**

---

## 1. 주차 배분

| 주 | 내용 |
|---|---|
| W9 | 우선순위 재계획 큐 + 레이트리미터 + 토큰 하드 캡 + 3-티어 라우터 |
| W10 | NPC 5,000 부하 테스트 + 가중치 튜닝 + 대시보드 완성 + 시나리오 B 통과 |

---

## 2. 우선순위 재계획 큐

§상위계획 §11.2의 핵심. **라운드로빈이 아니라 점수순으로 뽑는다.**

```csharp
// Npc.Planning/ReplanQueue.cs
public sealed class ReplanQueue
{
    // 고정 크기 이진 힙. 할당 없음. 중복 삽입은 점수 갱신으로 처리.
    private readonly int[]   _heap;          // heap 위치 → npc index
    private readonly float[] _score;         // npc index → 현재 점수
    private readonly int[]   _heapPos;       // npc index → heap 위치 (-1 = 미포함)
    private int _count;
    private readonly int _capacity;          // 기본 min(npcCapacity, 4096)

    // 반환값은 "새 항목이 들어갔는가" 다. 중복은 점수만 갱신하고 false.
    public bool TryEnqueue(int npc, float score)
    {
        if (_heapPos[npc] >= 0) { UpdateScore(npc, MathF.Max(_score[npc], score)); Deduplicated++; return false; }
        if (_count == _capacity)
        {
            int lowest = LowestSlot();                              // 최솟값은 반드시 잎이다
            if (!Precedes(npc, score, _heap[lowest])) return false;  // 최하위보다 낮으면 거절
            RemoveAt(lowest);
        }
        Insert(npc, score);
        return true;
    }

    public bool TryEnqueueUrgent(int npc, float urgency) => TryEnqueue(npc, 1000f + urgency);

    public int DequeueMax();
    public bool TryDequeueMax(out int npc);
}
```

**용량 4096으로 상한을 둔다.** 큐가 무한히 자라면 오래된 요청이 쌓여서 "이미 상황이 바뀐 NPC를 재계획"하게 된다. 넘치면 낮은 점수부터 버린다 — 버려진 NPC는 기존 플랜을 계속 쓰므로 안전하다.

> **T4-01 에서 초안 세 곳을 고쳤다.**
>
> | 초안 | 실제 | 이유 |
> |---|---|---|
> | 중복 삽입에 `return true` | `return false` | 호출부가 "몇 마리가 새로 대기하게 됐나" 를 셀 수 없다. P1 의 `Deduplicated` 카운터와 대시보드의 "초당 유입"이 이 구분에 기대고 있다 |
> | `_score[_heap[_count - 1]]` 을 최하위로 | 잎 구간을 훑어 실제 최솟값 | 최대 힙에서 `_heap[_count-1]` 은 *어떤* 잎일 뿐 최솟값이 아니다. 그대로 두면 급한 요청이 낮은 점수에 밀린다. 잎 스캔은 **포화 상태에서만** 돈다 |
> | 동점 처리 없음 | npc 첨자 오름차순 | 힙 구조에 순서를 맡기면 같은 점수의 처리 순서가 삽입 이력에 따라 달라져 리플레이가 깨진다 (CLAUDE.md §2.3) |
>
> 인터럽트 경로(`InterruptMatcher`)는 `TryEnqueueUrgent` 를 쓴다 — `rule.Urgency` 를 그대로 넣으면
> 최댓값 100 이라 일반 재계획(이탈 판정 40 · 스텝 실패 50)과 뒤섞인다.

### 점수 함수

```csharp
// Npc.Planning/ReplanScorer.cs
//
// NpcStore 를 받지 않는다 — 계산에 쓰는 네 값만 값 타입으로 받는다 (아래 주의 참조).
public readonly record struct NpcReplanState(
    byte Lod, WorldFlags Flags, long PlanAssignedTick, byte PendingUrgency);

public static float Score(in NpcReplanState s, CompiledPlan plan, Tick now, in Weights w)
{
    float proximity = s.Lod switch { 0 => 1.0f, 1 => 0.5f, 2 => 0.1f, _ => 0.0f };
    float staleness = MathF.Min(1f, (now.Value - s.PlanAssignedTick) / (float)w.StaleTicks);
    float deviation = MathF.Min(1f, BitOperations.PopCount(
                          (ulong)((plan.RequiredFlags & ~s.Flags) |
                                  (plan.ForbiddenFlags &  s.Flags))) / 8f);
    float urgency   = s.PendingUrgency / 100f;         // 인터럽트가 넣은 값

    return w.W1 * proximity + w.W2 * staleness + w.W3 * deviation + w.W4 * urgency;
}

public readonly record struct Weights(float W1, float W2, float W3, float W4, int StaleTicks)
{
    // W10(T4-17)에서 튜닝. 초기값 = B 세트:
    public static readonly Weights Default = Baseline;

    // 아래 표의 4세트. T4-17 의 A/B 가 Weights.AbSets 로 이 순서대로 돈다.
    public static Weights ProximityFirst => new(5.0f, 0.2f, 1.5f, 4.0f, 36_000);
    public static Weights Baseline       => new(3.0f, 0.5f, 2.0f, 4.0f, 36_000);
    public static Weights DeviationFirst => new(2.0f, 0.3f, 4.0f, 4.0f, 36_000);
    public static Weights Uniform        => new(1.0f, 1.0f, 1.0f, 1.0f, 36_000);
}
```

**`W1`(플레이어 근접도)을 크게 잡는 것이 핵심이다.** 이게 §2.2에서 지적한 "관측되지 않는 연산" 문제의 해결책이다. LOD 3(비활성) NPC는 `proximity = 0`이라 인터럽트가 없는 한 사실상 재계획되지 않는다.

네 항이 모두 [0, 1] 로 정규화되므로 점수 상한은 `W1+W2+W3+W4`(기본 9.5) 이고, 인터럽트가 넣는 `1000 + urgency` 와 겹치지 않는다. **이탈 항에도 `min(1, …)` 이 필요하다** — 없으면 플래그가 9개 이상 어긋난 NPC 하나가 인터럽트 근처까지 올라간다.

> ⚠ **`Score` 는 `NpcStore` 를 받을 수 없다.** `NpcStore` 는 `Npc.Runtime` 에 있고
> `Npc.Runtime → Npc.Planning` 이 이미 있어(CLAUDE.md §3) 역방향 참조는 순환이다.
> `IPlanVocabulary`(T1-23)·`IDryRunValidator`(T2-13) 와 같은 방법으로 갈랐다 —
> 계산에 실제로 쓰이는 네 값을 `NpcReplanState`(17바이트, 할당 0) 로 받고,
> SoA 배열에서 뽑아 넘기는 것은 `CognitionScheduler`(Npc.Runtime) 가 한다.
>
> **`PlanAssignedTick`·`PendingUrgency` 는 T4-02 에서 `NpcStore` 에 추가했다.**
> 둘 다 재계획 경로에서만 읽으므로 **콜드 영역**이다 — 핫 배열에 넣으면 `HotBytesPerNpc`(23B/NPC)가
> 늘어 `docs/11 §3`의 L2 목표가 깨진다.
> 쓰는 곳은 두 군데다: `PlanExecutor.AssignPlan`(배정 틱 기록 + 긴급도 소진)과
> `InterruptMatcher.Handle`(긴급도 기록). 후자는 큐에도 넣지만 **큐와 별개로 남긴다** —
> 큐에서 밀려나거나 인터럽트 슬롯 상한(T4-03)에 걸린 NPC 도 다음 스캔에서 우대받아야 한다.
>
> **점수 타입도 바뀌었다** (T4-01). P1 스텁은 `TryEnqueue(int npc, int score)` 였다.

### 가중치 튜닝 절차 (W10)

```
1. 시나리오 A를 4개 가중치 세트로 각각 실행
2. 측정: 캐시 히트율 / LLM 요청 수 / "플레이어 근처 NPC의 플랜 신선도"
3. 목표: LLM 요청 수를 최소화하면서 근처 NPC의 신선도를 최대화
```

| 세트 | W1 | W2 | W3 | W4 | 성격 |
|---|---|---|---|---|---|
| A (근접 우선) | 5.0 | 0.2 | 1.5 | 4.0 | 플레이어 근처만 똑똑 |
| B (기본) | 3.0 | 0.5 | 2.0 | 4.0 | |
| C (이탈 우선) | 2.0 | 0.3 | 4.0 | 4.0 | 플랜이 깨진 NPC 우선 |
| D (균등) | 1.0 | 1.0 | 1.0 | 1.0 | 대조군 |

---

## 3. 레이트리미터 — 토큰 버짓

```csharp
// Npc.Core/Planning/Tier.cs      — 계약. Npc.Llm 과 Npc.Planning 이 같이 본다
public enum Tier { None = 0, T1 = 1, T2 = 2 }

public interface IReplanBudget
{
    bool TryAcquire(Tier tier, int estimatedTokens, Tick now);
    Tier Acquire(Tier requested, int estimatedTokens, Tick now);   // T2 → T1 → None
    bool Peek(Tier tier, int estimatedTokens, Tick now);
    void Settle(Tier tier, int actualTokens, double actualCostUsd);
}

// Npc.Planning/ReplanBudget.cs   — 구현
public sealed class ReplanBudget : IReplanBudget
{
    private readonly TokenBucket _t1;     // T1 초당 요청 — GPU 용량이 상한
    private readonly TokenBucket _t2;     // T2 초당 요청 — rate limit 이 상한
    private long   _tokensToday;          // 일일 토큰 하드 캡
    private double _costToday;            // 일일 비용 하드 캡
}
```

> ⚠ **`TieredPlanCompiler`(Npc.Llm)가 `ReplanBudget`(Npc.Planning)을 직접 참조할 수 없다.**
> 둘은 형제 프로젝트다 (CLAUDE.md §3 — `Npc.Llm ← Core, MasterData`). 그래서 `Tier` 열거형과
> `IReplanBudget` 계약은 `Npc.Core` 에 두고 구현만 `Npc.Planning` 에 둔다 —
> `IDryRunValidator`(T2-13)·`IPlanReuseSource`(T2-11) 와 같은 방법이다.
>
> **시계는 `Tick` 이다.** `DateTime`·`Stopwatch` 를 보면 리플레이가 깨진다 (CLAUDE.md §2.3).
> 틱은 실시간 10Hz 이므로 하루 = 864,000 틱이다.

### 상한 4개 — T4-05 에서 실측으로 확정 (2026-07-26)

| 상한 | 값 | 근거 (실측 파일·행) |
|---|---|---|
| T1 초당 요청 | **0.195 req/s** (워커 1) · **0.39** (워커 2, 기본) | `W1_perf.csv` · `llamacpp-qwen3-8b` · `Cache=on` 30건 `TotalMs` 중앙값 **5,139.4 ms** → 1/5.1394. 같은 파일 4B 는 3,701.3 ms(0.270)인데 로컬 모델이 미확정(T0-12)이라 **느린 쪽**으로 잡았다 |
| T2 초당 요청 | **2.5 req/s** | `W8_prebake.md §4` 파일럿 288버킷 — **115.0 s / 288건 = 2.504 req/s**, 최대 동시 24 에서 **429 0회**. 상향 여지는 있으나 실측된 값은 이것뿐이다 (`W6_compile_stats.md §6`) |
| 일일 토큰 캡 | **15 M** | 정책값. 실측 **23,109 tok/요청**(6,655,438/288, 프리픽스 13,488 × 재시도 포함)으로 환산하면 **T2 약 649건/일** |
| 일일 비용 캡 | **$2** | 정책값. 실측 단가 **$0.001777/요청**($0.5119/288)로 환산하면 약 1,125건/일 — 토큰 캡(649건)이 먼저 걸려 두 캡이 어긋나지 않는다 |
| (파생) T2 단가 | **$0.0769 / M tok** | `$0.5119 / 6,655,438 tok`. 공개 단가보다 낮은 것은 캐시 적중 42.6% 가 할인가로 계산되기 때문이다. Poe 과금은 포인트라 USD 자체가 추정이다 |

버스트는 초당 상한의 **4초치**다(`max(1, rate × 4)`). T1 이 0.195 req/s 라 버스트가 1 이면 인터럽트가 몰린 순간 한 건만 나가고 나머지가 5초씩 기다린다.

> ⚠ **원래 값(T1 0.5 · T2 16 · "시나리오 B 20,000건/일의 2배")은 세 군데가 실측으로 무너졌다.**
>
> | 항목 | 계획 가정 | 실측 |
> |---|---|---|
> | 로컬 요청당 지연 | 1.75 s | **4B 3.7 s · 8B 5.1 s** (`W1_perf.csv` 중앙값, 프리픽스 캐시 적중) |
> | 외부 동시성 | 32 | 파일럿에서 **동시 24 · 429 0회**. 32 를 확인한 회차는 없다 (`W8_prebake.md §2` 는 65요청) |
> | 요청당 토큰 | ~375 (15M ÷ 40,000) | **23,109** — 60배 어긋난다 |
>
> 요청당 토큰의 60배 차이는 프리픽스가 축소판 4,409 → 정식 **13,488 tok** 으로 커진 결과다(`W6_compile_stats.md §1`).
> **그래도 15M 을 올리지 않는다** — 시나리오 B 의 20,000건/일은 **T1(로컬·무비용)이 받는다.**
> T2 로 가는 것은 아키타입 플랜(프리베이크 + 런타임 버킷 미스)뿐이고, 히트율 98% 목표에서 그 수는 세 자리다.
> 전량 프리베이크(2,880건 · 66.6M tok · $5.12)는 런타임이 아니라 `tools/Npc.Prebake` 의 몫이고
> 그쪽은 `BudgetGuard`(T3-09)의 `--budget-usd` 가 지킨다.

**GPU 사용률 60% 목표는 T1 초당 요청을 그만큼 더 낮추는 것으로 달성한다.** 남는 40%는 스파이크 흡수용이다.
다만 W1 측정 기기는 **VRAM 8GB** 라 8B Q4_K_M 가중치(4,789 MiB) + KV 캐시가 거의 꽉 찬다 — GPU 사용률 상한은 VRAM 여유와 같이 봐야 한다 (`W1_env.md §4.3`).

---

## 4. 3-티어 라우터

§상위계획 §10.4를 구현한다.

```csharp
// Npc.Llm/TieredPlanCompiler.cs
public sealed class TieredPlanCompiler : IPlanCompiler
{
    public async ValueTask<PlanCompileResult> CompileAsync(PlanRequest req, CancellationToken ct)
    {
        var tier = SelectTier(req);
        if (!_budget.TryAcquire(tier, Estimate(req)))
        {
            tier = Downgrade(tier);                        // T2 → T1 → 거절
            if (tier == Tier.None) return Rejected(req);
        }
        try   { return await _compilers[tier].CompileAsync(req, ct); }
        catch (Exception ex) when (tier == Tier.T2)
        {
            _metrics.TierFailover.Add(1);
            return await _compilers[Tier.T1].CompileAsync(req, ct);   // 외부 장애 → 로컬
        }
    }

    private Tier SelectTier(in PlanRequest req) =>
        req.Quality == PlanQuality.Archetype ? Tier.T2        // 재사용 多 → 고품질
      : _localQueueDepth > _spilloverThreshold ? Tier.T2      // 로컬 폭주 → 스필오버
      : Tier.T1;                                              // 1회용 → 로컬
}
```

### 티어 선택 규칙

| 요청 종류 | 티어 | 이유 |
|---|---|---|
| 아키타입 플랜 (프리베이크) | **T2** | 수천 NPC가 재사용 → 품질에 투자할 가치가 있다 |
| 아키타입 플랜 (런타임 미스) | **T2** | 동일 |
| 개별 NPC 재계획 (LOD 0) | **T1** | 1회용. 저지연·무비용 |
| 개별 NPC 재계획 (LOD 1+) | **T1** | 동일 |
| T1 큐 깊이 > 임계(64) | **T2** | 스필오버 |
| 예산 초과 | **T1 강등** → 거절 | 비용 방어 |

### 워커

```csharp
// Npc.Planning/ReplanWorker.cs : BackgroundService
protected override async Task ExecuteAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        if (!_queue.TryDequeueMax(out int npc)) { await Task.Delay(50, ct); continue; }

        var req = BuildRequest(npc);
        var res = await _compiler.CompileAsync(req, ct);
        if (res.Plan is { } plan)
        {
            int slot = _individualPool.Rent(npc, plan);
            Volatile.Write(ref _store.PendingPlanId[npc], ~slot);   // 실행기가 스텝 경계에서 스왑
        }
        _metrics.Record(res.Stats);
    }
}
```

**워커 수**: T1은 **2개**, T2는 8개. 각각 별도 `BackgroundService`. 둘 다 설정으로 뺀다.

> ⚠ **"로컬은 배칭이 없어 워커를 늘려도 이득이 없다"는 W1 실측과 어긋난다.**
>
> | 엔진 | 동시 2 | 동시 4 | 동시 8 |
> |---|---|---|---|
> | `llamacpp-gemma3-4b` | 12.07 req/s | 11.67 | 11.76 |
> | `llamacpp-qwen3-8b` | 2.15 req/s | 2.11 | **0.36** |
>
> 동시 2에서 처리량이 크게 오르고 4까지 평평하다. **8B가 동시 8에서 붕괴하는 것은 배칭 부재가 아니라 VRAM 부족**이다 — KV 캐시가 넘쳐 PCIe 페이징이 걸린다(`W1_env.md §4.3`).
> 즉 "이득이 없다"가 아니라 **"이득 구간이 좁다"** 가 맞다. 동시 1 측정치(8B 평균 13,197ms)는 모델 로드가 섞여 있어 비교 기준으로 쓰지 않는다.
> 최종 워커 수는 T4-15에서 1/2/4 를 재서 정한다.

---

## 5. 시간대 전환 스파이크 완화

`GameTimeChanged` 수신 시 5,000마리가 동시에 버킷 플랜을 갈아탄다. 이건 LLM 호출이 아니라 **순수 배열 쓰기**지만, 5,000회를 한 틱에 하면 스파이크가 생긴다.

```csharp
// Npc.Runtime/BucketTransition.cs
// npcId 해시로 ±300틱(게임시간 ±5분) 지터를 주어 분산
static int JitterTicks(NpcId npc) => PlanHash.Jitter(npc, Salt, JitterSpread);   // T1-61

// 전환 예약(카운팅 정렬) → 각 NPC가 자기 시각에 도달하면 스왑
```

지터는 **결정론적**이어야 한다(리플레이 재현). `Random` 금지, `npcId` 해시만 사용.

측정: 전환 구간의 틱 시간 p99가 평시 대비 2배를 넘지 않아야 한다.

> **T1-61이 이미 절반을 끝냈다.** 지터·예약·`Apply`·`PlanSwapper` 결선이 있고, `RequestSwap` 은 버킷 키 4차원을 다 만들어(아키타입 + 시간대 + 플래그에서 읽은 RegionState·Climate) `PlanStore.Resolve` 로 새 플랜을 받는다.
> **빠진 것은 전환 계기다.** `Tick` 이 `clock.TimeOfDayChanged` 만 보므로 `ZoneStateChanged`·`WeatherChanged` 가 와도 예약이 안 걸린다 — 플래그는 이미 바뀌었는데 다음 시간대 경계까지 낡은 플랜을 쓴다. T4-13에서 채운다.
> 그때 **존 이벤트는 그 존만** 예약하게 한다. `Schedule` 은 전원을 다시 정렬하므로(O(N)) 존 이벤트마다 5,000마리를 다시 세면 완화하려던 스파이크가 그대로 돌아온다.

---

## 6. 부하 테스트 (W10)

```
Npc.Host --loopback --npcs 5000 --time-scale 60 --days 7 --scenario scenarios/siege.jsonl
```

### 측정 매트릭스

| 축 | 값 | 옵션 |
|---|---|---|
| NPC 수 | 500 / 1,000 / 2,500 / 5,000 / 10,000(스트레스) | `--npcs` |
| 시간 배속 | 1 / 60 / 600 | `--time-scale` |
| 티어 구성 | T0만 / T0+T1 / T0+T1+T2 | `--tier` ⚠ **아직 없다** |
| 플레이어 봇 | 0 / 20 / 100 | `--player-bots` |

> `HostOptions` 에는 지금 `--no-llm` 하나뿐이라 티어 축을 못 가른다. T4-15에서 `--tier none|t1|t2|all` 을 추가하고 `--no-llm` 을 `--tier none` 의 별칭으로 남긴다(P1 게이트 스크립트가 쓴다).
> 틱 지연은 **페이싱을 켜고** 잰다. `--max-speed` 는 처리량 측정용이다 (`P1_gate.md §4`).

### 기록 지표

```
틱:        p50, p95, p99, 최대, 오버런 횟수
인지:      틱당 스캔 대상, 밴드 이동 수
큐:        깊이 p50/p99, 거절 수, 평균 대기 시간
캐시:      히트율(전체/아키타입별), 콜드 버킷 수, 개별 플랜 풀 회전율
LLM:       티어별 요청 수/지연/실패, 토큰, 비용, 캐시 적중률
링크:      명령 송출/초, 드롭 수, 이벤트 수신/초, 시퀀스 갭
GC:        Gen0/1/2 횟수, 할당량/초, 힙 크기
GPU:       사용률, VRAM (nvidia-smi 폴링)
```

### 스케일 곡선 확인

NPC 수 대비 각 지표가 **선형인지** 본다. 초선형(superlinear)이면 어딘가에 O(n²)가 있다.

| 지표 | 기대 |
|---|---|
| 틱 시간 | O(n) — 실행기가 전체 순회 |
| 인지 스캔 | **O(1)** — 슬라이스가 고정 비율이므로 |
| LLM 요청 | **O(1)** — 예산 상한에 걸림 |
| 명령 송출 | O(n) |
| 메모리 | O(n) |

**"인지 스캔이 O(1)"이 §11.3 설계의 검증 지점이다.** NPC를 10배로 늘려도 틱당 스캔 대상이 10배가 되면 안 된다 (슬라이스 수를 비례해서 늘리므로).

> **주의: 지금 이 지표는 그냥 재면 항상 O(1)로 보인다.** `CognitionScheduler.MaxScansPerTick = 150` 이 상한을 걸고 있어서다 —
> P1 실측에서 NPC 5,000 이 정확히 150 이었다(상한에 붙음. 상한이 없으면 612까지 간다, `docs/11 §4`).
> 차수 판정은 **상한을 푼 회차를 따로 돌려서** 한다. 상한이 걸린 회차는 "굶는 NPC 없이 잘리는가"를 보는 용도다.

---

## 7. 시나리오 B 통과 (W10)

§00 문서 §2의 공성 시나리오를 통과시킨다.

```
검증 항목
[ ] ZoneStateChanged(War) 수신 → 인터럽트 경로로 1틱 내 즉시 반응 (경비병 무기 장비 등)
[ ] 버킷 키가 *.War.* 로 전환 → 3초 내 마을 전체 플랜 스왑 완료
[ ] 캐시 미스가 발생한 버킷만 LLM 호출 (전량 재생성 아님)
[ ] 전환 구간 틱 p99 ≤ 40ms (평시 20ms의 2배)
[ ] 아키타입별 행동 변화가 로그에서 확인된다
      guard    : Patrol → Guard($gate)
      farmer   : Farm → Flee($nearest_safe)
      blacksmith: Craft(tool) → Craft(weapon)
[ ] Peace 복귀 시 원래 플랜으로 복원
```

**"아키타입별로 다르게 반응한다"가 이 시나리오의 핵심이다.** 전부 똑같이 도망가면 LLM을 쓴 의미가 없다. 여기서 실패하면 W6의 플랜 다양성 지표를 다시 본다.

---

## 8. 대시보드 완성

W4의 5개 패널에 추가한다.

| 패널 | 내용 |
|---|---|
| **캐시** | 히트율 시계열, 콜드 버킷 수, 아키타입별 히트율 히트맵 (40 × 72) |
| **재계획** | 큐 깊이, 티어별 처리율, 점수 분포 히스토그램, 거절 수 |
| **비용** | 누적 토큰/비용, 일일 캡 소진율, 티어별 분해, 프롬프트 캐시 적중률 |
| **NPC 추적** | NPC 1마리를 선택해 현재 플랜·스텝·플래그·최근 이벤트를 실시간 표시 |

**"NPC 추적" 패널이 데모에서 제일 설득력이 있다.** 대장장이 한 마리를 찍어놓고 하루가 흘러가는 걸 보여주는 것이 숫자 표보다 강하다.

히트맵(40 아키타입 × 72 버킷)은 어느 조합이 실제로 쓰이는지 한눈에 보여준다 — 대부분이 비어 있을 것이고, 그 자체가 **"2,880개가 아니라 300개면 충분했다"** 는 발견일 수 있다. W12 보고서에 넣을 가치가 있다.

---

## 9. 게이트 확인

- [ ] NPC 5,000, 7게임일 완주
- [ ] 캐시 히트율 ≥ 98%
- [ ] 틱 p99 ≤ 20ms
- [ ] 인지 스캔 ≤ 150/틱 (5,000에서도, 10,000에서도)
- [ ] GPU 사용률 ≤ 60%
- [ ] 시나리오 B 전 항목 통과
- [ ] 일일 토큰 캡 초과 시 T2 → T1 자동 강등 동작
- [ ] T2 강제 실패 주입 시 T1 페일오버 동작
- [ ] 틱 루프 Gen0 GC = 0
- [ ] 큐 거절이 발생해도 해당 NPC가 정상 동작 (기존 플랜 유지)

---

## 10. 이 단계의 함정

| 함정 | 증상 | 대응 |
|---|---|---|
| 큐에 같은 NPC가 중복 삽입 | 큐가 즉시 포화 | `_heapPos`로 중복 검출 후 점수 갱신 |
| 재계획 결과가 이미 낡음 | 큐 대기 중 상황이 바뀜 | 큐에 넣을 때의 플래그 스냅샷을 저장, 적용 시점에 크게 달라졌으면 폐기 |
| 인터럽트가 큐를 독점 | 일반 재계획이 영원히 안 됨 | 인터럽트 슬롯을 큐 용량의 50%로 제한 |
| 워커가 틱 루프를 블록 | 틱 p99 폭증 | 워커는 반드시 별도 `BackgroundService`. 공유 상태는 `Volatile` 읽기/쓰기만 |
| T2 페일오버가 무한 재시도 | 외부 장애 시 지연 폭발 | 서킷 브레이커. 연속 실패 5회 → 60초 차단 |
| 개별 플랜 풀 고갈 | 새 재계획이 반영 안 됨 | LRU 회수 + 풀 회전율 메트릭 감시 |
| 가중치 튜닝을 감으로 | 재현 불가 | §2의 4세트 A/B를 스크립트로 자동화 |
