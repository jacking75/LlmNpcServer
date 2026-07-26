// 플랜 다양성 지표. docs/12 §10 · T2-22.
//
// .NET 10 파일 기반 앱이다:
//     dotnet run tools/measure_diversity.cs -- --run docs/measurements/runs/W6_round7.jsonl
//
// 무엇을 재는가
//   유니크 액션 시퀀스 / 전체. W1(T0-12)의 채점식과 같은 정의다 —
//   거기서 llamacpp-gemma3-4b 가 유니크 비율 ≈ 58% 였다 (docs/measurements/W1_quality.md).
//   목표는 ≥ 60% 이고, 미달이면 W12 블라인드 평가에서 "구분 불가"가 나올 가능성이 높다.
//
// 왜 이것을 보는가
//   플랜은 (아키타입 × 버킷) 단위로 재사용된다. 같은 아키타입의 서로 다른 상황이
//   같은 액션 시퀀스를 내면, 버킷 차원 2,880 은 그냥 비용일 뿐 아무것도 사지 못한다.
//   그래서 전체 유니크 비율과 함께 **아키타입 안에서의** 유니크 비율도 같이 낸다.
//
// 결정론
//   시각을 넣지 않는다. 같은 입력이면 바이트 동일한 리포트가 나온다.

using System.Globalization;
using System.Text;
using System.Text.Json;

var runs = new List<string>();
string? outPath = null;
double target = 0.60;

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

        case "--target" when i + 1 < args.Length:
            target = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;

        case "--help" or "-h":
            Console.WriteLine(
                """
                measure_diversity — 플랜 다양성 지표 (T2-22)

                  --run <file.jsonl>   회차 결과. 여러 번 줄 수 있다
                  --out <file.md>      리포트 경로. 없으면 화면에만 낸다
                  --target <0..1>      목표 유니크 비율. 기본 0.60
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

var rows = new List<Row>();

foreach (string path in runs)
{
    foreach (string line in File.ReadAllLines(path))
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        using JsonDocument doc = JsonDocument.Parse(line);
        JsonElement e = doc.RootElement;

        rows.Add(new Row(
            e.GetProperty("archetype").GetString() ?? string.Empty,
            e.GetProperty("bucket").GetString() ?? string.Empty,
            e.GetProperty("ok").GetBoolean(),
            e.GetProperty("origin").GetString() ?? string.Empty,
            e.GetProperty("goal").GetString() ?? string.Empty,
            e.GetProperty("actions").GetString() ?? string.Empty));
    }
}

// 폴백은 사람이 쓴 같은 플랜이라 다양성 분모에 넣으면 지표가 거짓으로 낮아진다.
// 생성에 성공한 것만 센다 — "LLM 이 상황마다 다른 플랜을 내는가"가 질문이다.
List<Row> generated = [.. rows.Where(r => r.Ok && r.Actions.Length > 0)];

int total = generated.Count;
int uniqueSequences = generated.Select(r => r.Actions).Distinct(StringComparer.Ordinal).Count();
int uniqueGoals = generated.Select(r => r.Goal).Distinct(StringComparer.Ordinal).Count();

double sequenceRate = total == 0 ? 0 : (double)uniqueSequences / total;
double goalRate = total == 0 ? 0 : (double)uniqueGoals / total;

// 아키타입 안에서의 다양성 — 여기가 진짜 질문이다.
var byArchetype = generated
    .GroupBy(r => r.Archetype, StringComparer.Ordinal)
    .Select(g => new
    {
        Archetype = g.Key,
        Count = g.Count(),
        Unique = g.Select(r => r.Actions).Distinct(StringComparer.Ordinal).Count(),
    })
    .Where(x => x.Count > 1)
    .OrderBy(x => (double)x.Unique / x.Count)
    .ThenBy(x => x.Archetype, StringComparer.Ordinal)
    .ToList();

double withinArchetype = byArchetype.Count == 0
    ? 1.0
    : (double)byArchetype.Sum(x => x.Unique) / byArchetype.Sum(x => x.Count);

var sb = new StringBuilder(8 * 1024);

sb.AppendLine("# W6 플랜 다양성 (T2-22)");
sb.AppendLine();
sb.AppendLine("> `tools/measure_diversity.cs` 가 생성한다. **손으로 고치지 않는다.**");
sb.AppendLine("> 정의는 W1(T0-12)과 같다 — 유니크 액션 시퀀스 / 전체. 폴백은 분모에서 뺀다.");
sb.AppendLine();
sb.AppendLine("| 지표 | 값 | 목표 | 판정 |");
sb.AppendLine("|---|---:|---:|---|");
sb.AppendLine(CultureInfo.InvariantCulture,
    $"| 유니크 액션 시퀀스 / 생성 성공 | {sequenceRate:P1} ({uniqueSequences}/{total}) | {target:P0} | {(sequenceRate >= target ? "통과" : "미달")} |");
sb.AppendLine(CultureInfo.InvariantCulture,
    $"| 아키타입 안 유니크 비율 | {withinArchetype:P1} | {target:P0} | {(withinArchetype >= target ? "통과" : "미달")} |");
sb.AppendLine(CultureInfo.InvariantCulture,
    $"| 유니크 goal / 생성 성공 | {goalRate:P1} ({uniqueGoals}/{total}) | - | - |");
sb.AppendLine();

if (byArchetype.Count > 0)
{
    sb.AppendLine("## 아키타입별 (표본이 2건 이상인 것, 낮은 순 상위 15)");
    sb.AppendLine();
    sb.AppendLine("| 아키타입 | 생성 | 유니크 시퀀스 | 비율 |");
    sb.AppendLine("|---|---:|---:|---:|");

    foreach (var a in byArchetype.Take(15))
    {
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| {a.Archetype} | {a.Count} | {a.Unique} | {(double)a.Unique / a.Count:P0} |");
    }

    sb.AppendLine();
}

sb.AppendLine("## 가장 흔한 액션 시퀀스 (상위 10)");
sb.AppendLine();
sb.AppendLine("| 건수 | 시퀀스 |");
sb.AppendLine("|---:|---|");

foreach (var g in generated
    .GroupBy(r => r.Actions, StringComparer.Ordinal)
    .OrderByDescending(g => g.Count())
    .ThenBy(g => g.Key, StringComparer.Ordinal)
    .Take(10))
{
    sb.AppendLine(CultureInfo.InvariantCulture, $"| {g.Count()} | `{g.Key}` |");
}

sb.AppendLine();

string report = sb.ToString();

if (outPath is not null)
{
    if (Path.GetDirectoryName(outPath) is { Length: > 0 } directory)
    {
        Directory.CreateDirectory(directory);
    }

    File.WriteAllText(outPath, report, Encoding.UTF8);
}

Console.WriteLine($"생성 성공 {total}건 · 유니크 시퀀스 {uniqueSequences} ({sequenceRate:P1})");
Console.WriteLine($"아키타입 안 유니크 비율 {withinArchetype:P1} · 목표 {target:P0}");

if (outPath is not null)
{
    Console.WriteLine($"리포트: {outPath}");
}

return sequenceRate >= target ? 0 : 1;

internal readonly record struct Row(
    string Archetype, string Bucket, bool Ok, string Origin, string Goal, string Actions);
