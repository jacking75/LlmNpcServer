using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Host;
using Npc.Host.Metrics;
using Npc.Llm;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.Planning;
using Npc.Prebake;

namespace Npc.Tests.Gates;

/// <summary>
/// P3 게이트. docs/13 §7 체크리스트 9항목을 자동화한다.
///
/// <b>여기가 통과해야 P4 로 넘어간다.</b> 항목마다 테스트가 하나씩 붙어 있어
/// 무엇이 미달인지 이름만 보고 안다.
///
/// <para>
/// <b>실측 산출물이 필요한 항목이 다섯 있다</b> — 생성 완료율 · wall-clock · 실비용 ·
/// 프롬프트 캐시 적중률 · 검수 채택률. 앞 넷은 <c>planstore/manifest.json</c>, 다섯째는
/// <c>docs/measurements/review_W8.jsonl</c> 이 근거다.
/// </para>
///
/// <para>
/// 그 다섯은 <c>Category=Gate</c> 다. 산출물이 없으면 <b>실패한다</b> —
/// 없는 것을 통과로 세면 게이트가 거짓이 된다. 대신 기본 CI 에서는 제외한다 (CLAUDE.md §5).
/// 기제(mechanism)만 보는 나머지 항목은 상시 돈다.
/// </para>
/// </summary>
/// <param name="output">
/// 게이트 실측치를 남긴다. <c>dotnet test --logger "console;verbosity=detailed"</c> 로 볼 수 있고
/// 그 값이 <c>docs/measurements/P3_gate.md</c> 의 근거다.
/// </param>
public sealed class Phase3GateTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>
    /// 픽스처가 채우는 비율. <b>게이트 기준이 아니다</b> —
    /// 항목 1 의 "생성 완료 ≥ 95%" 는 2026-07-28 에 빠졌다 (docs/13 §7 개정).
    /// 여기 남은 것은 "5% 가 비어 있어도 폴백이 덮는가" 를 만들기 위한 픽스처 값이다.
    /// </summary>
    private const double FixtureFillRate = 0.95;

    /// <summary>시나리오 A 캐시 히트율 하한.</summary>
    private const double MinRuntimeHitRate = 0.98;

    /// <summary>
    /// 프롬프트 캐시 적중률 하한 — <b>명시적 캐싱</b> 엔진 (docs/13 §7 개정).
    /// 요청에 <c>cache_control</c> 을 실어 우리가 적중을 지시하는 방식이라 높게 잡을 수 있다.
    /// </summary>
    private const double MinPromptCacheHitRateExplicit = 0.95;

    /// <summary>
    /// 프롬프트 캐시 적중률 하한 — <b>암시적 캐싱</b> 엔진 (Gemini 계열).
    ///
    /// 제공사가 적중 여부를 요청 단위로 알아서 판정하고 그 판정이 비결정적이라
    /// (<c>W8_prebake.md §2</c>) 프리픽스를 고정해도 95% 에 닿지 않는다. 실측 42.6~69.2%.
    ///
    /// <b>40% 는 손익 기준이다</b> — 적중분이 1/10 단가라 적중률 40% 면 비용이 약 1/3 준다
    /// (<c>cost_actual.md §3</c>: 40.6% → 33.4% 절감). 그 아래면 프리픽스를 고정한 값어치가 없다.
    /// </summary>
    private const double MinPromptCacheHitRateImplicit = 0.40;

    /// <summary>프리베이크 wall-clock 상한(초).</summary>
    private const double MaxWallClockSeconds = 300;

    /// <summary>프리베이크 실비용 상한(USD).</summary>
    private const double MaxCostUsd = 5.00;

    /// <summary>검수 채택률 하한.</summary>
    private const double MinAdoptionRate = 0.80;

    /// <summary>검수 표본 수. docs/13 §5.</summary>
    private const int ReviewSampleSize = 40;

    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static HostOptions Options(string store, params string[] args)
    {
        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return options with { MasterData = TestPaths.MasterData, PlanStore = store };
    }

    // ── 1. 생성 완료 ≥ 95%, 나머지는 폴백으로 안전 해소 ──────────────

    /// <summary>
    /// 항목 1 (기제) — 5% 가 비어 있어도 <c>Resolve</c> 는 절대 null 이 아니고 폴백으로 해소된다.
    /// </summary>
    [Fact]
    public void Gate_ColdBucketsResolveToFallback()
    {
        PlanStore store = PlanStore.CreateIdleOnly(s_data);

        Phase3Fixture.RegisterFallbacks(store, s_data);

        // 우선순위 순으로 95% 만 채운다 — 남는 5% 는 Disaster 쪽 꼬리다.
        ImmutableArray<BucketKey> order = TargetSelector.SortByPriority(TargetSelector.All(s_data.Buckets), s_data);
        int filled = (int)(TestPaths.TotalKeys * FixtureFillRate);

        for (int i = 0; i < filled; i++)
        {
            store.SetBucket(order[i], Phase3Fixture.PlanFor(order[i], s_data));
        }

        Assert.True(store.FilledBuckets >= TestPaths.TotalKeys * FixtureFillRate);

        // 전 버킷이 유효한 플랜을 받는다. 미생성분은 폴백이다.
        for (int index = 0; index < TestPaths.TotalKeys; index++)
        {
            var bucket = BucketKey.FromIndex(index);
            CompiledPlan plan = store.Resolve(bucket, out PlanOrigin origin);

            Assert.NotNull(plan);
            Assert.NotEmpty(plan.Steps);

            if (!store.HasBucket(bucket))
            {
                Assert.Equal(PlanOrigin.Fallback, origin);
                Assert.NotEqual("idle_fallback", plan.Goal);   // 아키타입 폴백이 잡혔다
            }
        }
    }

    // 항목 1 의 "생성 완료율 ≥ 95%" 판정은 2026-07-28 에 빠졌다 (docs/13 §7 개정).
    //
    // 분모가 2,880 인데 도달 집합(264)만 만드는 방침을 택했으므로 정의상 영원히 미달이고,
    // 분모를 "선언된 대상 집합" 으로 바꾸면 docs/12 §9 의 통과율과 같은 것을 두 번 재게 된다.
    // 이 항목이 원래 지키려던 것 — "미생성 버킷이 있어도 Resolve 가 null 을 주지 않는다" —
    // 는 위의 Gate_ColdBucketsResolveToFallback 이 그대로 본다.

    // ── 2. 프리베이크 wall-clock ≤ 5분 ─────────────────────────────

    /// <summary>
    /// 항목 2 — 프리베이크가 5분 안에 끝난다.
    ///
    /// <b>"동시 32 → 3분" 은 상위 계획의 추정이지 실측이 아니다.</b> 실측 동시성이 32보다 낮게
    /// 나오면 이 기준을 그때 갱신하고 <c>TASKS.md §3</c> 에 남긴다 — 기준을 맞추려고
    /// 동시성을 올려 429 를 맞지 않는다 (docs/13 §7 의 각주).
    /// </summary>
    [Fact]
    [Trait("Category", "Gate")]
    public void Gate_PrebakeFinishesWithinFiveMinutes()
    {
        Manifest manifest = RequireCompleteRun(RequireManifest());

        Assert.True(
            manifest.WallClockSeconds <= MaxWallClockSeconds,
            $"wall-clock {manifest.WallClockSeconds:F1}s — 상한 {MaxWallClockSeconds}s {Scope(manifest)} "
            + $"(동시성 시작 {manifest.GeneratedBy.Concurrency} · 최대 {manifest.GeneratedBy.PeakConcurrency} · "
            + $"429 최초 {manifest.GeneratedBy.FirstRateLimitConcurrency})");
    }

    // ── 3. 프리베이크 실비용 ≤ $5 ─────────────────────────────────

    /// <summary>항목 3 — 실비용이 $5 이하다.</summary>
    [Fact]
    [Trait("Category", "Gate")]
    public void Gate_PrebakeCostsAtMostFiveDollars()
    {
        Manifest manifest = RequireCompleteRun(RequireManifest());

        Assert.True(
            manifest.CostUsd <= MaxCostUsd,
            $"실비용 ${manifest.CostUsd:F4} — 상한 ${MaxCostUsd:F2} {Scope(manifest)}");
    }

    // ── 4. 프롬프트 캐시 적중률 — 엔진 계열별 ─────────────────────

    /// <summary>
    /// 항목 4 — manifest 의 프롬프트 캐시 적중률이 <b>그 엔진 계열의 하한</b> 이상이다
    /// (docs/13 §7, 2026-07-28 개정).
    ///
    /// 로컬 엔진은 <c>cached_tokens</c> 를 항상 0 으로 보고하므로 이 항목은 외부 API 회차에만 적용된다
    /// (<c>W1_env.md §4.4</c>).
    /// </summary>
    [Fact]
    [Trait("Category", "Gate")]
    public void Gate_PromptCacheHitRateMeetsEngineFloor()
    {
        Manifest manifest = RequireCompleteRun(RequireManifest());

        // 로컬 엔진은 cached_tokens 를 항상 0 으로 보고한다. 그 회차에는 이 항목이 성립하지 않는다.
        Assert.False(
            manifest.GeneratedBy.Tier == "T1",
            "로컬 티어(T1) 회차의 manifest 다. 이 항목은 외부 API 회차에만 판정된다 (W1_env.md §4.4).");

        bool explicitCaching = UsesExplicitCaching(manifest.GeneratedBy.Model);
        double floor = explicitCaching ? MinPromptCacheHitRateExplicit : MinPromptCacheHitRateImplicit;

        Assert.True(
            manifest.CacheHitRate >= floor,
            $"프롬프트 캐시 적중률 {manifest.CacheHitRate:P1} — "
            + $"{(explicitCaching ? "명시적" : "암시적")} 캐싱 하한 {floor:P0} "
            + $"(엔진 {manifest.GeneratedBy.Model}) {Scope(manifest)}. "
            + "프리픽스가 흔들렸는지(SHA 종류 수)와 워밍업 여부를 본다.");
    }

    /// <summary>
    /// 이 엔진이 <b>명시적</b> 프롬프트 캐싱을 쓰는가 (docs/13 §7 개정).
    ///
    /// 명시적 = 요청에 <c>cache_control</c> 을 실어 우리가 적중을 지시한다. 지금은 Anthropic 뿐이다.
    /// 그 외(Gemini·OpenAI 호환 경유 포함)는 암시적이라 적중 판정이 제공사 몫이고 비결정적이다.
    ///
    /// <b>모르는 엔진은 암시적으로 본다</b> — 높은 하한을 잘못 씌워 통과시키는 것보다
    /// 낮은 하한으로 실측을 남기는 편이 안전하다.
    /// </summary>
    private static bool UsesExplicitCaching(string model) =>
        model.Contains("claude", StringComparison.OrdinalIgnoreCase)
        || model.Contains("anthropic", StringComparison.OrdinalIgnoreCase);

    // ── 5. 시나리오 A(7게임일) 캐시 히트율 ≥ 98% ──────────────────

    /// <summary>
    /// 항목 5 — 95% 채운 스토어로 게임 7일을 돌려도 런타임 캐시 히트율이 98% 이상이다.
    ///
    /// <b>95% 로 채우는 것이 핵심이다.</b> 전량 채운 스토어로 재면 히트율은 정의상 100% 이고
    /// 아무것도 증명하지 못한다. 비워 두는 5% 는 <c>prebake_priority</c> 가 뒤로 미룬 꼬리이므로,
    /// 이 테스트는 "우선순위 정렬이 실제로 덜 쓰이는 버킷을 뒤로 보내는가"까지 같이 잰다.
    /// </summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Gate_ScenarioAKeepsHitRateAbove98Percent()
    {
        string store = Phase3Fixture.NewTempStore();

        try
        {
            Phase3Fixture.WriteStore(store, s_data, count: (int)(TestPaths.TotalKeys * FixtureFillRate), writeManifest: true);

            await using NpcHost host = NpcHost.Create(
                Options(
                    store,
                    "--loopback", "--npcs", "500", "--time-scale", "600", "--days", "7",
                    "--player-bots", "20", "--no-llm", "--max-speed", "--no-dashboard"),
                TextWriter.Null);

            await host.RunAsync(CancellationToken.None);

            MetricsSnapshot m = host.Metrics.Snapshot();

            Assert.Equal(7, m.Tick.GameDay);
            Assert.True(m.Cache.Hits + m.Cache.Misses > 0, "버킷 조회가 한 번도 일어나지 않았다.");

            output.WriteLine(
                $"시나리오 A: 히트율 {m.Cache.HitRate:P2} · 히트 {m.Cache.Hits} · 미스 {m.Cache.Misses} · "
                + $"채운 버킷 {m.Cache.FilledBuckets} · 콜드 {m.Cache.ColdBuckets} · 틱 {m.Tick.Ticks}");

            Assert.True(
                m.Cache.HitRate >= MinRuntimeHitRate,
                $"캐시 히트율 {m.Cache.HitRate:P2} — 하한 {MinRuntimeHitRate:P0}. "
                + $"히트 {m.Cache.Hits} · 미스 {m.Cache.Misses} · 콜드 버킷 {m.Cache.ColdBuckets}. "
                + $"미스 상위: {string.Join(", ", m.Cache.TopMisses.AsEnumerable().Take(3).Select(t => t.Bucket))}");

            // 개별 재계획이 과다하면 히트율이 떨어진다 (docs/13 §6 의 원인 표).
            Assert.Equal(0, m.Cache.IndividualTurnover);
        }
        finally
        {
            Phase3Fixture.Delete(store);
        }
    }

    // ── 6. pinned 가 재프리베이크로 덮어써지지 않는다 ───────────────

    /// <summary>
    /// 항목 6 — 파일 계층까지 포함해 pinned 가 살아남는다.
    /// 저장 → 재프리베이크 → 다시 로드를 한 바퀴 돌린다.
    /// </summary>
    [Fact]
    public void Gate_PinnedSurvivesRebake()
    {
        string store = Phase3Fixture.NewTempStore();

        try
        {
            Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));

            var pinned = new BucketKey(smith.Code, TimeOfDay.Evening, RegionState.War, Climate.Cold);
            var plain = new BucketKey(smith.Code, TimeOfDay.Dawn, RegionState.Peace, Climate.Fair);

            // 1차 프리베이크 + 사람이 한 건을 고쳐 pinned 로 올린다.
            PlanStoreIo.SavePlan(store, PlanLayer.Plans, plain, Phase3Fixture.PlanFor(plain, s_data, "v1"), s_data);
            PlanStoreIo.SavePlan(
                store, PlanLayer.Pinned, pinned,
                Phase3Fixture.PlanFor(pinned, s_data, "human_reviewed", PlanOrigin.Pinned), s_data);

            // 2차 프리베이크 — 로드 → 전 버킷 재생성 시도 → 저장.
            PlanStore reloaded = PlanStore.CreateIdleOnly(s_data);

            Phase3Fixture.RegisterFallbacks(reloaded, s_data);
            PlanStoreIo.LoadAll(store, reloaded, s_data);

            Assert.True(reloaded.IsPinned(pinned));

            for (int index = 0; index < TestPaths.TotalKeys; index++)
            {
                var bucket = BucketKey.FromIndex(index);

                reloaded.SetBucket(bucket, Phase3Fixture.PlanFor(bucket, s_data, "v2"));
            }

            PlanStoreIo.SaveAll(store, reloaded, s_data);

            // 사람이 고친 것은 그대로다.
            Assert.Equal("human_reviewed", reloaded.PeekBucket(pinned)!.Goal);
            Assert.Equal("v2", reloaded.PeekBucket(plain)!.Goal);

            // 파일에서도 그대로다. pinned/ 는 저장 대상이 아니라 손대지 않는다.
            PlanStore third = PlanStore.CreateIdleOnly(s_data);

            PlanStoreIo.LoadAll(store, third, s_data);

            Assert.Equal("human_reviewed", third.PeekBucket(pinned)!.Goal);
            Assert.True(third.IsPinned(pinned));
            Assert.Equal(1, third.PinnedBuckets);

            // pinned 버킷은 plans/ 에도 안 생긴다 — 두 계층에 같은 버킷이 있으면 검수본이 헷갈린다.
            Assert.False(File.Exists(PlanStoreIo.PathOf(store, PlanLayer.Plans, pinned, s_data)));
        }
        finally
        {
            Phase3Fixture.Delete(store);
        }
    }

    // ── 7. POI 1개 추가 시 Partial 무효화 ─────────────────────────

    /// <summary>
    /// 항목 7 — POI 하나가 추가되면 Partial 이고 전량 재생성하지 않는다.
    ///
    /// 여기가 틀리면 POI 하나 추가마다 2,880건 · 3분 · $0.5 가 나간다 (docs/13 §3).
    /// </summary>
    [Fact]
    public void Gate_AddingOnePoiTriggersPartialNotFull()
    {
        PromptPrefix prefix = PromptPrefix.Build(s_data, TestPaths.MasterData);

        // pois.json 만 바뀐 manifest — POI 를 하나 추가한 상황이다.
        Manifest before = Manifest.For(
            s_data, prefix.Sha256, new ManifestGeneratedBy("T2", "test", 0.4));

        Manifest stale = before with
        {
            FileHashes = [.. before.FileHashes.AsEnumerable().Select(h =>
                h.FileName == "pois.json" ? new FileHash(h.FileName, "옛날해시") : h)],
            MasterdataHash = "옛날콘텐츠해시",
        };

        InvalidationScope scope = PlanStoreValidator.Compare(
            stale, s_data, prefix.Sha256, out ImmutableArray<string> changed);

        Assert.Equal(InvalidationScope.Partial, scope);
        Assert.Equal(["pois.json"], changed.ToArray());

        // 전량 채워진 스토어에서 Partial 대상은 0 이다 — 기존 플랜이 유효하므로 다시 만들지 않는다.
        PlanStore full = PlanStore.CreateIdleOnly(s_data);

        for (int index = 0; index < TestPaths.TotalKeys; index++)
        {
            full.SetBucket(BucketKey.FromIndex(index), Phase3Fixture.PlanFor(BucketKey.FromIndex(index), s_data));
        }

        Assert.True(PrebakeOptions.TryParse([], out PrebakeOptions options, out string? error), error);

        TargetSelection targets = TargetSelector.Select(options, s_data, full, scope);

        Assert.Equal(TargetMode.Partial, targets.Mode);
        Assert.Equal(0, targets.Count);

        // 폴백으로 메운 버킷이 있으면 그것만 다시 던진다 — 전량이 아니다.
        PlanStore partial = PlanStore.CreateIdleOnly(s_data);

        for (int index = 0; index < TestPaths.TotalKeys; index++)
        {
            var bucket = BucketKey.FromIndex(index);

            partial.SetBucket(
                bucket,
                Phase3Fixture.PlanFor(
                    bucket, s_data, "goal", index < 100 ? PlanOrigin.Fallback : PlanOrigin.Prebaked));
        }

        TargetSelection retry = TargetSelector.Select(options, s_data, partial, scope);

        Assert.Equal(100, retry.Count);
        Assert.True(retry.Count < TestPaths.TotalKeys, "Partial 이 전량 재생성으로 번졌다.");
    }

    // ── 8. --budget-usd 초과 시 중단 + --resume 재개 ───────────────

    /// <summary>
    /// 항목 8 — 예산 캡에 걸리면 중단하고, manifest 가 부분 상태로 남고, resume 이 이어받는다.
    /// </summary>
    [Fact]
    public void Gate_BudgetCapStopsAndResumeContinues()
    {
        // 캡을 넘기면 예약이 거절된다.
        var guard = new BudgetGuard(capUsd: 0.001, estimatePerCallUsd: 0.0005);

        Assert.True(guard.TryReserve(2));
        guard.Record(0.0005);
        guard.Record(0.0005);

        Assert.False(guard.TryReserve(2));
        Assert.True(guard.Exhausted);
        Assert.Contains("--resume", guard.StopMessage(4, TestPaths.TotalKeys), StringComparison.Ordinal);

        // 중단된 회차의 manifest 는 부분 상태다.
        var stopped = new BulkRunReport([], 12.0, 0, 8, 0, 8, StoppedByBudget: true, Attempted: 0);

        Manifest manifest = ManifestWriter.Build(
            s_data,
            PromptPrefix.Build(s_data, TestPaths.MasterData),
            LlmOptions.LoadDefault(TestPaths.RepoRoot).Engine("openrouter-gemini-2.5-flash-lite"),
            "T2",
            stopped,
            new DryRunReport([], 0, 0, 0.01, 16),
            PlanStore.CreateIdleOnly(s_data),
            "2026-07-26T09:00:00Z");

        Assert.True(manifest.Partial);

        // resume 은 미생성 버킷만 고른다.
        PlanStore half = PlanStore.CreateIdleOnly(s_data);

        for (int index = 0; index < 1_000; index++)
        {
            half.SetBucket(BucketKey.FromIndex(index), Phase3Fixture.PlanFor(BucketKey.FromIndex(index), s_data));
        }

        Assert.True(PrebakeOptions.TryParse(["--resume"], out PrebakeOptions options, out string? error), error);

        TargetSelection resume = TargetSelector.Select(options, s_data, half, InvalidationScope.Full);

        Assert.Equal(TargetMode.Resume, resume.Mode);
        Assert.Equal(TestPaths.TotalKeys - 1_000, resume.Count);
    }

    // ── 9. 검수 40건 채택률 ≥ 80% ─────────────────────────────────

    /// <summary>
    /// 항목 9 — 검수 40건의 채택률(accept + edit)이 80% 이상이다.
    ///
    /// <b>이 검수는 사람이 한다.</b> <c>npc review</c> (F-06)가 만드는
    /// <c>docs/measurements/review_W8.jsonl</c> 이 근거이고, 여기서 나오는 채택률과 검수 시간이
    /// R&amp;D 결론의 원자료다 (docs/13 §5).
    /// </summary>
    [Fact]
    [Trait("Category", "Gate")]
    public void Gate_ReviewAdoptionRateIsAtLeast80Percent()
    {
        string path = TestPaths.At("docs", "measurements", "review_W8.jsonl");

        Assert.True(
            File.Exists(path),
            $"검수 기록이 없다: {path}. 사람이 `npc review --sample 40` 으로 40건을 검수해야 한다 (F-06).");

        string[] lines = [.. File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l))];

        Assert.True(
            lines.Length >= ReviewSampleSize,
            $"검수 기록이 {lines.Length}행이다. 표본은 {ReviewSampleSize}건이다 (docs/13 §5).");

        int accept = 0;
        int edit = 0;
        int reject = 0;
        double minutes = 0;

        foreach (string line in lines)
        {
            using JsonDocument entry = JsonDocument.Parse(line);
            JsonElement root = entry.RootElement;

            Assert.True(root.TryGetProperty("bucket", out JsonElement bucket), line);
            Assert.True(root.TryGetProperty("verdict", out JsonElement verdict), line);
            Assert.True(root.TryGetProperty("minutes", out JsonElement m), line);

            Assert.False(string.IsNullOrWhiteSpace(bucket.GetString()), line);

            // 버킷 이름이 실제 버킷이어야 한다 — 오타면 그 판정이 어디에 속하는지 알 수 없다.
            Assert.True(
                PlanStoreIo.TryParseBucket(bucket.GetString()!, s_data, out _),
                $"버킷 이름을 읽을 수 없다: {bucket.GetString()}");

            switch (verdict.GetString())
            {
                case "accept":
                    accept++;
                    break;

                case "edit":
                    edit++;
                    break;

                case "reject":
                    reject++;
                    break;

                default:
                    Assert.Fail($"verdict 는 accept/edit/reject 중 하나다: {verdict.GetString()}");
                    break;
            }

            minutes += m.GetDouble();
        }

        int total = accept + edit + reject;
        double rate = (double)(accept + edit) / total;

        Assert.True(
            rate >= MinAdoptionRate,
            string.Create(
                CultureInfo.InvariantCulture,
                $"채택률 {rate:P1} — 하한 {MinAdoptionRate:P0}. "
                + $"채택 {accept} · 수정후채택 {edit} · 폐기 {reject} / {total}"));

        // 검수 시간이 기록돼 있어야 절감률을 계산할 수 있다 (docs/13 §5).
        Assert.True(minutes > 0, "검수 시간이 전부 0 이다. 절감률을 계산할 수 없다.");
    }

    // ── 헬퍼 ────────────────────────────────────────────────────

    /// <summary>
    /// 실측 산출물을 요구한다. 없으면 <b>건너뛴다</b> — 없는 것을 통과로 세면 게이트가 거짓이 된다.
    /// </summary>
    private static Manifest RequireManifest()
    {
        string store = TestPaths.At("planstore");
        Manifest? manifest = Manifest.LoadFrom(store);

        Assert.True(
            manifest is not null,
            $"{Path.Combine(store, Manifest.FileName)} 이 없다. "
            + "tools/Npc.Prebake 로 전량 회차를 돌려야 이 항목을 판정할 수 있다.");

        return manifest!;
    }

    /// <summary>
    /// <b>선언한 대상 집합을 완주한</b> 회차의 manifest 를 요구한다 (2026-07-28 결정 15-A).
    ///
    /// <para>
    /// 원래는 <c>생성/2,880 ≥ 95%</c> 를 요구했다. 그 방어의 목적은
    /// "288버킷 파일럿의 115초를 상한 300초 통과로 세지 않는다" 였고 <b>그 목적은 지금도 옳다.</b>
    /// 다만 판정선을 전량 2,880 에 묶어 두면, 도달 집합만 만드는 방침
    /// (<c>RnD_Report</c> 권고 2)에서는 <b>영원히 판정할 수 없다.</b>
    /// </para>
    ///
    /// <para>
    /// 그래서 묻는 것을 바꾼다 — <b>"이 회차가 만들기로 한 것을 다 만들었는가"</b> 다.
    /// 예산 캡에 걸려 중단된 회차(<see cref="Manifest.Partial"/>)는 여전히 거절한다.
    /// </para>
    ///
    /// <para>
    /// <b>대신 wall-clock·비용을 범위 없이 읽지 않게 한다.</b> 이 헬퍼를 통과한 값은
    /// 항상 <c>counts.target</c> 과 같이 보고된다 — "97초" 는 264버킷의 97초이지
    /// 2,880버킷의 97초가 아니다. 그 문맥을 잃으면 게이트가 다시 거짓이 된다.
    /// </para>
    /// </summary>
    private static Manifest RequireCompleteRun(Manifest manifest)
    {
        Assert.True(
            manifest.Counts.Target > 0,
            "manifest 에 counts.target 이 없다 — 2026-07-28 이전 스키마다. "
            + "프리베이크를 다시 돌려야 이 항목을 판정할 수 있다 (docs/03 §7).");

        Assert.False(
            manifest.Partial,
            $"예산 캡에 걸려 중단된 회차다 (대상 {manifest.Counts.Target}버킷). "
            + "끝까지 돈 회차가 아니면 wall-clock·비용은 그 범위의 값이 아니다.");

        return manifest;
    }

    /// <summary>측정치에 항상 붙이는 범위 꼬리표. 이걸 빼면 숫자가 문맥을 잃는다.</summary>
    private static string Scope(Manifest manifest) =>
        $"[대상 {manifest.Counts.Target}버킷 · 스토어 {manifest.Counts.Generated}/{manifest.Counts.Total}]";
}

