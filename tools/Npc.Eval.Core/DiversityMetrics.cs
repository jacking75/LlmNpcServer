using System.Collections.Immutable;

namespace Npc.Eval.Core;

/// <summary>아키타입 하나의 다양성.</summary>
/// <param name="Archetype">아키타입 id.</param>
/// <param name="Generated">생성에 성공한 수.</param>
/// <param name="Unique">그중 유니크 액션 시퀀스 수.</param>
public readonly record struct ArchetypeDiversity(string Archetype, int Generated, int Unique)
{
    /// <summary>유니크 비율.</summary>
    public double Rate => Generated == 0 ? 0 : (double)Unique / Generated;
}

/// <summary>
/// 플랜 다양성 지표 (C-04 · T2-22 의 정의를 그대로 쓴다).
///
/// <para>
/// <b>왜 이것을 보는가.</b> 플랜은 (아키타입 × 버킷) 단위로 재사용된다. 같은 아키타입의
/// 서로 다른 상황이 같은 액션 시퀀스를 내면 버킷 차원 2,880 은 <b>그냥 비용일 뿐</b>이고
/// 아무것도 사지 못한다. 그래서 전체 유니크 비율과 함께 <b>아키타입 안에서의</b> 비율도 낸다 —
/// 후자가 진짜 질문이다.
/// </para>
///
/// <para>
/// <b>폴백은 분모에서 뺀다.</b> 사람이 쓴 같은 플랜이라 넣으면 지표가 거짓으로 낮아진다.
/// </para>
/// </summary>
/// <param name="Generated">생성에 성공한 수. 모든 비율의 분모다.</param>
/// <param name="UniqueSequences">유니크 액션 시퀀스 수.</param>
/// <param name="UniqueGoals">유니크 goal 수.</param>
/// <param name="ByArchetype">아키타입별. 비율 낮은 순 → 이름 순.</param>
/// <param name="TopSequences">가장 흔한 액션 시퀀스. 건수 많은 순 → 사전순.</param>
public sealed record DiversityMetrics(
    int Generated,
    int UniqueSequences,
    int UniqueGoals,
    ImmutableArray<ArchetypeDiversity> ByArchetype,
    ImmutableArray<(string Actions, int Count)> TopSequences)
{
    /// <summary>목표 유니크 비율. W1(T0-12) 이래 같은 값이다.</summary>
    public const double Target = 0.60;

    /// <summary>유니크 액션 시퀀스 / 생성 성공.</summary>
    public double SequenceRate => Generated == 0 ? 0 : (double)UniqueSequences / Generated;

    /// <summary>유니크 goal / 생성 성공.</summary>
    public double GoalRate => Generated == 0 ? 0 : (double)UniqueGoals / Generated;

    /// <summary>
    /// 아키타입 안 유니크 비율. <b>표본이 2건 이상인 아키타입만</b> 센다 —
    /// 1건짜리는 언제나 100% 라 평균을 끌어올리기만 한다.
    /// </summary>
    public double WithinArchetype
    {
        get
        {
            int generated = 0;
            int unique = 0;

            foreach (ArchetypeDiversity row in ByArchetype)
            {
                generated += row.Generated;
                unique += row.Unique;
            }

            return generated == 0 ? 1.0 : (double)unique / generated;
        }
    }

    /// <summary>표본에서 계산한다. <b>결정론</b> — 같은 입력이면 같은 순서로 나온다.</summary>
    /// <param name="samples">표본.</param>
    /// <param name="topSequences">가장 흔한 시퀀스를 몇 개 낼까.</param>
    public static DiversityMetrics Of(ImmutableArray<PlanSample> samples, int topSequences = 10)
    {
        PlanSample[] generated = [.. samples.Where(s => s.CountsForDiversity)];

        ImmutableArray<ArchetypeDiversity> byArchetype =
        [
            .. generated
                .GroupBy(s => s.Archetype, StringComparer.Ordinal)
                .Select(g => new ArchetypeDiversity(
                    g.Key,
                    g.Count(),
                    g.Select(s => s.Actions).Distinct(StringComparer.Ordinal).Count()))
                .Where(a => a.Generated > 1)
                .OrderBy(a => a.Rate)
                .ThenBy(a => a.Archetype, StringComparer.Ordinal),
        ];

        ImmutableArray<(string, int)> top =
        [
            .. generated
                .GroupBy(s => s.Actions, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(Math.Max(0, topSequences))
                .Select(g => (g.Key, g.Count())),
        ];

        return new DiversityMetrics(
            generated.Length,
            generated.Select(s => s.Actions).Distinct(StringComparer.Ordinal).Count(),
            generated.Select(s => s.Goal).Distinct(StringComparer.Ordinal).Count(),
            byArchetype,
            top);
    }
}
