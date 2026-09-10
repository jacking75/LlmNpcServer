using System.Collections.Immutable;
using System.Text;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;

namespace Npc.Narrative;

/// <summary>
/// 플랜 설명 (F-03). 스텝마다 <c>requires</c>/<c>requires_any</c>/<c>forbids</c> 가
/// <b>어떻게 충족되는가</b>를 앞 스텝의 <c>grants</c> 로 추적해 보여 준다.
///
/// <para>
/// <b>전이 계산은 <see cref="CoherenceValidator"/> 와 같은 규칙이다.</b> 다르면 카드가
/// "괜찮다" 고 하는데 검증기가 반려하는 상태가 되고, 그러면 검수자가 카드를 믿지 않게 된다.
/// <c>PlanExplain_AgreesWithCoherenceValidator</c> 가 같은 플랜에 같은 판정·같은 스텝을
/// 내는지 강제한다.
/// </para>
///
/// <para>
/// 여기는 <b>이미 컴파일된 플랜</b>을 읽는다. 3단 검증기는 <c>PlanDocument</c>(JSON 모델)를
/// 읽으므로 입력이 다르지만, <b>전이 계산은 같은 세 가지를 더한다</b>:
/// 액션 정의의 <c>grants</c> · 도착으로 완료되는 액션이 세우는 장소 플래그
/// (<see cref="CoherenceValidator.GrantsOf(PoiSymbol)"/>) · 수령 액션이 받는 아이템의
/// <c>grants</c>. 셋 중 하나라도 빠뜨리면 카드가 멀쩡한 플랜을 반려로 그린다.
/// </para>
/// </summary>
public static class PlanExplain
{
    /// <summary>플랜 한 장. 머리말 + 스텝 트레이스 + 수지 + 다양성.</summary>
    public static string Render(MasterDataSet data, CompiledPlan plan)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(plan);

        var sb = new StringBuilder(4 * 1024);
        ArchetypeDef archetype = data.Archetypes[plan.Bucket.A];

        sb.Append("# ").Append(plan.Bucket.Format(archetype.Id)).AppendLine();
        sb.AppendLine();

        Md.TableHead(sb, "항목", "값");
        Md.Row(sb, "목표", Md.Code(plan.Goal));
        Md.Row(sb, "아키타입", $"{Lexicon.Archetype(archetype.Id)} (`{archetype.Id}`)");
        Md.Row(
            sb,
            "버킷",
            $"{Lexicon.Of(plan.Bucket.T)} · {Lexicon.Of(plan.Bucket.R)} · {Lexicon.Of(plan.Bucket.C)}"
            + $" (첨자 {Md.N(plan.Bucket.ToIndex())})");
        Md.Row(sb, "반복", plan.Loop ? "loop — 마지막 뒤 첫 스텝으로" : "1회 — 휴식으로 끝나야 한다");
        Md.Row(sb, "스텝 실패 시", Md.Code(plan.OnFail.ToString()));
        Md.Row(sb, "출처", Md.Code(plan.Origin.ToString()));
        Md.Row(sb, "시작 플래그", Md.Code(WorldFlagTable.Format(data.InitialFlags(plan.Bucket))));
        sb.AppendLine();

        sb.Append(Steps(data, plan, plan.Bucket, plan.Bucket.A));
        sb.AppendLine();
        Budget(sb, data, plan);
        Duration(sb, data, plan);

