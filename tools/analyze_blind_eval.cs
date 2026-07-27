// 블라인드 A/B 평가 통계 처리. docs/15 §6 · T5-15.
//
// 실행:
//   dotnet run tools/analyze_blind_eval.cs
//   dotnet run tools/analyze_blind_eval.cs -- --self-test     검정 구현을 알려진 값으로 검증
//
// 입력:
//   docs/measurements/blind_eval_raw.jsonl   응답 원자료 (T5-14)
//   docs/measurements/blind_eval_key.md      정답 키 (T5-13)
// 산출:
//   docs/measurements/blind_eval_result.md
//
// 검정 세 가지 (docs/15 §6):
//   Q1  이항검정 H0 = 정답률 0.5 · Wilson 95% 신뢰구간
//   Q2  짝지은 t검정 (A - B) + Cohen's d · 95% 신뢰구간
//   쌍대 부호검정 (동점 제외) · Wilson 95% 신뢰구간
//
// **신뢰구간을 반드시 병기한다.** 점추정만 적으면 "정답률 55%" 가
// "구분한다" 인지 "표본이 모자란다" 인지 읽는 사람이 알 수 없다.
//
// 통계 라이브러리를 쓰지 않는다 — 이 저장소에 NuGet 을 하나 더 들이는 것보다
// 필요한 함수 넷(logΓ · 정규화 불완전 베타 · t분포 · 이항 꼬리)을 두는 편이 낫다.
// 구현이 맞는지는 --self-test 가 알려진 값으로 확인한다.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

string raw = "./docs/measurements/blind_eval_raw.jsonl";
string keyPath = "./docs/measurements/blind_eval_key.md";
string outPath = "./docs/measurements/blind_eval_result.md";
string measuredOn = "(미기록)";
bool selfTest = false;
bool pilot = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--raw" when i + 1 < args.Length: raw = args[++i]; break;
        case "--key" when i + 1 < args.Length: keyPath = args[++i]; break;
        case "--out" when i + 1 < args.Length: outPath = args[++i]; break;
        case "--date" when i + 1 < args.Length: measuredOn = args[++i]; break;
        case "--self-test": selfTest = true; break;

        // 예비 실시(docs/15 §1) 결과에 붙는다. LLM 심사원이라는 사실을 리포트 첫머리에 못 박는다.
        case "--pilot": pilot = true; break;
        default: throw new ArgumentException($"모르는 인자다: {args[i]}");
    }
}

if (selfTest)
{
    return SelfTest();
}

// ── 1. 정답 키 ────────────────────────────────────────────────────────
var caseGroup = new Dictionary<int, string>();
var caseNpc = new Dictionary<int, string>();
var pairSides = new Dictionary<int, (string Left, string Right)>();

string keyText = File.Exists(keyPath) ? File.ReadAllText(keyPath) : string.Empty;

foreach (Match m in Regex.Matches(
    keyText,
    @"^\|\s*(?<no>\d{2})\s*\|\s*(?<group>[AB])\s*\|\s*#(?<npc>\d+)\s*\|",
    RegexOptions.Multiline | RegexOptions.CultureInvariant))
{
    int no = int.Parse(m.Groups["no"].Value, CultureInfo.InvariantCulture);

    caseGroup[no] = m.Groups["group"].Value;
    caseNpc[no] = m.Groups["npc"].Value;
}

foreach (Match m in Regex.Matches(
    keyText,
    @"^\|\s*(?<pair>\d{2})\s*\|\s*(?<left>\d{2})\s*\|\s*(?<lg>[AB])\s*\|\s*(?<right>\d{2})\s*\|\s*(?<rg>[AB])\s*\|",
    RegexOptions.Multiline | RegexOptions.CultureInvariant))
{
    pairSides[int.Parse(m.Groups["pair"].Value, CultureInfo.InvariantCulture)] =
        (m.Groups["lg"].Value, m.Groups["rg"].Value);
}

// ── 2. 원자료 ────────────────────────────────────────────────────────
var judgements = new List<Judgement>();
var pairwise = new List<Pairwise>();

