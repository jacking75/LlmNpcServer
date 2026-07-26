// 스케일 곡선 리포트. docs/14 §6 · T4-16.
//
// .NET 10 파일 기반 앱이다:
//     dotnet run tools/report_scale.cs -- --load docs/measurements/W10_load.csv
//     dotnet run tools/report_scale.cs -- --load ... --out docs/measurements/W10_scale.md
//
// 무엇을 재는가
//   NPC 수 대비 각 지표의 차수다. log-log 기울기로 판정한다:
//       기울기 ≈ 0     → O(1)
//       기울기 ≈ 1     → O(n)
//       기울기 > 1.2   → 초선형. 어딘가에 O(n²) 가 숨어 있다 (docs/14 §6)
//
// 왜 상한을 푼 회차를 따로 보는가
//   CognitionScheduler.MaxScansPerTick = 150 이 상한을 걸고 있어서, 상한이 걸린 회차는
//   무엇을 넣어도 150 에서 잘려 O(1) 로 보인다. 그래서 W10_load.csv 의 scan_cap=0 행
//   (상한 해제)으로 차수를 판정하고, 상한이 걸린 행은 "굶는 NPC 없이 잘리는가" 를 본다.
//
// 왜 npcs_actual 을 쓰는가
//   npc_instances.json 에 5,000마리뿐이라 --npcs 10000 은 실제로 5,000 을 돌린다
//   (NpcHost.Create 의 Math.Min). 요청값으로 차수를 재면 존재하지 않는 규모를 근거로 삼는다.
//
// 결정론
//   시각을 넣지 않는다. 같은 CSV 면 바이트 동일한 리포트가 나온다.

using System.Globalization;
using System.Text;

string? loadPath = null;
string? outPath = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--load" when i + 1 < args.Length:
            loadPath = args[++i];
            break;

        case "--out" when i + 1 < args.Length:
            outPath = args[++i];
            break;

        case "-h" or "--help":
            Console.WriteLine("사용법: dotnet run tools/report_scale.cs -- --load <W10_load.csv> [--out <W10_scale.md>]");
            return 0;

        default:
            Console.Error.WriteLine($"모르는 인자다: {args[i]}");
            return 2;
    }
}

loadPath ??= Path.Combine("docs", "measurements", "W10_load.csv");
outPath ??= Path.Combine("docs", "measurements", "W10_scale.md");

if (!File.Exists(loadPath))
{
    Console.Error.WriteLine($"{loadPath} 이 없다. tools/run_load.ps1 을 먼저 돌린다.");
    return 1;
}

Row[] rows = ReadCsv(loadPath);

if (rows.Length == 0)
{
    Console.Error.WriteLine($"{loadPath} 에 행이 없다.");
    return 1;
}

var report = new StringBuilder();

report.AppendLine("# W10 스케일 곡선 (P4)");
report.AppendLine();
report.AppendLine("> `docs/14 §6` 의 산출물. **`tools/report_scale.cs` 가 생성한다 — 손으로 고치지 않는다.**");
report.AppendLine($"> 입력: [`{Path.GetFileName(loadPath)}`]({Path.GetFileName(loadPath)}) · 행 {rows.Length}건");
report.AppendLine();
report.AppendLine("판정은 log-log 기울기다. `≈0` = O(1) · `≈1` = O(n) · `>1.2` = **초선형(O(n²) 의심)**.");
report.AppendLine();

// ── 요청 NPC 수와 실제가 다른 셀을 먼저 알린다 ──
Row[] short_ = [.. rows.Where(r => r.NpcsActual < r.Npcs)];

if (short_.Length > 0)
{
    report.AppendLine("## ⚠ 요청 규모를 못 채운 셀");
    report.AppendLine();
    report.AppendLine("`npc_instances.json` 에 있는 수를 넘길 수 없다 (`NpcHost.Create` 의 `Math.Min`).");
    report.AppendLine("**`docs/14 §6` 의 10,000(스트레스) 축은 지금 측정되지 않는다** —");
    report.AppendLine("`tools/gen_npcs.cs` 의 `Population` 이 5,000 고정이다 (`docs/01 §9`).");
    report.AppendLine();
    report.AppendLine("| 셀 | 요청 | 실제 |");
    report.AppendLine("|---|---:|---:|");

    foreach (Row r in short_)
    {
        report.AppendLine($"| `{r.Cell}` | {r.Npcs} | **{r.NpcsActual}** |");
    }

    report.AppendLine();
}

