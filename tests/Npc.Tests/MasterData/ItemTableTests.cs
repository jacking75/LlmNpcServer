using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;

namespace Npc.Tests.MasterData;

/// <summary>
/// docs/01 §3. grants 가 데이터에서만 오는지 확인한다.
/// "빵을 가지면 HasFood" 를 코드에 하드코딩하면 마스터데이터가 단일 원천이 아니게 된다.
/// </summary>
public sealed class ItemTableTests
{
    private static readonly ItemTable s_table =
        ItemTable.Load(Path.Combine(TestPaths.MasterData, "items.json"));

    [Fact]
    public void ItemTable_LoadsAroundEightyItems()
    {
        Assert.InRange(s_table.Items.Length, 70, 90);
    }

    [Fact]
    public void ItemTable_CodesAreUniqueAndIndexable()
    {
        foreach (ItemDef def in s_table.Items)
        {
            Assert.Equal(def, s_table[def.Code]);
        }

        Assert.Equal(s_table.Items.Length, s_table.Items.Select(i => i.Code).Distinct().Count());
    }

    /// <summary>docs/01 §3 이 예시로 든 code. 재배치하면 프리베이크 플랜이 깨진다.</summary>
    [Theory]
    [InlineData("iron_ore", 1)]
    [InlineData("coal", 2)]
    [InlineData("iron_sword", 20)]
    [InlineData("bread", 40)]
    [InlineData("water", 41)]
    [InlineData("smith_hammer", 60)]
    [InlineData("coin", 79)]
    public void ItemTable_SpecSampleCodesAreStable(string id, int code)
    {
        Assert.True(s_table.TryGet(id, out ItemDef def));
        Assert.Equal(code, def.Code.Value);
    }

    /// <summary>
    /// T1-11 완료 조건. 인벤 변경 → 플래그 재계산이 데이터만 보고 동작한다.
    /// 테스트 안에서 아이템 id 와 플래그의 대응을 하드코딩하지 않고,
    /// items.json 이 선언한 grants 를 그대로 기대값으로 쓴다.
    /// </summary>
    [Fact]
    public void ItemTable_GrantsAreDataDriven()
    {
        int[] inventory = new int[s_table.MaxCode + 1];

        Assert.Equal(WorldFlags.None, s_table.ComputeFlags(inventory));

        // 데이터가 말하는 대로 — 코드가 아이템 이름을 알 필요가 없다.
        foreach (ItemDef def in s_table.Items)
        {
            Array.Clear(inventory);
            inventory[def.Code.Value] = 1;

            Assert.Equal(def.Grants, s_table.ComputeFlags(inventory));
        }

        // 여러 개를 동시에 가지면 OR 된다.
        Assert.True(s_table.TryGet("bread", out ItemDef bread));
        Assert.True(s_table.TryGet("water", out ItemDef water));
        Array.Clear(inventory);
        inventory[bread.Code.Value] = 2;
        inventory[water.Code.Value] = 1;

        Assert.Equal(bread.Grants | water.Grants, s_table.ComputeFlags(inventory));

        // 수량이 0 이 되면 플래그가 내려간다.
        inventory[bread.Code.Value] = 0;
        Assert.Equal(water.Grants, s_table.ComputeFlags(inventory));
    }

    [Fact]
    public void ItemTable_ComputeFlagsDoesNotAllocate()
    {
        int[] inventory = new int[s_table.MaxCode + 1];
        inventory[40] = 3;

        // 워밍업 — JIT 이 첫 호출에서 할당할 수 있다.
        for (int i = 0; i < 100; i++)
        {
            _ = s_table.ComputeFlags(inventory);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            _ = s_table.ComputeFlags(inventory);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ItemTable_UnknownFlagReferenceThrows()
    {
        const string Json = """
            {
              "version": 1,
              "items": [
                { "id": "ghost", "code": 1, "category": "raw", "grants": ["NoSuchFlag"], "stack": 1 }
              ]
            }
            """;

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => ItemTable.Parse(Json));

        Assert.Contains("NoSuchFlag", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemTable_DuplicateCodeThrows()
    {
        const string Json = """
            {
              "version": 1,
              "items": [
                { "id": "a", "code": 1, "category": "raw", "grants": [], "stack": 1 },
                { "id": "b", "code": 1, "category": "raw", "grants": [], "stack": 1 }
              ]
            }
            """;

        Assert.Throws<InvalidDataException>(() => ItemTable.Parse(Json));
    }

    [Fact]
    public void ItemTable_RecipeIdsAreAlsoItemIds()
    {
        // 플랜 DSL 의 recipe 인자는 item_ref 로 검증된다 (docs/01 §2.3 의 7종 유지).
        foreach (RecipeDef recipe in s_table.Recipes)
        {
            Assert.True(
                s_table.TryGet(recipe.Id, out _),
                $"레시피 '{recipe.Id}' 와 같은 id 의 아이템이 없다.");
        }
    }

    [Fact]
    public void ItemTable_RecipeSlotsResolveToKnownItems()
    {
        foreach (RecipeDef recipe in s_table.Recipes)
        {
            Assert.NotEmpty(recipe.Outputs);

            foreach (RecipeSlot slot in recipe.Inputs.Concat(recipe.Outputs))
            {
                Assert.NotNull(s_table[slot.Item]);
                Assert.True(slot.Count > 0);
            }
        }
    }

    [Fact]
    public void ItemTable_UnknownCodeThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => s_table[new ItemId(9999)]);
    }
}
