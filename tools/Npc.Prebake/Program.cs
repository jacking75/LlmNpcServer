using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Npc.Core;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;
using Npc.Prebake;

// 프리베이크 CLI. docs/13 §4.
//
// T2-19 의 전량 생성 러너가 여기로 승격됐다. 옵션 파싱은 PrebakeOptions 가 하고
// 이 파일은 조립만 한다 — 흐름은 docs/13 §4 의 8단계 그대로다.

if (!PrebakeOptions.TryParse(args, out PrebakeOptions options, out string? parseError))
{
    Console.Error.WriteLine(parseError);
    Console.Error.WriteLine();
    Console.Error.WriteLine(PrebakeOptions.Usage);
    return 2;
}

if (options.Help)
{
    Console.Out.WriteLine(PrebakeOptions.Usage);
    return 0;
}

// 1. 마스터데이터 로드 + V1~V11 검증 (로더가 검증까지 한다)
MasterDataSet data = MasterDataLoader.Load(options.MasterData);
PromptPrefix prefix = PromptPrefix.Build(data, options.MasterData);

if (options.PrintPrefix)
{
    foreach (SchemaProfile profile in new[] { SchemaProfile.Full, SchemaProfile.Bare })
    {
        PromptPrefix p = PromptPrefix.Build(data, options.MasterData, profile);

        Console.WriteLine($"--- {profile}: {p.TokenCount} tok · sha {p.Sha256[..16]}");

        foreach (PrefixSection section in p.Sections)
        {
            Console.WriteLine($"    {section.Name,-16} {section.Tokens,6}");
        }
    }

    return 0;
}

// 2. 기존 manifest 와 비교 → 무효화 범위 판정
Manifest? previous = Manifest.LoadFrom(options.Out);
InvalidationScope scope = PlanStoreValidator.Compare(
    previous, data, prefix.Sha256, out var changedFiles);

Console.WriteLine($"masterdata : {options.MasterData}");
Console.WriteLine($"planstore  : {options.Out}");
Console.WriteLine($"prefix     : {prefix.TokenCount} tok · {prefix.Sha256[..16]}");
Console.WriteLine(
    $"무효화     : {scope}"
    + (changedFiles.IsEmpty ? string.Empty : $" (바뀐 파일: {string.Join(", ", changedFiles)})"));

// 3. 생성 대상 버킷 목록 산출. --resume·Partial 은 기존 스토어를 봐야 한다
PlanStore existing = PlanStore.CreateIdleOnly(data);
PlanStoreLoadReport loaded = PlanStoreIo.LoadAll(options.Out, existing, data);

if (loaded.Total > 0 || loaded.Errors.Length > 0)
{
    Console.WriteLine(
        $"기존 스토어: {loaded.Loaded} + pinned {loaded.Pinned}"
        + (loaded.Skipped + loaded.Failed > 0 ? $" · 건너뜀 {loaded.Skipped} · 실패 {loaded.Failed}" : string.Empty));
}

TargetSelection selection = TargetSelector.Select(options, data, existing, scope);
ImmutableArray<BucketKey> buckets = selection.Buckets;

Console.WriteLine(
    $"대상       : {selection.Count} 버킷 ({selection.Mode})"
    + (selection.SkippedPinned > 0 ? $" · pinned {selection.SkippedPinned} 건 제외" : string.Empty));

if (options.Plan)
{
    foreach (BucketKey bucket in buckets.Take(40))
    {
        Console.WriteLine($"  {bucket.Format(data.Archetypes[bucket.A].Id)}");
    }

    if (buckets.Length > 40)
    {
        Console.WriteLine($"  … 그리고 {buckets.Length - 40} 개 더");
    }

    return 0;
}

if (buckets.Length == 0)
{
    Console.WriteLine("생성할 것이 없다.");
    return 0;
}

LlmOptions llm = LlmOptions.LoadDefault(Directory.GetCurrentDirectory());
LlmEngineOptions engine = llm.Engine(options.Model);

if (!engine.IsConfigured)
{
    Console.Error.WriteLine($"{engine.ApiKeyEnv} 환경변수가 없다. 엔진: {engine.Id}");
    return 2;
}

