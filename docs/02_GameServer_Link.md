# 02. 게임서버 연동 링크 사양

> **이 문서는 "게임서버를 어떻게 만드는가"가 아니다.**
> **우리가 만드는 NPC 서버가 게임서버와 주고받는 경계(Link)를 NPC 서버 관점에서 정의한 문서다.**
>
> **이번 범위: 인터페이스와 패킷 정의까지만.** 실제 네트워크 통신(소켓·직렬화·재접속)은 구현하지 않는다.
> 목표: 나중에 실제 게임서버에 붙일 때 **NPC 서버 코드가 한 줄도 바뀌지 않는 것.**

---

## 0. 관점 — 누가 무엇을 만드는가

이 프로젝트에서 만드는 것은 **NPC 서버**다. 게임서버는 만들지 않는다. 다만 NPC 서버를 혼자 돌려볼 수 없으므로, **게임서버 흉내를 내는 대역(`Npc.Sim`)** 을 함께 만든다.

```
        ┌───────────────────────────────┐
        │   NPC 서버   ★ 이번 프로젝트   │
        │                               │
        │  Npc.Runtime · Npc.Planning   │
        │  Npc.Llm     · Npc.Core       │
        └───────────┬───────────────────┘
                    │
        ┌───────────┴───────────────────┐
        │   IGameServerLink   ★ 이 문서  │   ← NPC 서버의 바깥쪽 포트(port)
        │   NpcCommand ▲ / GameEvent ▼   │      "게임서버로 나가는 문"
        └───────────┬───────────────────┘
                    │
   ┌────────────────┴────────────────┐
   │                                 │
┌──┴──────────────────┐   ┌──────────┴────────────────────┐
│ Npc.Sim              │   │ 실제 MMORPG 게임서버           │
│ ★ 이번 프로젝트 (대역) │   │ ✗ 이번 범위 아님. 남이 만든다  │
│ 게임서버 흉내         │   │ (또는 이미 존재)               │
└──────────────────────┘   └───────────────────────────────┘
```

| 대상 | 이번 프로젝트에서 | 비고 |
|---|---|---|
| **NPC 서버** | ✅ 만든다 | 이 프로젝트의 본체 |
| **`IGameServerLink` (경계 정의)** | ✅ 만든다 | **이 문서** |
| **`Npc.Sim` (게임서버 대역)** | ✅ 만든다 | 실제 게임서버 없이 NPC 서버를 검증하기 위한 가짜. §5 |
| **실제 게임서버** | ❌ 안 만든다 | 이 문서는 "붙일 때 이렇게 붙는다"만 정의 |
| **실제 네트워크 전송** | ❌ 안 만든다 | 인터페이스·패킷 정의까지. §7에 남는 작업 명시 |

**용어 정리**

| 용어 | 뜻 |
|---|---|
| `NpcCommand` | NPC 서버가 **발행**하는 명령. "이 NPC를 저기로 보내라" — 방향: NPC 서버 → 게임서버 |
| `GameEvent` | NPC 서버가 **수신**하는 통보. "그 NPC가 도착했다" — 방향: 게임서버 → NPC 서버 |
| `IGameServerLink` | 위 둘이 오가는 **NPC 서버 쪽 포트**. 이름의 "GameServer"는 *상대방*을 가리킨다 |
| `Npc.Sim` | 그 상대방 역할을 하는 우리 쪽 가짜 구현 |

> 즉 `IGameServerLink`는 "게임서버의 인터페이스"가 아니라 **"게임서버로 향하는 NPC 서버의 링크"** 다. 헥사고날 아키텍처의 아웃바운드 포트에 해당한다.

---

## 1. 설계 원칙 — 나중에 네트워크로 갈 때 안 깨지려면

인터페이스만 만들고 나중에 네트워크를 붙이는 계획의 실패 원인은 거의 항상 같다. **인프로세스라서 공짜였던 가정이 네트워크에서 깨진다.** 아래 8개 규칙은 그 가정을 미리 제거하기 위한 것이며, 전부 **컴파일 타임 또는 테스트로 강제**한다.

