using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Llm;
using Npc.Memory;

namespace Npc.Host.Replan;

/// <summary>
/// 워커가 집어 든 재계획 일감 하나. docs/14 §4.
/// </summary>
/// <param name="Npc">개별 재계획이면 NPC 첨자. 아키타입 플랜이면 -1.</param>
/// <param name="Bucket">버킷 키. 요청 프롬프트의 서픽스가 여기서 나온다.</param>
/// <param name="Flags">일감을 집어 든 시점의 월드 플래그.</param>
/// <param name="Quality">아키타입 플랜인가 개체 플랜인가. <b>티어 선택의 입력이다</b>.</param>
/// <param name="Score">큐에서의 점수. 지연 통계와 재삽입에 쓴다.</param>
/// <param name="QueuedTick">
/// 큐에 들어간 틱. <b>-1 이면 모른다</b>(스냅샷이 없는 경로).
/// docs/14 §6 의 "큐 평균 대기 시간" 이 이 값의 차분이다.
/// </param>
/// <param name="GlobalNpc">
/// 이 NPC 의 전역 id (A-08). <b>기억 저장소의 키다</b> (D-03) — 슬롯은 우리 안쪽 사정이라
/// 프로세스를 다시 띄우면 같은 NPC 가 다른 슬롯에 앉는다. 0 이면 모른다.
/// </param>
/// <param name="Subject">
/// 이 NPC 가 가장 최근에 상대한 플레이어 (D-03). 0 = 없음.
/// 관계 밴드를 어느 쌍에서 읽을지 정한다.
/// </param>
internal readonly record struct ReplanJob(
    int Npc,
    BucketKey Bucket,
    WorldFlags Flags,
    PlanQuality Quality,
    float Score,
    long QueuedTick = -1,
    int GlobalNpc = 0,
    int Subject = 0);

/// <summary>
/// 워커의 일감 공급원. docs/14 §4.
///
/// <b>공급원이 둘이다</b> — 개별 재계획(<c>ReplanQueue</c>)과 아키타입 버킷 미스.
/// 전자는 T1(1회용·저지연), 후자는 T2(수천 NPC 재사용·고품질)로 간다. 각각 별도 워커 풀이다.
/// </summary>
internal interface IReplanSource
{
    /// <summary>로그·메트릭에 찍히는 이름.</summary>
    string Name { get; }

    /// <summary>대기 중인 일감 수.</summary>
    int Depth { get; }

    /// <summary>일감 하나를 꺼낸다. 없으면 false.</summary>
    bool TryTake(Tick now, out ReplanJob job);

    /// <summary>완성된 플랜을 반영한다. <b>틱 루프 상태를 직접 고치지 않는다</b> (CLAUDE.md §2.1).</summary>
    void Apply(in ReplanJob job, CompiledPlan plan, Tick now);

    /// <summary>플랜을 못 만들었다. 필요하면 되돌리거나 다시 넣는다.</summary>
    void Abandon(in ReplanJob job, Tick now);
}

/// <summary>
/// 재계획 워커 풀. docs/14 §4 · CLAUDE.md §2.1.
///
/// <b>틱 루프 밖에서 돈다.</b> <see cref="BackgroundService"/> 이고, 완성된 플랜은
/// <see cref="IReplanSource.Apply"/> 를 통해 <c>Volatile.Write</c> 로만 건넨다 —
/// 워커가 런타임 상태를 직접 고치면 데이터 레이스와 비결정성이 생긴다 (CLAUDE.md §7).
///
/// <para><b>이 클래스가 <c>Npc.Planning</c> 이 아니라 <c>Npc.Host</c> 에 있는 이유.</b>
/// <c>docs/14 §4</c> 는 <c>Npc.Planning/ReplanWorker.cs</c> 라고 적었지만, 워커는
/// <see cref="IPlanCompiler"/>(<c>Npc.Llm</c>)와 <c>NpcStore</c>(<c>Npc.Runtime</c>)를 둘 다 필요로 한다.
/// <c>Npc.Planning</c> 에 두면 <c>Npc.Runtime → Npc.Planning → Npc.Llm</c> 경로가 생겨
/// <b>"틱 루프에 LLM 이 들어올 길"</b> 이 열린다 (CLAUDE.md §3). 조립 루트가 유일하게 전부를 아는 곳이다.</para>
///
/// <b>워커 수는 설정값이다.</b> T1 기본 2 · T2 기본 8. W1 실측에서 로컬은 동시 2 에서 처리량이
/// 크게 오르고 4 까지 평평하며 <b>8B 는 동시 8 에서 0.36 req/s 로 붕괴한다</b>(VRAM 8GB 부족,
/// <c>W1_concurrency.md</c>). 즉 "이득이 없다" 가 아니라 "이득 구간이 좁다" 다.
/// 최종값은 T4-15 에서 1/2/4 를 재서 정한다.
/// </summary>
internal sealed class ReplanWorker : BackgroundService
{
    /// <summary>T1(로컬) 기본 워커 수. docs/14 §4 — W1 실측 동시 2 에서 처리량 최대.</summary>
    public const int DefaultT1Workers = 2;

