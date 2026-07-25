using Npc.Contracts;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>docs/02 §3.4. 스왑 뒤에 도착한 낡은 응답을 무시해야 플랜이 어긋나지 않는다.</summary>
public sealed class CorrelationTableTests
{
    [Fact]
    public void Correlation_IdsAreMonotonicAndNeverZero()
    {
        var table = new CorrelationTable(4);
        var seen = new HashSet<uint>();

        for (int i = 0; i < 1_000; i++)
        {
            CorrelationId id = table.Next(i % 4);

            Assert.NotEqual(CorrelationTable.None, id);
            Assert.True(seen.Add(id.Value), $"상관 ID {id.Value} 가 중복 발급됐다.");
        }

        Assert.Equal(1_000, table.Issued);
    }

    /// <summary>T1-31 완료 조건 — 스왑 뒤 도착한 응답은 무시된다.</summary>
    [Fact]
    public void Correlation_StaleResponseIgnored()
    {
        var table = new CorrelationTable(8);
        const int Npc = 3;

        CorrelationId issued = table.Next(Npc);
        Assert.True(table.IsCurrent(Npc, issued));
        Assert.False(table.IsStale(Npc, issued));

        // 플랜을 스왑했다 — 진행 중이던 명령의 응답은 이제 낡은 것이다.
        table.Invalidate(Npc);

        Assert.False(table.IsCurrent(Npc, issued));
        Assert.True(table.IsStale(Npc, issued));
        Assert.Equal(CorrelationTable.None, table.Current(Npc));

        // 새 명령을 내면 그것만 유효하다.
        CorrelationId fresh = table.Next(Npc);

        Assert.True(table.IsCurrent(Npc, fresh));
        Assert.True(table.IsStale(Npc, issued));
    }

    [Fact]
    public void Correlation_IsPerNpc()
    {
        var table = new CorrelationTable(8);

        CorrelationId a = table.Next(1);
        CorrelationId b = table.Next(2);

        Assert.True(table.IsCurrent(1, a));
        Assert.True(table.IsCurrent(2, b));
        Assert.False(table.IsCurrent(1, b));
        Assert.False(table.IsCurrent(2, a));

        table.Invalidate(1);

        Assert.False(table.IsCurrent(1, a));
        Assert.True(table.IsCurrent(2, b));
    }

    [Fact]
    public void Correlation_DefaultIdIsNeverCurrent()
    {
        var table = new CorrelationTable(4);

        // 상관 없는 이벤트(NpcTransform 등)는 Correlation = default 다 (docs/02 §3.4).
        Assert.False(table.IsCurrent(0, default));
        Assert.True(table.IsStale(0, default));
    }

    [Fact]
    public void Correlation_NextDoesNotAllocate()
    {
        var table = new CorrelationTable(16);

        // JIT 티어 승격이 끝날 때까지 돌린다.
        for (int i = 0; i < 30_000; i++)
        {
            table.Next(i % 16);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            table.Next(i % 16);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Correlation_ResetClearsEverything()
    {
        var table = new CorrelationTable(4);
        CorrelationId id = table.Next(0);

        table.Reset();

        Assert.Equal(CorrelationTable.None, table.Current(0));
        Assert.True(table.IsStale(0, id));
        Assert.Equal(0, table.Issued);
    }
}
