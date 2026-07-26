# P4 (W9–10) 작업 지시서 — 스케줄러 · 3-티어 라우팅 · 부하 테스트

사양: [`14_Phase4_W9-10_Scheduler_Tiering.md`](14_Phase4_W9-10_Scheduler_Tiering.md) · 규약: [`../TASKS.md`](../TASKS.md)

> **게이트: NPC 5,000에서 히트율 ≥ 98% · 틱 p99 ≤ 20ms · GPU ≤ 60% · 시나리오 B 통과**
> 이 Phase의 핵심은 "LLM 예산을 가장 필요한 NPC에 집중시키는 것"이다.

### W1·P1 실측이 이 Phase에 미치는 영향

| 실측 | P4에 대한 영향 |
|---|---|
| **로컬 지연이 가정의 2~3배다.** 프리픽스 캐시 적중 상태에서 4B 3.7s · 8B 5.1s (`W1_perf.csv` 중앙값). 상위 계획의 1.75s 가정이 무너졌다 | T4-05. T1 초당 요청 0.5 는 성립하지 않는다 |
| **"로컬은 동시성을 늘려도 이득 없다"가 반증됐다.** 4B 2.46 → 12.07 req/s, 8B → 2.15 req/s (동시 2). 다만 8B 는 동시 8 에서 0.36 으로 붕괴한다 (`W1_concurrency.md`) | T4-10. 워커 1개 고정의 근거가 사라졌다 |
| 동시 1 측정치는 모델 로드가 섞여 있다 (8B 평균 13,197ms) — **기준선으로 쓰지 않는다** | T4-15에서 재측정 |
| 외부 API 동시성·비용은 **측정되지 않았다** | T4-05 일일 비용 캡은 T2-19 실측 후 |
| P1 게이트 실측: NPC 5,000 · 1,440틱에서 틱 p99 **2.375ms**, Gen0 0, 인지 스캔 150/틱 (`P1_gate.md`) | T4-24의 비교 기준선. LLM 배선이 붙은 뒤 이 값이 얼마나 나빠지는지가 실제 관심사다 |

---

## A. 우선순위 재계획 큐 (W9)

**T4-01** `ReplanQueue` 이진 힙 · `L` · 선행 T1-38
  파일 `src/Npc.Planning/ReplanQueue.cs` (수정 — P1 FIFO 스텁 교체), `src/Npc.Runtime/CognitionScheduler.cs`·`InterruptMatcher.cs`·`PlanExecutor.cs` (수정)
  사양 `docs/14 §2`
  내용 고정 크기(4096) 힙. `_heapPos`로 중복 검출 → 점수 갱신. 포화 시 최하위 축출. **할당 0.**
  내용 ⚠ **점수 타입이 바뀐다.** P1 스텁은 `TryEnqueue(int npc, int score)`이고 §2는 `float`다. 호출부가 3곳 있다 — `CognitionScheduler`(자체 `Score`), `InterruptMatcher`(`rule.Urgency`), `PlanExecutor`(상수 50). 세 곳을 같이 고친다. `Dropped`·`Deduplicated`·`TotalEnqueued` 카운터는 대시보드(T1-59)가 이미 읽고 있으므로 유지한다.
  완료 `ReplanQueue_NoDuplicateInsert` 통과 (같은 NPC 재삽입 → 점수만 갱신) · `ReplanQueue_EvictsLowest` 통과 · `ReplanQueue_ZeroAlloc` 통과 · P1의 `ReplanQueueTests`·대시보드 테스트 전부 통과

