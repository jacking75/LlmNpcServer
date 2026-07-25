# Phase 1 (W2–4) — 코어 · 런타임 · 시뮬레이터

> **목적: LLM 없이 5,000 NPC가 살아 움직이게 만든다.**
> 이 단계에서 LLM을 일부러 배제하는 이유는 둘이다. (1) LLM의 기여분을 나중에 측정하려면 기준선이 필요하다. (2) 폴백 경로가 공짜로 확보된다 — 시나리오 C의 통과 조건이 여기서 완성된다.

**게이트: LLM 0회 호출로 NPC 500마리가 게임 시간 7일 사이클을 완주한다.**

---

## 1. 주차 배분

| 주 | 내용 |
|---|---|
| W2 | 솔루션 구조 + `Npc.Contracts` + `Npc.Core` + 마스터데이터 1~7번 |
| W3 | `Npc.Runtime` (틱 루프, 실행기, 인지 스캐너) + `Npc.Sim` (루프백 월드) |
| W4 | 폴백 플랜 40개 수작성 + NPC 5,000 생성 + 성능 튜닝 + 대시보드 1차 |

---

## 2. 솔루션 구조

```
NpcServer.sln
├─ src/
│  ├─ Npc.Contracts/        게임서버 IF + 패킷 DTO          (의존: 없음)
│  ├─ Npc.Core/             플랜 DSL, 검증기, WorldFlags     (의존: Contracts)
│  ├─ Npc.MasterData/       로더 · 검증 · 인덱스             (의존: Core)
│  ├─ Npc.Runtime/          틱 · 실행기 · 인지 스캐너         (의존: Core, MasterData, Contracts, Planning)
│  ├─ Npc.Planning/         [W7~] 캐시 · 재계획 큐            (의존: Core, MasterData)
│  ├─ Npc.Llm/              [W5~] 티어 라우터 · 프롬프트      (의존: Core)
│  ├─ Npc.Gateway/          IGameServerLink 구현체들         (의존: Contracts)
│  ├─ Npc.Sim/              헤드리스 월드 (게임서버 대역)     (의존: Contracts, MasterData)
│  └─ Npc.Host/             ASP.NET 호스트 · 메트릭 · 대시보드
├─ tools/
│  ├─ Npc.Prebake/          [W7~] 프리베이크 CLI
│  ├─ Npc.Replay/           리플레이 CLI
│  ├─ gen_npcs.cs            NPC 인스턴스 생성기 (.NET 10 파일 기반 앱)
│  └─ gen_poi_distances.cs   POI 거리 행렬 생성기 (동일)
├─ masterdata/              §01 문서
├─ tests/Npc.Tests/
└─ Directory.Build.props
```

```xml
<!-- Directory.Build.props -->
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>preview</LangVersion>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <ServerGarbageCollection>true</ServerGarbageCollection>
    <ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

> **`Npc.Core`와 `Npc.Contracts`는 외부 NuGet 의존이 0이어야 한다.** 순수 로직만 담아서 테스트가 빠르고 미래 이식이 자유롭게 한다.

---

## 3. NPC 상태 — SoA 레이아웃

5,000 NPC를 매 틱 훑으므로 캐시 지역성이 곧 성능이다. `class Npc`를 5,000개 만들면 안 된다.

```csharp
// Npc.Runtime/NpcStore.cs
public sealed class NpcStore
{
    public int Count { get; private set; }

    // --- 핫 (매 틱 접근) ---
    public WorldFlags[] Flags       = [];   // 8B × 5000 = 40KB   ← 캐시에 통째로 들어간다
    public int[]        PlanId      = [];   // 4B × 5000 = 20KB
    public byte[]       StepIndex   = [];
    public byte[]       StepStatus  = [];
    public long[]       StepIssuedTick = [];
    public byte[]       Lod         = [];   // 0..3

    // --- 웜 (이벤트 수신 시 갱신) ---
    public WorldPos[]   Pos         = [];
    public short[]      Hp          = [];
    public short[]      Stamina     = [];
    public ushort[]     ZoneCode    = [];
    public ushort[]     ArchetypeCode = [];

