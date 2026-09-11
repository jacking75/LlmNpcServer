using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;
using Npc.Narrative;
using Npc.Planning;
using Npc.Sim.Validation;

namespace Npc.Cli.Review;

/// <summary>
/// <c>npc review</c> (F-06). 프리베이크된 플랜을 사람이 보고 판정한다.
///
/// <para>
/// <b>v1(<c>tools/review.ps1</c>)이 못 하던 넷을 고쳤다.</b>
/// (1) 판정에 필요한 정보를 보여 준다 — 플랜 설명(F-03)·아키타입 요약·<b>같은 아키타입의 다른
/// 버킷 3개</b>(다양성 대조)·같은 버킷의 반려 이력. (2) <c>[e] 수정</c> 이 실제로 플랜을 고치고
/// 4단 재검증 후 <c>pinned/</c> 에 저장한다. (3) <b>폴백으로 대체된 버킷도 표본에 든다</b>.
/// (4) 폐기 사유가 코드라 집계된다.
/// </para>
///
/// <para>
/// <b>판정 시간은 게이트에서 뺐다.</b> v1 은 시간을 절감률 게이트에 넣었는데, 그러면
/// <b>꼼꼼히 볼수록 성적이 나빠진다</b> — 검수를 대충 하게 만드는 지표는 지표가 아니다.
/// 기록은 계속 하되 보고용이다.
/// </para>
/// </summary>
public static class ReviewCommand
{
    /// <summary>다양성 대조로 보여 주는 같은 아키타입의 다른 버킷 수.</summary>
    public const int ContrastCount = 3;

    /// <summary>기본 기록 파일.</summary>
    public const string DefaultOut = "docs/measurements/review.jsonl";

    /// <summary>돌린다.</summary>
    /// <param name="ctx">CLI 맥락.</param>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        PlanStore store = PlanStore.CreateIdleOnly(ctx.Data);

        // <b>아키타입 폴백을 먼저 올린다.</b> 안 올리면 폴백 대체분이 전부 최후 플랜
        // (Wait>Emote>Rest)으로 보이고, 검수자는 진짜 폴백을 보지 못한다.
        if (ctx.Data.Fallbacks is { } fallbacks)
        {
            foreach (FallbackPlanEntry entry in fallbacks.Plans)
            {
                store.SetFallback(entry.Archetype, store.Register(entry.Plan));
            }
        }

        PlanStoreLoadReport report = PlanStoreIo.LoadAll(ctx.PlanStore, store, ctx.Data);

        int sample = IntFlag(ctx, "--sample", ReviewSampler.DefaultSample);
        int seed = IntFlag(ctx, "--seed", ReviewSampler.DefaultSeed);
        string? archetype = Program.Flag(ctx, "--archetype");
        string outPath = Program.Flag(ctx, "--out") ?? DefaultOut;

        ImmutableArray<ReviewSample> samples =
            ReviewSampler.Take(store, ctx.Data, sample, seed, archetype, ctx.PlanStore);

        ctx.Out.WriteLine($"planstore  {Path.GetFullPath(ctx.PlanStore)}");
        ctx.Out.WriteLine(
            $"  로드 {report.Loaded} · 핀 {report.Pinned} · 채워짐 {store.FilledBuckets}/{ctx.Data.Buckets.TotalKeys}");
        ctx.Out.WriteLine($"  표본 {samples.Length}건 (시드 {seed}) · 기록 {outPath}");
        ctx.Out.WriteLine($"  {Strata(samples)}");
        ctx.Out.WriteLine();

        if (Program.HasFlag(ctx, "--list"))
        {
            foreach (ReviewSample item in samples)
            {
                ctx.Out.WriteLine($"  {item.Origin,-9} {item.Name,-42} {Sequence(item.Plan, ctx.Data)}");
            }

            return Program.Ok;
        }

        // 비대화 경로 — 판정을 인자로 준다. 스크립트로 여러 건을 넘길 때와 테스트가 쓴다.
        if (Program.Flag(ctx, "--verdict") is { } verdictText)
        {
            return Decide(ctx, store, samples, verdictText, outPath);
        }

