using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Llm;

/// <summary>
/// 플랜 스키마 변종. docs/12 §4 · docs/measurements/W1_schema.md.
///
/// 스파이크의 3종(<c>plan.schema.json</c> / <c>.bare.json</c> / <c>.relaxed.json</c>) 중
/// relaxed 는 남기지 않았다 — relaxed 가 빼던 요소(<c>pattern</c>·<c>additionalProperties</c>·
/// <c>maxLength</c>)를 W1 실측에 따라 <b>양쪽 프로파일에서 전부 뺐기</b> 때문이다.
/// </summary>
public enum SchemaProfile
{
    /// <summary>
    /// <c>args</c> 에 전 액션 파라미터의 평탄화 합집합을 싣는다.
    /// W1(T0-09) 에서 확인: <c>args</c> 를 properties 없는 object 로 두면 강제 디코딩이
    /// 매 스텝 <c>args: {}</c> 만 뱉는다.
    /// </summary>
    Full = 0,

    /// <summary>docs/03 §2 원안 — <c>args</c> 가 빈 object. 비교용으로 남긴다.</summary>
    Bare = 1,
}

/// <summary>
/// <c>actions.json</c> → 플랜 JSON Schema. docs/03 §2 · docs/12 §4.
///
/// <b><c>action</c> 열거값을 손으로 유지하지 않는다.</b> 손으로 두면 반드시 카탈로그와 어긋난다.
///
/// <b>스키마를 강제할수록 나빠졌다.</b> W1(T0-09) 에서 <c>response_format=json_schema</c> 는
/// 유효 JSON 99/100 을 내면서 어휘 검증 통과 0 이었다(<c>V2.UNKNOWN_ARG</c> 99 — 문법을 지키며
/// 인자를 지어낸다). 그래서 이 스키마는 <b>프롬프트에 실리는 문서</b>가 주 용도이고,
/// <c>response_format</c> 으로 강제할지는 설정으로 뺀다 (T2-09).
///
/// 아래 요소는 W1 실측으로 전부 뺐다. 전부 검증기 1·2단이 다시 잡는다.
/// <list type="bullet">
///   <item><c>minItems</c>/<c>maxItems</c> — 7건 (<c>V1.STEP_COUNT</c>)</item>
///   <item><c>additionalProperties: false</c> — 1건 (<c>V1.EXTRA_FIELD</c>)</item>
///   <item><c>required</c> — 1건 (<c>V2.MISSING_REQUIRED_ARG</c>)</item>
///   <item><c>maximum</c>(과 짝인 <c>minimum</c>) — 1건 (<c>V2.RANGE</c>)</item>
///   <item><c>pattern</c> — 제공사별 지원 편차. <c>V1.SCHEMA</c> 가 goal 정규식을 본다</item>
///   <item><c>oneOf</c>/<c>anyOf</c> — 액션 37종 분기는 강제 디코딩 FSM 을 폭발시킨다</item>
/// </list>
/// </summary>
public sealed class SchemaProvider
{
    private SchemaProvider(
        SchemaProfile profile,
        string planSchemaJson,
        ImmutableArray<string> actionIds,
        ImmutableArray<string> itemIds,
        ImmutableArray<string> recipeIds)
    {
        Profile = profile;
        PlanSchemaJson = planSchemaJson;
        Sha256 = HashOf(planSchemaJson);
        ActionIds = actionIds;
        ItemIds = itemIds;
        RecipeIds = recipeIds;
    }

    /// <summary>어느 변종인가.</summary>
    public SchemaProfile Profile { get; }

    /// <summary>
    /// 생성된 스키마. 들여쓰기가 없다 — 그대로 프리픽스에 실리므로 공백이 곧 토큰이다.
    /// </summary>
    public string PlanSchemaJson { get; }

    /// <summary>스키마 본문의 SHA-256. 프리픽스 해시의 구성 요소다.</summary>
    public string Sha256 { get; }

    /// <summary><c>action</c> 열거값. 카탈로그 code 오름차순.</summary>
    public ImmutableArray<string> ActionIds { get; }

    /// <summary><c>item_ref</c> 인자에 쓸 수 있는 아이템 id. items.json code 오름차순.</summary>
    public ImmutableArray<string> ItemIds { get; }

    /// <summary><c>recipe</c> 인자에 쓸 수 있는 레시피 id. items.json 등장 순서.</summary>
    public ImmutableArray<string> RecipeIds { get; }

    /// <summary>마스터데이터에서 스키마를 만든다. 기동 시 1회.</summary>
    public static SchemaProvider Build(MasterDataSet data, SchemaProfile profile = SchemaProfile.Full)
    {
        ArgumentNullException.ThrowIfNull(data);

        ImmutableArray<string> actionIds = [.. data.Actions.Actions.Select(a => a.Id)];
        ImmutableArray<string> itemIds = [.. data.Items.Items.Select(i => i.Id)];
        ImmutableArray<string> recipeIds = [.. data.Items.Recipes.Select(r => r.Id)];

        JsonObject root = BuildRoot(data, profile, actionIds, itemIds, recipeIds);

        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        return new SchemaProvider(profile, json, actionIds, itemIds, recipeIds);
    }

