# LlmNpcServer

> LLM 기반 MMORPG NPC 행동 서버 — **사내 R&D / 기술 검증 프로젝트**

로컬 LLM(dotLLM) 및 외부 OpenAI 호환 API를 **행동 플랜 컴파일러**로 사용해, 5,000마리 NPC의 행동 루틴을 상황(시간대·지역상태·기후)에 따라 자동 생성하고, 결정론적 런타임이 그것을 실행하며 게임서버에 명령 패킷을 송출한다.

**현재 상태: 구현 진행 중 — 태스크 196 / 203 완료.** `docs/` 아래 사양을 코드보다 먼저 확정하는 **spec-first** 규칙은 그대로다.

Phase 게이트는 **P1 전 항목 통과**, 나머지는 부분 통과다
(P4 9/10 · P6 9/10 · P5 6/9 · P3 5통과·3미판정·1미측정 · P2 3/5 · P0 2/4).
남은 항목은 대부분 **"아직 안 돌렸다"**(전량 프리베이크 회차 · GPU를 켠 부하 회차)거나 **"사람 시간이 필요하다"**(블라인드 평가 12명 · 플랜 검수 40건)다.
판정 근거는 [`docs/reference_metrics.html`](docs/reference_metrics.html) 과 [`docs/measurements/`](docs/measurements/) 의 원자료에 있다 — **미달·미측정을 통과로 적지 않는다.**

---

## 무엇을 만들고, 무엇을 안 만드는가
이 저장소에서 만드는 것은 **NPC 서버**다. **게임서버는 만들지 않는다.** 다만 NPC 서버를 혼자 돌려볼 수 없으므로 게임서버 흉내를 내는 대역(`Npc.Sim`)을 함께 만든다.

| 대상 | 이번 프로젝트 | 비고 |
|---|---|---|
| **NPC 서버** (`Npc.Host`) | ✅ 만든다 | 본체 |
| **경계 정의** (`IGameServerLink` + 패킷) | ✅ 만든다 | NPC 서버의 아웃바운드 포트. → [`reference_link.html`](docs/reference_link.html) |
| **게임서버 대역** (`Npc.Sim`) | ✅ 만든다 | 실제 게임서버 없이 검증하기 위한 가짜 |
| **소켓 전송** (`Npc.Wire` + TCP 링크) | ✅ 만들었다 | P6. 프레임 코덱 · 핸드셰이크 · 재접속. → [`reference_link.html`](docs/reference_link.html) |
| **소켓 게임서버 대역 · 뷰어** (`testbed/`) | ✅ 만들었다 | P6. 눈으로 보는 자리. → [`testbed/README.md`](testbed/README.md) |
| 실제 MMORPG 게임서버 | ❌ 안 만든다 | 이미 있거나 남이 만든다. "붙일 때 이렇게 붙는다"만 정의 |

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
| 소비자 GPU 1장으로 5,000 NPC 주기 재계획 = **추정 2.4배 · 실측(8B) 7.1배 초과** | 플랜을 (아키타입 × 상황) 단위로 캐시. 개별 재계획은 예산 내에서 우선순위로 선별 |
| 장시간 실행 시 LLM 컨텍스트 관리 | **모든 호출을 무상태로.** 기억은 게임 DB에 구조체로, 요약은 규칙으로. 컨텍스트 드리프트가 원천 발생하지 않음 |
| 외부 API 지연 변동성 (p99 수십 초) | LLM은 게임 크리티컬 패스에 없다. 틱 루프에 LLM 호출이 존재하지 않음 |
| LLM 장애 / 비결정성 | 플랜을 생성 시점에 저장. 캐시 → 폴백 3단 방어. 리플레이는 LLM 없이 재현 |

---

## 결과물

| 구분 | 내용 |
|---|---|
| **실행 바이너리** | `Npc.Host`(NPC 서버 — 게임서버 대역·리플레이는 `--link` 로 갈아끼운다) · `npc`(마스터데이터·플랜 CLI) · `Npc.Prebake`(플랜 생성 CLI) · `Npc.Narrate`(기록을 하루 일지로) · `Npc.Conformance`(게임서버 적합성 키트) · `Npc.TestGameServer`·`Npc.TestClient`(P6) |
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

## 기능 목록

**무엇이 실제로 도는가.** 미구현은 [`PRODUCTION_ROADMAP.md`](PRODUCTION_ROADMAP.md) 체크리스트에
있고, 아래 표에는 **동작하고 테스트가 있는 것만** 적는다.

### 런타임 — NPC 를 움직인다

| 기능 | 내용 | 근거 |
|---|---|---|
| **틱 루프** | 10 Hz · NPC 5,000 · 단일 스레드 · **틱당 힙 할당 0 B** | p99 0.801 ms (예산 20 ms) |
| **SoA 저장소** | `class Npc` 를 5,000개 만들지 않는다. 핫 배열 합계 ~100 KB → L2 상주 | `NpcStore` |
| **플랜 실행기** | 스텝 상태 기계 · `timeout_s` 만료 시 `ActionFailed(Timeout)` **로컬 합성** | 명령 유실을 전제한다 |
| **인지 스캔 LOD** | 4밴드 · 틱당 상한 150 · 밴드 이동 틱당 64 | 5,000마리를 매 틱 훑지 않는다 |
| **인터럽트** | 14규칙 · **LLM 을 쓰지 않는다**. 우선순위 내림차순, 같으면 id 오름차순 | 반응 속도가 생명이다 |
| **플랜 스왑** | 스텝 경계에서만 원자적. 인터럽트는 예외(즉시 스왑 + 상관 ID 로 진행 중 명령 무시) | `CorrelationId` 가 꼬이지 않는다 |
| **결정론** | `DateTime`·`Stopwatch`·무시드 `Random` 금지. 지터는 `(npcId, tick)` 해시 | 루프백 리플레이 100 % 일치 |
| **게임 시계** | 재기동 시 게임서버가 준 시각으로 복원 · 정지 감시(`TickSyncWatchdog`) | 새벽 6시로 되돌아가지 않는다 |

### 플랜 — LLM 이 만들고 코드가 검증한다

