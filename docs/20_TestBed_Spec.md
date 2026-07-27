# 20. 테스트 베드 사양 — 게임서버 대역 + 테스트 클라이언트 (소켓)

> **이 문서는 "무엇을 왜 이렇게 만드는가"다.** 작업 순서와 완료 조건은 [`20_TestBed_TASKS.md`](20_TestBed_TASKS.md)에 있다.
>
> **선행 독서: [`CLAUDE.md`](../CLAUDE.md) §2·§3 · [`docs/02`](02_GameServer_Link.md) 전문 · [`docs/01`](01_MasterData_Spec.md) §4.**
> 특히 `docs/02` §1의 N1~N8과 §7("실제 네트워크 구현 시 남는 작업")을 읽지 않고 이 문서를 구현하지 않는다. 이 문서는 그 §7을 실제로 구현하는 문서다.

---

## 0. 무엇을 만들고 무엇을 만들지 않는가

| 대상 | 이번에 | 비고 |
|---|---|---|
| **`Npc.Wire`** — 링크 와이어 프로토콜 (MemoryPack DTO + 프레임 코덱) | ✅ 신규 | `docs/02` §7-1·2 |
| **`TcpGameServerLink`** — 실제 소켓 링크 | ✅ 골격 → 구현 | `docs/02` §7-3·4·5 |
| **`Npc.TestGameServer`** — 소켓을 가진 게임서버 대역 | ✅ 신규 | 기존 `Npc.Sim`을 그대로 쓴다. 시뮬 로직을 새로 짜지 않는다 |
| **`Npc.TestClient`** — WinForms 시각화 클라이언트 | ✅ 신규 | NPC 동작 확인이 목적. 게임을 만드는 것이 아니다 |
| **`Npc.TestBed.Protocol`** — 클라이언트 프로토콜 | ✅ 신규 | 게임서버 ↔ 클라이언트 |
| **데모 시나리오 3종 + 실행 스크립트** | ✅ 신규 | `testbed/scenarios/` |
| **NPC 서버 본체 로직** | ❌ 손대지 않는다 | 이 작업의 성패 판정 기준이다 (§1) |
| **마스터데이터** | ❌ **한 바이트도 고치지 않는다** | §3.2. 고치면 플랜 스토어 2,880칸이 무효다 |
| **실제 MMORPG 게임서버** | ❌ | 여전히 범위 밖이다 (`docs/02` §0) |
| **인증·암호화·세션 복구** | ❌ | 테스트 베드다. §16 |

**관점은 `docs/02` §0 그대로다.** 우리가 만드는 것은 NPC 서버이고, `Npc.TestGameServer`는 그 상대역이다. 이름에 "GameServer"가 들어가도 그것은 *상대방*을 가리킨다.

---

## 1. 왜 만드는가 — 이 작업의 합격 기준

`docs/02` §7은 이렇게 끝난다.

> 이 5가지 중 **NPC 서버 코드를 건드려야 하는 것은 없다.** 그것이 이번 인터페이스 설계의 검증 기준이다.

지금까지 이 문장은 **주장**이었다. 이 테스트 베드가 그 주장을 검증한다.

**합격 기준 세 줄.**

1. `Npc.Runtime`·`Npc.Planning`·`Npc.Core`·`Npc.Contracts`의 코드가 **한 줄도 바뀌지 않는다.** (`Npc.Host`의 배선과 `Npc.Gateway`의 링크 구현체는 바뀐다 — 그 두 곳이 바뀌라고 만든 자리다.)
2. 같은 시나리오를 `--loopback`으로 돌린 것과 `--link tcp`로 돌린 것이 **같은 종류의 결과**를 낸다. (틱 단위 일치는 아니다 — §14.3)
3. 사람이 클라이언트를 켜고 **NPC가 무엇을 하는지 눈으로 본다.** 이것이 `docs/15` §6 블라인드 평가와 W12 보고서의 시연 자료가 된다.

부수 효과가 하나 더 있다. **지금까지 `Npc.Sim`은 틱 루프와 같은 프로세스에서 락스텝으로 돌았다**(`Npc.Host/Program.cs`의 `MaxLeadTicks = 1`). 프로세스가 갈리면 그 목발이 없어진다. 명령이 늦게 도착하고, 이벤트가 몰려서 오고, 링크가 끊긴다 — `docs/02` §1이 대비하려던 바로 그 상황이다. **그 대비가 실제로 작동하는지는 이 테스트 베드에서만 확인된다.**

---

## 2. 전체 구조

```
   프로세스 A                        프로세스 B                       프로세스 C
┌────────────────────┐         ┌─────────────────────────┐      ┌──────────────────┐
│  Npc.Host          │  TCP    │  Npc.TestGameServer     │ TCP  │  Npc.TestClient  │
│  (NPC 서버)         │ :7010   │  (게임서버 대역)          │:7020 │  (WinForms)      │
│                    │◄───────►│                         │◄────►│                  │
│  Npc.Runtime       │  링크    │   SimWorld              │ 클라  │  MapRenderer     │
│  Npc.Planning      │ 프로토콜 │   MovementSim           │프로토콜│  Inspector       │
│  Npc.Llm           │  §5     │   InteractionSim        │  §8  │  LogPanel        │
│  Npc.Gateway       │         │   NeedsSim              │      │  ControlPanel    │
│   └TcpGameServerLink│        │   PlayerRegistry (신규)  │      │                  │
└─────────┬──────────┘         │   ScenarioRunner        │      └────────┬─────────┘
          │                    └─────────────────────────┘               │
          │ HTTP :5080                                                   │
          │  GET /npc/{id} · /metrics · /status                          │
          └──────────────────────────────────────────────────────────────┘
                     인스펙터 패널이 직접 폴링한다 (§9.5)
```

**방향과 역할.**

| 경계 | 누가 듣는가 | 무엇이 흐르는가 | 규칙 |
|---|---|---|---|
| NPC 서버 ↔ 게임서버 | **게임서버가 :7010에서 듣는다.** NPC 서버가 접속한다 | `NpcCommand` ▲ / `GameEvent` ▼ | N1~N8 전부 적용 (§3.4) |
| 게임서버 ↔ 클라이언트 | **게임서버가 :7020에서 듣는다** | 스냅샷·로그 ▼ / 입력·제어 ▲ | 별개 프로토콜. N 규칙 대상 아님. 단 문자열은 여기도 없다 (§3.5) |
| 클라이언트 → NPC 서버 | NPC 서버의 HTTP :5080 | `GET /npc/{id}` 등 | 읽기 전용 디버그 경로. 이미 있는 것을 쓴다 |

**왜 게임서버가 듣는가.** 실제 배치에서 NPC 서버는 게임서버에 붙는 위성 서비스다. 게임서버가 먼저 떠 있고 NPC 서버가 나중에 붙었다 끊겼다 한다. 재접속 로직을 NPC 서버 쪽에만 두면 되므로 `TcpGameServerLink`도 단순해진다.

**왜 클라이언트가 NPC 서버 HTTP를 직접 보는가.** 플랜 id·스텝 번호·월드 플래그는 **게임서버가 알 수 없는 정보**다. 링크에는 그런 필드가 없고(N1·N3), 있어서도 안 된다. 게임서버를 경유해 중계하면 실제로는 존재하지 않는 결합을 테스트 베드가 만들어내는 셈이다. 그래서 디버그 정보는 디버그 경로로 가져온다.

---

## 3. 절대 규칙 — 이 작업에서 깨지기 쉬운 것들

### 3.1 `Npc.Contracts`·`Npc.Core`에 NuGet을 추가하지 않는다

`CLAUDE.md` §3의 규칙이다. **MemoryPack은 `Npc.Contracts`에 들어갈 수 없다.** `[MemoryPackable]`을 `NpcCommand`에 붙이고 싶어지겠지만 그 순간 규칙 위반이다.

그래서 **와이어 DTO를 따로 만들고 1:1 매핑한다**(§5.3). 손이 더 가는 대신 얻는 것이 있다.

- 도메인 타입(`NpcCommand`)과 전송 표현(`WireCommand`)이 갈라진다. 나중에 직렬화기를 바꿔도 도메인이 안 흔들린다.
- 매핑 함수가 있으므로 **드리프트를 테스트로 잡을 수 있다**(§13, `Wire_MirrorsContractMembers`). `NpcCommand`에 필드를 추가하고 와이어를 잊으면 빌드가 아니라 테스트가 깨진다.

