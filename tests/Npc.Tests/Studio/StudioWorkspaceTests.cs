using System.Collections.Immutable;
using Npc.MasterData.Authoring;
using Npc.Narrative;
using Npc.Studio;
using Npc.Studio.Services;

namespace Npc.Tests.Studio;

/// <summary>NPC Studio가 원본을 보호하면서 코어 편집·검증 경로를 쓰는지 확인한다.</summary>
public sealed class StudioWorkspaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "npc-studio-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>되돌리기 백업. 기본 위치(%LOCALAPPDATA%)에 시험 부스러기를 남기지 않는다.</summary>
    private readonly string _backups = Path.Combine(
        Path.GetTempPath(), "npc-studio-backup-" + Guid.NewGuid().ToString("N")[..8]);

    public StudioWorkspaceTests() => CopyDirectory(TestPaths.MasterData, _directory);

    [Fact]
    public void LoadCatalog_ReturnsArchetypesAndInstances()
    {
        StudioWorkspace workspace = CreateWorkspace();

        StudioCatalog catalog = workspace.LoadCatalog();

        Assert.True(catalog.Archetypes.Length > 0);
        Assert.True(catalog.InstanceCount > 0);
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
        int catalogCount = workspace.LoadCatalog().InstanceCount;
        StudioNpcSummary overridden = Assert.Single(directory, npc => npc.HasOverride);

        Assert.Equal(catalogCount, directory.Length);
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

            // H07 — 적용에 성공하면 <b>원본으로 돌아간다</b>. 연습장에 남아 있으면 파급 패널이
            // "연습장에서는 다시 만들 수 없다" 를 방금 적용한 사람에게 보여 준다.
            Assert.Equal(string.Empty, workspace.SandboxName);
            Assert.Equal(_directory, workspace.CurrentDirectory);

            // 연습장 폴더는 남는다 — 적용했다고 실험까지 지우지 않는다.
            Assert.True(Directory.Exists(sandbox));
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

    /// <summary>
    /// H04 — 같은 이름의 연습장을 덮어쓰지 않는다.
    ///
    /// <b>회귀: 기본 이름이 <c>MMdd</c> 라 같은 날 두 번 누르면 아침 작업이 확인 없이 사라졌다.</b>
    /// </summary>
    [Fact]
    public void OpenSandbox_RefusesToOverwriteExisting()
    {
        StudioWorkspace workspace = CreateWorkspace();
        string sandbox = workspace.OpenSandbox("h04");

        try
        {
            StudioArchetypeDocument document = workspace.LoadArchetype("blacksmith");

            Assert.True(workspace.SaveArchetype(
                "blacksmith",
                JsonSurgeon.SetTopLevel(document.Json, "desc", "\"연습장에서 바꾼 설명이다.\"")).Saved);

            workspace.CloseSandbox();

            SandboxExistsException exists = Assert.Throws<SandboxExistsException>(() => workspace.OpenSandbox("h04"));

            Assert.Equal("h04", exists.Name);
            Assert.Contains("archetypes.json", exists.ChangedFiles);

            // 이어서 열면 그 변경이 그대로 있다.
            _ = workspace.ResumeSandbox("h04");

            Assert.Contains(
                "연습장에서 바꾼 설명이다.",
                workspace.LoadArchetype("blacksmith").Json,
                StringComparison.Ordinal);

            Assert.Contains(workspace.Sandboxes(), item => item.Name == "h04" && item.ChangedFiles == 1);
        }
        finally
        {
            workspace.CloseSandbox();

            string folder = Path.GetDirectoryName(sandbox)!;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// H15 — 연습장에서 만든 표시 이름이 원본으로 간다.
    ///
    /// <b>회귀: 비교 대상이 편집 파일 12개뿐이라 <c>localization/</c> 이 빠졌다.</b>
    /// 마법사로 만든 직업의 한국어 이름이 원본에 안 갔고 오류도 없었다.
    /// </summary>
    [Fact]
    public void ApplyToOrigin_CarriesLocalizationFiles()
    {
        StudioWorkspace workspace = CreateWorkspace();
        string sandbox = workspace.OpenSandbox("h15");

        try
        {
            double weight = 0.004;

            ImmutableArray<WeightChange> rebalance = workspace.WithData((data, _) =>
                WeightRebalancer.Propose(data, "beekeeper", weight, ArchetypeCard.PopulationBase, "farm")
                    .First(p => p.IsValid).Changes);

            Assert.True(workspace.CreateArchetype(new StudioNewArchetype(
                "beekeeper", "마을의 양봉가.", "shepherd", weight, "farm", rebalance,
                [("ko-KR", "양봉가"), ("en-US", "Beekeeper")])).Saved);

            Assert.Contains("localization/ko-KR.json", workspace.SandboxChanges());

            StudioSaveResult applied = workspace.ApplyToOrigin();

            Assert.True(applied.Saved, applied.Message);
            Assert.Contains("localization/ko-KR.json", applied.Files);
            Assert.Contains(
                "양봉가",
                File.ReadAllText(Path.Combine(_directory, "localization", "ko-KR.json")),
                StringComparison.Ordinal);
        }
        finally
        {
            workspace.CloseSandbox();

            string folder = Path.GetDirectoryName(sandbox)!;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// H15 — 표시 이름이 빠지면 화면의 검증에 잡힌다.
    ///
    /// <b>검증기는 V0~V13 만 돈다.</b> V14 는 기동 경고라 그 밖에 있고, 그래서 화면이
    /// "위반이 없다" 라고 적는데 정작 이름이 빠져 있었다.
    /// </summary>
    [Fact]
    public void Validate_ReportsMissingDisplayName()
    {
        StudioWorkspace workspace = CreateWorkspace();

        Assert.Empty(workspace.Validate());

        string path = Path.Combine(_directory, "localization", "ko-KR.json");

        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace("\"npc.blacksmith\"", "\"npc.blacksmith_removed\"", StringComparison.Ordinal));

        ImmutableArray<StudioIssue> issues = workspace.Validate();

        Assert.Contains(issues, i => i.Code == "V14" && i.Path == "npc.blacksmith");
    }

    /// <summary>
    /// H12 — 번호 재배치는 검증도 로더도 못 잡는다. 저장이 거절한다.
    ///
    /// <b>대조군: 두 직업의 <c>code</c> 를 맞바꾼다.</b> 둘 다 유일하므로 V1 은 통과하고,
    /// 로더도 읽는다 — 그런데 프리베이크 파일 이름과 명단의 직업이 통째로 어긋난다.
    /// </summary>
    [Fact]
    public void ValidateAndWrite_RejectsCodeReassignment()
    {
        StudioWorkspace workspace = CreateWorkspace();
        string path = Path.Combine(_directory, "archetypes.json");
        string source = File.ReadAllText(path);
        byte[] before = File.ReadAllBytes(path);

        int a = workspace.LoadCatalog().Archetypes[0].Code;
        int b = workspace.LoadCatalog().Archetypes[1].Code;
        string first = workspace.LoadCatalog().Archetypes[0].Id;
        string second = workspace.LoadCatalog().Archetypes[1].Id;

        string swapped = JsonSurgeon.SetInArrayItem(source, "archetypes", "id", first, "code", b.ToString());
        swapped = JsonSurgeon.SetInArrayItem(swapped, "archetypes", "id", second, "code", a.ToString());

        StudioSaveResult result = workspace.SaveFile("archetypes.json", swapped);

        Assert.False(result.Saved);
        Assert.Contains(result.Issues, i => i.Code == "PIN");
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>대조군 — 맨 뒤에 더하는 번호는 통과한다. 막는 것은 재배치뿐이다.</summary>
    [Fact]
    public void ValidateAndWrite_AllowsAppendedCode()
    {
        StudioWorkspace workspace = CreateWorkspace();
        var places = new StudioPlaces(workspace);
        StudioZone zone = places.Zones()[0];

        StudioSaveResult result = places.AddPlace(new StudioNewPlace(
            places.SuggestId("apiary", zone.Id), zone.Id, "Workplace", "apiary",
            10, 20, 12, "Morning", "Evening", [], []));

        Assert.True(result.Saved, result.Message);
    }

    /// <summary>
    /// H13 — 읽은 뒤 디스크가 바뀌면 저장을 거절한다.
    ///
    /// <b>VS Code 흉내다.</b> 예전에는 마지막 저장이 조용히 이겨서, 밖에서 고친 것이
    /// 아무 말 없이 사라졌다.
    /// </summary>
    [Fact]
    public void Save_RejectsWhenFileChangedSinceLoad()
    {
        StudioWorkspace workspace = CreateWorkspace();
        StudioArchetypeForm form = workspace.LoadArchetypeForm("blacksmith");
        string path = Path.Combine(_directory, "archetypes.json");

        // 밖에서 다른 직업의 설명을 고친다.
        File.WriteAllText(
            path,
            JsonSurgeon.SetInArrayItem(
                File.ReadAllText(path), "archetypes", "id", "farmer", "desc", "\"밖에서 고친 설명이다.\""));

        byte[] outside = File.ReadAllBytes(path);

        StudioSaveResult result = workspace.SaveArchetypeForm(form with { Desc = "Studio 에서 고친 설명이다." });

        Assert.False(result.Saved);
        Assert.Contains(result.Issues, i => i.Code == "STALE");

        // 밖의 편집이 그대로 남는다 — 되돌려 쓰지 않는다.
        Assert.Equal(outside, File.ReadAllBytes(path));

        // 다시 읽으면 저장된다.
        Assert.True(workspace.SaveArchetypeForm(
            workspace.LoadArchetypeForm("blacksmith") with { Desc = "Studio 에서 고친 설명이다." }).Saved);
    }

    /// <summary>
    /// H14 — 트랜잭션 하나를 통째로 되돌린다.
    ///
    /// <b>마법사는 6개 파일을 같이 쓴다.</b> <c>archetypes.json</c> 만 되돌리면 V7 이고,
    /// <c>fallback_plans.json</c> 만 먼저 되돌려도 V7 이다 — 단일 파일 복원은 어느 순서로도 통과하지 못한다.
    /// </summary>
    [Fact]
    public void Restore_RevertsWholeTransaction()
    {
        StudioWorkspace workspace = CreateWorkspace();
        string[] files = ["archetypes.json", "fallback_plans.json", "context_buckets.json", "pois.json"];
        Dictionary<string, byte[]> before = files.ToDictionary(
            f => f, f => File.ReadAllBytes(Path.Combine(_directory, f)), StringComparer.Ordinal);

        double weight = 0.004;

        ImmutableArray<WeightChange> rebalance = workspace.WithData((data, _) =>
            WeightRebalancer.Propose(data, "beekeeper", weight, ArchetypeCard.PopulationBase, "farm")
                .First(p => p.IsValid).Changes);

        Assert.True(workspace.CreateArchetype(new StudioNewArchetype(
            "beekeeper", "마을의 양봉가.", "shepherd", weight, "farm", rebalance,
            [("ko-KR", "양봉가"), ("en-US", "Beekeeper")])).Saved);

        StudioBackup backup = workspace.Backups().First();

        Assert.Contains("archetypes.json", backup.Files);
        Assert.Contains("fallback_plans.json", backup.Files);
        Assert.Contains("localization/ko-KR.json", backup.Files);
        Assert.Contains("beekeeper", backup.Label, StringComparison.Ordinal);

        StudioSaveResult restored = workspace.Restore(backup);

        Assert.True(restored.Saved, restored.Message);

        foreach ((string file, byte[] bytes) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_directory, file)));
        }

        Assert.Empty(workspace.Validate());
        Assert.DoesNotContain(workspace.LoadCatalog().Archetypes, a => a.Id == "beekeeper");
    }

    /// <summary>
    /// H14 — 백업이 하위 폴더를 보존한다.
    ///
    /// <b>회귀: <c>localization/ko-KR.json</c> 을 <c>ko-KR.json</c> 으로 눕혀서,</b>
    /// 목록엔 뜨는데 복원하면 "편집할 수 없는 파일" 로 거절됐다.
    /// </summary>
    [Fact]
    public void Backup_PreservesSubdirectory()
    {
        StudioWorkspace workspace = CreateWorkspace();
        double weight = 0.004;

        ImmutableArray<WeightChange> rebalance = workspace.WithData((data, _) =>
            WeightRebalancer.Propose(data, "beekeeper", weight, ArchetypeCard.PopulationBase, "farm")
                .First(p => p.IsValid).Changes);

        Assert.True(workspace.CreateArchetype(new StudioNewArchetype(
            "beekeeper", "마을의 양봉가.", "shepherd", weight, "farm", rebalance,
            [("ko-KR", "양봉가"), ("en-US", "Beekeeper")])).Saved);

        StudioBackup backup = workspace.Backups().First();

        Assert.True(File.Exists(Path.Combine(backup.Path, "localization", "ko-KR.json")));
    }

    /// <summary>
    /// H03 — 인구 재배분이 <b>같은 저장</b>에 들어간다.
    ///
    /// <b>예전에는 "이 안으로" 버튼이 곧바로 파일을 쓰고 폼을 다시 읽었다</b> —
    /// 저장하지 않은 다른 편집이 통째로 사라졌고, 저장 요약 모달도 건너뛰었다.
    /// </summary>
    [Fact]
    public void SaveArchetypeForm_WithRebalance_WritesAllRowsInOneTransaction()
    {
        StudioWorkspace workspace = CreateWorkspace();

        // 일터가 없는 직업을 고른다 — 인구를 늘려도 V10(정원)에 걸리지 않는다.
        string id = workspace.LoadCatalog().Archetypes.First(a => a.Workplace == "—").Id;
        StudioArchetypeForm form = workspace.LoadArchetypeForm(id);
        double target = form.PopulationWeight + 0.004;

        ImmutableArray<WeightChange> rebalance = workspace.WithData((data, _) =>
            WeightRebalancer.Propose(data, id, target, ArchetypeCard.PopulationBase, form.WorkplacePoiType)
                .First(p => p.IsValid).Changes);

        int backupsBefore = workspace.Backups().Length;

        StudioSaveResult result = workspace.SaveArchetypeForm(form with
        {
            PopulationWeight = target,
            Desc = "재배분과 같이 저장한 설명이다.",
            Rebalance = rebalance,
        });

        Assert.True(result.Saved, result.Message + " " + string.Join(" | ", result.Issues.Select(i => i.Code + " " + i.Detail)));

        // 백업 묶음이 하나다 — 저장도 한 번이었다는 뜻이다.
        Assert.Equal(backupsBefore + 1, workspace.Backups().Length);

        StudioCatalog after = workspace.LoadCatalog();

        Assert.Empty(after.Issues);
        Assert.Equal(1.0, after.Archetypes.Sum(a => a.Weight), 3);
        Assert.Contains("재배분과 같이 저장한 설명이다.", workspace.LoadArchetype(id).Json, StringComparison.Ordinal);
    }

    /// <summary>H17 — 개별 설정도 무변경 저장은 바이트 동일이다.</summary>
    [Fact]
    public void SaveNpcOverride_WithoutChanges_IsByteIdentical()
    {
        StudioWorkspace workspace = CreateWorkspace();
        string path = Path.Combine(_directory, "npc_overrides.json");
        byte[] before = File.ReadAllBytes(path);
        StudioNpcOverrideEditor editor = workspace.LoadNpcOverride(2326);

        Assert.True(editor.Exists);

        StudioSaveResult result = workspace.SaveNpcOverride(new StudioNpcOverrideDraft(
            2326,
            editor.PatrolRoute,
            editor.AggroRadiusM,
            editor.Faction,
            editor.DialogueProfile,
            editor.ScheduleOffsetMinutes));

        Assert.True(result.Saved, result.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>
    /// 회귀 — 순찰로가 없는 NPC 에서 개요가 터졌다 (<c>1fd9812</c>).
    /// <c>ImmutableArray</c> 기본값을 그대로 <c>Select</c> 했다.
    /// </summary>
    [Fact]
    public void LoadNpcOverview_WorksWithoutPatrolRoute()
    {
        StudioWorkspace workspace = CreateWorkspace();

        StudioNpcOverview overview = workspace.LoadNpcOverview(1);

        Assert.Empty(overview.PatrolRoute);
        Assert.NotEmpty(overview.Sentence);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        if (Directory.Exists(_backups)) Directory.Delete(_backups, recursive: true);
        GC.SuppressFinalize(this);
    }

    private StudioWorkspace CreateWorkspace() =>
        new(new StudioOptions(_directory, "127.0.0.1", 25_056, ReadOnly: false) { BackupRoot = _backups });

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
