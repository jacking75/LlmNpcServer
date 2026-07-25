using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Npc.MasterData.Validation;

/// <summary>검증 위반 하나. docs/01 §11 의 V1~V11.</summary>
/// <param name="Code">V1 ~ V11.</param>
/// <param name="Detail">무엇이 어디서 어긋났는지. 사람이 읽고 고칠 수 있어야 한다.</param>
public readonly record struct MasterDataViolation(string Code, string Detail);

/// <summary>검사를 건너뛴 규칙과 이유.</summary>
public readonly record struct SkippedRule(string Code, string Reason);

/// <summary>검증 결과.</summary>
public sealed record MasterDataValidationReport(
    ImmutableArray<MasterDataViolation> Violations,
    ImmutableArray<SkippedRule> Skipped)
{
    /// <summary>위반이 없으면 참. 건너뛴 규칙은 통과로 본다.</summary>
    public bool IsValid => Violations.IsEmpty;

    /// <summary>이 코드의 위반이 있는가.</summary>
    public bool HasViolation(string code)
    {
        foreach (MasterDataViolation v in Violations)
        {
            if (string.Equals(v.Code, code, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>검증 실패. <b>기동 실패</b>로 처리한다 — 경고 후 진행을 허용하지 않는다.</summary>
public sealed class MasterDataValidationException : Exception
{
    /// <summary>검증 실패 예외.</summary>
    public MasterDataValidationException(MasterDataValidationReport report)
        : base(BuildMessage(report)) => Report = report;

    /// <summary>메시지만 있는 생성자. 호출자가 직접 쓰지 않는다.</summary>
    public MasterDataValidationException(string message)
        : base(message) => Report = new MasterDataValidationReport([], []);

    /// <summary>메시지와 내부 예외.</summary>
    public MasterDataValidationException(string message, Exception innerException)
        : base(message, innerException) => Report = new MasterDataValidationReport([], []);

    /// <summary>기본 생성자.</summary>
    public MasterDataValidationException()
        : base("마스터데이터 검증에 실패했다.") => Report = new MasterDataValidationReport([], []);

    /// <summary>실패 보고서.</summary>
    public MasterDataValidationReport Report { get; }

    private static string BuildMessage(MasterDataValidationReport report)
    {
        var lines = new List<string> { $"마스터데이터 검증 실패 {report.Violations.Length}건:" };

        foreach (MasterDataViolation v in report.Violations)
        {
            lines.Add($"  {v.Code}  {v.Detail}");
        }

        return string.Join('\n', lines);
    }
}

/// <summary>검증 옵션. P1 시점에 아직 존재하지 않는 입력을 밖에서 넣어준다.</summary>
/// <param name="PromptPrefixTokens">
/// 조립된 프롬프트 프리픽스의 토큰 수 (V9). null 이면 V9 를 건너뛴다.
/// 프리픽스 조립은 Npc.Llm 소관이고 P2 에서 생긴다 (docs/01 §10).
/// </param>
public sealed record MasterDataValidationOptions(int? PromptPrefixTokens = null);

/// <summary>
/// 마스터데이터 검증 V1~V11. docs/01 §11.
///
/// <b>원본 JSON 을 직접 읽는다.</b> 컴파일된 <see cref="MasterDataSet"/> 을 보지 않는 이유는,
/// 로더가 중복 code 같은 것을 이미 예외로 걷어내 버려서 V1 을 확인할 수 없기 때문이다.
/// docs/01 §11 의 로딩 규약도 "전량 로드 → 검증 → 인덱스 컴파일" 순서다.
/// </summary>
public static class MasterDataValidator
{
    /// <summary>검증 후 실패면 예외. 기동 경로에서 쓴다.</summary>
    public static void ValidateOrThrow(string masterDataDirectory, MasterDataValidationOptions? options = null)
    {
        MasterDataValidationReport report = Validate(masterDataDirectory, options);

        if (!report.IsValid)
        {
            throw new MasterDataValidationException(report);
        }
    }

    /// <summary>V1~V11 전부 실행. 위반을 모아서 돌려준다 (첫 실패에서 멈추지 않는다).</summary>
    public static MasterDataValidationReport Validate(
        string masterDataDirectory, MasterDataValidationOptions? options = null)
    {
        options ??= new MasterDataValidationOptions();

        var violations = ImmutableArray.CreateBuilder<MasterDataViolation>();
        var skipped = ImmutableArray.CreateBuilder<SkippedRule>();

        var files = new Dictionary<string, JsonDocument>(StringComparer.Ordinal);

        try
        {
            foreach (string name in new[]
            {
                "world_flags.json", "items.json", "actions.json", "zones.json",
                "pois.json", "archetypes.json", "context_buckets.json", "interrupts.json",
            })
            {
                string path = Path.Combine(masterDataDirectory, name);
                if (!File.Exists(path))
                {
                    violations.Add(new MasterDataViolation("V0", $"필수 파일이 없다: {name}"));
                    continue;
                }

                files[name] = JsonDocument.Parse(File.ReadAllText(path));
            }

            if (violations.Count > 0)
            {
                return new MasterDataValidationReport(violations.ToImmutable(), skipped.ToImmutable());
            }

            string? fallbackPath = Path.Combine(masterDataDirectory, "fallback_plans.json");
            JsonDocument? fallbacks = File.Exists(fallbackPath)
                ? JsonDocument.Parse(File.ReadAllText(fallbackPath))
                : null;

            var ctx = new Context(files, fallbacks, masterDataDirectory);

            CheckV1(ctx, violations);
            CheckV2(ctx, violations);
            CheckV3(ctx, violations);
            CheckV4(ctx, violations);
            CheckV5(ctx, violations);
            CheckV6(ctx, violations);
            CheckV7(ctx, violations, skipped);
            CheckV8(ctx, violations, skipped);
            CheckV9(options, violations, skipped);
            CheckV10(ctx, violations);
            CheckV11(ctx, violations);

            fallbacks?.Dispose();
        }
        finally
        {
            foreach (JsonDocument doc in files.Values)
            {
                doc.Dispose();
            }
        }

        return new MasterDataValidationReport(violations.ToImmutable(), skipped.ToImmutable());
    }

    // ---------------------------------------------------------------- V1

    /// <summary>V1 — 모든 code 값이 파일 내에서 유일하다.</summary>
    private static void CheckV1(Context ctx, ImmutableArray<MasterDataViolation>.Builder violations)
    {
        foreach ((string file, string array) in new[]
        {
            ("items.json", "items"), ("actions.json", "actions"),
            ("zones.json", "zones"), ("pois.json", "pois"), ("archetypes.json", "archetypes"),
        })
        {
            var seen = new Dictionary<int, string>();

            foreach (JsonElement e in ctx.Array(file, array))
            {
                int code = e.GetProperty("code").GetInt32();
                string id = e.GetProperty("id").GetString()!;

                if (seen.TryGetValue(code, out string? other))
                {
                    violations.Add(new MasterDataViolation(
                        "V1", $"{file}: code {code} 가 중복이다 ('{other}' 와 '{id}')."));
                }
                else
                {
                    seen[code] = id;
                }
            }
        }

        // 규칙 id 는 code 가 없으므로 id 중복을 본다.
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement rule in ctx.Array("interrupts.json", "rules"))
        {
            string id = rule.GetProperty("id").GetString()!;
            if (!ruleIds.Add(id))
            {
                violations.Add(new MasterDataViolation("V1", $"interrupts.json: 규칙 id '{id}' 가 중복이다."));
            }
        }

        // world_flags 의 bit 중복
        var bits = new HashSet<int>();
        foreach (JsonElement flag in ctx.Array("world_flags.json", "flags"))
        {
            int bit = flag.GetProperty("bit").GetInt32();
            if (!bits.Add(bit))
            {
                violations.Add(new MasterDataViolation("V1", $"world_flags.json: bit {bit} 가 중복이다."));
            }
        }
    }

    // ---------------------------------------------------------------- V2

    /// <summary>V2 — 모든 flag id 참조가 world_flags.json 에 존재한다.</summary>
    private static void CheckV2(Context ctx, ImmutableArray<MasterDataViolation>.Builder violations)
    {
        HashSet<string> flags = ctx.FlagIds;

        void Check(string where, IEnumerable<string> ids)
        {
            foreach (string id in ids)
            {
                if (!flags.Contains(id))
                {
                    violations.Add(new MasterDataViolation("V2", $"{where} 가 없는 플래그 '{id}' 를 참조한다."));
                }
            }
        }

        foreach (JsonElement item in ctx.Array("items.json", "items"))
        {
            Check($"items.json:{item.GetProperty("id").GetString()}.grants", Strings(item, "grants"));
        }

        foreach (JsonElement action in ctx.Array("actions.json", "actions"))
        {
            string id = action.GetProperty("id").GetString()!;
            foreach (string field in new[] { "requires", "requires_any", "forbids", "grants", "clears" })
            {
                Check($"actions.json:{id}.{field}", Strings(action, field));
            }
        }

        foreach (JsonElement poi in ctx.Array("pois.json", "pois"))
        {
            Check($"pois.json:{poi.GetProperty("id").GetString()}.grants", Strings(poi, "grants"));
        }

        foreach (JsonElement rule in ctx.Array("interrupts.json", "rules"))
        {
            string id = rule.GetProperty("id").GetString()!;
            if (!rule.TryGetProperty("when", out JsonElement when))
            {
                continue;
            }

            foreach (string field in new[] { "any_flag", "all_flag", "none_flag" })
            {
                Check($"interrupts.json:{id}.when.{field}", Strings(when, field));
            }
        }

        JsonElement dimensions = ctx.Root("context_buckets.json").GetProperty("dimensions");
        foreach (JsonProperty dimension in dimensions.EnumerateObject())
        {
            if (!dimension.Value.TryGetProperty("world_flag", out JsonElement map))
            {
                continue;
            }

            foreach (JsonProperty entry in map.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                Check($"context_buckets.json:{dimension.Name}.{entry.Name}", [entry.Value.GetString()!]);
            }
        }
    }

    // ---------------------------------------------------------------- V3

    /// <summary>V3 — 모든 poi/item/zone/action 참조가 대상 파일에 존재한다.</summary>
    private static void CheckV3(Context ctx, ImmutableArray<MasterDataViolation>.Builder violations)
    {
        HashSet<string> items = ctx.ItemIds;
        HashSet<string> zones = ctx.ZoneIds;
        HashSet<string> actions = ctx.ActionIds;
        HashSet<string> recipes = ctx.RecipeIds;

        foreach (JsonElement recipe in ctx.Array("items.json", "recipes"))
        {
            string id = recipe.GetProperty("id").GetString()!;

            foreach (string field in new[] { "inputs", "outputs" })
            {
                if (!recipe.TryGetProperty(field, out JsonElement slots))
                {
                    continue;
                }

                foreach (JsonElement slot in slots.EnumerateArray())
                {
                    string item = slot.GetProperty("item").GetString()!;
                    if (!items.Contains(item))
                    {
                        violations.Add(new MasterDataViolation(
                            "V3", $"items.json: 레시피 '{id}'.{field} 가 없는 아이템 '{item}' 을 쓴다."));
                    }
                }
            }
        }

        foreach (JsonElement poi in ctx.Array("pois.json", "pois"))
        {
            string id = poi.GetProperty("id").GetString()!;
            string zone = poi.GetProperty("zone").GetString()!;

            if (!zones.Contains(zone))
            {
                violations.Add(new MasterDataViolation("V3", $"pois.json: {id} 가 없는 존 '{zone}' 을 가리킨다."));
            }

            foreach (string resource in Strings(poi, "resources"))
            {
                if (!items.Contains(resource))
                {
                    violations.Add(new MasterDataViolation(
                        "V3", $"pois.json: {id} 가 없는 아이템 '{resource}' 을 낸다."));
                }
            }
        }

        foreach (JsonElement zone in ctx.Array("zones.json", "zones"))
        {
            string id = zone.GetProperty("id").GetString()!;

            foreach (string neighbour in Strings(zone, "adjacent"))
            {
                if (!zones.Contains(neighbour))
                {
                    violations.Add(new MasterDataViolation(
                        "V3", $"zones.json: {id} 가 없는 존 '{neighbour}' 을 인접으로 든다."));
                }
            }
        }

        foreach (JsonElement rule in ctx.Array("interrupts.json", "rules"))
        {
            string id = rule.GetProperty("id").GetString()!;
            string action = rule.GetProperty("then").GetProperty("action").GetString()!;

            if (!actions.Contains(action))
            {
                violations.Add(new MasterDataViolation(
                    "V3", $"interrupts.json: 규칙 '{id}' 이 없는 액션 '{action}' 을 쓴다."));
            }
        }

        foreach (JsonElement archetype in ctx.Array("archetypes.json", "archetypes"))
        {
            string id = archetype.GetProperty("id").GetString()!;

            foreach (string recipe in Strings(archetype, "primary_recipes"))
            {
                if (!recipes.Contains(recipe))
                {
                    violations.Add(new MasterDataViolation(
                        "V3", $"archetypes.json: {id} 가 없는 레시피 '{recipe}' 를 든다."));
                }
            }
        }
    }

    // ---------------------------------------------------------------- V4

    /// <summary>V4 — archetypes[].allowed_actions 가 전부 카탈로그에 있다.</summary>
    private static void CheckV4(Context ctx, ImmutableArray<MasterDataViolation>.Builder violations)
    {
        HashSet<string> actions = ctx.ActionIds;

        foreach (JsonElement archetype in ctx.Array("archetypes.json", "archetypes"))
        {
            string id = archetype.GetProperty("id").GetString()!;

            foreach (string action in Strings(archetype, "allowed_actions"))
            {
                if (!actions.Contains(action))
                {
                    violations.Add(new MasterDataViolation(
                        "V4", $"archetypes.json: {id} 가 카탈로그에 없는 액션 '{action}' 을 허용한다."));
                }
            }
        }
    }

    // ---------------------------------------------------------------- V5

    /// <summary>V5 — population_weight 합 = 1.0 (±0.001).</summary>
    private static void CheckV5(Context ctx, ImmutableArray<MasterDataViolation>.Builder violations)
    {
        double sum = 0;

        foreach (JsonElement archetype in ctx.Array("archetypes.json", "archetypes"))
        {
            sum += archetype.GetProperty("population_weight").GetDouble();
        }

        if (Math.Abs(sum - 1.0) > 0.001)
        {
            violations.Add(new MasterDataViolation(
                "V5",
                string.Create(CultureInfo.InvariantCulture, $"archetypes.json: population_weight 합이 {sum:F6} 다. 1.0 ±0.001 이어야 한다.")));
        }
    }

    // ---------------------------------------------------------------- V6

    /// <summary>V6 — context_buckets.total_keys == 실제 조합 수.</summary>
    private static void CheckV6(Context ctx, ImmutableArray<MasterDataViolation>.Builder violations)
    {
        JsonElement root = ctx.Root("context_buckets.json");
        JsonElement dimensions = root.GetProperty("dimensions");

        int archetypes = ctx.Array("archetypes.json", "archetypes").Count;
        int product = archetypes;

        foreach (string name in new[] { "time_of_day", "region_state", "climate" })
        {
            if (!dimensions.TryGetProperty(name, out JsonElement dimension))
            {
                violations.Add(new MasterDataViolation("V6", $"context_buckets.json: 차원 '{name}' 이 없다."));
                return;
            }

            product *= dimension.GetProperty("values").GetArrayLength();
        }

        int declared = root.GetProperty("total_keys").GetInt32();

        if (declared != product)
        {
            violations.Add(new MasterDataViolation(
                "V6", $"context_buckets.json: total_keys 가 {declared} 인데 실제 조합은 {product} 다."));
        }
    }

    // ---------------------------------------------------------------- V7

    /// <summary>V7 — 모든 아키타입에 fallback_plan 이 존재한다.</summary>
    private static void CheckV7(
        Context ctx,
        ImmutableArray<MasterDataViolation>.Builder violations,
        ImmutableArray<SkippedRule>.Builder skipped)
    {
        if (ctx.Fallbacks is null)
        {
            skipped.Add(new SkippedRule("V7", "fallback_plans.json 이 아직 없다 (T1-54)."));
            return;
        }

        HashSet<string> planIds = ctx.FallbackPlanIds;

        foreach (JsonElement archetype in ctx.Array("archetypes.json", "archetypes"))
        {
            string id = archetype.GetProperty("id").GetString()!;
            string plan = archetype.GetProperty("fallback_plan").GetString()!;

            if (!planIds.Contains(plan))
            {
                violations.Add(new MasterDataViolation(
                    "V7", $"archetypes.json: {id} 의 폴백 플랜 '{plan}' 이 fallback_plans.json 에 없다."));
            }
        }

        foreach (JsonElement plan in ctx.FallbackPlans)
        {
            string id = plan.GetProperty("id").GetString()!;

            if (!plan.TryGetProperty("loop", out JsonElement loop) || !loop.GetBoolean())
            {
                violations.Add(new MasterDataViolation(
                    "V7", $"fallback_plans.json: '{id}' 이 loop:true 가 아니다. 폴백은 무한히 지속 가능해야 한다."));
            }
        }
    }

    // ---------------------------------------------------------------- V8

    /// <summary>V8 — 아키타입의 allowed_actions 만으로 폴백 플랜이 구성 가능하다.</summary>
    private static void CheckV8(
        Context ctx,
        ImmutableArray<MasterDataViolation>.Builder violations,
        ImmutableArray<SkippedRule>.Builder skipped)
    {
        if (ctx.Fallbacks is null)
        {
            skipped.Add(new SkippedRule("V8", "fallback_plans.json 이 아직 없다 (T1-54)."));
            return;
        }

        var allowedByArchetype = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (JsonElement archetype in ctx.Array("archetypes.json", "archetypes"))
        {
            allowedByArchetype[archetype.GetProperty("id").GetString()!] =
                [.. Strings(archetype, "allowed_actions")];
        }

        foreach (JsonElement plan in ctx.FallbackPlans)
        {
            string planId = plan.GetProperty("id").GetString()!;
            string archetypeId = plan.GetProperty("archetype").GetString()!;

            if (!allowedByArchetype.TryGetValue(archetypeId, out HashSet<string>? allowed))
            {
                violations.Add(new MasterDataViolation(
                    "V8", $"fallback_plans.json: '{planId}' 이 없는 아키타입 '{archetypeId}' 를 가리킨다."));
                continue;
            }

            if (!plan.TryGetProperty("steps", out JsonElement steps))
            {
                continue;
            }

            foreach (JsonElement step in steps.EnumerateArray())
            {
                string action = step.GetProperty("action").GetString()!;

                if (!allowed.Contains(action))
                {
                    violations.Add(new MasterDataViolation(
                        "V8", $"fallback_plans.json: '{planId}' 이 {archetypeId} 에게 허용되지 않은 액션 '{action}' 을 쓴다."));
                }
            }
        }
    }

    // ---------------------------------------------------------------- V9

    /// <summary>V9 — 프롬프트 프리픽스 토큰 수 ≥ 4,096.</summary>
    private static void CheckV9(
        MasterDataValidationOptions options,
        ImmutableArray<MasterDataViolation>.Builder violations,
        ImmutableArray<SkippedRule>.Builder skipped)
    {
        if (options.PromptPrefixTokens is not { } tokens)
        {
            skipped.Add(new SkippedRule("V9", "프롬프트 프리픽스는 Npc.Llm 이 조립한다 (P2). P1 에는 없다."));
            return;
        }

        if (tokens < 4_096)
        {
            violations.Add(new MasterDataViolation(
                "V9", $"프롬프트 프리픽스가 {tokens} 토큰이다. 4,096 미만이면 프롬프트 캐시가 아예 안 걸린다."));
        }
    }

    // ---------------------------------------------------------------- V10

    /// <summary>V10 — 일터 정원 총합이 그 일터를 쓰는 아키타입 인구 이상이다.</summary>
    private static void CheckV10(Context ctx, ImmutableArray<MasterDataViolation>.Builder violations)
    {
        const int TargetPopulation = 5_000;

        var capacity = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (JsonElement poi in ctx.Array("pois.json", "pois"))
        {
            string subtype = poi.GetProperty("subtype").GetString()!;
            capacity[subtype] = capacity.GetValueOrDefault(subtype) + poi.GetProperty("capacity").GetInt32();
        }

        var need = new Dictionary<string, int>(StringComparer.Ordinal);
        int homeNeed = 0;

        foreach (JsonElement archetype in ctx.Array("archetypes.json", "archetypes"))
        {
            int population = (int)Math.Ceiling(archetype.GetProperty("population_weight").GetDouble() * TargetPopulation);
            homeNeed += population;

            if (!archetype.TryGetProperty("workplace_poi_type", out JsonElement workplace)
                || workplace.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            string subtype = workplace.GetString()!;
            need[subtype] = need.GetValueOrDefault(subtype) + population;
        }

        foreach (string subtype in need.Keys.Order(StringComparer.Ordinal))
        {
            int have = capacity.GetValueOrDefault(subtype);

            if (have < need[subtype])
            {
                violations.Add(new MasterDataViolation(
                    "V10", $"pois.json: 일터 '{subtype}' 정원 합이 {have} 인데 그 일터를 쓰는 아키타입 인구는 {need[subtype]} 다."));
            }
        }

        int homeCapacity = 0;
        foreach (JsonElement poi in ctx.Array("pois.json", "pois"))
        {
            if (poi.GetProperty("type").GetString() == "home")
            {
                homeCapacity += poi.GetProperty("capacity").GetInt32();
            }
        }

        if (homeCapacity < homeNeed)
        {
            violations.Add(new MasterDataViolation(
                "V10", $"pois.json: 주거 정원 합이 {homeCapacity} 인데 NPC 는 {homeNeed} 마리다."));
        }
    }

    // ---------------------------------------------------------------- V11

    /// <summary>V11 — 모든 POI 가 존 그래프에서 도달 가능하다 (고립 POI 없음).</summary>
    private static void CheckV11(Context ctx, ImmutableArray<MasterDataViolation>.Builder violations)
    {
        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (JsonElement zone in ctx.Array("zones.json", "zones"))
        {
            adjacency[zone.GetProperty("id").GetString()!] = [.. Strings(zone, "adjacent")];
        }

        if (adjacency.Count == 0)
        {
            return;
        }

        // 인접의 대칭성 — 한쪽만 이어져 있으면 방향에 따라 도달 불가가 생긴다.
        foreach ((string zone, List<string> neighbours) in adjacency.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            foreach (string neighbour in neighbours)
            {
                if (adjacency.TryGetValue(neighbour, out List<string>? back) && !back.Contains(zone))
                {
                    violations.Add(new MasterDataViolation(
                        "V11", $"zones.json: 인접이 비대칭이다 — {zone} → {neighbour} 는 있는데 반대가 없다."));
                }
            }
        }

        string start = adjacency.Keys.Order(StringComparer.Ordinal).First();
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var stack = new Stack<string>([start]);

        while (stack.Count > 0)
        {
            foreach (string neighbour in adjacency[stack.Pop()])
            {
                if (adjacency.ContainsKey(neighbour) && seen.Add(neighbour))
                {
                    stack.Push(neighbour);
                }
            }
        }

        foreach (string zone in adjacency.Keys.Order(StringComparer.Ordinal))
        {
            if (!seen.Contains(zone))
            {
                violations.Add(new MasterDataViolation(
                    "V11", $"zones.json: 존 '{zone}' 이 그래프에서 고립돼 있다. 이 존의 POI 는 도달 불가다."));
            }
        }

        foreach (JsonElement poi in ctx.Array("pois.json", "pois"))
        {
            string zone = poi.GetProperty("zone").GetString()!;

            if (!seen.Contains(zone))
            {
                violations.Add(new MasterDataViolation(
                    "V11", $"pois.json: {poi.GetProperty("id").GetString()} 이 도달 불가 존 '{zone}' 에 있다."));
            }
        }
    }

    // ---------------------------------------------------------------- 헬퍼

    private static IEnumerable<string> Strings(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement e in array.EnumerateArray())
        {
            if (e.ValueKind == JsonValueKind.String)
            {
                yield return e.GetString()!;
            }
        }
    }

    private sealed class Context(
        Dictionary<string, JsonDocument> files, JsonDocument? fallbacks, string directory)
    {
        public JsonDocument? Fallbacks { get; } = fallbacks;

        public string Directory { get; } = directory;

        public JsonElement Root(string file) => files[file].RootElement;

        public IReadOnlyList<JsonElement> Array(string file, string property)
        {
            if (!Root(file).TryGetProperty(property, out JsonElement array)
                || array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. array.EnumerateArray()];
        }

        public IReadOnlyList<JsonElement> FallbackPlans =>
            Fallbacks is null || !Fallbacks.RootElement.TryGetProperty("plans", out JsonElement plans)
                ? []
                : [.. plans.EnumerateArray()];

        public HashSet<string> FlagIds => Ids("world_flags.json", "flags");

        public HashSet<string> ItemIds => Ids("items.json", "items");

        public HashSet<string> RecipeIds => Ids("items.json", "recipes");

        public HashSet<string> ActionIds => Ids("actions.json", "actions");

        public HashSet<string> ZoneIds => Ids("zones.json", "zones");

        public HashSet<string> FallbackPlanIds =>
            [.. FallbackPlans.Select(p => p.GetProperty("id").GetString()!)];

        private HashSet<string> Ids(string file, string property) =>
            [.. Array(file, property).Select(e => e.GetProperty("id").GetString()!)];
    }
}