// T3-11 실측 — 워밍업 유무 비용 차이. 두 회차의 프리픽스를 소금으로 갈라 둘 다 콜드로 만든다.
if (options.WarmupExperiment > 0)
{
    using IChatClient probe = ChatClientFactory.Create(engine);

    BucketKey sample = buckets[0];
    string suffix = PlanRequestSuffix.Build(new PlanRequest(sample, data.InitialFlags(sample)), data);

    Console.WriteLine($"engine     : {engine.Id}");
    Console.WriteLine($"실험       : 동시 {options.WarmupExperiment} · 버킷 {sample.Format(data.Archetypes[sample.A].Id)}");
    Console.WriteLine();

    // 소금에 --generated-at 을 섞는다. 회차마다 다른 값을 주지 않으면 두 번째 실행이
    // 첫 번째가 만들어 둔 캐시를 물려받아 "워밍업 없음" 쪽이 이겨 버린다 (실측으로 확인했다).
    string run = options.GeneratedAt.Length > 0 ? options.GeneratedAt : "default";

    if (options.GeneratedAt.Length == 0)
    {
        Console.WriteLine("warn: --generated-at 를 주지 않았다. 같은 소금이라 두 번째 실행부터 캐시가 오염된다.");
    }

    WarmupExperiment without = await Warmup.MeasureAsync(
        probe, engine, prefix.Text, prefix.Schema, suffix,
        options.WarmupExperiment, withWarmup: false, salt: $"// prebake warmup experiment {run}: A\n");

    WarmupExperiment with = await Warmup.MeasureAsync(
        probe, engine, prefix.Text, prefix.Schema, suffix,
        options.WarmupExperiment, withWarmup: true, salt: $"// prebake warmup experiment {run}: B\n");

    Console.WriteLine($"워밍업 없음 : 동시 {without.Concurrency} · 캐시 {without.WaveCachedTokens}/{without.WavePromptTokens} tok"
        + $" ({without.WaveCacheHitRate:P1}) · ${without.TotalCostUsd:F6}");
    Console.WriteLine($"워밍업 있음 : 단건 1 + 동시 {with.Concurrency} · 캐시 {with.WaveCachedTokens}/{with.WavePromptTokens} tok"
        + $" ({with.WaveCacheHitRate:P1}) · ${with.TotalCostUsd:F6}");

    double saved = without.TotalCostUsd - with.TotalCostUsd;

    Console.WriteLine(
        $"차이       : ${saved:F6}"
        + (without.TotalCostUsd > 0 ? $" ({saved / without.TotalCostUsd:P1})" : string.Empty)
        + $" · 요청 {without.Concurrency} vs {with.Concurrency + 1}");

    return 0;
}

Console.WriteLine($"engine     : {engine.Id} (forced={engine.ForceJsonSchema})");
Console.WriteLine($"concurrency: {options.Concurrency} (AIMD, 상한 {options.MaxConcurrency})");
Console.WriteLine($"budget     : ${options.BudgetUsd:F2}");
Console.WriteLine();

// 예산 하드 캡. 추정은 보수적으로 — 캐시가 전부 적중한다고 보면 캡을 넘긴 뒤에야 멈춘다.
var budget = new BudgetGuard(
    options.BudgetUsd,
    BudgetGuard.EstimateFor(engine, prefix.TokenCount));

var runner = new BulkRunner(
    data,
    prefix,
    engine,
    new BulkRunOptions(
        Concurrency: options.Concurrency,
        MaxConcurrency: options.MaxConcurrency,
        PlanStoreDirectory: options.Out,
        DryRunSample: options.DryRunSample,
        Budget: budget));

// 5. 프리픽스 워밍업 1회 → 6. 동시 N 워커.
//    순서가 뒤집히면 캐시 write 를 동시성 수만큼 낸다 (docs/13 §4 · §8).
using IChatClient warmupClient = ChatClientFactory.Create(engine);

