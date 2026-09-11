using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;

namespace Npc.MasterData;

/// <summary>성향 비교 연산자.</summary>
public enum TraitComparison
{
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Equal,
}

/// <summary>성향 조건 하나. 예: <c>courage &lt; 40</c>.</summary>
public readonly record struct TraitCondition(TraitKind Kind, TraitComparison Comparison, int Value)
{
    /// <summary>이 성향이 조건을 만족하는가.</summary>
    public bool Matches(in TraitSet traits)
    {
        int actual = traits[Kind];

        return Comparison switch
        {
            TraitComparison.Less => actual < Value,
            TraitComparison.LessOrEqual => actual <= Value,
            TraitComparison.Greater => actual > Value,
            TraitComparison.GreaterOrEqual => actual >= Value,
            TraitComparison.Equal => actual == Value,
            _ => false,
        };
    }
}

/// <summary>
/// 인터럽트 규칙 하나. docs/01 §7.
/// <c>then</c> 은 즉시 실행되고, <see cref="Urgency"/> 는 재계획 큐에 점수로 들어간다.
/// </summary>
/// <param name="Id">규칙 id.</param>
/// <param name="Priority">우선순위. 높은 것이 먼저 매칭된다.</param>
/// <param name="Event">이 종류의 이벤트에만 반응한다. null 이면 이벤트를 가리지 않는다.</param>
/// <param name="AnyFlag">하나라도 서 있으면 참. None 이면 검사하지 않는다.</param>
/// <param name="AllFlag">전부 서 있어야 참.</param>
/// <param name="NoneFlag">하나도 서 있지 않아야 참.</param>
/// <param name="Traits">성향 조건. 전부 만족해야 한다.</param>
/// <param name="CombatCapable">아키타입의 전투 가능 여부 조건. null 이면 검사하지 않는다.</param>
/// <param name="Action">즉시 실행할 액션.</param>
/// <param name="Poi">액션의 POI 인자 심볼. 없으면 <see cref="PoiSymbol.None"/>.</param>
/// <param name="TargetsThreat">
/// <c>$threat</c> 를 대상으로 하는가. 위협의 정체는 규칙이 아니라 이벤트가 알려준다.
/// </param>
/// <param name="NpcRef">
/// 대상 npc_ref 코드 (B-06). <see cref="NpcRefCodes.None"/> 이면 <c>$threat</c> 나 대상 없음이다.
///
/// <b>인터럽트는 아키타입을 이름으로 찍지 않는다</b> — 즉시 반응이 목적이라 근접 판정을 할
/// 시간이 없다. 지금 쓰이는 것은 <c>nearest:hostile_player</c> 하나로, 대상을 플랜이 아니라
/// <c>NpcStore.HostilePlayer</c> 에서 읽는다.
/// </param>
/// <param name="Amount">액션의 정수 인자 (duration_s, count). 없으면 0.</param>
/// <param name="Urgency">재계획 긴급도 0~100.</param>
public sealed record InterruptRule(
    string Id,
    int Priority,
    GameEventKind? Event,
    WorldFlags AnyFlag,
    WorldFlags AllFlag,
    WorldFlags NoneFlag,
    ImmutableArray<TraitCondition> Traits,
    bool? CombatCapable,
    ActionId Action,
    PoiSymbol Poi,
    bool TargetsThreat,
    ushort NpcRef,
    int Amount,
    int Urgency);

/// <summary>
/// interrupts.json 의 읽기 전용 인덱스. docs/01 §7.
///
/// <b>LLM 이 개입하지 않는다.</b> 인터럽트는 반응 속도가 생명이라 결정론 규칙으로만 돈다.
/// 매칭은 우선순위 내림차순, 같으면 id 오름차순 — 리플레이가 일치하려면 순서가 결정론이어야 한다.
/// </summary>
public sealed class InterruptRules
{
    private readonly ImmutableArray<InterruptRule> _rules;

    private InterruptRules(ImmutableArray<InterruptRule> rules) => _rules = rules;

    /// <summary>우선순위 내림차순(같으면 id 오름차순)으로 정렬된 규칙.</summary>
    public ImmutableArray<InterruptRule> Rules => _rules;

    /// <summary>규칙 수.</summary>
    public int Count => _rules.Length;

    /// <summary>
    /// 이 이벤트와 상태에 맞는 규칙 중 가장 우선순위가 높은 것. 없으면 false.
    /// 액션이 그 아키타입에 허용되지 않으면 건너뛴다 — 못 하는 행동을 강제하면 런타임에서 실패한다.
    /// </summary>
    public bool TryMatch(in GameEvent ev, WorldFlags flags, ArchetypeDef archetype, out InterruptRule matched)
    {
        foreach (InterruptRule rule in _rules)
        {
            if (rule.Event is { } kind && kind != ev.Kind)
            {
                continue;
            }

            if (!MatchesState(rule, flags, archetype))
            {
                continue;
            }

            matched = rule;
            return true;
        }

        matched = null!;
        return false;
    }

