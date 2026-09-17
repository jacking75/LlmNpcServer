# LlmNpcServer

> LLM으로 MMORPG NPC 행동을 자동으로 만들어 실행하는 서버 — **사내 R&D / 기술 검증 프로젝트**

## 이게 뭔가요?

MMORPG 에는 상인·경비병·몬스터 같은 NPC 가 수천 마리씩 나오는데, 이 NPC 들이 무엇을
할지를 사람이 일일이 스크립트로 정하는 대신 **LLM 이 상황에 맞게 자동으로 정해 주는
서버**다.

- **게임 서버를 대체하지 않는다.** 게임 서버 옆에 별도 프로세스로 띄워서 함께 쓴다.
- 게임 서버가 "지금 낮이다", "이 지역은 전쟁 중이다" 같은 상황을 알려주면, 이 서버는
  NPC 하나하나에게 "장사해라", "순찰해라", "숨어라" 같은 **행동 명령**을 내려 보낸다.
- LLM 은 행동을 **미리 계산해 저장**해 둔다. 실제 게임이 도는 동안에는 저장된 것을
  꺼내 쓰기만 해서, NPC 5,000마리를 초당 10번씩 움직이면서도 LLM 응답을 기다리는
  지연이 없다.
- LLM 이 아예 응답하지 않아도(장애·비용 초과·네트워크 차단) 미리 준비해 둔 대체
  행동으로 NPC 는 계속 움직인다. **LLM 이 죽어도 NPC 는 안 죽는다.**

도입 이점과 어떤 게임에 적합한지는 [`docs/FAQ.html`](docs/FAQ.html) 에 더 자세히 있다.

---

## 무엇을 할 수 있는가

**이 서버를 쓰는 사람 입장에서** 무엇이 되는지를 적는다. 구현 방식이 궁금하면
[`docs/book/`](docs/book/index.html), 수치 근거가 궁금하면
[`docs/reference_metrics.html`](docs/reference_metrics.html) 을 본다. 미구현 항목은
[`PRODUCTION_ROADMAP.md`](PRODUCTION_ROADMAP.md) 체크리스트에 있고, 아래에는 **지금
동작하고 테스트로 확인된 것만** 적는다.

### NPC 가 알아서 행동한다

| 기능 | 무엇을 해주는가 |
|---|---|
| **상황에 맞는 자동 행동** | 시간대·지역 상태(평시/전쟁 등)·날씨에 맞춰 NPC 가 무엇을 할지 스스로 정한다 — 낮엔 장사, 밤엔 귀가, 전쟁 중엔 방비 |
| **NPC 5,000마리 동시 처리** | 초당 10번, 지연 없이 다같이 움직인다. NPC 가 많아져도 화면이 버벅이거나 밀리지 않는다 |
| **돌발 상황 즉시 반응** | 플레이어가 다가오거나 공격받는 등의 사건에는 LLM 을 기다리지 않고 규칙으로 즉시 반응한다 |
| **직업·성격별 차이** | 대장장이·경비병·상인처럼 NPC 종류마다 다른 성향과 행동 범위를 가진다 |
| **시간이 흘러도 자연스럽다** | 서버를 재시작해도 게임 속 시간이 갑자기 되돌아가지 않는다 |

### 무슨 일이 있어도 멈추지 않는다

| 기능 | 무엇을 해주는가 |
|---|---|
| **LLM 장애에도 계속 동작** | LLM 이 응답을 안 하거나 아예 꺼져 있어도, 미리 준비해 둔 대체 행동으로 NPC 는 계속 움직인다 |
| **재시작 후 이어서 진행** | 서버가 죽었다 다시 켜져도 NPC 들이 하던 일을 그대로 이어간다 (상태를 주기적으로 저장해 둔다) |
| **안전한 종료** | 종료 신호를 받으면 하던 일을 정리하고 상태를 저장한 뒤에 꺼진다 |
| **이상 상황 알림** | 문제가 생기면 Slack/Teams 같은 곳으로 알림을 보낸다 |
| **재현 가능한 리플레이** | 같은 입력을 다시 넣으면 NPC 들이 이전과 똑같이 움직인다 — 버그를 재현하고 디버깅할 수 있다 |

### 게임 서버와 안전하게 연결된다

| 기능 | 무엇을 해주는가 |
|---|---|
| **정해진 연동 규약** | 게임 서버가 문서 한 장(연동 계약)대로만 붙이면 된다. 이 서버는 명령을 보내고, 결과는 게임 서버가 이벤트로 알려준다 |
| **통신 장애에 강함** | 네트워크로 보낸 명령이 유실되거나 늦게 와도 알아서 복구한다 (`--drop-rate` 로 이 상황을 상시 시험한다) |
| **중복 처리 방지** | 같은 결과가 두 번 들어와도 NPC 상태가 달라지지 않는다 |
| **여러 언어로 붙이기** | 게임 서버가 C++·Python 등 다른 언어로 짜여 있어도 붙을 수 있도록 참조 코드와 바이트 단위 예제를 제공한다 |
| **적합성 자가 진단** | 내 게임 서버가 규약을 제대로 지키는지 자동으로 점검하고 보고서를 낸다 |
| **인증·암호화** | 게임 서버와 이 서버 사이 통신을 암호화하고 신원을 확인할 수 있다 (TLS/상호 인증) |
| **플레이어 적대 대응** | 플레이어가 적대 행위를 하면 게임 서버가 알려주고, 이 서버는 그에 맞는 NPC 반응(전투 등)을 낸다 |

### LLM 사용 비용을 통제할 수 있다

