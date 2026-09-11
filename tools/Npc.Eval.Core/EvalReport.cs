using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Npc.Eval.Core;

/// <summary>엔진 하나의 평가 결과 한 벌 (C-04).</summary>
/// <param name="Run">회차 결과.</param>
/// <param name="Diversity">다양성 지표.</param>
/// <param name="Failures">실패 집계.</param>
/// <param name="Gate">게이트 판정.</param>
public sealed record EngineEvaluation(
    EngineRun Run,
    DiversityMetrics Diversity,
    FailureBreakdown Failures,
    ImmutableArray<GateRow> Gate)
{
    /// <summary>이 엔진이 게이트를 통과했는가.</summary>
    public bool Accepted => EvalGate.Accepted(Gate);

    /// <summary>회차 결과에서 전부 계산한다.</summary>
    /// <param name="run">회차 결과.</param>
    public static EngineEvaluation Of(EngineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        DiversityMetrics diversity = DiversityMetrics.Of(run.Samples);

        return new EngineEvaluation(
            run,
            diversity,
            FailureBreakdown.Of(run.Samples),
            EvalGate.Judge(run, diversity));
    }
}

/// <summary>
/// 평가 보고서 (C-04).
///
/// <para>
/// <b>시각을 넣지 않는다.</b> 같은 입력이면 바이트 동일한 보고서가 나와야 회차 간 diff 가
/// 의미를 갖는다 — 타임스탬프가 섞이면 모든 줄이 바뀐 것처럼 보인다. 언제 돌렸는지는
/// 파일 시각과 커밋이 안다.
/// </para>
///
/// <para>
/// <b>미판정을 통과로 세지 않는다.</b> 골든을 안 돌렸으면 "골든 통과" 가 아니라
/// "골든 미판정" 이고, 그것이 표에 그대로 남는다.
/// </para>
/// </summary>
public static class EvalReport
{
    /// <summary>사람이 읽는 보고서. Markdown.</summary>
    /// <param name="title">제목에 붙일 회차 이름.</param>
    /// <param name="evaluations">엔진별 결과. <b>준 순서를 지킨다</b> — 첫 엔진이 기준선이다.</param>
    public static string RenderMarkdown(string title, ImmutableArray<EngineEvaluation> evaluations)
    {
        ArgumentNullException.ThrowIfNull(title);

        var text = new StringBuilder(16 * 1024);

        text.Append(CultureInfo.InvariantCulture, $"# 평가 회차 — {title}\n\n");
        text.Append("> `tools/Npc.Eval` 이 생성한다. **손으로 고치지 않는다.**\n");
        text.Append("> 시각을 넣지 않으므로 같은 입력이면 바이트 동일하다 — diff 가 곧 변화다.\n");
        text.Append("> **미판정은 통과가 아니다.** 안 돌린 항목은 안 돌렸다고 적힌다.\n\n");

        if (evaluations.Length == 0)
        {
            text.Append("엔진이 하나도 없다. 회차가 비어 있다.\n");

            return text.ToString();
        }

        Summary(text, evaluations);
        Gates(text, evaluations);

        foreach (EngineEvaluation evaluation in evaluations)
        {
            Engine(text, evaluation);
        }

        return text.ToString();
    }

    /// <summary>기계가 읽는 보고서. JSON. 게이트 러너가 이것을 본다.</summary>
    /// <param name="title">회차 이름.</param>
    /// <param name="evaluations">엔진별 결과.</param>
    public static string RenderJson(string title, ImmutableArray<EngineEvaluation> evaluations)
    {
        ArgumentNullException.ThrowIfNull(title);

        var buffer = new MemoryStream(8 * 1024);

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("title", title);
            writer.WriteBoolean("accepted", evaluations.All(e => e.Accepted));
            writer.WritePropertyName("engines");
            writer.WriteStartArray();

            foreach (EngineEvaluation evaluation in evaluations)
            {
                WriteEngine(writer, evaluation);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()).ReplaceLineEndings("\n") + "\n";
    }

