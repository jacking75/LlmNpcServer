# P4 (W9–10) 작업 지시서 — 스케줄러 · 3-티어 라우팅 · 부하 테스트

사양: [`14_Phase4_W9-10_Scheduler_Tiering.md`](14_Phase4_W9-10_Scheduler_Tiering.md) · 규약: [`../TASKS.md`](../TASKS.md)

> **게이트: NPC 5,000에서 히트율 ≥ 98% · 틱 p99 ≤ 20ms · GPU ≤ 60% · 시나리오 B 통과**
> 이 Phase의 핵심은 "LLM 예산을 가장 필요한 NPC에 집중시키는 것"이다.

---

## A. 우선순위 재계획 큐 (W9)

**T4-01** `ReplanQueue` 이진 힙 · `L` · 선행 T1-38
  파일 `src/Npc.Planning/ReplanQueue.cs` (수정 — P1 FIFO 스텁 교체)
  사양 `docs/14 §2`
  내용 고정 크기(4096) 힙. `_heapPos`로 중복 검출 → 점수 갱신. 포화 시 최하위 축출. **할당 0.**
  완료 `ReplanQueue_NoDuplicateInsert` 통과 (같은 NPC 재삽입 → 점수만 갱신) · `ReplanQueue_EvictsLowest` 통과 · `ReplanQueue_ZeroAlloc` 통과

**T4-02** `ReplanScorer` + `Weights` · `M` · 선행 T4-01
  파일 `src/Npc.Planning/ReplanScorer.cs` (신규)
  사양 `docs/14 §2`
  내용 `w1·근접 + w2·노후 + w3·이탈(PopCount) + w4·긴급`. **`w1`을 크게 잡는 것이 §2.2 "관측되지 않는 연산" 문제의 해결책이다.**
  완료 `Scorer_Lod3ScoresZeroWithoutUrgency` 통과 · `Scorer_DeviationUsesPopCount` 통과

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
  사양 `docs/14 §3`
  내용 T1 초당 요청 / T2 초당 요청 / 일일 토큰 / 일일 비용 4개 상한. 초기값은 W1 실측(M1, M4)에서.
  완료 `Budget_RefillsAtRate` 통과 · `Budget_BlocksWhenExhausted` 통과

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
  사양 `docs/14 §4`
  내용 `BackgroundService` **1개**. 로컬은 배칭이 없어 늘려도 이득 없다.
  완료 `Worker_RunsOutsideTickLoop` 통과 (틱 스레드와 다른 스레드 ID)

**T4-11** `ReplanWorker` (T2) × 8 · `S` · 선행 T4-10
  파일 `src/Npc.Planning/ReplanWorker.cs` (수정)
  내용 외부용 워커 8개. 별도 `BackgroundService` 인스턴스.
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
  내용 `GameTimeChanged`/`ZoneStateChanged`/`WeatherChanged` → 버킷 키 전환 → 지터로 분산 스왑.
  완료 `Transition_P99Under2xBaseline` 통과 · `Transition_IsDeterministic` 통과

**T4-14** LOD 등급 갱신 정식 · `M` · 선행 T1-36, T1-51
  파일 `src/Npc.Runtime/LodUpdater.cs` (신규)
  사양 `docs/11 §4` LOD 등급 갱신
  내용 `PlayerProximity` 거리별 등급 + `ZoneStateChanged(War)` 시 존 전체 L1 승격.
  완료 `Lod_PromotesZoneOnWar` 통과 · `Lod_UpdatesFromProximityOnly` 통과 (스캔 안에서 거리 계산 0)

---

## D. 부하 테스트 (W10)

**T4-15** 부하 테스트 하네스 · `L` · 선행 T4-12, T4-13, T4-14
  파일 `tests/Npc.Tests/Load/LoadHarness.cs` (신규), `tools/run_load.ps1` (신규)
  사양 `docs/14 §6`
  내용 §6 매트릭스(NPC 5수준 × 배속 3 × 티어 3 × 봇 3). §6 "기록 지표" 8분류 전량 수집.
  완료 매트릭스 1회 완주 · `docs/measurements/W10_load.csv` 산출