    /// <summary>이벤트와 무관하게 상태만으로 매칭한다. 틱 경계의 상태 점검용.</summary>
    public bool TryMatchState(WorldFlags flags, ArchetypeDef archetype, out InterruptRule matched)
    {
        foreach (InterruptRule rule in _rules)
        {
            if (rule.Event is not null)
            {
                continue;
            }

            if (!MatchesState(rule, flags, archetype))
            {
                continue;
            }

            matched = rule;
            return true;
        }

        matched = null!;
        return false;
    }

    private static bool MatchesState(InterruptRule rule, WorldFlags flags, ArchetypeDef archetype)
    {
        if (rule.AnyFlag != WorldFlags.None && (rule.AnyFlag & flags) == 0)
        {
            return false;
        }

        if ((rule.AllFlag & ~flags) != 0)
        {
            return false;
        }

        if ((rule.NoneFlag & flags) != 0)
        {
            return false;
        }

        if (rule.CombatCapable is { } combat && combat != archetype.CombatCapable)
        {
            return false;
        }

        foreach (TraitCondition condition in rule.Traits)
        {
            if (!condition.Matches(archetype.Traits))
            {
                return false;
            }
        }

        return archetype.Allows(rule.Action);
    }

    /// <summary>masterdata/interrupts.json 로드.</summary>
    public static InterruptRules Load(string path, ActionCatalog actions)
    {
        using FileStream stream = File.OpenRead(path);
        InterruptsFile? file = JsonSerializer.Deserialize(stream, InterruptsJsonContext.Default.InterruptsFile);

        if (file?.Rules is null || file.Rules.Length == 0)
        {
            throw new InvalidDataException($"interrupts.json 에서 규칙을 읽지 못했다: {path}");
        }

        return Build(file, actions);
    }

    /// <summary>JSON 문자열에서 로드. 테스트 픽스처용.</summary>
    public static InterruptRules Parse(string json, ActionCatalog actions)
    {
        InterruptsFile? file = JsonSerializer.Deserialize(json, InterruptsJsonContext.Default.InterruptsFile);

        if (file?.Rules is null || file.Rules.Length == 0)
        {
            throw new InvalidDataException("interrupts.json 에서 규칙을 읽지 못했다.");
        }

        return Build(file, actions);
    }

