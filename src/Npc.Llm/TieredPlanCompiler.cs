using Npc.Core;
using Npc.Core.Validation;

namespace Npc.Llm;

/// <summary>플랜 생성 티어. docs/14 §4 · 상위계획 §10.4.</summary>
public enum PlanTier : byte
{
    /// <summary>쓸 티어가 없다. 요청을 거절한다.</summary>
    None = 0,

    /// <summary>로컬 LLM. 1회용 재계획. 저지연·무비용.</summary>
    T1 = 1,

    /// <summary>외부 API. 수천 NPC 가 재사용하는 아키타입 플랜.</summary>
    T2 = 2,
}

/// <summary>
/// 3-티어 라우터. docs/14 §4.
///
/// <b>여기 있는 것은 티어 선택과 강등뿐이다.</b> 예산(T4-06)·스필오버 임계(T4-08)·
/// 서킷 브레이커(T4-09)는 P4 의 태스크이고 아직 없다. T5-08 이 이 파일을 먼저 만든 이유는
/// 킬스위치를 <b>연결할 티어</b>가 있어야 시나리오 C 를 판정할 수 있어서다 —
/// "끊었다"고 기록만 남기면 안 끊긴 채로 게이트가 통과한다.
///
/// 강등 순서는 <c>T2 → T1 → 거절</c> 이다 (docs/14 §3). 거절도 결과이지 예외가 아니다 —
/// 상위(재계획 워커)는 플랜을 못 받으면 기존 플랜으로 계속 돌면 된다.
/// </summary>
public sealed class TieredPlanCompiler : IPlanCompiler
{
    private readonly IPlanCompiler? _t2;
    private readonly IPlanCompiler? _t1;
    private readonly KillSwitchState _switches;

    /// <summary>라우터를 만든다. 티어가 없으면(null) 그 티어는 처음부터 쓸 수 없는 것으로 본다.</summary>
    /// <param name="t2">외부 API 컴파일러. 없으면 null.</param>
    /// <param name="t1">로컬 컴파일러. 없으면 null.</param>
    /// <param name="switches">킬스위치 상태. 없으면 아무것도 끊기지 않은 것으로 본다.</param>
    public TieredPlanCompiler(IPlanCompiler? t2, IPlanCompiler? t1, KillSwitchState? switches = null)
    {
        _t2 = t2;
        _t1 = t1;
        _switches = switches ?? KillSwitchState.None;
    }

    /// <summary>T2 장애로 T1 에 넘긴 횟수. docs/14 §4 의 <c>TierFailover</c>.</summary>
    public long Failovers { get; private set; }

    /// <summary>쓸 티어가 없어 거절한 횟수.</summary>
    public long Rejections { get; private set; }

    /// <summary>이 요청이 어느 티어로 가는가. 킬스위치와 티어 유무까지 반영한 <b>최종</b> 답이다.</summary>
    public PlanTier SelectTier(in PlanRequest request)
    {
        // docs/14 §4 표 — 재사용 횟수에 비례해 품질에 투자한다 (CLAUDE.md §8).
        // 아키타입 플랜은 수천 NPC 가 공유하므로 T2, 개체 1회용은 T1.
        PlanTier wanted = request.Quality == PlanQuality.Archetype ? PlanTier.T2 : PlanTier.T1;

        return Downgrade(wanted);
    }

    /// <inheritdoc />
    public async ValueTask<PlanCompileResult> CompileAsync(PlanRequest request, CancellationToken cancellationToken)
    {
        PlanTier tier = SelectTier(in request);

        if (tier == PlanTier.None)
        {
            Rejections++;
            return Rejected("쓸 수 있는 티어가 없다 (T1·T2 가 전부 차단됐거나 구성되지 않았다).");
        }

        if (tier == PlanTier.T1)
        {
            return await _t1!.CompileAsync(request, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await _t2!.CompileAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 외부 장애 → 로컬 페일오버. T1 도 없으면 거절이다.
            if (Available(PlanTier.T1))
            {
                Failovers++;
                return await _t1!.CompileAsync(request, cancellationToken).ConfigureAwait(false);
            }

            Rejections++;
            return Rejected($"T2 가 실패했고 T1 도 쓸 수 없다: {e.Message}");
        }
    }

    /// <summary>이 티어를 지금 쓸 수 있는가. 구성돼 있고 끊기지 않았어야 한다.</summary>
    public bool Available(PlanTier tier) => tier switch
    {
        PlanTier.T2 => _t2 is not null && !_switches.IsDisabled(KillSwitchTarget.T2),
        PlanTier.T1 => _t1 is not null && !_switches.IsDisabled(KillSwitchTarget.T1),
        _ => false,
    };

    /// <summary>쓸 수 없으면 한 단계 내린다. <c>T2 → T1 → None</c>.</summary>
    private PlanTier Downgrade(PlanTier tier)
    {
        if (tier == PlanTier.T2 && Available(PlanTier.T2))
        {
            return PlanTier.T2;
        }

        return Available(PlanTier.T1) ? PlanTier.T1 : PlanTier.None;
    }

    /// <summary>
    /// 생성 자체가 안 됐다. <c>V0.CALL_FAILED</c> 와 같은 자리에 둔다 —
    /// 스키마 단계 실패로 세어야 통과율 분모가 줄지 않는다 (docs/12 §2).
    /// </summary>
    private static PlanCompileResult Rejected(string reason) => new(
        null,
        ValidationResult.Fail(ValidationStage.Schema, "V0.NO_TIER", -1, reason),
        CompileStats.None with { Error = reason },
        string.Empty);
}
