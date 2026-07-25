# P3 (W7–8) 작업 지시서 — 플랜 캐시 · 프리베이크

사양: [`13_Phase3_W7-8_Plan_Cache.md`](13_Phase3_W7-8_Plan_Cache.md) · 규약: [`../TASKS.md`](../TASKS.md)

> **게이트: 2,880키 콜드 필 ≤ 5분 · 캐시 히트율 ≥ 98% · 검수 채택률 ≥ 80%**
> 여기서 만들어지는 `planstore/`가 이 프로젝트의 실질적 핵심 산출물이다.

---

## A. `PlanStore` (W7)

**T3-01** `PlanStore` 배열 + Resolve/Publish · `M` · 선행 T1-39, T1-17
  파일 `src/Npc.Planning/PlanStore.cs` (수정 — P1 스텁 교체)
  사양 `docs/13 §2`
  내용 `CompiledPlan?[2880]` 고정 배열. `BucketKey.ToIndex()`로 O(1). **락 없음** — `Volatile` 읽기/쓰기 + 원자 참조 교체. 히트/미스 카운터.
  완료 `PlanStore_ResolveNeverNull` 통과 · `PlanStore_IsLockFree` (동시 읽기 1M회 중 예외 0) · `PlanStore_ResolveIsO1`

**T3-02** `PlanOrigin` + pinned 보호 · `S` · 선행 T3-01
  파일 `src/Npc.Planning/PlanStore.cs` (수정)
  사양 `docs/13 §2, §5`
  내용 `Prebaked | Runtime | Fallback | Pinned`. **Pinned는 `Publish`가 덮어쓰지 않는다.**
  완료 `PlanStore_PinnedIsNotOverwritten` 통과

**T3-03** `IndividualPlanPool` · `M` · 선행 T3-01
  파일 `src/Npc.Planning/IndividualPlanPool.cs` (신규)
  사양 `docs/13 §2`
  내용 링 버퍼 512개, LRU 회수. `NpcStore.PlanId`가 음수면 `~value`가 풀 인덱스.
  완료 `IndividualPool_LruEvicts` 통과 · `IndividualPool_NegativeIdRoundTrip` 통과 · 회전율 메트릭 노출

**T3-04** 캐시 메트릭 · `S` · 선행 T3-01, T1-58
  파일 `src/Npc.Planning/CacheMetrics.cs` (신규)
  사양 `docs/13 §6`
  내용 히트율(전체·아키타입별) · 콜드 버킷 수 · 미스 상위 10 버킷 · 개별 풀 회전율.
  완료 `/metrics`에 4개 지표 존재 · 아키타입별 분해 가능

---

## B. 저장 · 무효화 (W7)

**T3-05** `Manifest` 모델 · `S` · 선행 T3-01
  파일 `src/Npc.Planning/Manifest.cs` (신규)
  사양 `docs/03 §7`
  내용 masterdata_hash · prefix_hash · generated_by · counts · validation · cost_usd · wall_clock_s. **생성 시각은 외부에서 인자로 주입**(`DateTime.Now` 금지).
  완료 `Manifest_HasNoDateTimeNow` 통과 (소스에 `DateTime.Now`/`UtcNow` 0)

**T3-06** `PlanStoreValidator` 무효화 판정 · `M` · 선행 T3-05, T1-19
  파일 `src/Npc.Planning/PlanStoreValidator.cs` (신규)
  사양 `docs/13 §3`
  내용 §3 표대로 `None | Partial | Full` 판정. **POI 하나 추가할 때마다 전량 재생성하면 W8 이후가 지옥이 된다.**
  완료 §3 표의 10개 케이스 전부에 대해 정확한 범위 반환 (테스트 10건)

**T3-07** 파일 저장/로드 · `M` · 선행 T3-05, T3-02
  파일 `src/Npc.Planning/PlanStoreIo.cs` (신규)
  사양 `docs/03 §7`
  내용 `plans/` `pinned/` `rejected/` 3계층. 파일명 = `{bucket}.json`. 로드 시 pinned 우선.
  완료 `PlanStoreIo_RoundTrip` 통과 (2,880건 저장→로드→내용 동일) · pinned가 plans를 덮어씀

