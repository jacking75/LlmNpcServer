using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;
using Npc.Sim.Validation;

namespace Npc.Tests.Golden;

/// <summary>
/// 한 픽스처에 대해 만들어진 플랜 하나. 생성이 실패했으면 <see cref="Error"/> 만 채워진다.
/// </summary>
/// <param name="Plan">컴파일된 플랜. 생성·검증·컴파일 중 어디서든 실패하면 null.</param>
/// <param name="Json">모델이 뱉은 원문. <c>validates</c> 의 1단이 본다.</param>
/// <param name="Error">생성 자체가 실패한 사유. 성공이면 null.</param>
public readonly record struct GoldenPlan(CompiledPlan? Plan, string Json, string? Error)
{
    /// <summary>플랜을 만들지 못했다.</summary>
    public static GoldenPlan Failed(string error, string json = "") => new(null, json, error);
}

/// <summary>단언 하나의 판정 한 줄.</summary>
/// <param name="Kind">단언 종류.</param>
/// <param name="Label"><c>contains_action(Craft)</c> 처럼 인자까지 적은 표기.</param>
/// <param name="Passed">통과했는가.</param>
/// <param name="Detail">실패 사유.</param>
public readonly record struct AssertionReport(string Kind, string Label, bool Passed, string Detail);

/// <summary>픽스처 하나의 리포트.</summary>
public sealed record FixtureReport
{
    /// <summary>판정된 픽스처.</summary>
    public required GoldenFixture Fixture { get; init; }

    /// <summary>픽스처가 선언한 순서 그대로의 단언 판정.</summary>
    public required ImmutableArray<AssertionReport> Assertions { get; init; }

    /// <summary>플랜의 액션 시퀀스. 실패 리포트를 읽을 때 제일 먼저 보게 되는 줄이다.</summary>
    public string ActionSignature { get; init; } = string.Empty;

    /// <summary>플랜 생성 자체가 실패했으면 그 사유.</summary>
    public string? Error { get; init; }

    /// <summary>통과한 단언 수.</summary>
    public int PassedCount => Assertions.Count(a => a.Passed);

    /// <summary>단언 수.</summary>
    public int Total => Assertions.Length;

    /// <summary>단언이 전부 통과했는가. 단언이 하나도 없으면 통과로 보지 않는다.</summary>
    public bool Passed => Error is null && Total > 0 && PassedCount == Total;
}

/// <summary>픽스처 전량의 리포트.</summary>
public sealed record GoldenReport
{
    /// <summary>픽스처별 리포트. 픽스처 id 순서.</summary>
    public required ImmutableArray<FixtureReport> Fixtures { get; init; }

    /// <summary>단언 총수.</summary>
    public int TotalAssertions => Fixtures.Sum(f => f.Total);

    /// <summary>통과한 단언 수.</summary>
    public int PassedAssertions => Fixtures.Sum(f => f.PassedCount);

    /// <summary>단언 단위 합격률.</summary>
    public double AssertionPassRate => TotalAssertions == 0 ? 0 : (double)PassedAssertions / TotalAssertions;

    /// <summary>전 단언을 통과한 픽스처 수.</summary>
    public int PassedFixtures => Fixtures.Count(f => f.Passed);

    /// <summary>픽스처 단위 합격률. <b>docs/15 §2 의 "합격률 ≥ 90%" 는 이 값이다.</b></summary>
    public double FixturePassRate => Fixtures.Length == 0 ? 0 : (double)PassedFixtures / Fixtures.Length;