| 기능 | 무엇을 해주는가 |
|---|---|
| **캐시 재사용** | 비슷한 상황의 NPC 는 같은 행동을 재사용해서, 실제 LLM 호출 횟수를 크게 줄인다 (재사용률 실측 98%대) |
| **저품질→고품질 단계적 사용** | 흔한 상황은 저렴한 로컬 모델로, 드문 상황만 고품질 외부 API 로 처리한다 |
| **하루 사용량 상한** | 하루 비용에 상한을 걸어둘 수 있다. 넘으면 자동으로 더 저렴한 방식으로 낮추거나 요청을 거절한다 — **우회 경로 없음** |
| **여러 LLM 제공사 대비** | 쓰던 LLM 서비스가 응답을 못 하면 다음 제공사로 자동 전환한다 |
| **안전한 프롬프트** | 플레이어가 직접 입력한 문자열(캐릭터명·채팅 등)은 LLM 에게 절대 보내지 않는다 — 프롬프트 인젝션 방지 |

### 콘텐츠는 코드를 몰라도 늘릴 수 있다

| 기능 | 무엇을 해주는가 |
|---|---|
| **데이터 파일로 관리** | 새 직업·장소·아이템·행동을 코드가 아니라 파일(JSON)에 추가한다 |
| **자동 검증** | 추가한 내용이 앞뒤가 맞는지(인구 수, 근무 시간, 정원 등)를 자동으로 검사하고, 문제가 있으면 무엇을 고치면 되는지까지 알려준다 |
| **전용 명령어 도구(`npc`)** | 지금 설정이 무엇인지 확인하고, 다음 번호를 자동으로 받고, 바꿨을 때 영향 범위를 미리 확인할 수 있다 |
| **틀린 채로 못 올라간다** | 검증에 실패한 콘텐츠는 서버가 아예 켜지지 않는다 — "일단 켜놓고 나중에 고치는" 상태가 없다 |

### 운영 상태를 눈으로 확인하고 제어할 수 있다

| 기능 | 무엇을 해주는가 |
|---|---|
| **실시간 대시보드** | 웹 화면 하나로 NPC 상태와 서버 지표를 확인한다 |
| **원격 관리** | 필요하면 특정 기능만 켜고 끄거나, 그 자리에서 즉시 상태 저장을 시킬 수 있다. 모든 조작은 누가·언제·왜 했는지 기록에 남는다 |
| **문제 없이 콘텐츠 반영** | 서버를 끄지 않고도 고친 NPC 콘텐츠를 반영할 수 있다. 콘텐츠 중 하나라도 문제가 있으면 아무것도 안 바꾸고 이유를 알려준다 |
| **바로 배포 가능** | Docker·Kubernetes 로 띄울 수 있는 설정을 함께 제공한다 |

### 만들고 나면 무엇이 남는가

| 구분 | 내용 |
|---|---|
| **실행 바이너리** | `Npc.Host`(NPC 서버 본체) · `npc`(콘텐츠 명령어 도구) · `Npc.Prebake`(행동 대량 생성) · 그 외 운영·연동 도구 |
| **데이터** | 마스터데이터(직업·장소·아이템 등) · 미리 생성된 행동 2,880개 |
| **관측** | 웹 대시보드 (별도 설치 없이 브라우저로 접속) |

**현재 상태**: 구현 진행 중. 어느 항목이 통과했고 무엇이 남았는지는 아래 "로드맵" 표와
[`docs/reference_metrics.html`](docs/reference_metrics.html) 의 실측 근거를 따른다 —
**미달·미측정을 통과로 적지 않는다.**

---

## 어디부터 읽어야 하나

이 문서는 **두 부분**으로 나뉜다. 나에게 필요한 쪽만 읽으면 된다 — 서로 참고할 필요는
거의 없다.

