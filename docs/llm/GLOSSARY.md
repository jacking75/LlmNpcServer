# 용어

한 줄 뜻 + 근거 파일. 헷갈리는 것만 모았다.

## 구조

| 용어 | 뜻 | 근거 |
|---|---|---|
| **NPC 서버** | 우리가 만드는 것. 행동 플랜을 만들고 명령을 낸다 | `src/Npc.Host` |
| **게임서버** | 상대방. **우리가 만들지 않는다.** 명령을 받아 세계를 움직인다 | — |
| **`IGameServerLink`** | 게임서버로 **향하는** 아웃바운드 포트. 이름의 "GameServer" 는 상대방이다 | `Npc.Contracts` |
| **게임서버 대역** | 진짜 게임서버 없이 시험하려고 우리가 만든 가짜 구현 | `Npc.Sim` · `testbed/Npc.TestGameServer` |
| **루프백** | 인프로세스로 `Npc.Sim` 에 직결. **리플레이 일치는 이 경로의 성질**이다 | `LoopbackGameServerLink` |
| **와이어** | 소켓 위의 표현. 계약 타입과 1:1 매핑되는 별도 DTO | `src/Npc.Wire` |
| **프레임** | 8바이트 헤더 + 본문. 버전 범위 [1, 2] 를 협상한다 | `FrameCodec` |
| **확장 슬롯** | v2 패킷의 `Instance`·`Faction`·`ExtA`·`ExtB`. **`ExtSlots` 를 안 켠 게임서버에는 안 나간다** | `ExtensionSlots` |
| **오프셋 표** | 이기종 구현용 배치 명세. **생성물이다** — 손으로 고치지 않는다 | `docs/wire/layout_v2.md` |
| **골든 바이트 벡터** | 대표 메시지의 바이트. 다른 언어 구현이 자기 코덱을 이것으로 시험한다 | `docs/wire/vectors_v2/` |
| **인스턴스** | 채널·인스턴스 던전·레이어. **0 = 기본 월드.** 게임서버가 정하고 우리는 되돌려 준다 | `NpcStore.Instance` |
| **테스트 베드** | 소켓 게임서버 + 뷰어. **아무도 참조하지 않는 잎** | `testbed/` |

## 마스터데이터

| 용어 | 뜻 | 근거 |
|---|---|---|
| **아키타입** | NPC 의 직업/역할. 40종. `code` 가 곧 버킷 첫 차원 | `archetypes.json` |
| **액션** | 플랜이 조합할 수 있는 유일한 원자 단위. **40개 상한**(현재 37) | `actions.json` |
| **월드 플래그** | 64비트 상태. 전제조건·효과가 전부 이 위에서 돈다 | `world_flags.json` |
| **예약 구간** | `reserved_bits`. 새 플래그는 **여기서만** 가져온다 | `world_flags.json` |
| **POI** | 장소. `type`(집·일터·시장 …)과 `subtype`(대장간·양봉장 …)이 다르다 | `pois.json` |
| **POI 심볼** | `$home`·`$workplace`·`$nearest_safe`. **인스턴스 id 직접 지정은 금지** | `PoiSymbol` |
| **정원** | POI 의 `capacity`. 그 일터를 쓰는 아키타입 인구 이상이어야 한다 (V10) | `pois.json` |
| **`duty_hours`** | 근무 시간대. **`OnDuty` 를 세우는 유일한 것** — 없으면 Guard·Patrol 이 못 돈다 | `archetypes.json` |
| **파생물** | 생성물. `poi_distances.bin` · `npc_instances.json`. 낡으면 경고한다 | `derived.lock.json` |
| **구조 해시** | id↔code 대응·POI 좌표·버킷 차원. **핸드셰이크에서 완전 일치를 요구**한다 | `StructuralHash` |
| **내용 해시** | 파일 바이트 전부. 불일치는 **경고 후 수락**이다 | `MasterDataSet.ContentHash` |

## 플랜

