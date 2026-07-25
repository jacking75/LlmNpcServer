using Npc.Contracts;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Runtime;

namespace Npc.Tests.Plan;

/// <summary>docs/03 §2 · docs/11 §12. 심볼 바인딩과 분산.</summary>
public sealed class PoiBinderTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);
    private static readonly PoiBinder s_binder = new(s_data.Pois);

    private static PoiBindContext Context(int npcId, string archetypeId)
    {
        Assert.True(s_data.Archetypes.TryGet(archetypeId, out ArchetypeDef archetype));

        PoiId home = s_data.Pois.OfSubtype("house")[0];
        PoiId work = archetype.WorkplacePoiType is { } subtype && s_data.Pois.OfSubtype(subtype).Length > 0
            ? s_data.Pois.OfSubtype(subtype)[0]
            : default;

        return new PoiBindContext(new NpcId(npcId), archetype.Code, home, work, home);
    }

    [Fact]
    public void PoiSymbols_ParsesEveryAllowedSymbol()
    {
        foreach (PoiSymbol symbol in Enum.GetValues<PoiSymbol>())
        {
            if (symbol == PoiSymbol.None)
            {
                continue;
            }

            string text = PoiSymbols.ToText(symbol);

            Assert.StartsWith("$", text, StringComparison.Ordinal);
            Assert.True(PoiSymbols.TryParse(text, out PoiSymbol parsed));
            Assert.Equal(symbol, parsed);
        }

        Assert.Equal(PoiSymbols.Count, Enum.GetValues<PoiSymbol>().Length - 1);
    }

    [Fact]
    public void PoiSymbols_RejectsInventedSymbol()
    {
        Assert.False(PoiSymbols.TryParse("$smithy_01", out PoiSymbol symbol));
        Assert.Equal(PoiSymbol.None, symbol);
        Assert.False(PoiSymbols.TryParse(null, out _));
    }

    [Fact]
    public void PoiBinder_BindsInstanceSymbols()
    {
        PoiBindContext ctx = Context(1, "blacksmith");

        Assert.True(s_binder.TryBind(PoiSymbol.Home, ctx, out PoiId home));
        Assert.Equal(ctx.Home, home);

        Assert.True(s_binder.TryBind(PoiSymbol.Workplace, ctx, out PoiId work));
        Assert.Equal(ctx.Workplace, work);
    }

    [Fact]
    public void PoiBinder_MissingWorkplaceFailsToBind()
    {
        // villager 는 일터가 없다.
        PoiBindContext ctx = Context(1, "villager");

        Assert.Equal(default, ctx.Workplace);
        Assert.False(s_binder.TryBind(PoiSymbol.Workplace, ctx, out _));
    }

    /// <summary>T1-24 완료 조건 — 동일 조건 NPC 100마리가 2개 이상 POI 로 분산된다.</summary>
    [Fact]
    public void PoiBinder_DistributesNearest()
    {
        foreach (PoiSymbol symbol in new[]
        {
            PoiSymbol.Market, PoiSymbol.Tavern, PoiSymbol.Temple,
            PoiSymbol.Gate, PoiSymbol.NearestField, PoiSymbol.NearestSafe, PoiSymbol.NearestShelter,
        })
        {
            var chosen = new HashSet<PoiId>();

            for (int npc = 1; npc <= 100; npc++)
            {
                PoiBindContext ctx = Context(npc, "farmer");

                Assert.True(s_binder.TryBind(symbol, ctx, out PoiId poi), $"{symbol} 바인딩에 실패했다.");
                chosen.Add(poi);
            }

            Assert.True(chosen.Count >= 2, $"{symbol}: NPC 100마리가 전부 {chosen.Count}개 POI 로 몰렸다.");
        }
    }

    /// <summary>같은 NPC 는 언제나 같은 POI 로 바인딩된다 — 난수가 아니라 해시다.</summary>
    [Fact]
    public void PoiBinder_IsDeterministic()
    {
        PoiBindContext ctx = Context(4_242, "miner");

        Assert.True(s_binder.TryBind(PoiSymbol.NearestField, ctx, out PoiId first));

        for (int i = 0; i < 50; i++)
        {
            Assert.True(s_binder.TryBind(PoiSymbol.NearestField, ctx, out PoiId again));
            Assert.Equal(first, again);
        }

        // 바인더를 새로 만들어도 같아야 한다.
        var fresh = new PoiBinder(s_data.Pois);
        Assert.True(fresh.TryBind(PoiSymbol.NearestField, ctx, out PoiId fromFresh));
        Assert.Equal(first, fromFresh);
    }

    /// <summary>아키타입이 접근할 수 없는 field 는 고르지 않는다.</summary>
    [Fact]
    public void PoiBinder_RespectsArchetypeAccess()
    {
        Assert.True(s_data.Archetypes.TryGet("miner", out ArchetypeDef miner));

        for (int npc = 1; npc <= 50; npc++)
        {
            PoiBindContext ctx = Context(npc, "miner");

            Assert.True(s_binder.TryBind(PoiSymbol.NearestField, ctx, out PoiId poi));
            Assert.True(s_data.Pois[poi].Allows(miner.Code));
            Assert.Equal("mine", s_data.Pois[poi].Subtype);
        }
    }

    /// <summary>고른 POI 가 후보 중 가까운 축에 들어야 한다 — 지터가 지구 반대편을 고르면 안 된다.</summary>
    [Fact]
    public void PoiBinder_StaysAmongNearestCandidates()
    {
        PoiBindContext ctx = Context(77, "merchant");

        Assert.True(s_binder.TryBind(PoiSymbol.Market, ctx, out PoiId chosen));

        float chosenDistance = s_data.Pois.Distance(ctx.Current, chosen);
        float[] all = s_data.Pois.OfType(PoiType.Market)
            .Select(p => s_data.Pois.Distance(ctx.Current, p))
            .Order()
            .ToArray();

        Assert.True(
            chosenDistance <= all[Math.Min(PoiBinder.CandidateCount - 1, all.Length - 1)],
            $"고른 거리 {chosenDistance} 가 상위 {PoiBinder.CandidateCount} 후보 밖이다.");
    }

    [Fact]
    public void PoiBinder_BindDoesNotAllocate()
    {
        PoiBindContext ctx = Context(9, "farmer");

        // JIT 티어 승격이 끝날 때까지 돌린다. 승격 시점에 한 번 할당이 잡힌다.
        for (int i = 0; i < 30_000; i++)
        {
            _ = s_binder.TryBind(PoiSymbol.NearestField, ctx, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            _ = s_binder.TryBind(PoiSymbol.NearestField, ctx, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void PlanHash_JitterIsDeterministicAndBounded()
    {
        for (int npc = 1; npc <= 500; npc++)
        {
            int jitter = PlanHash.Jitter(new NpcId(npc), salt: 7, span: 300);

            Assert.InRange(jitter, -300, 300);
            Assert.Equal(jitter, PlanHash.Jitter(new NpcId(npc), salt: 7, span: 300));
        }

        // 씨앗이 다르면 값이 달라야 한다 (전원이 같은 지터를 받으면 분산 효과가 없다).
        var seen = new HashSet<int>();
        for (int npc = 1; npc <= 500; npc++)
        {
            seen.Add(PlanHash.Jitter(new NpcId(npc), salt: 7, span: 300));
        }

        Assert.True(seen.Count > 100, $"지터가 {seen.Count}종밖에 안 나온다.");
    }
}