    private static InterruptRules Build(InterruptsFile file, ActionCatalog actions)
    {
        var builder = ImmutableArray.CreateBuilder<InterruptRule>(file.Rules.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (RuleDto dto in file.Rules)
        {
            if (!seen.Add(dto.Id))
            {
                throw new InvalidDataException($"interrupts.json: 규칙 id '{dto.Id}' 가 중복이다.");
            }

            if (!actions.TryGet(dto.Then.Action, out ActionDef action))
            {
                throw new InvalidDataException(
                    $"interrupts.json: 규칙 '{dto.Id}' 가 카탈로그에 없는 액션 '{dto.Then.Action}' 를 쓴다.");
            }

            GameEventKind? eventKind = null;
            if (dto.When.Event is { } eventName)
            {
                if (!Enum.TryParse(eventName, out GameEventKind kind))
                {
                    throw new InvalidDataException($"interrupts.json: 규칙 '{dto.Id}' 의 event '{eventName}' 를 모른다.");
                }

                eventKind = kind;
            }

            builder.Add(new InterruptRule(
                dto.Id,
                dto.Priority,
                eventKind,
                ParseFlags(dto.Id, "any_flag", dto.When.AnyFlag),
                ParseFlags(dto.Id, "all_flag", dto.When.AllFlag),
                ParseFlags(dto.Id, "none_flag", dto.When.NoneFlag),
                ParseTraits(dto.Id, dto.When.ArchetypeTrait),
                dto.When.CombatCapable,
                action.Code,
                ParsePoi(dto.Id, ReadString(dto.Then.Params, "poi")),
                string.Equals(ReadString(dto.Then.Params, "target"), "$threat", StringComparison.Ordinal),
                ParseNpcRef(dto.Id, ReadString(dto.Then.Params, "target")),
                ReadInt(dto.Then.Params, "duration_s") + ReadInt(dto.Then.Params, "count"),
                dto.Replan?.Urgency ?? 0));
        }

        // 우선순위 내림차순, 같으면 id 오름차순. 매칭이 결정론이어야 리플레이가 일치한다.
        builder.Sort((a, b) =>
        {
            int byPriority = b.Priority.CompareTo(a.Priority);
            return byPriority != 0 ? byPriority : string.CompareOrdinal(a.Id, b.Id);
        });

        return new InterruptRules(builder.ToImmutable());
    }

    private static WorldFlags ParseFlags(string ruleId, string field, string[]? ids)
    {
        WorldFlags flags = WorldFlags.None;

        foreach (string id in ids ?? [])
        {
            if (!WorldFlagTable.TryParse(id, out WorldFlags flag))
            {
                throw new InvalidDataException(
                    $"interrupts.json: 규칙 '{ruleId}'.{field} 가 없는 플래그 '{id}' 를 참조한다.");
            }

            flags |= flag;
        }

        return flags;
    }

    private static ImmutableArray<TraitCondition> ParseTraits(string ruleId, Dictionary<string, string>? traits)
    {
        if (traits is null || traits.Count == 0)
        {
            return [];
        }

        // Dictionary 순회 순서에 의존하지 않는다 (CLAUDE.md §2.3).
        var builder = ImmutableArray.CreateBuilder<TraitCondition>(traits.Count);

        foreach (string name in traits.Keys.Order(StringComparer.Ordinal))
        {
            if (!Enum.TryParse(name, ignoreCase: true, out TraitKind kind))
            {
                throw new InvalidDataException($"interrupts.json: 규칙 '{ruleId}' 의 성향 '{name}' 을 모른다.");
            }

            string expr = traits[name].Trim();
            (TraitComparison comparison, int offset) = expr switch
            {
                _ when expr.StartsWith(">=", StringComparison.Ordinal) => (TraitComparison.GreaterOrEqual, 2),
                _ when expr.StartsWith("<=", StringComparison.Ordinal) => (TraitComparison.LessOrEqual, 2),
                _ when expr.StartsWith("==", StringComparison.Ordinal) => (TraitComparison.Equal, 2),
                _ when expr.StartsWith('>') => (TraitComparison.Greater, 1),
                _ when expr.StartsWith('<') => (TraitComparison.Less, 1),
                _ => throw new InvalidDataException(
                    $"interrupts.json: 규칙 '{ruleId}' 의 성향 조건 '{expr}' 을 해석할 수 없다."),
            };

            if (!int.TryParse(expr[offset..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                throw new InvalidDataException(
                    $"interrupts.json: 규칙 '{ruleId}' 의 성향 조건 '{expr}' 에 정수가 없다.");
            }

            builder.Add(new TraitCondition(kind, comparison, value));
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// 인터럽트의 <c>target</c> 을 npc_ref 코드로 (B-06).
    ///
    /// <para>
    /// <b>모르는 값은 기동 실패다.</b> 조용히 "대상 없음" 으로 두면 규칙이 있는데 아무 일도
    /// 안 나고, 그 증상은 "인터럽트가 가끔 안 먹는다" 로만 보인다.
    /// </para>
    ///
    /// <para>
    /// <b>아키타입 이름은 받지 않는다.</b> <c>nearest:&lt;archetype&gt;</c> 는 근접 판정을
    /// 게임서버에 넘기는 값이라 즉시 반응하는 인터럽트와 맞지 않는다 — 평시 플랜의 것이다.
    /// </para>
    /// </summary>
    private static ushort ParseNpcRef(string ruleId, string? text) => text switch
    {
        null or "$threat" => NpcRefCodes.None,
        "self" => NpcRefCodes.Self(),
        "nearest:hostile_player" => NpcRefCodes.HostilePlayer(),
        _ => throw new InvalidDataException(
            $"interrupts.json: 규칙 '{ruleId}' 의 target '{text}' 를 모른다. "
            + "$threat / self / nearest:hostile_player 만 쓴다."),
    };

    private static PoiSymbol ParsePoi(string ruleId, string? text)
    {
        if (text is null)
        {
            return PoiSymbol.None;
        }

        if (!PoiSymbols.TryParse(text, out PoiSymbol symbol))
        {
            throw new InvalidDataException(
                $"interrupts.json: 규칙 '{ruleId}' 의 poi '{text}' 는 허용된 POI 심볼이 아니다.");
        }

        return symbol;
    }

    private static string? ReadString(Dictionary<string, JsonElement>? map, string key) =>
        map is not null && map.TryGetValue(key, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int ReadInt(Dictionary<string, JsonElement>? map, string key) =>
        map is not null && map.TryGetValue(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 0;

    // --- JSON DTO. 소스 생성기로 직렬화한다 (런타임 리플렉션 0). ---

    internal sealed record InterruptsFile(RuleDto[] Rules);

    internal sealed record RuleDto(string Id, int Priority, WhenDto When, ThenDto Then, ReplanDto? Replan);

    internal sealed record WhenDto(
        string[]? AnyFlag,
        string[]? AllFlag,
        string[]? NoneFlag,
        string? Event,
        Dictionary<string, string>? ArchetypeTrait,
        bool? CombatCapable);

    internal sealed record ThenDto(string Action, Dictionary<string, JsonElement>? Params);

    internal sealed record ReplanDto(int Urgency);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(InterruptRules.InterruptsFile))]
internal sealed partial class InterruptsJsonContext : JsonSerializerContext;