    private static void WriteEngine(Utf8JsonWriter writer, EngineEvaluation evaluation)
    {
        EngineRun run = evaluation.Run;

        writer.WriteStartObject();
        writer.WriteString("engine", run.Engine);
        writer.WriteString("model", run.Model);
        writer.WriteBoolean("accepted", evaluation.Accepted);
        writer.WriteNumber("attempted", run.Attempted);
        writer.WriteNumber("passed", run.Passed);
        writer.WriteNumber("passRate", Round(run.PassRate));
        writer.WriteNumber("fallbackRate", Round(run.FallbackRate));
        writer.WriteNumber("uniqueSequenceRate", Round(evaluation.Diversity.SequenceRate));
        writer.WriteNumber("withinArchetypeRate", Round(evaluation.Diversity.WithinArchetype));
        writer.WriteNumber("costUsd", Round(run.CostUsd, 6));
        writer.WriteNumber("costPerRequestUsd", Round(run.CostPerRequest, 6));
        writer.WriteNumber("averageLatencyMs", Round(run.AverageLatencyMs, 1));
        writer.WriteNumber("cacheHitRate", Round(run.CacheHitRate));
        writer.WriteNumber("golden", Round(run.Golden));
        writer.WriteNumber("judge", Round(run.Judge, 2));
        writer.WriteBoolean("stoppedByBudget", run.StoppedByBudget);

        writer.WritePropertyName("gate");
        writer.WriteStartArray();

        foreach (GateRow row in evaluation.Gate)
        {
            writer.WriteStartObject();
            writer.WriteString("id", row.Id);
            writer.WriteString("name", row.Name);
            writer.WriteString("verdict", EvalGate.Text(row.Verdict));
            writer.WriteNumber("value", double.IsNaN(row.Value) ? -1 : Round(row.Value));
            writer.WriteNumber("threshold", Round(row.Threshold));
            writer.WriteString("detail", row.Detail);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("failures");
        writer.WriteStartArray();

        foreach (FailureRow row in evaluation.Failures.Rows)
        {
            writer.WriteStartObject();
            writer.WriteString("stage", row.Stage);
            writer.WriteString("code", row.Code);
            writer.WriteNumber("count", row.Count);
            writer.WriteNumber("topStep", row.TopStep);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void Summary(StringBuilder text, ImmutableArray<EngineEvaluation> evaluations)
    {
        text.Append("## 엔진 대조\n\n");
        text.Append("| 엔진 | 통과율 | 유니크 시퀀스 | 아키타입 안 | 폴백률 | 골든 | 심사원 | 요청당 비용 | 평균 지연 | 캐시 | 판정 |\n");
        text.Append("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|\n");

        foreach (EngineEvaluation e in evaluations)
        {
            EngineRun run = e.Run;

            text.Append(CultureInfo.InvariantCulture,
                $"| `{run.Engine}` | {Percent(run.PassRate)} | {Percent(e.Diversity.SequenceRate)} "
                + $"| {Percent(e.Diversity.WithinArchetype)} | {Percent(run.FallbackRate)} "
                + $"| {(run.Golden < 0 ? "미판정" : Percent(run.Golden))} "
                + $"| {(run.Judge < 0 ? "미판정" : run.Judge.ToString("0.00", CultureInfo.InvariantCulture))} "
                + $"| ${run.CostPerRequest.ToString("0.000000", CultureInfo.InvariantCulture)} "
                + $"| {run.AverageLatencyMs.ToString("0.0", CultureInfo.InvariantCulture)}ms "
                + $"| {Percent(run.CacheHitRate)} "
                + $"| {(e.Accepted ? "통과" : "불합격")} |\n");
        }

        text.Append('\n');
        text.Append(CultureInfo.InvariantCulture,
            $"기준 — 통과율 ≥ {Percent(EvalGate.PassRate)} · 다양성 ≥ {Percent(EvalGate.Diversity)} "
            + $"· 골든 ≥ {Percent(EvalGate.Golden)} · 폴백률 ≤ {Percent(EvalGate.MaxFallbackRate)}\n\n");
        text.Append("**LLM 심사원 점수는 게이트가 아니다.** 루브릭 채점은 권고 신호이고, "
            + "그것으로 배포를 막으면 심사 모델이 바뀔 때마다 기준이 소리 없이 움직인다.\n\n");
    }

    private static void Gates(StringBuilder text, ImmutableArray<EngineEvaluation> evaluations)
    {
        text.Append("## 게이트 판정\n\n");
        text.Append("| 엔진 | 항목 | 값 | 기준 | 판정 | 사유 |\n");
        text.Append("|---|---|---:|---:|---|---|\n");

        foreach (EngineEvaluation e in evaluations)
        {
            foreach (GateRow row in e.Gate)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"| `{e.Run.Engine}` | {row.Id} {row.Name} "
                    + $"| {(double.IsNaN(row.Value) ? "—" : Percent(row.Value))} | {Percent(row.Threshold)} "
                    + $"| {EvalGate.Text(row.Verdict)} | {row.Detail} |\n");
            }
        }

        text.Append('\n');
    }

    private static void Engine(StringBuilder text, EngineEvaluation evaluation)
    {
        EngineRun run = evaluation.Run;

        text.Append(CultureInfo.InvariantCulture, $"## `{run.Engine}`\n\n");
        text.Append(CultureInfo.InvariantCulture,
            $"모델 `{run.Model}` · 버킷 {run.Attempted}건 · 소요 "
            + $"{run.WallClockSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s "
            + $"· 총 ${run.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)}\n\n");

        if (run.StoppedByBudget)
        {
            text.Append("> **예산 캡에 걸려 중단됐다.** 통과율이 표본 전체를 대표하지 않는다.\n\n");
        }

        FailureBreakdown failures = evaluation.Failures;

        text.Append(CultureInfo.InvariantCulture,
            $"### 검증 실패 {failures.Failed}/{failures.Attempted} ({Percent(failures.FailRate)})\n\n");

        if (failures.Rows.Length == 0)
        {
            text.Append("실패가 없다.\n\n");
        }
        else
        {
            text.Append("| 단계 | 코드 | 건수 | 비중 | 최다 스텝 |\n");
            text.Append("|---|---|---:|---:|---:|\n");

            foreach (FailureRow row in failures.Rows)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"| {row.Stage} | `{row.Code}` | {row.Count} "
                    + $"| {Percent(failures.Failed == 0 ? 0 : (double)row.Count / failures.Failed)} "
                    + $"| {(row.TopStep < 0 ? "—" : row.TopStep.ToString(CultureInfo.InvariantCulture))} |\n");
            }

            text.Append('\n');
        }

