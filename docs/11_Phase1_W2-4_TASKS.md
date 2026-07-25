# P1 (W2–4) 작업 지시서 — 코어 · 런타임 · Sim

사양: [`11_Phase1_W2-4_Core_Runtime.md`](11_Phase1_W2-4_Core_Runtime.md) · 규약: [`../TASKS.md`](../TASKS.md)

> **게이트: LLM 0회 호출로 NPC 500마리가 게임 7일을 완주한다.**
> 이 Phase에는 `Npc.Llm`이 존재하지 않는다. 그 참조를 추가하는 태스크가 하나도 없다.

---

## A. 솔루션 골격 (W2)

**T1-01** 솔루션 + 빌드 설정 · `S` · 선행 —
  파일 `NpcServer.sln`, `Directory.Build.props`, `.gitignore`, `.editorconfig` (전부 신규)
  사양 `docs/11 §2` · `../CLAUDE.md §6`
  내용 net10.0 / `TreatWarningsAsErrors` / `Nullable` / `ServerGC` / `InvariantGlobalization`. gitignore는 `CLAUDE.md §6` 목록 그대로.
  완료 `dotnet build` 성공 · `planstore/pinned/`는 무시되지 않음을 확인

**T1-02** 프로젝트 10개 + 참조 그래프 · `M` · 선행 T1-01
  파일 `src/Npc.{Contracts,Core,MasterData,Runtime,Planning,Llm,Gateway,Sim,Host}/*.csproj`, `tests/Npc.Tests/*.csproj`
  사양 `docs/11 §2` · `../CLAUDE.md §3`
  내용 §3 의존 그래프대로 참조 배선. `Npc.Contracts`·`Npc.Core`는 NuGet 의존 0.
  완료 `dotnet build` 성공 · **`Npc.Runtime`이 `Npc.Llm`을 참조하지 않음**을 테스트로 확인 (`Architecture_RuntimeDoesNotReferenceLlm`)

**T1-03** CI 스크립트 · `S` · 선행 T1-02
  파일 `.github/workflows/ci.yml` 또는 `build.ps1` (신규)
  사양 `../CLAUDE.md §1, §5`
  내용 build → `dotnet format --verify-no-changes` → `dotnet test --filter Category!=Golden`.
  완료 로컬에서 스크립트 1회 실행으로 3단계 전부 통과

---

## B. `Npc.Contracts` (W2)

**T1-04** 강타입 ID + 좌표 · `S` · 선행 T1-02
  파일 `src/Npc.Contracts/Ids.cs` (신규)
  사양 `docs/02 §3.1`
  내용 NpcId/PlayerId/ArchetypeId/ZoneId/PoiId/ActionId/ItemId/DialogueId/AnimationId/PlanId/CorrelationId/Tick + WorldPos. 전부 `readonly record struct`.
  완료 12종 + WorldPos 정의 · 컴파일

**T1-05** `NpcCommand` · `M` · 선행 T1-04
  파일 `src/Npc.Contracts/NpcCommand.cs` (신규)
  사양 `docs/02 §3.2`
  내용 `NpcCommandKind` 12종 + `CommandPriority` + `VisualState` + 헤더/페이로드 필드. 필드 유니온 대신 명시 필드.
  완료 §3.2 "명령별 사용 필드" 표의 12종을 전부 구성 가능

**T1-06** `GameEvent` · `M` · 선행 T1-04
  파일 `src/Npc.Contracts/GameEvent.cs` (신규)
  사양 `docs/02 §3.3`
  내용 `GameEventKind` 17종 + `ActionFailReason` + `Sequence`/`Correlation` 헤더.
  완료 §3.3 표의 17종을 전부 구성 가능

**T1-07** `IGameServerLink` · `S` · 선행 T1-05, T1-06
  파일 `src/Npc.Contracts/IGameServerLink.cs` (신규)
  사양 `docs/02 §2`
  내용 `Enqueue`/`FlushAsync`/`Events`/`State`/`Stats`/`StateChanged`. **반환값 있는 전송 메서드를 두지 않는다(N1).**
  완료 인터페이스 정의 · `Task<T>`/`ValueTask<T>` 반환 멤버가 없음

