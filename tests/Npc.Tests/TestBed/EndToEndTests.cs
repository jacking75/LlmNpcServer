using System.Globalization;
using System.Net.Sockets;
using MemoryPack;
using Npc.Contracts;
using Npc.Core;
using Npc.Host;
using Npc.Host.Api;
using Npc.MasterData;
using Npc.Runtime;
using Npc.Sim;
using Npc.TestBed.Protocol;
using Npc.TestGameServer;
using Npc.TestGameServer.Client;
using Npc.TestGameServer.World;

namespace Npc.Tests.TestBed;

/// <summary>
/// T6-35 — 종단 테스트. docs/20 §13 · §14.1.
///
/// <para>
/// <b>여기서 처음으로 진짜 두 프로세스가 붙는다.</b> 게임서버(<see cref="GameServer"/>)와
/// NPC 서버(<see cref="NpcHost"/>)를 <b>인프로세스로, 포트 0 에</b> 띄워 실제 소켓으로 잇는다.
/// 가짜 상대를 하나라도 끼우면 "두 구현이 서로 맞는가" 라는 정작 볼 것을 못 본다.
/// </para>
///
/// <para>
/// <b>포트는 0(자동 할당)이다</b> (docs/20 §13) — 고정 포트를 쓰면 개발자가 데모를 띄워 둔 채
/// 테스트를 돌릴 때 깨진다.
/// </para>
///
/// <para>
/// <b>틱은 테스트가 민다.</b> <see cref="GameServer.RunAsync"/> 의 100ms 페이싱을 쓰면 300틱에
/// 30초가 걸린다. 대신 <see cref="GameServer.TickAsync"/> 를 직접 부르고, 매 틱
/// NPC 서버가 <see cref="Bed.MaxLead"/> 틱 이상 뒤처지지 않게 기다린다 —
/// <c>Npc.Host</c> 의 펌프가 루프백에서 하는 것과 같은 락스텝이다.
/// </para>
///
/// <para>
/// <b>결정론은 여기서 성립하지 않는다</b> (docs/20 §14.3). 명령이 몇 틱 늦게 도착할 수 있고
/// 그것이 스텝 경계를 바꾼다. 그래서 단언은 전부 <b>속성</b>이다 — 정확한 수를 세지 않는다.
/// </para>
///
/// <para>
/// <b><c>AllocationCollection</c> 에 넣는다.</b> 이 회차는 서버 둘을 띄우고 소켓 태스크 넷을
/// 돌리므로 스레드 풀을 꽤 먹는다 — 옆에서 <c>GC.GetAllocatedBytesForCurrentThread</c> 델타를
/// 재는 테스트가 돌면 그 값이 오염된다. 실제로 <c>Transition_DoesNotAllocate</c> 가
/// 2,608바이트로 한 번 흔들렸다. 병렬을 끄는 컬렉션에 같이 두는 것이 제일 싸다.
/// </para>
/// </summary>
[Trait("Category", "TestBed")]
[Collection(Npc.Tests.Runtime.AllocationCollection.Name)]
public sealed class EndToEndTests
{
    /// <summary>docs/20 §13 이 정한 회차 길이.</summary>
    private const int RunTicks = 300;

