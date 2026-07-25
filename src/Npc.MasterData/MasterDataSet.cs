using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;

namespace Npc.MasterData;

/// <summary>POI 타입. docs/01 §4 의 8종.</summary>
public enum PoiType
{
    Home,
    Workplace,
    Market,
    Tavern,
    Temple,
    Gate,
    Field,
    Wilderness,
}

/// <summary>존 정의. docs/01 §4.</summary>
public sealed record ZoneDef(
    ZoneId Code,
    string Id,
    string NameKey,
    ImmutableArray<ZoneId> Adjacent,
    RegionState DefaultRegionState,
    Climate DefaultClimate,
    int Capacity);

/// <summary>관심지점 정의. docs/01 §4.</summary>
/// <param name="Code">POI code. 거리 행렬의 첨자는 <c>Code - 1</c> 이다.</param>
/// <param name="Id">문자열 id.</param>
/// <param name="Zone">소속 존.</param>
/// <param name="Type">8종 타입.</param>
/// <param name="Subtype">세부 종류. 아키타입의 workplace_poi_type 이 이 값을 가리킨다.</param>
/// <param name="Pos">좌표.</param>
/// <param name="Capacity">정원.</param>
/// <param name="OpenFrom">개방 시작 시간대.</param>
/// <param name="OpenTo">개방 종료 시간대.</param>
/// <param name="Grants">이 POI 에 있을 때 서는 플래그.</param>
/// <param name="AllowedArchetypeMask">
/// 여기서 일하는 아키타입의 비트셋. 0 이면 제한 없음.
/// <b>출입 허가가 아니라 근무 허가다</b> — 시장·선술집·신전·성문은 누구나 드나든다.
/// </param>
/// <param name="Resources">채집 가능 자원.</param>
public sealed record PoiDef(
    PoiId Code,
    string Id,
    ZoneId Zone,
    PoiType Type,
    string Subtype,
    WorldPos Pos,
    int Capacity,
    TimeOfDay OpenFrom,
    TimeOfDay OpenTo,
    WorldFlags Grants,
    ulong AllowedArchetypeMask,
    ImmutableArray<ItemId> Resources)
{
    /// <summary>이 아키타입이 여기서 일할 수 있는가. 비트 검사 한 번. 마스크 0 은 제한 없음이다.</summary>
    public bool Allows(ArchetypeId archetype) =>
        AllowedArchetypeMask == 0 || (archetype.Value < 64 && (AllowedArchetypeMask & (1UL << archetype.Value)) != 0);

    /// <summary>
    /// 근무 허가가 출입까지 제한하는 POI 인가.
    /// 일터·채집지·야외 작업지는 아무나 들어가서 일할 수 없지만,
    /// 시장·선술집·신전·성문·주거는 공공장소라 누구나 드나든다.
    /// </summary>
    public bool IsWorkSite => Type is PoiType.Workplace or PoiType.Field or PoiType.Wilderness;

    /// <summary>이 아키타입이 들어갈 수 있는가. 공공장소는 언제나 참이다.</summary>
    public bool CanEnter(ArchetypeId archetype) => !IsWorkSite || Allows(archetype);
}

/// <summary>zones.json 의 읽기 전용 인덱스.</summary>
public sealed class ZoneTable
{
    private readonly ZoneDef?[] _byCode;
    private readonly Dictionary<string, ZoneDef> _byId;

    internal ZoneTable(ZoneDef?[] byCode, Dictionary<string, ZoneDef> byId, ImmutableArray<ZoneDef> zones)
    {
        _byCode = byCode;
        _byId = byId;
        Zones = zones;
    }

    /// <summary>code 오름차순의 모든 존.</summary>
    public ImmutableArray<ZoneDef> Zones { get; }

    /// <summary>존 수.</summary>
    public int Count => Zones.Length;

    /// <summary>code 로 조회.</summary>
    public ZoneDef this[ZoneId code] =>
        (uint)code.Value < (uint)_byCode.Length && _byCode[code.Value] is { } def
            ? def
            : throw new ArgumentOutOfRangeException(nameof(code), $"정의되지 않은 zone code: {code.Value}");

    /// <summary>문자열 id 로 조회.</summary>
    public bool TryGet(string id, out ZoneDef def) => _byId.TryGetValue(id, out def!);
}

