# 02. 게임서버 연동 링크 사양

> **이 문서는 "게임서버를 어떻게 만드는가"가 아니다.**
> **우리가 만드는 NPC 서버가 게임서버와 주고받는 경계(Link)를 NPC 서버 관점에서 정의한 문서다.**
>
> ~~**이번 범위: 인터페이스와 패킷 정의까지만.**~~ — **2026-07-28 갱신.** 실제 네트워크 통신은
> P6(테스트 베드)에서 구현됐다. §2 구현체 표와 §7 을 본다.
>
> 목표는 그대로였고, **지켜졌다** — 나중에 실제 게임서버에 붙일 때 NPC 서버 코드가 한 줄도 바뀌지 않는 것.
> TCP 링크를 구현한 아홉 커밋(T6-01~T6-09) 전부 `Npc.Runtime`·`Npc.Planning`·`Npc.Core`·`Npc.Contracts`
> 변경이 **0줄**이다.

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
/// 구현체는 인프로세스 루프백 / 널 / 기록·재생 데코레이터 / TCP (P6 에서 구현).
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
| `TcpGameServerLink` | 실제 네트워크 | **구현 (테스트 베드 · P6).** 연결·핸드셰이크·송신·수신·재접속. `docs/20` §5·§6 |

`TcpGameServerLink` 는 2026-07-28(T6-06~T6-09)에 골격에서 구현으로 바뀌었다. **스레드가 셋이다.**

```
 [틱 루프]                          [센더 태스크]              [리시버 태스크]
  Enqueue ──► PriorityCommandRing        │                          │
  Flush   ──► BatchQueue 게시 ──────────►│ → WireCommand[]          │
              (할당 0)                   │ → MemoryPack → 소켓       │
                                                                    │ PipeReader → 프레임
  link.Events.TryRead ◄─────────────────────────────────────────────┘ → GameEvent → 채널
```

**틱 루프는 소켓을 만지지 않는다.** 커널 송신 버퍼가 차기를 틱 루프에서 기다리면 그 순간 틱이 밀린다
(`docs/20` §3.3). `FlushAsync` 가 하는 일은 링을 배치 슬롯으로 옮기고 꼬리를 올리는 것뿐이고,
그 경로에 할당이 없다(`TcpLink_FlushDoesNotAllocate`).

> **링을 스레드 너머로 넘기지 않는다.** `PriorityCommandRing` 은 삽입과 꺼냄이 `_count` 를 함께
> 만져 SPSC 로 안전하지 않다. 경계는 `BatchQueue`(생산자/소비자 첨자가 갈린 SPSC)이고,
> 이 판단의 근거는 `ReplanQueue` 에서 같은 실수를 한 번 했기 때문이다 (`TASKS.md §3` 결정 16).

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
| `MoveTo` | `TargetPoi` 또는 `TargetPos`, `Flags`(속도) | 이동 지시. **경로 계산은 게임서버 소관**. `TargetNpc`가 설정되면 추종(`Follow`), `Zone`+`Amount`면 그 존 반경 안 배회(`Wander`) |
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

### TCP 링크 (P6 · `docs/20` §13)

| 테스트 | 내용 |
|---|---|
| `Wire_MirrorsContractMembers` | 리플렉션으로 `NpcCommand`↔`WireCommand` 멤버 집합 1:1 — **계약에 필드를 추가하고 와이어를 잊으면 여기서 깨진다** |
| `Wire_LayoutIsFrozen` | `sizeof`가 56·64. 패딩이 생기면 값이 달라진다 |
| `Frame_SplitAcrossReads` | 1바이트씩 넣어도 복원. 다 오기 전에는 버퍼를 건드리지 않는다 |
| `Frame_ReadsAcrossSegments` | 헤더 한가운데서 잘린 다중 세그먼트 — `PipeReader` 가 주는 모양이다 |
| `Frame_RejectsOversizePayload` | 1MiB 초과 거절. **프로세스는 산다** |
| `TcpLink_HandshakeRejectsMismatch` | 버전·타임스케일·마스터데이터·로스터 4가지 불일치 각각 해당 `RejectCode` |
| `TcpLink_OneFlushIsOneFrame` | 수신 측이 센 프레임 수 = Flush 횟수 (N8) |
| `TcpLink_FlushDoesNotAllocate` | 워밍업 후 10,000회 할당 델타 0 |
| `TcpLink_CountsSequenceGaps` | 시퀀스 건너뛴 배치 → **건너뛴 개수만큼** `EventGapsDetected` (N6) |
| `TcpLink_ReconnectKeepsSequenceMonotonic` | 끊고 다시 붙여도 시퀀스가 되감기지 않는다 (N6) |
| `TcpLink_StateTransitions` | §6.3 전이표가 `StateChanged` 로 관측된다 |
| `TcpLink_FaultedDoesNotRetry` | 거절되면 세션 1회로 끝난다 — 무한 재시도로 원인을 묻지 않는다 |

---

## 7. 실제 네트워크 구현 — 다섯 항목 전부 구현됐다 (P6)

| # | 항목 | 상태 |
|---|---|---|
| 1 | **직렬화** — `MemoryPack`. 패킷이 전부 값 타입이라 unmanaged 배열로 나간다 | **구현** — `docs/20` §5.3 · `Npc.Wire` |
| 2 | **프레이밍** — 길이 접두 + payload | **구현** — `docs/20` §5.1 · `FrameCodec`. 헤더는 8바이트 고정이고 **4바이트가 아니다** (Kind·Ver·Reserved 가 붙는다) |
| 3 | **전송** — `System.IO.Pipelines`. Nagle 비활성 | **구현** — `docs/20` §6.1. `SocketAsyncEventArgs` 대신 `PipeReader`/`NetworkStream` 이다 |
| 4 | **재접속 + 시퀀스 재동기화** | **구현** — `docs/20` §5.6. 백오프 250ms~4s · **시퀀스는 리셋하지 않는다**(N6) |
| 5 | **흐름 제어** | **구현** — `PriorityCommandRing`. `BoundedChannel` 이 아니라 우선순위별 링이고 `Critical` 은 무손실이다 |

**다섯 중 NPC 서버 코드를 건드려야 한 것은 없었다.** 이것이 이 인터페이스 설계의 검증 기준이었고,
**커밋 단위로 확인했다** — T6-01~T6-09 아홉 커밋 전부 `Npc.Runtime`·`Npc.Planning`·`Npc.Core`·
`Npc.Contracts` 변경 0줄이다 (`docs/20` §1 합격 기준 1).

### 아직 남은 것

| 항목 | 왜 남았나 |
|---|---|
| **이기종 엔디언·패딩** | unmanaged 배열을 원시 메모리로 복사하므로 **양쪽이 같은 x64 .NET 런타임**인 것을 전제한다. 다른 런타임의 게임서버에 붙일 때는 필드별 쓰기 포매터로 바꾼다 — **바꿀 자리가 `Npc.Wire` 한 곳뿐인 것이 이 설계의 요점이다** (`docs/20` §5.2) |
| **인증·암호화** | 핸드셰이크가 검증하는 것은 "같은 데이터를 보고 있는가" 지 "네가 누구인가" 가 아니다. 신뢰 경계 밖에 놓으려면 TLS 와 토큰이 필요하다 |
| **샤딩** | 링크 하나가 게임서버 하나에 붙는다. 월드를 여러 프로세스로 쪼개면 존별 라우팅이 필요하다 |
