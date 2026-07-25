using System.Threading.Channels;
using Npc.Contracts;
using Npc.Gateway;
using Npc.MasterData;
using Npc.Sim;
using Npc.Tests.Sim;

namespace Npc.Tests.Gateway;

/// <summary>docs/02 §2, §5 · N8. 역압이 이 링크의 존재 이유다.</summary>
public sealed class LoopbackLinkTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static NpcCommand Command(CommandPriority priority, int npc = 0, uint correlation = 1) => new()
    {
        Kind = NpcCommandKind.SetVisualState,
        Npc = new NpcId(npc),
        IssuedAt = new Tick(1),
        Correlation = new CorrelationId(correlation),
        Priority = priority,
        Visual = VisualState.Idle,
    };

    /// <summary>T1-42 완료 조건 — Critical 은 무손실, Cosmetic 부터 드롭.</summary>
    [Fact]
    public async Task Link_Backpressure()
    {
        var dispatched = new List<NpcCommand>();
        var events = Channel.CreateUnbounded<GameEvent>();

        await using var link = new LoopbackGameServerLink(
            (in NpcCommand c, Tick t) => dispatched.Add(c),
            events.Reader,
            capacity: 10);

        // Cosmetic 으로 큐를 채운다.
        for (int i = 0; i < 10; i++)
        {
            NpcCommand cosmetic = Command(CommandPriority.Cosmetic, i);
            link.Enqueue(in cosmetic);
        }

        Assert.Equal(10, link.Pending);
        Assert.Equal(0, link.Stats.CommandsDropped);

        // Critical 을 밀어넣으면 Cosmetic 이 밀려난다.
        for (int i = 0; i < 5; i++)
        {
            NpcCommand critical = Command(CommandPriority.Critical, 100 + i);
            link.Enqueue(in critical);
        }

        Assert.Equal(10, link.Pending);
        Assert.Equal(5, link.Stats.CommandsDropped);

        await link.FlushAsync(CancellationToken.None);

        // Critical 5개가 전부 살아 있고 먼저 나간다.
        Assert.Equal(5, dispatched.Count(c => c.Priority == CommandPriority.Critical));
        Assert.All(dispatched.Take(5), c => Assert.Equal(CommandPriority.Critical, c.Priority));
        Assert.Equal(5, dispatched.Count(c => c.Priority == CommandPriority.Cosmetic));
    }

    /// <summary>포화 상태에서 자기보다 낮은 우선순위가 없으면 자기를 버린다 (Critical 제외).</summary>
    [Fact]
    public async Task Link_DropsItselfWhenNothingLowerToEvict()
    {
        var events = Channel.CreateUnbounded<GameEvent>();

        await using var link = new LoopbackGameServerLink(
            (in NpcCommand c, Tick t) => { },
            events.Reader,
            capacity: 4);

        for (int i = 0; i < 4; i++)
        {
            NpcCommand critical = Command(CommandPriority.Critical, i);
            link.Enqueue(in critical);
        }

        NpcCommand cosmetic = Command(CommandPriority.Cosmetic, 99);
        link.Enqueue(in cosmetic);

        Assert.Equal(4, link.Pending);
        Assert.Equal(1, link.Stats.CommandsDropped);
    }

    [Fact]
    public async Task Link_EnqueueDoesNotAllocate()
    {
        var events = Channel.CreateUnbounded<GameEvent>();

        await using var link = new LoopbackGameServerLink(
            (in NpcCommand c, Tick t) => { },
            events.Reader,
            capacity: 1_024);

        NpcCommand command = Command(CommandPriority.Normal);

        for (int i = 0; i < 30_000; i++)
        {
            link.Enqueue(in command);

            if (link.Pending >= 1_000)
            {
                await link.FlushAsync(CancellationToken.None);
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 10_000; i++)
        {
            link.Enqueue(in command);

            if (link.Pending >= 1_000)
            {
                _ = link.FlushAsync(CancellationToken.None);
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>SimWorld 에 직결하면 명령이 이벤트로 돌아온다.</summary>
    [Fact]
    public async Task Link_RoundTripsThroughSimWorld()
    {
        SimWorld world = SimWorldTests.NewWorld(4);
        var movement = new MovementSim(world);
        world.Handler = movement.TryHandle;

        await using var link = new LoopbackGameServerLink(world.ApplyCommand, world.Events);

        NpcCommand spawn = SimWorldTests.Command(NpcCommandKind.Spawn, 0);
        link.Enqueue(in spawn);

        Assert.Equal(0, world.CommandsHandled);

        await link.FlushAsync(CancellationToken.None);

        Assert.Equal(1, world.CommandsHandled);
        Assert.True(link.Events.TryRead(out GameEvent ev));
        Assert.Equal(GameEventKind.NpcSpawned, ev.Kind);
        Assert.Equal(1, link.Stats.CommandsFlushed);
    }

    /// <summary>N8 — 배치가 기본이다. Flush 전에는 아무것도 나가지 않는다.</summary>
    [Fact]
    public async Task Link_BatchesUntilFlush()
    {
        int dispatched = 0;
        var events = Channel.CreateUnbounded<GameEvent>();

        await using var link = new LoopbackGameServerLink(
            (in NpcCommand c, Tick t) => dispatched++,
            events.Reader);

        for (int i = 0; i < 100; i++)
        {
            NpcCommand command = Command(CommandPriority.Normal, i);
            link.Enqueue(in command);
        }

        Assert.Equal(0, dispatched);
        Assert.Equal(100, link.Pending);

        await link.FlushAsync(CancellationToken.None);

        Assert.Equal(100, dispatched);
        Assert.Equal(0, link.Pending);
    }
}