        return sb.ToString();
    }

    /// <summary>
    /// 스텝 트레이스만. 아키타입 카드가 폴백 하루를 그릴 때 이것만 쓴다.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="plan">설명할 플랜.</param>
    /// <param name="bucket">시작 상태를 정하는 버킷.</param>
    /// <param name="archetype">POI 심볼 바인딩 가능 여부를 보는 아키타입.</param>
    public static string Steps(
        MasterDataSet data, CompiledPlan plan, BucketKey bucket, ArchetypeId archetype)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(plan);

        var sb = new StringBuilder(2 * 1024);

        // 3단 검증기와 같은 시작 상태다 — 버킷 + 아키타입 기본 인벤토리 + 근무 시간대.
        WorldFlags state = data.InitialFlags(bucket);

        Md.TableHead(sb, "#", "액션", "대상", "전제", "판정", "이 스텝 뒤 상태");

        ActionId previous = default;
        int repeats = 0;

        for (int i = 0; i < plan.Steps.Length; i++)
        {
            CompiledStep step = plan.Steps[i];
            StepFlags flags = plan.FlagsOf(i);

            repeats = step.Action == previous ? repeats + 1 : 1;
            previous = step.Action;

            string code = Verdict(data, archetype, step, flags, state, repeats);

            state = Apply(data, step, flags, state);

            Md.Row(
                sb,
                Md.N(i + 1),
                Md.Code(data.ActionName(step.Action)),
                Md.Cell(Target(data, step)),
                Md.Cell(Requirement(flags)),
                Md.Cell(code.Length == 0 ? "✓" : "✗ `" + code + "` — " + Reason(data, archetype, step, flags, state, code)),
                Md.Code(WorldFlagTable.Format(state)));
        }

        Loop(sb, plan, state);

        return sb.ToString();
    }

    /// <summary>
    /// 카드가 그리는 첫 실패. 없으면 <c>StepIndex = -1</c> 이고 <c>Code</c> 가 빈 문자열이다.
    ///
    /// <b><see cref="Steps"/> 와 같은 계산을 쓴다</b> — 표에 ✓ 가 찍혔는데 여기서 실패가 나오면
    /// 카드와 판정이 갈린 것이고, 그러면 둘 중 무엇도 믿을 수 없다.
    ///
    /// <para>
    /// <b>자원 수지(<c>V3.RESOURCE_IMBALANCE</c>)는 여기서 보지 않는다.</b> 그것은 스텝 하나의
    /// 성질이 아니라 플랜 전체의 합이고, 카드는 별도의 "인벤토리 수지" 표로 보여 준다.
    /// <c>PlanExplain_AgreesWithCoherenceValidator</c> 가 이 예외를 명시적으로 고정한다.
    /// </para>
    /// </summary>
    public static (int StepIndex, string Code) FirstFailure(
        MasterDataSet data, CompiledPlan plan, BucketKey bucket, ArchetypeId archetype)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(plan);

        WorldFlags state = data.InitialFlags(bucket);
        ActionId previous = default;
        int repeats = 0;

        for (int i = 0; i < plan.Steps.Length; i++)
        {
            CompiledStep step = plan.Steps[i];
            StepFlags flags = plan.FlagsOf(i);

            repeats = step.Action == previous ? repeats + 1 : 1;
            previous = step.Action;

            string code = Verdict(data, archetype, step, flags, state, repeats);

            if (code.Length > 0)
            {
                return (i, code);
            }

            state = Apply(data, step, flags, state);
        }

        return Closing(plan, state);
    }

    /// <summary>고리·종료 판정. 검증기가 스텝 순회를 마친 뒤 보는 것과 같은 순서다.</summary>
    private static (int StepIndex, string Code) Closing(CompiledPlan plan, WorldFlags state)
    {
        if (plan.Steps.IsEmpty)
        {
            return (-1, string.Empty);
        }

        if (plan.Loop)
        {
            StepFlags first = plan.FlagsOf(0);

            bool closed = (first.Requires & ~state) == WorldFlags.None
                && (first.Forbids & state) == WorldFlags.None
                && (first.RequiresAny == WorldFlags.None || (first.RequiresAny & state) != 0);

            return closed ? (-1, string.Empty) : (-1, "V3.LOOP_NOT_CLOSED");
        }

        return (plan.FlagsOf(plan.Steps.Length - 1).Grants & WorldFlags.IsRested) != 0
            ? (-1, string.Empty)
            : (plan.Steps.Length - 1, "V3.NO_TERMINAL");
    }

    /// <summary>
    /// 이 스텝이 끝난 뒤의 상태. <b><see cref="CoherenceValidator"/> 와 같은 세 가지를 더한다.</b>
    ///
    /// <list type="bullet">
    /// <item>액션 정의의 <c>grants</c></item>
    /// <item>도착으로 완료되는 액션이면 그 POI 가 함의하는 장소 플래그 —
    ///       이것을 빼면 <c>MoveTo $workplace</c> 다음의 <c>Work</c> 가 전부 반려로 보인다</item>
    /// <item>수령 액션이면 받는 아이템의 <c>grants</c> — 액션 정의는 아이템을 모르지만
    ///       런타임은 인벤토리에서 플래그를 다시 계산한다</item>
    /// </list>
    /// </summary>
    private static WorldFlags Apply(
        MasterDataSet data, CompiledStep step, StepFlags flags, WorldFlags state)
    {
        WorldFlags grants = flags.Grants;

        if (data.CompletesOnArrival(step.Action))
        {
            grants |= CoherenceValidator.GrantsOf(step.Poi);
        }

        if (step.Item != default && data.ProducesItem(step.Action))
        {
            grants |= data.ItemGrants(step.Item);
        }

        return (state & ~flags.Clears) | grants;
    }

    /// <summary>
    /// 이 스텝이 지금 상태에서 실행 가능한가. <b>충족 못 하면 그 자리에 코드와 힌트를 적는다</b> —
    /// "어딘가 잘못됐다" 는 검수자에게 아무것도 알려 주지 않는다.
    /// </summary>
    private static string Verdict(
        MasterDataSet data,
        ArchetypeId archetype,
        CompiledStep step,
        StepFlags flags,
        WorldFlags state,
        int repeats)
    {
        // 순서가 CoherenceValidator 와 같아야 한다 — 두 실패가 겹치면 먼저 보는 쪽이 코드가 된다.
        if (step.Poi != PoiSymbol.None && !data.CanBindSymbol(archetype, step.Poi))
        {
            return "V3.UNREACHABLE_POI";
        }

        if ((flags.Requires & ~state) != WorldFlags.None)
        {
            return "V3.PRECONDITION_UNMET";
        }

        if (flags.RequiresAny != WorldFlags.None && (flags.RequiresAny & state) == 0)
        {
            return "V3.PRECONDITION_UNMET";
        }

        if ((flags.Forbids & state) != WorldFlags.None)
        {
            return "V3.FORBIDDEN_FLAG";
        }

        return repeats >= CoherenceValidator.MaxConsecutiveSameAction ? "V3.DEGENERATE" : string.Empty;
    }

    /// <summary>실패 코드를 사람이 읽는 이유로. <b>코드만 찍으면 검수자가 다시 소스를 열어야 한다.</b></summary>
    private static string Reason(
        MasterDataSet data,
        ArchetypeId archetype,
        CompiledStep step,
        StepFlags flags,
        WorldFlags state,
        string code)
    {
        switch (code)
        {
            case "V3.UNREACHABLE_POI":
                return $"이 아키타입에 {Lexicon.Of(step.Poi)} 가 없다";

            case "V3.FORBIDDEN_FLAG":
                return $"`{WorldFlagTable.Format(flags.Forbids & state)}` 가 서 있는 동안 금지다";

            case "V3.DEGENERATE":
                return $"`{data.ActionName(step.Action)}` 가 "
                    + $"{Md.N(CoherenceValidator.MaxConsecutiveSameAction)}회 연속이다";

            default:
                WorldFlags missing = flags.Requires & ~state;

                _ = archetype;

                return missing != WorldFlags.None
                    ? $"`{WorldFlagTable.Format(missing)}` 를 세우는 앞 스텝이 없다"
                    : $"`{WorldFlagTable.Format(flags.RequiresAny)}` 중 하나도 서 있지 않다";
        }
    }

    private static string Requirement(StepFlags flags)
    {
        var parts = new List<string>(3);

        if (flags.Requires != WorldFlags.None)
        {
            parts.Add("전부 `" + WorldFlagTable.Format(flags.Requires) + "`");
        }

        if (flags.RequiresAny != WorldFlags.None)
        {
            parts.Add("하나 `" + WorldFlagTable.Format(flags.RequiresAny) + "`");
        }

        if (flags.Forbids != WorldFlags.None)
        {
            parts.Add("금지 `" + WorldFlagTable.Format(flags.Forbids) + "`");
        }

        return parts.Count == 0 ? "없음" : string.Join(" · ", parts);
    }

    private static string Target(MasterDataSet data, CompiledStep step)
    {
        var parts = new List<string>(3);

        if (step.Poi != PoiSymbol.None)
        {
            parts.Add(Lexicon.Of(step.Poi));
        }

        if (step.Item != default)
        {
            string id = data.ItemName(step.Item);

            parts.Add(step.Count > 1
                ? $"{Lexicon.Item(id)} ×{Md.N(step.Count)}"
                : Lexicon.Item(id));
        }
        else if (step.Count > 0)
        {
            parts.Add("×" + Md.N(step.Count));
        }

        if (NpcRefCodes.KindOf(step.NpcRef) is var kind && kind != NpcRefKind.None)
        {
            parts.Add(NpcRef(data, kind, NpcRefCodes.PayloadOf(step.NpcRef)));
        }

        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    private static string NpcRef(MasterDataSet data, NpcRefKind kind, int payload) => kind switch
    {
        NpcRefKind.Self => "자신",
        NpcRefKind.NearestArchetype when payload < data.Archetypes.Count =>
            "가장 가까운 " + Lexicon.Archetype(data.Archetypes[new ArchetypeId((ushort)payload)].Id),
        NpcRefKind.PoiOwner => Lexicon.Of((PoiSymbol)payload) + "의 주인",
        _ => "—",
    };

    /// <summary>
    /// <c>loop: true</c> 면 마지막 상태가 첫 스텝의 전제를 만족해야 한다.
    /// <b>안 그러면 두 바퀴째부터 매번 재계획이 걸린다</b> — 하루가 도는 것처럼 보여도 비용이 샌다.
    /// </summary>
    private static void Loop(StringBuilder sb, CompiledPlan plan, WorldFlags finalState)
    {
        if (plan.Steps.IsEmpty)
        {
            return;
        }

        sb.AppendLine();

        if (!plan.Loop)
        {
            bool rested = (plan.FlagsOf(plan.Steps.Length - 1).Grants & WorldFlags.IsRested) != 0;

            sb.Append(Md.Mark(rested)).Append(" 1회 플랜이므로 마지막이 휴식이어야 한다")
              .AppendLine(rested ? "." : " — `V3.NO_TERMINAL`.");
            return;
        }

        StepFlags first = plan.FlagsOf(0);
        WorldFlags missing = first.Requires & ~finalState;
        WorldFlags violated = first.Forbids & finalState;
        bool anyOk = first.RequiresAny == WorldFlags.None || (first.RequiresAny & finalState) != 0;
        bool closed = missing == WorldFlags.None && violated == WorldFlags.None && anyOk;

        sb.Append(Md.Mark(closed)).Append(" 고리가 닫힌다 — 마지막 상태 `")
          .Append(WorldFlagTable.Format(finalState)).Append("` 로 1번 스텝을 다시 시작");

        if (closed)
        {
            sb.AppendLine("할 수 있다.");
            return;
        }

        sb.Append("할 수 없다 (`V3.LOOP_NOT_CLOSED`) — ");

        if (missing != WorldFlags.None)
        {
            sb.Append('`').Append(WorldFlagTable.Format(missing)).Append("` 가 없다.");
        }
        else if (violated != WorldFlags.None)
        {
            sb.Append('`').Append(WorldFlagTable.Format(violated)).Append("` 가 남아 있다.");
        }
        else
        {
            sb.Append('`').Append(WorldFlagTable.Format(first.RequiresAny)).Append("` 중 하나도 없다.");
        }

        sb.AppendLine();
    }

    /// <summary>
    /// 인벤토리 수지. <b>플랜이 스스로 모으고 스스로 쓴 것만 센다</b> — 안 모으는 자원은
    /// 기존 인벤토리·보관함에서 온다고 보는 것이 3단 검증기의 규칙이고, 여기도 같게 본다.
    /// </summary>
    private static void Budget(StringBuilder sb, MasterDataSet data, CompiledPlan plan)
    {
        var gathered = new Dictionary<ItemId, int>();
        var consumed = new Dictionary<ItemId, int>();

        for (int i = 0; i < plan.Steps.Length; i++)
        {
            CompiledStep step = plan.Steps[i];

            if (step.Item == default)
            {
                continue;
            }

            int count = step.Count == 0 ? 1 : step.Count;

            if (data.TryGetRecipeInputs(step.Item, out ImmutableArray<PlanRecipeInput> inputs))
            {
                foreach (PlanRecipeInput input in inputs)
                {
                    consumed[input.Item] = consumed.GetValueOrDefault(input.Item) + (input.Count * count);
                }

                gathered[step.Item] = gathered.GetValueOrDefault(step.Item) + count;
                continue;
            }

            if (data.ProducesItem(step.Action))
            {
                gathered[step.Item] = gathered.GetValueOrDefault(step.Item) + count;
            }
            else
            {
                consumed[step.Item] = consumed.GetValueOrDefault(step.Item) + count;
            }
        }

        ImmutableArray<ItemId> items =
        [
            .. gathered.Keys.Concat(consumed.Keys).Distinct().OrderBy(k => k.Value),
        ];

        sb.AppendLine("## 인벤토리 수지");
        sb.AppendLine();

        if (items.IsEmpty)
        {
            sb.AppendLine("아이템을 다루지 않는다.");
            sb.AppendLine();
            return;
        }

        Md.TableHead(sb, "아이템", "획득", "소비", "수지");

        foreach (ItemId item in items)
        {
            int got = gathered.GetValueOrDefault(item);
            int used = consumed.GetValueOrDefault(item);

            // 획득이 0 인 것은 기존 소지품에서 온다 — 부족으로 세지 않는다 (3단과 같은 판단).
            string balance = got == 0
                ? "기존 소지품"
                : (got - used) switch
                {
                    < 0 => $"{Md.N(got - used)} ✗ `V3.RESOURCE_IMBALANCE`",
                    0 => "0",
                    var d => "+" + Md.N(d),
                };

            Md.Row(sb, Lexicon.Item(data.ItemName(item)), Md.N(got), Md.N(used), balance);
        }

        sb.AppendLine();
    }

    /// <summary>예상 소요. <b>레시피 시간과 타임아웃만으로 센다</b> — 이동 시간은 개체 바인딩에 달렸다.</summary>
    private static void Duration(StringBuilder sb, MasterDataSet data, CompiledPlan plan)
    {
        int timeout = 0;

        foreach (CompiledStep step in plan.Steps)
        {
            timeout += step.TimeoutSeconds;
        }

        sb.AppendLine("## 예상 소요");
        sb.AppendLine();
        sb.Append("타임아웃 합 ").Append(Md.Seconds(timeout))
          .Append(" (스텝 ").Append(Md.N(plan.Steps.Length)).AppendLine("개).");
        sb.AppendLine();
        sb.AppendLine(
            "타임아웃은 **상한**이지 예상값이 아니다. 실제 소요는 이동 거리에 달려 있고, "
            + "거리는 개체 바인딩(집·일터가 어느 POI 인가) 이후에야 정해진다 — "
            + "인스턴스 카드가 그 값을 준다.");
        sb.AppendLine();

        _ = data;
    }
}
