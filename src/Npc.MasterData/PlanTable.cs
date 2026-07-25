using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;

namespace Npc.MasterData;

/// <summary>폴백 플랜 하나. docs/01 §8.</summary>
/// <param name="Id">플랜 id (<c>fb_blacksmith</c>).</param>
/// <param name="Archetype">이 플랜을 쓰는 아키타입.</param>
/// <param name="Document">심볼을 푼 뒤의 JSON 모델. 검수·재검증용.</param>
/// <param name="Plan">컴파일된 플랜.</param>
public sealed record FallbackPlanEntry(
    string Id,
    ArchetypeId Archetype,
    PlanDocument Document,
    CompiledPlan Plan);

/// <summary>
/// fallback_plans.json 의 읽기 전용 인덱스. docs/01 §8 · docs/11 §7.
///
/// <b>로드 시점에 검증기 1~3단을 통과시킨다.</b> 폴백은 최후 보루라 런타임에 깨지면 갈 곳이 없다.
/// 하나라도 실패하면 기동 실패다.
///
/// <c>$primary</c> 는 여기서 아키타입의 첫 <c>primary_recipes</c> 로 치환한다.
/// 플랜 DSL 자체에는 그런 심볼이 없다 — 폴백은 아키타입에 묶여 있어서 로드 시점에 풀 수 있다.
/// <c>$home</c>·<c>$workplace</c> 는 POI 심볼이라 그대로 두고 런타임(PoiBinder)이 개체별로 푼다.
/// </summary>
public sealed class PlanTable
{
    /// <summary>아키타입의 대표 레시피를 가리키는 심볼. docs/01 §8.</summary>
    public const string PrimarySymbol = "$primary";

    private readonly CompiledPlan?[] _byArchetype;
    private readonly Dictionary<string, FallbackPlanEntry> _byId;

    private PlanTable(
        CompiledPlan?[] byArchetype,
        Dictionary<string, FallbackPlanEntry> byId,
        ImmutableArray<FallbackPlanEntry> plans)
    {
        _byArchetype = byArchetype;
        _byId = byId;
        Plans = plans;
    }

    /// <summary>아키타입 code 오름차순의 모든 폴백 플랜.</summary>
    public ImmutableArray<FallbackPlanEntry> Plans { get; }

    /// <summary>플랜 수.</summary>
    public int Count => Plans.Length;

    /// <summary>이 아키타입의 폴백. 없으면 null.</summary>
    public CompiledPlan? For(ArchetypeId archetype) =>
        (uint)archetype.Value < (uint)_byArchetype.Length ? _byArchetype[archetype.Value] : null;

    /// <summary>플랜 id 로 조회.</summary>
    public bool TryGet(string id, out FallbackPlanEntry entry) => _byId.TryGetValue(id, out entry!);

    /// <summary>masterdata/fallback_plans.json 로드.</summary>
    public static PlanTable Load(string path, MasterDataSet data) =>
        Parse(File.ReadAllText(path), data);

    /// <summary>JSON 문자열에서 로드.</summary>
    public static PlanTable Parse(string json, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        JsonNode root = JsonNode.Parse(json)
            ?? throw new InvalidDataException("fallback_plans.json 을 읽지 못했다.");

        JsonArray? array = root["plans"]?.AsArray();

        if (array is null || array.Count == 0)
        {
            throw new InvalidDataException("fallback_plans.json 에 plans 가 없다.");
        }

        var byArchetype = new CompiledPlan?[data.Archetypes.Count];
        var byId = new Dictionary<string, FallbackPlanEntry>(array.Count, StringComparer.Ordinal);
        var entries = ImmutableArray.CreateBuilder<FallbackPlanEntry>(array.Count);

        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject plan)
            {
                continue;
            }

