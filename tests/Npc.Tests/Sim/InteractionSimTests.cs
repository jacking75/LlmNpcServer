using Npc.Contracts;
using Npc.MasterData;
using Npc.Sim;

namespace Npc.Tests.Sim;

/// <summary>docs/02 §5. 소요시간은 마스터데이터가 정하고, 완료 시 인벤토리가 바뀐다.</summary>
public sealed class InteractionSimTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private sealed record Rig(SimWorld World, InteractionSim Interaction);

    private static Rig NewRig(int timeScale = 600)
    {
        SimWorld world = SimWorldTests.NewWorld(4, new SimOptions(TimeScale: timeScale));
        var interaction = new InteractionSim(world);

        world.Handler = interaction.TryHandle;
        return new Rig(world, interaction);
    }

    private static NpcCommand Interact(int npc, ItemId item, int amount) => new()
    {
        Kind = NpcCommandKind.Interact,
        // A-08 — 와이어의 NpcId 는 전역 id 다. 이 픽스처는 슬롯 i 에 id i+1 을 앉힌다.
        Npc = new NpcId(npc + 1),
        IssuedAt = new Tick(0),
        Correlation = new CorrelationId(11),
        Priority = CommandPriority.Normal,
        Item = item,
        Amount = amount,
    };

    /// <summary>T1-48 완료 조건 — 소요시간이 카탈로그에서 온다.</summary>
    [Fact]
    public void Sim_ActionDurationFromCatalog()
    {
        Rig r = NewRig();

        Assert.True(s_data.Items.TryGetRecipe("iron_sword", out RecipeDef sword));
        Assert.True(s_data.Items.TryGet("iron_sword", out ItemDef item));

        // 레시피가 있으면 items.json 의 duration_s × 개수.
        Assert.Equal(sword.DurationSeconds * 3, r.Interaction.WorkSecondsFor(item.Code, 3));

        // 레시피가 없으면 actions.json 의 Gather.duration.base_s.
        Assert.True(s_data.Actions.TryGet("Gather", out ActionDef gather));
        Assert.True(s_data.Items.TryGet("iron_ore", out ItemDef ore));
        Assert.Equal(gather.Duration.BaseSeconds, r.Interaction.WorkSecondsFor(ore.Code, 5));
    }

    [Fact]
    public void Sim_InteractCompletesAfterDuration()
    {
        Rig r = NewRig();

        Assert.True(s_data.Items.TryGet("iron_ore", out ItemDef ore));
        NpcCommand command = Interact(0, ore.Code, 5);
        r.World.ApplyCommand(in command, new Tick(0));

        long expected = r.Interaction.WorkTicks(r.Interaction.WorkSecondsFor(ore.Code, 5));

        // 예산 전에는 아무 일도 없다.
        for (long tick = 1; tick < expected; tick++)
        {
            r.Interaction.Tick(new Tick(tick));
        }

        Assert.DoesNotContain(SimWorldTests.Drain(r.World), e => e.Kind == GameEventKind.NpcActionCompleted);

        r.Interaction.Tick(new Tick(expected));

        List<GameEvent> events = SimWorldTests.Drain(r.World);

        Assert.Contains(events, e => e.Kind == GameEventKind.NpcActionCompleted);
        Assert.Contains(events, e => e.Kind == GameEventKind.NpcInventoryChanged);
        Assert.Equal(5, r.World.InventoryOf(0)[ore.Code.Value]);
    }

    /// <summary>레시피는 입력을 소비하고 산출물을 넣는다.</summary>
    [Fact]
    public void Sim_RecipeConsumesInputsAndProducesOutputs()
    {
        Rig r = NewRig();

        Assert.True(s_data.Items.TryGetRecipe("iron_sword", out RecipeDef recipe));
        Assert.True(s_data.Items.TryGet("iron_sword", out ItemDef sword));

        // 재료를 미리 넣어둔다.
        Span<int> inventory = r.World.InventoryOf(0);
        foreach (RecipeSlot input in recipe.Inputs)
        {
            inventory[input.Item.Value] = input.Count * 10;
        }

        NpcCommand craft = Interact(0, sword.Code, 2);
        r.World.ApplyCommand(in craft, new Tick(0));

        long at = r.Interaction.CompletionTickOf(0);
        r.Interaction.Tick(new Tick(at));

        Span<int> after = r.World.InventoryOf(0);

        Assert.Equal(2, after[sword.Code.Value]);

        foreach (RecipeSlot input in recipe.Inputs)
        {
            Assert.Equal((input.Count * 10) - (input.Count * 2), after[input.Item.Value]);
        }
    }

    [Fact]
    public void Sim_InventoryChangeIsImmediate()
    {
        Rig r = NewRig();

        Assert.True(s_data.Items.TryGet("bread", out ItemDef bread));
        r.World.InventoryOf(0)[bread.Code.Value] = 3;

        var eat = new NpcCommand
        {
            Kind = NpcCommandKind.InventoryChange,
            Npc = new NpcId(1),   // 슬롯 0 의 전역 id
            IssuedAt = new Tick(1),
            Correlation = new CorrelationId(5),
            Priority = CommandPriority.Normal,
            Item = bread.Code,
            Amount = -1,
        };

        r.World.ApplyCommand(in eat, new Tick(1));

        GameEvent ev = Assert.Single(SimWorldTests.Drain(r.World));

        Assert.Equal(GameEventKind.NpcInventoryChanged, ev.Kind);
        Assert.Equal(-1, ev.Amount);
        Assert.Equal(2, r.World.InventoryOf(0)[bread.Code.Value]);
    }

    [Fact]
    public void Sim_InventoryChangeFailsWhenShort()
    {
        Rig r = NewRig();

        Assert.True(s_data.Items.TryGet("bread", out ItemDef bread));

        var eat = new NpcCommand
        {
            Kind = NpcCommandKind.InventoryChange,
            Npc = new NpcId(1),   // 슬롯 0 의 전역 id
            IssuedAt = new Tick(1),
            Correlation = new CorrelationId(5),
            Priority = CommandPriority.Normal,
            Item = bread.Code,
            Amount = -1,
        };

        r.World.ApplyCommand(in eat, new Tick(1));

        GameEvent ev = Assert.Single(SimWorldTests.Drain(r.World));

        Assert.Equal(GameEventKind.NpcActionFailed, ev.Kind);
        Assert.Equal((byte)ActionFailReason.InsufficientResource, ev.Code);
    }

    /// <summary>전투는 판정하지 않는다 — 시드 고정 확률로 성공/실패만 답한다.</summary>
    [Fact]
    public void Sim_CombatIsProbabilisticAndDeterministic()
    {
        var kinds = new List<GameEventKind>();

        for (int run = 0; run < 2; run++)
        {
            Rig r = NewRig();
            var seen = new List<GameEventKind>();

            for (long tick = 1; tick <= 50; tick++)
            {
                var attack = new NpcCommand
                {
                    Kind = NpcCommandKind.CombatAction,
                    Npc = new NpcId(1),   // 슬롯 0 의 전역 id
                    IssuedAt = new Tick(tick),
                    Correlation = new CorrelationId((uint)tick),
                    Priority = CommandPriority.Critical,
                    TargetNpc = new NpcId(2),
                    Flags = (byte)CombatActionKind.MeleeAttack,
                };

                r.World.ApplyCommand(in attack, new Tick(tick));
                seen.AddRange(SimWorldTests.Drain(r.World).Select(e => e.Kind));
            }

            if (run == 0)
            {
                kinds = seen;
            }
            else
            {
                // 같은 시드면 같은 결과 — 리플레이가 일치한다.
                Assert.Equal(kinds, seen);
            }
        }

        Assert.Contains(GameEventKind.NpcActionCompleted, kinds);
        Assert.Contains(GameEventKind.NpcActionFailed, kinds);
    }

    [Fact]
    public void Sim_WorkTicksFollowTimeScale()
    {
        Rig slow = NewRig(timeScale: 60);
        Rig fast = NewRig(timeScale: 600);

        Assert.Equal(10 * fast.Interaction.WorkTicks(600), slow.Interaction.WorkTicks(600));
        Assert.True(fast.Interaction.WorkTicks(1) >= 1);
    }
}
