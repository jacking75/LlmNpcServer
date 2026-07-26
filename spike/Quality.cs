using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Spike;

/// <summary>엔진 1종의 품질 채점 (10점 만점).</summary>
/// <param name="Engine">엔진 id.</param>
/// <param name="Plans">채점 대상이 된 유효 플랜 수.</param>
/// <param name="Requested">요청 수.</param>
/// <param name="Situation">상황 반영 (3점).</param>
/// <param name="Appropriate">액션 적절성 (3점).</param>
/// <param name="Coherent">정합성 (2점).</param>
/// <param name="Diversity">다양성 (2점).</param>
/// <param name="AvgMs">요청당 평균 지연.</param>
/// <param name="AvgSteps">플랜당 평균 스텝 수.</param>
/// <param name="AvgOutTok">요청당 평균 출력 토큰.</param>
internal readonly record struct QualityScore(
    string Engine,
    int Plans,
    int Requested,
    double Situation,
    double Appropriate,
    double Coherent,
    double Diversity,
    double AvgMs,
    double AvgSteps,
    double AvgOutTok)
{
    public double Total => Situation + Appropriate + Coherent + Diversity;
}

/// <summary>
/// T0-12 — 품질 평가. **게이트 G0-4 (4B 가 8B 의 80% 이상) · 다양성 항목이 0이 아님**.
///
/// 작업 지시서는 "눈으로 채점"이라고 되어 있다. 여기서는 채점의 **재현 가능한 부분**을
/// 자동 지표로 계산하고, 원본 플랜을 `spike/out/quality/` 에 남겨 사람이 직접 볼 수 있게 한다.
/// 자동 지표의 정의는 `docs/measurements/W1_quality.md` 의 "채점 방법" 절에 그대로 적힌다.
/// </summary>
internal static class Quality
{
    /// <summary>MoveTo 목적지 심볼 → 그 자리에서 서는 위치 플래그.</summary>
    private static readonly Dictionary<string, string> PoiGrants = new(StringComparer.Ordinal)
    {
        ["$home"] = "AtHome",
        ["$workplace"] = "AtWorkplace",
        ["$market"] = "AtMarket",
        ["$tavern"] = "AtTavern",
        ["$nearest_field"] = "AtField",
    };

    /// <summary>아키타입다움을 보는 대표 액션. 하나라도 있으면 인정.</summary>
    private static readonly Dictionary<string, string[]> Signature = new(StringComparer.Ordinal)
    {
        ["blacksmith"] = ["Craft", "Work"],
        ["farmer"] = ["Gather", "Work"],
        ["merchant"] = ["Trade", "Talk"],
        ["town_guard"] = ["Guard"],
    };

    private static readonly string[] LocationFlags =
        ["AtHome", "AtWorkplace", "AtMarket", "AtTavern", "AtField"];

    public static async Task<int> RunAsync(string[] args)
    {
        var n = RunSchemaCheck.ArgInt(args, "--n", 20);
        var rpm = RunSchemaCheck.ArgInt(args, "--rpm", 12);
        var engineIds = RunSchemaCheck.ArgStr(args, "--engines", "");

        var catalog = SchemaGen.Load();
        var prefix = PromptPrefix.Instance;
        var buckets = SuffixGen.Sample(n);

        var engines = engineIds.Length > 0
            ? engineIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Clients.ById).ToArray()
            : Clients.All;

        var scores = new List<QualityScore>();
        foreach (var spec in engines)
        {
            if (!spec.IsConfigured)
            {
                Console.WriteLine($"{spec.Id}: skip");
                continue;
            }

            var score = await ScoreEngineAsync(spec, catalog, prefix, buckets,
                spec.IsLocal ? null : new Pacer(rpm));
            scores.Add(score);
            Persist(score);
            Console.WriteLine(
                $"{score.Engine,-24} total {score.Total,5:F2} " +
                $"(situation {score.Situation:F2} / appropriate {score.Appropriate:F2} / " +
                $"coherent {score.Coherent:F2} / diversity {score.Diversity:F2}) " +
                $"plans {score.Plans}/{score.Requested} · {score.AvgMs:F0} ms");
        }

