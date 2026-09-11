# CODEMAP.md — 무엇을 하려면 어디를 여는가

`src` 96 파일 20,914줄 · `testbed` 29 파일 8,318줄이다. **전체를 훑지 않는다.**
아래 표에서 작업에 해당하는 줄을 찾아 거기 적힌 파일만 연다.

관련 규칙은 [`CLAUDE.md`](CLAUDE.md), 계약 전문은
[`docs/reference_link.html`](docs/reference_link.html)·[`docs/reference_masterdata.html`](docs/reference_masterdata.html) 이다.

---

## 0. 읽기 원칙

1. **테스트가 사양이다.** 어떤 파일을 고칠지 정했으면 `tests/Npc.Tests/<같은이름>Tests.cs` 를 먼저 연다.
   무엇을 보장하는지가 거기 다 적혀 있고, 고친 뒤 깨지는 것도 거기다.
2. **파일 이름이 곧 책임이다.** 이 저장소는 한 파일 한 관심사다. `LodUpdater` 는 LOD 등급만,
   `PoiBinder` 는 심볼 바인딩만 한다. 이름으로 못 찾으면 §2 의 추적 경로를 탄다.
3. **계약을 먼저 본다.** 타입 이름이 헷갈리면 `Npc.Contracts`(5파일 472줄)를 통째로 읽는 게 제일 빠르다.

---

## 1. 작업별 지도

### 콘텐츠 · 마스터데이터

| 하려는 일 | 여는 곳 | 주의 |
|---|---|---|
| **새 액션 추가** | `masterdata/actions.json` → `src/Npc.MasterData/ActionCatalog.cs` → `src/Npc.Runtime/CommandEmitter.cs`(`emits.map` 해석) | **40개 상한**(현재 37). `code` 재배치 금지. 프리픽스가 바뀌므로 **플랜 스토어 전량 무효** |
| **새 월드 플래그 추가** | `masterdata/world_flags.json` → `src/Npc.Core/Generators/WorldFlagsGenerator.cs`(소스 생성기가 enum 을 만든다. 손으로 쓰지 않는다) | **bit 재배치 금지.** 44~63 이 비어 있다 |
| **새 아이템·레시피** | `masterdata/items.json` → `src/Npc.MasterData/ItemTable.cs` → `src/Npc.Runtime/EventApplier.cs`(`RecomputeItemFlags`) | `grants` 가 플래그를 만든다. 코드에 하드코딩 금지 |
| **새 아키타입·POI·존** | `masterdata/*.json` → `tools/gen_npcs.cs` 재실행 → 프리베이크 | **코드 수정 없음**(F-05) · `population_weight` 합 1.0(V5) · POI 정원(V10) · 프리픽스 해시 변경 → 플랜 전량 무효 |
| **인터럽트 규칙 추가·수정** | `masterdata/interrupts.json` → `src/Npc.MasterData/InterruptRules.cs`(파싱) → `src/Npc.Runtime/InterruptMatcher.cs`(판정·엣지 트리거) | **LLM 이 만들지 않는다.** `cooldown_s` 를 두지 않는다 — 결정론이 깨진다 |
| **검증 규칙(V1~V15) 추가** | `src/Npc.MasterData/Validation/MasterDataValidator.cs` → **`Validation/FixHints.cs` 에 힌트도 같이** | 실패는 **기동 실패**다. 힌트를 빼먹으면 `FixHintTests` 가 깨진다 |
| **검증 결과를 기계가 읽어야 한다** | `validate --format json` — `src/Npc.Host/Commands/ValidationJson.cs` · 사전 문서는 `hints --out` 이 생성한다 |
| **마스터데이터가 안 읽힌다** | `src/Npc.MasterData/MasterDataLoader.cs` → `MasterDataSet.cs` | `dotnet run --project src/Npc.Host -- validate --masterdata ./masterdata` 로 먼저 재현 |
| **정의가 무엇을 뜻하는지 알고 싶다** | `src/Npc.Narrative/{ArchetypeCard,PlanExplain,InterruptExplain,InstanceCard}.cs` · 껍질은 `npc card`·`npc explain` | 카드의 ✗ 는 3단 검증기와 **같은 판정**이다(`NarrativeTests`). 갈리면 둘 중 하나가 버그다 |
| **터미널에서 뭐든 한다** | `tools/Npc.Cli/` — `validate`·`explain`·`card`·`timeline`·`next-code`·`scaffold`·`diff`·`regen`·`plan`·`buckets`·`pin`·`hints` | **CLI 에 로직을 두지 않는다.** 인자를 읽고 코어를 부른다 — MCP(E-03)·Studio(F-02)가 같은 함수를 부른다 |
| **표기(한국어 이름)를 고친다** | `src/Npc.Narrative/Lexicon.cs` | 표시 계층이다. 행동을 정하는 값은 여전히 `masterdata/` 가 원천 |
| **다음 `code`·`bit` 를 알아야 한다** | `src/Npc.MasterData/Authoring/CodeAllocator.cs` | 비트는 **예약 구간을 먼저 채운다**. 재배치 API 는 없다 |
| **가중치를 재배분한다** | `src/Npc.MasterData/Authoring/WeightRebalancer.cs` | 3안을 내고 **고르는 것은 사람**이다. 반올림 잔차까지 맞춰 V5 를 지킨다 |
| **JSON 을 서식 보존으로 고친다** | `src/Npc.MasterData/Authoring/JsonSurgeon.cs` | 무변경 편집은 **바이트 동일**이어야 한다 |
| **파생물이 낡았는지 본다** | `src/Npc.MasterData/Authoring/DerivedArtifacts.cs` · `masterdata/derived.lock.json` | 기록이 없으면 **낡은 것**이다. 생성기가 쓰고 로더가 경고한다 |
| **스키마가 필요하다 (LLM·에디터)** | `npc schema` → `docs/schema/*.schema.json` · 생성기는 `src/Npc.MasterData/Schema/SchemaCatalog.cs` | **생성물이다.** 허용 값은 지금 마스터데이터에서 나온다 — `*.base.schema.json` 은 값 없는 정적 변형 |
| **무엇을 다시 해야 하는지 본다** | `src/Npc.MasterData/Authoring/ImpactAnalyzer.cs` | `PlanStoreValidator` 와 **같은 판정**이어야 한다(테스트가 강제) |