            entries.Add(Build(plan, data, byArchetype, byId));
        }

        entries.Sort((a, b) => a.Archetype.Value.CompareTo(b.Archetype.Value));

        return new PlanTable(byArchetype, byId, entries.ToImmutable());
    }

    private static FallbackPlanEntry Build(
        JsonObject plan,
        MasterDataSet data,
        CompiledPlan?[] byArchetype,
        Dictionary<string, FallbackPlanEntry> byId)
    {
        string id = plan["id"]?.GetValue<string>()
            ?? throw new InvalidDataException("fallback_plans.json: id 가 없는 플랜이 있다.");
        string archetypeId = plan["archetype"]?.GetValue<string>()
            ?? throw new InvalidDataException($"fallback_plans.json: '{id}' 에 archetype 이 없다.");

        if (!data.Archetypes.TryGet(archetypeId, out ArchetypeDef archetype))
        {
            throw new InvalidDataException($"fallback_plans.json: '{id}' 가 없는 아키타입 '{archetypeId}' 를 가리킨다.");
        }

        if (!string.Equals(id, archetype.FallbackPlanId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"fallback_plans.json: '{id}' 와 archetypes.json 의 fallback_plan '{archetype.FallbackPlanId}' 가 다르다.");
        }

        // 플랜 DSL 에 없는 필드(id, archetype)를 떼고 심볼을 푼다.
        // fallback_plans.json 은 컨테이너라 파일 수준에 version 이 있고 플랜마다 schema 를 적지 않는다
        // (docs/01 §8). 검증기 1단은 DSL 문서를 기대하므로 여기서 채워 넣는다.
        var document = plan.DeepClone().AsObject();
        document.Remove("id");
        document.Remove("archetype");
        document["schema"] ??= JsonValue.Create(1);
        ResolvePrimary(document, archetype, id);

        string documentJson = document.ToJsonString();

        // 폴백은 최후 보루다. 로드 시점에 1~3단을 통과하지 못하면 기동 실패다.
        ValidationResult schema = SchemaValidator.Validate(documentJson, out PlanDocument? parsed);
        Require(schema, id);

        var bucket = new BucketKey(archetype.Code, TimeOfDay.Morning, RegionState.Peace, Climate.Fair);

        Require(VocabularyValidator.Validate(parsed!, archetype.Code, data), id);
        Require(CoherenceValidator.Validate(parsed!, bucket, archetype.Code, data), id);

        CompiledPlan compiled = PlanCompiler.Compile(
            parsed!, bucket, new PlanId(0), data, PlanOrigin.Fallback, version: 1, sourceJson: documentJson);

        var entry = new FallbackPlanEntry(id, archetype.Code, parsed!, compiled);

        if (!byId.TryAdd(id, entry))
        {
            throw new InvalidDataException($"fallback_plans.json: 플랜 id '{id}' 가 중복이다.");
        }

        if (byArchetype[archetype.Code.Value] is not null)
        {
            throw new InvalidDataException($"fallback_plans.json: 아키타입 '{archetypeId}' 에 폴백이 둘 이상이다.");
        }

        byArchetype[archetype.Code.Value] = compiled;
        return entry;
    }

    /// <summary><c>$primary</c> 를 아키타입의 첫 대표 레시피로 바꾼다.</summary>
    private static void ResolvePrimary(JsonObject document, ArchetypeDef archetype, string planId)
    {
        JsonArray? steps = document["steps"]?.AsArray();

        if (steps is null)
        {
            return;
        }

        foreach (JsonNode? stepNode in steps)
        {
            if (stepNode is not JsonObject step || step["args"] is not JsonObject args)
            {
                continue;
            }

            foreach (string key in args.Select(kv => kv.Key).ToArray())
            {
                if (args[key] is not JsonValue value
                    || !value.TryGetValue(out string? text)
                    || !string.Equals(text, PrimarySymbol, StringComparison.Ordinal))
                {
                    continue;
                }

                if (archetype.PrimaryRecipes.Length == 0)
                {
                    throw new InvalidDataException(
                        $"fallback_plans.json: '{planId}' 이 $primary 를 쓰는데 {archetype.Id} 에 primary_recipes 가 없다.");
                }

                args[key] = JsonValue.Create(archetype.PrimaryRecipes[0]);
            }
        }
    }

    private static void Require(ValidationResult result, string planId)
    {
        if (result.IsValid)
        {
            return;
        }

        throw new InvalidDataException(
            $"fallback_plans.json: '{planId}' 이 검증기 {result.FailedAt} 단에서 걸렸다. "
            + $"{result.Code} (스텝 {result.StepIndex}): {result.Detail}");
    }
}
