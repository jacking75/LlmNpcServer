using System.Collections.Immutable;
using System.Globalization;

namespace Npc.Cli;

/// <summary>부하 회차 한 셀 (G-03). CSV 한 줄에서 판정에 쓰는 값만 뽑는다.</summary>
/// <param name="Cell">셀 이름. 어느 회차의 어느 조합인가.</param>
/// <param name="TickP99Ms">틱 p99(ms).</param>
/// <param name="ScanPerTick">틱당 인지 스캔 수.</param>
/// <param name="BytesPerTick">틱당 할당 바이트.</param>
/// <param name="Overruns">예산(20ms)을 넘긴 틱 수.</param>
/// <param name="SequenceGaps">이벤트 시퀀스 갭.</param>
/// <param name="ScanCapSetting">
/// 그 회차가 건 스캔 상한. <b>0 이면 일부러 끈 대조 회차다</b>
/// (<c>LoadCell.ScanUncapped</c>) — 상한을 끄고 돌린 셀을 상한 위반으로 세면
/// 그 대조 실험이 영영 빨간불이 된다. <c>-1</c> 은 "기본값을 쓴다" 이고 판정 대상이다.
/// </param>
public readonly record struct PerfRow(
    string Cell,
    double TickP99Ms,
    double ScanPerTick,
    double BytesPerTick,
    long Overruns,
    long SequenceGaps,
    double ScanCapSetting = 0);

/// <summary>판정 하나 (G-03).</summary>
/// <param name="Cell">셀 이름.</param>
/// <param name="Rule">어느 규칙인가.</param>
/// <param name="Detail">사람이 읽는 근거.</param>
public readonly record struct PerfViolation(string Cell, string Rule, string Detail);

/// <summary>
/// <c>npc perf --check</c> (G-03). 부하 회차 결과를 절대 기준과 기준선에 견준다.
///
/// <para>
/// <b>워크플로 파일이 아니라 판정 명령이다.</b> 판정을 CI 설정에 적어 두면
/// <b>로컬에서 같은 답을 얻을 수 없고</b>, 그러면 "CI 에서만 빨간불" 이 된다 —
/// 그 상태에서는 아무도 고치기 전에 원인을 모른다 (A-09 가 CI 워크플로를 제외한 것과 같은 논거).
/// </para>
///
/// <para>
/// <b>기준선을 커밋한다.</b> 기준이 저장소 밖에 있으면 판정이 재현되지 않는다.
/// 갱신은 사람이 <c>--write-baseline</c> 으로 명시한다 — 도구가 조용히 기준을 낮추면
/// 회귀가 회귀로 보이지 않는다.
/// </para>
/// </summary>
public static class PerfCommand
{
    /// <summary>틱 예산(ms). CLAUDE.md §2.1.</summary>
    public const double TickBudgetMs = 20.0;

    /// <summary>인지 스캔 상한. <c>CognitionScheduler.MaxScansPerTick</c> 과 같다.</summary>
    public const double ScanCap = 150.0;

    /// <summary>
    /// 기준선 대비 허용 악화 배수. p99 가 기준선의 이 배를 넘으면 회귀다.
    ///
    /// <b>1.0 으로 두지 않는다</b> — 같은 코드도 회차마다 흔들리고, 그때마다 빨간불이면
    /// 사람이 이 판정을 끈다. 1.3 은 "노이즈가 아니라 변화" 로 읽을 만한 폭이다.
    /// </summary>
    public const double RegressionFactor = 1.3;

    /// <summary>기본 부하 결과 경로.</summary>
    public const string DefaultCsv = "docs/measurements/W10_load.csv";

    /// <summary>기준선 경로. <b>커밋한다.</b></summary>
    public const string DefaultBaseline = "docs/measurements/perf_baseline.csv";

