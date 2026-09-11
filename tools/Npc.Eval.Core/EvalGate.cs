using System.Collections.Immutable;
using System.Globalization;

namespace Npc.Eval.Core;

/// <summary>판정 하나.</summary>
/// <param name="Id">항목 id. 보고서의 정렬 키다.</param>
/// <param name="Name">사람이 읽는 이름.</param>
/// <param name="Value">실측값.</param>
/// <param name="Threshold">기준값.</param>
/// <param name="Verdict">판정.</param>
/// <param name="Detail">한 줄 사유. <b>미판정이면 왜 못 봤는지가 여기 있다.</b></param>
public readonly record struct GateRow(
    string Id, string Name, double Value, double Threshold, GateVerdict Verdict, string Detail);

/// <summary>게이트 판정.</summary>
public enum GateVerdict
{
    /// <summary>
    /// <b>보지 못했다.</b> 표본이 없거나 회차를 안 돌린 항목이다.
    /// <b>통과가 아니다</b> — 합격 수에 넣지 않는다.
    /// </summary>
    Unknown = 0,

    /// <summary>기준을 넘었다.</summary>
    Pass = 1,

    /// <summary>기준에 못 미쳤다.</summary>
    Fail = 2,
}

/// <summary>
/// 평가 게이트 (C-04).
///
/// <para>
/// <b>기준은 여기 한 곳에만 있다.</b> 로드맵·보고서·CI 가 각자 숫자를 들고 있으면
/// 기준을 올릴 때 한 곳만 고쳐지고, 그때부터 게이트는 거짓이 된다.
/// </para>
///
/// <para>
/// <b>LLM 심사원 점수는 게이트가 아니다.</b> 루브릭 채점은 권고 신호이고, 그것으로 배포를
/// 막으면 심사 모델이 바뀔 때마다 기준이 소리 없이 움직인다 — 보고서에 싣되 판정에서 뺀다.
/// </para>
/// </summary>
public static class EvalGate
{
    /// <summary>4단 검증 통과율 하한. C-05 의 목표(수선 전 ≤10% 실패)와 짝이다.</summary>
    public const double PassRate = 0.90;

    /// <summary>다양성 하한. W1(T0-12) 이래 같은 값이다.</summary>
    public const double Diversity = DiversityMetrics.Target;

    /// <summary>골든 단언 합격률 하한.</summary>
    public const double Golden = 0.90;

    /// <summary>폴백률 상한. 이것만 <b>낮을수록 좋다</b>.</summary>
    public const double MaxFallbackRate = 0.05;

    /// <summary>
    /// 엔진 하나를 판정한다.
    /// </summary>
    /// <param name="run">회차 결과.</param>
    /// <param name="diversity">그 회차의 다양성 지표.</param>
    public static ImmutableArray<GateRow> Judge(EngineRun run, DiversityMetrics diversity)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(diversity);

        var rows = ImmutableArray.CreateBuilder<GateRow>(5);

        rows.Add(run.Attempted == 0
            ? Unknown("G1", "검증 통과율", PassRate, "던진 버킷이 0건이다")
            : Row("G1", "검증 통과율", run.PassRate, PassRate, higherIsBetter: true,
                Count(run.Passed, run.Attempted)
                + (run.StoppedByBudget ? " · 예산 캡에 걸려 중단됐다 — 표본을 대표하지 않는다" : string.Empty)));

        rows.Add(diversity.Generated == 0
            ? Unknown("G2", "유니크 액션 시퀀스", Diversity, "생성에 성공한 플랜이 0건이다")
            : Row("G2", "유니크 액션 시퀀스", diversity.SequenceRate, Diversity, higherIsBetter: true,
                Count(diversity.UniqueSequences, diversity.Generated)));

        rows.Add(diversity.ByArchetype.Length == 0
            ? Unknown("G3", "아키타입 안 유니크 비율", Diversity, "표본이 2건 이상인 아키타입이 없다")
            : Row("G3", "아키타입 안 유니크 비율", diversity.WithinArchetype, Diversity, higherIsBetter: true,
                $"아키타입 {diversity.ByArchetype.Length}종"));

        rows.Add(run.Golden < 0
            ? Unknown("G4", "골든 단언 합격률", Golden, "골든 회차를 안 돌렸다 (--golden)")
            : Row("G4", "골든 단언 합격률", run.Golden, Golden, higherIsBetter: true,
                $"단언 {run.GoldenAssertions}건"));

        rows.Add(run.Attempted == 0
            ? Unknown("G5", "폴백률", MaxFallbackRate, "던진 버킷이 0건이다")
            : Row("G5", "폴백률", run.FallbackRate, MaxFallbackRate, higherIsBetter: false,
                Count(run.FellBack, run.Attempted)));

        return rows.MoveToImmutable();
    }

    /// <summary>불합격이 하나라도 있으면 false. <b>미판정은 불합격이 아니지만 통과도 아니다.</b></summary>
    /// <param name="rows">판정들.</param>
    public static bool Accepted(ImmutableArray<GateRow> rows) =>
        !rows.Any(r => r.Verdict == GateVerdict.Fail);

    /// <summary>판정을 사람 말로.</summary>
    /// <param name="verdict">판정.</param>
    public static string Text(GateVerdict verdict) => verdict switch
    {
        GateVerdict.Pass => "통과",
        GateVerdict.Fail => "불합격",
        _ => "미판정",
    };

    private static GateRow Row(
        string id, string name, double value, double threshold, bool higherIsBetter, string detail) =>
        new(id, name, value, threshold,
            (higherIsBetter ? value >= threshold : value <= threshold)
                ? GateVerdict.Pass
                : GateVerdict.Fail,
            detail);

    private static GateRow Unknown(string id, string name, double threshold, string why) =>
        new(id, name, double.NaN, threshold, GateVerdict.Unknown, why);

    private static string Count(int part, int whole) =>
        string.Create(CultureInfo.InvariantCulture, $"{part}/{whole}");
}
