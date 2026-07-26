using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Npc.Core;
using Npc.Llm;
using Npc.MasterData;
using Npc.Prebake;

namespace Npc.Tests.Prebake;

/// <summary>프리픽스 워밍업. docs/13 §4 · §8 · T3-11.</summary>
public sealed class WarmupTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);
    private static readonly PromptPrefix s_prefix = PromptPrefix.Build(s_data, TestPaths.MasterData);

    private static readonly LlmEngineOptions s_external = new(
        "test-external",
        LlmEngineKind.External,
        "test-model",
        "https://example.invalid/v1",
        InputUsdPerMTok: 0.10,
        CachedInputUsdPerMTok: 0.01,
        OutputUsdPerMTok: 0.40);

    private static readonly LlmEngineOptions s_local = s_external with
    {
        Id = "test-local",
        Kind = LlmEngineKind.LlamaCpp,
        InputUsdPerMTok = 0,
        CachedInputUsdPerMTok = 0,
        OutputUsdPerMTok = 0,
    };

    private static BucketKey Bucket()
    {
        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));

        return new BucketKey(smith.Code, TimeOfDay.Dawn, RegionState.Peace, Climate.Fair);
    }

    /// <summary>
    /// T3-11 완료 조건 — 첫 요청이 <b>완료된 뒤에야</b> 워커가 시작한다.
    ///
    /// 순서가 뒤집히면 동시 N 개가 전부 cache miss 로 시작해 write 할증을 N 번 낸다.
    /// </summary>
    [Fact]
    public async Task Prebake_WarmupBeforeConcurrency()
    {
        var client = new ConcurrencyProbeClient(delayMs: 40);

        (WarmupResult warmup, int workerCalls) = await Warmup.RunThenAsync(
            ct => Warmup.RunAsync(client, s_external, s_prefix, s_data, Bucket(), settleDelayMs: 0, ct),
            async ct =>
            {
                // 워커 8개를 동시에 던진다.
                await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.CallAsync(ct)));

                return 8;
            });

        Assert.True(warmup.Attempted);
        Assert.True(warmup.Succeeded);
        Assert.Equal(8, workerCalls);
        Assert.Equal(9, client.Total);

        // 첫 요청 하나만 도는 동안 관측된 동시성이 1 이어야 한다.
        Assert.Equal(1, client.ConcurrencyDuringFirstCall);

        // 워커 단계에서는 실제로 겹쳤다 — 워밍업이 전체를 직렬화해 버리지 않았다.
        Assert.True(client.PeakConcurrency > 1, $"워커가 겹치지 않았다 (peak {client.PeakConcurrency}).");

        // 첫 요청이 끝난 시점이 두 번째 요청이 시작된 시점보다 앞이다.
        Assert.True(
            client.FirstCallEndOrder < client.SecondCallStartOrder,
            $"워밍업 완료({client.FirstCallEndOrder})가 다음 요청 시작({client.SecondCallStartOrder})보다 늦다.");
    }

    /// <summary>워밍업 요청은 워커가 보낼 것과 같은 메시지 모양이어야 한다 — 프리픽스가 흔들리면 캐시가 깨진다.</summary>
    [Fact]
    public async Task Warmup_SendsSamePrefixAsWorkers()
    {
        var client = new ConcurrencyProbeClient();

        WarmupResult result = await Warmup.RunAsync(
            client, s_external, s_prefix, s_data, Bucket(), settleDelayMs: 0);

        Assert.True(result.Succeeded);
        Assert.Equal(s_prefix.Text, Assert.Single(client.SystemPrompts));

        // 서픽스는 실제 대상 버킷의 것이다.
        string expected = PlanRequestSuffix.Build(
            new PlanRequest(Bucket(), s_data.InitialFlags(Bucket())), s_data);

        Assert.Equal(expected, Assert.Single(client.UserPrompts));
    }

    /// <summary>
    /// 로컬 티어에서는 던지지 않는다. dotLLM 은 <c>cached_tokens</c> 를 항상 0 으로 보고해
    /// 이 실험 자체가 성립하지 않는다 (W1_env.md §4.4).
    /// </summary>
    [Fact]
    public async Task Warmup_SkipsLocalEngines()
    {
        var client = new ConcurrencyProbeClient();

        WarmupResult result = await Warmup.RunAsync(
            client, s_local, s_prefix, s_data, Bucket(), settleDelayMs: 0);

        Assert.False(result.Attempted);
        Assert.False(result.Succeeded);
        Assert.Equal(0, client.Total);
        Assert.Equal(WarmupResult.Skipped, result);
    }

    /// <summary>워밍업이 실패해도 회차를 멈추지 않는다 — 비용이 조금 더 드는 일일 뿐이다.</summary>
    [Fact]
    public async Task Warmup_FailureDoesNotThrow()
    {
        var client = new ThrowingClient(new HttpRequestException("429 Too Many Requests"));

        WarmupResult result = await Warmup.RunAsync(
            client, s_external, s_prefix, s_data, Bucket(), settleDelayMs: 0);

        Assert.True(result.Attempted);
        Assert.False(result.Succeeded);
        Assert.Contains("429", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>비용을 엔진 단가로 계산한다. 워밍업 요청은 캐시 적중이 없는 것이 정상이다.</summary>
    [Fact]
    public async Task Warmup_ReportsCost()
    {
        var client = new ConcurrencyProbeClient { PromptTokens = 12_000, CachedTokens = 0, CompletionTokens = 400 };

        WarmupResult result = await Warmup.RunAsync(
            client, s_external, s_prefix, s_data, Bucket(), settleDelayMs: 0);

        Assert.Equal(12_000, result.PromptTokens);
        Assert.Equal(0, result.CachedTokens);

        // (12,000 × 0.10 + 0 + 400 × 0.40) / 1e6
        Assert.Equal(((12_000 * 0.10) + (400 * 0.40)) / 1_000_000d, result.CostUsd, 12);
    }

    /// <summary>동시성을 관측하는 가짜 클라이언트. 스레드 안전하다.</summary>
    private sealed class ConcurrencyProbeClient(int delayMs = 0) : IChatClient
    {
        private readonly Lock _gate = new();
        private readonly List<string> _system = [];
        private readonly List<string> _user = [];
        private int _inFlight;
        private int _peak;
        private int _order;
        private int _total;
        private int _concurrencyDuringFirst;
        private int _firstEnd = int.MaxValue;
        private int _secondStart = int.MaxValue;

        public long PromptTokens { get; init; } = 12_000;

        public long CachedTokens { get; init; } = 11_500;

        public long CompletionTokens { get; init; } = 400;

        public int Total
        {
            get
            {
                lock (_gate)
                {
                    return _total;
                }
            }
        }

        public int PeakConcurrency
        {
            get
            {
                lock (_gate)
                {
                    return _peak;
                }
            }
        }

        public int ConcurrencyDuringFirstCall
        {
            get
            {
                lock (_gate)
                {
                    return _concurrencyDuringFirst;
                }
            }
        }

        public int FirstCallEndOrder
        {
            get
            {
                lock (_gate)
                {
                    return _firstEnd;
                }
            }
        }

        public int SecondCallStartOrder
        {
            get
            {
                lock (_gate)
                {
                    return _secondStart;
                }
            }
        }

        public ImmutableArray<string> SystemPrompts
        {
            get
            {
                lock (_gate)
                {
                    return [.. _system];
                }
            }
        }

        public ImmutableArray<string> UserPrompts
        {
            get
            {
                lock (_gate)
                {
                    return [.. _user];
                }
            }
        }

        public Task<ChatResponse> CallAsync(CancellationToken ct) =>
            GetResponseAsync([new ChatMessage(ChatRole.User, "worker")], null, ct);

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            int index;

            lock (_gate)
            {
                index = _total++;
                _system.Add(list.FirstOrDefault(m => m.Role == ChatRole.System)?.Text ?? string.Empty);
                _user.Add(list.FirstOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty);

                _inFlight++;
                _peak = Math.Max(_peak, _inFlight);

                int order = _order++;

                if (index == 0)
                {
                    _concurrencyDuringFirst = _inFlight;
                }
                else if (index == 1)
                {
                    _secondStart = order;
                }
            }

            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }

            lock (_gate)
            {
                if (index == 0)
                {
                    // 첫 요청이 도는 동안 다른 요청이 들어왔다면 여기서 드러난다.
                    _concurrencyDuringFirst = Math.Max(_concurrencyDuringFirst, _inFlight);
                    _firstEnd = _order;
                }

                _order++;
                _inFlight--;
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"))
            {
                ModelId = "probe",
                Usage = new UsageDetails
                {
                    InputTokenCount = PromptTokens,
                    OutputTokenCount = CompletionTokens,
                    AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cached_tokens"] = CachedTokens },
                },
            };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);

            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>언제나 던지는 클라이언트.</summary>
    private sealed class ThrowingClient(Exception error) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw error;

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw error;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