---

## C. `Npc.Prebake` CLI (W8)

**T3-08** CLI 골격 + 옵션 · `M` · 선행 T3-07, T2-19
  파일 `tools/Npc.Prebake/Program.cs`, `PrebakeOptions.cs` (신규)
  사양 `docs/13 §4`
  내용 `--masterdata --out --tier --model --concurrency --dryrun-sample --resume --only --budget-usd`.
  완료 `--help` 출력 · `--only "blacksmith@*"` glob 파싱 테스트 통과

**T3-09** 대상 버킷 산출 · `M` · 선행 T3-08, T3-06
  파일 `tools/Npc.Prebake/TargetSelector.cs` (신규)
  사양 `docs/13 §4`
  내용 Full(2,880) / Partial(변경분) / `--resume`(미생성분) / `--only`(glob).
  완료 4가지 모드 각각 대상 수가 기대와 일치 (테스트 4건)

**T3-10** `prebake_priority` 정렬 · `S` · 선행 T3-09
  파일 `tools/Npc.Prebake/TargetSelector.cs` (수정)
  사양 `docs/01 §6` · `docs/13 §4`
  내용 Peace 계열 먼저. 중단되어도 실제로 많이 쓰이는 버킷이 먼저 채워진다.
  완료 정렬 결과의 앞 25%가 전부 `RegionState.Peace`

**T3-11** 프리픽스 워밍업 · `S` · 선행 T3-08
  파일 `tools/Npc.Prebake/Warmup.cs` (신규)
  사양 `docs/13 §4, §8`
  내용 동시 요청 **전에** 단건을 먼저 던져 캐시 write를 1회만 지불. 생략하면 Anthropic 기준 write 할증을 32번 낸다.
  완료 `Prebake_WarmupBeforeConcurrency` 통과 (첫 요청 완료 후에야 워커 시작) · 워밍업 유무 비용 차이를 실측 기록

**T3-12** `AdaptiveConcurrency` (AIMD) · `M` · 선행 T3-08
  파일 `tools/Npc.Prebake/AdaptiveConcurrency.cs` (신규)
  사양 `docs/13 §4`
  내용 성공 16연속 → +1, throttle → /2. 초기값은 W1 T0-11 실측값.
  완료 `Aimd_HalvesOnThrottle` · `Aimd_GrowsOnStreak` 통과

**T3-13** 429 백오프 · `S` · 선행 T3-12
  파일 `tools/Npc.Prebake/RetryPolicy.cs` (신규)
  사양 `docs/13 §4`
  내용 지수 백오프 + 지터. **동시 32를 그냥 던지면 초반에 다 튕긴다.**
  완료 `Backoff_IsExponentialWithJitter` 통과 · 429 주입 시 전량 완주

**T3-14** 예산 하드 캡 · `S` · 선행 T3-08, T2-10
  파일 `tools/Npc.Prebake/BudgetGuard.cs` (신규)
  사양 `docs/13 §4` · 리스크 R8
  내용 `--budget-usd` 초과 시 중단, manifest는 부분 상태로 기록, `--resume` 가능.
  완료 `Budget_StopsAndResumes` 통과 (캡 도달 → 중단 → resume으로 이어짐)

**T3-15** 전수 드라이런 병렬 · `M` · 선행 T3-08, T2-13
  파일 `tools/Npc.Prebake/DryRunStage.cs` (신규)
  사양 `docs/13 §8`
  내용 `--dryrun-sample 1.0`. 2,880 × ~50ms를 병렬화. **프리베이크에서 생략하면 데드락 플랜이 런타임에 배포된다.**
  완료 2,880건 드라이런 ≤ 60초 · 결정론 유지 (2회 실행 → 동일 판정)

**T3-16** manifest 작성 · `S` · 선행 T3-14, T3-15
  파일 `tools/Npc.Prebake/ManifestWriter.cs` (신규)
  사양 `docs/03 §7`
  완료 `manifest.json`에 counts/validation/cost/wall_clock 전부 · 프리베이크 실행마다 보존

---

## D. 사람 검수 (W8) — 오써링 자동화 검증의 핵심

