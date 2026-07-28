# 20. 테스트 베드 작업 지시서 (P6 · T6-01 ~ T6-36)

> 사양은 [`20_TestBed_Spec.md`](20_TestBed_Spec.md)에 있다. **이 문서에 사양을 복사하지 않는다.**
> 태스크 규약(ID·규모·선행·파일·사양·완료)은 [`TASKS.md`](../TASKS.md) §1이 정한다.

---

## 0. 시작 전에

```
1. CLAUDE.md §2 절대 규칙 · §3 의존 규칙을 읽는다
2. docs/02 전문을 읽는다                      ← N1~N8 을 모르면 여기서 반드시 어긴다
3. docs/20 §3 (절대 규칙) 을 읽는다            ← 이 작업에서 깨지기 쉬운 것 5가지
4. 태스크의 "사양" 필드 절을 읽는다
5. 선행 태스크가 §4 원장에서 x 인지 확인한다
6. "파일" 에 적힌 경로에만 만든다
7. "완료" 의 테스트를 작성하고 통과시킨다
8. dotnet build -c Release && dotnet test  전체 통과
9. dotnet format --verify-no-changes 통과
10. 원장을 x 로 바꾸고 커밋한다               (예: `T6-02: 와이어 명령·이벤트 DTO`)
```

**세션 하나에 3개 이상 처리하지 않는다. 1 태스크 = 1 커밋이다.**

### 이 Phase의 상시 확인 사항

매 커밋 전에 이것을 확인한다.

```powershell
git diff --stat main -- src/Npc.Runtime src/Npc.Planning src/Npc.Core src/Npc.Contracts
```

**비어 있어야 한다.** 이 네 프로젝트에 diff가 생기면 그 순간 멈추고 보고한다. `docs/20` §1의 합격 기준 1이다. (`Npc.Gateway`·`Npc.Host`·`Npc.MasterData`는 바뀌어도 된다 — 바뀌라고 만든 자리다.)

`masterdata/` 아래 파일은 **한 바이트도 바뀌면 안 된다**(`docs/20` §3.2).

---

## 1. 태스크

### A. 와이어 프로토콜

**T6-01** `Npc.Wire` 프로젝트 생성 · `S` · 선행 없음
  파일  `src/Npc.Wire/Npc.Wire.csproj` (신규) · `NpcServer.sln` (수정)
  사양  `docs/20` §3.1 · §4
  내용  `Npc.Contracts`와 MemoryPack 1.21.4만 참조하는 클래스 라이브러리. 솔루션의 `src` 폴더에 등록한다. 프로젝트 상단 주석에 "왜 `Npc.Contracts`에 `[MemoryPackable]`을 붙이지 않는가"(§3.1)를 남긴다.
  완료  `dotnet build -c Release` 통과 · `Npc.Contracts.csproj`에 `PackageReference`가 늘지 않았다 · 빈 프로젝트라도 sln에서 빌드된다

**T6-02** 와이어 명령·이벤트 DTO와 매핑 · `M` · 선행 T6-01
  파일  `src/Npc.Wire/WireCommand.cs` (신규) · `src/Npc.Wire/WireEvent.cs` (신규) · `tests/Npc.Tests/Wire/WireDtoTests.cs` (신규)
  사양  `docs/20` §5.3 · `docs/02` §3.2·§3.3
  내용  `NpcCommand`·`GameEvent`와 1:1인 unmanaged struct와 `From`/`To` 매핑. 필드 순서는 사양의 것을 그대로 쓴다(패딩 없이 56/64바이트가 나오는 순서다).
  완료  `Wire_CommandRoundTrips`(12개 Kind, 모든 필드에 서로 다른 값) · `Wire_EventRoundTrips`(17개 Kind) · `Wire_LayoutIsFrozen`(`sizeof`가 56·64) · `Wire_MirrorsContractMembers`(리플렉션으로 멤버 집합 1:1) 통과

**T6-03** 제어 메시지와 `WireHash` · `S` · 선행 T6-02
  파일  `src/Npc.Wire/LinkMessages.cs` (신규) · `tests/Npc.Tests/Wire/WireDtoTests.cs` (수정)
  사양  `docs/20` §5.2 · §5.4
  내용  `WireHash`·`WireHello`·`WireHelloAck`·`WireHeartbeat`·`WireBye`와 `LinkMessageKind`·`LinkRejectCode`·`LinkByeCode`. 해시는 hex 문자열이 아니라 `ulong` 4개다(N3).
  완료  `WireHash.FromHex(h).ToHex() == h`(소문자 64자) · 제어 메시지 4종 왕복 · `Wire_NoStringOrDateTimeFields`가 `Npc.Wire` 어셈블리에서 통과

