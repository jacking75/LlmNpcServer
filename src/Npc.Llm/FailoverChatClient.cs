using Microsoft.Extensions.AI;
using Npc.Contracts;

namespace Npc.Llm;

/// <summary>체인 안 엔진 하나. 클라이언트와 자기 브레이커를 같이 든다.</summary>
/// <param name="Id">엔진 id. 메트릭 태그이자 브레이커 열쇠다.</param>
/// <param name="Client">호출 대상.</param>
/// <param name="Breaker">이 엔진 전용 브레이커.</param>
public sealed record FailoverEngine(string Id, IChatClient Client, CircuitBreaker Breaker);

/// <summary>페일오버가 일어났을 때의 통보.</summary>
/// <param name="From">실패한 엔진.</param>
/// <param name="To">넘어간 엔진. 마지막이면 null.</param>
/// <param name="Reason">사유 한 줄.</param>
public readonly record struct FailoverEvent(string From, string? To, string Reason);

/// <summary>
/// 제공사 페일오버 체인 (C-01).
///
/// <b>있던 페일오버는 티어 간(T2 → T1)뿐이고 제공사 간은 없었다.</b> T2 제공사 하나가 죽으면
/// 브레이커가 60초 열리고 전부 T1(로컬 GPU, 0.195 req/s)로 몰렸다. GPU 가 없으면 캐시+폴백만 남았다.
///
/// <para>
/// <b>실패를 두 종류로 가른다.</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>전송 실패</b>(429·5xx·타임아웃·연결 실패) → 다음 엔진. 같은 프롬프트를 다른 경로로
///     보내면 되는 문제다.
///   </description></item>
///   <item><description>
///     <b>모델 품질 문제</b>(400·스키마 거절) → 페일오버하지 않는다. 다른 제공사에 보내도
///     같은 프롬프트라 같은 결과가 나올 가능성이 높고, 그동안 비용만 두 배가 된다.
///     이것은 <c>TieredPlanCompiler</c> 가 이미 지키는 원칙이다.
///   </description></item>
/// </list>
///
/// <para>
/// <b>재시도 1회 규칙(CLAUDE.md §2.6)과 다른 이야기다.</b> 그 규칙은 <b>검증 실패</b> 재시도를
/// 말한다 — LLM 이 틀린 플랜을 냈을 때 두 번 이상 조르면 성공률이 거의 안 오르고 토큰만 탄다.
/// 여기의 재시도는 <b>전송 실패</b>이고, 요청이 상대에게 닿지도 않았다.
/// </para>
///
/// <para>
/// <b>비용 주의.</b> 프리픽스 캐시는 제공사별이라 페일오버 순간 캐시가 미적중된다.
/// 체인을 "같은 모델을 다른 경로로" 로 두면 프롬프트 품질은 유지된다.
/// </para>
/// </summary>
public sealed class FailoverChatClient : IChatClient
{
    private readonly IReadOnlyList<FailoverEngine> _chain;
    private readonly Func<Tick> _now;
    private readonly Action<FailoverEvent>? _onFailover;
    private readonly int _maxAttemptsPerEngine;

