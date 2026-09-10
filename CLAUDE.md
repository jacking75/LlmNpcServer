# CLAUDE.md

이 저장소에서 작업할 때 반드시 따를 규칙. **읽고 시작한다.**

---

## 0. 이 저장소의 성격

**구현이 끝난 제품이다.** 주차별 작업 지시서와 단계별 설계 사양은 구현 완료로 전부 삭제했다
(2026-08-06). 남아 있는 문서는 **레퍼런스**이지 작업 지시가 아니다.

- **`masterdata/` 와 코드가 사실의 출처다.** 문서는 그것을 설명한다.
- 문서와 코드가 어긋나면 **둘 중 하나가 버그다.** 임의로 코드에 맞추지 말고, 어느 쪽이 맞는지
  물어본 뒤 **같은 커밋에서 문서도 고친다.**
- 언어: 코드 주석·커밋 메시지·문서는 **한국어**. 식별자·타입명·로그 메시지는 영어.

### 문서 지도

| 문서 | 무엇 |
|---|---|
| **`CODEMAP.md`** | **무엇을 하려면 어디를 여는가 — 작업별 파일 지도** ★ |
| `LLM_NPC_Server_Plan.md` | 왜 이 구조인가 (판단 근거) |
| `README.md` | 저장소 개요 · 빌드 · 실행 |
| **`docs/reference_link.html`** | **게임서버 연동 계약 — N1~N8, 패킷, 와이어, 발행 규약** ★ |
| **`docs/reference_masterdata.html`** | **마스터데이터 스키마 — 플래그·액션·아키타입·버킷·검증** ★ |
| `docs/reference_metrics.html` | 실측 데이터 — 성능·비용·품질·수용 기준 판정 |
| `docs/index.html` | 프로젝트 안내서 (허브) |
| `docs/book/` | 코드 이해와 활용 안내서 (13장) |
| `docs/tutorial/` | **활용 실습서 — 손으로 만들며 배우기 (6부 21장).** 예제는 `samples/` 에 실물로 있다 |
| `docs/startup_flow.html` | 기동 흐름 |
| `docs/testbed_guide.html` | 테스트 베드 · 게임서버 연동 시험 |
| `docs/FAQ.html` | 도입·행동 플랜·전투 반응·대화 확장 |
| **`docs/llm/`** | **LLM 온보딩 팩 — `SKILL.md` · `CONTEXT.md`(3,000토큰 압축) · `RECIPES/`(작업별 절차 11) · `ANTIPATTERNS.md` · `GLOSSARY.md` · `PROMPTS.md` · `VALIDATION.md`(생성물)** |
| `docs/security/` | 위협 모델 · 시크릿(목록·회전·유출 대응) · 개인정보. **"미실시" 표시를 지우지 않는다** |
| `docs/legal/` | dotLLM GPLv3 배포 경계 · 모델 약관. **법무 확인은 미실시**다 |
| **`PRODUCTION_ROADMAP.md`** | **상용 투입 로드맵 — 결손 태스크 50건(체크리스트)·구현 방법·LLM 온보딩·NPC 정의 툴. 유일한 작업 지시서** |

★ **`reference_link.html` · `reference_masterdata.html` 을 읽지 않고 계약·마스터데이터를 건드리지
않는다.** 타입 이름·ID 체계·스키마가 전부 여기 있다.

> 삭제된 사양 문서(`docs/00`·`01`·`02`·`03`·`10`~`15`·`20`, `TASKS.md`, `docs/measurements/*.md`)의
> 원문이 필요하면 `git log --diff-filter=D -- docs/ TASKS.md` 에서 꺼낸다.
> **코드 주석에 남아 있는 `docs/NN §M` 참조는 그 시점의 근거를 가리키는 이력이다** — 지금은
> 위 레퍼런스 HTML 이 대응한다. 새 주석에는 쓰지 않는다.

### 작업 방식

- **변경 하나 = 커밋 하나.** 여러 관심사를 한 커밋에 묶지 않는다
- 사양에 없는 구조를 새로 도입하지 않는다. 필요하면 **먼저 레퍼런스 문서에 추가**한다
- 근거가 불충분하거나 문서와 코드가 어긋나면 임의로 코드에 맞추지 말고 **멈추고 보고**한다

