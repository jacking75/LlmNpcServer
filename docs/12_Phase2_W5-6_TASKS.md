# P2 (W5–6) 작업 지시서 — 플랜 컴파일러 · 검증기

사양: [`12_Phase2_W5-6_Plan_Compiler.md`](12_Phase2_W5-6_Plan_Compiler.md) · 규약: [`../TASKS.md`](../TASKS.md)

> **게이트: 검증 통과율 ≥ 90%** (재시도 1회 포함, 4단 전체 기준)
> 이 Phase에서 처음으로 LLM이 등장한다. 캐시도 티어링도 아직 없다 — 단건 생성만.
> **선행: P1 게이트 통과.** LLM 없이 도는 런타임이 있어야 LLM의 기여분을 측정할 수 있다.

### W1 실측이 이 Phase에 미치는 영향

`docs/measurements/W1_schema.md` · `W1_perf.csv` · `W1_env.md` 기준. **T0-13(`W1_results.md`)은 아직 미작성이므로 M1~M6 번호로 인용하지 않는다.**

| 실측 | P2에 대한 영향 |
|---|---|
| **강제 디코딩이 프롬프트 모드보다 나빴다.** full/forced 는 유효 JSON 99/100 인데 검증통과 0 (`V2.UNKNOWN_ARG` 99 — 스키마에 없는 인자를 지어낸다). full/prompt 는 93/100 통과 | T2-02·T2-09. 강제 디코딩을 **기본으로 두지 않는다.** 두 모드를 다 재고, 통과율이 높은 쪽을 고른다 |
| 문제된 스키마 요소: `minItems/maxItems` 7 · `additionalProperties` 1 · `required` 1 · `maximum` 1 | T2-02 위험 요소표에 반영 |
| 프리픽스 4,409 tok 은 **축소판**(액션 12·플래그 16) 기준이다. 정식은 액션 37·플래그 42 | T2-04. 4,200~4,500 예산이 정식 카탈로그와 양립하지 않는다 |
| 프리픽스 캐시 적중 시 prefill 1,531 → 237ms (4B) · 2,608 → 316ms (8B). **85~88% 감소** | G0-3 통과. 프리픽스 고정 전략은 유효하다 |
| **외부 API 동시성은 측정되지 않았다.** `W1_concurrency.md` 는 로컬 2종뿐 | T2-19. "동시 32" 는 아직 근거 없는 수다 |

---

## A. `Npc.Llm` 기반 (W5)

**T2-01** `Npc.Llm` 프로젝트 배선 · `S` · 선행 T1-02
  파일 `src/Npc.Llm/Npc.Llm.csproj` (수정)
  사양 `../CLAUDE.md §3`
  내용 P1 시점에 `Npc.Core` 참조와 `Microsoft.Extensions.AI` 10.8.1 은 이미 걸려 있다. 추가할 것은 `Microsoft.Extensions.AI.OpenAI`·`OpenAI` 와, 토큰 예산을 재기 위한 `Microsoft.ML.Tokenizers`(+`Data.O200kBase`) 다. `Npc.Runtime`이 이 프로젝트를 참조하지 않음을 재확인.
  내용 ⚠ **`Npc.Llm → Npc.MasterData` 참조를 추가한다.** 프리픽스는 `actions.json`·`world_flags.json`·`archetypes.json`에서 생성되고(`docs/01 §10.1`), `docs/12 §3`의 `Build(in PlanRequest, MasterDataSet)`·`§6`의 `_md.Fallbacks[...]` 가 이미 `MasterDataSet`을 받는다. `../CLAUDE.md §3`·`docs/11 §2`·`Architecture_DependencyGraphMatchesSpec` 을 같은 커밋에서 갱신한다.
  완료 `Architecture_RuntimeDoesNotReferenceLlm` 여전히 통과 · `Architecture_DependencyGraphMatchesSpec` 통과

