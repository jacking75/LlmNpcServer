using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.Llm;
using Npc.MasterData;

namespace Npc.Tests.Llm;

/// <summary>
/// T2-07 — 서픽스 토큰 예산. docs/12 §3.
///
/// <b>이 테스트가 없으면 서픽스는 반드시 자란다.</b> 필드가 하나씩 붙다가 어느 순간
/// 800토큰이 되고 prefill 시간이 3배가 된다. 프리픽스와 달리 서픽스는 매번 prefill 되므로
/// 그대로 지연이다.
/// </summary>
public sealed class SuffixBudgetTests
{
    /// <summary>docs/12 §3 · CLAUDE.md §2.5 의 서픽스 상한.</summary>
    private const int Budget = 300;

    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    [Fact]
    public void Suffix_NeverExceeds300Tokens()
    {
        var worst = new Dictionary<string, (int Tokens, string Where)>(StringComparer.Ordinal);

        for (int index = 0; index < TestPaths.TotalKeys; index++)
        {
            BucketKey bucket = BucketKey.FromIndex(index);

            foreach ((string name, PlanRequest request) in StressCases(bucket))
            {
                int tokens = PromptPrefix.CountTokens(PlanRequestSuffix.Build(request, s_data));

                if (!worst.TryGetValue(name, out (int Tokens, string Where) current) || tokens > current.Tokens)
                {
                    worst[name] = (tokens, bucket.ToString());
                }
            }
        }

        string report = string.Join(
            " · ", worst.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => $"{kv.Key}={kv.Value.Tokens}@{kv.Value.Where}"));