---

## 1. 빌드 · 테스트

```powershell
dotnet build -c Release
dotnet test                                    # 전체
dotnet test --filter Category=Golden           # 골든 회귀 (LLM 호출 있음, 느림)
dotnet test --filter Category=Gate             # 실측 산출물이 있어야 판정되는 게이트 항목
dotnet test --filter Category=Load             # 부하 (수 분. 측정 CSV 를 덮어쓴다)
dotnet test "--filter Category!=Golden&Category!=Gate&Category!=Load"   # CI 기본 (= .\build.ps1)
dotnet format --verify-no-changes              # 스타일 검사
```

```powershell
# 마스터데이터·플랜 도구 (F-01). 저장소 루트에서 돈다
dotnet run --project tools/Npc.Cli -- validate                 # V1~V13 + 로더 + 파생물 신선도
dotnet run --project tools/Npc.Cli -- card archetype blacksmith
dotnet run --project tools/Npc.Cli -- next-code flags          # 다음 bit (예약 구간부터)
dotnet run --project tools/Npc.Cli -- regen --check            # 파생물이 낡았으면 비0
dotnet run --project tools/Npc.Cli -- --help                   # 명령 전체

# 마스터데이터 검증만 (V1~V13). 기동 스크립트 호환으로 남겨 둔 경로다
dotnet run --project src/Npc.Host -- validate --masterdata ./masterdata

# LLM 없이 스모크
dotnet run -c Release --project src/Npc.Host -- --loopback --npcs 500 --time-scale 600 --days 1 --no-llm

# 부하
dotnet run -c Release --project src/Npc.Host -- --loopback --npcs 5000 --time-scale 60 --days 7
```

`TreatWarningsAsErrors=true`다. 경고를 억제(`#pragma warning disable`)하지 말고 고친다.

---

## 2. 절대 규칙 (위반 시 리뷰 반려)

### 2.1 틱 루프 — `Npc.Runtime`

10Hz, 예산 20ms, NPC 5,000. 이 루프 안에서:

| 금지 | 이유 |
|---|---|
| `await` (예외: `link.FlushAsync` 하나) | 틱이 밀린다 |
| **LLM 호출** | 재계획은 별도 `BackgroundService`. 결과는 `Volatile.Write`로 전달 |
| 힙 할당 (`new`, LINQ, 문자열 보간, 클로저) | Gen0 GC = 0 이 목표 |
| `lock` / `Monitor` / `SemaphoreSlim` | 공유 상태는 `Volatile` 읽기/쓰기 + 원자 참조 교체만 |
| `Dictionary` 순회 | 순서 비결정성 |
| `DateTime.Now` / `Stopwatch` | §2.3 |

로깅은 소스 생성 로거만 쓴다.

```csharp
[LoggerMessage(Level = LogLevel.Debug, Message = "npc {Npc} step {Step} failed: {Reason}")]
static partial void LogStepFailed(ILogger l, int npc, int step, ActionFailReason reason);
```

NPC 상태는 **SoA(struct of arrays)**다. `class Npc`를 5,000개 만들지 않는다. `NpcStore`의 핫 배열(`Flags`, `PlanId`, `StepIndex`, `StepStatus`, `Lod`)은 합쳐 ~100KB로 L2에 들어가야 한다.

### 2.2 게임서버 연동 계약 — `Npc.Contracts` (N1~N8)

> **관점 주의.** 우리가 만드는 것은 **NPC 서버**다. 게임서버는 만들지 않는다.
> `IGameServerLink`는 "게임서버의 인터페이스"가 아니라 **"게임서버로 향하는 NPC 서버의 아웃바운드 포트"** 다. 이름의 "GameServer"는 상대방을 가리킨다.
> `Npc.Sim`은 그 상대방 역할을 하는 **우리 쪽 가짜 구현**(게임서버 대역)이며, 이것은 만든다.

전문은 `docs/reference_link.html` 이다. 테스트로 강제되며, 어기면 빌드가 아니라 테스트가 깨진다.

