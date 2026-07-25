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
    private readonly int[]   _heap;          // npc index
    private readonly float[] _score;         // npc index → 현재 점수
    private readonly int[]   _heapPos;       // npc index → heap 위치 (-1 = 미포함)
    private int _count;
    private readonly int _capacity = 4096;

    public bool TryEnqueue(int npc, float score)
    {
        if (_heapPos[npc] >= 0) { UpdateScore(npc, MathF.Max(_score[npc], score)); return true; }
        if (_count == _capacity)
        {
            if (score <= _score[_heap[_count - 1]]) return false;   // 최하위보다 낮으면 거절
            EvictLowest();
        }
        Insert(npc, score);
        return true;
    }

    public void TryEnqueueUrgent(int npc, float urgency) => TryEnqueue(npc, 1000f + urgency);

    public int DequeueMax();
}
```

**용량 4096으로 상한을 둔다.** 큐가 무한히 자라면 오래된 요청이 쌓여서 "이미 상황이 바뀐 NPC를 재계획"하게 된다. 넘치면 낮은 점수부터 버린다 — 버려진 NPC는 기존 플랜을 계속 쓰므로 안전하다.

### 점수 함수

```csharp
// Npc.Planning/ReplanScorer.cs
public static float Score(int i, NpcStore s, CompiledPlan plan, Tick now, in Weights w)
{
    float proximity = s.Lod[i] switch { 0 => 1.0f, 1 => 0.5f, 2 => 0.1f, _ => 0.0f };
    float staleness = MathF.Min(1f, (now.Value - s.PlanAssignedTick[i]) / (float)w.StaleTicks);
    float deviation = BitOperations.PopCount(
                          (ulong)((plan.RequiredFlags & ~s.Flags[i]) |
                                  (plan.ForbiddenFlags &  s.Flags[i]))) / 8f;
    float urgency   = s.PendingUrgency[i] / 100f;      // 인터럽트가 넣은 값

    return w.W1 * proximity + w.W2 * staleness + w.W3 * deviation + w.W4 * urgency;
}

public readonly record struct Weights(float W1, float W2, float W3, float W4, int StaleTicks)
{
    // W10에서 튜닝. 초기값:
    public static readonly Weights Default = new(W1: 3.0f, W2: 0.5f, W3: 2.0f, W4: 4.0f, StaleTicks: 36_000);
}
```

**`W1`(플레이어 근접도)을 크게 잡는 것이 핵심이다.** 이게 §2.2에서 지적한 "관측되지 않는 연산" 문제의 해결책이다. LOD 3(비활성) NPC는 `proximity = 0`이라 인터럽트가 없는 한 사실상 재계획되지 않는다.

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
// Npc.Planning/ReplanBudget.cs
public sealed class ReplanBudget
{
    private readonly TokenBucket _perSecond;    // 초당 요청 수
    private readonly TokenBucket _perDay;       // 일일 토큰 하드 캡

    // T1(로컬)은 GPU 용량, T2(외부)는 rate limit + 비용이 상한
    public bool TryAcquire(Tier tier, int estimatedTokens);
}
```

| 상한 | 초기값 | 근거 |
|---|---|---|
| T1 초당 요청 | 0.5 req/s | W1 M1 실측 (1.75s/req)의 88% |
| T2 초당 요청 | 16 req/s | W1 M4 실측 (동시 32) |
| 일일 토큰 캡 | 15M | 시나리오 B(20,000건/일)의 2배 여유 |
| 일일 비용 캡 | $10 | 하드 스톱 |

**GPU 사용률 60% 목표는 T1 초당 요청을 0.5 → 0.34로 낮추는 것으로 달성한다.** 남는 40%는 스파이크 흡수용이다.

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

**워커 수**: T1은 1개(배칭이 없어 늘려도 이득 없음), T2는 8개. 각각 별도 `BackgroundService`.

---

## 5. 시간대 전환 스파이크 완화

`GameTimeChanged` 수신 시 5,000마리가 동시에 버킷 플랜을 갈아탄다. 이건 LLM 호출이 아니라 **순수 배열 쓰기**지만, 5,000회를 한 틱에 하면 스파이크가 생긴다.

```csharp
// Npc.Runtime/BucketTransition.cs
// npcId 해시로 ±300틱(게임시간 ±5분) 지터를 주어 분산
static long JitterTicks(int npcId) => (Hash(npcId) % 601) - 300;

// 전환 예약 → 각 NPC가 자기 시각에 도달하면 스왑
```

지터는 **결정론적**이어야 한다(리플레이 재현). `Random` 금지, `npcId` 해시만 사용.

측정: 전환 구간의 틱 시간 p99가 평시 대비 2배를 넘지 않아야 한다.

---

## 6. 부하 테스트 (W10)

```
Npc.Host --loopback --npcs 5000 --time-scale 60 --days 7 --scenario scenarios/siege.jsonl
```

### 측정 매트릭스

| 축 | 값 |
|---|---|
| NPC 수 | 500 / 1,000 / 2,500 / 5,000 / 10,000(스트레스) |
| 시간 배속 | 1 / 60 / 600 |
| 티어 구성 | T0만 / T0+T1 / T0+T1+T2 |
| 플레이어 봇 | 0 / 20 / 100 |

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