    /// <summary>T2(외부) 기본 워커 수. docs/14 §4 — 파일럿에서 동시 24 까지 429 0회였다.</summary>
    public const int DefaultT2Workers = 8;

    /// <summary>일감이 없을 때 쉬는 시간(ms). docs/14 §4 의 코드값.</summary>
    public const int DefaultIdleDelayMs = 50;

    private readonly IReplanSource _source;
    private readonly IPlanCompiler _compiler;
    private readonly Func<Tick> _now;
    private readonly int _workers;
    private readonly int _idleDelayMs;

    private long _taken;
    private long _applied;
    private long _failed;
    private long _busy;
    private long _waitTicks;
    private long _waitSamples;
    private long _latencyTicks;

    /// <summary>워커 풀을 만든다. 기동 시 1회.</summary>
    /// <param name="source">일감 공급원.</param>
    /// <param name="compiler">플랜 컴파일러. 보통 <c>TieredPlanCompiler</c> 다.</param>
    /// <param name="now">현재 틱 공급자. 호스트가 <c>GameClock.Current</c> 를 넘긴다.</param>
    /// <param name="workers">동시 워커 수.</param>
    /// <param name="idleDelayMs">일감이 없을 때 쉬는 시간(ms).</param>
    public ReplanWorker(
        IReplanSource source,
        IPlanCompiler compiler,
        Func<Tick> now,
        int workers,
        int idleDelayMs = DefaultIdleDelayMs)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(idleDelayMs);

