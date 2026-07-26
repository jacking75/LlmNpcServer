using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Spike;

/// <summary>엔진 종류. 비용 계산과 동시성 측정 대상 선별에 쓴다.</summary>
internal enum EngineKind
{
    /// <summary>dotLLM (별도 프로세스, OpenAI 호환). GPLv3 — 인프로세스 임베딩 금지.</summary>
    DotLlm,

    /// <summary>llama.cpp 계열 로컬 서버 (LM Studio).</summary>
    LlamaCpp,

    /// <summary>외부 API.</summary>
    External,
}

/// <summary>
/// 측정 대상 엔진 1종. 로컬이든 외부든 <see cref="Clients.Create"/> 가 같은
/// <see cref="IChatClient"/> 로 만들어 준다 — 이게 §10.4 3-티어 라우팅의 전제다.
/// </summary>
/// <param name="Id">CSV 에 찍히는 짧은 식별자.</param>
/// <param name="Kind">엔진 종류.</param>
/// <param name="Model">서버에 보낼 모델 id.</param>
/// <param name="Endpoint">OpenAI 호환 base URL.</param>
/// <param name="ApiKeyEnv">API 키 환경변수 이름. 로컬은 null.</param>
/// <param name="InputUsdPerMTok">입력 100만 토큰당 단가 (USD). 로컬은 0.</param>
/// <param name="CachedInputUsdPerMTok">캐시 적중 입력 100만 토큰당 단가 (USD).</param>
/// <param name="OutputUsdPerMTok">출력 100만 토큰당 단가 (USD).</param>
/// <param name="SuffixTag">
/// 서픽스 끝에 붙는 엔진 전용 꼬리표. Qwen3 의 `/no_think` 처럼 모델별 스위치를 넣는 자리다.
/// **프리픽스는 절대 건드리지 않는다** — 캐시가 깨진다 (CLAUDE.md §2.5).
/// </param>
internal sealed record EngineSpec(
    string Id,
    EngineKind Kind,
    string Model,
    string Endpoint,
    string? ApiKeyEnv,
    double InputUsdPerMTok = 0,
    double CachedInputUsdPerMTok = 0,
    double OutputUsdPerMTok = 0,
    string SuffixTag = "")
{
    public bool IsLocal => Kind != EngineKind.External;

    /// <summary>API 키가 필요한데 환경변수가 비어 있으면 이 엔진은 건너뛴다.</summary>
    public bool IsConfigured =>
        ApiKeyEnv is null ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyEnv));

    public double CostUsd(long inputTok, long cachedTok, long outputTok)
    {
        var fresh = Math.Max(0, inputTok - cachedTok);
        return (fresh * InputUsdPerMTok
                + cachedTok * CachedInputUsdPerMTok
                + outputTok * OutputUsdPerMTok) / 1_000_000d;
    }
}

/// <summary>한 번의 호출 결과. 측정에 필요한 것만 담는다.</summary>
/// <param name="Text">응답 본문.</param>
/// <param name="PromptTokens">입력 토큰 수 (캐시 적중분 포함).</param>
/// <param name="CachedTokens">그중 프롬프트 캐시가 적중한 토큰 수.</param>
/// <param name="CompletionTokens">출력 토큰 수.</param>
/// <param name="TotalMs">요청 전체 소요.</param>
/// <param name="FirstTokenMs">첫 토큰까지 (스트리밍일 때만. 비스트리밍은 -1). prefill 대용치.</param>
/// <param name="Error">실패 시 메시지. 성공이면 null.</param>
/// <param name="Retries">429 로 다시 던진 횟수.</param>
internal readonly record struct Reply(
    string Text,
    long PromptTokens,
    long CachedTokens,
    long CompletionTokens,
    double TotalMs,
    double FirstTokenMs,
    string? Error,
    int Retries = 0)
{
    public bool Ok => Error is null;

    public double DecodeMs => FirstTokenMs < 0 ? -1 : TotalMs - FirstTokenMs;

    public bool IsRateLimited => Error is not null && Error.Contains("429", StringComparison.Ordinal);
}