**T4-16** 스케일 곡선 리포트 · `M` · 선행 T4-15
  파일 `tools/report_scale.csx` (신규), `docs/measurements/W10_scale.md` (산출)
  사양 `docs/14 §6`
  내용 NPC 수 대비 각 지표의 차수 판정. **초선형이면 O(n²)가 숨어 있다.**
  완료 `Scale_CognitionIsConstant` 통과 (NPC 10배 → 틱당 스캔 대상 증가 ≤ 20%) · `Scale_LlmRequestsAreConstant` 통과

**T4-17** 가중치 A/B 자동화 · `M` · 선행 T4-15, T4-02
  파일 `tools/run_weight_ab.ps1` (신규), `docs/measurements/W10_weights.md` (산출)
  사양 `docs/14 §2` 튜닝 절차
  내용 4세트(A 근접우선 / B 기본 / C 이탈우선 / D 균등) × 시나리오 A. **감으로 튜닝하면 재현 불가.**
  완료 4세트 결과표 · LLM 요청 수 최소 × 근처 NPC 신선도 최대 조합 선정 · 선정값이 코드 기본값에 반영

---

## E. 대시보드 완성 (W10)

**T4-18** 캐시 패널 · `S` · 선행 T3-04, T1-59
  파일 `src/Npc.Host/wwwroot/dashboard.html` (수정)
  사양 `docs/14 §8`
  내용 히트율 시계열 · 콜드 버킷 수 · 아키타입별 히트율.
  완료 3요소 실시간 갱신

**T4-19** 재계획 패널 · `S` · 선행 T4-01, T4-07
  파일 `src/Npc.Host/wwwroot/dashboard.html` (수정)
  내용 큐 깊이 · 티어별 처리율 · 점수 분포 히스토그램 · 거절 수.
  완료 4요소 실시간 갱신

**T4-20** 비용 패널 · `S` · 선행 T2-10, T4-05
  파일 `src/Npc.Host/wwwroot/dashboard.html` (수정)
  내용 누적 토큰/비용 · 일일 캡 소진율 · 티어별 분해 · **프롬프트 캐시 적중률**.
  완료 4요소 실시간 갱신 · 프리픽스 SHA 유니크 카운트도 표시(R11 감시)

**T4-21** NPC 추적 패널 · `M` · 선행 T4-18
  파일 `src/Npc.Host/wwwroot/dashboard.html` (수정), `src/Npc.Host/Api/NpcTraceEndpoint.cs` (신규)
  사양 `docs/14 §8`
  내용 NPC 1마리 선택 → 현재 플랜·스텝·플래그·최근 이벤트 실시간. **데모에서 제일 설득력 있는 패널이다.**
  완료 임의 NPC ID 입력 → 상태가 틱마다 갱신

**T4-22** 버킷 히트맵 · `M` · 선행 T4-18
  파일 `src/Npc.Host/wwwroot/dashboard.html` (수정)
  사양 `docs/14 §8`
  내용 40 아키타입 × 72 버킷 히트맵. **대부분 비어 있을 것이고, 그게 곧 "2,880이 아니라 300이면 충분했다"는 발견이다.**
  완료 히트맵 렌더 · 실사용 버킷 비율(%)을 숫자로 표시

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
  파일 `tests/Npc.Tests/Gates/Phase4GateTests.cs` (신규)
  사양 `docs/14 §9`
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
[ ] T4-01 ReplanQueue힙 ★   [ ] T4-02 ReplanScorer ★   [ ] T4-03 인터럽트슬롯제한
[ ] T4-04 스냅샷낡음판정    [ ] T4-05 TokenBucket      [ ] T4-06 일일캡+강등
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