**T4-02** `ReplanScorer` + `Weights` · `M` · 선행 T4-01
  파일 `src/Npc.Planning/ReplanScorer.cs` (신규), `src/Npc.Runtime/NpcStore.cs` (수정)
  사양 `docs/14 §2` · `docs/11 §3`
  내용 `w1·근접 + w2·노후 + w3·이탈(PopCount) + w4·긴급`. **`w1`을 크게 잡는 것이 §2.2 "관측되지 않는 연산" 문제의 해결책이다.**
  내용 ⚠ **§2가 쓰는 `NpcStore.PlanAssignedTick`·`PendingUrgency`가 아직 없다.** 이 태스크에서 추가한다. 둘 다 재계획 경로에서만 읽으므로 **콜드 영역**에 둔다 — 핫 배열에 넣으면 `HotBytesPerNpc`(현재 23B/NPC)가 늘어 `docs/11 §3`의 L2 목표가 깨진다. `PendingUrgency`는 `byte`로 충분하다(`InterruptRule.Urgency`가 0~100).
  완료 `Scorer_Lod3ScoresZeroWithoutUrgency` 통과 · `Scorer_DeviationUsesPopCount` 통과 · `NpcStore_HotBytesUnchanged` 통과 (핫 배열 크기 불변)

**T4-03** 인터럽트 슬롯 제한 · `S` · 선행 T4-01
  파일 `src/Npc.Planning/ReplanQueue.cs` (수정)
  사양 `docs/14 §10`
  내용 인터럽트(`1000+urgency`) 항목이 큐 용량의 **50%를 넘지 못하게** 한다. 안 그러면 일반 재계획이 영원히 안 된다.
  완료 `ReplanQueue_InterruptSlotCapped` 통과 (인터럽트 5,000건 주입 → 일반 항목이 여전히 처리됨)

**T4-04** 스냅샷 낡음 판정 · `M` · 선행 T4-01
  파일 `src/Npc.Planning/ReplanRequestSnapshot.cs` (신규)
  사양 `docs/14 §10`
  내용 큐 삽입 시 플래그 스냅샷 저장. 워커가 꺼낼 때 현재 플래그와 크게 다르면 **폐기하고 재삽입**.
  완료 `Replan_DiscardsStaleRequest` 통과

---

## B. 예산 · 라우팅 (W9)

**T4-05** `TokenBucket` / `ReplanBudget` · `M` · 선행 T2-10
  파일 `src/Npc.Planning/ReplanBudget.cs`, `TokenBucket.cs` (신규)
  사양 `docs/14 §3` · `docs/measurements/W1_perf.csv`
  내용 T1 초당 요청 / T2 초당 요청 / 일일 토큰 / 일일 비용 4개 상한.
  내용 ⚠ **§3 표의 초기값은 1.75s/req 가정에서 나온 값이라 전부 다시 잡아야 한다.** W1 실측(프리픽스 캐시 적중, 중앙값)은 4B **3.7s** · 8B **5.1s** 다. 8B 기준 T1 상한은 0.5 가 아니라 **~0.2 req/s** 이고, GPU 60% 목표를 얹으면 더 낮다. **T2 초당 요청과 일일 비용 캡은 외부 API 실측이 없으므로 T2-19 첫 회차 결과가 나온 뒤에 확정한다** — 그 전에는 보수적으로(요청 4/s · $2/일) 잡고 값의 출처를 주석에 남긴다.
  완료 `Budget_RefillsAtRate` 통과 · `Budget_BlocksWhenExhausted` 통과 · 4개 상한 각각의 근거(실측 파일·행)가 코드 주석에 있음 · `docs/14 §3` 표를 같은 커밋에서 실측치로 갱신하고 `../TASKS.md §3`에 기록

**T4-06** 일일 하드 캡 + 자동 강등 · `M` · 선행 T4-05
  파일 `src/Npc.Planning/ReplanBudget.cs` (수정)
  사양 `docs/14 §3` · 리스크 R8 · `../CLAUDE.md §2.7`
  내용 초과 시 T2 → T1 강등 → 거절. 경보 발생. **캡을 우회하는 경로를 만들지 않는다.**
  완료 `Budget_DowngradesT2ToT1OnCap` 통과 · `Budget_RejectsWhenT1AlsoExhausted` 통과

**T4-07** `TieredPlanCompiler` 라우터 · `L` · 선행 T4-06, T2-09
  파일 `src/Npc.Llm/TieredPlanCompiler.cs` (신규)
  사양 `docs/14 §4` · 상위계획 §10.4
  내용 §4 티어 선택 규칙표. `PlanQuality.Archetype` → T2, 1회용 → T1. **재사용 횟수에 비례해 품질에 투자한다.**
  완료 §4 표의 6개 케이스 전부 기대 티어 반환 (테스트 6건)