**T1-08** Contracts 규칙 테스트 · `M` · 선행 T1-07
  파일 `tests/Npc.Tests/Contracts/ContractRuleTests.cs` (신규)
  사양 `docs/02 §1 N2·N3·N4` · `docs/02 §6`
  내용 리플렉션으로 `Npc.Contracts` 어셈블리의 모든 public struct 순회.
  완료 `Contracts_NoStringFields` · `Contracts_NoDateTimeFields` · `Contracts_AllValueTypes` 3개 통과

---

## C. 마스터데이터 — 데이터 작성 (W2)

> 작성 순서를 지킨다 (`docs/01 §12`). 역순이면 계속 되돌아온다.

**T1-09** `world_flags.json` 작성 · `M` · 선행 —
  파일 `masterdata/world_flags.json` (신규)
  사양 `docs/01 §1`
  내용 42개 플래그. bit 22~23, 44~63 예약.
  완료 42개 · bit 중복 없음 · 예약 구간 비어 있음

**T1-10** `WorldFlags` 소스 생성기 · `M` · 선행 T1-09, T1-02
  파일 `src/Npc.Core/Generators/WorldFlagsGenerator.cs` (신규) 또는 T4 빌드 태스크
  사양 `docs/01 §1`
  내용 `world_flags.json` → `[Flags] enum WorldFlags : ulong`. 손으로 쓴 enum을 두지 않는다.
  완료 `WorldFlags.AtHome == 1UL<<0` 등 5개 샘플 검증 테스트 통과

**T1-11** `items.json` 작성 + 로더 · `M` · 선행 T1-09
  파일 `masterdata/items.json` (신규), `src/Npc.MasterData/ItemTable.cs` (신규)
  사양 `docs/01 §3`
  내용 아이템 ~80 + 레시피. `grants`로 플래그 연결. **"빵을 가지면 HasFood"를 코드에 하드코딩하지 않는다.**
  완료 `ItemTable_GrantsAreDataDriven` 통과 (인벤 변경 → 플래그 재계산이 데이터만 보고 동작)

**T1-12** `actions.json` 작성 · `L` · 선행 T1-09, T1-11
  파일 `masterdata/actions.json` (신규)
  사양 `docs/01 §2`
  내용 **37개**. `docs/01 §2.2` 표 그대로. 각 액션의 desc는 LLM 프롬프트에 실리므로 문장으로 쓴다.
  완료 37개 · code 중복 없음 · 모든 requires/forbids/grants/clears가 world_flags에 존재 · 모든 param type이 §2.3의 7종 중 하나

**T1-13** `ActionCatalog` 로더 · `M` · 선행 T1-12, T1-10
  파일 `src/Npc.MasterData/ActionCatalog.cs` (신규)
  사양 `docs/01 §2` · `docs/03 §5`
  내용 `ActionDef[]` 배열. `ActionId.Value`가 곧 인덱스. requires/grants를 `WorldFlags`로 파싱.
  완료 `ActionCatalog_IndexesByCode` 통과 · 37개 로드 · 미정의 flag 참조 시 예외

**T1-14** `zones.json` + `pois.json` 작성 · `L` · 선행 T1-09
  파일 `masterdata/zones.json`, `masterdata/pois.json` (신규)
  사양 `docs/01 §4`
  내용 존 12개, POI 약 250개. POI 타입 8종. `allowed_archetypes`·`capacity`·`open_hours`·`grants`.
  완료 존 12 · POI 250±20 · 모든 POI의 zone 참조 유효 · 고립 존 없음

**T1-15** POI 거리 행렬 생성 도구 · `M` · 선행 T1-14
  파일 `tools/gen_poi_distances.cs` (신규), `masterdata/poi_distances.bin` (산출)
  사양 `docs/01 §4`
  내용 250×250 `Half` 행렬. 존 그래프 기반 최단거리(Floyd–Warshall이면 충분).
  완료 파일 크기 ≈ 122KB · 대칭성 검증 · 무한대(도달 불가) 항목 0

**T1-16** `archetypes.json` 작성 + 로더 · `L` · 선행 T1-12, T1-14
  파일 `masterdata/archetypes.json` (신규), `src/Npc.MasterData/ArchetypeTable.cs` (신규)
  사양 `docs/01 §5`
  내용 40종. §5의 그룹 구성안(생산10/채집7/상업5/치안5/종교4/주민6/특수3).
  완료 40종 · `population_weight` 합 = 1.0 ±0.001 · 모든 `allowed_actions`가 카탈로그에 존재

