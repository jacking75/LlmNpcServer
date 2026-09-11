using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Npc.Host.Api;

/// <summary>질의 파라미터 하나.</summary>
/// <param name="Name">이름.</param>
/// <param name="Type">OpenAPI 타입 — <c>string</c>·<c>integer</c>.</param>
/// <param name="Description">무엇인가.</param>
/// <param name="Required">필수인가. 질의 파라미터는 전부 선택이다.</param>
public readonly record struct ApiParameter(
    string Name, string Type, string Description, bool Required = false);

/// <summary>라우트 하나.</summary>
/// <param name="Path">경로.</param>
/// <param name="OperationId">툴 이름이 되는 식별자. <b>재배치하지 않는다</b> — 툴 정의가 이것을 부른다.</param>
/// <param name="Summary">한 줄 설명.</param>
/// <param name="Description">왜·언제 쓰는가.</param>
/// <param name="Response">응답 타입. null 이면 스키마 없이 <c>200</c> 만 적는다.</param>
/// <param name="Parameters">질의·경로 파라미터.</param>
/// <param name="Protected">관리 토큰이 필요한가 (A-06).</param>
public readonly record struct ApiRoute(
    string Path,
    string OperationId,
    string Summary,
    string Description,
    Type? Response,
    ImmutableArray<ApiParameter> Parameters,
    bool Protected);

/// <summary>
/// OpenAPI 3.1 명세를 <b>코드에서 뽑는다</b> (E-05).
///
/// <para>
/// <b>왜 <c>Microsoft.AspNetCore.OpenApi</c> 를 쓰지 않는가.</b> 그 패키지는 <b>돌고 있는 앱</b>에서
/// 문서를 만든다 — <c>docs/openapi.json</c> 을 생성물로 커밋하려면 빌드나 테스트가 서버를 띄워야
/// 하고, 그러면 포트·수명·종료가 생성 과정에 끼어든다. 이 저장소는 이미
/// <c>docs/schema/</c>·<c>docs/wire/</c> 를 <b>테스트가 만들고 대조하는</b> 방식으로 두고 있고,
/// 명세도 같은 자리에 둔다.
/// </para>
///
/// <para>
/// <b>응답 스키마는 타입에서 뽑는다.</b> DTO 를 고치면 명세가 따라오고, 안 따라오면
/// <c>OpenApiTests</c> 가 깨진다 — 손으로 쓴 명세는 반드시 코드와 어긋난다.
/// </para>
/// </summary>
internal static class OpenApiCatalog
{
    /// <summary>발행 경로. 저장소 루트 기준이다.</summary>
    public const string Path = "docs/openapi.json";

    /// <summary>살아 있는 서버가 명세를 주는 자리. 툴 러너가 여기서 받아 간다.</summary>
    public const string Route = "/openapi/v1.json";

    /// <summary>
    /// 발행하는 라우트. <b>순서가 명세 순서다.</b>
    ///
    /// <para>
    /// <b>여기 없는 라우트는 명세에 없다.</b> 대시보드 HTML·CSV 처럼 사람이 브라우저로 여는 것은
    /// 툴이 부를 것이 아니라서 뺐다 — 명세는 <b>기계가 호출할 것</b>의 목록이다.
    /// </para>
    /// </summary>
    public static ImmutableArray<ApiRoute> Routes { get; } =
    [
        new ApiRoute(
            "/status",
            "get_status",
            "서버 요약",
            "틱·게임 시각·링크 상태·계기 한 장. \"돌고 있나\" 에 답한다.",
            typeof(HostSnapshot),
            [],
            Protected: true),

        new ApiRoute(
            "/npcs",
            "list_npcs",
            "NPC 벌크 요약",
            "존·아키타입·플래그·상태로 거르고 커서로 넘긴다. Matched 는 필터에 걸린 총수이고 "
            + "Returned 는 이 쪽에 실린 수다 — 둘은 다르다.",
            typeof(NpcListPage),
            [
                new ApiParameter("zone", "string", "존 id. 모르는 값이면 0건이다"),
                new ApiParameter("archetype", "string", "아키타입 id"),
                new ApiParameter("flag", "string", "월드 플래그 이름. 그 플래그가 선 NPC 만"),
                new ApiParameter("status", "string", "스텝 상태 — Ready·Waiting·Done·Unspawned·Completed·Failed"),
                new ApiParameter("limit", "integer", "한 쪽 크기. 1~1000, 기본 100"),
                new ApiParameter("cursor", "integer", "이 슬롯부터. 앞 응답의 nextCursor 를 넣는다"),
            ],
            Protected: true),

        new ApiRoute(
            "/npc/{id}",
            "get_npc",
            "NPC 한 마리의 속사정",
            "플랜·스텝·플래그·인벤토리·최근 사건. \"왜 저기로 가는가\" 에 답한다.",
            typeof(NpcTrace),
            [new ApiParameter("id", "integer", "NPC 슬롯 첨자", Required: true)],
            Protected: true),

        new ApiRoute(
            "/npc/{id}/context",
            "get_npc_context",
            "대화·진단용 최소 맥락",
            "전부 id·enum·숫자다. 플레이어가 쓴 문자열은 들어 있지 않다 — "
            + "이 응답이 그대로 프롬프트에 실릴 수 있기 때문이다.",
            typeof(NpcContext),
            [new ApiParameter("id", "integer", "NPC 슬롯 첨자", Required: true)],
            Protected: true),

        new ApiRoute(
            "/buckets",
            "list_buckets",
            "플랜 스토어 상태",
            "집계는 항상 준다. 줄은 state 를 줬을 때만 — 2,880줄을 기본으로 뱉지 않는다.",
            typeof(BucketReport),
            [new ApiParameter("state", "string", "missing·pinned·filled. 비우면 집계만")],
            Protected: true),

        new ApiRoute(
            "/metrics",
            "get_metrics",
            "계기 전체",
            "패널 7종의 수치. 대시보드가 폴링하는 것과 같은 자료다.",
            null,
            [],
            Protected: true),

        new ApiRoute(
            "/healthz/live",
            "get_live",
            "살아 있는가",
            "틱 루프 하트비트. **무인증이다** — 오케스트레이터가 토큰을 들고 다니게 하지 않는다.",
            null,
            [],
            Protected: false),

        new ApiRoute(
            "/healthz/ready",
            "get_ready",
            "받을 준비가 됐는가",
            "링크·플랜 스토어·복원 판정까지 끝났는가. **무인증이다.**",
            null,
            [],
            Protected: false),
    ];