### 런타임 · 틱 루프

| 하려는 일 | 여는 곳 | 주의 |
|---|---|---|
| **틱 루프 자체** | `src/Npc.Runtime/NpcServerLoop.cs` (213줄. 여기가 심장이다) | `await` 는 `FlushAsync` 하나뿐 |
| **NPC 상태를 하나 더 들고 싶다** | `src/Npc.Runtime/NpcStore.cs` (SoA 배열) | 핫 배열은 합쳐 ~100KB. `class Npc` 를 만들지 않는다 |
| **틱 p99 초과 · 할당이 생겼다** | `tests/Npc.Tests/Runtime/TickAllocationTests.cs` 로 먼저 재현 → `NpcServerLoop`·`CognitionScheduler`·`PlanExecutor` 순으로 본다 | 계기는 `/metrics` 의 `bytesPerTick` 하나다 |
| **플랜 스텝이 안 넘어간다** | `src/Npc.Runtime/PlanExecutor.cs` → `CorrelationTable.cs`(상관 ID) → `EventApplier.cs`(`CompleteStep`/`FailStep`) | 타임아웃 합성은 `PlanExecutor` 안에 있다 |
| **NPC 가 이상한 곳으로 간다** | `src/Npc.Runtime/PoiBinder.cs`(`$home`·`$workplace`·`$nearest_*`·`$patrol_route` 해석) → `CommandEmitter.cs` | POI code 불일치면 마스터데이터 해시부터 의심 |
| **NPC 마다 다른 순찰로·세력·대화 성격을 준다** | `masterdata/npc_overrides.json`(사람 편집) + `masterdata/factions.json` → 병합은 `src/Npc.MasterData/MasterDataSet.cs` 의 `NpcInstanceTable.ApplyOverrides` → `NpcStore.{PatrolRoute,Faction,AggroRadiusM,ScheduleOffsetMin}` → `EventApplier.Seed(npc, def)` | **`npc_instances.json` 에 손편집하지 않는다** — 생성물이라 재생성에 사라진다. 순찰 지점은 같은 존이어야 한다 (V13) |
| **인지 스캔 · 누가 재계획 대상인가** | `src/Npc.Runtime/CognitionScheduler.cs` → `LodBand.cs`(밴드 멤버십) → `LodUpdater.cs`(등급) | 상한 `MaxScansPerTick`(150)이 O(1) 을 만든다 |
| **플레이어 근접 반응** | `src/Npc.Runtime/EventApplier.cs`(`ApplyProximity`) → `LodUpdater.cs` → `src/Npc.Planning/ReplanScorer.cs`(W1) | 거리 원값은 저장하지 않는다. 등급으로만 남는다 |
| **시간대·존 상태가 바뀔 때 대량 플랜 교체** | `src/Npc.Runtime/BucketTransition.cs` → `GameClock.cs` · `ZoneStateTable.cs` | 한 틱에 다 갈면 그 자체가 스파이크다 |
| **플랜 교체 타이밍** | `src/Npc.Runtime/PlanSwapper.cs` → `src/Npc.Planning/ReplanHandoff.cs` | 스텝 경계에서만. 인터럽트만 예외 |