**T6-04** 프레임 코덱 · `M` · 선행 T6-03
  파일  `src/Npc.Wire/FrameCodec.cs` (신규) · `tests/Npc.Tests/Wire/FrameCodecTests.cs` (신규)
  사양  `docs/20` §5.1
  내용  8바이트 헤더 쓰기(`IBufferWriter<byte>`)와 읽기(`ref SequenceReader<byte>` 또는 `ReadOnlySequence<byte>`에서 완전한 프레임 하나를 떼어내는 `TryReadFrame`). 페이로드 상한 1MiB.
  완료  `Frame_SplitAcrossReads`(1바이트씩 넣어도 복원) · `Frame_MultipleFramesInOneBuffer` · `Frame_RejectsOversizePayload`(예외를 던지되 프로세스는 산다) · `Frame_RejectsWrongVersion` 통과

**T6-05** `PriorityCommandRing` 추출 · `M` · 선행 T6-01
  파일  `src/Npc.Gateway/PriorityCommandRing.cs` (신규) · `src/Npc.Gateway/LoopbackGameServerLink.cs` (수정)
  사양  `docs/20` §5.7 · `docs/02` §1
  내용  `LoopbackGameServerLink`의 우선순위 링·역압 정책을 클래스로 뽑는다. 두 링크가 같은 인스턴스 타입을 쓰게 하기 위한 것이다 — **정책을 복사하면 반드시 갈라진다.** 동작은 한 줄도 바뀌지 않는다.
  완료  기존 `LoopbackLinkTests` 전량 무수정 통과 · `Link_Backpressure` 통과 · `Enqueue` 할당 0 테스트 통과

### B. TCP 링크

**T6-06** `TcpGameServerLink` 연결·핸드셰이크 · `M` · 선행 T6-04, T6-05
  파일  `src/Npc.Gateway/TcpGameServerLink.cs` (수정: 골격 → 구현) · `src/Npc.Gateway/TcpLinkOptions.cs` (신규) · `tests/Npc.Tests/Gateway/NullAndTcpLinkTests.cs` (수정)
  사양  `docs/20` §5.5 · §6.3
  내용  `ConnectAsync` → `Hello` 수신 → 검증 → `HelloAck` 송신. 검증 항목은 프로토콜 버전·타임스케일·마스터데이터 해시·로스터 해시 넷이고, 하나라도 다르면 거절 코드를 담아 보낸 뒤 `Faulted`로 간다. **우회 옵션을 만들지 않는다.**
  완료  `TcpLink_HandshakeRejectsMismatch`(4가지 불일치 각각 해당 `RejectCode`) · `TcpLink_HandshakeAcceptsMatch` 통과 · 기존 `TcpLink_IsSkeletonOnly`는 삭제하고 `Link_ImplementationsAreInterchangeable`은 새 생성자로 갱신

**T6-07** `TcpGameServerLink` 송신 경로 · `M` · 선행 T6-06
  파일  `src/Npc.Gateway/TcpGameServerLink.cs` (수정)
  사양  `docs/20` §6.1 · §6.2 · §5.7
  내용  `Enqueue` → `PriorityCommandRing`. `FlushAsync` → 게시 꼬리를 `Volatile.Write`하고 `ValueTask.CompletedTask` 반환. 소켓 쓰기는 센더 태스크가 한다. **한 번의 Flush = 한 개의 `CommandBatch` 프레임**(N8).
  완료  `TcpLink_FlushDoesNotAllocate`(워밍업 후 10,000회 할당 델타 0) · `TcpLink_BackpressureDropsCosmeticFirst` · `TcpLink_OneFlushIsOneFrame`(수신 측이 센 프레임 수 = Flush 횟수) 통과

**T6-08** `TcpGameServerLink` 수신 경로 · `M` · 선행 T6-07
  파일  `src/Npc.Gateway/TcpGameServerLink.cs` (수정)
  사양  `docs/20` §6.1 · §6.4 · `docs/02` §1 N6
  내용  `PipeReader` → `TryReadFrame` → `WireEvent[]` → `GameEvent` → `Channel<GameEvent>`(단일 기록자·단일 독자). 시퀀스 불연속을 `EventGapsDetected`에 누적한다.
  완료  `TcpLink_DeliversEventsInOrder` · `TcpLink_CountsSequenceGaps`(시퀀스를 건너뛴 배치 주입 → 갭 수 증가) · `LinkStats` 6개 필드가 모두 갱신됨 통과