    /// <summary>
    /// 사람이 읽는 리포트. 통과한 픽스처는 한 줄, 실패한 픽스처는 실패한 단언까지 적는다 —
    /// 전부 적으면 50건 × 8단언에서 읽히지 않는다.
    /// </summary>
    public string Format()
    {
        var sb = new StringBuilder(8 * 1024);

        sb.Append(CultureInfo.InvariantCulture, $"픽스처 {PassedFixtures}/{Fixtures.Length} ({FixturePassRate:P1}) · ");
        sb.Append(CultureInfo.InvariantCulture, $"단언 {PassedAssertions}/{TotalAssertions} ({AssertionPassRate:P1})");
        sb.Append('\n');

        foreach (FixtureReport report in Fixtures)
        {
            if (report.Passed)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  [ok]   {report.Fixture.Id} {report.Fixture.BucketText}\n");
                continue;
            }

            sb.Append(CultureInfo.InvariantCulture,
                $"  [FAIL] {report.Fixture.Id} {report.Fixture.BucketText} ({report.PassedCount}/{report.Total})\n");

            if (report.Error is { } error)
            {
                sb.Append(CultureInfo.InvariantCulture, $"           플랜 생성 실패: {error}\n");
                continue;
            }

            sb.Append(CultureInfo.InvariantCulture, $"           {report.ActionSignature}\n");

            foreach (AssertionReport assertion in report.Assertions)
            {
                if (!assertion.Passed)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"           - {assertion.Label}: {assertion.Detail}\n");
                }
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// 픽스처 JSON → 단언 실행 → 결과 리포트. docs/15 T5-02.
///
/// <b>플랜을 만들지 않는다.</b> 만드는 쪽(LLM 호출)은 골든 러너(T5-04)의 일이고,
/// 이 타입은 이미 만들어진 플랜을 받아 판정만 한다 — 그래야 LLM 없이도
/// 평가기 자체를 테스트할 수 있다.
///
/// <c>differs_from</c> 이 다른 버킷의 플랜을 필요로 하므로 버킷별 플랜을
/// <see cref="Publish"/> 로 등록해 둔다. 등록되지 않은 버킷을 가리키는 단언은
/// 통과가 아니라 <b>실패</b>다 (비교하지 않은 것을 다양성으로 세면 지표가 거짓이 된다).
/// </summary>
public sealed class AssertionEvaluator
{
    private readonly MasterDataSet _data;
    private readonly IDryRunValidator _dryRun;

    // 조회만 한다 — 순회하지 않으므로 순서 비결정성이 끼어들 자리가 없다.
    private readonly Dictionary<BucketKey, CompiledPlan> _byBucket = [];

    /// <summary>평가기 하나. 4단 검증기는 <c>Npc.Sim</c> 구현체를 넣는다.</summary>
    public AssertionEvaluator(MasterDataSet data, IDryRunValidator dryRun)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _dryRun = dryRun ?? throw new ArgumentNullException(nameof(dryRun));
    }

    /// <summary>마스터데이터에서 기본 평가기를 만든다 (4단은 <see cref="DryRunValidator"/>).</summary>
    public static AssertionEvaluator Create(MasterDataSet data) => new(data, new DryRunValidator(data));

    /// <summary><c>differs_from</c> 이 볼 수 있게 버킷별 플랜을 등록한다. 같은 버킷은 덮어쓴다.</summary>
    public void Publish(BucketKey bucket, CompiledPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        _byBucket[bucket] = plan;
    }

    /// <summary>등록된 플랜을 전부 지운다. 회차 사이에 부른다.</summary>
    public void Clear() => _byBucket.Clear();

    /// <summary>
    /// JSON 한 덩어리를 이 픽스처의 플랜으로 컴파일한다.
    /// 스키마 실패·컴파일 실패는 예외가 아니라 <see cref="GoldenPlan.Error"/> 로 돌려준다 —
    /// 골든 러너는 50건을 끝까지 돌아야 하고, 한 건이 던지면 나머지 49건의 결과가 사라진다.
    /// </summary>
    public GoldenPlan CompileFrom(GoldenFixture fixture, string json)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        ValidationResult schema = SchemaValidator.Validate(json, out PlanDocument? document);

        if (!schema.IsValid || document is null)
        {
            return GoldenPlan.Failed($"{schema.Code} {schema.Detail}", json);
        }