/// <summary>
/// P3 게이트·결선 테스트가 쓰는 플랜 스토어 지그.
///
/// 폴백 플랜을 버킷 플랜으로 복제해 스토어를 채운다 — <b>LLM 을 부르지 않는다.</b>
/// 폴백은 V7 이 기동 시점에 4단 통과를 보장하므로 "유효한 프리베이크 산출물"의 대역으로 쓸 수 있다.
/// </summary>
public static class Phase3Fixture
{
    /// <summary>아키타입 폴백 40개를 등록한다. 호스트 조립이 하는 것과 같다.</summary>
    public static void RegisterFallbacks(PlanStore store, MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(data);

        if (data.Fallbacks is not { } table)
        {
            return;
        }

        foreach (FallbackPlanEntry entry in table.Plans)
        {
            store.SetFallback(entry.Archetype, store.Register(entry.Plan));
        }
    }

    /// <summary>이 버킷에 쓸 플랜. 아키타입 폴백을 대상 버킷으로 다시 표시한 것이다.</summary>
    public static CompiledPlan PlanFor(
        BucketKey bucket,
        MasterDataSet data,
        string? goal = null,
        PlanOrigin origin = PlanOrigin.Prebaked)
    {
        ArgumentNullException.ThrowIfNull(data);

        CompiledPlan source = data.Fallbacks?.For(bucket.A)
            ?? throw new InvalidOperationException($"아키타입 {bucket.A.Value} 의 폴백이 없다.");

        CompiledPlan plan = source with { Bucket = bucket, Origin = origin, Id = new PlanId(0) };

        if (goal is null)
        {
            return plan;
        }

        // goal 만 바꾸면 SourceJson 의 goal 과 어긋난다. PlanStoreIo.Serialize 는 SourceJson 이 있으면
        // 그것을 그대로 담으므로(route 경유지를 잃지 않기 위해) 여기서 비워 재생성시킨다.
        return plan with { Goal = goal, SourceJson = string.Empty };
    }

