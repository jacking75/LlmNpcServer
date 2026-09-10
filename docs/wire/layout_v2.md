# 와이어 배치 — 오프셋 표 (v1 · v2)

> **생성물이다. 손으로 고치지 않는다.**
> `dotnet test --filter FullyQualifiedName~LayoutDocTests` 가 코드에서 다시 뽑아
> 이 파일과 대조한다. 어긋나면 테스트가 깨지고, 그때 새로 쓴 것이 여기 남는다.

이 문서는 **다른 언어로 게임서버를 구현할 때** 읽는 것이다. C# DTO 의 선언 순서를
추측하지 않아도 되도록 필드별 오프셋·크기·부호·엔디언을 그대로 적는다.

참조 코덱은 `docs/wire/reference/` 에, 골든 바이트 벡터는 `docs/wire/vectors_v2/` 에 있다.

## 공통 규칙

| 항목 | 값 |
|---|---|
| 바이트 순서 | **리틀엔디언**. 예외는 `WireNonce` 하나로, HMAC 입력이라 빅엔디언으로 푼다 |
| 패딩 | **배치(명령·이벤트)에는 없다.** v1 핸드셰이크에는 있다 — 아래 표에 `(패딩)` 줄로 적었다. 쓰는 쪽은 0 으로 채우고 읽는 쪽은 건너뛴다 |
| 부동소수 | IEEE 754 `binary32`. 좌표는 게임서버 좌표계 그대로다 |
| 문자열 | **없다** (N3). 해시도 `uint64` 4개다 |
| 시각 | `Tick`(int64) 뿐이다 (N4) |

## 프레임 헤더 (8바이트)

| 필드 | 오프셋 | 크기 | 타입 | 뜻 |
|---|---:|---:|---|---|
| `PayloadLength` | 0 | 4 | uint32 | 헤더를 뺀 페이로드 바이트 수. 상한 1 MiB |
| `Kind` | 4 | 1 | uint8 | `LinkMessageKind` |
| `Ver` | 5 | 1 | uint8 | 와이어 버전. 받아 줄 범위 [1, 2] |
| `Reserved` | 6 | 2 | uint16 | 0 |

**`Ver` 가 배치를 정한다.** 협상 결과가 아니라 프레임이 말하는 버전을 믿는다 —
협상 직후 경계에서 두 버전이 섞여 도착할 수 있다.

## 배치 페이로드

| 항목 | 값 |
|---|---|
| 접두 | `int32` 원소 수 (리틀엔디언). **음수는 null 배열**이고 빈 배치(0)와 구분한다 |
| 원소 | 접두 뒤에 크기 고정 구조체가 연속으로. 원소 사이에 태그·구분자가 없다 |
| 명령 | v1 56 B · **v2 72 B** |
| 이벤트 | v1 64 B · **v2 80 B** |

## `WireHello` — 88 B

v1 핸드셰이크. 게임서버 → NPC 서버

| 필드 | 오프셋 | 크기 | 타입 | 바이트 순서 |
|---|---:|---:|---|---|
| `ProtocolVersion` | 0 | 4 | int32 | LE |
| `TickRate` | 4 | 4 | int32 | LE |
| `TimeScale` | 8 | 4 | int32 | LE |
| `NpcCount` | 12 | 4 | int32 | LE |
| `StartTick` | 16 | 8 | int64 | LE |
| `MasterData.A` | 24 | 8 | uint64 | LE |
| `MasterData.B` | 32 | 8 | uint64 | LE |
| `MasterData.C` | 40 | 8 | uint64 | LE |
| `MasterData.D` | 48 | 8 | uint64 | LE |
| `Roster.A` | 56 | 8 | uint64 | LE |
| `Roster.B` | 64 | 8 | uint64 | LE |
| `Roster.C` | 72 | 8 | uint64 | LE |
| `Roster.D` | 80 | 8 | uint64 | LE |

줄 크기 합 **88 B** = 구조체 크기 **88 B**.

## `WireHelloAck` — 88 B

v1 핸드셰이크 응답

