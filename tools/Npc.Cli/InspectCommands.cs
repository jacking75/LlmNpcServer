using System.Collections.Immutable;
using System.Text;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.MasterData.Validation;
using Npc.Narrative;

namespace Npc.Cli;

/// <summary>
/// <c>npc validate</c> (F-01). <b>규칙 검증만이 아니다</b> — 로더까지 돌리고 파생물 신선도까지 본다.
///
/// 규칙만 통과하고 참조가 깨진 상태를 통과로 내면 호출부가 그 위에 다음 작업을 쌓는다.
/// <c>Npc.Host validate</c> 와 같은 코어를 부른다 — 기동 스크립트 호환 때문에 그쪽도 남긴다.
/// </summary>
public static class ValidateCommand
{
    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        MasterDataValidationReport report = MasterDataValidator.Validate(ctx.MasterData);

        if (ctx.Json)
        {
            return Json(ctx, report);
        }

        ctx.Out.WriteLine($"masterdata  {Path.GetFullPath(ctx.MasterData)}");

        foreach (SkippedRule skip in report.Skipped)
        {
            ctx.Out.WriteLine($"  SKIP {skip.Code}  {skip.Reason}");
        }

        foreach (MasterDataViolation violation in report.Violations)
        {
            ctx.Out.WriteLine($"  FAIL {violation.Code}  {violation.Detail}");

            if (violation.FixHint is { Length: > 0 } hint)
            {
                ctx.Out.WriteLine($"       → {hint}");
            }
        }

        if (!report.IsValid)
        {
            ctx.Out.WriteLine($"검증 실패 {report.Violations.Length}건");
            return Program.Failed;
        }

        MasterDataSet data = ctx.Data;

        ctx.Out.WriteLine(
            $"  OK   액션 {data.Actions.Count} · 아이템 {data.Items.Items.Length} · 존 {data.Zones.Count}"
            + $" · POI {data.Pois.Count} · 아키타입 {data.Archetypes.Count} · 인터럽트 {data.Interrupts.Count}");
        ctx.Out.WriteLine($"  content_hash    {data.ContentHash}");
        ctx.Out.WriteLine($"  structural_hash {data.StructuralHash}");

        foreach (DerivedStatus stale in data.StaleArtifacts)
        {
            ctx.Out.WriteLine($"  WARN 파생물 {stale}");
        }

        ctx.Out.WriteLine(
            data.StaleArtifacts.IsEmpty
                ? $"검증 통과 (V1~V13, 건너뜀 {report.Skipped.Length}건)"
                : $"검증 통과 (V1~V13, 건너뜀 {report.Skipped.Length}건) · 파생물 {data.StaleArtifacts.Length}건 낡음 → `npc regen`");

        // 파생물이 낡은 것은 규칙 위반이 아니다 — 판정은 사람이 한다. 종료 코드는 0 이다.
        return Program.Ok;
    }

    private static int Json(CliContext ctx, MasterDataValidationReport report)
    {
        MasterDataSet? data = null;
        string? loadError = null;

        try
        {
            data = ctx.Data;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            loadError = ex.Message;
        }

        // E-04 의 형식을 그대로 쓴다 — Npc.Host validate --format json 과 같은 JSON 이어야
        // LLM 이 두 껍질에서 같은 것을 읽는다. 그래서 ValidationJson 이 Npc.MasterData 에 있다.
        ctx.Out.WriteLine(
            ValidationJson.Serialize(ValidationJson.From(report, ctx.MasterData, data, loadError)));

        return report.IsValid && loadError is null ? Program.Ok : Program.Failed;
    }
}

/// <summary>
/// <c>npc explain &lt;종류&gt; &lt;id&gt;</c> (F-01).
/// 카드보다 짧다 — 정의 하나가 무엇인지만 본다.
/// </summary>
public static class ExplainCommand
{
    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ImmutableArray<string> args = Program.Positional(ctx);

        if (args.Length == 0)
        {
            ctx.Out.WriteLine("사용법: npc explain archetype|action|poi|item|flag|interrupt <id>");
            return Program.BadUsage;
        }

        string kind = args[0];
        string? id = args.Length > 1 ? args[1] : null;

