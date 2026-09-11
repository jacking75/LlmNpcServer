using System.Text.RegularExpressions;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;
using Npc.Tests.Fakes;

namespace Npc.Tests.Runtime;

/// <summary>docs/11 §5 · docs/02 §4. 틱 루프 조립.</summary>
public sealed class NpcServerLoopTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private sealed record Rig(
        NpcStore Store,
        RecordingLink Link,
        GameClock Clock,
        NpcServerLoop Loop,
        PlanStore Plans,
        PlanExecutor Executor);

    private static Rig NewRig(int npcs, int timeScale = 600)
    {
        var store = new NpcStore();
        store.Allocate(npcs, s_data.Items.MaxCode + 1);

        var link = new RecordingLink();
        var clock = new GameClock(s_data.Buckets, timeScale);
        var correlations = new CorrelationTable(npcs);
        var applier = new EventApplier(s_data, store, clock, correlations);
        PlanStore plans = PlanStore.CreateIdleOnly(s_data);
        var emitter = new CommandEmitter(s_data, new PoiBinder(s_data.Pois));
        var swapper = new PlanSwapper(store);
        var queue = new ReplanQueue(npcs);
        var executor = new PlanExecutor(s_data, store, plans, correlations, emitter, timeScale)
        {
            Swapper = swapper,
            ReplanQueue = queue,
        };
        var bands = new LodBandSet(store);
        var cognition = new CognitionScheduler(store, bands, plans);
        var interrupts = new InterruptMatcher(s_data, store);

        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));
        PoiId home = s_data.Pois.OfSubtype("house")[0];
        PoiId work = s_data.Pois.OfSubtype("smithy")[0];

        for (int i = 0; i < npcs; i++)
        {
            applier.Seed(i, home, s_data.Pois[home].Zone, smith.Code, home, work);

            // A-08 — 와이어의 NpcId 는 전역 인스턴스 id 다. 이 픽스처는 슬롯 i 에
            // id i+1 을 앉힌다 (0 은 "없음" 이라 쓸 수 없다).
            store.Bind(i, i + 1);
            store.StepStatus[i] = (byte)StepStatus.Ready;
        }

        var loop = new NpcServerLoop(
            link, clock, applier, interrupts, cognition, executor, swapper, bands, queue);

        return new Rig(store, link, clock, loop, plans, executor);
    }

    /// <summary>T1-40 완료 조건 — 루프 안의 await 은 이벤트 대기와 FlushAsync 뿐이다.</summary>
    [Fact]
    public void Loop_NoAwaitExceptFlush()
    {
        string source = File.ReadAllText(TestPaths.At("src", "Npc.Runtime", "NpcServerLoop.cs"));

        string[] allowed = ["WaitToReadAsync", "FlushAsync"];
        List<string> violations = [];

        foreach (Match match in Regex.Matches(source, @"\bawait\b[^;]*;", RegexOptions.Singleline))
        {
            if (!allowed.Any(a => match.Value.Contains(a, StringComparison.Ordinal)))
            {
                violations.Add(match.Value.Trim());
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>틱 루프에 LLM 이 없다 — Npc.Runtime 전체가 Npc.Llm 을 모른다.</summary>
    [Fact]
    public void Loop_HasNoLlmReference()
    {
        foreach (string file in Directory.EnumerateFiles(
            TestPaths.At("src", "Npc.Runtime"), "*.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("Npc.Llm", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    /// <summary>TickSync 를 받아야 틱이 진행된다 (docs/02 §3.3).</summary>
    [Fact]
    public async Task Loop_AdvancesOnlyOnTickSync()
    {
        Rig r = NewRig(10);

        r.Link.Push(new GameEvent
        {
            Kind = GameEventKind.TickSync,
            Sequence = 1,
            OccurredAt = new Tick(5),
        });
        r.Link.Complete();

        await r.Loop.RunAsync(CancellationToken.None);

        Assert.Equal(5, r.Loop.TicksProcessed);
        Assert.Equal(5, r.Clock.Current.Value);
        Assert.True(r.Link.Flushes > 0);
    }

    /// <summary>500 NPC 가 게임 1일을 완주한다. 크래시도 데드락도 없다.</summary>
    [Fact]
    public async Task Loop_FiveHundredNpcsFinishOneGameDay()
    {
        Rig r = NewRig(500);
        long ticks = r.Clock.TicksPerGameDay;

        // 하루치 TickSync 를 한 번에 밀어넣는다.
        r.Link.Push(new GameEvent
        {
            Kind = GameEventKind.TickSync,
            Sequence = 1,
            OccurredAt = new Tick(ticks),
        });
        r.Link.Complete();

        await r.Loop.RunAsync(CancellationToken.None);

        Assert.Equal(ticks, r.Loop.TicksProcessed);
        Assert.True(r.Executor.CommandsEmitted > 0, "명령이 하나도 안 나갔다.");

        // 최후 플랜은 타임아웃으로 계속 전진하므로 아무도 멈춰 있으면 안 된다.
        Assert.All(
            Enumerable.Range(0, 500),
            npc => Assert.NotEqual((byte)StepStatus.Done, r.Store.StepStatus[npc]));
    }

    [Fact]
    public async Task Loop_StopsAtRequestedTick()
    {
        Rig r = NewRig(10);
        var loop = new NpcServerLoop(
            r.Link,
            r.Clock,
            new EventApplier(s_data, r.Store, r.Clock, new CorrelationTable(10)),
            new InterruptMatcher(s_data, r.Store),
            new CognitionScheduler(r.Store, new LodBandSet(r.Store), r.Plans),
            r.Executor,
            new PlanSwapper(r.Store),
            new LodBandSet(r.Store),
            new ReplanQueue(10))
        {
            StopAtTick = 7,
        };

        r.Link.Push(new GameEvent
        {
            Kind = GameEventKind.TickSync,
            Sequence = 1,
            OccurredAt = new Tick(1_000),
        });

        await loop.RunAsync(CancellationToken.None);

        Assert.Equal(7, loop.TicksProcessed);
    }

    /// <summary>인터럽트는 이벤트 배수 구간에서 즉시 처리된다.</summary>
    [Fact]
    public async Task Loop_HandlesInterruptDuringDrain()
    {
        Rig r = NewRig(1);
        r.Store.Flags[0] |= WorldFlags.ThreatNearby;

        r.Link.Push(new GameEvent
        {
            Kind = GameEventKind.CombatStarted,
            Sequence = 1,
            OccurredAt = new Tick(1),
            Npc = new NpcId(1),   // A-08 — 슬롯 0 의 전역 id
            OtherNpc = new NpcId(9),
        });
        r.Link.Push(new GameEvent
        {
            Kind = GameEventKind.TickSync,
            Sequence = 2,
            OccurredAt = new Tick(1),
        });
        r.Link.Complete();

        await r.Loop.RunAsync(CancellationToken.None);

        Assert.Contains(r.Link.Commands, c => c.Priority == CommandPriority.Critical);
    }

    [Fact]
    public async Task Loop_ObserverSeesEveryTick()
    {
        Rig r = NewRig(5);
        var observer = new CountingObserver();

        var loop = new NpcServerLoop(
            r.Link,
            r.Clock,
            new EventApplier(s_data, r.Store, r.Clock, new CorrelationTable(5)),
            new InterruptMatcher(s_data, r.Store),
            new CognitionScheduler(r.Store, new LodBandSet(r.Store), r.Plans),
            r.Executor,
            new PlanSwapper(r.Store),
            new LodBandSet(r.Store),
            new ReplanQueue(5))
        {
            Observer = observer,
        };

        r.Link.Push(new GameEvent
        {
            Kind = GameEventKind.TickSync,
            Sequence = 1,
            OccurredAt = new Tick(12),
        });
        r.Link.Complete();

        await loop.RunAsync(CancellationToken.None);

        Assert.Equal(12, observer.Begins);
        Assert.Equal(12, observer.Ends);
    }

    private sealed class CountingObserver : ITickObserver
    {
        public int Begins { get; private set; }

        public int Ends { get; private set; }

        public void OnTickBegin(Tick tick) => Begins++;

        public void OnTickEnd(Tick tick, int scanned, int drained) => Ends++;
    }
}
