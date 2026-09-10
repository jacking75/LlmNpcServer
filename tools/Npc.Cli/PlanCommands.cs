using System.Collections.Immutable;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;
using Npc.Narrative;
using Npc.Planning;
using Npc.Sim.Validation;

namespace Npc.Cli;

/// <summary>
/// <c>npc plan validate|explain|narrate &lt;파일&gt;</c> (F-01).
///
/// <b>검증기 4단을 그대로 부른다.</b> CLI 가 자기 판정을 만들면 터미널에서 통과한 플랜이
/// 기동에서 거절되는 상태가 생긴다.
/// </summary>
public static class PlanCommand
{
    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ImmutableArray<string> args = Program.Positional(ctx, "--bucket");

        if (args.Length < 2)
        {
            ctx.Out.WriteLine("사용법: npc plan validate|explain|narrate <파일> [--bucket <키>]");
            ctx.Out.WriteLine("  repair 는 아직 없다 — C-05 를 기다린다.");
            return Program.BadUsage;
        }

        string verb = args[0];
        string file = args[1];

        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"플랜 파일이 없다: {file}", file);
        }

        // 플랜 스토어 파일은 봉투에 싸여 있다 (schema·bucket·archetype·version·origin·plan).
        // LLM 이 낸 것은 문서 하나뿐이다. 둘 다 받는다 — 검수자가 어느 쪽을 줄지 알 수 없다.
        (string json, string? envelopeBucket) = Unwrap(File.ReadAllText(file));
        BucketKey bucket = Bucket(ctx, file, envelopeBucket);

        return verb switch
        {
            "validate" => Validate(ctx, json, bucket),
            "explain" => Explain(ctx, json, bucket),
            "narrate" => Explain(ctx, json, bucket),
            _ => Unknown(ctx, verb),
        };
    }

    private static int Unknown(CliContext ctx, string verb)
    {
        ctx.Out.WriteLine($"모르는 하위 명령: {verb}");
        ctx.Out.WriteLine("사용법: npc plan validate|explain|narrate <파일> [--bucket <키>]");
        return Program.BadUsage;
    }

    /// <summary>
    /// 버킷을 정한다. <c>--bucket</c> 이 있으면 그것, 없으면 <b>파일 이름에서 읽는다</b> —
    /// 플랜 스토어 파일명이 <c>blacksmith@Dawn.Peace.Fair.json</c> 이라 그것으로 충분하다.
    /// </summary>
    private static BucketKey Bucket(CliContext ctx, string file, string? envelopeBucket)
    {
        // 우선순위: 명시한 것 → 봉투가 적어 둔 것 → 파일 이름.
        foreach (string? text in new[] { Program.Flag(ctx, "--bucket"), envelopeBucket, Path.GetFileNameWithoutExtension(file) })
        {
            if (text is { Length: > 0 } && PlanStoreIo.TryParseBucket(text, ctx.Data, out BucketKey bucket))
            {
                return bucket;
            }
        }

        throw new ArgumentException(
            "버킷을 정할 수 없다. --bucket blacksmith@Dawn.Peace.Fair 처럼 준다 "
            + "(플랜 스토어 파일이면 봉투나 파일 이름에서 읽는다).");
    }

    /// <summary>
    /// 플랜 스토어 봉투를 벗긴다. 봉투가 아니면 원문을 그대로 돌려준다.
    /// </summary>
    /// <returns>플랜 문서 JSON 과, 봉투가 적어 둔 버킷 키 (없으면 null).</returns>
    private static (string Json, string? Bucket) Unwrap(string raw)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("plan", out JsonElement plan))
            {
                return (raw, null);
            }

            string? bucket = document.RootElement.TryGetProperty("bucket", out JsonElement key)
                && key.ValueKind == JsonValueKind.String
                    ? key.GetString()
                    : null;

            return (plan.GetRawText(), bucket);
        }
        catch (JsonException)
        {
            // 파싱이 안 되는 것은 1단 검증기가 판정한다 — 여기서 오류 문구를 따로 만들면
            // 같은 실패에 두 가지 메시지가 생긴다.
            return (raw, null);
        }
    }

    /// <summary>4단 전부. <b>어느 단에서 떨어졌는지와 코드를 낸다</b> — 그것이 고칠 근거다.</summary>
    private static int Validate(CliContext ctx, string json, BucketKey bucket)
    {
        ctx.Out.WriteLine($"버킷  {bucket.Format(ctx.Data.Archetypes[bucket.A].Id)}");

        // 1단 스키마
        ValidationResult result = SchemaValidator.Validate(json, out PlanDocument? document);

        if (!result.IsValid || document is null)
        {
            return Report(ctx, result, 1);
        }

        // 2단 어휘
        result = VocabularyValidator.Validate(document, bucket.A, ctx.Data);

        if (!result.IsValid)
        {
            return Report(ctx, result, 2);
        }

        // 3단 정합성
        result = CoherenceValidator.Validate(document, bucket, bucket.A, ctx.Data);

        if (!result.IsValid)
        {
            return Report(ctx, result, 3);
        }

        // 4단 드라이런 — 컴파일이 된 뒤에야 돌린다.
        CompiledPlan plan;

        try
        {
            plan = PlanCompiler.Compile(document, bucket, new PlanId(0), ctx.Data, PlanOrigin.Runtime);
        }
        catch (PlanCompilationException ex)
        {
            ctx.Out.WriteLine($"  FAIL 컴파일  {ex.Message}");
            return Program.Failed;
        }

        result = new DryRunValidator(ctx.Data).Validate(plan);

        if (!result.IsValid)
        {
            return Report(ctx, result, 4);
        }

        ctx.Out.WriteLine("  OK   4단 전부 통과");
        ctx.Out.WriteLine($"  목표 {plan.Goal} · 스텝 {plan.Steps.Length} · {(plan.Loop ? "loop" : "1회")}");

        return Program.Ok;
    }

    private static int Report(CliContext ctx, ValidationResult result, int stage)
    {
        ctx.Out.WriteLine($"  FAIL {stage}단 {result.Code}  (스텝 {result.StepIndex})");
        ctx.Out.WriteLine($"       {result.Detail}");

        if (Npc.MasterData.Validation.FixHints.For(result.Code) is { } hint)
        {
            ctx.Out.WriteLine($"       → {hint.Hint}");
        }

        return Program.Failed;
    }

    /// <summary>플랜을 사람 말로. 트레이스·수지·소요가 다 들어 있다 (F-03).</summary>
    private static int Explain(CliContext ctx, string json, BucketKey bucket)
    {
        ValidationResult result = SchemaValidator.Validate(json, out PlanDocument? document);

        if (!result.IsValid || document is null)
        {
            return Report(ctx, result, 1);
        }

        CompiledPlan plan = PlanCompiler.Compile(
            document, bucket, new PlanId(0), ctx.Data, PlanOrigin.Runtime);

        ctx.Out.Write(PlanExplain.Render(ctx.Data, plan));

        return Program.Ok;
    }
}

