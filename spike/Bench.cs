using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;

namespace Spike;

/// <summary>측정 1건. docs/10 §2 T0-5 의 `Measurement` 레코드.</summary>
/// <param name="Engine">엔진 id.</param>
/// <param name="Model">모델 id.</param>
/// <param name="Cache">on = 같은 프리픽스 연속, off = 프리픽스 끝에 난수 주석으로 강제 미적중.</param>
/// <param name="PrefixTok">프리픽스 토큰 수 (o200k 기준).</param>
/// <param name="SuffixTok">서픽스 토큰 수 (o200k 기준).</param>
/// <param name="OutTok">출력 토큰 수 (usage).</param>
/// <param name="PrefillMs">첫 토큰까지. prefill 대용치.</param>
/// <param name="DecodeMs">첫 토큰 이후.</param>
/// <param name="TotalMs">요청 전체.</param>
/// <param name="CachedTok">제공사가 보고한 캐시 적중 토큰 수.</param>
/// <param name="CostUsd">단가표 × 실측 토큰.</param>
internal readonly record struct Measurement(
    string Engine,
    string Model,
    string Cache,
    int PrefixTok,
    int SuffixTok,
    long OutTok,
    double PrefillMs,
    double DecodeMs,
    double TotalMs,
    long CachedTok,
    double CostUsd)
{
    public static string CsvHeader =>
        "Engine,Model,Cache,PrefixTok,SuffixTok,OutTok,PrefillMs,DecodeMs,TotalMs,CachedTok,CostUsd";

    public string ToCsv() => string.Join(',',
        Engine, Model, Cache,
        PrefixTok.ToString(CultureInfo.InvariantCulture),
        SuffixTok.ToString(CultureInfo.InvariantCulture),
        OutTok.ToString(CultureInfo.InvariantCulture),
        PrefillMs.ToString("F1", CultureInfo.InvariantCulture),
        DecodeMs.ToString("F1", CultureInfo.InvariantCulture),
        TotalMs.ToString("F1", CultureInfo.InvariantCulture),
        CachedTok.ToString(CultureInfo.InvariantCulture),
        CostUsd.ToString("F6", CultureInfo.InvariantCulture));
}

/// <summary>
/// T0-10 — 성능 측정 하네스. **게이트 G0-2 (지연) · G0-3 (캐시 prefill 감소)**.
/// </summary>
internal static class Bench
{
    public static async Task<int> RunAsync(string[] args)
    {
        var reps = RunSchemaCheck.ArgInt(args, "--n", 30);
        var rpm = RunSchemaCheck.ArgInt(args, "--rpm", 12);
        var engineIds = RunSchemaCheck.ArgStr(args, "--engines", "");
        var cacheModes = RunSchemaCheck.ArgStr(args, "--cache", "on,off")
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        // CSV 는 누적이 기본이다 — 로컬 모델은 VRAM 때문에 한 번에 하나씩만 잴 수 있다.
        var fresh = args.Contains("--fresh");

        var prefix = PromptPrefix.Instance;
        var engines = engineIds.Length > 0
            ? engineIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Clients.ById).ToArray()
            : Clients.All;

        var rows = new List<Measurement>();
        Console.WriteLine($"prefix {prefix.TokenCount} tok · reps {reps} · cache [{string.Join('|', cacheModes)}]");
        Console.WriteLine();

        foreach (var spec in engines)
        {
            if (!spec.IsConfigured)
            {
                Console.WriteLine($"{spec.Id}: skip ({spec.ApiKeyEnv} 없음)");
                continue;
            }

            foreach (var cache in cacheModes)
            {
                var measured = await MeasureAsync(spec, prefix, cache, reps,
                    spec.IsLocal ? null : new Pacer(rpm));
                rows.AddRange(measured);
                Summarize(spec, cache, measured);
            }
        }

