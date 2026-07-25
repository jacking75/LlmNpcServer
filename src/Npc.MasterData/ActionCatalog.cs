using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npc.Contracts;
using Npc.Core;

namespace Npc.MasterData;

/// <summary>액션 분류. docs/01 §2.2 의 카테고리.</summary>
public enum ActionCategory
{
    Movement,
    Labor,
    Social,
    Needs,
    Combat,
    Items,
    Misc,
}

/// <summary>파라미터 타입. docs/01 §2.3 의 7종. 늘리지 않는다.</summary>
public enum ParamType
{
    PoiRef,
    ItemRef,
    NpcRef,
    ZoneRef,
    Enum,
    Int,
    Route,
}

/// <summary>액션 파라미터 정의.</summary>
public sealed record ParamDef(
    string Name,
    ParamType Type,
    bool Required,
    ImmutableArray<string> EnumValues,
    string? DefaultText,
    int Min,
    int Max,
    int DefaultInt);

/// <summary>소요 시간 산정 방식.</summary>
public enum DurationKind
{
    /// <summary>base_s 고정.</summary>
    Fixed,

    /// <summary>거리 × per_meter_s. POI 거리 행렬을 쓴다.</summary>
    Distance,

    /// <summary>플랜 인자에서 온다 (duration_s 등).</summary>
    Param,

    /// <summary>지정한 시간대가 될 때까지.</summary>
    UntilTime,
}

/// <summary>소요 시간 정의.</summary>
public sealed record DurationDef(DurationKind Kind, int BaseSeconds, float PerMeterSeconds, string? Param);

/// <summary><see cref="NpcCommand"/> 의 어느 필드에 쓸 것인가.</summary>
public enum CommandField : byte
{
    TargetPoi,
    TargetPos,
    TargetNpc,
    TargetPlayer,
    Item,
    Amount,
    Animation,
    Dialogue,
    Visual,
    Archetype,
    Zone,
    Flags,
}

/// <summary>
/// 명령 필드에 넣을 값이 어디에서 오는가. docs/01 §2.1 의 map 값 문법을 컴파일한 결과다.
/// 실행기는 이 열거값 하나로 분기하므로 문자열 파싱이 런타임에 없다.
/// </summary>
public enum EmitSource : byte
{
    /// <summary>정수 리터럴. <c>Number</c> 를 그대로 쓴다.</summary>
    LiteralNumber,

    /// <summary>심볼 리터럴 (아이템 code / 열거 ordinal / 애니메이션·대사 id). <c>Number</c>.</summary>
    LiteralSymbol,

    /// <summary>컴파일된 스텝의 POI 심볼을 개체 바인딩한 값.</summary>
    StepPoi,

    /// <summary>컴파일된 스텝의 아이템.</summary>
    StepItem,

    /// <summary>컴파일된 스텝의 수량.</summary>
    StepCount,

    /// <summary>컴파일된 스텝의 열거 인자 (speed, topic, until_time …).</summary>
    StepArgFlags,

    /// <summary>컴파일된 스텝의 npc_ref 심볼.</summary>
    StepNpcRef,

    /// <summary>NPC 인스턴스의 home_poi.</summary>
    InstanceHome,

    /// <summary>NPC 인스턴스의 workplace_poi.</summary>
    InstanceWorkplace,

    /// <summary>자기 자신.</summary>
    InstanceSelf,

    /// <summary>NPC 가 지금 있는 존.</summary>
    InstanceZone,

    /// <summary>해당 WorldFlags 를 세우는 인벤토리 아이템 중 첫 번째. <c>Flag</c>.</summary>
    FirstWithFlag,
}

/// <summary>명령 필드 하나에 대한 값 배선.</summary>
/// <param name="Field">채울 <see cref="NpcCommand"/> 필드.</param>
/// <param name="Source">값의 출처.</param>
/// <param name="Number">리터럴 값.</param>
/// <param name="Flag">FirstWithFlag 일 때의 대상 플래그.</param>
/// <param name="ArgMap">
/// StepArgFlags 를 애니메이션·대사 id 로 옮겨야 할 때의 변환표.
/// 비어 있으면 ArgFlags 를 그대로 쓴다.
/// </param>
public readonly record struct EmitMapping(
    CommandField Field,
    EmitSource Source,
    int Number,
    WorldFlags Flag,
    ImmutableArray<ushort> ArgMap);