**T6-09** 재접속·하트비트·상태 전이 · `M` · 선행 T6-08
  파일  `src/Npc.Gateway/TcpGameServerLink.cs` (수정)
  사양  `docs/20` §5.6 · §6.3
  내용  1초 하트비트, 3초 무수신 시 `Degraded` → 재접속(백오프 250ms~4s). 재접속 시 **링에 남은 명령을 버리고** 드롭에 계상한다. `Faulted`는 재시도하지 않는다.
  완료  `TcpLink_ReconnectKeepsSequenceMonotonic`(세션을 끊고 다시 붙여도 시퀀스가 되감기지 않고 NPC가 다시 움직인다) · `TcpLink_StateTransitions`(§6.3 표의 전이가 `StateChanged`로 관측된다) 통과

**T6-10** 링크 사양 문서 갱신 · `S` · 선행 T6-09
  파일  `docs/02_GameServer_Link.md` (수정) · `CLAUDE.md` (수정)
  사양  `docs/20` §17
  내용  `docs/02` §2 표의 `TcpGameServerLink`를 "미구현"에서 "구현(테스트 베드)"으로, §6에 링크 테스트 추가, §7의 5항목에 구현 표시와 남은 것 정리. `CLAUDE.md` §3 의존 그래프에 `Npc.Wire`와 `testbed/*` 추가.
  완료  `docs/02` §2·§6·§7과 `CLAUDE.md` §3이 코드와 일치한다 · **테스트 없음**(문서 태스크)

### C. 로스터와 호스트 배선

**T6-11** `NpcRoster` 추출 · `M` · 선행 없음
  파일  `src/Npc.MasterData/NpcRoster.cs` (신규) · `src/Npc.Host/Program.cs` (수정) · `tests/Npc.Tests/MasterData/NpcRosterTests.cs` (신규)
  사양  `docs/20` §10.1 · §10.2
  내용  NPC 선택 규칙(균등 간격)과 로스터 해시를 한 곳에 모으고 `Program.cs`의 인라인 루프를 이 호출로 바꾼다. 존 필터를 받는다.
  완료  `Roster_SelectionMatchesLegacyFormula`(npcs 1·7·500·5000에서 기존 공식과 **원소 단위로 동일**) · `Roster_HashDetectsDifference` · **기존 결정론 테스트 전량 무수정 통과**

**T6-12** `Npc.Host` `--link tcp` 배선 · `M` · 선행 T6-09, T6-11
  파일  `src/Npc.Host/HostOptions.cs` (수정) · `src/Npc.Host/Program.cs` (수정)
  사양  `docs/20` §10.3 · §12
  내용  `LinkKind.Tcp` 추가와 `--gs-host`·`--gs-port`·`--zone` 옵션. `Tcp`는 `SimDriver`를 만들지 않는다(`Replay`와 같은 경로 — 세계를 미는 것은 게임서버다). `--link record`가 TCP 링크도 감쌀 수 있게 한다.
  완료  `Host_TcpLinkDoesNotCreateSimDriver` · `HostOptions_ParsesTcpOptions` · `--link tcp`로 기동 후 게임서버가 없으면 `Connecting` 상태로 재시도하며 크래시하지 않는다

**T6-13** 킬스위치 전달 경로 · `M` · 선행 T6-12
  파일  `src/Npc.Host/KillSwitchSchedule.cs` (신규) · `src/Npc.Runtime/NpcServerLoop.cs` (수정 — **훅 한 줄만**) · `src/Npc.Host/Program.cs` (수정)
  사양  `docs/20` §11.4
  내용  틱 기반 킬스위치 스케줄(시나리오 jsonl의 `KillSwitch` 줄만 처리)과 `--dev-control` 일 때만 열리는 `POST /control/killswitch?target=...`. **이벤트 주입은 하지 않는다** — 그것은 게임서버의 일이다.
  완료  `KillSwitchSchedule_FiresAtTick`(±0틱) · `KillSwitchSchedule_IgnoresNonSwitchLines` · `--dev-control` 없이 기동하면 `/control/*`이 404 · **`NpcServerLoop`의 diff가 null 체크 한 줄을 넘지 않는다**

> **주의.** T6-13은 `Npc.Runtime`을 건드리는 유일한 태스크다. 훅 한 줄(널 체크 + `Advance(tick)`) 이상으로 커지면 멈추고 보고한다. `docs/20` §1의 합격 기준과 충돌하는 자리이므로 리뷰에서 제일 먼저 볼 곳이다.

### D. 테스트 게임서버

