# 2장 — `/metrics` 필드 사전

`GET http://localhost:5080/metrics` 가 주는 JSON 한 장을 패널별로 푼다.
**정상 범위와 경보 기준**을 같이 적었다. 값이 왜 그렇게 나오는지는 각 열의 "더 볼 것" 장에 있다.

아래 실측 값은 NPC 20 · 배속 600 · `--no-llm` 으로 게임 1일을 돌린 회차의 것이다.

---

## `tick` — 틱 루프

```json
"tick": { "p50Ms":0.014, "p99Ms":0.568, "maxMs":6.566, "overruns":0, "ticks":427,
          "gameDay":0, "gameHour":13, "timeOfDay":"Noon",
          "gen0Collections":0, "bytesPerTick":0, "managedHeapMb":70.2 }
```

| 필드 | 뜻 | 정상 | 경보 |
|---|---|---|---|
| `p50Ms` · `p99Ms` | 틱 실행 시간 백분위 (최근 4,096틱) | p99 ≤ 20 ms | p99 > 20 ms |
| `maxMs` | 관측 창의 최댓값 | 첫 몇 틱은 JIT 때문에 크다 | 정상 구간에서 튀면 본다 |
| `overruns` | 예산(20ms)을 넘긴 틱 수 | **0** | 1 이상 |
| `ticks` | 지금까지 돈 틱 | — | — |
| `gameDay`·`gameHour`·`timeOfDay` | 게임 시각 | — | — |
| `gen0Collections` | Gen0 GC 횟수 | — | **이 값으로 틱 루프를 재지 않는다** |
| `bytesPerTick` | 틱당 힙 할당 바이트 | **0** | 0이 아니면 그 자체가 회귀 |
| `managedHeapMb` | 관리 힙 크기 | 70MB 안팎 | 계속 자라면 누수 |

**`gen0Collections` 로 재지 않는 이유.** 이 값은 `GC.CollectionCount(0)` 이라 **프로세스 전역**이다.
대시보드 HTTP·소켓 리시버·JSON 직렬화가 전부 여기에 들어간다. 틱 루프만 재는 계기는
`bytesPerTick` **하나뿐**이다. → 더 볼 것: 19장

---

## `npc` — NPC 분포

```json
"npc": { "total":20, "byLod":[0,11,0,9], "byStepStatus":[0,20,0,0,0,0,0,0], "bandMigrations":471 }
```

| 필드 | 뜻 |
|---|---|
| `total` | NPC 수 (`--npcs`) |
| `byLod` | 인지 LOD 밴드별 인원. 첨자가 곧 밴드다 |
| `byStepStatus` | 스텝 상태별 인원. 첨자가 곧 상태 코드다 |
| `bandMigrations` | 밴드 사이를 오간 누적 횟수. 플레이어가 움직이면 늘어난다 |

**`byLod` 첨자 = 밴드.** 앞이 가깝고 비싸다.

| 첨자 | 밴드 | 스캔 주기 |
|---:|---|---|
| 0 | 시야내 | **매 틱** |
| 1 | 동일존 | 10틱마다 |
| 2 | 원거리 | 100틱마다 |
| 3 | 비활성 | 안 본다 |

위 실측 `[0, 11, 0, 9]` 는 시야내 0 · 동일존 11 · 원거리 0 · 비활성 9 다.
**플레이어 근처에 아무도 없으면 0번 밴드가 비고, 그것이 정상이다.**

**`byStepStatus` 첨자 = 상태.** `0 Ready` · `1 Waiting` · `2 Done` · `3 Unspawned` ·
`4 Completed` · `5 Failed`. 위 실측 `[0,20,0,0,0,0,0,0]` 은 20마리 전원이 `Waiting` —
명령을 보내고 게임서버의 응답 이벤트를 기다리는 상태다. **정상 구동의 대부분이 이 상태다.**

`byLod` 의 합도 `byStepStatus` 의 합도 `total` 과 같아야 한다. → 더 볼 것: 6장

---

## `actions` — 지금 무엇을 하고 있나

```json
"actions": [ {"action":"MoveTo","count":10}, {"action":"Farm","count":2}, ... ]
```

현재 진행 중인 스텝의 액션별 인원. **`MoveTo` 가 항상 1등이다** — 무엇을 하든 거기까지 가야 하기 때문이다.
이 목록이 한 액션으로 쏠려 있으면 플랜 다양성을 의심한다. → 더 볼 것: 18장

---

## `link` — 게임서버 링크

```json
"link": { "commandsEnqueued":3503, "commandsFlushed":3503, "commandsDropped":0,
          "pendingCommands":0, "eventsDrained":4498, "eventGaps":0, "eventBacklogs":0 }
```