/// <summary>한 액션이 발행하는 명령 하나.</summary>
public sealed record EmitDef(
    NpcCommandKind Command,
    CommandPriority Priority,
    ImmutableArray<EmitMapping> Map);

/// <summary>액션 정의. docs/01 §2.</summary>
public sealed record ActionDef(
    ActionId Code,
    string Id,
    ActionCategory Category,
    string Description,
    ImmutableArray<ParamDef> Params,
    WorldFlags Requires,
    WorldFlags RequiresAny,
    WorldFlags Forbids,
    WorldFlags Grants,
    WorldFlags Clears,
    DurationDef Duration,
    int Cost,
    int DefaultTimeoutSeconds,
    ImmutableArray<EmitDef> Emits,
    ImmutableArray<GameEventKind> CompletesOn,
    ImmutableArray<GameEventKind> FailsOn)
{
    /// <summary>이름으로 파라미터 찾기. 로딩·검증 경로에서만 쓴다.</summary>
    public ParamDef? Param(string name)
    {
        foreach (ParamDef p in Params)
        {
            if (string.Equals(p.Name, name, StringComparison.Ordinal))
            {
                return p;
            }
        }

        return null;
    }

    /// <summary>
    /// 이 액션이 지금 상태에서 실행 가능한가. docs/01 §2.1.
    /// 비트 연산 세 번. 검증기 3단과 인지 스캔이 같은 판정을 쓴다.
    /// </summary>
    public bool IsSatisfiedBy(WorldFlags state) =>
        (Requires & ~state) == 0
        && (RequiresAny == WorldFlags.None || (RequiresAny & state) != 0)
        && (Forbids & state) == 0;
}

/// <summary>
/// actions.json 의 읽기 전용 인덱스. docs/01 §2 · docs/03 §5.
/// <b><see cref="ActionId"/>.Value 가 곧 배열 첨자다</b> — 조회에 해시맵이 필요 없다.
/// </summary>
public sealed class ActionCatalog
{
    private readonly ActionDef?[] _byCode;
    private readonly Dictionary<string, ActionDef> _byId;

    private ActionCatalog(
        ActionDef?[] byCode,
        Dictionary<string, ActionDef> byId,
        ImmutableArray<ActionDef> actions,
        ImmutableArray<string> animations,
        ImmutableArray<string> dialogues)
    {
        _byCode = byCode;
        _byId = byId;
        Actions = actions;
        Animations = animations;
        Dialogues = dialogues;
    }

    /// <summary>code 오름차순의 모든 액션.</summary>
    public ImmutableArray<ActionDef> Actions { get; }

    /// <summary>애니메이션 이름 표. 첨자가 곧 <see cref="AnimationId"/> 다. 정렬돼 있어 결정론적이다.</summary>
    public ImmutableArray<string> Animations { get; }

    /// <summary>대사 이름 표. 첨자가 곧 <see cref="DialogueId"/> 다. 정렬돼 있어 결정론적이다.</summary>
    public ImmutableArray<string> Dialogues { get; }

    /// <summary>정의된 액션 수.</summary>
    public int Count => Actions.Length;

    /// <summary>가장 큰 code.</summary>
    public int MaxCode => _byCode.Length - 1;

    /// <summary>code 로 조회. 첨자 접근이라 O(1) 이고 할당이 없다.</summary>
    public ActionDef this[ActionId code] =>
        (uint)code.Value < (uint)_byCode.Length && _byCode[code.Value] is { } def
            ? def
            : throw new ArgumentOutOfRangeException(nameof(code), $"정의되지 않은 action code: {code.Value}");

    /// <summary>문자열 id 로 조회. 로딩·검증 경로에서만 쓴다.</summary>
    public bool TryGet(string id, out ActionDef def) => _byId.TryGetValue(id, out def!);

    /// <summary>이 code 가 정의돼 있는가.</summary>
    public bool IsDefined(ActionId code) =>
        (uint)code.Value < (uint)_byCode.Length && _byCode[code.Value] is not null;

    /// <summary>masterdata/actions.json 로드.</summary>
    public static ActionCatalog Load(string path, ItemTable items)
    {
        using FileStream stream = File.OpenRead(path);
        ActionsFile? file = JsonSerializer.Deserialize(stream, ActionsJsonContext.Default.ActionsFile);

        if (file?.Actions is null || file.Actions.Length == 0)
        {
            throw new InvalidDataException($"actions.json 에서 액션을 읽지 못했다: {path}");
        }

        return Build(file, items);
    }

