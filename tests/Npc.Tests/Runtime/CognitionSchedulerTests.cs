using System.Diagnostics;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>
/// docs/11 §4. 틱당 판정 대상 ≤ 150, 스캔 ≤ 3ms, NPC 를 늘려도 상수.
///
/// <b><see cref="AllocationCollection"/> 에 넣는다</b> (2026-07-28) —
/// <c>Cognition_ScanDoesNotAllocate</c> 가 전체 스위트에서 5회 중 1회 실패했다.
/// 단독 실행은 3/3 통과였다.
///
/// 원인은 <b>계층형 JIT 이다.</b> 이 테스트는 이미 1,000틱 워밍업을 하지만,
/// 다른 테스트와 병렬로 돌면 CPU 경합으로 tier-1 승격이 뒤로 밀려 측정 창 안에서 일어난다 —
/// 그때 나는 몇십 바이트가 "할당" 으로 잡힌다. <c>Category=Load</c> 로 빼면 CI 에서 사라지지만
/// 그건 검사를 없애는 것이다. 직렬화가 원인을 없앤다.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class CognitionSchedulerTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private sealed record Rig(NpcStore Store, LodBandSet Bands, PlanStore Plans, CognitionScheduler Scanner);

    /// <summary>
    /// docs/11 §4 의 등급 분포. <b>절대값이다</b> — 등급은 PlayerProximity 로만 바뀌므로
    /// 시야내·동일존 인원은 플레이어 수에 묶이지 파퓰레이션에 비례하지 않는다.
    /// 나머지는 비활성(LOD3)이 흡수한다. 이게 NPC 를 늘려도 틱당 스캔이 상수인 이유다.
    /// </summary>
    private const int LodZeroCount = 30;
    private const int LodOneCount = 300;
    private const int LodTwoCount = 1_000;

    private static Rig NewRig(int npcs)
    {
        var store = new NpcStore();
        store.Allocate(npcs, s_data.Items.MaxCode + 1);

        for (int npc = 0; npc < npcs; npc++)
        {
            store.StepStatus[npc] = (byte)StepStatus.Ready;
            store.Lod[npc] = npc < LodZeroCount ? (byte)0
                : npc < LodZeroCount + LodOneCount ? (byte)1
                : npc < LodZeroCount + LodOneCount + LodTwoCount ? (byte)2
                : NpcStore.InactiveLod;
        }

        var bands = new LodBandSet(store);
        while (bands.HasPendingMigration())
        {
            bands.Rebalance();
        }

        PlanStore plans = PlanStore.CreateIdleOnly(s_data);

        return new Rig(store, bands, plans, new CognitionScheduler(store, bands, plans));
    }

    /// <summary>T1-37 완료 조건 — NPC 5,000 에서 틱당 스캔 ≤ 150.</summary>
    [Fact]
    public void Cognition_ScanTargetsUnder150()
    {
        Rig r = NewRig(5_000);
        var queue = new ReplanQueue(5_000);

        int peak = 0;

        for (long tick = 0; tick < 300; tick++)
        {
            r.Scanner.Scan(new Tick(tick), queue);
            peak = Math.Max(peak, r.Scanner.LastScanned);
            queue.Clear();
        }

        Assert.True(peak <= 150, $"틱당 최대 스캔이 {peak} 이다. 150 이하여야 한다.");
    }

    /// <summary>T1-37 완료 조건 — NPC 를 10,000 으로 늘려도 틱당 스캔 ≤ 150.</summary>
    [Fact]
    public void Cognition_IsConstantWithScale()
    {
        int[] peaks = new int[2];
        int[] sizes = [5_000, 10_000];

        for (int i = 0; i < sizes.Length; i++)
        {
            Rig r = NewRig(sizes[i]);
            var queue = new ReplanQueue(sizes[i]);

            for (long tick = 0; tick < 300; tick++)
            {
                r.Scanner.Scan(new Tick(tick), queue);
                peaks[i] = Math.Max(peaks[i], r.Scanner.LastScanned);
                queue.Clear();
            }
        }

        Assert.True(peaks[1] <= 150, $"NPC 10,000 에서 틱당 최대 스캔이 {peaks[1]} 이다.");

        // 2배로 늘려도 틱당 부하는 거의 그대로여야 한다. 이게 이 구조의 존재 이유다.
        Assert.True(
            peaks[1] <= peaks[0] * 1.5,
            $"NPC 를 2배로 늘렸더니 스캔이 {peaks[0]} → {peaks[1]} 로 늘었다.");
    }

    /// <summary>docs/11 §4 성능 목표 — 스캔 소요 ≤ 3ms.</summary>
    [Fact]
    public void Cognition_ScanIsUnderThreeMilliseconds()
    {
        Rig r = NewRig(5_000);
        var queue = new ReplanQueue(5_000);

        // 워밍업
        for (long tick = 0; tick < 500; tick++)
        {
            r.Scanner.Scan(new Tick(tick), queue);
            queue.Clear();
        }

        var sw = Stopwatch.StartNew();
        const int Ticks = 1_000;

        for (long tick = 0; tick < Ticks; tick++)
        {
            r.Scanner.Scan(new Tick(tick), queue);
            queue.Clear();
        }

        sw.Stop();
        double perTickMs = sw.Elapsed.TotalMilliseconds / Ticks;

        Assert.True(perTickMs <= 3.0, $"스캔이 틱당 {perTickMs:F3}ms 다. 3ms 이하여야 한다.");
    }

    /// <summary>이탈한 NPC 만 큐에 들어간다.</summary>
    [Fact]
    public void Cognition_EnqueuesOnlyDeviatedNpcs()
    {
        Rig r = NewRig(1_000);
        var queue = new ReplanQueue(1_000);

        // 최후 플랜은 전제가 없어 아무도 이탈하지 않는다.
        for (long tick = 0; tick < 100; tick++)
        {
            r.Scanner.Scan(new Tick(tick), queue);
        }

        Assert.Equal(0, queue.Count);
        Assert.Equal(0, r.Scanner.Deviations);

        // 전제가 있는 플랜을 걸면 이탈한다.
        const string NeedsWorkplace = """
            { "schema": 1, "goal": "needs_workplace", "loop": true,
              "steps": [
                { "action": "Work", "args": { "recipe": "iron_sword", "count": 1 } },
                { "action": "Rest", "args": { "duration_s": 600 } },
                { "action": "Wait", "args": { "duration_s": 60 } }
              ] }
            """;

        PlanId plan = r.Plans.Register(PlanExecutorTests.Compile(NeedsWorkplace));
        r.Store.PlanId[0] = plan.Value;
        r.Store.Flags[0] = WorldFlags.None;

        for (long tick = 0; tick < 100; tick++)
        {
            r.Scanner.Scan(new Tick(tick), queue);
        }

        Assert.True(queue.Contains(0));
        Assert.True(r.Scanner.Deviations > 0);
    }

    /// <summary>스폰 확인 전 NPC 는 판정하지 않는다.</summary>
    [Fact]
    public void Cognition_SkipsUnspawned()
    {
        Rig r = NewRig(1_000);
        var queue = new ReplanQueue(1_000);

        for (int npc = 0; npc < 1_000; npc++)
        {
            r.Store.StepStatus[npc] = (byte)StepStatus.Unspawned;
        }

        for (long tick = 0; tick < 100; tick++)
        {
            r.Scanner.Scan(new Tick(tick), queue);
        }

        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Cognition_ScanDoesNotAllocate()
    {
        Rig r = NewRig(5_000);
        var queue = new ReplanQueue(5_000);

        for (long tick = 0; tick < 1_000; tick++)
        {
            r.Scanner.Scan(new Tick(tick), queue);
            queue.Clear();
        }

        Assert.Equal(0, AllocationProbe.MinimumBytes(
            () =>
            {
                for (long tick = 0; tick < 1_000; tick++)
                {
                    r.Scanner.Scan(new Tick(tick), queue);
                    queue.Clear();
                }
            },
            warmup: 1,
            windows: 3));
    }

    /// <summary>모든 NPC 가 자기 밴드의 주기대로 판정된다 — 굶는 NPC 가 없다.</summary>
    [Fact]
    public void Cognition_EveryNpcIsScannedWithinItsPeriod()
    {
        Rig r = NewRig(3_000);
        var queue = new ReplanQueue(3_000);

        int longestPeriod = LodBandSet.Bands.Max(b => b.Period);
        long before = r.Scanner.TotalScanned;

        for (long tick = 0; tick < longestPeriod; tick++)
        {
            r.Scanner.Scan(new Tick(tick), queue);
            queue.Clear();
        }

        long scanned = r.Scanner.TotalScanned - before;

        // 100틱 동안: LOD0 은 100번, LOD1 은 10번, LOD2 는 1번씩 판정된다. LOD3 은 0번.
        long expected = ((long)r.Bands.CountOf(0) * 100)
            + ((long)r.Bands.CountOf(1) * 10)
            + r.Bands.CountOf(2);

        Assert.Equal(expected, scanned);
        Assert.True(r.Bands.CountOf(NpcStore.InactiveLod) > 0, "비활성 밴드가 비어 있으면 테스트가 무의미하다.");
    }
}
