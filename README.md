# LlmNpcServer

> LLM 기반 MMORPG NPC 행동 서버 — **사내 R&D / 기술 검증 프로젝트**

로컬 LLM(dotLLM) 및 외부 OpenAI 호환 API를 **행동 플랜 컴파일러**로 사용해, 5,000마리 NPC의 행동 루틴을 상황(시간대·지역상태·기후)에 따라 자동 생성하고, 결정론적 런타임이 그것을 실행하며 게임서버에 명령 패킷을 송출한다.

**현재 상태: 설계 완료, 구현 착수 전 (Spec-first).** `docs/` 아래 사양이 먼저 확정되어 있고, 코드는 이 사양을 따라 작성한다.

---

## 무엇을 만들고, 무엇을 안 만드는가

이 저장소에서 만드는 것은 **NPC 서버**다. **게임서버는 만들지 않는다.** 다만 NPC 서버를 혼자 돌려볼 수 없으므로 게임서버 흉내를 내는 대역(`Npc.Sim`)을 함께 만든다.

| 대상 | 이번 프로젝트 | 비고 |
|---|---|---|
| **NPC 서버** (`Npc.Host`) | ✅ 만든다 | 본체 |
| **경계 정의** (`IGameServerLink` + 패킷) | ✅ 만든다 | NPC 서버의 아웃바운드 포트. → `docs/02` |
| **게임서버 대역** (`Npc.Sim`) | ✅ 만든다 | 실제 게임서버 없이 검증하기 위한 가짜 |
| 실제 MMORPG 게임서버 | ❌ 안 만든다 | 이미 있거나 남이 만든다. "붙일 때 이렇게 붙는다"만 정의 |
| 실제 네트워크 전송 (소켓·직렬화) | ❌ 안 만든다 | 인터페이스·패킷 정의까지 |

> `IGameServerLink`의 "GameServer"는 **상대방**을 가리킨다. "게임서버의 인터페이스"가 아니라 **"게임서버로 향하는 NPC 서버의 링크"** 다.

---

## 핵심 아이디어

> **LLM을 실행기가 아니라 컴파일러로 쓴다.**

LLM은 행동 플랜을 *생성*하고, 결정론적 런타임이 그것을 *실행*한다. 생성은 느리고 비싸도 되지만, 실행은 빠르고 재현 가능해야 한다.

```
┌──────────────────────────────────────────────────────┐
│ L0  결정론적 NPC 런타임    (10Hz 틱, LLM 무관)        │
│     플랜 실행기 · 인지 LOD 스캐너 · 게임서버 명령 송출  │
└──────────────▲───────────────────────────────────────┘
               │ 플랜 조회 (배열 첨자, O(1), 락 프리)
┌──────────────┴───────────────────────────────────────┐
│ L1  플랜 스토어  key = (아키타입 × 시간대 × 지역 × 기후)│
│     40 × 6 × 4 × 3 = 2,880  ·  목표 히트율 98%+       │
└──────────────▲───────────────────────────────────────┘
               │ 캐시 미스 / 무효화 시에만
┌──────────────┴───────────────────────────────────────┐
│ L2  우선순위 재계획 큐 (사건 기반, 예산 상한)          │
│     점수 = 플레이어근접 + 플랜노후 + 전제이탈 + 긴급도 │
└──────────────▲───────────────────────────────────────┘
               │
┌──────────────┴───────────────────────────────────────┐
│ L3  플랜 컴파일러 (3-티어)                            │
│     T0 캐시 → T1 로컬 dotLLM → T2 외부 API           │
│     무상태 호출 · JSON Schema 강제 · 4단 검증기       │
└──────────────────────────────────────────────────────┘
```

### 왜 이 구조인가