### 게임서버 연동

| 하려는 일 | 여는 곳 | 주의 |
|---|---|---|
| **계약 버전 · 와이어 협상 · 기능 비트** | `src/Npc.Contracts/ContractVersion.cs` → `src/Npc.Wire/V2/VersionNegotiation.cs` · v2 핸드셰이크는 `src/Npc.Wire/V2/LinkMessagesV2.cs` · **`V1/` 은 수정하지 않는다** |
| **패킷에 필드 추가 · 새 명령/이벤트 종류** | `src/Npc.Contracts/NpcCommand.cs`·`GameEvent.cs` → **반드시** `src/Npc.Wire/V2/WireCommandV2.cs`·`WireEventV2.cs` 도 같이 | `Wire_MirrorsContractMembers`·`Wire_LayoutIsFrozen_V2` 가 먼저 깨진다. **v1(`V1/`)은 동결 — 56B·64B.** v2 는 72B·80B |
| **남의 게임서버가 규약을 지키는지 본다** | `tools/Npc.Conformance/` — 검사는 `Checks/*.cs`(순수 함수) · 소켓은 `Observer.cs` · 보고서는 `Report.cs` | **검사와 관찰을 갈랐다** — 그래야 고의 위반을 합성해 검사를 시험할 수 있다. 대역 보고서는 `docs/measurements/conformance_testbed.md` |
| **다른 언어(C++·파이썬)로 게임서버를 짠다** | `docs/wire/layout_v2.md`(오프셋 표·생성물) · `docs/wire/vectors_v2/`(골든 바이트) · `docs/wire/reference/npc_wire.h`·`npc_wire.py` | **표를 손으로 고치지 않는다** — `LayoutDocTests` 가 다시 뽑는다. 참조 코덱이 표와 어긋나면 `WireReferenceTests` 가 깨진다 |
| **와이어 바이트를 직접 쓴다·읽는다** | `src/Npc.Wire/V2/WireWriter.cs` | v2 는 명시 리틀엔디언이다. 바이트는 원시 복사와 같아야 한다(`WireWriter_MatchesMemoryPackBytes`) |
| **프롬프트를 고쳤다 · 되돌린다** | `masterdata/prompt/prompt_manifest.json`(라벨) · `src/Npc.Planning/PlanStoreLayout.cs`(SHA 별 폴더) · `planstore/prefix/<sha8>.md`(전문) | **되돌리기 = 프롬프트 파일을 되돌리는 것.** SHA 가 이전 값이 되면 그 폴더가 자동 선택된다 — 다시 굽지 않는다 |
| **적대 플레이어를 선제공격하게 한다** | `masterdata/interrupts.json`(`attack_hostile_player`) · `world_flags.json`(bit 44) · `NpcStore.HostilePlayer` · `NpcRefKind.HostilePlayer` · `EventApplier.ApplyHostility` | **적대 판정은 게임서버가 한다** — 우리는 `PlayerHostility` 를 받을 뿐이다. 대상은 플랜이 아니라 런타임 상태에서 온다 |
| **런타임에 NPC 를 넣고 뺀다** | `src/Npc.Runtime/DynamicRoster.cs` · `NpcStore.Occupant`(슬롯 거주자) · `EventApplier` 스폰/디스폰 · `--dynamic-roster`·`--npc-capacity` | **양쪽이 같이 켜야 붙는다** — 로스터 해시가 인스턴스 테이블 전체의 것으로 바뀐다. 슬롯 배정은 게임서버가 한다 |
| **채널·인스턴스 던전 · 세력 · 예약 슬롯** | `src/Npc.Contracts/ExtensionSlots.cs`(의미 등록부) · `NpcStore.Instance` · `docs/reference_link.html` §05 | **등록되지 않은 예약 슬롯은 0 이다.** 쓰려면 등록부에 줄을 넣고 문서를 같은 커밋에서 고친다 |
| **실제 게임서버에 붙인다** | `docs/reference_link.html` 를 상대 팀에 전달 → `src/Npc.Gateway/TcpGameServerLink.cs`(661줄) | 이기종 런타임이면 `Npc.Wire` 한 곳만 고친다 |
| **링크 인증·암호화** | `src/Npc.Wire/V2/LinkAuth.cs`(HMAC·nonce) · `src/Npc.Gateway/TlsStreamFactory.cs`(TLS/mTLS) · 관리 API 는 `src/Npc.Host/Api/AdminAuth.cs` |
| **소켓이 끊긴다 · 재접속** | `src/Npc.Gateway/TcpGameServerLink.cs` → `TcpLinkOptions.cs` → `src/Npc.Wire/LinkMessages.cs`(핸드셰이크) | 시퀀스를 리셋하면 NPC 가 영원히 멈춘다 |
| **명령이 드롭된다 · 역압** | `src/Npc.Gateway/PriorityCommandRing.cs` | `Critical` 은 무손실. 예외를 던지지 않는다 |
| **링크를 갈아 끼운다** | `src/Npc.Gateway/` 6파일 중 하나 + `src/Npc.Host/Program.cs` 배선 | 런타임 3프로젝트에 diff 가 생기면 잘못 짠 것이다 |
| **프레임이 깨진다** | `src/Npc.Wire/FrameCodec.cs` (98줄) | 헤더 8바이트 · 1MiB 상한 |