### 3.2 `masterdata/`를 고치지 않는다

**이 규칙을 어기면 조용히 큰 손해가 난다.**

`MasterDataSet.ContentHash`는 `masterdata/`의 파일 내용 해시를 접은 값이고, `PlanStoreValidator`가 이 값으로 프리베이크된 플랜의 무효화 범위를 판정한다(`docs/13` §3). `zones.json`에 화면 좌표 필드 하나를 추가하는 순간 해시가 바뀌고 **플랜 스토어가 전량 무효**가 된다. 지금 `planstore/plans/`에 254건이 있고 이것은 실제 비용을 들여 만든 것이다.

- 화면 표시에 필요한 값은 **`testbed/` 아래 별도 파일**에 둔다.
- 다행히 **좌표는 이미 전역이다.** `pois.json`의 `pos`는 존별 로컬이 아니라 전역 좌표다(존 중심이 서로 떨어져 있다. town_center centroid ≈ (0, 5), mine_hills ≈ (−1615, −801)). **레이아웃 파일이 필요 없다.** 월드 범위는 x ∈ [−1940, 1895], z ∈ [−1592, 1756], 약 3.8km × 3.3km다.
- POI·존·아키타입의 **이름**은 클라이언트가 `Npc.MasterData`를 참조해 직접 읽는다. 파서를 새로 쓰지 않는다.

### 3.3 틱 루프에서 소켓 I/O를 하지 않는다

`CLAUDE.md` §2.1이 틱 루프 안에서 허용하는 `await`는 `link.FlushAsync` 하나다. 여기에 소켓 쓰기를 넣으면 **커널 송신 버퍼가 차는 순간 틱이 밀린다.** 5,000 NPC × 초당 명령이면 반드시 찬다.

그래서 `TcpGameServerLink`는 이렇게 나눈다(§6).

- `Enqueue` — 미리 잡은 우선순위 링에 쓴다. **할당 0.**
- `FlushAsync` — 링의 현재 꼬리를 "여기까지가 이번 배치"라고 게시하고 `ValueTask.CompletedTask`를 돌려준다. **소켓을 만지지 않는다.**
- 실제 송신은 **전용 센더 태스크**가 한다. 수신도 **전용 리시버 태스크**가 하고 `Channel<GameEvent>`에만 쓴다.

`lock`·`SemaphoreSlim`을 쓰지 않는다. 틱 스레드와 센더 태스크 사이는 SPSC 링과 `Volatile` 읽기/쓰기뿐이다.

### 3.4 N1~N8은 와이어에서도 그대로다

| # | 와이어에서 무엇을 뜻하는가 |
|---|---|
| N1 | 프레임에 **응답을 기다리는 메시지가 없다.** `CommandBatch`를 보내고 답을 기다리는 코드를 만들지 않는다 |
| N2 | `WireCommand`·`WireEvent`는 **unmanaged struct**다. 참조 필드·배열 필드·`object` 없음 |
| N3 | **두 프로토콜 모두 `string` 필드가 0개다.** 해시조차 `WireHash(ulong×4)`로 싣는다 (§5.4) |
| N4 | 시간은 `long Tick`뿐이다. 하트비트에도 `DateTime`이 없다 |
| N5 | `WireCommand.Correlation` 필수 |
| N6 | `WireEvent.Sequence`는 **게임서버 프로세스 수명 동안 순증한다. 재접속해도 리셋하지 않는다** — §5.6. 리셋하면 `EventApplier`가 전부 중복으로 버린다 |
| N7 | 재접속 시 재동기화 이벤트를 다시 보내도 상태가 같아야 한다. `EventApplier`가 NPC별 `LastEventSequence`로 막는다 |
| N8 | **한 번의 `FlushAsync` = 한 개의 프레임.** 배치 경계가 와이어에 그대로 보존된다 |

### 3.5 플레이어가 쓴 문자열은 어디에도 없다

`CLAUDE.md` §2.5의 프롬프트 인젝션 방어는 "프롬프트에 넣지 마라"가 아니라 **"구조적으로 넣을 수 없게 하라"**가 목적이다. 링크 패킷에 `string`이 없으므로 게임서버가 무엇을 받든 NPC 서버로 문자열을 넘길 방법이 없다.

이 성질을 클라이언트 프로토콜까지 밀어 올린다. **닉네임도 채팅도 없다.** 클라이언트는 `Player 1`처럼 id로 표시한다. 나중에 닉네임이 필요하면 클라이언트 프로토콜에만 넣고 게임서버에서 끊는다는 것을 문서에 남긴 뒤 넣는다.

---

## 4. 프로젝트 구성과 의존

```
src/Npc.Wire            ← Npc.Contracts + MemoryPack        (신규)
src/Npc.Gateway         ← Npc.Contracts + Npc.Wire          (수정: TcpGameServerLink 구현)
src/Npc.MasterData      ← Npc.Core                          (수정: NpcRoster 추가 §10)
src/Npc.Host            ← 전부                               (수정: --link tcp 배선 §10.3)

testbed/Npc.TestBed.Protocol  ← Npc.Wire                    (신규, 프레임 코덱 재사용)
testbed/Npc.TestGameServer    ← Npc.Sim, Npc.MasterData,    (신규, Exe)
                                Npc.Wire, Npc.TestBed.Protocol
testbed/Npc.TestClient        ← Npc.MasterData,             (신규, WinExe, net10.0-windows)
                                Npc.TestBed.Protocol
```

**의존 규칙 확인.**

- `Npc.Runtime`은 `Npc.Wire`를 **참조하지 않는다.** 런타임이 전송 표현을 알 이유가 없다.
- `Npc.Contracts`·`Npc.Core`의 참조는 그대로 0이다.
- `Npc.Gateway → Npc.Wire`가 새로 생긴다. `Npc.Wire`는 `Npc.Contracts`만 참조하므로 이 간선으로 LLM도 마스터데이터도 들어오지 않는다.
- `testbed/*`는 아무도 참조하지 않는다. **단방향 잎(leaf)이다.**
- **`Npc.Tests`는 `Npc.TestClient`를 참조하지 않는다.** 참조하면 테스트 프로젝트가 `net10.0-windows`로 끌려간다. 클라이언트는 수동 확인 대상이고, 프로토콜·게임서버만 테스트한다.

**TFM.** 전부 `net10.0`, `Npc.TestClient`만 `net10.0-windows` + `UseWindowsForms`. 솔루션 전체 빌드는 Windows에서만 된다 — 이 저장소는 이미 Windows 전용 스크립트(`build.ps1`, `tools/*.ps1`)를 쓰고 있으므로 새로 생기는 제약이 아니다.

**패키지.** `MemoryPack` 1.21.4 (`src/Npc.Wire`, `testbed/Npc.TestBed.Protocol`). 중앙 패키지 관리(`Directory.Packages.props`)는 이 저장소에 없다 — 기존 방식대로 각 `csproj`에 버전을 적는다.

---

## 5. 링크 프로토콜 — NPC 서버 ↔ 게임서버

### 5.1 프레임

```
 0      1      2      3      4      5      6      7      8 ...
+------+------+------+------+------+------+------+------+---------------+
|      PayloadLength (u32)   | Kind | Ver  |   Reserved  |    Payload    |
+------+------+------+------+------+------+------+------+---------------+
        little-endian          u8     u8      u16 = 0     MemoryPack
```

- 헤더 **8바이트 고정**. `PayloadLength`는 헤더를 뺀 바이트 수다.
- `Ver = 1`. 다르면 즉시 `Bye(ProtocolViolation)` 후 연결을 끊는다.
- `PayloadLength > 1,048,576`(1MiB)이면 연결을 끊는다. **길이 접두 프로토콜에서 이 검사를 빼면 잘못된 4바이트 하나로 프로세스가 죽는다.**
- 페이로드는 `MemoryPackSerializer`가 만든 바이트 그대로다. 헤더는 손으로 쓴다 — MemoryPack 안에 길이를 또 넣지 않는다(진실의 출처를 둘로 만들지 않는다).

### 5.2 메시지