**T6-14** 프로젝트 골격과 옵션 · `M` · 선행 T6-01
  파일  `testbed/Npc.TestGameServer/Npc.TestGameServer.csproj` (신규) · `Program.cs` (신규) · `GameServerOptions.cs` (신규) · `NpcServer.sln` (수정) · `tests/Npc.Tests/TestBed/GameServerOptionsTests.cs` (신규)
  사양  `docs/20` §7.1 · §7.5
  내용  Exe 프로젝트와 CLI 파싱. 파싱 방식은 `Npc.Host/HostOptions.cs`를 본뜬다(경로 해석 포함).
  완료  `GameServerOptions_ParsesAll`(§7.5의 전 옵션) · `GameServerOptions_RejectsUnknownArg` · `--help`가 사용법을 찍고 0으로 끝난다

**T6-15** `GameWorld` — Sim 조립과 틱 순서 · `M` · 선행 T6-14, T6-11
  파일  `testbed/Npc.TestGameServer/GameWorld.cs` (신규) · `testbed/Npc.TestGameServer/Link/CommandInbox.cs` (신규) · `tests/Npc.Tests/TestBed/GameWorldTests.cs` (신규)
  사양  `docs/20` §7.2 · §10.2
  내용  `SimWorld` + `MovementSim`·`InteractionSim`·`NeedsSim`·`TransformEmitter`·`FaultInjector`·`ScenarioRunner` 조립과 10Hz 틱. NPC 배치는 `NpcRoster`로 뽑는다. **`PlayerBots`는 쓰지 않는다**(§7.3).
  완료  `GameWorld_TickOrderMatchesSimDriver`(§7.2 순서대로 호출된다 — 호출 기록으로 확인) · `GameWorld_EmitsTickSyncEveryTick` · `GameWorld_SpawnsRosterAtStart`

**T6-16** 링크 리스너와 핸드셰이크 · `M` · 선행 T6-15, T6-06
  파일  `testbed/Npc.TestGameServer/Link/LinkListener.cs` (신규) · `Link/LinkSession.cs` (신규) · `tests/Npc.Tests/TestBed/LinkSessionTests.cs` (신규)
  사양  `docs/20` §5.5 · §7.1
  내용  :7010 accept(동시 세션 1개, 두 번째는 `Bye` 후 거절). `Hello` 송신 → `HelloAck` 검증 → 로스터 `NpcSpawned` 배치(프레임당 ≤256) → 존 상태 초기화.
  완료  `LinkSession_HandshakeCompletes`(포트 0) · `LinkSession_RejectsSecondSession` · `LinkSession_SendsSpawnForEveryNpc`(로스터 수만큼)

**T6-17** 명령 수신 · `M` · 선행 T6-16
  파일  `testbed/Npc.TestGameServer/Link/LinkSession.cs` (수정)
  사양  `docs/20` §7.2
  내용  `CommandBatch` 프레임 → `CommandInbox`(SPSC 링) → 틱 1단계에서 `SimWorld.ApplyCommand`. 링이 차면 버리고 센다.
  완료  `LinkSession_AppliesCommandsOnNextTick` · `LinkSession_CountsInboxDrops` · 명령 순서가 프레임 안 순서와 같다

**T6-18** 이벤트 송신 · `M` · 선행 T6-17
  파일  `testbed/Npc.TestGameServer/Link/LinkSession.cs` (수정)
  사양  `docs/20` §5.3 · §7.2
  내용  틱 9단계에서 `SimWorld.Events` 채널을 비워 **한 개의 `EventBatch` 프레임**으로 내보낸다. 프레임당 상한 1,024건, 넘으면 다음 틱으로 넘기고 센다. 하트비트 1초.
  완료  `LinkSession_FlushesEventsOncePerTick` · `LinkSession_SplitsOversizeBatch` · `LinkSession_SequenceIsContiguous`(수신 측 갭 0)

**T6-19** `PlayerRegistry` — 이동과 근접 · `M` · 선행 T6-15
  파일  `testbed/Npc.TestGameServer/World/PlayerRegistry.cs` (신규) · `tests/Npc.Tests/TestBed/PlayerRegistryTests.cs` (신규)
  사양  `docs/20` §7.3
  내용  플레이어 등록·입력 적용·존 판정, 5틱마다 근접 판정(200m/220m 히스테리시스) → `PlayerProximity`. `ObservedByPlayer`를 갱신하는 **유일한** 주체다.
  완료  `Player_MoveSpeedScalesWithTimeScale` · `Player_ProximityEntersAndLeavesOnce`(경계에서 왕복해도 이벤트가 반복되지 않는다) · `Player_OnlyScansOwnAndAdjacentZones`

