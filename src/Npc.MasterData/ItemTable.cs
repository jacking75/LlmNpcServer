using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npc.Contracts;
using Npc.Core;

namespace Npc.MasterData;

/// <summary>아이템 분류. items.json 의 category.</summary>
public enum ItemCategory
{
    Raw,
    Product,
    Food,
    Drink,
    Tool,
    Weapon,
    Currency,
}

/// <summary>아이템 정의. docs/01 §3.</summary>
/// <param name="Id">문자열 id. 마스터데이터·플랜 DSL 안에서만 쓴다 (패킷에는 안 실린다).</param>
/// <param name="Code">내부 코드. 인벤토리 배열의 첨자다.</param>
/// <param name="Category">분류.</param>
/// <param name="Grants">이 아이템을 하나 이상 가지고 있을 때 서는 WorldFlags.</param>
/// <param name="Stack">한 슬롯에 쌓을 수 있는 최대 수량.</param>
public sealed record ItemDef(
    string Id,
    ItemId Code,
    ItemCategory Category,
    WorldFlags Grants,
    int Stack);

/// <summary>레시피의 입력/출력 한 줄.</summary>
public readonly record struct RecipeSlot(ItemId Item, int Count);

/// <summary>제작 레시피. docs/01 §3.</summary>
/// <param name="Id">레시피 id. 산출 아이템 id 와 같다 — 플랜 DSL 의 recipe 인자가 item_ref 로 검증된다.</param>
/// <param name="WorkplaceType">이 레시피를 돌릴 수 있는 POI subtype.</param>
/// <param name="Inputs">소비 자원.</param>
/// <param name="Outputs">산출물.</param>
/// <param name="DurationSeconds">소요 시간(게임 초).</param>
public sealed record RecipeDef(
    string Id,
    string WorkplaceType,
    ImmutableArray<RecipeSlot> Inputs,
    ImmutableArray<RecipeSlot> Outputs,
    int DurationSeconds);

/// <summary>
/// items.json 의 읽기 전용 인덱스. docs/01 §3.
///
/// <b>grants 가 이 표의 핵심이다.</b> 인벤토리 변경 이벤트를 받으면
/// <see cref="ComputeFlags"/> 가 아이템의 grants 를 OR 해서 WorldFlags 를 재계산한다.
/// "빵을 가지면 HasFood" 같은 규칙이 코드에 하드코딩돼 있으면 안 된다 —
/// 그 순간 마스터데이터가 단일 원천이 아니게 된다.
/// </summary>
public sealed class ItemTable
{
    private readonly ItemDef?[] _byCode;          // 첨자 = code. 비어 있는 코드는 null
    private readonly WorldFlags[] _grantsByCode;  // 첨자 = code. ComputeFlags 의 핫패스
    private readonly Dictionary<string, ItemDef> _byId;
    private readonly Dictionary<string, RecipeDef> _recipesById;

    private ItemTable(
        ItemDef?[] byCode,
        WorldFlags[] grantsByCode,
        Dictionary<string, ItemDef> byId,
        Dictionary<string, RecipeDef> recipesById,
        ImmutableArray<ItemDef> items,
        ImmutableArray<RecipeDef> recipes)
    {
        _byCode = byCode;
        _grantsByCode = grantsByCode;
        _byId = byId;
        _recipesById = recipesById;
        Items = items;
        Recipes = recipes;
    }

    /// <summary>code 오름차순의 모든 아이템.</summary>
    public ImmutableArray<ItemDef> Items { get; }

    /// <summary>선언 순서의 모든 레시피.</summary>
    public ImmutableArray<RecipeDef> Recipes { get; }

    /// <summary>가장 큰 code. 인벤토리 배열의 길이는 이 값 + 1 이다.</summary>
    public int MaxCode => _byCode.Length - 1;

    /// <summary>code 로 조회. 정의되지 않은 code 면 예외.</summary>
    public ItemDef this[ItemId code] =>
        (uint)code.Value < (uint)_byCode.Length && _byCode[code.Value] is { } def
            ? def
            : throw new ArgumentOutOfRangeException(nameof(code), $"정의되지 않은 item code: {code.Value}");

    /// <summary>문자열 id 로 조회. 로딩·검증 경로에서만 쓴다 (틱 루프에서 부르지 않는다).</summary>
    public bool TryGet(string id, out ItemDef def) => _byId.TryGetValue(id, out def!);

    /// <summary>문자열 id 로 레시피 조회.</summary>
    public bool TryGetRecipe(string id, out RecipeDef recipe) => _recipesById.TryGetValue(id, out recipe!);

    /// <summary>
    /// 인벤토리 → WorldFlags. 첨자가 곧 item code 다.
    /// <b>할당 0.</b> EventApplier 가 NpcInventoryChanged 마다 부른다 (docs/02 §3.3).
    /// </summary>
    public WorldFlags ComputeFlags(ReadOnlySpan<int> inventory)
    {
        WorldFlags flags = WorldFlags.None;
        int n = Math.Min(inventory.Length, _grantsByCode.Length);

        for (int code = 0; code < n; code++)
        {
            if (inventory[code] > 0)
            {
                flags |= _grantsByCode[code];
            }
        }

        return flags;
    }

    /// <summary>한 아이템이 세우는 플래그. 데이터에서만 온다.</summary>
    public WorldFlags GrantsOf(ItemId code) =>
        (uint)code.Value < (uint)_grantsByCode.Length ? _grantsByCode[code.Value] : WorldFlags.None;