    /// <summary>임시 스토어 경로.</summary>
    public static string NewTempStore() =>
        Path.Combine(Path.GetTempPath(), "npc-p3-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>2,880 버킷을 전부 채운 스토어를 쓴다.</summary>
    public static void WriteFullStore(string directory, MasterDataSet data) =>
        WriteStore(directory, data, TestPaths.TotalKeys, writeManifest: true);

    /// <summary>
    /// 우선순위 순으로 <paramref name="count"/> 개를 채운 스토어를 쓴다.
    /// 남는 버킷은 <c>prebake_priority</c> 가 뒤로 미룬 꼬리다.
    /// </summary>
    public static void WriteStore(string directory, MasterDataSet data, int count, bool writeManifest)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(data);

        PlanStore store = PlanStore.CreateIdleOnly(data);
        ImmutableArray<BucketKey> order = TargetSelector.SortByPriority(TargetSelector.All(data.Buckets), data);

        for (int i = 0; i < Math.Min(count, order.Length); i++)
        {
            store.SetBucket(order[i], PlanFor(order[i], data));
        }

        PlanStoreIo.SaveAll(directory, store, data);

        if (!writeManifest)
        {
            return;
        }

        PromptPrefix prefix = PromptPrefix.Build(data, TestPaths.MasterData);

        Manifest manifest = Manifest.For(
            data, prefix.Sha256, new ManifestGeneratedBy("T2", "fixture", 0.4, 8, 8, 0), string.Empty) with
        {
            Counts = new ManifestCounts(TestPaths.TotalKeys, store.FilledBuckets, 0, 0),
            Validation = new ManifestValidation(store.FilledBuckets, 0, 0, 0, 0),
            CacheHitRate = 0.96,
            WallClockSeconds = 180,
            CostUsd = 0.5,
        };

        manifest.Save(Path.Combine(directory, Manifest.FileName));
    }

    /// <summary>임시 스토어를 지운다.</summary>
    public static void Delete(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