        try
        {
            CompiledPlan plan = PlanCompiler.Compile(
                document, fixture.Bucket, default, _data, PlanOrigin.Runtime, 1, json);

            return new GoldenPlan(plan, json, null);
        }
        catch (PlanCompilationException e)
        {
            return GoldenPlan.Failed(
                string.Create(CultureInfo.InvariantCulture, $"컴파일 실패 step{e.StepIndex}: {e.Message}"), json);
        }
    }

    /// <summary>픽스처 하나를 판정한다.</summary>
    public FixtureReport Evaluate(GoldenFixture fixture, in GoldenPlan produced)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (produced.Plan is not { } plan)
        {
            // 생성 실패도 단언 실패로 센다. 분모에서 빼면 통과율이 조용히 올라간다 (docs/12 §2).
            string reason = produced.Error ?? "플랜이 없다.";

            return new FixtureReport
            {
                Fixture = fixture,
                Assertions = [.. fixture.Assertions.Select(
                    a => new AssertionReport(a.Kind, a.Describe(), false, reason))],
                Error = reason,
            };
        }

        var subject = new GoldenSubject(
            fixture, plan, produced.Json, _data, _dryRun,
            bucket => _byBucket.TryGetValue(bucket, out CompiledPlan? companion) ? companion : null);

        var reports = ImmutableArray.CreateBuilder<AssertionReport>(fixture.Assertions.Length);

        foreach (GoldenAssertionSpec spec in fixture.Assertions)
        {
            AssertionOutcome outcome = GoldenAssertions.Evaluate(spec, subject);

            reports.Add(new AssertionReport(spec.Kind, spec.Describe(), outcome.Passed, outcome.Detail));
        }

        return new FixtureReport
        {
            Fixture = fixture,
            Assertions = reports.ToImmutable(),
            ActionSignature = subject.ActionSignature(),
        };
    }

    /// <summary>
    /// 픽스처 전량을 판정한다. <b>플랜을 먼저 전부 만들고 나서 판정한다</b> —
    /// <c>differs_from</c> 이 뒤 픽스처의 플랜을 가리킬 수 있어서, 만들면서 판정하면
    /// 앞쪽 픽스처만 비교 대상을 못 찾는다.
    /// </summary>
    public GoldenReport EvaluateAll(
        IReadOnlyList<GoldenFixture> fixtures, Func<GoldenFixture, GoldenPlan> produce)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        ArgumentNullException.ThrowIfNull(produce);

        var produced = new GoldenPlan[fixtures.Count];

        for (int i = 0; i < fixtures.Count; i++)
        {
            produced[i] = produce(fixtures[i]);

            if (produced[i].Plan is { } plan)
            {
                Publish(fixtures[i].Bucket, plan);
            }
        }

        var reports = ImmutableArray.CreateBuilder<FixtureReport>(fixtures.Count);

        for (int i = 0; i < fixtures.Count; i++)
        {
            reports.Add(Evaluate(fixtures[i], produced[i]));
        }

        return new GoldenReport { Fixtures = reports.ToImmutable() };
    }
}