| 기능 | 내용 | 근거 |
|---|---|---|
| **버킷 캐시** | (아키타입 × 시간대 6 × 지역 4 × 기후 3). 조회는 **배열 첨자 하나** | 히트율 98.67 % |
| **3-티어 라우팅** | T3 캐시 → T1 로컬(작은 모델) → T2 외부(고품질). 실패는 폴백으로 | 재사용 횟수에 비례해 품질에 투자 |
| **4단 검증기** | 스키마 → 어휘 → 정합성(GOAP 상태 전이) → 드라이런 | **강제 디코딩을 신뢰하지 않는다** |
| **폴백 보장** | `PlanStore.Resolve` 는 **절대 null 을 반환하지 않는다** | LLM 전면 차단(시나리오 C)에서도 NPC 가 산다 |
| **프롬프트 캐시** | 고정 프리픽스 1회 조립 후 불변 · 서픽스 ≤ 300 토큰(테스트 강제) | prefill 85~88 % 감소 |
| **예산 하드 캡** | 일일 토큰 캡 · 개체/버킷 **서브 쿼터** · 벽시계 USD 캡 | 초과 시 T2 → T1 강등 → 거절. **우회 경로 없음** |
| **LLM 페일오버** | 엔진별 회로 차단기. 429·5xx·타임아웃은 다음 엔진, 400·스키마 오류는 즉시 실패 | 전송 실패와 요청 오류를 구별한다 |
| **프롬프트 인젝션 방어** | **플레이어 작성 문자열을 프롬프트에 넣지 않는다.** 구조화 enum·내부 ID 만 | 리플렉션 테스트가 강제 |
| **`reasoning` 정화** | 제어문자·URL·이메일 제거 · 금칙어면 통째로 비운다. **플랜은 건드리지 않는다** | 검증 지난 플랜을 고치면 "검증됨" 이 거짓이 된다 |

### 게임서버 연동 — N1~N8

| 기능 | 내용 |
|---|---|
| **아웃바운드 포트** | `IGameServerLink`. 명령은 fire-and-forget, 결과는 이벤트로만 |
| **패킷 규약** | 전부 `readonly record struct` · **`string`·`DateTime` 금지** · 시간은 `Tick`(long) |
| **순서·멱등** | 명령에 `CorrelationId`, 이벤트에 `Sequence`(갭 검출 → 경보). 2회 주입 → 상태 해시 동일 |
| **링크 구현 5종** | `Loopback`(인프로세스 `Npc.Sim` 직결) · `Null` · `Recording` · `Replay` · `Tcp` |
| **와이어** | MemoryPack DTO + 8바이트 프레임 헤더. 버전 범위 [1, 2] 협상. **v1 56B/64B 는 동결**, v2 72B/80B |
| **확장 슬롯** | `Instance`(채널·인스턴스 던전) · `Faction`(세력) · `ExtA`/`ExtB`(예약). `ExtSlots` 기능 비트를 켠 게임서버에만 나간다 — 안 켜면 **버려진다** |
| **이기종 구현** | 오프셋 표(생성물) · 골든 바이트 벡터 7종 · **C++17/파이썬 참조 코덱** → [`docs/wire/`](docs/wire/layout_v2.md). v2 배치는 필드별 리틀엔디언 명시 직렬화다 |
| **적합성 키트** | `Npc.Conformance` 가 게임서버에 붙어 발행 규약 7종을 관찰하고 보고서를 낸다. **미판정을 통과로 세지 않는다** |
| **동적 로스터** | `--dynamic-roster` 로 런타임 스폰·디스폰. 여유 슬롯은 기동 시 잡고(`--npc-capacity`), **넘는 스폰은 무시하고 센다** |
| **적대 플레이어** | `PlayerHostility` 이벤트 → `HostilePlayerNearby` 플래그 → `attack_hostile_player` 인터럽트 → `CombatAction(TargetPlayer)`. **적대 판정은 게임서버가 한다** |
| **프롬프트 버전** | 플랜이 `planstore/<프리픽스 sha8>/` 에 쌓인다. **되돌리기 = 프롬프트 파일을 되돌리는 것** — 그 회차 폴더가 자동 선택된다. 전문은 `planstore/prefix/<sha8>.md` |
| **상호 인증** | HMAC-SHA256 + 논스 재사용 캐시 · `FixedTimeEquals` · TLS/mTLS |
| **해시 분할** | **구조 해시**는 완전 일치 요구(불일치 = 거절), **내용 해시**는 경고 후 수락 |
| **장애 주입** | `--drop-rate` 로 명령 유실을 상시 시험 |

### 운영 — 죽지 않는다

| 기능 | 내용 |
|---|---|
| **상태 스냅샷** | 주기 저장 + 종료 시 1회 · CRC32 · 손상되면 이전 세대로 폴백 |
| **복구 판정** | 포맷 버전 · 마스터데이터 해시 · 로스터 해시 · NPC 수가 맞아야 올린다 |
| **우아한 종료** | SIGTERM/SIGINT/SIGQUIT → 틱 루프 → 스냅샷 → 워커 → 링크(`Bye`) → 웹. 단계별 예산 |
| **헬스체크** | `/healthz/live` · `/ready` · `/startup`. 루프 하트비트가 근거 |
| **설정 3겹** | CLI > 환경변수(`NPC_*`) > 파일(`npc.settings.json`). 모르는 키는 거절 |
| **관측** | OpenTelemetry 메트릭·트레이스 · Prometheus(`/metrics/prometheus`) · OTLP · 구조화 로그 |
| **경보** | 14종 · 쿨다운 · 웹훅(Slack/Teams). 예산 임계 80/95/100 % |
| **관리 API** | 킬스위치 · 즉시 스냅샷. 토큰 인증 + 분당 실패 5회 제한 + **감사 로그**(`state/audit.jsonl`) |
| **질의 API** | `/npcs`(필터·페이지) · `/npc/{id}/context`(대화용, **전부 id·enum**) · `/buckets` · `/stream/npcs`(SSE 1Hz 변경분). **도구·대화·운영 전용** — 런타임 게임 로직은 링크만 쓴다 |
| **API 명세** | OpenAPI 3.1 → [`docs/openapi.json`](docs/openapi.json) (생성물) · 살아 있는 서버는 `GET /openapi/v1.json`. **툴 러너가 이것으로 호출을 만든다** |
| **비밀 취급** | 환경변수로만. 인자(`ps` 에 보인다)·파일(이미지에 굽힌다) 금지. `--bind 0.0.0.0` 은 토큰 없으면 거절 |
| **배포** | Dockerfile(비루트·`HEALTHCHECK`) · compose(데모 한 벌) · k8s(프로브 3종·시크릿) · Grafana |

### 콘텐츠 — 마스터데이터가 단일 원천

| 기능 | 내용 |
|---|---|
| **데이터 주도** | 액션·플래그·아키타입을 코드에 하드코딩하지 않는다. **아키타입 수도 코드가 모른다** |
| **검증 V1~V13** | 실패는 **기동 실패**. 경고 후 진행 없음 |
| **수정 힌트** | 검증 코드 41건 전부에 "무엇을 하면 되는가". `--json` 이 `fix_hint` 를 싣는다 |
| **편집 안전장치** | 다음 `code`/`bit`(예약 구간 우선) · 가중치 재배분 3안 · **서식 보존 JSON 편집** |
| **파생물 잠금** | `derived.lock.json`. 생성물이 낡으면 경고 — **기록이 없으면 낡은 것으로 본다** |
| **파급 분석** | 변경 → 무효화 범위 · 프리픽스 변경 · 구조 해시 변경(재배포) · 재생성 목록 |
| **설명 카드** | 아키타입·플랜·인터럽트·인스턴스를 한국어 markdown 한 장으로. **LLM·시각·난수 없음** |

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

