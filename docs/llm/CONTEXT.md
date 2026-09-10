# LlmNpcServer — LLM 컨텍스트

> 이 파일은 **첫 메시지에 그대로 붙여 넣는 용도**다. 3,000토큰 안에서 이 저장소를 다루는 데
> 필요한 것을 전부 담는다. 더 깊은 것은 각 절이 가리키는 문서에 있다.

---

## 1. 이것은 무엇이고 무엇이 아닌가

**LLM 을 컴파일러로 쓴다.** LLM 이 NPC 의 **행동 플랜**(JSON)을 만들고, 실행은 결정론 런타임이 한다.
런타임 안에서 LLM 을 부르지 않는다.

- **NPC 서버를 만든다. 게임서버는 만들지 않는다.**
- `IGameServerLink` 는 "게임서버의 인터페이스" 가 아니라 **게임서버로 향하는 아웃바운드 포트**다.
  이름의 "GameServer" 는 상대방을 가리킨다.
- `Npc.Sim` 은 그 상대방 역할을 하는 **우리 쪽 가짜 구현**(게임서버 대역)이며, 이것은 만든다.
- **대화는 만들지 않는다.** 이 서버는 행동 플랜만 만든다.

---

## 2. 절대 규칙 (어기면 리뷰 반려)

**틱 루프**(`Npc.Runtime`, 10Hz · NPC 5,000 · 예산 20ms) 안에서 금지:
`await`(예외: `link.FlushAsync`) · **LLM 호출** · 힙 할당(`new`·LINQ·문자열 보간·클로저) ·
`lock`/`Monitor`/`SemaphoreSlim` · `Dictionary` 순회 · `DateTime.Now`/`Stopwatch`.
로깅은 소스 생성 로거만. NPC 상태는 **SoA**(struct of arrays)다.

**연동 계약 N1~N8**: 명령은 fire-and-forget(반환값 있는 메서드 추가 금지) · 패킷은 전부
`readonly record struct` · **패킷에 `string`·`DateTime` 금지**(강타입 ID·`Tick`) ·
명령에 `CorrelationId` · 이벤트에 `Sequence`(갭 → 경보) · 이벤트 처리는 멱등 · 배치가 기본.
**명령은 유실된다고 가정한다** — 모든 스텝에 `timeout_s`, 응답이 없으면 로컬에서 실패를 합성한다.

**결정론**: `DateTime`/`Stopwatch` → `Tick` · 무시드 `Random` → `(npcId, tick)` 해시 ·
`Guid.NewGuid()` → 순증 `CorrelationId` · `Dictionary` 순회 → 정렬 배열 · 틱 루프는 단일 스레드.
(소켓 경로에서는 리플레이 일치가 성립하지 않는다. 결정론이 필요하면 `--loopback`.)

**마스터데이터**: `masterdata/` 가 단일 원천 — 액션·플래그·아키타입을 코드에 하드코딩하지 않는다.
**`code`·`bit` 번호는 절대 재배치하지 않는다**(프리베이크 플랜이 통째로 깨진다). 추가는 뒤에만.
검증 V1~V13 실패는 **기동 실패**다.

**프롬프트**: 고정 프리픽스는 기동 시 1회 조립 후 불변 · 프리픽스 ≥ 4,096 토큰 ·
가변 서픽스 ≤ 300 토큰 · 재시도 피드백은 **서픽스에만** ·
**플레이어 작성 문자열(캐릭터명·채팅·길드명)을 프롬프트에 넣지 않는다**(인젝션).

**LLM 출력**: 강제 디코딩을 신뢰하지 않고 4단 검증기를 항상 통과시킨다 · 재시도는 **1회만** ·
`PlanStore.Resolve` 는 **절대 null 을 반환하지 않는다** · 플랜 스왑은 스텝 경계에서만.

**비용·라이선스**: 일일 토큰 하드 캡을 우회하지 않는다(초과 시 T2 → T1 강등 → 거절) ·
**dotLLM 은 GPLv3라 별도 프로세스 + HTTP 로만** · LLM 호출은 `IChatClient` 를 통한다.