**T6-20** `PlayerRegistry` — 상호작용·공격·봇 · `M` · 선행 T6-19
  파일  `testbed/Npc.TestGameServer/World/PlayerRegistry.cs` (수정)
  사양  `docs/20` §7.3
  내용  `Interact`(≤30m) → `PlayerInteracted`, `Attack`(≤30m) → `CombatStarted` + `DamageTaken` + HP 차감. `--bots N`이면 결정론 랜덤 워크 플레이어를 같은 경로로 돌린다.
  완료  `Player_InteractRequiresRange`(31m에서는 이벤트가 없다) · `Player_AttackReducesHp` · `Bots_AreDeterministic`(같은 시드 → 같은 위치열)

**T6-21** `MirrorLog` · `S` · 선행 T6-17
  파일  `testbed/Npc.TestGameServer/World/MirrorLog.cs` (신규)
  사양  `docs/20` §7.4
  내용  명령·이벤트 각 1,024칸 링. 세션별 커서로 읽는다.
  완료  `MirrorLog_KeepsLatest1024` · `MirrorLog_CursorAdvancesPerSession` · 할당 0(사전 할당 배열)

### E. 클라이언트 프로토콜과 세션

**T6-22** `Npc.TestBed.Protocol` · `M` · 선행 T6-04
  파일  `testbed/Npc.TestBed.Protocol/*.cs` (신규) · `.csproj` (신규) · `NpcServer.sln` (수정) · `tests/Npc.Tests/TestBed/ClientProtocolTests.cs` (신규)
  사양  `docs/20` §8.1 · §8.2 · §8.3
  내용  §8의 메시지 전체와 `EntityState`. 프레임 코덱은 `Npc.Wire`의 것을 재사용한다. 서버 Kind 1~63, 클라 Kind 64~127.
  완료  `ClientProtocol_SnapshotRoundTrips`(256 엔티티) · `ClientProtocol_AllMessagesRoundTrip` · `Wire_NoStringOrDateTimeFields`가 이 어셈블리에서도 통과 · `sizeof(EntityState) == 32`

**T6-23** 클라이언트 리스너와 세션 · `M` · 선행 T6-22, T6-20
  파일  `testbed/Npc.TestGameServer/Client/ClientListener.cs` (신규) · `Client/ClientSession.cs` (신규)
  사양  `docs/20` §8.1 · §8.2
  내용  :7020 accept(최대 `--max-clients`), `CliHello` → `SrvHello`(월드 경계 포함) → `PlayerRegistry`에 플레이어 등록. 세션 종료 시 플레이어 제거. 수신: `Input`·`Interact`·`Attack`·`Select`·`Ping`.
  완료  `ClientSession_HelloAssignsPlayerId` · `ClientSession_DisconnectRemovesPlayer` · `ClientSession_RejectsOverMaxClients` · 한 세션이 죽어도 다른 세션과 링크는 산다

**T6-24** 스냅샷 빌더 · `M` · 선행 T6-23
  파일  `testbed/Npc.TestGameServer/Client/SnapshotBuilder.cs` (신규)
  사양  `docs/20` §8.1
  내용  2틱마다 AOI(반경 1,200m·최대 256) 스냅샷. NPC 위치는 `TransformEmitter.Interpolate`. `TargetPoi`·`StateFlags`를 채운다. 다른 플레이어도 포함한다.
  완료  `ClientProtocol_AoiCapsAt256`(NPC 1,000 중 가까운 순 256) · `Snapshot_IncludesOtherPlayers` · `Snapshot_MovingNpcHasTargetPoi` · 세션당 ≤ 41KB/s

**T6-25** 제어 처리 · `M` · 선행 T6-24
  파일  `testbed/Npc.TestGameServer/Client/ControlHandler.cs` (신규)
  사양  `docs/20` §8.3
  내용  `SetZoneState`·`SetWeather`·`SkipTime`·`SetFaultRate`·`Despawn`. `SkipTime`의 부작용을 코드 주석에 남긴다.
  완료  `Control_SetZoneStateEmitsEvent` · `Control_SkipTimeAdvancesTick`(정확한 틱 수) · `Control_DespawnEmitsNpcDespawned` · 모르는 `ControlKind`는 무시하고 센다

### F. 테스트 클라이언트

