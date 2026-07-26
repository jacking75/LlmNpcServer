// 실패 코드 집계 리포트. docs/12 §8 · T2-20.
//
// .NET 10 파일 기반 앱이다. 추가 도구 설치 없이 그대로 돈다:
//     dotnet run tools/report_failures.cs -- --run docs/measurements/W6_run.jsonl
//     dotnet run tools/report_failures.cs -- --run a.jsonl --run b.jsonl --out docs/measurements/W6_failures.md
//
// 무엇을 만드는가
//   전량 생성 러너(T2-19)가 남긴 JSONL 을 (stage, code, archetype) 3축으로 집계하고
//   상위 원인 Top 5 와 각 원인에 대한 docs/12 §8 의 처방을 리포트로 낸다.
//   회차를 여러 개 주면 차수별 통과율 변화가 같이 나온다 (T2-21 이 이걸로 개선을 확인한다).
//
// 왜 rejected/ 가 아니라 JSONL 인가
//   rejected/ 는 실패한 것만 있어서 분모가 없다. 통과율을 보려면 성공도 세야 한다.
//   원문이 필요할 때는 planstore/rejected/<bucket>.<attempt>.json 을 연다 (docs/13 §8).
//
// 결정론
//   시각을 넣지 않는다. 같은 입력이면 바이트 동일한 리포트가 나와야 차수 간 diff 가 의미를 갖는다.

using System.Globalization;
using System.Text;
using System.Text.Json;

var runs = new List<string>();
string outPath = "docs/measurements/W6_failures.md";
int topN = 5;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--run" when i + 1 < args.Length:
            runs.Add(args[++i]);
            break;

        case "--out" when i + 1 < args.Length:
            outPath = args[++i];
            break;

        case "--top" when i + 1 < args.Length:
            topN = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;

        case "--help" or "-h":
            Console.WriteLine(
                """
                report_failures — 실패 코드 3축 집계 (T2-20)

                  --run <file.jsonl>   회차 결과. 여러 번 줄 수 있다 (차수 순서대로)
                  --out <file.md>      기본 docs/measurements/W6_failures.md
                  --top <n>            상위 원인 개수. 기본 5
                """);
            return 0;

        default:
            Console.Error.WriteLine($"모르는 인자: {args[i]}");
            return 2;
    }
}

if (runs.Count == 0)
{
    Console.Error.WriteLine("--run 이 하나도 없다.");
    return 2;
}

// docs/12 §8 의 처방표. 코드 → 무엇을 고칠 것인가.
var remedies = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["V0.CALL_FAILED"] = "호출 자체가 실패했다. 동시성·재시도 정책 문제이지 프롬프트 문제가 아니다.",
    ["V1.PARSE"] = "JSON 이 아니다. 출력 상한(max_output_tokens)이나 추론형 모델의 사고 토큰을 의심한다.",
    ["V1.SCHEMA"] = "스키마 위반. §4 의 스키마 단순화 또는 모델 상향.",
    ["V1.STEP_COUNT"] = "스텝 수가 3~10 밖. system_rules 의 스텝 수 규칙을 강조한다.",
    ["V1.EXTRA_FIELD"] = "없는 필드를 만든다. DSL 요약에 허용 필드를 못 박는다.",
    ["V2.UNKNOWN_ACTION"] = "카탈로그에 없는 액션. 프리픽스의 액션 목록을 강조하고 흔한 오답을 금지 목록에 넣는다.",
    ["V2.ACTION_NOT_ALLOWED"] = "아키타입 허용 액션 밖. 서픽스에 allowed_actions 추가 또는 프리픽스의 아키타입 표 강조.",
    ["V2.UNKNOWN_ARG"] = "액션에 없는 인자를 지어낸다. 강제 디코딩을 끄고 카탈로그의 args 표기를 강조한다.",
    ["V2.MISSING_REQUIRED_ARG"] = "필수 인자 누락. 카탈로그에서 required 표기를 강조한다.",
    ["V2.TYPE_MISMATCH"] = "인자 타입 불일치. 열거값 목록을 카탈로그에 명시한다.",
    ["V2.UNKNOWN_POI"] = "POI 심볼 환각. 허용 심볼 9종을 system_rules 에서 반복한다.",
    ["V2.UNKNOWN_ITEM"] = "아이템 id 환각. 프리픽스에 아이템 어휘를 싣는다.",
    ["V2.UNKNOWN_RECIPE"] = "레시피 id 환각. 아키타입별 primary_recipes 를 강조한다.",
    ["V2.RANGE"] = "정수 범위 위반. 카탈로그의 range 표기를 강조한다.",
    ["V3.PRECONDITION_UNMET"] = "requires/grants 관계 미이해. 액션 카탈로그에 플래그 표, few-shot 에 수정 예시.",
    ["V3.FORBIDDEN_FLAG"] = "금지 플래그 위반. 같은 표를 강조한다.",
    ["V3.LOOP_NOT_CLOSED"] = "마지막이 Sleep/Rest 가 아니다. system_rules 에 loop 규칙을 강조한다.",
    ["V3.UNREACHABLE_POI"] = "이 아키타입이 못 가는 POI. 아키타입 표에 workplace 를 명시한다.",
    ["V3.RESOURCE_IMBALANCE"] = "수량 계산 실패. 레시피 입출력을 싣거나 count 를 심볼화한다 (T2-23).",
    ["V3.NO_TERMINAL"] = "loop=false 인데 휴식으로 안 끝난다. system_rules 강조.",
    ["V3.DEGENERATE"] = "같은 액션 3연속. few-shot 에 다양한 모양을 넣는다.",
    ["V4.DEADLOCK"] = "실행이 멈춘다. 전제 순서를 다시 가르친다.",
    ["V4.TIMEOUT"] = "36 게임시간 초과. 스텝 수·소요시간을 줄이게 한다.",
    ["V4.INFINITE_LOOP"] = "사이클이 하루보다 짧다. Sleep until 로 하루를 채우게 한다.",
    ["V4.RESOURCE_STARVE"] = "자기가 모은 자원이 바닥난다. 채집량을 늘리거나 소비를 줄이게 한다.",
    ["V4.OSCILLATION"] = "POI 왕복. few-shot 에 '한 장소의 일을 묶어라' 예시를 넣는다.",
};

