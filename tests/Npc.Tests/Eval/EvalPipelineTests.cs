using System.Collections.Immutable;
using Npc.Core;
using Npc.Eval;
using Npc.Eval.Core;
using Npc.Llm;
using Npc.MasterData;
using Npc.Prebake;
using Npc.Tests.Fakes;
using Npc.Tests.Llm;

namespace Npc.Tests.Eval;

/// <summary>
/// C-04 — 단일 평가 파이프라인.
///
/// <para>
/// <b>LLM 을 부르지 않는다.</b> 게이트 판정·다양성·보고서가 실제 호출을 필요로 하면
/// 그 로직은 영원히 릴리스 전에만 돌게 되고, 그때 처음 깨진다. 가짜 엔진으로 종단을 돌린다.
/// </para>
///
/// <para>
/// 여기서 지키는 것 — <b>표본은 엔진마다 같다</b> · <b>미판정은 통과가 아니다</b> ·
/// <b>보고서는 바이트 동일</b>(시각이 없다).
/// </para>
/// </summary>
public sealed class EvalPipelineTests
{
    private const string ValidPlan = """
        {"schema":1,"goal":"forge_batch","loop":true,"steps":[
          {"action":"MoveTo","args":{"poi":"$workplace"},"timeout_s":600},
          {"action":"Work","args":{"recipe":"iron_sword","count":1},"timeout_s":1800},
          {"action":"MoveTo","args":{"poi":"$home"},"timeout_s":600},
          {"action":"Sleep","args":{"until_time":"Morning"}}]}
        """;

    /// <summary>
    /// <b>표본은 결정론이고 엔진마다 같다.</b> 엔진별로 새로 뽑으면 통과율 차이가
    /// 모델 차이인지 표본 차이인지 구분되지 않는다.
    /// </summary>
    [Fact]
    public void Sample_IsDeterministicAndSpread()
    {
        BucketSpace space = LlmPlanCompilerTests.Data.Buckets;

        ImmutableArray<BucketKey> once = EvalRunner.Sample(space, 48);
        ImmutableArray<BucketKey> twice = EvalRunner.Sample(space, 48);

        Assert.Equal(48, once.Length);
        Assert.Equal([.. once], [.. twice]);
        Assert.Equal(48, once.Distinct().Count());

        // 앞에서 자르지 않는다 — 잘랐으면 아키타입이 한둘만 나온다.
        Assert.True(
            once.Select(b => b.A).Distinct().Count() > 1,
            "표본이 한 아키타입에 몰렸다 — 앞에서 자른 것이다");

        // 상한을 넘겨 달라고 해도 전체까지만.
        Assert.Equal(space.TotalKeys, EvalRunner.Sample(space, space.TotalKeys * 2).Length);
    }

    /// <summary>가짜 엔진으로 파이프라인 종단. <b>전부 통과하면 게이트도 통과한다.</b></summary>
    [Fact]
    public async Task Pipeline_RunsEndToEndWithAFakeEngine()
    {
        EngineRun run = await RunAsync(ValidPlan, buckets: 8);

        Assert.Equal(8, run.Attempted);
        Assert.Equal(8, run.Passed);
        Assert.Equal(1.0, run.PassRate);

        EngineEvaluation evaluation = EngineEvaluation.Of(run);

        // 같은 플랜을 8번 냈으니 다양성은 바닥이다 — 게이트가 그것을 잡아야 한다.
        Assert.True(evaluation.Diversity.SequenceRate < EvalGate.Diversity);
        Assert.False(evaluation.Accepted);

        Assert.Contains(evaluation.Gate, g => g.Id == "G1" && g.Verdict == GateVerdict.Pass);
        Assert.Contains(evaluation.Gate, g => g.Id == "G2" && g.Verdict == GateVerdict.Fail);

        // 골든을 안 돌렸으면 미판정이다. <b>통과가 아니다.</b>
        Assert.Contains(evaluation.Gate, g => g.Id == "G4" && g.Verdict == GateVerdict.Unknown);
    }

