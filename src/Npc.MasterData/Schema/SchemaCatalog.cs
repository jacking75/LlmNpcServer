using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Npc.Core;

namespace Npc.MasterData.Schema;

/// <summary>발행하는 스키마 하나.</summary>
/// <param name="File">스키마 파일 이름 (<c>archetypes.schema.json</c>).</param>
/// <param name="DataFile">이 스키마가 설명하는 마스터데이터 파일. 없으면 빈 문자열.</param>
/// <param name="Title">사람이 읽는 제목.</param>
public readonly record struct SchemaEntry(string File, string DataFile, string Title);

/// <summary>
/// 마스터데이터 JSON Schema 발행 (E-02).
///
/// <b>손으로 쓰지 않는다.</b> 스키마를 별도로 관리하면 로더와 반드시 어긋나고, 어긋난 스키마는
/// 없는 것보다 나쁘다 — LLM 과 에디터가 그것을 근거로 틀린 것을 만든다.
/// 구조는 <b>로더의 DTO</b>에서 <see cref="JsonSchemaExporter"/> 로 뽑는다.
///
/// <para>
/// <b>허용 값(enum)은 마스터데이터에서 채운다.</b> <c>allowed_actions</c> 의 값은
/// 지금 <c>actions.json</c> 에 있는 id 목록이지 고정 목록이 아니다 — 그래서 이 스키마는
/// "이 마스터데이터 상태에 대한 스키마" 이고 <c>$comment</c> 에 <c>content_hash</c> 를 적는다.
/// enum 없는 정적 변형(<c>*.base.schema.json</c>)도 같이 낸다.
/// </para>
///
/// <para>
/// <b>스키마는 검증기가 아니다.</b> 교차 제약(V5 가중치 합 1.0 · V6 · V10 정원 · V11 도달)은
/// JSON Schema 로 표현할 수 없다. <c>description</c> 에 "V5: …" 로 적어 두되,
/// 판정은 <c>MasterDataValidator</c> 가 한다. 스키마를 통과했다고 기동이 되는 것이 아니다.
/// </para>
/// </summary>
public static class SchemaCatalog
{
    /// <summary>
    /// 이 API 가 리플렉션을 요구하는 이유.
    ///
    /// <b>억제가 아니라 표시다.</b> <see cref="JsonSchemaExporter"/> 는 타입 그래프를 걸으며
    /// 원시 타입까지 <c>JsonTypeInfo</c> 를 요구하는데, 소스 생성 컨텍스트는 선언한 루트에서
    /// 닿는 것만 담는다 — 그래서 여기서는 리플렉션 기반 리졸버를 쓴다.
    ///
    /// <para>
    /// <b>서버 경로가 아니다.</b> 스키마 발행은 도구가 개발 중에 부르는 것이고,
    /// 트리밍·AOT 로 배포되는 <c>Npc.Host</c> 는 이 API 를 부르지 않는다.
    /// 부르는 쪽에 경고가 전파되므로 실수로 기동 경로에 들어오면 빌드가 알려 준다.
    /// </para>
    /// </summary>
    private const string ReflectionNeeded =
        "JSON Schema 발행은 리플렉션으로 타입 그래프를 건다. 도구 전용이고 서버 기동 경로가 아니다.";

    /// <summary>스키마 폴더 (저장소 루트 기준).</summary>
    public const string Directory = "docs/schema";

    /// <summary>정적 변형 접미사. 허용 값이 들어가지 않는다.</summary>
    public const string BaseSuffix = ".base.schema.json";

    /// <summary>동적 변형 접미사. 지금 마스터데이터의 허용 값이 들어간다.</summary>
    public const string Suffix = ".schema.json";

