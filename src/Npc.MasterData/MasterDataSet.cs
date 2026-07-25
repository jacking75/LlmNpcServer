using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;

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
public sealed class MasterDataSet : IPlanVocabulary
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

    // docs/01 §11 의 Fallbacks(PlanTable)·Npcs(NpcRoster) 는 아직 없다.
    // fallback_plans.json 은 T1-54·T1-55, npc_instances.json 은 T1-56 에서 만들어진다.
    // 그 태스크에서 이 클래스에 프로퍼티를 추가한다.

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

// --- zones.json / pois.json / context_buckets.json 의 JSON DTO ---

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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ZonesFile))]
[JsonSerializable(typeof(PoisFile))]
[JsonSerializable(typeof(BucketsFile))]
internal sealed partial class WorldJsonContext : JsonSerializerContext;