| 필드 | 오프셋 | 크기 | 타입 | 바이트 순서 |
|---|---:|---:|---|---|
| `ProtocolVersion` | 0 | 4 | int32 | LE |
| `TimeScale` | 4 | 4 | int32 | LE |
| `NpcCount` | 8 | 4 | int32 | LE |
| `(패딩)` | 12 | 4 | uint8[] | 0 으로 채운다 |
| `MasterData.A` | 16 | 8 | uint64 | LE |
| `MasterData.B` | 24 | 8 | uint64 | LE |
| `MasterData.C` | 32 | 8 | uint64 | LE |
| `MasterData.D` | 40 | 8 | uint64 | LE |
| `Roster.A` | 48 | 8 | uint64 | LE |
| `Roster.B` | 56 | 8 | uint64 | LE |
| `Roster.C` | 64 | 8 | uint64 | LE |
| `Roster.D` | 72 | 8 | uint64 | LE |
| `Accepted` | 80 | 1 | uint8 | LE |
| `RejectCode` | 81 | 1 | uint8 | LE |
| `(패딩)` | 82 | 6 | uint8[] | 0 으로 채운다 |

줄 크기 합 **88 B** = 구조체 크기 **88 B**.

## `WireHelloV2` — 216 B

v2 핸드셰이크. 버전 범위·계약 버전·기능 비트·인증 (B-01 · A-06)

| 필드 | 오프셋 | 크기 | 타입 | 바이트 순서 |
|---|---:|---:|---|---|
| `ProtocolVersion` | 0 | 4 | int32 | LE |
| `MinProtocolVersion` | 4 | 4 | int32 | LE |
| `ContractMajor` | 8 | 2 | uint16 | LE |
| `ContractMinor` | 10 | 2 | uint16 | LE |
| `(패딩)` | 12 | 4 | uint8[] | 0 으로 채운다 |
| `Features` | 16 | 8 | uint64 | LE |
| `TickRate` | 24 | 4 | int32 | LE |
| `TimeScale` | 28 | 4 | int32 | LE |
| `NpcCount` | 32 | 4 | int32 | LE |
| `(패딩)` | 36 | 4 | uint8[] | 0 으로 채운다 |
| `StartTick` | 40 | 8 | int64 | LE |
| `StartGameMinuteOfDay` | 48 | 2 | uint16 | LE |
| `ShardId` | 50 | 2 | uint16 | LE |
| `(패딩)` | 52 | 4 | uint8[] | 0 으로 채운다 |
| `ZoneMask` | 56 | 8 | uint64 | LE |
| `SessionEpoch` | 64 | 4 | uint32 | LE |
| `(패딩)` | 68 | 4 | uint8[] | 0 으로 채운다 |
| `MasterDataStructural.A` | 72 | 8 | uint64 | LE |
| `MasterDataStructural.B` | 80 | 8 | uint64 | LE |
| `MasterDataStructural.C` | 88 | 8 | uint64 | LE |
| `MasterDataStructural.D` | 96 | 8 | uint64 | LE |
| `MasterDataContent.A` | 104 | 8 | uint64 | LE |
| `MasterDataContent.B` | 112 | 8 | uint64 | LE |
| `MasterDataContent.C` | 120 | 8 | uint64 | LE |
| `MasterDataContent.D` | 128 | 8 | uint64 | LE |
| `Roster.A` | 136 | 8 | uint64 | LE |
| `Roster.B` | 144 | 8 | uint64 | LE |
| `Roster.C` | 152 | 8 | uint64 | LE |
| `Roster.D` | 160 | 8 | uint64 | LE |
| `Nonce.A` | 168 | 8 | uint64 | LE |
| `Nonce.B` | 176 | 8 | uint64 | LE |
| `Auth.A` | 184 | 8 | uint64 | LE |
| `Auth.B` | 192 | 8 | uint64 | LE |
| `Auth.C` | 200 | 8 | uint64 | LE |
| `Auth.D` | 208 | 8 | uint64 | LE |

줄 크기 합 **216 B** = 구조체 크기 **216 B**.

## `WireHelloAckV2` — 160 B

v2 핸드셰이크 응답