    /// <summary>
    /// T6-35 완료 조건 — 소켓 종단이 실제로 돈다. <c>NpcArrived ≥ 1</c> · 시퀀스 갭 0 · 드롭 0.
    ///
    /// <para>
    /// <b>이 하나가 G6-3 이다.</b> NPC 서버가 낸 <c>MoveTo</c> 가 소켓을 건너 게임서버에 닿고,
    /// <c>MovementSim</c> 이 도착시키고, <c>NpcArrived</c> 가 다시 소켓을 건너와 스텝이 전진한다 —
    /// 그 한 바퀴가 돌지 않으면 나머지 아홉 게이트는 전부 의미가 없다.
    /// </para>
    ///
    /// <para>가짜 클라이언트 소켓도 붙여 스냅샷이 오는 것까지 본다 (docs/20 §8.1).</para>
    /// </summary>
    [Fact]
    public async Task TestBed_EndToEnd_NpcArrives()
    {
        await using Bed bed = await Bed.StartAsync(npcs: 16);
        await using Peer client = await bed.JoinClientAsync();

        await bed.DriveAsync(RunTicks);
        await client.DrainAsync();

        LinkStats link = bed.Host.Snapshot().Link;

        Assert.True(
            bed.EventsOf(GameEventKind.NpcArrived) >= 1,
            $"도착이 한 건도 없다. {bed.Describe()}");

        // N6 — 갭이 0 이 아니면 이벤트가 새고 있다는 뜻이다.
        Assert.Equal(0, link.EventGapsDetected);

        // 역압에 걸린 명령이 없어야 한다. 16마리 회차에서 걸리면 링 크기가 아니라 경로가 이상한 것이다.
        Assert.Equal(0, link.CommandsDropped);

        // 스텝이 전진했다 = 도착 이벤트를 NPC 서버가 실제로 소비했다.
        Assert.True(bed.Host.Snapshot().StepsAdvanced > 0, bed.Describe());

        // 틱 예산은 소켓 경로에서도 그대로다 (docs/20 §14.1).
        Assert.Equal(0, bed.Host.Metrics.Snapshot().Tick.Overruns);

        // 클라이언트도 붙어서 세계를 본다. 2틱마다 한 장이므로 300틱이면 넉넉하다.
        Assert.NotEqual(0, client.PlayerId);
        Assert.True(client.Snapshots > 0, $"스냅샷을 한 장도 못 받았다 ({client.Snapshots}).");
    }

    /// <summary>
    /// T6-35 완료 조건 — 플레이어를 NPC 30m 안에 놓으면 그 NPC 의 LOD 가 0 이 된다.
    ///
    /// <para>
    /// <b>이 경로는 전부 게임서버가 계산해서 보내 준다</b> (docs/02 §3.3). NPC 서버는 거리를
    /// 재지 않는다 — 재면 NPC 5,000 × 플레이어 20 이 틱당 100,000회 거리 계산이 되고
    /// 그것만으로 틱 예산이 날아간다 (docs/11 §4).
    /// </para>
    ///
    /// <para>
    /// <b>플레이어를 매 틱 다시 NPC 위에 놓는다.</b> NPC 는 일터로 걸어가는 중이라
    /// 한 번만 놓으면 곧 <see cref="LodUpdater.LodZeroDistance"/> 밖으로 나간다.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TestBed_ProximityChangesLod()
    {
        const int Watched = 0;

        await using Bed bed = await Bed.StartAsync(npcs: 16);

        // 스폰 확인이 끝나야 NPC 가 세계에 있다.
        await bed.DriveAsync(10);

        PlayerId player = bed.Server.Players.Add();

        Assert.NotEqual(0, player.Value);

        // 붙기 전에는 관측 대상이 아니다.
        Assert.True(bed.Host.Trace(bed.GlobalOf(Watched)).Lod > 0, bed.Describe());

        await bed.DriveUntilAsync(
            () => bed.Host.Trace(bed.GlobalOf(Watched)).Lod == 0,
            maxTicks: 200,
            beforeTick: () => bed.Follow(player, Watched));

        Assert.Equal(0, bed.Host.Trace(bed.GlobalOf(Watched)).Lod);
    }