# 2) 마스터데이터 검증만 (V0~V15). --format json 이면 기계가 읽는 출력 + 수정 힌트
dotnet run --project src/Npc.Host -- validate --masterdata ./masterdata

# 3) LLM 없이 구동 — 폴백 플랜만으로 NPC 500마리
dotnet run -c Release --project src/Npc.Host -- \
    --loopback --npcs 500 --time-scale 600 --days 7 --no-llm

# 4) 로컬 추론 기동 (별도 터미널)
.\tools\dotllm\dotllm.exe serve --model .\models\Qwen3-8B-Q4_K_M.gguf --port 8080 --gpu-layers 99

# 5) 플랜 프리베이크 (외부 API)
#    --concurrency 는 AIMD 초기값이다. 8 에서 시작해 올린다 —
#    "동시 32" 는 상위 계획의 추정이고 실측이 아니다 (docs/reference_metrics.html §04)
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
| `--link null\|record\|replay\|loopback\|tcp` | 링크 구현체 교체. `tcp` 는 실제 게임서버에 붙는다 (P6) |
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
| `--bind <addr>` | 웹 호스트 바인드 주소 (기본 `127.0.0.1`). `0.0.0.0` 은 `NPC_ADMIN_TOKEN` 이 있을 때만 |
| `--config <path>` | 설정 파일. 안 주면 `npc.settings.json` 을 실행 파일·작업 폴더에서 찾는다 |
| `--profile dev\|service` | 실행 프로파일. `service` 는 `--days 0` 과 스냅샷을 강제한다 |
| `--snapshot-dir <dir>` | NPC 상태 스냅샷 디렉터리 (기본 `./state`) |
| `--snapshot-interval-s N` | 스냅샷 주기 초. `0`=끔. **이 값이 상태 손실 창의 상한이다** |
| `--snapshot-keep N` | 보존할 스냅샷 수 (기본 3). 최신 것이 깨졌을 때 물러날 자리다 |
| `--restore auto\|none\|<path>` | 복원 정책 (기본 `auto`). 해시가 안 맞으면 시드로 기동한다 |
| `--shutdown-timeout-s N` | 정상 종료 예산 초 (기본 15). 넘기면 **종료 코드 2** |
| `--tick-sync-stall-s N` | `TickSync` 가 멈춰도 되는 상한 초. `0`=끔 (기본 5) |
| `--otlp-endpoint <url>` | OpenTelemetry 수집기 주소 |
| `--prometheus` | `/metrics/prometheus` 를 연다 (`--profile service` 는 자동) |
| `--alarm-webhook <url>` | 경보 웹훅 (Slack/Teams 호환 JSON) |
| `--alarm-cooldown-s N` | 같은 경보의 재발화 간격 초 (기본 300) |
| `--log-format text\|json` | 로그 형식 (기본 `text`. `--profile service` 는 `json`) |
| `--link-tls off\|tls\|mtls` | 링크 암호화 (기본 `off`) |
| `--link-cert <pfx>` | 클라이언트 인증서. `mtls` 전용 |
| `--link-tls-host <name>` | TLS SNI 이름 (기본 `--gs-host`) |
| `--require-link-auth` | 게임서버가 인증을 지원하지 않으면 거절한다 |
| `--billing-cap-usd <n>` | 벽시계 하루 비용 캡(USD). `0`=끔. 넘으면 T2 를 끊는다 |
| `--billing-reset-hour N` | 청구일이 바뀌는 UTC 시각 0~23 (기본 0) |
| `--budget-individual-share <0~1>` | 개체 재계획이 쓸 T2 예산의 몫 (기본 0.20). 넘으면 T1 대기 |

**시크릿은 환경변수로만 온다** (A-06). 인자는 `ps` 에 보이고 파일은 이미지에 굽힌다.

| 환경변수 | 무엇 |
|---|---|
| `NPC_LINK_SECRET` | 링크 HMAC 비밀. hex 64자(32바이트) |
| `NPC_LINK_CERT_PASSWORD` | `--link-cert` pfx 비밀번호 |
| `NPC_ADMIN_TOKEN` | 관리·질의 API `Bearer` 토큰. `--bind 0.0.0.0` 의 전제조건이다 |

**운영 제어** (A-11). 토큰이 설정돼 있으면 열린다. **모든 호출이 감사 로그**(`state/audit.jsonl`)에 남는다 — 누가·언제·무엇을·왜.

```bash
# 킬스위치를 끊었다가 되살린다. 예전에는 되돌릴 수 없어 복구가 재기동 = 상태 전손이었다.
curl -XPOST -H "Authorization: Bearer $NPC_ADMIN_TOKEN"   "localhost:5080/admin/killswitch?target=T2&state=on&reason=제공사+장애"
curl -XPOST -H "Authorization: Bearer $NPC_ADMIN_TOKEN"   "localhost:5080/admin/killswitch?target=T2&state=off&reason=복구됨"

# 즉시 스냅샷 (배포 직전에 한 장)
curl -XPOST -H "Authorization: Bearer $NPC_ADMIN_TOKEN"   "localhost:5080/admin/snapshot?reason=배포+전"

# 무중단 리로드 (A-07). 프로세스를 내리지 않고 고친 플랜을 올린다.
#   scope=planstore  플랜·핀만          scope=content  + interrupts.json · fallback_plans.json
curl -XPOST -H "Authorization: Bearer $NPC_ADMIN_TOKEN"   "localhost:5080/admin/reload?scope=planstore&reason=검수+반영"
```

