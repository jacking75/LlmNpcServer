using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npc.Contracts;
using Npc.Core;

namespace Npc.MasterData;

/// <summary>
/// 아키타입 성향. 0~100. LLM 프롬프트에 노출되어 플랜 성향에 반영되고,
/// 인터럽트 규칙의 archetype_trait 조건이 이 값을 본다 (docs/01 §7).
/// </summary>
public readonly record struct TraitSet(byte Diligence, byte Sociability, byte Courage, byte Greed)
{
    /// <summary>이름으로 성향값 읽기. interrupts.json 의 조건 파싱에 쓴다.</summary>
    public int this[TraitKind kind] => kind switch
    {
        TraitKind.Diligence => Diligence,
        TraitKind.Sociability => Sociability,
        TraitKind.Courage => Courage,
        TraitKind.Greed => Greed,
        _ => 0,
    };
}

/// <summary>인벤토리 한 칸.</summary>
public readonly record struct InventorySlot(ItemId Item, int Count);

/// <summary>성향 종류.</summary>
public enum TraitKind
{
    Diligence,
    Sociability,
    Courage,
    Greed,
}

/// <summary>아키타입 정의. docs/01 §5.</summary>
/// <param name="Code">0..39. 버킷 키 인덱스가 이 번호를 그대로 쓴다.</param>
/// <param name="Id">문자열 id.</param>
/// <param name="NameKey">현지화 키.</param>
/// <param name="Description">LLM 프롬프트에 실리는 설명.</param>
/// <param name="AllowedActions">허용 액션. 검증기 2단의 V2.ACTION_NOT_ALLOWED 가 이걸 본다.</param>
/// <param name="AllowedMask">허용 액션의 비트셋. 액션 37종이라 ulong 하나에 들어간다.</param>
/// <param name="HomePoiType">주거 POI 타입.</param>
/// <param name="WorkplacePoiType">일터 POI subtype. null 이면 일터가 없다.</param>
/// <param name="PrimaryRecipes">$primary 심볼이 바인딩되는 레시피 목록.</param>
/// <param name="Traits">성향.</param>
/// <param name="DefaultGoals">기본 목표. 프롬프트 서픽스에 실린다.</param>
/// <param name="InitialInventory">
/// 기본 인벤토리. docs/11 §8 — gen_npcs 가 NPC 인스턴스에 복사한다.
/// 검증기 3단의 초기 상태(HasTool/HasFood/…)도 여기서 나온다.
/// </param>
/// <param name="BaselineFlags">
/// <see cref="InitialInventory"/> 가 세우는 <see cref="WorldFlags"/>.
/// 로드 시점에 items.json 의 grants 로 계산한다 — 코드에 하드코딩하지 않는다.
/// </param>
/// <param name="DutyHours">
/// 근무 시간대. 이 시간대의 버킷에서는 <c>OnDuty</c> 가 선다 (docs/01 §5).
/// <b>이게 없으면 <c>Guard</c>·<c>Patrol</c> 은 어떤 플랜에서도 성립할 수 없다</b> —
/// 두 액션은 <c>OnDuty</c> 를 요구하는데 마스터데이터의 어느 것도 그 플래그를 세우지 않았다 (T2-21 실측).
/// </param>
/// <param name="FallbackPlanId">fallback_plans.json 의 플랜 id.</param>
/// <param name="CombatCapable">전투 가능 여부. 인터럽트 규칙이 본다.</param>
/// <param name="PopulationWeight">인구 비중. 40종 합 = 1.0.</param>
public sealed record ArchetypeDef(
    ArchetypeId Code,
    string Id,
    string NameKey,
    string Description,
    ImmutableArray<ActionId> AllowedActions,
    ulong AllowedMask,
    string HomePoiType,
    string? WorkplacePoiType,
    ImmutableArray<string> PrimaryRecipes,
    TraitSet Traits,
    ImmutableArray<string> DefaultGoals,
    ImmutableArray<InventorySlot> InitialInventory,
    WorldFlags BaselineFlags,
    ImmutableArray<TimeOfDay> DutyHours,
    string FallbackPlanId,
    bool CombatCapable,
    double PopulationWeight)
{
    /// <summary>이 아키타입이 그 액션을 쓸 수 있는가. 비트 검사 한 번.</summary>
    public bool Allows(ActionId action) =>
        action.Value < 64 && (AllowedMask & (1UL << action.Value)) != 0;

    /// <summary>이 시간대에 근무 중인가. 근무 시간대가 비어 있으면 언제나 거짓이다.</summary>
    public bool IsOnDuty(TimeOfDay time) => DutyHours.Contains(time);
}