    /// <summary>masterdata/items.json 로드.</summary>
    public static ItemTable Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        ItemsFile? file = JsonSerializer.Deserialize(stream, ItemsJsonContext.Default.ItemsFile);

        if (file?.Items is null || file.Items.Length == 0)
        {
            throw new InvalidDataException($"items.json 에서 아이템을 읽지 못했다: {path}");
        }

        return Build(file);
    }

    /// <summary>JSON 문자열에서 로드. 테스트 픽스처용.</summary>
    public static ItemTable Parse(string json)
    {
        ItemsFile? file = JsonSerializer.Deserialize(json, ItemsJsonContext.Default.ItemsFile);

        if (file?.Items is null || file.Items.Length == 0)
        {
            throw new InvalidDataException("items.json 에서 아이템을 읽지 못했다.");
        }

        return Build(file);
    }

    private static ItemTable Build(ItemsFile file)
    {
        int maxCode = 0;
        foreach (ItemDto dto in file.Items)
        {
            maxCode = Math.Max(maxCode, dto.Code);
        }

        var byCode = new ItemDef?[maxCode + 1];
        var grantsByCode = new WorldFlags[maxCode + 1];
        var byId = new Dictionary<string, ItemDef>(file.Items.Length, StringComparer.Ordinal);
        var items = ImmutableArray.CreateBuilder<ItemDef>(file.Items.Length);

        foreach (ItemDto dto in file.Items)
        {
            if (dto.Code is < 0 or > ushort.MaxValue)
            {
                throw new InvalidDataException($"items.json: {dto.Id} 의 code {dto.Code} 가 범위를 벗어난다.");
            }

            if (byCode[dto.Code] is not null)
            {
                throw new InvalidDataException($"items.json: code {dto.Code} 가 중복이다 ({dto.Id}).");
            }

            WorldFlags grants = WorldFlags.None;
            foreach (string flagId in dto.Grants ?? [])
            {
                if (!WorldFlagTable.TryParse(flagId, out WorldFlags flag))
                {
                    throw new InvalidDataException(
                        $"items.json: {dto.Id} 가 world_flags.json 에 없는 플래그 '{flagId}' 를 참조한다.");
                }

                grants |= flag;
            }

            var def = new ItemDef(
                dto.Id,
                new ItemId((ushort)dto.Code),
                ParseCategory(dto.Id, dto.Category),
                grants,
                dto.Stack);

            if (!byId.TryAdd(def.Id, def))
            {
                throw new InvalidDataException($"items.json: id '{def.Id}' 가 중복이다.");
            }

            byCode[dto.Code] = def;
            grantsByCode[dto.Code] = grants;
            items.Add(def);
        }

        items.Sort((a, b) => a.Code.Value.CompareTo(b.Code.Value));

        var recipesById = new Dictionary<string, RecipeDef>(StringComparer.Ordinal);
        var recipes = ImmutableArray.CreateBuilder<RecipeDef>();

        foreach (RecipeDto dto in file.Recipes ?? [])
        {
            var recipe = new RecipeDef(
                dto.Id,
                dto.WorkplaceType,
                ToSlots(byId, dto.Id, dto.Inputs),
                ToSlots(byId, dto.Id, dto.Outputs),
                dto.DurationS);

            if (!recipesById.TryAdd(recipe.Id, recipe))
            {
                throw new InvalidDataException($"items.json: 레시피 id '{recipe.Id}' 가 중복이다.");
            }

            recipes.Add(recipe);
        }

        return new ItemTable(
            byCode, grantsByCode, byId, recipesById, items.ToImmutable(), recipes.ToImmutable());
    }

    private static ImmutableArray<RecipeSlot> ToSlots(
        Dictionary<string, ItemDef> byId, string recipeId, RecipeSlotDto[]? slots)
    {
        if (slots is null || slots.Length == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<RecipeSlot>(slots.Length);

        foreach (RecipeSlotDto slot in slots)
        {
            if (!byId.TryGetValue(slot.Item, out ItemDef? def))
            {
                throw new InvalidDataException(
                    $"items.json: 레시피 '{recipeId}' 가 없는 아이템 '{slot.Item}' 을 참조한다.");
            }

            builder.Add(new RecipeSlot(def.Code, slot.Count));
        }

        return builder.ToImmutable();
    }

    private static ItemCategory ParseCategory(string itemId, string category) => category switch
    {
        "raw" => ItemCategory.Raw,
        "product" => ItemCategory.Product,
        "food" => ItemCategory.Food,
        "drink" => ItemCategory.Drink,
        "tool" => ItemCategory.Tool,
        "weapon" => ItemCategory.Weapon,
        "currency" => ItemCategory.Currency,
        _ => throw new InvalidDataException($"items.json: {itemId} 의 category '{category}' 를 모른다."),
    };

    // --- JSON DTO. 소스 생성기로 직렬화한다 (런타임 리플렉션 0). ---

    internal sealed record ItemsFile(ItemDto[] Items, RecipeDto[]? Recipes);

    internal sealed record ItemDto(string Id, int Code, string Category, string[]? Grants, int Stack);

    internal sealed record RecipeDto(
        string Id,
        string WorkplaceType,
        RecipeSlotDto[]? Inputs,
        RecipeSlotDto[]? Outputs,
        int DurationS);

    internal sealed record RecipeSlotDto(string Item, int Count);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ItemTable.ItemsFile))]
internal sealed partial class ItemsJsonContext : JsonSerializerContext;
