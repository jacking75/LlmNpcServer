using System.Collections.Immutable;
using Npc.Core;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.Narrative;
using Npc.Studio;
using Npc.Studio.Services;

namespace Npc.Tests.Studio;

/// <summary>
/// 폼 편집 (T10~T13).
///
/// <b>여기서 지키는 불변식은 "무변경 저장은 바이트 동일" 하나다.</b> 폼이 값을 통째로
/// 다시 직렬화하면 서식·키 순서·주석 배열이 무너지고, 한 줄을 고쳤는데 3,000줄이 바뀐
/// diff 는 아무도 리뷰할 수 없다.
/// </summary>
public sealed class StudioFormTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "npc-studio-form-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>되돌리기 백업. 기본 위치(%LOCALAPPDATA%)에 시험 부스러기를 남기지 않는다.</summary>
    private readonly string _backups = Path.Combine(
        Path.GetTempPath(), "npc-studio-form-backup-" + Guid.NewGuid().ToString("N")[..8]);

    public StudioFormTests() => CopyDirectory(TestPaths.MasterData, _directory);

    /// <summary>
    /// 아무것도 안 바꾸고 저장하면 파일이 바이트 하나 변하지 않는다. 전 아키타입에서.
    /// </summary>
    [Fact]
    public void SaveArchetypeForm_WithoutChanges_IsByteIdentical()
    {
        StudioWorkspace workspace = CreateWorkspace();
        string path = Path.Combine(_directory, "archetypes.json");
        byte[] before = File.ReadAllBytes(path);

        foreach (StudioArchetype archetype in workspace.LoadCatalog().Archetypes)
        {
            StudioArchetypeForm form = workspace.LoadArchetypeForm(archetype.Id);
            StudioSaveResult result = workspace.SaveArchetypeForm(form);

            Assert.True(result.Saved, $"{archetype.Id}: {result.Message}");
            Assert.Equal(before, File.ReadAllBytes(path));
        }
    }

    /// <summary>
    /// 대장장이에게는 <c>duty_hours</c> 가 없다. 폼이 없는 속성을 넣을 수 있어야 한다.
    /// </summary>
    [Fact]
    public void SaveArchetypeForm_AddsDutyHoursWhenAbsent()
    {
        StudioWorkspace workspace = CreateWorkspace();
        StudioArchetypeForm form = workspace.LoadArchetypeForm("blacksmith");

        Assert.Empty(form.DutyHours);

        StudioSaveResult result = workspace.SaveArchetypeForm(
            form with { DutyHours = [TimeOfDay.Morning, TimeOfDay.Noon] });

        Assert.True(result.Saved, result.Message);

        StudioArchetypeForm after = workspace.LoadArchetypeForm("blacksmith");

        Assert.Equal<TimeOfDay>([TimeOfDay.Morning, TimeOfDay.Noon], after.DutyHours);

        // 지우면 속성 자체가 사라진다 — null 로 두는 것과 없는 것은 diff 에서 다르게 읽힌다.
        Assert.True(workspace.SaveArchetypeForm(after with { DutyHours = [] }).Saved);
        Assert.Empty(workspace.LoadArchetypeForm("blacksmith").DutyHours);
        Assert.DoesNotContain(
            "\"duty_hours\"", workspace.LoadArchetype("blacksmith").Json, StringComparison.Ordinal);
    }

    /// <summary>가중치 합이 1.0 이 아니면 저장을 거절한다 (V5). 원본은 그대로다.</summary>
    [Fact]
    public void SaveArchetypeForm_RejectsV5()
    {
        StudioWorkspace workspace = CreateWorkspace();
        string path = Path.Combine(_directory, "archetypes.json");
        byte[] before = File.ReadAllBytes(path);
        StudioArchetypeForm form = workspace.LoadArchetypeForm("blacksmith");

        StudioSaveResult result = workspace.SaveArchetypeForm(form with { PopulationWeight = 0.5 });

        Assert.False(result.Saved);
        Assert.NotEmpty(result.Issues);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>폼의 모든 필드에 ⓘ 설명이 있다 (T04 의 수치 목표를 이 표가 강제한다).</summary>
    [Fact]
    public void ArchetypeForm_EveryFieldHasFieldGuide()
    {
        foreach ((string property, string key) in StudioArchetypeForm.JsonKeys)
        {
            Assert.True(
                FieldGuide.Of("archetypes.json", "/" + key) is not null,
                $"폼 필드 {property} → /{key} 의 설명이 FieldGuide 에 없다.");
        }
    }

    /// <summary>하루 일과도 무변경 저장은 바이트 동일이다.</summary>
    [Fact]
    public void SaveFallbackForm_WithoutChanges_IsByteIdentical()
    {
        StudioWorkspace workspace = CreateWorkspace();
        string path = Path.Combine(_directory, "fallback_plans.json");
        byte[] before = File.ReadAllBytes(path);

        foreach (StudioArchetype archetype in workspace.LoadCatalog().Archetypes.Take(8))
        {
            StudioFallbackForm form = workspace.LoadFallbackForm(archetype.Id);
            StudioSaveResult result = workspace.SaveFallbackForm(form);

            Assert.True(result.Saved, $"{archetype.Id}: {result.Message}");
            Assert.Equal(before, File.ReadAllBytes(path));
        }
    }

    /// <summary>
    /// 검사기의 자체 시험 — 전제를 못 채우는 스텝을 심으면 미리보기가 그것을 잡는다.
    /// <b>대조군이 없으면 "아무것도 못 찾는 상태" 로도 통과한다.</b>
    /// </summary>
    [Fact]
    public void PreviewFallback_FlagsUnmetPrecondition()
    {
        StudioWorkspace workspace = CreateWorkspace();
        StudioFallbackForm form = workspace.LoadFallbackForm("blacksmith");

        StudioFallbackPreview clean = workspace.PreviewFallbackForm(form);

        Assert.All(clean.Trace, step => Assert.True(step.Ok, step.Code + " " + step.Reason));

        // 첫 스텝(일터로 이동)을 빼면 그 다음 "일하기" 가 AtWorkplace 를 못 채운다.
        // 컴파일러가 먼저 거절하면 Error 로, 컴파일은 됐는데 전제가 깨지면 Trace 로 나온다 —
        // 편집기는 둘 중 무엇이든 사람에게 말해 줘야 한다.
        StudioFallbackPreview broken = workspace.PreviewFallbackForm(
            form with { Steps = form.Steps.RemoveAt(0) });

        Assert.True(
            broken.Error.Length > 0 || broken.Trace.Any(step => !step.Ok),
            "전제가 깨진 초안인데 미리보기가 아무 말도 하지 않는다.");
    }

    /// <summary>그 직업이 못 하는 행동을 쓰면 저장이 거절된다 (V8).</summary>
    [Fact]
    public void SaveFallbackForm_RejectsDisallowedAction()
    {
        StudioWorkspace workspace = CreateWorkspace();
        string path = Path.Combine(_directory, "fallback_plans.json");
        byte[] before = File.ReadAllBytes(path);
        StudioFallbackForm form = workspace.LoadFallbackForm("blacksmith");

        StudioStepChoices choices = workspace.LoadStepChoices("blacksmith");

        Assert.DoesNotContain(choices.Actions, a => a.Id == "Attack");

        StudioSaveResult result = workspace.SaveFallbackForm(form with
        {
            Steps = form.Steps.SetItem(0, form.Steps[0] with
            {
                Action = "Attack",
                Args = [new StudioArg("target", "\"self\"")],
            }),
        });

        Assert.False(result.Saved);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    /// <summary>
    /// 마법사 종단 (T12). 양봉가를 만들고 검증이 초록인지 본다 —
    /// 직업만 만들고 하루 일과를 안 만들면 V7 로 기동이 막힌다.
    /// </summary>
    [Fact]
    public void CreateArchetype_Wizard_EndToEnd()
    {
        StudioWorkspace workspace = CreateWorkspace();
        double weight = 0.004;

        ImmutableArray<WeightChange> rebalance = workspace.WithData((data, _) =>
            WeightRebalancer.Propose(data, "beekeeper", weight, ArchetypeCard.PopulationBase, "farm")
                .First(p => p.IsValid).Changes);

        StudioSaveResult result = workspace.CreateArchetype(new StudioNewArchetype(
            "beekeeper",
            "마을의 양봉가. 벌통을 돌보고 꿀을 거둔다.",
            "shepherd",
            weight,
            "farm",
            rebalance,
            [("ko-KR", "양봉가"), ("en-US", "Beekeeper")]));

        Assert.True(result.Saved, result.Message);

        StudioCatalog catalog = workspace.LoadCatalog();

        Assert.Empty(catalog.Issues);
        Assert.Contains(catalog.Archetypes, a => a.Id == "beekeeper");
        Assert.Contains(
            "fb_beekeeper",
            File.ReadAllText(Path.Combine(_directory, "fallback_plans.json")),
            StringComparison.Ordinal);
        Assert.Contains(
            "양봉가",
            File.ReadAllText(Path.Combine(_directory, "localization", "ko-KR.json")),
            StringComparison.Ordinal);

        // 하루 예측이 돌아야 화면이 그린다 — 명단이 없으니 가상 개체로 떨어진다.
        StudioArchetypeView view = workspace.LoadArchetypeView("beekeeper");

        Assert.NotEmpty(view.Fallback);
        Assert.NotEmpty(view.ByZone);
        Assert.True(view.ByZone.All(z => z.Predicted));
    }

    /// <summary>
    /// 새 장소는 다음 code 를 받고, 저장 뒤 거리표가 낡는다 (T13).
    /// </summary>
    [Fact]
    public void AddPoi_AppendsWithNextCodeAndMarksDistancesStale()
    {
        StudioWorkspace workspace = CreateWorkspace();
        var places = new StudioPlaces(workspace);
        int expected = CodeAllocator.Next(_directory, "pois.json");
        StudioZone zone = places.Zones()[0];

        string id = places.SuggestId("apiary", zone.Id);

        StudioSaveResult result = places.AddPlace(new StudioNewPlace(
            id, zone.Id, "Workplace", "apiary", 10, 20, 12, "Morning", "Evening", [], []));

        Assert.True(result.Saved, result.Message);
        Assert.Contains("거리표", result.Message, StringComparison.Ordinal);

        // 로더로 읽지 않는다 — 거리표가 낡은 상태가 이 저장의 정상 결과다.
        string pois = File.ReadAllText(Path.Combine(_directory, "pois.json"));

        Assert.Contains($"\"{id}\"", pois, StringComparison.Ordinal);
        Assert.Contains($"\"code\": {expected}", pois, StringComparison.Ordinal);
        Assert.Contains("\"apiary\"", pois, StringComparison.Ordinal);

        // 장소를 더하면 거리표가 낡는다 — 파급 패널이 그것을 말해야 한다.
        Impact impact = ImpactAnalyzer.Of(_directory, ["pois.json"]);

        Assert.Contains(impact.Regenerate, r => r.Artifact == "poi_distances.bin");

        // 회귀 — 저장은 됐는데 그 다음 카탈로그 읽기가 터져 화면이 통째로 죽었다.
        // 다시 만들라고 말해 줄 화면까지 같이 죽으면 빠져나올 길이 없다.
        StudioCatalog after = workspace.LoadCatalog();

        Assert.True(after.IsBlocked, "거리표가 낡았으면 막힌 상태로 열려야 한다.");
        Assert.Contains("poi_distances.bin", after.Blocked, StringComparison.Ordinal);
        Assert.Contains("poi_distances.bin", after.StaleArtifacts);
    }

    /// <summary>JSON Pointer 의 배열 첨자를 id 로 바꿔 화면 링크를 만든다 (T15).</summary>
    [Fact]
    public void IssueLocator_ResolvesArrayIndexToId()
    {
        StudioWorkspace workspace = CreateWorkspace();
        var locator = new IssueLocator(workspace);

        StudioIssueView view = locator.View(new StudioIssue(
            "V5", "가중치 합이 1.0 이 아니다", "archetypes.json", "/archetypes/0/population_weight", string.Empty));

        Assert.Equal("/archetypes/blacksmith?tab=edit", view.Link);
        Assert.Equal("인구 비율의 합이 1.0 이 아니다", view.Title);
    }

    /// <summary>모르는 파일은 원문 화면으로 떨어진다 — 링크가 없는 오류를 만들지 않는다.</summary>
    [Fact]
    public void IssueLocator_FallsBackToFileView()
    {
        StudioWorkspace workspace = CreateWorkspace();
        var locator = new IssueLocator(workspace);

        StudioIssueView view = locator.View(new StudioIssue(
            "V1", "스키마가 틀렸다", "world_flags.json", "/flags/3", string.Empty));

        Assert.Equal("/files/world_flags.json", view.Link);
    }

    /// <summary>검증 코드 전수에 초보자 제목이 있다.</summary>
    [Fact]
    public void IssueGuide_CoversEveryFixHintCode()
    {
        foreach (string code in Npc.MasterData.Validation.FixHints.Codes)
        {
            Assert.NotEqual(code, IssueGuide.TitleOf(code));
        }
    }

    /// <summary>생성기는 저장소 안에서만, 원본 디렉터리에만 돈다 (T17).</summary>
    [Fact]
    public void GeneratorRunner_RefusesOutsideRepo()
    {
        var runner = new GeneratorRunner(new StudioOptions(_directory, "127.0.0.1", 25_056, ReadOnly: false));

        Assert.False(runner.Available);
        Assert.False(runner.CanRunFor(_directory));
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
