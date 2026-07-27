// 블라인드 A/B **예비** 평가 — LLM 심사원. docs/15 §1 · §6.
//
//   ⚠ 블라인드 평가의 예비 실시는 W8 에 이미 했어야 한다. 안 했다면 W11 시작 시점에
//     축약판을 먼저 돌려서 방향을 확인한다. W12 에 처음 알게 되면 대응할 시간이 없다.
//                                                          — docs/15 §1
//
// 실행:
//   dotnet run tools/pilot_blind_eval.cs -- --judges 6
//
// **이것은 정식 측정이 아니다.**
//   · docs/15 §6 의 정식 평가는 "사내 기획자·개발자 12명 이상 (게임 도메인 이해자)" 이다.
//   · 여기서 나온 판정은 P5 게이트의 `n ≥ 480` 을 충족하지 않는다. 게이트는 그대로 미달이다.
//   · 사람 응답 파일(`blind_eval_raw.jsonl`)에 절대 섞지 않는다. 별도 파일로 나간다.
//
// 그럼에도 돌리는 이유는 하나다 — **사람 12명 × 1시간을 쓰기 전에 방향을 안다.**
// 예비에서 정답률이 90 % 로 나오면 "티가 심하게 난다" 는 뜻이고, 그러면 정식 평가보다
// 프롬프트 개선이 먼저다. 50 % 근처면 정식 평가를 돌릴 값이 있다.
//
// 산출:
//   docs/measurements/blind_eval_pilot.jsonl        심사원 응답 (사람 자료와 별도)
//   docs/measurements/blind_eval_pilot_result.md    analyze_blind_eval.cs 로 만든다
#:project ../src/Npc.Llm/Npc.Llm.csproj

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Npc.Core.Plan;
using Npc.Llm;

string caseDir = "./artifacts/blind_eval";
string outPath = "./docs/measurements/blind_eval_pilot.jsonl";
string? engineId = null;
int judges = 6;
int seed = 20260727;
int concurrency = 8;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--cases" when i + 1 < args.Length: caseDir = args[++i]; break;
        case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
        case "--model" or "--engine" when i + 1 < args.Length: engineId = args[++i]; break;
        case "--judges" when i + 1 < args.Length: judges = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--concurrency" when i + 1 < args.Length: concurrency = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        default: throw new ArgumentException($"모르는 인자다: {args[i]}");
    }
}

string[] files = [.. Directory.GetFiles(caseDir, "case_*.md").OrderBy(Path.GetFileName, StringComparer.Ordinal)];

if (files.Length == 0)
{
    Console.Error.WriteLine($"{caseDir} 에 사례가 없다. 먼저 tools/gen_blind_eval.cs 를 돌린다.");
    return 1;
}

LlmOptions llm = LlmOptions.LoadDefault(Directory.GetCurrentDirectory());
LlmEngineOptions engine = engineId is null ? llm.PreferredEngine() : llm.Engine(engineId);

if (!ChatClientFactory.IsAvailable(engine))
{
    Console.Error.WriteLine($"'{engine.Id}' 의 API 키가 없다.");
    return 1;
}

// 참가자에게 주는 것과 **같은 문구**를 쓴다. "구분 불가도 성공" 같은 프레이밍은 넣지 않는다
// (docs/15 §6 편향 방지) — 심사원에게만 다르게 물으면 예비의 뜻이 없다.
const string Instruction = """
    아래는 게임 속 NPC 한 마리의 하루 일지입니다. 두 가지를 답해 주세요.

    Q1. 이 NPC 의 하루를 계획한 것은 무엇이라고 생각하십니까?
        "llm" 또는 "human" — 확신이 없어도 한쪽을 고릅니다.
    Q2. 이 NPC 의 행동이 상황에 얼마나 잘 맞습니까?
        1(전혀 안 맞음) ~ 5(매우 잘 맞음)

    JSON 객체 하나만 출력합니다. 다른 말은 쓰지 않습니다.
    {"q1":"llm","q2":4}
    """;

using IChatClient client = ChatClientFactory.Create(engine);

var answers = new ConcurrentBag<string>();
var failures = new ConcurrentBag<string>();
long promptTokens = 0;
long cachedTokens = 0;
long completionTokens = 0;
double costUsd = 0;
var gate = new object();

