using System.Collections.Immutable;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.Llm;
using Npc.MasterData;

namespace Npc.Tests.Llm;

/// <summary>T2-06 — 서픽스 조립기. docs/12 §3 의 두 예시를 재현한다.</summary>
public sealed class PlanRequestSuffixTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static BucketKey Blacksmith(TimeOfDay time, RegionState region, Climate climate)
    {
        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef archetype));
        return new BucketKey(archetype.Code, time, region, climate);
    }

    [Fact]
    public void Suffix_ReproducesArchetypeExample()
    {
        // docs/12 §3 의 "아키타입 플랜 요청 (캐시 대상)".
        var request = new PlanRequest(
            Blacksmith(TimeOfDay.Evening, RegionState.War, Climate.Cold),
            WorldFlags.AtWorkplace | WorldFlags.HasTool | WorldFlags.IsHungry
                | WorldFlags.RegionUnderAttack | WorldFlags.WeatherHarsh);

        JsonElement json = RequestObjectOf(PlanRequestSuffix.Build(request, s_data));

        Assert.Equal(
            """
            {"archetype":"blacksmith","time_of_day":"Evening","region_state":"War","climate":"Cold","flags":["AtWorkplace","HasTool","IsHungry","RegionUnderAttack","WeatherHarsh"],"traits":{"diligence":80,"sociability":40,"courage":55,"greed":50},"goals":["restock_ore","fulfill_orders","maintain_shop"]}
            """,
            json.GetRawText());
    }

    [Fact]
    public void Suffix_ReproducesIndividualExample()
    {
        // docs/12 §3 의 "개별 NPC 재계획 요청".
        // recent 의 표기만 사양의 예시와 다르다 — 사양은 "ResourceDepleted@mine" 같은 자유 문자열을
        // 썼지만, 플레이어 문자열이 프롬프트에 새는 길을 막기 위해 강타입 열거값만 싣는다
        // (CLAUDE.md §2.5 · docs/12 §3 주석).
        Assert.True(s_data.Items.TryGet("coal", out ItemDef coal));
        Assert.True(s_data.Items.TryGet("coin", out ItemDef coin));

        var snapshot = new NpcSnapshot(
            [new InventorySlot(coal.Code, 3), new InventorySlot(coin.Code, 45)],
            [
                new RecentEvent(GameEventKind.NpcActionFailed, PoiSymbol.NearestField, 90),
                new RecentEvent(GameEventKind.PlayerInteracted, PoiSymbol.None, 70),
                new RecentEvent(GameEventKind.DamageTaken, PoiSymbol.None, 60),
                new RecentEvent(GameEventKind.NpcArrived, PoiSymbol.Home, 10),
            ],
            PlanOutcome.FailedAtStep,
            1);

        var request = new PlanRequest(
            Blacksmith(TimeOfDay.Evening, RegionState.War, Climate.Cold),
            WorldFlags.AtWorkplace | WorldFlags.IsHungry | WorldFlags.IsExhausted
                | WorldFlags.RegionUnderAttack | WorldFlags.ResourceDepleted,
            PlanQuality.Individual,
            snapshot);

        JsonElement json = RequestObjectOf(PlanRequestSuffix.Build(request, s_data));

        Assert.Equal("blacksmith", json.GetProperty("archetype").GetString());
        Assert.Equal(
            ["AtWorkplace", "IsHungry", "IsExhausted", "RegionUnderAttack", "ResourceDepleted"],
            json.GetProperty("flags").EnumerateArray().Select(e => e.GetString()!).ToArray());

        // 0 이 아닌 항목만, ItemId 오름차순.
        Assert.Equal(
            """{"coal":3,"coin":45}""",
            json.GetProperty("inventory").GetRawText());

        // salience 상위 3개만. 네 번째(NpcArrived, 10)는 잘린다.
        Assert.Equal(
            ["NpcActionFailed@$nearest_field", "PlayerInteracted", "DamageTaken"],
            json.GetProperty("recent").EnumerateArray().Select(e => e.GetString()!).ToArray());

        Assert.Equal("failed_at_step_1", json.GetProperty("last_plan_outcome").GetString());
    }

    [Fact]
    public void Suffix_OmitsZeroInventoryAndFalseFlags()
    {
        Assert.True(s_data.Items.TryGet("coal", out ItemDef coal));
        Assert.True(s_data.Items.TryGet("bread", out ItemDef bread));

        var request = new PlanRequest(
            Blacksmith(TimeOfDay.Dawn, RegionState.Peace, Climate.Fair),
            WorldFlags.AtHome,
            PlanQuality.Individual,
            new NpcSnapshot([new InventorySlot(coal.Code, 0), new InventorySlot(bread.Code, 2)], []));

        JsonElement json = RequestObjectOf(PlanRequestSuffix.Build(request, s_data));

        Assert.Equal(["AtHome"], json.GetProperty("flags").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal("""{"bread":2}""", json.GetProperty("inventory").GetRawText());
        Assert.False(json.TryGetProperty("recent", out _));
        Assert.False(json.TryGetProperty("last_plan_outcome", out _));
    }

    [Fact]
    public void Suffix_KeepsFullKeyNames()
    {
        // 키를 줄이면 토큰은 줄지만 모델 이해도가 떨어진다 (docs/12 §3).
        string suffix = PlanRequestSuffix.Build(
            new PlanRequest(Blacksmith(TimeOfDay.Noon, RegionState.Peace, Climate.Fair), WorldFlags.AtWorkplace),
            s_data);

        foreach (string key in new[] { "archetype", "time_of_day", "region_state", "climate", "flags", "traits", "goals" })
        {
            Assert.Contains($"\"{key}\"", suffix, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Suffix_AppendsPreviousFailureBlockOnRetry()
    {
        ValidationResult failure = ValidationResult.Fail(
            ValidationStage.Coherence, "V3.PRECONDITION_UNMET", 3,
            "Craft requires HasRawMaterial but no preceding step grants it.");

        string suffix = PlanRequestSuffix.Build(
            new PlanRequest(
                Blacksmith(TimeOfDay.Noon, RegionState.Peace, Climate.Fair),
                WorldFlags.AtWorkplace,
                PlanQuality.Archetype,
                Individual: null,
                PreviousFailure: failure),
            s_data);

        Assert.Contains(CoherenceValidator.Explain(failure), suffix, StringComparison.Ordinal);
        Assert.Contains("V3.PRECONDITION_UNMET", suffix, StringComparison.Ordinal);

        // 통과한 결과를 넣으면 블록이 붙지 않는다.
        string clean = PlanRequestSuffix.Build(
            new PlanRequest(
                Blacksmith(TimeOfDay.Noon, RegionState.Peace, Climate.Fair),
                WorldFlags.AtWorkplace,
                PlanQuality.Archetype,
                Individual: null,
                PreviousFailure: ValidationResult.Ok),
            s_data);

        Assert.DoesNotContain("PREVIOUS ATTEMPT", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void Suffix_IsDeterministic()
    {
        var request = new PlanRequest(
            Blacksmith(TimeOfDay.Evening, RegionState.War, Climate.Cold),
            WorldFlags.AtWorkplace | WorldFlags.HasTool);

        string first = PlanRequestSuffix.Build(request, s_data);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(first, PlanRequestSuffix.Build(request, s_data), StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 서픽스 본문에서 첫 번째 JSON 객체(REQUEST)만 떼어낸다.
    /// 재시도 서픽스에는 뒤에 previous_attempt_failed 블록이 하나 더 붙으므로 균형을 세어 자른다.
    /// </summary>
    internal static JsonElement RequestObjectOf(string suffix)
    {
        int start = suffix.IndexOf('{', StringComparison.Ordinal);
        Assert.True(start >= 0, suffix);

        int depth = 0;
        bool inString = false;

        for (int i = start; i < suffix.Length; i++)
        {
            char c = suffix[i];

            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;

                case '{':
                    depth++;
                    break;

                case '}':
                    if (--depth == 0)
                    {
                        return JsonDocument.Parse(suffix[start..(i + 1)]).RootElement.Clone();
                    }

                    break;

                default:
                    break;
            }
        }

        Assert.Fail("서픽스에서 REQUEST JSON 을 찾지 못했다: " + suffix);
        return default;
    }
}