| 문제 | 해결 |
|---|---|
| 소비자 GPU 1장으로 5,000 NPC 주기 재계획 = 용량의 2.4배 초과 | 플랜을 (아키타입 × 상황) 단위로 캐시. 개별 재계획은 예산 내에서 우선순위로 선별 |
| 장시간 실행 시 LLM 컨텍스트 관리 | **모든 호출을 무상태로.** 기억은 게임 DB에 구조체로, 요약은 규칙으로. 컨텍스트 드리프트가 원천 발생하지 않음 |
| 외부 API 지연 변동성 (p99 수십 초) | LLM은 게임 크리티컬 패스에 없다. 틱 루프에 LLM 호출이 존재하지 않음 |
| LLM 장애 / 비결정성 | 플랜을 생성 시점에 저장. 캐시 → 폴백 3단 방어. 리플레이는 LLM 없이 재현 |

---

## 결과물

| 구분 | 내용 |
|---|---|
| **실행 바이너리** | `Npc.Host`(NPC 서버) · `Npc.SimHost`(게임서버 대역) · `Npc.Prebake`(플랜 생성 CLI) · `Npc.Replay` |
| **데이터 아티팩트** | 마스터데이터 11종 · **프리베이크 플랜 2,880개** · 골든 픽스처 50건 · 리플레이 로그 |
| **관측** | 운영 대시보드 (단일 HTML) |
| **테스트 베드** | `Npc.TestGameServer`(소켓 게임서버 대역) · `Npc.TestClient`(WinForms 뷰어) → [`testbed/`](testbed/README.md) |

실질적 핵심 산출물은 코드가 아니라 **`planstore/`의 플랜 2,880개**다. 사람이 읽고 고칠 수 있는 JSON이며, 이것이 오써링 자동화의 증거물이다.

### 데모 시나리오

| # | 내용 | 검증 대상 |
|---|---|---|
| **A** | 살아있는 마을 — 게임 7일 × 60배속, NPC 5,000 | 기본 동작 · 성능 · 캐시 히트율 |
| **B** | 공성 이벤트 — 지역 상태 War 전환 | 재계획 · 인터럽트 · 아키타입별 차별화 |
| **C** | LLM 전면 차단 — T2 → T1 → 캐시 순차 kill | 가용성 · 폴백 |

---

## 요구 환경