    /// <summary>발행 목록. <b>여기가 사실의 출처다</b> — 테스트가 이 표로 파일을 확인한다.</summary>
    public static ImmutableArray<SchemaEntry> Entries { get; } =
    [
        new("world_flags" + Suffix, "world_flags.json", "월드 플래그 — bit 번호는 재배치하지 않는다"),
        new("items" + Suffix, "items.json", "아이템·레시피"),
        new("actions" + Suffix, "actions.json", "액션 카탈로그 — 40개 상한"),
        new("zones" + Suffix, "zones.json", "존"),
        new("pois" + Suffix, "pois.json", "POI"),
        new("archetypes" + Suffix, "archetypes.json", "아키타입"),
        new("context_buckets" + Suffix, "context_buckets.json", "버킷 차원"),
        new("interrupts" + Suffix, "interrupts.json", "인터럽트 규칙"),
        new("fallback_plans" + Suffix, "fallback_plans.json", "폴백 플랜"),
        new("npc_instances" + Suffix, "npc_instances.json", "NPC 인스턴스 (생성물)"),
    ];

    /// <summary>
    /// 스키마 전부를 만든다. 파일 이름 → 내용.
    /// </summary>
    /// <param name="data">허용 값을 채울 마스터데이터. null 이면 정적 변형만 만든다.</param>
    [RequiresUnreferencedCode(ReflectionNeeded)]
    [RequiresDynamicCode(ReflectionNeeded)]
    public static ImmutableSortedDictionary<string, string> Render(MasterDataSet? data)
    {
        var built = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach (SchemaEntry entry in Entries)
        {
            string stem = entry.File[..^Suffix.Length];

            // 정적 변형 — 허용 값이 없다. 마스터데이터 없이도 구조는 설명할 수 있다.
            built[stem + BaseSuffix] = Serialize(Build(stem, entry, data: null));

            if (data is not null)
            {
                built[entry.File] = Serialize(Build(stem, entry, data));
            }
        }

        return built.ToImmutable();
    }

    /// <summary>스키마를 디스크에 쓴다. 폴더가 없으면 만든다.</summary>
    /// <param name="directory">쓸 폴더.</param>
    /// <param name="data">허용 값을 채울 마스터데이터.</param>
    /// <returns>쓴 파일 수.</returns>
    [RequiresUnreferencedCode(ReflectionNeeded)]
    [RequiresDynamicCode(ReflectionNeeded)]
    public static int Write(string directory, MasterDataSet? data)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        System.IO.Directory.CreateDirectory(directory);

        ImmutableSortedDictionary<string, string> schemas = Render(data);

        foreach ((string name, string json) in schemas)
        {
            File.WriteAllText(Path.Combine(directory, name), json);
        }

