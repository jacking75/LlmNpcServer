using System.Collections.Immutable;
using Npc.Core;
using Npc.Llm;
using Npc.MasterData;

namespace Npc.Tests.Llm;

/// <summary>T2-03 — 액션 카탈로그 렌더러. docs/01 §10.1 · docs/12 §8.</summary>
public sealed class CatalogRendererTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);
    private static readonly string s_rendered = CatalogRenderer.Render(s_data);

    [Fact]
    public void Catalog_ContainsEveryAction()
    {
        ImmutableArray<ActionDef> actions = s_data.Actions.Actions;

        Assert.Equal(37, actions.Length);

        foreach (ActionDef action in actions)
        {
            Assert.Contains($"## {action.Id}", s_rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Catalog_HasFiveFlagColumnsForEveryAction()
    {
        // docs/12 §8 의 V3.PRECONDITION_UNMET 처방 — requires/requires_any/forbids/grants/clears 를 표로.
        Assert.Contains(
            "| action | requires | requires_any | forbids | grants | clears |",
            s_rendered,
            StringComparison.Ordinal);

        string[] lines = s_rendered.Split('\n');

        foreach (ActionDef action in s_data.Actions.Actions)
        {
            string? row = lines.FirstOrDefault(l => l.StartsWith($"| {action.Id} |", StringComparison.Ordinal));

            Assert.NotNull(row);

            // "| id | requires | requires_any | forbids | grants | clears |" → 파이프 7개.
            Assert.Equal(7, row.Count(c => c == '|'));

            string[] cells = [.. row.Split('|', StringSplitOptions.TrimEntries)];

            AssertFlagCell(cells[2], action.Requires);
            AssertFlagCell(cells[3], action.RequiresAny);
            AssertFlagCell(cells[4], action.Forbids);
            AssertFlagCell(cells[5], action.Grants);
            AssertFlagCell(cells[6], action.Clears);
        }
    }

    [Fact]
    public void Catalog_ContainsEveryRecipeWithInputs()
    {
        // V3.RESOURCE_IMBALANCE 처방. 레시피 입출력이 없으면 모델이 수량을 맞출 수 없다.
        foreach (RecipeDef recipe in s_data.Items.Recipes)
        {
            Assert.Contains($"- `{recipe.Id}`:", s_rendered, StringComparison.Ordinal);
        }

        Assert.Contains(
            "- `iron_sword`: 2 iron_ore + 1 coal -> 1 iron_sword at smithy",
            s_rendered,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_ListsEveryWorldFlag()
    {
        foreach (string name in WorldFlagTable.Names)
        {
            Assert.Contains(name, s_rendered, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Catalog_IsDeterministic_Across100Renders()
    {
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(s_rendered, CatalogRenderer.Render(s_data), StringComparer.Ordinal);
        }
    }

    [Fact]
    public void Catalog_UsesLineFeedOnly()
    {
        // CRLF 가 섞이면 체크아웃 설정이 다른 기계에서 프리픽스 SHA 가 달라진다.
        Assert.DoesNotContain('\r', s_rendered);
    }

    private static void AssertFlagCell(string cell, WorldFlags expected)
    {
        if (expected == WorldFlags.None)
        {
            Assert.Equal("-", cell);
            return;
        }

        string[] names = [.. cell.Split(',', StringSplitOptions.TrimEntries)];

        WorldFlags parsed = WorldFlags.None;
        foreach (string name in names)
        {
            Assert.True(WorldFlagTable.TryParse(name, out WorldFlags flag), name);
            parsed |= flag;
        }

        Assert.Equal(expected, parsed);
    }
}