var rounds = new List<Round>();

foreach (string path in runs)
{
    rounds.Add(Load(path));
}

var sb = new StringBuilder(32 * 1024);

sb.AppendLine("# W6 실패 코드 집계 (T2-20)");
sb.AppendLine();
sb.AppendLine("> `tools/report_failures.cs` 가 생성한다. **손으로 고치지 않는다.**");
sb.AppendLine("> 해석은 맨 아래 \"판단\" 절에 사람이 쓴다.");
sb.AppendLine();

// --- 차수별 통과율 ---
sb.AppendLine("## 1. 차수별 통과율");
sb.AppendLine();
sb.AppendLine("| 차수 | 회차 파일 | 버킷 | 통과 | 통과율 | 1회 통과 | 폴백 | 비용(USD) |");
sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");

for (int i = 0; i < rounds.Count; i++)
{
    Round r = rounds[i];
    sb.Append(CultureInfo.InvariantCulture, $"| {i + 1} | `{Path.GetFileName(r.Path)}` | {r.Total} | {r.Passed} | ");
    sb.Append(CultureInfo.InvariantCulture, $"{r.PassRate:P1} | {r.FirstAttempt} | {r.FellBack} | {r.Cost:F4} |");
    sb.AppendLine();
}

sb.AppendLine();

Round last = rounds[^1];

// --- 상위 원인 ---
sb.AppendLine(CultureInfo.InvariantCulture, $"## 2. 상위 실패 원인 Top {topN} (마지막 차수)");
sb.AppendLine();
sb.AppendLine("| 순위 | stage | code | 건수 | 비중 | 처방 (docs/12 §8) |");
sb.AppendLine("|---:|---|---|---:|---:|---|");

var byCode = last.Failures
    .GroupBy(f => (f.Stage, f.Code))
    .Select(g => (g.Key.Stage, g.Key.Code, Count: g.Count()))
    .OrderByDescending(x => x.Count)
    .ThenBy(x => x.Code, StringComparer.Ordinal)
    .ToList();

for (int i = 0; i < Math.Min(topN, byCode.Count); i++)
{
    (string stage, string code, int count) = byCode[i];
    string remedy = remedies.TryGetValue(code, out string? r) ? r : "(처방 미정)";

    sb.Append(CultureInfo.InvariantCulture, $"| {i + 1} | {stage} | `{code}` | {count} | ");
    sb.Append(CultureInfo.InvariantCulture, $"{(double)count / Math.Max(1, last.Total):P1} | {remedy} |");
    sb.AppendLine();
}

sb.AppendLine();

// --- 3축: stage ---
sb.AppendLine("## 3. 단계별 (마지막 차수)");
sb.AppendLine();
sb.AppendLine("| stage | 건수 |");
sb.AppendLine("|---|---:|");

