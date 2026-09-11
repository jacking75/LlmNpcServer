using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Llm;

namespace Npc.Tests.Llm;

/// <summary>
/// C-08 — 로컬 추론 프로세스 감독.
///
/// <para>
/// <b>죽었다는 것을 요청 타임아웃으로 알면 늦다.</b> 요청당 5초짜리 지연이 워커 수만큼
/// 쌓이는 동안 재계획 큐는 계속 차고, 그 사이 아무 로그도 "사이드카가 죽었다" 를 말하지 않는다.
/// </para>
///
/// <para>
/// <b>진짜 프로세스를 죽이지 않는다.</b> 읽기 동작을 주입받으므로 가짜로 확인한다 —
/// "죽으면 어떻게 되나" 를 보려고 dotLLM 을 띄웠다 내리는 테스트는 CI 에서 돌 수 없다.
/// </para>
/// </summary>
public sealed class LocalEngineProbeTests
{
    /// <summary>첫 확인 전에는 살아 있다고 본다. <b>죽었다는 증거가 없다.</b></summary>
    [Fact]
    public void Probe_StartsHealthy()
    {
        var probe = new LocalEngineProbe("local", _ => Task.FromResult(Up()));

        Assert.True(probe.Healthy);
        Assert.Equal(0, probe.Checks);
        Assert.Contains("아직", probe.LastDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>전이에서만 알린다.</b> 매 확인마다 부르면 30초마다 경보가 오고,
    /// 그러면 사람이 그 경보를 음소거한다.
    /// </summary>
    [Fact]
    public async Task Probe_RaisesOnceOnEachTransition()
    {
        bool alive = true;
        var changes = new List<(bool Healthy, string Detail)>();

        var probe = new LocalEngineProbe(
            "local",
            _ => Task.FromResult(alive ? Up() : ProbeReading.Down("connection refused")))
        {
            OnChange = (healthy, detail) => changes.Add((healthy, detail)),
        };

        // 살아 있는 동안은 조용하다.
        await probe.CheckAsync();
        await probe.CheckAsync();

        Assert.Empty(changes);
        Assert.True(probe.Healthy);

        // 죽는다 — 한 번만 알린다.
        alive = false;

        await probe.CheckAsync();
        await probe.CheckAsync();

        Assert.Single(changes);
        Assert.False(changes[0].Healthy);
        Assert.Contains("응답하지 않는다", changes[0].Detail, StringComparison.Ordinal);
        Assert.False(probe.Healthy);
        Assert.Equal(1, probe.Outages);
        Assert.Equal(2, probe.Failures);

        // 돌아온다 — 다시 한 번.
        alive = true;

        await probe.CheckAsync();
        await probe.CheckAsync();

        Assert.Equal(2, changes.Count);
        Assert.True(changes[1].Healthy);
        Assert.True(probe.Healthy);
        Assert.Equal(1, probe.Outages);
    }

    /// <summary>예외를 밖으로 던지지 않는다. <b>감시가 프로세스를 죽이면 그것이 장애다.</b></summary>
    [Fact]
    public async Task Probe_SwallowsTransportErrors()
    {
        var probe = new LocalEngineProbe(
            "local", _ => throw new HttpRequestException("no route to host"));

        Assert.False(await probe.CheckAsync());
        Assert.Equal(1, probe.Failures);
        Assert.Contains("no route", probe.LastDetail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>모델 해시 불일치는 경고이지 차단이 아니다.</b> 막으면 사람이 이 검사를 꺼 버린다.
    /// 그리고 <b>한 번만</b> 알린다 — 30초마다 같은 경보가 오면 음소거된다.
    /// </summary>
    [Fact]
    public async Task Probe_WarnsOnceOnModelHashMismatch()
    {
        var changes = new List<string>();

        var probe = new LocalEngineProbe(
            "local",
            _ => Task.FromResult(new ProbeReading(true, "qwen3-8b-q4km-deadbeef", "ok")))
        {
            ExpectedModelSha256 = "cafef00d",
            OnChange = (_, detail) => changes.Add(detail),
        };

        Assert.True(await probe.CheckAsync());
        Assert.True(await probe.CheckAsync());

        Assert.True(probe.HashMismatch);
        Assert.Single(changes);
        Assert.Contains("모델이 기대와 다르다", changes[0], StringComparison.Ordinal);

        // 여전히 살아 있다 — 해시가 달라도 T1 은 돈다.
        Assert.True(probe.Healthy);
    }

    /// <summary>해시가 맞으면 아무 말도 하지 않는다.</summary>
    [Fact]
    public async Task Probe_StaysQuietWhenTheHashMatches()
    {
        var changes = new List<string>();

        var probe = new LocalEngineProbe(
            "local",
            _ => Task.FromResult(new ProbeReading(true, "qwen3-8b-q4km-cafef00d", "ok")))
        {
            ExpectedModelSha256 = "CAFEF00D",
            OnChange = (_, detail) => changes.Add(detail),
        };

        await probe.CheckAsync();

        Assert.False(probe.HashMismatch);
        Assert.Empty(changes);
    }

    /// <summary>
    /// <b>200 을 주면서 빈 목록을 내는 서버는 죽은 것이다.</b> 그 상태로는 어떤 요청도
    /// 성공하지 않는데, 헬스만 보면 살아 있는 것으로 읽힌다.
    /// </summary>
    [Fact]
    public void Probe_TreatsAnEmptyModelListAsDown()
    {
        Assert.False(Read("""{"data":[]}""").Ok);
        Assert.False(Read("""{"object":"list"}""").Ok);
        Assert.False(Read("""[]""").Ok);

        ProbeReading ok = Read("""{"data":[{"id":"qwen3-8b"}]}""");

        Assert.True(ok.Ok);
        Assert.Equal("qwen3-8b", ok.ModelId);
        Assert.Contains("모델 1종", ok.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>프로브가 죽었다고 하면 T1 요청이 T2 로 간다</b> (C-08 완료 조건).
    /// 킬스위치와 다른 길이다 — 사람이 끊은 것이 아니라 프로세스가 죽은 것이다.
    /// </summary>
    [Fact]
    public void Router_SpillsToT2WhileTheLocalEngineIsDown()
    {
        bool healthy = true;

        var router = new TieredPlanCompiler(
            new StubCompiler(), new StubCompiler(), new NoBudget(), () => new Tick(1))
        {
            LocalHealthy = () => healthy,
        };

        var individual = new PlanRequest
        {
            Bucket = default,
            Quality = PlanQuality.Individual,
            Flags = WorldFlags.None,
        };

        Assert.Equal(Tier.T1, router.SelectTier(in individual));
        Assert.Equal(0, router.LocalOutageSpillovers);

        healthy = false;

        Assert.Equal(Tier.T2, router.SelectTier(in individual));
        Assert.Equal(1, router.LocalOutageSpillovers);

        // 돌아오면 T1 으로 되돌아간다.
        healthy = true;

        Assert.Equal(Tier.T1, router.SelectTier(in individual));
    }

    /// <summary>
    /// <b>강제 개방은 연속 실패 계수를 건드리지 않는다.</b> 그 숫자는 "호출이 몇 번 연달아
    /// 실패했나" 이고, 프로브가 올리면 브레이커의 자기 계측이 오염된다.
    /// </summary>
    [Fact]
    public void Breaker_ForceOpenDoesNotTouchFailureCount()
    {
        var breaker = new CircuitBreaker(failureThreshold: 3, cooldownSeconds: 10);

        Assert.True(breaker.ForceOpen(new Tick(100)));
        Assert.Equal(0, breaker.ConsecutiveFailures);
        Assert.Equal(1, breaker.Opens);
        Assert.False(breaker.TryEnter(new Tick(101)));

        // 이미 열려 있으면 타이머를 밀지 않는다 — 밀면 프로브가 30초마다 부를 때
        // 차단이 영원히 안 풀린다.
        Assert.False(breaker.ForceOpen(new Tick(150)));
        Assert.Equal(1, breaker.Opens);

        // 쿨다운이 지나면 반개방이다.
        Assert.True(breaker.TryEnter(new Tick(100 + (10 * Tick.PerSecond))));
    }

    private static ProbeReading Up() => new(true, "qwen3-8b", "모델 1종");

    private static ProbeReading Read(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        return LocalEngineProbe.Read(document.RootElement);
    }

    /// <summary>아무것도 하지 않는 컴파일러. 라우팅 판정만 보므로 호출되지 않는다.</summary>
    private sealed class StubCompiler : IPlanCompiler
    {
        public ValueTask<PlanCompileResult> CompileAsync(
            PlanRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("라우팅 판정만 보는 테스트다.");
    }

    /// <summary>언제나 허락하는 예산. 티어 선택만 보는 테스트라 예산은 관심 밖이다.</summary>
    private sealed class NoBudget : IReplanBudget
    {
        public bool TryAcquire(Tier tier, int estimatedTokens, Tick now) => true;

        public Tier Acquire(Tier requested, int estimatedTokens, Tick now) => requested;

        public bool Peek(Tier tier, int estimatedTokens, Tick now) => true;

        public double EstimateCost(Tier tier, int tokens) => 0;

        public void Settle(Tier tier, int actualTokens, double actualCostUsd)
        {
            // 정산하지 않는다.
        }
    }
}
