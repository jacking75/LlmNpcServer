using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>플랜 스토어 파일 입출력. docs/03 §7 · T3-07.</summary>
public sealed class PlanStoreIoTests : IDisposable
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "npc-planstore-" + Guid.NewGuid().ToString("N")[..8]);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static CompiledPlan Plan(BucketKey bucket, string goal, PlanOrigin origin = PlanOrigin.Prebaked)
    {
        const string Template = """
            {"schema":1,"goal":"GOAL","loop":true,"on_step_fail":"skip","steps":[{"action":"MoveTo","args":{"poi":"$home"},"timeout_s":120},{"action":"Rest","args":{"duration_s":600}},{"action":"Wait","args":{"duration_s":60}}]}
            """;

        string json = Template.Replace("GOAL", goal, StringComparison.Ordinal);

        PlanDocument document = System.Text.Json.JsonSerializer.Deserialize(
            json, PlanJsonContext.Default.PlanDocument)!;

        return Npc.Core.Plan.PlanCompiler.Compile(
            document, bucket, new PlanId(0), s_data, origin, version: 3, sourceJson: json);
    }

    /// <summary>
    /// T3-07 완료 조건 — 2,880건 저장 → 로드 → 내용 동일.
    /// </summary>
    [Fact]
    public void PlanStoreIo_RoundTrip()
    {
        PlanStore saved = PlanStore.CreateIdleOnly(s_data);

        for (int index = 0; index < BucketKey.TotalKeys; index++)
        {
            var bucket = BucketKey.FromIndex(index);

            saved.SetBucket(bucket, Plan(bucket, "goal_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        Assert.Equal(BucketKey.TotalKeys, PlanStoreIo.SaveAll(_directory, saved, s_data));

        // 저장이 히트율 카운터를 건드리지 않는다 — 건드리면 게이트 측정이 오염된다.
        Assert.Equal(0, saved.Hits);
        Assert.Equal(0, saved.Misses);

        Assert.Equal(
            BucketKey.TotalKeys,
            Directory.GetFiles(Path.Combine(_directory, "plans"), "*.json").Length);

        PlanStore loaded = PlanStore.CreateIdleOnly(s_data);
        PlanStoreLoadReport report = PlanStoreIo.LoadAll(_directory, loaded, s_data);

        Assert.Equal(BucketKey.TotalKeys, report.Loaded);
        Assert.Equal(0, report.Pinned);
        Assert.Equal(0, report.Skipped);
        Assert.Equal(0, report.Failed);
        Assert.Empty(report.Errors);
        Assert.Equal(BucketKey.TotalKeys, loaded.FilledBuckets);

        for (int index = 0; index < BucketKey.TotalKeys; index++)
        {
            var bucket = BucketKey.FromIndex(index);

            CompiledPlan before = saved.PeekBucket(bucket)!;
            CompiledPlan after = loaded.PeekBucket(bucket)!;

            Assert.Equal(before.Goal, after.Goal);
            Assert.Equal(before.Loop, after.Loop);
            Assert.Equal(before.OnFail, after.OnFail);
            Assert.Equal(before.Version, after.Version);
            Assert.Equal(before.Origin, after.Origin);
            Assert.Equal(before.Bucket, after.Bucket);
            Assert.Equal(before.RequiredFlags, after.RequiredFlags);
            Assert.Equal(before.ForbiddenFlags, after.ForbiddenFlags);
            // ImmutableArray<T>.Equals 는 내부 배열 참조를 비교한다. 요소로 비교해야 한다.
            Assert.Equal(before.Steps.ToArray(), after.Steps.ToArray());
            Assert.Equal(before.StepFlagSets.ToArray(), after.StepFlagSets.ToArray());
            Assert.Equal(before.SourceJson, after.SourceJson);
        }
    }

    /// <summary>T3-07 완료 조건 — pinned 가 plans 를 덮어쓴다. 로드 순서가 곧 우선순위다.</summary>
    [Fact]
    public void PlanStoreIo_PinnedOverwritesPlans()
    {
        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));

        var pinnedBucket = new BucketKey(smith.Code, TimeOfDay.Evening, RegionState.War, Climate.Cold);
        var plainBucket = new BucketKey(smith.Code, TimeOfDay.Dawn, RegionState.Peace, Climate.Fair);

        // 같은 버킷이 두 계층에 다 있다.
        PlanStoreIo.SavePlan(_directory, PlanLayer.Plans, pinnedBucket, Plan(pinnedBucket, "prebaked"), s_data);
        PlanStoreIo.SavePlan(_directory, PlanLayer.Plans, plainBucket, Plan(plainBucket, "prebaked_only"), s_data);
        PlanStoreIo.SavePlan(
            _directory, PlanLayer.Pinned, pinnedBucket,
            Plan(pinnedBucket, "human_reviewed", PlanOrigin.Pinned), s_data);

        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        PlanStoreLoadReport report = PlanStoreIo.LoadAll(_directory, store, s_data);

        Assert.Equal(1, report.Loaded);   // 겹친 몫은 pinned 로 옮겨진다
        Assert.Equal(1, report.Pinned);
        Assert.Equal(2, report.Total);

        Assert.Equal("human_reviewed", store.PeekBucket(pinnedBucket)!.Goal);
        Assert.True(store.IsPinned(pinnedBucket));
        Assert.Equal(PlanOrigin.Pinned, store.OriginOf(pinnedBucket));

        Assert.Equal("prebaked_only", store.PeekBucket(plainBucket)!.Goal);
        Assert.False(store.IsPinned(plainBucket));

        // 재프리베이크가 pinned 를 덮어쓰지 못한다 (T3-02 와 같은 보장).
        store.SetBucket(pinnedBucket, Plan(pinnedBucket, "prebaked_v2"));
        Assert.Equal("human_reviewed", store.PeekBucket(pinnedBucket)!.Goal);

        // 저장도 pinned 버킷을 건너뛴다 — pinned/ 는 사람이 관리하는 계층이다.
        string other = Path.Combine(_directory, "재저장");
        Assert.Equal(1, PlanStoreIo.SaveAll(other, store, s_data));
        Assert.False(File.Exists(PlanStoreIo.PathOf(other, PlanLayer.Plans, pinnedBucket, s_data)));
        Assert.True(File.Exists(PlanStoreIo.PathOf(other, PlanLayer.Plans, plainBucket, s_data)));
    }

    /// <summary>파일명은 docs/03 §7 의 <c>{bucket}.json</c> 이고 왕복 파싱이 된다.</summary>
    [Fact]
    public void PlanStoreIo_FileNameIsBucketKey()
    {
        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));

        var bucket = new BucketKey(smith.Code, TimeOfDay.Dawn, RegionState.Peace, Climate.Fair);
        string path = PlanStoreIo.PathOf(_directory, PlanLayer.Plans, bucket, s_data);

        Assert.Equal("blacksmith@Dawn.Peace.Fair.json", Path.GetFileName(path));
        Assert.Equal("plans", Path.GetFileName(Path.GetDirectoryName(path)));

        Assert.True(PlanStoreIo.TryParseBucket("blacksmith@Dawn.Peace.Fair", s_data, out BucketKey parsed));
        Assert.Equal(bucket, parsed);

        // 전 버킷이 왕복한다.
        for (int index = 0; index < BucketKey.TotalKeys; index++)
        {
            var each = BucketKey.FromIndex(index);
            string name = each.Format(s_data.Archetypes[each.A].Id);

            Assert.True(PlanStoreIo.TryParseBucket(name, s_data, out BucketKey back), name);
            Assert.Equal(each, back);
        }

        // 못 읽는 이름들.
        Assert.False(PlanStoreIo.TryParseBucket("없는아키타입@Dawn.Peace.Fair", s_data, out _));
        Assert.False(PlanStoreIo.TryParseBucket("blacksmith@Dawn.Peace", s_data, out _));
        Assert.False(PlanStoreIo.TryParseBucket("blacksmith@Dawn.Peace.무지개", s_data, out _));
        Assert.False(PlanStoreIo.TryParseBucket("blacksmith@9.Peace.Fair", s_data, out _));
        Assert.False(PlanStoreIo.TryParseBucket("blacksmith", s_data, out _));
        Assert.False(PlanStoreIo.TryParseBucket(string.Empty, s_data, out _));
    }

    /// <summary>깨진 파일은 조용히 넘기지 않는다 — 이유를 남기고 그 버킷만 폴백으로 해소된다.</summary>
    [Fact]
    public void PlanStoreIo_ReportsBadFilesWithoutThrowing()
    {
        Assert.True(s_data.Archetypes.TryGet("farmer", out ArchetypeDef farmer));

        var good = new BucketKey(farmer.Code, TimeOfDay.Noon, RegionState.Peace, Climate.Fair);
        string folder = Path.Combine(_directory, "plans");

        PlanStoreIo.SavePlan(_directory, PlanLayer.Plans, good, Plan(good, "healthy"), s_data);

        File.WriteAllText(Path.Combine(folder, "farmer@Noon.Peace.Cold.json"), "{ 이건 JSON 이 아니다");
        File.WriteAllText(Path.Combine(folder, "farmer@Noon.Peace.Storm.json"), """{"schema":1}""");
        File.WriteAllText(Path.Combine(folder, "이건버킷이아니다.json"), "{}");
        File.WriteAllText(
            Path.Combine(folder, "farmer@Evening.Peace.Fair.json"),
            """{"schema":1,"plan":{"schema":1,"goal":"bad","loop":true,"steps":[{"action":"없는액션","args":{}}]}}""");

        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        PlanStoreLoadReport report = PlanStoreIo.LoadAll(_directory, store, s_data);

        Assert.Equal(1, report.Loaded);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(3, report.Failed);
        Assert.Equal(4, report.Errors.Length);
        Assert.Equal("healthy", store.PeekBucket(good)!.Goal);

        // 실패한 버킷은 비어 있고 Resolve 는 여전히 null 이 아니다.
        var failed = new BucketKey(farmer.Code, TimeOfDay.Evening, RegionState.Peace, Climate.Fair);
        Assert.False(store.HasBucket(failed));
        Assert.NotNull(store.Resolve(failed));
    }

    /// <summary>폴더가 없으면 아무것도 하지 않는다 — 첫 기동에는 스토어가 없다.</summary>
    [Fact]
    public void PlanStoreIo_LoadingMissingStoreIsNoop()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);
        PlanStoreLoadReport report = PlanStoreIo.LoadAll(_directory, store, s_data);

        Assert.Equal(0, report.Total);
        Assert.Empty(report.Errors);
        Assert.Equal(0, store.FilledBuckets);
    }

    /// <summary>3계층 폴더 이름이 docs/03 §7 그대로다.</summary>
    [Fact]
    public void PlanStoreIo_HasThreeLayers()
    {
        Assert.Equal("plans", PlanStoreIo.FolderOf(PlanLayer.Plans));
        Assert.Equal("pinned", PlanStoreIo.FolderOf(PlanLayer.Pinned));
        Assert.Equal("rejected", PlanStoreIo.FolderOf(PlanLayer.Rejected));
    }
}
