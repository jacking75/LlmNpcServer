using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Npc.Mcp.Tools;

/// <summary>
/// MCP 리소스 (E-03). <b>툴이 아니라 읽을거리다.</b>
///
/// <para>
/// 툴은 "부르라" 는 것이고 리소스는 "필요하면 읽으라" 는 것이다. 온보딩 컨텍스트와
/// 스키마는 매 호출에 넣을 것이 아니라 호스트가 필요할 때 당겨 가는 것이다.
/// </para>
///
/// <para>
/// <b>파일을 그대로 낸다.</b> 여기서 요약하면 저장소의 문서와 모델이 보는 것이 갈라지고,
/// 갈라진 쪽을 고치는 사람은 없다.
/// </para>
/// </summary>
[McpServerResourceType]
public sealed class Resources
{
    private readonly McpOptions _options;

    /// <summary>DI 가 만든다.</summary>
    /// <param name="options">서버 설정.</param>
    public Resources(McpOptions options) => _options = options;

    /// <summary>3,000토큰 압축 컨텍스트 (E-01).</summary>
    [McpServerResource(UriTemplate = "npc://context", Name = "context", MimeType = "text/markdown")]
    [Description("이 저장소를 처음 다룰 때 먼저 읽는 3,000토큰 압축 컨텍스트 (docs/llm/CONTEXT.md).")]
    public string Context() => Read("docs/llm/CONTEXT.md");

    /// <summary>하지 말아야 할 것 (E-01).</summary>
    [McpServerResource(
        UriTemplate = "npc://antipatterns", Name = "antipatterns", MimeType = "text/markdown")]
    [Description("이 저장소에서 하면 안 되는 것들과 그 이유 (docs/llm/ANTIPATTERNS.md).")]
    public string AntiPatterns() => Read("docs/llm/ANTIPATTERNS.md");

    /// <summary>검증 코드 사전 (E-04).</summary>
    [McpServerResource(
        UriTemplate = "npc://validation", Name = "validation", MimeType = "text/markdown")]
    [Description("검증 코드마다 무엇을 하면 되는지 (docs/llm/VALIDATION.md). 생성물이다.")]
    public string Validation() => Read("docs/llm/VALIDATION.md");

    /// <summary>용어 (E-01).</summary>
    [McpServerResource(UriTemplate = "npc://glossary", Name = "glossary", MimeType = "text/markdown")]
    [Description("이 저장소의 용어와 대응 타입 (docs/llm/GLOSSARY.md).")]
    public string Glossary() => Read("docs/llm/GLOSSARY.md");

    /// <summary>작업 지도 (CODEMAP).</summary>
    [McpServerResource(UriTemplate = "npc://codemap", Name = "codemap", MimeType = "text/markdown")]
    [Description("무엇을 하려면 어디를 여는가 — 작업별 파일 지도 (CODEMAP.md).")]
    public string CodeMap() => Read("CODEMAP.md");

    /// <summary>절대 규칙 (CLAUDE.md).</summary>
    [McpServerResource(UriTemplate = "npc://rules", Name = "rules", MimeType = "text/markdown")]
    [Description("이 저장소의 절대 규칙 — 틱 루프·계약·결정론·마스터데이터·프롬프트 (CLAUDE.md).")]
    public string Rules() => Read("CLAUDE.md");

    /// <summary>JSON 스키마 하나 (E-02).</summary>
    /// <param name="name">스키마 파일 이름(확장자 없이). 예: actions · npc_overrides.</param>
    [McpServerResource(
        UriTemplate = "npc://schema/{name}", Name = "schema", MimeType = "application/json")]
    [Description(
        "마스터데이터 파일 하나의 JSON Schema (docs/schema/). 허용 값이 지금 마스터데이터에서 나온다. "
        + "name 은 actions · items · archetypes · pois · zones · world_flags · interrupts · "
        + "fallback_plans · context_buckets · dialogue_lines · factions · npc_instances · npc_overrides.")]
    public string Schema(string name) => Read($"docs/schema/{Safe(name)}.schema.json");

    /// <summary>작업 절차 하나 (E-01).</summary>
    /// <param name="name">레시피 파일 이름(확장자 없이).</param>
    [McpServerResource(
        UriTemplate = "npc://recipes/{name}", Name = "recipe", MimeType = "text/markdown")]
    [Description("작업별 절차 (docs/llm/RECIPES/). 예: add-archetype · add-item-poi.")]
    public string Recipe(string name) => Read($"docs/llm/RECIPES/{Safe(name)}.md");

    /// <summary>
    /// 파일 하나를 읽는다. 없으면 <b>왜 없는지를 낸다</b> — 예외를 던지면 호스트가
    /// 리소스 목록 전체를 실패로 본다.
    /// </summary>
    private string Read(string relative)
    {
        string path = Path.Combine(_options.Root, relative.Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(path)
            ? File.ReadAllText(path)
            : $"{relative} 이 없다. --root 가 저장소 루트를 가리키는지 본다 (지금: {Path.GetFullPath(_options.Root)}).";
    }

    /// <summary>
    /// 경로 조각을 안전하게. <b>디렉터리 탈출을 막는다</b> — 리소스 이름은 호스트가 주는 값이고,
    /// 그 값으로 저장소 밖 파일을 읽히면 MCP 서버가 파일 유출 통로가 된다.
    /// </summary>
    private static string Safe(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? "_"
            : new string([.. name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')])
                .Replace("..", string.Empty, StringComparison.Ordinal);
}
