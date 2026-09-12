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

    /// <summary>2026-09-12 시점에 프리베이크된 플랜 2,880개가 깔고 있는 배치.</summary>
    private static readonly (int Bit, string Id)[] s_frozen =
    [
        ( 0, nameof(WorldFlags.AtHome)),
        ( 1, nameof(WorldFlags.AtWorkplace)),
        ( 2, nameof(WorldFlags.AtMarket)),
        ( 3, nameof(WorldFlags.AtTavern)),
        ( 4, nameof(WorldFlags.AtTemple)),
        ( 5, nameof(WorldFlags.AtGate)),
        ( 6, nameof(WorldFlags.AtField)),
        ( 7, nameof(WorldFlags.InWilderness)),
        ( 8, nameof(WorldFlags.HasFood)),
        ( 9, nameof(WorldFlags.HasWater)),
        (10, nameof(WorldFlags.HasTool)),
        (11, nameof(WorldFlags.HasRawMaterial)),
        (12, nameof(WorldFlags.HasProduct)),
        (13, nameof(WorldFlags.HasCoin)),
        (14, nameof(WorldFlags.InventoryFull)),
        (15, nameof(WorldFlags.HasWeapon)),
        (16, nameof(WorldFlags.IsRested)),
        (17, nameof(WorldFlags.IsHungry)),
        (18, nameof(WorldFlags.IsThirsty)),
        (19, nameof(WorldFlags.IsInjured)),
        (20, nameof(WorldFlags.IsExhausted)),
        (21, nameof(WorldFlags.IsSleeping)),
        (24, nameof(WorldFlags.IsDawn)),
        (25, nameof(WorldFlags.IsDay)),
        (26, nameof(WorldFlags.IsEvening)),
        (27, nameof(WorldFlags.IsNight)),
        (28, nameof(WorldFlags.HasCustomer)),
        (29, nameof(WorldFlags.HasCompanion)),
        (30, nameof(WorldFlags.IsAlone)),
        (31, nameof(WorldFlags.OnDuty)),
        (32, nameof(WorldFlags.ShopOpen)),
        (33, nameof(WorldFlags.MarketOpen)),
        (34, nameof(WorldFlags.GatesOpen)),
        (35, nameof(WorldFlags.RegionPeaceful)),
        (36, nameof(WorldFlags.RegionUnderAttack)),
        (37, nameof(WorldFlags.WeatherHarsh)),
        (38, nameof(WorldFlags.ResourceDepleted)),
        (39, nameof(WorldFlags.PathBlocked)),
        (40, nameof(WorldFlags.ThreatNearby)),
        (41, nameof(WorldFlags.PlayerNearby)),
        (42, nameof(WorldFlags.AllyNearby)),
        (43, nameof(WorldFlags.InCombat)),
        (44, nameof(WorldFlags.HostilePlayerNearby)),
    ];

    /// <summary>
    /// <b>bit 번호 동결.</b> CLAUDE.md §2.4 — code 와 bit 는 절대 재배치하지 않는다.
    ///
    /// <para>
    /// 재배치는 <b>컴파일도 되고 검증도 통과한다.</b> 생성기는 범위(0~63)와 중복만 보고
    /// <see cref="WorldFlags_EnumMatchesJsonExactly"/> 는 JSON 과 enum 이 <i>서로</i> 맞는지만 본다 —
    /// 둘이 나란히 움직이면 아무도 모른다. 그 상태로 돌면 프리베이크된 플랜이 조용히 다른
    /// 전제조건을 들고 실행된다.
    /// </para>
    ///
    /// <para>
    /// <b>추가는 이 표를 깨지 않는다.</b> 새 플래그는 뒤에 붙고 여기에 줄을 더하는 것은 선택이다.
    /// 깨지는 경우는 기존 번호가 <b>움직였거나 사라졌을 때</b>뿐이고, 그것이 잡으려는 것이다.
    /// 액션 쪽 대응물은 <c>ActionsDataTests.ActionsJson_SpikeCodesAreStable</c> 다.
    /// </para>
    /// </summary>
    [Fact]
    public void WorldFlags_BitsAreFrozen()
    {
        Dictionary<string, int> actual = JsonFlags().ToDictionary(f => f.Id, f => f.Bit, StringComparer.Ordinal);
        List<string> moved = [];

        foreach ((int bit, string id) in s_frozen)
        {
            if (!actual.TryGetValue(id, out int now))
            {
                moved.Add($"{id}: bit {bit} 이었는데 사라졌다");
            }
            else if (now != bit)
            {
                moved.Add($"{id}: bit {bit} → {now}");
            }
        }

        Assert.True(
            moved.Count == 0,
            "bit 이 움직였다. 프리베이크된 플랜이 전부 무효다 — " + string.Join(" / ", moved));
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