var work = new List<(int Judge, int Case, string Body)>(judges * files.Length);

for (int judge = 1; judge <= judges; judge++)
{
    // 심사원마다 제시 순서를 다르게 한다 — 순서 효과가 전원에게 같으면 그것도 편향이다.
    int[] order = [.. Enumerable.Range(0, files.Length)];

    for (int i = order.Length - 1; i > 0; i--)
    {
        int j = (int)(PlanHash.Mix(seed ^ judge, i) % (uint)(i + 1));
        (order[i], order[j]) = (order[j], order[i]);
    }

    foreach (int index in order)
    {
        work.Add((judge, index + 1, File.ReadAllText(files[index])));
    }
}

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"엔진 {engine.Id} · 심사원 {judges}명 × 사례 {files.Length}건 = {work.Count} 판정"));

await Parallel.ForEachAsync(
    work,
    new ParallelOptions { MaxDegreeOfParallelism = concurrency },
    async ((int Judge, int Case, string Body) item, CancellationToken token) =>
    {
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, Instruction),
            new ChatMessage(ChatRole.User, item.Body),
        ];

        var options = new ChatOptions { Temperature = 0.8f, MaxOutputTokens = 64 };

        try
        {
            ChatResponse response = await client.GetResponseAsync(messages, options, token).ConfigureAwait(false);

            (long prompt, long cached, long completion) = ChatClientFactory.ReadUsage(response);

            lock (gate)
            {
                promptTokens += prompt;
                cachedTokens += cached;
                completionTokens += completion;
                costUsd += engine.CostUsd(prompt, cached, completion);
            }

            if (!TryRead(response.Text, out string q1, out int q2))
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"J{item.Judge:D2}/{item.Case:D2}: {response.Text}"));
                return;
            }

            answers.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"participant\":\"J{item.Judge:D2}\",\"case\":{item.Case},\"q1\":\"{q1}\",\"q2\":{q2}}}"));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"J{item.Judge:D2}/{item.Case:D2}: {e.Message}"));
        }
    }).ConfigureAwait(false);

// 순서를 고정해서 쓴다 — 병렬 완료 순서가 파일에 남으면 재현이 안 된다.
string[] sorted = [.. answers.Order(StringComparer.Ordinal)];

var body = new StringBuilder(64 * 1024);

body.Append("{\"_schema\":\"blind_eval_pilot\",\"_doc\":\"docs/15 §1 예비 실시. **LLM 심사원이다 — 사람이 아니다.** ");
body.Append("정식 측정(docs/15 §6, 사내 12명)이 아니고 P5 게이트의 n>=480 을 충족하지 않는다. ");
body.Append("사람 응답은 blind_eval_raw.jsonl 에 따로 모은다.\"}\n");

foreach (string line in sorted)
{
    body.Append(line).Append('\n');
}

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
File.WriteAllText(outPath, body.ToString().ReplaceLineEndings("\n"), new UTF8Encoding(false));

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"{outPath}: 판정 {sorted.Length}건 · 실패 {failures.Count}건"));
Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"토큰 입력 {promptTokens:N0} (캐시 {cachedTokens:N0}) · 출력 {completionTokens:N0} · 비용 ${costUsd:F4}"));

foreach (string failure in failures.Order(StringComparer.Ordinal).Take(5))
{
    Console.Error.WriteLine("  " + failure);
}

return sorted.Length > 0 ? 0 : 1;

/// <summary>응답에서 q1·q2 를 읽는다. 울타리·군더더기를 견딘다.</summary>
static bool TryRead(string? text, out string q1, out int q2)
{
    q1 = string.Empty;
    q2 = 0;

    if (string.IsNullOrWhiteSpace(text))
    {
        return false;
    }

    int open = text.IndexOf('{');
    int close = text.LastIndexOf('}');

    if (open < 0 || close <= open)
    {
        return false;
    }

    try
    {
        using JsonDocument document = JsonDocument.Parse(text[open..(close + 1)]);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("q1", out JsonElement a) || a.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("q2", out JsonElement b) || b.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        q1 = a.GetString()!.Trim().ToLowerInvariant();
        q2 = b.GetInt32();

        return q1 is "llm" or "human" && q2 is >= 1 and <= 5;
    }
    catch (JsonException)
    {
        return false;
    }
}
