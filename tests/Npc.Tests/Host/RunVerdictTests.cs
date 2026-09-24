using Npc.Host.Metrics;

namespace Npc.Tests.Host;

public sealed class RunVerdictTests
{
    [Fact]
    public void P99_AtBudgetIsNormal_ButOverBudgetIsFlagged()
    {
        Assert.Null(RunVerdict.FirstIssue(20.0, 0, 0, 0, 0, 0, 0, 100, true));
        Assert.NotNull(RunVerdict.FirstIssue(20.1, 0, 0, 0, 0, 0, 0, 100, true));
    }

    [Fact]
    public void TickAllocationIsFlagged()
    {
        Assert.NotNull(RunVerdict.FirstIssue(1, 1, 0, 0, 0, 0, 0, 100, true));
    }
}
