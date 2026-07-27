using System.Globalization;
using System.Threading.Channels;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Validation;
using Npc.Host;
using Npc.Host.Metrics;
using Npc.Llm;
using Npc.Prebake;
using Npc.Tests.Runtime;

namespace Npc.Tests.FaultInjection;

/// <summary>
/// T5-10 — 링크 장애 주입 5항목. docs/15 §4.
///
/// <code>
/// [x] 링크 단절(Faulted) → 명령 축적 → 복구 시 재개
/// [x] 이벤트 시퀀스 갭 주입 → 경보 발생, 동작 유지
/// [x] 명령 드롭율 5% → 타임아웃 합성으로 진행 유지
/// [x] 외부 API 429 폭주 → AIMD 감속, 크래시 없음
/// [x] 외부 API 타임아웃 30초 → 서킷 브레이커 개방
/// </code>
///
/// 앞의 셋은 게임서버 링크, 뒤의 둘은 LLM 쪽이다. 한 파일에 둔 것은 docs/15 §4 가
/// 한 목록으로 적고 있어서다 — 다섯 다 "장애가 나도 멈추지 않는가" 하나를 본다.
/// </summary>
[Trait("Category", "FaultInjection")]
[Collection(AllocationCollection.Name)]
public sealed class LinkFaultTests
{
    // ================================================================ 1. 링크 단절

    /// <summary>
    /// 단절 중에는 명령이 <b>쌓이고</b>, 복구하면 <b>순서대로 전부</b> 나간다.
    ///
    /// <c>LoopbackGameServerLink</c> 는 인프로세스라 언제나 Connected 다 —
    /// <see cref="LinkState.Faulted"/> 경로는 실제 네트워크 구현(<c>TcpGameServerLink</c>)의 것이고
    /// 아직 없다. 그래서 계약(<see cref="IGameServerLink"/>)을 만족하는 최소 구현으로 확인한다.
    /// </summary>
    [Fact]
    public async Task Link_FaultedAccumulatesCommandsAndResumesOnRecovery()
    {
        await using var link = new FaultableLink();

        var states = new List<LinkState>();
        link.StateChanged += states.Add;

        Enqueue(link, 1, 2, 3);
        await link.FlushAsync(CancellationToken.None);

        Assert.Equal([1, 2, 3], link.Delivered.Select(c => (int)c.Correlation.Value));

        // ── 단절 ──
        link.Fault();

        Enqueue(link, 4, 5, 6);
        await link.FlushAsync(CancellationToken.None);

        Assert.Equal(3, link.Delivered.Count);          // 아무것도 나가지 않았다
        Assert.Equal(3, link.Pending);                  // 대신 쌓였다
        Assert.Equal(0, link.Stats.CommandsDropped);    // 버리지도 않았다

        // ── 복구 ──
        link.Recover();

        await link.FlushAsync(CancellationToken.None);

        // 순서가 보존돼야 한다 — 상관 ID 가 뒤섞이면 응답 대조가 깨진다 (N5).
        Assert.Equal([1, 2, 3, 4, 5, 6], link.Delivered.Select(c => (int)c.Correlation.Value));
        Assert.Equal(0, link.Pending);
        Assert.Equal([LinkState.Faulted, LinkState.Connected], states);
    }

    // ================================================================ 2. 이벤트 시퀀스 갭