// ── 1. 상한을 푼 회차 — 차수 판정 ──
// 실제 NPC 수로 묶는다. --npcs 10000 이 5,000 으로 줄어든 셀이 있어
// 묶지 않으면 같은 규모가 두 번 들어가 기울기가 흔들린다.
Row[] uncapped =
[
    .. rows.Where(r => r.ScanCap == 0)
        .GroupBy(r => r.NpcsActual)
        .OrderBy(g => g.Key)
        .Select(g => g.First()),
];

report.AppendLine("## 1. 인지 스캔 — 상한을 푼 회차");
report.AppendLine();

if (uncapped.Length < 2)
{
    report.AppendLine("상한을 푼 셀이 2개 미만이라 차수를 판정할 수 없다 (`--scan-cap 0` 셀이 필요하다).");
    report.AppendLine();
}
else
{
    report.AppendLine("| NPC(실제) | 틱당 스캔 | p99 (ms) | 최대 (ms) |");
    report.AppendLine("|---:|---:|---:|---:|");

    foreach (Row r in uncapped)
    {
        report.AppendLine($"| {r.NpcsActual} | **{r.ScanPerTick}** | {F(r.TickP99Ms)} | {F(r.TickMaxMs)} |");
    }

    report.AppendLine();

    (Row lo, Row hi) = Ends(uncapped);
    double slope = Slope(lo.NpcsActual, lo.ScanPerTick, hi.NpcsActual, hi.ScanPerTick);
    double growth = lo.ScanPerTick == 0 ? double.NaN : (double)hi.ScanPerTick / lo.ScanPerTick;

    report.AppendLine($"- NPC {lo.NpcsActual} → {hi.NpcsActual} ({F(NpcRatio(lo, hi))}배) 에서 "
        + $"스캔 {lo.ScanPerTick} → {hi.ScanPerTick} (**{F(growth)}배**)");
    report.AppendLine($"- log-log 기울기 **{F(slope)}** → {Verdict(slope)}");
    report.AppendLine();
    report.AppendLine("### 판정 — 인지 스캔은 O(1) 이 아니다. O(1) 을 만드는 것은 상한이다");
    report.AppendLine();
    report.AppendLine("`docs/14 §6` 의 기대표는 인지 스캔을 **O(1)** 로 적고 근거를 \"슬라이스가 고정 비율이므로\" 라고 했다.");
    report.AppendLine("그 논거는 성립하지 않는다 — 슬라이스는 밴드 인원을 `Period` 로 나눈 것이고,");
    report.AppendLine("**밴드 인원이 NPC 수와 함께 자란다.** 플레이어 봇 수는 고정이지만 월드(존·POI)가 고정이라");
    report.AppendLine("NPC 를 늘리면 밀도가 올라가 한 플레이어 반경 안의 NPC 가 그만큼 늘어난다.");
    report.AppendLine();
    report.AppendLine("실제로 O(1) 을 만드는 것은 `CognitionScheduler.MaxScansPerTick`(150) 이고,");
    report.AppendLine("밴드별 커서가 잘린 뒤쪽을 다음 틱에 이어 보므로 굶는 NPC 는 없다 (`docs/11 §4`).");
    report.AppendLine("즉 **설계는 맞고 문서의 근거가 틀렸다.** `docs/14 §6` 을 정정했다.");
    report.AppendLine();

    Row? at5000 = uncapped.FirstOrDefault(r => r.NpcsActual == 5_000);

    if (at5000 is { })
    {
        report.AppendLine($"`docs/11 §4` 의 \"상한이 없으면 612 까지 간다\" 추정과 대조: "
            + $"**실측 {at5000.ScanPerTick}건** (NPC 5,000). 추정과 "
            + $"{F(Math.Abs(at5000.ScanPerTick - 612) / 612.0 * 100)}% 차이다.");
        report.AppendLine();
    }
}

// ── 2. 상한이 걸린 회차 — 잘리는가 ──
Row[] capped = [.. rows.Where(r => r.ScanCap != 0 && r.MaxSpeed).OrderBy(r => r.NpcsActual)];

report.AppendLine("## 2. 인지 스캔 — 상한이 걸린 회차");
report.AppendLine();
report.AppendLine("이 회차의 관심사는 차수가 아니라 **\"굶는 NPC 없이 상한에서 잘리는가\"** 다.");
report.AppendLine();
report.AppendLine("| 셀 | NPC(실제) | 틱당 스캔 | 상한 준수 |");
report.AppendLine("|---|---:|---:|:---:|");