### 플랜 생성 (LLM)

| 하려는 일 | 여는 곳 | 주의 |
|---|---|---|
| **검증 통과율을 올린다** | `src/Npc.Llm/CatalogRenderer.cs`(프리픽스 본문) → `masterdata/prompt/system_rules.md` → `fewshot/*.json` | 실패는 `V3.PRECONDITION_UNMET` 에 몰린다. 프리픽스를 바꾸면 **플랜 스토어 전량 무효** |
| **프롬프트 서픽스에 값 추가** | `src/Npc.Llm/PlanRequestSuffix.cs` | **300 토큰 상한.** `SuffixBudgetTests` 가 막고, 문자열은 `PromptIsolationTests` 가 막는다 |
| **`reasoning` 이 이상한 것을 담고 있다** | `src/Npc.Llm/ReasoningSanitizer.cs` · 금칙어는 `masterdata/prompt/blocklist.txt` | 플랜 자체는 건드리지 않는다 |
| **프리픽스가 안 고정된다 · 캐시 미적중** | `src/Npc.Llm/PromptPrefix.cs` | SHA 유니크 2개 이상이면 경보. 기동 시 1회 조립 |
| **개체 파라미터를 하나 더 만든다** | `masterdata/npc_overrides.json` 스키마 → `NpcInstanceDef` → `NpcInstanceTable.Merge` → `NpcStore` 콜드 배열 → `EventApplier.Seed` → 읽는 쪽 | **서픽스에 실리는가 먼저 본다** (CLAUDE.md §8). 바인딩 전용이면 300토큰 예산과 무관하다 |
| **대사 주제를 추가한다 · 문구를 번역한다** | `masterdata/dialogue_lines.json`(code 재배치 금지) · `masterdata/localization/<locale>.json` · 로더는 `src/Npc.MasterData/{DialogueTable,LocalizationTable}.cs` — **V14 가 기동을 막는 것은 대사 심볼 누락뿐**이고, 문구 누락은 표시 계층이라 경고다 |
| **검증 실패를 기계로 고친다** | `src/Npc.Core/Validation/PlanRepair.cs`(C-05) — 붙는 곳은 `src/Npc.Llm/LlmPlanCompiler.cs` 의 `Validate`. **수선은 검증을 건너뛰는 것이 아니라 재검증 전의 변환이다** — 고친 문서는 1단부터 다시 지나고, 통과하면 `PlanOrigin.Repaired` 로 표시된다 |
| **검증기를 고친다** | `src/Npc.Core/Validation/` — `SchemaValidator`(V1) → `VocabularyValidator`(V2) → `CoherenceValidator`(V3) → `src/Npc.Sim/Validation/DryRunValidator.cs`(V4) | 4단은 순서대로다. 건너뛴 플랜을 런타임에 올리지 않는다 |
| **LLM 제공사 교체·추가** | `src/Npc.Llm/ChatClientFactory.cs` → `appsettings.Llm.json` | `IChatClient` 밖에서 제공사 SDK 를 부르지 않는다 |
| **제공사 하나가 죽었다** | `src/Npc.Llm/FailoverChatClient.cs` · 체인은 `appsettings.Llm.json` 의 `chains` | 전송 실패만 넘어간다. 400·스키마 거절은 페일오버하지 않는다 |
| **벽시계 기준으로 돈이 새고 있다** | `src/Npc.Host/Replan/BillingGuard.cs` (`--billing-cap-usd`) | `ReplanBudget` 의 틱 기준 하루와 별개다. 해제는 `/admin/killswitch` 로 손으로 |
| **티어 강등 · 실패 시 폴백** | `src/Npc.Llm/TieredPlanCompiler.cs` → `CircuitBreaker.cs` → `src/Npc.Host/Replan/TierWiring.cs` | 재시도는 **1회만** |
| **JSON 스키마 강제** | `src/Npc.Llm/SchemaProvider.cs` | 강제 디코딩을 신뢰하지 않는다. 4단 검증기를 항상 통과시킨다 |

