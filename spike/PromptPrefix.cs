using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace Spike;

/// <summary>
/// T0-06 — 고정 프롬프트 프리픽스. docs/01 §10 의 조립 규칙을 따른다.
///
/// 기동 시 1회 조립하고 그 뒤 절대 재조립하지 않는다. 프리픽스가 1바이트라도 흔들리면
/// 프롬프트 캐시가 전면 미적중이 되고 비용이 그대로 튄다 (docs/01 §10.2).
/// </summary>
internal sealed class PromptPrefix
{
    /// <summary>조립 결과. 프로세스 생애 동안 불변.</summary>
    public required string Text { get; init; }

    /// <summary>로그·메트릭에 항상 붙인다. 유니크 해시가 2개 이상이면 즉시 경보.</summary>
    public required string Sha256 { get; init; }

    /// <summary>o200k_base 기준 토큰 수. 목표 4,200~4,500, 하한 4,096.</summary>
    public required int TokenCount { get; init; }

    /// <summary>절별 토큰 수. 예산을 어디서 잡아먹는지 보려고 남긴다.</summary>
    public required IReadOnlyList<(string Section, int Tokens)> Sections { get; init; }

    private static PromptPrefix? _instance;

    /// <summary>
    /// 토크나이저. 로컬 모델(Qwen/Phi)은 서로 다른 vocab 을 쓰므로 이 값은 <b>기준치</b>이고,
    /// 엔진별 실제 prompt_tokens 는 T0-10 에서 usage 로 따로 받는다.
    /// </summary>
    private static readonly TiktokenTokenizer Tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

    public static int CountTokens(string s) => Tokenizer.CountTokens(s);

    /// <summary>기동 시 1회. 이후 같은 인스턴스를 돌려준다.</summary>
    public static PromptPrefix Instance => _instance ??= Build();

    // ---------------------------------------------------------------- 조립
    public static PromptPrefix Build()
    {
        var catalog = SchemaGen.Load();

        var parts = new List<(string Section, string Text)>
        {
            ("system_rules", ReadText(Path.Combine(SchemaGen.DataDir, "system_rules.md"))),
            ("action_catalog", RenderCatalog(catalog)),
            ("dsl_summary", RenderDsl(catalog)),
            ("fewshot", RenderFewShots()),
        };

        var sb = new StringBuilder(24 * 1024);
        foreach (var (_, text) in parts)
        {
            sb.Append(text);
            sb.Append('\n');
        }

        var full = sb.ToString();
        return new PromptPrefix
        {
            Text = full,
            Sha256 = Hash(full),
            TokenCount = CountTokens(full),
            Sections = [.. parts.Select(p => (p.Section, CountTokens(p.Text)))],
        };
    }

