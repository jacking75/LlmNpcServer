# 운영 — 기동·헬스·스냅샷·킬스위치·알람

## 기동

```powershell
dotnet run -c Release --project src/Npc.Host -- --loopback --npcs 5000 --time-scale 60
```

설정은 **세 겹**이다: CLI > 환경변수(`NPC_*`) > 파일(`npc.settings.json`) > 기본값.
모르는 키는 거절한다 — 오타로 옵션이 무시되는 것을 막는다.

**비밀은 환경변수로만.** 인자는 `ps` 에 보이고 파일은 이미지에 굽힌다.
`NPC_ADMIN_TOKEN` · `NPC_LINK_SECRET` · `NPC_LINK_CERT_PASSWORD`.
`--bind 0.0.0.0` 은 토큰 없으면 **기동을 거절한다.**

## 헬스

| 경로 | 뜻 |
|---|---|
| `/healthz/live` | 프로세스가 살아 있나. 루프 하트비트가 근거 |
| `/healthz/ready` | 트래픽을 받을 수 있나 |
| `/healthz/startup` | 초기화가 끝났나 |

컨테이너는 `Npc.Host healthcheck --url …` 을 `HEALTHCHECK` 로 쓴다.

## 스냅샷

주기 저장 + 종료 시 1회. CRC32 로 손상을 검출하고 **이전 세대로 폴백**한다.
복구는 포맷 버전 · 마스터데이터 해시 · 로스터 해시 · NPC 수가 **전부 맞아야** 올린다.

```
POST /admin/snapshot          # 즉시 한 장 (배포 직전에)
```

## 종료

SIGTERM/SIGINT/SIGQUIT → 틱 루프 → 스냅샷 → 워커 → 링크(`Bye`) → 웹.
단계마다 예산이 있고, **한 단계가 실패해도 나머지를 계속한다.**

## 킬스위치

```
POST /admin/killswitch?target=T2      # 외부 LLM 차단
POST /admin/killswitch?target=T1
POST /admin/killswitch?target=Store
```

한 번 켜지면 유지된다. 되돌리려면 `Clear`. 모든 조작은 **감사 로그**에 남는다
(`state/audit.jsonl` — 시각은 게임 틱이다).

## 알람

14종. 쿨다운이 걸리고 웹훅(Slack/Teams)으로 나간다. 예산 임계는 80/95/100%.

**먼저 볼 것**

| 증상 | 어디 |
|---|---|
| 틱이 밀린다 | `/metrics` 의 `bytesPerTick`(목표 0) · p99 · `scan/tick` |
| 비용이 샌다 | 프리픽스 해시 유니크 카운트. 2 이상이면 캐시가 안 걸린 것 |
| NPC 가 멍하다 | `linkState` · 시퀀스 갭 경보 · 타임아웃 합성 비율 |
| 게임 시각이 되돌아갔다 | `TickSyncWatchdog` · 핸드셰이크가 시각을 싣는가 |
| 파생물 경고 | `npc regen --check` |

## 확인

```
curl http://localhost:5080/healthz/ready     # 200
curl http://localhost:5080/status            # linkState · ticksBehind · killSwitches
curl http://localhost:5080/metrics           # bytesPerTick 0 · p99 · 예산
npc regen --check                            # 파생물 최신
```

`state/audit.jsonl` 에 방금 한 조작이 남았는지 본다 — 안 남았으면 조작이 안 걸린 것이다.

## 파급

운영 조작은 마스터데이터를 바꾸지 않는다. 킬스위치는 **재기동까지 유지**된다는 것만 기억한다.