| # | 규칙 |
|---|---|
| N1 | 명령은 fire-and-forget. **반환값 있는 메서드를 `IGameServerLink`에 추가하지 않는다.** 결과는 이벤트로만 |
| N2 | 모든 패킷은 `readonly record struct`. 참조 필드·가변 컬렉션·`object` 금지 |
| N3 | **패킷에 `string` 필드 금지.** 전부 강타입 ID |
| N4 | **패킷에 `DateTime`/`TimeSpan` 금지.** 시간은 `Tick`(long)만 |
| N5 | 모든 명령에 `CorrelationId` |
| N6 | 모든 이벤트에 `Sequence`. 갭 검출 → 경보 |
| N7 | 이벤트 처리는 멱등. 2회 주입 → 상태 해시 동일 |
| N8 | 배치가 기본. `Enqueue` + `FlushAsync`만 노출 |

**명령은 유실된다고 가정한다.** 모든 플랜 스텝에 `timeout_s`가 있고, 응답 이벤트가 안 오면 로컬에서 `ActionFailed(Timeout)`을 합성해 진행을 재개한다. 이 경로는 `Npc.Sim --drop-rate`로 상시 테스트한다.

### 2.3 결정론

리플레이 100% 일치가 게이트다. 게임 로직에서:

| 금지 | 대체 |
|---|---|
| `DateTime.Now` / `DateTime.UtcNow` / `Stopwatch` | `Tick` |
| `Random` (무시드) | `(npcId, tick)` 해시 |
| `Guid.NewGuid()` | 순증 `CorrelationId` |
| `Dictionary`/`HashSet` 순회 의존 | 정렬된 배열 |
| 병렬 실행 순서 의존 | 틱 루프는 단일 스레드. 워커 결과는 스텝 경계에서만 반영 |

지터·난수는 전부 결정론적이어야 한다. 예: 시간대 전환 분산은 `Hash(npcId) % 601 - 300`.

**소켓 경로에서는 성립하지 않는다.** 리플레이 일치는 루프백 경로의 성질이고, TCP 에서는 명령이
몇 틱 늦게 도착해 스텝 경계가 바뀐다. 결함이 아니라 사실이다 — 결정론이 필요한 회차는
`--loopback`으로 돌린다.

### 2.4 마스터데이터

- **`masterdata/`가 단일 원천(SSOT)이다.** 액션·플래그·아키타입을 코드에 하드코딩하지 않는다.
- **`code` 번호와 `bit` 번호는 절대 재배치하지 않는다.** 프리베이크된 플랜 2,880개가 통째로 깨진다. 추가는 뒤에만.
- `prompt/` 의 액션 카탈로그는 `actions.json`에서 **생성**된다. 손으로 편집하지 않는다.
- 검증 V1~V13 실패는 **기동 실패**다. 경고 후 진행을 허용하지 않는다.
- **파생물(`poi_distances.bin`·`npc_instances.json`)은 `derived.lock.json` 이 신선도를 건다.**
  낡으면 기동은 되지만 경고가 나온다 — `npc regen` 이 무엇을 다시 만들지 알려 준다.
  생성기(`tools/gen_*.cs`)가 잠금을 갱신하므로 **생성기를 돌린 뒤 잠금도 같이 커밋한다.**

작업 순서를 지킨다 (역순이면 계속 되돌아온다):

```
world_flags → items → actions → zones → pois → archetypes
→ context_buckets → interrupts → fallback_plans → prompt(자동생성) → npc_instances(스크립트)
```

스키마 전문과 검증 규칙은 `docs/reference_masterdata.html` 에 있다.

### 2.5 프롬프트

| 규칙 | 이유 |
|---|---|
| 고정 프리픽스는 기동 시 **1회 조립**하고 이후 불변 | 프롬프트 캐시 |
| 프리픽스 토큰 수 **하한 4,096** (실측 11,967. 상한 없음) | Gemini 3.x / Claude Haiku 4.5 캐시 최소 임계. 상한은 비용 문제일 뿐이라 카탈로그를 줄이지 않는다 |
| 프리픽스 SHA-256을 모든 요청 메트릭에 태그로 | 유니크 해시가 2개 이상 = 즉시 경보 |
| 가변 서픽스 **≤ 300 토큰** (테스트로 강제) | dotLLM prefill이 느리다. 방치하면 반드시 자란다 |
| 재시도 피드백은 **서픽스에만** 넣는다 | 프리픽스를 건드리면 캐시가 깨진다 |
| **플레이어 작성 문자열을 프롬프트에 넣지 않는다** | 프롬프트 인젝션. 캐릭터명·채팅·길드명 전부. 구조화 enum과 내부 ID만 |