### 플랜 캐시 · 재계획

| 하려는 일 | 여는 곳 | 주의 |
|---|---|---|
| **캐시 히트율이 낮다** | `src/Npc.Planning/PlanStore.cs` → `CacheMetrics.cs` → `BucketNeighbors.cs`(인접 재사용) | 개수가 아니라 **어느 버킷이냐**가 히트율을 정한다 |
| **`Resolve` 가 null 을 준다** | `src/Npc.Planning/PlanStore.cs` | **절대 null 을 반환하지 않는다.** 미스여도 폴백을 준다 |
| **프리베이크** | `tools/Npc.Prebake/` → `src/Npc.Planning/Manifest.cs`·`PlanStoreIo.cs` | `--concurrency` 기본 8. 429 를 먼저 재고 올린다 |
| **재계획 우선순위** | `src/Npc.Planning/ReplanQueue.cs` → `ReplanScorer.cs` | 같은 NPC 중복 삽입 금지(`_heapPos`). 지금은 **예산이 병목**이라 가중치 효과가 작다 |
| **일일 토큰 캡 · 예산** | `src/Npc.Planning/ReplanBudget.cs` → `TokenBucket.cs` · 개체 몫은 `src/Npc.Core/Planning/ReplanAccount.cs` | 캡 우회 코드를 만들지 않는다. **나누기만 한다** |
| **워커가 플랜을 못 넘긴다** | `src/Npc.Host/Replan/ReplanWorker.cs` → `src/Npc.Planning/ReplanHandoff.cs` | 워커는 `Volatile.Write` 만. 틱 루프 상태를 직접 고치지 않는다 |
| **마스터데이터를 고쳤는데 플랜이 안 맞는다** | `src/Npc.Planning/PlanStoreValidator.cs` | `ContentHash` 가 바뀌면 전량 무효 |

### 호스트 · 운영

