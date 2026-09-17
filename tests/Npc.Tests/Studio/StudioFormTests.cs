using System.Collections.Immutable;
using Npc.Core;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.Narrative;
using Npc.Studio;
using Npc.Studio.Components.Shared;
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

        // H07 — 거리표가 낡았다고 <b>화면을 막지 않는다</b>. 장소를 더한 사람은 바로 다음에
        // "다시 만들기" 를 눌러야 하는데, 그 버튼이 있는 화면까지 같이 죽으면 빠져나올 길이 없다.
        // 대신 거리 없이 읽고 배너로 알린다.
        StudioCatalog after = workspace.LoadCatalog();

        Assert.False(after.IsBlocked, "거리표가 낡아도 읽고 고칠 수는 있어야 한다.");
        Assert.False(after.DistancesAvailable, "거리표를 건너뛰고 읽었다는 사실이 화면에 가야 한다.");
        Assert.Contains("poi_distances.bin", after.StaleArtifacts);
        Assert.Contains(after.Archetypes, a => a.Id == "blacksmith");
    }

    /// <summary>
    /// 거리표가 <b>아예 없어도</b> 화면이 뜬다 (H07).
    /// 갓 클론한 저장소가 이 경우다 — 예전에는 DI 가 실패해 빈 화면이 떴다.
    /// </summary>
    [Fact]
    public void LoadCatalog_ReadsWithoutDistanceMatrix()
    {
        StudioWorkspace workspace = CreateWorkspace();

        File.Delete(Path.Combine(_directory, "poi_distances.bin"));

        StudioCatalog catalog = workspace.LoadCatalog();

        Assert.False(catalog.IsBlocked, catalog.Blocked);
        Assert.False(catalog.DistancesAvailable);
        Assert.True(catalog.Archetypes.Length > 0);
    }

    /// <summary>대조군 — JSON 문법이 깨지면 <b>막는다</b>. 그것은 다시 만들기로 풀리지 않는다.</summary>
    [Fact]
    public void LoadCatalog_ClassifiesJsonSyntaxError()
    {
        StudioWorkspace workspace = CreateWorkspace();
        string path = Path.Combine(_directory, "zones.json");

        File.WriteAllText(path, File.ReadAllText(path).Replace("{", "{{", StringComparison.Ordinal));

        StudioCatalog catalog = workspace.LoadCatalog();

        Assert.True(catalog.IsBlocked);
        Assert.Equal(StudioBlockKind.JsonSyntax, catalog.Kind);
    }

    /// <summary>
    /// 검사기의 자체 시험 — 같은 id 의 장소를 거절한다 (H11).
    ///
    /// <b>대조군이 없으면 "아무것도 못 찾는 상태" 로도 통과한다.</b> 예전에는 검증 V1 이
    /// code 중복만 봐서 같은 id 의 장소 둘이 조용히 생겼고, 로더는 마지막 것만 남겼다.
    /// </summary>
    [Fact]
    public void AddPoi_RejectsDuplicateId()
    {
        StudioWorkspace workspace = CreateWorkspace();
        var places = new StudioPlaces(workspace);
        StudioZone zone = places.Zones()[0];
        string existing = places.Places(zone.Id)[0].Poi.Id;
        string path = Path.Combine(_directory, "pois.json");
        byte[] before = File.ReadAllBytes(path);

        StudioSaveResult result = places.AddPlace(new StudioNewPlace(
            existing, zone.Id, "Workplace", "apiary", 10, 20, 12, "Morning", "Evening", [], []));

        Assert.False(result.Saved);
        Assert.Contains("이미 있다", result.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(path));

        // 대조군 — 쓰이지 않은 id 는 통과한다.
        Assert.True(places.AddPlace(new StudioNewPlace(
            places.SuggestId("apiary", zone.Id), zone.Id, "Workplace", "apiary",
            10, 20, 12, "Morning", "Evening", [], [])).Saved);
    }

    /// <summary>정원 0 · 형식 오류 · 없는 지역도 저장 전에 거절한다 (H11).</summary>
    [Fact]
    public void CheckNewPlace_RejectsBadDraft()
    {
        StudioWorkspace workspace = CreateWorkspace();
        var places = new StudioPlaces(workspace);
        StudioZone zone = places.Zones()[0];

        ImmutableArray<StudioIssue> issues = workspace.CheckNewPlace(new StudioNewPlace(
            "1Bad Id", "no_such_zone", "Workplace", string.Empty, 0, 0, 0, "Morning", "Evening", [], []));

        Assert.Contains(issues, i => i.Path == "/id");
        Assert.Contains(issues, i => i.Path == "/zone");
        Assert.Contains(issues, i => i.Path == "/capacity");
        Assert.Contains(issues, i => i.Path == "/subtype");

        // 대조군 — 멀쩡한 초안은 아무 문제도 내지 않는다.
        Assert.Empty(workspace.CheckNewPlace(new StudioNewPlace(
            places.SuggestId("apiary", zone.Id), zone.Id, "Workplace", "apiary",
            10, 20, 12, "Morning", "Evening", [], [])));
    }

    /// <summary>
    /// 마법사가 만질 파일 목록은 <b>실제로 쓰는 파일과 같다</b> (H05 드리프트).
    /// 매뉴얼이 "네 파일" 이라 적고 코드는 여섯 개를 쓰고 있었다.
    /// </summary>
    [Fact]
    public void CreateArchetype_PreviewListsEveryFileItWillTouch()
    {
        StudioWorkspace workspace = CreateWorkspace();
        double weight = 0.004;

        ImmutableArray<WeightChange> rebalance = workspace.WithData((data, _) =>
            WeightRebalancer.Propose(data, "beekeeper", weight, ArchetypeCard.PopulationBase, "farm")
                .First(p => p.IsValid).Changes);

        var draft = new StudioNewArchetype(
            "beekeeper",
            "마을의 양봉가. 벌통을 돌보고 꿀을 거둔다.",
            "shepherd",
            weight,
            "farm",
            rebalance,
            [("ko-KR", "양봉가"), ("en-US", "Beekeeper")]);

        ImmutableArray<string> preview = workspace.PreviewCreateArchetype(draft);
        StudioSaveResult result = workspace.CreateArchetype(draft);

        Assert.True(result.Saved, result.Message);
        Assert.Equal(string.Join(" ", preview), string.Join(" ", result.Files));
        Assert.Contains("localization/ko-KR.json", result.Files);
    }

    /// <summary>
    /// H06 — 마법사 4단계에서 손본 스텝이 그대로 들어간다.
    /// 복제만 하면 "하루 일과를 짠다" 는 체크리스트가 거짓말이 된다.
    /// </summary>
    [Fact]
    public void CreateArchetype_UsesEditedSteps()
    {
        StudioWorkspace workspace = CreateWorkspace();
        double weight = 0.004;

        ImmutableArray<WeightChange> rebalance = workspace.WithData((data, _) =>
            WeightRebalancer.Propose(data, "beekeeper", weight, ArchetypeCard.PopulationBase, "farm")
                .First(p => p.IsValid).Changes);

        StudioFallbackForm source = workspace.LoadFallbackForm("shepherd");

        StudioSaveResult result = workspace.CreateArchetype(new StudioNewArchetype(
            "beekeeper", "마을의 양봉가.", "shepherd", weight, "farm", rebalance,
            [("ko-KR", "양봉가"), ("en-US", "Beekeeper")])
        {
            Steps = source.Steps.RemoveAt(source.Steps.Length - 1).Add(source.Steps[^1]),
            Goal = "honey_day",
        });

        Assert.True(result.Saved, result.Message);
        Assert.Equal("honey_day", workspace.LoadFallbackForm("beekeeper").Goal);
    }

    /// <summary>초안 직업으로도 하루 일과를 판정한다 (H06) — 파일에 항목이 아직 없다.</summary>
    [Fact]
    public void PreviewDraftFallback_WorksWithoutFile()
    {
        StudioWorkspace workspace = CreateWorkspace();
        StudioFallbackForm source = workspace.LoadFallbackForm("shepherd");

        StudioFallbackPreview preview = workspace.PreviewDraftFallback(
            source with { Id = "fb_beekeeper", Archetype = "beekeeper" }, "shepherd");

        Assert.Equal(string.Empty, preview.Error);
        Assert.NotEmpty(preview.Trace);
    }

    /// <summary>JSON Pointer 의 배열 첨자를 id 로 바꿔 화면 링크를 만든다 (T15).</summary>
    [Fact]
    public void IssueLocator_ResolvesArrayIndexToId()
    {
        StudioWorkspace workspace = CreateWorkspace();
        var locator = new IssueLocator(workspace);

        StudioIssueView view = locator.View(new StudioIssue(
            "V5", "가중치 합이 1.0 이 아니다", "archetypes.json", "/archetypes/0/population_weight", string.Empty));

        // H10 — 링크는 <b>편집 탭의 그 칸</b>까지 간다. 직업 화면까지만 보내면 사람이 다시 찾아야 한다.
        Assert.Equal("/archetypes/blacksmith?tab=edit&field=population_weight", view.Link);
        Assert.Equal("인구 비율의 합이 1.0 이 아니다", view.Title);
    }

    /// <summary>
    /// 회귀 — "고치러 가기" 가 <b>고급 JSON 탭</b>으로 떨어졌다.
    ///
    /// <c>?tab=edit</c> 뒤에 <c>?field=</c> 를 또 붙여 탭 값이 <c>"edit?field=…"</c> 가 됐고,
    /// 탭 switch 의 <c>default</c> 가 원문 편집기였다 — 초보자를 가장 어려운 화면에 버린 것이다.
    /// </summary>
    [Fact]
    public void WithQuery_AppendsFieldWithAmpersand()
    {
        Assert.Equal(
            "/archetypes/blacksmith?tab=edit&field=traits%2Fcourage",
            StudioView.WithQuery("/archetypes/blacksmith?tab=edit", "field", "traits/courage"));

        Assert.Equal(
            "/places/market_east?poi=stall_001_04",
            StudioView.WithQuery("/places/market_east", "poi", "stall_001_04"));

        // 값이 비면 붙이지 않는다 — `?field=` 만 남은 주소는 아무것도 강조하지 못한다.
        Assert.Equal("/files", StudioView.WithQuery("/files", "field", string.Empty));
    }

    /// <summary>
    /// 폼이 있는 파일의 오류는 <b>칸까지</b> 짚는다 (H10).
    /// 짚지 못하면 링크가 화면만 열고 사람이 다시 찾아야 한다.
    /// </summary>
    [Theory]
    [InlineData("/archetypes/3/population_weight", "archetypes", "population_weight")]
    [InlineData("/archetypes/3/traits/courage", "archetypes", "traits/courage")]
    [InlineData("/archetypes/3/allowed_actions/2", "archetypes", "allowed_actions")]
    [InlineData("/plans/7/steps/2/action", "plans", "steps")]
    [InlineData("/overrides/0/patrol_route/1", "overrides", "patrol_route")]
    public void IssueLocator_MapsPointerToFormField(string pointer, string array, string expected) =>
        Assert.Equal(expected, IssueLocator.FieldOf(pointer, array));

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
        using var runner = new GeneratorRunner(new StudioOptions(_directory, "127.0.0.1", 25_056, ReadOnly: false));

        Assert.False(runner.Available);
        Assert.False(runner.CanRunFor(_directory));
    }

    /// <summary>
    /// 저장소 <b>안의</b> 연습장도 거절한다 (H16).
    ///
    /// <b>예전 테스트는 저장소 밖만 봤다</b> — 생성기는 원본 <c>masterdata/</c> 를 읽으므로,
    /// 연습장에 대고 돌리면 연습장에서 고친 <c>pois.json</c> 이 빠진 거리표가 나온다.
    /// 그 파일은 검증도 통과하고 기동도 되지만 조용히 틀렸다.
    /// </summary>
    [Fact]
    public void GeneratorRunner_RefusesSandboxInsideRepo()
    {
        var options = new StudioOptions(TestPaths.MasterData, "127.0.0.1", 25_056, ReadOnly: true);
        using var runner = new GeneratorRunner(options);

        Assert.True(runner.Available);
        Assert.True(runner.CanRunFor(TestPaths.MasterData));
        Assert.False(runner.CanRunFor(TestPaths.At("lab", "studio-x", "masterdata")));
    }

    /// <summary>
    /// 읽기 전용은 <b>모든</b> 쓰기 경로를 거절한다 (H28).
    /// 하나라도 새면 "읽기 전용" 이라는 약속이 거짓이 된다.
    /// </summary>
    [Fact]
    public void ReadOnly_RefusesEveryWritePath()
    {
        var workspace = new StudioWorkspace(
            new StudioOptions(_directory, "127.0.0.1", 25_056, ReadOnly: true) { BackupRoot = _backups });

        StudioArchetypeForm form = workspace.LoadArchetypeForm("blacksmith");
        StudioFallbackForm plan = workspace.LoadFallbackForm("blacksmith");

        Action[] writes =
        [
            () => workspace.SaveFile("archetypes.json", "{}"),
            () => workspace.SaveArchetype("blacksmith", workspace.LoadArchetype("blacksmith").Json),
            () => workspace.SaveArchetypeForm(form),
            () => workspace.SaveFallbackForm(plan),
            () => workspace.SaveNpcOverride(new StudioNpcOverrideDraft(1, [], null, string.Empty, string.Empty, null)),
            () => workspace.AppendPoi(new StudioNewPlace(
                "x_001_00", "market_east", "Workplace", "apiary", 0, 0, 1, "Morning", "Evening", [], [])),
            () => workspace.Restore(new StudioBackup("20260101-000000", "archetypes.json", _backups, DateTime.Now)),
            () => workspace.OpenSandbox("ro"),
            () => workspace.DiscardSandbox(),
            () => workspace.ApplyToOrigin(),
        ];

        foreach (Action write in writes)
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(write);

            Assert.Contains("읽기 전용", error.Message, StringComparison.Ordinal);
        }
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