| 필드 | 뜻 | 정상 | 경보 |
|---|---|---|---|
| `commandsEnqueued` / `Flushed` | 큐에 쌓은 / 내보낸 명령 | 둘이 거의 같다 | 벌어지면 역압 |
| `commandsDropped` | 역압으로 버린 명령 | **0** | 1 이상이면 발행 큐가 좁다 |
| `pendingCommands` | 아직 안 나간 명령 | 0에 가깝다 | 계속 쌓이면 링크가 느리다 |
| `eventsDrained` | 처리한 이벤트 | — | — |
| `eventGaps` | 시퀀스 갭 검출 (N6) | **0** | 1 이상 = 이벤트 유실 |
| `eventBacklogs` | 한 틱 상한에 걸려 다음 틱으로 넘긴 횟수 | **0** | 계속 쌓이면 이벤트가 밀린다 |

→ 더 볼 것: 12·13장

---

## `replan` — 재계획 큐

```json
"replan": { "queueDepth":19, "enqueuedPerSecond":6.43, "deviations":685, "scanPerTick":1,
            "interruptsForced":0, "interruptsSuppressed":0,
            "queueDropped":0, "queueDepthP99":19, "urgentCount":0,
            "scoreHistogram":[19,0,0,0,0,0,0,0,0,0,0], "tiers":[], "staleDiscarded":0 }
```

| 필드 | 뜻 |
|---|---|
| `queueDepth` · `queueDepthP99` | 재계획 대기열 깊이 (지금 · p99) |
| `deviations` | 인지 스캔이 "플랜이 상황과 어긋난다"고 판정한 누적 횟수 |
| `scanPerTick` | 이번 틱에 본 NPC 수. **상한 150** |
| `interruptsForced` | 인터럽트가 즉시 발행한 액션 |
| `scoreHistogram` | 재계획 점수 분포. **마지막 칸이 인터럽트 전용**(1000점대) |
| `tiers` | 티어별 워커 처리량. `--tier none` 이면 빈 배열이다 |

**`--tier none` 이면 큐가 차기만 하고 비지 않는다.** 꺼내 갈 워커가 없기 때문이고, 그것이 정상이다.
16장에서 티어를 켜면 이 배열에 행이 생긴다. → 더 볼 것: 6·16장

---

## `cache` — 플랜 캐시

```json
"cache": { "hitRate":0.9714, "hits":136, "misses":4,
           "filledBuckets":718, "coldBuckets":2162, "pinnedBuckets":0,
           "individualTurnover":0, "individualLive":0,
           "topMisses":[...], "worstArchetypes":[...] }
```

| 필드 | 뜻 |
|---|---|
| `hitRate` | 플랜 조회 적중률. 목표 ≥ 0.98 |
| `filledBuckets` / `coldBuckets` | 플랜이 있는 / 없는 버킷 (합 2,880) |
| `pinnedBuckets` | 사람이 검수해 고정한 플랜 수 |
| `topMisses` | 가장 자주 빗나간 버킷. **여기가 다음에 구울 목록이다** |
| `worstArchetypes` | 아키타입별 적중률 낮은 순 |

**`filledBuckets` 가 0이어도 정상이다.** `planstore/plans/` 는 생성물이라 `.gitignore` 대상이고,
저장소를 새로 받으면 비어 있다. 그때는 모든 NPC 가 아키타입 폴백 40개로 돌고
`/npc/{id}` 의 `planKind` 가 전부 `fallback` 으로 보인다. → 더 볼 것: 9·17장

---

## `llmCalls` · `cost` — LLM

```json
"llmCalls": 0,
"cost": { "enabled":false, "engine":null, "calls":0, "promptTokens":0, "costUsd":0,
          "promptCacheHitRate":0, "uniquePrefixHashes":0, "tokensToday":0, ... }
```

`--no-llm`(= `--tier none`) 인 동안은 **전부 0이고 `enabled` 가 false** 다.
1~15장 내내 이 상태로 진행한다. 여기가 0이 아니게 되는 첫 순간이 16장이다.

한 필드만 미리 기억해 둔다 — **`uniquePrefixHashes` 는 1이어야 한다.**
2 이상이면 프롬프트 프리픽스가 요청마다 달라졌다는 뜻이고, 캐시가 전면 미적중이라 비용이 튄다.
→ 더 볼 것: 16·17장

---

## `heatmap` — 버킷 히트맵

```json
"heatmap": { "archetypes":40, "columns":72, "cells":[...2880개...],
             "filled":718, "used":14, "usedRatio":0.0049, "peak":29,
             "archetypeNames":["blacksmith","carpenter", ... ] }
```

아키타입 40행 × (시간대 6 × 지역상태 4 × 기후 3 = 72)열의 조회 횟수 표다.
CSV 로 받으려면 `GET /heatmap.csv`.

**`usedRatio` 가 매우 작은 것이 정상이다.** 2,880 버킷이 있어도 한 회차에서 실제로
조회되는 것은 극히 일부다 — 위 회차는 20마리가 14개 버킷만 썼다(0.49%).
전체 실측으로도 도달 버킷은 264개(9.2%)뿐이다.
**"2,880개를 다 구울 필요가 없다"** 는 결론이 이 표에서 나왔다. → 더 볼 것: 17장
