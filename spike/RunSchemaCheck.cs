using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Spike;

/// <summary>강제 디코딩을 쓰는지 여부.</summary>
internal enum DecodeMode
{
    /// <summary>`response_format = json_schema`. 제공사 FSM 이 형식을 강제한다.</summary>
    Forced,

    /// <summary>형식 지정 없이 프롬프트만으로 JSON 을 받는다.</summary>
    Prompt,
}

/// <summary>엔진 1종 × 서픽스 N종의 집계.</summary>
internal sealed class SchemaCheckResult
{
    public required string Engine { get; init; }

    public required string SchemaVariant { get; init; }

    public required DecodeMode Mode { get; init; }

    public int Requested { get; set; }

    /// <summary>1단(스키마)까지 통과. **게이트 G0-1 의 "유효 JSON" 이 이것이다.**</summary>
    public int ValidJson { get; set; }

    /// <summary>1·2단 전부 통과.</summary>
    public int Valid { get; set; }

    /// <summary>액션에 없는 인자를 눈감아 주면 통과하는 수 (합집합 스키마의 부작용 보정).</summary>
    public int ValidTolerant { get; set; }

    public int CallFail { get; set; }

    public int ParseFail { get; set; }

    public int SchemaFail { get; set; }

    public int VocabFail { get; set; }

    /// <summary>429 로 다시 던진 총 횟수.</summary>
    public int Retries { get; set; }

    public Dictionary<string, int> Codes { get; } = new(StringComparer.Ordinal);

    /// <summary>스키마 검증에서 걸린 JSON Schema 키워드별 건수 — G0-1 미달 시 이게 답이다.</summary>
    public Dictionary<string, int> SchemaKeywords { get; } = new(StringComparer.Ordinal);

    public List<string> Examples { get; } = [];

    public double TotalMs { get; set; }

    public long PromptTokens { get; set; }

    public long CachedTokens { get; set; }

    public long CompletionTokens { get; set; }

    public double AvgMs => Requested == 0 ? 0 : TotalMs / Requested;

    public double AvgOutTokens => Requested == 0 ? 0 : (double)CompletionTokens / Requested;
}

/// <summary>
/// 실행마다 `out/schemacheck.jsonl` 에 한 줄씩 쌓이는 요약. 마크다운은 항상 이 파일 전체에서
/// 다시 만든다 — 엔진을 따로따로 돌려도 표가 온전하게 남는다.
/// </summary>
internal sealed record SchemaCheckRow(
    string Engine,
    string Variant,
    string Mode,
    int Requested,
    int ValidJson,
    int Valid,
    int ValidTolerant,
    int CallFail,
    int ParseFail,
    int SchemaFail,
    int VocabFail,
    int Retries,
    double AvgMs,
    double AvgOutTokens,
    long CachedTokens,
    Dictionary<string, int> Codes,
    Dictionary<string, int> SchemaKeywords,
    List<string> Examples)
{
    public string Key => $"{Engine}|{Variant}|{Mode}";
}

/// <summary>
/// T0-09 — 강제 디코딩 100회 러너. **게이트 G0-1**.
/// 엔진별로 서픽스 100종을 던지고 유효/파싱실패/스키마실패를 집계한다.
/// </summary>
internal static class RunSchemaCheck
{
    public static async Task<int> RunAsync(string[] args)
    {
        var n = ArgInt(args, "--n", 100);
        var engineIds = ArgStr(args, "--engines", "");
        var concurrency = ArgInt(args, "--concurrency", 4);
        var rpm = ArgInt(args, "--rpm", 10);
        var append = args.Contains("--append");

        // "full,forced" 형태의 조합 목록. 기본은 사양 원안·합집합·프롬프트전용 3종 비교.
        var modeSpecs = ArgStr(args, "--modes", "full:forced,bare:forced,full:prompt")
            .Split(',', StringSplitOptions.RemoveEmptyEntries);

        var catalog = SchemaGen.Load();
        var prefix = PromptPrefix.Instance;
        var buckets = SuffixGen.Sample(n);

        var engines = engineIds.Length > 0
            ? engineIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Clients.ById).ToArray()
            : Clients.All;

        Console.WriteLine($"prefix {prefix.TokenCount} tok / sha {prefix.Sha256[..12]} · n={n}");
        Console.WriteLine();

