// 오써링 공수 정산. docs/15 §7 · T5-16.
//
// 실행:
//   dotnet run tools/calc_authoring.cs
//   dotnet run tools/calc_authoring.cs -- --minutes-per-plan 15,30,60 --prompt-design-hours 20,40,80
//
// 입력:
//   docs/measurements/authoring_time.jsonl   수작성 실측 (분모)
//   planstore/manifest.json                  프리베이크 실측 (분자)
//   docs/measurements/review_W8.jsonl        검수 실측 (분자). 없으면 가정을 쓴다
// 산출:
//   docs/measurements/authoring_result.md
//
// 두 축을 다 낸다 (docs/15 §7).
//   축 A  절감률       — 같은 결과물(2,880개)을 만드는 비용 비교
//   축 B  커버리지 배수 — 같은 비용으로 얻는 결과물 (40개 → 2,880개)
//
// **함정 둘을 피한다.**
//   1. 프롬프트 설계 공수를 분자에서 빼지 않는다. W5~W6 의 개선 루프는 실제 비용이다.
//      다만 1회성이므로 버킷 수가 늘수록 희석된다는 점을 같이 보인다.
//   2. "사람은 애초에 2,880개를 만들지 않는다." 현실의 대안은 "2,880개 수작성" 이 아니라
//      "40개만 만들고 나머지는 포기" 다. 그래서 축 B 가 더 정직하다.
//
// **측정되지 않은 입력은 가정이라고 적는다.** authoring_time.jsonl 의 40행은 전부
// baseline_eligible=false 이고(코딩 에이전트가 만들었다) minutes 가 비어 있다.
// 분모를 지어내지 않고 민감도 표로 낸다 — 결론이 가정에 얼마나 매여 있는지 보여야 한다.

using System.Globalization;
using System.Text;
using System.Text.Json;

string authoringPath = "./docs/measurements/authoring_time.jsonl";
string manifestPath = "./planstore/manifest.json";
string reviewPath = "./docs/measurements/review_W8.jsonl";
string outPath = "./docs/measurements/authoring_result.md";
string measuredOn = "(미기록)";

double[] minutesPerPlan = [15, 30, 60];
double[] promptDesignHours = [20, 40, 80];
double reviewMinutesPerPlan = 2;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--authoring" when i + 1 < args.Length: authoringPath = args[++i]; break;
        case "--manifest" when i + 1 < args.Length: manifestPath = args[++i]; break;
        case "--review" when i + 1 < args.Length: reviewPath = args[++i]; break;
        case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
        case "--date" when i + 1 < args.Length: measuredOn = args[++i]; break;
        case "--minutes-per-plan" when i + 1 < args.Length: minutesPerPlan = Numbers(args[++i]); break;
        case "--prompt-design-hours" when i + 1 < args.Length: promptDesignHours = Numbers(args[++i]); break;
        case "--review-minutes" when i + 1 < args.Length:
            reviewMinutesPerPlan = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        default: throw new ArgumentException($"모르는 인자다: {args[i]}");
    }
}

// ── 1. 분모 — 수작성 실측 ────────────────────────────────────────────
int fallbackPlans = 0;
int measuredPlans = 0;
double measuredMinutes = 0;

if (File.Exists(authoringPath))
{
    foreach (string line in File.ReadLines(authoringPath))
    {
        string text = line.Trim();

        if (!text.StartsWith('{'))
        {
            continue;   // // 주석 줄
        }

        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;

        fallbackPlans++;

        bool eligible = root.TryGetProperty("baseline_eligible", out JsonElement flag)
            && flag.ValueKind == JsonValueKind.True;

        if (eligible
            && root.TryGetProperty("minutes", out JsonElement minutes)
            && minutes.ValueKind == JsonValueKind.Number)
        {
            measuredPlans++;
            measuredMinutes += minutes.GetDouble();
        }
    }
}

