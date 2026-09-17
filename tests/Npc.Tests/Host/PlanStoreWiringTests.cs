using Npc.Core;
using Npc.Host;
using Npc.Host.Metrics;
using Npc.MasterData;
using Npc.Tests.Gates;

namespace Npc.Tests.Host;

/// <summary>기동 시 플랜 스토어 로드. docs/13 §2 · T3-20.</summary>
public sealed class PlanStoreWiringTests : IDisposable
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private readonly string _store = Path.Combine(
        Path.GetTempPath(), "npc-host-store-" + Guid.NewGuid().ToString("N")[..8]);

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_store))
        {
            Directory.Delete(_store, recursive: true);
        }
    }

    private HostOptions Options(params string[] args)
    {
        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return options with { MasterData = TestPaths.MasterData, PlanStore = _store };
    }

    /// <summary>
    /// T3-20 완료 조건 — 기동 시 2,880건을 전부 로드한다.
    ///
    /// <para>
    /// <b>여기서 시간을 재지 않는다.</b> 예전에는 같은 테스트가 <c>Assert.True(elapsed &lt;= 3.0)</c>
    /// 로 기동 시간까지 판정했는데, 저장소 전체에서 벽시계를 단언하는 유일한 자리였고
    /// 기계가 붐비면 그 숫자만으로 빨간불이 났다 — 코드가 아니라 그날의 CPU 사정이
    /// 판정한 것이다. 시간은 <see cref="Host_BootsWithinBudget"/>(<c>Category=Load</c>)가 본다.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Host_LoadsEveryBucket()
    {
        Phase3Fixture.WriteFullStore(_store, s_data);

        var log = new StringWriter();

        await using (NpcHost host = NpcHost.Create(
            Options("--loopback", "--npcs", "50", "--days", "1", "--no-llm", "--no-dashboard"), log))
        {
            MetricsSnapshot metrics = host.Metrics.Snapshot();

            Assert.Equal(TestPaths.TotalKeys, metrics.Cache.FilledBuckets);
            Assert.Equal(0, metrics.Cache.ColdBuckets);
        }

        string output = log.ToString();

        Assert.Contains($"버킷 {TestPaths.TotalKeys}/{TestPaths.TotalKeys}", output, StringComparison.Ordinal);

        // 스토어가 지금 마스터데이터로 만들어진 것이면 경고가 없다.
        Assert.DoesNotContain("낡았다", output, StringComparison.Ordinal);
        Assert.DoesNotContain("미생성 버킷", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// T3-20 완료 조건의 시간 쪽 — 조립 전체(마스터데이터 · 인구 배치 · 버킷 2,880)가
    /// 3초 안에 끝난다.
    ///
    /// <para>
    /// <b><c>Category=Load</c> 다.</b> 벽시계 판정은 기계 사정을 타므로 기본 CI 에 두지 않는다 —
    /// 이 저장소의 성능 판정은 야간 부하와 <c>npc perf --check</c>(G-03)의 몫이다.
    /// 여기서 지키는 것은 "선형이고 느리지 않다" 이지 "정확히 3초" 가 아니다.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Host_BootsWithinBudget()
    {
        Phase3Fixture.WriteFullStore(_store, s_data);

        var log = new StringWriter();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        await using NpcHost host = NpcHost.Create(
            Options("--loopback", "--npcs", "50", "--days", "1", "--no-llm", "--no-dashboard"), log);

        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;

        Assert.True(elapsed <= 3.0, $"기동이 {elapsed:F2}초 걸렸다 (예산 3초).");
    }

    /// <summary>
    /// T3-20 완료 조건 — 미생성 버킷은 폴백으로 해소된다.
    /// 스토어가 아예 없어도 P1 과 똑같이 기동한다.
    /// </summary>
    [Fact]
    public async Task Host_FallsBackWhenStoreIsMissing()
    {
        var log = new StringWriter();

        await using NpcHost host = NpcHost.Create(
            Options("--loopback", "--npcs", "50", "--days", "1", "--no-llm", "--no-dashboard"), log);

        MetricsSnapshot metrics = host.Metrics.Snapshot();

        Assert.Equal(0, metrics.Cache.FilledBuckets);
        Assert.Equal(TestPaths.TotalKeys, metrics.Cache.ColdBuckets);

        // 폴백 40개는 그대로 등록돼 있다 — 이것이 시나리오 C 가 통과하는 이유다.
        Assert.Contains("폴백 40", log.ToString(), StringComparison.Ordinal);
        Assert.Contains("planstore 없음", log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>일부만 채워진 스토어 — 나머지는 폴백으로 해소되고 경고가 남는다.</summary>
    [Fact]
    public async Task Host_WarnsAboutColdBuckets()
    {
        Phase3Fixture.WriteStore(_store, s_data, count: 100, writeManifest: true);

        var log = new StringWriter();

        await using NpcHost host = NpcHost.Create(
            Options("--loopback", "--npcs", "50", "--days", "1", "--no-llm", "--no-dashboard"), log);

        Assert.Equal(100, host.Metrics.Snapshot().Cache.FilledBuckets);
        Assert.Contains($"미생성 버킷 {TestPaths.TotalKeys - 100}건", log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// T3-20 — 무효화 판정(T3-06) 결과를 로그로 경고한다. 기동을 막지는 않는다.
    /// </summary>
    [Fact]
    public async Task Host_WarnsWhenStoreIsStale()
    {
        Phase3Fixture.WriteFullStore(_store, s_data);

        // 프롬프트가 바뀐 상황을 만든다 — manifest 의 prefix_hash 만 다르게 둔다.
        Npc.Planning.Manifest stale = Npc.Planning.Manifest.LoadFrom(_store)!;

        (stale with { PrefixHash = "옛날프리픽스해시" }).Save(
            Path.Combine(_store, Npc.Planning.Manifest.FileName));

        var log = new StringWriter();

        await using NpcHost host = NpcHost.Create(
            Options("--loopback", "--npcs", "50", "--days", "1", "--no-llm", "--no-dashboard"), log);

        string output = log.ToString();

        Assert.Contains("낡았다", output, StringComparison.Ordinal);
        Assert.Contains("Full", output, StringComparison.Ordinal);
        Assert.Contains("prompt/", output, StringComparison.Ordinal);

        // 경고만 하고 스토어는 그대로 올린다.
        Assert.Equal(TestPaths.TotalKeys, host.Metrics.Snapshot().Cache.FilledBuckets);
    }

    /// <summary>manifest 가 없으면 그것도 경고한다 — 어느 마스터데이터로 만든 스토어인지 알 수 없다.</summary>
    [Fact]
    public async Task Host_WarnsWhenManifestIsMissing()
    {
        Phase3Fixture.WriteStore(_store, s_data, count: 10, writeManifest: false);

        var log = new StringWriter();

        await using NpcHost host = NpcHost.Create(
            Options("--loopback", "--npcs", "50", "--days", "1", "--no-llm", "--no-dashboard"), log);

        Assert.Contains("manifest.json 이 없다", log.ToString(), StringComparison.Ordinal);
        Assert.Equal(10, host.Metrics.Snapshot().Cache.FilledBuckets);
    }

    /// <summary>깨진 플랜 파일은 그 버킷만 비우고 사유를 남긴다. 기동은 계속된다.</summary>
    [Fact]
    public async Task Host_SurvivesBrokenPlanFiles()
    {
        Phase3Fixture.WriteStore(_store, s_data, count: 10, writeManifest: true);

        File.WriteAllText(Path.Combine(_store, "plans", "이건버킷이아니다.json"), "{}");
        File.WriteAllText(Path.Combine(_store, "plans", "farmer@Noon.Peace.Cold.json"), "{ 깨진 JSON");

        var log = new StringWriter();

        await using NpcHost host = NpcHost.Create(
            Options("--loopback", "--npcs", "50", "--days", "1", "--no-llm", "--no-dashboard"), log);

        Assert.Contains("건을 못 올렸다", log.ToString(), StringComparison.Ordinal);
        Assert.Equal(10, host.Metrics.Snapshot().Cache.FilledBuckets);
    }

    /// <summary><c>--planstore</c> 옵션이 파싱된다. 예전에는 경로가 코드에 박혀 있었다.</summary>
    [Fact]
    public void Host_ParsesPlanStoreOption()
    {
        Assert.True(HostOptions.TryParse(["--planstore", "./mystore"], out HostOptions options, out string? error), error);
        Assert.Equal("./mystore", options.PlanStore);

        Assert.Equal("./planstore", new HostOptions().PlanStore);
        Assert.Contains("--planstore", HostOptions.Usage, StringComparison.Ordinal);

        // 값이 없으면 거절한다.
        Assert.False(HostOptions.TryParse(["--planstore"], out _, out _));
    }
}
