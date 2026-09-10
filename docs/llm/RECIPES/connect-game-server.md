# 실제 게임서버 붙이기

**`docs/reference_link.html` 을 읽지 않고 시작하지 않는다.** 타입 이름·ID 체계가 전부 거기 있다.

## 전제

- `Npc.Contracts` 5파일 472줄을 통째로 읽었다. 그게 제일 빠르다.
- `testbed/Npc.TestGameServer` 를 띄워 봤다 — 우리 쪽 기준 구현이다.

## 순서

1. **계약을 읽는다.** N1~N8 이 전부다.
   - 명령은 fire-and-forget. 결과는 **이벤트로만**.
   - 패킷에 `string`·`DateTime` 금지. 강타입 ID 와 `Tick`.
   - 명령에 `CorrelationId`, 이벤트에 `Sequence`(갭 → 경보).
   - 이벤트 처리는 **멱등**이어야 한다 (2회 주입 → 상태 해시 동일).
2. **핸드셰이크를 맞춘다.**
   - 프로토콜 버전 범위 [1, 2] 협상.
   - **구조 해시는 완전 일치**를 요구한다. 불일치면 거절이다.
   - 내용 해시 불일치는 경고 후 수락 — 다른 파이프라인으로 배포되는 것이 정상이다.
   - 인증을 켰으면 HMAC-SHA256 + 논스.
3. **이벤트 발행 지점을 정한다.** 게임서버의 어느 코드에서 무엇을 낼 것인가.
4. **명령 유실을 전제한다.** 우리는 `timeout_s` 로 살아나지만, 그것이 잦으면 NPC 가 멍해진다.
5. `Npc.Sim --drop-rate` 로 유실 경로를 시험한다.

## 확인

```
# 우리 쪽 대역과 먼저 붙여 본다
dotnet run --project testbed/Npc.TestGameServer -- --headless
dotnet run -c Release --project src/Npc.Host -- --link tcp --gs-host 127.0.0.1

# 그다음 진짜 게임서버로
```

`/status` 의 `linkState` 가 `Attached` 이고 `linkReject` 가 비어야 한다.
거절되면 이유가 코드로 나온다 — `docs/llm/VALIDATION.md` 의 핸드셰이크 절을 본다.

## 되돌리기

`--link loopback` 으로 돌아간다. 게임서버 없이도 전부 돈다.

## 파급

| 무엇 | 범위 |
|---|---|
| 마스터데이터 | 없음 |
| **결정론** | 소켓 경로에서는 리플레이 일치가 **성립하지 않는다.** 결함이 아니다 |

> **계약 타입을 바꾸자는 제안을 하지 않는다.** 바꾸려면 먼저 `docs/reference_link.html` 을
> 고치는 PR 이다. 와이어 v1 은 동결이다 — 배포된 게임서버가 읽는다.