**T6-26** WinForms 골격과 접속 · `M` · 선행 T6-22
  파일  `testbed/Npc.TestClient/Npc.TestClient.csproj` (신규) · `Program.cs` · `MainForm.cs` · `Net/GameConnection.cs` (신규) · `NpcServer.sln` (수정)
  사양  `docs/20` §4 · §9.2
  내용  `net10.0-windows` + `UseWindowsForms`. 접속·수신 루프(백그라운드) → UI 스레드로 마샬링. 폼 레이아웃(맵 + 우측 탭 + 하단 상태바)까지.
  완료  게임서버에 붙어 `SrvHello`를 받고 상태바에 tick이 올라간다 · 게임서버가 없으면 재접속을 시도하며 크래시하지 않는다 · **`Npc.Tests`가 이 프로젝트를 참조하지 않는다**

**T6-27** 맵 렌더러 — 존·POI·카메라 · `M` · 선행 T6-26
  파일  `testbed/Npc.TestClient/Render/MapRenderer.cs` (신규) · `Render/Camera.cs` (신규)
  사양  `docs/20` §9.3 · §3.2
  내용  `Npc.MasterData`를 읽어 존 배경원·POI를 그린다. 좌표는 `pois.json`의 `pos` **그대로**다(전역 좌표다 — 레이아웃 파일을 만들지 않는다). 휠 줌·드래그 팬·`Home` 월드 맞춤.
  완료  실행하면 12개 존과 243개 POI가 보인다 · `Home`이 월드 전체를 화면에 맞춘다 · **`masterdata/`에 diff 없음**

**T6-28** 맵 렌더러 — 엔티티와 보간 · `M` · 선행 T6-27
  파일  `testbed/Npc.TestClient/Render/MapRenderer.cs` (수정) · `Render/EntityInterpolator.cs` (신규)
  사양  `docs/20` §9.3
  내용  아키타입 황금각 팔레트, `VisualState`별 테두리, 이동선, 플레이어 삼각형. 마지막 두 스냅샷 사이를 `now − 200ms`로 선형 보간하고, 스냅샷이 늦으면 **외삽하지 않고 멈춘다**. 색상별 `FillRectangles` 배치 호출.
  완료  NPC가 끊기지 않고 움직인다 · 256 엔티티에서 60fps 유지(프레임 시간 표시로 확인) · 스냅샷을 3초 끊어도 튀지 않는다

**T6-29** 입력 · `M` · 선행 T6-28
  파일  `testbed/Npc.TestClient/Input/InputController.cs` (신규)
  사양  `docs/20` §9.4
  내용  WASD 10Hz 전송, Shift 달리기, 좌클릭 선택, `E` 대화, `R` 공격, `Tab` 다음 NPC, `Space` 추적 복귀.
  완료  플레이어가 움직이고 화면이 따라간다 · 클릭으로 NPC가 선택되고 하이라이트된다 · 30m 밖에서 `E`를 누르면 서버가 무시한다(로그로 확인)

**T6-30** 인스펙터 패널 · `M` · 선행 T6-29
  파일  `testbed/Npc.TestClient/Panels/InspectorPanel.cs` (신규) · `Net/NpcServerHttp.cs` (신규)
  사양  `docs/20` §9.5
  내용  선택 NPC의 `GET /npc/{id}`를 1초마다 폴링해 아키타입·존·POI·LOD·플랜(출처·goal·스텝 목록·현재 스텝)·플래그·인벤토리·최근 사건을 표시. 실패는 조용히 넘긴다.
  완료  대장장이 한 마리를 선택하면 플랜의 스텝이 진행되는 것이 보인다 · NPC 서버를 꺼도 클라이언트가 계속 돈다 · 응답 파싱에 소스 생성 `JsonSerializerContext`를 쓴다

**T6-31** 로그 패널 · `M` · 선행 T6-30
  파일  `testbed/Npc.TestClient/Panels/LogPanel.cs` (신규) · `Format/IdNames.cs` (신규)
  사양  `docs/20` §9.6
  내용  `CommandLog`·`EventLog`를 시간 역순 500줄. `Npc.MasterData`로 id를 사람 말로 푼다(POI·아키타입·아이템·액션·실패 사유). 필터 3종.
  완료  `MoveTo poi=137` 대신 `MoveTo smithy_001_05`로 보인다 · `ActionFailed(Unreachable)`이 사유 이름으로 보인다 · 선택 NPC 필터가 동작한다

**T6-32** 제어 패널과 상태바 · `M` · 선행 T6-31
  파일  `testbed/Npc.TestClient/Panels/ControlPanel.cs` (신규) · `Panels/StatusBar.cs` (신규)
  사양  `docs/20` §8.3 · §9.2 · §11.4
  내용  존 상태·날씨 드롭다운, 시간 건너뛰기, 고장 주입 슬라이더, NPC despawn 버튼. 킬스위치 버튼은 NPC 서버의 `POST /control/killswitch`를 부른다(`--dev-control` 필요). 상태바에 게임 시각·존 상태·링크 지연(gs tick − npc tick)·드롭 수.
  완료  버튼으로 존을 War로 바꾸면 화면의 존 테두리가 빨개지고 NPC가 다른 행동을 시작한다 · 킬스위치 버튼 후 `/metrics`의 LLM 호출이 0이 된다 · `--dev-control` 없이 누르면 "미연결"로만 표시된다