// 실측이 있으면 가정 대신 그것을 쓴다.
double? measuredMinutesPerPlan = measuredPlans > 0 ? measuredMinutes / measuredPlans : null;

if (measuredMinutesPerPlan is { } measured)
{
    minutesPerPlan = [measured];
}

// ── 2. 분자 — 프리베이크 실측 ────────────────────────────────────────
int totalBuckets = 2880;
int generated = 0;
int attempted = 0;
double wallClockSeconds = 0;
double costUsd = 0;
string model = "(미기록)";
string generatedAt = "(미기록)";

if (File.Exists(manifestPath))
{
    using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
    JsonElement root = manifest.RootElement;

    if (root.TryGetProperty("counts", out JsonElement counts))
    {
        totalBuckets = counts.GetProperty("total").GetInt32();
        generated = counts.GetProperty("generated").GetInt32();

        // 이 회차가 실제로 건드린 버킷. wall-clock 과 비용은 여기에 비례한다 —
        // 생성 성공분(generated)만으로 나누면 폴백·재사용으로 해소된 버킷의 몫이
        // 두 번 세어져 전량 외삽이 부풀려진다 (실측 288버킷 → 195 로 나누면 1.5배 과대).
        attempted = generated
            + Count(counts, "fallback")
            + Count(counts, "reused")
            + Count(counts, "pinned");
    }

    wallClockSeconds = root.GetProperty("wall_clock_s").GetDouble();
    costUsd = root.GetProperty("cost_usd").GetDouble();

    if (root.TryGetProperty("generated_by", out JsonElement by))
    {
        model = by.GetProperty("model").GetString() ?? model;
    }

    generatedAt = root.TryGetProperty("generated_at", out JsonElement at) ? at.GetString() ?? generatedAt : generatedAt;
}

// manifest 가 파일럿 회차면 전량으로 외삽한다. 외삽했다는 사실을 리포트에 적는다.
bool extrapolated = attempted > 0 && attempted < totalBuckets;
double scale = extrapolated ? (double)totalBuckets / attempted : 1;
double fullWallClockHours = wallClockSeconds * scale / 3600;
double fullCostUsd = costUsd * scale;

// ── 3. 검수 실측 ─────────────────────────────────────────────────────
int reviewedPlans = 0;
double reviewMinutes = 0;

if (File.Exists(reviewPath))
{
    foreach (string line in File.ReadLines(reviewPath))
    {
        string text = line.Trim();

        if (!text.StartsWith('{'))
        {
            continue;
        }

        using JsonDocument document = JsonDocument.Parse(text);

        if (document.RootElement.TryGetProperty("minutes", out JsonElement minutes)
            && minutes.ValueKind == JsonValueKind.Number)
        {
            reviewedPlans++;
            reviewMinutes += minutes.GetDouble();
        }
    }
}

bool reviewMeasured = reviewedPlans > 0;

if (reviewMeasured)
{
    reviewMinutesPerPlan = reviewMinutes / reviewedPlans;
}

// ── 4. 리포트 ────────────────────────────────────────────────────────
var sb = new StringBuilder(16 * 1024);

sb.Append("# 오써링 공수 정산\n\n");
sb.Append("> `tools/calc_authoring.cs` 가 만든다. 손으로 고치지 않는다.\n");
sb.Append("> docs/15 §7 — 축 A(절감률)와 축 B(커버리지 배수)를 둘 다 낸다.\n\n");

sb.Append("| 항목 | 값 |\n|---|---|\n");
sb.Append(CultureInfo.InvariantCulture, $"| 측정일 | {measuredOn} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 폴백 플랜 (사람 경로의 결과물) | {fallbackPlans}개 |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 버킷 (LLM 경로의 결과물) | {totalBuckets}개 |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 프리베이크 회차 | {generatedAt} · `{model}` |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 회차 대상 | {attempted} / {totalBuckets} 버킷 |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 그중 생성 성공 | {generated} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 회차 wall-clock | {wallClockSeconds:F0}s |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 회차 비용 | ${costUsd:F4} |\n\n");