        WriteCsv(rows, !fresh);
        return Gates(rows);
    }

    private static async Task<List<Measurement>> MeasureAsync(
        EngineSpec spec, PromptPrefix prefix, string cache, int reps, Pacer? pacer)
    {
        var rows = new List<Measurement>(reps);
        using var client = Clients.Create(spec);

        // 캐시 판정에는 강제 디코딩을 쓰지 않는다 — T0-09 에서 prompt 모드가 이겼고,
        // 제공사 FSM 이 끼면 prefill/decode 분리가 흐려진다.
        var options = new ChatOptions { Temperature = 0.4f, MaxOutputTokens = 1024 };

        var buckets = SuffixGen.Sample(reps);
        for (var i = 0; i < reps; i++)
        {
            var suffix = SuffixGen.Build(buckets[i]);

            // 캐시 미적중을 만드는 법.
            //   off      — 프리픽스 **맨 앞**에 난수 주석. 공통 접두가 0이 되어 확실히 미적중이다.
            //   off_tail — 작업 지시서의 원문대로 **맨 뒤**에 붙인 것. 제공사·로컬 모두 접두 일치
            //              방식이라 앞의 4,300 토큰은 그대로 적중한다 — 미적중이 되지 않는다.
            //              둘을 나란히 재서 이 사실을 W1_perf.csv 에 남긴다.
            var buster = $"<!-- cache-buster {spec.Id}-{i}-{unchecked((uint)(i * 2654435761u))} -->";
            var prefixText = cache switch
            {
                "off" => buster + "\n" + prefix.Text,
                "off_tail" => prefix.Text + "\n" + buster + "\n",
                _ => prefix.Text,
            };

            var messages = Clients.BuildMessages(spec, prefixText, suffix);
            var r = await Clients.AskWithRetryAsync(spec, client, messages, options, pacer, streaming: true);
            if (!r.Ok)
            {
                Console.WriteLine($"  [{i}] ERROR {r.Error}");
                continue;
            }

            var prefixTok = PromptPrefix.CountTokens(prefixText);
            var suffixTok = PromptPrefix.CountTokens(suffix);
            var outTok = r.CompletionTokens > 0 ? r.CompletionTokens : PromptPrefix.CountTokens(r.Text);
            var inTok = r.PromptTokens > 0 ? r.PromptTokens : prefixTok + suffixTok;

            rows.Add(new Measurement(
                Engine: spec.Id,
                Model: spec.Model,
                Cache: cache,
                PrefixTok: prefixTok,
                SuffixTok: suffixTok,
                OutTok: outTok,
                PrefillMs: r.FirstTokenMs,
                DecodeMs: r.DecodeMs,
                TotalMs: r.TotalMs,
                CachedTok: r.CachedTokens,
                CostUsd: spec.CostUsd(inTok, r.CachedTokens, outTok)));
        }

        return rows;
    }

    private static void Summarize(EngineSpec spec, string cache, List<Measurement> rows)
    {
        if (rows.Count == 0)
        {
            Console.WriteLine($"{spec.Id} cache={cache}: 측정 0건");
            return;
        }

        Console.WriteLine($"{spec.Id} cache={cache}  n={rows.Count}");
        Console.WriteLine($"  total  avg {Avg(rows, r => r.TotalMs):F0} ms · p50 {Pct(rows, r => r.TotalMs, 50):F0} · p95 {Pct(rows, r => r.TotalMs, 95):F0}");
        Console.WriteLine($"  prefill avg {Avg(rows, r => r.PrefillMs):F0} ms · decode avg {Avg(rows, r => r.DecodeMs):F0} ms");
        Console.WriteLine($"  out {Avg(rows, r => r.OutTok):F0} tok · cached {Avg(rows, r => r.CachedTok):F0} tok · ${rows.Sum(r => r.CostUsd):F4}");
        Console.WriteLine();
    }

    private static double Avg(List<Measurement> rows, Func<Measurement, double> sel) =>
        rows.Count == 0 ? 0 : rows.Average(sel);

    private static double Pct(List<Measurement> rows, Func<Measurement, double> sel, int p)
    {
        var sorted = rows.Select(sel).OrderBy(x => x).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        var idx = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }

    // ---------------------------------------------------------------- 산출물
    private static void WriteCsv(List<Measurement> rows, bool append)
    {
        var path = Path.GetFullPath(
            Path.Combine(Program.SpikeRoot, "..", "docs", "measurements", "W1_perf.csv"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var sb = new StringBuilder(64 * 1024);
        if (!append || !File.Exists(path))
        {
            sb.Append(Measurement.CsvHeader).Append('\n');
        }

        foreach (var r in rows)
        {
            sb.Append(r.ToCsv()).Append('\n');
        }

        if (append && File.Exists(path))
        {
            File.AppendAllText(path, sb.ToString());
        }
        else
        {
            File.WriteAllText(path, sb.ToString());
        }

        Console.WriteLine($"wrote {path} (+{rows.Count} rows)");
    }

    /// <summary>게이트 판정. CSV 전체(누적분 포함)를 다시 읽어서 본다.</summary>
    private static int Gates(List<Measurement> fresh)
    {
        var path = Path.GetFullPath(
            Path.Combine(Program.SpikeRoot, "..", "docs", "measurements", "W1_perf.csv"));
        var rows = File.Exists(path) ? ReadCsv(path) : fresh;

        Console.WriteLine();
        Console.WriteLine("=== 게이트 ===");
        Console.WriteLine($"{"engine",-24} {"cache",5} {"n",4} {"total avg",10} {"prefill avg",12} {"G0-2",6}");

        var ok = true;
        foreach (var g in rows.GroupBy(r => (r.Engine, r.Cache)).OrderBy(g => g.Key.Engine).ThenBy(g => g.Key.Cache))
        {
            var list = g.ToList();
            var total = Avg(list, r => r.TotalMs);
            // G0-2: 상위 계획 가정 1.75s 의 ±50% → 875 ~ 2,625 ms
            var g02 = total is >= 875 and <= 2625;
            Console.WriteLine($"{g.Key.Engine,-24} {g.Key.Cache,5} {list.Count,4} {total,10:F0} {Avg(list, r => r.PrefillMs),12:F0} {(g02 ? "PASS" : "FAIL"),6}");
        }

        Console.WriteLine();
        Console.WriteLine($"{"engine",-24} {"prefill off",12} {"prefill on",11} {"감소율",8} {"G0-3",6}");
        foreach (var g in rows.GroupBy(r => r.Engine).OrderBy(g => g.Key))
        {
            var on = g.Where(r => r.Cache == "on").ToList();
            var off = g.Where(r => r.Cache == "off").ToList();
            if (on.Count == 0 || off.Count == 0)
            {
                Console.WriteLine($"{g.Key,-24} {"-",12} {"-",11} {"-",8} {"n/a",6}");
                continue;
            }

            var offMs = Avg(off, r => r.PrefillMs);
            var onMs = Avg(on, r => r.PrefillMs);
            var drop = offMs <= 0 ? 0 : (offMs - onMs) / offMs * 100;
            var g03 = drop >= 70;
            ok &= g03;
            Console.WriteLine($"{g.Key,-24} {offMs,12:F0} {onMs,11:F0} {drop,7:F1}% {(g03 ? "PASS" : "FAIL"),6}");
        }

        return ok ? 0 : 1;
    }

    private static List<Measurement> ReadCsv(string path)
    {
        var rows = new List<Measurement>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split(',');
            if (f.Length < 11)
            {
                continue;
            }

            rows.Add(new Measurement(
                f[0], f[1], f[2],
                int.Parse(f[3], CultureInfo.InvariantCulture),
                int.Parse(f[4], CultureInfo.InvariantCulture),
                long.Parse(f[5], CultureInfo.InvariantCulture),
                double.Parse(f[6], CultureInfo.InvariantCulture),
                double.Parse(f[7], CultureInfo.InvariantCulture),
                double.Parse(f[8], CultureInfo.InvariantCulture),
                long.Parse(f[9], CultureInfo.InvariantCulture),
                double.Parse(f[10], CultureInfo.InvariantCulture)));
        }

        return rows;
    }
}
