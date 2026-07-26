using System.Diagnostics;
using Microsoft.Extensions.AI;
using Npc.Core;
using Npc.Llm;
using Npc.MasterData;

namespace Npc.Prebake;

/// <summary>워밍업 한 번의 결과. manifest·리포트가 이 숫자를 남긴다.</summary>
/// <param name="Attempted">던졌는가. 로컬 티어에서는 거짓이다.</param>
/// <param name="Succeeded">응답을 받았는가.</param>
/// <param name="LatencyMs">지연(ms). 캐시 write 를 포함한 값이라 이후 요청보다 느리다.</param>
/// <param name="PromptTokens">입력 토큰.</param>
/// <param name="CachedTokens">
/// 캐시 적중 입력 토큰. <b>워밍업 요청 자체는 0 에 가깝다</b> — 캐시를 만드는 요청이기 때문이다.
/// </param>
/// <param name="CostUsd">비용(USD).</param>
/// <param name="Error">실패 사유. 실패해도 프리베이크를 멈추지 않는다.</param>
public readonly record struct WarmupResult(
    bool Attempted,
    bool Succeeded,
    double LatencyMs,
    long PromptTokens,
    long CachedTokens,
    double CostUsd,
    string? Error)
{
    /// <summary>던지지 않았다.</summary>
    public static WarmupResult Skipped => new(false, false, 0, 0, 0, 0, null);
}

/// <summary>
/// 프리픽스 워밍업. docs/13 §4 · §8.
///
/// <b>동시 요청 전에 단건을 먼저 던진다.</b> 프롬프트 캐시는 첫 요청이 만들고, 그 write 에는
/// 할증이 붙는다(Anthropic 기준 1.25~2배). 동시 N 개를 그냥 던지면 <b>N 개 전부가 cache miss 로
/// 시작해 write 비용을 N 번 낸다</b> — 2,880건 회차에서 이것이 비용 차이의 큰 몫이다.
///
/// <para>
/// <b>로컬 티어에서는 이 실험이 성립하지 않는다.</b> dotLLM 0.1.0-preview.3 은
/// <c>cached_tokens</c> 를 항상 0 으로 보고한다 (<c>W1_env.md §4.4</c>).
/// 워밍업 효과 실측은 외부 API 로만 한다 — <see cref="RunAsync"/> 는 로컬이면 던지지도 않는다.
/// </para>
///
/// <para>
/// 서픽스는 <b>실제 대상 버킷의 것을 쓴다.</b> 프리픽스만 같아도 캐시는 만들어지지만,
/// 워커가 보낼 메시지 모양과 똑같은 요청을 한 번 흘려 보는 것이 회차 전체의 예비 점검도 된다.
/// </para>
/// </summary>
public static class Warmup
{
    /// <summary>캐시 반영 대기(ms). docs/13 §4 의 <c>Task.Delay(200)</c>.</summary>
    public const int CacheSettleDelayMs = 200;

