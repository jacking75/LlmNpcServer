using System.Collections.Immutable;
using Npc.Contracts;
using Npc.MasterData;
using Npc.Sim;
using Npc.TestGameServer.Link;

namespace Npc.TestGameServer;

/// <summary>
/// 틱 한 번의 단계. docs/20 §7.2.
///
/// <b>이 순서는 <c>Npc.Host</c> 의 <c>SimDriver.Tick</c> 과 같아야 한다.</b>
/// 순서가 다르면 같은 시나리오가 다르게 흐르고, 루프백과 소켓을 비교할 수 없게 된다 —
/// 그 비교가 P6 합격 기준 2 다 (docs/20 §1).
/// </summary>
public enum TickStage : byte
{
    /// <summary>1. <see cref="CommandInbox"/> 배수 → <c>SimWorld.ApplyCommand</c>.</summary>
    Commands = 1,

    /// <summary>2. <c>ScenarioRunner.Tick</c> — jsonl 주입.</summary>
    Scenario,

    /// <summary>3. <c>PlayerRegistry.Tick</c> — 입력 적용 → 이동 → 근접 변화 (T6-19).</summary>
    Players,

    /// <summary>4. <c>MovementSim.Tick</c>.</summary>
    Movement,

    /// <summary>5. <c>InteractionSim.Tick</c>.</summary>
    Interaction,

    /// <summary>6. <c>TransformEmitter.Tick</c>.</summary>
    Transforms,

    /// <summary>7. <c>NeedsSim.Tick</c>.</summary>
    Needs,

    /// <summary>8. <c>SimWorld.Tick</c> — <c>TickSync</c> 발행.</summary>
    World,
}

/// <summary>
/// 게임서버 대역의 월드 한 벌. docs/20 §7.1 · §7.2.
///
/// <para>
/// <b>시뮬 로직을 새로 짜지 않는다.</b> <see cref="MovementSim"/>·<see cref="InteractionSim"/>·
/// <see cref="NeedsSim"/>·<see cref="TransformEmitter"/>·<see cref="FaultInjector"/>·
/// <see cref="ScenarioRunner"/> 는 <c>Npc.Sim</c> 의 것을 그대로 쓴다. <c>Npc.Host</c> 의
/// <c>SimDriver</c> 와 같은 조립이며, 다른 것은 둘뿐이다.
/// </para>
///
/// <list type="bullet">
///   <item><b><see cref="PlayerBots"/> 를 쓰지 않는다</b> (docs/20 §7.3). 같은 자리에
///     <c>PlayerRegistry</c>(T6-19)가 들어가 같은 일(<c>ObservedByPlayer</c> 갱신)을 한다 —
///     둘이 같은 배열에 쓰면 서로의 판정을 덮어쓴다.</item>
///   <item>인구를 <see cref="NpcRoster"/> 로 뽑는다 (docs/20 §10.2). NPC 서버도 같은 함수를
///     부르므로 첨자가 어긋나지 않는다.</item>
/// </list>
///
/// <para>
/// <b>NPC 서버를 기다리지 않는다.</b> 락스텝이 없는 것이 이 테스트 베드의 핵심이다
/// (docs/20 §1) — 명령이 늦게 오고 이벤트가 몰려 오는 상황을 여기서 처음 만난다.
/// </para>
/// </summary>
public sealed class GameWorld : IAsyncDisposable
{
    /// <summary>틱 주기. 10Hz 고정이다 (docs/02 §3.1).</summary>
    public const int TickRate = 10;

    private readonly SimWorld _world;
    private readonly MovementSim _movement;
    private readonly InteractionSim _interaction;
    private readonly TransformEmitter _transforms;
    private readonly NeedsSim _needs;
    private readonly ScenarioRunner _scenario;
    private readonly CommandInbox _inbox;

    private GameWorld(
        SimWorld world,
        MovementSim movement,
        InteractionSim interaction,
        TransformEmitter transforms,
        NeedsSim needs,
        ScenarioRunner scenario,
        CommandInbox inbox,
        NpcRoster roster)
    {
        _world = world;
        _movement = movement;
        _interaction = interaction;
        _transforms = transforms;
        _needs = needs;
        _scenario = scenario;
        _inbox = inbox;
        Roster = roster;
    }

    /// <summary>월드.</summary>
    public SimWorld World => _world;

    /// <summary>이 게임서버가 보는 NPC 집합. 해시가 핸드셰이크에 실린다 (docs/20 §5.5).</summary>
    public NpcRoster Roster { get; }