        return Interactive(ctx, store, samples, outPath);
    }

    /// <summary>층별 개수 한 줄.</summary>
    private static string Strata(ImmutableArray<ReviewSample> samples)
    {
        var parts = new List<string>();

        foreach (PlanOrigin origin in samples.Select(s => s.Origin).Distinct().Order())
        {
            parts.Add($"{origin} {samples.Count(s => s.Origin == origin)}");
        }

        return parts.Count == 0 ? "(표본 없음)" : string.Join(" · ", parts);
    }

    /// <summary>스텝 시퀀스 한 줄. 다양성 대조가 이것을 견준다.</summary>
    private static string Sequence(CompiledPlan plan, MasterDataSet data) =>
        string.Join(">", plan.Steps.Select(s => data.Actions[s.Action].Id));

    /// <summary>
    /// 비대화 판정. <c>--bucket</c> 하나에 <c>--verdict accept|edit|reject</c> 를 준다.
    ///
    /// <para>
    /// <b>사람의 판정을 대신하지 않는다.</b> 판정은 인자로 들어오고, 이 경로가 하는 일은
    /// 저장과 기록뿐이다 — 검수 회차를 스크립트로 나눠 돌릴 때 필요하다.
    /// </para>
    /// </summary>
    private static int Decide(
        CliContext ctx,
        PlanStore store,
        ImmutableArray<ReviewSample> samples,
        string verdictText,
        string outPath)
    {
        string? bucketName = Program.Flag(ctx, "--bucket");

        if (bucketName is null)
        {
            ctx.Out.WriteLine("--verdict 를 쓰려면 --bucket 도 준다.");
            return Program.BadUsage;
        }

        ReviewSample item = samples.FirstOrDefault(s =>
            string.Equals(s.Name, bucketName, StringComparison.Ordinal));

        if (item.Name is null or "")
        {
            // 표본 밖이어도 검수할 수 있어야 한다 — 표본은 권고이지 울타리가 아니다.
            //
            // <b>HasBucket 을 요구하지 않는다.</b> 폴백으로 대체된 버킷이야말로 검수 대상이고,
            // Resolve 는 절대 null 을 주지 않는다 (CLAUDE.md §2.6).
            if (!PlanStoreIo.TryParseBucket(bucketName, ctx.Data, out BucketKey bucket))
            {
                ctx.Out.WriteLine($"버킷 키가 잘못됐다: {bucketName}");
                return Program.Failed;
            }

            CompiledPlan plan = store.Resolve(bucket, out PlanOrigin origin);
            item = new ReviewSample(bucket, bucketName, origin, plan);
        }

        ReviewVerdict verdict = verdictText switch
        {
            "accept" => ReviewVerdict.Accept,
            "edit" => ReviewVerdict.Edit,
            "reject" => ReviewVerdict.Reject,
            _ => ReviewVerdict.Skip,
        };

        if (verdict == ReviewVerdict.Skip)
        {
            ctx.Out.WriteLine($"--verdict 값이 잘못됐다: {verdictText}. accept|edit|reject 중 하나다.");
            return Program.BadUsage;
        }

        string note = Program.Flag(ctx, "--note") ?? string.Empty;
        RejectReason reason = ParseReason(Program.Flag(ctx, "--reason"));

        if (verdict == ReviewVerdict.Reject && reason == RejectReason.None)
        {
            ctx.Out.WriteLine("폐기에는 --reason 이 필요하다. " + ReasonList());
            return Program.BadUsage;
        }

        int edited = 0;

        if (verdict is ReviewVerdict.Accept or ReviewVerdict.Edit)
        {
            string? file = Program.Flag(ctx, "--plan");

            if (verdict == ReviewVerdict.Edit && file is null)
            {
                ctx.Out.WriteLine("--verdict edit 에는 고친 플랜 파일(--plan)이 필요하다.");
                return Program.BadUsage;
            }

            if (!Pin(ctx, item, file, note, out edited, out string error))
            {
                ctx.Out.WriteLine(error);
                return Program.Failed;
            }
        }

        Append(outPath, new ReviewRecord(
            item.Name,
            verdict,
            DoubleFlag(ctx, "--minutes", 0),
            note,
            reason,
            edited,
            item.Origin.ToString()));

        ctx.Out.WriteLine($"{item.Name} → {ReviewRecord.Text(verdict)} 기록 ({outPath})");

        return Program.Ok;
    }

    /// <summary>대화 검수. 한 건씩 보여 주고 판정을 받는다.</summary>
    private static int Interactive(
        CliContext ctx, PlanStore store, ImmutableArray<ReviewSample> samples, string outPath)
    {
        int reviewed = 0;
        int pinned = 0;

        foreach (ReviewSample item in samples)
        {
            Present(ctx, store, item);

            var watch = Stopwatch.StartNew();

            (ReviewVerdict verdict, string note, RejectReason reason, string? planFile) = Ask(ctx);

            if (verdict == ReviewVerdict.Skip)
            {
                if (note == "quit")
                {
                    break;
                }

                continue;
            }

            watch.Stop();

            int edited = 0;

            if (verdict is ReviewVerdict.Accept or ReviewVerdict.Edit)
            {
                if (!Pin(ctx, item, planFile, note, out edited, out string error))
                {
                    ctx.Out.WriteLine(error);
                    ctx.Out.WriteLine("  이 건은 기록하지 않는다 — 저장에 실패한 판정은 판정이 아니다.");
                    continue;
                }

                pinned++;
            }

            Append(outPath, new ReviewRecord(
                item.Name,
                verdict,
                Math.Round(watch.Elapsed.TotalMinutes, 2),
                note,
                reason,
                edited,
                item.Origin.ToString()));

            reviewed++;
        }

        ctx.Out.WriteLine();
        ctx.Out.WriteLine($"검수 {reviewed}건 · 핀 {pinned}건 · 기록 {outPath}");
        ctx.Out.WriteLine("pinned/ 는 커밋한다 — 잃으면 검수 작업이 날아간다 (CLAUDE.md §6).");

        return Program.Ok;
    }

    /// <summary>한 건을 보여 준다. <b>판정에 필요한 것을 전부 한 화면에</b>.</summary>
    private static void Present(CliContext ctx, PlanStore store, ReviewSample item)
    {
        ctx.Out.WriteLine(new string('=', 92));
        ctx.Out.WriteLine($"{item.Name}   [{item.Origin}]");
        ctx.Out.WriteLine();

        ctx.Out.Write(PlanExplain.Render(ctx.Data, item.Plan));
        ctx.Out.WriteLine();

        ArchetypeDef def = ctx.Data.Archetypes[item.Bucket.A];

        ctx.Out.WriteLine(
            $"아키타입  {def.Id} · 근면 {def.Traits.Diligence} · 사교 {def.Traits.Sociability} "
            + $"· 용기 {def.Traits.Courage} · 탐욕 {def.Traits.Greed}");
        ctx.Out.WriteLine($"  목표    {string.Join(" · ", def.DefaultGoals)}");
        ctx.Out.WriteLine();

        // <b>다양성은 한 건만 봐서는 못 본다.</b> 같은 아키타입의 다른 버킷과 견줘야
        // "전부 같은 시퀀스" 를 알아챌 수 있다 — v1 이 원리적으로 못 하던 것이다.
        ctx.Out.WriteLine($"같은 아키타입의 다른 버킷 {ContrastCount}개 (다양성 대조)");

        foreach (ReviewSample other in Contrast(store, ctx.Data, item))
        {
            ctx.Out.WriteLine($"  {other.Name,-42} {Sequence(other.Plan, ctx.Data)}");
        }

        ctx.Out.WriteLine();

        string[] history = RejectedHistory(ctx, item);

        if (history.Length > 0)
        {
            ctx.Out.WriteLine($"이 버킷의 반려 이력 {history.Length}건");

            foreach (string line in history.Take(3))
            {
                ctx.Out.WriteLine($"  {line}");
            }

            ctx.Out.WriteLine();
        }
    }

    /// <summary>같은 아키타입의 다른 버킷. 시드 없이 <b>버킷 순서 고정</b>이다.</summary>
    private static ImmutableArray<ReviewSample> Contrast(
        PlanStore store, MasterDataSet data, ReviewSample item) =>
        [
            .. ReviewSampler
                .Population(store, data, data.Archetypes[item.Bucket.A].Id)
                .Where(s => !string.Equals(s.Name, item.Name, StringComparison.Ordinal))
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Take(ContrastCount),
        ];

    /// <summary>이 버킷의 반려 이력. <c>rejected/</c> 파일 이름이 <c>{버킷}.{시도}.json</c> 이다.</summary>
    private static string[] RejectedHistory(CliContext ctx, ReviewSample item)
    {
        string directory = Path.Combine(ctx.PlanStore, PlanStoreIo.FolderOf(PlanLayer.Rejected));

        if (!Directory.Exists(directory))
        {
            return [];
        }

        var lines = new List<string>();

        foreach (string path in Directory
            .EnumerateFiles(directory, item.Name + ".*.json")
            .Order(StringComparer.Ordinal))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

                string code = document.RootElement.TryGetProperty("code", out var c)
                    ? c.GetString() ?? string.Empty
                    : string.Empty;

                string detail = document.RootElement.TryGetProperty("detail", out var d)
                    ? d.GetString() ?? string.Empty
                    : string.Empty;

                lines.Add($"{code,-26} {Trim(detail, 60)}");
            }
            catch (System.Text.Json.JsonException)
            {
                // 반려 기록이 깨져 있어도 검수는 계속한다 — 이것은 참고 자료다.
            }
        }

        return [.. lines];
    }

    private static string Trim(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";

    /// <summary>판정을 묻는다.</summary>
    private static (ReviewVerdict Verdict, string Note, RejectReason Reason, string? PlanFile) Ask(
        CliContext ctx)
    {
        while (true)
        {
            ctx.Out.Write("[a] 채택  [e] 고쳐서 채택  [r] 폐기  [s] 건너뜀  [q] 끝  > ");

            string key = (Console.ReadLine() ?? "q").Trim().ToLowerInvariant();

            switch (key)
            {
                case "a":
                    return (ReviewVerdict.Accept, Prompt("한 줄 (없으면 비움)"), RejectReason.None, null);

                case "e":
                {
                    ctx.Out.WriteLine("  고친 플랜 파일 경로를 준다. (`npc plan repair` 결과를 저장해 두면 편하다)");
                    string file = Prompt("파일");

                    if (file.Length == 0)
                    {
                        ctx.Out.WriteLine("  파일이 없으면 수정으로 기록하지 않는다.");
                        continue;
                    }

                    return (ReviewVerdict.Edit, Prompt("무엇을 고쳤나"), RejectReason.None, file);
                }

                case "r":
                {
                    ctx.Out.WriteLine("  " + ReasonList());
                    RejectReason reason = ParseReason(Prompt("사유 번호"));

                    if (reason == RejectReason.None)
                    {
                        ctx.Out.WriteLine("  사유 없는 폐기는 집계가 안 된다. 1~4 중 하나를 고른다.");
                        continue;
                    }

                    return (ReviewVerdict.Reject, Prompt("한 줄"), reason, null);
                }

                case "s":
                    return (ReviewVerdict.Skip, string.Empty, RejectReason.None, null);

                case "q":
                    return (ReviewVerdict.Skip, "quit", RejectReason.None, null);

                default:
                    ctx.Out.WriteLine("  a / e / r / s / q 중 하나를 넣는다.");
                    break;
            }
        }
    }

    private static string Prompt(string label)
    {
        Console.Write($"  {label}: ");
        return (Console.ReadLine() ?? string.Empty).Trim();
    }

    private static string ReasonList() =>
        string.Join("  ", ReviewRecord.Reasons.Select(r => $"[{(int)r.Reason}] {r.Label.Split(' ')[0]}"));

    private static RejectReason ParseReason(string? text) => text switch
    {
        "1" or "situation" => RejectReason.Situation,
        "2" or "character" => RejectReason.Character,
        "3" or "route" => RejectReason.Route,
        "4" or "survival" => RejectReason.Survival,
        _ => RejectReason.None,
    };

    /// <summary>
    /// <c>pinned/</c> 에 저장한다. <b>고친 플랜은 4단을 다시 지난다</b> —
    /// 검수를 통과했다는 말은 검증을 건너뛴다는 뜻이 아니다 (CLAUDE.md §8).
    /// </summary>
    private static bool Pin(
        CliContext ctx,
        ReviewSample item,
        string? planFile,
        string note,
        out int editedSteps,
        out string error)
    {
        editedSteps = 0;
        error = string.Empty;

        CompiledPlan plan = item.Plan;

        if (planFile is not null)
        {
            if (!File.Exists(planFile))
            {
                error = $"  고친 플랜 파일이 없다: {planFile}";
                return false;
            }

            string json = Unwrap(File.ReadAllText(planFile));

            ValidationResult result = SchemaValidator.Validate(json, out PlanDocument? document);

            if (!result.IsValid || document is null)
            {
                error = $"  1단 {result.Code}  {result.Detail}";
                return false;
            }

            result = VocabularyValidator.Validate(document, item.Bucket.A, ctx.Data);

            if (!result.IsValid)
            {
                error = $"  2단 {result.Code}  {result.Detail}";
                return false;
            }

            result = CoherenceValidator.Validate(document, item.Bucket, item.Bucket.A, ctx.Data);

            if (!result.IsValid)
            {
                error = $"  3단 {result.Code}  {result.Detail}";
                return false;
            }

            try
            {
                plan = PlanCompiler.Compile(
                    document, item.Bucket, new PlanId(0), ctx.Data, PlanOrigin.Pinned);
            }
            catch (PlanCompilationException ex)
            {
                error = $"  컴파일 실패  {ex.Message}";
                return false;
            }

            result = new DryRunValidator(ctx.Data).Validate(plan);

            if (!result.IsValid)
            {
                error = $"  4단 {result.Code}  {result.Detail}";
                return false;
            }

            editedSteps = CountChanges(item.Plan, plan);
        }
        else
        {
            plan = plan with { Origin = PlanOrigin.Pinned };
        }

        if (!ctx.Apply)
        {
            ctx.Out.WriteLine(
                $"  dry-run — `--apply` 를 붙이면 pinned/ 에 저장한다 "
                + $"({(editedSteps > 0 ? editedSteps + "스텝 수정" : "그대로")})");
            return true;
        }

        PlanStoreIo.SavePlan(ctx.PlanStore, PlanLayer.Pinned, item.Bucket, plan, ctx.Data);

        ctx.Out.WriteLine(
            $"  핀 저장 {PlanStoreIo.PathOf(ctx.PlanStore, PlanLayer.Pinned, item.Bucket, ctx.Data)}"
            + (note.Length > 0 ? $" · {note}" : string.Empty));

        return true;
    }

    /// <summary>봉투째 온 파일에서 플랜 문서만 꺼낸다.</summary>
    private static string Unwrap(string raw)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(raw);

            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && document.RootElement.TryGetProperty("plan", out var plan)
                    ? plan.GetRawText()
                    : raw;
        }
        catch (System.Text.Json.JsonException)
        {
            return raw;
        }
    }

    /// <summary>고친 스텝 수. 길이가 다르면 그 차이도 센다.</summary>
    private static int CountChanges(CompiledPlan before, CompiledPlan after)
    {
        int changes = Math.Abs(before.Steps.Length - after.Steps.Length);
        int common = Math.Min(before.Steps.Length, after.Steps.Length);

        for (int i = 0; i < common; i++)
        {
            if (!before.Steps[i].Equals(after.Steps[i]))
            {
                changes++;
            }
        }

        return changes;
    }

    /// <summary>정수 플래그. 없거나 못 읽으면 기본값.</summary>
    private static int IntFlag(CliContext ctx, string name, int fallback) =>
        Program.Flag(ctx, name) is { } text
        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    /// <summary>실수 플래그. 없거나 못 읽으면 기본값.</summary>
    private static double DoubleFlag(CliContext ctx, string name, double fallback) =>
        Program.Flag(ctx, name) is { } text
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;

    private static void Append(string path, ReviewRecord record)
    {
        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(path, record.ToJsonLine() + "\n", Encoding.UTF8);
    }
}