    /// <summary>
    /// 단건을 던져 프롬프트 캐시를 만든다. 실패해도 예외를 밖으로 내지 않는다 —
    /// 워밍업 실패는 비용이 조금 더 드는 일이지 회차를 멈출 일이 아니다.
    /// </summary>
    /// <param name="client">엔진 클라이언트.</param>
    /// <param name="engine">엔진 설정. 로컬이면 던지지 않는다.</param>
    /// <param name="prefix">기동 시 1회 조립한 프리픽스.</param>
    /// <param name="data">마스터데이터. 서픽스 재료다.</param>
    /// <param name="bucket">서픽스에 쓸 대상 버킷. 보통 첫 대상이다.</param>
    /// <param name="settleDelayMs">캐시 반영 대기. 0 이면 기다리지 않는다.</param>
    /// <param name="cancellationToken">취소.</param>
    public static async Task<WarmupResult> RunAsync(
        IChatClient client,
        LlmEngineOptions engine,
        PromptPrefix prefix,
        MasterDataSet data,
        BucketKey bucket,
        int settleDelayMs = CacheSettleDelayMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(data);

        // 로컬 엔진은 cached_tokens 를 보고하지 않는다. 던져도 잴 것이 없다 (W1_env.md §4.4).
        if (engine.IsLocal)
        {
            return WarmupResult.Skipped;
        }

        string suffix = PlanRequestSuffix.Build(new PlanRequest(bucket, data.InitialFlags(bucket)), data);
        List<ChatMessage> messages = ChatClientFactory.BuildMessages(engine, prefix.Text, suffix);
        ChatOptions options = ChatClientFactory.BuildChatOptions(engine, prefix.Schema, attempt: 1);

        long started = Stopwatch.GetTimestamp();

        try
        {
            ChatResponse response = await client
                .GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);

            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            (long prompt, long cached, long completion) = ChatClientFactory.ReadUsage(response);

            if (settleDelayMs > 0)
            {
                // 캐시 반영 대기. 여기서 기다리지 않으면 뒤이은 동시 요청이 write 를 또 낸다.
                await Task.Delay(settleDelayMs, cancellationToken).ConfigureAwait(false);
            }

            return new WarmupResult(
                Attempted: true,
                Succeeded: true,
                LatencyMs: elapsed,
                PromptTokens: prompt,
                CachedTokens: cached,
                CostUsd: engine.CostUsd(prompt, cached, completion),
                Error: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new WarmupResult(
                Attempted: true,
                Succeeded: false,
                LatencyMs: Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                PromptTokens: 0,
                CachedTokens: 0,
                CostUsd: 0,
                Error: Describe(ex));
        }
    }

    /// <summary>
    /// <b>워밍업이 끝난 뒤에 동시 실행을 시작한다.</b> 이 순서를 코드로 못 박는 자리다 —
    /// 호출부가 순서를 뒤집으면 캐시 write 를 동시성 수만큼 낸다 (docs/13 §8 의 "흔한 실수" 1행).
    /// </summary>
    /// <typeparam name="T">동시 실행의 결과 타입.</typeparam>
    /// <param name="warmup">워밍업 단건.</param>
    /// <param name="concurrent">워커들. <paramref name="warmup"/> 이 완료된 뒤에 시작한다.</param>
    /// <param name="cancellationToken">취소.</param>
    public static async Task<(WarmupResult Warmup, T Result)> RunThenAsync<T>(
        Func<CancellationToken, Task<WarmupResult>> warmup,
        Func<CancellationToken, Task<T>> concurrent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(warmup);
        ArgumentNullException.ThrowIfNull(concurrent);

        WarmupResult first = await warmup(cancellationToken).ConfigureAwait(false);
        T result = await concurrent(cancellationToken).ConfigureAwait(false);

        return (first, result);
    }

    /// <summary>
    /// 워밍업 유무 비용 차이 실측. docs/13 T3-11 의 완료 조건.
    ///
    /// <b>두 회차의 프리픽스가 서로 달라야 한다.</b> 같은 프리픽스로 A → B 를 연달아 돌리면
    /// B 는 A 가 만든 캐시를 그냥 물려받아 비교가 성립하지 않는다. 그래서 프리픽스 맨 앞에
    /// 회차별 소금을 한 줄 붙인다 — 프롬프트 캐시는 토큰 열의 <b>앞부터</b> 일치를 보므로
    /// 첫 줄이 다르면 완전히 다른 캐시 항목이 된다.
    /// </summary>
    /// <param name="client">엔진 클라이언트. 스레드 안전해야 한다.</param>
    /// <param name="engine">엔진 설정.</param>
    /// <param name="prefixText">프리픽스 본문.</param>
    /// <param name="schema">스키마 공급자 (강제 디코딩용).</param>
    /// <param name="suffix">서픽스 본문.</param>
    /// <param name="concurrency">동시 요청 수.</param>
    /// <param name="withWarmup">참이면 단건을 먼저 던진 뒤 동시 요청을 시작한다.</param>
    /// <param name="salt">프리픽스 맨 앞에 붙일 회차 구분자. 캐시를 확실히 콜드로 만든다.</param>
    /// <param name="cancellationToken">취소.</param>
    public static async Task<WarmupExperiment> MeasureAsync(
        IChatClient client,
        LlmEngineOptions engine,
        string prefixText,
        SchemaProvider schema,
        string suffix,
        int concurrency,
        bool withWarmup,
        string salt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);