| 나는 이런 사람이다 | 여기부터 읽는다 |
|---|---|
| 이 서버를 실행해서 내 게임에 붙이거나, 일단 실행해서 써보고 싶다 | [사용자 가이드](#사용자-가이드--서버를-실행해서-쓴다) |
| 이 저장소의 코드를 고치거나, NPC 콘텐츠(직업·장소·아이템 등)를 직접 늘리고 싶다 | [개발자 가이드](#개발자-가이드--코드콘텐츠를-고친다) |

---

## 사용자 가이드 — 서버를 실행해서 쓴다

> 아래는 **코드를 고치지 않고 이 서버를 실행해서 쓰는** 사람을 위한 것이다. 개발 관련
> 내용(프로젝트 구조·의존 규칙·마스터데이터 스키마 등)은 전혀 필요 없다.

### 무엇을 준비해야 하는가

| 항목 | 요구 |
|---|---|
| OS | Windows 10/11 x64 |
| SDK | [.NET 10 SDK](https://dotnet.microsoft.com/) |
| GPU (선택) | NVIDIA, VRAM 12GB 이상 — 내 컴퓨터에서 LLM 을 직접 돌리고 싶을 때만 필요하다 |
| LLM (선택) | 로컬 [dotLLM](https://dotllm.dev/) 또는 OpenAI 호환 외부 API 중 하나 — 아예 없어도 대체 행동만으로 서버가 돈다 |

GPU 도 LLM 도 없어도 된다. 이 서버는 미리 준비된 대체 행동만으로도 완전히 동작한다 —
LLM 은 "더 다양한 행동"을 위한 선택 사항이다.

### 1) 빌드한다

```powershell
dotnet --version                     # 10.x 인지 확인
dotnet build -c Release
```

### 2) LLM 없이 먼저 돌려본다

가장 빠르게 동작을 확인하는 방법이다. NPC 500마리가 미리 준비된 행동만으로 움직인다.

```powershell
dotnet run -c Release --project src/Npc.Host -- `
    --loopback --npcs 500 --time-scale 600 --days 1 --no-llm
```

브라우저로 **http://localhost:5080/dashboard** 를 열면 NPC 들이 움직이는 것을 볼 수 있다.
`Ctrl+C` 로 종료한다.

### 3) 규모를 키우거나 이벤트를 넣어본다

```powershell
# NPC 5,000마리, 게임 7일, 공성 이벤트 시나리오 포함
dotnet run -c Release --project src/Npc.Host -- `
    --loopback --npcs 5000 --time-scale 60 --days 7 `
    --scenario ./scenarios/siege.jsonl
```

### 눈으로 직접 확인하고 싶다면 (Windows)

실제 게임 서버 없이도, 게임 서버 흉내와 뷰어 화면까지 한 번에 띄워서 NPC 가 움직이는
모습을 볼 수 있다.

```powershell
./testbed/run_demo.ps1 -Scenario siege
```

화면 보는 법과 알려진 한계는 [`testbed/README.md`](testbed/README.md) 에 있다.

### 내 게임 서버에 연결하고 싶다면

이 서버를 실제 게임 서버에 붙이려면 `--link tcp` 로 실행하고, 내 게임 서버 쪽에서 정해진
통신 규약을 구현해야 한다.

```powershell
dotnet run -c Release --project src/Npc.Host -- `
    --link tcp --gs-host 127.0.0.1 --gs-port 7010 --npcs 5000
```

- 통신 규약 전문은 [`docs/reference_link.html`](docs/reference_link.html) 한 장에 전부 있다 — **읽지 않고 붙이지 않는다.**
- 내 구현이 규약을 제대로 지키는지는 `Npc.Conformance` 도구가 자동으로 점검하고 보고서를 낸다.
- 먼저 [`testbed/`](testbed/README.md) 의 가짜 게임 서버와 붙여서 눈으로 확인해 볼 수 있다.

### LLM 을 켜서 더 다양한 행동을 쓰고 싶다면

```powershell
# 1) 로컬 LLM 을 별도 터미널에서 띄운다
.\tools\dotllm\dotllm.exe serve --model .\models\Qwen3-8B-Q4_K_M.gguf --port 8080 --gpu-layers 99

# 2) (선택) 외부 API 로 행동을 미리 대량 생성해 둔다 — 비용이 발생한다
$env:OPENROUTER_API_KEY = "..."
dotnet run -c Release --project tools/Npc.Prebake -- `
    --masterdata ./masterdata --out ./planstore `
    --tier T2 --model openrouter-gpt-5-nano --concurrency 8 --budget-usd 5.00

# 3) 서버를 티어를 켜서 실행한다
dotnet run -c Release --project src/Npc.Host -- --loopback --npcs 5000 --tier all
```

`--tier` 는 어디까지 LLM 을 쓸지 정한다 (`none`=대체 행동만 · `t1`=로컬만 · `t2`=외부까지 ·
`all`=전부). 비용을 걱정할 필요는 없다 — `--billing-cap-usd` 로 하루 상한을 걸어두면 넘는
순간 자동으로 낮은 단계로 내려간다.

### 컨테이너로 띄우고 싶다면 (Docker / Kubernetes)

```bash
# 데모 한 벌 (게임서버 흉내 + NPC 서버 + 모니터링 대시보드)
docker compose -f deploy/compose.yaml up --build
# → http://localhost:5080/dashboard · http://localhost:3000 (Grafana)

# 이미지만 만들기
docker build -f deploy/Dockerfile -t npc-server:dev .

# 쿠버네티스 (시크릿을 먼저 만든다)
kubectl create secret generic npc-server-secrets \
  --from-literal=admin-token="$(openssl rand -hex 32)" \
  --from-literal=link-secret="$(openssl rand -hex 32)"
kubectl apply -f deploy/k8s/deployment.yaml
```

| 파일 | 무엇 |
|---|---|
| `deploy/Dockerfile` | NPC 서버 이미지 |
| `deploy/compose.yaml` | 데모 한 벌을 한 번에 띄우는 설정 |
| `deploy/k8s/` | 쿠버네티스 배포 설정 |
| `deploy/prometheus.yml` · `deploy/grafana/` | 모니터링 설정 |

**dotLLM(로컬 LLM 엔진)은 이미지에 포함하지 않는다** — 라이선스(GPLv3) 때문에 별도로
설치해서 붙인다. 자세한 이유는 이 문서 맨 아래 "라이선스 주의" 참고.

### LLM(에이전트)에게 "이 서버를 실행해서 써봐" 라고 시키고 싶다면

아래 지시문을 그대로 붙여넣는다. **저장소의 코드나 콘텐츠를 고치는 지시가 아니라,
빌드된 서버를 실행하고 운영하는 지시**라는 점이 핵심이다.

```text
너는 LlmNpcServer 저장소를 실행해서 게임의 NPC 서버로 동작시키는 역할이다.
(이 저장소의 코드를 고치는 게 아니라, 빌드된 서버를 실행하고 운영하는 것이다.)

1. `dotnet build -c Release` 로 빌드한다.
2. README "사용자 가이드" 절의 명령으로, 필요한 조건(NPC 수 · LLM 사용 여부)에 맞게 실행한다.
   LLM 없이 우선 확인하려면 `--no-llm` 을 쓴다.
3. 실제 게임 서버를 붙이려면 `--link tcp` 로 실행하고, 그 게임 서버 쪽 구현은
   `docs/reference_link.html` 의 통신 규약을 그대로 따라야 한다는 것을 상대에게 알려준다.
4. 서버가 잘 도는지는 웹 대시보드(기본 http://localhost:5080/dashboard)와
   `/healthz/live` 로 확인한다.
5. 근거가 불충분하거나 무엇을 해야 할지 불확실하면 추측하지 말고 무엇이 불확실한지 말한다.
```

저장소의 **코드나 마스터데이터를 고치는** 개발 에이전트에게 줄 지시문은 이것과 다르다 —
아래 [개발자 가이드](#개발자-가이드--코드콘텐츠를-고친다)의 "MMO 개발에 LLM 을 붙일 때" 절을 대신 쓴다.

### 더 많은 실행 옵션 · 환경변수 · 관리 API

일상적으로는 위 명령만으로 충분하다. 세부 옵션이 필요할 때만 펼쳐서 본다.

<details>
<summary>전체 실행 옵션 표 보기</summary>

| 옵션 | 의미 |
|---|---|
| `--loopback` | `Npc.Sim` 인프로세스 월드에 직결 (기본) |
| `--link null\|record\|replay\|loopback\|tcp` | 링크 구현체 교체. `tcp` 는 실제 게임서버에 붙는다 |
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
| `--snapshot-interval-s N` | 스냅샷 주기 초. `0`=끔. 이 값이 상태 손실 창의 상한이다 |
| `--snapshot-keep N` | 보존할 스냅샷 수 (기본 3) |
| `--memory <dir>` | NPC 기억·관계 저장소. 없으면 꺼짐 — NPC 서버는 읽기만 한다 |
| `--memory-ttl-days N` | 기억 보존 게임 일. `0`=지우지 않음 |
| `--restore auto\|none\|<path>` | 복원 정책 (기본 `auto`). 해시가 안 맞으면 시드로 기동한다 |
| `--shutdown-timeout-s N` | 정상 종료 예산 초 (기본 15). 넘기면 종료 코드 2 |
| `--tick-sync-stall-s N` | 게임 시각 동기화가 멈춰도 되는 상한 초. `0`=끔 (기본 5) |
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
| `--budget-individual-share <0~1>` | 개체 재계획이 쓸 T2 예산의 몫 (기본 0.20) |
| `--max-speed` | 10Hz 페이싱 없이 최대 속도로. 부하 측정용 |
| `--no-dashboard` | 웹 호스트를 띄우지 않는다 |
| `--gs-host <host>` / `--gs-port N` | 게임서버 주소 (기본 `127.0.0.1:7010`). `--link tcp` 전용 |
| `--zone <id>[,<id>]` | 로스터 존 필터. 게임서버와 같아야 한다 (다르면 핸드셰이크 거절) |
| `--shard N` | 맡을 샤드. 0=단일. `--zone` 보다 우선하며 `deploy/shards.json` 이 존 목록을 정한다 |
| `--shards <path>` | 샤드 정의 파일 (기본 `deploy/shards.json`) |
| `--planstore <dir>` | 미리 생성된 행동 저장소 (기본 `./planstore`). 없으면 대체 행동 40개로 돈다 |
| `--weights A\|B\|C\|D` | 재계획 점수 가중치 세트 (기본 B) |
| `--scan-cap N` | 인지 스캔 틱당 상한. 0=해제. 측정 전용 |
| `--dev-control` | `NPC_ADMIN_TOKEN` 없이도 `/admin/*` 을 연다 (기본 꺼짐. 데모용) |
| `--watch` | `planstore/`·`masterdata/` 를 감시해 자동 리로드 (기본 꺼짐. 개발용) |
| `--on-link-fault exit\|wait` | 링크가 끊기면 어떻게 할까 (기본 `exit` → 종료 코드 3) |
| `--fault-grace-s N` | 링크 단절 후 종료까지 유예 초 (기본 5) |
| `--live-stall-s N` | `/healthz/live` 가 허용하는 루프 정지 초 (기본 30) |
| `--ready-tick-stall-s N` | `/healthz/ready` 가 허용하는 틱 정지 초 (기본 10) |
| `--health-port N` | 프로브 전용 포트 |

**설정 소스는 세 겹이다.** 우선순위 **CLI > 환경변수 > 설정 파일 > 기본값**.

- 환경변수 이름은 옵션 이름에서 도출한다 — `--gs-host` 는 `NPC_GS_HOST`, `--time-scale` 은
  `NPC_TIME_SCALE`. 스위치는 `1`·`true`·`yes`·`on` 이면 켜진다.
- 설정 파일은 평면 JSON 이고 키는 옵션 이름에서 앞의 `--` 를 뺀 것이다.
  `profiles.<이름>` 절을 두면 `--profile` 로 고른 절이 최상위를 덮는다.
  **모르는 키는 기동 실패다** — 오타 난 키를 조용히 무시하지 않는다.

```json
{
  "npcs": 500,
  "time-scale": 60,
  "profiles": {
    "service": { "link": "tcp", "npcs": 5000, "bind": "0.0.0.0", "health-port": 5081 }
  }
}
```

**시크릿은 환경변수로만 온다.** 인자는 `ps` 에 보이고 파일은 이미지에 굽힌다.

| 환경변수 | 무엇 |
|---|---|
| `NPC_LINK_SECRET` | 링크 인증 비밀. hex 64자(32바이트) |
| `NPC_LINK_CERT_PASSWORD` | `--link-cert` pfx 비밀번호 |
| `NPC_ADMIN_TOKEN` | 관리·질의 API `Bearer` 토큰. `--bind 0.0.0.0` 의 전제조건이다 |

**운영 제어.** 토큰이 설정돼 있으면 열린다. 모든 호출이 감사 로그(`state/audit.jsonl`)에
남는다 — 누가·언제·무엇을·왜.

```bash
# 킬스위치를 끊었다가 되살린다
curl -XPOST -H "Authorization: Bearer $NPC_ADMIN_TOKEN"   "localhost:5080/admin/killswitch?target=T2&state=on&reason=제공사+장애"
curl -XPOST -H "Authorization: Bearer $NPC_ADMIN_TOKEN"   "localhost:5080/admin/killswitch?target=T2&state=off&reason=복구됨"

# 즉시 스냅샷 (배포 직전에 한 장)
curl -XPOST -H "Authorization: Bearer $NPC_ADMIN_TOKEN"   "localhost:5080/admin/snapshot?reason=배포+전"

# 무중단 리로드 — 프로세스를 내리지 않고 고친 콘텐츠를 올린다
#   scope=planstore  행동 데이터만        scope=content  + 인터럽트 · 대체 행동
curl -XPOST -H "Authorization: Bearer $NPC_ADMIN_TOKEN"   "localhost:5080/admin/reload?scope=planstore&reason=검수+반영"
```

리로드는 트랜잭션이다 — 전부 읽고 전부 검증한 뒤에야 교체한다. 콘텐츠 하나라도 깨져
있으면 아무것도 안 바꾸고 사유를 응답에 담는다.

</details>

---

## 개발자 가이드 — 코드·콘텐츠를 고친다

> 이 저장소 자체의 코드를 고치거나, NPC 콘텐츠(직업·장소·아이템·행동 등)를 새로
> 만들거나 검증 규칙을 이해해야 한다면 여기부터 읽는다. 서버를 실행해서 쓰기만 할
> 거라면 이 아래는 읽지 않아도 된다.

### 먼저 읽는 문서

| 문서 | 무엇 |
|---|---|
| [`CLAUDE.md`](CLAUDE.md) | **절대 규칙.** 위반하면 리뷰 반려다. 코드를 고치기 전에 반드시 읽는다 |
| [`CODEMAP.md`](CODEMAP.md) | 무엇을 하려면 어디를 여는가 — 작업별 파일 지도 |
| [`AGENTS.md`](AGENTS.md) | 코딩 에이전트용 지침 |
| [`docs/book/index.html`](docs/book/index.html) | **코드 이해와 활용 안내서 (13장).** 코드를 읽거나 고쳐야 하면 여기부터 |
| [`docs/reference_link.html`](docs/reference_link.html) | 게임서버 연동 계약 전문 — N1~N8 · 패킷 · 와이어 · 핸드셰이크 |
| [`docs/reference_masterdata.html`](docs/reference_masterdata.html) | 마스터데이터 스키마 전문 — 플래그·액션·아키타입·버킷·검증 |
| [`docs/book/ch01.html`](docs/book/ch01.html) | 왜 이 구조인가 — 원안의 문제 · 발상 전환 · 세 겹의 방어선 |
| [`PRODUCTION_ROADMAP.md`](PRODUCTION_ROADMAP.md) | 상용 투입 로드맵 — 남은 태스크 6건 |

문서 전체 지도는 이 절 맨 아래 "문서" 표에 있다.

### 핵심 아이디어 — 왜 이런 구조인가

> **LLM을 실행기가 아니라 컴파일러로 쓴다.**

LLM은 행동 플랜을 *생성*하고, 결정론적 런타임이 그것을 *실행*한다. 생성은 느리고
비싸도 되지만, 실행은 빠르고 재현 가능해야 한다.

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

| 문제 | 해결 |
|---|---|
| 소비자 GPU 1장으로 5,000 NPC 주기 재계획 = **추정 2.4배 · 실측(8B) 7.1배 초과** | 플랜을 (아키타입 × 상황) 단위로 캐시. 개별 재계획은 예산 내에서 우선순위로 선별 |
| 장시간 실행 시 LLM 컨텍스트 관리 | **모든 호출을 무상태로.** 기억은 게임 DB에 구조체로, 요약은 규칙으로 |
| 외부 API 지연 변동성 (p99 수십 초) | LLM은 게임 크리티컬 패스에 없다. 틱 루프에 LLM 호출이 존재하지 않음 |
| LLM 장애 / 비결정성 | 플랜을 생성 시점에 저장. 캐시 → 폴백 3단 방어. 리플레이는 LLM 없이 재현 |

### 무엇을 만들고, 무엇을 안 만드는가

이 저장소에서 만드는 것은 **NPC 서버**다. **게임서버는 만들지 않는다.** 다만 NPC 서버를
혼자 돌려볼 수 없으므로 게임서버 흉내를 내는 대역(`Npc.Sim`)을 함께 만든다.

| 대상 | 이번 프로젝트 | 비고 |
|---|---|---|
| **NPC 서버** (`Npc.Host`) | ✅ 만든다 | 본체 |
| **경계 정의** (`IGameServerLink` + 패킷) | ✅ 만든다 | NPC 서버의 아웃바운드 포트 |
| **게임서버 대역** (`Npc.Sim`) | ✅ 만든다 | 실제 게임서버 없이 검증하기 위한 가짜 |
| **소켓 전송** (`Npc.Wire` + TCP 링크) | ✅ 만들었다 | 프레임 코덱 · 핸드셰이크 · 재접속 |
| **소켓 게임서버 대역 · 뷰어** (`testbed/`) | ✅ 만들었다 | 눈으로 보는 자리 |
| 실제 MMORPG 게임서버 | ❌ 안 만든다 | 이미 있거나 남이 만든다. "붙일 때 이렇게 붙는다"만 정의 |

> `IGameServerLink`의 "GameServer"는 **상대방**을 가리킨다. "게임서버의 인터페이스"가
> 아니라 **"게임서버로 향하는 NPC 서버의 링크"** 다.

### 프로젝트 구조

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
  Npc.Studio/       NPC 정의 웹 GUI — 읽기·하루 예측·폼 편집·새 직업 마법사·전체 검증
  Npc.Cli/          `npc` CLI — 검증·설명·편집·플랜
  Npc.Prebake/      프리베이크 CLI
  Npc.Narrate/      기록 → 하루 일지 · `card`·`explain` 서브커맨드(Npc.Narrative 껍질)
  Npc.Conformance/  게임서버 적합성 키트 — 규약 7종 관찰 + 보고서
  *.cs              파일 기반 .NET 앱 (gen_npcs · gen_poi_distances · report_scale · …)
  *.ps1             측정·검수 스크립트 (run_load · run_weight_ab · review · pin_plan)
masterdata/         마스터데이터 11종  → docs/reference_masterdata.html
deploy/             Dockerfile · compose · k8s · Prometheus/Grafana
planstore/          프리베이크 플랜 (plans/ 는 gitignore, pinned/ 는 버전관리)
state/              NPC 상태 스냅샷 (gitignore. --snapshot-dir)
scenarios/          시나리오 스크립트 (jsonl)
tests/Npc.Tests/    단위 · 골든 · 부하 · 장애주입
docs/               설계 사양 (아래 "문서" 표)
```

빌드·테스트·스타일 검사:

```powershell
dotnet build -c Release
dotnet test
dotnet format --verify-no-changes
.\build.ps1                                              # 빌드 · 스타일 · 테스트(CI 기본 필터)
```

**CI 워크플로 파일은 두지 않는다.** 파이프라인이 해야 할 일은 `build.ps1` 한 줄로
정의되어 있고, 사내 CI 든 GitHub Actions 든 그것을 부르면 된다.

### NPC 정의 GUI — `Npc Studio`

이 마을의 NPC 를 **읽고 · 예측하고 · 만드는** 독립 웹 도구다. NPC 서버 본체와 별도
프로세스로 실행하며, 기본 주소는 **http://127.0.0.1:25056** 이다.
아래 여섯 가지는 전부 **JSON 을 한 번도 보지 않고** 된다.

```powershell
dotnet run -c Release --project tools/Npc.Studio
```

1. **읽는다** — 직업 하나를 열어 어디서 살고, 무엇을 하고, 위험하면 어떻게 하는지를 3분 안에 읽는다.
2. **예측한다** — "하루 재생" 으로 집 → 일터 → 선술집 → 집을 지도에서 본다. 소요 시간 계산은 시뮬레이터와 같다.
3. **바꿔 본다** — 편집 탭에서 용기를 내리면 저장하기 전에 반응이 물러나기 → 도망으로 바뀌는 것을 본다.
4. **한 명만 고친다** — NPC #2326 의 순찰로를 지도에서 찍어 `npc_overrides.json` 에 넣는다.
5. **만든다** — 5단계 마법사로 새 직업을 만든다. 인구 재배분·일터 정원·하루 일과·작업 권한을 한 트랜잭션으로.
6. **안전하게 저장한다** — 연습장에서 실험하고, 저장 전 전체 검증을 돌고, 파급 패널이 다음 할 일을 시키고, 되돌릴 수 있다.

저장은 임시 작업본에 **전체 검증(V0~V14) → 실제 로더 → NPC 인스턴스 로더**를 먼저 적용한다.
하나라도 실패하면 원본 파일을 바꾸지 않는다. **무변경 저장은 바이트 동일**이다 — 바꾼
필드만 제자리에서 고치므로 diff 에 서식 변경이 섞이지 않는다. 생성물
(`npc_instances.json` · `poi_distances.bin` · `prompt/`)은 편집 목록에서 제외한다.

그 위에 안전장치가 넷 더 있다. **읽은 뒤 디스크가 바뀌면 거절한다**(다른 탭 · VS Code ·
생성기), **`code`·`bit` 재배치를 거절한다**(프리베이크 2,880건이 그 번호 위에 있다),
**여러 파일 쓰기는 한 묶음**이라 도중에 실패하면 되감고, **저장 직전 원본을 백업**해
저장 한 번을 통째로 되돌릴 수 있다.

```powershell
dotnet run -c Release --project tools/Npc.Studio -- --read-only
dotnet run -c Release --project tools/Npc.Studio -- --masterdata D:\game\masterdata --port 25057
dotnet run -c Release --project tools/Npc.Studio -- --server http://127.0.0.1:25055 --token <토큰>
dotnet run -c Release --project tools/Npc.Studio -- --planstore ./planstore --backup-root D:\bak
dotnet run -c Release --project tools/Npc.Studio -- --help
```

| 인자 | 기본값 | 의미 |
|---|---|---|
| `--masterdata` | `./masterdata` | 열 마스터데이터 폴더 |
| `--planstore` | `./planstore` | 미리 구운 계획. 없으면 **상황별** 탭을 숨긴다 |
| `--server` | 없음 | 실행 중인 NPC 서버. 없으면 라이브 관찰을 쓰지 않는다 |
| `--token` | 없음 | 대시보드 토큰 (A-06) |
| `--backup-root` | `%LOCALAPPDATA%\NpcStudio\backup` | 되돌리기 백업 위치 |
| `--bind` · `--port` | `127.0.0.1` · `25056` | 수신 주소·포트 |
| `--read-only` | 꺼짐 | 저장을 막고 조회·검증만 |

**모르는 인자는 거절한다** — `--readonly`(오타)로 띄우면 도움말을 찍고 종료 코드 2 다.

전체 사용법은 [`docs/npc_studio_manual.html`](docs/npc_studio_manual.html) 에 있다 —
초보자 시나리오 7장 + 저장 안전성 · 고급 · 문제 해결.

### 콘텐츠 CLI — `npc`

전부 **잎**이다 — 서버가 도는 데 필요 없고, 사람과 LLM 이 쓴다.

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
| `schema [--out <dir>]` | JSON Schema 발행. **허용 값은 지금 마스터데이터에서 나온다** — 정적 변형(`*.base.schema.json`)도 같이 낸다 | — |
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
> `repair`·`review`·`serve` 는 아직 없다. 치면 **"아직 없다 — 어느 태스크를 기다린다"**
> 고 답한다 — 오타와 미구현은 다른 말이다.

| 그 밖의 도구 | 무엇 |
|---|---|
| `tools/Npc.Prebake` | 플랜 대량 생성. `--budget-usd` 로 비용 상한 · `--resume` 로 이어서 · 실패분은 `planstore/rejected/` 에 코드와 함께 보존 |
| `tools/Npc.Narrate` | 명령 기록(`--link record`) → **사람이 읽는 하루 일지** |
| `tools/Npc.Conformance` | **게임서버 적합성 키트.** 규약 7종을 관찰하고 `conformance_*.md`·`.json` 을 낸다. 종료 코드 0=합격 · 1=위반 · 2=못 붙음 |
| `tools/gen_poi_distances.cs` · `gen_npcs.cs` | 파생물 생성기. 끝나면 `derived.lock.json` 을 갱신한다 |
| `testbed/Npc.TestGameServer` | 소켓 게임서버 **대역**. 진짜 게임서버 없이 연동을 시험한다 |
| `testbed/Npc.TestClient` | WinForms 뷰어. NPC 가 실제로 어떻게 움직이는지 눈으로 본다 |

라이브러리로도 쓸 수 있다 — CLI 는 껍질이고 로직은 `src/Npc.Narrative`·
`src/Npc.MasterData/Authoring`·`src/Npc.MasterData/Validation` 에 있다.

### MMO 개발에 LLM 을 붙일 때 — 어떻게 지시하는가

**이 서버를 쓰는 LLM 은 두 종류다.** 헷갈리면 잘못된 규칙을 주게 된다.

| 누구 | 무엇을 하나 | 어디서 도나 |
|---|---|---|
| **① 런타임 LLM** | NPC 의 **행동 플랜**을 만든다. 서버가 프롬프트를 조립하고 부른다 | `Npc.Llm` (T1 로컬 · T2 외부) |
| **② 개발 에이전트** | 사람 대신 **콘텐츠와 코드를 고친다**. 터미널에서 `npc` 를 부른다 | Claude Code · Copilot · 사내 에이전트 |

①은 이미 배선되어 있다 — 프롬프트도 검증도 예산도 코드 안에 있고, 지시할 것이 없다.
**아래는 ②에 대한 이야기다.**

#### 원칙 셋 — 이것만 지키면 나머지는 도구가 막는다

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

#### 온보딩 팩 — `docs/llm/`

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

#### 그대로 써도 되는 시스템 프롬프트

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

#### 자주 시키는 일 — 지시문 예시

| 시키는 일 | 이렇게 말한다 | 에이전트가 부를 것 |
|---|---|---|
| 새 직업 추가 | "양봉가를 추가한다. 목동을 베끼고 인구 비중 0.4 %. **dry-run 으로 파급부터 보여 달라**" | `npc scaffold archetype … ` → `npc validate` → `npc diff` |
| 성격 조정 | "위병을 더 겁 많게. `courage` 를 낮추면 어느 인터럽트가 빠지는지 같이 알려 달라" | `npc card archetype town_guard` (걸릴 인터럽트 표) |
| 플랜이 반려됐다 | "이 반려 플랜이 왜 떨어졌는지 스텝 단위로 설명하고 고쳐 달라" | `npc plan validate` → `npc plan explain` |
| 근무 시간 문제 | "위병이 밤에 자는 것 같다. 근무 시간과 폴백이 맞는지 봐 달라" | `npc timeline archetype town_guard` |
| 마스터데이터 리뷰 | "내 브랜치가 무엇을 무효화하는지, 재배포가 필요한지 알려 달라" | `npc diff --base main` |
| 검증 실패 | "`V10` 이 떴다. 고쳐 달라" | `docs/llm/VALIDATION.md` + `npc validate --json` 의 `fix_hint` |

#### 기계가 읽는 출력 — 에이전트에게 이것만 주면 된다

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

#### 게임서버 팀에게 줄 것

NPC 서버를 붙이는 쪽(게임서버)이 알아야 할 것은 **연동 계약 하나**다.

| 주는 것 | 무엇 |
|---|---|
| [`docs/reference_link.html`](docs/reference_link.html) | N1~N8 · 패킷 · 와이어 · 핸드셰이크 · 인증. **읽지 않고 붙이지 않는다** |
| `testbed/Npc.TestGameServer` | 우리 쪽 게임서버 **대역**. 자기 구현과 비교할 기준 |
| 구조 해시 | 핸드셰이크에서 **완전 일치**를 요구한다. 불일치면 거절이다 |

에이전트에게는 이렇게 말한다 — **"`Npc.Contracts` 는 5파일 472줄이다. 통째로 읽고 시작해라."**

#### 아직 없는 것 — 지시문에 넣지 않는다

이것들을 전제로 지시하면 에이전트가 없는 도구를 부르다 헤맨다.

| 없는 것 | 무엇을 대신 쓰나 |
|---|---|
| MCP 서버 | `npc … --json` 을 셸로 부른다 |
| 대화 생성 | **없다.** 이 서버는 행동 플랜만 만든다 |
| 세력 테이블 | 패킷의 `Faction` 은 **통과만** 한다 — 값을 정의하는 마스터데이터가 없다 |

세부 진행 상황은 [`PRODUCTION_ROADMAP.md`](PRODUCTION_ROADMAP.md) 체크리스트를 본다.

### 문서

> **코드를 이해하려고 왔다면 [`docs/book/`](docs/book/index.html) 부터 연다.**
> 사양 문서(`docs/0x`·`docs/1x`)는 *무엇을 왜 만드는가*를 정한 문서이고,
> 책은 *이미 만들어진 코드를 앞에 두고* 답한다.

**읽는 순서**

```
1. docs/index.html              전체 안내 — 아키텍처 · 동작 · 빌드 · 실행
2. docs/tutorial/index.html     활용 실습서 — 직접 돌리고 만들어 보려면 여기부터
   docs/book/index.html         코드 이해와 활용 안내서 (13장)
3. docs/reference_link.html     게임서버에 붙일 때 — 계약 전문
   docs/reference_masterdata.html  콘텐츠를 늘릴 때 — 스키마 전문
```

> **구현이 끝난 제품이다.** 주차별 작업 지시서와 단계별 설계 사양은 구현 완료로 전부 삭제했다
> (2026-08-06). 원문이 필요하면 `git log --diff-filter=D -- docs/ TASKS.md` 에서 꺼낸다.
> 코드 주석에 남아 있는 `docs/NN §M` 참조는 그 시점의 근거를 가리키는 이력이고,
> 지금은 아래 레퍼런스 HTML 이 대응한다.

| 문서 | 내용 |
|---|---|
| [`docs/index.html`](docs/index.html) | **한 장짜리 안내서.** 아키텍처 · 용도 · 빌드 · 실행 · 실측 결과 |
| [`docs/book/index.html`](docs/book/index.html) | **코드 이해와 활용 안내서 (13장).** 왜 이 구조인가 → 계약 → 런타임 → 플랜 생성 → 설정·실측 |
| [`docs/tutorial/index.html`](docs/tutorial/index.html) | **활용 실습서 (6부 21장 + 부록).** 실행 한 줄 → 콘텐츠 추가 → 내 게임서버 붙이기 → LLM 켜기 → 부하·테스트 |
| [`docs/reference_link.html`](docs/reference_link.html) | **게임서버 연동 계약 ★** N1~N8 · 패킷 · 와이어 프로토콜 · 핸드셰이크 · **게임서버가 지켜야 할 발행 규약** |
| [`docs/reference_masterdata.html`](docs/reference_masterdata.html) | **마스터데이터 레퍼런스 ★** 월드 플래그 43 · 액션 37 · 아키타입 40 · 버킷 2,880 · 검증 V1~V13 · 작성 순서 |
| [`docs/reference_metrics.html`](docs/reference_metrics.html) | **실측 데이터.** 런타임 성능 · 스케일 · LLM 지연 · 캐시 · 비용 · 프리베이크 · 플랜 품질 · 수용 기준 판정 · **미측정으로 남은 것** |
| [`docs/FAQ.html`](docs/FAQ.html) | 도입 이점 · 적합한 범위와 한계 · 행동 플랜 준비 · 전투 반응 설계 · NPC 대화 확장 |
| [`docs/startup_flow.html`](docs/startup_flow.html) | 기동 흐름 시각화 — 무엇이 어떤 순서로 조립되는가 |
| [`docs/testbed_guide.html`](docs/testbed_guide.html) | **게임서버 연동 테스트 안내서.** 아키텍처 그림 · 핸드셰이크·틱 루프 애니메이션 · 무엇을 바꾸며 테스트하나 · 코드 분석 순서 |
| [`testbed/README.md`](testbed/README.md) | **데모 띄우는 법** — 한 줄 실행 · 포트 · 화면 보는 법 · 알려진 한계 |
| [`PRODUCTION_ROADMAP.md`](PRODUCTION_ROADMAP.md) | **상용 투입 로드맵.** 태스크 50건 체크리스트(44건 완료) · 남은 6건의 상세. **유일한 작업 지시서** |
| [`docs/measurements/`](docs/measurements/) | 실측 **원자료** (jsonl · csv). 보고서는 `reference_metrics.html` 로 옮겼다 |
| [`docs/security/threat_model.md`](docs/security/threat_model.md) | **위협 모델.** 자산 · 신뢰 경계 · 위협 T1~T15 와 대응 · 실측 · **잔여 위험** |
| [`docs/security/secrets.md`](docs/security/secrets.md) | **시크릿.** 환경변수 목록 · 회전 절차 · 유출 대응. **무중단 회전은 없다** — 회전 = 재기동 |
| [`docs/security/privacy.md`](docs/security/privacy.md) | 플레이어 id 가 남는 위치와 삭제 경로 |
| [`docs/legal/dotllm.md`](docs/legal/dotllm.md) · [`models.md`](docs/legal/models.md) | dotLLM GPLv3 배포 경계 · 모델 약관. **법무 확인은 미실시** |
| [`docs/wire/layout_v2.md`](docs/wire/layout_v2.md) | **와이어 오프셋 표 (생성물).** 다른 언어로 게임서버를 짤 때 읽는다 — 필드별 오프셋·크기·부호·엔디언·패딩 위치 |

### 로드맵 (12주)

| 주차 | 내용 | 게이트 | 현재 |
|---|---|---|---|
| W1 | 스파이크 | 요청당 지연 가정이 실측과 ±50% 이내 | ❌ 실측 5.14s / 가정 1.75s (**2.9배**) |
| W2–4 | 코어 · 런타임 · Sim · 마스터데이터 | **LLM 0회 호출**로 NPC 500마리 게임 7일 완주 | ✅ 8항목 전부 |
| W5–6 | 플랜 컴파일러 · 4단 검증기 | 검증 통과율 ≥ 90% | ❌ 78.3 % |
| W7–8 | 플랜 캐시 · 프리베이크 | 2,880키 콜드 필 완주, 히트율 ≥ 98% | ⚠ 히트율 **98.67 %** 통과 · 전량 회차 미실행 |
| W9–10 | 스케줄러 · 3-티어 · 부하 테스트 | NPC 5,000, 틱 p99 ≤ 20ms, GPU ≤ 60% | ⚠ p99 **0.801 ms** 통과 · GPU 미측정 |
| W11–12 | 검증 · 블라인드 평가 · 보고 | 시나리오 A/B/C 통과, 리플레이 100% 일치 | ⚠ 둘 다 통과 · 블라인드 평가 미실시 |
| (본편 밖) | **P6** 테스트 베드 — 소켓 · 게임서버 대역 · 뷰어 | NPC 서버 본체 diff = 0줄 | ⚠ 9/10 · 남은 것은 "사람이 화면 앞에 앉기" |

### 성능 목표 (NPC 5,000)

| 지표 | 목표 |
|---|---|
| 틱 실행 시간 p99 | ≤ 20ms (100ms 예산의 20%) |
| 인지 스캔 대상 | ≤ 150마리/틱 (전수 5,000 대비 33배 감소) |
| 플랜 캐시 히트율 | ≥ 98% |
| 플랜 검증 실패율 | ≤ 3% |
| 명령 송출 | ≥ 2,000 cmd/s, 틱 루프 **`bytesPerTick` = 0** |
| GPU 사용률 | ≤ 60% |
| 프롬프트 캐시 적중률 | ≥ 95% |

### 범위 밖 (이번 R&D)

- 게임 클라이언트 / 렌더링 — 대시보드와 로그로만 관측
- NPC 대사 자연어 생성 — `Speak(DialogueId)`로 ID만 송출
- 전투 AI · 패스파인딩 — 게임서버 소관. 명령만 발행
- 멀티 월드 — 단일 월드 5,000 NPC. **샤딩은 1단계(정적)까지 구현됐다** (`--shard N` · `deploy/shards.json`). 존 간 핸드오프(2단계)는 미구현

---

## 라이선스 주의

**dotLLM은 GPLv3다.**

- ✅ **별도 프로세스 + HTTP(OpenAI 호환 엔드포인트)로만 사용한다**
- ❌ NuGet 패키지를 프로젝트에 참조해 인프로세스로 임베딩하지 않는다

프로세스 분리는 라이선스 경계 외에도 GC 격리·크래시 격리·엔진 교체 용이성이라는 실익이
있다. 상용 전환을 검토하는 시점에 법무 확인을 별도로 건다.

본 저장소 자체의 라이선스는 사내 정책에 따른다.