| 용어 | 뜻 | 근거 |
|---|---|---|
| **버킷** | 플랜 캐시 키 = 아키타입 × 시간대 6 × 지역 4 × 기후 3 | `BucketKey` |
| **버킷 공간** | 아키타입 수 × 72. **아키타입 수는 마스터데이터가 정한다** | `BucketSpace` |
| **도달 집합** | 실제로 조회되는 버킷. 2,880 중 **264개(9.2%)** 였다 | `reference_metrics.html` |
| **플랜 스토어** | 버킷 → 플랜 고정 배열. 조회는 **첨자 하나** | `PlanStore` |
| **폴백 플랜** | 아키타입마다 하나. `loop:true`. **LLM 이 전부 죽어도 NPC 가 산다** | `fallback_plans.json` |
| **핀(pinned)** | 사람이 고친 플랜. **프리베이크가 다시 만들지 않는다.** 커밋 대상 | `planstore/pinned/` |
| **프리베이크** | 플랜을 미리 대량 생성. 2,880건 ≈ $5.12 | `tools/Npc.Prebake` |
| **무효화 범위** | 무엇을 바꾸면 플랜이 얼마나 무효인가 — None/Partial/Full | `InvalidationScope` |
| **검증기 4단** | 스키마 → 어휘 → 정합성(GOAP 상태 전이) → 드라이런 | `Npc.Core/Validation` · `Npc.Sim` |
| **`timeout_s`** | 스텝 타임아웃. **0 이면 안 된다** — 명령 유실 시 진행을 재개하는 유일한 장치 | `PlanExecutor` |

## LLM

| 용어 | 뜻 | 근거 |
|---|---|---|
| **T1 / T2 / T3** | T1 로컬 작은 모델 · T2 외부 고품질 · T3 캐시 히트 | `TieredPlanCompiler` |
| **프리픽스** | 고정 프롬프트. 기동 시 1회 조립 후 불변. 실측 11,967 토큰 | `PromptPrefix` |
| **서픽스** | 가변 프롬프트. **≤ 300 토큰**(테스트로 강제) | `PlanRequestSuffix` |
| **프리픽스 해시** | 프리픽스 SHA-256. **유니크가 2개 이상이면 즉시 경보** | `/metrics` |
| **스필오버** | T1 큐가 넘쳐 T2 로 흐르는 것. 개체 몫에 **서브 쿼터**가 걸린다 | `SpilloverQuota` |
| **하드 캡** | 일일 토큰 상한. 초과 시 T2 → T1 강등 → 거절. **우회 없음** | `ReplanBudget` |
| **회로 차단기** | 엔진 실패가 쌓이면 끊는다. 429·5xx·타임아웃은 다음 엔진으로 | `FailoverChatClient` |

## 런타임

| 용어 | 뜻 | 근거 |
|---|---|---|
| **틱** | 10Hz. 시간의 유일한 단위 — `DateTime` 을 쓰지 않는다 | `Tick` |
| **SoA** | struct of arrays. `class Npc` 를 5,000개 만들지 않는다 | `NpcStore` |
| **`bytesPerTick`** | 틱 루프 할당 계기. **목표 0.** `gen0Collections` 로 재지 않는다 | `/metrics` |
| **LOD 밴드** | 인지 스캔 등급 4단. 밴드 이동은 틱당 64건 | `LodBandSet` |
| **인지 스캔** | 플랜 이탈 판정. 틱당 상한 150 | `CognitionScheduler` |
| **인터럽트** | 즉시 반응 규칙 14종. **LLM 이 만들지 않는다** — 반응 속도가 생명 | `interrupts.json` |
| **상관 ID** | 명령↔응답을 잇는 순증 번호. `Guid` 를 쓰지 않는다 | `CorrelationTable` |
| **시퀀스** | 이벤트 순번. 갭이 생기면 경보 | `GameEvent.Sequence` |
| **멱등** | 같은 이벤트를 2회 주입해도 상태 해시가 같다 (N7) | `EventApplier` |
| **스냅샷** | NPC 상태 저장. CRC32 · 손상되면 이전 세대로 | `Persistence/` |
| **킬스위치** | 티어·스토어를 끊어 가용성을 시험. 한 번 켜지면 재기동까지 유지 | `KillSwitch` |

## 검증

| 용어 | 뜻 |
|---|---|
| **V1~V13** | 마스터데이터 검증. **실패는 기동 실패**다 |
| **V1.~V4.** | 플랜 검증 4단의 코드 (`V3.PRECONDITION_UNMET` 등) |
| **수정 힌트** | 코드마다 "무엇을 하면 되는가". `--json` 의 `fix_hint` |
| **게이트** | 실측 산출물이 있어야 판정되는 항목. 산출물이 없으면 **실패한다** |
| **골든** | LLM 호출·비용이 발생하는 회귀. **속성 단언만** 쓴다 |