**리로드는 트랜잭션이다.** 전부 읽고 전부 검증한 뒤에야 교체한다 — 플랜 하나라도 깨져 있으면
아무것도 안 바꾸고 사유를 응답에 담는다. 무엇이 핫·온·콜드인지는
[`docs/reference_masterdata.html` §13](docs/reference_masterdata.html) 에 표로 있다.
**프리픽스에 실리는 것**(`prompt/`·아키타입의 `desc`·`traits`)**은 전부 콜드다** — 고치면
프리픽스 SHA 가 바뀌어 플랜 스토어가 다른 회차의 것이 된다.
| `--max-speed` | 10Hz 페이싱 없이 최대 속도로. 부하·게이트 측정용 |
| `--no-dashboard` | 웹 호스트를 띄우지 않는다 |
| `--gs-host <host>` / `--gs-port N` | 게임서버 주소 (기본 `127.0.0.1:7010`). `--link tcp` 전용 |
| `--zone <id>[,<id>]` | 로스터 존 필터. **게임서버와 같아야 한다** (다르면 핸드셰이크 거절) |
| `--shard N` | 맡을 샤드 (A-08). 0=단일. `--zone` 보다 우선하며 `deploy/shards.json` 이 존 목록을 정한다 |
| `--shards <path>` | 샤드 정의 파일 (기본 `deploy/shards.json`) |
| `--planstore <dir>` | 프리베이크된 플랜 스토어 (기본 `./planstore`). 없으면 폴백 40개로 돈다 |
| `--weights A\|B\|C\|D` | 재계획 점수 가중치 세트 (기본 B. A/B 결과는 `reference_metrics.html` §11) |
| `--scan-cap N` | 인지 스캔 틱당 상한. 0=해제. **측정 전용** |
| `--dev-control` | `NPC_ADMIN_TOKEN` 없이도 `/admin/*` 을 연다 (기본 꺼짐. 데모용) |
| `--watch` | `planstore/`·`masterdata/` 를 감시해 자동 리로드 (기본 꺼짐). **개발용이다** |
| `--on-link-fault exit\|wait` | 링크가 `Faulted` 로 가면 어떻게 할까 (기본 `exit` → 종료 코드 3) |
| `--fault-grace-s N` | `Faulted` 진입 후 종료까지 유예 초 (기본 5) |
| `--live-stall-s N` | `/healthz/live` 가 허용하는 루프 정지 초 (기본 30) |
| `--ready-tick-stall-s N` | `/healthz/ready` 가 허용하는 틱 정지 초 (기본 10) |
| `--health-port N` | 프로브 전용 포트. `--no-dashboard` 와 함께 쓰면 프로브 세 라우트만 뜬다 |

**설정 소스는 세 겹이다** (A-04). 우선순위 **CLI > 환경변수 > 설정 파일 > 기본값**.

- 환경변수 이름은 옵션 이름에서 도출한다 — `--gs-host` 는 `NPC_GS_HOST`, `--time-scale` 은 `NPC_TIME_SCALE`.
  스위치는 `1`·`true`·`yes`·`on` 이면 켜진다.
- 설정 파일은 평면 JSON 이고 키는 옵션 이름에서 앞의 `--` 를 뺀 것이다.
  `profiles.<이름>` 절을 두면 `--profile` 로 고른 절이 최상위를 덮는다.
  **모르는 키는 기동 실패다** — 오타 난 키를 조용히 무시하면 "설정했는데 안 먹는다" 가 된다.

```json
{
  "npcs": 500,
  "time-scale": 60,
  "profiles": {
    "service": { "link": "tcp", "npcs": 5000, "bind": "0.0.0.0", "health-port": 5081 }
  }
}
```

---

## 배포

```bash
# 데모 한 벌 (게임서버 대역 + NPC 서버 + Prometheus + Grafana)
docker compose -f deploy/compose.yaml up --build
# → http://localhost:5080/dashboard · http://localhost:3000 (Grafana)

# 이미지만
docker build -f deploy/Dockerfile -t npc-server:dev .

# 쿠버네티스 (시크릿을 먼저 만든다)
kubectl create secret generic npc-server-secrets \
  --from-literal=admin-token="$(openssl rand -hex 32)" \
  --from-literal=link-secret="$(openssl rand -hex 32)"
kubectl apply -f deploy/k8s/deployment.yaml
```

| 파일 | 무엇 |
|---|---|
| `deploy/Dockerfile` | NPC 서버. 멀티스테이지 · 비루트 · `HEALTHCHECK` → `/healthz/live` |
| `deploy/Dockerfile.testgameserver` | 게임서버 **대역**. 데모·적합성 시험용이지 운영 이미지가 아니다 |
| `deploy/compose.yaml` | `run_demo.ps1` 의 컨테이너판 |
| `deploy/k8s/` | Deployment(프로브 3종·시크릿·PVC) · Service · ConfigMap |
| `deploy/prometheus.yml` · `deploy/grafana/` | 스크레이프 설정 · 대시보드 |

**CI 워크플로 파일은 두지 않는다.** 이 저장소는 CI 제공자를 고르지 않았다 — 파이프라인이
해야 할 일은 `build.ps1` 한 줄과 아래 세 명령으로 정의되어 있고, 사내 CI 든 GitHub Actions 든
그것을 부르면 된다. 없는 워크플로 파일을 두면 "CI 가 있다" 는 거짓 신호가 된다.

```powershell
.\build.ps1                                              # 빌드 · 스타일 · 테스트(CI 기본 필터)
dotnet run --project tools/Npc.Cli -- validate            # 마스터데이터 V1~V13 + 로더
dotnet run --project tools/Npc.Cli -- regen --check       # 파생물이 낡았으면 비0 으로 끝난다
```

**이미지에 넣는 것과 넣지 않는 것.** `masterdata/`·`planstore/pinned/`·`manifest.json` 은 들어간다.
`planstore/plans/` 는 생성물이라 볼륨이고, `state/`(스냅샷)도 볼륨이다.
**dotLLM 은 넣지 않는다** — GPLv3 경계라 별도 배포다 (CLAUDE.md §2.7).

**버전.** `MAJOR.MINOR.PATCH` 의 기본값은 `Directory.Build.props` 의 `VersionPrefix` 이고,
릴리스는 빌드 시 `-p:VersionPrefix=<태그>` 로 덮어쓴다.
`/status.version` 은 거기에 마스터데이터 `content_hash` 앞 8자리를 붙인다 —
같은 바이너리라도 다른 콘텐츠면 다른 버전이다.

---

## 도구

전부 **잎**이다 — 서버가 도는 데 필요 없고, 사람과 LLM 이 쓴다.

### `npc` — 마스터데이터·플랜 CLI

```powershell
# 저장소에서 바로
dotnet run --project tools/Npc.Cli -- <명령>

# 또는 전역 도구로 설치해 `npc` 한 단어로 (콘텐츠 담당자에게는 이쪽을 준다)
dotnet pack -c Release tools/Npc.Cli
dotnet tool install --global --add-source ./artifacts/tool Npc.Cli
npc --help
```