/// <summary>
/// pois.json + poi_distances.bin 의 읽기 전용 인덱스.
/// 거리 조회는 배열 첨자 한 번이다 — <c>$nearest_*</c> 바인딩이 틱 루프에서 돌 수 있어야 한다.
/// </summary>
public sealed class PoiTable
{
    private readonly PoiDef?[] _byCode;
    private readonly Dictionary<string, PoiDef> _byId;
    private readonly Half[] _distances;   // n × n. 첨자 = code - 1
    private readonly int _matrixSize;
    private readonly ImmutableArray<PoiId>[] _byZone;
    private readonly ImmutableArray<PoiId>[] _byType;
    private readonly Dictionary<string, ImmutableArray<PoiId>> _bySubtype;

    internal PoiTable(
        PoiDef?[] byCode,
        Dictionary<string, PoiDef> byId,
        ImmutableArray<PoiDef> pois,
        Half[] distances,
        int matrixSize,
        ImmutableArray<PoiId>[] byZone,
        ImmutableArray<PoiId>[] byType,
        Dictionary<string, ImmutableArray<PoiId>> bySubtype)
    {
        _byCode = byCode;
        _byId = byId;
        Pois = pois;
        _distances = distances;
        _matrixSize = matrixSize;
        _byZone = byZone;
        _byType = byType;
        _bySubtype = bySubtype;
    }

    /// <summary>code 오름차순의 모든 POI.</summary>
    public ImmutableArray<PoiDef> Pois { get; }

    /// <summary>POI 수.</summary>
    public int Count => Pois.Length;

    /// <summary>code 로 조회.</summary>
    public PoiDef this[PoiId code] =>
        (uint)code.Value < (uint)_byCode.Length && _byCode[code.Value] is { } def
            ? def
            : throw new ArgumentOutOfRangeException(nameof(code), $"정의되지 않은 poi code: {code.Value}");

    /// <summary>문자열 id 로 조회.</summary>
    public bool TryGet(string id, out PoiDef def) => _byId.TryGetValue(id, out def!);

    /// <summary>두 POI 사이의 거리(m). 배열 첨자 한 번. 할당 0.</summary>
    public float Distance(PoiId a, PoiId b)
    {
        int i = a.Value - 1;
        int j = b.Value - 1;

        if ((uint)i >= (uint)_matrixSize || (uint)j >= (uint)_matrixSize)
        {
            return float.PositiveInfinity;
        }

        return (float)_distances[(i * _matrixSize) + j];
    }

    /// <summary>이 존 안의 POI (code 오름차순).</summary>
    public ImmutableArray<PoiId> InZone(ZoneId zone) =>
        (uint)zone.Value < (uint)_byZone.Length ? _byZone[zone.Value] : [];

    /// <summary>이 타입의 POI (code 오름차순).</summary>
    public ImmutableArray<PoiId> OfType(PoiType type) => _byType[(int)type];

    /// <summary>이 subtype 의 POI (code 오름차순).</summary>
    public ImmutableArray<PoiId> OfSubtype(string subtype) =>
        _bySubtype.TryGetValue(subtype, out ImmutableArray<PoiId> list) ? list : [];
}

/// <summary>
/// context_buckets.json 의 읽기 전용 인덱스. docs/01 §6.
/// 버킷이 함의하는 초기 <see cref="WorldFlags"/> 를 준다 — 검증기 3단의 <c>ctx.InitialFlags</c> 다.
/// </summary>
public sealed class BucketSpace
{
    private readonly WorldFlags[] _timeFlags;
    private readonly WorldFlags[] _regionFlags;
    private readonly WorldFlags[] _climateFlags;
    private readonly (int From, int To)[] _gameHours;

    internal BucketSpace(
        WorldFlags[] timeFlags,
        WorldFlags[] regionFlags,
        WorldFlags[] climateFlags,
        (int From, int To)[] gameHours,
        int declaredTotalKeys)
    {
        _timeFlags = timeFlags;
        _regionFlags = regionFlags;
        _climateFlags = climateFlags;
        _gameHours = gameHours;
        DeclaredTotalKeys = declaredTotalKeys;
    }

    /// <summary>context_buckets.json 이 선언한 total_keys. V6 이 실제 조합 수와 대조한다.</summary>
    public int DeclaredTotalKeys { get; }

    /// <summary>이 버킷이 함의하는 초기 상태. 검증기 3단이 여기서 시작한다.</summary>
    public WorldFlags InitialFlags(BucketKey key) =>
        _timeFlags[(int)key.T] | _regionFlags[(int)key.R] | _climateFlags[(int)key.C];

    /// <summary>이 시간대가 세우는 플래그.</summary>
    public WorldFlags FlagsOf(TimeOfDay time) => _timeFlags[(int)time];

    /// <summary>이 지역 상태가 세우는 플래그.</summary>
    public WorldFlags FlagsOf(RegionState state) => _regionFlags[(int)state];

