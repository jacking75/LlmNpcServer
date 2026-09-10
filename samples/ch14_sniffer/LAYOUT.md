# 14장 — 와이어 레이아웃 한 장

다른 언어·엔진에서 이 링크에 붙을 때 지켜야 할 것 전부.

---

## 1. 프레임 헤더 — 8바이트, 손으로 쓴다

```
 0      1      2      3      4      5      6      7      8 ...
+------+------+------+------+------+------+------+------+---------------+
|      PayloadLength (u32)   | Kind | Ver  |   Reserved  |    Payload    |
+------+------+------+------+------+------+------+------+---------------+
        little-endian          u8     u8      u16 = 0     MemoryPack
```

| 필드 | 크기 | 값 |
|---|---|---|
| `PayloadLength` | u32 LE | 헤더를 뺀 페이로드 길이. **상한 1 MiB** |
| `Kind` | u8 | 아래 표 |
| `Ver` | u8 | **1** |
| `Reserved` | u16 LE | 0 |

**이 8바이트에는 MemoryPack 이 안 들어간다.** 길이를 페이로드 안에 또 넣지 않는 이유는
진실의 출처를 둘로 만들면 언젠가 어긋나고, 어긋나는 순간 스트림이 통째로 깨지기 때문이다.

**검사를 빼지 않는다.** 버전을 길이보다 **먼저** 본다(버전이 다르면 길이 해석 자체를 못 믿는다).
길이 상한 검사가 없으면 잘못된 4바이트 하나로 프로세스가 죽는다 — 상대가 악의적이지 않아도
프레임 경계가 한 번 밀리면 바로 그 상황이 된다.

### 프레임 종류

| Kind | 이름 | 방향 | 언제 |
|---:|---|---|---|
| 1 | `Hello` | **게임서버 → NPC 서버** | 접속 직후. 게임서버가 규격을 먼저 제시한다 |
| 2 | `HelloAck` | NPC 서버 → 게임서버 | 수락/거절 |
| 3 | `CommandBatch` | NPC 서버 → 게임서버 | **`FlushAsync` 한 번 = 프레임 하나** (N8) |
| 4 | `EventBatch` | 게임서버 → NPC 서버 | 틱마다 |
| 5 | `Heartbeat` | 양방향 | 주기적 |
| 6 | `Bye` | 양방향 | 정상 종료·프로토콜 위반 |

> **연결은 NPC 서버가 걸고, `Hello` 는 게임서버가 보낸다.** 헷갈리기 쉬운 지점이다.
> 세계의 주인이 게임서버이므로 규격을 제시하는 쪽도 게임서버다.

---

## 2. 실측 — 스니퍼로 잰 값

NPC 50마리 · 배속 60 · 12초 (`samples/ch14_sniffer/sniff.py`)

| 방향 | 종류 | 프레임 | 바이트 | 평균 |
|---|---|---:|---:|---:|
| npc→gs | `CommandBatch` | 179 | 24,940 | 139 B |
| gs→npc | `EventBatch` | 89 | 29,612 | 332 B |
| npc→gs | `Heartbeat` | 8 | 192 | 24 B |
| gs→npc | `Hello` | 1 | 96 | 96 B |
| npc→gs | `HelloAck` | 1 | 96 | 96 B |

**핸드셰이크는 왕복 96바이트씩 한 번뿐이다.** 그 뒤로는 배치만 흐른다.
`CommandBatch` 프레임 수는 NPC 서버의 `FlushAsync` 횟수와 같다 —
**틱 수와 다를 수 있다**(보낼 것이 없는 틱은 프레임을 안 만든다).

---

## 3. 페이로드 — MemoryPack