    /// <summary>돌린다.</summary>
    /// <param name="ctx">CLI 맥락.</param>
    public static int Run(CliContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        string csvPath = Program.Flag(ctx, "--csv") ?? DefaultCsv;
        string baselinePath = Program.Flag(ctx, "--baseline") ?? DefaultBaseline;

        if (!File.Exists(csvPath))
        {
            ctx.Out.WriteLine($"부하 결과가 없다: {csvPath}");
            ctx.Out.WriteLine("먼저 돌린다: dotnet test -c Release --filter Category=Load");
            return Program.Failed;
        }

        ImmutableArray<PerfRow> rows = Read(csvPath);

        if (rows.IsEmpty)
        {
            ctx.Out.WriteLine($"{csvPath} 에 셀이 없다.");
            return Program.Failed;
        }

        if (Program.HasFlag(ctx, "--write-baseline"))
        {
            if (!ctx.Apply)
            {
                ctx.Out.WriteLine($"{csvPath} → {baselinePath} ({rows.Length}셀)");
                ctx.Out.WriteLine("dry-run 이다. 실제로 쓰려면 `--apply` 를 붙인다 — 기준을 낮추는 것은 사람이 정한다.");
                return Program.Ok;
            }

            Write(baselinePath, rows);

            ctx.Out.WriteLine($"기준선 갱신 {baselinePath} ({rows.Length}셀)");
            ctx.Out.WriteLine("이 파일은 커밋한다 — 기준이 저장소 밖에 있으면 판정이 재현되지 않는다.");

            return Program.Ok;
        }

        ImmutableArray<PerfRow> baseline =
            File.Exists(baselinePath) ? Read(baselinePath) : [];

        ImmutableArray<PerfViolation> violations = Judge(rows, baseline);

        ctx.Out.WriteLine($"부하 결과  {csvPath} ({rows.Length}셀)");
        ctx.Out.WriteLine(
            baseline.IsEmpty
                ? $"기준선     없다 ({baselinePath}) — 절대 기준만 본다"
                : $"기준선     {baselinePath} ({baseline.Length}셀)");
        ctx.Out.WriteLine();

        foreach (PerfViolation violation in violations)
        {
            ctx.Out.WriteLine($"  FAIL {violation.Rule,-16} {violation.Cell}");
            ctx.Out.WriteLine($"       {violation.Detail}");
        }

        if (violations.IsEmpty)
        {
            PerfRow worst = rows.MaxBy(r => r.TickP99Ms);

            ctx.Out.WriteLine(
                $"  OK   p99 최악 {worst.TickP99Ms:0.###}ms ({worst.Cell}) · "
                + $"할당 0 · 스캔 ≤ {ScanCap:0}");

            return Program.Ok;
        }

        ctx.Out.WriteLine();
        ctx.Out.WriteLine($"판정 실패 {violations.Length}건.");

        return Program.Failed;
    }