    /// <summary>JSON 문자열에서 로드. 테스트 픽스처용.</summary>
    public static ActionCatalog Parse(string json, ItemTable items)
    {
        ActionsFile? file = JsonSerializer.Deserialize(json, ActionsJsonContext.Default.ActionsFile);

        if (file?.Actions is null || file.Actions.Length == 0)
        {
            throw new InvalidDataException("actions.json 에서 액션을 읽지 못했다.");
        }

        return Build(file, items);
    }

    private static ActionCatalog Build(ActionsFile file, ItemTable items)
    {
        // 1단 — 애니메이션·대사 심볼을 먼저 모은다. 정렬해서 id 를 매기므로 결정론적이다.
        var animationNames = new SortedSet<string>(StringComparer.Ordinal);
        var dialogueNames = new SortedSet<string>(StringComparer.Ordinal);
        CollectSymbols(file, animationNames, dialogueNames);

        ImmutableArray<string> animations = [.. animationNames];
        ImmutableArray<string> dialogues = [.. dialogueNames];

        int maxCode = 0;
        foreach (ActionDto dto in file.Actions)
        {
            maxCode = Math.Max(maxCode, dto.Code);
        }

        var byCode = new ActionDef?[maxCode + 1];
        var byId = new Dictionary<string, ActionDef>(file.Actions.Length, StringComparer.Ordinal);
        var actions = ImmutableArray.CreateBuilder<ActionDef>(file.Actions.Length);

        foreach (ActionDto dto in file.Actions)
        {
            if (dto.Code is < 1 or > ushort.MaxValue)
            {
                throw new InvalidDataException($"actions.json: {dto.Id} 의 code {dto.Code} 가 범위를 벗어난다.");
            }

            if (byCode[dto.Code] is not null)
            {
                throw new InvalidDataException($"actions.json: code {dto.Code} 가 중복이다 ({dto.Id}).");
            }

            ImmutableArray<ParamDef> parameters = BuildParams(dto);

            var def = new ActionDef(
                new ActionId((ushort)dto.Code),
                dto.Id,
                ParseCategory(dto.Id, dto.Category),
                dto.Desc,
                parameters,
                ParseFlags(dto.Id, "requires", dto.Requires),
                ParseFlags(dto.Id, "requires_any", dto.RequiresAny),
                ParseFlags(dto.Id, "forbids", dto.Forbids),
                ParseFlags(dto.Id, "grants", dto.Grants),
                ParseFlags(dto.Id, "clears", dto.Clears),
                BuildDuration(dto),
                dto.Cost,
                dto.DefaultTimeoutS,
                BuildEmits(dto, parameters, items, animations, dialogues),
                ParseEvents(dto.Id, "completes_on", dto.CompletesOn),
                ParseEvents(dto.Id, "fails_on", dto.FailsOn));

            if (!byId.TryAdd(def.Id, def))
            {
                throw new InvalidDataException($"actions.json: id '{def.Id}' 가 중복이다.");
            }

            byCode[dto.Code] = def;
            actions.Add(def);
        }

        actions.Sort((a, b) => a.Code.Value.CompareTo(b.Code.Value));

        return new ActionCatalog(byCode, byId, actions.ToImmutable(), animations, dialogues);
    }