    /// <summary>NPC 서버가 보낸 명령이 들어오는 곳. 링크 세션이 채운다 (T6-17).</summary>
    public CommandInbox Inbox => _inbox;

    /// <summary>시나리오 러너. <c>KillSwitch</c> 줄은 이벤트를 내지 않는다 (docs/20 §11.4).</summary>
    public ScenarioRunner Scenario => _scenario;

    /// <summary>이동 시뮬. 스냅샷 빌더가 보간에 쓴다 (T6-24).</summary>
    public MovementSim Movement => _movement;

    /// <summary>상호작용 시뮬.</summary>
    public InteractionSim Interaction => _interaction;

    /// <summary>변환 발행기. <c>Interpolate</c> 가 클라이언트 스냅샷의 위치가 된다 (docs/20 §8.1).</summary>
    public TransformEmitter Transforms => _transforms;

    /// <summary>욕구 시뮬. 플레이어 공격의 HP 차감이 여기로 간다 (T6-20).</summary>
    public NeedsSim Needs => _needs;

    /// <summary>
    /// 3단계 자리. <c>PlayerRegistry</c>(T6-19)가 여기 들어온다. null 이면 건너뛴다.
    ///
    /// <b>단계 알림은 null 이어도 나간다</b> — 순서 단언이 자리 채움 여부에 흔들리면 안 된다.
    /// </summary>
    public Action<Tick>? Players { get; set; }

    /// <summary>
    /// 단계 관측자. <b>테스트가 §7.2 순서를 확인하는 통로다.</b>
    ///
    /// 하위 시뮬은 <c>Npc.Sim</c> 의 봉인된 구체 클래스라 가로챌 수 없다. 대신 각 단계를
    /// 수행하기 <b>직전</b>에 여기로 알린다 — 기록된 순서가 곧 호출 순서다.
    /// </summary>
    public Action<TickStage>? StageObserver { get; set; }

    /// <summary>
    /// 1단계에서 적용하기 <b>직전</b>의 명령을 하나씩 알린다. <see cref="World.MirrorLog"/> 가 여기 붙는다
    /// (docs/20 §7.2 — "CommandInbox 배수 → SimWorld.ApplyCommand (+ MirrorLog 기록)").
    ///
    /// <para>
    /// <b>적용 후가 아니라 전이다.</b> 뒤에 붙이면 <c>Despawn</c> 처럼 대상 상태를 지우는 명령이
    /// 무엇을 향한 것이었는지 로그가 말하지 못한다.
    /// </para>
    /// </summary>
    public CommandSink? CommandObserver { get; set; }

    /// <summary>지금까지 적용한 명령 수.</summary>
    public long CommandsApplied { get; private set; }

    /// <summary>지금까지 민 틱 수.</summary>
    public long TicksProcessed { get; private set; }

    /// <summary>
    /// 마지막으로 민 틱. 아직 안 돌았으면 0 이다.
    ///
    /// <b><see cref="TicksProcessed"/> 와 다른 값일 수 있다.</b> <c>SkipTime</c>(docs/20 §8.3)이
    /// 틱을 점프시키면 민 횟수보다 틱 번호가 앞선다 — 링크로 나가는 <c>OccurredAt</c> 은
    /// 이쪽이라야 게임서버와 NPC 서버의 시간축이 같다.
    /// </summary>
    public Tick Now { get; private set; }

    /// <summary>
    /// 게임 안 하루의 몇 분째인가 (0~1439). 핸드셰이크가 싣는다 (A-10).
    ///
    /// <b>대역도 새벽 6시에 시작한다</b> — NPC 서버의 <c>GameClock</c> 기본값과 같아야
    /// 재기동 뒤 두 시계가 붙는다. 환산은 <c>GameClock</c> 과 같은 식이다:
    /// 게임 초 = 틱 × TimeScale ÷ 틱레이트.
    /// </summary>
    public int GameMinuteOfDay =>
        (int)((((6 * 3600L) + (Now.Value * _world.Options.TimeScale / TickRate)) / 60) % (24 * 60));

    /// <summary>
    /// 이 틱이 시작돼야 하는 벽시계 시각(ms). 기동 시각 기준이다.
    ///
    /// <b>매 틱 100ms 를 더하지 않는다.</b> 그렇게 하면 처리 시간이 누적 오차가 되어
    /// 게임 시각이 조금씩 뒤로 밀린다 — <c>Npc.Host</c> 의 펌프와 같은 계산이다.
    /// </summary>
    public static long DueMillis(long tick) => tick * 1000 / TickRate;