### 2.6 LLM 출력

- **강제 디코딩을 신뢰하지 않는다.** 제공사별 `strict` 지원이 다르다. 4단 검증기를 항상 통과시킨다.
- 검증 실패 시 재시도는 **1회만**. 2회 이상은 성공률이 거의 안 오르고 토큰만 태운다.
- `PlanStore.Resolve`는 **절대 null을 반환하지 않는다.** 미스여도 폴백 플랜을 준다. 시나리오 C(LLM 전면 차단)가 통과하는 이유다.
- 플랜 스왑은 **스텝 경계에서만** 원자적으로. 스텝 중간에 바꾸면 `CorrelationId`가 꼬인다. (인터럽트는 예외 — 즉시 스왑하되 진행 중 명령은 상관 ID로 무시)

### 2.7 비용 · 라이선스

- 일일 토큰 하드 캡을 우회하는 코드를 만들지 않는다. 초과 시 T2 → T1 강등 → 거절이다.
- **dotLLM은 GPLv3다. NuGet 참조로 인프로세스 임베딩하지 않는다.** 별도 프로세스 + HTTP(OpenAI 호환)로만 쓴다.
- LLM 호출은 반드시 `IChatClient` 추상화를 통한다. 특정 제공사 SDK를 `Npc.Llm` 밖에서 직접 부르지 않는다.

---

## 3. 프로젝트 의존 규칙

```
Npc.Contracts  ←  외부 NuGet 의존 0. 참조하는 프로젝트 없음
Npc.Core       ←  외부 NuGet 의존 0. Contracts만 참조
Npc.MasterData ←  Core
Npc.Runtime    ←  Core, MasterData, Contracts, Planning
Npc.Planning   ←  Core, MasterData
Npc.Narrative  ←  Core, MasterData          (정의 설명 카드. LLM·시각·난수 없음)
Npc.Llm        ←  Core, MasterData (+ Microsoft.Extensions.AI)
Npc.Wire       ←  Contracts (+ MemoryPack)
Npc.Gateway    ←  Contracts, Wire
Npc.Sim        ←  Contracts, MasterData
Npc.Host       ←  전부
```

`Npc.Wire` 는 링크의 **전송 표현**이다 (`docs/reference_link.html`). `Npc.Contracts` 에 NuGet 의존을
만들지 않으려고 와이어 DTO 를 따로 두고 1:1 매핑한다 — `[MemoryPackable]` 을 `NpcCommand` 에 붙이는
순간 계약 프로젝트가 직렬화기 버전에 묶인다. **`Npc.Runtime` 은 `Npc.Wire` 를 참조하지 않는다** —
런타임이 전송 표현을 알 이유가 없다.

테스트 베드는 **단방향 잎(leaf)** 이다. 아무도 참조하지 않는다.

```
testbed/Npc.TestBed.Protocol  ←  Wire
testbed/Npc.TestGameServer    ←  Sim, MasterData, Wire, Protocol
testbed/Npc.TestClient        ←  MasterData, Protocol   (net10.0-windows · 미착수)
```

**`Npc.Tests` 는 `Npc.TestClient` 를 참조하지 않는다** — 참조하면 테스트 프로젝트가 `net10.0-windows` 로 끌려간다.

- **`Npc.Runtime`은 `Npc.Llm`을 참조하지 않는다.** 참조가 생기면 틱 루프에 LLM이 들어올 길이 열린다.
- `Npc.Runtime → Npc.Planning`은 허용한다. `CognitionScheduler.Scan`이 `PlanStore`·`ReplanQueue`를 직접 받기 때문이다. `Npc.Planning`은 `Core`·`MasterData`만 참조하므로 이 간선으로 LLM이 들어올 길은 없다.
- `Npc.Core`와 `Npc.Contracts`에 NuGet 패키지를 추가하지 않는다. 순수 로직만.
- **`Npc.Narrative` 는 잎이다.** `Npc.Host`·런타임이 참조하지 않는다 — 서버가 도는 데 설명 카드는
  필요 없다. 부르는 것은 도구(`npc` CLI · `Npc.Narrate`)와 앞으로의 Studio·MCP 다.