    /// <summary>줄바꿈을 LF 로 통일한다. CRLF 로 체크아웃된 기계에서 SHA 가 달라지면 안 된다.</summary>
    private static string ReadText(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd() + "\n";

    private static string Hash(string s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    // ---------------------------------------------------------------- [2] 액션 카탈로그
    /// <summary>
    /// `actions.min.json` 에서 생성한다. 손으로 쓴 카탈로그를 두면 반드시 마스터데이터와 어긋난다
    /// (docs/01 §0 의 원칙). 월드 플래그 표도 같은 절에 넣는다 — requires/grants 를 읽으려면
    /// 플래그 의미를 알아야 한다.
    /// </summary>
    private static string RenderCatalog(MinCatalog c)
    {
        var sb = new StringBuilder(16 * 1024);

        sb.Append("\n# WORLD FLAGS\n\n");
        sb.Append("A flag is either set or not set. `requires` must hold before a step runs,\n");
        sb.Append("`forbids` must not hold, `grants` becomes set after the step succeeds and\n");
        sb.Append("`clears` becomes unset. The request lists only the flags that are set.\n\n");
        foreach (var f in c.Flags)
        {
            sb.Append("- `").Append(f.Id).Append("` — ").Append(f.Desc).Append('\n');
        }

        sb.Append("\n# ACTION CATALOG\n\n");
        sb.Append("These ").Append(c.Actions.Length).Append(" ids are the complete vocabulary. ");
        sb.Append("Anything else is rejected by the validator.\n\n");
        sb.Append("Item ids usable in `item_ref` arguments: ");
        sb.Append(string.Join(", ", c.ItemVocabulary.Select(i => $"`{i}`")));
        sb.Append(".\n");

        foreach (var a in c.Actions)
        {
            sb.Append("\n## ").Append(a.Id)
              .Append("  (category ").Append(a.Category)
              .Append(", cost ").Append(a.Cost)
              .Append(", default timeout ").Append(a.DefaultTimeoutS).Append("s)\n\n");
            sb.Append(a.Desc).Append('\n');

            sb.Append("\nargs:\n");
            if (a.Params.Length == 0)
            {
                sb.Append("- (none — use an empty object)\n");
            }
            else
            {
                foreach (var p in a.Params)
                {
                    sb.Append("- `").Append(p.Name).Append("` : ").Append(p.Type);
                    if (p.Values is { Length: > 0 })
                    {
                        sb.Append(" one of {").Append(string.Join(", ", p.Values)).Append('}');
                    }

                    if (p.Min is not null || p.Max is not null)
                    {
                        sb.Append(" range ").Append(p.Min ?? 0).Append("..").Append(p.Max ?? 0);
                    }

                    sb.Append(p.Required ? " — required" : " — optional");
                    if (p.Default is not null)
                    {
                        sb.Append(", default ").Append(p.Default);
                    }

                    sb.Append('\n');
                }
            }

            sb.Append("flags: requires ").Append(FlagList(a.Requires))
              .Append(" / forbids ").Append(FlagList(a.Forbids))
              .Append(" / grants ").Append(FlagList(a.Grants))
              .Append(" / clears ").Append(FlagList(a.Clears))
              .Append('\n');
        }

        return sb.ToString();
    }

    private static string FlagList(string[] flags) =>
        flags.Length == 0 ? "—" : string.Join(", ", flags);

    // ---------------------------------------------------------------- [3] DSL 요약
    /// <summary>docs/03 §2 의 스키마를 사람이 읽는 형태로 요약한다. 스키마 본문도 같이 싣는다.</summary>
    private static string RenderDsl(MinCatalog c)
    {
        var schema = SchemaGen.Build(c.Actions.Select(a => a.Id), SchemaOptions.Full)
            .Replace("\r\n", "\n");

        // $$"""…""" — 중괄호가 잔뜩 나오는 본문이라 보간 구멍을 {{…}} 로 둔다.
        return $$"""

            # PLAN FORMAT

            Output exactly one JSON object with these fields.

            - `schema` — always the integer 1.
            - `goal` — snake_case id, 3..32 chars, matching ^[a-z][a-z0-9_]{2,31}$.
            - `reasoning` — at most 200 characters, for human reviewers only.
            - `steps` — 3 to 10 objects, executed strictly in order. No branches and no
              loops inside the list; if the situation changes the server replans.
            - `on_step_fail` — one of fallback, retry_once, skip, replan.
            - `loop` — boolean. true means the plan repeats until something interrupts it,
              which is what a daily routine wants.

            Each step is `{"action": <id>, "args": {...}, "timeout_s": <int 5..7200>}`.
            `timeout_s` is optional; when omitted the catalog default is used. If no event
            arrives within it the runtime synthesises a failure, so a value shorter than
            the action's real duration will break the plan.

            The plan is stored per (archetype x time_of_day x region_state x climate)
            bucket and shared by every NPC in that bucket, so it must never mention a
            single individual.

            JSON Schema actually enforced:

            ```json
            {{schema}}
            ```

            """;
    }

    // ---------------------------------------------------------------- [4] few-shot
    /// <summary>파일명 정렬 순서로 싣는다. 디렉터리 순회 순서에 의존하면 SHA 가 흔들린다.</summary>
    private static string RenderFewShots()
    {
        var dir = Path.Combine(SchemaGen.DataDir, "fewshot");
        var files = Directory.GetFiles(dir, "*.json")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToArray();

        var sb = new StringBuilder(8 * 1024);
        sb.Append("\n# EXAMPLES\n\n");
        sb.Append("Each example is one request and the plan it should produce.\n");

        var compact = new JsonSerializerOptions { WriteIndented = false };
        var n = 0;
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            n++;
            sb.Append("\n## example ").Append(n).Append("\n\nrequest:\n");
            sb.Append(JsonSerializer.Serialize(doc.RootElement.GetProperty("request"), compact));
            sb.Append("\n\nplan:\n");
            sb.Append(JsonSerializer.Serialize(doc.RootElement.GetProperty("plan"), compact));
            sb.Append('\n');
        }

        sb.Append("\nNotice that the three plans differ in shape, not only in wording: the\n");
        sb.Append("situation decides where the NPC goes, how long it works, and when it sleeps.\n");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 완료 조건
    /// <summary>`spike prefix` — 1,000회 조립해 SHA 가 같은지, 토큰 수가 예산 안인지 확인한다.</summary>
    public static int Run()
    {
        var first = Build();

        Console.WriteLine("section token counts (o200k_base)");
        foreach (var (section, tokens) in first.Sections)
        {
            Console.WriteLine($"  {section,-16} {tokens,6}");
        }

        Console.WriteLine($"prefix tokens = {first.TokenCount}, sha = {first.Sha256[..12]}, chars = {first.Text.Length}");

        var same = true;
        for (var i = 0; i < 1000; i++)
        {
            var again = Build();
            if (again.Sha256 != first.Sha256)
            {
                Console.Error.WriteLine($"SHA drift at build {i}: {again.Sha256[..12]}");
                same = false;
                break;
            }
        }

        Console.WriteLine($"1000 builds identical : {(same ? "PASS" : "FAIL")}");

        var inBudget = first.TokenCount is >= 4200 and <= 4500;
        var overMinimum = first.TokenCount >= 4096;
        Console.WriteLine($"4200 <= tokens <= 4500 : {(inBudget ? "PASS" : "FAIL")}");
        Console.WriteLine($"tokens >= 4096 (캐시 임계) : {(overMinimum ? "PASS" : "FAIL")}");

        File.WriteAllText(Path.Combine(SchemaGen.OutDir, "prefix.txt"), first.Text);
        return same && inBudget ? 0 : 1;
    }
}