        string prefix = salt + prefixText;
        ChatOptions options = ChatClientFactory.BuildChatOptions(engine, schema, attempt: 1);

        long warmupPrompt = 0;
        long warmupCached = 0;
        double warmupCost = 0;

        if (withWarmup)
        {
            (long p, long c, double cost) = await OneAsync(cancellationToken).ConfigureAwait(false);

            warmupPrompt = p;
            warmupCached = c;
            warmupCost = cost;

            await Task.Delay(CacheSettleDelayMs, cancellationToken).ConfigureAwait(false);
        }

        (long Prompt, long Cached, double Cost)[] wave = await Task.WhenAll(
            Enumerable.Range(0, concurrency).Select(_ => OneAsync(cancellationToken)))
            .ConfigureAwait(false);

        return new WarmupExperiment(
            WithWarmup: withWarmup,
            Concurrency: concurrency,
            WarmupPromptTokens: warmupPrompt,
            WarmupCachedTokens: warmupCached,
            WavePromptTokens: wave.Sum(x => x.Prompt),
            WaveCachedTokens: wave.Sum(x => x.Cached),
            TotalCostUsd: warmupCost + wave.Sum(x => x.Cost));

        async Task<(long Prompt, long Cached, double Cost)> OneAsync(CancellationToken ct)
        {
            ChatResponse response = await client
                .GetResponseAsync(ChatClientFactory.BuildMessages(engine, prefix, suffix), options, ct)
                .ConfigureAwait(false);

            (long prompt, long cached, long completion) = ChatClientFactory.ReadUsage(response);

            return (prompt, cached, engine.CostUsd(prompt, cached, completion));
        }
    }

    private static string Describe(Exception ex)
    {
        string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');

        return message.Length > 300 ? message[..300] : message;
    }
}

/// <summary>워밍업 유무 한 회차의 실측. docs/13 T3-11.</summary>
/// <param name="WithWarmup">워밍업을 했는가.</param>
/// <param name="Concurrency">동시 요청 수.</param>
/// <param name="WarmupPromptTokens">워밍업 단건의 입력 토큰.</param>
/// <param name="WarmupCachedTokens">워밍업 단건의 캐시 적중 토큰. 콜드라 0 에 가깝다.</param>
/// <param name="WavePromptTokens">동시 요청들의 입력 토큰 합.</param>
/// <param name="WaveCachedTokens">동시 요청들의 캐시 적중 토큰 합.</param>
/// <param name="TotalCostUsd">워밍업 포함 총비용(USD).</param>
public readonly record struct WarmupExperiment(
    bool WithWarmup,
    int Concurrency,
    long WarmupPromptTokens,
    long WarmupCachedTokens,
    long WavePromptTokens,
    long WaveCachedTokens,
    double TotalCostUsd)
{
    /// <summary>동시 요청들의 캐시 적중률. 워밍업이 통했으면 여기가 높다.</summary>
    public double WaveCacheHitRate => WavePromptTokens == 0 ? 0 : (double)WaveCachedTokens / WavePromptTokens;

    /// <summary>총 입력 토큰 (워밍업 포함).</summary>
    public long TotalPromptTokens => WarmupPromptTokens + WavePromptTokens;

    /// <summary>총 캐시 적중 토큰 (워밍업 포함).</summary>
    public long TotalCachedTokens => WarmupCachedTokens + WaveCachedTokens;
}
