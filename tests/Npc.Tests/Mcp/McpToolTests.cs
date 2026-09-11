using System.Collections.Immutable;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Npc.Mcp;
using Npc.Mcp.Tools;

namespace Npc.Tests.Mcp;

/// <summary>
/// E-03 — MCP 서버.
///
/// <para>
/// <b>여기서 지키는 것은 넷이다.</b> (1) 쓰기 툴은 <c>--allow-write</c> 없이는 등록되지 않는다,
/// (2) 툴 이름이 로드맵의 표와 같다, (3) 툴마다 설명이 있다(모델이 그것으로 고른다),
/// (4) <c>prebake_run</c> 의 예산 상한을 인자로 우회할 수 없다.
/// </para>
/// </summary>
public sealed class McpToolTests
{
    private static readonly McpOptions s_options = new()
    {
        MasterData = TestPaths.MasterData,
        PlanStore = TestPaths.At("planstore"),
        Root = TestPaths.RepoRoot,
    };

    /// <summary>로드맵 E-03 의 툴 표. <b>이름을 바꾸면 호스트 설정이 깨진다.</b></summary>
    private static readonly ImmutableArray<string> s_readTools =
    [
        "masterdata_validate",
        "masterdata_explain",
        "masterdata_next_code",
        "masterdata_scaffold",
        "masterdata_diff",
        "masterdata_regen_check",
        "plan_validate",
        "plan_repair",
        "plan_narrate",
        "bucket_status",
        "server_status",
        "server_metrics",
        "server_npc",
        "server_npcs",
        "docs_search",
    ];

    private static readonly ImmutableArray<string> s_writeTools =
    [
        "masterdata_apply",
        "server_admin",
        "prebake_run",
    ];