| # | 규칙 | 이유 | 강제 방법 |
|---|---|---|---|
| **N1** | **명령은 fire-and-forget. 결과는 반드시 이벤트로 돌아온다.** RPC 왕복(`await SendAndWait`)을 절대 만들지 않는다 | 네트워크 RTT가 틱 루프를 블록하면 그 순간 끝이다 | `IGameServerLink`에 반환값 있는 메서드를 두지 않는다 |
| **N2** | **모든 패킷은 순수 값 타입.** 참조 공유·가변 컬렉션·`object` 금지 | 인프로세스에서 참조를 공유하면 네트워크 전환 시 전부 재작성 | `readonly record struct` 사용. 분석기로 참조 필드 금지 |
| **N3** | **문자열 금지. 전부 ID.** | 직렬화 비용 + §4.3 프롬프트 인젝션 방어 | 패킷 타입에 `string` 필드가 있으면 테스트 실패 |
| **N4** | **시간은 `Tick`(long)만.** `DateTime`/`Stopwatch` 금지 | 서버 간 시계는 다르다. 결정론 리플레이도 깨진다 | 패킷에 `DateTime` 필드 금지 (테스트) |
| **N5** | **모든 명령에 `CorrelationId`.** 이벤트는 이걸로 명령과 매칭된다 | 네트워크는 재정렬·지연된다. 순서 가정 금지 | `NpcCommand` 헤더에 필수 |
| **N6** | **이벤트에 `SequenceNumber`.** 수신 측이 유실·중복을 검출한다 | 유실을 조용히 넘기면 NPC가 영원히 멈춘다 | 링크가 갭 검출 시 경보 |
| **N7** | **이벤트 처리는 멱등해야 한다.** 같은 이벤트를 두 번 받아도 상태가 같아야 한다 | 재전송은 반드시 생긴다 | 멱등성 테스트 (동일 이벤트 2회 주입 → 상태 동일) |
| **N8** | **배치가 기본.** 단건 전송은 배치 크기 1의 특수 케이스 | 5,000 NPC × 초당 명령 = 단건 전송이면 신텍스가 죽는다 | API에 `SendBatch`만 노출 |

추가로:

- **명령 유실 허용 설계** — 명령이 유실되어도 NPC가 영구히 멈추면 안 된다. 각 플랜 스텝에 `timeout_s`가 있고, 타임아웃 시 `ActionFailed`로 간주하고 다음으로 넘어간다 (§[03_PlanDSL](03_PlanDSL_Spec.md))
- **역압(backpressure) 명시** — 명령 채널은 `BoundedChannel`. 가득 차면 저우선순위 명령(Emote, Gossip)부터 드롭하고 카운터를 올린다

---

## 2. 핵심 인터페이스

```csharp
// Npc.Contracts/IGameServerLink.cs
namespace Npc.Contracts;

/// <summary>
/// NPC 서버 ↔ 게임서버 사이의 유일한 경계.
/// 구현체는 인프로세스 루프백 / 널 / 기록 데코레이터 / (미래) TCP.
/// </summary>
public interface IGameServerLink : IAsyncDisposable
{
    /// NPC 서버 → 게임서버. 배치 전송만 허용한다 (N8).
    /// 반환값 없음 — 결과는 Events로만 돌아온다 (N1).
    void Enqueue(in NpcCommand command);
    ValueTask FlushAsync(CancellationToken ct);

    /// 게임서버 → NPC 서버.
    ChannelReader<GameEvent> Events { get; }

    LinkState State { get; }
    LinkStats Stats { get; }

    event Action<LinkState>? StateChanged;
}

public enum LinkState { Disconnected, Connecting, Connected, Degraded, Faulted }

public readonly record struct LinkStats(
    long CommandsEnqueued,
    long CommandsFlushed,
    long CommandsDropped,      // 역압으로 드롭된 수
    long EventsReceived,
    long EventGapsDetected,    // N6 시퀀스 갭
    int  PendingCommands);
```