    /// <summary>
    /// 시퀀스가 끊기면 <b>경보만 올리고 계속 돈다.</b> N6.
    /// 갭에서 멈추면 게임서버가 이벤트 하나를 흘렸을 때 NPC 전체가 정지한다.
    /// </summary>
    [Fact]
    public async Task Link_SequenceGapRaisesAlarmAndKeepsRunning()
    {
        string trace = Path.Combine(Path.GetTempPath(), $"npc-gap-{Guid.NewGuid():N}.jsonl");

        try
        {
            // 정상 세션을 기록한 뒤, 그 기록에서 이벤트 한 줄을 도려내 갭을 만든다.
            await using (NpcHost recorder = Host("--link", "record", "--trace", trace))
            {
                await recorder.RunAsync(CancellationToken.None);
            }

            string[] lines = await File.ReadAllLinesAsync(trace);
            int victim = Array.FindIndex(lines, 40, l => l.StartsWith("{\"Kind\":1,", StringComparison.Ordinal));

            Assert.True(victim > 0, "기록에 도려낼 이벤트가 없다.");

            await File.WriteAllLinesAsync(trace, lines.Where((_, i) => i != victim));

            await using NpcHost host = Host("--link", "replay", "--trace", trace);

            await host.RunAsync(CancellationToken.None);

            MetricsSnapshot metrics = host.Metrics.Snapshot();

            // 경보는 올라야 하고,
            Assert.True(metrics.Link.EventGaps > 0, "갭을 만들었는데 검출되지 않았다.");

            // 동작은 유지돼야 한다.
            Assert.True(metrics.Tick.Ticks > 0);
            Assert.Equal(0, metrics.Tick.Overruns);
            Assert.True(host.Snapshot().StepsAdvanced > 0);
        }
        finally
        {
            File.Delete(trace);
        }
    }

    // ================================================================ 3. 명령 드롭율 5%

    /// <summary>
    /// 명령이 조용히 사라져도 타임아웃 합성으로 진행이 재개된다. docs/03 §6.
    /// <b>이 경로가 깨지면 NPC 가 영원히 멈춘다</b> — 평소에는 아무 일도 안 일어나므로
    /// 일부러 깨뜨려야 보인다 (CLAUDE.md §2.2).
    /// </summary>
    [Fact]
    public async Task Link_FivePercentCommandDropStillMakesProgress()
    {
        await using NpcHost clean = Host("--loopback");
        await using NpcHost lossy = Host("--loopback", "--drop-rate", "0.05");

        await clean.RunAsync(CancellationToken.None);
        await lossy.RunAsync(CancellationToken.None);

        HostSnapshot healthy = clean.Snapshot();
        HostSnapshot dropped = lossy.Snapshot();

        // 드롭이 있으면 타임아웃 합성이 그만큼 더 나와야 한다.
        Assert.True(
            dropped.TimeoutsSynthesized > healthy.TimeoutsSynthesized,
            Compare(healthy, dropped));

        // 그럼에도 스텝은 계속 전진한다 — 여기가 이 항목의 전부다.
        Assert.True(dropped.StepsAdvanced > 0, Compare(healthy, dropped));
        Assert.Equal(0, lossy.Metrics.Snapshot().Tick.Overruns);
    }

    // ================================================================ 4. 429 폭주 → AIMD

    /// <summary>
    /// 429 가 쏟아져도 동시성을 줄이며 버틴다. 던지지 않는다. docs/13 §4.
    /// </summary>
    [Fact]
    public void External_RateLimitStormBacksOffWithoutCrashing()
    {
        var aimd = new AdaptiveConcurrency(start: 32, min: 1, max: 64);

        Assert.Equal(32, aimd.Current);

        // 폭주 — 100회 연속 429.
        for (int i = 0; i < 100; i++)
        {
            aimd.OnThrottled();
        }

        Assert.Equal(aimd.Min, aimd.Current);
        Assert.Equal(100, aimd.ThrottleCount);

        // 최초 429 가 난 동시성을 기억한다 — 다음 회차의 출발점이 여기서 나온다.
        Assert.Equal(32, aimd.FirstThrottleConcurrency);
        Assert.True(aimd.RecommendedStart <= 32);

        // 429 를 실제로 429 로 알아본다.
        Assert.True(RetryPolicy.IsRateLimited("HTTP 429: Too Many Requests"));
        Assert.True(RetryPolicy.IsRateLimited("rate limit exceeded"));
        Assert.False(RetryPolicy.IsRateLimited("HTTP 500"));

        // 백오프는 시도마다 늘고 상한에서 멈춘다 — 무한히 자라면 프리베이크가 안 끝난다.
        int first = RetryPolicy.DelayMs(bucketIndex: 7, attempt: 1);
        int second = RetryPolicy.DelayMs(bucketIndex: 7, attempt: 2);

        Assert.True(second > first, $"{first}ms → {second}ms");
        Assert.True(RetryPolicy.DelayMs(bucketIndex: 7, attempt: 10) <= RetryPolicy.MaxDelayMs);

        // 회복 — 연속 성공이 쌓이면 다시 올라간다 (AIMD 의 AI).
        for (int i = 0; i < AdaptiveConcurrency.DefaultSuccessStreak * 3; i++)
        {
            aimd.OnSuccess();
        }

        Assert.True(aimd.Current > aimd.Min, aimd.ToString());
    }