| 명령 | 무엇 | `--json` |
|---|---|---|
| `validate` | V1~V13 + 로더 + 파생물 신선도. **로더까지 돌린다** — 규칙만 통과하고 참조가 깨진 상태를 통과로 내지 않는다 | ✓ |
| `explain archetype\|action\|poi\|item\|flag\|interrupt <id>` | 정의 하나가 무엇인지. `flag` 는 **누가 세우고 누가 요구하는지**까지 | — |
| `card archetype <id>` | 아키타입 카드 — 인구/정원(V10) · 근무 시간 · 성향 · 레시피 · 허용/미허용 액션 · **걸릴 인터럽트** · 폴백 하루 | — |
| `card npc <첨자>` · `card roster <id>` | 개체 카드(집·일터·거리·근무 허가) · 아키타입별 인스턴스 구간 | — |
| `timeline archetype <id>` | 24시간 띠. 근무 시간과 폴백 스텝을 **같은 축에** 놓는다 | — |
| `hints [--out <path>]` | 검증 오류 사전 41건. `--out` 은 `docs/llm/VALIDATION.md` 를 **생성**한다 | — |
| `schema [--out <dir>]` | JSON Schema 발행 (E-02). **허용 값은 지금 마스터데이터에서 나온다** — 정적 변형(`*.base.schema.json`)도 같이 낸다 | — |
| `next-code items\|pois\|actions\|archetypes\|zones\|flags` | 다음 번호. **비트는 예약 구간부터 채운다.** 재배치 API 는 없다 | ✓ |
| `scaffold archetype <id> --from <id> --weight <w>` | 새 아키타입 초안 + **가중치 재배분 3안** + 파급표. 기본 dry-run, `--apply` 로 반영 | — |
| `diff [--base <rev>]` | 바뀐 파일 → 사람 말 파급(무효화·프리픽스·구조 해시·재생성) | — |
| `regen [--check]` | 낡은 파생물. `--check` 는 **낡았으면 비0** — 파이프라인에 건다 | — |
| `plan validate <파일>` | 검증 4단 전부. 어느 단·어느 스텝·무슨 코드인지 + 수정 힌트 | — |
| `plan explain <파일>` | 스텝마다 전제가 **앞 스텝의 `grants` 로 어떻게 충족되는지** 추적 + 인벤토리 수지 | — |
| `buckets` · `pin <버킷>` | 플랜 스토어 상태 표 · 검수본을 `pinned/` 로 | — |

> `plan validate` 는 **플랜 스토어 파일과 문서 둘 다** 받는다.
> `npc plan validate planstore/plans/blacksmith@Dawn.Peace.Fair.json` 처럼 바로 준다.
>
> `repair`(C-05) · `review`(F-06) · `serve`(B-08) 는 아직 없다. 치면 **"아직 없다 — 어느
> 태스크를 기다린다"** 고 답한다 — 오타와 미구현은 다른 말이다.

### 그 밖

| 도구 | 무엇 |
|---|---|
| `tools/Npc.Prebake` | 플랜 대량 생성. `--budget-usd` 로 비용 상한 · `--resume` 로 이어서 · 실패분은 `planstore/rejected/` 에 코드와 함께 보존 |
| `tools/Npc.Narrate` | 명령 기록(`--link record`) → **사람이 읽는 하루 일지**. 틱 번호·상관 ID·플랜 id 를 남기지 않는다(블라인드 평가) |
| `tools/Npc.Conformance` | **게임서버 적합성 키트.** NPC 서버 대신 붙어 규약 7종(핸드셰이크·TickSync·시퀀스·근접·전투·명령 응답·처리량)을 관찰하고 `conformance_*.md`·`.json` 을 낸다. 종료 코드 0=합격 · 1=위반 · 2=못 붙음 |
| `tools/gen_poi_distances.cs` · `gen_npcs.cs` | 파생물 생성기. 끝나면 `derived.lock.json` 을 갱신한다 |
| `testbed/Npc.TestGameServer` | 소켓 게임서버 **대역**. 진짜 게임서버 없이 연동을 시험한다 |
| `testbed/Npc.TestClient` | WinForms 뷰어. NPC 가 실제로 어떻게 움직이는지 눈으로 본다 |

라이브러리로도 쓸 수 있다 — CLI 는 껍질이고 로직은 아래에 있다.
Studio(F-02)와 MCP 서버(E-03)가 **같은 함수**를 부를 자리다.

| 라이브러리 | 무엇 |
|---|---|
| `src/Npc.Narrative` | 설명 카드. `ArchetypeCard` · `PlanExplain` · `InterruptExplain` · `InstanceCard` |
| `src/Npc.MasterData/Authoring` | `CodeAllocator` · `WeightRebalancer` · `JsonSurgeon` · `DerivedArtifacts` · `ImpactAnalyzer` |
| `src/Npc.MasterData/Validation` | `MasterDataValidator`(V1~V13) · `FixHints` · `ValidationJson` |

---

## MMO 개발에 LLM 을 붙일 때 — 어떻게 지시하는가

**이 서버를 쓰는 LLM 은 두 종류다.** 헷갈리면 잘못된 규칙을 주게 된다.

| 누구 | 무엇을 하나 | 어디서 도나 |
|---|---|---|
| **① 런타임 LLM** | NPC 의 **행동 플랜**을 만든다. 서버가 프롬프트를 조립하고 부른다 | `Npc.Llm` (T1 로컬 · T2 외부) |
| **② 개발 에이전트** | 사람 대신 **콘텐츠와 코드를 고친다**. 터미널에서 `npc` 를 부른다 | Claude Code · Copilot · 사내 에이전트 |

①은 이미 배선되어 있다 — 프롬프트도 검증도 예산도 코드 안에 있고, 지시할 것이 없다.
**아래는 ②에 대한 이야기다.**

### 원칙 셋 — 이것만 지키면 나머지는 도구가 막는다

> **1. 코드가 아니라 데이터를 고치게 한다.**
> "대장장이가 밤에도 일하게 해줘" 는 `archetypes.json` 의 `duty_hours` 문제이지 C# 문제가 아니다.
> 에이전트가 `src/` 를 열기 시작하면 대개 방향이 틀렸다.
>
> **2. 판정을 에이전트에게 맡기지 않는다.**
> "이 플랜 괜찮아?" 를 LLM 에게 묻지 말고 `npc plan validate` 를 돌리게 한다.
> 4단 검증기가 답이고, 에이전트의 의견은 답이 아니다.
>
> **3. 되돌릴 수 없는 것은 사람이 한다.**
> `code`·`bit` 재배치, 프리베이크 실행(비용), `planstore/pinned/` 수정.
> 도구가 그 셋을 **API 로 막아** 두었지만, 지시에도 적어 둔다.

### 온보딩 팩 — `docs/llm/`

에이전트에게 줄 것이 파일로 있다. 아래 시스템 프롬프트는 그 요약이다.