**T4-08** 스필오버 임계 · `S` · 선행 T4-07
  파일 `src/Npc.Llm/TieredPlanCompiler.cs` (수정)
  사양 `docs/14 §4`
  내용 T1 큐 깊이 > 64 → T2로 오버플로.
  완료 `Tier_SpillsOverOnLocalBacklog` 통과

**T4-09** 서킷 브레이커 · `M` · 선행 T4-07
  파일 `src/Npc.Llm/CircuitBreaker.cs` (신규)
  사양 `docs/14 §10`
  내용 T2 연속 실패 5회 → 60초 차단 → 반개방. **무한 재시도는 외부 장애 시 지연을 폭발시킨다.**
  완료 `Breaker_OpensAfter5Failures` · `Breaker_HalfOpensAfterTimeout` 통과

**T4-10** `ReplanWorker` (T1) · `M` · 선행 T4-07, T4-04
  파일 `src/Npc.Planning/ReplanWorker.cs` (신규)
  사양 `docs/14 §4` · `docs/measurements/W1_concurrency.md`
  내용 `BackgroundService`. 워커 수는 설정값으로 빼고 **기본 2** 로 둔다.
  내용 ⚠ **§4의 "로컬은 배칭이 없어 늘려도 이득 없으므로 1개"는 W1 실측과 어긋난다.** 동시 2에서 처리량이 크게 올랐다 (4B 2.46 → 12.07 req/s, 8B → 2.15 req/s). 반대로 **8B 는 동시 8에서 0.36 req/s 로 붕괴한다** — VRAM 8GB 에서 KV 캐시가 넘쳐 PCIe 페이징이 걸린 것이다. 즉 상한이 없는 게 아니라 **상한이 낮다.** 동시 1 측정치(8B 13,197ms)는 모델 로드가 섞였으니 비교에 쓰지 않는다.
  완료 `Worker_RunsOutsideTickLoop` 통과 (틱 스레드와 다른 스레드 ID) · T4-15에서 워커 1/2/4 를 재서 기본값을 확정하고 근거를 `W10_load.csv`에 남김

**T4-11** `ReplanWorker` (T2) × 8 · `S` · 선행 T4-10
  파일 `src/Npc.Planning/ReplanWorker.cs` (수정)
  내용 외부용 워커 8개. 별도 `BackgroundService` 인스턴스. **8이라는 수도 아직 근거가 없다** — T2-19에서 나온 외부 429 발생 지점 이하로 잡는다.
  완료 동시 8건 처리 확인 · T1 워커와 독립적으로 동작

**T4-12** `PendingPlanId` 원자 전달 · `M` · 선행 T4-10, T1-34, T3-03
  파일 `src/Npc.Planning/ReplanWorker.cs` (수정)
  사양 `docs/14 §4` · `../CLAUDE.md §2.1`
  내용 워커가 개별 풀에 대여 → `Volatile.Write(ref PendingPlanId[npc], ~slot)`. **워커가 런타임 상태를 직접 수정하지 않는다.**
  완료 `Worker_OnlyWritesPendingPlanId` 통과 · 1시간 부하에서 데이터 레이스 0 (`--blame` 또는 스트레스 테스트)

---

## C. 전환 · LOD (W9)

**T4-13** `BucketTransition` 정식 · `M` · 선행 T1-61, T3-01
  파일 `src/Npc.Runtime/BucketTransition.cs` (수정)
  사양 `docs/14 §5`
  내용 T1-61이 이미 절반을 끝냈다 — `JitterTicks`(±300, `PlanHash.Jitter`) · 카운팅 정렬 예약 · `Apply` · `PlanSwapper` 결선. `RequestSwap`은 이미 **버킷 키 4차원을 다 만든다**(아키타입 + `_target` 시간대 + 플래그에서 읽은 RegionState·Climate) 그리고 `PlanStore.Resolve`로 새 플랜을 받는다.
  내용 ⚠ **빠진 것은 전환 계기다.** `Tick`이 `clock.TimeOfDayChanged`만 보므로 `ZoneStateChanged`·`WeatherChanged`가 와도 아무 예약이 안 걸린다 — 플래그는 이미 바뀌었는데 다음 시간대 경계까지 낡은 플랜을 쓴다. 이 두 이벤트에서도 예약이 걸리게 한다.
  내용 `Schedule`은 전원을 다시 정렬한다(O(N)). **존 이벤트는 그 존만** 예약하도록 범위를 받는다 — 5,000마리를 매 존 이벤트마다 다시 세면 스파이크가 그대로 돌아온다.
  완료 `Transition_P99Under2xBaseline` 통과 · `Transition_IsDeterministic` 통과 · 세 계기(시간대·지역상태·기후) 각각의 전환 테스트 · P1의 `BucketTransitionTests` 전부 통과