        _source = source;
        _compiler = compiler;
        _now = now;
        _workers = workers;
        _idleDelayMs = idleDelayMs;
    }

    /// <summary>
    /// 기억 저장소 (D-03). null 이면 밴드를 싣지 않는다 — <b>없는 것이 기본이다</b>.
    ///
    /// <para>
    /// <b>읽기 전용 타입으로 받는다.</b> 무슨 일이 있었는지 아는 것은 게임서버와 대화
    /// 서비스이고, 재계획 워커가 쓰기 시작하면 같은 사실을 두 곳이 기록하게 된다.
    /// </para>
    /// </summary>
    public IMemoryReader? Memory { get; init; }

    /// <summary>기억 조회가 밴드를 준 횟수 (D-03). 0 이면 아무 관계도 못 찾았다는 뜻이다.</summary>
    public long BandsAttached => Interlocked.Read(ref _bandsAttached);

    private long _bandsAttached;

    /// <summary>공급원 이름.</summary>
    public string Name => _source.Name;

    /// <summary>동시 워커 수.</summary>
    public int Workers => _workers;

    /// <summary>꺼낸 일감 수.</summary>
    public long Taken => Interlocked.Read(ref _taken);

    /// <summary>반영한 플랜 수.</summary>
    public long Applied => Interlocked.Read(ref _applied);

    /// <summary>플랜을 못 만든 수 (검증 실패 · 예산 거절 · 링크 장애).</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>지금 LLM 호출 중인 워커 수. 대시보드가 티어별 처리율을 볼 때 쓴다.</summary>
    public int Busy => (int)Interlocked.Read(ref _busy);

    /// <summary>
    /// 큐에서 기다린 평균 틱. docs/14 §6 의 "큐 평균 대기 시간".
    /// <b>이게 크면 스냅샷 낡음 판정(T4-04)이 대부분을 폐기하고 있을 것이다.</b>
    /// </summary>
    public double AverageWaitTicks
    {
        get
        {
            long samples = Interlocked.Read(ref _waitSamples);

            return samples == 0 ? 0 : (double)Interlocked.Read(ref _waitTicks) / samples;
        }
    }

    /// <summary>컴파일에 걸린 평균 틱. 실시간 10Hz 이므로 51 이면 5.1초다.</summary>
    public double AverageLatencyTicks
    {
        get
        {
            long taken = Taken;

            return taken == 0 ? 0 : (double)Interlocked.Read(ref _latencyTicks) / taken;
        }
    }

    /// <summary>
    /// 워커가 돈 스레드 id 집합. <b>테스트가 "틱 루프와 다른 스레드인가" 를 여기서 본다.</b>
    /// 진단 전용이라 상한을 둔다 — 무한히 모으면 그 자체가 누수다.
    /// </summary>
    public IReadOnlySet<int> ThreadIds => _threadIds;

    private readonly HashSet<int> _threadIds = [];

    /// <summary>
    /// 일감 하나를 처리한다. <b>테스트와 워커 루프가 같이 부른다</b> —
    /// 루프를 돌리지 않고 한 건만 검증할 수 있어야 한다.
    /// </summary>
    /// <returns>일감이 있었으면 true.</returns>
    public async ValueTask<bool> PumpOnceAsync(CancellationToken ct)
    {
        Tick now = _now();

        if (!_source.TryTake(now, out ReplanJob job))
        {
            return false;
        }

        Interlocked.Increment(ref _taken);
        RecordThread();

        if (job.QueuedTick >= 0)
        {
            Interlocked.Add(ref _waitTicks, Math.Max(0, now.Value - job.QueuedTick));
            Interlocked.Increment(ref _waitSamples);
        }

        PlanRequest request = await BuildRequestAsync(job, ct).ConfigureAwait(false);

        Interlocked.Increment(ref _busy);

        PlanCompileResult result;
        try
        {
            result = await _compiler.CompileAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
        }

        // 반영 시점의 틱을 다시 읽는다. 호출 중에 시간이 흘렀다 — T1 실측 5.1s = 51틱이다.
        Tick applied = _now();

        Interlocked.Add(ref _latencyTicks, Math.Max(0, applied.Value - now.Value));

        if (result.Plan is { } plan)
        {
            _source.Apply(in job, plan, applied);
            Interlocked.Increment(ref _applied);
            return true;
        }

        _source.Abandon(in job, applied);
        Interlocked.Increment(ref _failed);
        return true;
    }

    /// <summary>
    /// 일감을 요청으로. <b>기억이 붙는 곳은 여기 하나다</b> (D-03).
    ///
    /// <para>
    /// <b>개체 스냅샷에 밴드만 싣는다.</b> 인벤토리와 <c>recent</c> 도 싣고 싶지만
    /// 그것들은 여러 필드짜리 구조라 <b>틱 루프가 쓰는 중에 워커가 읽으면 찢어진 값</b>을 본다.
    /// 밴드의 입력은 <c>int</c> 한 칸(<c>NpcStore.RecentPlayer</c>)이라 그 문제가 없다.
    /// </para>
    /// </summary>
    private async ValueTask<PlanRequest> BuildRequestAsync(ReplanJob job, CancellationToken ct)
    {
        if (Memory is not { } memory
            || job.Quality != PlanQuality.Individual
            || job.GlobalNpc == 0
            || job.Subject == 0)
        {
            return new PlanRequest(job.Bucket, job.Flags, job.Quality);
        }

        RelationshipBand band = await memory
            .BandAsync(job.GlobalNpc, job.Subject, ct)
            .ConfigureAwait(false);

        if (band == RelationshipBand.Unknown)
        {
            return new PlanRequest(job.Bucket, job.Flags, job.Quality);
        }

        Interlocked.Increment(ref _bandsAttached);

        return new PlanRequest(
            job.Bucket,
            job.Flags,
            job.Quality,
            new NpcSnapshot([], [], Band: band));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = new Task[_workers];

        for (int i = 0; i < _workers; i++)
        {
            loops[i] = Task.Run(() => LoopAsync(stoppingToken), CancellationToken.None);
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool worked;

            try
            {
                worked = await PumpOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (worked)
            {
                continue;
            }

            try
            {
                await Task.Delay(_idleDelayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void RecordThread()
    {
        int id = Environment.CurrentManagedThreadId;

        lock (_threadIds)
        {
            if (_threadIds.Count < 64)
            {
                _threadIds.Add(id);
            }
        }
    }
}