| Kind | 이름 | 방향 | 페이로드 |
|---|---|---|---|
| 1 | `Hello` | GS → NPC | `WireHello` |
| 2 | `HelloAck` | NPC → GS | `WireHelloAck` |
| 3 | `CommandBatch` | NPC → GS | `WireCommand[]` |
| 4 | `EventBatch` | GS → NPC | `WireEvent[]` |
| 5 | `Heartbeat` | 양방향 | `WireHeartbeat` |
| 6 | `Bye` | 양방향 | `WireBye` |

`CommandBatch`·`EventBatch`의 페이로드는 **unmanaged 배열**이다. MemoryPack은 unmanaged 원소 배열을 원시 메모리로 복사한다(`WriteUnmanagedArray`) — 원소마다 태그를 붙이지 않으므로 크기도 작고 할당도 없다. 이것이 `docs/02` §7-1의 "패킷이 전부 값 타입이라 `MemoryMarshal`로 blittable 처리도 가능하다"를 실제로 쓰는 지점이다.

> **이 최적화가 실제로 걸리는지 T6-02에서 실측한다.** 배열 하나의 직렬화 길이가 `길이 접두 + count × sizeof(T)` 인지 단언한다. 안 걸리면(원소마다 태그가 붙으면) §15의 대체안으로 간다 — `Npc.Wire` 안쪽만 바뀐다.

> **아키텍처 가정.** 원시 메모리 복사는 엔디언·패딩에 의존한다. 양쪽이 같은 x64 .NET 런타임인 것을 전제한다. 이기종 게임서버에 붙일 때는 `WireCommand`/`WireEvent`에 명시적 필드별 쓰기를 하는 커스텀 포매터로 바꾼다 — **바꿀 자리가 `Npc.Wire` 한 곳뿐인 것이 이 설계의 요점이다.**

### 5.3 `WireCommand` / `WireEvent`

`NpcCommand`·`GameEvent`와 필드가 1:1이다. 순서는 큰 타입부터 — 패딩을 없애 크기를 고정한다.

```csharp
// src/Npc.Wire/WireCommand.cs
[MemoryPackable]
public partial struct WireCommand          // 56 bytes, unmanaged
{
    public long   IssuedAt;                // Tick
    public int    Npc;
    public int    TargetNpc;
    public int    TargetPlayer;
    public int    Amount;
    public uint   Correlation;
    public float  PosX, PosY, PosZ;        // WorldPos
    public ushort TargetPoi;
    public ushort Item;
    public ushort Animation;
    public ushort Dialogue;
    public ushort Archetype;
    public ushort Zone;
    public byte   Kind;                    // NpcCommandKind
    public byte   Priority;                // CommandPriority
    public byte   Visual;                  // VisualState
    public byte   Flags;

    public static WireCommand From(in NpcCommand c);
    public readonly NpcCommand To();
}
```

```csharp
// src/Npc.Wire/WireEvent.cs
[MemoryPackable]
public partial struct WireEvent            // 64 bytes, unmanaged
{
    public long   Sequence;
    public long   OccurredAt;              // Tick
    public int    Npc;
    public int    OtherNpc;
    public int    Player;
    public int    Amount;
    public uint   Correlation;
    public float  PosX, PosY, PosZ;
    public float  Heading;
    public ushort Poi;
    public ushort Zone;
    public ushort Item;
    public short  Hp;
    public short  Stamina;
    public byte   Kind;                    // GameEventKind
    public byte   Code;

    public static WireEvent From(in GameEvent e);
    public readonly GameEvent To();
}
```

**크기를 테스트로 못 박는다**(`Wire_LayoutIsFrozen`). `sizeof(WireCommand) == 56`, `sizeof(WireEvent) == 64`. 누가 필드를 끼워 넣으면 이 테스트가 먼저 깨진다.

**대역폭 어림.** NPC 500, 관측 대상 256 상한(`TransformEmitter.MaxPerTick`) 기준 이벤트는 최악 256 × 64B × 10Hz ≈ **164KB/s**. 명령은 NPC당 스텝 경계에서만 나가므로 훨씬 적다. 로컬 소켓에서 문제가 되지 않는다.

### 5.4 제어 메시지

```csharp
public readonly record struct WireHash(ulong A, ulong B, ulong C, ulong D)
{
    public static WireHash FromHex(string sha256Hex);   // 32바이트 → ulong 4개
    public readonly string ToHex();                     // 로그용. 와이어에는 안 나간다
}

[MemoryPackable] public partial struct WireHello        // GS → NPC
{
    public int      ProtocolVersion;    // = 1
    public int      TickRate;           // = 10
    public int      TimeScale;
    public int      NpcCount;
    public long     StartTick;          // 지금 게임서버의 틱
    public WireHash MasterData;         // MasterDataSet.ContentHash
    public WireHash Roster;             // §10.2
}

[MemoryPackable] public partial struct WireHelloAck     // NPC → GS
{
    public int      ProtocolVersion;
    public int      TimeScale;
    public int      NpcCount;
    public WireHash MasterData;
    public WireHash Roster;
    public byte     Accepted;           // 0/1
    public byte     RejectCode;         // LinkRejectCode
}

[MemoryPackable] public partial struct WireHeartbeat { public long Tick; public long Sequence; }
[MemoryPackable] public partial struct WireBye       { public byte Code; }   // LinkByeCode

public enum LinkRejectCode : byte
{ None = 0, ProtocolVersion, MasterDataMismatch, RosterMismatch, TimeScaleMismatch }

public enum LinkByeCode : byte
{ Shutdown = 0, ProtocolViolation, HandshakeRejected, Timeout }
```

**해시를 왜 문자열로 안 싣는가.** N3 때문이다. 그리고 `ulong` 4개면 정확히 SHA-256 32바이트라 손실도 없다. 로그에 찍을 때만 hex로 되돌린다.

### 5.5 핸드셰이크

```
NPC 서버                                            게임서버
   │                                                   │ (listen :7010)
   │ ─────────── TCP connect ─────────────────────────►│
   │                                                   │ accept
   │◄────────── Hello{ver,tickRate,timeScale,          │
   │                   npcCount,startTick,             │
   │                   masterDataHash,rosterHash} ─────│
   │ 검증:                                              │
   │   ver == 1                                        │
   │   timeScale == 내 GameClock.TimeScale             │
   │   masterDataHash == 내 MasterDataSet.ContentHash  │
   │   rosterHash == 내 NpcRoster.Hash                 │
   │ ─────────── HelloAck{accepted, rejectCode} ──────►│
   │                                     거절이면 Bye 후 종료 │
   │◄────────── EventBatch(NpcSpawned × N) ────────────│ 로스터 순서, 프레임당 ≤256
   │◄────────── EventBatch(ZoneStateChanged × Z) ──────│ 존 상태 초기화
   │◄────────── EventBatch(TickSync) ──────────────────│ 이후 매 틱
   │ ─────────── CommandBatch ─────────────────────────►│ 틱 루프의 FlushAsync
```

**거절은 조용히 넘기지 않는다.** 마스터데이터가 다른 두 프로세스를 붙이면 POI code가 어긋나 NPC가 엉뚱한 곳으로 간다. 원인을 찾는 데 하루가 든다. **연결 시점에 죽이는 편이 싸다.**

`--force` 같은 우회 옵션을 만들지 않는다.

### 5.6 재접속과 시퀀스

| 항목 | 규약 |
|---|---|
| 시퀀스 리셋 | **금지.** 게임서버 프로세스가 사는 동안 `SimWorld._sequence`는 계속 증가한다. 재접속해도 이어 붙는다 |
| 왜 | `EventApplier`는 `ev.Sequence <= LastEventSequence[npc]`인 이벤트를 **중복으로 버린다**(N7). 리셋하면 재접속 후 모든 이벤트가 버려지고 NPC가 영원히 멈춘다 |
| 재접속 시 재동기화 | 게임서버가 `NpcSpawned`(전원) + 존 상태를 다시 보낸다. 새 시퀀스를 달고 나가므로 멱등하게 반영된다 |
| 끊긴 동안 쌓인 명령 | **버린다.** 상관 ID는 이미 타임아웃으로 정리됐고, 뒤늦게 도착한 명령은 NPC를 과거로 되돌린다. `LinkStats.CommandsDropped`에 계상한다 |
| 백오프 | 250ms → 500 → 1s → 2s → 4s 상한. 지터 없음(결정론 필요 없음. 벽시계 영역이다) |
| 하트비트 | 양방향 1초(10틱)마다. **3초** 무수신이면 `Degraded` → 소켓을 닫고 재접속 |