    /// <summary>이 기후가 세우는 플래그.</summary>
    public WorldFlags FlagsOf(Climate climate) => _climateFlags[(int)climate];

    /// <summary>게임 시각(0~23)이 속한 시간대.</summary>
    public TimeOfDay TimeOfDayAt(int gameHour)
    {
        gameHour = ((gameHour % 24) + 24) % 24;

        for (int i = 0; i < _gameHours.Length; i++)
        {
            (int from, int to) = _gameHours[i];

            bool inside = from <= to
                ? gameHour >= from && gameHour < to
                : gameHour >= from || gameHour < to;   // Night 처럼 자정을 넘는 구간

            if (inside)
            {
                return (TimeOfDay)i;
            }
        }

        return TimeOfDay.Night;
    }

    /// <summary>이 시간대의 게임 시각 구간 [from, to).</summary>
    public (int From, int To) GameHoursOf(TimeOfDay time) => _gameHours[(int)time];
}

/// <summary>파일 하나의 해시. 부분 무효화 판정(docs/01 §11)에 쓴다.</summary>
public readonly record struct FileHash(string FileName, string Sha256);

/// <summary>
/// 마스터데이터 전체. docs/01 §11.
/// 기동 시 전량 로드 → 검증 → 읽기 전용 인덱스로 컴파일 → 이후 불변.
///
/// docs/01 §11 의 <c>PromptPrefix Prefix</c> 는 여기에 없다.
/// <see cref="PromptPrefix"/> 는 <c>Npc.Llm</c> 에 있고(docs/01 §10.2),
/// <c>Npc.MasterData → Npc.Llm</c> 참조는 CLAUDE.md §3 이 금지한다.
/// P1 에는 LLM 이 없으므로 P2 에서 조립 위치를 정한다.
/// </summary>
public sealed class MasterDataSet : IPlanValidationVocabulary
{
    /// <summary>액션 카탈로그.</summary>
    public required ActionCatalog Actions { get; init; }

    /// <summary>아이템·레시피.</summary>
    public required ItemTable Items { get; init; }

    /// <summary>존.</summary>
    public required ZoneTable Zones { get; init; }

    /// <summary>POI + 거리 행렬.</summary>
    public required PoiTable Pois { get; init; }

    /// <summary>아키타입.</summary>
    public required ArchetypeTable Archetypes { get; init; }

    /// <summary>버킷 공간.</summary>
    public required BucketSpace Buckets { get; init; }

    /// <summary>인터럽트 규칙.</summary>
    public required InterruptRules Interrupts { get; init; }

    /// <summary>
    /// 폴백 플랜. docs/01 §8. fallback_plans.json 이 없으면 null.
    /// 로더가 이 인스턴스를 만든 뒤에 채운다 — PlanTable 이 MasterDataSet 을 필요로 해서
    /// 생성자 안에서는 만들 수 없다.
    /// </summary>
    public PlanTable? Fallbacks { get; internal set; }

    /// <summary>파일별 해시. 파일명 오름차순.</summary>
    public required ImmutableArray<FileHash> FileHashes { get; init; }

    /// <summary>
    /// 전체 콘텐츠 해시. 이 값이 바뀌면 프리베이크된 플랜 스토어가 전량 무효다 (docs/01 §11).
    /// 같은 입력이면 언제 계산해도 같은 값이 나온다 — 시각도 난수도 섞지 않는다.
    /// </summary>
    public required string ContentHash { get; init; }

