using Npc.MasterData.Authoring;
using Npc.Studio;
using Npc.Studio.Services;

namespace Npc.Tests.Studio;

/// <summary>NPC Studio가 원본을 보호하면서 코어 편집·검증 경로를 쓰는지 확인한다.</summary>
public sealed class StudioWorkspaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "npc-studio-" + Guid.NewGuid().ToString("N")[..8]);

    public StudioWorkspaceTests() => CopyDirectory(TestPaths.MasterData, _directory);

    [Fact]
    public void LoadCatalog_ReturnsArchetypesAndInstances()
    {
        StudioWorkspace workspace = CreateWorkspace();

        StudioCatalog catalog = workspace.LoadCatalog();

        Assert.Equal(TestPaths.ArchetypeCount, catalog.Archetypes.Length);
        Assert.Equal(5_000, catalog.InstanceCount);
        Assert.Empty(catalog.Issues);
    }

    [Fact]
    public void SaveArchetype_RejectsInvalidCandidateWithoutChangingFile()
    {
        StudioWorkspace workspace = CreateWorkspace();
        string path = Path.Combine(_directory, "archetypes.json");
        string before = File.ReadAllText(path);
        StudioArchetypeDocument document = workspace.LoadArchetype("blacksmith");
        string invalid = JsonSurgeon.SetTopLevel(document.Json, "population_weight", "2");

        StudioSaveResult result = workspace.SaveArchetype("blacksmith", invalid);

        Assert.False(result.Saved);
        Assert.NotEmpty(result.Issues);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void SaveArchetype_WritesValidDescriptionOnly()
    {
        StudioWorkspace workspace = CreateWorkspace();
        StudioArchetypeDocument document = workspace.LoadArchetype("blacksmith");
        string edited = JsonSurgeon.SetTopLevel(
            document.Json,
            "desc",
            "\"테스트용 설명. 검증을 통과한 경우에만 저장된다.\"");

        StudioSaveResult result = workspace.SaveArchetype("blacksmith", edited);

        Assert.True(result.Saved, result.Message);
        Assert.Contains("테스트용 설명", workspace.LoadArchetype("blacksmith").Json, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateArchetype_AddsFallbackAndKeepsValidationGreen()
    {
        StudioWorkspace workspace = CreateWorkspace();

        StudioSaveResult result = workspace.CreateArchetype("test_smith", "blacksmith", 0.001);
        StudioCatalog catalog = workspace.LoadCatalog();

        Assert.True(result.Saved, result.Message);
        Assert.Contains(catalog.Archetypes, a => a.Id == "test_smith");
        Assert.Contains("fb_test_smith", File.ReadAllText(Path.Combine(_directory, "fallback_plans.json")), StringComparison.Ordinal);
        Assert.Empty(catalog.Issues);
    }

    [Fact]
    public void ReadOnlyMode_RejectsWrites()
    {
        var workspace = new StudioWorkspace(new StudioOptions(_directory, "127.0.0.1", 5090, ReadOnly: true));
        StudioArchetypeDocument document = workspace.LoadArchetype("blacksmith");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => workspace.SaveArchetype("blacksmith", document.Json));

        Assert.Contains("읽기 전용", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private StudioWorkspace CreateWorkspace() =>
        new(new StudioOptions(_directory, "127.0.0.1", 5090, ReadOnly: false));

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