/// <summary>
/// T5-02 완료 조건 — 픽스처 JSON → 단언 실행 → 결과 리포트.
///
/// <b>LLM 을 부르지 않는다.</b> 플랜 공급원으로 사람이 쓴 폴백 플랜(<c>fallback_plans.json</c>)을
/// 쓴다 — 실제로 도는 경로를 그대로 밟으면서도 비용도 변동성도 없다.
/// </summary>
public sealed class AssertionEvaluatorTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private const string TwoAssertions = """
        {
          "id": "G-001",
          "input": {
            "bucket": "blacksmith@Morning.Peace.Fair",
            "flags": ["AtWorkplace", "HasTool"]
          },
          "assertions": [
            { "kind": "step_count_between", "min": 3, "max": 10 },
            { "kind": "contains_action", "action": "Pray" }
          ]
        }
        """;

    [Fact]
    public void Evaluate_ReportsEachAssertionSeparately()
    {
        AssertionEvaluator evaluator = AssertionEvaluator.Create(s_data);
        GoldenFixture fixture = Fixture(TwoAssertions);
        GoldenPlan plan = evaluator.CompileFrom(fixture, SmithJson);

        FixtureReport report = evaluator.Evaluate(fixture, plan);

        Assert.Null(report.Error);
        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.PassedCount);
        Assert.False(report.Passed);

        Assert.True(report.Assertions[0].Passed);
        Assert.False(report.Assertions[1].Passed);
        Assert.Equal("contains_action(Pray)", report.Assertions[1].Label);
        Assert.NotEmpty(report.ActionSignature);
    }

    [Fact]
    public void Evaluate_CountsGenerationFailureAsEveryAssertionFailing()
    {
        AssertionEvaluator evaluator = AssertionEvaluator.Create(s_data);
        GoldenFixture fixture = Fixture(TwoAssertions);

        FixtureReport report = evaluator.Evaluate(fixture, GoldenPlan.Failed("모델이 응답하지 않았다."));

        // 실패분을 분모에서 빼면 통과율이 조용히 올라간다.
        Assert.Equal(2, report.Total);
        Assert.Equal(0, report.PassedCount);
        Assert.Equal("모델이 응답하지 않았다.", report.Error);
    }

    [Fact]
    public void CompileFrom_TurnsBadJsonIntoAnError_NotAnException()
    {
        AssertionEvaluator evaluator = AssertionEvaluator.Create(s_data);

        GoldenPlan broken = evaluator.CompileFrom(Fixture(TwoAssertions), "{ this is not json");

        Assert.Null(broken.Plan);
        Assert.NotNull(broken.Error);
        Assert.StartsWith("V1.", broken.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileFrom_ReportsUnknownActionAsAnError()
    {
        AssertionEvaluator evaluator = AssertionEvaluator.Create(s_data);

        GoldenPlan broken = evaluator.CompileFrom(
            Fixture(TwoAssertions),
            SmithJson.Replace("\"Craft\"", "\"Excavate\"", StringComparison.Ordinal));

        Assert.Null(broken.Plan);
        Assert.Contains("Excavate", broken.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateAll_ResolvesDiffersFromAcrossFixtures()
    {
        AssertionEvaluator evaluator = AssertionEvaluator.Create(s_data);

        // 두 픽스처가 서로를 가리킨다. 먼저 전부 만들고 나서 판정해야 둘 다 비교 대상을 찾는다.
        GoldenFixture first = Fixture("""
            {
              "id": "G-A",
              "input": { "bucket": "blacksmith@Morning.Peace.Fair", "flags": ["AtWorkplace", "HasTool"] },
              "assertions": [ { "kind": "differs_from", "bucket": "blacksmith@Evening.War.Cold" } ]
            }
            """);

        GoldenFixture second = Fixture("""
            {
              "id": "G-B",
              "input": { "bucket": "blacksmith@Evening.War.Cold", "flags": ["AtWorkplace", "HasTool"] },
              "assertions": [ { "kind": "differs_from", "bucket": "blacksmith@Morning.Peace.Fair" } ]
            }
            """);

        GoldenReport different = evaluator.EvaluateAll(
            [first, second],
            f => evaluator.CompileFrom(f, f.Bucket.R == RegionState.War ? GuardJson : SmithJson));

        Assert.Equal(2, different.PassedFixtures);

        // 같은 플랜을 주면 둘 다 실패해야 한다.
        evaluator.Clear();

        GoldenReport same = evaluator.EvaluateAll([first, second], f => evaluator.CompileFrom(f, SmithJson));

        Assert.Equal(0, same.PassedFixtures);
        Assert.Contains("액션 시퀀스가 같다", same.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateAll_FailsDiffersFromWhenCompanionWasNeverProduced()
    {
        AssertionEvaluator evaluator = AssertionEvaluator.Create(s_data);

        GoldenFixture lonely = Fixture("""
            {
              "id": "G-C",
              "input": { "bucket": "blacksmith@Morning.Peace.Fair", "flags": ["AtWorkplace", "HasTool"] },
              "assertions": [ { "kind": "differs_from", "bucket": "baker@Night.Disaster.Storm" } ]
            }
            """);

        GoldenReport report = evaluator.EvaluateAll([lonely], f => evaluator.CompileFrom(f, SmithJson));

        Assert.Equal(0, report.PassedFixtures);
        Assert.Contains("구하지 못했다", report.Format(), StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ShowsOnlyFailedAssertions()
    {
        AssertionEvaluator evaluator = AssertionEvaluator.Create(s_data);
        GoldenFixture fixture = Fixture(TwoAssertions);

        string text = evaluator
            .EvaluateAll([fixture], f => evaluator.CompileFrom(f, SmithJson))
            .Format();

        Assert.Contains("[FAIL] G-001", text, StringComparison.Ordinal);
        Assert.Contains("contains_action(Pray)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("step_count_between", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 사람이 쓴 폴백 플랜 40개를 그대로 통과시켜 본다.
    /// <b>이게 평가기의 실질 회귀 테스트다</b> — 폴백은 로드 시점에 1~3단을 이미 통과했으므로,
    /// 여기서 <c>validates</c> 가 깨지면 평가기 쪽이 잘못된 것이다.
    /// </summary>
    [Fact]
    public void Evaluate_AcceptsEveryHandWrittenFallbackPlan()
    {
        PlanTable fallbacks = s_data.Fallbacks!;
        AssertionEvaluator evaluator = AssertionEvaluator.Create(s_data);

        Assert.Equal(BucketKey.ArchetypeCount, fallbacks.Count);

        var failures = new StringBuilder();

        foreach (FallbackPlanEntry entry in fallbacks.Plans)
        {
            var bucket = new BucketKey(entry.Archetype, TimeOfDay.Morning, RegionState.Peace, Climate.Fair);
            string archetype = s_data.Archetypes[entry.Archetype].Id;

            GoldenFixture fixture = Fixture($$"""
                {
                  "id": {{JsonSerializer.Serialize(entry.Id)}},
                  "input": { "bucket": "{{archetype}}@Morning.Peace.Fair" },
                  "assertions": [
                    { "kind": "validates", "stage": "Coherence" },
                    { "kind": "step_count_between", "min": 3, "max": 10 }
                  ]
                }
                """);

            // 폴백은 컴파일된 형태로만 들고 있다 — 문서로 되돌려 JSON 을 만든다.
            string json = JsonSerializer.Serialize(
                PlanCompiler.ToDocument(entry.Plan, s_data), PlanJsonContext.Default.PlanDocument);

            FixtureReport report = evaluator.Evaluate(fixture, evaluator.CompileFrom(fixture, json));

            Assert.Equal(bucket, fixture.Bucket);

            if (!report.Passed)
            {
                failures.Append(CultureInfo.InvariantCulture, $"{entry.Id}: ");
                failures.AppendLine(string.Join(
                    " · ", report.Assertions.Where(a => !a.Passed).Select(a => $"{a.Label} {a.Detail}")));
            }
        }

        Assert.True(failures.Length == 0, failures.ToString());
    }

    // ------------------------------------------------------------------ 헬퍼

    private static GoldenFixture Fixture(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        return GoldenFixture.Parse(document.RootElement, s_data, "inline");
    }

    private const string SmithJson = """
        {
          "schema": 1,
          "goal": "forge_and_store",
          "steps": [
            { "action": "MoveTo", "args": { "poi": "$workplace", "speed": "walk" } },
            { "action": "Withdraw", "args": { "item": "iron_ore", "count": 6 } },
            { "action": "Craft", "args": { "recipe": "iron_sword", "count": 2 } },
            { "action": "Store", "args": { "item": "iron_sword", "count": 2 } }
          ],
          "on_step_fail": "fallback",
          "loop": true
        }
        """;

    private const string GuardJson = """
        {
          "schema": 1,
          "goal": "hold_the_gate",
          "steps": [
            { "action": "MoveTo", "args": { "poi": "$gate", "speed": "run" } },
            { "action": "Observe", "args": { "target": "self", "duration_s": 600 } },
            { "action": "Wait", "args": { "duration_s": 600 } }
          ],
          "on_step_fail": "fallback",
          "loop": true
        }
        """;
}