- **`Npc.MasterData/Authoring/` 은 편집 도구다** (F-04). 로더 옆에 두되 로더가 의존하지 않는다 —
  기동 경로에 편집 기능이 들어갈 이유가 없다. 예외는 `DerivedArtifacts` 하나로,
  로더가 파생물 신선도를 읽어 `MasterDataSet.StaleArtifacts` 에 싣는다.

도구는 전부 잎이다.

```
tools/Npc.Cli       ←  Contracts, Core, MasterData, Narrative, Planning, Sim   (npc 명령)
tools/Npc.Prebake   ←  Core, MasterData, Planning, Llm, Sim
tools/Npc.Narrate   ←  Contracts, Core, Gateway, MasterData, Narrative
tools/gen_*.cs      ←  #:project 로 MasterData (파생물 잠금 갱신)
```

---

## 4. 작업 시 확인 순서

새 기능·수정을 시작하기 전:

1. **`CODEMAP.md` 에서 해당 작업 줄을 찾는다** → 거기 적힌 파일만 연다.
   `src` 96파일 21,000줄을 매번 훑지 않는다. 이름으로 못 찾으면 `CODEMAP.md` §2 의 추적 경로를 탄다
2. 고칠 파일의 `tests/Npc.Tests/<같은이름>Tests.cs` 를 먼저 읽는다 — 무엇을 보장하는지가 거기 있다
3. 건드리는 타입이 `docs/reference_link.html`·`docs/reference_masterdata.html`에 정의되어 있는가
   → 있으면 그 이름·시그니처를 그대로 쓴다
4. §2의 절대 규칙에 걸리는가
5. 수용 기준에 해당 항목이 있는가 (`docs/reference_metrics.html` §13) → 테스트를 같이 쓴다

작업 후:

- [ ] `dotnet test` 통과
- [ ] `dotnet format --verify-no-changes` 통과
- [ ] 틱 루프를 건드렸다면 부하 테스트로 p99 ≤ 20ms, **`/metrics` 의 `bytesPerTick` = 0** 확인
      (`gen0Collections` 로 재지 않는다 — 그 값은 `GC.CollectionCount(0)` 라 **프로세스 전역**이고,
      대시보드 HTTP·소켓 리시버·직렬화가 전부 든다. 틱 루프를 재는 계기는 `bytesPerTick` 하나다)
- [ ] 패킷을 건드렸다면 `Contracts_*`·`Wire_*` 테스트 통과 (N2/N3/N4 + DTO 드리프트)
- [ ] 마스터데이터를 건드렸다면 무효화 범위 확인 — `ContentHash` 가 바뀌면 프리베이크 재실행이 필요하다
- [ ] 프롬프트를 건드렸다면 프리픽스 해시 변경 → **플랜 스토어 전량 무효**. 의도한 것인지 확인
- [ ] 문서와 어긋나는 변경이면 같은 커밋에서 해당 레퍼런스 HTML 도 수정

---

## 5. 테스트 카테고리

| Category | 내용 | CI |
|---|---|---|
| (없음) | 단위 테스트. 빠르고 LLM 미호출 | 항상 |
| `Contracts` | N1~N8 강제 (리플렉션 검사) | 항상 |
| `Wire` | 소켓 위의 표현 — 와이어 DTO 왕복·크기 고정·프레임 코덱. **계약 타입과의 드리프트를 여기서 잡는다** — `Npc.Contracts` 에 `[MemoryPackable]` 을 못 붙여 DTO 를 갈랐기 때문이다 | 항상 |
| `TestBed` | 게임서버 대역·클라이언트 세션·종단 | 항상 |
| `Determinism` | 리플레이 일치, 멱등성 | 항상 |
| `Load` | NPC 5,000 부하. 수 분 소요. **`docs/measurements/W10_load.csv` 를 덮어쓴다** — 돌린 뒤 `git diff` 로 의도한 갱신인지 확인한다 | 야간 |
| `Golden` | 골든 50건 × 3회. **LLM 호출·비용 발생** | 수동 / 릴리스 전 |
| `FaultInjection` | 시나리오 C, 링크 장애 | 야간 |
| `Gate` | **실측 산출물이 있어야 판정되는 항목.** `planstore/manifest.json`·검수 기록이 근거다. 산출물이 없으면 **실패한다** — 없는 것을 통과로 세면 게이트가 거짓이 된다 | 수동 / 게이트 판정 시 |