**T1-17** `context_buckets.json` + `BucketKey` · `M` · 선행 T1-16
  파일 `masterdata/context_buckets.json` (신규), `src/Npc.Core/Planning/BucketKey.cs` (신규)
  사양 `docs/01 §6`
  내용 TimeOfDay 6 / RegionState 4 / Climate 3. `ToIndex()` = `((A*6+T)*4+R)*3+C`.
  완료 `BucketKey_IndexIsBijective` 통과 (0..2879 전단사) · `total_keys == 2880`

**T1-18** `interrupts.json` + 규칙 파서 · `M` · 선행 T1-12
  파일 `masterdata/interrupts.json` (신규), `src/Npc.MasterData/InterruptRules.cs` (신규)
  사양 `docs/01 §7`
  내용 §7의 5개 규칙 + 추가 ~7개. `when`(플래그/이벤트/성향 조건) → `then`(즉시 액션) + `replan.urgency`.
  완료 `InterruptRules_MatchByPriority` 통과 (우선순위 높은 규칙이 먼저 매칭)

**T1-19** `MasterDataSet` + ContentHash · `M` · 선행 T1-13, T1-16, T1-17, T1-18
  파일 `src/Npc.MasterData/MasterDataSet.cs`, `MasterDataLoader.cs` (신규)
  사양 `docs/01 §11`
  내용 전체 로드 → 읽기 전용 인덱스. 파일별 해시 + 전체 `ContentHash`.
  완료 `MasterData_ContentHashIsStable` 통과 (동일 입력 → 동일 해시, 100회)

**T1-20** 검증 V1~V11 · `L` · 선행 T1-19
  파일 `src/Npc.MasterData/Validation/MasterDataValidator.cs` (신규)
  사양 `docs/01 §11`
  내용 V1~V11 전부. **실패는 기동 실패**로 처리(예외).
  완료 V1~V11 각각에 위반 픽스처 1개씩 만들어 정확한 코드로 실패하는 테스트 11개 통과

**T1-21** `validate` CLI 서브커맨드 · `S` · 선행 T1-20
  파일 `src/Npc.Host/Commands/ValidateCommand.cs` (신규)
  사양 `docs/11 §11`
  내용 `Npc.Host validate --masterdata ./masterdata` → V1~V11 결과 출력, 실패 시 비0 종료.
  완료 정상 데이터 종료코드 0 · 손상 데이터 종료코드 1 + 위반 코드 출력

---

## D. `Npc.Core` — 플랜 모델 · 검증기 (W2)

**T1-22** `PlanDocument` JSON 모델 · `M` · 선행 T1-02
  파일 `src/Npc.Core/Plan/PlanDocument.cs`, `PlanJsonContext.cs` (신규)
  사양 `docs/03 §1, §2`
  내용 DSL의 C# 모델. `System.Text.Json` 소스 생성기. 리플렉션 0.
  완료 `docs/03 §1` 예시 JSON 왕복 · 소스 생성 컨텍스트 사용 확인

**T1-23** `CompiledPlan` / `CompiledStep` · `M` · 선행 T1-22, T1-13
  파일 `src/Npc.Core/Plan/CompiledPlan.cs` (신규)
  사양 `docs/03 §5`
  내용 `RequiredFlags`/`ForbiddenFlags` 사전 OR. `CompiledStep`은 값 타입.
  완료 `CompiledStep_SizeIsBounded` 통과 (`Unsafe.SizeOf<CompiledStep>() <= 32`) · `Plan_RoundTrip` 통과

**T1-24** `PoiSymbol` + 바인딩 · `M` · 선행 T1-14, T1-16
  파일 `src/Npc.Core/Plan/PoiSymbol.cs`, `src/Npc.Runtime/PoiBinder.cs` (신규)
  사양 `docs/03 §2` POI 심볼 표
  내용 9종 심볼. 개체별 바인딩(`$home`→인스턴스 값, `$nearest_*`→거리 행렬). **바인딩에 `npcId` 해시 지터를 넣어** 전원이 같은 POI로 몰리지 않게 한다.
  완료 `PoiBinder_DistributesNearest` 통과 (동일 조건 NPC 100마리가 2개 이상 POI로 분산)

