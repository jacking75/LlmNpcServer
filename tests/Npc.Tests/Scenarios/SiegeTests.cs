using System.Diagnostics;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Gateway;
using Npc.Host.Api;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;
using Npc.Sim;
using Npc.Tests.Fakes;
using Npc.Tests.Runtime;

namespace Npc.Tests.Scenarios;

/// <summary>
/// 시나리오 B — 공성. docs/14 §7 · docs/00 §2.
///
/// <b>"아키타입별로 다르게 반응한다" 가 이 시나리오의 핵심이다.</b>
/// 전부 똑같이 도망가면 LLM 을 쓴 의미가 없다.
///
/// 실 LLM 을 부르지 않는다 — 필요한 War 버킷은 <c>planstore/</c> 에 프리베이크되어 있고
/// 이 테스트가 보는 것은 <b>런타임의 전환 경로</b>다.
///
/// <para>
/// <b>네 건은 <c>Category=Gate</c> 다</b> (2026-07-28 결정 14).
/// <c>planstore/plans/</c> 는 <c>.gitignore</c> 대상이라(CLAUDE.md §6 — 재현 가능한 생성물)
/// 저장소만 받은 상태에서는 War 버킷이 없고, 그러면 넷이
/// <b>"War 인데 아무 플랜도 갈아타지 않았다"</b> 로 실패한다. 실제로 커밋 <c>a8ddd61</c> 에서 재현했다.
/// </para>
///
/// <para>
/// <b>버킷을 테스트가 합성해서 채우지 않는다.</b> 그렇게 하면 "War 에서 플랜이 달라졌다" 가
/// 런타임이 아니라 픽스처의 성질이 되어 <b>테스트가 공허해진다</b> —
/// 합성 플랜의 goal 만 바꿔도 통과하기 때문이다. 이 넷이 판정하려는 것은
/// "프리베이크된 다른 버킷의 플랜으로 실제로 갈아타는가" 이고, 그러려면 진짜 산출물이 있어야 한다.
/// </para>
///
/// <para>
/// 그래서 <c>Category=Gate</c> 로 가른다 — <b>산출물이 없으면 실패하되</b> 기본 CI 에서는 뺀다.
/// 없는 것을 통과로 세면 게이트가 거짓이 된다 (CLAUDE.md §5).
/// 나머지 셋(인터럽트 1틱 · 전환 p99 · 시나리오 파일 점검)은 산출물 없이도 성립하므로 상시 돈다.
/// </para>
///
/// <para>
/// 판정하려면 도달 집합 + 공성 <b>978버킷</b>을 프리베이크한다 —
/// <c>docs/measurements/reach264_prebake.md §5.2</c> · §6 에 명령이 있다.
/// </para>
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class SiegeTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>시나리오 B 가 보는 세 아키타입. docs/14 §7.</summary>
    private static readonly string[] Watched = ["town_guard", "farmer", "blacksmith"];

    private sealed record Rig(
        NpcServerLoop Loop,
        NpcStore Store,
        GameClock Clock,
        PlanStore Plans,
        PlanSwapper Swapper,
        BucketTransition Transition,
        ZoneStateTable Zones,
        InterruptMatcher Interrupts,
        PlanExecutor Executor,
        ReplanQueue Queue,
        CompletingLink Link,
        EventApplier Applier);

    /// <summary>
    /// 공성 무대. 세 아키타입을 <c>town_center</c> 에 모아 둔다 —
    /// 실제 인구 배치는 아키타입이 흩어져 있어 한 존의 전환을 관측하기 어렵다.
    /// </summary>
    private static Rig NewRig(int perArchetype = 40)
    {
        int npcs = perArchetype * Watched.Length;

        var store = new NpcStore();
        store.Allocate(npcs, s_data.Items.MaxCode + 1);

        var link = new CompletingLink();
        // Noon(12시)에서 시작한다. town_guard 의 duty_hours 는 Morning~Evening 이라
        // Dawn 에서는 OnDuty 가 서지 않고, 그러면 Guard·Patrol 을 쓰는 플랜이 성립하지 않는다
        // (docs/01 §5). Noon 은 11~14시라 이 테스트가 도는 90틱(=90 게임분) 안에 경계가 없다.
        var clock = new GameClock(s_data.Buckets, timeScale: 600, startGameHour: 12);
        var correlations = new CorrelationTable(npcs);
        var zones = new ZoneStateTable(s_data);
        var lod = new LodUpdater(store) { ZoneStates = zones };
        var applier = new EventApplier(s_data, store, clock, correlations, lod);
        var emitter = new CommandEmitter(s_data, new PoiBinder(s_data.Pois));
        var swapper = new PlanSwapper(store);
        var queue = new ReplanQueue(npcs);
        var snapshots = new ReplanSnapshots(npcs);

        PlanStore plans = PlanStore.CreateIdleOnly(s_data);
        var fallbackOf = new int[s_data.Archetypes.Count];

        foreach (FallbackPlanEntry entry in s_data.Fallbacks!.Plans)
        {
            PlanId id = plans.Register(entry.Plan);

            plans.SetFallback(entry.Archetype, id);
            fallbackOf[entry.Archetype.Value] = id.Value;
        }

        PlanStoreIo.LoadAll(TestPaths.At("planstore"), plans, s_data);

        var executor = new PlanExecutor(s_data, store, plans, correlations, emitter, timeScale: 600)
        {
            Swapper = swapper,
            ReplanQueue = queue,
        };

        var bands = new LodBandSet(store);
        var interrupts = new InterruptMatcher(s_data, store) { Snapshots = snapshots };
        var transition = new BucketTransition(store, plans, s_data) { ZoneStates = zones };

        // town_center 의 POI 하나에 전원을 세운다. 존이 같아야 존 이벤트가 전원에 닿는다.
        Assert.True(s_data.Zones.TryGet("town_center", out ZoneDef center));
        PoiId home = FirstPoiIn(center.Code);

        for (int i = 0; i < npcs; i++)
        {
            Assert.True(s_data.Archetypes.TryGet(Watched[i % Watched.Length], out ArchetypeDef def));

            applier.Seed(i, home, center.Code, def.Code, home, home);

            // A-08 — 와이어의 NpcId 는 전역 인스턴스 id 다. 이 픽스처는 슬롯 i 에
            // id i+1 을 앉힌다 (0 은 "없음" 이라 쓸 수 없다).
            store.Bind(i, i + 1);
            store.StepStatus[i] = (byte)StepStatus.Ready;
            executor.AssignPlan(i, new PlanId(fallbackOf[def.Code.Value]));
        }

        while (bands.Rebalance() > 0)
        {
            // 초기 배치는 측정 밖에서 끝낸다.
        }

        var loop = new NpcServerLoop(
            link, clock, applier, interrupts,
            new CognitionScheduler(store, bands, plans) { Snapshots = snapshots },
            executor, swapper, bands, queue)
        {
            Transition = transition,
        };

        return new Rig(
            loop, store, clock, plans, swapper, transition, zones, interrupts, executor, queue, link, applier);
    }

    private static PoiId FirstPoiIn(ZoneId zone)
    {
        foreach (PoiDef poi in s_data.Pois.Pois)
        {
            if (poi.Zone.Value == zone.Value && poi.Subtype == "house")
            {
                return poi.Code;
            }
        }

        Assert.Fail($"존 {zone.Value} 에 house POI 가 없다.");
        return default;
    }

    /// <summary>
    /// 존 이벤트 하나. <b>시퀀스는 링크에서 받아 온다</b> —
    /// 링크가 내보내는 완료 이벤트와 번호를 공유하지 않으면 <c>EventApplier</c> 가
    /// 되돌아간 시퀀스를 재전송으로 보고 통째로 버린다 (N7).
    /// </summary>
    private static GameEvent ZoneEvent(Rig rig, ZoneId zone, RegionState state) => new()
    {
        Kind = GameEventKind.ZoneStateChanged,
        Sequence = rig.Link.NextSequence(),
        OccurredAt = rig.Clock.Current,
        Zone = zone,
        Code = (byte)state,
    };

    /// <summary>이벤트를 넣고 한 틱을 돈다. 실제 루프와 같은 순서다 (배수 → 틱).</summary>
    private static void Inject(Rig rig, in GameEvent ev)
    {
        rig.Applier.Apply(in ev);
        rig.Transition.Observe(in ev, rig.Clock);
        rig.Interrupts.HandleZone(in ev, rig.Clock.Current, rig.Executor, rig.Link, rig.Queue);
    }

    /// <summary>
    /// 틱을 돈다. 매 틱 앞서 나간 명령을 완료로 되돌린다 —
    /// 응답이 없으면 스텝이 타임아웃으로 실패하고 <c>on_step_fail: fallback</c> 이
    /// 방금 갈아탄 버킷 플랜을 아키타입 폴백으로 되돌려 버린다.
    /// </summary>
    private static void Run(Rig rig, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            Assert.True(rig.Clock.Step(out Tick tick));

            rig.Link.Drain(tick);

            while (rig.Link.Events.TryRead(out GameEvent done))
            {
                rig.Applier.Apply(in done);
            }

            rig.Loop.RunTick(tick);
        }
    }

    private static string GoalOf(Rig rig, int npc)
    {
        rig.Plans.TryPeekFor(npc, rig.Store.PlanId[npc], out CompiledPlan plan);
        return plan.Goal;
    }

    private static string ArchetypeOf(Rig rig, int npc) =>
        s_data.Archetypes[new ArchetypeId(rig.Store.ArchetypeCode[npc])].Id;

    /// <summary>아키타입별 대표 플랜 (goal + 액션 시퀀스).</summary>
    private static Dictionary<string, string> PlansByArchetype(Rig rig)
    {
        var byArchetype = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int npc = 0; npc < rig.Store.Count; npc++)
        {
            string archetype = ArchetypeOf(rig, npc);

            if (byArchetype.ContainsKey(archetype))
            {
                continue;
            }

            rig.Plans.TryPeekFor(npc, rig.Store.PlanId[npc], out CompiledPlan plan);

            byArchetype[archetype] =
                $"{plan.Goal}: {string.Join(">", plan.Steps.Select(s => s_data.ActionName(s.Action)))}";
        }

        return byArchetype;
    }

    // ── 항목 1 ────────────────────────────────────────────────────

    /// <summary>
    /// <c>ZoneStateChanged(War)</c> 수신 → 인터럽트 경로로 <b>1틱 내</b> 즉시 반응.
    ///
    /// 존 이벤트는 NPC 를 지목하지 않으므로(<c>ev.Npc = 0</c>) 존 전원을 평가해야 한다 —
    /// 그러지 않으면 마을이 War 인데 0번 NPC 만 반응한다 (T4-23).
    /// </summary>
    [Fact]
    public void Siege_WarTriggersInterruptWithinOneTick()
    {
        Rig rig = NewRig();

        Assert.True(s_data.Zones.TryGet("town_center", out ZoneDef center));
        Run(rig, 10);

        long before = rig.Interrupts.Forced;
        int commandsBefore = rig.Link.Commands.Count;

        GameEvent war = ZoneEvent(rig, center.Code, RegionState.War);
        Inject(rig, in war);

        // 인터럽트는 이벤트 배수 구간에서 즉시 발행한다 — 틱을 더 돌리지 않는다.
        Assert.True(
            rig.Interrupts.Forced > before,
            "War 를 받았는데 인터럽트가 한 건도 발동하지 않았다.");

        Assert.True(rig.Link.Commands.Count > commandsBefore, "즉시 명령이 나가지 않았다.");

        // 경비병은 근무 중이고 전투 가능하므로 defend_post_under_attack 이 걸린다.
        // 그 규칙의 then 은 Defend($gate) 다 (masterdata/interrupts.json).
        Assert.Contains(
            rig.Link.Commands.Skip(commandsBefore),
            c => c.Priority == CommandPriority.Critical);

        // 존 전원이 평가됐다 — 한 마리만 반응하면 그건 결선이 빠진 것이다.
        Assert.True(rig.Interrupts.Forced - before > 1, $"인터럽트가 {rig.Interrupts.Forced - before} 건뿐이다.");
    }

    // ── 항목 2 ────────────────────────────────────────────────────

    /// <summary>
    /// 버킷 키가 <c>*.War.*</c> 로 전환 → <b>3초(30틱) 내</b> 마을 전체 플랜 스왑 완료.
    ///
    /// 시간대 전환의 지터 폭(±300 = 60초 창)으로는 이 요구를 만족할 수 없다.
    /// 존 이벤트는 <see cref="BucketTransition.ZoneJitterSpread"/>(±14 = 2.9초 창)를 쓴다.
    /// </summary>
    [Fact]
    [Trait("Category", "Gate")]   // 프리베이크된 War 버킷이 있어야 판정된다 (아래 클래스 주석)
    public void Siege_TownSwapsWithinThreeSeconds()
    {
        Rig rig = NewRig();

        Assert.True(s_data.Zones.TryGet("town_center", out ZoneDef center));
        Run(rig, 10);

        GameEvent war = ZoneEvent(rig, center.Code, RegionState.War);
        Inject(rig, in war);

        Assert.Equal(RegionState.War, rig.Zones.RegionOf(center.Code));
        Assert.Equal(rig.Store.Count, rig.Transition.Pending);

        // 3초 = 30틱. 지터 창이 29틱이라 그 안에 전부 처리된다.
        Assert.True(
            BucketTransition.ZoneJitterWindow <= 30,
            $"존 지터 창이 {BucketTransition.ZoneJitterWindow} 틱이다. 3초(30틱) 안이어야 한다.");

        Run(rig, 30);

        Assert.False(rig.Transition.InProgress, $"30틱 뒤에도 예약 {rig.Transition.Pending} 건이 남았다.");
        Assert.True(rig.Transition.Swapped > 0, "War 로 바뀌었는데 아무 플랜도 갈아타지 않았다.");
    }

    // ── 항목 3 ────────────────────────────────────────────────────

    /// <summary>
    /// 캐시 미스 버킷만 LLM 호출 대상이 된다 — 전량 재생성이 아니다.
    ///
    /// T2 공급원(<c>BucketReplanSource</c>)은 <b>조회됐고 미생성인</b> 버킷만 집어 온다.
    /// 이미 채워진 버킷은 절대 일감으로 올라오지 않는다.
    /// </summary>
    [Fact]
    [Trait("Category", "Gate")]   // 프리베이크된 War 버킷이 있어야 판정된다 (아래 클래스 주석)
    public void Siege_OnlyMissedBucketsBecomeLlmWork()
    {
        Rig rig = NewRig();

        Assert.True(s_data.Zones.TryGet("town_center", out ZoneDef center));
        Run(rig, 10);

        GameEvent war = ZoneEvent(rig, center.Code, RegionState.War);
        Inject(rig, in war);
        Run(rig, 30);

        var source = new Npc.Host.Replan.BucketReplanSource(rig.Plans, s_data);

        int wanted = source.Depth;
        Assert.True(wanted > 0, "미스 버킷이 하나도 없다 — 표본이 이상하다.");

        // 전량(2,880)이 아니라 실제 조회된 미스만이다.
        Assert.True(wanted < TestPaths.TotalKeys, $"미스 버킷이 {wanted} 건 — 전량 재생성과 다를 바 없다.");

        // 집어 온 일감은 전부 미생성 버킷이다.
        var taken = new List<BucketKey>();

        while (source.TryTake(new Tick(100), out Npc.Host.Replan.ReplanJob job))
        {
            Assert.False(rig.Plans.HasBucket(job.Bucket), $"{job.Bucket} 은 이미 채워져 있다.");
            Assert.Equal(Npc.Llm.PlanQuality.Archetype, job.Quality);
            taken.Add(job.Bucket);
        }

        Assert.Equal(wanted, taken.Count);
        Assert.Equal(taken.Count, taken.Distinct().Count());

        // 채워진 버킷이 실제로 있고 그중 아무것도 일감이 아니다.
        Assert.True(rig.Plans.FilledBuckets > 0, "프리베이크된 버킷이 하나도 없다.");
    }

    // ── 항목 4 ────────────────────────────────────────────────────

    /// <summary>전환 구간 틱 p99 ≤ 40ms (평시 20ms 의 2배).</summary>
    [Fact]
    [Trait("Category", "Load")]
    public void Siege_TransitionTickP99UnderForty()
    {
        Rig rig = NewRig(perArchetype: 600);   // 1,800마리를 한 존에 몰아 넣는다

        Assert.True(s_data.Zones.TryGet("town_center", out ZoneDef center));

        // 워밍업은 시간대 경계를 넘지 않게 짧게 잡는다 — 넘으면 전원 예약(±300)이 걸려
        // 존 전환의 창을 재는 것이 아니라 시간대 전환을 재게 된다.
        Run(rig, 50);

        GameEvent war = ZoneEvent(rig, center.Code, RegionState.War);
        Inject(rig, in war);

        var samples = new double[BucketTransition.ZoneJitterWindow];
        double perMs = Stopwatch.Frequency / 1_000.0;

        for (int i = 0; i < samples.Length; i++)
        {
            Assert.True(rig.Clock.Step(out Tick tick));

            long start = Stopwatch.GetTimestamp();
            rig.Loop.RunTick(tick);
            samples[i] = (Stopwatch.GetTimestamp() - start) / perMs;
        }

        Array.Sort(samples);
        double p99 = samples[Math.Clamp((int)Math.Ceiling(0.99 * samples.Length) - 1, 0, samples.Length - 1)];

        Assert.True(p99 <= 40, $"전환 구간 틱 p99 {p99:F3}ms — 40ms 를 넘는다.");
        Assert.False(rig.Transition.InProgress, "전환이 창 안에 끝나지 않았다.");
    }

    // ── 항목 5 ────────────────────────────────────────────────────

    /// <summary>
    /// <b>guard·farmer·blacksmith 가 서로 다른 행동으로 전환된다.</b>
    /// 전부 똑같이 도망가면 LLM 을 쓴 의미가 없다 (docs/14 §7).
    /// </summary>
    [Fact]
    [Trait("Category", "Gate")]   // 프리베이크된 War 버킷이 있어야 판정된다 (아래 클래스 주석)
    public void Siege_ArchetypesDivergeUnderWar()
    {
        Rig rig = NewRig();

        Assert.True(s_data.Zones.TryGet("town_center", out ZoneDef center));
        Run(rig, 10);

        Dictionary<string, string> peace = PlansByArchetype(rig);

        GameEvent war = ZoneEvent(rig, center.Code, RegionState.War);
        Inject(rig, in war);
        Run(rig, 40);

        Dictionary<string, string> underWar = PlansByArchetype(rig);

        Assert.Equal(Watched.Length, underWar.Count);

        // 1) 평시와 다른 플랜으로 바뀌었다 — 적어도 프리베이크된 버킷이 있는 아키타입은.
        int changed = Watched.Count(a => peace[a] != underWar[a]);

        Assert.True(
            changed > 0,
            "War 인데 아무 아키타입도 플랜이 바뀌지 않았다.\n"
            + string.Join("\n", Watched.Select(a => $"  {a}: {peace[a]} -> {underWar[a]}")));

        // 2) 서로 다른 행동이다 — 셋이 같은 플랜이면 아키타입 차원이 값을 못 사고 있는 것이다.
        int distinct = underWar.Values.Distinct(StringComparer.Ordinal).Count();

        Assert.True(
            distinct >= 2,
            "War 에서 세 아키타입이 전부 같은 플랜이다.\n"
            + string.Join("\n", underWar.Select(kv => $"  {kv.Key}: {kv.Value}")));

        // 3) 경비병은 방어로 간다 — 이게 "아키타입별로 다르게" 의 알맹이다.
        Assert.True(
            underWar["town_guard"].Contains("Guard", StringComparison.Ordinal)
            || underWar["town_guard"].Contains("Defend", StringComparison.Ordinal)
            || underWar["town_guard"].Contains("Attack", StringComparison.Ordinal),
            $"경비병이 방어 행동을 하지 않는다: {underWar["town_guard"]}");
    }

    // ── 항목 6 ────────────────────────────────────────────────────

    /// <summary>Peace 복귀 시 원래 플랜으로 복원된다.</summary>
    [Fact]
    [Trait("Category", "Gate")]   // 프리베이크된 War 버킷이 있어야 판정된다 (아래 클래스 주석)
    public void Siege_RestoresPlansOnPeace()
    {
        Rig rig = NewRig();

        Assert.True(s_data.Zones.TryGet("town_center", out ZoneDef center));
        Run(rig, 5);

        // 기준은 "평시 버킷 플랜으로 도는 마을" 이다. 기동 직후에는 전원이 아키타입 폴백을
        // 들고 있고(Seed 가 그렇게 배정한다) 첫 버킷 전환은 시간대 경계에서야 온다 —
        // 그 상태를 "원래 플랜" 이라고 부르면 복원 판정이 폴백으로 돌아가는지를 보게 된다.
        rig.Transition.ScheduleZone(
            new Tick(rig.Clock.Current.Value + 1), rig.Clock.TimeOfDay, center.Code);

        Run(rig, 30);

        Dictionary<string, string> before = PlansByArchetype(rig);

        GameEvent war = ZoneEvent(rig, center.Code, RegionState.War);
        Inject(rig, in war);
        Run(rig, 40);

        Dictionary<string, string> underWar = PlansByArchetype(rig);
        Assert.NotEqual(before, underWar);

        GameEvent peace = ZoneEvent(rig, center.Code, RegionState.Peace);
        Inject(rig, in peace);
        Run(rig, 40);

        Dictionary<string, string> restored = PlansByArchetype(rig);

        Assert.Equal(RegionState.Peace, rig.Zones.RegionOf(center.Code));
        Assert.Equal(before, restored);
    }

    // ── 시나리오 파일 ─────────────────────────────────────────────

    /// <summary><c>scenarios/siege.jsonl</c> 이 파싱되고 시나리오 B 의 계기를 다 담고 있다.</summary>
    [Fact]
    public void Siege_ScenarioFileCoversTheChecklist()
    {
        ScenarioRunner runner = ScenarioRunner.Load(TestPaths.At("scenarios", "siege.jsonl"), s_data);

        Assert.NotEmpty(runner.Steps);

        // 틱 오름차순이어야 ±0틱 주입이 성립한다 (docs/02 §5).
        for (int i = 1; i < runner.Steps.Length; i++)
        {
            Assert.True(runner.Steps[i - 1].AtTick <= runner.Steps[i].AtTick);
        }

        Assert.Contains(runner.Steps, s => s.Kind == GameEventKind.ZoneStateChanged
            && s.Code == (byte)RegionState.War);
        Assert.Contains(runner.Steps, s => s.Kind == GameEventKind.ZoneStateChanged
            && s.Code == (byte)RegionState.Peace);
        Assert.Contains(runner.Steps, s => s.Kind == GameEventKind.WeatherChanged);

        // 시나리오 C 와 같은 킬스위치도 들어 있다. T5-08 이 타깃을 문자열에서 열거형으로
        // 바꿨다 — 오타가 지나가면 안 끊긴 채로 게이트가 통과하기 때문이다 (docs/15 §4).
        Assert.Contains(runner.Steps, s => s.KillSwitch == KillSwitchTarget.T1);
        Assert.Contains(runner.Steps, s => s.KillSwitch == KillSwitchTarget.T2);

        // War 는 Peace 복귀보다 먼저다.
        long war = runner.Steps.First(s => s.Code == (byte)RegionState.War).AtTick;
        long peace = runner.Steps.Last(s => s.Kind == GameEventKind.ZoneStateChanged
            && s.Code == (byte)RegionState.Peace).AtTick;

        Assert.True(war < peace, "War 가 Peace 복귀보다 늦다.");
    }
}
