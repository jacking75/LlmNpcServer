using System.Collections.Immutable;
using System.Text.Json;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.Narrative;
using Npc.Studio;
using Npc.Studio.Services;

namespace Npc.Tests.Studio;

/// <summary>
/// 읽기 화면이 쓰는 값들 (T07·T08·T18·T26·T27·T28·T34).
///
/// <b>"그려진다" 를 단언하지 않는다</b> (CLAUDE.md §5.1). 여기서 보는 것은
/// 드리프트(계약↔파서)·불변식(합·결정론)·검사기의 자체 시험뿐이다.
/// </summary>
public sealed class StudioViewTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "npc-studio-view-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>되돌리기 백업. 기본 위치(%LOCALAPPDATA%)에 시험 부스러기를 남기지 않는다.</summary>
    private readonly string _backups = Path.Combine(
        Path.GetTempPath(), "npc-studio-backup-" + Guid.NewGuid().ToString("N")[..8]);

    public StudioViewTests() => CopyDirectory(TestPaths.MasterData, _directory);

    /// <summary>개요의 통근 거리는 인스턴스 카드와 같은 계산이다 (T07).</summary>
    [Fact]
    public void LoadNpcOverview_ComputesCommuteLikeInstanceCard()
    {
        StudioWorkspace workspace = CreateWorkspace();

        foreach (int id in new[] { 1, 500, 2326, 4999 })
        {
            StudioNpcOverview overview = workspace.LoadNpcOverview(id);

            float expected = workspace.WithData((data, instances) =>
                InstanceCard.Facts(data, instances![id - 1]).CommuteMeters);

            Assert.Equal(expected, overview.Facts.CommuteMeters);
            Assert.NotEmpty(overview.Sentence);
            Assert.NotEmpty(overview.ZonePois);
            Assert.Contains(overview.ZonePois, p => p.IsHome);
        }
    }

    /// <summary>지역별 거주자 합은 명단 전체와 같다 (T08).</summary>
    [Fact]
    public void LoadPlaces_CountsResidentsFromInstances()
    {
        var places = new StudioPlaces(CreateWorkspace());

        Assert.Equal(5_000, places.Zones().Sum(z => z.Residents));
        Assert.All(places.Zones(), zone => Assert.True(zone.PoiCount > 0));
    }

    /// <summary>같은 <c>zones.json</c> 이면 타일이 같은 자리다 (T26).</summary>
    [Fact]
    public void ZoneLayout_IsDeterministic()
    {
        var places = new StudioPlaces(CreateWorkspace());

        ImmutableArray<ZoneTile> first = places.Tiles();
        ImmutableArray<ZoneTile> second = places.Tiles();

        Assert.Equal(first.Length, second.Length);
        Assert.All(
            first.Zip(second),
            pair =>
            {
                Assert.Equal(pair.First.Zone.Id, pair.Second.Zone.Id);
                Assert.Equal(pair.First.Column, pair.Second.Column);
                Assert.Equal(pair.First.Row, pair.Second.Row);
            });
    }

    /// <summary>
    /// 핀은 사람이 검수한 것이다 — 전부 컴파일되고 스텝 판정에 ✗ 가 없어야 한다 (T27).
    /// <b>✗ 가 있으면 실제 발견이다.</b>
    /// </summary>
    [Fact]
    public void PlanStoreReader_CompilesPinnedPlans()
    {
        string planStore = TestPaths.At("planstore");

        if (!Directory.Exists(Path.Combine(planStore, "pinned")))
        {
            return;
        }

        StudioWorkspace workspace = CreateWorkspace();
        var reader = new PlanStoreReader(
            new StudioOptions(_directory, "127.0.0.1", 25_056, ReadOnly: true) { PlanStore = planStore },
            workspace);

        int compiled = 0;

        foreach (string file in Directory.EnumerateFiles(Path.Combine(planStore, "pinned"), "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(file);

            Assert.True(
                BucketKey.TryParse(name, out string archetypeId, out _, out _, out _),
                $"{name} 을(를) 버킷으로 읽지 못했다.");

            ImmutableArray<BucketCell> grid = reader.Grid(archetypeId);
            BucketCell cell = grid.First(c => c.Bucket.Format(archetypeId) == name);

            Assert.Equal(BucketState.Pinned, cell.State);

            CompiledPlan plan = reader.Compile(archetypeId, cell.Bucket)
                ?? throw new InvalidOperationException($"{name} 을(를) 컴파일하지 못했다.");

            ImmutableArray<StepTrace> trace = workspace.WithData((data, _) =>
                PlanExplain.Trace(data, plan, cell.Bucket, plan.Bucket.A));

            Assert.All(trace, step => Assert.True(step.Ok, $"{name} 스텝 {step.Index + 1}: {step.Code} {step.Reason}"));
            compiled++;
        }

        Assert.True(compiled > 0, "핀 플랜을 하나도 읽지 못했다.");
    }

    /// <summary>
    /// 라이브 파서는 <c>docs/openapi.json</c> 의 <c>NpcSummary</c> 를 읽는다 (T28 드리프트).
    /// <b>필드 이름을 지어내면 화면이 조용히 빈 값을 그린다.</b>
    /// </summary>
    [Fact]
    public void LiveClient_ParsesNpcsSample()
    {
        using JsonDocument api = JsonDocument.Parse(File.ReadAllText(TestPaths.At("docs", "openapi.json")));

        JsonElement summary = api.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty("NpcSummary").GetProperty("properties");

        // 스키마가 선언한 필드 이름으로 표본을 만든다 — 이름이 바뀌면 여기서 깨진다.
        var sample = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (JsonProperty property in summary.EnumerateObject())
        {
            sample[property.Name] = property.Value.GetProperty("type").GetString() == "integer"
                ? 7
                : "x_" + property.Name;
        }

        string json = JsonSerializer.Serialize(new { tick = 1, total = 1, npcs = new[] { sample } });

        LiveNpc npc = Assert.Single(LiveClient.Parse(json));

        Assert.Equal(7, npc.Id);
        Assert.Equal(7, npc.Slot);
        Assert.Equal(7, npc.Lod);
        Assert.Equal("x_archetype", npc.Archetype);
        Assert.Equal("x_zone", npc.Zone);
        Assert.Equal("x_poi", npc.Poi);
        Assert.Equal("x_action", npc.Action);
        Assert.Equal("x_stepStatus", npc.StepStatus);
        Assert.Equal("x_planKind", npc.PlanKind);
    }

    /// <summary>용기를 내리면 요약이 그 결과를 말한다 (T34).</summary>
    [Fact]
    public void DefinitionDiff_MentionsReactionChange()
    {
        StudioWorkspace workspace = CreateWorkspace();

        ImmutableArray<string> lines = workspace.WithData((data, _) =>
        {
            ArchetypeDef before = data.Archetypes.Archetypes.First(a => a.Id == "blacksmith");
            ArchetypeDef after = before with { Traits = before.Traits with { Courage = 30 } };

            return DefinitionDiff.Describe(data, before, after);
        });

        Assert.Contains(lines, line => line.Contains("용기 55", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("flee_on_threat", StringComparison.Ordinal));
    }

    /// <summary>바꾸기 전 원본으로 되돌릴 수 있다 (T18).</summary>
    [Fact]
    public void Undo_RestoresPreviousBytes()
    {
        StudioWorkspace workspace = CreateWorkspace();
        string path = Path.Combine(_directory, "archetypes.json");
        byte[] before = File.ReadAllBytes(path);

        StudioArchetypeForm form = workspace.LoadArchetypeForm("blacksmith");

        Assert.True(workspace.SaveArchetypeForm(form with { Desc = "되돌리기 시험용 설명이다." }).Saved);
        Assert.NotEqual(before, File.ReadAllBytes(path));

        StudioBackup backup = workspace.Backups().First(b => b.File == "archetypes.json");
        StudioSaveResult restored = workspace.Restore(backup);

        Assert.True(restored.Saved, restored.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
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