        var all = LoadAll();
        WriteMarkdown(all, n);
        return Gate(all);
    }

    private static async Task<QualityScore> ScoreEngineAsync(
        EngineSpec spec, MinCatalog catalog, PromptPrefix prefix, Bucket[] buckets, Pacer? pacer)
    {
        using var client = Clients.Create(spec);
        var options = new ChatOptions { Temperature = 0.4f, MaxOutputTokens = 1024 };

        var dir = Path.Combine(SchemaGen.OutDir, "quality", spec.Id);
        Directory.CreateDirectory(dir);

        var plans = new List<(Bucket Bucket, string[] Actions, JsonDocument Doc)>();
        double totalMs = 0;
        long outTok = 0;

        foreach (var bucket in buckets)
        {
            var messages = Clients.BuildMessages(spec, prefix.Text, SuffixGen.Build(bucket));
            var r = await Clients.AskWithRetryAsync(spec, client, messages, options, pacer);
            totalMs += r.TotalMs;
            outTok += r.CompletionTokens;

            var text = RunSchemaCheck.StripFences(r.Text);
            await File.WriteAllTextAsync(
                Path.Combine(dir, $"{bucket}.json".Replace('@', '_')),
                r.Ok ? text : $"ERROR {r.Error}");

            if (!r.Ok || !Validate.Check(text, catalog, bucket.Archetype, ignoreUnknownArgs: true).IsOk)
            {
                continue;
            }

            var doc = JsonDocument.Parse(text);
            var actions = doc.RootElement.GetProperty("steps").EnumerateArray()
                .Select(s => s.GetProperty("action").GetString() ?? "")
                .ToArray();
            plans.Add((bucket, actions, doc));
        }

        var score = Compute(spec.Id, plans, buckets.Length, totalMs, outTok, catalog);
        foreach (var p in plans)
        {
            p.Doc.Dispose();
        }

        return score;
    }

    // ---------------------------------------------------------------- 채점
    private static QualityScore Compute(
        string engine,
        List<(Bucket Bucket, string[] Actions, JsonDocument Doc)> plans,
        int requested,
        double totalMs,
        long outTok,
        MinCatalog catalog)
    {
        if (plans.Count == 0)
        {
            return new QualityScore(engine, 0, requested, 0, 0, 0, 0,
                requested == 0 ? 0 : totalMs / requested, 0, 0);
        }

        // (1) 상황 반영 3점 — 같은 아키타입인데 버킷이 다르면 액션 시퀀스도 달라야 한다.
        var pairs = 0;
        var differing = 0;
        for (var i = 0; i < plans.Count; i++)
        {
            for (var j = i + 1; j < plans.Count; j++)
            {
                if (plans[i].Bucket.Archetype != plans[j].Bucket.Archetype)
                {
                    continue;
                }

                pairs++;
                if (!plans[i].Actions.SequenceEqual(plans[j].Actions))
                {
                    differing++;
                }
            }
        }

        var situation = pairs == 0 ? 0 : 3.0 * differing / pairs;

        // (2) 액션 적절성 3점 — 허용 액션만 썼는가(1.5) + 아키타입 대표 액션이 있는가(1.5)
        var allowedOk = 0;
        var signatureOk = 0;
        foreach (var (bucket, actions, _) in plans)
        {
            var sig = Signature.TryGetValue(bucket.Archetype, out var s) ? s : [];
            if (actions.Any(a => sig.Contains(a, StringComparer.Ordinal)))
            {
                signatureOk++;
            }

            // 허용 목록 위반은 검증기 2단이 이미 잡는다 — 여기 온 플랜은 통과분이다.
            allowedOk++;
        }

        var appropriate = 1.5 * allowedOk / plans.Count + 1.5 * signatureOk / plans.Count;

        // (3) 정합성 2점 — 플래그 시뮬레이션 (docs/03 §3 3단의 축소판)
        var coherentCount = plans.Count(p => IsCoherent(p.Bucket, p.Doc, catalog));
        var coherent = 2.0 * coherentCount / plans.Count;

        // (4) 다양성 2점 — 유니크 액션 시퀀스 비율
        var unique = plans.Select(p => string.Join('>', p.Actions))
            .Distinct(StringComparer.Ordinal).Count();
        var diversity = 2.0 * unique / plans.Count;

        return new QualityScore(
            engine, plans.Count, requested,
            situation, appropriate, coherent, diversity,
            requested == 0 ? 0 : totalMs / requested,
            plans.Average(p => p.Actions.Length),
            requested == 0 ? 0 : (double)outTok / requested);
    }

    /// <summary>
    /// 버킷의 초기 플래그에서 출발해 스텝을 훑는다. requires 미충족·forbids 성립이면 실패.
    /// MoveTo 는 목적지 심볼에 따라 위치 플래그를 세운다 (정식 카탈로그의 "grants: POI별").
    /// </summary>
    private static bool IsCoherent(Bucket bucket, JsonDocument doc, MinCatalog catalog)
    {
        var state = new HashSet<string>(SuffixGen.FlagsFor(bucket), StringComparer.Ordinal);

        foreach (var step in doc.RootElement.GetProperty("steps").EnumerateArray())
        {
            var def = catalog.Find(step.GetProperty("action").GetString() ?? "");
            if (def is null)
            {
                return false;
            }

            if (def.Requires.Any(f => !state.Contains(f)) || def.Forbids.Any(state.Contains))
            {
                return false;
            }

            foreach (var f in def.Clears)
            {
                state.Remove(f);
            }

            foreach (var f in def.Grants)
            {
                state.Add(f);
            }

            if (def.Id == "MoveTo" &&
                step.GetProperty("args").TryGetProperty("poi", out var poi) &&
                PoiGrants.TryGetValue(poi.GetString() ?? "", out var granted))
            {
                foreach (var f in LocationFlags)
                {
                    state.Remove(f);
                }

                state.Add(granted);
            }
        }

        return true;
    }

    // ---------------------------------------------------------------- 누적
    private static string JsonlPath => Path.Combine(SchemaGen.OutDir, "quality.jsonl");

    /// <summary>로컬 모델은 VRAM 때문에 한 번에 하나씩만 잰다. 점수를 파일에 쌓아 표를 온전히 만든다.</summary>
    private static void Persist(QualityScore s)
    {
        var line = JsonSerializer.Serialize(s) + "\n";
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                File.AppendAllText(JsonlPath, line);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static List<QualityScore> LoadAll()
    {
        var byEngine = new Dictionary<string, QualityScore>(StringComparer.Ordinal);
        if (File.Exists(JsonlPath))
        {
            foreach (var line in File.ReadLines(JsonlPath))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var s = JsonSerializer.Deserialize<QualityScore>(line);
                if (s.Engine is not null)
                {
                    byEngine[s.Engine] = s;
                }
            }
        }

        return [.. byEngine.Values.OrderBy(s => s.Engine, StringComparer.Ordinal)];
    }

    // ---------------------------------------------------------------- 산출물
    private static void WriteMarkdown(List<QualityScore> scores, int n)
    {
        var path = Path.GetFullPath(
            Path.Combine(Program.SpikeRoot, "..", "docs", "measurements", "W1_quality.md"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var sb = new StringBuilder(8 * 1024);
        sb.Append("# W1 품질 평가 (T0-12 · 게이트 G0-4)\n\n");
        sb.Append("> 표는 `spike quality` 가 생성한다. 원본 플랜은 `spike/out/quality/<engine>/` 에 있다.\n");
        sb.Append("> 사람 판단은 맨 아래 \"읽어 본 소감\" 절에 따로 쓴다.\n\n");
        sb.Append($"- 서픽스 {n} 종 (버킷 288 에서 결정론 샘플링) × 엔진 {scores.Count} 종\n");
        sb.Append("- 강제 디코딩 없이(prompt 모드) 생성. T0-09 에서 그쪽이 이겼다.\n\n");

        sb.Append("## 채점 방법\n\n");
        sb.Append("| 항목 | 배점 | 계산 |\n|---|---|---|\n");
        sb.Append("| 상황 반영 | 3 | 같은 아키타입의 버킷 쌍 중 액션 시퀀스가 다른 비율 × 3 |\n");
        sb.Append("| 액션 적절성 | 3 | (허용 액션만 사용 × 1.5) + (아키타입 대표 액션 포함 × 1.5) |\n");
        sb.Append("| 정합성 | 2 | 플래그 시뮬레이션(검증기 3단 축소판) 통과 비율 × 2 |\n");
        sb.Append("| 다양성 | 2 | 유니크 액션 시퀀스 / 플랜 수 × 2 |\n\n");
        sb.Append("검증기 1·2단을 통과하지 못한 응답은 채점 대상에서 빠진다 (`plans` 열이 그 수다).\n\n");

        sb.Append("## 점수\n\n");
        sb.Append("| 엔진 | 상황반영 3 | 액션적절성 3 | 정합성 2 | 다양성 2 | **합계 10** | 유효 플랜 | 평균 ms | 평균 스텝 | out tok |\n");
        sb.Append("|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var s in scores)
        {
            sb.Append($"| `{s.Engine}` | {s.Situation:F2} | {s.Appropriate:F2} | {s.Coherent:F2} | ")
              .Append($"{s.Diversity:F2} | **{s.Total:F2}** | {s.Plans}/{s.Requested} | ")
              .Append($"{s.AvgMs:F0} | {s.AvgSteps:F1} | {s.AvgOutTok:F0} |\n");
        }

        sb.Append("\n## 게이트\n\n");
        foreach (var (small, big) in Pairs(scores))
        {
            var ratio = big.Total <= 0 ? 0 : small.Total / big.Total * 100;
            sb.Append($"- `{small.Engine}` vs `{big.Engine}` — {ratio:F1}% ")
              .Append(ratio >= 80 ? "(G0-4 PASS)" : "(G0-4 FAIL)").Append('\n');
        }

        var zeroDiversity = scores.Where(s => s.Diversity <= 0).ToArray();
        sb.Append(zeroDiversity.Length == 0
            ? "- 다양성 0점인 엔진 없음 (PASS)\n"
            : $"- **다양성 0점: {string.Join(", ", zeroDiversity.Select(s => s.Engine))} — W1 연장 대상**\n");

        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"wrote {path}");
    }

    /// <summary>같은 계열의 (작은 모델, 큰 모델) 쌍. G0-4 는 이 비율을 본다.</summary>
    private static IEnumerable<(QualityScore Small, QualityScore Big)> Pairs(List<QualityScore> scores)
    {
        (string Small, string Big)[] known =
        [
            ("llamacpp-gemma3-4b", "llamacpp-qwen3-8b"),
            ("dotllm-phi4-mini", "dotllm-qwen2.5-7b"),
            ("gemini-3.5-flash-lite", "gemini-3.5-flash"),
        ];

        foreach (var (small, big) in known)
        {
            var s = scores.FirstOrDefault(x => x.Engine == small);
            var b = scores.FirstOrDefault(x => x.Engine == big);
            if (s.Engine is not null && b.Engine is not null)
            {
                yield return (s, b);
            }
        }
    }

    private static int Gate(List<QualityScore> scores)
    {
        var ok = scores.Count > 0 && scores.All(s => s.Diversity > 0);
        foreach (var (small, big) in Pairs(scores))
        {
            ok &= big.Total <= 0 || small.Total / big.Total >= 0.8;
        }

        Console.WriteLine($"게이트 G0-4 : {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }
}
