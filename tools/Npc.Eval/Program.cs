using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.AI;
using Npc.Core;
using Npc.Eval;
using Npc.Eval.Core;
using Npc.Llm;
using Npc.MasterData;
using Npc.Prebake;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(EvalOptions.Usage);
    return 0;
}

if (!EvalOptions.TryParse(args, out EvalOptions options, out string? error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(EvalOptions.Usage);
    return 2;
}

MasterDataSet data;
PromptPrefix prefix;
LlmOptions llm;

try
{
    data = MasterDataLoader.Load(options.MasterData);
    prefix = PromptPrefix.Build(data, options.MasterData);
    llm = LlmOptions.LoadDefault();
}
catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
{
    Console.Error.WriteLine($"기동 실패: {e.Message}");
    return 2;
}

// 표본은 <b>한 번만</b> 뽑는다. 엔진마다 새로 뽑으면 통과율 차이가 모델 차이인지
// 표본 차이인지 구분되지 않는다 — 대조표가 그 순간 거짓이 된다.
ImmutableArray<BucketKey> buckets = EvalRunner.Sample(data.Buckets, options.Sample);

Console.WriteLine(
    $"eval: 엔진 {options.Engines.Length}종 · 버킷 {buckets.Length} · 회차 {options.Runs} "
    + $"· 프리픽스 {Short(prefix.Sha256)}");

// 예산은 회차 전체를 덮는다. 엔진마다 따로 주면 첫 엔진이 다 쓰고도 다음 엔진이 또 쓴다.
BudgetGuard? budget = options.BudgetUsd > 0 ? new BudgetGuard(options.BudgetUsd) : null;

var evaluations = ImmutableArray.CreateBuilder<EngineEvaluation>(options.Engines.Length);

foreach (string engineId in options.Engines)
{
    LlmEngineOptions? engine = llm.Engines.FirstOrDefault(
        e => string.Equals(e.Id, engineId, StringComparison.OrdinalIgnoreCase));

    if (engine is null)
    {
        Console.Error.WriteLine(
            $"엔진 '{engineId}' 이 {LlmOptions.FileName} 에 없다. "
            + $"있는 것: {string.Join(", ", llm.Engines.Select(e => e.Id))}");

        return 2;
    }

    if (!engine.IsConfigured)
    {
        // 키가 없으면 붙을 수 없다. <b>여기서 멈춘다</b> — 한 엔진만 돌린 대조표는
        // 대조가 아니고, 그것을 "대조표" 라고 부르면 다음 사람이 속는다.
        Console.Error.WriteLine(
            $"엔진 '{engineId}' 의 API 키가 없다 (환경변수 {engine.ApiKeyEnv}).");

        return 2;
    }

    var runOptions = new BulkRunOptions(
        Concurrency: options.Concurrency,
        DryRunSample: options.DryRunSample,
        Budget: budget);

    EngineRun best = await RunAllAsync(engine, runOptions).ConfigureAwait(false);

    // 골든은 여기서 돌리지 않는다. 러너가 테스트 스위트에 있고, 도구가 그것을 다시 만들면
    // 두 벌이 갈라져 "테스트는 통과인데 평가는 불합격" 이 생긴다. 사람이 숫자를 넣는다.
    if (options.GoldenRate >= 0 && options.GoldenAssertions > 0)
    {
        best = best with { Golden = options.GoldenRate, GoldenAssertions = options.GoldenAssertions };
    }

    // LLM 심사원 (선택). <b>권고 신호이지 게이트가 아니다</b> — 루브릭 채점으로 배포를
    // 막으면 심사 모델이 바뀔 때마다 기준이 소리 없이 움직인다.
    if (options.Judge.Length > 0)
    {
        best = best with { Judge = await JudgeAsync(best).ConfigureAwait(false) };
    }

    evaluations.Add(EngineEvaluation.Of(best));
}

ImmutableArray<EngineEvaluation> results = evaluations.ToImmutable();

Directory.CreateDirectory(options.OutDirectory);

string markdownPath = Path.Combine(options.OutDirectory, "eval.md");
string jsonPath = Path.Combine(options.OutDirectory, "eval.json");

File.WriteAllText(markdownPath, EvalReport.RenderMarkdown(options.Title, results));
File.WriteAllText(jsonPath, EvalReport.RenderJson(options.Title, results));

Console.WriteLine();

foreach (EngineEvaluation evaluation in results)
{
    Console.WriteLine(
        $"{evaluation.Run.Engine}: 통과율 {Percent(evaluation.Run.PassRate)} "
        + $"· 다양성 {Percent(evaluation.Diversity.SequenceRate)} "
        + $"· ${evaluation.Run.CostUsd.ToString("0.0000", CultureInfo.InvariantCulture)} "
        + $"· {(evaluation.Accepted ? "통과" : "불합격")}");
}

Console.WriteLine($"보고서: {markdownPath} · {jsonPath}");

// 불합격이 하나라도 있으면 비0. 미판정은 막지 않는다 — 안 돌린 것으로 배포를 막으면
// 사람이 게이트를 꺼 버린다.
return results.All(e => e.Accepted) ? 0 : 1;

async Task<EngineRun> RunAllAsync(LlmEngineOptions engine, BulkRunOptions runOptions)
{
    EngineRun? best = null;

    for (int run = 1; run <= options.Runs; run++)
    {
        Console.WriteLine($"  {engine.Id} · 회차 {run}/{options.Runs}");

        EngineRun current = await EvalRunner.RunAsync(
            data, prefix, engine, buckets,
            () => ChatClientFactory.Create(engine),
            runOptions,
            (done, total, message) =>
            {
                if (message.Length > 0)
                {
                    Console.WriteLine($"    {done}/{total} {message}");
                }
            }).ConfigureAwait(false);

        // <b>중앙값이 아니라 최고를 쓴다.</b> 골든과 같은 규칙이다 (3회 중 2회 합격) —
        // LLM 출력은 매번 다르고 그게 정상이라, 한 번의 나쁜 회차로 모델을 탈락시키지 않는다.
        // 회차별 통과율은 콘솔에 그대로 남아 분산을 볼 수 있다.
        if (best is null || current.PassRate > best.PassRate)
        {
            best = current;
        }
    }

    return best!;
}

async Task<double> JudgeAsync(EngineRun run)
{
    LlmEngineOptions? judge = llm.Engines.FirstOrDefault(
        e => string.Equals(e.Id, options.Judge, StringComparison.OrdinalIgnoreCase));

    if (judge is null || !judge.IsConfigured)
    {
        Console.Error.WriteLine($"심사원 '{options.Judge}' 을 쓸 수 없다 — 채점을 건너뛴다.");

        return -1;
    }

    using IChatClient client = ChatClientFactory.Create(judge);

    double score = await PlanJudge
        .ScoreAsync(run.Samples, Rubric(options.MasterData), client, CancellationToken.None)
        .ConfigureAwait(false);

    Console.WriteLine(
        $"  {run.Engine} · 심사원 {judge.Id} "
        + $"{(score < 0 ? "채점 실패" : score.ToString("0.00", CultureInfo.InvariantCulture))}");

    return score;
}

// 루브릭은 system_rules.md 한 곳에만 있다. 여기에 다시 쓰면 두 벌이 되고 반드시 어긋난다.
static string Rubric(string masterDataDirectory)
{
    string path = Path.Combine(masterDataDirectory, "prompt", "system_rules.md");

    // 줄 끝을 먼저 통일한다. CRLF 체크아웃에서 절 경계를 못 찾으면 루브릭이 통째로 실린다.
    string text = File.ReadAllText(path).ReplaceLineEndings("\n");

    const string Marker = "WHAT MAKES A PLAN GOOD";

    int start = text.IndexOf(Marker, StringComparison.Ordinal);

    if (start < 0)
    {
        throw new InvalidDataException($"{path} 에 '{Marker}' 절이 없다. 루브릭이 사라졌다.");
    }

    // 다음 절 제목까지. 절 제목은 빈 줄 뒤의 대문자 줄이다. 없으면 끝까지 쓴다.
    const string Blank = "\n\n";

    int end = text.IndexOf(Blank, start, StringComparison.Ordinal);

    while (end > 0 && end + Blank.Length < text.Length && !char.IsUpper(text[end + Blank.Length]))
    {
        end = text.IndexOf(Blank, end + Blank.Length, StringComparison.Ordinal);
    }

    return (end < 0 ? text[start..] : text[start..end]).Trim();
}

static string Percent(double value) =>
    (value * 100).ToString("0.0", CultureInfo.InvariantCulture) + " %";

static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];