    /// <summary>명세 전문을 만든다.</summary>
    /// <param name="version">서버 버전. <c>info.version</c> 에 실린다.</param>
    public static string Render(string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);

        var schemas = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (ApiRoute route in Routes)
        {
            if (route.Response is { } type)
            {
                Collect(type, schemas);
            }
        }

        var text = new StringBuilder(16 * 1024);

        text.Append("{\n");
        text.Append("  \"openapi\": \"3.1.0\",\n");
        text.Append("  \"info\": {\n");
        text.Append("    \"title\": \"LlmNpcServer 관리·질의 API\",\n");
        text.Append(CultureInfo.InvariantCulture, $"    \"version\": {Quote(version)},\n");
        text.Append("    \"description\": \"");
        text.Append("읽기 전용 질의(B-08)와 관리(A-11) API. 생성물이다 — 손으로 고치지 않는다. ");
        text.Append("런타임 게임 로직이 이 API 에 의존하면 안 된다: 게임서버와의 경계는 링크이고, ");
        text.Append("명령은 fire-and-forget 이다(N1). 이 API 는 도구·대화 서비스·운영 진단 전용이다.");
        text.Append("\"\n");
        text.Append("  },\n");

        text.Append("  \"components\": {\n");
        text.Append("    \"securitySchemes\": {\n");
        text.Append("      \"adminToken\": {\n");
        text.Append("        \"type\": \"http\",\n");
        text.Append("        \"scheme\": \"bearer\",\n");
        text.Append("        \"description\": \"NPC_ADMIN_TOKEN 환경변수의 값 (A-06)\"\n");
        text.Append("      }\n");
        text.Append("    },\n");
        text.Append("    \"schemas\": {\n");

        int written = 0;

        foreach ((string name, string schema) in schemas)
        {
            text.Append(CultureInfo.InvariantCulture, $"      {Quote(name)}: {schema}");
            text.Append(++written < schemas.Count ? ",\n" : "\n");
        }

        text.Append("    }\n");
        text.Append("  },\n");

        text.Append("  \"paths\": {\n");

        for (int i = 0; i < Routes.Length; i++)
        {
            AppendPath(text, Routes[i], last: i + 1 == Routes.Length);
        }

        text.Append("  }\n}\n");