// ── 측정되지 않은 것 ──
sb.Append("## 0. 측정되지 않은 입력\n\n");
sb.Append("**여기 적힌 것은 실측이 아니다.** 아래 숫자는 가정이고, 결론이 가정에 ");
sb.Append("얼마나 매여 있는지는 §1 의 민감도 표로 본다.\n\n");
sb.Append("| 입력 | 상태 |\n|---|---|\n");

sb.Append(measuredMinutesPerPlan is { } m
    ? string.Create(CultureInfo.InvariantCulture, $"| 수작성 분/플랜 | **실측 {m:F1}분** ({measuredPlans}개 기준) |\n")
    : string.Create(CultureInfo.InvariantCulture,
        $"| 수작성 분/플랜 | **미측정.** `authoring_time.jsonl` {fallbackPlans}행이 전부 `baseline_eligible=false` 다 — 코딩 에이전트가 만든 플랜이라 사람의 작성 시간이 아니다 |\n"));

sb.Append(reviewMeasured
    ? string.Create(CultureInfo.InvariantCulture,
        $"| 검수 분/플랜 | **실측 {reviewMinutesPerPlan:F1}분** ({reviewedPlans}건 기준) |\n")
    : string.Create(CultureInfo.InvariantCulture,
        $"| 검수 분/플랜 | **미측정.** `review_W8.jsonl` 이 없다 (T3-18 미착수). 가정 {reviewMinutesPerPlan:F1}분 |\n"));

sb.Append("| 프롬프트 설계 공수 | **미측정.** W5~W6 의 실소요를 기록한 파일이 없다 |\n");

if (extrapolated)
{
    sb.Append(CultureInfo.InvariantCulture,
        $"| 프리베이크 전량 | **외삽.** {attempted}버킷 회차를 {scale:F1}배 해서 전량으로 늘렸다 |\n");
}

sb.Append('\n');

// ── 축 A ──
sb.Append("## 1. 축 A — 절감률 (같은 결과물 2,880개)\n\n");
sb.Append("LLM 경로 = 프롬프트 설계(1회) + 프리베이크 + 검수 + 수정.\n");
sb.Append("수작성 경로 = 2,880 × 평균 작성분.\n\n");
sb.Append(CultureInfo.InvariantCulture,
    $"프리베이크 전량은 **{fullWallClockHours:F2}시간 · ${fullCostUsd:F2}** 이고 사람 시간이 들지 않는다 ");
sb.Append("(기계가 돈다). 사람 시간은 프롬프트 설계와 검수뿐이다.\n\n");

double reviewHours = totalBuckets * reviewMinutesPerPlan / 60;

sb.Append(CultureInfo.InvariantCulture, $"전량 검수는 {totalBuckets} × {reviewMinutesPerPlan:F1}분 = **{reviewHours:F0}시간** 이다.\n\n");

sb.Append("| 수작성 분/플랜 → | ");

foreach (double minutes in minutesPerPlan)
{
    sb.Append(CultureInfo.InvariantCulture, $"{minutes:F0}분 | ");
}

sb.Append("\n|---|");

foreach (double _ in minutesPerPlan)
{
    sb.Append("---|");
}

sb.Append('\n');

sb.Append("| 수작성 2,880개 (시간) | ");

foreach (double minutes in minutesPerPlan)
{
    sb.Append(CultureInfo.InvariantCulture, $"{totalBuckets * minutes / 60:F0} | ");
}

sb.Append('\n');

foreach (double design in promptDesignHours)
{
    sb.Append(CultureInfo.InvariantCulture, $"| LLM 경로 (설계 {design:F0}h 가정) → 절감률 | ");

    foreach (double minutes in minutesPerPlan)
    {
        double manual = totalBuckets * minutes / 60;
        double llm = design + reviewHours + fullWallClockHours;
        double saving = manual == 0 ? 0 : 1 - (llm / manual);

        sb.Append(CultureInfo.InvariantCulture, $"{llm:F0}h · {saving:P0} | ");
    }

    sb.Append('\n');
}