    /// <summary>옵션대로 조립하고 로스터를 세계에 올린다. 기동 시 1회.</summary>
    public static GameWorld Create(GameServerOptions options, MasterDataSet data, NpcRoster roster)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(roster);

        // PlayerBots 는 0 이다. 대역의 플레이어는 PlayerRegistry 가 돌린다 (docs/20 §7.3) —
        // 둘 다 ObservedByPlayer 에 쓰면 서로의 근접 판정을 덮어쓴다.
        var world = new SimWorld(data, roster.Count, new SimOptions(
            Seed: options.Seed,
            TimeScale: options.TimeScale,
            FailRate: options.FailRate,
            DropRate: options.DropRate,
            PlayerBots: 0));

        ImmutableArray<NpcInstanceDef> npcs = roster.Npcs;

        for (int i = 0; i < npcs.Length; i++)
        {
            world.Place(i, npcs[i].Archetype, npcs[i].Home);
        }

        var movement = new MovementSim(world);
        var interaction = new InteractionSim(world);
        var faults = new FaultInjector(
            world,
            (in NpcCommand c, Tick t) => movement.TryHandle(in c, t) || interaction.TryHandle(in c, t));

        world.Handler = faults.TryHandle;

        // 인구를 세계에 올린다. 스폰되지 않은 NPC 는 MovementSim 도 TransformEmitter 도 건너뛴다.
        // 실제 게임서버라면 접속·로딩이 하는 일이고, 대역에서는 기동 시 한 번에 한다.
        // 여기서 나온 NpcSpawned 이벤트는 이벤트 채널에 쌓였다가 링크 세션이 붙을 때 나간다.
        for (int i = 0; i < npcs.Length; i++)
        {
            var spawn = new NpcCommand
            {
                Kind = NpcCommandKind.Spawn,
                Npc = new NpcId(i),
                IssuedAt = default,
                Correlation = default,
                Priority = CommandPriority.Critical,
                Archetype = npcs[i].Archetype,
                Zone = npcs[i].Zone,
                TargetPoi = npcs[i].Home,
            };

            world.ApplyCommand(in spawn, default);
        }

        return new GameWorld(
            world,
            movement,
            interaction,
            new TransformEmitter(world, movement),
            new NeedsSim(world),
            options.Scenario is { } path ? ScenarioRunner.Load(path, data) : ScenarioRunner.Empty,
            new CommandInbox(),
            roster);
    }

    /// <summary>
    /// 한 틱. docs/20 §7.2 의 1~8 단계다.
    ///
    /// 9단계(이벤트 프레임 송신)와 10단계(클라이언트 스냅샷)는 링크·클라이언트 세션의 몫이라
    /// 여기 없다 — 이 클래스는 소켓을 모른다.
    /// </summary>
    public void Tick(Tick now)
    {
        Now = now;

        StageObserver?.Invoke(TickStage.Commands);

        while (_inbox.TryDequeue(out NpcCommand command))
        {
            CommandObserver?.Invoke(in command, now);

            _world.ApplyCommand(in command, now);
            CommandsApplied++;
        }

        StageObserver?.Invoke(TickStage.Scenario);
        _scenario.Tick(now, _world);

        StageObserver?.Invoke(TickStage.Players);
        Players?.Invoke(now);

        StageObserver?.Invoke(TickStage.Movement);
        _movement.Tick(now);

        StageObserver?.Invoke(TickStage.Interaction);
        _interaction.Tick(now);

        StageObserver?.Invoke(TickStage.Transforms);
        _transforms.Tick(now);

        StageObserver?.Invoke(TickStage.Needs);
        _needs.Tick(now);

        StageObserver?.Invoke(TickStage.World);
        _world.Tick(now);

        TicksProcessed++;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _world.DisposeAsync();

    /// <summary>
    /// <see cref="CommandObserver"/> 의 모양.
    ///
    /// <b><c>Action&lt;NpcCommand, Tick&gt;</c> 가 아닌 이유는 <c>in</c> 이다.</b> 56바이트 구조체를
    /// 틱마다 수백 번 복사하는 자리라 값 전달을 쓰지 않는다 — <c>SimWorld.CommandHandler</c> 와 같은 규약이다.
    /// </summary>
    public delegate void CommandSink(in NpcCommand command, Tick now);
}