    /// <summary>Animation·Dialogue 필드에 실릴 수 있는 모든 문자열을 모은다.</summary>
    private static void CollectSymbols(ActionsFile file, SortedSet<string> animations, SortedSet<string> dialogues)
    {
        foreach (ActionDto dto in file.Actions)
        {
            foreach (EmitDto emit in dto.Emits ?? [])
            {
                foreach (KeyValuePair<string, JsonElement> entry in emit.Map ?? [])
                {
                    if (!TryParseField(entry.Key, out CommandField field))
                    {
                        throw new InvalidDataException(
                            $"actions.json: {dto.Id} 의 emits.map 에 없는 명령 필드 '{entry.Key}' 가 있다.");
                    }

                    SortedSet<string>? target = field switch
                    {
                        CommandField.Animation => animations,
                        CommandField.Dialogue => dialogues,
                        _ => null,
                    };

                    if (target is null || entry.Value.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string text = entry.Value.GetString()!;

                    if (!text.StartsWith('$'))
                    {
                        target.Add(text);
                        continue;
                    }

                    // "$topic" 같은 플랜 인자 → 그 파라미터의 열거값 전부가 심볼이 된다.
                    string paramName = text[1..];
                    if (dto.Params is not null
                        && dto.Params.TryGetValue(paramName, out ParamDto? p)
                        && p.Values is not null)
                    {
                        foreach (string v in p.Values)
                        {
                            target.Add(v);
                        }
                    }
                }
            }
        }
    }

    private static ImmutableArray<ParamDef> BuildParams(ActionDto dto)
    {
        if (dto.Params is null || dto.Params.Count == 0)
        {
            return [];
        }

        // Dictionary 순회 순서에 의존하지 않는다 (CLAUDE.md §2.3). 이름으로 정렬한다.
        var builder = ImmutableArray.CreateBuilder<ParamDef>(dto.Params.Count);

        foreach (string name in dto.Params.Keys.Order(StringComparer.Ordinal))
        {
            ParamDto p = dto.Params[name];
            ParamType type = ParseParamType(dto.Id, name, p.Type);

            if (type == ParamType.Enum && (p.Values is null || p.Values.Length == 0))
            {
                throw new InvalidDataException($"actions.json: {dto.Id}.{name} 이 enum 인데 values 가 없다.");
            }

            builder.Add(new ParamDef(
                name,
                type,
                p.Required,
                p.Values is null ? [] : [.. p.Values],
                p.Default.ValueKind == JsonValueKind.String ? p.Default.GetString() : null,
                p.Min ?? int.MinValue,
                p.Max ?? int.MaxValue,
                p.Default.ValueKind == JsonValueKind.Number ? p.Default.GetInt32() : 0));
        }

        return builder.ToImmutable();
    }

    private static DurationDef BuildDuration(ActionDto dto)
    {
        DurationDto d = dto.Duration ?? new DurationDto("fixed", 0, 0, null);

        DurationKind kind = d.Kind switch
        {
            "fixed" => DurationKind.Fixed,
            "distance" => DurationKind.Distance,
            "param" => DurationKind.Param,
            "until_time" => DurationKind.UntilTime,
            _ => throw new InvalidDataException($"actions.json: {dto.Id} 의 duration.kind '{d.Kind}' 를 모른다."),
        };

        if (kind is DurationKind.Param or DurationKind.UntilTime && string.IsNullOrEmpty(d.Param))
        {
            throw new InvalidDataException($"actions.json: {dto.Id} 의 duration 이 {d.Kind} 인데 param 이 없다.");
        }

        return new DurationDef(kind, d.BaseS, d.PerMeterS, d.Param);
    }

    private static ImmutableArray<EmitDef> BuildEmits(
        ActionDto dto,
        ImmutableArray<ParamDef> parameters,
        ItemTable items,
        ImmutableArray<string> animations,
        ImmutableArray<string> dialogues)
    {
        if (dto.Emits is null || dto.Emits.Length == 0)
        {
            throw new InvalidDataException($"actions.json: {dto.Id} 에 emits 가 없다.");
        }

        var builder = ImmutableArray.CreateBuilder<EmitDef>(dto.Emits.Length);

        foreach (EmitDto emit in dto.Emits)
        {
            if (!Enum.TryParse(emit.Command, out NpcCommandKind command))
            {
                throw new InvalidDataException($"actions.json: {dto.Id} 가 모르는 명령 '{emit.Command}' 를 발행한다.");
            }

            CommandPriority priority = CommandPriority.Normal;
            if (emit.Priority is not null && !Enum.TryParse(emit.Priority, out priority))
            {
                throw new InvalidDataException($"actions.json: {dto.Id} 의 priority '{emit.Priority}' 를 모른다.");
            }

            var mappings = ImmutableArray.CreateBuilder<EmitMapping>();

            // Dictionary 순회 순서에 의존하지 않는다 — 필드 이름으로 정렬한다.
            foreach (string fieldName in (emit.Map ?? []).Keys.Order(StringComparer.Ordinal))
            {
                TryParseField(fieldName, out CommandField field);
                mappings.Add(BuildMapping(
                    dto, parameters, items, animations, dialogues, field, emit.Map![fieldName]));
            }

            builder.Add(new EmitDef(command, priority, mappings.ToImmutable()));
        }

        return builder.ToImmutable();
    }

    private static EmitMapping BuildMapping(
        ActionDto dto,
        ImmutableArray<ParamDef> parameters,
        ItemTable items,
        ImmutableArray<string> animations,
        ImmutableArray<string> dialogues,
        CommandField field,
        JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return new EmitMapping(field, EmitSource.LiteralNumber, value.GetInt32(), WorldFlags.None, []);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"actions.json: {dto.Id} 의 emits.map.{field} 값이 문자열도 숫자도 아니다.");
        }

