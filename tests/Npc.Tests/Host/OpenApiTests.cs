using System.Text.Json;
using System.Text.Json.Nodes;
using Npc.Host.Api;

namespace Npc.Tests.Host;

/// <summary>
/// E-05 — OpenAPI 명세가 코드와 같은가.
///
/// <para>
/// <b>명세는 생성물이다.</b> 손으로 쓴 명세는 반드시 코드와 어긋나고, 어긋난 명세는
/// 없는 것보다 나쁘다 — 툴 러너가 그것을 근거로 호출을 만든다.
/// </para>
///
/// <para>
/// 이 회차는 <c>docs/openapi.json</c> 을 다시 쓴다. 어긋나면 새로 뽑은 것을 남기고 실패한다 —
/// 사람이 diff 를 보고 "의도한 변경인가" 를 판단한 뒤 커밋한다.
/// </para>
/// </summary>
public sealed class OpenApiTests
{
    /// <summary>명세에 실을 서버 버전. 회차마다 흔들리면 안 되므로 고정값을 쓴다.</summary>
    private const string Version = "docs";

    /// <summary>커밋된 <c>docs/openapi.json</c> 이 코드와 같아야 한다.</summary>
    [Fact]
    public void OpenApi_MatchesTheCode()
    {
        string generated = OpenApiCatalog.Render(Version);
        string path = TestPaths.At(OpenApiCatalog.Path.Split('/'));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string? existing = File.Exists(path)
            ? File.ReadAllText(path).ReplaceLineEndings("\n")
            : null;

        if (existing == generated)
        {
            return;
        }

        File.WriteAllText(path, generated);

        Assert.Fail(
            existing is null
                ? $"{OpenApiCatalog.Path} 이 없어 새로 썼다. 커밋한다."
                : $"{OpenApiCatalog.Path} 이 코드와 다르다. 새로 뽑은 것으로 덮었으니 diff 를 "
                  + "보고 의도한 변경인지 확인한 뒤 커밋한다 — 툴 러너가 이 파일로 호출을 만든다.");
    }

    /// <summary><b>유효한 JSON 이어야 한다.</b> 손으로 조립하므로 이것부터 본다.</summary>
    [Fact]
    public void OpenApi_IsValidJson()
    {
        JsonNode root = JsonNode.Parse(OpenApiCatalog.Render(Version))
            ?? throw new InvalidOperationException("명세가 비었다");

        Assert.Equal("3.1.0", (string?)root["openapi"]);
        Assert.Equal(Version, (string?)root["info"]?["version"]);
        Assert.NotNull(root["paths"]);
        Assert.NotNull(root["components"]?["schemas"]);
    }

    /// <summary>
    /// <b>모든 라우트가 명세에 있고, 응답 스키마 참조가 실제로 풀린다.</b>
    /// 풀리지 않는 <c>$ref</c> 는 툴 러너가 그 응답을 못 읽는다는 뜻이다.
    /// </summary>
    [Fact]
    public void OpenApi_EveryRefResolves()
    {
        JsonNode root = JsonNode.Parse(OpenApiCatalog.Render(Version))!;
        JsonObject schemas = root["components"]!["schemas"]!.AsObject();
        JsonObject paths = root["paths"]!.AsObject();

        Assert.Equal(OpenApiCatalog.Routes.Length, paths.Count);

        foreach (ApiRoute route in OpenApiCatalog.Routes)
        {
            Assert.True(paths.ContainsKey(route.Path), $"{route.Path} 가 명세에 없다");
        }

        foreach (string reference in Refs(root))
        {
            string name = reference["#/components/schemas/".Length..];

            Assert.True(schemas.ContainsKey(name), $"$ref 가 안 풀린다: {reference}");
        }
    }

