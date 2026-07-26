using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;

namespace Spike;

/// <summary>동시성 1수준의 결과.</summary>
/// <param name="Concurrency">동시 요청 수.</param>
/// <param name="Sent">보낸 요청 수.</param>
/// <param name="Ok">성공 수.</param>
/// <param name="RateLimited">429 수.</param>
/// <param name="OtherFail">그 외 실패 수.</param>
/// <param name="WallSeconds">벽시계 소요.</param>
/// <param name="AvgMs">성공 요청의 평균 지연.</param>
internal readonly record struct ConcurrencyPoint(
    int Concurrency,
    int Sent,
    int Ok,
    int RateLimited,
    int OtherFail,
    double WallSeconds,
    double AvgMs)
{
    /// <summary>성공 기준 처리량.</summary>
    public double ReqPerSec => WallSeconds <= 0 ? 0 : Ok / WallSeconds;
}

/// <summary>
/// T0-11 — 동시성 측정.
/// **여기서는 429 를 재시도하지 않는다.** 어느 동시성에서 429 가 처음 나는지가 측정 대상이고,
/// 그 값이 §14 문서 AIMD 의 초기값이 된다.
/// </summary>
internal static class BenchConcurrency
{
    public static async Task<int> RunAsync(string[] args)
    {
        var perLevel = RunSchemaCheck.ArgInt(args, "--n", 128);
        var engineIds = RunSchemaCheck.ArgStr(args, "--engines", "");
        var levels = RunSchemaCheck.ArgStr(args, "--levels", "1,8,16,32,64")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse)
            .ToArray();

        // 로컬은 프리픽스 4,400 토큰을 128회 던지면 몇 시간이 걸린다.
        // 배칭 유무만 보면 되므로 짧은 프롬프트로 잰다.
        var shortPrompt = args.Contains("--short");

        var engines = engineIds.Length > 0
            ? engineIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Clients.ById).ToArray()
            : Clients.All.Where(e => !e.IsLocal).ToArray();

        var prefix = PromptPrefix.Instance;
        var all = new List<(EngineSpec Spec, ConcurrencyPoint Point)>();

        foreach (var spec in engines)
        {
            if (!spec.IsConfigured)
            {
                Console.WriteLine($"{spec.Id}: skip ({spec.ApiKeyEnv} 없음)");
                continue;
            }

            Console.WriteLine($"=== {spec.Id} ({(shortPrompt ? "short prompt" : $"{prefix.TokenCount} tok prefix")}) ===");
            foreach (var c in levels)
            {
                var point = await MeasureAsync(spec, prefix, c, perLevel, shortPrompt);
                all.Add((spec, point));
                Console.WriteLine(
                    $"  concurrency={point.Concurrency,3} → {point.ReqPerSec,6:F2} req/s " +
                    $"(ok {point.Ok}/{point.Sent}, 429 {point.RateLimited}, err {point.OtherFail}, " +
                    $"wall {point.WallSeconds:F1}s, avg {point.AvgMs:F0} ms)");
            }

            Console.WriteLine();
        }

        WriteMarkdown(all, prefix, shortPrompt);
        return 0;
    }

    private static async Task<ConcurrencyPoint> MeasureAsync(
        EngineSpec spec, PromptPrefix prefix, int concurrency, int count, bool shortPrompt)
    {
        using var client = Clients.Create(spec);
        var options = new ChatOptions
        {
            Temperature = 0.4f,
            MaxOutputTokens = shortPrompt ? 32 : 1024,
        };

        var buckets = SuffixGen.Sample(count);
        var gate = new SemaphoreSlim(concurrency);
        var ok = 0;
        var rate = 0;
        var other = 0;
        var totalMs = 0.0;
        var lockObj = new object();

        var sw = Stopwatch.StartNew();
        var tasks = buckets.Select(async (bucket, i) =>
        {
            await gate.WaitAsync();
            try
            {
                var messages = shortPrompt
                    ? [new ChatMessage(ChatRole.User, $"Reply with the single word ok. request {i}{spec.SuffixTag}")]
                    : Clients.BuildMessages(spec, prefix.Text, SuffixGen.Build(bucket));

                // 재시도 없음 — 429 자체가 측정 대상이다.
                var r = await Clients.AskAsync(spec, client, messages, options);
                lock (lockObj)
                {
                    if (r.Ok)
                    {
                        ok++;
                        totalMs += r.TotalMs;
                    }
                    else if (r.IsRateLimited)
                    {
                        rate++;
                    }
                    else
                    {
                        other++;
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        sw.Stop();

        return new ConcurrencyPoint(
            concurrency, count, ok, rate, other,
            sw.Elapsed.TotalSeconds,
            ok == 0 ? 0 : totalMs / ok);
    }

    private static void WriteMarkdown(
        List<(EngineSpec Spec, ConcurrencyPoint Point)> all, PromptPrefix prefix, bool shortPrompt)
    {
        var path = Path.GetFullPath(
            Path.Combine(Program.SpikeRoot, "..", "docs", "measurements", "W1_concurrency.md"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var sb = new StringBuilder(8 * 1024);
        var exists = File.Exists(path);
        if (!exists)
        {
            sb.Append("# W1 동시성 측정 (T0-11)\n\n");
            sb.Append("> `spike concurrency` 가 생성한다. 표는 손으로 고치지 않는다.\n");
            sb.Append("> **429 를 재시도하지 않는다** — 어느 동시성에서 처음 나는지가 측정 대상이다.\n\n");
        }

        sb.Append($"\n## {(shortPrompt ? "짧은 프롬프트" : $"프리픽스 {prefix.TokenCount} 토큰")}\n\n");
        sb.Append("| 엔진 | 동시성 | req/s | 성공 | 429 | 기타실패 | 벽시계 s | 평균 ms |\n");
        sb.Append("|---|---|---|---|---|---|---|---|\n");
        foreach (var (spec, p) in all)
        {
            sb.Append($"| `{spec.Id}` | {p.Concurrency} | **{p.ReqPerSec:F2}** | {p.Ok}/{p.Sent} | ")
              .Append($"{p.RateLimited} | {p.OtherFail} | {p.WallSeconds:F1} | {p.AvgMs:F0} |\n");
        }

        foreach (var g in all.GroupBy(x => x.Spec.Id))
        {
            var first429 = g.Where(x => x.Point.RateLimited > 0)
                .OrderBy(x => x.Point.Concurrency)
                .Select(x => (int?)x.Point.Concurrency)
                .FirstOrDefault();

            var one = g.FirstOrDefault(x => x.Point.Concurrency == 1).Point;
            var eight = g.FirstOrDefault(x => x.Point.Concurrency == 8).Point;
            var ratio = one.ReqPerSec <= 0 ? 0 : eight.ReqPerSec / one.ReqPerSec;

            sb.Append($"\n- `{g.Key}` — 429 최초 발생 동시성: ")
              .Append(first429 is null ? "없음" : first429.Value.ToString())
              .Append(" · 동시 1 대비 8 처리량 비: ")
              .Append(eight.Sent == 0 ? "미측정" : $"{ratio:F2}x")
              .Append('\n');
        }

        if (exists)
        {
            File.AppendAllText(path, sb.ToString());
        }
        else
        {
            File.WriteAllText(path, sb.ToString());
        }

        Console.WriteLine($"wrote {path}");
    }
}