if (File.Exists(raw))
{
    foreach (string line in File.ReadLines(raw))
    {
        if (line.Trim().Length == 0)
        {
            continue;
        }

        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;

        // _schema / _plan / _doc 줄은 설명이다. 분석에서 뺀다.
        if (root.TryGetProperty("_schema", out _) || root.TryGetProperty("_plan", out _))
        {
            continue;
        }

        string participant = Text(root, "participant") ?? "?";

        if (root.TryGetProperty("case", out JsonElement caseNo))
        {
            judgements.Add(new Judgement(
                participant,
                caseNo.GetInt32(),
                Text(root, "q1") ?? string.Empty,
                root.TryGetProperty("q2", out JsonElement q2) ? q2.GetInt32() : 0));
        }
        else if (root.TryGetProperty("pair", out JsonElement pairNo))
        {
            pairwise.Add(new Pairwise(participant, pairNo.GetInt32(), Text(root, "prefer") ?? string.Empty));
        }
    }
}

int participants = judgements.Select(j => j.Participant)
    .Concat(pairwise.Select(p => p.Participant))
    .Distinct(StringComparer.Ordinal)
    .Count();

// ── 3. Q1 — 이항검정 ─────────────────────────────────────────────────
int q1Total = 0;
int q1Correct = 0;

foreach (Judgement j in judgements)
{
    if (!caseGroup.TryGetValue(j.Case, out string? group) || j.Q1.Length == 0)
    {
        continue;
    }

    q1Total++;

    // A군 = LLM 플랜, B군 = 사람이 짠 폴백. 맞히면 정답이다.
    bool guessedLlm = string.Equals(j.Q1, "llm", StringComparison.OrdinalIgnoreCase);

    if (guessedLlm == (group == "A"))
    {
        q1Correct++;
    }
}

double q1Rate = q1Total == 0 ? 0 : (double)q1Correct / q1Total;
double q1P = Stats.BinomialTwoSidedP(q1Correct, q1Total);
(double q1Low, double q1High) = Stats.WilsonInterval(q1Correct, q1Total);

// ── 4. Q2 — 짝지은 t검정 ─────────────────────────────────────────────
//
// 짝은 (참가자, NPC) 다. 같은 사람이 같은 NPC 의 A·B 를 둘 다 봤을 때만 쌍이 된다.
var scores = new Dictionary<(string Participant, string Npc, string Group), int>();

foreach (Judgement j in judgements)
{
    if (j.Q2 is < 1 or > 5 || !caseGroup.TryGetValue(j.Case, out string? group))
    {
        continue;
    }

    scores[(j.Participant, caseNpc[j.Case], group)] = j.Q2;
}

var deltas = new List<double>();

foreach (((string participant, string npc, string group), int a) in scores)
{
    if (group == "A" && scores.TryGetValue((participant, npc, "B"), out int b))
    {
        deltas.Add(a - b);
    }
}

(double q2Mean, double q2Sd) = Stats.MeanAndSd(deltas);
double q2Se = deltas.Count == 0 ? 0 : q2Sd / Math.Sqrt(deltas.Count);
double q2T = q2Se == 0 ? 0 : q2Mean / q2Se;
int q2Df = Math.Max(0, deltas.Count - 1);
double q2P = q2Df == 0 ? 1 : Stats.StudentTTwoSidedP(q2T, q2Df);
double q2D = q2Sd == 0 ? 0 : q2Mean / q2Sd;
double q2Crit = q2Df == 0 ? 0 : Stats.StudentTInverse(0.975, q2Df);

// ── 5. 쌍대 — 부호검정 ───────────────────────────────────────────────
int preferA = 0;
int preferB = 0;
int ties = 0;

foreach (Pairwise p in pairwise)
{
    if (!pairSides.TryGetValue(p.Pair, out (string Left, string Right) sides))
    {
        continue;
    }

    string choice = p.Prefer.ToLowerInvariant();

    if (choice == "tie")
    {
        ties++;
        continue;
    }

    string group = choice == "left" ? sides.Left : choice == "right" ? sides.Right : string.Empty;

    if (group == "A")
    {
        preferA++;
    }
    else if (group == "B")
    {
        preferB++;
    }
}