| 파일 | 무엇 |
|---|---|
| `docs/llm/SKILL.md` | 에이전트 스킬 정의. `.claude/skills/npc-server/` 에도 링크돼 있다 |
| `docs/llm/CONTEXT.md` | **3,000토큰 압축 컨텍스트.** 첫 메시지에 그대로 붙여 넣는다 |
| `docs/llm/RECIPES/` | 작업별 절차 11종 (전제 → 순서 → **확인** → 되돌리기 → 파급) |
| `docs/llm/ANTIPATTERNS.md` | 실수 30건 — 증상 · 확인 명령 · 올바른 방법 |
| `docs/llm/GLOSSARY.md` | 용어 |
| `docs/llm/PROMPTS.md` | 사람이 던지는 **요청 템플릿** 6종 + 나쁜 요청/좋은 요청 |
| `docs/llm/VALIDATION.md` | 검증 코드 → 무엇을 하면 되는가 (생성물) |

### 그대로 써도 되는 시스템 프롬프트

```text
너는 LlmNpcServer 저장소에서 MMO 의 NPC 콘텐츠를 만드는 개발 에이전트다.

## 먼저 읽는다
- CLAUDE.md          절대 규칙. 위반하면 리뷰 반려다
- CODEMAP.md         무엇을 하려면 어디를 여는가 (src 를 통째로 훑지 않는다)
- docs/reference_masterdata.html   스키마·검증 규칙 전문
- docs/llm/VALIDATION.md           검증 코드 → 무엇을 하면 되는가

## 작업 순서 — 이 순서를 지킨다
1. `npc card archetype <id>` 로 지금 정의가 무엇인지 먼저 본다.
   추측하지 않는다. 카드에 인구·정원·근무 시간·허용 액션·걸릴 인터럽트가 다 있다.
2. 고칠 파일을 CODEMAP.md 에서 찾는다.
3. 번호가 필요하면 `npc next-code <파일>` 에게 묻는다. 눈으로 세지 않는다.
4. 고친다. JSON 서식(들여쓰기·키 순서)을 보존한다.
5. `npc validate --json` 을 돌린다. 위반이 나오면 `fix_hint` 대로 고친다.
6. `npc diff` 로 파급을 확인하고 **사람에게 보고한다**.
7. `npc regen --check` 가 낡음을 보고하면 어떤 생성기를 돌려야 하는지 알려 준다.

## 절대 하지 않는다
- `code`·`bit` 번호 재배치 — 프리베이크된 플랜 2,880개가 통째로 깨진다. 추가는 뒤에만
- 액션·플래그·아키타입을 C# 에 하드코딩 — masterdata/ 가 단일 원천이다
- 플레이어가 쓴 문자열(캐릭터명·채팅·길드명)을 프롬프트에 넣기 — 인젝션이다
- 일일 토큰 캡을 우회하는 코드 — 초과 시 T2 → T1 강등 → 거절이 정답이다
- `#pragma warning disable` 로 경고 억제 — TreatWarningsAsErrors 다. 고친다
- 검증을 건너뛴 플랜을 런타임에 올리기
- 프리베이크 실행(비용 발생) · `planstore/pinned/` 수정 — 사람 승인 없이 하지 않는다

## 판정은 도구가 한다
"괜찮아 보인다" 라고 말하지 않는다. 종료 코드로 답한다:
  npc validate            0 = 통과
  npc plan validate <f>   0 = 4단 전부 통과
  npc regen --check       0 = 파생물 최신
  .\build.ps1             0 = 빌드·스타일·테스트·데이터 전부 통과

## 모르면 멈춘다
근거가 불충분하거나 문서와 코드가 어긋나면 임의로 코드에 맞추지 말고 멈추고 보고한다.
```

### 자주 시키는 일 — 지시문 예시

| 시키는 일 | 이렇게 말한다 | 에이전트가 부를 것 |
|---|---|---|
| 새 직업 추가 | "양봉가를 추가한다. 목동을 베끼고 인구 비중 0.4 %. **dry-run 으로 파급부터 보여 달라**" | `npc scaffold archetype … ` → `npc validate` → `npc diff` |
| 성격 조정 | "위병을 더 겁 많게. `courage` 를 낮추면 어느 인터럽트가 빠지는지 같이 알려 달라" | `npc card archetype town_guard` (걸릴 인터럽트 표) |
| 플랜이 반려됐다 | "이 반려 플랜이 왜 떨어졌는지 스텝 단위로 설명하고 고쳐 달라" | `npc plan validate` → `npc plan explain` |
| 근무 시간 문제 | "위병이 밤에 자는 것 같다. 근무 시간과 폴백이 맞는지 봐 달라" | `npc timeline archetype town_guard` |
| 마스터데이터 리뷰 | "내 브랜치가 무엇을 무효화하는지, 재배포가 필요한지 알려 달라" | `npc diff --base main` |
| 검증 실패 | "`V10` 이 떴다. 고쳐 달라" | `docs/llm/VALIDATION.md` + `npc validate --json` 의 `fix_hint` |

### 기계가 읽는 출력 — 에이전트에게 이것만 주면 된다

```powershell
npc validate --json
```

```json
{
  "ok": false,
  "master_data": "C:\game\LlmNpcServer\masterdata",
  "content_hash": "f37c0292…",
  "structural_hash": "a7dd5c98…",
  "violations": [
    {
      "code": "V10",
      "file": "pois.json",
      "path": "/pois",
      "message": "pois.json: 일터 'apiary' 정원 합이 12 인데 그 일터를 쓰는 아키타입 인구는 20 다.",
      "fix_hint": "POI 정원이 인구보다 적다. 그 종류의 POI 를 늘리거나 capacity 를 올린다 — 정원이 모자라면 그 아키타입 일부가 일터를 못 얻는다.",
      "related": ["docs/reference_masterdata.html#v10"]
    }
  ],
  "skipped": [ { "code": "V9", "reason": "…" } ]
}
```

**한글을 escape 하지 않는다.** LLM 도 사람도 같은 것을 읽는다.
**필드는 `snake_case` 다** — 마스터데이터 JSON 이 전부 그래서, 검증 출력만 다르면
에이전트가 두 표기를 오간다.
`Npc.Host validate --format json` 도 **같은 형식**을 낸다 — 껍질이 둘이어도 답은 하나다.

### 게임서버 팀에게 줄 것

NPC 서버를 붙이는 쪽(게임서버)이 알아야 할 것은 **연동 계약 하나**다.

| 주는 것 | 무엇 |
|---|---|
| [`docs/reference_link.html`](docs/reference_link.html) | N1~N8 · 패킷 · 와이어 · 핸드셰이크 · 인증. **읽지 않고 붙이지 않는다** |
| `testbed/Npc.TestGameServer` | 우리 쪽 게임서버 **대역**. 자기 구현과 비교할 기준 |
| 구조 해시 | 핸드셰이크에서 **완전 일치**를 요구한다. 불일치면 거절이다 |

에이전트에게는 이렇게 말한다 — **"`Npc.Contracts` 는 5파일 472줄이다. 통째로 읽고 시작해라."**

### 아직 없는 것 — 지시문에 넣지 않는다

이것들을 전제로 지시하면 에이전트가 없는 도구를 부르다 헤맨다.

| 없는 것 | 무엇을 대신 쓰나 | 로드맵 |
|---|---|---|
| MCP 서버 | `npc … --json` 을 셸로 부른다 | E-03 |
| 웹 편집기(Studio) | `npc scaffold` dry-run + 사람 리뷰 · VS Code 는 스키마·스니펫이 있다 | F-02 |
| 대화 생성 | **없다.** 이 서버는 행동 플랜만 만든다 | D-01 |
| 세력 테이블 | 패킷의 `Faction` 은 **통과만** 한다 — 값을 정의하는 마스터데이터가 없다 | D-04 |

---

## 프로젝트 구조

```
src/
  Npc.Contracts/    게임서버 연동 IF + 패킷 DTO        (외부 의존 0)
  Npc.Core/         플랜 DSL · 4단 검증기 · WorldFlags  (외부 의존 0)
  Npc.MasterData/   로더 · 검증 V1~V13 · 읽기전용 인덱스 · Authoring/(편집 안전장치)
  Npc.Runtime/      틱 스케줄러 · 플랜 실행기 · 인지 LOD
  Npc.Planning/     플랜 캐시 · 버킷터 · 우선순위 재계획 큐
  Npc.Llm/          IChatClient 어댑터 · 프롬프트 조립 · 3-티어 라우터
  Npc.Wire/         링크의 전송 표현 (MemoryPack DTO + 프레임 코덱. V1/ 동결 · V2/ 확장 슬롯)
  Npc.Gateway/      IGameServerLink 구현체 (Loopback/Null/Recording/Replay/Tcp)
  Npc.Narrative/    정의 설명 카드 (아키타입·플랜·인터럽트·인스턴스 → markdown)
  Npc.Sim/          헤드리스 월드 = 게임서버 대역
  Npc.Host/         ASP.NET 호스트 · 메트릭 · 대시보드