/// <summary>
/// archetypes.json 의 읽기 전용 인덱스. docs/01 §5.
/// <b><see cref="ArchetypeId"/>.Value(0..39) 가 곧 배열 첨자다</b> — 버킷 키 계산이 이 번호를 쓴다.
/// </summary>
public sealed class ArchetypeTable
{
    private readonly ArchetypeDef?[] _byCode;
    private readonly Dictionary<string, ArchetypeDef> _byId;

    private ArchetypeTable(
        ArchetypeDef?[] byCode,
        Dictionary<string, ArchetypeDef> byId,
        ImmutableArray<ArchetypeDef> archetypes)
    {
        _byCode = byCode;
        _byId = byId;
        Archetypes = archetypes;
    }

    /// <summary>code 오름차순의 모든 아키타입.</summary>
    public ImmutableArray<ArchetypeDef> Archetypes { get; }

    /// <summary>정의된 아키타입 수. 버킷 공간의 첫 번째 차원이다.</summary>
    public int Count => Archetypes.Length;

    /// <summary>code 로 조회.</summary>
    public ArchetypeDef this[ArchetypeId code] =>
        (uint)code.Value < (uint)_byCode.Length && _byCode[code.Value] is { } def
            ? def
            : throw new ArgumentOutOfRangeException(nameof(code), $"정의되지 않은 archetype code: {code.Value}");

    /// <summary>문자열 id 로 조회. 로딩·검증 경로에서만 쓴다.</summary>
    public bool TryGet(string id, out ArchetypeDef def) => _byId.TryGetValue(id, out def!);

    /// <summary>인구 비중의 합. V5 가 1.0 ±0.001 을 요구한다.</summary>
    public double TotalPopulationWeight
    {
        get
        {
            double sum = 0;
            foreach (ArchetypeDef def in Archetypes)
            {
                sum += def.PopulationWeight;
            }

            return sum;
        }
    }

    /// <summary>masterdata/archetypes.json 로드.</summary>
    public static ArchetypeTable Load(string path, ActionCatalog actions, ItemTable items)
    {
        using FileStream stream = File.OpenRead(path);
        ArchetypesFile? file = JsonSerializer.Deserialize(stream, ArchetypesJsonContext.Default.ArchetypesFile);

        if (file?.Archetypes is null || file.Archetypes.Length == 0)
        {
            throw new InvalidDataException($"archetypes.json 에서 아키타입을 읽지 못했다: {path}");
        }

        return Build(file, actions, items);
    }

    /// <summary>JSON 문자열에서 로드. 테스트 픽스처용.</summary>
    public static ArchetypeTable Parse(string json, ActionCatalog actions, ItemTable items)
    {
        ArchetypesFile? file = JsonSerializer.Deserialize(json, ArchetypesJsonContext.Default.ArchetypesFile);

        if (file?.Archetypes is null || file.Archetypes.Length == 0)
        {
            throw new InvalidDataException("archetypes.json 에서 아키타입을 읽지 못했다.");
        }

        return Build(file, actions, items);
    }