        string text = value.GetString()!;

        if (!text.StartsWith('$'))
        {
            return new EmitMapping(
                field, EmitSource.LiteralSymbol, ResolveSymbol(dto, items, animations, dialogues, field, text),
                WorldFlags.None, []);
        }

        string symbol = text[1..];

        switch (symbol)
        {
            case "home":
                return new EmitMapping(field, EmitSource.InstanceHome, 0, WorldFlags.None, []);
            case "workplace":
                return new EmitMapping(field, EmitSource.InstanceWorkplace, 0, WorldFlags.None, []);
            case "self":
                return new EmitMapping(field, EmitSource.InstanceSelf, 0, WorldFlags.None, []);
            case "zone":
                return new EmitMapping(field, EmitSource.InstanceZone, 0, WorldFlags.None, []);
        }

        if (symbol.StartsWith("first:", StringComparison.Ordinal))
        {
            string flagId = symbol["first:".Length..];
            if (!WorldFlagTable.TryParse(flagId, out WorldFlags flag))
            {
                throw new InvalidDataException(
                    $"actions.json: {dto.Id} 가 world_flags.json 에 없는 플래그 '{flagId}' 를 참조한다.");
            }

            return new EmitMapping(field, EmitSource.FirstWithFlag, 0, flag, []);
        }

        // 남은 것은 플랜 인자 참조다.
        ParamDef? param = null;
        foreach (ParamDef p in parameters)
        {
            if (string.Equals(p.Name, symbol, StringComparison.Ordinal))
            {
                param = p;
                break;
            }
        }

        if (param is null)
        {
            throw new InvalidDataException(
                $"actions.json: {dto.Id} 의 emits 가 없는 파라미터 '${symbol}' 을 참조한다.");
        }

        EmitSource source = param.Type switch
        {
            ParamType.PoiRef or ParamType.Route or ParamType.ZoneRef => EmitSource.StepPoi,
            ParamType.ItemRef => EmitSource.StepItem,
            ParamType.Int => EmitSource.StepCount,
            ParamType.Enum => EmitSource.StepArgFlags,
            ParamType.NpcRef => EmitSource.StepNpcRef,
            _ => throw new InvalidDataException($"actions.json: {dto.Id}.{symbol} 의 타입을 명령에 실을 수 없다."),
        };

        // Animation/Dialogue 필드는 열거 ordinal 이 아니라 심볼 id 를 요구한다.
        ImmutableArray<ushort> argMap = [];
        if (source == EmitSource.StepArgFlags && field is CommandField.Animation or CommandField.Dialogue)
        {
            ImmutableArray<string> table = field == CommandField.Animation ? animations : dialogues;
            var map = ImmutableArray.CreateBuilder<ushort>(param.EnumValues.Length);

            foreach (string v in param.EnumValues)
            {
                map.Add((ushort)table.IndexOf(v));
            }

            argMap = map.ToImmutable();
        }