testbed/            테스트 베드 — 단방향 잎(아무도 참조하지 않는다)  → testbed/README.md
  Npc.TestBed.Protocol/  클라이언트 프로토콜 (게임서버 ↔ 클라이언트)
  Npc.TestGameServer/    게임서버 대역 프로세스 (소켓 :7010 링크 · :7020 클라이언트)
  Npc.TestClient/        WinForms 클라이언트 (net10.0-windows)
  scenarios/             데모 시나리오 3종 · run_demo.ps1
tools/
  Npc.Cli/          `npc` CLI — 검증·설명·편집·플랜 (F-01)
  Npc.Prebake/      프리베이크 CLI
  Npc.Narrate/      기록 → 하루 일지 · `card`·`explain` 서브커맨드(Npc.Narrative 껍질)
  Npc.Conformance/  게임서버 적합성 키트 — 규약 7종 관찰 + 보고서 (B-07)
  *.cs              파일 기반 .NET 앱 (gen_npcs · gen_poi_distances · report_scale · …)
                    gen_* 는 `#:project` 로 Npc.MasterData 를 참조해 derived.lock.json 을 갱신한다
  *.ps1             측정·검수 스크립트 (run_load · run_weight_ab · review · pin_plan)
masterdata/         마스터데이터 11종  → docs/reference_masterdata.html
deploy/             Dockerfile · compose · k8s · Prometheus/Grafana  → README §배포
planstore/          프리베이크 플랜 (plans/ 는 gitignore, pinned/ 는 버전관리)
state/              NPC 상태 스냅샷 (gitignore. --snapshot-dir)
scenarios/          시나리오 스크립트 (jsonl)
tests/Npc.Tests/    단위 · 골든 · 부하 · 장애주입
docs/               설계 사양 (아래)
```

---

## 문서

> **코드를 이해하려고 왔다면 [`docs/book/`](docs/book/index.html) 부터 연다.**
> 사양 문서(`docs/0x`·`docs/1x`)는 *무엇을 왜 만드는가*를 정한 문서이고,
> 책은 *이미 만들어진 코드를 앞에 두고* 답한다 — 이 파일은 왜 여기 있고, 이 상수는 왜 이 값이며,
> 이 값을 바꾸면 무엇이 어떻게 달라지는가.

**읽는 순서**

```
1. docs/index.html              전체 안내 — 아키텍처 · 동작 · 빌드 · 실행
2. docs/tutorial/index.html     활용 실습서 — 직접 돌리고 만들어 보려면 여기부터
   docs/book/index.html         코드 이해와 활용 안내서 (13장)
3. docs/reference_link.html     게임서버에 붙일 때 — 계약 전문
   docs/reference_masterdata.html  콘텐츠를 늘릴 때 — 스키마 전문