**T1-25** 검증기 1단 (스키마) · `S` · 선행 T1-22
  파일 `src/Npc.Core/Validation/SchemaValidator.cs` (신규)
  사양 `docs/03 §3` 1단
  완료 `V1.PARSE`/`V1.SCHEMA`/`V1.STEP_COUNT`/`V1.EXTRA_FIELD` 각각 유발 픽스처로 검증

**T1-26** 검증기 2단 (어휘) · `M` · 선행 T1-25, T1-13, T1-16
  파일 `src/Npc.Core/Validation/VocabularyValidator.cs` (신규)
  사양 `docs/03 §3` 2단
  완료 `V2.*` 8종 전부 유발 픽스처로 검증

**T1-27** 검증기 3단 (정합성) · `L` · 선행 T1-26, T1-23
  파일 `src/Npc.Core/Validation/CoherenceValidator.cs` (신규)
  사양 `docs/03 §3` 3단 · `docs/12 §5`
  내용 GOAP 스타일 상태 전이 시뮬. `Explain()`으로 실패를 **자연어**로 만든다(재시도 프롬프트용). 비트마스크 숫자를 그대로 내보내지 않는다.
  완료 `V3.*` 7종 전부 유발 픽스처로 검증 · `Explain()`이 플래그 이름을 포함

---

## E. `Npc.Runtime` (W3)

**T1-28** `NpcStore` SoA · `M` · 선행 T1-10, T1-23
  파일 `src/Npc.Runtime/NpcStore.cs`, `RingBuffer8.cs` (신규)
  사양 `docs/11 §3`
  내용 핫/웜/콜드 배열 분리. `RingBuffer8<RecentEvent>` (salience 낮은 것부터 밀림).
  완료 `NpcStore_HotArraysUnder128KB` 통과 (NPC 5,000 기준) · `RingBuffer8_EvictsLowestSalience` 통과

**T1-29** `GameClock` · `M` · 선행 T1-17
  파일 `src/Npc.Runtime/GameClock.cs` (신규)
  사양 `docs/11 §5`
  내용 10Hz 틱, `TimeScale`, `TimeOfDay` 산출. **`DateTime` 금지.**
  완료 `GameClock_TimeOfDayTransitions` 통과 (게임 24시간에 6구간 전부 통과) · `DateTime` 사용 0

**T1-30** `EventApplier` (멱등) · `L` · 선행 T1-28, T1-06, T1-11
  파일 `src/Npc.Runtime/EventApplier.cs` (신규)
  사양 `docs/02 §3.3` · `docs/02 §1 N6·N7`
  내용 17종 이벤트 → 위치/플래그/인벤/HP/LOD 갱신. 시퀀스 갭 검출. 인벤 변경 시 `items.json`의 `grants`로 플래그 재계산.
  완료 `Link_Idempotent` 통과 (동일 이벤트 2회 → 상태 해시 동일) · `Link_SequenceGap` 통과

**T1-31** `CorrelationTable` · `S` · 선행 T1-28
  파일 `src/Npc.Runtime/CorrelationTable.cs` (신규)
  사양 `docs/02 §3.4`
  내용 NPC별 진행 중 상관 ID. 스왑 시 무효화(낡은 응답 무시).
  완료 `Correlation_StaleResponseIgnored` 통과

**T1-32** `PlanExecutor` 스텝 발행 · `L` · 선행 T1-23, T1-24, T1-28, T1-31, T1-07
  파일 `src/Npc.Runtime/PlanExecutor.cs`, `CommandEmitter.cs` (신규)
  사양 `docs/03 §6` · `docs/01 §2.1` `emits`
  내용 액션 → `NpcCommand` 매핑은 `actions.json`의 `emits`가 결정한다. **`IEnumerable` 반환 금지 — `Span<NpcCommand>`에 쓴다.**
  완료 `Executor_EmitsPerCatalog` 통과 · 발행 경로 할당 0 (`GC.GetAllocatedBytesForCurrentThread` 델타 = 0)

**T1-33** 타임아웃 합성 · `M` · 선행 T1-32
  파일 `src/Npc.Runtime/PlanExecutor.cs` (수정)
  사양 `docs/02 §3.4` · `docs/03 §6`
  내용 `timeout_s` 경과 시 로컬에서 `ActionFailed(Timeout)` 합성 → 진행 재개. 모든 스텝에 기본 타임아웃 강제.
  완료 `Executor_TimeoutSynthesis` 통과 (명령 드롭 → 타임아웃 후 다음 스텝 진행)

