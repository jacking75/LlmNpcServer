using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Npc.Core;
using Npc.Llm;
using Npc.MasterData;
using Npc.Prebake;

// T2-19 전량 생성 러너의 실행 진입점.
// P3(T3-08)에서 정식 프리베이크 CLI 로 승격된다 — 지금은 회차를 돌리고 집계를 남기는 데 필요한 만큼만 있다.

string masterData = "./masterdata";
string? engineId = null;
string planStore = "./planstore";
string outPath = "./docs/measurements/W6_run.jsonl";
int limit = 0;
int concurrency = 8;
int stride = 1;
bool printPrefix = false;
int archetypes = 0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--masterdata" when i + 1 < args.Length:
            masterData = args[++i];
            break;

        case "--engine" when i + 1 < args.Length:
            engineId = args[++i];
            break;

        case "--planstore" when i + 1 < args.Length:
            planStore = args[++i];
            break;

        case "--out" when i + 1 < args.Length:
            outPath = args[++i];
            break;

        case "--limit" when i + 1 < args.Length:
            limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;

        case "--stride" when i + 1 < args.Length:
            stride = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;

        case "--concurrency" when i + 1 < args.Length:
            concurrency = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;

        case "--archetypes" when i + 1 < args.Length:
            archetypes = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;

        case "--print-prefix":
            printPrefix = true;
            break;

        case "--help" or "-h":
            Console.WriteLine(
                """
                Npc.Prebake — 전량 생성 러너 (T2-19)

                  --masterdata <dir>   기본 ./masterdata
                  --engine <id>        appsettings.Llm.json 의 엔진 id. 없으면 default
                  --planstore <dir>    산출물 위치. 기본 ./planstore
                  --out <file>         버킷별 결과 JSONL. 기본 ./docs/measurements/W6_run.jsonl
                  --limit <n>          앞에서 n 개만 (0 = 전량 2,880)
                  --stride <n>         버킷을 n 간격으로 골라 표본을 흩는다 (기본 1)
                  --concurrency <n>    시작 동시성. 기본 8 — AIMD 로 올린다
                  --archetypes <n>     앞 n 개 아키타입의 72버킷을 전부 (연속 슬라이스).
                                       인접 버킷 재사용을 실제로 태우려면 연속이어야 한다
                  --print-prefix       프리픽스 절별 토큰 수만 찍고 끝낸다 (W6_compile_stats §1)
                """);
            return 0;

        default:
            Console.Error.WriteLine($"모르는 인자: {args[i]}");
            return 2;
    }
}

MasterDataSet data = MasterDataLoader.Load(masterData);
PromptPrefix prefix = PromptPrefix.Build(data, masterData);

if (printPrefix)
{
    foreach (SchemaProfile profile in new[] { SchemaProfile.Full, SchemaProfile.Bare })
    {
        PromptPrefix p = PromptPrefix.Build(data, masterData, profile);

        Console.WriteLine($"--- {profile}: {p.TokenCount} tok · sha {p.Sha256[..16]}");

        foreach (PrefixSection section in p.Sections)
        {
            Console.WriteLine($"    {section.Name,-16} {section.Tokens,6}");
        }
    }

    return 0;
}

LlmOptions options = LlmOptions.LoadDefault(Directory.GetCurrentDirectory());
LlmEngineOptions engine = options.Engine(engineId);

if (!engine.IsConfigured)
{
    Console.Error.WriteLine($"{engine.ApiKeyEnv} 환경변수가 없다. 엔진: {engine.Id}");
    return 2;
}

List<BucketKey> buckets = [.. BulkRunner.AllBuckets()];

if (stride > 1)
{
    // 2,880 과 서로소인 stride 를 주면 표본이 전 아키타입에 흩어진다. 난수를 쓰지 않는다.
    buckets = [.. Enumerable.Range(0, BucketKey.TotalKeys)
        .Select(i => BucketKey.FromIndex(i * stride % BucketKey.TotalKeys))
        .Distinct()];
}

// 연속 슬라이스. 인접 버킷(같은 아키타입의 다른 상황)이 스토어에 들어와야
// 재사용 경로가 동작한다 — 흩어진 표본으로는 폴백 비율을 잴 수 없다.
if (archetypes > 0)
{
    int perArchetype = BucketKey.TimeOfDayCount * BucketKey.RegionStateCount * BucketKey.ClimateCount;

    buckets = [.. Enumerable.Range(0, Math.Min(archetypes, BucketKey.ArchetypeCount) * perArchetype)
        .Select(BucketKey.FromIndex)];
}

if (limit > 0 && limit < buckets.Count)
{
    buckets = [.. buckets.Take(limit)];
}

Console.WriteLine($"engine     : {engine.Id} (forced={engine.ForceJsonSchema})");
Console.WriteLine($"prefix     : {prefix.TokenCount} tok · {prefix.Sha256[..16]}");
Console.WriteLine($"buckets    : {buckets.Count}");
Console.WriteLine($"concurrency: {concurrency} (AIMD)");
Console.WriteLine();

var runner = new BulkRunner(
    data, prefix, engine,
    new BulkRunOptions(Concurrency: concurrency, PlanStoreDirectory: planStore));

BulkRunReport report = await runner.RunAsync(
    buckets,
    () => ChatClientFactory.Create(engine),
    (done, total, message) =>
    {
        if (done % 25 == 0 || done == total)
        {
            Console.WriteLine($"  [{done,5}/{total}] {message}");
        }
    },
    CancellationToken.None);

WriteJsonl(outPath, report, data);

Console.WriteLine();
Console.WriteLine($"통과율        : {report.Passed}/{report.Total} ({report.PassRate:P1})");
Console.WriteLine($"1회 통과      : {report.PassedFirstAttempt}");
Console.WriteLine($"인접 재사용   : {report.Reused}");
Console.WriteLine($"폴백          : {report.FellBack} ({report.FallbackRate:P1})");
Console.WriteLine($"캐시 적중률   : {report.CacheHitRate:P1} ({report.CachedTokens}/{report.PromptTokens} tok)");
Console.WriteLine($"평균 지연     : {report.AverageLatencyMs:F0} ms");
Console.WriteLine($"비용          : ${report.CostUsd:F4}");
Console.WriteLine($"소요          : {report.WallClockSeconds:F1}s");
Console.WriteLine($"429 최초 동시성: {report.FirstRateLimitConcurrency} (총 {report.RateLimitHits}회, 최대 동시성 {report.PeakConcurrency})");
Console.WriteLine($"프리픽스 해시  : {runner.Stats.UniquePrefixHashes} 종");
Console.WriteLine($"결과          : {outPath}");

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
