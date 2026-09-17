using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.Host.Replan;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Tests.Host;

/// <summary>docs/14 §4 — T2 워커 8개. 아키타입 버킷 미스만 채운다.</summary>
public sealed class BucketReplanSourceTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>
    /// 동시에 몇 건이 안에 들어와 있는지 세는 컴파일러.
    ///
    /// <para>
    /// <b>스레드를 붙잡지 않는다.</b> 예전에는 <c>Task.Run(() =&gt; hold.Wait())</c> 로
    /// 막았는데, 그러면 동시 8건을 보려고 <b>스레드풀 스레드 8개를 재워 둬야</b> 했다 —
    /// 풀은 스레드를 초당 한둘씩만 늘리므로 기계가 붐비면 8에 닿기 전에 시간이 갔다.
    /// 지금은 완료되지 않은 <see cref="Task"/> 를 기다린다: 스레드를 하나도 쓰지 않으므로
    /// 8건이 겹치는 데 스케줄러의 협조가 필요 없다.
    /// </para>
    /// </summary>
    private sealed class ConcurrencyProbe(CompiledPlan plan, int target) : IPlanCompiler
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _inside;
        private int _peak;

        public int Peak => Volatile.Read(ref _peak);

        public int Calls { get; private set; }

        /// <summary><paramref name="target"/> 건이 동시에 들어오면 완료된다.</summary>
        public Task Reached => _reached.Task;

        /// <summary>안에 있는 것들을 전부 내보낸다.</summary>
        public void Release() => _release.TrySetResult();

        public async ValueTask<PlanCompileResult> CompileAsync(
            PlanRequest request, CancellationToken cancellationToken)
        {
            int inside = Interlocked.Increment(ref _inside);

            lock (this)
            {
                Calls++;

                if (inside > _peak)
                {
                    Volatile.Write(ref _peak, inside);
                }
            }

            if (inside >= target)
            {
                _reached.TrySetResult();
            }

            try
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inside);
            }

            return new PlanCompileResult(
                plan,
                ValidationResult.Ok,
                CompileStats.None with { Model = "t2-stub", PromptTokens = 13_488, CompletionTokens = 300 },
                "{}");
        }
    }

    private sealed class FailingCompiler : IPlanCompiler
    {
        public int Calls { get; private set; }

        public ValueTask<PlanCompileResult> CompileAsync(
            PlanRequest request, CancellationToken cancellationToken)
        {
            Calls++;

            return ValueTask.FromResult(new PlanCompileResult(
                null,
                ValidationResult.Fail(ValidationStage.Vocabulary, "V2.UNKNOWN_ACTION", 0, "테스트"),
                CompileStats.None with { Model = "t2-stub" },
                "{}"));
        }
    }

    /// <summary>미스를 만들어 콜드 버킷을 "실제로 쓰이는데 없는 것" 으로 만든다.</summary>
    private static PlanStore NewStoreWithMisses(params (int Archetype, TimeOfDay Time, int Misses)[] wanted)
    {
        PlanStore plans = PlanStore.CreateIdleOnly(s_data);

        foreach ((int archetype, TimeOfDay time, int misses) in wanted)
        {
            var key = new BucketKey(new ArchetypeId((ushort)archetype), time, RegionState.Peace, Climate.Fair);

            for (int i = 0; i < misses; i++)
            {
                plans.Resolve(key);   // 미생성이라 미스로 센다
            }
        }

        return plans;
    }

    /// <summary>
    /// T4-11 완료 조건 — 워커 8개가 동시에 8건을 처리한다. T1 워커와 독립이다.
    /// </summary>
    [Fact]
    public async Task Worker_T2RunsEightConcurrently()
    {
        var wanted = new (int, TimeOfDay, int)[16];

        for (int i = 0; i < wanted.Length; i++)
        {
            wanted[i] = (i, TimeOfDay.Morning, 10 + i);
        }

        PlanStore plans = NewStoreWithMisses(wanted);
        var source = new BucketReplanSource(plans, s_data);
        var compiler = new ConcurrencyProbe(plans[PlanStore.IdlePlanId], target: 8);

        var worker = new ReplanWorker(
            source, compiler, () => new Tick(0), workers: ReplanWorker.DefaultT2Workers);

        Assert.Equal(8, worker.Workers);
        Assert.Equal(16, source.Depth);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // 8건이 동시에 안에 들어올 때까지 기다린다.
        //
        // <b>돌지 않고 기다린다.</b> 예전에는 `SpinWait` 로 Peak 를 들여다봤는데, 그
        // 바쁜 대기가 코어 하나를 물고 있어 <b>정작 워커가 못 올라오는</b> 일이 있었다.
        // 마감은 "멈췄다" 를 구별하기 위한 것이지 판정 기준이 아니다 — 넉넉히 준다.
        Task reached = await Task.WhenAny(compiler.Reached, Task.Delay(TimeSpan.FromMinutes(2)));

        Assert.True(
            ReferenceEquals(reached, compiler.Reached),
            $"워커 8개가 동시에 들어오지 않았다 — 최대 동시 {compiler.Peak}건 · 호출 {compiler.Calls}건. "
            + "워커 루프가 직렬화됐는지 본다.");

        Assert.Equal(8, compiler.Peak);
        Assert.Equal(8, source.InFlightCount);

        compiler.Release();

        for (int chance = 0; chance < 2_000 && source.Filled < 16; chance++)
        {
            await Task.Delay(1);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(16, source.Filled);
        Assert.Equal(0, source.InFlightCount);
        Assert.Equal(0, source.Depth);

        // 채운 버킷은 이제 히트한다 — 전량 재생성이 아니라 미스 버킷만 채웠다.
        Assert.Equal(16, plans.FilledBuckets);
    }

    /// <summary>미스가 많은 버킷을 먼저 집는다. 동점이면 인덱스 오름차순이다.</summary>
    [Fact]
    public void Bucket_PicksHottestMissFirst()
    {
        PlanStore plans = NewStoreWithMisses(
            (3, TimeOfDay.Morning, 5),
            (7, TimeOfDay.Night, 40),
            (1, TimeOfDay.Noon, 12));

        var source = new BucketReplanSource(plans, s_data);

        Assert.True(source.TryTake(new Tick(0), out ReplanJob first));
        Assert.Equal(7, first.A());
        Assert.Equal(TimeOfDay.Night, first.Bucket.T);
        Assert.Equal(PlanQuality.Archetype, first.Quality);
        Assert.Equal(-1, first.Npc);

        // 버킷이 함의하는 초기 플래그가 요청에 실린다 (docs/03 §3).
        Assert.Equal(s_data.Buckets.InitialFlags(first.Bucket), first.Flags);

        Assert.True(source.TryTake(new Tick(0), out ReplanJob second));
        Assert.Equal(1, second.A());

        Assert.True(source.TryTake(new Tick(0), out ReplanJob third));
        Assert.Equal(3, third.A());

        Assert.False(source.TryTake(new Tick(0), out _));
        Assert.Equal(3, source.InFlightCount);
    }

    /// <summary>조회된 적 없는 버킷은 만들지 않는다 — 2,880 전량 채우기는 프리베이크의 일이다.</summary>
    [Fact]
    public void Bucket_IgnoresNeverQueriedBuckets()
    {
        PlanStore plans = PlanStore.CreateIdleOnly(s_data);
        var source = new BucketReplanSource(plans, s_data);

        Assert.Equal(0, source.Depth);
        Assert.False(source.TryTake(new Tick(0), out _));
        Assert.Equal(TestPaths.TotalKeys, plans.ColdBuckets);
    }

    /// <summary>
    /// 못 만든 버킷은 쿨다운을 건다. 안 걸면 워커 8개가 만들 수 없는 버킷 하나에 매달린다.
    /// </summary>
    [Fact]
    public async Task Bucket_CoolsDownAfterFailure()
    {
        PlanStore plans = NewStoreWithMisses((2, TimeOfDay.Dawn, 9));
        var source = new BucketReplanSource(plans, s_data);
        var compiler = new FailingCompiler();

        long tick = 0;
        var worker = new ReplanWorker(source, compiler, () => new Tick(tick), workers: 1);

        Assert.True(await worker.PumpOnceAsync(CancellationToken.None));
        Assert.Equal(1, worker.Failed);
        Assert.Equal(1, source.Skipped);
        Assert.Equal(0, source.InFlightCount);

        // 쿨다운 중에는 다시 집지 않는다.
        Assert.False(await worker.PumpOnceAsync(CancellationToken.None));
        Assert.Equal(1, compiler.Calls);

        // 쿨다운이 지나면 다시 시도한다.
        tick = BucketReplanSource.FailureCooldownTicks + 1;
        Assert.True(await worker.PumpOnceAsync(CancellationToken.None));
        Assert.Equal(2, compiler.Calls);
        Assert.Equal(3_000, BucketReplanSource.FailureCooldownTicks);   // 실시간 5분
    }

    /// <summary>사람이 고정한 버킷은 런타임 재계획이 덮어쓰지 않는다 (docs/13 §2·§5).</summary>
    [Fact]
    public async Task Bucket_DoesNotOverwritePinned()
    {
        PlanStore plans = NewStoreWithMisses((5, TimeOfDay.Evening, 20));
        var key = new BucketKey(new ArchetypeId(5), TimeOfDay.Evening, RegionState.Peace, Climate.Fair);

        CompiledPlan pinned = plans[PlanStore.IdlePlanId] with { Goal = "pinned_by_human" };
        PlanId pinnedId = plans.Pin(key, pinned);

        var source = new BucketReplanSource(plans, s_data);

        // pinned 라서 HasBucket 이 참 — 애초에 일감으로 올라오지 않는다.
        Assert.False(source.TryTake(new Tick(0), out _));

        // 직접 Apply 를 불러도 SetBucket 이 거절한다.
        var job = new ReplanJob(-1, key, WorldFlags.None, PlanQuality.Archetype, 20);
        source.Apply(in job, plans[PlanStore.IdlePlanId] with { Goal = "robot_plan" }, new Tick(0));

        Assert.Equal(pinnedId.Value, plans.PeekBucket(key)!.Id.Value);
        Assert.Equal("pinned_by_human", plans.PeekBucket(key)!.Goal);

        await Task.CompletedTask;
    }
}

/// <summary>테스트 가독성용 확장 — 아키타입 code 를 짧게 읽는다.</summary>
internal static class ReplanJobTestExtensions
{
    public static int A(this ReplanJob job) => job.Bucket.A.Value;
}