int signN = preferA + preferB;
double signP = Stats.BinomialTwoSidedP(preferA, signN);
(double signLow, double signHigh) = Stats.WilsonInterval(preferA, signN);

// ── 6. 판정 ──────────────────────────────────────────────────────────
const int MinParticipants = 6;
const int TargetJudgements = 480;

bool enough = participants >= MinParticipants && q1Total >= TargetJudgements;

string quadrant = !enough
    ? "**결론 보류**"
    : (q1Rate > 0.70, q2Mean >= 0) switch
    {
        (false, true) => "Q1 ≈ 50% + Q2 A ≥ B — **오써링 자동화로 가치 확정.** 런타임 LLM 은 선택 사항",
        (false, false) => "Q1 ≈ 50% + Q2 A < B — LLM 플랜이 열등한데 티만 안 남. 프롬프트·모델 개선 필요",
        (true, true) => "Q1 > 70% + Q2 A > B — **최상 결과.** 런타임 투입 검토",
        (true, false) => "Q1 > 70% + Q2 A < B — LLM 플랜이 어색함. 액션 카탈로그·검증기 강화 필요",
    };

// ── 7. 리포트 ────────────────────────────────────────────────────────
var sb = new StringBuilder(16 * 1024);

sb.Append(pilot ? "# 블라인드 A/B **예비** 평가 — 결과\n\n" : "# 블라인드 A/B 평가 — 결과\n\n");
sb.Append("> `tools/analyze_blind_eval.cs` 가 만든다. 손으로 고치지 않는다.\n");
sb.Append(CultureInfo.InvariantCulture,
    $"> 원자료는 `{Path.GetFileName(raw)}`, 정답 키는 `{Path.GetFileName(keyPath)}` 다.\n\n");

if (pilot)
{
    // 이 문단이 없으면 다음 사람이 이 파일을 정식 결과로 읽는다.
    sb.Append("> ⚠ **심사원이 사람이 아니라 LLM 이다.** docs/15 §1 이 말하는 예비 실시이고,\n");
    sb.Append("> docs/15 §6 의 정식 측정(사내 기획자·개발자 12명 이상)이 아니다.\n");
    sb.Append("> **P5 게이트의 `n ≥ 480` 을 충족하지 않는다** — 게이트는 그대로 미달이다.\n");
    sb.Append("> 쓰임새는 하나다: 사람 12명 × 1시간을 쓰기 전에 방향을 본다.\n\n");
}

sb.Append("| 항목 | 값 |\n|---|---|\n");
sb.Append(CultureInfo.InvariantCulture, $"| 측정일 | {measuredOn} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 참가자 | {participants}명 (목표 12 · 최소 {MinParticipants}) |\n");
sb.Append(CultureInfo.InvariantCulture, $"| Q1·Q2 판정 | {q1Total} (목표 {TargetJudgements}) |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 쌍대 비교 | {pairwise.Count} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 자료 | A군 {caseGroup.Values.Count(g => g == "A")} · B군 {caseGroup.Values.Count(g => g == "B")} |\n\n");

sb.Append("## 1. 판정\n\n");
sb.Append(CultureInfo.InvariantCulture, $"{quadrant}\n\n");

if (!enough)
{
    sb.Append("docs/15 §6 — **참가자가 6명 미만이면 결론을 내지 말고 표본을 늘린다.**\n");
    sb.Append(CultureInfo.InvariantCulture,
        $"지금은 참가자 {participants}명 · 판정 {q1Total}건이다. ");
    sb.Append("아래 숫자는 그 상태에서 계산한 것이고, 판정으로 쓰지 않는다.\n\n");
}

