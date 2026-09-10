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
| **실행 바이너리** | `Npc.Host`(NPC 서버 — 게임서버 대역·리플레이는 `--link` 로 갈아끼운다) · `Npc.Prebake`(플랜 생성 CLI) · `Npc.Narrate`(플랜을 사람 말로) · `Npc.TestGameServer`·`Npc.TestClient`(P6) |
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
```
| `--max-speed` | 10Hz 페이싱 없이 최대 속도로. 부하·게이트 측정용 |
| `--no-dashboard` | 웹 호스트를 띄우지 않는다 |
| `--gs-host <host>` / `--gs-port N` | 게임서버 주소 (기본 `127.0.0.1:7010`). `--link tcp` 전용 |
| `--zone <id>[,<id>]` | 로스터 존 필터. **게임서버와 같아야 한다** (다르면 핸드셰이크 거절) |
| `--planstore <dir>` | 프리베이크된 플랜 스토어 (기본 `./planstore`). 없으면 폴백 40개로 돈다 |
| `--weights A\|B\|C\|D` | 재계획 점수 가중치 세트 (기본 B. A/B 결과는 `reference_metrics.html` §11) |
| `--scan-cap N` | 인지 스캔 틱당 상한. 0=해제. **측정 전용** |
| `--dev-control` | `NPC_ADMIN_TOKEN` 없이도 `/admin/*` 을 연다 (기본 꺼짐. 데모용) |
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
| `.github/workflows/ci.yml` | PR·푸시 — 빌드 · 스타일 · 테스트(리눅스/윈도) · 마스터데이터 검증 · 이미지 |
| `.github/workflows/nightly.yml` | 야간 — Load · FaultInjection · 실측 diff |
| `.github/workflows/release.yml` | 태그 — 버전 주입 · 이미지 · SBOM · `planstore/pinned` 아티팩트 |

**이미지에 넣는 것과 넣지 않는 것.** `masterdata/`·`planstore/pinned/`·`manifest.json` 은 들어간다.
`planstore/plans/` 는 생성물이라 볼륨이고, `state/`(스냅샷)도 볼륨이다.
**dotLLM 은 넣지 않는다** — GPLv3 경계라 별도 배포다 (CLAUDE.md §2.7).

**버전.** `MAJOR.MINOR.PATCH` 는 태그가 정하고 CI 가 `-p:VersionPrefix` 로 주입한다.
`/status.version` 은 거기에 마스터데이터 `content_hash` 앞 8자리를 붙인다 —
같은 바이너리라도 다른 콘텐츠면 다른 버전이다.

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
  Npc.Narrate/      플랜을 사람 말로 풀어 주는 도구
  *.cs              파일 기반 .NET 앱 (gen_npcs · gen_poi_distances · report_scale · …)
  *.ps1             측정·검수 스크립트 (run_load · run_weight_ab · review · pin_plan)
masterdata/         마스터데이터 11종  → docs/reference_masterdata.html
deploy/             Dockerfile · compose · k8s · Prometheus/Grafana  → README §배포
.github/workflows/  CI · 야간 · 릴리스
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
| [`docs/reference_masterdata.html`](docs/reference_masterdata.html) | **마스터데이터 레퍼런스 ★** 월드 플래그 42 · 액션 37 · 아키타입 40 · 버킷 2,880 · 검증 V1~V11 · 작성 순서 |
| [`docs/reference_metrics.html`](docs/reference_metrics.html) | **실측 데이터.** 런타임 성능 · 스케일 · LLM 지연 · 캐시 · 비용 · 프리베이크 · 플랜 품질 · 수용 기준 판정 · **미측정으로 남은 것** |
| [`docs/FAQ.html`](docs/FAQ.html) | 도입 이점 · 적합한 범위와 한계 · 행동 플랜 준비 · 전투 반응 설계 · NPC 대화 확장 |
| [`docs/startup_flow.html`](docs/startup_flow.html) | 기동 흐름 시각화 — 무엇이 어떤 순서로 조립되는가 |
| [`docs/testbed_guide.html`](docs/testbed_guide.html) | **게임서버 연동 테스트 안내서.** 아키텍처 그림 · 핸드셰이크·틱 루프 애니메이션 · 무엇을 바꾸며 테스트하나 · 코드 분석 순서 |
| [`testbed/README.md`](testbed/README.md) | **데모 띄우는 법** — 한 줄 실행 · 포트 · 화면 보는 법 · 알려진 한계 |
| [`LLM_NPC_Server_Plan.md`](LLM_NPC_Server_Plan.md) | 상위 계획 · 타당성 판단 · 아키텍처 · 비용 분석 · 리스크 대장 |
| [`PRODUCTION_ROADMAP.md`](PRODUCTION_ROADMAP.md) | **상용 투입 로드맵.** 상용 결손 진단 · 태스크 50건(체크리스트) · 구현 방법 · LLM 온보딩 · NPC 정의 툴 |
| [`docs/measurements/`](docs/measurements/) | 실측 **원자료** (jsonl · csv). 보고서는 `reference_metrics.html` 로 옮겼다 |

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
- 멀티 월드 / 샤딩 — 단일 월드 5,000 NPC

---

## 라이선스 주의

**dotLLM은 GPLv3다.**

- ✅ **별도 프로세스 + HTTP(OpenAI 호환 엔드포인트)로만 사용한다**
- ❌ NuGet 패키지를 프로젝트에 참조해 인프로세스로 임베딩하지 않는다

프로세스 분리는 라이선스 경계 외에도 GC 격리·크래시 격리·엔진 교체 용이성이라는 실익이 있다. 상용 전환을 검토하는 시점에 법무 확인을 별도로 건다.

본 저장소 자체의 라이선스는 사내 정책에 따른다.