    // ================================================================ 5. 타임아웃 → 서킷 브레이커

    /// <summary>
    /// T2 가 계속 타임아웃하면 서킷이 열리고, T2 를 <b>시도조차 하지 않고</b> T1 으로 간다.
    /// docs/14 §10 — 무한 재시도는 외부 장애 시 지연을 폭발시킨다.
    /// </summary>
    [Fact]
    public async Task External_TimeoutStormOpensTheCircuitAndFailsOver()
    {
        // 시계는 Tick 이다 (CLAUDE.md §2.3). 10Hz 이므로 30초 = 300틱 · 60초 = 600틱.
        long tick = 0;
        var breaker = new CircuitBreaker(
            failureThreshold: CircuitBreaker.DefaultFailureThreshold,
            cooldownSeconds: CircuitBreaker.DefaultCooldownSeconds);

        var t2 = new TimingOutCompiler();
        var t1 = new AlwaysOkCompiler();

        var router = new TieredPlanCompiler(t1, t2, new OpenBudget(), () => new Tick(tick))
        {
            Breaker = breaker,
        };

        // 30초 타임아웃이 연달아 난다. 임계(5회)에서 열려야 한다.
        for (int i = 0; i < CircuitBreaker.DefaultFailureThreshold; i++)
        {
            Assert.Equal(CircuitState.Closed, breaker.StateAt(new Tick(tick)));

            await router.CompileAsync(Request(), CancellationToken.None);

            tick += 300;
        }

        Assert.Equal(CircuitState.Open, breaker.StateAt(new Tick(tick)));
        Assert.Equal(1, breaker.Opens);
        Assert.Equal(CircuitBreaker.DefaultFailureThreshold, t2.Calls);

        // 열린 동안에는 T2 를 부르지 않는다 — 이게 지연 폭발을 막는 지점이다.
        int before = t2.Calls;

        await router.CompileAsync(Request(), CancellationToken.None);

        Assert.Equal(before, t2.Calls);
        Assert.Equal(1, breaker.ShortCircuits);
        Assert.True(t1.Calls > 0);

        // 60초가 지나면 반개방 — 시험 호출 하나만 나간다.
        tick += 600;

        Assert.Equal(CircuitState.HalfOpen, breaker.StateAt(new Tick(tick)));

        await router.CompileAsync(Request(), CancellationToken.None);

        Assert.Equal(before + 1, t2.Calls);
        Assert.Equal(1, breaker.Probes);
        Assert.Equal(CircuitState.Open, breaker.StateAt(new Tick(tick)));

        // 그 시험 호출이 성공하면 닫힌다.
        tick += 600;
        t2.Recover();

        await router.CompileAsync(Request(), CancellationToken.None);

        Assert.Equal(CircuitState.Closed, breaker.StateAt(new Tick(tick)));
    }

    // ================================================================ 헬퍼

    private static PlanRequest Request() =>
        new(new BucketKey(default, TimeOfDay.Morning, RegionState.Peace, Climate.Fair), WorldFlags.None);

    private static NpcHost Host(params string[] extra)
    {
        string[] args =
        [
            .. extra,
            "--npcs", "100", "--time-scale", "600", "--days", "1", "--max-speed", "--no-dashboard",
        ];

        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return NpcHost.Create(options with { MasterData = TestPaths.MasterData }, TextWriter.Null);
    }

    private static void Enqueue(IGameServerLink link, params int[] correlations)
    {
        foreach (int correlation in correlations)
        {
            var command = new NpcCommand
            {
                Kind = NpcCommandKind.MoveTo,
                Npc = new NpcId(1),
                IssuedAt = new Tick(correlation),
                Correlation = new CorrelationId((uint)correlation),
                Priority = CommandPriority.Normal,
            };

            link.Enqueue(in command);
        }
    }