foreach (var g in last.Failures.GroupBy(f => f.Stage).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
{
    sb.AppendLine(CultureInfo.InvariantCulture, $"| {g.Key} | {g.Count()} |");
}

sb.AppendLine();

// --- 3축: archetype ---
sb.AppendLine("## 4. 아키타입별 (마지막 차수, 실패 많은 순 상위 15)");
sb.AppendLine();
sb.AppendLine("| 아키타입 | 시도 | 통과 | 통과율 | 가장 흔한 실패 |");
sb.AppendLine("|---|---:|---:|---:|---|");

var byArchetype = last.Rows
    .GroupBy(r => r.Archetype, StringComparer.Ordinal)
    .Select(g => new
    {
        Archetype = g.Key,
        Total = g.Count(),
        Passed = g.Count(r => r.Ok),
        Top = g.Where(r => !r.Ok)
               .GroupBy(r => r.Code, StringComparer.Ordinal)
               .OrderByDescending(x => x.Count())
               .ThenBy(x => x.Key, StringComparer.Ordinal)
               .Select(x => $"{x.Key} ({x.Count()})")
               .FirstOrDefault() ?? "-",
    })
    .OrderBy(x => (double)x.Passed / Math.Max(1, x.Total))
    .ThenBy(x => x.Archetype, StringComparer.Ordinal)
    .Take(15);

foreach (var a in byArchetype)
{
    sb.Append(CultureInfo.InvariantCulture, $"| {a.Archetype} | {a.Total} | {a.Passed} | ");
    sb.AppendLine(CultureInfo.InvariantCulture, $"{(double)a.Passed / Math.Max(1, a.Total):P0} | {a.Top} |");
}

sb.AppendLine();

// --- 3축 전체 조합 ---
sb.AppendLine("## 5. (stage, code, archetype) 3축 (마지막 차수, 상위 20)");
sb.AppendLine();
sb.AppendLine("| stage | code | 아키타입 | 건수 |");
sb.AppendLine("|---|---|---|---:|");

foreach (var g in last.Failures
    .GroupBy(f => (f.Stage, f.Code, f.Archetype))
    .OrderByDescending(g => g.Count())
    .ThenBy(g => g.Key.Code, StringComparer.Ordinal)
    .ThenBy(g => g.Key.Archetype, StringComparer.Ordinal)
    .Take(20))
{
    sb.AppendLine(CultureInfo.InvariantCulture,
        $"| {g.Key.Stage} | `{g.Key.Code}` | {g.Key.Archetype} | {g.Count()} |");
}

sb.AppendLine();
sb.AppendLine("## 6. 판단");
sb.AppendLine();
sb.AppendLine("*여기는 사람이 쓴다. 무엇을 고쳤고 다음 차수에서 무엇을 기대하는가.*");
sb.AppendLine();

if (Path.GetDirectoryName(outPath) is { Length: > 0 } directory)
{
    Directory.CreateDirectory(directory);
}

File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);

Console.WriteLine($"차수 {rounds.Count}개 · 마지막 차수 통과율 {last.PassRate:P1} ({last.Passed}/{last.Total})");
Console.WriteLine("상위 원인:");

for (int i = 0; i < Math.Min(topN, byCode.Count); i++)
{
    Console.WriteLine($"  {i + 1}. {byCode[i].Code,-28} {byCode[i].Count,4}건");
}

Console.WriteLine($"리포트: {outPath}");
return 0;

static Round Load(string path)
{
    var rows = new List<Row>();

    foreach (string line in File.ReadAllLines(path))
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement e = doc.RootElement;

        rows.Add(new Row(
            e.GetProperty("bucket").GetString() ?? string.Empty,
            e.GetProperty("archetype").GetString() ?? string.Empty,
            e.GetProperty("ok").GetBoolean(),
            e.GetProperty("stage").GetString() ?? string.Empty,
            e.GetProperty("code").GetString() ?? string.Empty,
            e.GetProperty("attempt").GetInt32(),
            e.GetProperty("origin").GetString() ?? string.Empty,
            e.GetProperty("cost_usd").GetDouble()));
    }

    return new Round(path, rows);
}

internal readonly record struct Row(
    string Bucket, string Archetype, bool Ok, string Stage, string Code, int Attempt, string Origin, double Cost);

internal sealed record Round(string Path, List<Row> Rows)
{
    public int Total => Rows.Count;

    public int Passed => Rows.Count(r => r.Ok);

    public int FirstAttempt => Rows.Count(r => r is { Ok: true, Attempt: 1 });

    public int FellBack => Rows.Count(r => string.Equals(r.Origin, "Fallback", StringComparison.Ordinal));

    public double PassRate => Total == 0 ? 0 : (double)Passed / Total;

    public double Cost => Rows.Sum(r => r.Cost);

    public IEnumerable<Row> Failures => Rows.Where(r => !r.Ok);
}