    /// <summary>파일 하나의 해시 조회. 부분 무효화 판정용.</summary>
    public string? HashOf(string fileName)
    {
        foreach (FileHash hash in FileHashes)
        {
            if (string.Equals(hash.FileName, fileName, StringComparison.Ordinal))
            {
                return hash.Sha256;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ IPlanVocabulary
    //
    // docs/03 §5 의 컴파일 어휘. CompiledPlan 은 Npc.Core 에 있고 ActionCatalog 는 여기 있는데
    // CLAUDE.md §3 이 Core → MasterData 를 금지하므로, Core 가 선언한 인터페이스를 여기서 구현한다.

    /// <inheritdoc />
    public bool TryGetAction(string actionId, out ActionId action)
    {
        if (Actions.TryGet(actionId, out ActionDef def))
        {
            action = def.Code;
            return true;
        }

        action = default;
        return false;
    }

    /// <inheritdoc />
    public string ActionName(ActionId action) => Actions[action].Id;

    /// <inheritdoc />
    public StepFlags FlagsOf(ActionId action)
    {
        ActionDef def = Actions[action];
        return new StepFlags(def.Requires, def.RequiresAny, def.Forbids, def.Grants, def.Clears);
    }

    /// <inheritdoc />
    public int DefaultTimeoutSeconds(ActionId action) => Actions[action].DefaultTimeoutSeconds;

    /// <inheritdoc />
    public bool TryGetItem(string itemId, out ItemId item)
    {
        if (Items.TryGet(itemId, out ItemDef def))
        {
            item = def.Code;
            return true;
        }

        item = default;
        return false;
    }

    /// <inheritdoc />
    public bool TryPackArgs(
        ActionId action,
        IReadOnlyDictionary<string, JsonElement> args,
        out PackedArgs packed,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(args);

        ActionDef def = Actions[action];

        PoiSymbol poi = PoiSymbol.None;
        ItemId item = default;
        ushort count = 0;
        byte argFlags = 0;
        byte npcRef = NpcRefCodes.None;

        foreach (ParamDef param in def.Params)
        {
            bool present = args.TryGetValue(param.Name, out JsonElement value);

            if (!present)
            {
                if (param.Required)
                {
                    error = $"필수 파라미터 '{param.Name}' 이 없다.";
                    packed = default;
                    return false;
                }

                // 기본값으로 채운다.
                switch (param.Type)
                {
                    case ParamType.Int:
                        count = (ushort)Math.Clamp(param.DefaultInt, 0, ushort.MaxValue);
                        break;

                    case ParamType.Enum when param.DefaultText is { } fallback:
                        argFlags = (byte)Math.Max(0, param.EnumValues.IndexOf(fallback));
                        break;

                    default:
                        break;
                }

                continue;
            }

            switch (param.Type)
            {
                case ParamType.PoiRef:
                    if (!PoiSymbols.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : null, out poi))
                    {
                        error = $"'{param.Name}' 이 허용된 POI 심볼이 아니다: {value}";
                        packed = default;
                        return false;
                    }

                    break;

                case ParamType.Route:
                    // 순찰로는 첫 지점만 컴파일한다. 나머지 경유지는 P1 범위 밖이다.
                    JsonElement first = value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0
                        ? value[0]
                        : value;

                    if (!PoiSymbols.TryParse(first.ValueKind == JsonValueKind.String ? first.GetString() : null, out poi))
                    {
                        error = $"'{param.Name}' 의 첫 지점이 허용된 POI 심볼이 아니다: {value}";
                        packed = default;
                        return false;
                    }

                    break;

                case ParamType.ItemRef:
                    if (value.ValueKind != JsonValueKind.String
                        || !Items.TryGet(value.GetString()!, out ItemDef itemDef))
                    {
                        error = $"'{param.Name}' 이 items.json 에 없는 아이템이다: {value}";
                        packed = default;
                        return false;
                    }

                    item = itemDef.Code;
                    break;

                case ParamType.Int:
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
                    {
                        error = $"'{param.Name}' 이 정수가 아니다: {value}";
                        packed = default;
                        return false;
                    }

                    count = (ushort)Math.Clamp(number, 0, ushort.MaxValue);
                    break;

                case ParamType.Enum:
                    int index = value.ValueKind == JsonValueKind.String
                        ? param.EnumValues.IndexOf(value.GetString()!)
                        : -1;

                    if (index < 0)
                    {
                        error = $"'{param.Name}' 이 허용된 열거값이 아니다: {value}";
                        packed = default;
                        return false;
                    }

                    argFlags = (byte)index;
                    break;

                case ParamType.NpcRef:
                    if (!TryPackNpcRef(value, out npcRef))
                    {
                        error = $"'{param.Name}' 이 허용된 npc_ref 심볼이 아니다: {value}";
                        packed = default;
                        return false;
                    }

                    break;

                case ParamType.ZoneRef:
                    // 플랜은 존 id 를 직접 쓰지 않는다 — 버킷 단위 재사용이 깨진다.
                    // 런타임이 NPC 의 현재 존을 쓴다.
                    break;

                default:
                    error = $"'{param.Name}' 의 타입을 모른다.";
                    packed = default;
                    return false;
            }
        }

        packed = new PackedArgs(poi, item, count, argFlags, npcRef);
        error = string.Empty;
        return true;
    }

    /// <inheritdoc />
    public void UnpackArgs(ActionId action, in PackedArgs packed, IDictionary<string, JsonElement> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        ActionDef def = Actions[action];

        foreach (ParamDef param in def.Params)
        {
            switch (param.Type)
            {
                case ParamType.PoiRef when packed.Poi != PoiSymbol.None:
                    args[param.Name] = Json(PoiSymbols.ToText(packed.Poi));
                    break;

                case ParamType.Route when packed.Poi != PoiSymbol.None:
                    args[param.Name] = JsonDocument.Parse(
                        $"[{JsonSerializer.Serialize(PoiSymbols.ToText(packed.Poi))}]").RootElement.Clone();
                    break;

                case ParamType.ItemRef when packed.Item.Value != 0:
                    args[param.Name] = Json(Items[packed.Item].Id);
                    break;

                case ParamType.Int:
                    args[param.Name] = JsonDocument.Parse(
                        packed.Count.ToString(CultureInfo.InvariantCulture)).RootElement.Clone();
                    break;

                case ParamType.Enum when packed.ArgFlags < param.EnumValues.Length:
                    args[param.Name] = Json(param.EnumValues[packed.ArgFlags]);
                    break;

                case ParamType.NpcRef when packed.NpcRef != NpcRefCodes.None:
                    args[param.Name] = Json(UnpackNpcRef(packed.NpcRef));
                    break;

                default:
                    break;
            }
        }

        static JsonElement Json(string text) =>
            JsonDocument.Parse(JsonSerializer.Serialize(text)).RootElement.Clone();
    }

    // ------------------------------------------------------------------ IPlanValidationVocabulary

    /// <inheritdoc />
    public bool IsActionAllowed(ArchetypeId archetype, ActionId action) => Archetypes[archetype].Allows(action);

    /// <inheritdoc />
    public WorldFlags InitialFlags(BucketKey bucket) =>
        Buckets.InitialFlags(bucket) | Archetypes[bucket.A].BaselineFlags;

    /// <inheritdoc />
    public bool CompletesOnArrival(ActionId action) =>
        Actions[action].CompletesOn.Contains(GameEventKind.NpcArrived);

    /// <inheritdoc />
    public bool CanBindSymbol(ArchetypeId archetype, PoiSymbol symbol)
    {
        ArchetypeDef def = Archetypes[archetype];

        return symbol switch
        {
            PoiSymbol.None => true,
            PoiSymbol.Home => Pois.OfType(PoiType.Home).Length > 0,
            PoiSymbol.Workplace => def.WorkplacePoiType is { } subtype && Pois.OfSubtype(subtype).Length > 0,
            PoiSymbol.Market => HasEnterable(PoiType.Market, archetype),
            PoiSymbol.Tavern => HasEnterable(PoiType.Tavern, archetype),
            PoiSymbol.Temple => HasEnterable(PoiType.Temple, archetype),
            PoiSymbol.Gate => HasEnterable(PoiType.Gate, archetype),
            PoiSymbol.NearestField => HasEnterable(PoiType.Field, archetype),
            PoiSymbol.NearestSafe => HasEnterable(PoiType.Gate, archetype) || HasEnterable(PoiType.Home, archetype),
            PoiSymbol.NearestShelter => HasEnterable(PoiType.Home, archetype),
            _ => false,
        };
    }

    private bool HasEnterable(PoiType type, ArchetypeId archetype)
    {
        foreach (PoiId id in Pois.OfType(type))
        {
            if (Pois[id].CanEnter(archetype))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool TryGetRecipeInputs(ItemId recipe, out ImmutableArray<PlanRecipeInput> inputs)
    {
        if (!Items.TryGetRecipe(Items[recipe].Id, out RecipeDef def))
        {
            inputs = [];
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<PlanRecipeInput>(def.Inputs.Length);
        foreach (RecipeSlot slot in def.Inputs)
        {
            builder.Add(new PlanRecipeInput(slot.Item, slot.Count));
        }

        inputs = builder.ToImmutable();
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 데이터로 판정한다. 채집 계열은 resource/crop 파라미터를 갖고,
    /// 수령 계열(PickUp·Withdraw)은 InventoryFull 을 금지 플래그로 갖는다.
    /// 액션 id 를 코드에 하드코딩하지 않는다.
    /// </remarks>
    public bool ProducesItem(ActionId action)
    {
        ActionDef def = Actions[action];

        return def.Param("resource") is not null
            || def.Param("crop") is not null
            || ((def.Forbids & WorldFlags.InventoryFull) != 0 && def.Param("item") is not null);
    }

    /// <inheritdoc />
    public bool ConsumesRecipe(ActionId action) => Actions[action].Param("recipe") is not null;

    /// <inheritdoc />
    public ValidationResult ValidateArgs(
        ActionId action, int stepIndex, IReadOnlyDictionary<string, JsonElement> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        ActionDef def = Actions[action];

        // --- 정의되지 않은 인자 ---
        foreach (string key in args.Keys.Order(StringComparer.Ordinal))
        {
            if (def.Param(key) is null)
            {
                return ValidationResult.Fail(
                    ValidationStage.Vocabulary, "V2.UNKNOWN_ARG", stepIndex,
                    $"{def.Id} 에 '{key}' 파라미터가 없다. 정의: {string.Join(", ", def.Params.Select(p => p.Name))}");
            }
        }

        foreach (ParamDef param in def.Params)
        {
            if (!args.TryGetValue(param.Name, out JsonElement value))
            {
                if (param.Required)
                {
                    return ValidationResult.Fail(
                        ValidationStage.Vocabulary, "V2.MISSING_REQUIRED_ARG", stepIndex,
                        $"{def.Id} 의 필수 파라미터 '{param.Name}' 이 없다.");
                }

                continue;
            }

            ValidationResult result = ValidateParam(def, param, value, stepIndex);
            if (!result.IsValid)
            {
                return result;
            }
        }

        return ValidationResult.Ok;
    }

    private ValidationResult ValidateParam(ActionDef def, ParamDef param, JsonElement value, int stepIndex)
    {
        switch (param.Type)
        {
            case ParamType.PoiRef:
                if (value.ValueKind != JsonValueKind.String)
                {
                    return TypeMismatch(def, param, value, stepIndex, "문자열 POI 심볼");
                }

                if (!PoiSymbols.TryParse(value.GetString(), out _))
                {
                    return ValidationResult.Fail(
                        ValidationStage.Vocabulary, "V2.UNKNOWN_POI", stepIndex,
                        $"{def.Id}.{param.Name} 의 '{value.GetString()}' 는 허용된 POI 심볼이 아니다. "
                        + $"허용: {string.Join(", ", PoiSymbols.Names.Skip(1))}");
                }

                break;

            case ParamType.Route:
                if (value.ValueKind != JsonValueKind.Array)
                {
                    return TypeMismatch(def, param, value, stepIndex, "POI 심볼 배열");
                }

                if (value.GetArrayLength() is < 2 or > 8)
                {
                    return ValidationResult.Fail(
                        ValidationStage.Vocabulary, "V2.RANGE", stepIndex,
                        $"{def.Id}.{param.Name} 의 길이가 {value.GetArrayLength()} 다. 2~8 이어야 한다.");
                }

                foreach (JsonElement waypoint in value.EnumerateArray())
                {
                    if (waypoint.ValueKind != JsonValueKind.String
                        || !PoiSymbols.TryParse(waypoint.GetString(), out _))
                    {
                        return ValidationResult.Fail(
                            ValidationStage.Vocabulary, "V2.UNKNOWN_POI", stepIndex,
                            $"{def.Id}.{param.Name} 의 '{waypoint}' 는 허용된 POI 심볼이 아니다.");
                    }
                }

                break;

            case ParamType.ItemRef:
                if (value.ValueKind != JsonValueKind.String)
                {
                    return TypeMismatch(def, param, value, stepIndex, "문자열 아이템 id");
                }

                // recipe 인자는 레시피 표를, 나머지는 아이템 표를 본다.
                if (string.Equals(param.Name, "recipe", StringComparison.Ordinal))
                {
                    if (!Items.TryGetRecipe(value.GetString()!, out _))
                    {
                        return ValidationResult.Fail(
                            ValidationStage.Vocabulary, "V2.UNKNOWN_RECIPE", stepIndex,
                            $"{def.Id}.{param.Name} 의 '{value.GetString()}' 는 items.json 의 레시피가 아니다.");
                    }
                }
                else if (!Items.TryGet(value.GetString()!, out _))
                {
                    return ValidationResult.Fail(
                        ValidationStage.Vocabulary, "V2.UNKNOWN_ITEM", stepIndex,
                        $"{def.Id}.{param.Name} 의 '{value.GetString()}' 는 items.json 에 없다.");
                }

                if (param.EnumValues.Length > 0 && !param.EnumValues.Contains(value.GetString()!))
                {
                    return ValidationResult.Fail(
                        ValidationStage.Vocabulary, "V2.UNKNOWN_ITEM", stepIndex,
                        $"{def.Id}.{param.Name} 은 {string.Join(", ", param.EnumValues)} 중 하나여야 한다.");
                }

                break;

            case ParamType.NpcRef:
                if (value.ValueKind != JsonValueKind.String)
                {
                    return TypeMismatch(def, param, value, stepIndex, "문자열 npc_ref 심볼");
                }

                if (!TryPackNpcRef(value, out _))
                {
                    return ValidationResult.Fail(
                        ValidationStage.Vocabulary, "V2.TYPE_MISMATCH", stepIndex,
                        $"{def.Id}.{param.Name} 의 '{value.GetString()}' 는 self / nearest:<archetype> / poi_owner:<poi> 가 아니다.");
                }

                break;

            case ParamType.ZoneRef:
                if (value.ValueKind != JsonValueKind.String || !Zones.TryGet(value.GetString()!, out _))
                {
                    return TypeMismatch(def, param, value, stepIndex, "zones.json 의 존 id");
                }

                break;

            case ParamType.Enum:
                if (value.ValueKind != JsonValueKind.String)
                {
                    return TypeMismatch(def, param, value, stepIndex, "문자열 열거값");
                }

                if (!param.EnumValues.Contains(value.GetString()!))
                {
                    return ValidationResult.Fail(
                        ValidationStage.Vocabulary, "V2.TYPE_MISMATCH", stepIndex,
                        $"{def.Id}.{param.Name} 은 {string.Join(", ", param.EnumValues)} 중 하나여야 한다. 받은 값: {value}");
                }

                break;

            case ParamType.Int:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int number))
                {
                    return TypeMismatch(def, param, value, stepIndex, "정수");
                }

                if (number < param.Min || number > param.Max)
                {
                    return ValidationResult.Fail(
                        ValidationStage.Vocabulary, "V2.RANGE", stepIndex,
                        $"{def.Id}.{param.Name} 이 {number} 다. {param.Min}~{param.Max} 이어야 한다.");
                }

                break;

            default:
                return TypeMismatch(def, param, value, stepIndex, "알 수 없는 타입");
        }

        return ValidationResult.Ok;
    }

    private static ValidationResult TypeMismatch(
        ActionDef def, ParamDef param, JsonElement value, int stepIndex, string expected) =>
        ValidationResult.Fail(
            ValidationStage.Vocabulary, "V2.TYPE_MISMATCH", stepIndex,
            $"{def.Id}.{param.Name} 은 {expected} 여야 한다. 받은 값: {value}");

    private bool TryPackNpcRef(JsonElement value, out byte code)
    {
        code = NpcRefCodes.None;

        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string text = value.GetString()!;

        if (string.Equals(text, "self", StringComparison.Ordinal))
        {
            code = NpcRefCodes.Self();
            return true;
        }

        if (text.StartsWith("nearest:", StringComparison.Ordinal))
        {
            return Archetypes.TryGet(text["nearest:".Length..], out ArchetypeDef archetype)
                && SetNearest(archetype, out code);
        }

        if (text.StartsWith("poi_owner:", StringComparison.Ordinal)
            && PoiSymbols.TryParse(text["poi_owner:".Length..], out PoiSymbol symbol))
        {
            code = NpcRefCodes.PoiOwner(symbol);
            return true;
        }

        return false;

        static bool SetNearest(ArchetypeDef archetype, out byte code)
        {
            code = NpcRefCodes.NearestArchetype(archetype.Code.Value);
            return archetype.Code.Value < 64;
        }
    }

    private string UnpackNpcRef(byte code) => NpcRefCodes.KindOf(code) switch
    {
        NpcRefKind.Self => "self",
        NpcRefKind.NearestArchetype =>
            "nearest:" + Archetypes[new ArchetypeId((ushort)NpcRefCodes.PayloadOf(code))].Id,
        NpcRefKind.PoiOwner =>
            "poi_owner:" + PoiSymbols.ToText((PoiSymbol)NpcRefCodes.PayloadOf(code)),
        _ => "self",
    };
}

/// <summary>NPC 인스턴스 하나. docs/01 §9.</summary>
/// <param name="Id">1부터 시작하는 인스턴스 id. NpcStore 첨자는 <c>Id - 1</c> 이다.</param>
/// <param name="Archetype">아키타입 code.</param>
/// <param name="Zone">집이 있는 존.</param>
/// <param name="Home">집 POI.</param>
/// <param name="Workplace">일터 POI. 없으면 <c>default</c> (villager·child 등).</param>
/// <param name="Spawn">스폰 좌표.</param>
public readonly record struct NpcInstanceDef(
    int Id,
    ArchetypeId Archetype,
    ZoneId Zone,
    PoiId Home,
    PoiId Workplace,
    WorldPos Spawn);

/// <summary>
/// npc_instances.json 의 읽기 전용 인덱스. docs/01 §9.
///
/// <b>마스터데이터 본체와 따로 읽는다.</b> 3MB 넘는 산출물이라
/// 검증기나 단위 테스트가 매번 읽을 이유가 없다 — 호스트만 기동 시 1회 읽는다.
/// 같은 이유로 content_hash 에도 넣지 않는다. 인스턴스가 바뀌어도 프리베이크된 플랜은
/// 그대로다 (플랜은 버킷 단위이지 개체 단위가 아니다).
///
/// <c>trait_offsets</c> 는 P1 에서 읽지 않는다. 소요시간 랜덤화(P4)가 쓸 값이다.
/// </summary>
public sealed class NpcInstanceTable
{
    private NpcInstanceTable(int seed, ImmutableArray<NpcInstanceDef> instances)
    {
        Seed = seed;
        Instances = instances;
    }

    /// <summary>생성 시드. 같은 시드면 같은 파일이 나온다.</summary>
    public int Seed { get; }

    /// <summary>id 오름차순의 인스턴스.</summary>
    public ImmutableArray<NpcInstanceDef> Instances { get; }

    /// <summary>인스턴스 수.</summary>
    public int Count => Instances.Length;

    /// <summary>NpcStore 첨자로 조회.</summary>
    public NpcInstanceDef this[int index] => Instances[index];

    /// <summary>masterdata/npc_instances.json 로드.</summary>
    public static NpcInstanceTable Load(string path, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        NpcInstancesFile file = JsonSerializer.Deserialize(
            File.ReadAllText(path), WorldJsonContext.Default.NpcInstancesFile)
            ?? throw new InvalidDataException("npc_instances.json 을 읽지 못했다.");

        var instances = ImmutableArray.CreateBuilder<NpcInstanceDef>(file.Npcs.Length);

        foreach (NpcInstanceDto dto in file.Npcs)
        {
            if (!data.Archetypes.TryGet(dto.Archetype, out ArchetypeDef archetype))
            {
                throw new InvalidDataException($"npc_instances.json: NPC {dto.Id} 의 아키타입 '{dto.Archetype}' 이 없다.");
            }

            if (!data.Pois.TryGet(dto.HomePoi, out PoiDef home))
            {
                throw new InvalidDataException($"npc_instances.json: NPC {dto.Id} 의 집 '{dto.HomePoi}' 가 없다.");
            }

            PoiId workplace = default;

            if (dto.WorkplacePoi is { } workId)
            {
                if (!data.Pois.TryGet(workId, out PoiDef work))
                {
                    throw new InvalidDataException($"npc_instances.json: NPC {dto.Id} 의 일터 '{workId}' 가 없다.");
                }

                workplace = work.Code;
            }

            instances.Add(new NpcInstanceDef(
                dto.Id,
                archetype.Code,
                home.Zone,
                home.Code,
                workplace,
                new WorldPos(dto.SpawnPos.X, dto.SpawnPos.Y, dto.SpawnPos.Z)));
        }

        // id 오름차순이어야 첨자(Id - 1)와 순서가 맞는다.
        instances.Sort((a, b) => a.Id.CompareTo(b.Id));

        return new NpcInstanceTable(file.Seed, instances.ToImmutable());
    }
}

// --- zones.json / pois.json / context_buckets.json / npc_instances.json 의 JSON DTO ---

internal sealed record ZonesFile(ZoneDto[] Zones);

internal sealed record ZoneDto(
    string Id,
    int Code,
    string NameKey,
    string[]? Adjacent,
    string DefaultRegionState,
    string DefaultClimate,
    int Capacity);

internal sealed record PoisFile(PoiDto[] Pois);

internal sealed record PoiDto(
    string Id,
    int Code,
    string Zone,
    string Type,
    string Subtype,
    PosDto Pos,
    int Capacity,
    OpenHoursDto? OpenHours,
    string[]? Grants,
    string[]? AllowedArchetypes,
    string[]? Resources);

internal sealed record PosDto(float X, float Y, float Z);

internal sealed record OpenHoursDto(string From, string To);

internal sealed record BucketsFile(
    Dictionary<string, DimensionDto> Dimensions,
    int TotalKeys);

internal sealed record DimensionDto(
    string[] Values,
    Dictionary<string, int[]>? GameHours,
    Dictionary<string, string?>? WorldFlag);

internal sealed record NpcInstancesFile(int Version, int Seed, NpcInstanceDto[] Npcs);

internal sealed record NpcInstanceDto(
    int Id,
    string Archetype,
    string Zone,
    string HomePoi,
    string? WorkplacePoi,
    PosDto SpawnPos);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ZonesFile))]
[JsonSerializable(typeof(PoisFile))]
[JsonSerializable(typeof(BucketsFile))]
[JsonSerializable(typeof(NpcInstancesFile))]
internal sealed partial class WorldJsonContext : JsonSerializerContext;