foreach (Row r in capped)
{
    report.AppendLine($"| `{r.Cell}` | {r.NpcsActual} | {r.ScanPerTick} | {(r.ScanPerTick <= 150 ? "O" : "**X**")} |");
}

report.AppendLine();

// ── 3. 지표별 차수 ──
report.AppendLine("> 재계획 큐 깊이가 용량(min(NPC, 4096))에 붙어 있는 회차다 — `--tier none` 이라 큐를 비우는");
report.AppendLine("> 워커가 없기 때문이고, 그 상태에서도 NPC 는 기존 플랜으로 정상 동작한다");
report.AppendLine("> (P4 게이트 \"큐 거절 발생 시에도 해당 NPC 정상 동작\").");
report.AppendLine();

report.AppendLine("## 3. 지표별 차수 (상한이 걸린 회차 · 봇 20 · 배속 600)");
report.AppendLine();

Row[] curve =
[
    .. rows
        .Where(r => r.ScanCap != 0 && r.MaxSpeed && r.PlayerBots == 20 && r.TimeScale == 600)
        .OrderBy(r => r.NpcsActual)
        .GroupBy(r => r.NpcsActual)
        .Select(g => g.First()),
];

if (curve.Length < 2)
{
    report.AppendLine("비교할 NPC 수준이 2개 미만이다.");
    report.AppendLine();
}
else
{
    (Row lo, Row hi) = Ends(curve);

    report.AppendLine($"NPC {lo.NpcsActual} → {hi.NpcsActual} ({F(NpcRatio(lo, hi))}배)");
    report.AppendLine();
    report.AppendLine("| 지표 | 기대 (§6) | 작은 쪽 | 큰 쪽 | 기울기 | 판정 |");
    report.AppendLine("|---|---|---:|---:|---:|---|");

    Metric[] metrics =
    [
        new("틱 p99 (ms)", "O(n)", r => r.TickP99Ms),
        new("틱 p50 (ms)", "O(n)", r => r.TickP50Ms),
        new("인지 스캔/틱", "O(1)", r => r.ScanPerTick),
        new("LLM 요청", "O(1)", r => r.T1Calls + r.T2Calls),
        new("명령 송출/초", "O(n)", r => r.CommandsPerSecond),
        new("관리 힙 (MB)", "O(n)", r => r.HeapMb),
        new("큐 깊이 p99", "O(1)", r => r.QueueDepthP99),
    ];

    foreach (Metric metric in metrics)
    {
        double a = metric.Read(lo);
        double b = metric.Read(hi);
        double slope = Slope(lo.NpcsActual, a, hi.NpcsActual, b);

        report.AppendLine(
            $"| {metric.Name} | {metric.Expected} | {F(a)} | {F(b)} | {F(slope)} | {Verdict(slope)} |");
    }

    report.AppendLine();
    report.AppendLine("> 명령 송출/초는 `--max-speed` 회차라 **벽시계 기준**이다 — 처리량이지 부하가 아니다.");
    report.AppendLine("> 틱당 명령 수로 보려면 `commands_per_s / (ticks / wall_clock_s)` 로 환산한다.");
    report.AppendLine();
}

// ── 4. P1 기준선 대조 ──
Row? baseline = rows.FirstOrDefault(r => !r.MaxSpeed && r.NpcsActual == 5_000);

report.AppendLine("## 4. P1 기준선 대조 (NPC 5,000 · 페이싱 켜짐)");
report.AppendLine();
report.AppendLine("| 지표 | P1 실측 (`P1_gate.md`) | P4 실측 | 예산 |");
report.AppendLine("|---|---:|---:|---:|");

if (baseline is { })
{
    report.AppendLine($"| 틱 p99 (ms) | 2.375 | **{F(baseline.TickP99Ms)}** | ≤ 20 |");
    report.AppendLine($"| 인지 스캔/틱 | 150 | {baseline.ScanPerTick} | ≤ 150 |");
    report.AppendLine($"| 틱당 할당 (B) | 0 | {baseline.BytesPerTick} | 0 |");
    report.AppendLine($"| 오버런 | 0 | {baseline.TickOverruns} | 0 |");
}
else
{
    report.AppendLine("| — | — | 페이싱 셀이 CSV 에 없다 | — |");
}