4. LLM_NPC_Server_Plan.md       왜 이 구조인가 (판단 근거 · 리스크)
```

> **구현이 끝난 제품이다.** 주차별 작업 지시서와 단계별 설계 사양은 구현 완료로 전부 삭제했다
> (2026-08-06). 원문이 필요하면 `git log --diff-filter=D -- docs/ TASKS.md` 에서 꺼낸다.
> 코드 주석에 남아 있는 `docs/NN §M` 참조는 그 시점의 근거를 가리키는 이력이고,
> 지금은 아래 레퍼런스 HTML 이 대응한다.

| 문서 | 내용 |
|---|---|
| [`docs/index.html`](docs/index.html) | **한 장짜리 안내서.** 아키텍처 · 용도 · 빌드 · 실행 · 실측 결과. 브라우저로 파일을 그대로 열면 된다 |
| [`docs/book/index.html`](docs/book/index.html) | **코드 이해와 활용 안내서 (13장).** 왜 이 구조인가 → 계약 → 런타임 → 플랜 생성 → 설정·실측. **코드를 읽거나 고쳐야 하면 여기부터** |
| [`docs/tutorial/index.html`](docs/tutorial/index.html) | **활용 실습서 (6부 21장 + 부록).** 실행 한 줄 → 콘텐츠 추가 → 내 게임서버 붙이기 → LLM 켜기 → 부하·테스트. 장마다 예제(`samples/` 24종)와 확인 절차가 붙고, **실린 수치는 전부 실제로 돌려 얻은 것**이다. **직접 만들어 보려면 여기부터** |
| [`docs/reference_link.html`](docs/reference_link.html) | **게임서버 연동 계약 ★** N1~N8 · 패킷 · 와이어 프로토콜 · 핸드셰이크 · **게임서버가 지켜야 할 발행 규약**. 연동 팀에 그대로 건넬 수 있다 |
| [`docs/reference_masterdata.html`](docs/reference_masterdata.html) | **마스터데이터 레퍼런스 ★** 월드 플래그 43 · 액션 37 · 아키타입 40 · 버킷 2,880 · 검증 V1~V13 · 작성 순서 |
| [`docs/reference_metrics.html`](docs/reference_metrics.html) | **실측 데이터.** 런타임 성능 · 스케일 · LLM 지연 · 캐시 · 비용 · 프리베이크 · 플랜 품질 · 수용 기준 판정 · **미측정으로 남은 것** |
| [`docs/FAQ.html`](docs/FAQ.html) | 도입 이점 · 적합한 범위와 한계 · 행동 플랜 준비 · 전투 반응 설계 · NPC 대화 확장 |
| [`docs/startup_flow.html`](docs/startup_flow.html) | 기동 흐름 시각화 — 무엇이 어떤 순서로 조립되는가 |
| [`docs/testbed_guide.html`](docs/testbed_guide.html) | **게임서버 연동 테스트 안내서.** 아키텍처 그림 · 핸드셰이크·틱 루프 애니메이션 · 무엇을 바꾸며 테스트하나 · 코드 분석 순서 |
| [`testbed/README.md`](testbed/README.md) | **데모 띄우는 법** — 한 줄 실행 · 포트 · 화면 보는 법 · 알려진 한계 |
| [`LLM_NPC_Server_Plan.md`](LLM_NPC_Server_Plan.md) | 상위 계획 · 타당성 판단 · 아키텍처 · 비용 분석 · 리스크 대장 |
| [`PRODUCTION_ROADMAP.md`](PRODUCTION_ROADMAP.md) | **상용 투입 로드맵.** 상용 결손 진단 · 태스크 50건(체크리스트) · 구현 방법 · LLM 온보딩 · NPC 정의 툴 |
| [`docs/measurements/`](docs/measurements/) | 실측 **원자료** (jsonl · csv). 보고서는 `reference_metrics.html` 로 옮겼다 |
| [`docs/security/threat_model.md`](docs/security/threat_model.md) | **위협 모델.** 자산 · 신뢰 경계 · 위협 T1~T15 와 대응 · 실측 · **잔여 위험** |
| [`docs/security/secrets.md`](docs/security/secrets.md) | **시크릿.** 환경변수 목록 · 회전 절차 · 유출 대응. **무중단 회전은 없다** — 회전 = 재기동 |
| [`docs/security/privacy.md`](docs/security/privacy.md) | 플레이어 id 가 남는 위치와 삭제 경로 |
| [`docs/legal/dotllm.md`](docs/legal/dotllm.md) · [`models.md`](docs/legal/models.md) | dotLLM GPLv3 배포 경계 · 모델 약관. **법무 확인은 미실시** |
| [`docs/wire/layout_v2.md`](docs/wire/layout_v2.md) | **와이어 오프셋 표 (생성물).** 다른 언어로 게임서버를 짤 때 읽는다 — 필드별 오프셋·크기·부호·엔디언·패딩 위치 |

코드를 고친다면 [`CLAUDE.md`](CLAUDE.md)(규칙)와 [`CODEMAP.md`](CODEMAP.md)(무엇을 하려면 어디를 여는가)를 먼저 읽는다.

---

## 로드맵 (12주)

| 주차 | 내용 | 게이트 | 현재 |
|---|---|---|---|
| W1 | 스파이크 | 요청당 지연 가정이 실측과 ±50% 이내 | ❌ 실측 5.14s / 가정 1.75s (**2.9배**) |
| W2–4 | 코어 · 런타임 · Sim · 마스터데이터 | **LLM 0회 호출**로 NPC 500마리 게임 7일 완주 | ✅ 8항목 전부 |
| W5–6 | 플랜 컴파일러 · 4단 검증기 | 검증 통과율 ≥ 90% | ❌ 78.3 % |
| W7–8 | 플랜 캐시 · 프리베이크 | 2,880키 콜드 필 완주, 히트율 ≥ 98% | ⚠ 히트율 **98.67 %** 통과 · 전량 회차 미실행 |
| W9–10 | 스케줄러 · 3-티어 · 부하 테스트 | NPC 5,000, 틱 p99 ≤ 20ms, GPU ≤ 60% | ⚠ p99 **0.801 ms** 통과 · GPU 미측정 |
| W11–12 | 검증 · 블라인드 평가 · 보고 | 시나리오 A/B/C 통과, 리플레이 100% 일치 | ⚠ 둘 다 통과 · 블라인드 평가 미실시 |
| (본편 밖) | **P6** 테스트 베드 — 소켓 · 게임서버 대역 · 뷰어 | NPC 서버 본체 diff = 0줄 | ⚠ 9/10 · 남은 것은 "사람이 화면 앞에 앉기" |

---

## 성능 목표 (NPC 5,000)

| 지표 | 목표 |
|---|---|
| 틱 실행 시간 p99 | ≤ 20ms (100ms 예산의 20%) |
| 인지 스캔 대상 | ≤ 150마리/틱 (전수 5,000 대비 33배 감소) |
| 플랜 캐시 히트율 | ≥ 98% |
| 플랜 검증 실패율 | ≤ 3% |
| 명령 송출 | ≥ 2,000 cmd/s, 틱 루프 **`bytesPerTick` = 0** |
| GPU 사용률 | ≤ 60% |
| 프롬프트 캐시 적중률 | ≥ 95% |

---

## 범위 밖 (이번 R&D)

- 게임 클라이언트 / 렌더링 — 대시보드와 로그로만 관측
- NPC 대사 자연어 생성 — `Speak(DialogueId)`로 ID만 송출
- 전투 AI · 패스파인딩 — 게임서버 소관. 명령만 발행
- 멀티 월드 — 단일 월드 5,000 NPC. **샤딩은 1단계(정적)까지 구현됐다** (A-08 · `--shard N` · `deploy/shards.json`). 존 간 핸드오프(2단계)는 미구현

---

## 라이선스 주의

**dotLLM은 GPLv3다.**

- ✅ **별도 프로세스 + HTTP(OpenAI 호환 엔드포인트)로만 사용한다**
- ❌ NuGet 패키지를 프로젝트에 참조해 인프로세스로 임베딩하지 않는다

프로세스 분리는 라이선스 경계 외에도 GC 격리·크래시 격리·엔진 교체 용이성이라는 실익이 있다. 상용 전환을 검토하는 시점에 법무 확인을 별도로 건다.

본 저장소 자체의 라이선스는 사내 정책에 따른다.