        return text.ToString().ReplaceLineEndings("\n");
    }

    private static void AppendPath(StringBuilder text, ApiRoute route, bool last)
    {
        text.Append(CultureInfo.InvariantCulture, $"    {Quote(route.Path)}: {{\n");
        text.Append("      \"get\": {\n");
        text.Append(CultureInfo.InvariantCulture, $"        \"operationId\": {Quote(route.OperationId)},\n");
        text.Append(CultureInfo.InvariantCulture, $"        \"summary\": {Quote(route.Summary)},\n");
        text.Append(CultureInfo.InvariantCulture, $"        \"description\": {Quote(route.Description)},\n");

        if (route.Protected)
        {
            text.Append("        \"security\": [{ \"adminToken\": [] }],\n");
        }

        if (!route.Parameters.IsEmpty)
        {
            text.Append("        \"parameters\": [\n");

            for (int i = 0; i < route.Parameters.Length; i++)
            {
                ApiParameter p = route.Parameters[i];
                bool inPath = route.Path.Contains('{' + p.Name + '}', StringComparison.Ordinal);

                text.Append("          {\n");
                text.Append(CultureInfo.InvariantCulture, $"            \"name\": {Quote(p.Name)},\n");
                text.Append(CultureInfo.InvariantCulture, $"            \"in\": {Quote(inPath ? "path" : "query")},\n");
                text.Append(CultureInfo.InvariantCulture, $"            \"required\": {(p.Required ? "true" : "false")},\n");
                text.Append(CultureInfo.InvariantCulture, $"            \"description\": {Quote(p.Description)},\n");
                text.Append(CultureInfo.InvariantCulture, $"            \"schema\": {{ \"type\": {Quote(p.Type)} }}\n");
                text.Append(i + 1 < route.Parameters.Length ? "          },\n" : "          }\n");
            }

            text.Append("        ],\n");
        }

        text.Append("        \"responses\": {\n");
        text.Append("          \"200\": {\n");
        text.Append("            \"description\": \"성공\"");

        if (route.Response is { } type)
        {
            text.Append(",\n            \"content\": {\n");
            text.Append("              \"application/json\": {\n");
            text.Append(CultureInfo.InvariantCulture,
                $"                \"schema\": {{ \"$ref\": \"#/components/schemas/{type.Name}\" }}\n");
            text.Append("              }\n");
            text.Append("            }\n");
        }
        else
        {
            text.Append('\n');
        }

        text.Append("          }");

        if (route.Protected)
        {
            text.Append(",\n          \"401\": { \"description\": \"관리 토큰이 없거나 틀렸다\" }\n");
        }
        else
        {
            text.Append('\n');
        }

        text.Append("        }\n");
        text.Append("      }\n");
        text.Append(last ? "    }\n" : "    },\n");
    }

    /// <summary>
    /// 타입에서 스키마를 뽑고, 중첩된 레코드도 같이 담는다.
    ///
    /// <b>리플렉션으로 속성을 훑는다.</b> DTO 가 전부 <c>readonly record struct</c> 에
    /// 원시 타입·문자열·배열뿐이라 이것으로 충분하고, 직렬화기를 끌어오지 않아
    /// 트리밍 주석이 번지지 않는다.
    /// </summary>
    private static void Collect(Type type, SortedDictionary<string, string> into)
    {
        if (into.ContainsKey(type.Name))
        {
            return;
        }

        into[type.Name] = string.Empty;   // 재귀 방지

        var body = new StringBuilder(1024);
        var nested = new List<Type>();

        body.Append("{\n        \"type\": \"object\",\n        \"properties\": {\n");

        PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        for (int i = 0; i < properties.Length; i++)
        {
            PropertyInfo p = properties[i];

            body.Append(CultureInfo.InvariantCulture, $"          {Quote(CamelCase(p.Name))}: ");
            body.Append(SchemaOf(p.PropertyType, nested));
            body.Append(i + 1 < properties.Length ? ",\n" : "\n");
        }

        body.Append("        }\n      }");

        into[type.Name] = body.ToString();

        foreach (Type child in nested)
        {
            Collect(child, into);
        }
    }

    private static string SchemaOf(Type type, List<Type> nested)
    {
        Type inner = Nullable.GetUnderlyingType(type) ?? type;

        if (inner.IsArray)
        {
            return $"{{ \"type\": \"array\", \"items\": {SchemaOf(inner.GetElementType()!, nested)} }}";
        }

        if (inner == typeof(string))
        {
            return "{ \"type\": \"string\" }";
        }

        if (inner == typeof(bool))
        {
            return "{ \"type\": \"boolean\" }";
        }

        if (inner == typeof(double) || inner == typeof(float))
        {
            return "{ \"type\": \"number\" }";
        }

        if (inner.IsPrimitive)
        {
            return "{ \"type\": \"integer\" }";
        }

        if (inner.IsEnum)
        {
            return "{ \"type\": \"string\" }";
        }

        // 중첩 레코드. 이름으로 참조하고 뒤에서 같이 담는다.
        nested.Add(inner);

        return $"{{ \"$ref\": \"#/components/schemas/{inner.Name}\" }}";
    }

    /// <summary>ASP.NET 의 기본 직렬화와 같은 camelCase.</summary>
    private static string CamelCase(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static string Quote(string value)
    {
        var text = new StringBuilder(value.Length + 2);

        text.Append('"');

        foreach (char c in value)
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        text.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    }
                    else
                    {
                        text.Append(c);
                    }

                    break;
            }
        }

        return text.Append('"').ToString();
    }
}
