using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Runtime;

/// <summary>
/// B-05 — 런타임 스폰·디스폰.
///
/// <para>
/// <b>여기서 지키는 것은 "슬롯을 물려받지 않는다" 다.</b> 같은 슬롯에 다른 인스턴스가 앉을 수
/// 있게 되면, 비우지 않은 슬롯은 <b>이전 거주자의 인벤토리·플래그·플랜을 물려받은 NPC</b> 를
/// 만든다. 그 증상은 "어떤 NPC 가 가끔 남의 물건을 들고 있다" 로만 나타난다.
/// </para>
///
/// <para><b>용량은 기동 시 정해진다.</b> 넘는 스폰은 무시하고 <b>센다</b> — 조용히 넘기지 않는다.</para>
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed class DynamicRosterTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>슬롯 수. 초기 활성 4 + 여유 4.</summary>
    private const int Capacity = 8;

    /// <summary>초기 활성 슬롯 수.</summary>
    private const int Active = 4;

    /// <summary>
    /// <b>여유 슬롯에 앉힌다.</b> 집·일터·아키타입·폴백 플랜이 정적 시드와 같아야 한다 —
    /// 경로가 갈리면 "런타임에 스폰된 NPC 만 이상하다" 가 된다.
    /// </summary>
    [Fact]
    public void Activate_SeedsTheSlotLikeStaticSeeding()
    {
        Rig rig = Rig.Build();
        NpcInstanceDef def = rig.Instances[Active + 1];

        Assert.False(rig.Store.IsOccupied(Active));

        Assert.True(rig.Roster.TryActivate(Active, def.Id, new Tick(100)));

        Assert.True(rig.Store.IsOccupied(Active));
        Assert.Equal(def.Id, rig.Store.Occupant[Active]);
        Assert.Equal(def.Archetype.Value, rig.Store.ArchetypeCode[Active]);
        Assert.Equal(def.Home.Value, rig.Store.HomePoi[Active]);
        Assert.Equal(def.Workplace.Value, rig.Store.WorkPoi[Active]);
        Assert.Equal(def.Zone.Value, rig.Store.ZoneCode[Active]);
        Assert.Equal((byte)StepStatus.Ready, rig.Store.StepStatus[Active]);

        // 폴백 플랜이 붙어야 계획 없이도 산다 (CLAUDE.md §2.6).
        Assert.NotEqual(0, rig.Store.PlanId[Active]);
    }

    /// <summary>
    /// <b>디스폰 → 재스폰이면 다른 슬롯으로 살아난다</b> (B-05 완료 조건).
    ///
    /// 그리고 <b>이전 슬롯은 완전히 비어야 한다</b> — 인벤토리 한 칸이라도 남으면
    /// 다음 거주자가 그것을 들고 시작한다.
    /// </summary>
    [Fact]
    public void Deactivate_ThenActivateElsewhere_LeavesNothingBehind()
    {
        Rig rig = Rig.Build();
        NpcInstanceDef def = rig.Instances[0];

        // 슬롯 0 의 거주자를 확인하고, 물건을 하나 얹는다.
        Assert.Equal(def.Id, rig.Store.Occupant[0]);

        rig.Store.InventoryOf(0)[1] = 7;
        rig.Store.Flags[0] |= WorldFlags.PlayerNearby;

        Assert.True(rig.Roster.Deactivate(0));

        Assert.False(rig.Store.IsOccupied(0));
        Assert.Equal(0, rig.Store.Occupant[0]);
        Assert.Equal((byte)StepStatus.Unspawned, rig.Store.StepStatus[0]);
        Assert.Equal(0, rig.Store.InventoryOf(0)[1]);
        Assert.Equal(default, rig.Store.Flags[0]);
        Assert.Equal(0, rig.Store.PlanId[0]);
        Assert.Equal(NpcStore.InactiveLod, rig.Store.Lod[0]);

        // 같은 인스턴스를 다른 슬롯에 다시 앉힌다.
        const int Elsewhere = Active + 2;

        Assert.True(rig.Roster.TryActivate(Elsewhere, def.Id, new Tick(200)));

        Assert.Equal(def.Id, rig.Store.Occupant[Elsewhere]);
        Assert.Equal(def.Archetype.Value, rig.Store.ArchetypeCode[Elsewhere]);

        // 옛 슬롯은 그대로 비어 있다.
        Assert.False(rig.Store.IsOccupied(0));
    }

    /// <summary>
    /// 멱등 (N7). 같은 스폰을 두 번 받아도 <b>상태 해시가 같아야</b> 한다 —
    /// 두 번째가 슬롯을 지우고 다시 앉히면 인벤토리 변화가 날아간다.
    /// </summary>
    [Fact]
    public void Activate_IsIdempotent()
    {
        Rig rig = Rig.Build();
        NpcInstanceDef def = rig.Instances[Active + 1];

        Assert.True(rig.Roster.TryActivate(Active, def.Id, new Tick(100)));

        ulong once = rig.Store.StateHash();

        Assert.True(rig.Roster.TryActivate(Active, def.Id, new Tick(101)));

        Assert.Equal(once, rig.Store.StateHash());
        Assert.Equal(1, rig.Roster.Activated);
        Assert.Equal(1, rig.Roster.AlreadyActive);
    }

    /// <summary>디스폰도 멱등이다. 두 번째는 아무 일도 하지 않는다.</summary>
    [Fact]
    public void Deactivate_IsIdempotent()
    {
        Rig rig = Rig.Build();

        Assert.True(rig.Roster.Deactivate(1));

        ulong once = rig.Store.StateHash();

        Assert.False(rig.Roster.Deactivate(1));
        Assert.Equal(once, rig.Store.StateHash());
        Assert.Equal(1, rig.Roster.Deactivated);
    }

    /// <summary>
    /// <b>용량을 넘는 스폰은 무시하고 센다.</b> 배열을 늘릴 수 없으므로(§2.1) 다른 길이 없고,
    /// 조용히 넘기면 "게임서버에는 있는데 NPC 서버에는 없는 NPC" 가 아무 흔적 없이 생긴다.
    /// </summary>
    [Fact]
    public void Activate_RejectsBeyondCapacity()
    {
        Rig rig = Rig.Build();

        Assert.False(rig.Roster.TryActivate(Capacity, rig.Instances[0].Id, new Tick(100)));
        Assert.False(rig.Roster.TryActivate(-1, rig.Instances[0].Id, new Tick(100)));
        Assert.Equal(2, rig.Roster.Rejected);
        Assert.Equal(0, rig.Roster.Activated);
    }

    /// <summary>
    /// 인스턴스 테이블에 없는 id 는 거절한다. 1단계에서 "즉석 NPC" 를 지원하지 않는다 —
    /// 아키타입을 알 방법이 없으므로 시드할 수 없다.
    /// </summary>
    [Fact]
    public void Activate_RejectsUnknownInstance()
    {
        Rig rig = Rig.Build();

        Assert.False(rig.Roster.TryActivate(Active, 999_999, new Tick(100)));
        Assert.False(rig.Roster.TryActivate(Active, 0, new Tick(100)));
        Assert.Equal(2, rig.Roster.Rejected);
        Assert.False(rig.Store.IsOccupied(Active));
    }

    /// <summary>
    /// <c>NpcSpawned</c> 가 <c>ExtA</c> 로 온 id 를 그대로 쓴다 — 이벤트 경로가 이어져 있는가.
    /// </summary>
    [Fact]
    public void EventApplier_ActivatesFromNpcSpawned()
    {
        Rig rig = Rig.Build();
        NpcInstanceDef def = rig.Instances[Active + 3];

        rig.Applier.Apply(new GameEvent
        {
            Kind = GameEventKind.NpcSpawned,
            Sequence = 1,
            OccurredAt = new Tick(10),
            Npc = new NpcId(Active + 1),
            Pos = new WorldPos(1, 2, 3),
            ExtA = (uint)def.Id,
        });

        Assert.Equal(def.Id, rig.Store.Occupant[Active + 1]);
        Assert.Equal((byte)StepStatus.Ready, rig.Store.StepStatus[Active + 1]);
    }

    /// <summary><c>NpcDespawned</c> 가 슬롯을 비운다.</summary>
    [Fact]
    public void EventApplier_DeactivatesOnDespawn()
    {
        Rig rig = Rig.Build();

        rig.Applier.Apply(new GameEvent
        {
            Kind = GameEventKind.NpcDespawned,
            Sequence = 1,
            OccurredAt = new Tick(10),
            Npc = new NpcId(2),
        });

        Assert.False(rig.Store.IsOccupied(2));
        Assert.Equal(0, rig.Store.Occupant[2]);
    }

    /// <summary>
    /// <b><c>ExtA</c> 가 0 이면 슬롯을 건드리지 않는다.</b> 확장 슬롯을 안 켠 게임서버의
    /// 스폰이거나 이미 앉아 있는 슬롯의 재스폰이고, 둘 다 오늘의 동작이 맞다.
    /// </summary>
    [Fact]
    public void EventApplier_LeavesSlotAloneWhenExtAIsZero()
    {
        Rig rig = Rig.Build();
        int before = rig.Store.Occupant[1];

        rig.Applier.Apply(new GameEvent
        {
            Kind = GameEventKind.NpcSpawned,
            Sequence = 1,
            OccurredAt = new Tick(10),
            Npc = new NpcId(1),
        });

        Assert.Equal(before, rig.Store.Occupant[1]);
        Assert.Equal(0, rig.Roster.Rejected);
    }

    /// <summary>
    /// <b>스폰·디스폰 경로에 힙 할당이 없다.</b> 이벤트 적용은 틱 루프 안이다 (§2.1).
    ///
    /// 첫 회차는 JIT·정적 초기화가 섞이므로 예열한 뒤 잰다.
    /// </summary>
    [Fact]
    public void ActivateAndDeactivate_DoNotAllocate()
    {
        Rig rig = Rig.Build();
        int[] ids = [.. rig.Instances.Instances.Take(Capacity).Select(i => i.Id)];

        // 예열.
        for (int i = 0; i < 4; i++)
        {
            rig.Roster.Deactivate(i % Capacity);
            rig.Roster.TryActivate(i % Capacity, ids[i % Capacity], new Tick(i));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 64; i++)
        {
            int slot = i % Capacity;

            rig.Roster.Deactivate(slot);
            rig.Roster.TryActivate(slot, ids[slot], new Tick(100 + i));
        }

        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(delta == 0, $"스폰·디스폰 64회에 {delta}B 할당됐다");
    }

    /// <summary>
    /// <see cref="NpcStore.OccupiedSlots"/> 가 실제 거주 수를 센다. 메트릭이 보는 값이다.
    /// </summary>
    [Fact]
    public void OccupiedSlots_CountsOnlySeatedSlots()
    {
        Rig rig = Rig.Build();

        Assert.Equal(Active, rig.Store.OccupiedSlots());

        rig.Roster.Deactivate(0);

        Assert.Equal(Active - 1, rig.Store.OccupiedSlots());

        rig.Roster.TryActivate(Active, rig.Instances[Active].Id, new Tick(1));

        Assert.Equal(Active, rig.Store.OccupiedSlots());
    }

    // ---------------------------------------------------------------- 조립

    /// <summary>
    /// 최소 조립. <b>호스트와 같은 순서로 만든다</b> — 적용기가 실행기보다 먼저 생기고,
    /// 동적 로스터는 둘 다 필요해서 마지막에 붙는다.
    /// </summary>
    private sealed class Rig
    {
        public required NpcStore Store { get; init; }

        public required EventApplier Applier { get; init; }

        public required DynamicRoster Roster { get; init; }

        public required NpcInstanceTable Instances { get; init; }

        public static Rig Build()
        {
            NpcInstanceTable instances = NpcInstanceTable.Load(
                Path.Combine(TestPaths.MasterData, "npc_instances.json"), s_data);

            var store = new NpcStore();
            store.Allocate(Capacity, s_data.Items.MaxCode + 1);

            var clock = new GameClock(s_data.Buckets, 600);
            var correlations = new CorrelationTable(Capacity);
            var zoneStates = new ZoneStateTable(s_data);
            var lod = new LodUpdater(store) { ZoneStates = zoneStates };
            var applier = new EventApplier(s_data, store, clock, correlations, lod);

            // 폴백 플랜만 있는 최소 스토어. 아키타입마다 하나다 (CLAUDE.md §2.6).
            // Npc.Host 의 BuildPlanStore 에서 planstore 로드를 뺀 것과 같다.
            PlanStore plans = PlanStore.CreateIdleOnly(s_data);
            int[] fallbackOf = new int[s_data.Archetypes.Count];

            if (s_data.Fallbacks is { } table)
            {
                foreach (FallbackPlanEntry entry in table.Plans)
                {
                    PlanId id = plans.Register(entry.Plan);

                    plans.SetFallback(entry.Archetype, id);
                    fallbackOf[entry.Archetype.Value] = id.Value;
                }
            }

            var executor = new PlanExecutor(
                s_data, store, plans, correlations,
                new CommandEmitter(s_data, new PoiBinder(s_data.Pois)), 600);

            for (int i = 0; i < Active; i++)
            {
                NpcInstanceDef def = instances[i];

                applier.Seed(i, def.Home, def.Zone, def.Archetype, def.Home, def.Workplace);
                store.Occupant[i] = def.Id;
                store.StepStatus[i] = (byte)StepStatus.Ready;
                executor.AssignPlan(i, new PlanId(fallbackOf[def.Archetype.Value]));
            }

            var roster = new DynamicRoster(instances, store, applier, executor, fallbackOf);

            applier.Roster = roster;

            return new Rig
            {
                Store = store,
                Applier = applier,
                Roster = roster,
                Instances = instances,
            };
        }
    }
}