        DiversityMetrics diversity = evaluation.Diversity;

        text.Append("### 다양성\n\n");
        text.Append(CultureInfo.InvariantCulture,
            $"생성 성공 {diversity.Generated}건 · 유니크 시퀀스 {diversity.UniqueSequences} "
            + $"({Percent(diversity.SequenceRate)}) · 유니크 goal {diversity.UniqueGoals} "
            + $"({Percent(diversity.GoalRate)})\n\n");

        if (diversity.ByArchetype.Length > 0)
        {
            text.Append("| 아키타입 | 생성 | 유니크 | 비율 |\n");
            text.Append("|---|---:|---:|---:|\n");

            foreach (ArchetypeDiversity row in diversity.ByArchetype.Take(15))
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"| {row.Archetype} | {row.Generated} | {row.Unique} | {Percent(row.Rate)} |\n");
            }

            text.Append('\n');
        }

        if (diversity.TopSequences.Length > 0)
        {
            text.Append("가장 흔한 액션 시퀀스\n\n");
            text.Append("| 건수 | 시퀀스 |\n");
            text.Append("|---:|---|\n");

            foreach ((string actions, int count) in diversity.TopSequences)
            {
                text.Append(CultureInfo.InvariantCulture, $"| {count} | `{actions}` |\n");
            }

            text.Append('\n');
        }
    }

    private static string Percent(double value) =>
        double.IsNaN(value) ? "—" : (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";

    private static double Round(double value, int digits = 4) =>
        double.IsNaN(value) ? -1 : Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
