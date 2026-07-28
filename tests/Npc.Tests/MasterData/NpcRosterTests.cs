using System.Collections.Immutable;
using Npc.Contracts;
using Npc.MasterData;

namespace Npc.Tests.MasterData;

/// <summary>
/// docs/20 §10 — NPC 서버와 게임서버가 같은 NPC 집합을 보는가.
///
/// <b>이 규칙이 갈리면 첨자 7번이 서로 다른 NPC 가 된다.</b> 대장장이에게 밭을 갈라고
/// 명령하게 되고, 증상은 "가끔 이상하게 행동한다" 로 나타나 원인을 찾기가 매우 어렵다.
/// </summary>
public sealed class NpcRosterTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static readonly NpcInstanceTable s_instances = NpcInstanceTable.Load(
        Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

    /// <summary>
    /// <b>기존 공식과 원소 단위로 같다</b> (T6-11 완료 조건).
    ///
    /// 추출은 리팩터링이지 재설계가 아니다 — 결과가 한 마리라도 달라지면 프리베이크·리플레이
    /// 산출물의 첨자가 전부 어긋난다.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(500)]
    [InlineData(5000)]
    public void Roster_SelectionMatchesLegacyFormula(int npcs)
    {
        // Npc.Host/Program.cs 에 인라인으로 있던 그대로.
        var legacy = new NpcInstanceDef[npcs];

        for (int i = 0; i < npcs; i++)
        {
            legacy[i] = s_instances[(int)((long)i * s_instances.Count / npcs)];
        }

        NpcRoster roster = NpcRoster.Select(s_instances, npcs);

        Assert.Equal(npcs, roster.Count);

        for (int i = 0; i < npcs; i++)
        {
            Assert.Equal(legacy[i], roster.Npcs[i]);
        }
    }

    /// <summary>
    /// 해시가 차이를 잡는다 (T6-11 완료 조건). 이 값이 핸드셰이크에 실리고,
    /// 다르면 연결이 거절된다 (docs/20 §5.5).
    /// </summary>
    [Fact]
    public void Roster_HashDetectsDifference()
    {
        NpcRoster a = NpcRoster.Select(s_instances, 500);
        NpcRoster b = NpcRoster.Select(s_instances, 500);
        NpcRoster fewer = NpcRoster.Select(s_instances, 499);

        // 같은 입력 → 같은 해시. 결정론적이다.
        Assert.Equal(a.Hash, b.Hash);
        Assert.Equal(64, a.Hash.Length);
        Assert.DoesNotContain(a.Hash, char.IsUpper);

        // 수가 다르면 다른 집합이다.
        Assert.NotEqual(a.Hash, fewer.Hash);

        // <b>순서만 달라도 다르다.</b> 같은 NPC 를 다른 순서로 뽑으면 첨자가 어긋나므로
        // 집합이 아니라 배열의 해시여야 한다.
        ImmutableArray<NpcInstanceDef> reversed = [.. a.Npcs.Reverse()];

        Assert.NotEqual(a.Hash, NpcRoster.HashOf(reversed));
    }

    /// <summary>
    /// 좌표는 해시에 넣지 않는다. 게임서버가 스폰 위치를 조정해도 같은 NPC 이고,
    /// 그것 때문에 거절되면 <b>우회 옵션을 만들고 싶어진다</b> — 그게 이 검사를 무력화하는 길이다.
    /// </summary>
    [Fact]
    public void Roster_HashIgnoresSpawnPosition()
    {
        NpcRoster roster = NpcRoster.Select(s_instances, 50);

        ImmutableArray<NpcInstanceDef> moved =
            [.. roster.Npcs.Select(n => n with { Spawn = new WorldPos(1, 2, 3) })];

        Assert.Equal(roster.Hash, NpcRoster.HashOf(moved));
    }

    /// <summary>존 필터를 주면 그 존만 남기고 같은 균등 간격을 적용한다.</summary>
    [Fact]
    public void Roster_ZoneFilterKeepsOnlyThatZone()
    {
        Assert.True(s_data.Zones.TryGet("town_center", out ZoneDef center));

        ZoneId zone = center.Code;
        NpcRoster filtered = NpcRoster.Select(s_instances, 5_000, [zone]);

        Assert.NotEmpty(filtered.Npcs);
        Assert.All(filtered.Npcs, n => Assert.Equal(zone, n.Zone));

        // 그 존의 전원보다 많이 달라고 해도 있는 만큼만 준다.
        Assert.True(filtered.Count < s_instances.Count);

        // 필터가 다르면 해시도 다르다 — 존을 나눠 붙일 때 서로를 구별한다.
        Assert.NotEqual(NpcRoster.Select(s_instances, 5_000).Hash, filtered.Hash);
    }

    /// <summary>전체보다 많이 달라고 해도 전체까지만 준다. 예외가 아니다.</summary>
    [Fact]
    public void Roster_CapsAtAvailableCount()
    {
        NpcRoster roster = NpcRoster.Select(s_instances, s_instances.Count * 2);

        Assert.Equal(s_instances.Count, roster.Count);
    }
}