    /// <summary>읽기 툴이 전부 있다.</summary>
    [Fact]
    public void ReadTools_AreAllRegistered()
    {
        ImmutableArray<string> found = ToolNames(McpToolSet.ReadToolTypes);

        Assert.Equal([.. s_readTools.Order(StringComparer.Ordinal)], [.. found.Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// <b>쓰기 툴은 읽기 타입에 섞여 있지 않다.</b> 섞이면 <c>--allow-write</c> 가 아무것도 막지 못한다.
    /// </summary>
    [Fact]
    public void WriteTools_AreSeparate()
    {
        ImmutableArray<string> read = ToolNames(McpToolSet.ReadToolTypes);

        foreach (string name in s_writeTools)
        {
            Assert.DoesNotContain(name, read);
        }

        Assert.Equal(
            [.. s_writeTools.Order(StringComparer.Ordinal)],
            [.. ToolNames(McpToolSet.WriteToolTypes).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// 툴마다 설명이 있어야 한다. <b>모델은 설명으로 고른다</b> —
    /// 없으면 이름만 보고 찍고, 찍으면 틀린 툴을 부른다.
    /// </summary>
    [Fact]
    public void EveryTool_HasADescription()
    {
        foreach (MethodInfo method in Methods([.. McpToolSet.ReadToolTypes, .. McpToolSet.WriteToolTypes]))
        {
            string? text = method.GetCustomAttribute<DescriptionAttribute>()?.Description;

            Assert.False(
                string.IsNullOrWhiteSpace(text),
                $"{method.DeclaringType?.Name}.{method.Name} 에 설명이 없다");

            Assert.True(text!.Length >= 20, $"{method.Name} 의 설명이 너무 짧다: {text}");
        }
    }

    /// <summary>리소스도 전부 이름과 설명을 갖는다.</summary>
    [Fact]
    public void EveryResource_HasNameAndDescription()
    {
        MethodInfo[] methods = [.. typeof(Resources)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttribute<McpServerResourceAttribute>() is not null)];

        Assert.NotEmpty(methods);

        foreach (MethodInfo method in methods)
        {
            McpServerResourceAttribute attribute = method.GetCustomAttribute<McpServerResourceAttribute>()!;

            Assert.False(string.IsNullOrWhiteSpace(attribute.Name), $"{method.Name} 에 이름이 없다");
            Assert.StartsWith("npc://", attribute.UriTemplate!, StringComparison.Ordinal);
            Assert.NotNull(method.GetCustomAttribute<DescriptionAttribute>());
        }
    }

    /// <summary>
    /// <b>예산 상한은 코드가 강제한다</b> (CLAUDE.md §2.7). 인자로 큰 값을 줘도 잘려야 한다.
    ///
    /// <para>실제로 프리베이크를 돌리지 않는다 — <c>only</c> 없이 부르면 거절한다는 것만 본다.</para>
    /// </summary>
    [Fact]
    public void Prebake_RefusesWithoutAFilter()
    {
        var tools = new WriteTools(s_options);

        string result = tools.Prebake(only: "   ");

        Assert.Contains("거절", result, StringComparison.Ordinal);
        Assert.Equal(1.0, McpOptions.PrebakeBudgetCapUsd);
    }

    /// <summary>모르는 action 은 부르기 전에 거절한다 — 잘못된 URL 로 서버를 두드리지 않는다.</summary>
    [Fact]
    public async Task Admin_RefusesUnknownAction()
    {
        Assert.Contains(
            "모르는 action",
            await WriteTools.AdminAsync("nope", "token"),
            StringComparison.Ordinal);

        Assert.Contains(
            "token 이 없다",
            await WriteTools.AdminAsync("reload", string.Empty),
            StringComparison.Ordinal);
    }

    /// <summary>검증 툴이 CLI 와 같은 답을 낸다 — <b>껍질이 둘이고 코어는 하나다</b>.</summary>
    [Fact]
    public void Validate_MatchesTheCli()
    {
        var tools = new MasterDataTools(s_options);

        string fromMcp = tools.Validate(json: true);

        using JsonDocument document = JsonDocument.Parse(fromMcp);

        Assert.True(document.RootElement.TryGetProperty("violations", out JsonElement violations));
        Assert.Equal(JsonValueKind.Array, violations.ValueKind);
        Assert.Equal(0, violations.GetArrayLength());
    }

    /// <summary>문서 검색이 실제로 찾는다. 못 찾으면 <b>왜 못 찾았는지</b>를 낸다.</summary>
    [Fact]
    public void DocsSearch_FindsAndExplains()
    {
        var tools = new DocsTools(s_options);

        Assert.Contains("CLAUDE.md", tools.Search("프리픽스"), StringComparison.Ordinal);
        Assert.Contains("찾지 못했다", tools.Search("zzz-없는말-zzz"), StringComparison.Ordinal);
        Assert.Contains("query 가 비어 있다", tools.Search("  "), StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>리소스 이름으로 저장소 밖을 읽을 수 없다.</b> 이름은 호스트가 주는 값이고,
    /// 막지 않으면 MCP 서버가 파일 유출 통로가 된다.
    /// </summary>
    [Theory]
    [InlineData("../../../../../../etc/passwd")]
    [InlineData("..\\..\\..\\windows\\win.ini")]
    [InlineData("actions/../../../secret")]
    public void Resources_RefuseDirectoryEscape(string name)
    {
        string result = new Resources(s_options).Schema(name);

        Assert.DoesNotContain("root:", result, StringComparison.Ordinal);
        Assert.Contains("이 없다", result, StringComparison.Ordinal);
    }

    /// <summary>있는 스키마는 읽힌다.</summary>
    [Fact]
    public void Resources_ReadRealFiles()
    {
        var resources = new Resources(s_options);

        Assert.Contains("\"$schema\"", resources.Schema("actions"), StringComparison.Ordinal);
        Assert.Contains("절대 규칙", resources.Rules(), StringComparison.Ordinal);
    }

    /// <summary>플랜 툴은 빈 입력을 조용히 삼키지 않는다.</summary>
    [Fact]
    public void PlanTools_RejectEmptyInput()
    {
        var tools = new PlanTools(s_options);

        Assert.Contains("plan 이 비어 있다", tools.Validate(string.Empty, "x"), StringComparison.Ordinal);
        Assert.Contains("bucket 이 비어 있다", tools.Validate("{}", string.Empty), StringComparison.Ordinal);
    }

    /// <summary>기본은 읽기 전용이다.</summary>
    [Fact]
    public void Options_DefaultToReadOnly()
    {
        Assert.True(McpOptions.TryParse([], out McpOptions options, out string? error));
        Assert.Null(error);
        Assert.False(options.AllowWrite);

        Assert.True(McpOptions.TryParse(["--allow-write"], out options, out _));
        Assert.True(options.AllowWrite);

        Assert.False(McpOptions.TryParse(["--nope"], out _, out error));
        Assert.Contains("모르는 옵션", error!, StringComparison.Ordinal);
    }

    /// <summary>저장소 루트의 <c>.mcp.json</c> 이 이 서버를 가리킨다.</summary>
    [Fact]
    public void McpJson_PointsAtThisServer()
    {
        string path = TestPaths.At(".mcp.json");

        Assert.True(File.Exists(path), ".mcp.json 이 없다 — 호스트가 서버를 못 찾는다");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

        JsonElement server = document.RootElement.GetProperty("mcpServers").GetProperty("npc-server");
        string args = server.GetProperty("args").ToString();

        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        Assert.Contains("tools/Npc.Mcp", args, StringComparison.Ordinal);

        // 쓰기는 기본으로 꺼져 있어야 한다.
        Assert.DoesNotContain("--allow-write", args, StringComparison.Ordinal);
    }

    private static ImmutableArray<string> ToolNames(IEnumerable<Type> types) =>
        [.. Methods(types).Select(m => m.GetCustomAttribute<McpServerToolAttribute>()!.Name!)];

    private static IEnumerable<MethodInfo> Methods(IEnumerable<Type> types) =>
        types.SelectMany(t => t
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null));
}
