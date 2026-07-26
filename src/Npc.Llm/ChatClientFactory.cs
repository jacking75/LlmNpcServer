using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Npc.Llm;

/// <summary>엔진 종류. 비용 계산과 캐시 판정 방식이 갈린다.</summary>
public enum LlmEngineKind
{
    /// <summary>dotLLM (별도 프로세스 · OpenAI 호환). <b>GPLv3 — 인프로세스 임베딩 금지</b> (CLAUDE.md §2.7).</summary>
    DotLlm = 0,

    /// <summary>llama.cpp 계열 로컬 서버 (LM Studio 등).</summary>
    LlamaCpp = 1,

    /// <summary>외부 API.</summary>
    External = 2,
}

/// <summary>
/// 엔진 하나의 설정. <c>appsettings.Llm.json</c> 한 줄에 대응한다.
/// </summary>
/// <param name="Id">짧은 식별자. 메트릭·manifest 에 그대로 실린다.</param>
/// <param name="Kind">엔진 종류.</param>
/// <param name="Model">서버에 보낼 모델 id.</param>
/// <param name="Endpoint">OpenAI 호환 base URL.</param>
/// <param name="ApiKeyEnv">API 키 환경변수 이름. 로컬은 null. <b>키 자체를 설정 파일에 쓰지 않는다.</b></param>
/// <param name="InputUsdPerMTok">입력 100만 토큰당 단가(USD). 로컬은 0.</param>
/// <param name="CachedInputUsdPerMTok">캐시 적중 입력 100만 토큰당 단가(USD).</param>
/// <param name="OutputUsdPerMTok">출력 100만 토큰당 단가(USD).</param>
/// <param name="SuffixTag">
/// 서픽스 끝에 붙는 엔진 전용 꼬리표 (Qwen3 의 <c>/no_think</c> 같은 것).
/// <b>프리픽스는 절대 건드리지 않는다</b> — 캐시가 깨진다 (CLAUDE.md §2.5).
/// </param>
/// <param name="Temperature">시도 1의 temperature. 재시도는 +0.2 다 (docs/12 §6).</param>
/// <param name="MaxOutputTokens">출력 상한. 플랜 하나는 300~700 토큰이다.</param>
/// <param name="ForceJsonSchema">
/// <c>response_format=json_schema</c> 로 강제할 것인가.
/// <b>기본값은 false 다</b> — W1(T0-09)에서 강제 디코딩은 유효 JSON 99/100 을 내면서
/// 어휘 검증 통과가 0 이었다. 두 모드를 T2-19 첫 회차에서 재고 확정한다.
/// </param>
/// <param name="TimeoutSeconds">HTTP 타임아웃. 로컬 CPU 추론은 분 단위가 나온다.</param>
public sealed record LlmEngineOptions(
    string Id,
    LlmEngineKind Kind,
    string Model,
    string Endpoint,
    string? ApiKeyEnv = null,
    double InputUsdPerMTok = 0,
    double CachedInputUsdPerMTok = 0,
    double OutputUsdPerMTok = 0,
    string SuffixTag = "",
    float Temperature = 0.4f,
    int MaxOutputTokens = 1024,
    bool ForceJsonSchema = false,
    int TimeoutSeconds = 600)
{
    /// <summary>로컬 엔진인가. 로컬은 캐시 적중을 토큰이 아니라 prefill 시간으로 판정한다.</summary>
    public bool IsLocal => Kind != LlmEngineKind.External;

    /// <summary>키가 필요한데 환경변수가 비어 있으면 이 엔진은 쓸 수 없다.</summary>
    public bool IsConfigured =>
        ApiKeyEnv is null || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyEnv));

    /// <summary>이번 호출의 비용(USD). 캐시 적중분은 싼 단가로 센다.</summary>
    public double CostUsd(long promptTokens, long cachedTokens, long completionTokens)
    {
        long fresh = Math.Max(0, promptTokens - cachedTokens);

        return ((fresh * InputUsdPerMTok)
            + (cachedTokens * CachedInputUsdPerMTok)
            + (completionTokens * OutputUsdPerMTok)) / 1_000_000d;
    }
}