/// <summary>
/// 요청 간격을 벌린다. 이 키는 무료 등급이라 분당 요청 수 상한이 낮다 —
/// 간격을 안 두면 T0-09 가 429 로 뒤덮인다. (상한 실측은 T0-11 의 몫이다.)
/// </summary>
internal sealed class Pacer(double requestsPerMinute)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _interval = requestsPerMinute <= 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds(60.0 / requestsPerMinute);

    private long _nextTicks;

    public async Task WaitAsync(CancellationToken ct = default)
    {
        if (_interval == TimeSpan.Zero)
        {
            return;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var now = Environment.TickCount64;
            var wait = _nextTicks - now;
            if (wait > 0)
            {
                await Task.Delay((int)wait, ct);
                now += wait;
            }

            _nextTicks = now + (long)_interval.TotalMilliseconds;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// T0-03 — 로컬(dotLLM · llama.cpp)과 외부(Gemini)를 동일한 <see cref="IChatClient"/> 코드 경로로 부른다.
/// </summary>
internal static class Clients
{
    // ---------------------------------------------------------------- 엔진 목록
    // 단가는 2026-07 기준 공개가. W1_results.md §M5 에서 실측 토큰 수와 곱한다.
    public static readonly EngineSpec DotLlm8B = new(
        Id: "dotllm-qwen2.5-7b",
        Kind: EngineKind.DotLlm,
        Model: "Qwen2.5-7B-Instruct-Q4_K_M",
        Endpoint: "http://localhost:8080/v1",
        ApiKeyEnv: null);

    public static readonly EngineSpec DotLlm4B = new(
        Id: "dotllm-phi4-mini",
        Kind: EngineKind.DotLlm,
        Model: "microsoft_Phi-4-mini-instruct-Q4_K_M",
        Endpoint: "http://localhost:8080/v1",
        ApiKeyEnv: null);

    // Qwen3 는 하이브리드 추론 모델이라 기본이 thinking 이다. 끄지 않으면 플랜 1건에
    // 2,000 토큰을 쓰고 51초가 걸린다 (실측). `/no_think` 는 서픽스에만 붙인다.
    public static readonly EngineSpec LlamaCpp8B = new(
        Id: "llamacpp-qwen3-8b",
        Kind: EngineKind.LlamaCpp,
        Model: "qwen3-8b",
        Endpoint: "http://localhost:1234/v1",
        ApiKeyEnv: null,
        SuffixTag: " /no_think");

    public static readonly EngineSpec LlamaCpp4B = new(
        Id: "llamacpp-gemma3-4b",
        Kind: EngineKind.LlamaCpp,
        Model: "gemma-3-4b",
        Endpoint: "http://localhost:1234/v1",
        ApiKeyEnv: null);

    // 2.5 계열은 이 키로 404("no longer available to new users") 다. 3.x 만 쓴다.
    // 단가는 W1_results.md §M5 에서 확정한다 — 아래 값은 lite/flash 등급의 잠정치다.
    public static readonly EngineSpec Gemini31FlashLite = new(
        Id: "gemini-3.1-flash-lite",
        Kind: EngineKind.External,
        Model: "gemini-3.1-flash-lite",
        Endpoint: "https://generativelanguage.googleapis.com/v1beta/openai",
        ApiKeyEnv: "GEMINI_API_KEY",
        InputUsdPerMTok: 0.10,
        CachedInputUsdPerMTok: 0.025,
        OutputUsdPerMTok: 0.40);

    public static readonly EngineSpec Gemini35FlashLite = new(
        Id: "gemini-3.5-flash-lite",
        Kind: EngineKind.External,
        Model: "gemini-3.5-flash-lite",
        Endpoint: "https://generativelanguage.googleapis.com/v1beta/openai",
        ApiKeyEnv: "GEMINI_API_KEY",
        InputUsdPerMTok: 0.10,
        CachedInputUsdPerMTok: 0.025,
        OutputUsdPerMTok: 0.40);

    public static readonly EngineSpec Gemini35Flash = new(
        Id: "gemini-3.5-flash",
        Kind: EngineKind.External,
        Model: "gemini-3.5-flash",
        Endpoint: "https://generativelanguage.googleapis.com/v1beta/openai",
        ApiKeyEnv: "GEMINI_API_KEY",
        InputUsdPerMTok: 0.30,
        CachedInputUsdPerMTok: 0.075,
        OutputUsdPerMTok: 2.50);

    /// <summary>T0-10 매트릭스의 "엔진 7종".</summary>
    public static EngineSpec[] All =>
    [
        DotLlm8B, DotLlm4B, LlamaCpp8B, LlamaCpp4B,
        Gemini31FlashLite, Gemini35FlashLite, Gemini35Flash,
    ];

    public static EngineSpec ById(string id) =>
        All.FirstOrDefault(e => e.Id == id)
        ?? throw new ArgumentException($"unknown engine: {id} (있는 것: {string.Join(", ", All.Select(a => a.Id))})");

    // ---------------------------------------------------------------- 배선
    /// <summary>로컬·외부를 가리지 않고 같은 방식으로 만든다. 이 함수가 T0-03 의 본체다.</summary>
    public static IChatClient Create(EngineSpec e)
    {
        var key = e.ApiKeyEnv is null
            ? "not-needed"
            : Environment.GetEnvironmentVariable(e.ApiKeyEnv) ?? "";

        var options = new OpenAIClientOptions { Endpoint = new Uri(e.Endpoint) };

        // 로컬 서버는 재시도가 오히려 측정을 흐린다. 외부는 429 를 그대로 보고 싶으므로 역시 끈다.
        options.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);
        options.NetworkTimeout = TimeSpan.FromMinutes(10);

        return new OpenAIClient(new ApiKeyCredential(key), options)
            .GetChatClient(e.Model)
            .AsIChatClient();
    }

    /// <summary>프리픽스(system) + 서픽스(user) 2메시지. 엔진 꼬리표는 서픽스 끝에만 붙는다.</summary>
    public static List<ChatMessage> BuildMessages(EngineSpec spec, string prefixText, string suffixText) =>
    [
        new(ChatRole.System, prefixText),
        new(ChatRole.User, suffixText + spec.SuffixTag),
    ];

    // ---------------------------------------------------------------- 호출 (단일 경로)
    /// <summary>
    /// 비스트리밍 호출. 로컬·외부가 이 함수 하나를 공유한다.
    /// </summary>
    public static async Task<Reply> AskAsync(
        EngineSpec spec,
        IChatClient client,
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var r = await client.GetResponseAsync(messages, options, ct);
            sw.Stop();

            var (prompt, cached, completion) = ReadUsage(r);
            return new Reply(r.Text, prompt, cached, completion, sw.Elapsed.TotalMilliseconds, -1, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Reply("", 0, 0, 0, sw.Elapsed.TotalMilliseconds, -1, Describe(ex));
        }
    }

    /// <summary>
    /// 페이서로 간격을 벌리고, 429 면 지수 백오프로 다시 던진다.
    /// T0-09 · T0-10 · T0-12 가 쓴다. **T0-11 은 쓰지 않는다** — 거기서는 429 자체가 측정 대상이다.
    /// </summary>
    public static async Task<Reply> AskWithRetryAsync(
        EngineSpec spec,
        IChatClient client,
        IList<ChatMessage> messages,
        ChatOptions? options,
        Pacer? pacer,
        bool streaming = false,
        int maxRetries = 5,
        CancellationToken ct = default)
    {
        var delayMs = 4000;
        for (var attempt = 0; ; attempt++)
        {
            if (pacer is not null)
            {
                await pacer.WaitAsync(ct);
            }

            var r = streaming
                ? await AskStreamingAsync(spec, client, messages, options, ct)
                : await AskAsync(spec, client, messages, options, ct);

            if (r.Ok || !r.IsRateLimited || attempt >= maxRetries)
            {
                return r with { Retries = attempt };
            }

            await Task.Delay(delayMs, ct);
            delayMs = Math.Min(delayMs * 2, 60_000);
        }
    }

    /// <summary>
    /// 스트리밍 호출. 첫 토큰까지의 시간을 prefill 대용치로 쓴다 (T0-10).
    /// </summary>
    public static async Task<Reply> AskStreamingAsync(
        EngineSpec spec,
        IChatClient client,
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        double firstMs = -1;
        var text = new System.Text.StringBuilder(1024);
        var updates = new List<ChatResponseUpdate>(256);

        try
        {
            await foreach (var u in client.GetStreamingResponseAsync(messages, options, ct))
            {
                if (firstMs < 0 && !string.IsNullOrEmpty(u.Text))
                {
                    firstMs = sw.Elapsed.TotalMilliseconds;
                }

                text.Append(u.Text);
                updates.Add(u);
            }

            sw.Stop();
            var (prompt, cached, completion) = ReadUsage(updates.ToChatResponse());
            return new Reply(text.ToString(), prompt, cached, completion,
                sw.Elapsed.TotalMilliseconds, firstMs, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Reply(text.ToString(), 0, 0, 0, sw.Elapsed.TotalMilliseconds, firstMs, Describe(ex));
        }
    }

    // ---------------------------------------------------------------- usage 읽기
    /// <summary>
    /// (prompt, cached, completion) 토큰 수를 꺼낸다.
    /// 캐시 적중 토큰은 제공사마다 이름이 달라 3곳을 순서대로 본다.
    /// </summary>
    public static (long Prompt, long Cached, long Completion) ReadUsage(ChatResponse r)
    {
        var u = r.Usage;
        long prompt = u?.InputTokenCount ?? 0;
        long completion = u?.OutputTokenCount ?? 0;
        long cached = 0;

        // (1) M.E.AI 가 매핑해 주는 추가 카운트 — OpenAI 호환 서버는 대개 여기에 들어온다.
        if (u?.AdditionalCounts is { Count: > 0 } counts)
        {
            foreach (var kv in counts)
            {
                if (kv.Key.Contains("cached", StringComparison.OrdinalIgnoreCase))
                {
                    cached = kv.Value;
                    break;
                }
            }
        }

        // (2) OpenAI SDK 원시 객체 — InputTokenDetails.CachedTokenCount
        if (cached == 0 && r.RawRepresentation is OpenAI.Chat.ChatCompletion cc)
        {
            cached = cc.Usage?.InputTokenDetails?.CachedTokenCount ?? 0;
        }

        // (3) 그래도 0 이면 원시 JSON 에서 prompt_tokens_details.cached_tokens 를 긁는다.
        if (cached == 0)
        {
            cached = ScrapeCachedTokens(r.RawRepresentation);
        }

        return (prompt, cached, completion);
    }

    private static long ScrapeCachedTokens(object? raw)
    {
        if (raw is null)
        {
            return 0;
        }

        try
        {
            // BinaryData / JsonElement 형태로 오는 경우만 시도한다. 실패는 조용히 0.
            var json = raw switch
            {
                BinaryData bd => bd.ToString(),
                JsonElement je => je.GetRawText(),
                string s => s,
                _ => null,
            };

            if (json is null)
            {
                return 0;
            }

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("usage", out var usage) &&
                usage.TryGetProperty("prompt_tokens_details", out var details) &&
                details.TryGetProperty("cached_tokens", out var cachedTok))
            {
                return cachedTok.GetInt64();
            }
        }
        catch (JsonException)
        {
        }

        return 0;
    }

    private static string Describe(Exception ex)
    {
        var msg = ex.Message.Replace("\r", " ").Replace("\n", " ");
        return msg.Length > 300 ? msg[..300] : msg;
    }

    // ---------------------------------------------------------------- 스모크
    /// <summary>
    /// `spike clients [engineId...]` — 엔진마다 같은 함수에 클라이언트만 바꿔 넣어 호출하고
    /// (prompt/cached/completion) 토큰 수를 찍는다. T0-03 의 완료 조건.
    /// </summary>
    public static async Task<int> RunSmokeAsync(string[] args)
    {
        // --short: 배선만 확인한다. 로컬 CPU 추론은 4,000 토큰 프리필에 10분이 넘어가므로
        //          긴 프리픽스로는 배선 검증이 불가능하다.
        var shortMode = args.Contains("--short");
        var targets = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray() is { Length: > 0 } ids
            ? ids.Select(ById).ToArray()
            : All;

        // 프롬프트 캐시가 걸리는지 보려면 같은 긴 프리픽스를 두 번 던져야 한다.
        // T0-06 이전이므로 여기서는 캐시 임계(4,096)를 넘기는 더미 프리픽스를 쓴다.
        var filler = string.Join('\n',
            Enumerable.Range(0, shortMode ? 4 : 420).Select(i =>
                $"- rule {i:D4}: the planner must never invent an action id outside the catalog."));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are a terse NPC behavior planner.\n" + filler),
            new(ChatRole.User, "Reply with exactly one word: ok"),
        };

        var options = new ChatOptions { MaxOutputTokens = 16, Temperature = 0f };

        Console.WriteLine($"{"engine",-24} {"pass",-5} {"prompt",7} {"cached",7} {"out",5} {"ms",9}  text");
        Console.WriteLine(new string('-', 96));

        var anyOk = false;
        foreach (var spec in targets)
        {
            if (!spec.IsConfigured)
            {
                Console.WriteLine($"{spec.Id,-24} skip  ({spec.ApiKeyEnv} 없음)");
                continue;
            }

            using var client = Create(spec);

            // 같은 함수(AskAsync)에 클라이언트만 갈아 끼운다 — 2회: 1회차 미적중 / 2회차 적중 기대
            for (var pass = 1; pass <= 2; pass++)
            {
                var r = await AskAsync(spec, client, messages, options);
                if (r.Ok)
                {
                    anyOk = true;
                    var text = r.Text.Replace("\n", " ");
                    Console.WriteLine(
                        $"{spec.Id,-24} {pass,-5} {r.PromptTokens,7} {r.CachedTokens,7} " +
                        $"{r.CompletionTokens,5} {r.TotalMs,9:F0}  {text[..Math.Min(24, text.Length)]}");
                }
                else
                {
                    Console.WriteLine($"{spec.Id,-24} {pass,-5} ERROR {r.Error}");
                    break;
                }
            }
        }

        return anyOk ? 0 : 1;
    }
}
