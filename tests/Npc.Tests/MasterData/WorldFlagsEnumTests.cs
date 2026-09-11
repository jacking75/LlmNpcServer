using System.Text.Json;
using Npc.Core;

namespace Npc.Tests.MasterData;

/// <summary>
/// 생성된 WorldFlags enum 이 masterdata/world_flags.json 과 일치하는지 확인한다. docs/01 §1.
/// 손으로 쓴 enum 을 두지 않는 이유가 이것이다 — 둘이 어긋나면 플랜 검증이 조용히 틀린 답을 낸다.
/// </summary>
public sealed class WorldFlagsEnumTests
{
    private static (int Bit, string Id)[] JsonFlags()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestPaths.MasterData, "world_flags.json")));

        return doc.RootElement.GetProperty("flags").EnumerateArray()
            .Select(e => (e.GetProperty("bit").GetInt32(), e.GetProperty("id").GetString()!))
            .OrderBy(f => f.Item1)
            .ToArray();
    }

    /// <summary>docs/01 §1 이 예시로 든 값들. 여기가 밀리면 프리베이크 플랜이 전부 무효다.</summary>
    [Theory]
    [InlineData(nameof(WorldFlags.AtHome), 0)]
    [InlineData(nameof(WorldFlags.AtWorkplace), 1)]
    [InlineData(nameof(WorldFlags.HasFood), 8)]
    [InlineData(nameof(WorldFlags.IsSleeping), 21)]
    [InlineData(nameof(WorldFlags.InCombat), 43)]
    public void WorldFlags_SampleBitsMatchSpec(string name, int bit)
    {
        WorldFlags expected = (WorldFlags)(1UL << bit);

        Assert.True(WorldFlagTable.TryParse(name, out WorldFlags actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WorldFlags_EnumMatchesJsonExactly()
    {
        (int Bit, string Id)[] expected = JsonFlags();

        Assert.Equal(WorldFlagTable.Count, expected.Length);
        Assert.Equal(expected.Select(f => f.Id).ToArray(), WorldFlagTable.Names);

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal((WorldFlags)(1UL << expected[i].Bit), WorldFlagTable.Values[i]);
        }
    }

    [Fact]
    public void WorldFlags_AllMaskCoversEveryDefinedBit()
    {
        WorldFlags or = WorldFlags.None;
        foreach (WorldFlags v in WorldFlagTable.Values)
        {
            or |= v;
        }

        Assert.Equal(WorldFlagTable.All, or);
        // 예약 구간(22~23, 45~63)은 비어 있어야 한다. B-06 이 44 를 가져갔다.
        Assert.Equal(WorldFlags.None, WorldFlagTable.All & (WorldFlags)0xFFFF_E000_00C0_0000UL);
    }

    [Fact]
    public void WorldFlags_TryParseRejectsUnknownId()
    {
        Assert.False(WorldFlagTable.TryParse("NoSuchFlag", out WorldFlags flag));
        Assert.Equal(WorldFlags.None, flag);
    }

    [Fact]
    public void WorldFlags_FormatUsesNamesNotBitmask()
    {
        string text = WorldFlagTable.Format(WorldFlags.AtHome | WorldFlags.HasFood);

        Assert.Equal("AtHome|HasFood", text);
        Assert.Equal("None", WorldFlagTable.Format(WorldFlags.None));
    }

    /// <summary>docs/01 §1 의 이탈 판정. 인지 스캔의 핵심 연산이다.</summary>
    [Fact]
    public void WorldFlags_DeviationCheckIsSingleBitOperation()
    {
        WorldFlags cur = WorldFlags.AtWorkplace | WorldFlags.HasTool;
        WorldFlags required = WorldFlags.AtWorkplace | WorldFlags.HasTool;
        WorldFlags forbidden = WorldFlags.IsSleeping;

        Assert.False(IsDeviated(cur, required, forbidden));
        Assert.True(IsDeviated(cur & ~WorldFlags.HasTool, required, forbidden));
        Assert.True(IsDeviated(cur | WorldFlags.IsSleeping, required, forbidden));
    }

    private static bool IsDeviated(WorldFlags cur, WorldFlags required, WorldFlags forbidden)
        => (required & ~cur) != 0 || (forbidden & cur) != 0;
}
