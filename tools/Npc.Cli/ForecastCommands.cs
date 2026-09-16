using System.Collections.Immutable;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Narrative;

namespace Npc.Cli;

/// <summary>
/// <c>npc forecast archetype &lt;id&gt; [--bucket …] [--npc &lt;번호&gt;]</c> (T22).
///
/// <b>Studio 와 같은 함수를 부른다.</b> 화면이 그리는 하루와 터미널이 내는 표가 갈리면
/// 둘 중 무엇도 믿을 수 없다.
/// </summary>
public static class ForecastCommand
{
    private const string Usage = "사용법: npc forecast archetype <id> [--bucket <키>] [--npc <번호>]";

    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ImmutableArray<string> args = Program.Positional(ctx, "--bucket", "--npc");

        if (args.Length < 2 || args[0] != "archetype")
        {
            ctx.Out.WriteLine(Usage);
            return Program.BadUsage;
        }

        ArchetypeDef def = Program.Archetype(ctx, args[1]);
        NpcInstanceTable? instances = TryInstances(ctx);

        ForecastSubject subject = Program.Flag(ctx, "--npc") is { Length: > 0 } text
            ? Subject(ctx, instances, text)
            : DayForecast.Representative(ctx.Data, instances, def.Code);

        BucketKey bucket = Bucket(ctx, def);

        if (DayForecast.RunFallback(ctx.Data, subject, bucket) is not { } forecast)
        {
            ctx.Out.WriteLine($"'{def.Id}' 의 하루 일과를 찾지 못했다 — fallback_plans.json 을 본다 (V7).");
            return Program.Failed;
        }

        ctx.Out.Write(DayForecast.RenderMarkdown(ctx.Data, forecast));

        return Program.Ok;
    }

    private static ForecastSubject Subject(CliContext ctx, NpcInstanceTable? instances, string text)
    {
        if (instances is null || !int.TryParse(text, out int id))
        {
            throw new ArgumentException($"NPC 번호가 잘못됐거나 명단이 없다: {text}");
        }

        int index = id - 1;

        if ((uint)index >= (uint)instances.Count || instances[index].Id != id)
        {
            throw new ArgumentException($"NPC {id} 가 없다.");
        }

        return DayForecast.Of(ctx.Data, instances[index]);
    }

    private static BucketKey Bucket(CliContext ctx, ArchetypeDef def)
    {
        if (Program.Flag(ctx, "--bucket") is { Length: > 0 } text)
        {
            if (!Npc.Planning.PlanStoreIo.TryParseBucket(text, ctx.Data, out BucketKey parsed))
            {
                throw new ArgumentException($"버킷을 읽지 못했다: {text}");
            }

            return parsed;
        }

        return ctx.Data.Fallbacks?.For(def.Code)?.Bucket
            ?? new BucketKey(def.Code, TimeOfDay.Dawn, RegionState.Peace, Climate.Fair);
    }

    internal static NpcInstanceTable? TryInstances(CliContext ctx)
    {
        string path = Path.Combine(ctx.MasterData, "npc_instances.json");

        return File.Exists(path) ? NpcInstanceTable.Load(path, ctx.Data) : null;
    }
}

/// <summary>
/// <c>npc lint archetype &lt;id&gt;|all</c> (T29).
///
/// <b>V1~V13 은 "기동이 되는가" 만 본다.</b> 검증을 통과하고도 이상한 정의가 있고,
/// 그것은 차단할 일이 아니라 알려 줄 일이다 — <b>카드 골든을 흔들지 않으려고 별도 명령이다.</b>
/// </summary>
public static class LintCommand
{
    private const string Usage = "사용법: npc lint archetype <id>|all";

    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ImmutableArray<string> args = Program.Positional(ctx);

        if (args.Length < 2 || args[0] != "archetype")
        {
            ctx.Out.WriteLine(Usage);
            return Program.BadUsage;
        }

        NpcInstanceTable? instances = ForecastCommand.TryInstances(ctx);

        ImmutableArray<ArchetypeDef> targets = string.Equals(args[1], "all", StringComparison.Ordinal)
            ? ctx.Data.Archetypes.Archetypes
            : [Program.Archetype(ctx, args[1])];

        int total = 0;

        foreach (ArchetypeDef def in targets)
        {
            ImmutableArray<LintFinding> findings = ArchetypeLint.Run(ctx.Data, def, instances);

            if (findings.IsEmpty)
            {
                if (targets.Length == 1)
                {
                    ctx.Out.WriteLine($"{Lexicon.Archetype(def.Id)} ({def.Id}) — 주의할 것이 없다.");
                }

                continue;
            }

            total += findings.Length;
            ctx.Out.WriteLine($"## {Lexicon.Archetype(def.Id)} (`{def.Id}`) — {findings.Length}건");
            ctx.Out.WriteLine();

            foreach (LintFinding finding in findings)
            {
                ctx.Out.WriteLine($"- **{finding.Code}** {finding.Title}");
                ctx.Out.WriteLine($"  - {finding.Detail}");
                ctx.Out.WriteLine($"  - 고치려면: {finding.Fix}");
            }

            ctx.Out.WriteLine();
        }

        if (targets.Length > 1)
        {
            ctx.Out.WriteLine(total == 0 ? "전체 아키타입에 주의할 것이 없다." : $"주의 {total}건.");
        }

        // 진단은 차단이 아니다 — 종료 코드를 0 으로 둔다.
        return Program.Ok;
    }
}