### G. 시나리오와 마무리

**T6-33** 데모 시나리오 3종 · `S` · 선행 T6-18
  파일  `testbed/scenarios/demo_day.jsonl` · `demo_siege.jsonl` · `demo_blackout.jsonl` (신규)
  사양  `docs/20` §11
  내용  `--time-scale 60` 기준 틱 좌표로 작성. **기존 `scenarios/`를 고치지 않는다.** 각 파일 첫 줄 `_comment`에 무엇을 보는 시나리오인지와 실시간 소요를 적는다.
  완료  `ScenarioRunner.Parse`가 세 파일을 예외 없이 읽는다(테스트) · 존 id가 전부 `zones.json`에 있다 · `demo_blackout`의 `KillSwitch` 줄을 게임서버가 무시한다

**T6-34** 실행 스크립트 · `S` · 선행 T6-32, T6-33
  파일  `testbed/run_demo.ps1` (신규)
  사양  `docs/20` §12
  내용  게임서버 → NPC 서버 → 클라이언트를 순서대로 띄우고 Ctrl-C에 함께 내린다. `-Scenario day|siege|blackout`, `-Npcs`, `-TimeScale`. **UTF-8 BOM으로 저장한다**(Windows PowerShell 5.1이 BOM 없는 한국어 스크립트를 깨뜨린다).
  완료  `./testbed/run_demo.ps1 -Scenario siege` 한 줄로 세 프로세스가 뜨고 클라이언트에 NPC가 보인다 · Ctrl-C로 셋 다 종료된다 · 스크립트 첫 3바이트가 `EF BB BF`

**T6-35** 종단 테스트 · `M` · 선행 T6-25, T6-13
  파일  `tests/Npc.Tests/TestBed/EndToEndTests.cs` (신규)
  사양  `docs/20` §13 · §14.1
  내용  게임서버와 NPC 서버를 **인프로세스로 포트 0에 띄워** 소켓으로 붙이고 300틱을 돌린다. 가짜 클라이언트 소켓도 붙여 스냅샷을 받는다.
  완료  `TestBed_EndToEnd_NpcArrives`(`NpcArrived ≥ 1` · 시퀀스 갭 0 · 드롭 0) · `TestBed_ProximityChangesLod` · `TestBed_InteractRaisesInterrupt` · `TcpLink_CommandLossSynthesizesTimeout`(`--drop-rate 0.3`에서 합성 타임아웃 > 0, 멈춘 NPC 0) 통과

**T6-36** 문서와 원장 · `S` · 선행 T6-35
  파일  `testbed/README.md` (신규) · `TASKS.md` (수정) · `README.md` (수정)
  사양  `docs/20` §12 · §17
  내용  실행 방법·포트·화면 설명·알려진 한계(§14.3 결정론 · §11.4 T5-21)를 `testbed/README.md`에 정리하고, `TASKS.md` 원장의 P6 행과 저장소 `README.md`의 실행 예시를 갱신한다.
  완료  `testbed/README.md`만 보고 처음 사람이 데모를 띄울 수 있다 · `TASKS.md` §3에 P6 행이 있다 · **테스트 없음**(문서 태스크)

---

## 2. 게이트 — P6 수용 기준

전부 통과해야 이 Phase가 끝난다. `docs/20` §1의 합격 기준을 항목으로 편 것이다.

| # | 항목 | 판정 |
|---|---|---|
| G6-1 | **NPC 서버 본체 무변경** | `git diff main -- src/Npc.Runtime src/Npc.Planning src/Npc.Core src/Npc.Contracts`가 T6-13의 훅 한 줄 외에 비어 있다 |
| G6-2 | **마스터데이터 무변경** | `git diff main -- masterdata/`가 비어 있다 · `MasterDataSet.ContentHash`가 그대로다 |
| G6-3 | 소켓 종단 동작 | `TestBed_EndToEnd_NpcArrives` 통과 |
| G6-4 | 링크 계약 유지 | `Contracts`·`Wire` 카테고리 전량 통과 · `Wire_NoStringOrDateTimeFields`가 두 새 어셈블리에서 통과 |
| G6-5 | 틱 예산 유지 | `--link tcp --npcs 500`으로 10분 → p99 ≤ 20ms · Gen0 증가 0 |
| G6-6 | 할당 0 | `TcpLink_FlushDoesNotAllocate` 통과 |
| G6-7 | 명령 유실 내성 | `--drop-rate 0.3`에서 300틱 후 멈춘 NPC 0 |
| G6-8 | 재접속 | NPC 서버를 죽였다 살려도 게임서버를 재기동하지 않고 다시 붙어 NPC가 움직인다 |
| G6-9 | 데모 3종 | `run_demo.ps1`로 세 시나리오가 각각 끝까지 돈다 · 크래시 0 |
| G6-10 | 눈으로 보인다 | 사람이 클라이언트에서 NPC 하나를 골라 **왜 그 행동을 하는지** 인스펙터로 설명할 수 있다 |

