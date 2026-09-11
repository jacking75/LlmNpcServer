using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Eval.Core;
using Npc.Llm;
using Npc.MasterData;
using Npc.Prebake;

namespace Npc.Eval;

/// <summary>
/// 엔진 하나를 돌려 <see cref="EngineRun"/> 을 만든다 (C-04).
///
/// <para>
/// <b><see cref="BulkRunner"/> 를 다시 쓴다.</b> "같은 프리픽스로 버킷 표본을 생성한다" 는
/// 프리베이크가 이미 하는 일이고, 여기서 또 만들면 두 러너가 갈라져 <b>"프리베이크에서는
/// 되는데 평가에서는 안 된다"</b> 가 생긴다 — 그때 어느 쪽이 진실인지 알 수 없다.
/// </para>
///
/// <para>
/// <b>표본은 엔진마다 같아야 한다.</b> 버킷 선택을 엔진별로 새로 뽑으면 통과율 차이가
/// 모델 차이인지 표본 차이인지 구분되지 않는다 — <see cref="Sample"/> 이 결정론이라
/// 같은 <c>--sample</c> 이면 같은 버킷이 나온다.
/// </para>
/// </summary>
public static class EvalRunner
{
    /// <summary>
    /// 버킷 표본을 결정론으로 고른다.
    ///
    /// <b>앞에서부터 자르지 않는다.</b> 버킷 첨자는 아키타입 우선이라 앞에서 자르면
    /// 대장장이만 뽑히고 농부가 한 마리도 안 나온다 — <c>NpcRoster.Select</c> 와 같은
    /// 균등 간격 공식을 쓴다.
    /// </summary>
    /// <param name="space">버킷 공간.</param>
    /// <param name="count">뽑을 수. 전체보다 크면 전체를 준다.</param>
    public static ImmutableArray<BucketKey> Sample(BucketSpace space, int count)
    {
        ArgumentNullException.ThrowIfNull(space);

        ImmutableArray<BucketKey> all = BulkRunner.AllBuckets(space);
        int take = Math.Clamp(count, 1, all.Length);
        var chosen = ImmutableArray.CreateBuilder<BucketKey>(take);

        for (int i = 0; i < take; i++)
        {
            chosen.Add(all[(int)((long)i * all.Length / take)]);
        }

        return chosen.MoveToImmutable();
    }

    /// <summary>
    /// 한 엔진으로 표본을 돌린다.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="prefix">프롬프트 프리픽스. <b>엔진마다 같아야 한다</b>.</param>
    /// <param name="engine">엔진 설정.</param>
    /// <param name="buckets">표본 버킷.</param>
    /// <param name="clientFactory">클라이언트 공급자. 테스트는 가짜를 넣는다.</param>
    /// <param name="options">러너 설정.</param>
    /// <param name="progress">진행 보고.</param>
    /// <param name="cancellationToken">취소.</param>
    public static async Task<EngineRun> RunAsync(
        MasterDataSet data,
        PromptPrefix prefix,
        LlmEngineOptions engine,
        ImmutableArray<BucketKey> buckets,
        Func<IChatClient> clientFactory,
        BulkRunOptions? options = null,
        Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(engine);

        var runner = new BulkRunner(data, prefix, engine, options);

        long started = Stopwatch.GetTimestamp();

        BulkRunReport report = await runner
            .RunAsync(buckets, clientFactory, progress, cancellationToken)
            .ConfigureAwait(false);

        return new EngineRun(
            engine.Id,
            ModelOf(report, engine),
            [.. report.Outcomes.Select(o => ToSample(o, data))],
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            StoppedByBudget: report.StoppedByBudget);
    }

    /// <summary>
    /// <see cref="BucketOutcome"/> → <see cref="PlanSample"/>.
    ///
    /// <b>여기가 경계다.</b> <c>Npc.Eval.Core</c> 는 프리베이크 타입을 모르고, 그래서
    /// 가짜 데이터로 게이트 로직을 테스트할 수 있다.
    /// </summary>
    private static PlanSample ToSample(in BucketOutcome outcome, MasterDataSet data) => new(
        Archetype: outcome.Bucket.A.Value < data.Archetypes.Count
            ? data.Archetypes[outcome.Bucket.A].Id
            : string.Empty,
        Bucket: outcome.Bucket.A.Value < data.Archetypes.Count
            ? outcome.Bucket.Format(data.Archetypes[outcome.Bucket.A].Id)
            : string.Empty,
        Ok: outcome.Validation.IsValid,
        Origin: outcome.Origin.ToString(),
        Goal: outcome.Goal ?? string.Empty,
        Actions: outcome.Actions.IsDefaultOrEmpty
            ? string.Empty
            : string.Join(" → ", outcome.Actions),
        FailStage: outcome.Validation.IsValid ? string.Empty : outcome.Validation.FailedAt.ToString(),
        FailCode: outcome.Validation.IsValid ? string.Empty : outcome.Validation.Code,
        FailStep: outcome.Validation.IsValid ? -1 : outcome.Validation.StepIndex,
        Attempt: outcome.Stats.Attempt,
        CostUsd: outcome.Stats.CostUsd,
        LatencyMs: outcome.Stats.LatencyMs,
        PromptTokens: outcome.Stats.PromptTokens,
        CachedTokens: outcome.Stats.CachedTokens);

    /// <summary>실제로 응답한 모델 이름. 하나도 안 왔으면 엔진 설정의 모델을 쓴다.</summary>
    private static string ModelOf(BulkRunReport report, LlmEngineOptions engine)
    {
        foreach (BucketOutcome outcome in report.Outcomes)
        {
            if (!string.IsNullOrEmpty(outcome.Stats.Model))
            {
                return outcome.Stats.Model;
            }
        }

        return engine.Model;
    }
}