sb.Append('\n');
sb.Append("> 프롬프트 설계는 **1회성**이다. 버킷이 2,880 → 28,800 이 되면 설계 공수는 그대로이고\n");
sb.Append("> 검수만 늘어난다 — 표의 절감률은 버킷 수가 커질수록 좋아진다.\n\n");

// ── 축 B ──
sb.Append("## 2. 축 B — 커버리지 배수 (같은 비용)\n\n");
sb.Append("**사람은 애초에 2,880개를 만들지 않는다.** 현실의 대안은 \"2,880개 수작성\" 이 아니라\n");
sb.Append("**\"40개만 만들고 나머지는 포기\"** 다. 실제로 이 저장소의 폴백이 정확히 그 40개다.\n\n");

sb.Append("| 항목 | 사람 경로 | LLM 경로 |\n|---|---|---|\n");
sb.Append(CultureInfo.InvariantCulture, $"| 결과물 | {fallbackPlans}개 (아키타입당 1개) | {totalBuckets}개 (아키타입 × 시간대 × 지역상태 × 기후) |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 상황 해상도 | 아키타입만 | 아키타입 × 72 상황 |\n");
sb.Append(CultureInfo.InvariantCulture,
    $"| 커버리지 배수 | 1× | **{(double)totalBuckets / Math.Max(1, fallbackPlans):F0}×** |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 기계 시간 | - | {fullWallClockHours:F2}h |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 비용 | - | ${fullCostUsd:F2} |\n\n");

sb.Append("축 B 가 더 정직하고 더 설득력 있다 (docs/15 §7). 축 A 는 아무도 하지 않을 작업과\n");
sb.Append("비교하는 것이고, 축 B 는 실제로 있었던 선택지와 비교하는 것이다.\n\n");

// ── 남은 것 ──
sb.Append("## 3. 이 정산을 확정하려면\n\n");
sb.Append("| 필요한 것 | 어디서 |\n|---|---|\n");
sb.Append("| 사람이 폴백 40개를 다시 짜고 시간을 기록 | `authoring_time.jsonl` 의 `minutes`·`baseline_eligible=true` |\n");
sb.Append("| 검수 40건 실측 | T3-18 — `review_W8.jsonl` |\n");
sb.Append("| W5~W6 프롬프트 설계 실소요 | 기록 파일 신설 필요 |\n");
sb.Append("| 프리베이크 전량 회차 | T2-19 — 지금 manifest 는 파일럿이다 |\n\n");

sb.Append("## 4. 다시 돌리는 법\n\n");
sb.Append("```\n");
sb.Append("dotnet run tools/calc_authoring.cs -- --date \"2026-07-27 KST\"\n");
sb.Append("dotnet run tools/calc_authoring.cs -- --minutes-per-plan 20,45 --prompt-design-hours 30\n");
sb.Append("```\n");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
File.WriteAllText(outPath, sb.ToString().ReplaceLineEndings("\n"), new UTF8Encoding(false));

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"{outPath}: 축 B {(double)totalBuckets / Math.Max(1, fallbackPlans):F0}배 · "
    + $"전량 외삽 {fullWallClockHours:F2}h · ${fullCostUsd:F2} · "
    + $"수작성 실측 {measuredPlans}/{fallbackPlans} · 검수 실측 {reviewedPlans}건"));

// 실측이 다 모이면 0, 가정이 섞여 있으면 2 ("아직 아니다").
return measuredPlans > 0 && reviewMeasured ? 0 : 2;

static int Count(JsonElement counts, string name) =>
    counts.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : 0;

static double[] Numbers(string csv) =>
    [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => double.Parse(s, CultureInfo.InvariantCulture))];