골든 테스트는 **속성 단언만** 쓴다. 정확한 문자열 비교를 하지 않는다 — LLM 출력은 매번 다르고 그게 정상이다. 3회 중 2회 통과를 합격으로 본다.

---

## 6. 버전 관리

```gitignore
planstore/plans/         # 생성물. 재현 가능
planstore/rejected/
models/                  # GGUF 파일
tools/dotllm/            # GPLv3 바이너리. 별도 배포
logs/ replays/ artifacts/
```

**반드시 커밋하는 것:**

- `masterdata/**` — 소스
- `planstore/pinned/**` — **사람이 수정한 플랜.** 잃으면 검수 작업이 날아간다
- `planstore/manifest.json` — 실측 원자료
- `tests/golden/**`
- `docs/measurements/**` — 실측 원자료(jsonl·csv). **보고서 md 는 `docs/reference_metrics.html` 로 옮겼다**

커밋 메시지: `<scope>: <내용>` (예: `runtime: 인지 LOD 밴드 이동을 틱당 64건으로 제한`)

---

## 7. 자주 하는 실수

| 실수 | 결과 | 방지 |
|---|---|---|
| 틱 루프에 LINQ 한 줄 추가 | Gen0 GC 폭증, p99 초과 | 부하 테스트를 CI 야간에 |
| 서픽스에 필드 추가 | 300 → 800 토큰, prefill 3배 | `Suffix_NeverExceeds300Tokens` 테스트 |
| 프리픽스에 동적 값 혼입 | 캐시 전면 미적중, 비용 급증 | 프리픽스 해시 유니크 카운트 감시 |
| 프리픽스 캐시 무효화 주석을 **뒤에** 붙임 | 캐시가 안 깨져 측정이 거짓이 된다 | 접두 일치다. 무효화는 반드시 프리픽스 **앞** |
| `world_flags.json` 비트 재배치 | 플랜 2,880개 전부 무효 | 추가는 뒤에만 |
| 재계획 큐에 같은 NPC 중복 삽입 | 큐 즉시 포화 | `_heapPos`로 중복 검출 후 점수 갱신 |
| 워커가 틱 루프 상태를 직접 수정 | 데이터 레이스, 비결정성 | `PendingPlanId`에 `Volatile.Write`만 |
| 검증 실패분을 조용히 폐기 | 품질 개선 원자료 소실 | `planstore/rejected/`에 실패 코드와 함께 보존 |
| `manifest.json`에 `DateTime.Now` | 결정론 파괴 | 시각은 외부에서 인자로 주입 |
| `Npc.Runtime`에서 `Npc.Llm` 참조 추가 | 틱 루프에 LLM이 들어올 길 | §3 의존 규칙 |
| v1 와이어 DTO(`Npc.Wire/V1/`)에 필드 추가 | v1 게임서버가 읽던 배치가 통째로 어긋난다 | **동결이다.** 새 필드는 `V2/` 에만 |
| 의미를 등록하지 않고 `ExtA`·`ExtB` 사용 | 두 팀이 같은 칸에 다른 것을 넣고 알아챌 계기가 없다 | `ExtensionSlots` 에 등록 + `Ext_ZeroForUndefinedKinds` |
| 아키타입에 `duty_hours` 없이 `Guard`·`Patrol` 허용 | 인지 스캔이 매번 이탈로 읽어 재계획 큐 포화 | **V12 가 기동을 막는다.** `OnDuty`를 세우는 것은 `duty_hours` 뿐이다 |
| `code`·`bit` 를 눈으로 세어 다음 번호를 정함 | 중복·예약 구간 침범 | `npc next-code <파일>` 이 답한다 |
| 마스터데이터를 고치고 생성기를 안 돌림 | 낡은 거리표로 조용히 돈다 | `npc regen --check` (CI 에 걸 수 있다) |
| 편집 스크립트가 문자열 치환으로 JSON 을 고침 | 서식이 깨져 diff 를 못 읽는다 | `JsonSurgeon` (F-04). 무변경 편집은 바이트 동일 |

