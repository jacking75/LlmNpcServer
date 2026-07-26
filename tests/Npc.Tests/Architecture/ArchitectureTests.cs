using System.Xml.Linq;

namespace Npc.Tests.Architecture;

/// <summary>
/// CLAUDE.md §3 의 프로젝트 의존 규칙을 강제한다.
/// 특히 Npc.Runtime → Npc.Llm 참조는 틱 루프에 LLM 호출이 들어올 길을 여는 것이라 금지다.
/// </summary>
public sealed class ArchitectureTests
{
    /// <summary>CLAUDE.md §3 · docs/11 §2 의 의존 그래프. 값이 허용된 참조 집합이다.</summary>
    private static readonly Dictionary<string, string[]> s_allowedReferences = new()
    {
        ["Npc.Contracts"] = [],
        ["Npc.Core"] = ["Npc.Contracts"],
        ["Npc.MasterData"] = ["Npc.Core"],
        ["Npc.Planning"] = ["Npc.Core", "Npc.MasterData"],
        ["Npc.Runtime"] = ["Npc.Contracts", "Npc.Core", "Npc.MasterData", "Npc.Planning"],
        ["Npc.Llm"] = ["Npc.Core", "Npc.MasterData"],
        ["Npc.Gateway"] = ["Npc.Contracts"],
        ["Npc.Sim"] = ["Npc.Contracts", "Npc.MasterData"],
        ["Npc.Host"] =
        [
            "Npc.Contracts", "Npc.Core", "Npc.MasterData", "Npc.Planning",
            "Npc.Runtime", "Npc.Llm", "Npc.Gateway", "Npc.Sim",
        ],
    };

    [Fact]
    public void Architecture_RuntimeDoesNotReferenceLlm()
    {
        string[] refs = ProjectReferencesOf("Npc.Runtime");

        Assert.DoesNotContain("Npc.Llm", refs);
    }

    [Fact]
    public void Architecture_DependencyGraphMatchesSpec()
    {
        foreach ((string project, string[] allowed) in s_allowedReferences)
        {
            string[] actual = ProjectReferencesOf(project);

            Assert.Equal(allowed.Order().ToArray(), actual.Order().ToArray());
        }
    }

    [Fact]
    public void Architecture_ContractsAndCoreHaveNoNuGetDependency()
    {
        foreach (string project in new[] { "Npc.Contracts", "Npc.Core" })
        {
            XDocument doc = XDocument.Load(CsprojPath(project));
            string[] packages = doc.Descendants("PackageReference")
                .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
                .ToArray();

            Assert.Empty(packages);
        }
    }

    private static string CsprojPath(string project) =>
        TestPaths.At("src", project, project + ".csproj");

    private static string[] ProjectReferencesOf(string project)
    {
        XDocument doc = XDocument.Load(CsprojPath(project));

        return doc.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(
                (e.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')))
            .ToArray();
    }
}