**T3-17** 검수 도구 · `M` · 선행 T3-07
  파일 `tools/review.ps1` (신규)
  사양 `docs/13 §5`
  내용 `--sample 40` 무작위 추출 → 사이드바이사이드 출력 → `[채택/수정후채택/폐기]` 입력 받음. **검수 소요 시간을 자동 계측.**
  완료 40건 순회 · 판정과 소요 시간이 jsonl로 기록됨

**T3-18** 검수 결과 기록 포맷 · `S` · 선행 T3-17
  파일 `docs/measurements/review_W8.jsonl` (산출)
  사양 `docs/13 §5`
  내용 `{bucket, verdict, minutes, note}`. **이 파일이 W12 보고서의 핵심 수치다.**
  완료 40행 · verdict가 3종 중 하나 · minutes 기록

**T3-19** pinned 승격 도구 · `S` · 선행 T3-18, T3-02
  파일 `tools/pin_plan.ps1` (신규)
  사양 `docs/13 §5, §8`
  내용 수정 채택된 플랜을 `plans/` → `pinned/`로 이동. **`pinned/`는 반드시 버전 관리에 올린다.**
  완료 승격 후 재프리베이크가 덮어쓰지 않음 · `.gitignore`가 `pinned/`를 무시하지 않음

---

## E. 결선 · 게이트 (W8)

**T3-20** Host에 `PlanStore` 결선 · `M` · 선행 T3-01, T3-07, T1-57
  파일 `src/Npc.Host/Program.cs` (수정)
  사양 `docs/13 §2`
  내용 기동 시 `planstore/` 로드 → `PlanStore` 주입. P1의 폴백 전용 스텁을 교체. 무효화 판정 결과를 로그로 경고.
  완료 기동 시 2,880건 로드 · 미생성 버킷은 폴백으로 해소 · 로드 시간 ≤ 3초

**T3-21** P3 게이트 검증 · `M` · 선행 T3-20, T3-16, T3-18
  파일 `tests/Npc.Tests/Gates/Phase3GateTests.cs` (신규)
  사양 `docs/13 §7`
  완료 아래 9항목 전부

```
[ ] 2,880 버킷 중 생성 완료 ≥ 95%, 나머지는 폴백으로 안전 해소
[ ] 프리베이크 wall-clock ≤ 5분 (외부 API 동시 32)
[ ] 프리베이크 실비용 ≤ $5
[ ] 프롬프트 캐시 적중률 ≥ 95% (manifest 기록)
[ ] 시나리오 A(7게임일)에서 캐시 히트율 ≥ 98%
[ ] pinned 플랜이 재프리베이크로 덮어써지지 않는다
[ ] POI 1개 추가 시 Partial 무효화 동작 (전량 재생성 안 함)
[ ] --budget-usd 초과 시 중단 + --resume 재개
[ ] 검수 40건 샘플의 채택률(accept+edit) ≥ 80%
```

---

## 진행 체크리스트

```
W7 ── 스토어 · 무효화
[ ] T3-01 PlanStore배열     [ ] T3-02 pinned보호      [ ] T3-03 개별플랜풀
[ ] T3-04 캐시메트릭        [ ] T3-05 Manifest모델    [ ] T3-06 무효화판정 ★
[ ] T3-07 파일저장/로드

W8 ── 프리베이크 · 검수
[ ] T3-08 CLI골격           [ ] T3-09 대상버킷산출    [ ] T3-10 우선순위정렬
[ ] T3-11 프리픽스워밍업 ★  [ ] T3-12 AIMD           [ ] T3-13 429백오프
[ ] T3-14 예산하드캡        [ ] T3-15 전수드라이런    [ ] T3-16 manifest작성
[ ] T3-17 검수도구 ★        [ ] T3-18 검수기록        [ ] T3-19 pinned승격
[ ] T3-20 Host결선          [ ] T3-21 게이트검증 ★
```

★ T3-06(무효화 판정)과 T3-11(워밍업)은 빼먹기 쉬운데 각각 개발 속도와 비용을 좌우한다.
★ T3-17 검수는 **사람이 한다.** 여기서 나오는 채택률과 검수 시간이 R&D 결론의 근거다.