---

## 3. 마스터데이터 작업 순서

역순이면 계속 되돌아온다.

```
world_flags → items → actions → zones → pois → archetypes
→ context_buckets → interrupts → fallback_plans → prompt(자동생성) → npc_instances(스크립트)
```

---

## 4. 무엇을 하려면 어디를 여는가

`src` 를 통째로 훑지 않는다. 전체 표는 `CODEMAP.md` 다.

| 하려는 일 | 여는 곳 |
|---|---|
| 새 액션 | `masterdata/actions.json` → `ActionCatalog.cs` → `CommandEmitter.cs` |
| 새 월드 플래그 | `masterdata/world_flags.json` (소스 생성기가 enum 을 만든다) |
| 새 아이템·레시피 | `masterdata/items.json` → `ItemTable.cs` → `EventApplier.cs` |
| 새 아키타입·POI·존 | `masterdata/*.json` → `gen_npcs.cs` 재실행 → 프리베이크 |
| 인터럽트 규칙 | `masterdata/interrupts.json` → `InterruptRules.cs` → `InterruptMatcher.cs` |
| 검증 규칙 추가 | `Validation/MasterDataValidator.cs` + **`Validation/FixHints.cs` 에 힌트도** |
| 정의가 무슨 뜻인지 | `npc card archetype <id>` · `src/Npc.Narrative/` |
| 틱 루프 | `src/Npc.Runtime/NpcServerLoop.cs` |
| 플랜 실행·스텝 상태 | `src/Npc.Runtime/PlanExecutor.cs` |
| 인지 스캔·LOD | `CognitionScheduler.cs` · `LodUpdater.cs` |
| 플랜 캐시·히트율 | `src/Npc.Planning/PlanStore.cs` · `CacheMetrics.cs` |
| 재계획 큐·예산 | `ReplanQueue.cs` · `ReplanBudget.cs` |
| 프롬프트 조립 | `src/Npc.Llm/PromptPrefix.cs` · `PlanRequestSuffix.cs` |
| 검증기 4단 | `Npc.Core/Validation/` · 4단은 `Npc.Sim/Validation/DryRunValidator.cs` |
| 링크·와이어 | `src/Npc.Gateway/TcpGameServerLink.cs` · `src/Npc.Wire/` |
| 기동 조립 | `src/Npc.Host/Program.cs` (유일한 조립 자리) |
| 옵션 추가 | `HostOptions.cs` → `Config/HostOptionsSource.cs` 표에도 |
| 메트릭 | `src/Npc.Host/Metrics/NpcMeter.cs` |
| 스냅샷·복구 | `src/Npc.Host/Persistence/` |
| 편집 안전장치 | `src/Npc.MasterData/Authoring/` |

---

## 5. 확인 명령 — "괜찮아 보인다" 라고 말하지 않는다

종료 코드로 답한다.

```powershell
dotnet run --project tools/Npc.Cli -- validate          # V1~V13 + 로더 + 파생물. 0 = 통과
dotnet run --project tools/Npc.Cli -- regen --check     # 파생물 낡으면 비0
dotnet run --project tools/Npc.Cli -- card archetype <id>   # 정의 한 장
dotnet run --project tools/Npc.Cli -- diff              # 변경 → 무효화·재생성·재배포 파급
.\build.ps1                                             # 빌드·스타일·테스트·데이터 전부
dotnet run -c Release --project src/Npc.Host -- --loopback --npcs 500 --days 1 --no-llm
```

플랜 하나: `npc plan validate <파일>` (4단 전부) · `npc plan explain <파일>` (스텝 트레이스).

---

## 6. 손대면 안 되는 것