**T1-34** `PlanSwapper` 원자 스왑 · `M` · 선행 T1-32
  파일 `src/Npc.Runtime/PlanSwapper.cs` (신규)
  사양 `docs/03 §6` 스왑 규약
  내용 `PendingPlanId`를 스텝 경계에서만 반영. 인터럽트 시에는 즉시 허용 + 상관 ID 무효화.
  완료 `Executor_AtomicSwap` 통과 (스텝 실행 중 스왑 요청 → 경계에서만 교체, 상관 ID 누수 0)

**T1-35** `InterruptMatcher` · `M` · 선행 T1-18, T1-30, T1-34
  파일 `src/Npc.Runtime/InterruptMatcher.cs` (신규)
  사양 `docs/01 §7` · `docs/11 §5`
  내용 이벤트 수신 시 규칙 매칭 → 즉시 액션 강제 + urgency를 큐에 삽입. **LLM 개입 없음.**
  완료 `Interrupt_ImmediateWithinOneTick` 통과 (전투 이벤트 → 1틱 내 Flee/Attack 명령 발행)

**T1-36** LOD 밴드 멤버십 · `M` · 선행 T1-28
  파일 `src/Npc.Runtime/LodBand.cs` (신규)
  사양 `docs/11 §4`
  내용 4밴드, 슬라이스 분할. 등급 변경은 `PlayerProximity`로만. **틱당 이동 최대 64건.**
  완료 `Lod_MigrationCappedPerTick` 통과 · 1,000건 동시 변경 요청이 16틱에 걸쳐 처리됨

**T1-37** `CognitionScheduler` · `L` · 선행 T1-36, T1-23
  파일 `src/Npc.Runtime/CognitionScheduler.cs` (신규)
  사양 `docs/11 §4` · `docs/03 §5`
  내용 밴드별 주기(1/10/100/이벤트) 슬라이스 스캔. 이탈 판정은 **비트 연산 한 번**.
  완료 `Cognition_ScanTargetsUnder150` 통과 (NPC 5,000) · `Cognition_IsConstantWithScale` 통과 (10,000으로 늘려도 틱당 스캔 ≤ 150) · 스캔 소요 ≤ 3ms

**T1-38** `ReplanQueue` 스텁 · `S` · 선행 T1-37
  파일 `src/Npc.Planning/ReplanQueue.cs` (신규)
  사양 `docs/14 §2` (정식은 P4)
  내용 **이 Phase는 FIFO + 중복 제거만.** 우선순위 힙은 T4-01에서 교체.
  완료 `ReplanQueue_DeduplicatesNpc` 통과 · 인터페이스가 T4-01에서 교체 가능한 형태

**T1-39** `PlanStore` 스텁 · `S` · 선행 T1-23
  파일 `src/Npc.Planning/PlanStore.cs` (신규)
  사양 `docs/13 §2` (정식은 P3)
  내용 **이 Phase는 폴백만 반환한다.** `Resolve`는 절대 null 아님.
  완료 `PlanStore_ResolveNeverNull` 통과

**T1-40** `NpcServerLoop` 조립 · `L` · 선행 T1-30, T1-32, T1-34, T1-35, T1-37, T1-39
  파일 `src/Npc.Runtime/NpcServerLoop.cs` (신규)
  사양 `docs/02 §4` · `docs/11 §5`
  내용 이벤트 배수 → 틱 진행 → 스캔 → 실행 → 스왑 → Flush. **루프 안 `await`은 `FlushAsync` 하나뿐.**
  완료 `Loop_NoAwaitExceptFlush` (Roslyn 분석 또는 코드 리뷰 체크) · 500 NPC 1게임일 완주

---

## F. `Npc.Gateway` (W3)

**T1-41** `NullGameServerLink` · `S` · 선행 T1-07
  파일 `src/Npc.Gateway/NullGameServerLink.cs` (신규)
  사양 `docs/02 §2`
  완료 명령 폐기 · 이벤트 없음 · `Stats.CommandsEnqueued` 증가