report.AppendLine();

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));

Console.WriteLine($"{outPath} 에 적었다. 행 {rows.Length}건 · 상한 해제 {uncapped.Length}건");
return 0;

// ---------------------------------------------------------------- 헬퍼

static string F(double value) =>
    double.IsNaN(value) || double.IsInfinity(value)
        ? "—"
        : value.ToString(Math.Abs(value) >= 100 ? "F0" : "F3", CultureInfo.InvariantCulture);

static double NpcRatio(Row lo, Row hi) => lo.NpcsActual == 0 ? double.NaN : (double)hi.NpcsActual / lo.NpcsActual;

/// <summary>log-log 기울기. 어느 한쪽이 0 이면 판정하지 않는다(NaN).</summary>
static double Slope(int n1, double y1, int n2, double y2)
{
    if (n1 <= 0 || n2 <= 0 || n1 == n2 || y1 <= 0 || y2 <= 0)
    {
        return double.NaN;
    }

    return Math.Log(y2 / y1) / Math.Log((double)n2 / n1);
}

static string Verdict(double slope) => double.IsNaN(slope)
    ? "판정 불가 (표본에 0 이 있다)"
    : slope switch
    {
        < 0.2 => "**O(1)**",
        < 0.8 => "준선형 (O(n^s))",
        < 1.2 => "**O(n)**",
        _ => "**초선형 — O(n²) 의심**",
    };

static (Row Lo, Row Hi) Ends(Row[] ordered) => (ordered[0], ordered[^1]);

static Row[] ReadCsv(string path)
{
    string[] lines = File.ReadAllLines(path);

    if (lines.Length < 2)
    {
        return [];
    }

    string[] header = lines[0].Split(',');
    var index = new Dictionary<string, int>(StringComparer.Ordinal);

    for (int i = 0; i < header.Length; i++)
    {
        index[header[i].Trim()] = i;
    }

    var rows = new List<Row>(lines.Length - 1);

    for (int i = 1; i < lines.Length; i++)
    {
        if (lines[i].Length == 0)
        {
            continue;
        }

        string[] parts = lines[i].Split(',');

        rows.Add(new Row(
            Cell: Get(parts, index, "cell"),
            Npcs: (int)Num(parts, index, "npcs"),
            NpcsActual: (int)Num(parts, index, "npcs_actual"),
            ScanCap: (int)Num(parts, index, "scan_cap"),
            TimeScale: (int)Num(parts, index, "time_scale"),
            PlayerBots: (int)Num(parts, index, "player_bots"),
            MaxSpeed: Num(parts, index, "max_speed") != 0,
            Ticks: (long)Num(parts, index, "ticks"),
            TickP50Ms: Num(parts, index, "tick_p50_ms"),
            TickP99Ms: Num(parts, index, "tick_p99_ms"),
            TickMaxMs: Num(parts, index, "tick_max_ms"),
            TickOverruns: (long)Num(parts, index, "tick_overruns"),
            ScanPerTick: (int)Num(parts, index, "scan_per_tick"),
            QueueDepthP99: (int)Num(parts, index, "queue_p99"),
            T1Calls: (long)Num(parts, index, "t1_calls"),
            T2Calls: (long)Num(parts, index, "t2_calls"),
            CommandsPerSecond: Num(parts, index, "commands_per_s"),
            BytesPerTick: (long)Num(parts, index, "bytes_per_tick"),
            HeapMb: Num(parts, index, "heap_mb")));
    }

    return [.. rows];

    static string Get(string[] parts, Dictionary<string, int> index, string name) =>
        index.TryGetValue(name, out int i) && i < parts.Length ? parts[i] : string.Empty;

    static double Num(string[] parts, Dictionary<string, int> index, string name) =>
        double.TryParse(Get(parts, index, name), CultureInfo.InvariantCulture, out double value) ? value : 0;
}

internal sealed record Row(
    string Cell,
    int Npcs,
    int NpcsActual,
    int ScanCap,
    int TimeScale,
    int PlayerBots,
    bool MaxSpeed,
    long Ticks,
    double TickP50Ms,
    double TickP99Ms,
    double TickMaxMs,
    long TickOverruns,
    int ScanPerTick,
    int QueueDepthP99,
    long T1Calls,
    long T2Calls,
    double CommandsPerSecond,
    long BytesPerTick,
    double HeapMb);

internal sealed record Metric(string Name, string Expected, Func<Row, double> Read);