| 대상 | 왜 |
|---|---|
| `masterdata/prompt/` 카탈로그 | **생성물**이다. `actions.json` 에서 만들어진다 |
| `masterdata/npc_instances.json` | **생성물**이다. `tools/gen_npcs.cs` 가 만든다 |
| `masterdata/poi_distances.bin` | **생성물**이다. `tools/gen_poi_distances.cs` 가 만든다 |
| `docs/schema/*.json` · `docs/llm/VALIDATION.md` | **생성물**이다. `npc schema` · `npc hints --out` |
| `src/Npc.Wire/V1/**` | 와이어 v1 은 **동결**이다. 배포된 게임서버가 읽는다 |
| `planstore/pinned/**` | **사람이 고친 플랜**이다. 지우면 검수 작업이 날아간다 |
| `code`·`bit` 번호 | 재배치하면 프리베이크 플랜 2,880개가 통째로 깨진다 |

---

## 7. 무엇을 바꾸면 무엇이 무효인가

| 바꾼 것 | 플랜 | 비고 |
|---|---|---|
| `world_flags.json` bit 재배치 | **전량** | 하지 말 것 |
| `actions.json` (액션·desc) | **전량** | 프리픽스가 바뀐다 |
| `archetypes.json` | **전량** | 프리픽스 + 구조 해시(게임서버 재배포) |
| `context_buckets.json` | **전량** | 인덱싱이 바뀐다 |
| `prompt/**` 한 글자 | **전량** | 프롬프트 캐시 전면 미적중 |
| `pois.json` · `items.json` | 부분 | 거리표 재생성 필요 |
| `zones.json` | 부분 | 도달 버킷 집합이 바뀐다 |
| `interrupts.json` · `fallback_plans.json` | 없음 | 재기동만 |
| `npc_instances.json` · `poi_distances.bin` | 없음 | 플랜은 개체에 안 묶인다 |

`npc diff` 가 이 표대로 답한다.

---

## 8. 실측 수치 — **추정치를 쓰지 않는다**

어긋난 값은 어긋난 대로 적혀 있다. 전문은 `docs/reference_metrics.html`.

| 항목 | 값 |
|---|---|
| 틱 p99 (NPC 5,000) | **0.801 ms** (예산 20ms) · 틱당 할당 **0 B** |
| 플랜 캐시 히트율 | **98.67 %** (195버킷으로 달성) |
| 요청당 입력 토큰 | **13,948** (프리픽스 11,967) — 계획 가정의 **8배** |
| 프리베이크 2,880건 | **$5.12** (도달집합 264건은 $0.37) |
| 프롬프트 캐시 적중률 | **30.7~42.6 %** — 암시적 캐싱이라 95 % 는 도달 불가 |
| 플랜 생성 통과율 | **64.6 %** · 유니크 시퀀스 37.1 % |

로컬 수치는 전부 VRAM 8GB 에 묶여 있다. **24GB 급 외삽에 그대로 쓰지 않는다.**

---

## 9. 모르면 멈추고 물어본다

임의로 코드에 맞추지 않는다. 다음은 **반드시 사람에게 확인한다**:

- 문서와 코드가 어긋날 때 — 둘 중 하나가 버그다
- `code`·`bit` 번호가 필요할 때 — `npc next-code <파일>` 이 답하지만 **재배치는 사람 결정**
- 프리픽스가 바뀌는 변경 — 플랜 스토어 전량 무효 + 프리베이크 비용
- 프리베이크 실행 — 돈이 든다
- `planstore/pinned/` 수정 — 검수 작업이다
- 계약 타입(`Npc.Contracts`) 변경 — 먼저 `docs/reference_link.html` 을 고치는 PR

---

## 더 볼 것

| 문서 | 무엇 |
|---|---|
| `docs/llm/RECIPES/` | 작업별 절차 (전제 → 순서 → 확인 → 되돌리기 → 파급) |
| `docs/llm/ANTIPATTERNS.md` | 실수 / 증상 / 확인 명령 / 올바른 방법 |
| `docs/llm/GLOSSARY.md` | 용어 |
| `docs/llm/VALIDATION.md` | 검증 코드 → 무엇을 하면 되는가 |
| `docs/reference_link.html` | 게임서버 연동 계약 전문 |
| `docs/reference_masterdata.html` | 마스터데이터 스키마 전문 |
| `CLAUDE.md` · `CODEMAP.md` | 저장소 규칙 · 파일 지도 |