**T1-42** `LoopbackGameServerLink` · `M` · 선행 T1-07, T1-46
  파일 `src/Npc.Gateway/LoopbackGameServerLink.cs` (신규)
  사양 `docs/02 §2, §5`
  내용 `SimWorld`에 직결. `BoundedChannel` 역압 — 포화 시 `Cosmetic` 우선순위부터 드롭.
  완료 `Link_Backpressure` 통과 (`Critical`은 무손실, `Cosmetic`부터 드롭)

**T1-43** `RecordingGameServerLink` · `M` · 선행 T1-42
  파일 `src/Npc.Gateway/RecordingGameServerLink.cs` (신규)
  사양 `docs/02 §2` · `docs/15 §3`
  내용 데코레이터. 명령·이벤트를 jsonl로 기록. **시각은 `Tick`만 기록**(`DateTime` 금지).
  완료 기록 파일에 `DateTime` 문자열 0 · 명령 수 == `Stats.CommandsFlushed`

**T1-44** `ReplayGameServerLink` · `M` · 선행 T1-43
  파일 `src/Npc.Gateway/ReplayGameServerLink.cs` (신규)
  사양 `docs/15 §3`
  완료 기록 로그 재생 시 이벤트 시퀀스가 원본과 동일

**T1-45** `TcpGameServerLink` 골격 · `S` · 선행 T1-07
  파일 `src/Npc.Gateway/TcpGameServerLink.cs` (신규)
  사양 `docs/02 §2, §7`
  내용 **구현하지 않는다.** 인터페이스를 만족하는 골격 + `NotSupportedException` + §7의 TODO 5항목 주석.
  완료 **컴파일된다** · 모든 메서드가 `NotSupportedException`

---

## G. `Npc.Sim` — 게임서버 대역 (W3)

**T1-46** `SimWorld` 골격 · `M` · 선행 T1-05, T1-06, T1-19
  파일 `src/Npc.Sim/SimWorld.cs` (신규)
  사양 `docs/02 §5` · `docs/11 §6`
  내용 명령 디스패치 + 틱 진행 + 이벤트 발행 채널. 시퀀스 번호 부여.
  완료 `Spawn` → `NpcSpawned` 왕복

**T1-47** 이동 시뮬 · `M` · 선행 T1-46, T1-15
  파일 `src/Npc.Sim/MovementSim.cs` (신규)
  사양 `docs/02 §5`
  내용 거리 행렬 ÷ 속도 = 소요 틱. 도달 시 `NpcArrived`. **패스파인딩 안 한다.**
  완료 `Sim_ArrivalTimingMatchesDistance` 통과 (오차 ±1틱)

**T1-48** 상호작용/작업 시뮬 · `M` · 선행 T1-47, T1-13
  파일 `src/Npc.Sim/InteractionSim.cs` (신규)
  사양 `docs/02 §5`
  내용 `actions.json`의 duration만큼 대기 → `NpcActionCompleted`. 인벤 증감.
  완료 `Sim_ActionDurationFromCatalog` 통과

**T1-49** `NpcTransform` 발행 · `S` · 선행 T1-47
  파일 `src/Npc.Sim/TransformEmitter.cs` (신규)
  사양 `docs/11 §12` (이벤트 큐 폭주 대응)
  내용 이동 중 N틱마다 보간 발행. **LOD 2·3은 발행하지 않는다.**
  완료 `Sim_NoTransformForFarLod` 통과 · NPC 5,000에서 이벤트/초 ≤ 3,000

**T1-50** 욕구 진행 · `M` · 선행 T1-46
  파일 `src/Npc.Sim/NeedsSim.cs` (신규)
  사양 `docs/11 §6`
  내용 시간에 따라 배고픔·피로 상승 → `NpcVitalsChanged`. **이게 없으면 NPC가 계획을 바꿀 이유가 없다.**
  완료 `Sim_NeedsProgress` 통과 (게임 12시간 후 `IsHungry` 성립)

**T1-51** 가상 플레이어 봇 · `M` · 선행 T1-46
  파일 `src/Npc.Sim/PlayerBots.cs` (신규)
  사양 `docs/02 §5` · `docs/11 §6`
  내용 존을 랜덤 워크하는 봇 N명 → `PlayerProximity`(Enter/Leave). 난수는 시드 고정.
  완료 `Sim_PlayerBotsAreDeterministic` 통과 · 봇 20명 시 LOD 0 NPC가 발생함