**T2-02** `SchemaProvider` — 스키마 생성 · `M` · 선행 T2-01, T1-13
  파일 `src/Npc.Llm/SchemaProvider.cs` (신규)
  사양 `docs/03 §2` · `docs/12 §4` · `docs/measurements/W1_schema.md`
  내용 `actions.json` → JSON Schema. **`action` 열거값을 손으로 유지하지 않는다.** §4 위험 요소표(`oneOf`·깊은 중첩·`pattern`)를 제거. W1(T0-09)에서 실제로 걸린 요소는 `minItems/maxItems`(7건)·`additionalProperties`(1)·`required`(1)·`maximum`(1) 이다 — **전부 검증기 1·2단이 다시 잡으므로 스키마에서 뺀다.**
  내용 스파이크의 3종(`plan.schema.json` / `.bare.json` / `.relaxed.json`)에 대응하는 **full / bare 두 프로파일**을 노출한다. 어느 쪽을 쓸지는 T2-09가 정한다.
  완료 `Schema_GeneratedFromCatalog` 통과 (열거값이 `actions.json` 37개와 일치) · `docs/03 §1` 예시 플랜 통과 · 위 4개 요소가 생성물에 없음

**T2-03** 액션 카탈로그 렌더러 · `M` · 선행 T2-02
  파일 `src/Npc.Llm/CatalogRenderer.cs` (신규)
  사양 `docs/01 §10.1` · `docs/12 §8`
  내용 `actions.json` → 프롬프트용 마크다운. **requires/`requires_any`/forbids/grants/clears를 표 형태로** 명시 (§8의 `V3.PRECONDITION_UNMET` 처방). `requires_any`(OR 전제)는 T1-12에서 추가된 필드다 — 빠뜨리면 `AtHome|AtTavern` 계열 액션이 전부 오해된다.
  완료 렌더 결과에 37개 액션 전부 · 각 액션의 5종 플래그가 표에 존재 · 결정론적 출력(100회 동일)

**T2-04** `PromptPrefix` 정식판 · `M` · 선행 T2-03
  파일 `src/Npc.Llm/PromptPrefix.cs` (신규), `masterdata/prompt/system_rules.md` (신규), `masterdata/prompt/fewshot/*.json` (신규 3건)
  사양 `docs/01 §10` · `docs/measurements/W1_schema.md`
  내용 시스템 규칙 + 카탈로그 + DSL 요약 + few-shot 3건. **기동 시 1회 조립하고 이후 불변.**
  내용 ⚠ **`docs/01 §10.1`의 4,200~4,500 예산은 축소판 기준이다.** W1 프리픽스 4,409 tok 은 액션 12·플래그 16 으로 잰 값이고, 정식은 액션 37·플래그 42 다. 스파이크 실측 단가(액션 ≈139 tok, 플래그 ≈13 tok)로 외삽하면 정식 프리픽스는 **약 8,000 tok** 이다. 상한을 지키려고 카탈로그를 줄이지 않는다 — **캐시에 필요한 것은 하한(4,096)뿐이고 상한은 비용 문제일 뿐이다.**
  완료 `Text`/`Sha256`/`TokenCount` 노출 · **≥ 4,096** · 실측 토큰 수를 `docs/measurements/W6_compile_stats.md`에 기록 · 4,500 을 넘으면 **같은 커밋에서** `docs/01 §10.1`과 `../CLAUDE.md §2.5`의 예산을 실측치로 갱신하고 `../TASKS.md §3`에 남긴다

**T2-05** 프리픽스 불변성 강제 · `S` · 선행 T2-04
  파일 `tests/Npc.Tests/Llm/PromptPrefixTests.cs` (신규)
  사양 `docs/01 §10.2` · `../CLAUDE.md §2.5`
  내용 리스크 R11(캐시 미적중)의 탐지 장치. **토큰 상한은 단언하지 않는다** — T2-04에서 예산을 확정한 뒤에 붙인다. 하한은 지금 건다.
  완료 `PromptPrefix_IsByteIdentical_Across1000Builds` 통과 · `PromptPrefix_MeetsTokenFloor` 통과 (≥ 4,096)

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