**T4-14** LOD 등급 갱신 정식 · `M` · 선행 T1-36, T1-51
  파일 `src/Npc.Runtime/LodUpdater.cs` (신규), `src/Npc.Runtime/LodBand.cs` (수정)
  사양 `docs/11 §4` LOD 등급 갱신
  내용 T1-36의 `LodBandSet`은 **밴드 멤버십과 이동 상한(`MaxBandMigrationsPerTick` 64)**만 갖고 있다. 등급을 무엇으로 정하는지는 아직 없다. `LodUpdater`가 그 부분이다 — `PlayerProximity` 거리별 등급 + `ZoneStateChanged(War)` 시 존 전체 L1 승격. **등급 산출은 이벤트 수신 시에만** 하고, 인지 스캔 안에서는 `Lod[]`를 읽기만 한다.
  완료 `Lod_PromotesZoneOnWar` 통과 · `Lod_UpdatesFromProximityOnly` 통과 (스캔 안에서 거리 계산 0) · `LodBandSet`의 이동 상한이 여전히 지켜짐

---

## D. 부하 테스트 (W10)

**T4-15** 부하 테스트 하네스 · `L` · 선행 T4-12, T4-13, T4-14
  파일 `tests/Npc.Tests/Load/LoadHarness.cs` (신규), `tools/run_load.ps1` (신규), `src/Npc.Host/HostOptions.cs` (수정)
  사양 `docs/14 §6`
  내용 §6 매트릭스(NPC 5수준 × 배속 3 × 티어 3 × 봇 3). §6 "기록 지표" 8분류 전량 수집.
  내용 ⚠ **티어 축을 돌릴 옵션이 없다.** 현재 `HostOptions`에는 `--no-llm` 하나뿐이라 "T0만"과 "T0+T1+T2"를 못 가른다. `--tier none|t1|t2|all` 을 추가하고 `--no-llm`은 `--tier none`의 별칭으로 남긴다(P1 게이트 스크립트가 쓴다). T1 워커 수도 옵션으로 뺀다(T4-10).
  내용 `--max-speed`는 처리량 측정용이다 — 틱 지연 매트릭스는 페이싱을 켜고 잰다(`P1_gate.md §4`). `.ps1`은 UTF-8 BOM (T3-17).
  완료 매트릭스 1회 완주 · `docs/measurements/W10_load.csv` 산출 · P1 기준선(5,000 NPC p99 2.375ms)과 나란히 배치

**T4-16** 스케일 곡선 리포트 · `M` · 선행 T4-15
  파일 `tools/report_scale.cs` (신규 — 파일 기반 앱), `docs/measurements/W10_scale.md` (산출)
  사양 `docs/14 §6`
  내용 NPC 수 대비 각 지표의 차수 판정. **초선형이면 O(n²)가 숨어 있다.**
  내용 인지 스캔은 `MaxScansPerTick = 150` 으로 이미 잘려 있다 — P1 실측에서 5,000 NPC 가 정확히 150 이었다(상한에 붙음). 따라서 이 지표의 O(1) 여부는 **상한을 풀고** 재야 의미가 있다. 상한을 푼 회차를 따로 돌려 `docs/11 §4`의 612건 추정과 비교한다.
  완료 `Scale_CognitionIsConstant` 통과 (NPC 10배 → 틱당 스캔 대상 증가 ≤ 20%) · `Scale_LlmRequestsAreConstant` 통과