    /// <summary>
    /// <b>실패는 코드별로 센다.</b> "실패율 24%" 는 고칠 수 없지만
    /// "PRECONDITION_UNMET 이 스텝 1번에서 271건" 은 고칠 수 있다 (C-05 의 입력이다).
    /// </summary>
    [Fact]
    public async Task Pipeline_BreaksFailuresDownByCode()
    {
        // Craft 앞에 MoveTo 가 없다 — 전제조건 미충족이다.
        EngineRun run = await RunAsync(
            """
            {"schema":1,"goal":"forge_now","loop":true,"steps":[
              {"action":"Craft","args":{"recipe":"iron_sword","count":1}},
              {"action":"MoveTo","args":{"poi":"$home"}},
              {"action":"Sleep","args":{"until_time":"Morning"}}]}
            """,
            buckets: 6);

        EngineEvaluation evaluation = EngineEvaluation.Of(run);

        Assert.Equal(6, evaluation.Failures.Attempted);
        Assert.Equal(6, evaluation.Failures.Failed);
        Assert.Equal(1.0, evaluation.Failures.FailRate);
        Assert.NotEmpty(evaluation.Failures.Rows);

        // 건수 많은 순이다.
        Assert.Equal(
            evaluation.Failures.Rows.Select(r => r.Count).OrderByDescending(c => c),
            evaluation.Failures.Rows.Select(r => r.Count));

        // 전부 폴백으로 떨어졌으니 G5(폴백률 ≤ 5%)가 불합격이어야 한다.
        Assert.Contains(evaluation.Gate, g => g.Id == "G5" && g.Verdict == GateVerdict.Fail);
    }