/// <summary>
/// <c>appsettings.Llm.json</c> 전체. docs/10 §2 T0-2.
/// <b>설정만 바꿔 로컬 ↔ 외부를 바꾼다</b> — 코드에는 제공사가 나오지 않는다.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>설정 파일 이름.</summary>
    public const string FileName = "appsettings.Llm.json";

    /// <summary>기본 엔진 id.</summary>
    public required string Default { get; init; }

    /// <summary>설정된 엔진들. 파일 등장 순서.</summary>
    public required ImmutableArray<LlmEngineOptions> Engines { get; init; }

    /// <summary>id 로 엔진을 고른다. null 이면 <see cref="Default"/>.</summary>
    public LlmEngineOptions Engine(string? id = null)
    {
        string wanted = id ?? Default;

        foreach (LlmEngineOptions engine in Engines)
        {
            if (string.Equals(engine.Id, wanted, StringComparison.Ordinal))
            {
                return engine;
            }
        }

        throw new InvalidDataException(
            $"'{wanted}' 엔진이 {FileName} 에 없다. 있는 것: {string.Join(", ", Engines.Select(e => e.Id))}");
    }

    /// <summary>파일에서 읽는다.</summary>
    public static LlmOptions Load(string path)
    {
        LlmOptionsDto? dto = JsonSerializer.Deserialize(
            File.ReadAllText(path), LlmJsonContext.Default.LlmOptionsDto);

        if (dto?.Engines is null || dto.Engines.Length == 0)
        {
            throw new InvalidDataException($"{path} 에서 엔진을 하나도 읽지 못했다.");
        }

        return new LlmOptions
        {
            Default = dto.Default,
            Engines = [.. dto.Engines.Select(e => e.ToOptions())],
        };
    }

    /// <summary>
    /// 실행 위치에서 위로 올라가며 <c>appsettings.Llm.json</c> 을 찾는다.
    /// 호스트·프리베이크 도구·테스트가 같은 파일 하나를 본다 — 사본이 생기면 어느 쪽이 진짜인지 알 수 없다.
    /// </summary>
    public static LlmOptions LoadDefault(string? startDirectory = null)
    {
        var directory = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, FileName);

            if (File.Exists(candidate))
            {
                return Load(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{FileName} 을 찾지 못했다.", FileName);
    }

    // --- JSON DTO. 소스 생성기로 직렬화한다. ---

    internal sealed record LlmOptionsDto(string Default, LlmEngineDto[] Engines);

    internal sealed record LlmEngineDto(
        string Id,
        string Kind,
        string Model,
        string Endpoint,
        string? ApiKeyEnv,
        double InputUsdPerMTok,
        double CachedInputUsdPerMTok,
        double OutputUsdPerMTok,
        string? SuffixTag,
        float? Temperature,
        int? MaxOutputTokens,
        bool ForceJsonSchema,
        int? TimeoutSeconds)
    {
        public LlmEngineOptions ToOptions() => new(
            Id,
            Enum.TryParse(Kind, ignoreCase: true, out LlmEngineKind kind)
                ? kind
                : throw new InvalidDataException($"{LlmOptions.FileName}: '{Kind}' 엔진 종류를 모른다 ({Id})."),
            Model,
            Endpoint,
            ApiKeyEnv,
            InputUsdPerMTok,
            CachedInputUsdPerMTok,
            OutputUsdPerMTok,
            SuffixTag ?? string.Empty,
            Temperature ?? 0.4f,
            MaxOutputTokens ?? 1024,
            ForceJsonSchema,
            TimeoutSeconds ?? 600);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(LlmOptions.LlmOptionsDto))]
internal sealed partial class LlmJsonContext : JsonSerializerContext;

/// <summary>
/// 로컬(dotLLM · llama.cpp)과 외부를 <b>같은 <see cref="IChatClient"/> 코드 경로</b>로 부른다.
/// docs/10 §2 T0-2 · CLAUDE.md §2.7.
///
/// 제공사 SDK(<c>OpenAI</c>)를 아는 곳은 이 파일 하나뿐이다 —
/// <c>Npc.Llm</c> 밖에서 제공사 SDK 를 직접 부르면 티어 라우팅(W9)이 성립하지 않는다.
/// </summary>
public static class ChatClientFactory
{
    /// <summary>엔진 설정에서 클라이언트를 만든다.</summary>
    public static IChatClient Create(LlmEngineOptions engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        string key = engine.ApiKeyEnv is null
            ? "not-needed"
            : Environment.GetEnvironmentVariable(engine.ApiKeyEnv) ?? string.Empty;

        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(engine.Endpoint),
            // 429 를 그대로 보고 싶다. 재시도는 호출부(AIMD)가 정한다 — SDK 가 조용히 삼키면
            // T2-19 의 "429 최초 발생 동시성" 실측이 통째로 무의미해진다.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            NetworkTimeout = TimeSpan.FromSeconds(engine.TimeoutSeconds),
        };