    // --- 콜드 (재계획 시에만) ---
    public PoiId[]      HomePoi     = [];
    public PoiId[]      WorkPoi     = [];
    public int[][]      Inventory   = [];   // [npc][itemCode]
    public RingBuffer8<RecentEvent>[] Recent = [];   // §4.1 구조화 기억
    public int[]        PendingPlanId = [];  // 원자 스왑 대상
}
```

**핫 배열만 합쳐 약 100KB.** L2 캐시에 들어간다. 이게 틱 예산 20ms를 지키는 근거다.

```csharp
public readonly record struct RecentEvent(GameEventKind Kind, Tick At, int Subject, byte Salience);
```

`RingBuffer8`은 고정 8슬롯. salience 낮은 것부터 밀려난다 (§상위계획 §4.1).

---

## 4. 인지 LOD 스캐너

```csharp
// Npc.Runtime/CognitionScheduler.cs
public sealed class CognitionScheduler
{
    // LOD별 슬라이스. 각 LOD의 NPC 인덱스 목록을 period개 슬라이스로 분할
    private readonly LodBand[] _bands =
    [
        new(Lod: 0, Period:   1, Name: "시야내"),
        new(Lod: 1, Period:  10, Name: "동일존"),
        new(Lod: 2, Period: 100, Name: "원거리"),
        new(Lod: 3, Period:   0, Name: "비활성"),   // Period 0 = 이벤트 시에만
    ];

    public void Scan(Tick tick, NpcStore s, PlanStore plans, ReplanQueue queue)
    {
        foreach (var band in _bands)
        {
            if (band.Period == 0) continue;              // 비활성 — 이벤트 시에만

            // 밴드 멤버를 Period 개의 슬라이스로 나누고 매 틱 하나를 본다.
            // 그래서 이 밴드의 NPC 한 마리는 Period 틱마다 정확히 한 번 판정된다.
            var slice = band.Slices[tick.Value % band.Period];
            for (int k = slice.Start; k < slice.End; k++)
            {
                int i = band.Members[k];
                var plan = plans[s.PlanId[i]];
                if ((plan.RequiredFlags & ~s.Flags[i]) != 0 ||
                    (plan.ForbiddenFlags &  s.Flags[i]) != 0)
                {
                    queue.TryEnqueue(i, Score(i, s, tick));   // [W9~] 우선순위. W3에서는 단순 FIFO
                }
            }
        }
    }
}
```

### LOD 등급 갱신

`PlayerProximity` 이벤트로만 바뀐다. 스캔 안에서 거리를 계산하지 않는다.

```
PlayerProximity(Enter, dist < 50m)   → Lod 0
PlayerProximity(Enter, dist < 200m)  → Lod 1
PlayerProximity(Leave)               → 존 활성도에 따라 Lod 2 또는 3
ZoneStateChanged(War)                → 해당 존 전체를 Lod 1로 승격
```

등급 변경 시 밴드 멤버십 배열을 갱신해야 한다. 매 틱 재구축하면 안 되고, **더티 플래그 + 틱당 최대 N건 이동**으로 분할 처리한다.

```csharp
// 틱당 최대 64건만 밴드 이동. 나머지는 다음 틱으로.
private const int MaxBandMigrationsPerTick = 64;
```

### 성능 목표

| 항목 | 목표 |
|---|---|
| 틱당 판정 대상 | ≤ 150마리 (§00 수용기준) |
| 스캔 소요 | ≤ 3ms |
| 밴드 이동 | ≤ 1ms |

---

## 5. 틱 루프

```csharp
// Npc.Runtime/NpcServerLoop.cs
public sealed class NpcServerLoop
{
    private const int TickHz = 10;                 // 100ms
    private const int TickBudgetMs = 20;           // 예산 20%

    public async Task RunAsync(CancellationToken ct)
    {
        while (await _link.Events.WaitToReadAsync(ct))
        {
            // ── 1. 이벤트 배수 (멱등) ────────────────────────
            int drained = 0;
            while (drained < MaxEventsPerTick && _link.Events.TryRead(out var ev))
            {
                _applier.Apply(in ev, _store);     // 플래그·위치·LOD·인벤 갱신
                if (_interrupts.Match(in ev, _store, out var rule))
                {
                    _executor.ForceAction(ev.Npc, rule.Action);          // 즉시 반응
                    _replanQueue.TryEnqueueUrgent(ev.Npc, rule.Urgency); // 후속 재계획
                }
                drained++;
            }

            // ── 2. 틱 진행 ───────────────────────────────────
            if (!_clock.TryAdvance(out Tick tick)) continue;

            var sw = ValueStopwatch.Start();
            _cognition.Scan(tick, _store, _plans, _replanQueue);
            _executor.Step(tick, _store, _plans, _link);
            _swapper.ApplyPendingSwaps(_store);     // 원자 플랜 스왑 (스텝 경계에서만)
            await _link.FlushAsync(ct);
            _metrics.TickDuration.Record(sw.ElapsedMs);

            if (sw.ElapsedMs > TickBudgetMs) _metrics.TickOverruns.Add(1);
        }
    }
}
```

**이 루프 안에 LLM이 없다.** 재계획 워커는 별도 `BackgroundService`이고, 완성된 플랜을 `_store.PendingPlanId[i]`에 `Volatile.Write`로 넣을 뿐이다.

### 시간 관리

```csharp
// Npc.Runtime/GameClock.cs
public sealed class GameClock
{
    public Tick     Current   { get; private set; }
    public int      TimeScale { get; init; }        // 1 = 실시간, 60 = 60배속
    public TimeOfDay TimeOfDay { get; private set; }