    private static string Compare(in HostSnapshot healthy, in HostSnapshot dropped) => string.Create(
        CultureInfo.InvariantCulture,
        $"무손실: 스텝 {healthy.StepsAdvanced} · 타임아웃 {healthy.TimeoutsSynthesized} / "
        + $"드롭 5%: 스텝 {dropped.StepsAdvanced} · 타임아웃 {dropped.TimeoutsSynthesized}");

    /// <summary>
    /// 끊었다 붙일 수 있는 링크. <c>Faulted</c> 동안 명령을 쌓아 두고 복구 시 순서대로 내보낸다.
    /// 실제 네트워크 구현이 지켜야 할 성질을 계약 수준에서 고정한다 (docs/02 §1 N8).
    /// </summary>
    private sealed class FaultableLink : IGameServerLink
    {
        private readonly Queue<NpcCommand> _queue = new();
        private readonly Channel<GameEvent> _events = Channel.CreateUnbounded<GameEvent>();
        private long _enqueued;
        private long _flushed;

        public List<NpcCommand> Delivered { get; } = [];

        public int Pending => _queue.Count;

        public LinkState State { get; private set; } = LinkState.Connected;

        public ChannelReader<GameEvent> Events => _events.Reader;

        public LinkStats Stats => new(_enqueued, _flushed, 0, 0, 0, _queue.Count);

        public event Action<LinkState>? StateChanged;

        public void Fault() => Transition(LinkState.Faulted);

        public void Recover() => Transition(LinkState.Connected);

        public void Enqueue(in NpcCommand command)
        {
            _enqueued++;
            _queue.Enqueue(command);
        }

        public ValueTask FlushAsync(CancellationToken ct)
        {
            // 단절 중에는 내보내지 않는다. 버리지도 않는다 — 쌓아 둔다.
            if (State == LinkState.Faulted)
            {
                return ValueTask.CompletedTask;
            }

            while (_queue.Count > 0)
            {
                Delivered.Add(_queue.Dequeue());
                _flushed++;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private void Transition(LinkState next)
        {
            if (State == next)
            {
                return;
            }

            State = next;
            StateChanged?.Invoke(next);
        }
    }

    /// <summary>30초 뒤 타임아웃하는 T2. 실제로 기다리지는 않는다.</summary>
    private sealed class TimingOutCompiler : IPlanCompiler
    {
        private bool _healthy;

        public int Calls { get; private set; }

        public void Recover() => _healthy = true;

        public ValueTask<PlanCompileResult> CompileAsync(PlanRequest request, CancellationToken cancellationToken)
        {
            Calls++;

            if (_healthy)
            {
                return ValueTask.FromResult(new PlanCompileResult(
                    null, ValidationResult.Ok, CompileStats.None with { Model = "t2" }, "t2"));
            }

            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.");
        }
    }

    /// <summary>예산을 무한으로 열어 둔 판정기. 여기서 볼 것은 브레이커이지 예산이 아니다.</summary>
    private sealed class OpenBudget : IReplanBudget
    {
        public Tier Acquire(Tier requested, int estimatedTokens, Tick now) => requested;

        public bool TryAcquire(Tier tier, int estimatedTokens, Tick now) => tier != Tier.None;

        public bool Peek(Tier tier, int estimatedTokens, Tick now) => tier != Tier.None;

        public double EstimateCost(Tier tier, int tokens) => 0;

        public void Settle(Tier tier, int actualTokens, double actualCostUsd)
        {
        }
    }

    private sealed class AlwaysOkCompiler : IPlanCompiler
    {
        public int Calls { get; private set; }

        public ValueTask<PlanCompileResult> CompileAsync(PlanRequest request, CancellationToken cancellationToken)
        {
            Calls++;

            return ValueTask.FromResult(new PlanCompileResult(
                null, ValidationResult.Ok, CompileStats.None with { Model = "t1" }, "t1"));
        }
    }
}
