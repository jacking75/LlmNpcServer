using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Npc.MasterData;
using Npc.MasterData.Schema;

namespace Npc.Tests.Schema;

/// <summary>
/// E-02 JSON Schema 발행.
///
/// <b>스키마가 틀리면 없는 것보다 나쁘다.</b> LLM 과 에디터가 그것을 근거로 틀린 것을 만들고,
/// 사람은 "스키마가 통과했으니 맞겠지" 라고 넘긴다.
/// 그래서 여기서 <b>커밋된 마스터데이터가 실제로 통과하는지</b>를 본다.
/// </summary>
public sealed class SchemaCatalogTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static readonly ImmutableSortedDictionary<string, string> s_schemas =
        SchemaCatalog.Render(s_data);

    /// <summary><b>이것이 이 태스크의 요점이다.</b> 지금 마스터데이터가 지금 스키마를 통과한다.</summary>
    [Theory]
    [MemberData(nameof(DataFiles))]
    public void Schema_AcceptsTheCommittedMasterData(string schemaFile, string dataFile)
    {
        JsonSchema schema = Parse(s_schemas[schemaFile]);

        using JsonDocument instance = Load(dataFile);

        EvaluationResults result = schema.Evaluate(
            instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, Explain(dataFile, result));
    }

    /// <summary>정적 변형도 통과해야 한다 — 허용 값만 뺀 같은 구조다.</summary>
    [Theory]
    [MemberData(nameof(DataFiles))]
    public void BaseSchema_AcceptsTheCommittedMasterData(string schemaFile, string dataFile)
    {
        string stem = schemaFile[..^SchemaCatalog.Suffix.Length];
        JsonSchema schema = Parse(s_schemas[stem + SchemaCatalog.BaseSuffix]);

        using JsonDocument instance = Load(dataFile);

        EvaluationResults result = schema.Evaluate(
            instance.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid, Explain(dataFile, result));
    }

    /// <summary>
    /// <b>허용 값은 마스터데이터에서 온다.</b> 코드에 목록을 적어 두면 그 순간 카탈로그와 어긋난다.
    /// </summary>
    [Fact]
    public void Schema_FillsAllowedValuesFromMasterData()
    {
        JsonNode archetypes = JsonNode.Parse(s_schemas["archetypes" + SchemaCatalog.Suffix])!;

        JsonArray actions = archetypes["properties"]!["archetypes"]!["items"]!
            ["properties"]!["allowed_actions"]!["items"]!["enum"]!.AsArray();

        Assert.Equal(s_data.Actions.Count, actions.Count);

        foreach (ActionDef def in s_data.Actions.Actions)
        {
            Assert.Contains(actions, n => n!.GetValue<string>() == def.Id);
        }
    }

    /// <summary>정적 변형에는 허용 값이 없다 — 마스터데이터 없이도 쓸 수 있어야 한다.</summary>
    [Fact]
    public void BaseSchema_HasNoAllowedValues()
    {
        JsonNode archetypes = JsonNode.Parse(s_schemas["archetypes" + SchemaCatalog.BaseSuffix])!;

        JsonNode? items = archetypes["properties"]!["archetypes"]!["items"]!
            ["properties"]!["allowed_actions"]!["items"];

        Assert.Null(items?["enum"]);
    }

    /// <summary>
    /// 스키마는 <b>지금 마스터데이터 상태</b>에 대한 것이다. 그 사실을 파일에 적어 둔다 —
    /// 안 적으면 다른 브랜치의 스키마를 그대로 쓰다 엉뚱한 허용 값을 믿는다.
    /// </summary>
    [Fact]
    public void Schema_RecordsTheContentHash()
    {
        foreach (SchemaEntry entry in SchemaCatalog.Entries)
        {
            JsonNode node = JsonNode.Parse(s_schemas[entry.File])!;

            Assert.Contains(s_data.ContentHash, node["$comment"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }

    /// <summary>같은 입력이면 바이트 동일이다 — CI 가 `git diff --exit-code` 로 볼 수 있어야 한다.</summary>
    [Fact]
    public void Schema_IsDeterministic()
    {
        Assert.Equal(s_schemas, SchemaCatalog.Render(s_data));
    }

    /// <summary>
    /// <c>$schema</c> 와 <c>_comment</c> 를 스키마가 거절하면 마스터데이터 파일에 그것을 못 붙인다.
    /// 붙일 수 없으면 에디터가 스키마를 찾지 못한다 (F-08).
    /// </summary>
    [Fact]
    public void Schema_AllowsEditorAndCommentFields()
    {
        foreach (SchemaEntry entry in SchemaCatalog.Entries)
        {
            JsonNode node = JsonNode.Parse(s_schemas[entry.File])!;

            Assert.NotNull(node["properties"]!["$schema"]);
            Assert.NotNull(node["properties"]!["_comment"]);
        }
    }

    /// <summary>
    /// <b>고의로 깨뜨린 것은 걸려야 한다.</b> 통과만 확인하면 스키마가 <c>{}</c> 여도 초록불이다.
    /// </summary>
    [Fact]
    public void Schema_RejectsBrokenData()
    {
        JsonSchema schema = Parse(s_schemas["archetypes" + SchemaCatalog.Suffix]);

        // ① 타입 불일치 — code 가 문자열이다.
        JsonNode wrongType = Mutable("archetypes.json");
        wrongType["archetypes"]![0]!["code"] = "영";

        Assert.False(Check(schema, wrongType), "code 를 문자열로 바꿨는데 통과했다.");

        // ② 허용 값 밖 — 카탈로그에 없는 액션.
        JsonNode unknownAction = Mutable("archetypes.json");
        unknownAction["archetypes"]![0]!["allowed_actions"]!.AsArray().Add("Fly");

        Assert.False(Check(schema, unknownAction), "없는 액션 'Fly' 가 통과했다.");
    }

    /// <summary>
    /// <b>스키마는 검증기가 아니다.</b> 교차 제약은 표현할 수 없다 — 그 사실을 문서에 적어 두고,
    /// 여기서 실제로 그런지 확인한다. 못 잡는다는 것을 알아야 검증기를 건너뛰지 않는다.
    /// </summary>
    [Fact]
    public void Schema_DoesNotCatchCrossFileRules()
    {
        JsonSchema schema = Parse(s_schemas["archetypes" + SchemaCatalog.Suffix]);

        // V5 위반 — 가중치 합이 1.0 이 아니다. 스키마는 필드 하나만 보므로 통과한다.
        JsonNode broken = Mutable("archetypes.json");
        broken["archetypes"]![0]!["population_weight"] = 0.99;

        Assert.True(
            Check(schema, broken),
            "스키마가 V5 를 잡았다 — 그렇다면 이 테스트의 전제가 틀렸으니 문서를 고친다.");

        // 검증기는 잡는다. 그것이 판정이다.
        Assert.Contains("V5", DescriptionOf("archetypes" + SchemaCatalog.Suffix), StringComparison.Ordinal);
    }

    /// <summary>발행 목록과 실제 파일이 맞아야 한다.</summary>
    [Fact]
    public void Schema_EntriesMatchTheRenderedFiles()
    {
        foreach (SchemaEntry entry in SchemaCatalog.Entries)
        {
            Assert.True(s_schemas.ContainsKey(entry.File), $"{entry.File} 이 없다");
            Assert.True(
                File.Exists(Path.Combine(TestPaths.MasterData, entry.DataFile)),
                $"{entry.DataFile} 이 masterdata 에 없다 — 발행 목록이 실제와 어긋났다.");
        }

        // 정적 변형까지 세면 두 배다.
        Assert.Equal(SchemaCatalog.Entries.Length * 2, s_schemas.Count);
    }

    /// <summary>커밋된 <c>docs/schema/</c> 가 최신이어야 한다. 생성물이므로 두 벌 관리하지 않는다.</summary>
    [Fact]
    public void PublishedSchemas_AreUpToDate()
    {
        string directory = TestPaths.At("docs", "schema");

        Assert.True(
            Directory.Exists(directory),
            "docs/schema 가 없다. `npc schema` 로 만든다.");

        foreach ((string name, string json) in s_schemas)
        {
            string path = Path.Combine(directory, name);

            Assert.True(File.Exists(path), $"{name} 이 없다. `npc schema` 를 다시 돌린다.");
            Assert.Equal(json.ReplaceLineEndings(), File.ReadAllText(path).ReplaceLineEndings());
        }
    }

    /// <summary>발행 목록 → (스키마 파일, 마스터데이터 파일).</summary>
    public static TheoryData<string, string> DataFiles
    {
        get
        {
            var data = new TheoryData<string, string>();

            foreach (SchemaEntry entry in SchemaCatalog.Entries)
            {
                data.Add(entry.File, entry.DataFile);
            }

            return data;
        }
    }

    private static string DescriptionOf(string schemaFile) =>
        JsonNode.Parse(s_schemas[schemaFile])!["description"]!.GetValue<string>();

    private static JsonSchema Parse(string json) =>
        JsonSerializer.Deserialize<JsonSchema>(json)
        ?? throw new InvalidOperationException("스키마를 읽지 못했다.");

    private static JsonDocument Load(string dataFile) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.MasterData, dataFile)));

    /// <summary>고쳐 볼 수 있는 사본. 고의 오류를 넣을 때 쓴다.</summary>
    private static JsonNode Mutable(string dataFile) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(TestPaths.MasterData, dataFile)))!;

    /// <summary><see cref="JsonNode"/> 를 평가한다. JsonSchema.Net 은 <see cref="JsonElement"/> 를 받는다.</summary>
    private static bool Check(JsonSchema schema, JsonNode instance)
    {
        using JsonDocument document = JsonDocument.Parse(instance.ToJsonString());

        return schema.Evaluate(document.RootElement).IsValid;
    }

    private static string Explain(string dataFile, EvaluationResults result)
    {
        IEnumerable<string> errors = (result.Details ?? [])
            .Where(d => d.Errors is { Count: > 0 })
            .SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))
            .Take(5);

        return $"{dataFile} 이 스키마를 통과하지 못했다:{Environment.NewLine}"
            + string.Join(Environment.NewLine, errors);
    }
}