### 5.7 역압

`docs/02` §1의 규칙 그대로다. `LoopbackGameServerLink`가 이미 구현한 정책을 **`PriorityCommandRing`으로 추출해 두 링크가 공유한다**(T6-05).

- 큐가 차면 **자기보다 낮은 우선순위**(`Cosmetic` → `Normal`)부터 밀어낸다.
- `Critical`은 무손실이다.
- 드롭 수는 `LinkStats.CommandsDropped`에만 나타난다. 예외를 던지지 않는다.

정책을 복사하지 않고 추출하는 이유: 두 벌이 되면 반드시 갈라지고, `Link_Backpressure` 테스트는 한쪽만 본다.

---

## 6. `TcpGameServerLink` 설계

### 6.1 스레드 모델

```
 [틱 루프 스레드]                    [센더 태스크]                [리시버 태스크]
        │                                 │                            │
  Enqueue(cmd) ──► PriorityCommandRing    │                            │
        │           (사전 할당, 할당 0)     │                            │
  FlushAsync() ──► Volatile.Write(         │                            │
        │            _publishedTail)       │                            │
        │          return CompletedTask    │                            │
        │                                 ▼                            │
        │                    publishedTail 까지 Pop                      │
        │                    → WireCommand[] 스테이징                    │
        │                    → MemoryPack → PipeWriter                  │
        │                    → socket.SendAsync                         │
        │                                                              ▼
        │                                              PipeReader → 프레임 분해
        │                                              → WireEvent[] → GameEvent
        │                                              → Channel.Writer.TryWrite
        ▼                                                              │
  link.Events.TryRead ◄────────────────────────────────────────────────┘
```

- **채널은 `SingleReader = true, SingleWriter = true` 무제한 채널.** 기록자는 리시버 태스크 하나뿐이다.
- 센더 태스크는 게시된 꼬리가 없으면 `await Task.Delay(1, ct)`로 쉰다. 10Hz 배치에 1ms 폴링은 무시할 수 있고, `SemaphoreSlim`을 틱 루프 쪽에서 건드리지 않아도 된다(`CLAUDE.md` §2.1).
- **한 번의 `FlushAsync` = 한 개의 `CommandBatch` 프레임.** 센더는 게시 경계 단위로만 프레임을 만든다(N8).

### 6.2 할당 0

`FlushAsync`가 틱 루프 안에서 불리므로 그 경로에 할당이 있으면 안 된다. 이 설계에서 그 경로는 `Volatile.Write` 한 줄이다. 직렬화·소켓 쓰기는 다른 스레드에 있고, 그쪽 버퍼는 전부 사전 할당(`WireCommand[]` 스테이징, `PipeWriter` 풀 메모리)이다.

**테스트로 강제한다** — `TcpLink_FlushDoesNotAllocate`: 워밍업 후 `Enqueue` + `FlushAsync` 10,000회의 `GC.GetAllocatedBytesForCurrentThread()` 델타가 0.

### 6.3 상태 전이

| From | 계기 | To |
|---|---|---|
| `Disconnected` | `ConnectAsync` 호출 | `Connecting` |
| `Connecting` | `HelloAck(accepted=1)` 송신 완료 | `Connected` |
| `Connecting` | 접속 실패 / 핸드셰이크 거절 | `Faulted`(거절) 또는 `Connecting` 재시도(접속 실패) |
| `Connected` | 송신 실패 · 3초 하트비트 무수신 · EOF | `Degraded` |
| `Degraded` | 소켓 정리 후 재시도 | `Connecting` |
| 임의 | `DisposeAsync` | `Disconnected` |

`Faulted`는 **사람이 고쳐야 하는 상태**다(프로토콜 버전·마스터데이터 불일치). 재시도하지 않는다 — 무한 재시도로 로그만 채우면 원인이 묻힌다.

### 6.4 통계

`LinkStats`의 6개 필드를 전부 채운다. 지금 `LoopbackGameServerLink`는 이벤트 쪽 3개를 0으로 두고 있는데, TCP 링크는 실제로 센다.

| 필드 | 의미 |
|---|---|
| `CommandsEnqueued` | `Enqueue` 호출 수 |
| `CommandsFlushed` | 소켓에 실제로 나간 명령 수 |
| `CommandsDropped` | 역압 드롭 + 재접속 시 폐기 |
| `EventsReceived` | 채널에 쓴 이벤트 수 |
| `EventGapsDetected` | 수신 `Sequence`의 불연속 합 (N6) |
| `PendingCommands` | 링에 남아 있는 수 |

---

## 7. 테스트 게임서버 설계

### 7.1 구성

```
testbed/Npc.TestGameServer/
  Program.cs                  CLI 파싱 → 조립 → 10Hz 루프 → 종료 요약
  GameServerOptions.cs        옵션 + 파싱 (Npc.Host/HostOptions.cs 를 본뜬다)
  GameWorld.cs                SimWorld + 하위 시뮬 조립 + Tick(now)
  Link/LinkListener.cs        :7010 accept. 동시 세션 1개 (두 번째는 거절)
  Link/LinkSession.cs         핸드셰이크 · 프레임 수신 → CommandInbox · 이벤트 → 프레임
  Link/CommandInbox.cs        SPSC 링 (Npc.Host/Program.cs 의 CommandRing 과 같은 모양)
  Client/ClientListener.cs    :7020 accept. 최대 4 세션
  Client/ClientSession.cs     세션 상태: PlayerId · 선택 NPC · AOI 반경 · 송신 큐
  Client/SnapshotBuilder.cs   AOI 스냅샷 조립
  Client/ControlHandler.cs    시나리오 제어 적용
  World/PlayerRegistry.cs     플레이어 이동 · 근접 판정 · 상호작용/공격 이벤트
  World/MirrorLog.cs          명령·이벤트 미러 링 (클라 로그 패널의 원천)
```

**시뮬 로직을 새로 짜지 않는다.** `MovementSim`·`InteractionSim`·`NeedsSim`·`TransformEmitter`·`FaultInjector`·`ScenarioRunner`는 `Npc.Sim`의 것을 그대로 쓴다. 새로 만드는 것은 `PlayerRegistry` 하나다.

### 7.2 틱 순서

`Npc.Host/Program.cs`의 `SimDriver.Tick`과 **같은 순서를 지킨다.** 순서가 다르면 같은 시나리오가 다르게 흐르고, 루프백과 소켓을 비교할 수 없게 된다.

```
1. CommandInbox 배수 → SimWorld.ApplyCommand   (+ MirrorLog 기록)
2. ScenarioRunner.Tick                          (jsonl 주입)
3. PlayerRegistry.Tick                          (PlayerBots 자리. 입력 적용 → 이동 → 근접 변화)
4. MovementSim.Tick
5. InteractionSim.Tick
6. TransformEmitter.Tick
7. NeedsSim.Tick
8. SimWorld.Tick                                (TickSync 발행)
9. LinkSession.FlushEvents                      (이벤트 채널 → EventBatch 프레임 1개)
10. 2틱마다: ClientSession.SendSnapshot + SendLogs
```

**페이싱.** 벽시계 기준 100ms 주기. `Npc.Host`의 펌프와 같은 방식(누적 오차 없는 `due = tick * 1000 / 10` 계산)을 쓴다. 게임서버는 **NPC 서버를 기다리지 않는다** — 락스텝이 없는 것이 이 테스트 베드의 핵심이다(§1).

### 7.3 `PlayerRegistry`

`Npc.Sim.PlayerBots`를 **쓰지 않는다.** 대신 같은 자리에 들어가 같은 일(`ObservedByPlayer` 갱신 + `PlayerProximity` 발행)을 한다. 두 개가 같은 배열에 쓰면 서로의 판정을 덮어쓴다.

