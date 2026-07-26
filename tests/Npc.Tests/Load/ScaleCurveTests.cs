using Npc.Contracts;
using Npc.Core;
using Npc.Host;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;
using Npc.Tests.Runtime;

namespace Npc.Tests.Load;

/// <summary>
/// docs/14 §6 스케일 곡선 · T4-16.
///
/// <b>§11.3 설계의 검증 지점이다.</b> 상한이 걸린 회차는 무엇을 넣어도 150 에서 잘려
/// O(1) 로 보이므로, 차수 판정은 <b>상한을 푼 회차</b>로만 한다.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class ScaleCurveTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private sealed record Rig(NpcStore Store, LodBandSet Bands, CognitionScheduler Scanner, ReplanQueue Queue);

    /// <summary>
    /// docs/11 §4 의 등급 분포 — <b>절대값이다.</b> 등급은 <c>PlayerProximity</c> 로만 바뀌고
    /// 시야내·동일존 인원은 플레이어 수에 묶인다. 이 전제가 성립하면 스캔은 상수다.
    /// </summary>
    private static Rig NewFixedBandRig(int npcs, int scanCap)
    {
        var store = new NpcStore();
        store.Allocate(npcs, s_data.Items.MaxCode + 1);

        const int LodZero = 30;
        const int LodOne = 300;
        const int LodTwo = 1_000;

        for (int npc = 0; npc < npcs; npc++)
        {
            store.StepStatus[npc] = (byte)StepStatus.Ready;
            store.Lod[npc] = npc < LodZero ? (byte)0
                : npc < LodZero + LodOne ? (byte)1
                : npc < LodZero + LodOne + LodTwo ? (byte)2
                : NpcStore.InactiveLod;
        }

        var bands = new LodBandSet(store);

        while (bands.Rebalance() > 0)
        {
            // 초기 배치는 측정 밖에서 끝낸다.
        }

        PlanStore plans = PlanStore.CreateIdleOnly(s_data);

        return new Rig(
            store,
            bands,
            new CognitionScheduler(store, bands, plans) { ScanBudgetPerTick = scanCap },
            new ReplanQueue(npcs));
    }

    private static int PeakScan(Rig rig, int ticks = 300)
    {
        int peak = 0;

        for (long tick = 0; tick < ticks; tick++)
        {
            rig.Scanner.Scan(new Tick(tick), rig.Queue);
            peak = Math.Max(peak, rig.Scanner.LastScanned);
            rig.Queue.Clear();
        }

        return peak;
    }

    /// <summary>
    /// T4-16 완료 조건 — NPC 10배에 틱당 스캔 증가 ≤ 20%.
    ///
    /// <b>상한을 풀고 잰다.</b> 상한이 걸린 회차는 판정에 쓸 수 없다 (docs/14 §6).
    /// 성립 조건은 <b>밴드 인원이 NPC 수에 비례하지 않는 것</b>이다 —
    /// docs/11 §4 가 "등급은 PlayerProximity 로만 바뀐다" 고 한 그 전제다.
    /// </summary>
    [Fact]
    public void Scale_CognitionIsConstant()
    {
        int small = PeakScan(NewFixedBandRig(500, CognitionScheduler.Unlimited));
        int large = PeakScan(NewFixedBandRig(5_000, CognitionScheduler.Unlimited));

        Assert.True(small > 0, "작은 쪽이 한 건도 안 잡혔다. 측정이 무의미하다.");

        double growth = (double)large / small;

        Assert.True(
            growth <= 1.20,
            $"NPC 10배에 틱당 스캔이 {small} → {large} ({growth:F2}배) 로 늘었다. 1.20배 이하여야 한다.");

        // 이 분포에서는 상한이 아예 걸리지 않는다 (30 + 300/10 + 1000/100 = 70 < 150) —
        // 그래서 상한을 걸어도 같은 값이 나온다. 전제가 성립하면 상한은 놀고 있다.
        Assert.Equal(large, PeakScan(NewFixedBandRig(5_000, CognitionScheduler.MaxScansPerTick)));
        Assert.True(large <= CognitionScheduler.MaxScansPerTick, $"상한을 풀었는데 {large} 건이다.");
    }

    /// <summary>
    /// <b>실측 반증.</b> 밴드 인원이 NPC 수에 비례하면 스캔도 비례한다 —
    /// 그것이 <c>W10_scale.md</c> 가 기록한 O(n)(기울기 1.04)의 정체다.
    ///
    /// 월드(존·POI)가 고정인데 NPC 를 늘리면 밀도가 올라가 플레이어 반경 안의 NPC 가 늘어난다.
    /// <b>실제로 O(1) 을 만드는 것은 <see cref="CognitionScheduler.MaxScansPerTick"/> 이다.</b>
    /// </summary>
    [Fact]
    public void Scale_CognitionGrowsWhenBandsGrowWithPopulation()
    {
        int small = PeakScan(NewProportionalBandRig(500, CognitionScheduler.Unlimited));
        int large = PeakScan(NewProportionalBandRig(5_000, CognitionScheduler.Unlimited));

        // 상한을 풀면 자란다 — 이게 W10_load.csv 의 uncapped 행이 보여 준 것이다.
        Assert.True(large > small * 5, $"밴드가 인구에 비례하는데 스캔이 {small} → {large} 밖에 안 늘었다.");

        // 상한을 걸면 두 규모가 같아진다. 그것이 O(1) 을 만드는 실제 장치다.
        Assert.Equal(
            PeakScan(NewProportionalBandRig(500, CognitionScheduler.MaxScansPerTick)),
            PeakScan(NewProportionalBandRig(5_000, CognitionScheduler.MaxScansPerTick)));

        static Rig NewProportionalBandRig(int npcs, int scanCap)
        {
            var store = new NpcStore();
            store.Allocate(npcs, s_data.Items.MaxCode + 1);

            for (int npc = 0; npc < npcs; npc++)
            {
                store.StepStatus[npc] = (byte)StepStatus.Ready;
                store.Lod[npc] = (byte)(npc % 3);   // 인구에 비례해 세 밴드에 흩어진다
            }

            var bands = new LodBandSet(store);

            while (bands.Rebalance() > 0)
            {
                // 초기 배치는 측정 밖에서 끝낸다.
            }

            PlanStore plans = PlanStore.CreateIdleOnly(s_data);

            return new Rig(
                store,
                bands,
                new CognitionScheduler(store, bands, plans) { ScanBudgetPerTick = scanCap },
                new ReplanQueue(npcs));
        }
    }

    /// <summary>
    /// T4-16 완료 조건 — LLM 요청 수는 NPC 수와 무관하다.
    ///
    /// <b>예산 상한이 그것을 보장한다</b> (docs/14 §3). 상한은 초당 요청이고
    /// NPC 수가 인자로 들어가지 않는다 — 큐가 아무리 길어도 나가는 수는 같다.
    /// </summary>
    [Fact]
    public void Scale_LlmRequestsAreConstant()
    {
        // 10초(100틱) 동안 예산이 허락하는 요청 수는 NPC 수와 무관하다.
        int? expected = null;

        foreach (int npcs in LoadHarness.NpcLevels)
        {
            var budget = new ReplanBudget(ReplanBudgetLimits.Measured with { T1RequestsPerSecond = 1.0 });
            var queue = new ReplanQueue(npcs);

            // 큐를 가득 채운다 — NPC 가 많을수록 큐도 길다.
            for (int npc = 0; npc < npcs; npc++)
            {
                queue.TryEnqueue(npc, npc % 40);
            }

            int granted = 0;

            for (long tick = 0; tick <= 100; tick++)
            {
                while (budget.TryAcquire(Tier.T1, 1_000, new Tick(tick)))
                {
                    granted++;
                }
            }

            // 큐 길이는 NPC 수를 따라가지만(min(NPC, 4096)) 나가는 수는 그렇지 않다.
            Assert.Equal(Math.Min(npcs, ReplanQueue.DefaultCapacity), queue.Count);
            Assert.True(granted > 0, "예산이 한 건도 허락하지 않았다. 측정이 무의미하다.");

            expected ??= granted;
            Assert.Equal(expected.Value, granted);
        }

        // 상한 자체에 NPC 수가 들어가지 않는다 — 워커 수로만 곱해진다 (docs/14 §3).
        ReplanBudgetLimits limits = ReplanBudgetLimits.Measured;

        Assert.Equal(limits.T1RequestsPerSecond * 2, limits.ForWorkers(2).T1RequestsPerSecond, 6);
        Assert.Equal(limits.T2RequestsPerSecond, limits.ForWorkers(4).T2RequestsPerSecond);
    }

    /// <summary>
    /// 상한을 푸는 것은 <b>측정 전용</b>이다. 기본값은 150 이어야 한다 —
    /// 운영에서 풀면 틱 예산을 지키는 장치가 사라진다.
    /// </summary>
    [Fact]
    public void Scale_ScanCapDefaultsToSpecValue()
    {
        var store = new NpcStore();
        store.Allocate(8, s_data.Items.MaxCode + 1);

        var scanner = new CognitionScheduler(store, new LodBandSet(store), PlanStore.CreateIdleOnly(s_data));

        Assert.Equal(CognitionScheduler.MaxScansPerTick, scanner.ScanBudgetPerTick);
        Assert.Equal(150, CognitionScheduler.MaxScansPerTick);
        Assert.Equal(0, CognitionScheduler.Unlimited);

        // 호스트 기본값도 상한을 유지한다 (--scan-cap 을 안 주면 -1 = 기본).
        Assert.True(HostOptions.TryParse([], out HostOptions options, out _));
        Assert.Equal(-1, options.ScanCap);

        Assert.True(HostOptions.TryParse(["--scan-cap", "0"], out HostOptions uncapped, out _));
        Assert.Equal(0, uncapped.ScanCap);
    }
}
