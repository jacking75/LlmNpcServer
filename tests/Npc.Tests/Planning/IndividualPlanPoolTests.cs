using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Tests.Planning;

/// <summary>개별 오버라이드 플랜 풀. docs/13 §2 · T3-03.</summary>
public sealed class IndividualPlanPoolTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static CompiledPlan Plan(string goal)
    {
        const string Json = """
            { "schema": 1, "goal": "individual", "loop": false,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "$home" } },
                { "action": "Rest", "args": { "duration_s": 600 } },
                { "action": "Wait", "args": { "duration_s": 60 } }
              ] }
            """;

        PlanDocument document = System.Text.Json.JsonSerializer.Deserialize(
            Json, PlanJsonContext.Default.PlanDocument)!;

        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));

        CompiledPlan compiled = Npc.Core.Plan.PlanCompiler.Compile(
            document,
            new BucketKey(smith.Code, TimeOfDay.Night, RegionState.Peace, Climate.Fair),
            new PlanId(0),
            s_data,
            PlanOrigin.Runtime);

        return compiled with { Goal = goal };
    }

    /// <summary>
    /// T3-03 완료 조건 — 음수 id 왕복. <b>슬롯 0 을 포함해야 한다.</b>
    /// <c>~slot</c> 이라 슬롯 0 이 -1 이 되고, <c>PendingPlanId == 0</c>("없음")과 겹치지 않는다.
    /// </summary>
    [Fact]
    public void IndividualPool_NegativeIdRoundTrip()
    {
        // 슬롯 0 이 반드시 음수여야 한다 — -slot 인코딩이면 여기서 깨진다.
        Assert.Equal(-1, IndividualPlanPool.PlanIdOf(0));
        Assert.Equal(0, IndividualPlanPool.SlotOf(-1));
        Assert.True(IndividualPlanPool.IsIndividual(IndividualPlanPool.PlanIdOf(0)));

        for (int slot = 0; slot < IndividualPlanPool.Capacity; slot++)
        {
            int planId = IndividualPlanPool.PlanIdOf(slot);

            Assert.True(planId < 0, $"슬롯 {slot} 의 id 가 음수가 아니다: {planId}");
            Assert.NotEqual(0, planId);
            Assert.Equal(slot, IndividualPlanPool.SlotOf(planId));
        }

        // 버킷 플랜 id 는 개별로 오해되지 않는다.
        Assert.False(IndividualPlanPool.IsIndividual(PlanStore.IdlePlanId));
        Assert.False(IndividualPlanPool.IsIndividual(2_879));

        var pool = new IndividualPlanPool();

        // 첫 배정이 슬롯 0 이라 id 가 -1 이다.
        int first = pool.Assign(Plan("first"), npc: 7, tick: 1);

        Assert.Equal(-1, first);
        Assert.Equal(7, pool.OwnerOf(first));
        Assert.True(pool.TryGet(first, npc: 7, tick: 2, out CompiledPlan found));
        Assert.Equal("first", found.Goal);

        // 주인이 아니면 못 꺼낸다.
        Assert.False(pool.TryGet(first, npc: 8, tick: 2, out _));

        // 반납하면 비고, 예전 id 는 죽는다.
        Assert.True(pool.Release(first, npc: 7));
        Assert.False(pool.TryGet(first, npc: 7, tick: 3, out _));
        Assert.False(pool.Release(first, npc: 7));
        Assert.Equal(0, pool.Live);
    }

    /// <summary>T3-03 완료 조건 — 꽉 차면 가장 오래 안 쓴 슬롯을 회수한다.</summary>
    [Fact]
    public void IndividualPool_LruEvicts()
    {
        var pool = new IndividualPlanPool();
        var ids = new int[IndividualPlanPool.Capacity];

        // 512 칸을 꽉 채운다. 틱은 NPC 번호와 같게 준다 — 0번이 가장 오래됐다.
        for (int npc = 0; npc < IndividualPlanPool.Capacity; npc++)
        {
            ids[npc] = pool.Assign(Plan($"plan{npc}"), npc, tick: npc + 1);
        }

        Assert.Equal(IndividualPlanPool.Capacity, pool.Live);
        Assert.Equal(IndividualPlanPool.Capacity, pool.Assigned);
        Assert.Equal(0, pool.Evicted);
        Assert.Equal(0, pool.TurnoverRate);

        // 오래된 것 몇 개를 최근에 쓴 것으로 만든다 — LRU 는 배정 순서가 아니라 접근 순서다.
        Assert.True(pool.TryGet(ids[0], npc: 0, tick: 10_000, out _));
        Assert.True(pool.TryGet(ids[1], npc: 1, tick: 10_001, out _));

        // 이제 가장 오래 안 쓴 것은 2번이다.
        int fresh = pool.Assign(Plan("evictor"), npc: 9_000, tick: 10_002);

        Assert.Equal(1, pool.Evicted);
        Assert.Equal(IndividualPlanPool.Capacity, pool.Live);   // 칸 수는 그대로
        Assert.Equal(ids[2], fresh);                            // 2번 칸을 뺏었다
        Assert.Equal(9_000, pool.OwnerOf(fresh));

        // 뺏긴 NPC 는 개별 플랜을 잃는다 — 호출부는 버킷 플랜으로 되돌아간다.
        Assert.False(pool.TryGet(ids[2], npc: 2, tick: 10_003, out _));

        // 최근에 쓴 0·1 번은 살아 있다.
        Assert.True(pool.TryGet(ids[0], npc: 0, tick: 10_004, out CompiledPlan zero));
        Assert.Equal("plan0", zero.Goal);
        Assert.True(pool.TryGet(ids[1], npc: 1, tick: 10_005, out _));

        // 회전율이 노출된다 (docs/13 §6).
        Assert.Equal(1.0 / IndividualPlanPool.Capacity, pool.TurnoverRate, 9);
        Assert.Equal(IndividualPlanPool.Capacity + 1, pool.Assigned);
    }

    /// <summary>회수가 링을 한 바퀴 돌면 회전율이 1 이다. 512 칸을 두 번 채워 확인한다.</summary>
    [Fact]
    public void IndividualPool_TurnoverReachesOneAfterFullRotation()
    {
        var pool = new IndividualPlanPool();

        for (int i = 0; i < IndividualPlanPool.Capacity * 2; i++)
        {
            pool.Assign(Plan("churn"), npc: i, tick: i + 1);
        }

        Assert.Equal(IndividualPlanPool.Capacity, pool.Evicted);
        Assert.Equal(1.0, pool.TurnoverRate, 9);
        Assert.Equal(IndividualPlanPool.Capacity, pool.Live);
    }

    /// <summary>여러 워커가 동시에 배정해도 슬롯이 겹치지 않는다. 배정은 재계획 워커 쪽 경로다.</summary>
    [Fact]
    public void IndividualPool_ConcurrentAssignDoesNotShareSlots()
    {
        var pool = new IndividualPlanPool();
        var ids = new int[IndividualPlanPool.Capacity];

        Parallel.For(0, IndividualPlanPool.Capacity, npc =>
        {
            ids[npc] = pool.Assign(Plan("parallel"), npc, tick: npc + 1);
        });

        Assert.Equal(IndividualPlanPool.Capacity, ids.Distinct().Count());
        Assert.Equal(0, pool.Evicted);
        Assert.Equal(IndividualPlanPool.Capacity, pool.Live);

        for (int npc = 0; npc < IndividualPlanPool.Capacity; npc++)
        {
            Assert.Equal(npc, pool.OwnerOf(ids[npc]));
        }
    }
}