    /// <summary>
    /// T6-35 완료 조건 — <c>Interact</c> 가 그 NPC 의 최근 사건과 플래그에 남는다.
    ///
    /// <b>사거리 밖에서는 아무 일도 없다</b> (docs/20 §7.3) — 그래서 플레이어를 NPC 위에 올린다.
    /// 사거리 판정은 게임서버가 하고, 거절 사유는 클라이언트에 돌려주지 않는다.
    /// </summary>
    [Fact]
    public async Task TestBed_InteractRaisesInterrupt()
    {
        const int Watched = 0;

        await using Bed bed = await Bed.StartAsync(npcs: 16);

        await bed.DriveAsync(10);

        PlayerId player = bed.Server.Players.Add();

        await bed.DriveUntilAsync(
            () => Saw(bed, Watched, GameEventKind.PlayerInteracted),
            maxTicks: 200,
            beforeTick: () =>
            {
                bed.Follow(player, Watched);
                bed.Server.Players.TryInteract(player, Watched, new Tick(bed.Now));
            });

        Assert.True(bed.Server.Players.Interacts > 0, bed.Describe());
        Assert.True(Saw(bed, Watched, GameEventKind.PlayerInteracted), bed.Describe());

        // 근접 판정은 5틱마다다. 상호작용이 닿았으면 그 사이에 이미 들어와 있어야 한다.
        Assert.Contains(
            nameof(WorldFlags.PlayerNearby),
            bed.Host.Trace(bed.GlobalOf(Watched)).Flags,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// T6-35 완료 조건 — <c>--drop-rate 0.3</c> 에서 합성 타임아웃 &gt; 0, 멈춘 NPC 0.
    ///
    /// <para>
    /// <b>명령은 유실된다고 가정한다</b> (CLAUDE.md §2.2). 게임서버가 명령을 조용히 버리면
    /// 응답 이벤트가 아예 오지 않고, NPC 서버는 스텝의 <c>timeout_s</c> 로 <c>ActionFailed</c> 를
    /// 스스로 합성해 진행을 재개해야 한다. <b>이 경로가 깨지면 NPC 가 영원히 멈춘다</b> —
    /// 평소에는 아무 일도 안 일어나므로 일부러 깨뜨려야 보인다.
    /// </para>
    ///
    /// <para>
    /// <b>"멈춘 NPC" 를 무엇으로 세는가.</b> 스텝은 늦게라도 전진하므로 "지금 대기 중" 은
    /// 멈춘 것이 아니다. 여기서 세는 것은 <b>영원히 못 움직이는 상태</b> 둘이다 —
    /// <c>Unspawned</c>(스폰 확인을 못 받아 명령을 하나도 못 내는 상태)와
    /// <c>Done</c>(플랜이 끝났는데 순환이 아니라 다음이 없는 상태).
    /// </para>
    /// </summary>
    [Fact]
    public async Task TcpLink_CommandLossSynthesizesTimeout()
    {
        await using Bed bed = await Bed.StartAsync(npcs: 16, dropRate: 0.3);

        await bed.DriveAsync(RunTicks);

        HostSnapshot snapshot = bed.Host.Snapshot();

        Assert.True(snapshot.TimeoutsSynthesized > 0, $"합성이 0 이다. {bed.Describe()}");
        Assert.True(snapshot.StepsAdvanced > 0, bed.Describe());

        int stuck = 0;

        for (int npc = 0; npc < bed.Host.Npcs; npc++)
        {
            var status = (StepStatus)bed.Host.Store.StepStatus[npc];

            if (status is StepStatus.Unspawned or StepStatus.Done)
            {
                stuck++;
            }
        }

        Assert.Equal(0, stuck);

        // 드롭이 있어도 링크 자체는 멀쩡하다 — 버리는 것은 게임서버 안쪽이다.
        Assert.Equal(0, bed.Host.Snapshot().Link.EventGapsDetected);
    }

    /// <summary>
    /// B-02 완료 조건 — <b>게임서버 대역이 <c>InstanceId=2</c> 로 NPC 일부를 띄우고
    /// NPC 서버가 그것을 명령에 되돌려준다.</b>
    ///
    /// <para>
    /// 확장 슬롯이 통과하려면 네 곳이 전부 맞아야 한다 — 협상(프로토콜 2 + <c>ExtSlots</c>) ·
    /// v2 이벤트 배치 · <c>NpcStore.Instance</c> · v2 명령 배치. <b>하나라도 v1 로 떨어지면
    /// 값이 조용히 0 이 되고, 그 증상은 "인스턴스 던전 NPC 가 기본 월드에 보인다" 로
    /// 한참 뒤에 나타난다.</b> 그래서 대조를 게임서버 대역 쪽에서 센다.
    /// </para>
    ///
    /// <para>
    /// <c>InstanceEchoChecked</c> 를 같이 본다 — 0 이면 "통과했다" 가 아니라
    /// <b>아예 대조하지 않았다</b> 는 뜻이고, 그것을 합격으로 세면 게이트가 거짓이 된다.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TestBed_InstanceIdSurvivesTheRoundTrip()
    {
        const int Npcs = 16;
        const ushort Dungeon = 2;

        await using Bed bed = await Bed.StartAsync(
            npcs: Npcs,
            configure: server =>
            {
                // 짝수 NPC 만 인스턴스 던전에 넣는다. 전부 넣으면 "그냥 상수를 쓰고 있다" 와
                // 구분이 안 된다.
                for (int npc = 0; npc < Npcs; npc += 2)
                {
                    server.World.World.SetInstance(npc, new InstanceId(Dungeon));
                }
            });

        Assert.True(
            bed.Server.Link.Session is { ExtSlotsNegotiated: true },
            $"확장 슬롯이 협상되지 않았다. {bed.Describe()}");

        await bed.DriveAsync(RunTicks);

        SimWorld world = bed.Server.World.World;

        Assert.True(
            world.InstanceEchoChecked > 0,
            $"인스턴스를 한 번도 대조하지 않았다 — 통과가 아니다. {bed.Describe()}");

        Assert.Equal(0, world.InstanceMismatches);

        // NPC 서버가 실제로 기억하고 있는가. 홀수는 기본 월드(0) 그대로여야 한다.
        for (int npc = 0; npc < Npcs; npc++)
        {
            Assert.Equal(
                npc % 2 == 0 ? Dungeon : (ushort)0,
                bed.Host.Store.Instance[npc]);
        }
    }

    /// <summary>
    /// B-05 완료 조건 — <b>디스폰했다가 다시 스폰하면 살아난다.</b>
    ///
    /// <para>
    /// 소켓 경로로 확인하는 것은 <b>배선</b>이다 — 제어 → 게임서버 → <c>NpcDespawned</c> →
    /// NPC 서버가 슬롯을 비우고, <c>NpcSpawned</c>(<c>ExtA</c>=인스턴스 id) → 다시 앉는다.
    /// "다른 슬롯으로 살아난다" 와 "이전 거주자의 물건을 물려받지 않는다" 는
    /// <c>DynamicRosterTests</c> 가 슬롯 단위로 본다.
    /// </para>
    ///
    /// <para>
    /// <b>양쪽이 동적 로스터를 켠다.</b> 한쪽만 켜면 로스터 해시가 어긋나 붙지 못하고,
    /// 그것이 의도된 동작이다 — 이 회차가 붙는 것 자체가 그 합의를 확인한다.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TestBed_DespawnThenRespawnBringsTheNpcBack()
    {
        const int Watched = 3;

        await using Bed bed = await Bed.StartAsync(npcs: 16, dynamicRoster: true);

        await bed.DriveAsync(20);

        // 붙었다는 것은 곧 로스터 해시가 맞았다는 뜻이다.
        Assert.True(bed.Server.Link.Session is { IsAccepted: true }, bed.Describe());

        int occupant = bed.Host.Store.Occupant[Watched];

        Assert.NotEqual(0, occupant);

        // 디스폰 — 게임서버 제어로 내린다.
        Assert.True(bed.Server.Controls.Apply(
            new Control { Kind = (byte)ControlKind.Despawn, Amount = Watched }, new Tick(bed.Now)));

        await bed.DriveUntilAsync(
            () => bed.Host.Store.Occupant[Watched] == 0,
            maxTicks: 200);

        Assert.Equal(0, bed.Host.Store.Occupant[Watched]);
        Assert.Equal((byte)StepStatus.Unspawned, bed.Host.Store.StepStatus[Watched]);

        // 다시 스폰 — NpcSpawned 의 ExtA 가 "누구인가" 를 실어 온다.
        Assert.True(bed.Server.Controls.Apply(
            new Control { Kind = (byte)ControlKind.Spawn, Amount = Watched }, new Tick(bed.Now)));

        await bed.DriveUntilAsync(
            () => bed.Host.Store.Occupant[Watched] == occupant,
            maxTicks: 200);

        Assert.Equal(occupant, bed.Host.Store.Occupant[Watched]);

        // <b>Ready 를 그대로 단언하지 않는다.</b> NPC 서버는 다른 스레드에서 계속 도므로
        // 슬롯이 앉은 직후 이미 명령을 내고 Waiting 으로 넘어가 있을 수 있다 —
        // 여기서 볼 것은 "세계에 있다" 이고 그것은 Unspawned 가 아님이다.
        Assert.NotEqual((byte)StepStatus.Unspawned, bed.Host.Store.StepStatus[Watched]);

        // 아키타입·집이 다시 채워졌는가 — 시드가 돌았다는 증거다.
        Assert.NotEqual(0, bed.Host.Store.HomePoi[Watched]);
        Assert.NotEqual(0, bed.Host.Store.PlanId[Watched]);
    }

    /// <summary>
    /// B-06 완료 조건 — <b>플레이어를 적대로 두고 경비병 곁을 지나면 경비병이 공격한다.</b>
    ///
    /// <para>
    /// 뷰어가 하는 일을 제어 메시지로 대신한다 — <c>ControlKind.SetHostile</c> 로 플레이어를
    /// 적대로 두고, 그 플레이어를 전투 가능한 NPC 위로 옮긴다. 게임서버가
    /// <c>PlayerHostility(Hostile)</c> 를 내고, NPC 서버가 인터럽트로
    /// <c>CombatAction(TargetPlayer=…)</c> 을 낸다.
    /// </para>
    ///
    /// <para>
    /// <b>적대 판정은 게임서버가 한다.</b> 여기서 확인하는 것은 그 결과가 소켓을 건너와
    /// 명령이 되는가다 — 판정 자체는 대역의 몫이고 실제 게임서버는 세력·PK 상태로 한다.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TestBed_HostilePlayerMakesTheGuardAttack()
    {
        const int Npcs = 48;

        await using Bed bed = await Bed.StartAsync(npcs: Npcs);

        await bed.DriveAsync(20);

        // 전투 가능하고 Attack 을 쓸 수 있는 아키타입의 슬롯을 찾는다.
        // 없으면 회차가 아무것도 못 본다 — 그때는 테스트 설정이 잘못된 것이다.
        MasterDataSet data = MasterDataLoader.Load(TestPaths.MasterData);

        Assert.True(data.Actions.TryGet("Attack", out ActionDef attack));

        int guard = -1;

        for (int npc = 0; npc < Npcs; npc++)
        {
            ArchetypeDef def = data.Archetypes[new ArchetypeId(bed.Host.Store.ArchetypeCode[npc])];

            if (def.CombatCapable && def.Allows(attack.Code))
            {
                guard = npc;
                break;
            }
        }

        Assert.True(guard >= 0, $"전투 가능한 NPC 가 로스터에 없다. {bed.Describe()}");

        PlayerId player = bed.Server.Players.Add();

        Assert.True(bed.Server.Controls.Apply(
            new Control { Kind = (byte)ControlKind.SetHostile, Amount = player.Value, Code = 1 },
            new Tick(bed.Now)));

        long before = bed.Host.Snapshot().PlayerTargetedCommands;

        await bed.DriveUntilAsync(
            () => bed.Host.Snapshot().PlayerTargetedCommands > before,
            maxTicks: 300,
            beforeTick: () => bed.Follow(player, guard));

        HostSnapshot snapshot = bed.Host.Snapshot();

        Assert.True(
            snapshot.HostilityEvents > 0,
            $"PlayerHostility 가 소켓을 건너오지 않았다. {bed.Describe()}");

        Assert.True(
            snapshot.PlayerTargetedCommands > before,
            $"TargetPlayer 를 찍은 명령이 안 나갔다. {bed.Describe()}");

        // 그 NPC 가 적대 플레이어를 기억하고 있다.
        Assert.Equal(player.Value, bed.Host.Store.HostilePlayer[guard]);
    }

    // ---------------------------------------------------------------- 보조

    private static bool Saw(Bed bed, int npc, GameEventKind kind)
    {
        foreach (TraceEvent recent in bed.Host.Trace(bed.GlobalOf(npc)).Recent)
        {
            if (string.Equals(recent.Kind, kind.ToString(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 게임서버 + NPC 서버 한 벌. <b>둘 다 진짜다.</b>
    /// </summary>
    private sealed class Bed : IAsyncDisposable
    {
        /// <summary>NPC 서버가 뒤처져도 되는 틱 수. <c>Npc.Host</c> 펌프의 값과 같다.</summary>
        public const int MaxLead = 1;

        /// <summary>따라오기를 기다리는 상한(ms). 넘으면 테스트 실패다 — 조용히 넘어가면 회차가 비어 버린다.</summary>
        public const int CatchUpTimeoutMillis = 10_000;

        private readonly CancellationTokenSource _cts = new();
        private readonly MirrorLog.Cursor _cursor;
        private readonly LoggedEvent[] _buffer = new LoggedEvent[MirrorLog.Capacity];
        private readonly long[] _counts = new long[256];

        private Task? _accept;
        private Task? _run;

        private Bed(GameServer server, NpcHost host)
        {
            Server = server;
            Host = host;
            _cursor = server.Mirror.NewCursor();
        }

        public GameServer Server { get; }

        public NpcHost Host { get; }

        public CancellationToken Token => _cts.Token;

        /// <summary>지금 틱 번호. <b>이름이 <c>Tick</c> 이 아닌 이유는 그것이 타입이기 때문이다.</b></summary>
        public long Now { get; private set; }

        /// <summary>게임서버가 낸 이 종류의 이벤트 수. 미러에서 샌다 (docs/20 §7.4).</summary>
        public long EventsOf(GameEventKind kind) => _counts[(byte)kind];

        /// <param name="npcs">NPC 수.</param>
        /// <param name="dropRate">게임서버가 명령을 조용히 버릴 확률.</param>
        /// <param name="configure">
        /// 게임서버를 <b>듣기 시작하기 전에</b> 손보는 자리. 인스턴스 배정처럼
        /// 스폰보다 먼저 정해져야 하는 값이 여기 들어간다 (B-02).
        /// </param>
        public static async Task<Bed> StartAsync(
            int npcs = 32,
            double dropRate = 0,
            Action<GameServer>? configure = null,
            bool dynamicRoster = false)
        {
            GameServer server = GameServer.Create(
                new GameServerOptions
                {
                    Npcs = npcs,
                    LinkPort = 0,
                    ClientPort = 0,
                    TimeScale = 60,
                    DropRate = dropRate,
                    MasterData = TestPaths.MasterData,

                    // B-05 — 양쪽이 같이 켜야 한다. 로스터 해시가 달라지기 때문이다.
                    DynamicRoster = dynamicRoster,
                },
                TextWriter.Null);

            configure?.Invoke(server);

            server.Start();

            // --npcs·--time-scale·로스터·마스터데이터가 넷 다 같아야 핸드셰이크가 통과한다
            // (docs/20 §5.5). 하나라도 다르면 여기서 거절되고, 그것이 의도된 동작이다.
            string[] args =
            [
                "--link", "tcp",
                "--gs-port", server.LinkPort.ToString(CultureInfo.InvariantCulture),
                "--npcs", npcs.ToString(CultureInfo.InvariantCulture),
                "--time-scale", "60",
                "--days", "0",
                "--tier", "none",
                "--no-dashboard",

                // <b>프리베이크 스토어를 일부러 안 읽는다.</b> 이 회차가 보는 것은 소켓 경로이지
                // 플랜 품질이 아니고, 폴백 40개만으로도 MoveTo → 도착이 돈다.
                // 읽으면 회차마다 254개 파일을 다시 로드하는데, 그 디스크·할당이
                // 옆에서 도는 예산 테스트(Host_LoadsEveryBucketWithinThreeSeconds 등)를 흔든다.
                // 이름을 유니크하게 둬야 ResolvePlanStore 가 위로 올라가 저장소의 것을 찾지 않는다.
                "--planstore", "no-planstore-testbed-e2e",
            ];

            if (dynamicRoster)
            {
                // 여유 슬롯을 둔다. 디스폰한 NPC 가 다른 슬롯으로 살아나는 것을 보려면
                // 로스터 수보다 슬롯이 많아야 한다 (B-05).
                args = [.. args, "--dynamic-roster", "--npc-capacity", (npcs * 2).ToString(CultureInfo.InvariantCulture)];
            }

            Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

            NpcHost host = NpcHost.Create(
                options with { MasterData = TestPaths.MasterData }, TextWriter.Null);

            var bed = new Bed(server, host);

            bed._accept = server.AcceptAsync(bed.Token);
            bed._run = host.RunAsync(bed.Token);

            // accept 도 접속도 다른 태스크라 몇 틱 걸린다. 붙을 때까지 민다.
            for (int i = 0; i < 600 && server.Link.Session is not { IsAccepted: true }; i++)
            {
                await bed.TickAsync();
                await Task.Delay(5, bed.Token);
            }

            Assert.True(
                server.Link.Session is { IsAccepted: true },
                $"링크 세션이 붙지 않았다. {bed.Describe()}");

            // <b>NPC 서버가 첫 틱을 돌 때까지는 락스텝을 걸지 않는다.</b> 걸면 교착한다 —
            // NPC 서버의 시계는 <c>TickSync</c> 로만 움직이는데(docs/15 §3), 그 TickSync 는
            // 게임서버가 다음 틱을 돌아야 나간다. 세션이 붙기 전 틱들의 TickSync 는
            // 링크 없는 구간의 이벤트로 이미 배수돼 버렸으므로, 여기서 기다리면
            // "아직 안 돈 NPC 서버" 를 기다리며 "TickSync 를 낼 게임서버" 를 멈춰 세우게 된다.
            for (int i = 0; i < 200 && host.Loop.TicksCommitted == 0; i++)
            {
                await bed.TickAsync();
                await Task.Delay(1, bed.Token);
            }

            Assert.True(host.Loop.TicksCommitted > 0, $"NPC 서버가 틱을 돌지 않았다. {bed.Describe()}");

            return bed;
        }

        /// <summary>틱 한 번. §7.2 의 1~10단계 전부다.</summary>
        public async Task TickAsync()
        {
            await Server.TickAsync(new Tick(++Now), Token);

            Harvest();
        }

        /// <summary>NPC 서버와 보조를 맞춰 <paramref name="ticks"/> 틱을 민다.</summary>
        public async Task DriveAsync(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                await TickAsync();
                await CatchUpAsync();
            }
        }

        /// <summary>조건이 참이 될 때까지 민다. 안 되면 실패다.</summary>
        public async Task DriveUntilAsync(Func<bool> until, int maxTicks, Action? beforeTick = null)
        {
            ArgumentNullException.ThrowIfNull(until);

            for (int i = 0; i < maxTicks; i++)
            {
                beforeTick?.Invoke();

                await TickAsync();
                await CatchUpAsync();

                if (until())
                {
                    return;
                }
            }

            Assert.Fail($"{maxTicks}틱 안에 조건이 만족되지 않았다. {Describe()}");
        }

        /// <summary>플레이어를 그 NPC 위로 옮긴다. 근접·상호작용 사거리 판정의 기준이 위치뿐이다.</summary>
        public void Follow(PlayerId player, int npc) =>
            Server.Players.Teleport(player, Server.World.Transforms.Interpolate(npc, new Tick(Now)));

        /// <summary>
        /// 슬롯의 전역 NPC id (A-08). <c>/npc/{id}</c>·<c>Host.Trace</c> 가 받는 값이다.
        ///
        /// <b>두 서버는 같은 로스터를 같은 순서로 뽑는다</b>(docs/20 §10.2)이라 슬롯이 같고,
        /// 그 슬롯의 거주자 id 도 같다.
        /// </summary>
        /// <param name="slot">슬롯 첨자.</param>
        public int GlobalOf(int slot) => Server.World.World.NpcIdOf(slot).Value;

        /// <summary>실패 메시지에 붙일 한 줄. <b>숫자가 없으면 왜 실패했는지 알 수 없다.</b></summary>
        public string Describe()
        {
            HostSnapshot s = Host.Snapshot();

            return string.Create(
                CultureInfo.InvariantCulture,
                $"gs tick {Now} · npc tick {s.TicksProcessed}/{Host.Loop.TicksCommitted} · "
                + $"cmd {s.CommandsEmitted}→{Server.World.CommandsApplied} · "
                + $"step {s.StepsAdvanced} · timeout {s.TimeoutsSynthesized} · "
                + $"arrived {EventsOf(GameEventKind.NpcArrived)} · "
                + $"gap {s.Link.EventGapsDetected} · drop {s.Link.CommandsDropped}");
        }

        /// <summary>클라이언트 소켓 하나를 붙인다. <c>CliHello</c> → <c>SrvHello</c> 까지 간다.</summary>
        public async Task<Peer> JoinClientAsync()
        {
            var client = new TcpClient { NoDelay = true };

            await client.ConnectAsync("127.0.0.1", Server.ClientPort, Token);

            var peer = new Peer(client, Token);

            await peer.SendHelloAsync();

            for (int i = 0; i < 200 && peer.PlayerId == 0; i++)
            {
                await TickAsync();
                await peer.DrainAsync();
            }

            Assert.NotEqual(0, peer.PlayerId);

            return peer;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();

            foreach (Task? task in new[] { _run, _accept })
            {
                if (task is null)
                {
                    continue;
                }

                try
                {
                    await task;
                }
                catch (Exception)
                {
                    // 취소·소켓 종료로 끝난다.
                }
            }

            await Host.DisposeAsync();
            await Server.DisposeAsync();

            _cts.Dispose();
        }

        /// <summary>
        /// NPC 서버가 <see cref="MaxLead"/> 틱 이상 뒤처지면 기다린다.
        ///
        /// <b>이것이 없으면 게임서버가 300틱을 혼자 다 돌아 버린다</b> — 그때 NPC 서버의 명령은
        /// 전부 지나간 세계에 도착하고, 회차가 "아무 일도 없었다" 로 끝난다.
        /// </summary>
        private async Task CatchUpAsync()
        {
            long deadline = Environment.TickCount64 + CatchUpTimeoutMillis;

            while (Now - Host.Loop.TicksCommitted > MaxLead)
            {
                if (Environment.TickCount64 > deadline)
                {
                    Assert.Fail($"NPC 서버가 따라오지 못했다. {Describe()}");
                }

                await Task.Delay(1, Token);
            }
        }

        /// <summary>미러에 새로 쌓인 이벤트를 종류별로 센다. 링이 1,024칸이라 틱마다 걷는다.</summary>
        private void Harvest()
        {
            int got;

            while ((got = Server.Mirror.ReadEvents(_cursor, _buffer)) > 0)
            {
                for (int i = 0; i < got; i++)
                {
                    _counts[_buffer[i].Kind]++;
                }
            }
        }
    }

    /// <summary>테스트 쪽 클라이언트 소켓. 받은 것을 세기만 한다.</summary>
    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly CancellationToken _ct;

        public Peer(TcpClient client, CancellationToken ct)
        {
            _client = client;
            _stream = client.GetStream();
            _ct = ct;
        }

        public int PlayerId { get; private set; }

        public int Snapshots { get; private set; }

        public Task SendHelloAsync() => ClientSession.WriteFrameAsync(
            _stream,
            ClientMessageKind.CliHello,
            MemoryPackSerializer.Serialize(
                new CliHello { ProtocolVersion = ClientProtocol.Version, ClientVersion = 1 }),
            _ct);

        /// <summary>지금 와 있는 프레임을 전부 읽는다.</summary>
        public async Task DrainAsync()
        {
            while (_client.Available > 0)
            {
                (ClientMessageKind kind, byte[] payload) =
                    await ClientSession.ReadFrameAsync(_stream, _ct);

                switch (kind)
                {
                    case ClientMessageKind.SrvHello:
                        PlayerId = MemoryPackSerializer.Deserialize<SrvHello>(payload).PlayerId;
                        break;

                    case ClientMessageKind.Snapshot:
                        Snapshots++;
                        break;

                    default:
                        break;
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