**T2-09** `LlmPlanCompiler` 단건 생성 · `L` · 선행 T2-02, T2-04, T2-06, T2-08
  파일 `src/Npc.Llm/IPlanCompiler.cs`, `LlmPlanCompiler.cs` (신규)
  사양 `docs/12 §2` · `docs/measurements/W1_schema.md`
  내용 ⚠ **`docs/12 §2`의 `PlanCompiler` 라는 이름을 쓰지 않는다.** T1-23이 이미 `Npc.Core.Plan.PlanCompiler`(`PlanDocument` ↔ `CompiledPlan`)를 만들었고, `Npc.Llm`은 `Npc.Core`를 참조하므로 같은 이름을 두면 그 타입이 가려진다. 구현체 이름은 `LlmPlanCompiler`, 인터페이스는 `IPlanCompiler` 그대로.
  내용 `PlanRequest` → LLM 호출 → `PlanDocument`. 재시도·폴백은 T2-15/T2-17에서 추가.
  내용 **디코딩 모드를 고정하지 않는다.** W1에서 `response_format=json_schema`(forced)는 유효 JSON 99/100 을 내면서 검증통과 0 이었다 — 스키마에 없는 인자를 지어냈다(`V2.UNKNOWN_ARG` 99). 같은 모델의 prompt 모드는 93/100 통과였다. **모드를 설정으로 빼고 T2-19 첫 회차에서 둘 다 재서 높은 쪽으로 확정한다.**
  완료 `Compiler_ProducesValidJson` 통과 (임의 버킷 20건, 유효 JSON 100%) · **두 모드의 1·2단 통과율이 `W6_compile_stats.md`에 나란히 기록됨**

**T2-10** `CompileStats` 수집 · `M` · 선행 T2-09, T1-58
  파일 `src/Npc.Llm/CompileStats.cs` (신규), `src/Npc.Host/Metrics/NpcMeter.cs` (수정)
  사양 `docs/12 §2`
  내용 prompt/cached/completion 토큰, 지연, 비용, 모델, 시도 횟수. **모든 호출에서 기록** — P3 manifest와 W12 보고서의 원자료다.
  완료 `Stats_RecordedOnEveryCall` 통과 · 메트릭에 `prefix_sha` 태그 포함

**T2-11** 검증기 1·2단 결선 · `S` · 선행 T2-09, T1-25, T1-26
  파일 `src/Npc.Llm/LlmPlanCompiler.cs` (수정)
  사양 `docs/03 §3` · `docs/12 §5`
  내용 **강제 디코딩을 신뢰하지 않고 재검증.** `SchemaValidator`(T1-25)·`VocabularyValidator`(T1-26)를 그대로 부른다. 실패 코드를 `(stage, code, archetype)` 태그로 메트릭에 기록.
  완료 `Compiler_AlwaysValidates` 통과 (검증 우회 경로 없음)

---

## C. 검증기 완성 (W6)

**T2-12** 검증기 3단 실패 설명 보강 · `M` · 선행 T1-27
  파일 `src/Npc.Core/Validation/CoherenceValidator.cs`, `src/Npc.Core/Validation/VocabularyValidator.cs`, `src/Npc.MasterData/ActionCatalog.cs` (수정)
  사양 `docs/12 §5`
  내용 `Explain()` 자체는 T1-27에 이미 있고 `previous_attempt_failed` JSON 블록(stage/code/step/detail)을 만든다. **대부분의 `detail`도 이미 자연어다** — `PRECONDITION_UNMET`·`FORBIDDEN_FLAG`·`LOOP_NOT_CLOSED`는 `WorldFlagTable.Format`으로 플래그 이름을 펴서 넣는다.
  내용 ⚠ **남은 구멍은 `V3.RESOURCE_IMBALANCE` 하나다.** 지금 `"...but consumes..."` 문장이 아이템을 **code 숫자**(`item.Value`)로 찍는다 — 정확히 "숫자를 그대로 내보내면 모델이 못 고친다"에 걸리는 경우다. `IPlanValidationVocabulary`에 `ItemName(ItemId)`이 없어서 그렇다. 인터페이스에 추가하고 `Npc.MasterData`가 구현한다.
  완료 `Explain_IsHumanReadable` 통과 (V3 7종 전부에 대해 플래그·액션·아이템이 **이름**으로 나오고 스텝 번호 포함) · 숫자 code 가 `detail`에 남지 않음 · 블록 형식(JSON)은 그대로 — T2-15가 이 문자열을 서픽스에 붙인다