        var results = new List<SchemaCheckResult>();
        foreach (var spec in engines)
        {
            if (!spec.IsConfigured)
            {
                Console.WriteLine($"{spec.Id}: skip ({spec.ApiKeyEnv} 없음)");
                continue;
            }

            foreach (var ms in modeSpecs)
            {
                var parts = ms.Split(':');
                var variant = SchemaOptions.ByName(parts[0]);
                var mode = parts.Length > 1 && parts[1] == "prompt" ? DecodeMode.Prompt : DecodeMode.Forced;

                var result = await RunEngineAsync(spec, catalog, variant, mode, prefix, buckets,
                    spec.IsLocal ? 1 : concurrency,
                    spec.IsLocal ? null : new Pacer(rpm));
                results.Add(result);
                Report(result);
                Persist(result);
            }
        }

        WriteMarkdown(prefix, n);

        // 게이트는 "유효 JSON" 기준이다. 어휘 오류는 검증기 2단의 몫이라 여기서 세지 않는다.
        var best = results.Where(r => r.Requested > 0).ToArray();
        var gate = best.Length > 0 && best.All(r => r.ValidJson == r.Requested);
        Console.WriteLine();
        Console.WriteLine($"게이트 G0-1 (전 엔진·전 모드 유효 JSON {n}/{n}) : {(gate ? "PASS" : "FAIL")}");
        return gate ? 0 : 1;
    }

    private static async Task<SchemaCheckResult> RunEngineAsync(
        EngineSpec spec,
        MinCatalog catalog,
        SchemaOptions variant,
        DecodeMode mode,
        PromptPrefix prefix,
        Bucket[] buckets,
        int concurrency,
        Pacer? pacer)
    {
        var result = new SchemaCheckResult { Engine = spec.Id, SchemaVariant = variant.Name, Mode = mode };
        using var client = Clients.Create(spec);

        using var schemaDoc = JsonDocument.Parse(SchemaGen.BuildFromCatalog(variant));
        var options = new ChatOptions
        {
            Temperature = 0.4f,
            MaxOutputTokens = 2048,
            ResponseFormat = mode == DecodeMode.Forced
                ? ChatResponseFormat.ForJsonSchema(schemaDoc.RootElement.Clone(), "NpcPlan")
                : null,
        };

        var tag = $"{spec.Id}.{variant.Name}.{mode.ToString().ToLowerInvariant()}";
        var rawDir = Path.Combine(SchemaGen.OutDir, "raw", tag);
        Directory.CreateDirectory(rawDir);

        var gate = new SemaphoreSlim(concurrency);
        var lockObj = new object();

        var tasks = buckets.Select(async (bucket, i) =>
        {
            await gate.WaitAsync();
            try
            {
                var messages = Clients.BuildMessages(spec, prefix.Text, SuffixGen.Build(bucket));
                var reply = await Clients.AskWithRetryAsync(spec, client, messages, options, pacer);
                var text = StripFences(reply.Text);

                var strict = reply.Ok ? Validate.Check(text, catalog, bucket.Archetype) : ValidationResult.Ok;
                var tolerant = reply.Ok
                    ? Validate.Check(text, catalog, bucket.Archetype, ignoreUnknownArgs: true)
                    : ValidationResult.Ok;

                await File.WriteAllTextAsync(
                    Path.Combine(rawDir, $"{i:D3}_{bucket}.json".Replace('@', '_')),
                    reply.Ok ? reply.Text : $"ERROR {reply.Error}");

                lock (lockObj)
                {
                    Accumulate(result, reply, strict, tolerant, bucket);
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return result;
    }

    /// <summary>```json 울타리를 벗긴다. 강제 디코딩을 끄면 모델이 자주 붙인다.</summary>
    public static string StripFences(string s)
    {
        var t = s.Trim();
        if (!t.StartsWith("```", StringComparison.Ordinal))
        {
            return t;
        }

        var firstNewline = t.IndexOf('\n');
        if (firstNewline < 0)
        {
            return t;
        }

        t = t[(firstNewline + 1)..];
        var fence = t.LastIndexOf("```", StringComparison.Ordinal);
        return (fence >= 0 ? t[..fence] : t).Trim();
    }

    private static void Accumulate(
        SchemaCheckResult r, in Reply reply, ValidationResult v, ValidationResult tolerant, in Bucket bucket)
    {
        r.Requested++;
        r.Retries += reply.Retries;
        r.TotalMs += reply.TotalMs;
        r.PromptTokens += reply.PromptTokens;
        r.CachedTokens += reply.CachedTokens;
        r.CompletionTokens += reply.CompletionTokens;

        if (!reply.Ok)
        {
            r.CallFail++;
            Bump(r.Codes, "CALL_FAILED");
            AddExample(r, $"{bucket}: CALL_FAILED {reply.Error}");
            return;
        }

        if (v.FailedAt != ValidationStage.Schema)
        {
            r.ValidJson++;
        }

        if (tolerant.IsOk)
        {
            r.ValidTolerant++;
        }

        if (v.IsOk)
        {
            r.Valid++;
            return;
        }

        Bump(r.Codes, v.Code);
        switch (v.Code)
        {
            case "V1.PARSE":
                r.ParseFail++;
                break;
            case "V1.SCHEMA":
            case "V1.STEP_COUNT":
            case "V1.EXTRA_FIELD":
                r.SchemaFail++;
                foreach (var keyword in ExtractKeywords(v))
                {
                    Bump(r.SchemaKeywords, keyword);
                }

                break;
            default:
                r.VocabFail++;
                break;
        }

        AddExample(r, $"{bucket}: {v}");
    }

    /// <summary>실패한 스키마 요소를 뽑는다. "미달 시 어떤 스키마 요소가 문제였는지" 기록용.</summary>
    private static IEnumerable<string> ExtractKeywords(ValidationResult v)
    {
        if (v.Code == "V1.STEP_COUNT")
        {
            yield return "minItems/maxItems";
            yield break;
        }

        if (v.Code == "V1.EXTRA_FIELD")
        {
            yield return $"additionalProperties ({v.Detail})";
            yield break;
        }

        string[] known = ["enum", "pattern", "maxLength", "additionalProperties", "minItems", "maxItems",
            "const", "required", "type", "minimum", "maximum"];
        var hit = false;
        foreach (var k in known)
        {
            if (v.Detail.Contains(k, StringComparison.OrdinalIgnoreCase))
            {
                hit = true;
                yield return k;
            }
        }

        if (!hit)
        {
            yield return "(unclassified)";
        }
    }

    private static void Bump(Dictionary<string, int> d, string key) =>
        d[key] = d.TryGetValue(key, out var c) ? c + 1 : 1;

    private static void AddExample(SchemaCheckResult r, string line)
    {
        if (r.Examples.Count < 8)
        {
            r.Examples.Add(line);
        }
    }

    private static void Report(SchemaCheckResult r)
    {
        Console.WriteLine($"{r.Engine} [{r.SchemaVariant}/{r.Mode}]");
        Console.WriteLine($"  유효 JSON {r.ValidJson}/{r.Requested} · 검증통과 {r.Valid} · 관용통과 {r.ValidTolerant}");
        Console.WriteLine($"  call {r.CallFail} · parse {r.ParseFail} · schema {r.SchemaFail} · vocab {r.VocabFail}");
        Console.WriteLine($"  avg {r.AvgMs:F0} ms · out {r.AvgOutTokens:F0} tok/req · cached {r.CachedTokens} · 429 재시도 {r.Retries}");
        if (r.Codes.Count > 0)
        {
            Console.WriteLine("  codes: " + string.Join(", ",
                r.Codes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value}")));
        }

        foreach (var e in r.Examples.Take(3))
        {
            Console.WriteLine($"    {e}");
        }

        Console.WriteLine();
    }

    // ---------------------------------------------------------------- 산출물
    private static string JsonlPath => Path.Combine(SchemaGen.OutDir, "schemacheck.jsonl");

    private static void Persist(SchemaCheckResult r)
    {
        var row = new SchemaCheckRow(
            r.Engine, r.SchemaVariant, r.Mode.ToString().ToLowerInvariant(),
            r.Requested, r.ValidJson, r.Valid, r.ValidTolerant,
            r.CallFail, r.ParseFail, r.SchemaFail, r.VocabFail, r.Retries,
            Math.Round(r.AvgMs, 1), Math.Round(r.AvgOutTokens, 1), r.CachedTokens,
            r.Codes, r.SchemaKeywords, r.Examples);

        var line = JsonSerializer.Serialize(row) + "\n";

        // 로컬 실행과 외부 실행을 동시에 돌리면 같은 파일에 붙는다. 공유 위반은 잠깐 기다렸다 다시.
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

        Console.Error.WriteLine("schemacheck.jsonl 에 쓰지 못했다");
    }

    /// <summary>jsonl 전체에서 다시 만든다. 같은 (엔진·스키마·모드) 는 마지막 실행이 이긴다.</summary>
    private static void WriteMarkdown(PromptPrefix prefix, int n)
    {
        var path = Path.GetFullPath(
            Path.Combine(Program.SpikeRoot, "..", "docs", "measurements", "W1_schema.md"));

        var rows = new Dictionary<string, SchemaCheckRow>(StringComparer.Ordinal);
        if (File.Exists(JsonlPath))
        {
            foreach (var line in File.ReadLines(JsonlPath))
            {
                if (line.Length == 0)
                {
                    continue;
                }

                var row = JsonSerializer.Deserialize<SchemaCheckRow>(line);
                if (row is not null)
                {
                    rows[row.Key] = row;
                }
            }
        }

        var ordered = rows.Values
            .OrderBy(r => r.Engine, StringComparer.Ordinal)
            .ThenBy(r => r.Mode, StringComparer.Ordinal)
            .ThenBy(r => r.Variant, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder(16 * 1024);
        sb.Append("# W1 강제 디코딩 검증 (T0-09 · 게이트 G0-1)\n\n");
        sb.Append("> `spike schemacheck` 이 `spike/out/schemacheck.jsonl` 전체에서 다시 만든다.\n");
        sb.Append("> 표는 손으로 고치지 않는다. 해석은 맨 아래 \"판단\" 절에 사람이 쓴다.\n\n");
        sb.Append($"- 프리픽스 {prefix.TokenCount} 토큰 · SHA `{prefix.Sha256[..16]}`\n");
        sb.Append($"- 서픽스 최대 {n} 종 (버킷 288 에서 결정론 샘플링, 전부 서로 다름)\n");
        sb.Append("- 검증: 검증기 1·2단 (T0-08). 3·4단은 W6 범위.\n");
        sb.Append("- **유효 JSON** = 1단(파싱+스키마) 통과. 게이트 G0-1 의 기준.\n");
        sb.Append("- **검증통과** = 1·2단 전부 통과. **관용통과** = 액션에 없는 인자를 눈감아 준 것.\n");
        sb.Append("- `forced` = `response_format=json_schema`, `prompt` = 형식 지정 없이 프롬프트만.\n\n");

        sb.Append("| 엔진 | 스키마 | 디코딩 | 유효 JSON | 검증통과 | 관용통과 | 평균 ms | out tok/req |\n");
        sb.Append("|---|---|---|---|---|---|---|---|\n");
        foreach (var r in ordered)
        {
            sb.Append($"| `{r.Engine}` | {r.Variant} | {r.Mode} | **{r.ValidJson}/{r.Requested}** | ")
              .Append($"{r.Valid} | {r.ValidTolerant} | {r.AvgMs:F0} | {r.AvgOutTokens:F0} |\n");
        }

        foreach (var r in ordered.Where(r => r.Valid != r.Requested))
        {
            sb.Append($"\n### `{r.Engine}` [{r.Variant}/{r.Mode}] 실패 분해\n\n");
            sb.Append("| 코드 | 건수 |\n|---|---|\n");
            foreach (var (code, count) in r.Codes.OrderByDescending(kv => kv.Value))
            {
                sb.Append($"| `{code}` | {count} |\n");
            }

            if (r.SchemaKeywords.Count > 0)
            {
                sb.Append("\n문제된 스키마 요소:\n\n| 요소 | 건수 |\n|---|---|\n");
                foreach (var (k, count) in r.SchemaKeywords.OrderByDescending(kv => kv.Value))
                {
                    sb.Append($"| `{k}` | {count} |\n");
                }
            }

            sb.Append("\n예시:\n\n```\n");
            foreach (var e in r.Examples)
            {
                sb.Append(e).Append('\n');
            }

            sb.Append("```\n");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString());
        Console.WriteLine($"wrote {path} ({ordered.Count} rows)");
    }

    // ---------------------------------------------------------------- 인자
    public static string ArgStr(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    public static int ArgInt(string[] args, string name, int fallback) =>
        int.TryParse(ArgStr(args, name, ""), out var v) ? v : fallback;
}
