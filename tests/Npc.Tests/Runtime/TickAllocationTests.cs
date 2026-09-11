using System.Text.RegularExpressions;
using Npc.Contracts;
using Npc.Core.Plan;
using Npc.Gateway;
using Npc.Host;
using Npc.Host.Metrics;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>
/// 할당 측정 전용 컬렉션. <b>다른 테스트와 같이 돌리면 안 된다</b> —
/// <c>GC.CollectionCount(0)</c> 는 프로세스 전역이라 옆 스레드가 할당하면 델타가 오염된다.
/// </summary>
[CollectionDefinition(AllocationCollection.Name, DisableParallelization = true)]
public sealed class AllocationCollection
{
    /// <summary>컬렉션 이름.</summary>
    public const string Name = "Allocation";
}

/// <summary>
/// docs/11 §9 할당 제거 체크리스트. 틱 루프는 Gen0 을 만들지 않는다.
///
/// 틱 루프만 격리해서 잰다. 같은 프로세스의 Sim 이나 테스트 러너가 할당하면
/// <c>GC.CollectionCount(0)</c> 는 틱 루프와 무관하게 올라간다.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class TickAllocationTests
{
    private const int Npcs = 5_000;
    private const int MeasuredTicks = 1_000;
    private const int WarmupTicks = 1_000;

    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private sealed record Rig(NpcServerLoop Loop, NpcStore Store, CognitionScheduler Cognition);

    private static Rig NewRig(int npcs)
    {
        NpcInstanceTable instances = NpcInstanceTable.Load(
            Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

        var store = new NpcStore();
        store.Allocate(npcs, s_data.Items.MaxCode + 1);

        var link = new NullGameServerLink();
        var clock = new GameClock(s_data.Buckets, timeScale: 600);
        var correlations = new CorrelationTable(npcs);
        var applier = new EventApplier(s_data, store, clock, correlations);
        var emitter = new CommandEmitter(s_data, new PoiBinder(s_data.Pois));
        var swapper = new PlanSwapper(store);
        var queue = new ReplanQueue(npcs);

        PlanStore plans = PlanStore.CreateIdleOnly(s_data);
        var fallbackOf = new int[s_data.Archetypes.Count];

        foreach (FallbackPlanEntry entry in s_data.Fallbacks!.Plans)
        {
            PlanId id = plans.Register(entry.Plan);

            plans.SetFallback(entry.Archetype, id);
            fallbackOf[entry.Archetype.Value] = id.Value;
        }

        var executor = new PlanExecutor(s_data, store, plans, correlations, emitter, timeScale: 600)
        {
            Swapper = swapper,
            ReplanQueue = queue,
        };

        var bands = new LodBandSet(store);
        var cognition = new CognitionScheduler(store, bands, plans);
        var interrupts = new InterruptMatcher(s_data, store);

        for (int i = 0; i < npcs; i++)
        {
            NpcInstanceDef def = instances[i % instances.Count];

            applier.Seed(i, def.Home, def.Zone, def.Archetype, def.Home, def.Workplace);

            // A-08 — 와이어의 NpcId 는 전역 인스턴스 id 다. 이 픽스처는 슬롯 i 에
            // id i+1 을 앉힌다 (0 은 "없음" 이라 쓸 수 없다).
            store.Bind(i, i + 1);
            store.StepStatus[i] = (byte)StepStatus.Ready;
            executor.AssignPlan(i, new PlanId(fallbackOf[def.Archetype.Value]));

            // 밴드가 골고루 차야 스캔 경로가 전부 돈다.
            store.Lod[i] = (byte)(i % LodBandSet.BandCount);
        }

        while (bands.Rebalance() > 0)
        {
            // 초기 배치는 측정 밖에서 끝낸다.
        }

        var loop = new NpcServerLoop(
            link, clock, applier, interrupts, cognition, executor, swapper, bands, queue);

        return new Rig(loop, store, cognition);
    }

    /// <summary>T1-60 완료 조건 — 1,000틱 동안 Gen0 컬렉션 델타 = 0.</summary>
    [Fact]
    [Trait("Category", "Load")]
    public void Runtime_NoGen0GcInTickLoop()
    {
        Rig rig = NewRig(Npcs);

        // JIT 승격이 측정 창 안에서 일어나면 할당으로 보인다.
        for (long tick = 1; tick <= WarmupTicks; tick++)
        {
            rig.Loop.RunTick(new Tick(tick));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int gen0Before = GC.CollectionCount(0);
        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();

        for (long tick = WarmupTicks + 1; tick <= WarmupTicks + MeasuredTicks; tick++)
        {
            rig.Loop.RunTick(new Tick(tick));
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
        int gen0 = GC.CollectionCount(0) - gen0Before;

        Assert.Equal(0, allocated);
        Assert.Equal(0, gen0);
    }

    /// <summary>docs/11 §9 — 인지 스캔은 인구와 무관하게 상한 안에 있다.</summary>
    [Fact]
    [Trait("Category", "Load")]
    public void Runtime_ScanStaysUnderBudgetAtFullPopulation()
    {
        Rig rig = NewRig(Npcs);
        int worst = 0;

        for (long tick = 1; tick <= MeasuredTicks; tick++)
        {
            rig.Loop.RunTick(new Tick(tick));
            worst = Math.Max(worst, rig.Cognition.LastScanned);
        }

        // NPC 5,000 에 밴드를 골고루 채워도 상한을 넘지 않는다.
        Assert.InRange(worst, 1, CognitionScheduler.MaxScansPerTick);
        Assert.Equal(Npcs, rig.Store.Count);
    }

    /// <summary>
    /// 호스트 전체를 돌려도 틱 창 안의 <b>정상 상태</b> 할당이 0 이다.
    ///
    /// <b>누계로 재지 않는다</b> (2026-07-28, P4 게이트 항목 9 와 같은 정정).
    /// 누계는 콜드 스타트(JIT · 정적 초기화 · 첫 인터페이스 디스패치)를 함께 세고,
    /// 그것은 JIT 런타임에서 0 으로 만들 수 없다 — 실측으로 게임 1일과 3일 회차의
    /// 할당이 <b>같은 3건·같은 264 B</b> 였다 (<c>P4_gate.md §4.4</c>).
    /// </summary>
    [Fact]
    [Trait("Category", "Load")]
    public async Task Runtime_TickWindowAllocatesNothingInHost()
    {
        Assert.True(
            HostOptions.TryParse(
                ["--loopback", "--npcs", "5000", "--time-scale", "600", "--days", "1",
                 "--max-speed", "--no-dashboard"],
                out HostOptions options,
                out string? error),
            error);

        await using NpcHost host = NpcHost.Create(options with { MasterData = TestPaths.MasterData }, TextWriter.Null);
        await host.RunAsync(CancellationToken.None);

        MetricsSnapshot m = host.Metrics.Snapshot();

        Assert.True(m.Tick.Ticks >= MeasuredTicks, $"틱 {m.Tick.Ticks}");
        Assert.Equal(0, m.Tick.BytesPerTick);

        // 정상 상태 = 회차 후반부. 여기서 할당이 나면 콜드 스타트로 설명할 수 없다.
        long last = host.Metrics.LastAllocatingTick;

        Assert.True(
            last * 2 < host.Loop.TicksProcessed,
            $"회차 후반부에 할당이 있다 — 마지막 할당 틱 {last} / 전체 {host.Loop.TicksProcessed} "
            + $"(누계 {host.Metrics.AllocatedInTicks}B).");

        Assert.Equal(0, m.Tick.Overruns);
        Assert.InRange(m.Replan.ScanPerTick, 0, CognitionScheduler.MaxScansPerTick);
    }

    /// <summary>docs/11 §9 체크리스트 — 틱 루프에 LINQ 가 없다.</summary>
    [Fact]
    public void Runtime_HasNoLinqInTickPath()
    {
        var linq = new Regex(@"\.(Select|Where|OrderBy|ToArray|ToList|Any|All|First|Sum|Count)\s*\(");
        List<string> violations = [];

        foreach (string file in Sources("Npc.Runtime"))
        {
            string source = File.ReadAllText(file);

            if (linq.IsMatch(source) || source.Contains("using System.Linq", StringComparison.Ordinal))
            {
                violations.Add(Path.GetFileName(file));
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>docs/11 §9 체크리스트 — 로깅이 문자열 보간을 하지 않는다.</summary>
    [Fact]
    public void Runtime_LogsThroughSourceGeneratorOnly()
    {
        List<string> violations = [];

        foreach (string file in Sources("Npc.Runtime"))
        {
            string source = File.ReadAllText(file);

            // ILogger 를 직접 부르면 인자 배열이 박싱된다. [LoggerMessage] 만 쓴다.
            foreach (Match match in Regex.Matches(source, @"\.Log(Trace|Debug|Information|Warning|Error|Critical)\s*\("))
            {
                violations.Add($"{Path.GetFileName(file)}: {match.Value}");
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>docs/11 §9 체크리스트 — 런타임 JSON 리플렉션이 없다.</summary>
    [Fact]
    public void Runtime_UsesNoRuntimeJsonReflection()
    {
        // 틱 루프에는 JSON 이 아예 없다.
        foreach (string file in Sources("Npc.Runtime"))
        {
            Assert.DoesNotContain("JsonSerializer", File.ReadAllText(file), StringComparison.Ordinal);
        }

        // 로드 경로의 역직렬화는 전부 소스 생성 컨텍스트를 지나야 한다.
        // (JsonSerializer.Serialize(string) 은 문자열 이스케이프라 리플렉션이 아니다.)
        List<string> violations = [];

        foreach (string project in new[] { "Npc.Core", "Npc.MasterData" })
        {
            foreach (string file in Sources(project))
            {
                foreach (Match match in Regex.Matches(
                    File.ReadAllText(file), @"JsonSerializer\.Deserialize[^;]*?;", RegexOptions.Singleline))
                {
                    if (!match.Value.Contains("JsonContext.Default", StringComparison.Ordinal))
                    {
                        violations.Add($"{Path.GetFileName(file)}: {match.Value.Trim()}");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>docs/11 §9 체크리스트 — Emit 이 IEnumerable 을 반환하지 않는다.</summary>
    [Fact]
    public void Runtime_EmitWritesIntoSpan()
    {
        string source = File.ReadAllText(TestPaths.At("src", "Npc.Runtime", "CommandEmitter.cs"));

        Assert.DoesNotMatch(new Regex(@"public\s+IEnumerable"), source);
        Assert.DoesNotContain("yield return", source, StringComparison.Ordinal);
        Assert.Contains("Span<NpcCommand>", source, StringComparison.Ordinal);
    }

    /// <summary>생성 파일(*.g.cs)은 뺀다 — 암시적 using 이 System.Linq 를 끌고 온다.</summary>
    private static IEnumerable<string> Sources(string project) =>
        Directory.EnumerateFiles(TestPaths.At("src", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".g.cs", StringComparison.Ordinal));
}