| 하려는 일 | 여는 곳 |
|---|---|
| **기동 순서 · 무엇이 어디에 꽂히나** | `src/Npc.Host/Program.cs` (905줄 — **조립의 유일한 자리**) · 그림은 `docs/startup_flow.html` |
| **CLI 옵션 추가** | `src/Npc.Host/HostOptions.cs` → `src/Npc.Host/Config/HostOptionsSource.cs` 의 옵션 표에도 한 줄 (환경변수·설정 파일이 그것을 본다) → `tests/Npc.Tests/Host/HostOptionsTests.cs` |
| **환경변수·설정 파일로 옵션을 주고 싶다** | `src/Npc.Host/Config/HostOptionsSource.cs` (CLI > 환경변수 > 파일) · 탐색 기준은 `Config/ConfigPaths.cs` |
| **메트릭 · `/metrics` 대시보드** | `src/Npc.Host/Metrics/NpcMeter.cs` · 이름 사전은 `docs/reference_metrics.html` §13.5 |
| **경보를 어디로 보내나** | `src/Npc.Host/Observability/Alarms.cs`(싱크·쿨다운·웹훅) · 예산 임계는 `Observability/BudgetAlarmBridge.cs` |
| **Prometheus·OTLP·구조화 로그** | `src/Npc.Host/Observability/Telemetry.cs` |
| **틱 루프에 금지된 API 를 썼다** | `src/Npc.Runtime/BannedSymbols.txt` — 빌드가 RS0030 으로 막는다 |
| **API 명세가 필요하다 (툴·에이전트)** | `src/Npc.Host/Api/OpenApiCatalog.cs` → `docs/openapi.json` · 살아 있는 서버는 `GET /openapi/v1.json` | **생성물이다.** 라우트를 더하면 `Routes` 에 한 줄 — `OpenApiTests` 가 대조한다 |
| **여러 NPC 를 한 번에 본다 · 검색한다** | `src/Npc.Host/Api/QueryEndpoints.cs`(`/npcs`·`/npc/{id}/context`·`/buckets`) · `QueryStream.cs`(SSE) | **도구·대화·운영 전용이다.** 런타임 게임 로직이 의존하면 링크를 우회한 동기 호출이 된다 |
| **NPC 하나를 추적하고 싶다** | `src/Npc.Host/Api/NpcTraceEndpoint.cs` |
| **로컬 추론이 죽었는데 아무도 모른다** | `src/Npc.Llm/LocalEngineProbe.cs`(C-08) · 라우터 우회는 `TieredPlanCompiler.LocalHealthy` · 사이드카는 `deploy/compose.yaml --profile local` · GPU 는 `dcgm-exporter`(Prometheus `gpu` 잡). **NPC 서버는 프로세스를 되살리지 않는다** — 그것은 오케스트레이터의 일이고, 여기서는 30초 안에 알고 T1 을 T2 로 흘린다 |
| **모델·프롬프트를 바꾸고 품질·비용을 잰다** | `tools/Npc.Eval`(러너·심사원) · `tools/Npc.Eval.Core`(다양성·실패 집계·게이트·보고서) — **게이트 기준은 `EvalGate` 한 곳에만 있다.** 골든 러너는 여기 없다: `dotnet test --filter Category=Golden` 의 결과를 `--golden-rate` 로 넣는다 |
| **컨테이너·릴리스** | `deploy/` (Dockerfile · compose · k8s · Grafana) · 버전은 `src/Npc.Host/HostVersion.cs` · 패키지 버전은 `Directory.Packages.props` 한 곳 |
| **CI 파이프라인을 짠다** | 워크플로 파일을 두지 않는다 — `build.ps1` · `npc validate` · `npc regen --check` 세 명령이 파이프라인의 내용이다 |
| **NPC id 가 슬롯인가 인스턴스 id 인가** | 와이어는 **전역 인스턴스 id** 다 (A-08) — `src/Npc.Core/GlobalIdMap.cs`(역방향 표) · `src/Npc.Runtime/NpcStore.cs`(`Bind`·`SlotOf`·`GlobalOf`) · 경계는 `EventApplier.Apply` 와 `PlanExecutor.ContextOf` 두 곳뿐이다 |
| **월드를 여러 프로세스로 쪼갠다 (샤딩)** | `deploy/shards.json` · `src/Npc.MasterData/ShardTable.cs`(V15 검증) · `--shard N` · POI 제한은 `src/Npc.Runtime/PoiBinder.cs`(`ZoneMask`) · 핸드셰이크 검증은 `src/Npc.Gateway/TcpGameServerLink.cs`(`ShardMismatch`) |
| **재기동하면 게임 시각이 새벽 6시로 돌아간다** | `src/Npc.Runtime/GameClock.cs`(`RequestOrigin`·`TryApplyPendingOrigin`) — 값은 핸드셰이크가 싣는다 · 정지 감시는 `src/Npc.Host/TickSyncWatchdog.cs` |
| **종료가 지저분하다 · SIGTERM 을 안 받는다** | `src/Npc.Host/HostShutdown.cs` (신호 등록 · 6단계 시퀀스) · `Bye` 송신은 `src/Npc.Gateway/TcpGameServerLink.cs`(`SendByeAsync`) |
| **상태를 저장·복구한다** | `src/Npc.Host/Persistence/` — `SnapshotFile`(형식·CRC) · `SnapshotWriter`(주기 쓰기) · `SnapshotRestorer`(조건 판정) · 틱 루프 쪽 통로는 `src/Npc.Runtime/NpcStoreSnapshot.cs` |
| **헬스체크 · 죽었는지 살았는지** | `src/Npc.Host/Api/HealthEndpoints.cs` (`/healthz/live`·`ready`·`startup`) · 루프 하트비트는 `src/Npc.Runtime/ILoopProbe.cs` |
| **링크가 `Faulted` 인데 프로세스가 안 죽는다** | `src/Npc.Host/LinkFaultPolicy.cs` (`--on-link-fault`) |
| **운영 제어 · 감사 로그** | `src/Npc.Host/Api/AdminEndpoints.cs`(`/admin/killswitch`·`/admin/snapshot`·`/admin/reload`) · `Api/AuditLog.cs` — 시각은 게임 틱이다 |
| **실행 중에 플랜을 바꾼다 (재기동 없이)** | `src/Npc.Host/Reload/ReloadService.cs`(트랜잭션 교체) · `ReloadWatcher.cs`(`--watch`, 개발용) · 스토어 쪽 통로는 `src/Npc.Planning/PlanStore.cs`(`Adopt`) · 등급 표는 `docs/reference_masterdata.html` §13 |
| **시크릿을 돌린다 · 유출됐다** | `docs/security/secrets.md` — 읽는 곳은 `src/Npc.Host/Program.cs`(링크 비밀) · `src/Npc.Host/Api/AdminAuth.cs`(관리 토큰) · `src/Npc.Llm/ChatClientFactory.cs`(엔진 키) |
| **보안 위협·잔여 위험을 본다** | `docs/security/threat_model.md` (T1~T15) · 개인정보는 `docs/security/privacy.md` · SBOM 은 `tools/sbom.ps1` |
| **라이선스가 걸린다 (dotLLM·모델 약관)** | `docs/legal/dotllm.md` · `docs/legal/models.md` — **법무 확인은 미실시**다. 지금은 별도 프로세스 + HTTP 경계가 유일한 조치 |
| **시나리오 주입 · 킬스위치** | `src/Npc.Sim/ScenarioRunner.cs` · `src/Npc.Core/KillSwitch.cs` · `src/Npc.Host/KillSwitchSchedule.cs` · `scenarios/*.jsonl` |

