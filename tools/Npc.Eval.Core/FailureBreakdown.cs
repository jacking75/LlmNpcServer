using System.Collections.Immutable;

namespace Npc.Eval.Core;

/// <summary>실패 코드 하나의 집계.</summary>
/// <param name="Stage">검증 단계.</param>
/// <param name="Code">실패 코드.</param>
/// <param name="Count">건수.</param>
/// <param name="TopStep">가장 자주 실패한 스텝 첨자. 스텝과 무관하면 -1.</param>
public readonly record struct FailureRow(string Stage, string Code, int Count, int TopStep);

/// <summary>
/// 검증 실패 집계 (C-04 · C-05 의 입력).
///
/// <para>
/// <b>코드별로 세는 것이 요점이다.</b> "실패율 24%" 는 고칠 수 없지만
/// "<c>PRECONDITION_UNMET</c> 이 47.9% 이고 그중 스텝 1번이 271건" 은 고칠 수 있다 —
/// 그 대부분이 "장소 플래그 앞에 <c>MoveTo</c> 가 없다" 류의 기계적 오류다.
/// </para>
/// </summary>
/// <param name="Attempted">던진 수. 비율의 분모다.</param>
/// <param name="Failed">실패 수.</param>
/// <param name="Rows">코드별. 건수 많은 순 → 단계 → 코드 순.</param>
public sealed record FailureBreakdown(int Attempted, int Failed, ImmutableArray<FailureRow> Rows)
{
    /// <summary>실패율.</summary>
    public double FailRate => Attempted == 0 ? 0 : (double)Failed / Attempted;

    /// <summary>표본에서 계산한다. <b>결정론</b>이다.</summary>
    /// <param name="samples">표본.</param>
    public static FailureBreakdown Of(ImmutableArray<PlanSample> samples)
    {
        PlanSample[] attempted = [.. samples.Where(s => s.Attempted)];
        PlanSample[] failed = [.. attempted.Where(s => !s.Ok)];

        ImmutableArray<FailureRow> rows =
        [
            .. failed
                .GroupBy(s => (s.FailStage, s.FailCode))
                .Select(g => new FailureRow(
                    g.Key.FailStage,
                    g.Key.FailCode,
                    g.Count(),
                    // 가장 자주 걸린 스텝. 같은 건수면 앞 스텝을 쓴다 — 앞쪽이 원인일 때가 많다.
                    g.Where(s => s.FailStep >= 0)
                        .GroupBy(s => s.FailStep)
                        .OrderByDescending(x => x.Count())
                        .ThenBy(x => x.Key)
                        .Select(x => x.Key)
                        .DefaultIfEmpty(-1)
                        .First()))
                .OrderByDescending(r => r.Count)
                .ThenBy(r => r.Stage, StringComparer.Ordinal)
                .ThenBy(r => r.Code, StringComparer.Ordinal),
        ];

        return new FailureBreakdown(attempted.Length, failed.Length, rows);
    }
}