    /// <summary>
    /// <b>보고서에 시각이 없다.</b> 같은 입력이면 바이트 동일해야 회차 간 diff 가 변화를 뜻한다 —
    /// 타임스탬프가 섞이면 모든 줄이 바뀐 것처럼 보인다.
    /// </summary>
    [Fact]
    public void Report_IsByteIdenticalForTheSameInput()
    {
        ImmutableArray<EngineEvaluation> evaluations = [EngineEvaluation.Of(Synthetic())];

        Assert.Equal(
            EvalReport.RenderMarkdown("r1", evaluations),
            EvalReport.RenderMarkdown("r1", evaluations));

        Assert.Equal(
            EvalReport.RenderJson("r1", evaluations),
            EvalReport.RenderJson("r1", evaluations));

        string markdown = EvalReport.RenderMarkdown("r1", evaluations);

        Assert.Contains("## 엔진 대조", markdown, StringComparison.Ordinal);
        Assert.Contains("## 게이트 판정", markdown, StringComparison.Ordinal);
        Assert.Contains("미판정", markdown, StringComparison.Ordinal);

        // 시각이 섞이면 여기가 걸린다.
        Assert.DoesNotContain("20", markdown.Split('\n')[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>미판정을 통과로 세지 않는다.</b> 표본이 0이면 통과율은 "0%" 가 아니라 "못 봤다" 다 —
    /// 0% 로 적으면 불합격이 되고, 사람은 게이트를 끄는 쪽을 고른다.
    /// </summary>
    [Fact]
    public void Gate_DistinguishesUnknownFromFail()
    {
        var empty = new EngineRun("none", "none", [], 0);
        ImmutableArray<GateRow> rows = EvalGate.Judge(empty, DiversityMetrics.Of([]));

        Assert.All(rows, r => Assert.Equal(GateVerdict.Unknown, r.Verdict));

        // 미판정만 있으면 막지 않는다 — 안 돌린 것으로 배포를 막으면 게이트가 꺼진다.
        Assert.True(EvalGate.Accepted(rows));

        // 판정된 것 중 하나라도 불합격이면 막는다.
        ImmutableArray<GateRow> mixed = EvalGate.Judge(Synthetic(), DiversityMetrics.Of(Synthetic().Samples));

        Assert.False(EvalGate.Accepted(mixed));
    }

    /// <summary>
    /// <b>다양성 분모에서 폴백을 뺀다.</b> 사람이 쓴 같은 플랜이라 넣으면 지표가 거짓으로 낮아진다.
    /// </summary>
    [Fact]
    public void Diversity_ExcludesFallbacks()
    {
        ImmutableArray<PlanSample> samples =
        [
            Sample("smith", ok: true, "Runtime", "a", "MoveTo → Work"),
            Sample("smith", ok: true, "Runtime", "b", "MoveTo → Sleep"),
            Sample("smith", ok: false, "Fallback", "f", "MoveTo → Sleep"),
            Sample("smith", ok: false, "Fallback", "f", "MoveTo → Sleep"),
        ];

        DiversityMetrics metrics = DiversityMetrics.Of(samples);

        Assert.Equal(2, metrics.Generated);
        Assert.Equal(2, metrics.UniqueSequences);
        Assert.Equal(1.0, metrics.SequenceRate);

        // 아키타입 안 비율도 생성분만 본다.
        Assert.Equal(1.0, metrics.WithinArchetype);
    }

    /// <summary>
    /// <b>액션이 빈 플랜은 분모에서 뺀다.</b> 시퀀스가 없으면 "같은 시퀀스" 도 성립하지 않는다.
    /// </summary>
    [Fact]
    public void Diversity_IgnoresEmptySequences()
    {
        ImmutableArray<PlanSample> samples =
        [
            Sample("smith", ok: true, "Runtime", "a", string.Empty),
            Sample("smith", ok: true, "Runtime", "b", "MoveTo"),
        ];

        Assert.Equal(1, DiversityMetrics.Of(samples).Generated);
    }

    // ---------------------------------------------------------------- 도우미

    private static async Task<EngineRun> RunAsync(string plan, int buckets)
    {
        Assert.True(LlmPlanCompilerTests.Data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));

        var keys = ImmutableArray.CreateBuilder<BucketKey>(buckets);

        for (int i = 0; i < buckets; i++)
        {
            keys.Add(new BucketKey(
                smith.Code,
                (TimeOfDay)(i % BucketKey.TimeOfDayCount),
                (RegionState)(i / BucketKey.TimeOfDayCount % BucketKey.RegionStateCount),
                (Climate)(i % BucketKey.ClimateCount)));
        }

        return await EvalRunner.RunAsync(
            LlmPlanCompilerTests.Data,
            LlmPlanCompilerTests.Prefix,
            LlmPlanCompilerTests.Options.Engine("gemini-3.1-flash-lite"),
            keys.ToImmutable(),
            () => new FakeChatClient(plan),
            new BulkRunOptions(Concurrency: 4));
    }

    /// <summary>합성 회차 하나. 게이트가 통과·불합격·미판정을 전부 내도록 섞었다.</summary>
    private static EngineRun Synthetic() => new(
        "fake-engine",
        "fake-model",
        [
            Sample("smith", ok: true, "Runtime", "a", "MoveTo → Work"),
            Sample("smith", ok: true, "Runtime", "b", "MoveTo → Work"),
            Sample("smith", ok: false, "Fallback", "c", string.Empty, "Precondition", "PRECONDITION_UNMET", 1),
            Sample("farmer", ok: true, "Runtime", "d", "MoveTo → Harvest"),
        ],
        WallClockSeconds: 1.5);

    private static PlanSample Sample(
        string archetype,
        bool ok,
        string origin,
        string goal,
        string actions,
        string failStage = "",
        string failCode = "",
        int failStep = -1) =>
        new(archetype, $"{archetype}@Dawn.Peace.Fair", ok, origin, goal, actions,
            failStage, failCode, failStep,
            Attempt: 1, CostUsd: 0.001, LatencyMs: 1_200, PromptTokens: 12_000, CachedTokens: 11_500);
}
