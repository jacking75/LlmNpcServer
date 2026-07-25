# P2 (W5–6) 작업 지시서 — 플랜 컴파일러 · 검증기

사양: [`12_Phase2_W5-6_Plan_Compiler.md`](12_Phase2_W5-6_Plan_Compiler.md) · 규약: [`../TASKS.md`](../TASKS.md)

> **게이트: 검증 통과율 ≥ 90%** (재시도 1회 포함, 4단 전체 기준)
> 이 Phase에서 처음으로 LLM이 등장한다. 캐시도 티어링도 아직 없다 — 단건 생성만.
> **선행: P1 게이트 통과.** LLM 없이 도는 런타임이 있어야 LLM의 기여분을 측정할 수 있다.

---

## A. `Npc.Llm` 기반 (W5)

**T2-01** `Npc.Llm` 프로젝트 배선 · `S` · 선행 T1-02
  파일 `src/Npc.Llm/Npc.Llm.csproj` (수정)
  사양 `../CLAUDE.md §3`
  내용 `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `OpenAI` 참조. **`Npc.Runtime`이 이 프로젝트를 참조하지 않음을 재확인.**
  완료 `Architecture_RuntimeDoesNotReferenceLlm` 여전히 통과

**T2-02** `SchemaProvider` — 스키마 생성 · `M` · 선행 T2-01, T1-13
  파일 `src/Npc.Llm/SchemaProvider.cs` (신규)
  사양 `docs/03 §2` · `docs/12 §4`
  내용 `actions.json` → JSON Schema. **`action` 열거값을 손으로 유지하지 않는다.** §4 위험 요소표(`oneOf`·깊은 중첩·`pattern`)를 제거. W1(T0-09)에서 문제됐던 요소도 제거.
  완료 `Schema_GeneratedFromCatalog` 통과 (열거값이 `actions.json`과 일치) · `docs/03 §1` 예시 플랜 통과

**T2-03** 액션 카탈로그 렌더러 · `M` · 선행 T2-02
  파일 `src/Npc.Llm/CatalogRenderer.cs` (신규)
  사양 `docs/01 §10.1` · `docs/12 §8`
  내용 `actions.json` → 프롬프트용 마크다운. **requires/grants를 표 형태로** 명시 (§8의 `V3.PRECONDITION_UNMET` 처방).
  완료 렌더 결과에 37개 액션 전부 · 각 액션의 requires/grants가 표에 존재 · 결정론적 출력(100회 동일)

**T2-04** `PromptPrefix` 정식판 · `M` · 선행 T2-03
  파일 `src/Npc.Llm/PromptPrefix.cs` (신규), `masterdata/prompt/system_rules.md` (신규), `masterdata/prompt/fewshot/*.json` (신규 3건)
  사양 `docs/01 §10`
  내용 시스템 규칙 + 카탈로그 + DSL 요약 + few-shot 3건. **기동 시 1회 조립하고 이후 불변.** 토큰 수는 W1(T0-13)에서 확정한 값.
  완료 토큰 수가 확정 범위 내 · ≥ 4,096 · `Text`/`Sha256`/`TokenCount` 노출

**T2-05** 프리픽스 불변성 강제 · `S` · 선행 T2-04
  파일 `tests/Npc.Tests/Llm/PromptPrefixTests.cs` (신규)
  사양 `docs/01 §10.2` · `../CLAUDE.md §2.5`
  내용 리스크 R11(캐시 미적중)의 탐지 장치.
  완료 `PromptPrefix_IsByteIdentical_Across1000Builds` 통과 · `PromptPrefix_MeetsTokenFloor` 통과

**T2-06** `PlanRequestSuffix` 조립기 · `M` · 선행 T2-04, T1-19
  파일 `src/Npc.Llm/PlanRequestSuffix.cs` (신규)
  사양 `docs/12 §3`
  내용 §3의 압축 원칙. 참인 플래그만, 0 아닌 인벤만, recent는 salience 상위 3개만. **키 이름은 줄이지 않는다**(모델 이해도 저하).
  완료 §3의 두 예시(아키타입용/개별용) 재현

**T2-07** 서픽스 토큰 예산 테스트 · `S` · 선행 T2-06
  파일 `tests/Npc.Tests/Llm/SuffixBudgetTests.cs` (신규)
  사양 `docs/12 §3`
  내용 **이 테스트가 없으면 서픽스는 반드시 자란다.** 전 버킷 2,880 × 스트레스 스냅샷(인벤 만재·recent 만재).
  완료 `Suffix_NeverExceeds300Tokens` 통과

**T2-08** `ChatClientFactory` · `M` · 선행 T2-01
  파일 `src/Npc.Llm/ChatClientFactory.cs` (신규), `appsettings.Llm.json` (신규)
  사양 `docs/10 §2 T0-2` · `../CLAUDE.md §2.7`
  내용 로컬(dotLLM HTTP) / 외부를 `IChatClient`로 생성. **제공사 SDK를 `Npc.Llm` 밖에서 부르지 않는다.**
  완료 설정만 바꿔 로컬↔외부 전환 · usage와 `cached_tokens` 노출

---

## B. 단건 생성 (W5)

**T2-09** `PlanCompiler` 단건 생성 · `L` · 선행 T2-02, T2-04, T2-06, T2-08
  파일 `src/Npc.Llm/IPlanCompiler.cs`, `PlanCompiler.cs` (신규)
  사양 `docs/12 §2`
  내용 `PlanRequest` → 강제 디코딩 호출 → `PlanDocument`. 재시도·폴백은 T2-15/T2-17에서 추가.
  완료 `Compiler_ProducesValidJson` 통과 (임의 버킷 20건, 강제 디코딩 유효율 100%)

**T2-10** `CompileStats` 수집 · `M` · 선행 T2-09, T1-58
  파일 `src/Npc.Llm/CompileStats.cs` (신규), `src/Npc.Host/Metrics/NpcMeter.cs` (수정)
  사양 `docs/12 §2`
  내용 prompt/cached/completion 토큰, 지연, 비용, 모델, 시도 횟수. **모든 호출에서 기록** — P3 manifest와 W12 보고서의 원자료다.
  완료 `Stats_RecordedOnEveryCall` 통과 · 메트릭에 `prefix_sha` 태그 포함

**T2-11** 검증기 1·2단 결선 · `S` · 선행 T2-09, T1-25, T1-26
  파일 `src/Npc.Llm/PlanCompiler.cs` (수정)
  사양 `docs/03 §3` · `docs/12 §5`
  내용 **강제 디코딩을 신뢰하지 않고 재검증.** 실패 코드를 `(stage, code, archetype)` 태그로 메트릭에 기록.
  완료 `Compiler_AlwaysValidates` 통과 (검증 우회 경로 없음)

---

## C. 검증기 완성 (W6)

**T2-12** 검증기 3단 `Explain()` 보강 · `M` · 선행 T1-27
  파일 `src/Npc.Core/Validation/CoherenceValidator.cs` (수정)
  사양 `docs/12 §5`
  내용 실패를 "HasRawMaterial 플래그가 없는데 Craft를 시도함" 형태의 자연어로. **비트마스크 숫자를 그대로 내보내면 모델이 못 고친다.**
  완료 `Explain_IsHumanReadable` 통과 (플래그 이름 + 액션 이름 + 스텝 번호 포함)

**T2-13** `DryRunValidator` (4단) · `L` · 선행 T2-12, T1-46
  파일 `src/Npc.Core/Validation/DryRunValidator.cs`, `src/Npc.Sim/SimWorld.CreateMinimal.cs` (신규)
  사양 `docs/03 §3` 4단 · `docs/12 §5`
  내용 NPC 1마리 · 1000배속 축소 Sim에서 1~2 사이클 실행.
  완료 `V4.*` 5종(DEADLOCK/TIMEOUT/INFINITE_LOOP/RESOURCE_STARVE/OSCILLATION) 전부 유발 픽스처로 검증 · 1건 ≤ 100ms

**T2-14** 드라이런 결정론 · `S` · 선행 T2-13
  파일 `src/Npc.Core/Validation/DryRunValidator.cs` (수정)
  사양 `docs/12 §5`
  내용 `seed`를 버킷 키에서 유도. **비결정적이면 골든 테스트가 불안정해진다.**
  완료 `DryRun_IsDeterministic` 통과 (같은 플랜 100회 → 동일 판정)

---

## D. 실패 처리 (W6)

**T2-15** 재시도 파이프라인 · `M` · 선행 T2-11, T2-13
  파일 `src/Npc.Llm/PlanCompiler.cs` (수정)
  사양 `docs/12 §6`
  내용 **1회만.** 실패 코드를 `previous_attempt_failed` 블록으로 **서픽스에만** 추가. temperature 0.4 → 0.6.
  완료 `Retry_DoesNotTouchPrefix` 통과 (재시도 시 프리픽스 SHA 동일) · `Retry_MaxOnce` 통과

**T2-16** 인접 버킷 재사용 · `M` · 선행 T2-15, T1-17
  파일 `src/Npc.Planning/BucketNeighbors.cs` (신규)
  사양 `docs/12 §7`
  내용 §7의 5순위. **아키타입은 절대 바꾸지 않는다.** 재사용 플랜도 2·3단 재검증.
  완료 `Neighbors_NeverCrossArchetype` 통과 · `Neighbors_ReusedPlanRevalidated` 통과

**T2-17** 폴백 경로 · `S` · 선행 T2-16, T1-55
  파일 `src/Npc.Llm/PlanCompiler.cs` (수정)
  사양 `docs/12 §6`
  완료 `Compiler_NeverReturnsNullPlan` 통과 (LLM 전면 실패 시에도 폴백 반환)

**T2-18** `rejected/` 보존 · `S` · 선행 T2-11
  파일 `src/Npc.Llm/RejectedStore.cs` (신규)
  사양 `docs/13 §8`
  내용 검증 실패 산출물 + 실패 코드 전량 보존. **조용히 버리면 품질 개선 원자료가 사라진다.**
  완료 실패 1건당 `planstore/rejected/<bucket>.<attempt>.json` 생성

---

## E. 통과율 개선 루프 (W6 후반)

**T2-19** 전량 생성 러너 · `M` · 선행 T2-17, T2-18
  파일 `tools/Npc.Prebake/BulkRunner.cs` (신규, P3에서 CLI로 승격)
  사양 `docs/12 §8`
  내용 2,880 버킷 전량 1회 생성. 외부 API 동시 32(W1 T0-11 실측값 사용).
  완료 2,880건 완주 · 소요 ≤ 10분 · 비용 기록

**T2-20** 실패 코드 집계 리포트 · `S` · 선행 T2-19
  파일 `tools/report_failures.csx` (신규), `docs/measurements/W6_failures.md` (산출)
  사양 `docs/12 §8`
  내용 `(stage, code, archetype)` 3축 집계 + 상위 원인 Top 5.
  완료 리포트 생성 · 상위 3개 원인이 명시됨

**T2-21** 통과율 개선 1~3차 · `L` · 선행 T2-20
  파일 (프롬프트·스키마·카탈로그 수정)
  사양 `docs/12 §8` 처방표
  내용 §8 표의 처방을 상위 3개 원인에 적용 → 재생성 → 재집계. **최대 3회 반복.**
  완료 통과율 ≥ 90% · 각 차수의 통과율 변화가 `W6_failures.md`에 기록

**T2-22** 플랜 다양성 지표 · `M` · 선행 T2-19
  파일 `tools/measure_diversity.csx` (신규)
  사양 `docs/12 §10`
  내용 유니크 액션 시퀀스 / 전체. 아키타입 고정 시 버킷별로 실제로 다른가.
  완료 지표 산출 · **≥ 60%.** 미달 시 T2-21에 반영하고, W12 블라인드 평가에서 "구분 불가"가 나올 가능성을 `../TASKS.md` §3에 경보로 기록

**T2-23** `count` 심볼화 (조건부) · `M` · 선행 T2-20
  파일 `masterdata/actions.json` (수정), `src/Npc.Core/Plan/*.cs` (수정)
  사양 `docs/12 §8` 말미
  내용 `V3.RESOURCE_IMBALANCE`가 상위 원인이면 실행한다. `count`를 모델이 정하지 않고 `"max"`/`"needed"` 심볼만 허용 → 런타임이 인벤 보고 결정.
  완료 (실행 시) `V3.RESOURCE_IMBALANCE` 발생률 0 · (미실행 시) 원장에 `-`로 표기하고 이유 기록

**T2-24** W6 측정 리포트 · `S` · 선행 T2-21, T2-22
  파일 `docs/measurements/W6_compile_stats.md` (신규)
  사양 `docs/12 §10`
  내용 통과율(전체/아키타입별/차원별) · 실패 분포(개선 전후) · 재시도별 누적 성공률 · 모델별 통과율 · 요청당 지연/토큰/비용 · 다양성 지표.
  완료 6개 항목 전부 기록 · P2 게이트 체크리스트 판정

---

## 게이트 확인

```
[ ] 2,880 버킷 전량 생성 시 검증 통과율 ≥ 90% (재시도 1회 포함)
[ ] 폴백으로 떨어지는 비율 ≤ 5%
[ ] 서픽스 토큰 ≤ 300 (전 버킷·스트레스 케이스)
[ ] 프리픽스 SHA가 전 요청에서 1종
[ ] 프롬프트 캐시 적중률 ≥ 95% (외부 API)
[ ] 드라이런 결정론 (같은 플랜 100회 → 동일 판정)
[ ] 플랜 다양성 ≥ 60%
```

---

## 진행 체크리스트

```
W5 ── 기반 · 단건 생성
[ ] T2-01 Llm프로젝트배선   [ ] T2-02 SchemaProvider   [ ] T2-03 카탈로그렌더러
[ ] T2-04 PromptPrefix정식  [ ] T2-05 프리픽스불변성   [ ] T2-06 서픽스조립기
[ ] T2-07 서픽스예산테스트  [ ] T2-08 ChatClientFactory
[ ] T2-09 PlanCompiler      [ ] T2-10 CompileStats     [ ] T2-11 검증1·2단결선

W6 ── 검증기완성 · 실패처리 · 개선루프
[ ] T2-12 Explain보강       [ ] T2-13 DryRunValidator  [ ] T2-14 드라이런결정론
[ ] T2-15 재시도(1회)       [ ] T2-16 인접버킷재사용   [ ] T2-17 폴백경로
[ ] T2-18 rejected보존
[ ] T2-19 전량생성러너      [ ] T2-20 실패집계리포트   [ ] T2-21 개선루프 ★
[ ] T2-22 다양성지표 ★      [ ] T2-23 count심볼화(조건) [ ] T2-24 W6리포트
```

★ T2-21과 T2-22가 이 Phase의 실질이다. 나머지는 배관이다.