**T2-13** `DryRunValidator` (4단) · `L` · 선행 T2-12, T1-46
  파일 `src/Npc.Core/Validation/IDryRunValidator.cs` (신규), `src/Npc.Sim/Validation/DryRunValidator.cs`, `src/Npc.Sim/SimWorld.Minimal.cs` (신규)
  사양 `docs/03 §3` 4단 · `docs/12 §5` · `../CLAUDE.md §3`
  내용 ⚠ **`docs/12 §5`가 지정한 `Npc.Core/Validation/DryRunValidator.cs` 위치는 의존 규칙 위반이다.** `Npc.Core`는 `Npc.Contracts`만 참조하는데 `SimWorld`는 `Npc.Sim`에 있다. **`IPlanVocabulary`와 같은 패턴으로 가른다** — 계약(`IDryRunValidator`)은 `Npc.Core`가 선언하고, `SimWorld`를 쓰는 구현은 `Npc.Sim`이 갖는다. 호출자(`Npc.Llm`)는 인터페이스만 보므로 `Npc.Llm → Npc.Core` 한 줄이 유지된다. 구현체 주입은 Host·Prebake가 한다.
  내용 NPC 1마리 · 1000배속 축소 Sim에서 1~2 사이클 실행. `SimWorld`에 `CreateMinimal`/`Spawn`/`AssignPlan`/`RunCycles`가 **아직 없다** — 이 태스크에서 만든다.
  완료 `V4.*` 5종(DEADLOCK/TIMEOUT/INFINITE_LOOP/RESOURCE_STARVE/OSCILLATION) 전부 유발 픽스처로 검증 · 1건 ≤ 100ms · `Architecture_DependencyGraphMatchesSpec` 통과 (`Npc.Core`의 허용 참조는 `Npc.Contracts` 하나뿐이다 — 이 테스트가 위반을 잡는다)

**T2-14** 드라이런 결정론 · `S` · 선행 T2-13
  파일 `src/Npc.Sim/Validation/DryRunValidator.cs` (수정)
  사양 `docs/12 §5` · `../CLAUDE.md §2.3`
  내용 `seed`를 버킷 키에서 유도. **비결정적이면 골든 테스트가 불안정해진다.** `SimWorld`의 기존 시드 경로(`SimOptions.Seed`)를 쓰고 새 `Random`을 만들지 않는다.
  완료 `DryRun_IsDeterministic` 통과 (같은 플랜 100회 → 동일 판정)

---

## D. 실패 처리 (W6)

**T2-15** 재시도 파이프라인 · `M` · 선행 T2-11, T2-13
  파일 `src/Npc.Llm/LlmPlanCompiler.cs` (수정)
  사양 `docs/12 §6`
  내용 **1회만.** `CoherenceValidator.Explain`이 만드는 `previous_attempt_failed` 블록을 **서픽스에만** 붙인다 (블록을 새로 만들지 않는다). temperature 0.4 → 0.6.
  완료 `Retry_DoesNotTouchPrefix` 통과 (재시도 시 프리픽스 SHA 동일) · `Retry_MaxOnce` 통과 · 재시도 서픽스도 300 토큰 이내 (T2-07 테스트에 재시도 케이스 추가)

