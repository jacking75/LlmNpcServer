# 20. 테스트 베드 작업 지시서 (P6 · T6-01 ~ T6-38)

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
git diff --stat HEAD -- src/Npc.Runtime src/Npc.Planning src/Npc.Core src/Npc.Contracts
```

**비어 있어야 한다.** 이 네 프로젝트에 diff가 생기면 그 순간 멈추고 보고한다. `docs/20` §1의 합격 기준 1이다. (`Npc.Gateway`·`Npc.Host`·`Npc.MasterData`는 바뀌어도 된다 — 바뀌라고 만든 자리다.)

> **`git diff main` 이라고 적혀 있었다 (2026-08-01 정정).** P6 작업이 `main` 위에서 진행되므로
> 그것은 `git diff HEAD` 와 같은 뜻이었다 — **커밋 직전 확인으로는 맞지만 누적 게이트로는 공회전이다.**
> 커밋하는 순간 `main` 이 따라 움직여서 diff 가 언제나 비기 때문이다.
> **G6-1 을 판정하는 명령은 따로 있다** — 아래 §2 를 본다.

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
  파일  `src/Npc.Host/KillSwitchSchedule.cs` (신규) · `src/Npc.Host/Program.cs` (수정) · `src/Npc.Host/HostOptions.cs` (수정 — `--dev-control`)
        ~~`src/Npc.Runtime/NpcServerLoop.cs`~~ — **건드리지 않았다.** `ITickObserver` 로 대신했다 (아래 주의)
  사양  `docs/20` §11.4
  내용  틱 기반 킬스위치 스케줄(시나리오 jsonl의 `KillSwitch` 줄만 처리)과 `--dev-control` 일 때만 열리는 `POST /control/killswitch?target=...`. **이벤트 주입은 하지 않는다** — 그것은 게임서버의 일이다.
  완료  `KillSwitchSchedule_FiresAtTick`(±0틱) · `KillSwitchSchedule_IgnoresNonSwitchLines` · `--dev-control` 없이 기동하면 `/control/*`이 404 · **`NpcServerLoop`의 diff가 null 체크 한 줄을 넘지 않는다**

> **주의(해소됨).** T6-13은 `Npc.Runtime`을 건드리는 유일한 태스크로 적혀 있었다. 실제로는 **건드리지 않았다** — `ITickObserver` 라는 이음매가 이미 있어서, `KillSwitchSchedule` 이 그 인터페이스를 구현하고 원래 관측자(`NpcMeter`)를 감싸는 것으로 끝났다. **P6 전 구간에서 본체 diff 가 0줄이다.**
>
> `--dev-control` 은 §10.3 에 있으므로 원래 T6-12 의 몫이었는데 그때 빠뜨렸다. 여기서 `HostOptions` 에 같이 넣었다.

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

### E-2. 게임 루프 — 조립과 방송 (2026-07-28 추가)

> **왜 뒤늦게 생겼는가.** `docs/20` §7.2 의 10단계를 도는 루프가 **어느 태스크의 파일 목록에도 없었다.**
> T6-14 의 완료 조건이 "옵션 파싱 + `--help`" 까지였고 이후 태스크들은 부품만 지정했다 —
> 각 태스크는 자기 파일 목록을 지켰으므로 규약 위반은 아니다. 그 결과
> `MirrorLog`(T6-21)·`SnapshotBuilder`(T6-24)·`ControlHandler`(T6-25)·`ClientListener.TickAsync`(T6-23)·
> `LinkSession.FlushEventsAsync`(T6-18)가 **전부 만들어졌지만 아무도 부르지 않는다.**
> `TASKS.md` 의 "결정 대기" 항목이 권한 (1)안이다. **T6-34·T6-35 의 선행이다.**
>
> **하나가 아니라 둘로 쪼갰다.** 원장의 예상은 `M` 한 개였는데, 실제로는 `ClientSession` 에
> **스냅샷·로그를 보내는 메서드 자체가 없다** — 지금 보내는 것은 `SrvHello` 와 `Pong` 뿐이다.
> 링크 쪽(1~9단계)과 클라이언트 쪽(10단계)은 선행도 테스트도 갈리므로 태스크를 나눈다.

**T6-37** `GameServer` — 조립과 10Hz 루프(1~9단계) · `M` · 선행 T6-18, T6-21, T6-25
  파일  `testbed/Npc.TestGameServer/GameServer.cs` (신규) · `Program.cs` (수정) · `GameWorld.cs` (수정 — 명령 관측자) · `Link/LinkSession.cs` (수정 — 이벤트 관측자·`Now`) · `tests/Npc.Tests/TestBed/GameServerTests.cs` (신규)
  사양  `docs/20` §7.2 · §7.5 · §12
  내용  마스터데이터·로스터·월드·등록기·수신기를 조립하고 §7.2 의 1~9단계를 100ms 페이싱으로 돈다. `MirrorLog` 를 명령·이벤트 양쪽에 배선한다. 링크 세션이 없으면 이벤트를 배수해 버린다 — 안 그러면 채널이 무한히 자란다. 종료 시 §7.5 의 한 줄 요약.
  완료  `GameServer_TickOrderMatchesSpec` · `GameServer_MirrorsCommandsAndEvents` · `GameServer_RunsWithoutNpcServer`(링크 없이 100틱, 채널이 자라지 않는다) · `GameServer_ResyncsWhenLinkAttaches` · `GameServer_SkipTimeAdvancesTicks` 통과

**T6-38** 클라이언트 방송 — 10단계 · `M` · 선행 T6-37, T6-24
  파일  `testbed/Npc.TestGameServer/Client/ClientSession.cs` (수정) · `GameServer.cs` (수정) · `tests/Npc.Tests/TestBed/ClientBroadcastTests.cs` (신규)
  사양  `docs/20` §8.1 · §7.2 · §7.4
  내용  `Snapshot`(2틱) · `CommandLog`·`EventLog`(2틱, §7.4 의 세션별 필터) · `ZoneStates`(변화 시 + 5초) · `LinkStatus`(1초) 송신과 `Control` → `ControlHandler` 라우팅. 소켓에 쓰는 것은 틱 스레드 하나뿐이라는 계약을 지킨다.
  완료  `Broadcast_SnapshotEveryTwoTicks` · `Broadcast_SelectedNpcLogsAreComplete`(선택 NPC 의 줄은 전부, 그 외는 배치당 ≤32) · `Broadcast_ZoneStatesOnChange` · `Broadcast_RoutesControlToHandler` · `Broadcast_LinkStatusEverySecond` 통과

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
| G6-1 | **NPC 서버 본체 무변경** | 아래 명령이 **아무것도 내지 않는다.** T6-13의 훅 한 줄도 결국 필요 없었다 |
| G6-2 | **마스터데이터 무변경** | `git diff --stat c36e183..HEAD -- masterdata/`가 비어 있다 · `MasterDataSet.ContentHash`가 그대로다 |
| G6-3 | 소켓 종단 동작 | `TestBed_EndToEnd_NpcArrives` 통과 |
| G6-4 | 링크 계약 유지 | `Contracts`·`Wire` 카테고리 전량 통과 · `Wire_NoStringOrDateTimeFields`가 두 새 어셈블리에서 통과 |
| G6-5 | 틱 예산 유지 | `--link tcp --npcs 500`으로 10분 → p99 ≤ 20ms · Gen0 증가 0 |
| G6-6 | 할당 0 | `TcpLink_FlushDoesNotAllocate` 통과 |
| G6-7 | 명령 유실 내성 | `--drop-rate 0.3`에서 300틱 후 멈춘 NPC 0 |
| G6-8 | 재접속 | NPC 서버를 죽였다 살려도 게임서버를 재기동하지 않고 다시 붙어 NPC가 움직인다 |
| G6-9 | 데모 3종 | `run_demo.ps1`로 세 시나리오가 각각 끝까지 돈다 · 크래시 0 |
| G6-10 | 눈으로 보인다 | 사람이 클라이언트에서 NPC 하나를 골라 **왜 그 행동을 하는지** 인스펙터로 설명할 수 있다 |

**G6-10이 이 Phase의 목적이다.** 나머지 아홉은 그것이 거짓이 아님을 보증하는 장치다.

### G6-1을 판정하는 명령

```powershell
# c36e183 = T6-01 직전 커밋 (P6 시작점)
git log --oneline c36e183..HEAD --grep "^T6-" -- `
    src/Npc.Runtime src/Npc.Planning src/Npc.Core src/Npc.Contracts
```

**아무것도 나오지 않아야 한다.** 대조로 경로를 `testbed` 로 바꾸면 T6-* 커밋들이 나온다 —
그래야 이 명령이 실제로 무언가를 보고 있다는 것이 확인된다.

**`git diff` 가 아니라 `git log` 인 이유.** 이 기간에 본체를 바꾼 커밋이 실제로 둘 있다 —
`3fca4b3`(결정 16 · `ReplanQueue` 데이터 레이스)와 `fb45ac3`(결정 13·15-A · `Manifest`)다.
**둘 다 P6 작업이 아니다.** G6-1이 주장하는 것은 "이 기간에 본체가 안 바뀌었다"가 아니라
**"P6 태스크가 본체를 안 바꿨다"** 이고, 그것을 재려면 커밋 단위로 봐야 한다.
`git diff c36e183..HEAD` 로 재면 남의 작업을 P6 탓으로 돌리게 된다.

**2026-08-01 실측: T6-* 커밋 40개 중 이 네 프로젝트를 건드린 것이 0개다.**

---

## 3. 막히면

| 상황 | 어떻게 |
|---|---|
| 사양이 틀렸거나 부족하다 | 임의로 코드에 맞추지 말고 **멈추고 보고한다.** `docs/20`을 먼저 고치고 같은 커밋에 담는다 |
| `Npc.Runtime`을 고쳐야만 될 것 같다 | **거의 항상 설계가 틀린 것이다.** 멈추고 보고한다. 유일한 예외로 적혀 있던 T6-13조차 `ITickObserver` 로 끝났다 — 이음매를 먼저 찾는다 |
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
- [x] T6-10 링크 사양 문서 갱신

### C. 로스터와 호스트 배선
- [x] T6-11 `NpcRoster` 추출
- [x] T6-12 `--link tcp` 배선 — **2026-07-28 보강**: 링크를 만들기만 하고 아무도 안 돌리고 있었다 (아래)
- [x] T6-13 킬스위치 전달 경로

> **T6-12 의 빠진 한 줄 — 2026-07-28 발견·수정.**
> `--link tcp` 는 `TcpGameServerLink` 를 **만들기만 하고 `RunAsync` 를 부르지 않았다.**
> `NpcHost.RunAsync` 가 도는 것은 펌프와 틱 루프 둘인데, `Tcp` 는 `SimDriver` 를 안 만드니
> 펌프가 즉시 반환하고(그게 §10.3 이 시킨 것이다) **접속 루프를 돌릴 사람이 아무도 없었다.**
> 증상: NPC 서버가 조용히 뜨고 `ticksProcessed` 가 영원히 0, 게임서버는 `link down`.
> 완료 조건에 "게임서버가 없으면 `Connecting` 상태로 재시도" 는 있었지만 **붙는 쪽은 없었다** —
> 게임서버가 그때는 돌지 않았으므로(T6-37 이전) 그 차이가 드러날 수 없었다.
>
> **고친 자리:** `NpcHost` 가 소켓 링크를 구체 타입으로 들고 `RunAsync` 를 같이 돌린다.
> `WhenAll` 에 넣지 않는다 — 그 루프는 취소될 때까지 안 끝나므로 `--days N` 회차의 종료를 막는다.
> `--link record` 가 감싼 경우도 안쪽 소켓을 잡아야 해서 데코레이터가 아니라 구체 타입을 든다.
> **회귀 방지는 T6-35 가 맡는다** — 종단 테스트가 정확히 이 경로를 지난다.

> **T6-12 의 남은 일 — 해소됐다 (2026-07-28).** `--zone` 이 `NpcRoster.Select` 로 넘어간다.
> 로스터 선택을 할당 블록 **위로** 올리고 `npcs` 를 `roster.Count` 에서 받는다 — 그러지 않으면
> `NpcStore`·`CorrelationTable`·`ReplanQueue` 가 `--npcs` 로 잡히고 뒤쪽이 영원히 빈 배열이 된다.
> **모르는 존 id 와 0 마리는 기동 실패다.** 조용히 넘기면 사고가 핸드셰이크 거절로만 나타나
> "새로 만든 게임서버가 이상하다" 로 오해하기 쉽다.
> 테스트: `Host_ZoneFilterAppliesToRoster`(게임서버와 같은 호출의 해시 일치) · `Host_UnknownZoneFailsStartup`.

### D. 테스트 게임서버
- [x] T6-14 프로젝트 골격과 옵션
- [x] T6-15 `GameWorld` — Sim 조립과 틱 순서
- [x] T6-16 링크 리스너와 핸드셰이크
- [x] T6-17 명령 수신
- [x] T6-18 이벤트 송신
- [x] T6-19 `PlayerRegistry` — 이동과 근접
- [x] T6-20 `PlayerRegistry` — 상호작용·공격·봇
- [x] T6-21 `MirrorLog` — 링·커서만. **틱 루프 배선은 T6-23·T6-24 몫**(소비자가 거기 있다)

### E. 클라이언트 프로토콜과 세션
- [x] T6-22 `Npc.TestBed.Protocol`
- [x] T6-23 클라이언트 리스너와 세션
- [x] T6-24 스냅샷 빌더 — `EntityFlags.InCombat` 은 **미채움**(전투 상태를 들고 있는 곳이 없다. 아래)
- [x] T6-25 제어 처리

> **T6-25 의 판단 — `SetFaultRate` 는 `FaultInjector` 를 떼지 않고 그 앞에 선다 (2026-07-28).**
> `Npc.Sim.FaultInjector` 는 주입률을 **생성 시점에 고정**한다(`readonly` 임계값). 런타임 변경을 넣으려면
> `Npc.Sim` 을 고쳐야 하는데, `docs/20` §4 의 수정 대상 목록에 그 프로젝트가 없다.
> 대신 `ControlHandler` 가 `SimWorld.Handler` 앞에 관문을 세운다 — **떼지 않는 이유**는
> 떼면 `--fail-rate` 로 시작한 회차가 `ControlHandler` 를 만드는 순간 조용히 무고장이 되기 때문이다.
> **대가:** 시작 옵션과 런타임 값이 겹쳐 걸린다(`--fail-rate 0.1` + 슬라이더 0.3 → 실효 약 0.37).
> 데모 기본이 0 이라 실제로는 슬라이더 값이 전부다. 정확한 치환이 필요해지면 그때 `Npc.Sim` 을 고친다.

> **T6-24 의 남은 일 — `EntityFlags.InCombat`(bit2)이 늘 0 이다 (2026-07-28).**
> `docs/20` §8.1 이 그 비트를 정의했지만 **전투 상태를 들고 있는 곳이 없다.**
> `PlayerRegistry.TryAttack`(T6-20)도 `InteractionSim.ResolveCombat` 도 그 자리에서 끝나고
> 아무 표시를 남기지 않는다. HP 같은 것으로 흉내내면 **화면이 거짓말을 한다** — 그래서 비웠다.
> 채우려면 먼저 전투 상태를 어디에 둘지 정해야 한다(`PlayerRegistry` 에 마지막 피격 틱 배열이
> 가장 싸다). **화면에서 붉은 테두리가 안 보이는 것 외에 다른 증상은 없다.**

### E-2. 게임 루프 (2026-07-28 추가)
- [x] T6-37 `GameServer` — 조립과 10Hz 루프(1~9단계)
- [x] T6-38 클라이언트 방송 — 10단계

> **T6-38 에서 드러난 것 둘 (2026-07-28).**
> 1. **`LinkSession.EventsPending` 이 부풀어 있었다.** 링크가 없는 구간에 `GameServer` 가 배수한
>    분이 세션의 `_drained` 에 안 잡혀서, 붙은 뒤에도 `EventsDeferred` 가 매 틱 헛되이 늘었다.
>    `ExternallyDrained` 를 세션에 알려 뺀다. **적체가 없는데 적체 압력이 보이는 것이 가장 나쁜 계기다.**
> 2. **`ZoneStates` 는 주기만으로는 늦다.** 방금 붙은 세션이 다음 5초 주기까지 존 색을 모르면
>    사람은 그 5초를 "제어가 안 먹는다" 로 읽는다. `ClientSession.NeedsZoneStates` 로 첫 장을 즉시 보낸다.
>
> **`LinkStatus.NpcServerTick` 은 명령의 `IssuedAt` 에서 딴다.** 하트비트에는 없다 —
> `TcpGameServerLink` 는 런타임의 틱을 모르고(`IGameServerLink` 에 그런 것이 없다), 넣으려면 계약을
> 바꿔야 한다. **NPC 서버가 조용하면 이 값이 늙는다** — 상태바의 지연이 커지는 것으로 보이는데, 그것도 정보다.

### F. 테스트 클라이언트
- [x] T6-26 WinForms 골격과 접속
- [x] T6-27 맵 렌더러 — 존·POI·카메라
- [x] T6-28 맵 렌더러 — 엔티티와 보간
- [x] T6-29 입력
- [x] T6-30 인스펙터 패널
- [x] T6-31 로그 패널
- [x] T6-32 제어 패널과 상태바 — **링크 탭도 여기서 채웠다**(아래)

> **T6-32 에서 같이 한 것 둘 (2026-08-01).**
> 1. **`링크` 탭이 비어 있었다.** `docs/20` §9.5 마지막 줄("링크 패널에는 `GET /metrics` 의 요약을
>    같이 띄운다")이 인스펙터 절에 곁들여 있어서 T6-30 에서 빠졌다. T6-32 의 완료 조건이
>    "킬스위치 버튼 후 `/metrics` 의 LLM 호출이 0 이 된다" 라 **여기서 필요해졌다** —
>    새 파일을 만들지 않고 `Panels/ControlPanel.cs` 에 `LinkPanel` 을 같이 뒀다.
> 2. **상태바의 시간대 이름이 틀려 있었다.** `MainForm` 이 `TimeOfDay` 를 `Dawn/Morning/Day/
>    Evening/Night/LateNight` 로 다시 적어 두고 있었는데, `context_buckets.json` 의 실제 순서는
>    `Dawn/Morning/Noon/Afternoon/Evening/Night` 다. 즉 **오후 3시가 화면에 `Evening` 으로 나왔다.**
>    `StatusBar` 가 `Npc.Core.TimeOfDay` 를 그대로 쓰면서 없어졌다 — 클라이언트는 이미
>    `Npc.MasterData` 를 통해 그 열거형을 보고 있었으므로 다시 적을 이유가 없었다.
>
> **`SetFaultRate` 슬라이더는 시작 옵션과 겹쳐 걸린다** (T6-25 판단 참조). 데모 기본이 0 이라
> 실제로는 슬라이더 값이 전부다.

### G. 시나리오와 마무리
- [x] T6-33 데모 시나리오 3종 — 테스트 파일이 파일 목록에 없었다 (아래)

> **T6-33 의 파일 목록에 테스트가 빠져 있었다 (2026-08-01).**
> 완료 조건은 "`ScenarioRunner.Parse` 가 세 파일을 예외 없이 읽는다(테스트)" 인데
> 파일 목록에는 jsonl 세 개뿐이었다. `tests/Npc.Tests/TestBed/DemoScenarioTests.cs` 를 만들었다.
>
> **`docs/20` §11.1 의 "12:00 Day" 를 "12:00 Noon" 으로 고쳤다.** `context_buckets.json` 의
> `time_of_day.values` 는 `Dawn/Morning/Noon/Afternoon/Evening/Night` 이고 12:00 은 `Noon`
> 구간(11–14)이다. T6-32 에서 상태바가 같은 이름을 틀리게 적고 있던 것과 같은 뿌리다.
- [x] T6-34 실행 스크립트 — 테스트 파일이 파일 목록에 없었다 (T6-33 과 같다)

> **T6-34 의 판단 — `dotnet run` 이 아니라 빌드된 exe 를 띄운다 (2026-08-01).**
> `docs/20` §12 는 `dotnet run --project ...` 로 적혀 있는데, 스크립트는 한 번 빌드한 뒤
> `bin/Release` 의 exe 를 직접 띄운다. `dotnet run` 은 자기 자식 프로세스를 하나 더 끼워서
> **PID 를 붙잡아도 진짜 서버가 안 죽는다** — Ctrl-C 로 셋 다 내리는 것이 완료 조건이라
> 그 한 겹이 그대로 결함이 된다. `-NoBuild` 로 빌드를 건너뛸 수 있다.
>
> **`--tier` 를 파라미터로 넣었다.** §12 의 예시는 `--tier none` 고정인데, §11.4 가
> "`demo_blackout` 을 `--tier t2` 단독으로 돌려도 된다" 고 적어 두었다. 기본은 `none` 이다.
>
> **테스트:** `tests/Npc.Tests/TestBed/RunDemoScriptTests.cs`. BOM 3바이트와,
> `ValidateSet` 의 시나리오 이름이 `testbed/scenarios/demo_*.jsonl` 과 1:1 인지 본다 —
> 파일을 추가했는데 목록에 안 넣으면 **그 시나리오를 띄울 방법이 조용히 사라진다.**
- [x] T6-35 종단 테스트 — 락스텝에 예열이 필요했다 (아래)

> **T6-35 에서 드러난 것 둘 (2026-08-01).**
> 1. **락스텝은 예열 없이 걸면 교착한다.** 테스트가 게임서버 틱을 직접 밀면서 NPC 서버가
>    1틱 이상 뒤처지지 않게 기다리는데, NPC 서버의 시계는 `TickSync` 로만 움직이고
>    그 `TickSync` 는 게임서버가 다음 틱을 돌아야 나간다. 세션이 붙기 전 틱들의 `TickSync` 는
>    링크 없는 구간의 이벤트로 이미 배수돼 버렸으므로, 붙자마자 기다리면
>    **"아직 안 돈 NPC 서버" 를 기다리며 "TickSync 를 낼 게임서버" 를 멈춰 세운다.**
>    `Bed.StartAsync` 가 `TicksCommitted > 0` 이 될 때까지 락스텝 없이 먼저 민다.
> 2. **종단 회차가 옆 테스트의 예산 측정을 흔들었다.** 처음에는 NPC 32 · 프리베이크 스토어
>    로드까지 했더니 `Transition_DoesNotAllocate`(할당 델타)와
>    `Host_LoadsEveryBucketWithinThreeSeconds`(벽시계 3초)가 번갈아 흔들렸다.
>    NPC 를 16 으로 줄이고 **`--planstore` 를 없는 경로로 줘 254개 파일 로드를 뺐다** —
>    이 회차가 보는 것은 소켓 경로이지 플랜 품질이 아니고, 폴백 40개로도 MoveTo → 도착이 돈다.
>    `AllocationCollection`(병렬 끔)에도 같이 넣었다. 이후 전체 3회 연속 통과.
- [x] T6-36 문서와 원장

### 게이트

**6 통과 · 4 미판정 (2026-08-01).** 미판정 넷은 전부 **사람이 눈으로 보는 항목**이다 —
코드 결함으로 미달한 것이 없다. 테스트로 판정되는 여섯은 CI 기본 회차에서 상시 확인된다.

- [x] G6-1 NPC 서버 본체 무변경 — **T6-* 커밋 40개 중 네 프로젝트를 건드린 것이 0개다** (2026-08-01, §2 의 명령). T6-13 의 훅 한 줄도 결국 필요 없었다. **판정 명령이 틀려 있던 것을 이때 고쳤다** — `git diff main` 은 main 위에서 자기 자신과의 비교라 언제나 비었다
- [x] G6-2 마스터데이터 무변경 — `git diff --stat c36e183..HEAD -- masterdata/` 가 비어 있다 (2026-08-01)
- [x] G6-3 소켓 종단 동작 — `TestBed_EndToEnd_NpcArrives` 통과 (T6-35)
- [x] G6-4 링크 계약 유지 — `Category=Wire` **26건** · `Category=Contracts` **8건** 전량 통과. `Wire_NoStringOrDateTimeFields` 가 `Npc.Wire`·`Npc.TestBed.Protocol` 두 어셈블리에서 통과한다 (2026-08-01). **§13 이 정한 `Wire` 카테고리가 코드에 없던 것을 이때 붙였다** — 아래 참조
- [ ] G6-5 틱 예산 유지 — `--link tcp --npcs 500` 10분 회차를 **안 돌렸다.** 짧은 회차(NPC 50 · 13초)에서는 p99 4.0ms · Gen0 0
- [x] G6-6 할당 0 — `TcpLink_FlushDoesNotAllocate` 통과
- [x] G6-7 명령 유실 내성 — `TcpLink_CommandLossSynthesizesTimeout` 통과 (드롭 0.3 · 300틱 · 멈춘 NPC 0)
- [x] G6-8 재접속 — **실측 통과 (2026-08-01).** NPC 200 회차에서 게임서버(pid 고정)를 그대로 둔 채 NPC 서버 프로세스를 죽이고 다시 띄웠다. 게임서버 로그가 `link up`(tick 50~250) → `link down`(tick 300) → **`link up`(tick 350~)** 을 그대로 찍고, 누적 명령 수가 3,202 → 4,333 → 7,423 으로 계속 늘었다 — **NPC 가 다시 움직인다는 뜻이다.** `TcpLink_ReconnectKeepsSequenceMonotonic` 도 통과한다
- [ ] G6-9 데모 3종 — `run_demo.ps1` 로 세 시나리오를 끝까지 돌린 회차가 없다 (day 17분 · siege 4분 · blackout 3분)
- [ ] G6-10 눈으로 보인다 — **이것이 이 Phase 의 목적이다.** 사람이 클라이언트에서 NPC 하나를 골라 왜 그 행동을 하는지 인스펙터로 설명할 수 있어야 한다. 화면은 다 붙었고 사람이 앉는 일만 남았다

> **G6-4 판정 중에 드러난 것 — `Wire` 카테고리가 코드에 없었다 (2026-08-01).**
> §13 은 와이어 테스트를 `Wire` 로 분류해 두었는데 `[Trait("Category", "Wire")]` 를 단 것이
> **0개**였다. T6-02~T6-04 가 파일을 `tests/Npc.Tests/Wire/` 에 두는 것으로 갈음했고,
> `CLAUDE.md` §5 의 카테고리 표에도 `Wire` 가 없었다 — **사양 한 곳만 그 카테고리를 알고 있었다.**
>
> `WireDtoTests`·`FrameCodecTests`·`ClientProtocolTests` 에 붙이고 `CLAUDE.md` §5 에 `Wire`·`TestBed`
> 두 행을 더했다. `ClientProtocolTests` 는 `TestBed` 에서 `Wire` 로 옮겼다 — 그 파일이 보는 것은
> 소켓 위의 표현이지 게임서버의 동작이 아니다(§13 도 `ClientProtocol_SnapshotRoundTrips` 를
> `Wire` 로 적었다). 같은 이름으로 시작하는 `ClientProtocol_AoiCapsAt256` 은
> `SnapshotBuilderTests` 에 있고 그쪽은 `TestBed` 그대로다.
>
> **`Wire_NoStringOrDateTimeFields` 둘만 `Contracts` 를 겹쳐 단다.** N3·N4 는 와이어의 성질이
> 아니라 계약의 성질이고, `Category=Contracts` 로 N1~N8 을 한 번에 돌릴 때 여기가 빠지면
> 그 회차가 "패킷에 문자열이 없다" 를 안 보게 된다. xUnit 은 클래스·메서드 트레이트를 합치므로
> 두 회차 모두에 든다. 결과: `Wire` 26 · `Contracts` 6 → **8** · `TestBed` 58 → **52**.