        return new OpenAIClient(new ApiKeyCredential(key), options)
            .GetChatClient(engine.Model)
            .AsIChatClient();
    }

    /// <summary>프리픽스(system) + 서픽스(user) 2메시지. 엔진 꼬리표는 서픽스 끝에만 붙는다.</summary>
    public static List<ChatMessage> BuildMessages(LlmEngineOptions engine, string prefix, string suffix)
    {
        ArgumentNullException.ThrowIfNull(engine);

        return
        [
            new ChatMessage(ChatRole.System, prefix),
            new ChatMessage(ChatRole.User, suffix + engine.SuffixTag),
        ];
    }

    /// <summary>이 시도의 호출 옵션. 재시도는 temperature 를 올린다 (docs/12 §6).</summary>
    public static ChatOptions BuildChatOptions(LlmEngineOptions engine, SchemaProvider schema, int attempt)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(schema);

        var options = new ChatOptions
        {
            Temperature = engine.Temperature + (0.2f * Math.Max(0, attempt - 1)),
            MaxOutputTokens = engine.MaxOutputTokens,
        };

        if (engine.ForceJsonSchema)
        {
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema(
                JsonDocument.Parse(schema.PlanSchemaJson).RootElement, "NpcPlan");
        }

        return options;
    }

    /// <summary>
    /// (prompt, cached, completion) 토큰 수를 꺼낸다.
    ///
    /// 캐시 적중 토큰은 제공사마다 이름이 달라 세 곳을 순서대로 본다.
    /// <b>dotLLM 0.1.0-preview.3 은 항상 0 을 보고한다</b> (W1_env.md §4.4) —
    /// 로컬 엔진의 캐시 판정은 토큰이 아니라 prefill 시간 감소로 한다.
    /// </summary>
    public static (long Prompt, long Cached, long Completion) ReadUsage(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        UsageDetails? usage = response.Usage;
        long prompt = usage?.InputTokenCount ?? 0;
        long completion = usage?.OutputTokenCount ?? 0;
        long cached = 0;

        // (1) M.E.AI 가 매핑해 주는 추가 카운트 — OpenAI 호환 서버는 대개 여기에 들어온다.
        if (usage?.AdditionalCounts is { Count: > 0 } counts)
        {
            foreach (KeyValuePair<string, long> entry in counts)
            {
                if (entry.Key.Contains("cached", StringComparison.OrdinalIgnoreCase))
                {
                    cached = entry.Value;
                    break;
                }
            }
        }

        // (2) OpenAI SDK 원시 객체.
        if (cached == 0 && response.RawRepresentation is OpenAI.Chat.ChatCompletion completionRaw)
        {
            cached = completionRaw.Usage?.InputTokenDetails?.CachedTokenCount ?? 0;
        }

        // (3) 그래도 0 이면 원시 JSON 에서 prompt_tokens_details.cached_tokens 를 긁는다.
        if (cached == 0)
        {
            cached = ScrapeCachedTokens(response.RawRepresentation);
        }

        return (prompt, cached, completion);
    }

    private static long ScrapeCachedTokens(object? raw)
    {
        string? json = raw switch
        {
            BinaryData data => data.ToString(),
            JsonElement element => element.GetRawText(),
            string text => text,
            _ => null,
        };

        if (json is null)
        {
            return 0;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("usage", out JsonElement usage)
                && usage.TryGetProperty("prompt_tokens_details", out JsonElement details)
                && details.TryGetProperty("cached_tokens", out JsonElement cached))
            {
                return cached.GetInt64();
            }
        }
        catch (JsonException)
        {
            // 원시 표현이 JSON 이 아닐 수 있다. 캐시 토큰을 모르는 것은 실패가 아니다.
        }

        return 0;
    }
}