(WarmupResult warmup, BulkRunReport report) = await Warmup.RunThenAsync(
    ct => Warmup.RunAsync(warmupClient, engine, prefix, data, buckets[0], cancellationToken: ct),
    ct => runner.RunAsync(
        buckets,
        () => ChatClientFactory.Create(engine),
        (done, total, message) =>
        {
            if (done % 25 == 0 || done == total)
            {
                Console.WriteLine($"  [{done,5}/{total}] {message}");
            }
        },
        ct),
    CancellationToken.None);

Console.WriteLine(
    warmup.Attempted
        ? $"워밍업        : {(warmup.Succeeded ? "성공" : "실패 — " + warmup.Error)} · "
            + $"{warmup.LatencyMs:F0}ms · {warmup.CachedTokens}/{warmup.PromptTokens} tok 캐시 · ${warmup.CostUsd:F6}"
        : "워밍업        : 건너뜀 (로컬 엔진은 cached_tokens 를 보고하지 않는다)");

// 7. 통과한 것을 plans/ 에 쓴다. pinned 는 건드리지 않는다
int written = PlanStoreIo.SaveAll(options.Out, runner.Store, data);

WriteJsonl(options.Report, report, data);

Console.WriteLine();

if (report.StoppedByBudget)
{
    Console.WriteLine($"⚠ {budget.StopMessage(report.AttemptedCount, report.Total)}");
}

Console.WriteLine($"통과율        : {report.Passed}/{report.AttemptedCount} ({report.PassRate:P1})");
Console.WriteLine($"1회 통과      : {report.PassedFirstAttempt}");
Console.WriteLine($"인접 재사용   : {report.Reused}");
Console.WriteLine($"폴백          : {report.FellBack} ({report.FallbackRate:P1})");
Console.WriteLine($"캐시 적중률   : {report.CacheHitRate:P1} ({report.CachedTokens}/{report.PromptTokens} tok)");
Console.WriteLine($"평균 지연     : {report.AverageLatencyMs:F0} ms");
Console.WriteLine($"비용          : ${report.CostUsd:F4}");
Console.WriteLine($"소요          : {report.WallClockSeconds:F1}s");
Console.WriteLine($"429 최초 동시성: {report.FirstRateLimitConcurrency} (총 {report.RateLimitHits}회, 최대 동시성 {report.PeakConcurrency})");
Console.WriteLine($"프리픽스 해시  : {runner.Stats.UniquePrefixHashes} 종");
Console.WriteLine($"저장          : {written} 건 → {Path.Combine(options.Out, "plans")}");
Console.WriteLine($"결과          : {options.Report}");

return report.PassRate >= 0.90 ? 0 : 1;

static void WriteJsonl(string path, BulkRunReport report, MasterDataSet data)
{
    if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
    {
        Directory.CreateDirectory(directory);
    }

    var sb = new StringBuilder(64 * 1024);

    foreach (BucketOutcome outcome in report.Outcomes)
    {
        string bucket = outcome.Bucket.Format(data.Archetypes[outcome.Bucket.A].Id);

        sb.Append(CultureInfo.InvariantCulture, $$"""
            {"bucket":"{{bucket}}","archetype":"{{data.Archetypes[outcome.Bucket.A].Id}}","ok":{{(outcome.Validation.IsValid ? "true" : "false")}},"stage":"{{outcome.Validation.FailedAt}}","code":"{{outcome.Validation.Code}}","step":{{outcome.Validation.StepIndex}},"attempt":{{outcome.Stats.Attempt}},"origin":"{{outcome.Origin}}","goal":"{{outcome.Goal}}","actions":"{{string.Join('>', outcome.Actions)}}","prompt_tokens":{{outcome.Stats.PromptTokens}},"cached_tokens":{{outcome.Stats.CachedTokens}},"completion_tokens":{{outcome.Stats.CompletionTokens}},"latency_ms":{{outcome.Stats.LatencyMs.ToString("F1", CultureInfo.InvariantCulture)}},"cost_usd":{{outcome.Stats.CostUsd.ToString("F8", CultureInfo.InvariantCulture)}},"model":"{{outcome.Stats.Model}}","forced":{{(outcome.Stats.Forced ? "true" : "false")}}}
            """);
        sb.Append('\n');
    }

    File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
}