        return new EmitMapping(field, source, 0, WorldFlags.None, argMap);
    }

    private static int ResolveSymbol(
        ActionDto dto,
        ItemTable items,
        ImmutableArray<string> animations,
        ImmutableArray<string> dialogues,
        CommandField field,
        string text)
    {
        switch (field)
        {
            case CommandField.Item:
                if (!items.TryGet(text, out ItemDef item))
                {
                    throw new InvalidDataException(
                        $"actions.json: {dto.Id} 가 items.json 에 없는 아이템 '{text}' 를 참조한다.");
                }

                return item.Code.Value;

            case CommandField.Animation:
                return animations.IndexOf(text);

            case CommandField.Dialogue:
                return dialogues.IndexOf(text);

            case CommandField.Visual:
                return Enum.TryParse(text, out VisualState visual)
                    ? (int)visual
                    : throw new InvalidDataException($"actions.json: {dto.Id} 의 Visual '{text}' 를 모른다.");

            case CommandField.Flags:
                // Flags 리터럴은 액션 파라미터의 열거값과 같은 표기를 쓴다 ("run", "walk").
                // 열거형 멤버는 PascalCase 이므로 대소문자를 무시하고 맞춘다.
                if (Enum.TryParse(text, ignoreCase: true, out MoveSpeed speed))
                {
                    return (int)speed;
                }

                if (Enum.TryParse(text, ignoreCase: true, out CombatActionKind combat))
                {
                    return (int)combat;
                }

                throw new InvalidDataException($"actions.json: {dto.Id} 의 Flags '{text}' 를 모른다.");

            default:
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                    ? n
                    : throw new InvalidDataException(
                        $"actions.json: {dto.Id} 의 emits.map.{field} 리터럴 '{text}' 를 해석할 수 없다.");
        }
    }

    private static WorldFlags ParseFlags(string actionId, string field, string[]? ids)
    {
        WorldFlags flags = WorldFlags.None;

        foreach (string id in ids ?? [])
        {
            if (!WorldFlagTable.TryParse(id, out WorldFlags flag))
            {
                throw new InvalidDataException(
                    $"actions.json: {actionId}.{field} 가 world_flags.json 에 없는 플래그 '{id}' 를 참조한다.");
            }

            flags |= flag;
        }

        return flags;
    }

    private static ImmutableArray<GameEventKind> ParseEvents(string actionId, string field, string[]? names)
    {
        if (names is null || names.Length == 0)
        {
            throw new InvalidDataException($"actions.json: {actionId} 에 {field} 가 없다.");
        }

        var builder = ImmutableArray.CreateBuilder<GameEventKind>(names.Length);

        foreach (string name in names)
        {
            if (!Enum.TryParse(name, out GameEventKind kind))
            {
                throw new InvalidDataException($"actions.json: {actionId}.{field} 의 '{name}' 를 모른다.");
            }

            builder.Add(kind);
        }

        return builder.ToImmutable();
    }

    private static bool TryParseField(string name, out CommandField field) => Enum.TryParse(name, out field);

    private static ActionCategory ParseCategory(string actionId, string category) => category switch
    {
        "movement" => ActionCategory.Movement,
        "labor" => ActionCategory.Labor,
        "social" => ActionCategory.Social,
        "needs" => ActionCategory.Needs,
        "combat" => ActionCategory.Combat,
        "items" => ActionCategory.Items,
        "misc" => ActionCategory.Misc,
        _ => throw new InvalidDataException($"actions.json: {actionId} 의 category '{category}' 를 모른다."),
    };

    private static ParamType ParseParamType(string actionId, string paramName, string type) => type switch
    {
        "poi_ref" => ParamType.PoiRef,
        "item_ref" => ParamType.ItemRef,
        "npc_ref" => ParamType.NpcRef,
        "zone_ref" => ParamType.ZoneRef,
        "enum" => ParamType.Enum,
        "int" => ParamType.Int,
        "route" => ParamType.Route,
        _ => throw new InvalidDataException(
            $"actions.json: {actionId}.{paramName} 의 type '{type}' 은 docs/01 §2.3 의 7종이 아니다."),
    };

    // --- JSON DTO. 소스 생성기로 직렬화한다 (런타임 리플렉션 0). ---

    internal sealed record ActionsFile(ActionDto[] Actions);

    internal sealed record ActionDto(
        string Id,
        int Code,
        string Category,
        string Desc,
        Dictionary<string, ParamDto>? Params,
        string[]? Requires,
        string[]? RequiresAny,
        string[]? Forbids,
        string[]? Grants,
        string[]? Clears,
        DurationDto? Duration,
        int Cost,
        int DefaultTimeoutS,
        EmitDto[]? Emits,
        string[]? CompletesOn,
        string[]? FailsOn);

    internal sealed record ParamDto(
        string Type,
        bool Required,
        string[]? Values,
        JsonElement Default,
        int? Min,
        int? Max);

    internal sealed record DurationDto(string Kind, int BaseS, float PerMeterS, string? Param);

    internal sealed record EmitDto(string Command, string? Priority, Dictionary<string, JsonElement>? Map);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ActionCatalog.ActionsFile))]
internal sealed partial class ActionsJsonContext : JsonSerializerContext;
