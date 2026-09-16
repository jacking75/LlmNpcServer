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
        Assert.True(catalog.PoiCount > 0);
        Assert.True(catalog.ZoneCount > 0);
        Assert.True(catalog.InterruptCount > 0);
        Assert.False(catalog.IsSandbox);
    }

    [Fact]
    public void LoadNpcDirectory_ReturnsGeneratedFieldsAndOverrideMarkers()
    {
        StudioWorkspace workspace = CreateWorkspace();

        var directory = workspace.LoadNpcDirectory();
        StudioNpcSummary overridden = Assert.Single(directory, npc => npc.HasOverride);

        Assert.Equal(5_000, directory.Length);
        Assert.Equal(2326, overridden.Id);
        Assert.NotEmpty(overridden.Archetype);
        Assert.NotEmpty(overridden.Zone);
        Assert.NotEmpty(overridden.Home);
        Assert.Equal("town_watch", overridden.Faction);
    }

    [Fact]
    public void PreviewArchetype_ExplainsUnsavedJsonValues()
    {
        StudioWorkspace workspace = CreateWorkspace();
        StudioArchetypeDocument document = workspace.LoadArchetype("blacksmith");
        string draft = JsonSurgeon.SetTopLevel(document.Json, "desc", "\"저장하지 않은 실시간 설명이다.\"");
        draft = JsonSurgeon.SetTopLevel(draft, "combat_capable", "false");

        string preview = workspace.PreviewArchetype("blacksmith", draft);

        Assert.Contains("저장하지 않은 실시간 설명이다.", preview, StringComparison.Ordinal);
        Assert.Contains("전투 가능 | 아니오", preview, StringComparison.Ordinal);
        Assert.DoesNotContain("저장하지 않은 실시간 설명이다.", workspace.LoadArchetype("blacksmith").Json, StringComparison.Ordinal);
    }

    [Fact]
    public void StudioMarkdown_RendersTablesAndEscapesHtml()
    {
        const string Markdown = "# 제목\n\n| 항목 | 값 |\n|---|---|\n| 코드 | `A\\|B` |\n\n<script>alert('x')</script>";

        string html = StudioMarkdown.ToHtml(Markdown);

        Assert.Contains("<h1>제목</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("<code>A|B</code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveNpcOverride_WritesAndDeletesValidatedOverride()
    {
        StudioWorkspace workspace = CreateWorkspace();
        StudioNpcOverrideEditor before = workspace.LoadNpcOverride(1);
        StudioPoiChoice poiChoice = Assert.Single(before.ZonePois.Take(1));
        string poi = poiChoice.Id;
        string faction = Assert.Single(before.Factions.Take(1));

        Assert.NotEmpty(poiChoice.Type);
        Assert.NotEmpty(poiChoice.Subtype);
        Assert.True(before.ZonePois.Any(choice => choice.IsHome));
        Assert.True(before.ZonePois.Any(choice => choice.IsWorkplace));

        StudioSaveResult saved = workspace.SaveNpcOverride(new StudioNpcOverrideDraft(
            1,
            [poi],
            12,
            faction,
            "test_profile",
            10));

        Assert.True(saved.Saved, saved.Message);
        StudioNpcOverrideEditor after = workspace.LoadNpcOverride(1);
        Assert.True(after.Exists);
        Assert.Equal<string>([poi], after.PatrolRoute);
        Assert.Equal(12, after.AggroRadiusM);
        Assert.Equal(faction, after.Faction);
        Assert.Equal("test_profile", after.DialogueProfile);
        Assert.Equal(10, after.ScheduleOffsetMinutes);
        Assert.Contains(workspace.LoadNpcDirectory(), npc => npc.Id == 1 && npc.HasOverride);

        StudioSaveResult deleted = workspace.SaveNpcOverride(new StudioNpcOverrideDraft(
            1,
            [],
            null,
            string.Empty,
            string.Empty,
            null));

        Assert.True(deleted.Saved, deleted.Message);
        Assert.False(workspace.LoadNpcOverride(1).Exists);
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

    /// <summary>
    /// T30 — 연습장에서 저장해도 원본은 바이트 하나 변하지 않는다.
    /// 이것이 성립하지 않으면 "망쳐도 된다" 가 거짓말이 되고, 초보자는 다시 손대지 못한다.
    /// </summary>
    [Fact]
    public void Sandbox_CopiesAndIsolatesWrites()
    {
        StudioWorkspace workspace = CreateWorkspace();
        string originPath = Path.Combine(_directory, "archetypes.json");
        byte[] before = File.ReadAllBytes(originPath);

        string sandbox = workspace.OpenSandbox("t30");

        try
        {
            Assert.True(Directory.Exists(sandbox));
            Assert.Equal("t30", workspace.SandboxName);
            Assert.Equal(sandbox, workspace.CurrentDirectory);
            Assert.True(File.Exists(Path.Combine(sandbox, "localization", "ko-KR.json")));
            Assert.Empty(workspace.SandboxChanges());

            StudioArchetypeDocument document = workspace.LoadArchetype("blacksmith");
            string edited = JsonSurgeon.SetTopLevel(document.Json, "desc", "\"연습장에서만 바꾼 설명이다.\"");

            Assert.True(workspace.SaveArchetype("blacksmith", edited).Saved);
            Assert.Equal<string>(["archetypes.json"], workspace.SandboxChanges());

            // 원본은 그대로다.
            Assert.Equal(before, File.ReadAllBytes(originPath));

            StudioSaveResult applied = workspace.ApplyToOrigin();

            Assert.True(applied.Saved, applied.Message);
            Assert.Contains("연습장에서만 바꾼 설명이다.", File.ReadAllText(originPath), StringComparison.Ordinal);

            workspace.DiscardSandbox();

            Assert.Equal(string.Empty, workspace.SandboxName);
            Assert.Equal(_directory, workspace.CurrentDirectory);
            Assert.False(Directory.Exists(sandbox));
        }
        finally
        {
            workspace.CloseSandbox();

            string folder = Path.GetDirectoryName(sandbox)!;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ReadOnlyMode_RejectsWrites()
    {
        var workspace = new StudioWorkspace(new StudioOptions(_directory, "127.0.0.1", 25_056, ReadOnly: true));
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
        new(new StudioOptions(_directory, "127.0.0.1", 25_056, ReadOnly: false));

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