### 구현체 (이번 범위)

| 구현체 | 용도 | 상태 |
|---|---|---|
| `LoopbackGameServerLink` | `Npc.Sim` 헤드리스 월드에 직결. **W2~W10의 주력** | 구현 |
| `NullGameServerLink` | 명령 폐기, 이벤트 없음. 단위 테스트·성능 측정 기준선 | 구현 |
| `RecordingGameServerLink` | 데코레이터. 명령·이벤트를 jsonl로 기록 → 리플레이 | 구현 |
| `ReplayGameServerLink` | 기록된 jsonl을 이벤트 소스로 재생 | 구현 |
| `TcpGameServerLink` | 실제 네트워크 | **미구현.** 클래스 골격 + `TODO` 주석 + `NotSupportedException` 만 |

```csharp
// Npc.Gateway/TcpGameServerLink.cs
/// <summary>
/// [범위 밖] 실제 게임서버 TCP 링크.
/// 이 클래스는 인터페이스가 네트워크 전환에 견디는지 확인하기 위한 골격이다.
/// 구현 시 필요한 것:
///   1. 프레이밍 (길이 접두 4바이트 + payload)
///   2. 직렬화 — MemoryPack 또는 소스 생성 기반. 리플렉션 금지
///   3. 재접속 + 시퀀스 재동기화 (N6)
///   4. Nagle 비활성 + 배치 코얼레싱 (N8)
/// </summary>
public sealed class TcpGameServerLink : IGameServerLink
{
    public void Enqueue(in NpcCommand command) => throw new NotSupportedException("TODO: 범위 밖");
    // ...
}
```

---

## 3. 패킷 정의

### 3.1 공통 타입

```csharp
// Npc.Contracts/Ids.cs — 강타입 ID (오용 방지 + 직렬화 시 원시값)
public readonly record struct NpcId(int Value);
public readonly record struct PlayerId(int Value);
public readonly record struct ArchetypeId(ushort Value);
public readonly record struct ZoneId(ushort Value);
public readonly record struct PoiId(ushort Value);
public readonly record struct ActionId(ushort Value);
public readonly record struct ItemId(ushort Value);
public readonly record struct DialogueId(ushort Value);
public readonly record struct AnimationId(ushort Value);
public readonly record struct PlanId(int Value);
public readonly record struct CorrelationId(uint Value);
public readonly record struct Tick(long Value);

/// 게임서버 좌표계 그대로. 고정소수점 아님 — 게임서버 규약에 맞춰 교체 가능하도록 별도 타입.
public readonly record struct WorldPos(float X, float Y, float Z);
```

### 3.2 NPC 서버 → 게임서버: `NpcCommand`

```csharp
public enum NpcCommandKind : byte
{
    Spawn = 1, Despawn, MoveTo, Stop, FaceTo,
    PlayAnimation, SetVisualState, Interact, Speak,
    InventoryChange, CombatAction, SetAggro,
}

/// 판별 공용체(discriminated union)를 struct로 표현.
/// 필드 유니온 대신 명시 필드를 쓴다 — 직렬화기가 단순해지고 디버깅이 쉽다.
public readonly record struct NpcCommand
{
    // --- 헤더 (모든 명령 공통) ---
    public required NpcCommandKind Kind          { get; init; }
    public required NpcId          Npc           { get; init; }
    public required Tick           IssuedAt      { get; init; }   // N4
    public required CorrelationId  Correlation   { get; init; }   // N5
    public required CommandPriority Priority     { get; init; }   // 역압 시 드롭 우선순위

    // --- 페이로드 (Kind에 따라 사용 필드가 달라짐) ---
    public PoiId       TargetPoi     { get; init; }
    public WorldPos    TargetPos     { get; init; }
    public NpcId       TargetNpc     { get; init; }
    public PlayerId    TargetPlayer  { get; init; }
    public ItemId      Item          { get; init; }
    public int         Amount        { get; init; }
    public AnimationId Animation     { get; init; }
    public DialogueId  Dialogue      { get; init; }
    public VisualState Visual        { get; init; }
    public ArchetypeId Archetype     { get; init; }
    public ZoneId      Zone          { get; init; }
    public byte        Flags         { get; init; }   // MoveSpeed 등급, Aggro on/off 등
}

public enum CommandPriority : byte { Critical = 0, Normal = 1, Cosmetic = 2 }
public enum VisualState : byte { Idle, Walking, Running, Working, Fighting, Sleeping, Sitting, Dead }
```

