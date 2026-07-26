using System.Globalization;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;

namespace Npc.Sim.Validation;

/// <summary>
/// 검증기 4단 — 드라이런. docs/03 §3 · docs/12 §5.
///
/// <b>이 검증기만 <c>Npc.Core</c> 에 두지 않는다.</b> 1~3단과 달리 4단은 세계를 굴려야 하고
/// <see cref="SimWorld"/> 는 여기 있다. 계약(<see cref="IDryRunValidator"/>)만 <c>Npc.Core</c> 에 두고
/// 구현을 이쪽에 둔 이유다 (CLAUDE.md §3).
///
/// <b>결정론이다.</b> 시드는 버킷 키에서 나오고(<see cref="ValidationContext.SeedOf"/>)
/// <see cref="SimOptions.Seed"/> 경로만 쓴다. 같은 플랜을 100번 돌려도 같은 판정이 나온다 —
/// 안 그러면 골든 테스트가 불안정해진다.
/// </summary>
public sealed class DryRunValidator(MasterDataSet data) : IDryRunValidator
{
    private readonly MasterDataSet _data = data ?? throw new ArgumentNullException(nameof(data));

    /// <summary><c>loop</c> 플랜의 한 사이클이 이보다 짧으면 <c>V4.INFINITE_LOOP</c>. docs/03 §3.</summary>
    public const int MinCycleGameHours = 24;

    /// <summary>
    /// 플랜의 버킷에서 상황을 통째로 유도해 돌린다. <b>호출자가 시드를 정할 수 없다</b> —
    /// 시드를 밖에서 넣을 수 있으면 언젠가 시각이나 난수가 섞이고, 그 순간 골든 테스트가 흔들린다.
    /// </summary>
    public ValidationResult Validate(CompiledPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return Validate(plan, ValidationContext.For(plan.Bucket, _data.InitialFlags(plan.Bucket)));
    }

    /// <inheritdoc />
    public ValidationResult Validate(CompiledPlan plan, ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // loop 플랜은 2사이클을 돈다 — 두 번째 사이클에서만 드러나는 고갈·교착이 있다.
        int cycles = plan.Loop ? 2 : 1;

        var world = SimWorld.CreateMinimal(_data, context.Bucket, context.Seed);
        world.DryRunInitialFlags = context.InitialFlags;

        int npc = world.Spawn(context.Bucket.A);
        world.AssignPlan(npc, plan);

        DryRunTrace trace = world.RunCycles(context.MaxGameHours, cycles);

        return Judge(plan, context, trace);
    }

    /// <summary>흔적을 판정으로. docs/12 §5 의 순서를 그대로 따른다.</summary>
    private static ValidationResult Judge(CompiledPlan plan, in ValidationContext context, in DryRunTrace trace)
    {
        if (trace.StalledAtStep >= 0)
        {
            return ValidationResult.Fail(
                ValidationStage.DryRun, "V4.DEADLOCK", trace.StalledAtStep,
                $"The plan stops at step {trace.StalledAtStep.ToString(CultureInfo.InvariantCulture)}: {trace.StallReason}.");
        }

        if (trace.GameHours > context.MaxGameHours)
        {
            return ValidationResult.Fail(
                ValidationStage.DryRun, "V4.TIMEOUT", -1,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"One run takes {trace.GameHours:F1} game hours, over the {context.MaxGameHours} hour budget. Use fewer or shorter steps."));
        }

        if (trace.StarvedResource is { } resource)
        {
            return ValidationResult.Fail(
                ValidationStage.DryRun, "V4.RESOURCE_STARVE", -1,
                $"The plan runs out of {resource} and never restocks it. Add a step that gathers or buys it.");
        }

        if (trace.MaxPoiOscillation >= SimWorld.OscillationLimit)
        {
            return ValidationResult.Fail(
                ValidationStage.DryRun, "V4.OSCILLATION", -1,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The NPC walks back and forth between the same two places {trace.MaxPoiOscillation} times. Group the steps that happen in one place."));
        }

        // loop 플랜인데 사이클이 너무 짧으면 하루가 아니라 몇 분마다 도는 것이다.
        // 24 시간 정각으로 끝나는 일과가 부동소수 오차로 걸리지 않도록 여유를 1분 둔다.
        if (plan.Loop && trace.CycleHours < MinCycleGameHours - (1.0 / 60.0))
        {
            return ValidationResult.Fail(
                ValidationStage.DryRun, "V4.INFINITE_LOOP", -1,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"loop is true but one cycle takes only {trace.CycleHours:F1} game hours. A daily routine must fill about {MinCycleGameHours} hours - end it with Sleep until the next morning."));
        }

        return ValidationResult.Ok;
    }
}