**T2-16** 인접 버킷 재사용 · `M` · 선행 T2-15, T1-17
  파일 `src/Npc.Planning/BucketNeighbors.cs` (신규)
  사양 `docs/12 §7`
  내용 §7의 5순위. **아키타입은 절대 바꾸지 않는다.** 재사용 플랜도 2·3단 재검증.
  완료 `Neighbors_NeverCrossArchetype` 통과 · `Neighbors_ReusedPlanRevalidated` 통과

**T2-17** 폴백 경로 · `S` · 선행 T2-16, T1-55
  파일 `src/Npc.Llm/LlmPlanCompiler.cs` (수정)
  사양 `docs/12 §6`
  내용 `PlanStore.Resolve`는 T1-39부터 이미 절대 null 이 아니다(버킷 미스 → 아키타입 폴백 → 최후 플랜). 컴파일러도 같은 보장을 갖게 한다.
  완료 `Compiler_NeverReturnsNullPlan` 통과 (LLM 전면 실패 시에도 폴백 반환)

**T2-18** `rejected/` 보존 · `S` · 선행 T2-11
  파일 `src/Npc.Llm/RejectedStore.cs` (신규)
  사양 `docs/13 §8`
  내용 검증 실패 산출물 + 실패 코드 전량 보존. **조용히 버리면 품질 개선 원자료가 사라진다.**
  완료 실패 1건당 `planstore/rejected/<bucket>.<attempt>.json` 생성

---

## E. 통과율 개선 루프 (W6 후반)

**T2-19** 전량 생성 러너 · `M` · 선행 T2-17, T2-18
  파일 `tools/Npc.Prebake/BulkRunner.cs`, `tools/Npc.Prebake/Npc.Prebake.csproj` (신규, P3에서 CLI로 승격), `NpcServer.sln` (수정)
  사양 `docs/12 §8` · `docs/measurements/W1_concurrency.md`
  내용 2,880 버킷 전량 1회 생성.
  내용 ⚠ **"외부 API 동시 32"에는 아직 근거가 없다.** W1 T0-11은 로컬 2종만 쟀고(`llamacpp-gemma3-4b`·`qwen3-8b`), 외부 API는 측정하지 않았다. **동시 8에서 시작해 T3-12의 AIMD로 올리며 429 최초 발생 지점을 이 실행에서 실측한다.** 그 값이 T3-08 `--concurrency` 기본값이 된다.
  내용 T2-09가 남긴 디코딩 모드 2종(forced/prompt)의 통과율도 이 회차에서 가른다.
  완료 2,880건 완주 · **소요·비용·429 최초 동시성을 `W6_compile_stats.md`에 기록** (소요 ≤ 10분은 목표이지 게이트가 아니다 — 근거가 되는 외부 처리량 실측이 아직 없다)

**T2-20** 실패 코드 집계 리포트 · `S` · 선행 T2-19
  파일 `tools/report_failures.cs` (신규 — `.NET 10` 파일 기반 앱. `dotnet run tools/report_failures.cs`), `docs/measurements/W6_failures.md` (산출)
  사양 `docs/12 §8`
  내용 `(stage, code, archetype)` 3축 집계 + 상위 원인 Top 5. **`.csx`(dotnet-script)를 쓰지 않는다** — 이 저장소의 도구는 `tools/gen_npcs.cs`처럼 전부 파일 기반 앱이다.
  완료 리포트 생성 · 상위 3개 원인이 명시됨

**T2-21** 통과율 개선 1~3차 · `L` · 선행 T2-20
  파일 (프롬프트·스키마·카탈로그 수정)
  사양 `docs/12 §8` 처방표
  내용 §8 표의 처방을 상위 3개 원인에 적용 → 재생성 → 재집계. **최대 3회 반복.**
  완료 통과율 ≥ 90% · 각 차수의 통과율 변화가 `W6_failures.md`에 기록