| 필드 | 오프셋 | 크기 | 타입 | 바이트 순서 |
|---|---:|---:|---|---|
| `ProtocolVersion` | 0 | 4 | int32 | LE |
| `ContractMajor` | 4 | 2 | uint16 | LE |
| `ContractMinor` | 6 | 2 | uint16 | LE |
| `Features` | 8 | 8 | uint64 | LE |
| `TimeScale` | 16 | 4 | int32 | LE |
| `NpcCount` | 20 | 4 | int32 | LE |
| `MasterDataStructural.A` | 24 | 8 | uint64 | LE |
| `MasterDataStructural.B` | 32 | 8 | uint64 | LE |
| `MasterDataStructural.C` | 40 | 8 | uint64 | LE |
| `MasterDataStructural.D` | 48 | 8 | uint64 | LE |
| `MasterDataContent.A` | 56 | 8 | uint64 | LE |
| `MasterDataContent.B` | 64 | 8 | uint64 | LE |
| `MasterDataContent.C` | 72 | 8 | uint64 | LE |
| `MasterDataContent.D` | 80 | 8 | uint64 | LE |
| `Roster.A` | 88 | 8 | uint64 | LE |
| `Roster.B` | 96 | 8 | uint64 | LE |
| `Roster.C` | 104 | 8 | uint64 | LE |
| `Roster.D` | 112 | 8 | uint64 | LE |
| `Auth.A` | 120 | 8 | uint64 | LE |
| `Auth.B` | 128 | 8 | uint64 | LE |
| `Auth.C` | 136 | 8 | uint64 | LE |
| `Auth.D` | 144 | 8 | uint64 | LE |
| `Accepted` | 152 | 1 | uint8 | LE |
| `RejectCode` | 153 | 1 | uint8 | LE |
| `ContentHashWarning` | 154 | 1 | uint8 | LE |
| `(패딩)` | 155 | 5 | uint8[] | 0 으로 채운다 |

줄 크기 합 **160 B** = 구조체 크기 **160 B**.

## `WireCommand` — 56 B

v1 명령. **동결이다** — 새 필드는 v2 에만

| 필드 | 오프셋 | 크기 | 타입 | 바이트 순서 |
|---|---:|---:|---|---|
| `IssuedAt` | 0 | 8 | int64 | LE |
| `Npc` | 8 | 4 | int32 | LE |
| `TargetNpc` | 12 | 4 | int32 | LE |
| `TargetPlayer` | 16 | 4 | int32 | LE |
| `Amount` | 20 | 4 | int32 | LE |
| `Correlation` | 24 | 4 | uint32 | LE |
| `PosX` | 28 | 4 | float32 (IEEE 754) | LE |
| `PosY` | 32 | 4 | float32 (IEEE 754) | LE |
| `PosZ` | 36 | 4 | float32 (IEEE 754) | LE |
| `TargetPoi` | 40 | 2 | uint16 | LE |
| `Item` | 42 | 2 | uint16 | LE |
| `Animation` | 44 | 2 | uint16 | LE |
| `Dialogue` | 46 | 2 | uint16 | LE |
| `Archetype` | 48 | 2 | uint16 | LE |
| `Zone` | 50 | 2 | uint16 | LE |
| `Kind` | 52 | 1 | uint8 | LE |
| `Priority` | 53 | 1 | uint8 | LE |
| `Visual` | 54 | 1 | uint8 | LE |
| `Flags` | 55 | 1 | uint8 | LE |

줄 크기 합 **56 B** = 구조체 크기 **56 B**.

## `WireEvent` — 64 B

v1 이벤트. **동결**

| 필드 | 오프셋 | 크기 | 타입 | 바이트 순서 |
|---|---:|---:|---|---|
| `Sequence` | 0 | 8 | int64 | LE |
| `OccurredAt` | 8 | 8 | int64 | LE |
| `Npc` | 16 | 4 | int32 | LE |
| `OtherNpc` | 20 | 4 | int32 | LE |
| `Player` | 24 | 4 | int32 | LE |
| `Amount` | 28 | 4 | int32 | LE |
| `Correlation` | 32 | 4 | uint32 | LE |
| `PosX` | 36 | 4 | float32 (IEEE 754) | LE |
| `PosY` | 40 | 4 | float32 (IEEE 754) | LE |
| `PosZ` | 44 | 4 | float32 (IEEE 754) | LE |
| `Heading` | 48 | 4 | float32 (IEEE 754) | LE |
| `Poi` | 52 | 2 | uint16 | LE |
| `Zone` | 54 | 2 | uint16 | LE |
| `Item` | 56 | 2 | uint16 | LE |
| `Hp` | 58 | 2 | int16 | LE |
| `Stamina` | 60 | 2 | int16 | LE |
| `Kind` | 62 | 1 | uint8 | LE |
| `Code` | 63 | 1 | uint8 | LE |