/// <summary>
/// <c>npc buckets</c> (F-01). 플랜 스토어의 상태를 표로.
///
/// <b>파일 시스템만 본다.</b> 서버에 붙지 않으므로 히트 수는 알 수 없다 —
/// 그것은 <c>serve metrics</c>(B-08)의 일이다.
/// </summary>
public static class BucketsCommand
{
    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        PlanStore store = PlanStore.CreateIdleOnly(ctx.Data);
        PlanStoreLoadReport report = PlanStoreIo.LoadAll(ctx.PlanStore, store, ctx.Data);

        string? only = Program.Flag(ctx, "--archetype");
        string? state = Program.Flag(ctx, "--state");

        ctx.Out.WriteLine($"planstore  {Path.GetFullPath(ctx.PlanStore)}");
        ctx.Out.WriteLine(
            $"  로드 {report.Loaded} · 핀 {report.Pinned} · 건너뜀 {report.Skipped} · 실패 {report.Failed}");
        ctx.Out.WriteLine($"  채워짐 {store.FilledBuckets} / {ctx.Data.Buckets.TotalKeys}");
        ctx.Out.WriteLine();

        foreach (ArchetypeDef def in ctx.Data.Archetypes.Archetypes)
        {
            if (only is not null && !string.Equals(only, def.Id, StringComparison.Ordinal))
            {
                continue;
            }

            int generated = 0;
            int pinned = 0;
            int fallback = 0;
            int missing = 0;

            for (int slot = 0; slot < BucketKey.PerArchetype; slot++)
            {
                var bucket = new BucketKey(
                    def.Code,
                    (TimeOfDay)(slot / (BucketKey.RegionStateCount * BucketKey.ClimateCount)),
                    (RegionState)(slot / BucketKey.ClimateCount % BucketKey.RegionStateCount),
                    (Climate)(slot % BucketKey.ClimateCount));

                if (!store.HasBucket(bucket))
                {
                    missing++;
                    continue;
                }

                switch (store.OriginOf(bucket))
                {
                    case PlanOrigin.Pinned:
                        pinned++;
                        break;

                    case PlanOrigin.Fallback:
                        fallback++;
                        break;

                    default:
                        generated++;
                        break;
                }
            }

            if (!Matches(state, generated, pinned, fallback, missing))
            {
                continue;
            }

            ctx.Out.WriteLine(
                $"  {def.Id,-20} 생성 {generated,3} · 핀 {pinned,3} · 폴백 {fallback,3} · 미생성 {missing,3}");
        }