| 항목 | 규약 |
|---|---|
| 플레이어 상태 | `PlayerId`, `WorldPos`, `Heading`, `ZoneCode`, `Run`. 접속 시 `town_center` 중심에서 시작 |
| 이동 | 입력 `(DirX, DirZ)`를 정규화 → `speed × TimeScale / 10` m/틱. 기본 걷기 2.5, 뛰기 5.0 게임m/게임초 |
| 왜 게임 시간 기준인가 | NPC가 게임 시간으로 움직이기 때문이다. `--time-scale 60`이면 NPC의 겉보기 속도가 실시간의 60배다. 플레이어만 실시간이면 화면에서 멈춰 있는 것처럼 보인다 |
| 존 판정 | 가장 가까운 POI의 존. 매 틱 |
| 근접 | **5틱마다.** 플레이어가 있는 존 + 인접 존의 NPC만 검사. 유클리드 거리 ≤ 200m → `Enter`, ≥ 220m → `Leave`(히스테리시스 20m). 상태가 바뀐 NPC만 이벤트를 낸다 |
| 왜 히스테리시스 | 경계에 선 NPC가 매 판정마다 Enter/Leave를 번갈아 내면 재계획 큐가 폭주한다 |
| Interact | 거리 ≤ 30m일 때만. `PlayerInteracted{Npc, Player}` 발행 |
| Attack | 거리 ≤ 30m일 때만. `CombatStarted` + `DamageTaken{Amount}` 발행 + `NeedsSim`의 HP 차감 |
| 봇 플레이어 | `--bots N`이면 결정론 랜덤 워크(`PlanHash.Mix(seed, bot, tick/20)`)하는 가상 플레이어를 **같은 경로로** 돌린다. 클라이언트 없이도 데모가 돈다 |

**왜 상호작용에 거리 제한을 두는가.** 클라이언트가 화면 밖 NPC를 찍어서 인터럽트를 걸 수 있으면 "플레이어 근접이 인지 LOD를 바꾼다"는 검증이 무의미해진다.

### 7.4 `MirrorLog`

클라이언트 로그 패널의 원천이다. 링 버퍼 1,024칸 두 개(명령·이벤트).

- 명령: `(Tick, NpcId, Kind, TargetPoi, Correlation)`
- 이벤트: `(Tick, NpcId, Kind, Code, Amount)`

**전량을 클라이언트로 보내지 않는다.** NPC 500이면 초당 수백 건이 나온다. 세션별로 이렇게 거른다.

- 선택된 NPC의 것은 **전부**
- 그 외는 AOI 안 NPC의 것 중 **배치당 최대 32건**(라운드로빈)

### 7.5 CLI

```
--link-port N          NPC 서버 링크 수신 포트 (기본 7010)
--client-port N        클라이언트 수신 포트 (기본 7020)
--npcs N               NPC 수 (기본 300)
--zone <id>[,<id>]     이 존의 NPC 만 뽑는다 (기본 전체) — NPC 서버와 반드시 같아야 한다
--time-scale N         (기본 60) — NPC 서버와 반드시 같아야 한다
--masterdata <dir>     (기본 ./masterdata)
--scenario <jsonl>     이벤트 주입. KillSwitch 줄은 무시한다 (§11.4)
--fail-rate <0~1>      액션 실패 주입
--drop-rate <0~1>      명령 유실 주입
--bots N               가상 플레이어 (기본 0)
--seed N               (기본 20260725)
--player-speed F       걷기 속도, 게임m/게임초 (기본 2.5)
--max-clients N        (기본 4)
--headless             콘솔 통계만. 클라 접속은 계속 받는다
-h, --help
```

종료 시 한 줄 요약을 찍는다 — `ticks · commands in · events out · dropped · clients · p99 tick ms`.

---

## 8. 클라이언트 프로토콜 — 게임서버 ↔ 클라이언트

프레임 헤더는 §5.1과 **같은 것을 쓴다**(`Npc.Wire.FrameCodec` 재사용). `Kind`만 다른 열거형이다.

### 8.1 서버 → 클라이언트

| Kind | 이름 | 페이로드 | 주기 |
|---|---|---|---|
| 1 | `SrvHello` | `{ ProtocolVersion, TickRate, TimeScale, PlayerId, NpcCount, MinX, MinZ, MaxX, MaxZ, MasterData(WireHash) }` | 접속 1회 |
| 2 | `Snapshot` | `{ Tick, GameDay, GameHour, TimeOfDay, EntityCount, EntityState[] }` | 2틱(5Hz) |
| 3 | `ZoneStates` | `{ Tick, ZoneState[] }` — `{ ZoneCode, RegionState, Climate }` | 변화 시 + 5초마다 |
| 4 | `CommandLog` | `{ LogCommand[] }` — `{ Tick, Npc, Kind, TargetPoi, Correlation }` | 2틱 |
| 5 | `EventLog` | `{ LogEvent[] }` — `{ Tick, Npc, Kind, Code, Amount }` | 2틱 |
| 6 | `LinkStatus` | `{ Connected, GsTick, NpcServerTick, CommandsIn, EventsOut, Dropped, Gaps }` | 1초 |
| 7 | `Pong` | `{ ClientStamp, ServerTick }` | 요청 시 |

```csharp
[MemoryPackable]
public partial struct EntityState          // 30 bytes + 정렬 패딩 2 = sizeof 32, unmanaged
{
    public int    Id;                      // NPC 첨자 또는 PlayerId
    public float  X, Z;
    public float  Heading;
    public ushort Poi;                     // 현재 POI (0 = 이동 중)
    public ushort TargetPoi;               // 이동 목적지 (0 = 정지). 클라가 선을 그린다
    public ushort Zone;
    public ushort Archetype;
    public short  Hp;
    public byte   Kind;                    // 0 = NPC, 1 = Player
    public byte   Visual;                  // VisualState
    public byte   StateFlags;              // bit0 Moving · bit1 Working · bit2 InCombat · bit3 Observed
    public byte   Reserved;
}
```

**AOI.** 플레이어 위치 반경 1,200m 안, 최대 **256** 엔티티. 초과하면 가까운 순으로 자른다. 다른 클라이언트의 플레이어도 포함한다.

**NPC 위치는 어떻게 구하는가.** `TransformEmitter.Interpolate(npc, now)`가 `public`이다. 그대로 쓴다 — NPC 서버로 나가는 `NpcTransform`은 관측 대상만 발행되지만, 클라이언트 스냅샷은 게임서버 내부 상태에서 직접 만들므로 그 상한에 걸리지 않는다.

### 8.2 클라이언트 → 서버

| Kind | 이름 | 페이로드 | 비고 |
|---|---|---|---|
| 64 | `CliHello` | `{ ProtocolVersion, ClientVersion }` | **문자열 없음**(§3.5) |
| 65 | `Input` | `{ Seq, DirX, DirZ, Run }` | 10Hz. 키가 눌린 동안만 |
| 66 | `Interact` | `{ NpcId }` | 거리 ≤ 30m |
| 67 | `Attack` | `{ NpcId, Amount }` | 거리 ≤ 30m |
| 68 | `Select` | `{ NpcId }` | 로그 필터 + 인스펙터 대상 |
| 69 | `Control` | `{ Kind, ZoneCode, Code, Amount }` | §8.3 |
| 70 | `Ping` | `{ ClientStamp }` | 1초 |

서버 쪽 Kind는 1~63, 클라 쪽은 64~127로 갈라 둔다. 로그에서 방향을 헷갈리지 않는다.

### 8.3 `Control`

| ControlKind | 인자 | 효과 |
|---|---|---|
| 1 `SetZoneState` | `ZoneCode`, `Code`(RegionState) | `ZoneStateChanged` 주입 → **존 전체 플랜 스왑** |
| 2 `SetWeather` | `ZoneCode`, `Code`(Climate) | `WeatherChanged` 주입 |
| 3 `SkipTime` | `Amount`(게임 분) | 게임서버 틱을 `Amount × 60 × 10 / TimeScale` 만큼 점프 |
| 4 `SetFaultRate` | `Code`(0=fail,1=drop), `Amount`(‱) | 런타임 고장 주입률 변경 |
| 5 `Despawn` | `NpcId` | 해당 NPC `Despawn` — `TargetGone` 경로 확인용 |

**`SkipTime`의 부작용을 문서에 남긴다.** 틱을 점프시키면 `MovementSim._arriveAt`·`InteractionSim._completeAt`이 전부 만료되어 진행 중인 이동·작업이 즉시 끝난다. 데모 편의 기능이고 **결정론 리플레이 대상이 아니다.** 시나리오 파일로 돌리는 회차에서는 쓰지 않는다.