**T1-52** 시나리오 스크립트 로더 · `M` · 선행 T1-46
  파일 `src/Npc.Sim/ScenarioRunner.cs` (신규), `scenarios/siege.jsonl` (신규)
  사양 `docs/02 §5`
  내용 jsonl의 `at_tick` 이벤트 주입. `ZoneStateChanged`/`WeatherChanged`/`KillSwitch`.
  완료 `Sim_ScenarioInjectsAtTick` 통과 (지정 틱 ±0에 이벤트 발생)

**T1-53** 실패·드롭 주입 · `S` · 선행 T1-48
  파일 `src/Npc.Sim/FaultInjector.cs` (신규)
  사양 `docs/11 §6`
  내용 `--fail-rate` (ActionFailed 확률), `--drop-rate` (명령 조용히 버림). 시드 고정.
  완료 `Sim_DropRateTriggersTimeoutPath` 통과 (drop 0.1 → 타임아웃 합성 경로가 실행됨)

---

## H. 폴백 · 인스턴스 · 호스트 (W4)

**T1-54** 폴백 플랜 40개 수작성 · `L` · 선행 T1-16, T1-27
  파일 `masterdata/fallback_plans.json` (신규), `docs/measurements/authoring_time.jsonl` (신규)
  사양 `docs/01 §8` · `docs/11 §7`
  내용 아키타입당 1개. 하루 사이클 4~8스텝, `loop: true`. **아키타입 하나당 작성 소요 시간을 반드시 기록한다** — W12 보고서의 분모다.
  완료 40개 전부 검증기 1~3단 통과 (`Validator_AcceptsAllFallbacks`) · `authoring_time.jsonl`에 40행

**T1-55** 폴백 로더 + 심볼 바인딩 · `M` · 선행 T1-54, T1-24
  파일 `src/Npc.MasterData/PlanTable.cs` (신규)
  사양 `docs/01 §8`
  내용 `$workplace`/`$home`/`$primary` 심볼을 NPC 인스턴스에서 바인딩.
  완료 `Fallback_BindsPerInstance` 통과

**T1-56** NPC 5,000 생성 · `M` · 선행 T1-16, T1-14
  파일 `tools/gen_npcs.cs` (신규), `masterdata/npc_instances.json` (산출)
  사양 `docs/01 §9` · `docs/11 §8`
  내용 인구 비중 배분 → POI 정원 내 배정 → `trait_offsets` 시드 난수.
  완료 `GenNpcs_IsReproducible` 통과 (같은 seed → 바이트 동일) · V10 통과 · 존별 인구가 capacity 이내

**T1-57** `Npc.Host` CLI + DI 조립 · `M` · 선행 T1-40, T1-42, T1-55, T1-56
  파일 `src/Npc.Host/Program.cs`, `HostOptions.cs` (신규)
  사양 `README §빠른 시작` 옵션 표
  내용 `--loopback/--link/--npcs/--time-scale/--days/--no-llm/--scenario/--fail-rate/--drop-rate`.
  완료 `--link null|record|replay` 3종이 코드 변경 없이 교체됨

**T1-58** 메트릭 등록 · `M` · 선행 T1-57
  파일 `src/Npc.Host/Metrics/NpcMeter.cs` (신규)
  사양 `docs/11 §9, §10`
  내용 `Meter` 히스토그램/카운터/게이지. 틱 시간, 스캔 대상, 큐 깊이, 링크 통계, 액션 분포.
  완료 `/metrics` JSON에 §10 5패널이 필요로 하는 지표 전부 존재

**T1-59** 대시보드 1차 · `M` · 선행 T1-58
  파일 `src/Npc.Host/wwwroot/dashboard.html` (신규)
  사양 `docs/11 §10`
  내용 단일 HTML. 5패널(틱/NPC/액션/링크/재계획). `/metrics` 폴링.
  완료 `http://localhost:5080/dashboard` 에서 5패널이 실시간 갱신

**T1-60** 할당 제거 패스 · `L` · 선행 T1-57
  파일 (다수 수정)
  사양 `docs/11 §9` 체크리스트
  내용 배치 큐 풀링, `Span` 반환, LINQ 제거, 소스 생성 로거, STJ 소스 생성기.
  완료 `Runtime_NoGen0GcInTickLoop` 통과 (1,000틱 동안 `GC.CollectionCount(0)` 델타 = 0)