        return schemas.Count;
    }

    private static string Serialize(JsonNode node) =>
        (node.ToJsonString(s_write) + Environment.NewLine).ReplaceLineEndings();

    private static readonly JsonSerializerOptions s_write = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>파일 하나의 스키마.</summary>
    [RequiresUnreferencedCode(ReflectionNeeded)]
    [RequiresDynamicCode(ReflectionNeeded)]
    private static JsonNode Build(string stem, SchemaEntry entry, MasterDataSet? data)
    {
        JsonNode schema = Structure(stem, data);

        if (schema is not JsonObject root)
        {
            return schema;
        }

        // $schema 는 마스터데이터 파일이 자기를 가리키기 위해 쓰는 필드이기도 하다.
        // 로더는 무시하지만 에디터는 이것으로 스키마를 찾는다 (F-08).
        root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        root["title"] = entry.Title;

        root["$comment"] = data is null
            ? "정적 변형 — 허용 값(enum)이 없다. 구조만 설명한다."
            : $"content_hash={data.ContentHash} 기준. 허용 값은 이 마스터데이터 상태의 것이다.";

        root["description"] =
            $"{entry.DataFile} 의 구조. **스키마는 검증기가 아니다** — "
            + "교차 제약(V5 가중치 합 · V6 total_keys · V10 정원 · V11 도달)은 여기서 표현할 수 없고 "
            + "MasterDataValidator 가 판정한다. docs/reference_masterdata.html 이 전문이다.";

        // required 를 싣지 않는다.
        //
        // <b>스키마의 일은 자동완성과 허용 값이지 필수 판정이 아니다.</b> DTO 의 생성자 인자가
        // 전부 required 로 나오는데 실제 파일은 선택 필드를 생략하고(예: duty_hours),
        // 무엇이 진짜 필수인지는 로더와 V1~V13 이 안다 — 스키마가 그것을 흉내 내면
        // 멀쩡한 파일에 빨간 줄이 그어지고 사람이 스키마를 꺼 버린다.
        Strip(root, "required");

        Permit(root, stem);

        return root;
    }

    /// <summary>
    /// DTO 에서 구조를 뽑는다. <b>손으로 쓰지 않으므로 로더와 어긋날 수 없다.</b>
    /// </summary>
    [RequiresUnreferencedCode(ReflectionNeeded)]
    [RequiresDynamicCode(ReflectionNeeded)]
    private static JsonNode Structure(string stem, MasterDataSet? data)
    {
        var exporter = new JsonSchemaExporterOptions
        {
            // false 로 둔다. true 면 nullable 이 아닌 모든 생성자 인자가 required 가 되는데,
            // 실제 파일은 선택 필드를 생략한다 (duty_hours 가 그렇다).
            TreatNullObliviousAsNonNullable = false,
            TransformSchemaNode = (context, node) => Decorate(stem, context, node, data),
        };

        var options = new JsonSerializerOptions
        {
            // 로더 컨텍스트와 같은 이름 규칙이다. 다르면 스키마가 없는 필드명을 요구하게 된다.
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };

        return JsonSchemaExporter.GetJsonSchemaAsNode(options, Source(stem), exporter);
    }

    /// <summary>
    /// 파일 이름 → 그 파일을 읽는 DTO. <b>로더가 쓰는 것과 같은 타입이다</b> —
    /// 스키마 전용 타입을 새로 만들면 그 순간 드리프트가 시작된다.
    /// </summary>
    private static Type Source(string stem) => stem switch
    {
        "items" => typeof(ItemTable.ItemsFile),
        "actions" => typeof(ActionCatalog.ActionsFile),
        "archetypes" => typeof(ArchetypeTable.ArchetypesFile),
        "interrupts" => typeof(InterruptRules.InterruptsFile),
        "zones" => typeof(ZonesFile),
        "pois" => typeof(PoisFile),
        "context_buckets" => typeof(BucketsFile),
        "npc_instances" => typeof(NpcInstancesFile),
        "world_flags" => typeof(WorldFlagsFile),
        "fallback_plans" => typeof(FallbackPlansFile),
        _ => throw new ArgumentException($"'{stem}' 의 DTO 를 모른다.", nameof(stem)),
    };

    /// <summary>
    /// 노드 하나를 꾸민다 — 허용 값과 설명.
    ///
    /// <b>허용 값은 마스터데이터에서 온다.</b> 액션 id 목록을 코드에 적어 두면 그 순간
    /// 스키마가 카탈로그와 어긋난다.
    /// </summary>
    private static JsonNode Decorate(
        string stem, JsonSchemaExporterContext context, JsonNode node, MasterDataSet? data)
    {
        if (node is not JsonObject schema || context.PropertyInfo is not { } property)
        {
            return node;
        }

        if (Describe(stem, property.Name) is { } text)
        {
            schema["description"] = text;
        }

        if (data is null)
        {
            return schema;
        }

        if (Allowed(stem, property.Name, data) is { } values)
        {
            // 배열이면 items 에, 스칼라면 자기 자신에 건다.
            // type 은 문자열일 수도 배열(["string","null"])일 수도 있다 — GetValue<string> 을
            // 그냥 부르면 후자에서 던진다.
            if (schema["items"] is JsonObject items && IsType(schema, "array"))
            {
                // <b>원소에는 null 을 넣지 않는다.</b> 배열이 통째로 없을 수는 있어도
                // ["MoveTo", null] 은 어느 파일에서도 유효하지 않다.
                items["enum"] = values;
            }
            else
            {
                // 선택 필드는 값 자리에 null 이 올 수 있다 (workplace_poi_type 이 그렇다).
                // 넣지 않으면 일터 없는 아키타입이 전부 떨어진다.
                if (IsType(schema, "null"))
                {
                    values.Add(null);
                }

                schema["enum"] = values;
            }
        }

        return schema;
    }

    /// <summary>
    /// (파일, 필드) → 지금 마스터데이터가 허용하는 값.
    ///
    /// <b>파일까지 봐야 한다.</b> 같은 이름이 파일마다 다른 뜻이다 —
    /// <c>pois.type</c> 은 POI 종류이고 <c>actions.params.*.type</c> 은 파라미터 타입이다.
    /// 이름만 보고 붙이면 멀쩡한 파일이 전부 떨어진다.
    /// </summary>
    private static JsonArray? Allowed(string stem, string property, MasterDataSet data) =>
        (stem, property) switch
        {
            ("archetypes", "allowed_actions") => Array(data.Actions.Actions.Select(a => a.Id)),
            ("archetypes", "home_poi_type") => Array(PoiTypeNames),
            ("archetypes", "workplace_poi_type") => Array(data.Pois.Pois.Select(p => p.Subtype)),
            ("archetypes", "fallback_plan") => Fallbacks(data),
            ("archetypes", "primary_recipes") => Array(data.Items.Recipes.Select(r => r.Id)),

            ("pois", "type") => Array(PoiTypeNames),
            ("pois", "zone") => Array(data.Zones.Zones.Select(z => z.Id)),
            ("pois", "allowed_archetypes") => Array(data.Archetypes.Archetypes.Select(a => a.Id)),
            ("pois", "resources") => Array(data.Items.Items.Select(i => i.Id)),

            ("npc_instances", "archetype") => Array(data.Archetypes.Archetypes.Select(a => a.Id)),
            ("npc_instances", "zone") => Array(data.Zones.Zones.Select(z => z.Id)),

            ("interrupts", "action") => Array(data.Actions.Actions.Select(a => a.Id)),

            ("fallback_plans", "archetype") => Array(data.Archetypes.Archetypes.Select(a => a.Id)),
            ("fallback_plans", "action") => Array(data.Actions.Actions.Select(a => a.Id)),

            // 플래그 이름은 world_flags.json 이 정하고 소스 생성기가 enum 으로 만든다.
            ("actions", "requires" or "requires_any" or "forbids" or "grants" or "clears") =>
                Array(WorldFlagTable.Names),
            ("items", "grants") => Array(WorldFlagTable.Names),
            ("archetypes", "baseline_flags") => Array(WorldFlagTable.Names),

            _ => null,
        };

    /// <summary>POI 종류 이름. JSON 은 소문자다.</summary>
    private static IEnumerable<string> PoiTypeNames =>
        Enum.GetNames<PoiType>().Select(n => n.ToLowerInvariant());

    /// <summary>폴백 플랜 id. 아직 안 읽었으면 비운다 — 없는 것을 허용 값으로 낼 수 없다.</summary>
    private static JsonArray? Fallbacks(MasterDataSet data) =>
        data.Fallbacks is null ? null : Array(data.Fallbacks.Plans.Select(p => p.Id));

    /// <summary>
    /// <b>로더가 무시하는 필드를 스키마가 거절하면 안 된다.</b>
    ///
    /// 파일에는 로더가 읽지 않는 것이 있다 — <c>version</c>(형식 버전) ·
    /// <c>_comment</c>(사람이 읽는 주석) · <c>$schema</c>(에디터가 스키마를 찾는 경로) ·
    /// 그리고 파일별 설명 필드. 스키마가 이것들을 모르면 <b>멀쩡한 파일에 빨간 줄이 그어지고</b>
    /// 사람이 스키마를 꺼 버린다.
    ///
    /// <para>
    /// 무시한다는 사실을 <c>description</c> 에 적는다 — 값을 고쳐도 동작이 바뀌지 않는다는 것을
    /// 알아야 한다.
    /// </para>
    /// </summary>
    private static void Permit(JsonObject root, string stem)
    {
        // _comment 는 어디에나 올 수 있다. 중첩 객체까지 훑어 허용한다.
        PermitCommentEverywhere(root);

        if (root["properties"] is not JsonObject props)
        {
            return;
        }

        props["$schema"] = Ignored("에디터용 스키마 경로", "string");
        props["version"] = Ignored("파일 형식 버전", "integer");

        foreach (string extra in ExtraFields(stem))
        {
            props[extra] = Ignored("설명용 필드", null);
        }
    }

    /// <summary>파일별로 로더가 읽지 않는 설명 필드.</summary>
    private static IEnumerable<string> ExtraFields(string stem) => stem switch
    {
        // 소스 생성기가 flags 와 reserved_bits 만 읽는다. exclusive_groups 는 문서용이다.
        "world_flags" => ["exclusive_groups"],

        // 로더는 dimensions·total_keys·prebake_priority 만 본다. 나머지는 키 표기 설명이다.
        "context_buckets" => ["key_format", "key_example", "index_formula"],

        _ => [],
    };

    private static JsonObject Ignored(string what, string? type)
    {
        var node = new JsonObject
        {
            ["description"] = $"{what}. **로더는 읽지 않는다** — 값을 고쳐도 동작이 바뀌지 않는다.",
        };

        if (type is not null)
        {
            node["type"] = type;
        }

        return node;
    }

    /// <summary><c>_comment</c> 는 어느 객체에나 올 수 있다.</summary>
    private static void PermitCommentEverywhere(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["properties"] is JsonObject props)
                {
                    props["_comment"] = Ignored("사람이 읽는 주석", null);
                }

                foreach (KeyValuePair<string, JsonNode?> child in o.ToArray())
                {
                    PermitCommentEverywhere(child.Value);
                }

                break;

            case JsonArray a:
                foreach (JsonNode? child in a)
                {
                    PermitCommentEverywhere(child);
                }

                break;
        }
    }

    /// <summary>스키마 트리 전체에서 그 키워드를 지운다.</summary>
    private static void Strip(JsonNode? node, string keyword)
    {
        switch (node)
        {
            case JsonObject o:
                o.Remove(keyword);

                foreach (KeyValuePair<string, JsonNode?> child in o.ToArray())
                {
                    Strip(child.Value, keyword);
                }

                break;

            case JsonArray a:
                foreach (JsonNode? child in a)
                {
                    Strip(child, keyword);
                }

                break;
        }
    }

    /// <summary>이 스키마 노드가 그 타입인가. <c>type</c> 은 문자열일 수도 배열일 수도 있다.</summary>
    private static bool IsType(JsonObject schema, string type) => schema["type"] switch
    {
        JsonValue value => value.TryGetValue(out string? text) && text == type,
        JsonArray array => array.Any(n => n is JsonValue v && v.TryGetValue(out string? t) && t == type),
        _ => false,
    };

    private static JsonArray Array(IEnumerable<string> values)
    {
        var array = new JsonArray();

        foreach (string value in values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            array.Add(value);
        }

        return array;
    }

    /// <summary>
    /// 필드 설명. <b>검증 규칙 번호를 같이 적는다</b> — 스키마를 보는 사람이
    /// "이건 스키마가 아니라 검증기가 잡는다" 를 알아야 한다.
    /// </summary>
    private static string? Describe(string stem, string property) => (stem, property) switch
    {
        (_, "code") => "파일 내 유일해야 한다 (V1). **재배치하지 않는다** — 프리베이크된 플랜이 깨진다.",
        ("world_flags", "bit") => "0..63. **재배치하지 않는다.** 추가는 reserved_bits 안에서만 (V1).",
        ("archetypes", "population_weight") => "0..1. 전체 합이 정확히 1.0 ±0.001 이어야 한다 (V5).",
        ("archetypes", "duty_hours") =>
            "근무 시간대. **비우면 OnDuty 가 서지 않는다** — Guard·Patrol 을 허용하려면 필요하다 (V12).",
        ("archetypes", "fallback_plan") => "모든 아키타입에 있어야 한다 (V7). 허용 액션만으로 구성 (V8).",
        ("pois", "capacity") => "정원. 이 종류를 쓰는 아키타입 인구의 합 이상이어야 한다 (V10).",
        ("zones", "adjacent") => "인접 존. **대칭이어야 한다** — 한쪽만 이으면 도달 불가가 생긴다 (V11).",
        ("context_buckets", "total_keys") =>
            "아키타입 수 × 72. **사람이 읽는 기록이다** — 코드는 이 값을 읽지 않고 V6 이 대조한다.",
        (_, "id") => "문자열 id. 다른 파일이 이 값으로 참조한다 (V3).",
        _ => null,
    };
}