**T4-17** 가중치 A/B 자동화 · `M` · 선행 T4-15, T4-02
  파일 `tools/run_weight_ab.ps1` (신규), `docs/measurements/W10_weights.md` (산출)
  사양 `docs/14 §2` 튜닝 절차
  내용 4세트(A 근접우선 / B 기본 / C 이탈우선 / D 균등) × 시나리오 A. **감으로 튜닝하면 재현 불가.** `.ps1`은 UTF-8 BOM (T3-17).
  완료 4세트 결과표 · LLM 요청 수 최소 × 근처 NPC 신선도 최대 조합 선정 · 선정값이 `Weights.Default`에 반영

---

## E. 대시보드 완성 (W10)

> **T4-18~T4-22 공통.** 대시보드는 `GET /metrics` 가 내는 `MetricsSnapshot` JSON 한 장만 읽는다(`dashboard.html`은 정적 파일).
> 새 패널은 **`NpcMeter.MetricsSnapshot`에 필드를 먼저 늘려야 한다** — 지금은 `Tick`·`Npc`·`Actions`·`Link`·`Replan`·`LlmCalls` 뿐이다.
> 아래 각 태스크의 `NpcMeter.cs (수정)`이 그 몫이다.

**T4-18** 캐시 패널 · `S` · 선행 T3-04, T1-59
  파일 `src/Npc.Host/wwwroot/dashboard.html`, `src/Npc.Host/Metrics/NpcMeter.cs` (수정)
  사양 `docs/14 §8`
  내용 히트율 시계열 · 콜드 버킷 수 · 아키타입별 히트율. 값은 T3-04의 `CacheMetrics`에서 온다.
  완료 3요소 실시간 갱신

**T4-19** 재계획 패널 · `S` · 선행 T4-01, T4-07
  파일 `src/Npc.Host/wwwroot/dashboard.html`, `src/Npc.Host/Metrics/NpcMeter.cs` (수정)
  내용 큐 깊이 · 티어별 처리율 · 점수 분포 히스토그램 · 거절 수. `ReplanPanel`에 이미 깊이·거절(`Dropped`)이 있다 — 티어별 처리율과 히스토그램을 얹는다.
  완료 4요소 실시간 갱신

**T4-20** 비용 패널 · `S` · 선행 T2-10, T4-05
  파일 `src/Npc.Host/wwwroot/dashboard.html`, `src/Npc.Host/Metrics/NpcMeter.cs` (수정)
  내용 누적 토큰/비용 · 일일 캡 소진율 · 티어별 분해 · **프롬프트 캐시 적중률**. 원자료는 T2-10의 `CompileStats`다.
  완료 4요소 실시간 갱신 · 프리픽스 SHA 유니크 카운트도 표시(R11 감시) · **로컬 티어는 `cached_tokens`가 항상 0이므로 "미보고"로 표시한다** — 0%로 그리면 캐시가 안 걸린 것처럼 보인다(`W1_env.md §4.4`)

**T4-21** NPC 추적 패널 · `M` · 선행 T4-18
  파일 `src/Npc.Host/wwwroot/dashboard.html` (수정), `src/Npc.Host/Api/NpcTraceEndpoint.cs` (신규), `src/Npc.Host/Program.cs` (수정 — 라우트 등록)
  사양 `docs/14 §8`
  내용 NPC 1마리 선택 → 현재 플랜·스텝·플래그·최근 이벤트 실시간. `NpcStore`의 `Recent`(RingBuffer8)를 그대로 읽는다. **데모에서 제일 설득력 있는 패널이다.**
  완료 임의 NPC ID 입력 → 상태가 틱마다 갱신 · 추적 조회가 틱 루프를 막지 않음(스냅샷 복사)

**T4-22** 버킷 히트맵 · `M` · 선행 T4-18
  파일 `src/Npc.Host/wwwroot/dashboard.html`, `src/Npc.Host/Metrics/NpcMeter.cs` (수정)
  사양 `docs/14 §8`
  내용 40 아키타입 × 72 버킷 히트맵(= 2,880). **대부분 비어 있을 것이고, 그게 곧 "2,880이 아니라 300이면 충분했다"는 발견이다.**
  완료 히트맵 렌더 · 실사용 버킷 비율(%)을 숫자로 표시 · 원자료를 T5-18이 읽을 수 있게 덤프 가능