---

## 8. 판단이 필요할 때 기준

| 상황 | 기준 |
|---|---|
| 성능 vs 가독성 (틱 루프) | 성능. 대신 주석으로 이유를 남긴다 |
| 성능 vs 가독성 (그 외) | 가독성 |
| LLM에 시킬까 규칙으로 짤까 | **반응 속도가 필요하면 규칙.** 인터럽트·전제조건 판정은 항상 규칙 |
| 캐시 vs 실시간 생성 | **재사용 횟수에 비례해 품질에 투자한다.** 수천 NPC가 공유하면 T2 고품질, 1회용이면 T1 |
| 정확성 vs 처리량 (검증) | 정확성. 검증을 건너뛴 플랜을 런타임에 올리지 않는다 |
| 새 액션 추가 요청 | 40개 상한(현재 37). 하나를 빼거나 기존 액션의 파라미터로 흡수 |
| 새 마스터데이터 필드 | 프롬프트 서픽스에 실리는가 확인. 실린다면 300토큰 예산부터 |

---

## 9. 알아둘 배경 수치

**전부 실측이다** (2026-07-28 기준). 추정치를 쓰지 않는다 — 어긋난 값은 어긋난 대로 적는다.
전문과 판정은 **`docs/reference_metrics.html`** 에 있다.

| 항목 | 값 |
|---|---|
| 틱 p99 (NPC 5,000) | **0.801 ms** (예산 20ms) · 틱당 할당 **0 B** · 오버런 0 |
| 플랜 캐시 히트율 | **98.67 %** (시나리오 A · 195버킷으로 달성) |
| 로컬 요청당 지연 | **5.14s** (8B) · **3.70s** (4B). 둘 다 프리픽스 캐시 적중 |
| 로컬 prefill / decode | **0.32s / 4.83s** (8B). prefill 은 가정대로, **decode 가 3.2배 느리다** |
| 로컬 처리량 | 동시 2 에서 **4B 12.07 · 8B 2.15 req/s**. **8B 는 동시 8 에서 0.36 으로 붕괴** |
| 프리픽스 캐시 효과 | prefill **85~88 % 감소**. 이 설계의 유일한 검증된 승리다 |
| 요청당 입력 토큰 | **13,948** (프리픽스 11,967). 계획 가정 1,750 의 **8배** |
| 요청 단가 · 프리베이크 | **$0.001015/요청** · 2,880건 **$5.12** (도달집합 264건은 $0.37) |
| 프롬프트 캐시 적중률 | **30.7~42.6 %.** 암시적 캐싱이라 95 % 는 도달 불가 |
| 버킷 수 | 아키타입 40 × 6 × 4 × 3 = **2,880**. 단 **실제 도달은 264개(9.2 %)**<br>아키타입 수는 `archetypes.json` 이 정한다 (F-05) — 코드 상수가 아니다 |
| 외부 동시성 | **24 까지 429 0회.** 32·64 는 미측정 — `--concurrency` 기본은 **8** |
| 플랜 생성 통과율 · 다양성 | **64.6 %** (288버킷) · 유니크 시퀀스 **37.1 %** |
| 골든 회귀 | 단언 합격률 **93.6 %** (206/220) |
| dotLLM 특성 | decode는 llama.cpp의 0.8x, **prefill은 2~5배 느림**. continuous batching 없음 |

> **로컬 수치는 전부 VRAM 8 GB(RTX 4060)에 묶여 있다.** 8B Q4_K_M 가중치만 4,789 MiB 라
> KV 캐시가 넘치면 PCIe 페이징이 걸린다. **24 GB 급 외삽에 이 표를 그대로 쓰지 않는다.**

미측정으로 남은 것은 `docs/reference_metrics.html` §14 에 있다 — 8B 품질 채점 · 외부 동시 32·64 ·
프리베이크 전량 회차 · 블라인드 평가 · 수작성 시간 기준선.