줄 크기 합 **64 B** = 구조체 크기 **64 B**.

## `WireCommandV2` — 72 B

v2 명령. 확장 슬롯 포함 (B-02)

| 필드 | 오프셋 | 크기 | 타입 | 바이트 순서 |
|---|---:|---:|---|---|
| `IssuedAt` | 0 | 8 | int64 | LE |
| `Npc` | 8 | 4 | int32 | LE |
| `TargetNpc` | 12 | 4 | int32 | LE |
| `TargetPlayer` | 16 | 4 | int32 | LE |
| `Amount` | 20 | 4 | int32 | LE |
| `Correlation` | 24 | 4 | uint32 | LE |
| `PosX` | 28 | 4 | float32 (IEEE 754) | LE |
| `PosY` | 32 | 4 | float32 (IEEE 754) | LE |
| `PosZ` | 36 | 4 | float32 (IEEE 754) | LE |
| `ExtA` | 40 | 4 | uint32 | LE |
| `ExtB` | 44 | 4 | uint32 | LE |
| `Reserved` | 48 | 4 | uint32 | LE |
| `TargetPoi` | 52 | 2 | uint16 | LE |
| `Item` | 54 | 2 | uint16 | LE |
| `Animation` | 56 | 2 | uint16 | LE |
| `Dialogue` | 58 | 2 | uint16 | LE |
| `Archetype` | 60 | 2 | uint16 | LE |
| `Zone` | 62 | 2 | uint16 | LE |
| `Instance` | 64 | 2 | uint16 | LE |
| `Faction` | 66 | 2 | uint16 | LE |
| `Kind` | 68 | 1 | uint8 | LE |
| `Priority` | 69 | 1 | uint8 | LE |
| `Visual` | 70 | 1 | uint8 | LE |
| `Flags` | 71 | 1 | uint8 | LE |

줄 크기 합 **72 B** = 구조체 크기 **72 B**.

## `WireEventV2` — 80 B

v2 이벤트. 확장 슬롯 포함 (B-02)

| 필드 | 오프셋 | 크기 | 타입 | 바이트 순서 |
|---|---:|---:|---|---|
| `Sequence` | 0 | 8 | int64 | LE |
| `OccurredAt` | 8 | 8 | int64 | LE |
| `Npc` | 16 | 4 | int32 | LE |
| `OtherNpc` | 20 | 4 | int32 | LE |
| `Player` | 24 | 4 | int32 | LE |
| `Amount` | 28 | 4 | int32 | LE |
| `Correlation` | 32 | 4 | uint32 | LE |
| `PosX` | 36 | 4 | float32 (IEEE 754) | LE |
| `PosY` | 40 | 4 | float32 (IEEE 754) | LE |
| `PosZ` | 44 | 4 | float32 (IEEE 754) | LE |
| `Heading` | 48 | 4 | float32 (IEEE 754) | LE |
| `ExtA` | 52 | 4 | uint32 | LE |
| `ExtB` | 56 | 4 | uint32 | LE |
| `Reserved` | 60 | 4 | uint32 | LE |
| `Poi` | 64 | 2 | uint16 | LE |
| `Zone` | 66 | 2 | uint16 | LE |
| `Item` | 68 | 2 | uint16 | LE |
| `Instance` | 70 | 2 | uint16 | LE |
| `Faction` | 72 | 2 | uint16 | LE |
| `Hp` | 74 | 2 | int16 | LE |
| `Stamina` | 76 | 2 | int16 | LE |
| `Kind` | 78 | 1 | uint8 | LE |
| `Code` | 79 | 1 | uint8 | LE |

줄 크기 합 **80 B** = 구조체 크기 **80 B**.

## `WireHeartbeat` — 16 B

하트비트. 양방향

| 필드 | 오프셋 | 크기 | 타입 | 바이트 순서 |
|---|---:|---:|---|---|
| `Tick` | 0 | 8 | int64 | LE |
| `Sequence` | 8 | 8 | int64 | LE |

줄 크기 합 **16 B** = 구조체 크기 **16 B**.

## `WireBye` — 1 B

종료 통보. 양방향

| 필드 | 오프셋 | 크기 | 타입 | 바이트 순서 |
|---|---:|---:|---|---|
| `Code` | 0 | 1 | uint8 | LE |

줄 크기 합 **1 B** = 구조체 크기 **1 B**.

