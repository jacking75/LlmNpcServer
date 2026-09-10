using Microsoft.Extensions.AI;
using Npc.Contracts;
using Npc.Llm;

namespace Npc.Tests.Llm;

/// <summary>C-01 — 제공사 페일오버 체인. PRODUCTION_ROADMAP §6 C-01.</summary>
public sealed class FailoverChatClientTests
{
    [Fact]
    public async Task FirstEngineFails_SecondAnswers()
    {
        var a = new StubClient(new HttpRequestException("HTTP 429: Too Many Requests"));
        var b = new StubClient("ok-b");
        var failovers = new List<FailoverEvent>();

        using var chain = Chain(failovers, ("a", a), ("b", b));

        ChatResponse response = await chain.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("ok-b", response.Text);
        Assert.Equal("b", chain.LastEngineId);
        Assert.Equal(1, chain.Failovers);
        Assert.Equal("b", failovers[0].To);
    }

    [Fact]
    public async Task BadRequest_DoesNotFailOver()
    {
        // 400·스키마 거절은 모델 품질 문제다. 다른 제공사에 보내도 같은 프롬프트라
        // 같은 결과가 나올 가능성이 높고, 그동안 비용만 두 배가 된다.
        var a = new StubClient(new InvalidOperationException("HTTP 400: invalid schema"));
        var b = new StubClient("ok-b");
        var failovers = new List<FailoverEvent>();

        using var chain = Chain(failovers, ("a", a), ("b", b));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chain.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(0, b.Calls);
        Assert.Empty(failovers);
    }

    [Fact]
    public async Task EveryEngineFails_ThrowsForTheTierBreaker()
    {
        var a = new StubClient(new HttpRequestException("HTTP 503"));
        var b = new StubClient(new TimeoutException("timeout"));
        var failovers = new List<FailoverEvent>();

        using var chain = Chain(failovers, ("a", a), ("b", b));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => chain.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Contains("a → b", error.Message, StringComparison.Ordinal);

        // 마지막 페일오버는 갈 곳이 없다 — 그때가 티어 강등으로 가는 순간이다.
        Assert.Null(failovers[^1].To);
    }

    [Fact]
    public async Task BreakersAreIndependent()
    {
        var a = new StubClient(new HttpRequestException("HTTP 503"));
        var b = new StubClient("ok-b");
        var failovers = new List<FailoverEvent>();

        using var chain = Chain(failovers, ("a", a), ("b", b));

        // a 를 연속 실패시켜 브레이커를 연다.
        for (int i = 0; i < CircuitBreaker.DefaultFailureThreshold + 1; i++)
        {
            await chain.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        }

        int callsBefore = a.Calls;

        await chain.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        // 열린 브레이커는 시도조차 하지 않는다. 타임아웃까지 기다린 뒤 넘어가면
        // 재계획 하나가 (외부 타임아웃 + 로컬 5.1s) 를 먹는다.
        Assert.Equal(callsBefore, a.Calls);

        // b 는 멀쩡하다 — 브레이커는 엔진마다다.
        Assert.Equal("b", chain.LastEngineId);
    }

    [Fact]
    public async Task PromptIsNotConsumedByTheFirstEngine()
    {
        var a = new StubClient(new HttpRequestException("HTTP 503"));
        var b = new StubClient("ok-b");

        using var chain = Chain([], ("a", a), ("b", b));

        // 열거를 한 번만 해야 한다. 뒤 엔진이 이미 소비된 시퀀스를 받으면 빈 프롬프트를 보낸다.
        IEnumerable<ChatMessage> lazy = Lazy();

        await chain.GetResponseAsync(lazy);

        Assert.Equal(1, b.LastMessageCount);

        static IEnumerable<ChatMessage> Lazy()
        {
            yield return new ChatMessage(ChatRole.User, "hi");
        }
    }

    [Fact]
    public void EmptyChain_IsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() =>
            new FailoverChatClient([], () => default));
    }

    [Theory]
    [InlineData("HTTP 429: Too Many Requests", true)]
    [InlineData("rate limit exceeded", true)]
    [InlineData("HTTP 503 Service Unavailable", true)]
    [InlineData("HTTP 500", true)]
    [InlineData("request timeout", true)]
    [InlineData("HTTP 400: bad request", false)]
    [InlineData("schema validation failed", false)]
    public void TransportFailures_AreTheOnesWorthFailingOver(string message, bool expected)
    {
        Assert.Equal(expected, FailoverChatClient.IsTransport(new InvalidOperationException(message)));
    }

    [Fact]
    public void NetworkExceptions_AreAlwaysTransport()
    {
        Assert.True(FailoverChatClient.IsTransport(new HttpRequestException("무엇이든")));
        Assert.True(FailoverChatClient.IsTransport(new TimeoutException()));
        Assert.True(FailoverChatClient.IsTransport(new IOException()));
        Assert.True(FailoverChatClient.IsTransport(new System.Net.Sockets.SocketException()));
    }

    private static FailoverChatClient Chain(
        List<FailoverEvent> failovers, params (string Id, IChatClient Client)[] engines)
    {
        long tick = 0;

        return new FailoverChatClient(
            [.. engines.Select(e => new FailoverEngine(e.Id, e.Client, new CircuitBreaker()))],
            () => new Tick(++tick),
            failovers.Add,
            maxAttemptsPerEngine: 1);
    }

    /// <summary>고정 응답 또는 고정 예외를 내는 가짜 클라이언트.</summary>
    private sealed class StubClient : IChatClient
    {
        private readonly string? _answer;
        private readonly Exception? _error;

        public StubClient(string answer) => _answer = answer;

        public StubClient(Exception error) => _error = error;

        public int Calls { get; private set; }

        public int LastMessageCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastMessageCount = messages.Count();

            return _error is not null
                ? Task.FromException<ChatResponse>(_error)
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _answer!)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // 가짜다. 놓을 것이 없다.
        }
    }
}
