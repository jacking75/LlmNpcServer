using System.Collections.Immutable;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>
/// 캐시 무효화 판정. docs/13 §3 · T3-06.
///
/// <b>표의 10개 케이스 전부에 테스트가 하나씩 있다.</b> 여기가 틀리면 POI 하나 추가에
/// 2,880건 전량 재생성이 걸려 W8 이후 작업이 지옥이 된다.
/// </summary>
public sealed class PlanStoreValidatorTests
{
    private const string Prefix = "56a0c601f48392e0";

    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>지금 마스터데이터와 완전히 일치하는 manifest.</summary>
    private static Manifest Current() => Manifest.For(
        s_data, Prefix, new ManifestGeneratedBy("T2", "test-model", 0.4));

    /// <summary>그 파일 하나의 해시만 다르게 만든 manifest — 그 파일이 바뀐 상황이다.</summary>
    private static Manifest WithChanged(params string[] files)
    {
        Manifest manifest = Current();
        var hashes = manifest.FileHashes.ToBuilder();

        foreach (string file in files)
        {
            int index = -1;

            for (int i = 0; i < hashes.Count; i++)
            {
                if (string.Equals(hashes[i].FileName, file, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            Assert.True(index >= 0, $"{file} 이 마스터데이터 해시 목록에 없다.");
            hashes[index] = new FileHash(file, "0000000000000000000000000000000000000000000000000000000000000000");
        }

        // 파일 하나가 바뀌면 전체 콘텐츠 해시도 달라진다.
        return manifest with
        {
            FileHashes = hashes.ToImmutable(),
            MasterdataHash = "diff-" + manifest.MasterdataHash,
        };
    }

    private static InvalidationScope ScopeAfterChanging(params string[] files) =>
        PlanStoreValidator.Compare(WithChanged(files), s_data, Prefix);

    // ── §3 표: Full 4종 ──

    [Fact]
    public void Invalidation_WorldFlagsIsFull() =>
        Assert.Equal(InvalidationScope.Full, ScopeAfterChanging("world_flags.json"));

    [Fact]
    public void Invalidation_ActionsIsFull() =>
        Assert.Equal(InvalidationScope.Full, ScopeAfterChanging("actions.json"));

    [Fact]
    public void Invalidation_ArchetypesIsFull() =>
        Assert.Equal(InvalidationScope.Full, ScopeAfterChanging("archetypes.json"));

    [Fact]
    public void Invalidation_ContextBucketsIsFull() =>
        Assert.Equal(InvalidationScope.Full, ScopeAfterChanging("context_buckets.json"));

    /// <summary>prompt/** — 프리픽스 해시가 바뀌면 파일 diff 를 볼 것도 없이 전량이다.</summary>
    [Fact]
    public void Invalidation_PromptIsFull()
    {
        Assert.Equal(InvalidationScope.Full, PlanStoreValidator.ScopeOf("prompt/system_rules.md"));
        Assert.Equal(InvalidationScope.Full, PlanStoreValidator.ScopeOf("prompt/fewshot/blacksmith.json"));

        // 마스터데이터는 그대로인데 프리픽스만 달라진 상황.
        InvalidationScope scope = PlanStoreValidator.Compare(
            Current(), s_data, "다른프리픽스해시", out ImmutableArray<string> changed);

        Assert.Equal(InvalidationScope.Full, scope);
        // ImmutableArray<T>.Equals 는 내부 배열 참조를 비교한다. 요소로 비교해야 한다.
        Assert.Equal(["prompt/"], changed.ToArray());
    }

    // ── §3 표: Partial 3종 ──

    [Fact]
    public void Invalidation_PoisIsPartial() =>
        Assert.Equal(InvalidationScope.Partial, ScopeAfterChanging("pois.json"));

    [Fact]
    public void Invalidation_ItemsIsPartial() =>
        Assert.Equal(InvalidationScope.Partial, ScopeAfterChanging("items.json"));

    [Fact]
    public void Invalidation_ZonesIsPartial() =>
        Assert.Equal(InvalidationScope.Partial, ScopeAfterChanging("zones.json"));

    // ── §3 표: None 4종 ──

    [Fact]
    public void Invalidation_PoiDistancesIsNone() =>
        Assert.Equal(InvalidationScope.None, ScopeAfterChanging("poi_distances.bin"));

    [Fact]
    public void Invalidation_FallbackPlansIsNone() =>
        Assert.Equal(InvalidationScope.None, ScopeAfterChanging("fallback_plans.json"));

    [Fact]
    public void Invalidation_InterruptsIsNone() =>
        Assert.Equal(InvalidationScope.None, ScopeAfterChanging("interrupts.json"));

    /// <summary>
    /// npc_instances.json — 플랜은 개체에 안 묶인다.
    /// 애초에 <see cref="MasterDataSet.ContentHash"/> 에도 안 들어가므로 diff 에 나타나지도 않는다.
    /// </summary>
    [Fact]
    public void Invalidation_NpcInstancesIsNone()
    {
        Assert.Equal(InvalidationScope.None, PlanStoreValidator.ScopeOf("npc_instances.json"));
        Assert.Null(s_data.HashOf("npc_instances.json"));
    }

    // ── 판정 순서와 경계 ──

    /// <summary>콘텐츠 해시가 같으면 볼 것이 없다.</summary>
    [Fact]
    public void Invalidation_UnchangedIsNone()
    {
        InvalidationScope scope = PlanStoreValidator.Compare(
            Current(), s_data, Prefix, out ImmutableArray<string> changed);

        Assert.Equal(InvalidationScope.None, scope);
        Assert.Empty(changed);
    }

    /// <summary>manifest 가 없으면 스토어도 없다 — 전량 생성이다.</summary>
    [Fact]
    public void Invalidation_MissingManifestIsFull() =>
        Assert.Equal(InvalidationScope.Full, PlanStoreValidator.Compare(null, s_data, Prefix));

    /// <summary>여러 파일이 바뀌면 가장 큰 범위를 따른다. Partial + Full = Full.</summary>
    [Fact]
    public void Invalidation_TakesWidestScope()
    {
        Assert.Equal(
            InvalidationScope.Partial,
            ScopeAfterChanging("pois.json", "interrupts.json", "poi_distances.bin"));

        Assert.Equal(
            InvalidationScope.Full,
            ScopeAfterChanging("pois.json", "actions.json"));
    }

    /// <summary>모르는 파일이 바뀌었으면 안전한 쪽이다.</summary>
    [Fact]
    public void Invalidation_UnknownFileIsFull()
    {
        Assert.Equal(InvalidationScope.Full, PlanStoreValidator.ScopeOf("새로운파일.json"));

        Manifest previous = Current();

        previous = previous with
        {
            FileHashes = previous.FileHashes.Add(new FileHash("새로운파일.json", "abc")),
            MasterdataHash = "diff-" + previous.MasterdataHash,
        };

        Assert.Equal(InvalidationScope.Full, PlanStoreValidator.Compare(previous, s_data, Prefix));
    }

    /// <summary>구버전 manifest 는 파일 해시가 없어 어느 파일이 바뀐지 알 수 없다 — 전량이다.</summary>
    [Fact]
    public void Invalidation_ManifestWithoutFileHashesIsFull()
    {
        Manifest previous = Current() with
        {
            FileHashes = [],
            MasterdataHash = "옛날해시",
        };

        Assert.Equal(InvalidationScope.Full, PlanStoreValidator.Compare(previous, s_data, Prefix));
    }

    /// <summary>바뀐 파일 목록이 이름 오름차순으로 나온다. 로그가 "왜 전량인가"를 보여줘야 한다.</summary>
    [Fact]
    public void Invalidation_ReportsChangedFiles()
    {
        _ = PlanStoreValidator.Compare(
            WithChanged("pois.json", "actions.json"), s_data, Prefix, out ImmutableArray<string> changed);

        Assert.Equal(["actions.json", "pois.json"], changed.ToArray());
    }
}