    // 게임 하루 = 24 게임시간(86,400 게임초). TimeScale 60이면 실시간 24분.
    // 환산: 게임초 = 틱 × TimeScale ÷ 10  (실시간 1초 = 10틱)
    // scale 60  → 1틱 = 6 게임초,  게임 하루 = 14,400틱
    // scale 600 → 1틱 = 60 게임초, 게임 하루 = 1,440틱
}
```

`TimeOfDay` 전환 시 `GameTimeChanged` 이벤트가 Sim에서 발행되고, NPC 서버는 이걸 받아 **해당 시간대의 버킷 키로 전체 플랜을 스왑**한다. 이 순간이 초당 최대 부하 지점이므로 W4에서 반드시 측정한다.

> **대량 스왑 완화**: 5,000마리가 동시에 스왑하면 스파이크가 생긴다. 시간대 전환은 ±5분(게임시간) 지터를 주어 NPC별로 분산한다. 지터는 `npcId` 해시로 결정론적으로 만든다.

---

## 6. `Npc.Sim` — 게임서버 대역

§02 문서 §5의 사양을 구현한다. W3의 절반이 여기에 들어간다.

```csharp
// Npc.Sim/SimWorld.cs
public sealed class SimWorld
{
    public void ApplyCommand(in NpcCommand cmd, Tick now)
    {
        switch (cmd.Kind)
        {
            case NpcCommandKind.MoveTo:
                var dist = _poiDistances[_curPoi[cmd.Npc], cmd.TargetPoi];
                var arriveAt = now.Value + (long)(dist * SecPerMeter(cmd.Flags) * TickHz);
                _pendingArrivals.Add(new(cmd.Npc, cmd.Correlation, cmd.TargetPoi, arriveAt));
                break;

            case NpcCommandKind.Interact:
                var dur = _actions[cmd.ActionCode].DurationTicks(cmd.Amount);
                _pendingCompletions.Add(new(cmd.Npc, cmd.Correlation, now.Value + dur));
                break;
            // ...
        }
    }