**명령별 사용 필드**

| Kind | 필수 필드 | 의미 |
|---|---|---|
| `Spawn` | `Archetype, Zone, TargetPos` | NPC 생성 |
| `Despawn` | — | NPC 제거 |
| `MoveTo` | `TargetPoi` 또는 `TargetPos`, `Flags`(속도) | 이동 지시. **경로 계산은 게임서버 소관** |
| `Stop` | — | 즉시 정지 |
| `FaceTo` | `TargetNpc` 또는 `TargetPos` | 방향 전환 |
| `PlayAnimation` | `Animation, Amount`(반복) | 연출 |
| `SetVisualState` | `Visual` | 상태 표현 |
| `Interact` | `TargetPoi`/`TargetNpc`, `Item` | POI·NPC 상호작용 (채집, 제작대 사용 등) |
| `Speak` | `Dialogue, TargetNpc?` | **대사 ID만.** 자연어 생성은 범위 밖 |
| `InventoryChange` | `Item, Amount`(부호 있음) | 인벤 증감 요청 |
| `CombatAction` | `TargetNpc`/`TargetPlayer`, `Flags`(행동 종류) | 전투 행동. 판정은 게임서버 |
| `SetAggro` | `Flags` | 적대 상태 토글 |

### 3.3 게임서버 → NPC 서버: `GameEvent`

```csharp
public enum GameEventKind : byte
{
    TickSync = 1, GameTimeChanged,
    NpcSpawned, NpcDespawned, NpcTransform,
    NpcArrived, NpcActionCompleted, NpcActionFailed,
    NpcVitalsChanged, NpcInventoryChanged,
    PlayerProximity, PlayerInteracted,
    CombatStarted, CombatEnded, DamageTaken,
    ZoneStateChanged, WeatherChanged,
}

public readonly record struct GameEvent
{
    public required GameEventKind Kind        { get; init; }
    public required long          Sequence    { get; init; }   // N6
    public required Tick          OccurredAt  { get; init; }   // N4
    public NpcId          Npc          { get; init; }
    public CorrelationId  Correlation  { get; init; }          // N5 — 명령 응답일 때
    public WorldPos       Pos          { get; init; }
    public float          Heading      { get; init; }
    public PoiId          Poi          { get; init; }
    public ZoneId         Zone         { get; init; }
    public PlayerId       Player       { get; init; }
    public NpcId          OtherNpc     { get; init; }
    public ItemId         Item         { get; init; }
    public int            Amount       { get; init; }
    public short          Hp           { get; init; }
    public short          Stamina      { get; init; }
    public byte           Code         { get; init; }   // ResultCode / FailReason / RegionState / Climate / TimeOfDay
}

public enum ActionFailReason : byte
{
    None = 0, Unreachable, Timeout, Interrupted, PreconditionFailed,
    TargetGone, InventoryFull, InsufficientResource, Dead, Rejected,
}
```

**이벤트별 의미**