        switch (kind)
        {
            case "archetype" when id is not null:
                ctx.Out.Write(ArchetypeCard.Render(ctx.Data, Program.Archetype(ctx, id).Code, ctx.Population));
                return Program.Ok;

            case "interrupt" when id is null:
                ctx.Out.Write(InterruptExplain.Render(ctx.Data));
                return Program.Ok;

            case "interrupt":
                return Interrupt(ctx, id);

            case "action" when id is not null:
                return Action(ctx, id);

            case "item" when id is not null:
                return Item(ctx, id);

            case "poi" when id is not null:
                return Poi(ctx, id);

            case "flag" when id is not null:
                return Flag(ctx, id);

            default:
                ctx.Out.WriteLine("사용법: npc explain archetype|action|poi|item|flag|interrupt <id>");
                return Program.BadUsage;
        }
    }

    private static int Interrupt(CliContext ctx, string id)
    {
        InterruptRule? rule = ctx.Data.Interrupts.Rules
            .FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));

        if (rule is null)
        {
            throw new ArgumentException($"인터럽트 '{id}' 이 없다.");
        }

        ctx.Out.WriteLine($"# {rule.Id} (우선순위 {rule.Priority})");
        ctx.Out.WriteLine();
        ctx.Out.WriteLine($"언제  {InterruptExplain.When(rule)}");
        ctx.Out.WriteLine($"무엇을 {InterruptExplain.Then(ctx.Data, rule)}");
        ctx.Out.WriteLine($"재계획 긴급도 {rule.Urgency}");

        return Program.Ok;
    }

    private static int Action(CliContext ctx, string id)
    {
        if (!ctx.Data.TryGetAction(id, out ActionId action))
        {
            throw new ArgumentException($"액션 '{id}' 이 없다.");
        }

        ActionDef def = ctx.Data.Actions[action];
        var sb = new StringBuilder(1024);

        sb.Append("# ").Append(def.Id).Append(" (code ").Append(def.Code.Value)
          .Append(" · ").Append(Lexicon.Of(def.Category)).AppendLine(")");
        sb.AppendLine();
        sb.AppendLine(def.Description);
        sb.AppendLine();

        Md.TableHead(sb, "항목", "값");
        Md.Row(sb, "전제(전부)", Md.Code(WorldFlagTable.Format(def.Requires)));
        Md.Row(sb, "전제(하나)", Md.Code(WorldFlagTable.Format(def.RequiresAny)));
        Md.Row(sb, "금지", Md.Code(WorldFlagTable.Format(def.Forbids)));
        Md.Row(sb, "세움", Md.Code(WorldFlagTable.Format(def.Grants)));
        Md.Row(sb, "지움", Md.Code(WorldFlagTable.Format(def.Clears)));
        Md.Row(sb, "기본 타임아웃", Md.Seconds(def.DefaultTimeoutSeconds));
        Md.Row(sb, "인자", Md.Codes(def.Params.Select(p => $"{p.Name}:{p.Type}")));
        Md.Row(sb, "완료 이벤트", Md.Codes(def.CompletesOn.Select(e => e.ToString())));
        Md.Row(sb, "실패 이벤트", Md.Codes(def.FailsOn.Select(e => e.ToString())));

        // 어느 아키타입이 쓸 수 있는지 — 액션을 추가·수정할 때 파급을 여기서 본다.
        ImmutableArray<string> users =
        [
            .. ctx.Data.Archetypes.Archetypes.Where(a => a.Allows(action)).Select(a => a.Id),
        ];

        Md.Row(sb, $"허용 아키타입 {users.Length}", Md.Codes(users));

        ctx.Out.Write(sb.ToString());

        return Program.Ok;
    }

    private static int Item(CliContext ctx, string id)
    {
        if (!ctx.Data.Items.TryGet(id, out ItemDef def))
        {
            throw new ArgumentException($"아이템 '{id}' 이 없다.");
        }

        var sb = new StringBuilder(1024);

        sb.Append("# ").Append(Lexicon.Item(def.Id)).Append(" (`").Append(def.Id)
          .Append("` · code ").Append(def.Code.Value).AppendLine(")");
        sb.AppendLine();

        Md.TableHead(sb, "항목", "값");
        Md.Row(sb, "분류", Md.Code(def.Category.ToString()));
        Md.Row(sb, "세우는 플래그", Md.Code(WorldFlagTable.Format(def.Grants)));
        Md.Row(sb, "스택", Md.N(def.Stack));

        ImmutableArray<RecipeDef> makes =
        [
            .. ctx.Data.Items.Recipes.Where(r => r.Outputs.Any(o => o.Item == def.Code)),
        ];

        ImmutableArray<RecipeDef> uses =
        [
            .. ctx.Data.Items.Recipes.Where(r => r.Inputs.Any(i => i.Item == def.Code)),
        ];

        Md.Row(sb, "만드는 레시피", Md.Codes(makes.Select(r => r.Id)));
        Md.Row(sb, "재료로 쓰는 레시피", Md.Codes(uses.Select(r => r.Id)));

        ctx.Out.Write(sb.ToString());

        return Program.Ok;
    }

    private static int Poi(CliContext ctx, string id)
    {
        if (!ctx.Data.Pois.TryGet(id, out PoiDef def))
        {
            throw new ArgumentException($"POI '{id}' 이 없다.");
        }

        var sb = new StringBuilder(1024);

        sb.Append("# ").Append(Lexicon.Place(def.Subtype)).Append(" `").Append(def.Id)
          .Append("` (code ").Append(def.Code.Value).AppendLine(")");
        sb.AppendLine();

        Md.TableHead(sb, "항목", "값");
        Md.Row(sb, "존", Md.Code(ctx.Data.Zones[def.Zone].Id));
        Md.Row(sb, "종류", $"{def.Type} · `{def.Subtype}`");
        Md.Row(sb, "정원", Md.N(def.Capacity));
        Md.Row(sb, "여는 시간", $"{Lexicon.Of(def.OpenFrom)} ~ {Lexicon.Of(def.OpenTo)}");
        Md.Row(sb, "세우는 플래그", Md.Code(WorldFlagTable.Format(def.Grants)));
        Md.Row(sb, "자원", Md.Codes(def.Resources.Select(r => ctx.Data.Items[r].Id)));
        Md.Row(
            sb,
            "근무 허가",
            def.AllowedArchetypeMask == 0
                ? "제한 없음"
                : Md.Codes(ctx.Data.Archetypes.Archetypes.Where(a => def.Allows(a.Code)).Select(a => a.Id)));

        ctx.Out.Write(sb.ToString());

        return Program.Ok;
    }

    private static int Flag(CliContext ctx, string id)
    {
        if (!WorldFlagTable.TryParse(id, out WorldFlags flag))
        {
            throw new ArgumentException($"플래그 '{id}' 이 없다.");
        }

        var sb = new StringBuilder(1024);
        int bit = Array.IndexOf(WorldFlagTable.Values, flag);

        sb.Append("# ").Append(id).Append(" (bit ").Append(bit).AppendLine(")");
        sb.AppendLine();

        // 이 플래그를 누가 세우고 누가 지우고 누가 요구하는가 — 비트 하나를 고칠 때의 파급이다.
        Md.TableHead(sb, "관계", "액션");
        Md.Row(sb, "요구(전부)", Md.Codes(Where(ctx, a => (a.Requires & flag) != 0)));
        Md.Row(sb, "요구(하나)", Md.Codes(Where(ctx, a => (a.RequiresAny & flag) != 0)));
        Md.Row(sb, "금지", Md.Codes(Where(ctx, a => (a.Forbids & flag) != 0)));
        Md.Row(sb, "세움", Md.Codes(Where(ctx, a => (a.Grants & flag) != 0)));
        Md.Row(sb, "지움", Md.Codes(Where(ctx, a => (a.Clears & flag) != 0)));

        ImmutableArray<string> items =
        [
            .. ctx.Data.Items.Items.Where(i => (i.Grants & flag) != 0).Select(i => i.Id),
        ];

        sb.AppendLine();
        sb.Append("이 플래그를 세우는 아이템: ").AppendLine(Md.Codes(items));

        ctx.Out.Write(sb.ToString());

        return Program.Ok;
    }

    private static ImmutableArray<string> Where(CliContext ctx, Func<ActionDef, bool> predicate) =>
        [.. ctx.Data.Actions.Actions.Where(predicate).Select(a => a.Id)];
}

