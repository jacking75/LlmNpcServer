using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Npc.Core;
using Npc.Llm;
using Npc.MasterData;
using Npc.Sim.Validation;
using Npc.Tests.Llm;
using Npc.Tests.Runtime;

namespace Npc.Tests.Golden;

/// <summary>
/// 골든 픽스처가 있는 폴더와, 회차 집계 규칙. 러너와 커버리지 테스트가 같이 쓴다.
/// </summary>
public static class GoldenSuite
{
    /// <summary><c>tests/golden/</c>.</summary>
    public static string Directory => TestPaths.At("tests", "golden");

    /// <summary>docs/15 §2 — 변동성을 흡수하려고 3회 생성한다.</summary>
    public const int Runs = 3;

    /// <summary>docs/15 §2 — 3회 중 이만큼 통과하면 그 단언은 합격이다.</summary>
    public const int RequiredPasses = 2;

    /// <summary>docs/15 §2 · §10 의 CI 게이트.</summary>
    public const double PassRateGate = 0.90;

    /// <summary>동시 요청 수. 32 는 실측 근거가 없어 8 로 둔다 (docs/13 §4 재검토).</summary>
    public const int Concurrency = 8;

    /// <summary>실측 산출물의 자리. CLAUDE.md §6 — 실측치는 커밋한다.</summary>
    public static string ResultPath => TestPaths.At("docs", "measurements", "W11_golden.md");

    /// <summary>마스터데이터. 픽스처 로딩과 검증이 같은 것을 본다.</summary>
    public static MasterDataSet Data { get; } = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>픽스처 전량 (파일명 정렬 순서).</summary>
    public static ImmutableArray<GoldenFixture> Fixtures { get; } = GoldenFixture.LoadAll(Directory, Data);
}