**킬스위치는 여기 없다.** 그것은 NPC 서버 안쪽 상태이고 링크로 보낼 수단이 없다(`GameEvent`에 그런 종류가 없고, 만들면 N 규칙을 어긴다). §11.4를 본다.

---

## 9. 테스트 클라이언트 설계

### 9.1 WinForms를 고른다

| 기준 | WinForms | MonoGame |
|---|---|---|
| 그려야 하는 것 | 점·선·원·글자뿐 → GDI+로 충분 | 오버스펙 |
| UI 위젯(목록·버튼·탭·트리) | **공짜** | 전부 직접 만들어야 한다 |
| 콘텐츠 파이프라인·에셋 | 필요 없음 | 설정·빌드 통합 필요 |
| 프로젝트 설정 | `net10.0-windows` + `UseWindowsForms` 두 줄 | 템플릿·패키지·플랫폼별 설정 |
| 이 저장소와의 궁합 | 이미 Windows 전용 스크립트를 쓴다 | — |

**WinForms로 간다.** 목적은 "NPC의 동작을 확인"이지 게임을 만드는 것이 아니다. 인스펙터·로그·제어 버튼이 화면의 절반을 차지하는데, 그 절반이 공짜인 쪽이 옳다.

성능 우려(엔티티 수백 개 GDI+)는 AOI 상한 256으로 이미 막혀 있다. 그래도 **아키타입 색상별로 묶어 `Graphics.FillRectangles(brush, RectangleF[])`로 배치 호출**한다 — 개별 `FillEllipse` 256번보다 확실히 빠르고, 어차피 NPC는 사각 점으로 그린다.

### 9.2 화면

```
┌───────────────────────────────────────────────┬──────────────────────────┐
│                                               │ [인스펙터][로그][제어][링크]│
│                                               │                          │
│                 맵 패널                        │  npc #128 blacksmith     │
│         (GDI+, DoubleBuffered)                │  zone town_west_crafts   │
│                                               │  poi  smithy_001_05      │
│   ○ 존 배경원      · POI      ■ NPC           │  lod  1                  │
│   ▲ 내 플레이어    ─ 이동선   ◎ 선택           │  plan bucket #412        │
│                                               │   goal 아침 제작 준비      │
│                                               │   0 MoveTo  smithy  ✓    │
│                                               │ ▶ 1 Craft   sword   ●    │
│                                               │   2 MoveTo  market       │
│                                               │  flags AtWorkplace|IsDay │
├───────────────────────────────────────────────┤  inv   iron_ingot × 3    │
│ day 2  09:14 Morning │ town_center: Peace/Fair │                          │
│ link ● 12ms  gs 5,412  npc 5,410 (−2)  drop 0 │                          │
└───────────────────────────────────────────────┴──────────────────────────┘
```

### 9.3 렌더링

| 요소 | 그리는 법 |
|---|---|
| 존 | 존 소속 POI의 centroid를 중심으로, 최원 POI 거리 + 40m 반경의 옅은 원. 존 상태에 따라 테두리 색(Peace 회색 · Alert 주황 · War 빨강) |
| POI | 4px 사각. 타입별 회색조. 마우스 오버 시 id 툴팁 |
| NPC | 6px 사각. **색 = 아키타입 code × 137.5°의 HSV**(황금각 팔레트 — 40개가 균등하게 갈린다). `VisualState`에 따라 테두리(Working 흰 테두리 · Fighting 빨간 테두리 · Sleeping 반투명) |
| 이동선 | `TargetPoi != 0`이면 현재 위치 → 목표 POI로 옅은 선 |
| 플레이어 | 10px 삼각형. 내 플레이어는 채움, 남은 외곽선 |
| 선택 NPC | 노란 원 하이라이트 + 목표 POI까지 굵은 선 |
| 카메라 | 기본은 플레이어 추적. 휠 줌(0.05~4배), 드래그 팬, `Home` 키 = 월드 전체 맞춤, `Space` = 플레이어 추적 복귀 |

**보간.** 스냅샷은 5Hz다. 그대로 그리면 200ms마다 순간이동한다. **마지막 두 스냅샷을 들고 `renderTime = now − 200ms` 시점을 선형 보간**한다. 새 스냅샷이 늦으면 외삽하지 않고 마지막 위치에 멈춘다 — 데모에서는 튀는 것보다 멈추는 것이 낫다.

렌더 루프는 `System.Windows.Forms.Timer` 16ms(≈60fps) → `Invalidate()`.

### 9.4 입력

| 키/마우스 | 동작 |
|---|---|
| `W A S D` | 이동(월드 축 기준). 눌린 동안 10Hz로 `Input` 전송 |
| `Shift` | 달리기 |
| 좌클릭 | 가장 가까운 엔티티 선택(15px 안) → `Select` |
| `E` | 선택 NPC에게 `Interact`(≤30m) |
| `R` | 선택 NPC에게 `Attack`(≤30m) |
| `Tab` | 다음 NPC 선택 |
| 휠/드래그 | 줌/팬 |
| `Home` / `Space` | 월드 맞춤 / 플레이어 추적 |

### 9.5 인스펙터 — NPC 서버 HTTP 직결

선택된 NPC에 대해 **1초마다** `GET {npcHttp}/npc/{id}`를 폴링한다. 응답이 곧 `NpcTrace`(`Npc.Host/Api/NpcTraceEndpoint.cs`)이고, 여기에 플랜 id·출처(`bucket`/`individual`/`fallback`)·goal·스텝 목록·현재 스텝·월드 플래그·인벤토리·최근 사건이 전부 들어 있다.

**이 패널이 데모의 핵심이다.** "NPC가 왜 저기로 가는가"에 답하는 화면은 이것뿐이다. 게임서버는 그 답을 모른다.

- 실패해도 조용히 넘긴다(NPC 서버가 죽어 있어도 클라이언트는 계속 돈다).
- 응답 파싱은 `System.Text.Json`. 소스 생성 컨텍스트를 쓴다.
- 링크 패널에는 `GET /metrics`의 요약(틱 p99·Gen0·재계획 큐·LLM 호출 수)을 같이 띄운다.

### 9.6 로그 패널

`CommandLog`·`EventLog`를 시간 역순으로 최대 500줄. **여기서 처음으로 id가 사람 말이 된다** — 클라이언트가 `Npc.MasterData`를 참조하므로 `PoiId 137` → `smithy_001_05`, `ActionFailReason 1` → `Unreachable`로 풀어 쓴다.

필터: 선택 NPC만 / 실패만 / 인터럽트 계열만.

---

## 10. 마스터데이터와 로스터 일치

### 10.1 문제

두 프로세스가 각자 `npc_instances.json`(5,000마리)에서 NPC를 뽑는다. 뽑는 규칙이 다르면 **첨자 7번이 서로 다른 NPC**가 되고, 그러면 대장장이에게 밭을 갈라고 명령하게 된다. 증상은 "가끔 이상하게 행동한다"로 나타나서 원인을 찾기가 매우 어렵다.

현재 규칙은 `Npc.Host/Program.cs`에 인라인으로 박혀 있다.

```csharp
chosen[i] = instances[(int)((long)i * instances.Count / npcs)];   // 균등 간격
```

### 10.2 해법

`Npc.MasterData`에 `NpcRoster`를 만들어 **양쪽이 같은 함수를 부른다.**

```csharp
public sealed class NpcRoster
{
    public static NpcRoster Select(NpcInstanceTable all, int count, ReadOnlySpan<ZoneId> zoneFilter);

    public ImmutableArray<NpcInstanceDef> Npcs { get; }
    public int Count { get; }
    public string Hash { get; }    // SHA-256 of "id,archetype,home,workplace" 줄들
}
```

- **기존 선택 결과를 바꾸지 않는다.** `zoneFilter`가 비면 위 공식과 **바이트 단위로 같은** 결과여야 한다. 테스트로 못 박는다(`Roster_SelectionMatchesLegacyFormula`, npcs = 1·7·500·5000).
- `zoneFilter`가 있으면 먼저 그 존의 인스턴스만 남기고 같은 균등 간격을 적용한다.
- `Hash`가 핸드셰이크에 실린다(§5.4·§5.5). 다르면 연결이 거절된다.

`Npc.Host/Program.cs`의 인라인 루프를 `NpcRoster.Select` 호출로 바꾼다. **동작이 바뀌면 안 된다** — 기존 결정론 테스트가 그대로 통과해야 한다.

