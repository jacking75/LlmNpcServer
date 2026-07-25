using Npc.Contracts;
using Npc.Gateway;

namespace Npc.Tests.Gateway;

/// <summary>docs/02 §2. Null 링크와 Tcp 골격.</summary>
public sealed class NullAndTcpLinkTests
{
    private static NpcCommand Command(int npc) => new()
    {
        Kind = NpcCommandKind.MoveTo,
        Npc = new NpcId(npc),
        IssuedAt = new Tick(1),
        Correlation = new CorrelationId(1),
        Priority = CommandPriority.Normal,
    };

    /// <summary>T1-41 완료 조건 — 명령 폐기 · 이벤트 없음 · Stats.CommandsEnqueued 증가.</summary>
    [Fact]
    public async Task NullLink_DiscardsCommandsAndCountsThem()
    {
        await using var link = new NullGameServerLink();

        for (int i = 0; i < 100; i++)
        {
            NpcCommand command = Command(i);
            link.Enqueue(in command);
        }

        Assert.Equal(100, link.Stats.CommandsEnqueued);
        Assert.Equal(0, link.Stats.CommandsFlushed);
        Assert.Equal(100, link.Stats.PendingCommands);

        await link.FlushAsync(CancellationToken.None);

        Assert.Equal(100, link.Stats.CommandsFlushed);
        Assert.Equal(0, link.Stats.PendingCommands);
        Assert.Equal(0, link.Stats.CommandsDropped);

        // 이벤트는 오지 않는다.
        Assert.False(link.Events.TryRead(out _));
        Assert.Equal(LinkState.Connected, link.State);
    }

    [Fact]
    public async Task NullLink_EnqueueDoesNotAllocate()
    {
        await using var link = new NullGameServerLink();
        NpcCommand command = Command(1);

        for (int i = 0; i < 30_000; i++)
        {
            link.Enqueue(in command);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            link.Enqueue(in command);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task NullLink_StateChangedIsObservable()
    {
        await using var link = new NullGameServerLink();
        LinkState? seen = null;

        link.StateChanged += s => seen = s;
        link.RaiseStateChanged(LinkState.Degraded);

        Assert.Equal(LinkState.Degraded, seen);
    }

    /// <summary>T1-45 완료 조건 — 컴파일되고 모든 전송 메서드가 NotSupportedException 이다.</summary>
    [Fact]
    public async Task TcpLink_IsSkeletonOnly()
    {
        await using var link = new TcpGameServerLink();
        NpcCommand command = Command(1);

        Assert.Throws<NotSupportedException>(() =>
        {
            NpcCommand c = command;
            link.Enqueue(in c);
        });

        await Assert.ThrowsAsync<NotSupportedException>(
            async () => await link.FlushAsync(CancellationToken.None));

        Assert.Equal(LinkState.Disconnected, link.State);
        Assert.Equal(default, link.Stats);
    }

    /// <summary>두 링크 모두 IGameServerLink 계약을 만족한다 — 교체에 코드 변경이 없다.</summary>
    [Fact]
    public void Link_ImplementationsAreInterchangeable()
    {
        IGameServerLink[] links = [new NullGameServerLink(), new TcpGameServerLink()];

        foreach (IGameServerLink link in links)
        {
            Assert.NotNull(link.Events);
            Assert.True(Enum.IsDefined(link.State));
        }
    }
}