    /// <summary>체인을 만든다.</summary>
    /// <param name="chain">순서대로 시도할 엔진. 비어 있으면 안 된다.</param>
    /// <param name="now">지금 틱. 브레이커가 본다 — <c>DateTime</c> 을 쓰면 리플레이가 깨진다.</param>
    /// <param name="onFailover">페일오버 통보. 경보·계측이 받는다.</param>
    /// <param name="maxAttemptsPerEngine">같은 엔진 안에서의 시도 수. 1 이면 재시도 없음.</param>
    public FailoverChatClient(
        IReadOnlyList<FailoverEngine> chain,
        Func<Tick> now,
        Action<FailoverEvent>? onFailover = null,
        int maxAttemptsPerEngine = 2)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttemptsPerEngine);

        if (chain.Count == 0)
        {
            throw new ArgumentException("페일오버 체인이 비어 있다.", nameof(chain));
        }

        _chain = chain;
        _now = now;
        _onFailover = onFailover;
        _maxAttemptsPerEngine = maxAttemptsPerEngine;
    }

    /// <summary>페일오버 누계. 0 이 아니면 제공사 하나가 흔들리고 있다.</summary>
    public long Failovers { get; private set; }

    /// <summary>마지막으로 성공한 엔진 id. 대시보드가 읽는다.</summary>
    public string LastEngineId { get; private set; } = string.Empty;

    /// <summary>체인 안 엔진 id 전부. 로그·계측이 쓴다.</summary>
    public IReadOnlyList<string> EngineIds => [.. _chain.Select(e => e.Id)];

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // 열거를 한 번만 한다 — 뒤 엔진이 이미 소비된 시퀀스를 받으면 빈 프롬프트를 보낸다.
        var prompt = messages as IList<ChatMessage> ?? [.. messages];

        Exception? last = null;

        for (int index = 0; index < _chain.Count; index++)
        {
            FailoverEngine engine = _chain[index];
            Tick now = _now();

            if (!engine.Breaker.TryEnter(now))
            {
                // 열린 브레이커는 시도조차 하지 않는다. 타임아웃까지 기다린 뒤 넘어가면
                // 재계획 하나가 (외부 타임아웃 + 로컬 5.1s) 를 먹는다.
                Report(engine.Id, index, "브레이커 개방");
                continue;
            }

            for (int attempt = 1; attempt <= _maxAttemptsPerEngine; attempt++)
            {
                try
                {
                    ChatResponse response = await engine.Client
                        .GetResponseAsync(prompt, options, cancellationToken)
                        .ConfigureAwait(false);

                    engine.Breaker.RecordSuccess();
                    LastEngineId = engine.Id;

                    return response;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;   // 우리가 취소했다. 페일오버할 일이 아니다.
                }
                catch (Exception e)
                {
                    last = e;

                    if (!IsTransport(e))
                    {
                        // 모델 품질 문제다. 다른 제공사에 보내도 같은 프롬프트라 같은 결과가
                        // 나올 가능성이 높고, 그동안 비용만 두 배가 된다.
                        engine.Breaker.RecordSuccess();
                        throw;
                    }

                    engine.Breaker.RecordFailure(_now());

                    if (attempt < _maxAttemptsPerEngine && IsWorthRetrying(e))
                    {
                        await Task.Delay(
                            RetryPolicy.DelayMs(index, attempt, baseDelayMs: 500, maxDelayMs: 5_000),
                            cancellationToken).ConfigureAwait(false);

                        continue;
                    }

                    break;
                }
            }

            Report(engine.Id, index, last?.Message ?? "전송 실패");
        }

        throw new InvalidOperationException(
            $"페일오버 체인 {string.Join(" → ", EngineIds)} 이 전부 실패했다. "
            + "티어 브레이커가 T1 으로 내린다.",
            last);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // 스트리밍은 쓰지 않는다 — 플랜은 한 덩어리 JSON 이고 부분 결과에 의미가 없다.
        // 체인 첫 엔진에 그대로 넘긴다.
        return _chain[0].Client.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this)
            ? this
            : _chain[0].Client.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (FailoverEngine engine in _chain)
        {
            engine.Client.Dispose();
        }
    }

    /// <summary>
    /// 이 예외가 <b>전송</b> 실패인가.
    ///
    /// 전송 실패면 다음 엔진으로 간다. 아니면 그대로 던져 티어 라우터가 판단하게 한다.
    /// </summary>
    public static bool IsTransport(Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e is HttpRequestException or TaskCanceledException or TimeoutException
            or System.Net.Sockets.SocketException or IOException)
        {
            return true;
        }

        // 제공사 SDK 는 상태 코드를 메시지에 넣어 던지는 일이 많다.
        // 400 은 우리 요청이 틀린 것이므로 페일오버하지 않는다.
        string message = e.Message;

        return RetryPolicy.IsRateLimited(message)
            || message.Contains("500", StringComparison.Ordinal)
            || message.Contains("502", StringComparison.Ordinal)
            || message.Contains("503", StringComparison.Ordinal)
            || message.Contains("504", StringComparison.Ordinal)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>같은 엔진에 한 번 더 던질 값이 있는가. 429 만 그렇다.</summary>
    private static bool IsWorthRetrying(Exception e) => RetryPolicy.IsRateLimited(e.Message);

    private void Report(string from, int index, string reason)
    {
        Failovers++;

        string? next = index + 1 < _chain.Count ? _chain[index + 1].Id : null;

        _onFailover?.Invoke(new FailoverEvent(from, next, reason));
    }
}