### 10.3 `Npc.Host` 배선 변경

```
--link tcp             TcpGameServerLink 를 쓴다
--gs-host <host>       (기본 127.0.0.1)
--gs-port N            (기본 7010)
--zone <id>[,<id>]     로스터 존 필터. 게임서버와 같아야 한다
--dev-control          POST /control/* 을 연다 (§11.4)
```

`LinkKind`에 `Tcp`를 추가한다. `NpcHost.Create`의 링크 스위치에서 `Tcp`는 `SimDriver`를 만들지 않는다 — `Replay`와 같은 경로다(`PumpAsync`가 즉시 반환하고, 세계를 미는 것은 게임서버다).

`--link record --gs-port ...` 조합도 살린다. `RecordingGameServerLink`는 내부 링크를 감싸는 데코레이터이므로 TCP 링크도 감쌀 수 있다. 그러면 **소켓으로 받은 이벤트 열을 그대로 기록해 나중에 `--link replay`로 재생**할 수 있다.

---

## 11. 데모 시나리오

`testbed/scenarios/` 아래에 둔다. **기존 `scenarios/`를 고치지 않는다** — 그쪽은 `--time-scale 600`·5,000 NPC 게이트 회차용이고 틱 좌표가 다르다.

`--time-scale 60` 기준 환산: **게임 1시간 = 600틱 = 실시간 60초.** 시작 시각은 06:00(Dawn)이다.

### 11.1 `demo_day.jsonl` — 평범한 하루

주입 이벤트가 없다. 시간만 흐른다.

| 실시간 | 게임 시각 | 볼 것 |
|---|---|---|
| 0:00 | 06:00 Dawn | 집에서 나온다. 존 인구가 주거 → 일터로 이동 |
| 1:00 | 07:00 Morning | 일터 도착. `VisualState.Working` 흰 테두리가 켜진다 |
| 6:00 | 12:00 Day | 시장 존 인구 증가. 상인 계열 활동 |
| 12:00 | 18:00 Evening | **시간대 전환 → 버킷 키 변경 → 대량 플랜 스왑.** 인스펙터의 plan id가 바뀐다 |
| 17:00 | 23:00 Night | 귀가. `go_home_at_night` 인터럽트가 도는 것이 로그에 보인다 |

**이 시나리오가 제일 중요하다.** 이벤트 없이 NPC가 하루를 사는 것 자체가 이 프로젝트의 결과물이다.

### 11.2 `demo_siege.jsonl` — 공성

```jsonc
{"at_tick": 900,  "event": "ZoneStateChanged", "zone": "town_center", "code": "Alert"}
{"at_tick": 1200, "event": "ZoneStateChanged", "zone": "town_center", "code": "War"}
{"at_tick": 1200, "event": "ZoneStateChanged", "zone": "gate_east",   "code": "War"}
{"at_tick": 1200, "event": "ZoneStateChanged", "zone": "town_east_market", "code": "Alert"}
{"at_tick": 1260, "event": "WeatherChanged",   "zone": "town_center", "code": "Storm"}
{"at_tick": 2400, "event": "ZoneStateChanged", "zone": "town_center", "code": "Peace"}
{"at_tick": 2400, "event": "ZoneStateChanged", "zone": "gate_east",   "code": "Peace"}
{"at_tick": 2400, "event": "ZoneStateChanged", "zone": "town_east_market", "code": "Peace"}
{"at_tick": 2400, "event": "WeatherChanged",   "zone": "town_center", "code": "Fair"}
```

실시간 4분. 볼 것: **tick 1200에 존 테두리가 빨개지는 순간 그 존의 NPC 전원이 동시에 다른 계획으로 갈아탄다.** 경비병은 성문으로, 전투 불가 아키타입은 안전한 POI로. `/metrics`의 버킷 전환 카운터가 튄다.

### 11.3 `demo_blackout.jsonl` — 단계적 차단

```jsonc
{"at_tick": 600,  "event": "KillSwitch", "target": "T2"}
{"at_tick": 1200, "event": "KillSwitch", "target": "T1"}
{"at_tick": 1800, "event": "KillSwitch", "target": "PlanStore"}
```

실시간 3분. 볼 것: **아무 일도 일어나지 않는다.** NPC는 계속 움직이고, 틱 시간은 그대로고, 크래시가 없다. 인스펙터의 `PlanKind`가 `bucket` → `fallback`으로 내려간다. 그것이 이 시나리오의 성공이다(`docs/15` §4, `CLAUDE.md` §2.6).

### 11.4 킬스위치를 어떻게 전달하는가

킬스위치는 NPC 서버 안쪽 상태다. 링크로 보낼 수단이 없고, 만들면 N 규칙을 어긴다. 두 경로를 둔다.

1. **스크립트 경로** — 같은 jsonl을 **양쪽에 준다.** 게임서버(`--scenario`)는 `KillSwitch` 줄을 **무시**하고, NPC 서버(`--scenario`)는 `KillSwitch` 줄만 처리한다. 틱 번호는 게임서버가 보내는 `TickSync`로 동기화되어 있으므로 같은 틱에 발동한다.
   - `Npc.Host`에 `KillSwitchSchedule`(틱 기반)을 두고 `NpcServerLoop`의 틱 구간에서 `Advance(tick)`을 부른다. null 체크 한 번이라 틱 예산에 영향이 없다.
   - `ScenarioRunner`의 파서를 재사용한다. **`--link tcp`에서 `ScenarioRunner.Tick(now, world)`를 부르지 않는다** — 이벤트 주입은 게임서버의 일이다.
2. **대화형 경로** — `--dev-control`을 준 경우에만 `POST /control/killswitch?target=T2`를 연다. 클라이언트의 제어 패널 버튼이 이것을 부른다.
   - 기본은 꺼져 있다. **상태를 바꾸는 HTTP를 기본으로 열지 않는다.**

> **알려진 결함.** `TASKS.md`의 T5-21이 미착수다 — `TierWiring`이 없는 티어를 있는 티어로 메꾸므로 `--tier t2`에서 T2를 끊어도 같은 외부 컴파일러가 계속 불린다. **`demo_blackout`은 `--tier none` 또는 `--tier all`로 돌린다.** `--tier t2` 단독 회차를 데모에 쓰려면 T5-21을 먼저 끝낸다.

---

## 12. 실행

```powershell
# 0) 빌드
dotnet build -c Release

# 1) 게임서버 (먼저 떠 있어야 한다)
dotnet run -c Release --project testbed/Npc.TestGameServer -- `
    --npcs 300 --time-scale 60 --link-port 7010 --client-port 7020 `
    --scenario testbed/scenarios/demo_siege.jsonl

# 2) NPC 서버
dotnet run -c Release --project src/Npc.Host -- `
    --link tcp --gs-port 7010 --npcs 300 --time-scale 60 --days 0 `
    --tier none --planstore ./planstore --port 5080 --dev-control

# 3) 클라이언트
dotnet run -c Release --project testbed/Npc.TestClient -- `
    --host 127.0.0.1 --port 7020 --npc-http http://localhost:5080
