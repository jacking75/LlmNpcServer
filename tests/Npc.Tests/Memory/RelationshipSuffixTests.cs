using Npc.Core;
using Npc.Core.Plan;
using Npc.Llm;
using Npc.MasterData;

namespace Npc.Tests.Memory;

/// <summary>
/// D-03 — 관계 밴드가 서픽스에 실리는 방식.
///
/// <para>
/// <b>서픽스에 들어가는 것은 enum 하나다.</b> 호감도 원값·상호작용 횟수·플레이어 id 는
/// 들어가지 않는다 — 앞의 둘은 모델이 플랜에 되쓰려 하고, 마지막은 CLAUDE.md §2.5 위반이다.
/// </para>
/// </summary>
public sealed class RelationshipSuffixTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>밴드가 있으면 소문자 enum 한 값으로 실린다.</summary>
    [Theory]
    [InlineData(RelationshipBand.Hostile, "hostile")]
    [InlineData(RelationshipBand.Neutral, "neutral")]
    [InlineData(RelationshipBand.Friendly, "friendly")]
    public void Band_RidesAsAnEnum(RelationshipBand band, string expected)
    {
        string suffix = Build(band);

        Assert.Contains($"\"relationship_band\":\"{expected}\"", suffix, StringComparison.Ordinal);
    }

    /// <summary>기록이 없으면 아무것도 싣지 않는다 — "모른다" 를 적는 데 토큰을 쓰지 않는다.</summary>
    [Fact]
    public void UnknownBand_IsNotWritten()
    {
        Assert.DoesNotContain("relationship_band", Build(RelationshipBand.Unknown), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>밴드는 4토큰이다.</b> 예산을 재는 테스트가 따로 있지만, 여기서는 "밴드를 넣어도
    /// 서픽스가 거의 안 자란다" 는 것 자체를 지킨다 — 자라기 시작하면 그때가 설계가 바뀐 때다.
    /// </summary>
    [Fact]
    public void Band_CostsAlmostNothing()
    {
        int without = PromptPrefix.CountTokens(Build(RelationshipBand.Unknown));
        int with = PromptPrefix.CountTokens(Build(RelationshipBand.Friendly));

        Assert.InRange(with - without, 1, 8);
        Assert.True(with <= PlanRequestSuffix.TokenBudget, $"밴드를 넣었더니 {with} 토큰이다");
    }

    /// <summary>
    /// <b>플레이어 id 도 호감도 원값도 서픽스에 없다.</b> 밴드를 만든 수치가 새어 나가면
    /// 그 순간 3단 압축의 이유가 없어진다.
    /// </summary>
    [Fact]
    public void Suffix_LeaksNeitherPlayerNorAffinity()
    {
        string suffix = Build(RelationshipBand.Friendly);

        Assert.DoesNotContain("player", suffix, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("affinity", suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Build(RelationshipBand band)
    {
        ArchetypeDef archetype = s_data.Archetypes.Archetypes[0];

        var request = new PlanRequest(
            new BucketKey(archetype.Code, TimeOfDay.Noon, RegionState.Peace, Climate.Fair),
            WorldFlags.IsDay,
            PlanQuality.Individual,
            new NpcSnapshot([], [], Band: band));

        return PlanRequestSuffix.Build(request, s_data);
    }
}