sb.Append("## 2. Q1 — 이항검정 (H0: 정답률 = 0.5)\n\n");
sb.Append("| 항목 | 값 |\n|---|---|\n");
sb.Append(CultureInfo.InvariantCulture, $"| 정답 / 전체 | {q1Correct} / {q1Total} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 정답률 | {q1Rate:P1} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 95% 신뢰구간 (Wilson) | [{q1Low:P1}, {q1High:P1}] |\n");
sb.Append(CultureInfo.InvariantCulture, $"| p (양측) | {Format(q1P)} |\n");
sb.Append(CultureInfo.InvariantCulture,
    $"| 해석 | {(q1Total == 0 ? "자료 없음" : q1P < 0.05 ? "0.5 와 유의하게 다르다" : "0.5 와 다르다고 할 수 없다")} |\n\n");

sb.Append("## 3. Q2 — 짝지은 t검정 (A − B)\n\n");
sb.Append("같은 참가자가 같은 NPC 의 A·B 를 둘 다 본 경우만 쌍으로 센다.\n\n");
sb.Append("| 항목 | 값 |\n|---|---|\n");
sb.Append(CultureInfo.InvariantCulture, $"| 쌍 수 | {deltas.Count} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 평균 차이 (A − B) | {q2Mean:F3} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 표준편차 | {q2Sd:F3} |\n");
sb.Append(CultureInfo.InvariantCulture,
    $"| 95% 신뢰구간 | [{q2Mean - (q2Crit * q2Se):F3}, {q2Mean + (q2Crit * q2Se):F3}] |\n");
sb.Append(CultureInfo.InvariantCulture, $"| t ({q2Df} df) | {q2T:F3} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| p (양측) | {Format(q2P)} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| Cohen's d | {q2D:F3} |\n\n");

sb.Append("## 4. 쌍대 비교 — 부호검정\n\n");
sb.Append("| 항목 | 값 |\n|---|---|\n");
sb.Append(CultureInfo.InvariantCulture, $"| A 선호 | {preferA} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| B 선호 | {preferB} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 동점 (검정에서 제외) | {ties} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| A 선호율 | {(signN == 0 ? 0 : (double)preferA / signN):P1} |\n");
sb.Append(CultureInfo.InvariantCulture, $"| 95% 신뢰구간 (Wilson) | [{signLow:P1}, {signHigh:P1}] |\n");
sb.Append(CultureInfo.InvariantCulture, $"| p (양측) | {Format(signP)} |\n\n");

sb.Append("## 5. 다시 돌리는 법\n\n");
sb.Append("```\n");
sb.Append("dotnet run tools/analyze_blind_eval.cs -- --date \"2026-07-27 KST\"\n");
sb.Append("dotnet run tools/analyze_blind_eval.cs -- --self-test   # 검정 구현 검증\n");
sb.Append("```\n");

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
File.WriteAllText(outPath, sb.ToString().ReplaceLineEndings("\n"), new UTF8Encoding(false));

Console.WriteLine(string.Create(
    CultureInfo.InvariantCulture,
    $"{outPath}: 참가자 {participants}명 · 판정 {q1Total}건 · 쌍 {deltas.Count}개 · 쌍대 {signN}건"));

return enough ? 0 : 2;   // 2 = 자료 부족. 실패가 아니라 "아직 아니다" 다.

static string? Text(JsonElement root, string name) =>
    root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

static string Format(double p) =>
    p < 0.0001
        ? "< 0.0001"
        : p.ToString("F4", CultureInfo.InvariantCulture);