```

`testbed/run_demo.ps1 -Scenario siege`가 이 셋을 순서대로 띄우고 Ctrl-C에 함께 내린다.
**스크립트는 UTF-8 BOM으로 저장한다** — Windows PowerShell 5.1이 BOM 없는 한국어 스크립트를 깨뜨린다.

**포트 요약.** 7010 링크 · 7020 클라이언트 · 5080 NPC 서버 HTTP.

---

## 13. 테스트 계약

| 테스트 | Category | 내용 |
|---|---|---|
| `Wire_CommandRoundTrips` | `Wire` | 12개 Kind × 모든 필드에 서로 다른 값 → 왕복 후 동일 |
| `Wire_EventRoundTrips` | `Wire` | 17개 Kind 동일 |
| `Wire_LayoutIsFrozen` | `Wire` | `sizeof(WireCommand)==56`, `sizeof(WireEvent)==64` |
| `Wire_NoStringOrDateTimeFields` | `Contracts` | `Npc.Wire`·`Npc.TestBed.Protocol` 어셈블리에 `string`/`DateTime` 필드 0개 (N3·N4) |
| `Wire_MirrorsContractMembers` | `Wire` | `NpcCommand`/`GameEvent`의 멤버 집합과 와이어 DTO가 1:1. **드리프트 방지** |
| `Frame_SplitAcrossReads` | `Wire` | 한 프레임을 1바이트씩 나눠 넣어도 정확히 복원 |
| `Frame_RejectsOversizePayload` | `Wire` | 1MiB 초과 길이 접두 → 예외, 프로세스는 산다 |
| `TcpLink_HandshakeRejectsMismatch` | `TestBed` | 마스터데이터·로스터·타임스케일 불일치 각각 → `Accepted=0`, 해당 `RejectCode` |
| `TcpLink_FlushDoesNotAllocate` | `TestBed` | 워밍업 후 `Enqueue`+`Flush` 10,000회 할당 델타 0 |
| `TcpLink_BackpressureDropsCosmeticFirst` | `TestBed` | `Cosmetic`부터 드롭, `Critical` 무손실 (Loopback과 같은 링을 쓰므로 같은 결과) |
| `TcpLink_ReconnectKeepsSequenceMonotonic` | `TestBed` | 세션을 끊고 다시 붙여도 시퀀스가 되감기지 않고, NPC가 다시 움직인다 |
| `TcpLink_CommandLossSynthesizesTimeout` | `TestBed` | `--drop-rate 0.3`로 300틱 → 합성 타임아웃 > 0, 멈춘 NPC 0 |
| `Roster_SelectionMatchesLegacyFormula` | (없음) | 추출한 선택 함수가 기존 공식과 동일 (npcs 1·7·500·5000) |
| `Roster_HashDetectsDifference` | (없음) | 존 필터가 다르면 해시가 다르다 |
| `TestBed_EndToEnd_NpcArrives` | `TestBed` | 게임서버+NPC서버를 인프로세스 소켓(포트 0)으로 붙여 300틱 → `NpcArrived ≥ 1`, 시퀀스 갭 0, 드롭 0 |
| `TestBed_ProximityChangesLod` | `TestBed` | 플레이어를 NPC 30m 안에 놓으면 그 NPC의 `/npc/{id}` Lod가 0이 된다 |
| `TestBed_InteractRaisesInterrupt` | `TestBed` | `Interact` → 그 NPC의 최근 사건에 `PlayerInteracted`, `PlayerNearby` 플래그가 선다 |
| `ClientProtocol_SnapshotRoundTrips` | `Wire` | 256 엔티티 스냅샷 왕복 |
| `ClientProtocol_AoiCapsAt256` | `TestBed` | NPC 1,000 중 256개만, 가까운 순 |

**테스트는 포트 0(자동 할당)으로 연다.** 고정 포트를 쓰면 개발자가 데모를 띄워 둔 채 테스트를 돌릴 때 깨진다.

`Npc.Tests.csproj`에 `Npc.Wire`·`Npc.TestBed.Protocol`·`Npc.TestGameServer` 참조를 추가한다. **`Npc.TestClient`는 추가하지 않는다**(§4).

---

## 14. 예산과 한계

### 14.1 성능

| 대상 | 예산 | 확인 방법 |
|---|---|---|
| NPC 서버 틱 | p99 ≤ 20ms, Gen0 증가 0 | 기존 예산 그대로. `--link tcp`에서도 지킨다 |
| 게임서버 틱 | p99 ≤ 10ms (NPC 500 · 클라 4) | 종료 요약 |
| 이벤트 대역 | ≤ 200KB/s | `LinkStats` |
| 스냅샷 대역 | ≤ 40KB/s / 클라 | 32B × 256 × 5Hz = 41KB/s |
| 클라이언트 | 60fps, 입력→화면 ≤ 300ms | 눈으로 |

### 14.2 규모

데모는 **NPC 300**이 기본이다. 5,000은 클라이언트 화면에서 의미가 없고(AOI 256 상한), 부하 측정은 이미 `--loopback --max-speed` 경로가 한다. 게임서버는 1,000까지는 무리 없이 돈다.

### 14.3 결정론은 여기서 성립하지 않는다

`docs/15` §3의 "리플레이 100% 일치"는 **루프백 경로의 게이트다.** 소켓 경로에서는 명령이 몇 틱 늦게 도착할 수 있고, 그것이 스텝 경계를 바꾼다.

- **이것은 결함이 아니라 사실이다.** 실제 게임서버에 붙으면 원래 그렇다.
- 결정론이 필요한 회차는 계속 `--loopback`으로 돌린다.
- 소켓 경로에서 재현이 필요하면 `--link record`로 이벤트 열을 기록해 `--link replay`로 되민다. 그 경로는 결정론이다.

**이 절을 지우지 않는다.** 나중에 누군가 "소켓에서 리플레이가 안 맞는다"고 버그를 열 것이다.

---

## 15. 리스크

| 리스크 | 신호 | 대응 |
|---|---|---|
| MemoryPack 소스 생성기가 `TreatWarningsAsErrors`와 충돌 | 생성 코드에서 경고 → 빌드 실패 | 생성 파일은 `*.g.cs`다. 억제가 필요하면 **`Npc.Wire`의 `csproj`에만** `NoWarn`을 넣고 이유를 주석으로 남긴다. 본체 코드의 경고는 억제하지 않는다(`CLAUDE.md` §1) |
| MemoryPack이 net10 미지원 | 복원 실패 | `netstandard2.1` 자산으로 동작한다. 그래도 막히면 **직접 쓰기로 대체**한다 — 와이어 타입이 전부 unmanaged라 `MemoryMarshal.Write`/`Read` 20줄이면 된다. `Npc.Wire` 안쪽만 바뀐다 |
| `FlushAsync` 경로에 숨은 할당 | `TcpLink_FlushDoesNotAllocate` 실패 | 설계상 `Volatile.Write` 한 줄이다. 실패하면 원인이 다른 데 있다는 신호다 |
| 로스터 불일치를 놓친 채 데모 | NPC가 엉뚱하게 행동 | 핸드셰이크에서 거절한다(§5.5). 우회 옵션을 만들지 않는다 |
| 시간 배속과 시각 속도의 부조화 | NPC가 순간이동하거나 기어간다 | `--time-scale 60` 기본. 겉보기 속도 = 실제 속도 × TimeScale (§7.3). 배속을 바꾸면 플레이어 속도도 같이 환산된다 |
| 클라이언트가 NPC 서버 HTTP를 못 찾음 | 인스펙터 빈칸 | 조용히 넘기고 패널에 "NPC 서버 미연결"만 표시. 클라이언트는 계속 돈다 |
| T5-21 미착수로 `--tier t2` 킬스위치가 안 먹음 | blackout 데모가 거짓 통과 | §11.4의 경고대로 `--tier none`/`--tier all`로 돌린다 |
| 테스트 베드 편의 기능이 본체로 새어 들어옴 | `Npc.Runtime`에 diff 발생 | **§1의 합격 기준 1이 곧 리뷰 기준이다.** `src/Npc.Runtime`·`Npc.Planning`·`Npc.Core`·`Npc.Contracts`에 diff가 생기면 멈추고 보고한다 |

---

## 16. 범위 밖

- 인증·암호화·세션 토큰. 로컬 루프백 전용이다.
- 클라이언트의 예측·서버 재조정(prediction/reconciliation). 보간만 한다(§9.3).
- 여러 게임서버 샤드, NPC 서버 이중화.
- 클라이언트에서 NPC를 직접 조종하는 기능. **그것은 이 프로젝트가 부정하는 것이다.**
- 3D·스프라이트·애니메이션. 점과 선으로 충분하다.
- 채팅·닉네임 등 플레이어 작성 문자열(§3.5).

---

## 17. 문서 동기화 의무

이 작업은 기존 사양 두 곳의 서술을 바꾼다. **같은 커밋에서 고친다**(`CLAUDE.md` §0).

| 문서 | 무엇을 |
|---|---|
| `docs/02` §2 구현체 표 | `TcpGameServerLink` **미구현 → 구현(테스트 베드)** |
| `docs/02` §6 테스트 계약 | §13의 링크 관련 항목 추가 |
| `docs/02` §7 | 5개 항목에 "구현됨 — `docs/20` §5·§6" 표시. 남은 것(이기종 엔디언·인증·샤딩)만 남긴다 |
| `CLAUDE.md` §3 | `Npc.Wire`와 `testbed/*`를 의존 그래프에 추가 |
| `TASKS.md` §3 원장 | P6 행 추가 |
