using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>
/// B-06 — 적대 플레이어 감지와 플레이어 대상 바인딩.
///
/// <para>
/// <b>"보이는 모든 플레이어를 선제공격하는 경비병" 이 지금까지 불가능했다.</b>
/// <c>PlayerProximity</c> 는 중립 인지만 세우고, <c>Attack</c> 의 대상은 <c>TargetNpc</c> 중심이라
/// 플레이어를 찍을 길이 없었다 (FAQ Q6).
/// </para>
///
/// <para>
/// <b>적대 판정은 게임서버가 한다.</b> 세력·PK 상태·퀘스트가 섞인 판단이고 그 자료는 전부
/// 게임서버의 것이다 — 두 쪽에서 판정하면 어긋나고, 어긋난 순간
/// <b>"경비병이 아군을 공격한다"</b> 가 된다. 여기서 시험하는 것은 <b>받은 결과를 어떻게 쓰는가</b> 다.
/// </para>
/// </summary>
public sealed class HostilityTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>적대 플레이어 id. 0 이 아니어야 "없음" 과 구분된다.</summary>
    private const int Attacker = 4242;

    /// <summary><c>PlayerHostility(Hostile)</c> 가 플래그를 세우고 플레이어를 기억한다.</summary>
    [Fact]
    public void Hostility_RaisesTheFlagAndRemembersThePlayer()
    {
        PlanExecutorTests.Harness h = PlanExecutorTests.NewHarness(1, "town_guard");

        h.Applier.Apply(Hostile(0, Attacker, Hostility.Hostile, 1));

        Assert.True((h.Store.Flags[0] & WorldFlags.HostilePlayerNearby) != 0);
        Assert.Equal(Attacker, h.Store.HostilePlayer[0]);

        // 적대면 근처에 있는 것이기도 하다 — 둘을 따로 받을 이유가 없다.
        Assert.True((h.Store.Flags[0] & WorldFlags.PlayerNearby) != 0);
    }

    /// <summary>중립·우호는 플래그를 내리고 기억도 지운다.</summary>
    [Theory]
    [InlineData(Hostility.Neutral)]
    [InlineData(Hostility.Friendly)]
    public void Hostility_LowersTheFlagOnNeutralOrFriendly(Hostility after)
    {
        PlanExecutorTests.Harness h = PlanExecutorTests.NewHarness(1, "town_guard");

        h.Applier.Apply(Hostile(0, Attacker, Hostility.Hostile, 1));
        h.Applier.Apply(Hostile(0, Attacker, after, 2));

        Assert.False((h.Store.Flags[0] & WorldFlags.HostilePlayerNearby) != 0);
        Assert.Equal(0, h.Store.HostilePlayer[0]);
    }

    /// <summary>
    /// <b><c>PlayerProximity(Leave)</c> 도 적대를 내린다.</b> 떠난 플레이어를 대상으로 남겨 두면
    /// 인터럽트가 이미 없는 플레이어를 공격하라고 시킨다.
    /// </summary>
    [Fact]
    public void Proximity_LeaveClearsHostility()
    {
        PlanExecutorTests.Harness h = PlanExecutorTests.NewHarness(1, "town_guard");

        h.Applier.Apply(Hostile(0, Attacker, Hostility.Hostile, 1));

        h.Applier.Apply(new GameEvent
        {
            Kind = GameEventKind.PlayerProximity,
            Sequence = 2,
            OccurredAt = new Tick(20),
            Npc = new NpcId(0),
            Player = new PlayerId(Attacker),
            Amount = 250,
            Code = (byte)ProximityChange.Leave,
        });

        Assert.False((h.Store.Flags[0] & WorldFlags.HostilePlayerNearby) != 0);
        Assert.Equal(0, h.Store.HostilePlayer[0]);
    }

    /// <summary>
    /// 멱등 (N7). 같은 적대 이벤트를 두 번 주입해도 상태 해시가 같다.
    /// </summary>
    [Fact]
    public void Hostility_IsIdempotent()
    {
        PlanExecutorTests.Harness h = PlanExecutorTests.NewHarness(1, "town_guard");

        h.Applier.Apply(Hostile(0, Attacker, Hostility.Hostile, 1));

        ulong once = h.Store.StateHash();

        h.Applier.Apply(Hostile(0, Attacker, Hostility.Hostile, 2));

        Assert.Equal(once, h.Store.StateHash());
    }

    /// <summary>
    /// <b>다른 플레이어의 중립 통보가 적대를 지우지 않는다.</b> 지우면 경비병이 공격을 멈춘다 —
    /// 게임서버는 NPC 당 1건만 보내므로 보통 같은 id 지만, 순서가 엇갈린 회차에서 이 차이가 난다.
    /// </summary>
    [Fact]
    public void Hostility_OnlyTheSamePlayerClearsIt()
    {
        PlanExecutorTests.Harness h = PlanExecutorTests.NewHarness(1, "town_guard");

        h.Applier.Apply(Hostile(0, Attacker, Hostility.Hostile, 1));
        h.Applier.Apply(Hostile(0, Attacker + 1, Hostility.Neutral, 2));

        // 플래그는 내려간다(게임서버가 "이 NPC 주변에 적대가 없다" 고 말한 것이다).
        Assert.False((h.Store.Flags[0] & WorldFlags.HostilePlayerNearby) != 0);

        // 그러나 기억한 id 는 그 플레이어의 것이 아니라 그대로다.
        Assert.Equal(Attacker, h.Store.HostilePlayer[0]);
    }

    // ---------------------------------------------------------------- 바인딩 · 인터럽트

    /// <summary>
    /// <b>인터럽트가 <c>CombatAction(TargetPlayer=…)</c> 을 낸다</b> (B-06 완료 조건의 핵심).
    ///
    /// <c>TargetNpc</c> 는 0 이어야 한다 — 둘 다 채우면 게임서버가 어느 쪽을 공격할지 모른다.
    /// </summary>
    [Fact]
    public void Interrupt_AttacksTheHostilePlayer()
    {
        Rig rig = NewRig("town_guard");

        rig.H.Applier.Apply(Hostile(0, Attacker, Hostility.Hostile, 1));

        GameEvent trigger = Hostile(0, Attacker, Hostility.Hostile, 2);

        Assert.True(rig.Matcher.Handle(in trigger, new Tick(30), rig.Executor, rig.H.Link, rig.Queue));

        NpcCommand attack = Assert.Single(rig.H.Link.Commands);

        Assert.Equal(NpcCommandKind.CombatAction, attack.Kind);
        Assert.Equal(CommandPriority.Critical, attack.Priority);
        Assert.Equal(Attacker, attack.TargetPlayer.Value);
        Assert.Equal(0, attack.TargetNpc.Value);
        Assert.Equal((byte)CombatActionKind.MeleeAttack, attack.Flags);

        // 먼저 공격하고 계획은 나중이다 — 재계획 큐에 긴급으로 들어간다.
        Assert.True(rig.Queue.Contains(0));
    }

    /// <summary>
    /// <c>nearest:hostile_player</c> 가 왕복한다. 마스터데이터에 적어 둔 문자열이 그대로 돌아와야
    /// 편집 도구·설명 카드가 같은 것을 보여 준다.
    /// </summary>
    [Fact]
    public void NpcRef_HostilePlayerRoundTrips()
    {
        ushort code = NpcRefCodes.HostilePlayer();

        Assert.Equal(NpcRefKind.HostilePlayer, NpcRefCodes.KindOf(code));

        // 페이로드를 쓰지 않는다 — 대상은 런타임 상태에서 온다.
        Assert.Equal(0, NpcRefCodes.PayloadOf(code));
    }

    /// <summary>
    /// <b>인터럽트 규칙이 마스터데이터에 있다.</b> 코드가 준비돼도 규칙이 없으면 아무 일도 안 난다.
    /// </summary>
    [Fact]
    public void Interrupts_HaveTheHostilePlayerRule()
    {
        InterruptRule rule = Assert.Single(
            s_data.Interrupts.Rules,
            r => string.Equals(r.Id, "attack_hostile_player", StringComparison.Ordinal));

        Assert.True((rule.AnyFlag & WorldFlags.HostilePlayerNearby) != 0);
        Assert.True(rule.CombatCapable);
        Assert.Equal(NpcRefKind.HostilePlayer, NpcRefCodes.KindOf(rule.NpcRef));
    }

    // ---------------------------------------------------------------- 조립

    private sealed record Rig(
        PlanExecutorTests.Harness H, InterruptMatcher Matcher, PlanExecutor Executor, ReplanQueue Queue);

    private static Rig NewRig(string archetype)
    {
        PlanExecutorTests.Harness h = PlanExecutorTests.NewHarness(1, archetype);
        var queue = new ReplanQueue(1);

        var executor = new PlanExecutor(
            s_data, h.Store, h.Plans, h.Correlations,
            new CommandEmitter(s_data, new PoiBinder(s_data.Pois)),
            timeScale: 600)
        {
            ReplanQueue = queue,
        };

        return new Rig(h, new InterruptMatcher(s_data, h.Store), executor, queue);
    }

    private static GameEvent Hostile(int npc, int player, Hostility hostility, long sequence) => new()
    {
        Kind = GameEventKind.PlayerHostility,
        Sequence = sequence,
        OccurredAt = new Tick(10 + sequence),
        Npc = new NpcId(npc),
        Player = new PlayerId(player),
        Code = (byte)hostility,
    };
}