**T2-22** 플랜 다양성 지표 · `M` · 선행 T2-19
  파일 `tools/measure_diversity.cs` (신규 — 파일 기반 앱)
  사양 `docs/12 §10` · `docs/measurements/W1_quality.md`
  내용 유니크 액션 시퀀스 / 전체. 아키타입 고정 시 버킷별로 실제로 다른가. **W1(T0-12)의 채점식과 같은 정의를 쓴다** — 거기서 `llamacpp-gemma3-4b`가 다양성 1.17/2 (유니크 시퀀스 비율 ≈ 58%)였다. 지금 60% 미달은 이미 예고된 값이지 새 소식이 아니다.
  완료 지표 산출 · **≥ 60%.** 미달 시 T2-21에 반영하고, W12 블라인드 평가에서 "구분 불가"가 나올 가능성을 `../TASKS.md` §3에 경보로 기록

**T2-23** `count` 심볼화 (조건부) · `M` · 선행 T2-20
  파일 `masterdata/actions.json` (수정), `src/Npc.Core/Plan/CompiledPlan.cs`·`PlanDocument.cs` (수정), `src/Npc.MasterData/ActionCatalog.cs` (수정 — `IPlanVocabulary.TryPackArgs`/`UnpackArgs`)
  사양 `docs/12 §8` 말미 · `docs/03 §5`
  내용 `V3.RESOURCE_IMBALANCE`가 상위 원인이면 실행한다. `count`를 모델이 정하지 않고 `"max"`/`"needed"` 심볼만 허용 → 런타임이 인벤 보고 결정. **`CompiledStep.Count`는 `ushort` 한 칸뿐이다** — 심볼은 예약값(예: `0xFFFF`, `0xFFFE`)으로 인코딩하고 `docs/03 §5`에 명문화한다. 스텝 크기 32바이트 상한(`CompiledStep_SizeIsBounded`)을 깨지 않는다.
  완료 (실행 시) `V3.RESOURCE_IMBALANCE` 발생률 0 · `CompiledStep_SizeIsBounded` 여전히 통과 · (미실행 시) 원장에 `-`로 표기하고 이유 기록

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
[ ] 서픽스 토큰 ≤ 300 (전 버킷·스트레스 케이스·재시도 케이스)
[ ] 프리픽스 SHA가 전 요청에서 1종
[ ] 프롬프트 캐시 적중률 ≥ 95% (외부 API — cached_tokens 기준)
[ ] 드라이런 결정론 (같은 플랜 100회 → 동일 판정)
[ ] 플랜 다양성 ≥ 60%
```

> **로컬 엔진은 캐시 적중을 토큰으로 못 잰다.** dotLLM 0.1.0-preview.3 은 `cached_tokens`를 항상 0으로 보고한다(`W1_env.md §4.4`).
> 로컬 판정은 **prefill 시간 감소**로 한다 — W1 실측으로 4B 1,531 → 237ms, 8B 2,608 → 316ms 였다.

---

## 진행 체크리스트

```
W5 ── 기반 · 단건 생성
[x] T2-01 Llm프로젝트배선   [x] T2-02 SchemaProvider   [x] T2-03 카탈로그렌더러
[ ] T2-04 PromptPrefix정식  [ ] T2-05 프리픽스불변성   [ ] T2-06 서픽스조립기
[ ] T2-07 서픽스예산테스트  [ ] T2-08 ChatClientFactory
[ ] T2-09 LlmPlanCompiler   [ ] T2-10 CompileStats     [ ] T2-11 검증1·2단결선

W6 ── 검증기완성 · 실패처리 · 개선루프
[ ] T2-12 Explain보강       [ ] T2-13 DryRunValidator  [ ] T2-14 드라이런결정론
[ ] T2-15 재시도(1회)       [ ] T2-16 인접버킷재사용   [ ] T2-17 폴백경로
[ ] T2-18 rejected보존
[ ] T2-19 전량생성러너      [ ] T2-20 실패집계리포트   [ ] T2-21 개선루프 ★
[ ] T2-22 다양성지표 ★      [ ] T2-23 count심볼화(조건) [ ] T2-24 W6리포트
```

★ T2-21과 T2-22가 이 Phase의 실질이다. 나머지는 배관이다.