| Kind | 주요 필드 | 처리 |
|---|---|---|
| `TickSync` | `OccurredAt` | 게임서버 틱과 동기화. **NPC 서버의 시간 기준은 이것뿐** |
| `GameTimeChanged` | `Code`(TimeOfDay) | 시간대 전환 → 컨텍스트 버킷 키 변경 → 대량 플랜 스왑 |
| `NpcSpawned` / `NpcDespawned` | `Npc, Pos` | 스폰 확인. 확인 전까지 명령 발행 금지 |
| `NpcTransform` | `Npc, Pos, Heading` | 위치 갱신. **배치로 대량 수신**되는 최다 이벤트 |
| `NpcArrived` | `Npc, Correlation, Poi` | `MoveTo` 완료 |
| `NpcActionCompleted` | `Npc, Correlation, Code` | 플랜 스텝 전진 |
| `NpcActionFailed` | `Npc, Correlation, Code`(FailReason) | 폴백 또는 재계획 트리거 |
| `NpcVitalsChanged` | `Npc, Hp, Stamina` | WorldFlags 갱신 (`IsInjured`, `IsExhausted`) |
| `NpcInventoryChanged` | `Npc, Item, Amount` | WorldFlags 갱신 (`HasFood`, `InventoryFull`) |
| `PlayerProximity` | `Npc, Player, Amount`(거리), `Code`(Enter/Leave) | **인지 LOD 등급 결정의 입력** |
| `PlayerInteracted` | `Npc, Player, Code` | 인터럽트 경로 → 즉시 재계획 큐 최고 점수 |
| `CombatStarted` / `DamageTaken` | `Npc, OtherNpc/Player, Amount` | 인터럽트 경로 (최우선) |
| `ZoneStateChanged` | `Zone, Code`(RegionState) | **버킷 키 변경 → 존 전체 플랜 스왑** |
| `WeatherChanged` | `Zone, Code`(Climate) | 동일 |

### 3.4 명령↔이벤트 상관 규약

```
NPC서버                                     게임서버
   │                                            │
   │ MoveTo(npc=7, poi=mine, corr=1042) ───────►│
   │                                            │ (경로 계산, 이동 시뮬)
   │◄────── NpcTransform(npc=7, pos=...) ×N ────│
   │◄────── NpcArrived(npc=7, corr=1042) ───────│
   │  → 플랜 스텝 0 완료, 스텝 1로 전진          │
   │                                            │
   │ Interact(npc=7, poi=mine, corr=1043) ─────►│
   │◄─ NpcActionFailed(corr=1043, Unreachable) ─│
   │  → 폴백 플랜 전환 + 재계획 큐 삽입          │
```

- 상관 없는 이벤트(`NpcTransform`, `DamageTaken` 등)는 `Correlation = default`
- 명령 발행 후 `timeout_s` 내에 대응 이벤트가 안 오면 **로컬에서 `ActionFailed(Timeout)`을 합성**한다. 이게 명령 유실 방어책이다

---

## 4. 런타임 배선

```csharp
// Npc.Runtime/NpcServerLoop.cs (개념)
public sealed class NpcServerLoop
{
    private readonly IGameServerLink _link;
    private readonly CognitionScheduler _cognition;
    private readonly PlanExecutor _executor;
    private readonly EventApplier _applier;

    public async Task RunAsync(CancellationToken ct)
    {
        while (await _link.Events.WaitToReadAsync(ct))
        {
            // 1) 수신 이벤트 전부 배수 → 월드 뷰 갱신 (멱등, N7)
            while (_link.Events.TryRead(out var ev))
                _applier.Apply(in ev);          // WorldFlags/위치/LOD 등급 갱신
                                                // 인터럽트 이벤트는 여기서 즉시 큐 삽입

            // 2) 틱 진행 (TickSync 수신 시에만)
            if (_applier.TickAdvanced(out Tick tick))
            {
                _cognition.Scan(tick);           // 인지 LOD 슬라이스 스캔 → 재계획 큐
                _executor.Step(tick, _link);     // 플랜 스텝 실행 → _link.Enqueue()
                await _link.FlushAsync(ct);      // 배치 송출 (N8)
            }
        }
    }
}
```

**핵심: 틱 루프 안에 `await` 대상이 `FlushAsync` 하나뿐이다.** LLM 호출은 이 루프에 존재하지 않는다 — 별도 `BackgroundService`가 재계획 큐를 소비하고, 완성된 플랜을 원자적으로 스왑한다.

---

## 5. `Npc.Sim` — 게임서버 대역