| 항목 | 요구 |
|---|---|
| OS | Windows 10/11 x64 |
| SDK | .NET 10 SDK |
| GPU | NVIDIA, VRAM 12GB 이상 (T1 로컬 추론용). 없으면 T2 전용으로 동작 |
| 로컬 추론 | [dotLLM](https://dotllm.dev/) — **별도 프로세스로만 실행** (GPLv3, §라이선스 참고) |
| 외부 API | OpenAI 호환 엔드포인트 (선택) |

---

## 빠른 시작

```powershell
# 0) 의존 확인
dotnet --version                     # 10.x

# 1) 빌드 + 테스트
dotnet build -c Release
dotnet test

# 2) 마스터데이터 검증만 (V1~V11)
dotnet run --project src/Npc.Host -- validate --masterdata ./masterdata

# 3) LLM 없이 구동 — 폴백 플랜만으로 NPC 500마리
dotnet run -c Release --project src/Npc.Host -- \
    --loopback --npcs 500 --time-scale 600 --days 7 --no-llm

# 4) 로컬 추론 기동 (별도 터미널)
.\tools\dotllm\dotllm.exe serve --model .\models\Qwen3-8B-Q4_K_M.gguf --port 8080 --gpu-layers 99

# 5) 플랜 프리베이크 (외부 API)
#    --concurrency 는 AIMD 초기값이다. 8 에서 시작해 올린다 —
#    "동시 32" 는 상위 계획의 추정이고 실측이 아니다 (docs/measurements/W6_compile_stats.md §6)
$env:OPENROUTER_API_KEY = "..."
dotnet run -c Release --project tools/Npc.Prebake -- \
    --masterdata ./masterdata --out ./planstore \
    --tier T2 --model openrouter-gpt-5-nano --concurrency 8 --budget-usd 5.00

# 6) 전체 구동 + 대시보드
dotnet run -c Release --project src/Npc.Host -- \
    --loopback --npcs 5000 --time-scale 60 --days 7 \
    --scenario ./scenarios/siege.jsonl
# → http://localhost:5080/dashboard

# 7) 눈으로 보기 — 게임서버 대역 + 테스트 클라이언트를 소켓으로 붙여 띄운다 (Windows)
#    게임서버 → NPC 서버 → 클라이언트를 순서대로 띄우고 Ctrl-C 에 셋 다 내린다.
./testbed/run_demo.ps1 -Scenario siege
# → testbed/README.md 에 화면 보는 법과 알려진 한계가 있다
```

### 주요 실행 옵션

| 옵션 | 의미 |
|---|---|
| `--loopback` | `Npc.Sim` 인프로세스 월드에 직결 (기본) |
| `--link null\|record\|replay` | 링크 구현체 교체 |
| `--npcs N` | NPC 수 |
| `--time-scale N` | 시간 압축 (1=실시간, 60=1초당 게임 1분) |
| `--tier none\|t1\|t2\|all` | 어느 티어까지 켤까 (기본 `none`). `t1`=로컬 개별 재계획, `t2`=외부 버킷 미스 |
| `--no-llm` | `--tier none` 의 별칭. 캐시 + 폴백만 |
| `--t1-workers N` / `--t2-workers N` | 워커 수 (기본 2 / 8) |
| `--t1-engine <id>` / `--t2-engine <id>` | `appsettings.Llm.json` 의 엔진 id |
| `--scenario <jsonl>` | 시나리오 이벤트 주입 |
| `--fail-rate` / `--drop-rate` | Sim의 액션 실패 / 명령 유실 주입 |
| `--trace <path>` | `--link record` 의 출력 · `--link replay` 의 입력 (jsonl) |
| `--days N` | 돌릴 게임 일수. 0=무제한 |
| `--player-bots N` | 가상 플레이어 수 (기본 20). 0이면 모든 NPC가 비활성 밴드에 머문다 |
| `--masterdata <dir>` | 마스터데이터 디렉터리 (기본 `./masterdata`) |
| `--seed N` | Sim 시드 (기본 20260725) |
| `--port N` | 대시보드·메트릭 포트 (기본 5080) |
| `--max-speed` | 10Hz 페이싱 없이 최대 속도로. 부하·게이트 측정용 |
| `--no-dashboard` | 웹 호스트를 띄우지 않는다 |

---

## 프로젝트 구조

```
src/
  Npc.Contracts/    게임서버 연동 IF + 패킷 DTO        (외부 의존 0)
  Npc.Core/         플랜 DSL · 4단 검증기 · WorldFlags  (외부 의존 0)
  Npc.MasterData/   로더 · 검증 · 읽기전용 인덱스
  Npc.Runtime/      틱 스케줄러 · 플랜 실행기 · 인지 LOD
  Npc.Planning/     플랜 캐시 · 버킷터 · 우선순위 재계획 큐
  Npc.Llm/          IChatClient 어댑터 · 프롬프트 조립 · 3-티어 라우터
  Npc.Wire/         링크의 전송 표현 (MemoryPack DTO + 프레임 코덱)
  Npc.Gateway/      IGameServerLink 구현체 (Loopback/Null/Recording/Replay/Tcp)
  Npc.Sim/          헤드리스 월드 = 게임서버 대역
  Npc.Host/         ASP.NET 호스트 · 메트릭 · 대시보드
testbed/            테스트 베드 — 단방향 잎(아무도 참조하지 않는다)  → testbed/README.md
  Npc.TestBed.Protocol/  클라이언트 프로토콜 (게임서버 ↔ 클라이언트)
  Npc.TestGameServer/    게임서버 대역 프로세스 (소켓 :7010 링크 · :7020 클라이언트)
  Npc.TestClient/        WinForms 클라이언트 (net10.0-windows)
  scenarios/             데모 시나리오 3종 · run_demo.ps1
tools/
  Npc.Prebake/      프리베이크 CLI
  Npc.Replay/       리플레이 CLI
  gen_npcs.cs       NPC 인스턴스 생성기 (.NET 10 파일 기반 앱)
masterdata/         마스터데이터 11종  → docs/01
planstore/          프리베이크 플랜 (plans/ 는 gitignore, pinned/ 는 버전관리)
scenarios/          시나리오 스크립트 (jsonl)
tests/Npc.Tests/    단위 · 골든 · 부하 · 장애주입
docs/               설계 사양 (아래)
```

---

## 문서

**구현 착수 시 읽는 순서**

```
1. LLM_NPC_Server_Plan.md   왜 이 구조인가 (타당성 판단 · 아키텍처 · 리스크)
2. docs/00                  무엇을 만드는가 (결과물 · 데모 · 수용 기준)
3. docs/01 → 02 → 03        공통 계약 3종. 여기서 정한 타입 이름을 전 코드가 쓴다
4. docs/10                  W1 설계 사양
5. TASKS.md                 태스크 규약 · 진행 원장
6. docs/10_..._TASKS.md     W1 작업 지시서 — 여기서부터 구현 착수
```

> 설계 사양(`docs/1x_Phase*.md`)은 **무엇을 왜**, 작업 지시서(`docs/1x_*_TASKS.md`)는 **무엇을 어떤 순서로**다.
> 코딩 에이전트에게는 항상 태스크 ID 하나(`T1-028` 등)를 준다. 총 164개 태스크.

| 문서 | 내용 |
|---|---|
| [`docs/index.html`](docs/index.html) | **한 장짜리 안내서 (HTML).** 아키텍처 · 용도 · 빌드 · 실행 · 실측 결과. 브라우저로 파일을 그대로 열면 된다 |
| [`LLM_NPC_Server_Plan.md`](LLM_NPC_Server_Plan.md) | 상위 계획 · 타당성 판단 · 아키텍처 · 비용 분석 · 리스크 대장 |
| [`docs/00_Deliverables.md`](docs/00_Deliverables.md) | 결과물 명세 · 데모 시나리오 · 최종 수용 기준 |
| [`docs/01_MasterData_Spec.md`](docs/01_MasterData_Spec.md) | 마스터데이터 11종 스키마 · 검증 규칙 V1~V11 · 작업 순서 |
| [`docs/02_GameServer_Link.md`](docs/02_GameServer_Link.md) | **NPC 서버 ↔ 게임서버 경계** · 패킷 29종 · 네트워크 안전 규칙 N1~N8 · Sim 사양 |
| [`docs/03_PlanDSL_Spec.md`](docs/03_PlanDSL_Spec.md) | 플랜 DSL · JSON Schema · 4단 검증기 · 실행기 · 스토어 포맷 |
| [`TASKS.md`](TASKS.md) | **태스크 규약 · 진행 원장 · 게이트 요약** (164개 태스크) |
| [`docs/10_Phase0_W1_Spike.md`](docs/10_Phase0_W1_Spike.md) · [`_TASKS`](docs/10_Phase0_W1_TASKS.md) | **W1** 스파이크 — 숫자 6개를 뽑는다 (13) |
| [`docs/11_Phase1_W2-4_Core_Runtime.md`](docs/11_Phase1_W2-4_Core_Runtime.md) · [`_TASKS`](docs/11_Phase1_W2-4_TASKS.md) | **W2–4** 코어 · 런타임 · Sim — LLM 없이 돌린다 (62) |
| [`docs/12_Phase2_W5-6_Plan_Compiler.md`](docs/12_Phase2_W5-6_Plan_Compiler.md) · [`_TASKS`](docs/12_Phase2_W5-6_TASKS.md) | **W5–6** 플랜 컴파일러 · 검증기 · 통과율 개선 (24) |
| [`docs/13_Phase3_W7-8_Plan_Cache.md`](docs/13_Phase3_W7-8_Plan_Cache.md) · [`_TASKS`](docs/13_Phase3_W7-8_TASKS.md) | **W7–8** 플랜 캐시 · 프리베이크 · 사람 검수 (21) |
| [`docs/14_Phase4_W9-10_Scheduler_Tiering.md`](docs/14_Phase4_W9-10_Scheduler_Tiering.md) · [`_TASKS`](docs/14_Phase4_W9-10_TASKS.md) | **W9–10** 우선순위 큐 · 3-티어 라우팅 · 부하 테스트 (24) |
| [`docs/15_Phase5_W11-12_Verification.md`](docs/15_Phase5_W11-12_Verification.md) · [`_TASKS`](docs/15_Phase5_W11-12_TASKS.md) | **W11–12** 골든 · 결정론 · 장애주입 · 블라인드 평가 · 보고 (20) |
| [`docs/20_TestBed_Spec.md`](docs/20_TestBed_Spec.md) · [`_TASKS`](docs/20_TestBed_TASKS.md) | **P6** 테스트 베드 — 소켓 링크 · 게임서버 대역 · 테스트 클라이언트 (38). W1–12 본편 밖의 트랙이다 |
| [`docs/testbed_guide.html`](docs/testbed_guide.html) | **게임서버 연동 테스트 안내서 (HTML).** 아키텍처 그림 · 핸드셰이크·틱 루프 애니메이션 · 무엇을 바꾸며 테스트하나 · **코드 분석 순서**. 브라우저로 파일을 그대로 열면 된다 |
| [`testbed/README.md`](testbed/README.md) | **데모 띄우는 법** — 한 줄 실행 · 포트 · 화면 보는 법 · 알려진 한계 |

AI 코딩 에이전트로 작업한다면 [`CLAUDE.md`](CLAUDE.md)를 먼저 읽는다.

---

## 로드맵 (12주)

| 주차 | 내용 | 게이트 |
|---|---|---|
| W1 | 스파이크 | 요청당 지연 가정이 실측과 ±50% 이내 |
| W2–4 | 코어 · 런타임 · Sim · 마스터데이터 | **LLM 0회 호출**로 NPC 500마리 게임 7일 완주 |
| W5–6 | 플랜 컴파일러 · 4단 검증기 | 검증 통과율 ≥ 90% |
| W7–8 | 플랜 캐시 · 프리베이크 | 2,880키 콜드 필 완주, 히트율 ≥ 98% |
| W9–10 | 스케줄러 · 3-티어 · 부하 테스트 | NPC 5,000, 틱 p99 ≤ 20ms, GPU ≤ 60% |
| W11–12 | 검증 · 블라인드 평가 · 보고 | 시나리오 A/B/C 통과, 리플레이 100% 일치 |

---

## 성능 목표 (NPC 5,000)

| 지표 | 목표 |
|---|---|
| 틱 실행 시간 p99 | ≤ 20ms (100ms 예산의 20%) |
| 인지 스캔 대상 | ≤ 150마리/틱 (전수 5,000 대비 33배 감소) |
| 플랜 캐시 히트율 | ≥ 98% |
| 플랜 검증 실패율 | ≤ 3% |
| 명령 송출 | ≥ 2,000 cmd/s, 틱 루프 Gen0 GC = 0 |
| GPU 사용률 | ≤ 60% |
| 프롬프트 캐시 적중률 | ≥ 95% |

---

## 범위 밖 (이번 R&D)

- **실제 네트워크 통신** — `IGameServerLink` 인터페이스와 패킷 정의까지만. 소켓·직렬화·재접속 미구현
- 게임 클라이언트 / 렌더링 — 대시보드와 로그로만 관측
- NPC 대사 자연어 생성 — `Speak(DialogueId)`로 ID만 송출
- 전투 AI · 패스파인딩 — 게임서버 소관. 명령만 발행
- 멀티 월드 / 샤딩 — 단일 월드 5,000 NPC

---

## 라이선스 주의

**dotLLM은 GPLv3다.**

- ✅ **별도 프로세스 + HTTP(OpenAI 호환 엔드포인트)로만 사용한다**
- ❌ NuGet 패키지를 프로젝트에 참조해 인프로세스로 임베딩하지 않는다

프로세스 분리는 라이선스 경계 외에도 GC 격리·크래시 격리·엔진 교체 용이성이라는 실익이 있다. 상용 전환을 검토하는 시점에 법무 확인을 별도로 건다.

본 저장소 자체의 라이선스는 사내 정책에 따른다.