    public void Tick(Tick now, ChannelWriter<GameEvent> events)
    {
        EmitArrivals(now, events);          // NpcArrived
        EmitCompletions(now, events);       // NpcActionCompleted
        EmitTransforms(now, events);        // NpcTransform (N틱마다 보간)
        _playerBots.Tick(now, events);      // PlayerProximity
        _scenario.Tick(now, events);        // 시나리오 스크립트 주입
        _needs.Tick(now, events);           // 배고픔/피로 진행 → NpcVitalsChanged
    }
}
```

### 반드시 넣어야 하는 것

| 기능 | 이유 |
|---|---|
| **실패 주입** | `--fail-rate 0.02` 로 `NpcActionFailed`를 확률 발생. 폴백 경로가 실제로 도는지 확인 |
| **명령 드롭 주입** | `--drop-rate 0.001` 로 명령을 조용히 버림. 타임아웃 합성(§03 §6) 검증 |
| **가상 플레이어 봇** | LOD와 우선순위 큐 `w1` 검증에 필수. 존을 랜덤 워크하는 봇 N명 |
| **시나리오 스크립트** | §02 §5의 jsonl. 공성/재해/킬스위치 주입 |
| **욕구 진행** | 배고픔·피로가 시간에 따라 오르지 않으면 NPC가 계획을 바꿀 이유가 없다 |

**욕구 진행이 없으면 이 프로젝트 전체가 정지 화면이 된다.** Sim이 NPC 상태를 실제로 변화시켜야 재계획이 의미를 갖는다.

---

## 7. 폴백 플랜 40개 수작성 (W4)

**이 프로젝트에서 사람 손이 가장 많이 가는 부분이자, R&D 보고서의 비교 기준선이다.**

```
작성 절차 (아키타입 1개당)
1. 아키타입의 allowed_actions 확인
2. 하루 사이클(기상→노동→식사→휴식→취침)을 4~8스텝으로 구성
3. loop: true 로 닫히는지 확인
4. 검증기 1~3단 통과 확인 (4단은 W6에 구현되므로 이 시점엔 생략)
5. **작성 소요 시간을 기록한다**  ← 반드시
```

```jsonc
// 시간 기록 (docs/measurements/authoring_time.jsonl)
{"archetype":"blacksmith","minutes":22,"revisions":3,"author":"heungbae"}
{"archetype":"town_guard","minutes":14,"revisions":1,"author":"heungbae"}
```

이 로그가 W12 보고서의 "오써링 공수 절감률" 분자가 된다. 40개 × 평균 15분 = 약 10시간이 예상되며, LLM은 2,880개를 3분에 만든다. **비교 대상은 개수가 아니라 "1개당 소요"**이므로 정직하게 계산한다 — 사람 15분/개 vs LLM 0.06초/개 + 검수 시간.

---

## 8. NPC 5,000 생성

```csharp
// tools/gen_npcs.cs
// 1) archetypes.population_weight 로 아키타입별 인원 배분
// 2) pois.capacity 를 넘지 않게 workplace/home 배정 (그리디 + 존 균형)
// 3) trait_offsets 를 seed 고정 난수로 부여 (±15)
// 4) 초기 인벤토리를 아키타입 기본값으로
// 5) V10(정원 충족) 검증 후 출력
```

체크:
- [ ] 같은 seed로 두 번 돌리면 바이트 동일한 파일이 나온다
- [ ] 모든 NPC에 `home_poi`와 `workplace_poi`가 배정됐다 (`villager`·`child` 등은 workplace 없음 허용)
- [ ] 존별 인구가 `zones.capacity` 이내다

---

## 9. 성능 튜닝 (W4)

### 측정 항목

| 지표 | 목표 | 도구 |
|---|---|---|
| 틱 실행 시간 p50/p99 | ≤ 8ms / ≤ 20ms | `Meter` 히스토그램 |
| 인지 스캔 대상/틱 | ≤ 150 | 카운터 |
| 명령 송출량 | ≥ 2,000 cmd/s 무할당 | 카운터 + `GC.GetAllocatedBytesForCurrentThread` |
| Gen0 컬렉션 | 틱 루프에서 0 | `GC.CollectionCount(0)` 델타 |
| 정상 상태 관리 힙 | ≤ 400MB | `GC.GetTotalMemory` |

### 할당 제거 체크리스트

- [ ] `NpcCommand` 배치 큐는 `ArrayPool<NpcCommand>` 또는 사전 할당 링 버퍼
- [ ] `Emit()`이 `IEnumerable`을 반환하지 않는다 → `Span<NpcCommand>`에 쓰기
- [ ] LINQ가 틱 루프에 없다
- [ ] 로깅이 틱 루프에서 문자열 보간을 하지 않는다 → 소스 생성 로거(`[LoggerMessage]`)
- [ ] `System.Text.Json` 소스 생성기 사용, 런타임 리플렉션 0

```csharp
[LoggerMessage(Level = LogLevel.Debug, Message = "npc {Npc} step {Step} failed: {Reason}")]
static partial void LogStepFailed(ILogger l, int npc, int step, ActionFailReason reason);
```

---

## 10. 대시보드 1차 (W4)

단일 HTML. `Npc.Host`가 `/dashboard`에서 서빙하고, `/metrics`의 JSON을 폴링한다.

| 패널 | 내용 |
|---|---|
| 틱 | p50/p99 실행 시간, 오버런 횟수, 게임 시각 |
| NPC | 총원, LOD 밴드별 분포, 상태(VisualState) 히스토그램 |
| 액션 | 현재 실행 중인 액션 Top 10 |
| 링크 | 명령 송출/드롭, 이벤트 수신, 시퀀스 갭 |
| 재계획 | 큐 깊이, 초당 유입 (W9에 티어별 분리) |

W7 이후 캐시 히트율, W9 이후 토큰/비용 패널을 추가한다.

---

## 11. 게이트 확인

- [ ] `Npc.Host --loopback --npcs 500 --time-scale 600 --days 7` 이 완주한다
- [ ] LLM 호출 카운터가 **0**이다
- [ ] 7일 동안 크래시·데드락 없음
- [ ] 대장장이 NPC 1마리를 추적한 로그가 "기상→광산→대장간→선술집→귀가" 사이클을 보여준다
- [ ] 틱 p99 ≤ 20ms (5,000 NPC 기준으로도 측정)
- [ ] `IGameServerLink`를 `Null`로 바꿔도 코드 변경 없이 기동한다
- [ ] 마스터데이터 검증 V1~V11 전부 통과

---

## 12. 흔한 실패와 대응

| 증상 | 원인 | 대응 |
|---|---|---|
| NPC가 전부 같은 POI에 몰림 | `$nearest_*` 바인딩이 결정론적 | 바인딩에 `npcId` 해시 지터 추가 |
| 틱 시간이 계단식으로 튐 | 시간대 전환 시 대량 스왑 | §5의 지터 적용 |
| 이벤트 큐가 무한히 자람 | `NpcTransform` 폭주 | Sim의 transform 발행 주기를 LOD에 연동 (Lod 2·3은 발행 안 함) |
| Gen0 GC가 계속 돔 | 틱 루프 할당 | §9 체크리스트 |
| NPC가 특정 스텝에서 영구 정지 | 완료 이벤트 미수신 | `timeout_s` 누락. 모든 스텝에 기본 타임아웃 강제 |