---

## F. 시나리오 · 게이트 (W10)

**T4-23** 시나리오 B 통과 · `M` · 선행 T4-13, T4-21, T1-52
  파일 `scenarios/siege.jsonl` (수정), `tests/Npc.Tests/Scenarios/SiegeTests.cs` (신규)
  사양 `docs/14 §7` · `docs/00 §2` 시나리오 B
  내용 **"아키타입별로 다르게 반응한다"가 핵심이다.** 전부 똑같이 도망가면 LLM을 쓴 의미가 없다.
  완료 아래 6항목

```
[ ] War 수신 → 인터럽트 경로로 1틱 내 즉시 반응
[ ] 버킷 키 *.War.* 전환 → 3초 내 마을 전체 플랜 스왑
[ ] 캐시 미스 버킷만 LLM 호출 (전량 재생성 아님)
[ ] 전환 구간 틱 p99 ≤ 40ms
[ ] guard/farmer/blacksmith가 서로 다른 행동으로 전환됨을 로그로 확인
[ ] Peace 복귀 시 원래 플랜 복원
```

**T4-24** P4 게이트 검증 · `M` · 선행 T4-23, T4-16, T4-17
  파일 `tests/Npc.Tests/Gates/Phase4GateTests.cs` (신규), `docs/measurements/P4_gate.md` (산출)
  사양 `docs/14 §9`
  내용 `Phase1GateTests`(T1-62)와 같은 형태로 쓰고, 실측은 `P1_gate.md`와 같은 형식으로 남긴다. **P1 기준선을 표에 같이 싣는다** — 5,000 NPC p99 2.375ms · Gen0 0 · 인지 스캔 150/틱. LLM 배선 후 이 값이 얼마나 나빠졌는지가 이 게이트의 실질이다.
  완료 아래 10항목

```
[ ] NPC 5,000 · 7게임일 완주
[ ] 캐시 히트율 ≥ 98%
[ ] 틱 p99 ≤ 20ms
[ ] 인지 스캔 ≤ 150/틱 (5,000에서도, 10,000에서도)
[ ] GPU 사용률 ≤ 60%
[ ] 시나리오 B 전 항목 통과
[ ] 일일 토큰 캡 초과 시 T2→T1 자동 강등
[ ] T2 강제 실패 주입 시 T1 페일오버
[ ] 틱 루프 Gen0 GC = 0
[ ] 큐 거절 발생 시에도 해당 NPC 정상 동작 (기존 플랜 유지)
```

---

## 진행 체크리스트

```
W9 ── 큐 · 예산 · 라우팅 · 전환
[x] T4-01 ReplanQueue힙 ★   [x] T4-02 ReplanScorer ★   [x] T4-03 인터럽트슬롯제한
[x] T4-04 스냅샷낡음판정    [x] T4-05 TokenBucket      [x] T4-06 일일캡+강등
[ ] T4-07 티어라우터 ★      [ ] T4-08 스필오버         [ ] T4-09 서킷브레이커
[ ] T4-10 워커T1            [ ] T4-11 워커T2×8         [ ] T4-12 원자전달
[ ] T4-13 버킷전환정식      [ ] T4-14 LOD갱신정식

W10 ── 부하 · 튜닝 · 대시보드 · 시나리오
[ ] T4-15 부하하네스        [ ] T4-16 스케일곡선 ★     [ ] T4-17 가중치A/B ★
[ ] T4-18 캐시패널          [ ] T4-19 재계획패널       [ ] T4-20 비용패널
[ ] T4-21 NPC추적패널 ★     [ ] T4-22 버킷히트맵 ★
[ ] T4-23 시나리오B ★       [ ] T4-24 게이트검증 ★
```

★ T4-16(스케일 곡선)이 §11.3 설계의 검증 지점이다. 인지 스캔이 O(1)이 아니면 설계가 틀린 것이다.
★ T4-22(버킷 히트맵)는 W12 보고서 §6 "발견"의 원자료가 될 가능성이 높다.