        Assert.True(worst.Values.Max(v => v.Tokens) <= Budget, $"예산 {Budget} 초과. 케이스별 최대: {report}");
    }

    [Fact]
    public void Suffix_NeverDropsSituationFields()
    {
        // 예산을 맞추려고 덜어내는 것은 개체 부가정보(recent · outcome · inventory)와,
        // 최후에 성향·목표뿐이다. 상황·플래그·허용 액션은 유효성을 결정하므로 언제나 남는다.
        foreach ((string name, PlanRequest request) in
            StressCases(BucketKey.FromIndex(16 * BucketKey.PerArchetype)).Concat(StressCases(BucketKey.FromIndex((TestPaths.ArchetypeCount - 1) * BucketKey.PerArchetype))))
        {
            string suffix = PlanRequestSuffix.Build(request, s_data);

            foreach (string key in new[]
            {
                "archetype", "allowed_actions", "time_of_day", "region_state", "climate", "flags",
            })
            {
                Assert.Contains($"\"{key}\"", suffix, StringComparison.Ordinal);
            }

            Assert.True(PromptPrefix.CountTokens(suffix) <= Budget, name);
        }
    }

    [Fact]
    public void Suffix_TrimsOnlyWhenOverBudget()
    {
        // 평범한 요청은 덜어내지 않는다 — 덜어내기가 상시 동작하면 개체 정보가 조용히 사라진다.
        Assert.True(s_data.Items.TryGet("coal", out ItemDef coal));

        var request = new PlanRequest(
            BucketKey.FromIndex(0),
            s_data.InitialFlags(BucketKey.FromIndex(0)),
            PlanQuality.Individual,
            new NpcSnapshot(
                [new InventorySlot(coal.Code, 3)],
                [new RecentEvent(GameEventKind.PlayerInteracted, PoiSymbol.None, 90)],
                PlanOutcome.Completed));

        string suffix = PlanRequestSuffix.Build(request, s_data);

        Assert.Contains("\"recent\"", suffix, StringComparison.Ordinal);
        Assert.Contains("\"last_plan_outcome\"", suffix, StringComparison.Ordinal);
        Assert.Contains("\"inventory\"", suffix, StringComparison.Ordinal);
        Assert.Contains("\"traits\"", suffix, StringComparison.Ordinal);
        Assert.Contains("\"goals\"", suffix, StringComparison.Ordinal);
    }

    [Fact]
    public void Suffix_CoversEveryBucket()
    {
        // 표본이 아니라 전수다. 개수는 masterdata 가 정한다 (F-05).
        Assert.Equal(s_data.Archetypes.Count * BucketKey.PerArchetype, s_data.Buckets.TotalKeys);
        Assert.Equal(TestPaths.TotalKeys, s_data.Buckets.TotalKeys);
    }

    /// <summary>버킷 하나에 대한 스트레스 스냅샷들. 전부 최악값으로 채운다.</summary>
    private static IEnumerable<(string Name, PlanRequest Request)> StressCases(BucketKey bucket)
    {
        // (1) 아키타입 요청 — 실제 초기 플래그.
        yield return ("archetype", new PlanRequest(bucket, s_data.InitialFlags(bucket)));

        // (2) 아키타입 요청 — 동시에 설 수 있는 플래그가 전부 선 최악값.
        yield return ("max-flags", new PlanRequest(bucket, s_maxFlags));

        // (3) 개별 요청 — 인벤 만재 · recent 만재 · 직전 실패.
        yield return ("individual-stress", new PlanRequest(
            bucket, s_maxFlags, PlanQuality.Individual, FullSnapshot()));

        // (4) 재시도 — 가장 긴 실패 설명이 붙은 경우 (T2-15).
        yield return ("retry", new PlanRequest(
            bucket,
            s_maxFlags,
            PlanQuality.Individual,
            FullSnapshot(),
            WorstExplanation()));
    }

    /// <summary>
    /// 한 NPC 에게 동시에 설 수 있는 플래그의 최대 집합.
    ///
    /// 42개 전부를 켜는 것은 스트레스가 아니라 있을 수 없는 상태다. 배타 관계는 셋이다:
    /// <list type="bullet">
    ///   <item>시간 4종 — <c>world_flags.json</c> 의 <c>exclusive_groups.time</c></item>
    ///   <item>장소 8종 — <c>MoveTo</c> 가 전부 내리고 도착이 하나만 세운다 (docs/01 §2.2)</item>
    ///   <item>지역 2종 — <c>context_buckets.json</c> 이 region_state 마다 하나만 매핑한다</item>
    /// </list>
    /// 각 그룹에서 <b>가장 이름이 긴 것</b>을 남긴다 — 토큰이 가장 많이 드는 조합이다.
    /// </summary>
    private static readonly WorldFlags s_maxFlags = BuildMaxFlags();

    private static WorldFlags BuildMaxFlags()
    {
        WorldFlags time = WorldFlags.IsDawn | WorldFlags.IsDay | WorldFlags.IsEvening | WorldFlags.IsNight;
        WorldFlags location =
            WorldFlags.AtHome | WorldFlags.AtWorkplace | WorldFlags.AtMarket | WorldFlags.AtTavern
            | WorldFlags.AtTemple | WorldFlags.AtGate | WorldFlags.AtField | WorldFlags.InWilderness;
        WorldFlags region = WorldFlags.RegionPeaceful | WorldFlags.RegionUnderAttack;

        return (WorldFlagTable.All & ~(time | location | region))
            | WorldFlags.IsEvening          // 시간 4종 중 가장 긴 이름
            | WorldFlags.InWilderness       // 장소 8종 중 가장 긴 이름
            | WorldFlags.RegionUnderAttack; // 지역 2종 중 가장 긴 이름
    }

    /// <summary>인벤토리 전 품목 만재 + recent 만재.</summary>
    private static NpcSnapshot FullSnapshot()
    {
        var inventory = ImmutableArray.CreateBuilder<InventorySlot>(s_data.Items.Items.Length);

        foreach (ItemDef item in s_data.Items.Items)
        {
            inventory.Add(new InventorySlot(item.Code, item.Stack));
        }

        var recent = ImmutableArray.CreateBuilder<RecentEvent>(32);

        for (int i = 0; i < 32; i++)
        {
            recent.Add(new RecentEvent(
                (GameEventKind)((i % 16) + 1),
                (PoiSymbol)(i % (PoiSymbols.Count + 1)),
                (byte)(255 - i)));
        }

        // D-03 — 관계 밴드도 최악에 포함한다. 가장 긴 표기가 friendly 다.
        return new NpcSnapshot(
            inventory.ToImmutable(), recent.ToImmutable(), PlanOutcome.FailedAtStep, 9,
            RelationshipBand.Friendly);
    }

    /// <summary>
    /// 검증기가 만들 수 있는 가장 긴 설명. 3단은 실패 시점의 상태를 통째로 펴서 넣으므로
    /// 플래그 42개가 전부 선 상태가 최악이다.
    /// </summary>
    private static ValidationResult WorstExplanation()
    {
        string longest = string.Empty;

        foreach (ActionDef action in s_data.Actions.Actions)
        {
            string detail =
                $"{action.Id} requires {WorldFlagTable.Format(WorldFlagTable.All)} but no preceding step grants it. "
                + $"State before this step: {WorldFlagTable.Format(WorldFlagTable.All)}.";

            if (detail.Length > longest.Length)
            {
                longest = detail;
            }
        }

        return ValidationResult.Fail(ValidationStage.Coherence, "V3.PRECONDITION_UNMET", 9, longest);
    }
}
