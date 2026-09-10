using Npc.Contracts;
using Npc.Core;
using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>C-07 — 개체 스필오버 서브 쿼터. PRODUCTION_ROADMAP §6 C-07.</summary>
public sealed class SpilloverQuotaTests
{
    [Fact]
    public void TicksPerDay_MatchesTheBudget()
    {
        // 두 계정의 하루가 갈리면 서브 쿼터가 총 캡과 다른 주기로 돈다.
        Assert.Equal(ReplanBudget.TicksPerDay, SpilloverQuota.TicksPerDay);
    }

    [Fact]
    public void IndividualCap_IsTheShareOfTheDailyCap()
    {
        var quota = new SpilloverQuota(0.20, dailyTokenCap: 1_000_000);

        Assert.Equal(200_000, quota.IndividualCap);
    }

    [Fact]
    public void Individual_StopsAtTheSubQuota()
    {
        var quota = new SpilloverQuota(0.20, dailyTokenCap: 1_000);

        // 200 토큰까지다.
        Assert.True(quota.TryUseT2(ReplanAccount.Individual, 150, new Tick(1)));
        Assert.True(quota.TryUseT2(ReplanAccount.Individual, 50, new Tick(2)));

        // 넘으면 거절이 아니라 T1 대기다 — 호출부가 티어를 내린다.
        Assert.False(quota.TryUseT2(ReplanAccount.Individual, 1, new Tick(3)));
        Assert.Equal(1, quota.IndividualDeferred);
        Assert.Equal(200, quota.IndividualTokensToday);
    }

    [Fact]
    public void Bucket_KeepsGoingAfterIndividualIsExhausted()
    {
        var quota = new SpilloverQuota(0.20, dailyTokenCap: 1_000);

        Assert.False(quota.TryUseT2(ReplanAccount.Individual, 500, new Tick(1)));

        // 버킷 플랜은 수천 NPC 가 공유한다. 개체가 예산을 태웠다고 그것까지 굶으면
        // 서브 쿼터를 둔 이유가 사라진다.
        Assert.True(quota.TryUseT2(ReplanAccount.Bucket, 900, new Tick(2)));
        Assert.Equal(900, quota.BucketTokensToday);
    }

    [Fact]
    public void ZeroShare_MeansIndividualNeverUsesT2()
    {
        var quota = new SpilloverQuota(0, dailyTokenCap: 1_000);

        Assert.False(quota.TryUseT2(ReplanAccount.Individual, 1, new Tick(1)));
        Assert.True(quota.TryUseT2(ReplanAccount.Bucket, 1, new Tick(1)));
    }

    [Fact]
    public void Rollover_ResetsBothAccounts()
    {
        var quota = new SpilloverQuota(0.20, dailyTokenCap: 1_000);

        quota.TryUseT2(ReplanAccount.Individual, 200, new Tick(1));
        quota.TryUseT2(ReplanAccount.Bucket, 500, new Tick(1));

        Assert.False(quota.TryUseT2(ReplanAccount.Individual, 1, new Tick(1)));

        // 다음 날.
        var tomorrow = new Tick(SpilloverQuota.TicksPerDay + 1);

        Assert.True(quota.TryUseT2(ReplanAccount.Individual, 200, tomorrow));
        Assert.Equal(200, quota.IndividualTokensToday);
        Assert.Equal(0, quota.BucketTokensToday);
    }

    [Fact]
    public void Share_IsValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpilloverQuota(-0.1, 1_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpilloverQuota(1.1, 1_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpilloverQuota(0.2, -1));
    }
}
