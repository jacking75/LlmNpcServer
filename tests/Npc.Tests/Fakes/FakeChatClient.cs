using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Npc.Tests.Fakes;

/// <summary>
/// LLM 없이 컴파일러 경로를 도는 가짜 클라이언트.
/// 응답 본문을 통째로 지정하거나 예외를 던지게 할 수 있다 — 호출 실패 경로도 테스트해야 한다.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly Func<int, string> _reply;
    private readonly Exception? _throw;

    /// <summary>시도 번호(1부터)에 따라 다른 응답을 주는 클라이언트.</summary>
    public FakeChatClient(Func<int, string> reply) => _reply = reply;

    /// <summary>언제나 같은 응답을 주는 클라이언트.</summary>
    public FakeChatClient(string reply) => _reply = _ => reply;

    /// <summary>언제나 예외를 던지는 클라이언트.</summary>
    public FakeChatClient(Exception error)
    {
        _reply = _ => string.Empty;
        _throw = error;
    }

    /// <summary>호출 횟수.</summary>
    public int Calls { get; private set; }

    /// <summary>마지막 호출의 메시지.</summary>
    public IList<ChatMessage> LastMessages { get; private set; } = [];

    /// <summary>호출마다의 옵션.</summary>
    public List<ChatOptions?> Options { get; } = [];

    /// <summary>호출마다의 system 메시지 본문. 프리픽스가 흔들리지 않는지 보는 데 쓴다.</summary>
    public List<string> SystemPrompts { get; } = [];

    /// <summary>호출마다의 user 메시지 본문.</summary>
    public List<string> UserPrompts { get; } = [];

    /// <inheritdoc />
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastMessages = [.. messages];
        Options.Add(options);
        SystemPrompts.Add(LastMessages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty);
        UserPrompts.Add(LastMessages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty);

        if (_throw is not null)
        {
            throw _throw;
        }

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply(Calls)))
        {
            ModelId = "fake",
            Usage = new UsageDetails
            {
                InputTokenCount = 12_000,
                OutputTokenCount = 400,
                AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cached_tokens"] = 11_500 },
            },
        };

        return Task.FromResult(response);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);

        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