### 시뮬 · 테스트 베드

| 하려는 일 | 여는 곳 |
|---|---|
| **게임서버 대역의 동작을 바꾼다** | `src/Npc.Sim/` — `MovementSim`(이동) · `InteractionSim`(상호작용) · `NeedsSim`(허기·피로) · `TransformEmitter`(위치 발행) · `SimWorld` |
| **명령 유실·지연 주입** | `src/Npc.Sim/FaultInjector.cs` (`--drop-rate`) |
| **소켓 게임서버 · 데모** | `testbed/Npc.TestGameServer/GameServer.cs` → `World/PlayerRegistry.cs`(근접 판정) → `Link/LinkSession.cs` |
| **뷰어 화면** | `testbed/Npc.TestClient/MainForm.cs` → `Render/MapRenderer.cs` · `Panels/` |
| **데모를 띄운다** | `testbed/README.md` · 안내는 `docs/testbed_guide.html` |

### 결정론

| 하려는 일 | 여는 곳 | 주의 |
|---|---|---|
| **리플레이가 안 맞는다** | `tests/Npc.Tests/Determinism/ReplayTests.cs` 로 재현 → `ClockAuditTests`·`RandomAuditTests` 가 위반 지점을 짚어 준다 | 소켓 경로에서는 원래 안 맞는다. `--loopback` 으로 확인 |
| **기록·재생** | `src/Npc.Gateway/RecordingGameServerLink.cs` · `ReplayGameServerLink.cs` | |

---

## 2. 추적 경로 — 이름으로 못 찾을 때

**명령 하나가 나가는 길**

```
PlanExecutor.Step
  → CommandEmitter.Emit        actions.json 의 emits.map 을 명령 필드로
  → PoiBinder                  $home · $workplace · $nearest_* 해석
  → CorrelationTable           상관 ID 발급
  → IGameServerLink.Enqueue → PriorityCommandRing → FlushAsync
  → TcpGameServerLink 센더 → WireCommand(v1) 또는 WireCommandV2(v2) → FrameCodec → 소켓
```