/// <summary><c>npc card …</c> (F-01). <c>Npc.Narrative</c> 를 그대로 낸다.</summary>
public static class CardCommand
{
    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ImmutableArray<string> args = Program.Positional(ctx);

        if (args.Length < 2)
        {
            ctx.Out.WriteLine("사용법: npc card archetype <id> | npc <첨자> | roster <id>");
            return Program.BadUsage;
        }

        switch (args[0])
        {
            case "archetype":
                ctx.Out.Write(ArchetypeCard.Render(ctx.Data, Program.Archetype(ctx, args[1]).Code, ctx.Population));
                return Program.Ok;

            case "npc":
                if (!int.TryParse(args[1], out int index) || index < 0)
                {
                    throw new ArgumentException($"NPC 첨자가 잘못됐다: {args[1]}");
                }

                ctx.Out.Write(InstanceCard.Render(ctx.Data, Instances(ctx), index));
                return Program.Ok;

            case "roster":
                ctx.Out.Write(
                    InstanceCard.RenderRoster(ctx.Data, Instances(ctx), Program.Archetype(ctx, args[1]).Code));
                return Program.Ok;

            default:
                ctx.Out.WriteLine("사용법: npc card archetype <id> | npc <첨자> | roster <id>");
                return Program.BadUsage;
        }
    }

    internal static NpcInstanceTable Instances(CliContext ctx) =>
        NpcInstanceTable.Load(Path.Combine(ctx.MasterData, "npc_instances.json"), ctx.Data);
}

