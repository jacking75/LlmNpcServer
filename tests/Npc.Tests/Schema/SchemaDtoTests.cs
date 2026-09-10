using System.Text.Json;
using System.Text.Json.Nodes;
using Npc.MasterData.Schema;

namespace Npc.Tests.Schema;

/// <summary>
/// E-02 — 스키마 전용 DTO 가 실제 파일과 어긋나지 않는다.
///
/// <b><c>world_flags.json</c> 과 <c>fallback_plans.json</c> 에는 로더 DTO 가 없다.</b>
/// 전자는 빌드 시점에 소스 생성기가 정규식으로 읽고, 후자는 <c>JsonNode</c> 로 읽어
/// 플랜 본문을 검증기에 그대로 넘긴다. 스키마 발행에는 모양이 필요하므로 DTO 를 따로 뒀는데,
/// <b>따로 둔 것은 반드시 어긋난다</b> — 파일에 필드가 늘었는데 DTO 가 그대로면
/// 스키마가 거짓을 말하고 LLM 이 그것을 근거로 필드를 빠뜨린다.
///
/// <para>
/// 여기서 <b>실제 파일을 DTO 로 왕복</b>시켜 손실이 없는지 본다. 필드가 늘면 이 테스트가 먼저 깨진다.
/// </para>
/// </summary>
public sealed class SchemaDtoTests
{
    /// <summary>
    /// 파일 → DTO → JSON 이 원본과 <b>같은 필드 집합</b>을 갖는가.
    ///
    /// 값까지 비교하지는 않는다 — 숫자 서식·키 순서가 달라질 수 있고, 여기서 잡으려는 것은
    /// "DTO 가 모르는 필드가 파일에 있다" 하나다.
    /// </summary>
    [Theory]
    [InlineData("world_flags.json")]
    [InlineData("fallback_plans.json")]
    [InlineData("archetypes.json")]
    [InlineData("items.json")]
    [InlineData("actions.json")]
    [InlineData("zones.json")]
    [InlineData("pois.json")]
    [InlineData("context_buckets.json")]
    [InlineData("interrupts.json")]
    public void Dto_KnowsEveryFieldInTheFile(string dataFile)
    {
        string stem = dataFile[..^".json".Length];

        // 스키마가 곧 "DTO 가 아는 필드" 다. 스키마에 없는 필드가 파일에 있으면 DTO 가 모른다.
        JsonNode schema = JsonNode.Parse(
            SchemaCatalog.Render(data: null)[stem + SchemaCatalog.BaseSuffix])!;

        using JsonDocument file = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestPaths.MasterData, dataFile)));

        var missing = new List<string>();

        Walk(file.RootElement, schema, "/", missing);

        Assert.True(
            missing.Count == 0,
            $"{dataFile} 에 DTO 가 모르는 필드가 있다: {string.Join(" · ", missing.Take(10))}"
            + $"{Environment.NewLine}스키마 전용 DTO 라면 src/Npc.MasterData/Schema/SchemaOnlyDtos.cs 를 고친다.");
    }

    /// <summary>
    /// 객체를 훑으며 스키마가 모르는 속성을 모은다.
    /// <c>_comment</c>·<c>$schema</c> 는 로더가 무시하는 필드라 스키마도 허용한다.
    /// </summary>
    private static void Walk(JsonElement value, JsonNode? schema, string path, List<string> missing)
    {
        // 스키마 노드는 true/false 일 수 있다 ("무엇이든 허용" · "무엇도 불허").
        // 그때는 색인이 던지므로 객체일 때만 들어간다.
        if (schema is not JsonObject)
        {
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                JsonNode? properties = schema["properties"];

                foreach (JsonProperty property in value.EnumerateObject())
                {
                    JsonNode? child = properties?[property.Name];

                    if (child is null)
                    {
                        // 자유 형식 맵(예: actions.params, emits.map)은 properties 가 없다.
                        // 그런 자리는 스키마가 열려 있는 것이지 모르는 것이 아니다.
                        if (properties is not null)
                        {
                            missing.Add(path + property.Name);
                        }

                        continue;
                    }

                    Walk(property.Value, child, path + property.Name + "/", missing);
                }

                break;

            case JsonValueKind.Array:
                JsonNode? items = schema["items"];

                foreach (JsonElement element in value.EnumerateArray())
                {
                    Walk(element, items, path + "*/", missing);
                }

                break;
        }
    }
}