`LoopbackGameServerLink`의 반대편. 실제 게임서버가 할 일을 최소한으로 흉내낸다.

| 게임서버 기능 | Sim의 근사 |
|---|---|
| 패스파인딩 | 직선거리 ÷ 이동속도 = 소요 틱. POI 간 거리는 마스터데이터의 사전계산 행렬 |
| 이동 시뮬 | 목표 틱에 도달하면 `NpcArrived` 발행. 중간에 `NpcTransform`을 N틱마다 보간 발행 |
| 상호작용 판정 | `Interact` 수신 → 마스터데이터의 액션 소요시간만큼 대기 → `ActionCompleted` |
| 인벤토리 | 단순 `Dictionary<NpcId, int[]>` |
| 전투 | 없음. `CombatAction`은 확률로 성공/실패만 반환 |
| 플레이어 | **가상 플레이어 봇 N명**이 존을 랜덤 워크 → `PlayerProximity` 발생원 |
| 시간 | 압축 클럭. `--time-scale 60` 이면 실시간 1초 = 게임 1분 |

**가상 플레이어 봇이 중요하다.** 인지 LOD와 우선순위 큐의 `w1`(플레이어 근접도)를 검증하려면 플레이어가 움직여야 한다.

### Sim이 주입할 수 있는 시나리오 이벤트

```jsonc
// scenarios/siege.jsonl — 시나리오 스크립트
{"at_tick": 18000, "event": "ZoneStateChanged", "zone": "town", "code": "War"}
{"at_tick": 18000, "event": "WeatherChanged",   "zone": "town", "code": "Storm"}
{"at_tick": 54000, "event": "ZoneStateChanged", "zone": "town", "code": "Peace"}
{"at_tick": 20000, "event": "KillSwitch",       "target": "T2"}   // 외부 API 차단
{"at_tick": 26000, "event": "KillSwitch",       "target": "T1"}   // 로컬 LLM 차단
```

---

## 6. 테스트 계약

| 테스트 | 내용 |
|---|---|
| `Contracts_NoStringFields` | 리플렉션으로 모든 패킷 타입 순회 → `string` 필드 0개 (N3) |
| `Contracts_NoDateTimeFields` | 동일 → `DateTime`/`TimeSpan` 필드 0개 (N4) |
| `Contracts_AllValueTypes` | 모든 패킷 타입이 `readonly record struct` (N2) |
| `Link_Idempotent` | 동일 이벤트 2회 주입 → 월드 상태 해시 동일 (N7) |
| `Link_SequenceGap` | 시퀀스 건너뛴 이벤트 주입 → `EventGapsDetected` 증가 |
| `Link_Backpressure` | 채널 포화 → `Cosmetic` 우선순위부터 드롭, `Critical`은 무손실 |
| `Link_CommandLoss` | 명령을 의도적으로 드롭 → `timeout_s` 후 합성 `ActionFailed` 발생 → 플랜 진행 재개 |
| `Link_Swappable` | 같은 시나리오를 `Loopback`/`Recording`/`Replay`로 각각 실행 → NPC 서버 코드 무변경 |

---

## 7. 실제 네트워크 구현 시 남는 작업 (범위 밖 · 미래 참고)

1. **직렬화** — `MemoryPack` 또는 소스 생성기. 패킷이 전부 값 타입이라 `MemoryMarshal`로 blittable 처리도 가능
2. **프레이밍** — 길이 접두 4바이트 + payload. 배치는 `[count][cmd][cmd]...`
3. **전송** — `System.IO.Pipelines` + `SocketAsyncEventArgs`. Nagle 비활성
4. **재접속** — `LinkState.Degraded` 동안 명령은 링 버퍼에 축적, 재연결 시 시퀀스 재동기화
5. **흐름 제어** — 게임서버가 처리 못 하면 링크가 역압을 올려야 함. 현재 `BoundedChannel` 자리에 그대로 들어감

이 5가지 중 **NPC 서버 코드를 건드려야 하는 것은 없다.** 그것이 이번 인터페이스 설계의 검증 기준이다.