    /// <summary>
    /// 판정. <b>절대 기준이 먼저다</b> — 기준선이 나빠도 예산은 예산이다.
    /// </summary>
    /// <param name="rows">이번 회차.</param>
    /// <param name="baseline">기준선. 비어 있으면 절대 기준만 본다.</param>
    public static ImmutableArray<PerfViolation> Judge(
        ImmutableArray<PerfRow> rows, ImmutableArray<PerfRow> baseline)
    {
        var violations = ImmutableArray.CreateBuilder<PerfViolation>();

        foreach (PerfRow row in rows)
        {
            // 틱당 할당 0 이 이 프로젝트의 계기다 (CLAUDE.md §4). gen0 으로 재지 않는다 —
            // 그 값은 프로세스 전역이라 대시보드 HTTP·소켓이 전부 든다.
            if (row.BytesPerTick != 0)
            {
                violations.Add(new PerfViolation(
                    row.Cell, "bytes_per_tick", $"{row.BytesPerTick:0} B/tick — 0 이어야 한다"));
            }

            if (row.TickP99Ms > TickBudgetMs)
            {
                violations.Add(new PerfViolation(
                    row.Cell, "tick_p99", $"{row.TickP99Ms:0.###} ms > 예산 {TickBudgetMs:0} ms"));
            }

            // 상한을 일부러 끈 대조 회차(scan_cap == 0)는 이 규칙에서 뺀다.
            if (row.ScanCapSetting != 0 && row.ScanPerTick > ScanCap)
            {
                violations.Add(new PerfViolation(
                    row.Cell, "scan_per_tick", $"{row.ScanPerTick:0.#} > 상한 {ScanCap:0}"));
            }

            if (row.Overruns > 0)
            {
                violations.Add(new PerfViolation(
                    row.Cell, "tick_overruns", $"{row.Overruns}틱이 예산을 넘었다"));
            }

            if (row.SequenceGaps > 0)
            {
                violations.Add(new PerfViolation(
                    row.Cell, "sequence_gaps", $"{row.SequenceGaps}건 — 이벤트가 유실됐다 (N6)"));
            }

            // 기준선 대비 회귀. 같은 셀이 기준선에 없으면 비교하지 않는다 —
            // 새 셀을 회귀로 세면 매트릭스를 넓힐 수 없다.
            PerfRow before = baseline.FirstOrDefault(b =>
                string.Equals(b.Cell, row.Cell, StringComparison.Ordinal));

            if (before.Cell is null or "" || before.TickP99Ms <= 0)
            {
                continue;
            }

            double limit = before.TickP99Ms * RegressionFactor;

            if (row.TickP99Ms > limit)
            {
                violations.Add(new PerfViolation(
                    row.Cell,
                    "p99_regression",
                    $"{row.TickP99Ms:0.###} ms > 기준선 {before.TickP99Ms:0.###} ms × {RegressionFactor:0.#}"));
            }
        }

        return violations.ToImmutable();
    }

    /// <summary>부하 CSV 를 읽는다. 판정에 쓰는 열만 본다.</summary>
    public static ImmutableArray<PerfRow> Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        string[] lines = File.ReadAllLines(path);

        if (lines.Length < 2)
        {
            return [];
        }

        string[] header = Split(lines[0]);
        var rows = ImmutableArray.CreateBuilder<PerfRow>(lines.Length - 1);

        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }

            string[] cells = Split(lines[i]);

            rows.Add(new PerfRow(
                Text(header, cells, "cell"),
                Number(header, cells, "tick_p99_ms"),
                Number(header, cells, "scan_per_tick"),
                Number(header, cells, "bytes_per_tick"),
                (long)Number(header, cells, "tick_overruns"),
                (long)Number(header, cells, "sequence_gaps"),
                Number(header, cells, "scan_cap")));
        }

        return rows.ToImmutable();
    }

    /// <summary>기준선을 쓴다. 판정에 쓰는 열만 남긴다 — 나머지는 회차 기록이지 기준이 아니다.</summary>
    public static void Write(string path, ImmutableArray<PerfRow> rows)
    {
        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = new List<string>(rows.Length + 1)
        {
            "cell,tick_p99_ms,scan_per_tick,bytes_per_tick,tick_overruns,sequence_gaps,scan_cap",
        };

        foreach (PerfRow row in rows.OrderBy(r => r.Cell, StringComparer.Ordinal))
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.Cell},{row.TickP99Ms:0.###},{row.ScanPerTick:0.#},{row.BytesPerTick:0},"
                + $"{row.Overruns},{row.SequenceGaps},{row.ScanCapSetting:0}"));
        }

        File.WriteAllLines(path, lines);
    }

    private static string[] Split(string line) => [.. line.Split(',').Select(c => c.Trim())];

    private static string Text(string[] header, string[] cells, string name)
    {
        int at = Array.IndexOf(header, name);

        return at >= 0 && at < cells.Length ? cells[at] : string.Empty;
    }

    private static double Number(string[] header, string[] cells, string name) =>
        double.TryParse(
            Text(header, cells, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;
}