헤더 뒤는 [MemoryPack](https://github.com/Cysharp/MemoryPack) 인코딩이다.

| Kind | 페이로드 타입 |
|---|---|
| `Hello` | `WireHello` |
| `HelloAck` | `WireHelloAck` |
| `CommandBatch` | `WireCommand[]` |
| `EventBatch` | `WireEvent[]` |
| `Heartbeat` | `WireHeartbeat` |
| `Bye` | `WireBye` |

> **더 정확한 표가 있다.** `docs/wire/layout_v2.md` 는 코드에서 뽑는 생성물이라
> 필드별 오프셋·패딩 위치까지 있다. 이 문서는 스니퍼를 쓰는 데 필요한 만큼만 적는다.
> 코덱을 직접 쓸 것이라면 `docs/wire/reference/npc_wire.py` 를 베낀다.

### 구조체 크기는 **고정**이다

| 타입 | 크기 | 테스트 |
|---|---:|---|
| `WireCommand` | **56 B** | `Wire_LayoutIsFrozen` |
| `WireCommandV2` | **72 B** | `Wire_LayoutIsFrozen_V2` (B-02. `ExtSlots` 협상 시) |
| `WireEvent` | **64 B** | `Wire_LayoutIsFrozen` |
| `WireEventV2` | **80 B** | `Wire_LayoutIsFrozen_V2` (B-02) |

필드 순서는 **큰 타입부터**다(`long` → `int` → `float` → `ushort` → `byte`).
패딩이 생기면 이 크기가 달라지고 테스트가 먼저 깨진다.

> **v2 는 프레임 헤더의 `Ver` 가 2 일 때다.** `ExtSlots` 기능 비트를 켠 게임서버에만 나가고,
> 배치 뒤쪽에 `Instance`·`Faction`·`ExtA`·`ExtB` 와 꼬리 정렬 `Reserved`(항상 0)가 붙는다.
> 스니퍼는 **`Ver` 를 보고 원소 크기를 고른다** — 협상 결과로 고르면 경계에서 어긋난다.

### 핸드셰이크 필드

```
WireHello       ProtocolVersion:i32  TickRate:i32  TimeScale:i32  NpcCount:i32
                StartTick:i64  MasterData:WireHash  Roster:WireHash

WireHelloAck    ProtocolVersion:i32  TimeScale:i32  NpcCount:i32
                MasterData:WireHash  Roster:WireHash  Accepted:u8  RejectCode:u8

WireHash        A:u64 B:u64 C:u64 D:u64      // SHA-256 32바이트를 ulong 4개로
```

**해시를 hex 문자열로 싣지 않는다.** 패킷에 `string` 이 없다는 규칙(N3)이 와이어에서도 그대로다.
`ulong` 4개면 정확히 32바이트이고, hex 로 실으면 64바이트에 파싱까지 붙는다.

---

## 4. 붙을 때 지켜야 할 것 다섯

1. **헤더 8바이트를 손으로 쓴다.** 리틀엔디언 u32 길이 + Kind + Ver=1 + Reserved=0.
2. **TCP 경계를 믿지 않는다.** 버퍼에 모아 두고 `len(buf) >= 8 + length` 일 때만 한 프레임을 뗀다.
3. **`Hello` 를 먼저 보낸다.** 네 값(프로토콜 버전 · 타임스케일 · 마스터데이터 해시 · 로스터 해시)이
   NPC 서버와 같아야 한다. **하나라도 다르면 거절이고 우회 옵션은 없다.**
4. **`Sequence` 를 순증으로 매긴다** (N6). 건너뛰면 NPC 서버가 갭으로 읽고 경보를 올린다.
   `CorrelationId` 는 명령에서 받은 값을 **그대로 돌려준다** (N5).
5. **`TickSync` 를 매 틱 보낸다.** 이게 없으면 NPC 서버의 게임 시계가 안 간다.

## 5. MemoryPack 을 못 쓰는 언어라면

두 갈래다.

- **`Npc.Wire` 를 고친다.** 이 프로젝트 하나만 다른 직렬화(FlatBuffers·직접 바이트)로 바꾸면 된다.
  `Npc.Contracts` 도 `Npc.Runtime` 도 안 바뀐다 — **와이어 DTO 를 따로 둔 이유가 이것이다.**
- **구조체가 고정 크기라는 점을 쓴다.** `WireCommand[]` 는 결국 56바이트짜리 연속 블록이다.
  MemoryPack 의 배열 헤더만 해석하면 나머지는 raw 로 읽을 수 있다.

어느 쪽이든 **헤더 8바이트와 다섯 규칙은 그대로다.**