        return Program.Ok;
    }

    /// <summary>필터. 없으면 전부 낸다.</summary>
    private static bool Matches(string? state, int generated, int pinned, int fallback, int missing) =>
        state switch
        {
            null => true,
            "missing" => missing > 0,
            "pinned" => pinned > 0,
            "fallback" => fallback > 0,
            "generated" => generated > 0,
            _ => throw new ArgumentException(
                $"--state 값이 잘못됐다: {state}. missing|pinned|fallback|generated 중 하나다."),
        };
}

/// <summary>
/// <c>npc pin &lt;버킷&gt;</c> (F-01). <c>plans/</c> 의 플랜을 <c>pinned/</c> 로 옮긴다.
///
/// <b><c>pinned/</c> 는 사람이 고친 플랜이다.</b> 잃으면 검수 작업이 날아가므로 커밋 대상이고,
/// 프리베이크가 다시 만들지 않는다.
/// </summary>
public static class PinCommand
{
    /// <summary>돌린다.</summary>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        ImmutableArray<string> args = Program.Positional(ctx);

        if (args.Length == 0)
        {
            ctx.Out.WriteLine("사용법: npc pin <버킷>   예: npc pin blacksmith@Dawn.Peace.Fair");
            return Program.BadUsage;
        }

        if (!PlanStoreIo.TryParseBucket(args[0], ctx.Data, out BucketKey bucket))
        {
            throw new ArgumentException($"버킷 키가 잘못됐다: '{args[0]}'");
        }

        string source = PlanStoreIo.PathOf(ctx.PlanStore, PlanLayer.Plans, bucket, ctx.Data);
        string target = PlanStoreIo.PathOf(ctx.PlanStore, PlanLayer.Pinned, bucket, ctx.Data);

        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"생성된 플랜이 없다: {source}", source);
        }

        if (File.Exists(target))
        {
            throw new InvalidOperationException(
                $"이미 핀 되어 있다: {target}. 덮어쓰려면 그 파일을 먼저 지운다 — "
                + "사람이 고친 것을 도구가 조용히 지우지 않는다.");
        }

        if (!ctx.Apply)
        {
            ctx.Out.WriteLine($"{source}");
            ctx.Out.WriteLine($"  → {target}");
            ctx.Out.WriteLine("dry-run 이다. 실제로 옮기려면 `--apply` 를 붙인다.");
            return Program.Ok;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // 복사가 아니라 이동이다 — 두 벌이 남으면 어느 쪽이 검수본인지 알 수 없다.
        File.Move(source, target);

        ctx.Out.WriteLine($"핀 완료 {target}");
        ctx.Out.WriteLine("이 파일은 커밋한다 — 잃으면 검수 작업이 날아간다 (CLAUDE.md §6).");

        return Program.Ok;
    }
}
