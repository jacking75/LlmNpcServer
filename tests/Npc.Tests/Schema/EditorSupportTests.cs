using System.Text.Json;
using System.Text.Json.Nodes;
using Npc.MasterData.Schema;

namespace Npc.Tests.Schema;

/// <summary>
/// F-08 에디터 지원.
///
/// <b>설정이 실제 파일을 가리켜야 한다.</b> 스키마 경로가 하나라도 틀리면 그 파일만 조용히
/// 자동완성이 죽는다 — 에디터는 오류를 내지 않고 그냥 아무것도 안 해 준다.
/// </summary>
public sealed class EditorSupportTests
{
    private static readonly string s_vscode = TestPaths.At(".vscode");

    [Theory]
    [InlineData("settings.json")]
    [InlineData("tasks.json")]
    [InlineData("npc.code-snippets")]
    public void Editor_FilesExistAndParse(string name)
    {
        string path = Path.Combine(s_vscode, name);

        Assert.True(File.Exists(path), $".vscode/{name} 이 없다");

        // 주석(//)이 든 JSONC 다. VS Code 는 읽지만 엄격 파서는 못 읽는다.
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    /// <summary><b>매핑이 실제 스키마 파일을 가리켜야 한다.</b> 틀리면 그 파일만 조용히 죽는다.</summary>
    [Fact]
    public void Editor_SchemaMappingsPointAtRealFiles()
    {
        JsonNode settings = Load("settings.json");
        JsonArray mappings = settings["json.schemas"]!.AsArray();

        Assert.Equal(SchemaCatalog.Entries.Length, mappings.Count);

        foreach (JsonNode? mapping in mappings)
        {
            string url = mapping!["url"]!.GetValue<string>();
            string path = TestPaths.At(url.Replace("./", string.Empty, StringComparison.Ordinal));

            Assert.True(File.Exists(path), $"{url} 이 없다. `npc schema` 를 돌린다.");

            string match = mapping["fileMatch"]!.AsArray()[0]!.GetValue<string>();

            Assert.True(
                File.Exists(TestPaths.At(match.TrimStart('/'))),
                $"{match} 이 없다 — 매핑이 실제 마스터데이터와 어긋났다.");
        }
    }

    /// <summary>발행 목록의 모든 파일에 매핑이 있어야 한다. 하나 빠뜨리면 그 파일만 도움이 없다.</summary>
    [Fact]
    public void Editor_MapsEveryPublishedSchema()
    {
        JsonNode settings = Load("settings.json");

        string joined = settings["json.schemas"]!.ToJsonString();

        foreach (SchemaEntry entry in SchemaCatalog.Entries)
        {
            Assert.Contains(entry.File, joined, StringComparison.Ordinal);
            Assert.Contains(entry.DataFile, joined, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// 마스터데이터 파일이 자기 스키마를 가리켜야 한다 — 설정 없이 파일만 열어도 동작하려면 필요하다.
    /// </summary>
    [Fact]
    public void MasterData_PointsAtItsSchema()
    {
        foreach (SchemaEntry entry in SchemaCatalog.Entries)
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(TestPaths.MasterData, entry.DataFile)));

            Assert.True(
                document.RootElement.TryGetProperty("$schema", out JsonElement schema),
                $"{entry.DataFile} 에 $schema 가 없다");

            string expected = "../" + SchemaCatalog.Directory + "/" + entry.File;

            Assert.Equal(expected, schema.GetString());
        }
    }

    /// <summary>
    /// <b>작업이 파이프라인과 같은 것을 돌려야 한다.</b> 갈리면 "내 기계에서는 됐다" 를
    /// 환경 차이로 좁힐 수 없다.
    /// </summary>
    [Fact]
    public void Editor_TasksMatchThePipeline()
    {
        string tasks = File.ReadAllText(Path.Combine(s_vscode, "tasks.json"));

        Assert.Contains("build.ps1", tasks, StringComparison.Ordinal);
        Assert.Contains("validate", tasks, StringComparison.Ordinal);
        Assert.Contains("regen", tasks, StringComparison.Ordinal);
        Assert.Contains("Category!=Golden&Category!=Gate&Category!=Load", tasks, StringComparison.Ordinal);
    }

    /// <summary>스니펫이 빠뜨리기 쉬운 필드를 자리와 함께 내야 한다 — 그것이 스니펫의 이유다.</summary>
    [Fact]
    public void Snippets_CarryTheEasilyMissedFields()
    {
        string snippets = File.ReadAllText(Path.Combine(s_vscode, "npc.code-snippets"));

        // 폴백 플랜이 없으면 V7 로 기동 실패, timeout_s 가 0 이면 스텝이 영원히 안 끝난다.
        Assert.Contains("fallback_plan", snippets, StringComparison.Ordinal);
        Assert.Contains("timeout_s", snippets, StringComparison.Ordinal);

        // duty_hours 없이 Guard·Patrol 을 허용하는 것이 CLAUDE.md §7 의 함정이다 (V12).
        Assert.Contains("duty_hours", snippets, StringComparison.Ordinal);
        Assert.Contains("V12", snippets, StringComparison.Ordinal);

        // 번호는 스니펫이 아니라 next-code 가 답한다.
        Assert.Contains("next-code", snippets, StringComparison.Ordinal);
    }

    private static JsonNode Load(string name) =>
        JsonNode.Parse(
            File.ReadAllText(Path.Combine(s_vscode, name)),
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            })!;
}