    private static JsonObject BuildRoot(
        MasterDataSet data,
        SchemaProfile profile,
        ImmutableArray<string> actionIds,
        ImmutableArray<string> itemIds,
        ImmutableArray<string> recipeIds)
    {
        var step = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = ToArray(actionIds) },
                ["args"] = profile == SchemaProfile.Full
                    ? ArgsUnion(data, itemIds, recipeIds)
                    : new JsonObject { ["type"] = "object" },
                ["timeout_s"] = new JsonObject { ["type"] = "integer" },
            },
        };

        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "NpcPlan",
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["schema"] = new JsonObject { ["const"] = 1 },
                ["goal"] = new JsonObject { ["type"] = "string" },
                ["reasoning"] = new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = PlanDocument.MaxReasoningLength,
                },
                ["loop"] = new JsonObject { ["type"] = "boolean" },
                ["on_step_fail"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray("fallback", "retry_once", "skip", "replan"),
                },
                ["steps"] = new JsonObject { ["type"] = "array", ["items"] = step },
            },
        };
    }

    /// <summary>
    /// 전 액션 파라미터의 평탄화 합집합. 액션별 <c>oneOf</c> 분기 대신 쓴다 (docs/03 §2 주석).
    /// 어떤 액션이 어떤 인자를 받는지는 검증기 2단이 본다.
    ///
    /// 순서는 <b>카탈로그 등장 순서</b>다. 이름으로 정렬하면 프리픽스 SHA 가 흔들린다 —
    /// 카탈로그는 code 오름차순이고 액션 안의 파라미터는 이름순이라 이미 결정론적이다.
    /// </summary>
    private static JsonObject ArgsUnion(
        MasterDataSet data,
        ImmutableArray<string> itemIds,
        ImmutableArray<string> recipeIds)
    {
        var order = new List<string>();
        var byName = new Dictionary<string, List<ParamDef>>(StringComparer.Ordinal);

        foreach (ActionDef action in data.Actions.Actions)
        {
            foreach (ParamDef param in action.Params)
            {
                if (!byName.TryGetValue(param.Name, out List<ParamDef>? list))
                {
                    byName[param.Name] = list = [];
                    order.Add(param.Name);
                }

                list.Add(param);
            }
        }

        var properties = new JsonObject();

        foreach (string name in order)
        {
            properties[name] = PropertySchema(name, byName[name], itemIds, recipeIds);
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
    }

    private static JsonObject PropertySchema(
        string name,
        List<ParamDef> definitions,
        ImmutableArray<string> itemIds,
        ImmutableArray<string> recipeIds)
    {
        // 같은 이름이 액션마다 다른 타입일 수 있다 (Repair.target 은 poi_ref, Attack.target 은 npc_ref).
        // 그럴 때는 좁히지 않고 문자열로 둔다 — 좁히는 일은 검증기 2단의 몫이다.
        ParamType type = definitions[0].Type;
        foreach (ParamDef def in definitions)
        {
            if (def.Type != type)
            {
                return new JsonObject { ["type"] = "string" };
            }
        }

        switch (type)
        {
            case ParamType.PoiRef:
                return new JsonObject { ["type"] = "string", ["enum"] = PoiSymbolArray() };

            case ParamType.Route:
                return new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string", ["enum"] = PoiSymbolArray() },
                };

            case ParamType.ItemRef:
                // recipe 인자는 레시피 표를, 나머지는 아이템 표를 본다 (검증기 2단과 같은 갈래).
                return new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = ToArray(
                        string.Equals(name, "recipe", StringComparison.Ordinal) ? recipeIds : itemIds),
                };

            case ParamType.Enum:
                return new JsonObject { ["type"] = "string", ["enum"] = ToArray(EnumValues(definitions)) };

            case ParamType.Int:
                // min/max 는 싣지 않는다 — W1 에서 로컬 모델이 어겼고, V2.RANGE 가 다시 본다.
                return new JsonObject { ["type"] = "integer" };

            case ParamType.NpcRef:
                // self / nearest:<archetype> / poi_owner:<poi> — 열거로 못 박기 어렵다.
                return new JsonObject { ["type"] = "string" };

            default:
                return new JsonObject { ["type"] = "string" };
        }
    }

    /// <summary>같은 이름 파라미터의 열거값 합집합. 등장 순서를 유지한다.</summary>
    private static List<string> EnumValues(List<ParamDef> definitions)
    {
        var values = new List<string>();

        foreach (ParamDef def in definitions)
        {
            foreach (string value in def.EnumValues)
            {
                if (!values.Contains(value, StringComparer.Ordinal))
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private static JsonArray PoiSymbolArray()
    {
        var array = new JsonArray();

        // Names[0] 은 None 의 빈 문자열이라 뺀다.
        for (int i = 1; i < PoiSymbols.Names.Length; i++)
        {
            array.Add(PoiSymbols.Names[i]);
        }

        return array;
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();

        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static string HashOf(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