// ── 자기 검증 ────────────────────────────────────────────────────────
//
// 통계 구현이 틀리면 보고서 전체가 거짓이 된다. 라이브러리를 안 쓰기로 한 이상
// 알려진 값으로 확인하는 것이 유일한 방어다.
static int SelfTest()
{
    int failed = 0;

    // 이항검정 — n=20, k=15, p0=0.5 의 양측 p 는 2 × 21700/2^20 = 0.0413894 다.
    Check("binom(15,20)", Stats.BinomialTwoSidedP(15, 20), 0.0413894, 1e-6);
    Check("binom(10,20)", Stats.BinomialTwoSidedP(10, 20), 1.0, 1e-9);
    Check("binom(20,20)", Stats.BinomialTwoSidedP(20, 20), 2.0 / 1048576.0, 1e-12);

    // Wilson 95% — 15/20 은 [0.5313, 0.8881] 이다.
    //   center = 0.75 + z²/40 = 0.846037 · margin = z·√(0.1875/20 + z²/1600) = 0.212690
    //   분모 = 1 + z²/20 = 1.192074
    (double low, double high) = Stats.WilsonInterval(15, 20);
    Check("wilson low", low, 0.531299, 1e-5);
    Check("wilson high", high, 0.888138, 1e-5);

    // t 임계값 — 표에서 t(0.975, 4) = 2.776 · t(0.975, 10) = 2.228 · t(0.975, 1000) ≈ 1.962
    Check("t crit df=4", Stats.StudentTInverse(0.975, 4), 2.776, 1e-3);
    Check("t crit df=10", Stats.StudentTInverse(0.975, 10), 2.228, 1e-3);
    Check("t crit df=1000", Stats.StudentTInverse(0.975, 1000), 1.962, 1e-3);

    // 그 임계값에서 양측 p 는 정확히 0.05 여야 한다 (CDF 와 역함수의 왕복).
    Check("t p at crit", Stats.StudentTTwoSidedP(2.776445, 4), 0.05, 1e-4);
    Check("t p at 0", Stats.StudentTTwoSidedP(0, 4), 1.0, 1e-9);

    // 평균·표준편차 (표본, n-1)
    (double mean, double sd) = Stats.MeanAndSd([1, 2, 3, 4, 5]);
    Check("mean", mean, 3.0, 1e-12);
    Check("sd", sd, 1.5811388, 1e-6);

    Console.WriteLine(failed == 0 ? "self-test: 통과" : $"self-test: {failed}건 실패");

    return failed == 0 ? 0 : 1;

    void Check(string name, double actual, double expected, double tolerance)
    {
        bool ok = Math.Abs(actual - expected) <= tolerance;

        if (!ok)
        {
            failed++;
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  [{(ok ? "ok" : "FAIL")}] {name}: {actual:G8} (기대 {expected:G8} ±{tolerance:G3})"));
    }
}

/// <summary>응답 한 줄.</summary>
internal readonly record struct Judgement(string Participant, int Case, string Q1, int Q2);

/// <summary>쌍대 비교 한 줄.</summary>
internal readonly record struct Pairwise(string Participant, int Pair, string Prefer);

/// <summary>
/// 검정 넷. docs/15 §6.
///
/// 라이브러리를 들이지 않는다 — 필요한 것은 logΓ · 정규화 불완전 베타 ·
/// t분포 · 이항 꼬리 넷뿐이고, 맞는지는 <c>--self-test</c> 가 알려진 값으로 확인한다.
/// </summary>
internal static class Stats
{
    /// <summary>표본 평균과 표준편차(n−1).</summary>
    public static (double Mean, double Sd) MeanAndSd(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return (0, 0);
        }

        double mean = values.Sum() / values.Count;

        if (values.Count == 1)
        {
            return (mean, 0);
        }

        double sum = values.Sum(v => (v - mean) * (v - mean));

        return (mean, Math.Sqrt(sum / (values.Count - 1)));
    }

    /// <summary>
    /// 이항검정 양측 p (H0: p = 0.5).
    /// 분포가 대칭이라 <c>2 × min(P(X ≤ k), P(X ≥ k))</c> 로 충분하다.
    /// </summary>
    public static double BinomialTwoSidedP(int k, int n)
    {
        if (n == 0)
        {
            return 1;
        }

        double lower = 0;
        double upper = 0;

        for (int i = 0; i <= n; i++)
        {
            double p = Math.Exp(LogChoose(n, i) - (n * Math.Log(2)));

            if (i <= k)
            {
                lower += p;
            }

            if (i >= k)
            {
                upper += p;
            }
        }

        return Math.Min(1.0, 2.0 * Math.Min(lower, upper));
    }

    /// <summary>
    /// 비율의 95% Wilson 점수 구간.
    /// 정규 근사(Wald)를 쓰지 않는다 — 0 이나 1 근처에서 구간이 범위를 벗어난다.
    /// </summary>
    public static (double Low, double High) WilsonInterval(int successes, int n, double z = 1.959964)
    {
        if (n == 0)
        {
            return (0, 0);
        }

        double p = (double)successes / n;
        double z2 = z * z;
        double denominator = 1 + (z2 / n);
        double center = p + (z2 / (2 * n));
        double margin = z * Math.Sqrt((p * (1 - p) / n) + (z2 / (4.0 * n * n)));

        return ((center - margin) / denominator, (center + margin) / denominator);
    }

    /// <summary>Student t 양측 p.</summary>
    public static double StudentTTwoSidedP(double t, int df)
    {
        if (df <= 0)
        {
            return 1;
        }

        double x = df / (df + (t * t));

        return BetaRegularized(df / 2.0, 0.5, x);
    }

    /// <summary>Student t 분위수. 이분법 — 정확도보다 구현이 맞는지가 중요하다.</summary>
    public static double StudentTInverse(double p, int df)
    {
        if (df <= 0)
        {
            return 0;
        }

        double low = 0;
        double high = 1000;

        for (int i = 0; i < 200; i++)
        {
            double mid = (low + high) / 2;
            double cdf = 1 - (StudentTTwoSidedP(mid, df) / 2);

            if (cdf < p)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) / 2;
    }

    private static double LogChoose(int n, int k) =>
        LogGamma(n + 1) - LogGamma(k + 1) - LogGamma(n - k + 1);

    /// <summary>Lanczos 근사.</summary>
    private static double LogGamma(double x)
    {
        double[] c =
        [
            676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012,
            9.9843695780195716e-6, 1.5056327351493116e-7,
        ];

        if (x < 0.5)
        {
            return Math.Log(Math.PI / Math.Sin(Math.PI * x)) - LogGamma(1 - x);
        }

        x -= 1;

        double a = 0.99999999999980993;
        double t = x + 7.5;

        for (int i = 0; i < c.Length; i++)
        {
            a += c[i] / (x + i + 1);
        }

        return (0.5 * Math.Log(2 * Math.PI)) + ((x + 0.5) * Math.Log(t)) - t + Math.Log(a);
    }

    /// <summary>정규화 불완전 베타 I_x(a, b). Lentz 연분수.</summary>
    private static double BetaRegularized(double a, double b, double x)
    {
        if (x <= 0)
        {
            return 0;
        }

        if (x >= 1)
        {
            return 1;
        }

        double front = Math.Exp(
            LogGamma(a + b) - LogGamma(a) - LogGamma(b)
            + (a * Math.Log(x)) + (b * Math.Log(1 - x)));

        return x < (a + 1) / (a + b + 2)
            ? front * ContinuedFraction(a, b, x) / a
            : 1 - (Math.Exp(
                LogGamma(a + b) - LogGamma(a) - LogGamma(b)
                + (b * Math.Log(1 - x)) + (a * Math.Log(x))) * ContinuedFraction(b, a, 1 - x) / b);
    }

    private static double ContinuedFraction(double a, double b, double x)
    {
        const double Tiny = 1e-30;

        double f = 1;
        double c = 1;
        double d = 0;

        for (int i = 0; i <= 300; i++)
        {
            int m = i / 2;

            double numerator = i == 0
                ? 1
                : i % 2 == 0
                    ? m * (b - m) * x / ((a + (2.0 * m) - 1) * (a + (2.0 * m)))
                    : -(a + m) * (a + b + m) * x / ((a + (2.0 * m)) * (a + (2.0 * m) + 1));

            d = 1 + (numerator * d);
            d = Math.Abs(d) < Tiny ? Tiny : 1 / d;

            c = 1 + (numerator / c);
            c = Math.Abs(c) < Tiny ? Tiny : c;

            double delta = c * d;
            f *= delta;

            if (Math.Abs(1 - delta) < 1e-12)
            {
                break;
            }
        }

        return f - 1;
    }
}