**G6-10이 이 Phase의 목적이다.** 나머지 아홉은 그것이 거짓이 아님을 보증하는 장치다.

---

## 3. 막히면

| 상황 | 어떻게 |
|---|---|
| 사양이 틀렸거나 부족하다 | 임의로 코드에 맞추지 말고 **멈추고 보고한다.** `docs/20`을 먼저 고치고 같은 커밋에 담는다 |
| `Npc.Runtime`을 고쳐야만 될 것 같다 | **거의 항상 설계가 틀린 것이다.** 멈추고 보고한다. T6-13 외에 그런 태스크는 없다 |
| MemoryPack이 말썽이다 | `docs/20` §15의 대체안(직접 `MemoryMarshal` 쓰기)을 쓴다. `Npc.Wire` 안쪽만 바뀐다 |
| 성능이 예산을 넘는다 | 어디서 넘는지 먼저 잰다. 게임서버 쪽이면 AOI·스냅샷 주기를 줄인다. NPC 서버 쪽이면 §6.1 스레드 모델이 지켜지고 있는지부터 본다 |

---

## 4. 진행 원장

### A. 와이어 프로토콜
- [x] T6-01 `Npc.Wire` 프로젝트 생성
- [x] T6-02 와이어 명령·이벤트 DTO와 매핑
- [x] T6-03 제어 메시지와 `WireHash`
- [x] T6-04 프레임 코덱
- [x] T6-05 `PriorityCommandRing` 추출

### B. TCP 링크
- [x] T6-06 연결·핸드셰이크
- [x] T6-07 송신 경로
- [x] T6-08 수신 경로
- [x] T6-09 재접속·하트비트·상태 전이
- [ ] T6-10 링크 사양 문서 갱신

### C. 로스터와 호스트 배선
- [ ] T6-11 `NpcRoster` 추출
- [ ] T6-12 `--link tcp` 배선
- [ ] T6-13 킬스위치 전달 경로

### D. 테스트 게임서버
- [ ] T6-14 프로젝트 골격과 옵션
- [ ] T6-15 `GameWorld` — Sim 조립과 틱 순서
- [ ] T6-16 링크 리스너와 핸드셰이크
- [ ] T6-17 명령 수신
- [ ] T6-18 이벤트 송신
- [ ] T6-19 `PlayerRegistry` — 이동과 근접
- [ ] T6-20 `PlayerRegistry` — 상호작용·공격·봇
- [ ] T6-21 `MirrorLog`

### E. 클라이언트 프로토콜과 세션
- [ ] T6-22 `Npc.TestBed.Protocol`
- [ ] T6-23 클라이언트 리스너와 세션
- [ ] T6-24 스냅샷 빌더
- [ ] T6-25 제어 처리

### F. 테스트 클라이언트
- [ ] T6-26 WinForms 골격과 접속
- [ ] T6-27 맵 렌더러 — 존·POI·카메라
- [ ] T6-28 맵 렌더러 — 엔티티와 보간
- [ ] T6-29 입력
- [ ] T6-30 인스펙터 패널
- [ ] T6-31 로그 패널
- [ ] T6-32 제어 패널과 상태바

### G. 시나리오와 마무리
- [ ] T6-33 데모 시나리오 3종
- [ ] T6-34 실행 스크립트
- [ ] T6-35 종단 테스트
- [ ] T6-36 문서와 원장

### 게이트
- [ ] G6-1 NPC 서버 본체 무변경
- [ ] G6-2 마스터데이터 무변경
- [ ] G6-3 소켓 종단 동작
- [ ] G6-4 링크 계약 유지
- [ ] G6-5 틱 예산 유지
- [ ] G6-6 할당 0
- [ ] G6-7 명령 유실 내성
- [ ] G6-8 재접속
- [ ] G6-9 데모 3종
- [ ] G6-10 눈으로 보인다