    /// <summary>
    /// <b>보호되는 라우트에는 <c>security</c> 와 401 이 있다</b> (A-06).
    /// 명세가 인증을 안 적으면 툴 러너가 토큰 없이 부르고 401 을 장애로 읽는다.
    /// </summary>
    [Fact]
    public void OpenApi_MarksProtectedRoutes()
    {
        JsonObject paths = JsonNode.Parse(OpenApiCatalog.Render(Version))!["paths"]!.AsObject();

        foreach (ApiRoute route in OpenApiCatalog.Routes)
        {
            JsonNode get = paths[route.Path]!["get"]!;

            if (route.Protected)
            {
                Assert.NotNull(get["security"]);
                Assert.NotNull(get["responses"]!["401"]);
            }
            else
            {
                Assert.Null(get["security"]);
            }

            // 명세의 보호 여부가 서버의 판정과 같아야 한다 — 다르면 둘 중 하나가 거짓이다.
            Assert.Equal(route.Protected, AdminAuth.IsProtected(SamplePath(route.Path)));
        }
    }

    /// <summary>
    /// <c>operationId</c> 는 툴 이름이 된다. <b>중복되면 툴 정의가 겹친다.</b>
    /// </summary>
    [Fact]
    public void OpenApi_OperationIdsAreUnique()
    {
        string[] ids = [.. OpenApiCatalog.Routes.Select(r => r.OperationId)];

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^[a-z][a-z0-9_]*$", id));
    }

    /// <summary>
    /// <b>경로 파라미터는 전부 선언돼 있어야 한다.</b> 빠지면 툴 러너가 <c>{id}</c> 를
    /// 문자 그대로 보내고 404 가 난다.
    /// </summary>
    [Fact]
    public void OpenApi_DeclaresEveryPathParameter()
    {
        foreach (ApiRoute route in OpenApiCatalog.Routes)
        {
            foreach (string name in Placeholders(route.Path))
            {
                Assert.Contains(
                    route.Parameters,
                    p => string.Equals(p.Name, name, StringComparison.Ordinal) && p.Required);
            }
        }
    }

    /// <summary>
    /// <b>질의 API 의 응답 타입이 명세에 실린다.</b> B-08 이 만든 네 라우트가 근거다.
    /// </summary>
    [Fact]
    public void OpenApi_CoversTheQueryApi()
    {
        JsonObject schemas = JsonNode.Parse(OpenApiCatalog.Render(Version))!["components"]!["schemas"]!.AsObject();

        foreach (string name in new[]
        {
            nameof(NpcListPage), nameof(NpcSummary), nameof(NpcContext),
            nameof(BucketReport), nameof(BucketRow), nameof(NpcTrace),
        })
        {
            Assert.True(schemas.ContainsKey(name), $"{name} 스키마가 없다");
        }

        // 속성 이름은 ASP.NET 의 기본과 같은 camelCase 여야 한다 — 아니면 툴이 못 읽는다.
        JsonObject page = schemas[nameof(NpcListPage)]!["properties"]!.AsObject();

        Assert.True(page.ContainsKey("nextCursor"));
        Assert.True(page.ContainsKey("npcs"));
    }

    private static IEnumerable<string> Refs(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach ((string key, JsonNode? value) in obj)
                {
                    if (string.Equals(key, "$ref", StringComparison.Ordinal) && value is not null)
                    {
                        yield return (string)value!;
                        continue;
                    }

                    if (value is null)
                    {
                        continue;
                    }

                    foreach (string found in Refs(value))
                    {
                        yield return found;
                    }
                }

                break;

            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    foreach (string found in Refs(item))
                    {
                        yield return found;
                    }
                }

                break;

            default:
                break;
        }
    }

    /// <summary><c>{id}</c> 를 실제 값으로 바꾼 경로. 인증 판정에 쓴다.</summary>
    private static string SamplePath(string path) =>
        System.Text.RegularExpressions.Regex.Replace(
            path, "\\{[^}]+\\}", "1", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2));

    private static IEnumerable<string> Placeholders(string path)
    {
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(
                path, "\\{([^}]+)\\}", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)))
        {
            yield return m.Groups[1].Value;
        }
    }
}