/// <summary>
/// T5-03 완료 조건 — 픽스처 50건의 커버리지.
///
/// <b>Golden 카테고리가 아니다.</b> LLM 을 부르지 않으므로 기본 CI 에서 항상 돈다 —
/// 픽스처가 깨지거나 커버리지가 무너지는 것은 LLM 없이도 알 수 있어야 한다.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class GoldenFixtureCoverageTests
{
    /// <summary>docs/15 T5-03 — 아키타입 20종 이상.</summary>
    private const int MinArchetypes = 20;

    /// <summary>docs/15 T5-03 — differs_from 을 최소 10건에 포함.</summary>
    private const int MinDiversityFixtures = 10;

    [Fact]
    public void Fixtures_AreFiftyAndCoverTheSituationSpace()
    {
        ImmutableArray<GoldenFixture> fixtures = GoldenSuite.Fixtures;

        Assert.Equal(50, fixtures.Length);
        Assert.Equal(fixtures.Length, fixtures.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count());

        Assert.True(
            fixtures.Select(f => f.Archetype).Distinct(StringComparer.Ordinal).Count() >= MinArchetypes,
            "아키타입 20종 이상을 덮어야 한다.");

        // 4개 지역상태 전부.
        Assert.Equal(BucketKey.RegionStateCount, fixtures.Select(f => f.Bucket.R).Distinct().Count());

        // 시간대·기후도 비어 있는 축이 없어야 고르게 덮은 것이다.
        Assert.Equal(BucketKey.TimeOfDayCount, fixtures.Select(f => f.Bucket.T).Distinct().Count());
        Assert.Equal(BucketKey.ClimateCount, fixtures.Select(f => f.Bucket.C).Distinct().Count());
    }

    [Fact]
    public void Fixtures_CarryDiversityAssertions()
    {
        GoldenFixture[] withDiffers = [.. GoldenSuite.Fixtures.Where(
            f => f.Assertions.Any(a => string.Equals(a.Kind, "differs_from", StringComparison.Ordinal)))];

        Assert.True(withDiffers.Length >= MinDiversityFixtures, $"differs_from 이 {withDiffers.Length}건뿐이다.");
    }

    /// <summary>
    /// <c>differs_from</c> 이 가리키는 버킷은 <b>이 묶음 안에 있어야 한다.</b>
    /// 없으면 그 단언은 영영 비교 대상을 못 찾고 항상 실패한다 —
    /// 픽스처를 옮기거나 지울 때 제일 먼저 깨지는 곳이다.
    /// </summary>
    [Fact]
    public void Fixtures_DiffersFromPointsInsideTheSuite()
    {
        var present = GoldenSuite.Fixtures.Select(f => f.Bucket).ToHashSet();

        foreach (GoldenFixture fixture in GoldenSuite.Fixtures)
        {
            foreach (GoldenAssertionSpec spec in fixture.Assertions)
            {
                if (!string.Equals(spec.Kind, "differs_from", StringComparison.Ordinal))
                {
                    continue;
                }

                (_, BucketKey other) = GoldenFixture.ParseBucket(spec.Bucket!, GoldenSuite.Data);

                Assert.True(present.Contains(other), $"{fixture.Id}: '{spec.Bucket}' 픽스처가 없다.");
                Assert.NotEqual(fixture.Bucket, other);
            }
        }
    }

    /// <summary>
    /// 픽스처가 선언한 플래그는 마스터데이터가 유도하는 것을 <b>포함해야 한다.</b>
    /// 시간대·지역상태·기후가 함의하는 플래그를 빼먹으면 그 픽스처는
    /// 있을 수 없는 상황을 요구하게 되고, 검증기가 반려할 플랜을 기대하게 된다.
    /// </summary>
    [Fact]
    public void Fixtures_FlagsAgreeWithMasterData()
    {
        foreach (GoldenFixture fixture in GoldenSuite.Fixtures)
        {
            WorldFlags derived = GoldenSuite.Data.InitialFlags(fixture.Bucket);

            Assert.True(
                (derived & ~fixture.Flags) == 0,
                $"{fixture.Id}: {WorldFlagTable.Format(derived & ~fixture.Flags)} 가 빠졌다.");
        }
    }

    /// <summary>모든 단언이 알려진 종류이고, 픽스처마다 <c>validates</c> 를 하나씩 갖는다.</summary>
    [Fact]
    public void Fixtures_EveryAssertionIsKnownAndValidatesIsAlwaysThere()
    {
        foreach (GoldenFixture fixture in GoldenSuite.Fixtures)
        {
            Assert.NotEmpty(fixture.Assertions);

            foreach (GoldenAssertionSpec spec in fixture.Assertions)
            {
                Assert.Contains(spec.Kind, GoldenAssertions.Kinds, StringComparer.Ordinal);
            }

            Assert.Contains(fixture.Assertions, a => string.Equals(a.Kind, "validates", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// <c>Category=Golden</c> 필터가 <b>양방향으로</b> 먹는지 확인한다 (docs/15 T5-04).
    ///
    /// 러너 자체로 확인하면 LLM 호출 비용이 든다. 그래서 같은 특성을 단
    /// <see cref="GoldenFilterCanary"/> 를 대신 돌린다 — 마커 파일 하나를 쓰는 것이 전부다.
    /// </summary>
    [Fact]
    [Trait("Category", "Determinism")]
    public void GoldenCategory_IsExcludedByTheDefaultCiFilterAndSelectedByItsOwn()
    {
        string marker = Path.Combine(Path.GetTempPath(), $"golden-canary-{Guid.NewGuid():N}.txt");

        try
        {
            // 1) 기본 CI 필터 — 돌면 안 된다.
            RunCanary("Category!=Golden&Category!=Gate", marker);
            Assert.False(File.Exists(marker), "Category!=Golden 인데 Golden 테스트가 실행됐다.");

            // 2) Golden 필터 — 돌아야 한다.
            RunCanary("Category=Golden", marker);
            Assert.True(File.Exists(marker), "Category=Golden 인데 Golden 테스트가 실행되지 않았다.");
        }
        finally
        {
            File.Delete(marker);
        }
    }

    private static void RunCanary(string category, string marker)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = TestPaths.RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        info.ArgumentList.Add("test");
        info.ArgumentList.Add(typeof(GoldenFilterCanary).Assembly.Location);
        info.ArgumentList.Add("--filter");
        info.ArgumentList.Add($"{category}&FullyQualifiedName~GoldenFilterCanary");
        info.Environment[GoldenFilterCanary.MarkerVariable] = marker;

        using Process process = Process.Start(info)
            ?? throw new InvalidOperationException("dotnet 을 실행하지 못했다.");

        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();

        Assert.True(process.WaitForExit(TimeSpan.FromMinutes(5)), "하위 dotnet test 가 5분 안에 끝나지 않았다.");
    }
}

/// <summary>
/// 필터 확인용 카나리아. <b>LLM 을 부르지 않는다</b> — 환경변수가 가리키는 파일 하나를 쓸 뿐이다.
/// 하위 프로세스로 돌 때만 쓰기가 일어나므로 평소 <c>Category=Golden</c> 실행에는 아무 영향이 없다.
/// </summary>
[Trait("Category", "Golden")]
public sealed class GoldenFilterCanary
{
    /// <summary>마커 파일 경로를 넘기는 환경변수.</summary>
    public const string MarkerVariable = "NPC_GOLDEN_CANARY";

    [Fact]
    public void Canary_WritesMarkerWhenSelected()
    {
        if (Environment.GetEnvironmentVariable(MarkerVariable) is { Length: > 0 } marker)
        {
            File.WriteAllText(marker, "ran", Encoding.UTF8);
        }

        Assert.True(true);
    }
}

/// <summary>
/// T5-04 골든 러너 — 픽스처 50건 × 3회 생성. docs/15 §2.
///
/// <b>비용이 발생한다.</b> CLAUDE.md §5 에 따라 Golden 카테고리이며 CI 기본 실행에서 빠진다.
///
/// 3회를 도는 이유는 LLM 출력이 확률적이기 때문이다. 1회로 판정하면 테스트가 산발적으로
/// 깨져서 아무도 안 믿게 된다. 집계는 <b>단언 단위</b>로 한다 —
/// docs/15 §2 의 "각 단언의 3회 중 2회 이상 통과를 합격으로 · 합격률 ≥ 90%" 가 그 뜻이다.
/// </summary>
[Trait("Category", "Golden")]
public sealed class GoldenRunner
{
    /// <summary>측정일을 밖에서 넣는다. 코드가 <c>DateTime.Now</c> 를 읽으면 산출물이 재현되지 않는다.</summary>
    public const string RunDateVariable = "NPC_GOLDEN_RUN_DATE";

    [Fact]
    public async Task Golden_FiftyFixturesPassAtLeastNinetyPercent()
    {
        LlmEngineOptions engine = LlmPlanCompilerTests.Options.PreferredEngine();

        // xunit 2.x 에는 동적 skip 이 없다. 키가 없으면 부를 수 없으므로 그냥 끝낸다 —
        // 이 카테고리는 어차피 CI 기본 실행에서 빠진다 (CLAUDE.md §5).
        if (!engine.IsConfigured)
        {
            return;
        }

        ImmutableArray<GoldenFixture> fixtures = GoldenSuite.Fixtures;
        var evaluator = AssertionEvaluator.Create(GoldenSuite.Data);
        var dryRun = new DryRunValidator(GoldenSuite.Data);

        using IChatClient client = ChatClientFactory.Create(engine);
        var compiler = new LlmPlanCompiler(
            GoldenSuite.Data, LlmPlanCompilerTests.Prefix, engine, client, dryRun: dryRun);

        var reports = new GoldenReport[GoldenSuite.Runs];
        var cost = new RunCost();

        for (int run = 0; run < GoldenSuite.Runs; run++)
        {
            ConcurrentDictionary<string, GoldenPlan> plans =
                await GenerateAsync(compiler, evaluator, fixtures, cost, CancellationToken.None);

            // 회차마다 비운다 — 지난 회차의 플랜이 differs_from 의 비교 대상으로 남으면 안 된다.
            evaluator.Clear();
            reports[run] = evaluator.EvaluateAll(fixtures, f => plans[f.Id]);
        }

        Aggregate aggregate = Summarise(fixtures, reports);

        File.WriteAllText(GoldenSuite.ResultPath, Render(engine, aggregate, cost, reports), new UTF8Encoding(false));

        Assert.True(
            aggregate.PassRate >= GoldenSuite.PassRateGate,
            string.Create(
                CultureInfo.InvariantCulture,
                $"단언 합격률 {aggregate.PassRate:P1} ({aggregate.Passed}/{aggregate.Total}) 로 "
                + $"{GoldenSuite.PassRateGate:P0} 미달이다.\n{aggregate.Detail}"));
    }

    // ------------------------------------------------------------------ 생성

    private static async Task<ConcurrentDictionary<string, GoldenPlan>> GenerateAsync(
        LlmPlanCompiler compiler,
        AssertionEvaluator evaluator,
        ImmutableArray<GoldenFixture> fixtures,
        RunCost cost,
        CancellationToken cancellationToken)
    {
        var plans = new ConcurrentDictionary<string, GoldenPlan>(StringComparer.Ordinal);

        await Parallel.ForEachAsync(
            fixtures,
            new ParallelOptions { MaxDegreeOfParallelism = GoldenSuite.Concurrency, CancellationToken = cancellationToken },
            async (fixture, token) =>
            {
                PlanCompileResult result = await compiler.CompileAsync(
                    new PlanRequest(fixture.Bucket, fixture.Flags), token);

                cost.Add(result.Stats);

                // 컴파일러가 검증에서 반려했어도 원문은 남는다. 그것을 다시 컴파일해서
                // validates 이외의 단언(스텝 수·마지막 액션 …)도 판정한다 —
                // 반려분을 통째로 버리면 어디가 나빴는지 알 길이 없다.
                plans[fixture.Id] = result.Stats.Error is { } error
                    ? GoldenPlan.Failed(error)
                    : evaluator.CompileFrom(fixture, result.ResponseText);
            });

        return plans;
    }

    // ------------------------------------------------------------------ 집계

    /// <summary>단언 하나의 3회 집계.</summary>
    private readonly record struct Cell(string FixtureId, string Label, int Passes, string FirstFailure)
    {
        public bool Accepted => Passes >= GoldenSuite.RequiredPasses;
    }

    private readonly record struct Aggregate(
        int Passed, int Total, double PassRate, ImmutableArray<Cell> Cells, string Detail);

    private static Aggregate Summarise(ImmutableArray<GoldenFixture> fixtures, GoldenReport[] reports)
    {
        var cells = ImmutableArray.CreateBuilder<Cell>();

        for (int f = 0; f < fixtures.Length; f++)
        {
            GoldenFixture fixture = fixtures[f];

            for (int a = 0; a < fixture.Assertions.Length; a++)
            {
                int passes = 0;
                string firstFailure = string.Empty;

                foreach (GoldenReport report in reports)
                {
                    AssertionReport judgement = report.Fixtures[f].Assertions[a];

                    if (judgement.Passed)
                    {
                        passes++;
                    }
                    else if (firstFailure.Length == 0)
                    {
                        firstFailure = judgement.Detail;
                    }
                }

                cells.Add(new Cell(fixture.Id, fixture.Assertions[a].Describe(), passes, firstFailure));
            }
        }

        ImmutableArray<Cell> all = cells.ToImmutable();
        int accepted = all.Count(c => c.Accepted);
        var detail = new StringBuilder();

        foreach (Cell cell in all.Where(c => !c.Accepted))
        {
            detail.Append(CultureInfo.InvariantCulture,
                $"  {cell.FixtureId} {cell.Label} ({cell.Passes}/{GoldenSuite.Runs}) {cell.FirstFailure}\n");
        }

        return new Aggregate(
            accepted,
            all.Length,
            all.Length == 0 ? 0 : (double)accepted / all.Length,
            all,
            detail.ToString());
    }

    // ------------------------------------------------------------------ 비용

    private sealed class RunCost
    {
        private readonly object _gate = new();

        public int Calls { get; private set; }

        public int PromptTokens { get; private set; }

        public int CachedTokens { get; private set; }

        public int CompletionTokens { get; private set; }

        public double CostUsd { get; private set; }

        public double LatencyMs { get; private set; }

        public int Errors { get; private set; }

        /// <summary>재시도까지 합친 누계. <c>CompileStats</c> 는 이미 시도를 누적해 온다.</summary>
        public void Add(in CompileStats stats)
        {
            lock (_gate)
            {
                Calls += Math.Max(1, stats.Attempt);
                PromptTokens += stats.PromptTokens;
                CachedTokens += stats.CachedTokens;
                CompletionTokens += stats.CompletionTokens;
                CostUsd += stats.CostUsd;
                LatencyMs += stats.LatencyMs;

                if (stats.Error is not null)
                {
                    Errors++;
                }
            }
        }
    }

    private static string Render(LlmEngineOptions engine, in Aggregate aggregate, RunCost cost, GoldenReport[] reports)
    {
        string date = Environment.GetEnvironmentVariable(RunDateVariable) is { Length: > 0 } given
            ? given
            : "(미기록 — " + RunDateVariable + " 환경변수로 넣는다)";

        var sb = new StringBuilder(16 * 1024);

        sb.Append("# W11 골든 회귀 — 실측\n\n");
        sb.Append("> `dotnet test --filter Category=Golden` 이 만든다. 손으로 고치지 않는다.\n\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 측정일 | {date} |\n|---|---|\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 엔진 | `{engine.Id}` ({engine.Model}) |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 픽스처 | {GoldenSuite.Fixtures.Length}건 |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 회차 | {GoldenSuite.Runs}회 (3회 중 {GoldenSuite.RequiredPasses}회 이상 통과 = 합격) |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 동시 요청 | {GoldenSuite.Concurrency} |\n\n");

        sb.Append("## 1. 판정\n\n");
        sb.Append("| 항목 | 값 | 기준 | 판정 |\n|---|---|---|---|\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"| 단언 합격률 | {aggregate.PassRate:P1} ({aggregate.Passed}/{aggregate.Total}) | ≥ {GoldenSuite.PassRateGate:P0} | "
            + $"{(aggregate.PassRate >= GoldenSuite.PassRateGate ? "통과" : "미달")} |\n");

        for (int run = 0; run < reports.Length; run++)
        {
            GoldenReport report = reports[run];

            sb.Append(CultureInfo.InvariantCulture,
                $"| {run + 1}회차 단언 통과 | {report.PassedAssertions}/{report.TotalAssertions} ({report.AssertionPassRate:P1}) | - | - |\n");
            sb.Append(CultureInfo.InvariantCulture,
                $"| {run + 1}회차 전단언 통과 픽스처 | {report.PassedFixtures}/{report.Fixtures.Length} ({report.FixturePassRate:P1}) | - | - |\n");
        }

        sb.Append("\n## 2. 비용\n\n");
        sb.Append("| 항목 | 값 |\n|---|---|\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 호출 수 (재시도 포함) | {cost.Calls} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 입력 토큰 | {cost.PromptTokens:N0} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 그중 캐시 적중 | {cost.CachedTokens:N0} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 출력 토큰 | {cost.CompletionTokens:N0} |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 비용 | ${cost.CostUsd:F4} |\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"| 평균 지연 | {(cost.Calls == 0 ? 0 : cost.LatencyMs / cost.Calls):F0} ms |\n");
        sb.Append(CultureInfo.InvariantCulture, $"| 호출 실패 | {cost.Errors} |\n");

        sb.Append("\n## 3. 합격하지 못한 단언\n\n");

        if (aggregate.Detail.Length == 0)
        {
            sb.Append("없다.\n");
        }
        else
        {
            sb.Append("```\n").Append(aggregate.Detail).Append("```\n");
        }

        sb.Append("\n## 4. 단언 종류별 합격률\n\n");
        sb.Append("| kind | 합격 | 전체 | 비율 |\n|---|---|---|---|\n");

        foreach (var group in aggregate.Cells
            .GroupBy(c => c.Label.Split('(')[0], StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            int accepted = group.Count(c => c.Accepted);

            sb.Append(CultureInfo.InvariantCulture,
                $"| {group.Key} | {accepted} | {group.Count()} | {(double)accepted / group.Count():P1} |\n");
        }

        return sb.ToString().ReplaceLineEndings("\n");
    }
}
