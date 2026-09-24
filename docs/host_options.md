# Npc.Host 실행 옵션

## 기본 실행

| 옵션 | 설명 |
|---|---|
| `--loopback` | 인프로세스 게임 월드에 직결한다 (기본) |
| `--npcs N` | NPC 수 (기본 500) |
| `--time-scale N` | 시간 압축 (기본 60) |
| `--days N` | 게임 일수. 0은 무제한 (기본 1) |
| `--masterdata <dir>` | 마스터데이터 폴더 (기본 ./masterdata) |
| `--planstore <dir>` | 플랜 스토어 (기본 ./planstore) |
| `--port N` | 대시보드 포트 (기본 5080) |
| `--bind <addr>` | 웹 바인드 주소 (기본 127.0.0.1) |
| `--profile dev|service` | 실행 프로파일. service는 무제한 실행 |
| `--scenario <jsonl>` | 시나리오 이벤트 주입 |
| `--config <path>` | 설정 파일 (기본 npc.settings.json 자동 탐색) |
| `--no-dashboard` | 대시보드 없이 실행한다 |

## 게임서버 연결

| 옵션 | 설명 |
|---|---|
| `--link null|record|replay|loopback|tcp` | 게임서버 링크 종류 |
| `--trace <path>` | 기록 또는 재생 파일 (jsonl) |
| `--gs-host <host>` | 게임서버 주소 (기본 127.0.0.1) |
| `--gs-port N` | 게임서버 링크 포트 (기본 7010) |
| `--zone <id>[,<id>]` | 로스터 존 필터. 게임서버와 같아야 한다 |
| `--shard N` | 담당 샤드. 0은 단일 샤드 |
| `--shards <path>` | 샤드 정의 파일 (기본 deploy/shards.json) |
| `--dynamic-roster` | 실행 중 NPC 스폰·디스폰 허용 |
| `--npc-capacity N` | 동적 로스터 슬롯 상한 |

## LLM

| 옵션 | 설명 |
|---|---|
| `--tier none|t1|t2|all` | 사용할 LLM 티어 (기본 none) |
| `--no-llm` | LLM을 호출하지 않는다 |
| `--t1-engine <id>` | 로컬 엔진 ID |
| `--t2-engine <id>` | 외부 엔진 ID |
| `--t1-workers N` | 로컬 워커 수 (기본 2) |
| `--t2-workers N` | 외부 워커 수 (기본 8) |
| `--billing-cap-usd <n>` | 하루 외부 비용 상한 USD. 0은 끔 |
| `--billing-reset-hour N` | UTC 청구일 경계 시각 (기본 0) |
| `--budget-individual-share <0~1>` | 개체 재계획의 T2 예산 몫 |

## 저장·복원

| 옵션 | 설명 |
|---|---|
| `--snapshot-dir <dir>` | 상태 스냅샷 폴더 (기본 ./state) |
| `--snapshot-interval-s N` | 스냅샷 주기 초. 0은 끔 |
| `--snapshot-keep N` | 보존할 스냅샷 수 (기본 3) |
| `--restore auto|none|<path>` | 복원 정책 (기본 auto) |
| `--memory <dir>` | NPC 기억·관계 저장소 폴더 |
| `--memory-ttl-days N` | 기억 보존 게임 일. 0은 무제한 |
| `--planstore-sha <sha8>` | 특정 프리픽스 회차 플랜을 선택 |

## 관측·경보

| 옵션 | 설명 |
|---|---|
| `--prometheus` | Prometheus 메트릭 라우트 활성화 |
| `--otlp-endpoint <url>` | OpenTelemetry 수집기 주소 |
| `--alarm-webhook <url>` | 경보 웹훅 주소 |
| `--alarm-cooldown-s N` | 같은 경보의 재발화 간격 초 |
| `--log-format text|json` | 로그 형식 (기본 text) |
| `--health-port N` | 헬스 프로브 전용 포트 |
| `--live-stall-s N` | live 프로브의 루프 정지 상한 |
| `--ready-tick-stall-s N` | ready 프로브의 틱 정지 상한 |
| `--query-max-streams N` | 동시 조회 스트림 수 (기본 8) |

## 보안

| 옵션 | 설명 |
|---|---|
| `--link-tls off|tls|mtls` | 게임서버 링크 암호화 |
| `--link-cert <pfx>` | mTLS 클라이언트 인증서 |
| `--link-tls-host <name>` | TLS SNI 이름 |
| `--require-link-auth` | 인증 없는 게임서버 연결을 거절 |

## 측정·개발용

| 옵션 | 설명 |
|---|---|
| `--dev-control` | 데모용 제어 API 활성화 |
| `--watch` | 플랜·데이터 변경 시 자동 리로드 |
| `--fail-rate <0~1>` | 게임 대역의 액션 실패 주입 |
| `--drop-rate <0~1>` | 게임 대역의 명령 유실 주입 |
| `--player-bots N` | 가상 플레이어 수 (기본 20) |
| `--hostile-bots N` | 적대 가상 플레이어 수 |
| `--seed N` | 게임 대역 난수 시드 |
| `--weights A|B|C|D` | 재계획 점수 가중치 세트 |
| `--scan-cap N` | 틱당 인지 스캔 상한 |
| `--max-speed` | 10Hz 페이싱 없이 측정 |
| `--on-link-fault exit|wait` | 링크 고장 정책 (기본 exit) |
| `--fault-grace-s N` | 링크 고장 후 종료 유예 초 |
| `--shutdown-timeout-s N` | 정상 종료 시간 상한 |
| `--tick-sync-stall-s N` | 게임 틱 동기화 정지 상한 |