**T1-61** 시간대 전환 지터 · `M` · 선행 T1-29, T1-34
  파일 `src/Npc.Runtime/BucketTransition.cs` (신규)
  사양 `docs/14 §5`
  내용 `Hash(npcId) % 601 - 300` 틱 지터. **`Random` 금지 — 결정론이어야 리플레이가 된다.**
  완료 `Transition_JitterIsDeterministic` 통과 · 전환 구간 틱 p99 ≤ 평시 2배

**T1-62** P1 게이트 검증 러너 · `M` · 선행 T1-59, T1-60, T1-61
  파일 `tests/Npc.Tests/Gates/Phase1GateTests.cs` (신규)
  사양 `docs/11 §11`
  내용 §11 체크리스트를 자동화. 500 NPC × 7게임일 + 5,000 NPC 틱 측정.
  완료 아래 체크리스트 8항목 전부 통과

```
[ ] 500 NPC × 게임 7일 완주, 크래시·데드락 없음
[ ] LLM 호출 카운터 = 0
[ ] 대장장이 1마리 추적 로그가 기상→일터→선술집→귀가 사이클을 보임
[ ] 틱 p99 ≤ 20ms (5,000 NPC 기준)
[ ] 인지 스캔 ≤ 150/틱
[ ] IGameServerLink를 Null로 교체해도 코드 변경 없이 기동
[ ] 마스터데이터 V1~V11 통과
[ ] 틱 루프 Gen0 GC = 0
```

---

## 진행 체크리스트

```
W2 ── 골격 · 계약 · 마스터데이터 · 코어
[x] T1-01 솔루션+빌드설정   [x] T1-02 프로젝트10개   [x] T1-03 CI
[x] T1-04 강타입ID          [x] T1-05 NpcCommand     [x] T1-06 GameEvent
[x] T1-07 IGameServerLink   [x] T1-08 Contracts테스트
[x] T1-09 world_flags       [x] T1-10 WorldFlags생성기
[x] T1-11 items             [x] T1-12 actions(37)    [x] T1-13 ActionCatalog
[x] T1-14 zones+pois        [x] T1-15 거리행렬       [x] T1-16 archetypes(40)
[x] T1-17 buckets+BucketKey [x] T1-18 interrupts     [x] T1-19 MasterDataSet
[x] T1-20 검증V1~V11        [x] T1-21 validate CLI
[x] T1-22 PlanDocument      [x] T1-23 CompiledPlan   [x] T1-24 PoiSymbol
[x] T1-25 검증1단           [x] T1-26 검증2단        [x] T1-27 검증3단

W3 ── 런타임 · 게이트웨이 · Sim
[ ] T1-28 NpcStore SoA      [ ] T1-29 GameClock      [ ] T1-30 EventApplier
[ ] T1-31 CorrelationTable  [ ] T1-32 PlanExecutor   [ ] T1-33 타임아웃합성
[ ] T1-34 PlanSwapper       [ ] T1-35 InterruptMatcher
[ ] T1-36 LodBand           [ ] T1-37 CognitionScheduler
[ ] T1-38 ReplanQueue스텁   [ ] T1-39 PlanStore스텁  [ ] T1-40 NpcServerLoop
[ ] T1-41 NullLink          [ ] T1-42 LoopbackLink   [ ] T1-43 RecordingLink
[ ] T1-44 ReplayLink        [ ] T1-45 TcpLink골격
[ ] T1-46 SimWorld          [ ] T1-47 이동시뮬       [ ] T1-48 상호작용시뮬
[ ] T1-49 Transform발행     [ ] T1-50 욕구진행       [ ] T1-51 플레이어봇
[ ] T1-52 시나리오로더      [ ] T1-53 실패·드롭주입

W4 ── 폴백 · 인스턴스 · 호스트 · 튜닝
[ ] T1-54 폴백40개 수작성 ★ [ ] T1-55 폴백로더       [ ] T1-56 NPC5000생성
[ ] T1-57 Host CLI+DI       [ ] T1-58 메트릭         [ ] T1-59 대시보드1차
[ ] T1-60 할당제거          [ ] T1-61 전환지터       [ ] T1-62 게이트검증 ★
```

★ T1-54는 사람이 직접 하는 작업이다. 코딩 에이전트에게 맡기면 W12 보고서의 비교 기준선이 무의미해진다.