**이벤트 하나가 들어오는 길**

```
소켓 → FrameCodec → WireEvent → GameEvent → link.Events 채널
  → NpcServerLoop            배수
  → EventApplier.Apply       NpcStore 의 Flags · Pos · StepStatus 갱신
      ├→ LodUpdater          PlayerProximity → LOD 등급
      └→ InterruptMatcher    즉시 액션 + ReplanQueue 삽입
```

**플랜 하나가 만들어지는 길**

```
CognitionScheduler.Scan        이탈 판정 → ReplanQueue (ReplanScorer 점수)
  → ReplanWorker               별도 BackgroundService. 틱 루프 밖이다
  → BucketReplanSource / IndividualReplanSource
  → TieredPlanCompiler         T2(외부) → T1(로컬) → 폴백
  → LlmPlanCompiler            PromptPrefix + PlanRequestSuffix + SchemaProvider
  → 4단 검증                   Schema → Vocabulary → Coherence → DryRun
  → CompiledPlan → PlanStore → ReplanHandoff → PlanSwapper (스텝 경계)
```

---

## 3. 프로젝트 한 줄 요약

| 프로젝트 | 무엇 | 진입 파일 |
|---|---|---|
| `Npc.Contracts` | 게임서버 경계. 472줄뿐이니 통째로 읽어도 된다 | `IGameServerLink.cs` |
| `Npc.Core` | 순수 로직 — 플랜 표현·검증기 1~3단·버킷 키(크기는 `BucketSpace` 가 안다) | `Plan/CompiledPlan.cs` |
| `Npc.MasterData` | JSON 로딩·인덱싱·검증 V1~V13 · `Authoring/`(편집 안전장치) | `MasterDataSet.cs` |
| `Npc.Runtime` | **틱 루프.** 여기의 규칙이 제일 엄하다 | `NpcServerLoop.cs` |
| `Npc.Planning` | 플랜 캐시·재계획 큐·예산 | `PlanStore.cs` |
| `Npc.Narrative` | **정의 설명 카드** — 아키타입·플랜·인터럽트·인스턴스 → 한국어 markdown. LLM·시각·난수 없음 | `ArchetypeCard.cs` |
| `Npc.Llm` | 프롬프트 조립·컴파일·티어링 | `TieredPlanCompiler.cs` |
| `Npc.Wire` | 소켓 위의 표현 (MemoryPack) | `V1/WireCommand.cs`(동결) · `V2/WireCommandV2.cs` |
| `Npc.Gateway` | 링크 구현 5종 (Loopback·Null·Recording·Replay·Tcp) | `TcpGameServerLink.cs` |
| `Npc.Sim` | 게임서버 대역 (인프로세스) | `SimWorld.cs` |
| `Npc.Host` | 조립·CLI·메트릭 | `Program.cs` |
| `testbed/` | 소켓 게임서버 + 뷰어. **아무도 참조하지 않는 잎** | `Npc.TestGameServer/GameServer.cs` |

### 도구 — 전부 잎이다

| 도구 | 무엇 | 진입 파일 |
|---|---|---|
| `tools/Npc.Cli` | **`npc` 명령.** 검증·설명·편집·플랜. 로직은 코어에 있고 여기는 껍질이다 | `Program.cs` |
| `tools/Npc.Prebake` | 플랜 대량 생성 · 매니페스트 | `Program.cs` |
| `tools/Npc.Narrate` | 명령 기록 → 하루 일지 | `Program.cs` |
| `tools/gen_*.cs` | 파생물 생성기. `#:project` 로 `Npc.MasterData` 를 참조해 잠금을 갱신한다 | — |

---

## 4. 안 봐도 되는 것

- `tools/*.cs` — 일회성 측정·생성 스크립트다. 그 도구를 고칠 때만 연다.
- `testbed/Npc.TestClient/` — 뷰어 UI. 서버 동작과 무관하다.
- `src/Npc.Core/Generators/` — 소스 생성기. `world_flags.json` 을 바꿀 때만.
- `docs/book/` — 사람이 읽는 해설서. 코드를 고칠 근거는 아니다.