/// <summary>
/// <c>npc timeline archetype &lt;id&gt;</c> (F-01). 24시간 띠.
///
/// <b>근무 시간과 폴백 스텝을 같은 축에 놓는다.</b> 둘이 어긋나 있으면 표에서 바로 보인다 —
/// 위병이 근무 시간에 자고 있는 폴백은 파일 두 개를 대조해야 알 수 있었다.
/// </summary>
public static class TimelineCommand
{
    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ImmutableArray<string> args = Program.Positional(ctx);

        if (args.Length < 2 || args[0] != "archetype")
        {
            ctx.Out.WriteLine("사용법: npc timeline archetype <id>");
            return Program.BadUsage;
        }

        ArchetypeDef def = Program.Archetype(ctx, args[1]);
        var sb = new StringBuilder(2048);

        sb.Append("# ").Append(Lexicon.Archetype(def.Id)).AppendLine(" 24시간");
        sb.AppendLine();

        Md.TableHead(sb, "시간대", "게임 시각", "근무", "이 시간대가 세우는 플래그");

        foreach (TimeOfDay time in Enum.GetValues<TimeOfDay>())
        {
            (int from, int to) = ctx.Data.Buckets.GameHoursOf(time);

            Md.Row(
                sb,
                Lexicon.Of(time),
                $"{from:D2}:00–{to:D2}:00",
                def.IsOnDuty(time) ? "근무 ✓" : "—",
                Md.Code(WorldFlagTable.Format(ctx.Data.Buckets.FlagsOf(time))));
        }

        sb.AppendLine();

        if (ctx.Data.Fallbacks?.For(def.Code) is { } plan)
        {
            sb.Append("## 폴백 하루 (`").Append(def.FallbackPlanId).AppendLine("`)");
            sb.AppendLine();
            sb.Append(PlanExplain.Steps(ctx.Data, plan, plan.Bucket, def.Code));
        }
        else
        {
            sb.AppendLine("폴백 플랜을 찾지 못했다 — `fallback_plans.json` 을 확인한다 (V7).");
        }

        ctx.Out.Write(sb.ToString());

        return Program.Ok;
    }
}

/// <summary><c>npc hints</c> (F-01 · E-04). 검증 오류 사전.</summary>
public static class HintsCommand
{
    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        // 사전은 마스터데이터를 읽지 않는다 — 코드가 원천이다.
        foreach (string code in FixHints.Codes.OrderBy(c => c, StringComparer.Ordinal))
        {
            FixHint hint = FixHints.For(code)!.Value;

            ctx.Out.WriteLine($"{code,-24} {hint.Hint}");
        }

        ctx.Out.WriteLine();
        ctx.Out.WriteLine($"검증 코드 {FixHints.Codes.Count()}건. markdown 문서는 docs/llm/VALIDATION.md 다.");

        return Program.Ok;
    }
}