    private static ArchetypeTable Build(ArchetypesFile file, ActionCatalog actions, ItemTable items)
    {
        int maxCode = 0;
        foreach (ArchetypeDto dto in file.Archetypes)
        {
            maxCode = Math.Max(maxCode, dto.Code);
        }

        var byCode = new ArchetypeDef?[maxCode + 1];
        var byId = new Dictionary<string, ArchetypeDef>(file.Archetypes.Length, StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<ArchetypeDef>(file.Archetypes.Length);

        foreach (ArchetypeDto dto in file.Archetypes)
        {
            if (dto.Code is < 0 or > ushort.MaxValue)
            {
                throw new InvalidDataException($"archetypes.json: {dto.Id} 의 code {dto.Code} 가 범위를 벗어난다.");
            }

            if (byCode[dto.Code] is not null)
            {
                throw new InvalidDataException($"archetypes.json: code {dto.Code} 가 중복이다 ({dto.Id}).");
            }

            var allowed = ImmutableArray.CreateBuilder<ActionId>();
            ulong mask = 0;

            foreach (string actionId in dto.AllowedActions ?? [])
            {
                if (!actions.TryGet(actionId, out ActionDef action))
                {
                    throw new InvalidDataException(
                        $"archetypes.json: {dto.Id} 가 카탈로그에 없는 액션 '{actionId}' 를 허용한다.");
                }

                if (action.Code.Value >= 64)
                {
                    throw new InvalidDataException(
                        $"archetypes.json: 액션 code 가 64 이상이면 허용 비트셋에 담을 수 없다 ({actionId}).");
                }

                allowed.Add(action.Code);
                mask |= 1UL << action.Code.Value;
            }

            var inventory = ImmutableArray.CreateBuilder<InventorySlot>();
            WorldFlags baseline = WorldFlags.None;

            foreach (InventorySlotDto slot in dto.InitialInventory ?? [])
            {
                if (!items.TryGet(slot.Item, out ItemDef item))
                {
                    throw new InvalidDataException(
                        $"archetypes.json: {dto.Id} 의 initial_inventory 가 없는 아이템 '{slot.Item}' 을 든다.");
                }

                inventory.Add(new InventorySlot(item.Code, slot.Count));

                if (slot.Count > 0)
                {
                    baseline |= item.Grants;
                }
            }

            var dutyHours = ImmutableArray.CreateBuilder<TimeOfDay>();

            foreach (string hour in dto.DutyHours ?? [])
            {
                if (!Enum.TryParse(hour, out TimeOfDay time))
                {
                    throw new InvalidDataException(
                        $"archetypes.json: {dto.Id} 의 duty_hours 에 모르는 시간대 '{hour}' 가 있다.");
                }

                dutyHours.Add(time);
            }

            var def = new ArchetypeDef(
                new ArchetypeId((ushort)dto.Code),
                dto.Id,
                dto.NameKey,
                dto.Desc,
                allowed.ToImmutable(),
                mask,
                dto.HomePoiType,
                dto.WorkplacePoiType,
                dto.PrimaryRecipes is null ? [] : [.. dto.PrimaryRecipes],
                new TraitSet(
                    Clamp(dto.Traits?.Diligence ?? 50),
                    Clamp(dto.Traits?.Sociability ?? 50),
                    Clamp(dto.Traits?.Courage ?? 50),
                    Clamp(dto.Traits?.Greed ?? 50)),
                dto.DefaultGoals is null ? [] : [.. dto.DefaultGoals],
                inventory.ToImmutable(),
                baseline,
                dutyHours.ToImmutable(),
                dto.FallbackPlan,
                dto.CombatCapable,
                dto.PopulationWeight);

            if (!byId.TryAdd(def.Id, def))
            {
                throw new InvalidDataException($"archetypes.json: id '{def.Id}' 가 중복이다.");
            }

            byCode[dto.Code] = def;
            builder.Add(def);
        }

        builder.Sort((a, b) => a.Code.Value.CompareTo(b.Code.Value));

        return new ArchetypeTable(byCode, byId, builder.ToImmutable());
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 100);

    // --- JSON DTO. 소스 생성기로 직렬화한다 (런타임 리플렉션 0). ---

    internal sealed record ArchetypesFile(ArchetypeDto[] Archetypes);

    internal sealed record ArchetypeDto(
        string Id,
        int Code,
        string NameKey,
        string Desc,
        string[]? AllowedActions,
        string HomePoiType,
        string? WorkplacePoiType,
        string[]? PrimaryRecipes,
        TraitDto? Traits,
        string[]? DefaultGoals,
        InventorySlotDto[]? InitialInventory,
        string[]? DutyHours,
        string FallbackPlan,
        bool CombatCapable,
        double PopulationWeight);

    internal sealed record TraitDto(int Diligence, int Sociability, int Courage, int Greed);

    internal sealed record InventorySlotDto(string Item, int Count);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ArchetypeTable.ArchetypesFile))]
internal sealed partial class ArchetypesJsonContext : JsonSerializerContext;
