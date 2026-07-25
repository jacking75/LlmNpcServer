using Npc.Contracts;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Sim;

/// <summary>
/// 상호작용·작업 시뮬. docs/02 §5 · docs/11 §6.
///
/// <c>Interact</c> 를 받으면 마스터데이터의 소요시간만큼 기다렸다가 <c>NpcActionCompleted</c> 를 낸다.
/// 소요시간은 <b>레시피가 있으면 items.json 의 duration_s</b>, 없으면 actions.json 의
/// <c>Gather.duration.base_s</c> 를 쓴다 — 코드에 시간을 박지 않는다.
///
/// 완료 시 인벤토리를 증감한다. 레시피면 입력을 소비하고 산출물을 넣는다.
/// 전투는 판정하지 않는다 — 시드 고정 확률로 성공/실패만 답한다 (docs/02 §5).
/// </summary>
public sealed class InteractionSim
{
    /// <summary>전투 행동의 성공 확률.</summary>
    public const double CombatSuccessRate = 0.7;

    private readonly SimWorld _world;
    private readonly int _defaultWorkSeconds;
    private readonly long[] _completeAt;    // 0 = 작업 중 아님
    private readonly uint[] _correlation;
    private readonly ushort[] _item;
    private readonly int[] _amount;

    /// <summary>상호작용 시뮬을 만든다.</summary>
    public InteractionSim(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        _world = world;
        _completeAt = new long[world.Capacity];
        _correlation = new uint[world.Capacity];
        _item = new ushort[world.Capacity];
        _amount = new int[world.Capacity];

        _defaultWorkSeconds = world.Data.Actions.TryGet("Gather", out ActionDef gather)
            ? gather.Duration.BaseSeconds
            : 300;
    }

    /// <summary>완료시킨 작업 수.</summary>
    public long Completions { get; private set; }

    /// <summary>인벤토리를 바꾼 횟수.</summary>
    public long InventoryChanges { get; private set; }

    /// <summary>이 NPC 가 작업 중인가.</summary>
    public bool IsWorking(int npc) => _completeAt[npc] != 0;

    /// <summary>이 NPC 의 완료 예정 틱.</summary>
    public long CompletionTickOf(int npc) => _completeAt[npc];

    /// <summary>명령을 받는다. <see cref="SimWorld.Handler"/> 가 부른다.</summary>
    public bool TryHandle(in NpcCommand command, Tick now)
    {
        switch (command.Kind)
        {
            case NpcCommandKind.Interact:
                Begin(in command, now);
                return true;

            case NpcCommandKind.InventoryChange:
                ApplyInventory(in command, now);
                return true;

            case NpcCommandKind.CombatAction:
                ResolveCombat(in command, now);
                return true;

            default:
                return false;
        }
    }

    /// <summary>이 아이템(레시피)에 걸리는 게임 초.</summary>
    public int WorkSecondsFor(ItemId item, int count)
    {
        int batches = count < 1 ? 1 : count;

        if (item.Value != 0
            && _world.Data.Items.TryGetRecipe(_world.Data.Items[item].Id, out RecipeDef recipe))
        {
            return recipe.DurationSeconds * batches;
        }

        return _defaultWorkSeconds;
    }

    /// <summary>게임 초 → 틱. 최소 1틱.</summary>
    public long WorkTicks(int gameSeconds)
    {
        long ticks = (long)gameSeconds * Contracts.Tick.PerSecond / _world.Options.TimeScale;
        return ticks < 1 ? 1 : ticks;
    }

    /// <summary>완료된 작업의 이벤트를 낸다.</summary>
    public void Tick(Tick now)
    {
        for (int npc = 0; npc < _completeAt.Length; npc++)
        {
            if (_completeAt[npc] == 0 || _completeAt[npc] > now.Value)
            {
                continue;
            }

            var correlation = new CorrelationId(_correlation[npc]);
            var item = new ItemId(_item[npc]);
            int amount = _amount[npc];

            _completeAt[npc] = 0;

            Produce(npc, item, amount, now, correlation);

            _world.Emit(new GameEvent
            {
                Kind = GameEventKind.NpcActionCompleted,
                Sequence = 0,
                OccurredAt = now,
                Npc = new NpcId(npc),
                Correlation = correlation,
                Item = item,
                Amount = amount,
            });

            Completions++;
        }
    }

    /// <summary>작업을 취소한다.</summary>
    public void Cancel(int npc) => _completeAt[npc] = 0;

    private void Begin(in NpcCommand command, Tick now)
    {
        int npc = command.Npc.Value;

        _correlation[npc] = command.Correlation.Value;
        _item[npc] = command.Item.Value;
        _amount[npc] = command.Amount;
        _completeAt[npc] = now.Value + WorkTicks(WorkSecondsFor(command.Item, command.Amount));
    }

    /// <summary>레시피면 입력을 소비하고 산출물을 넣는다. 아니면 그 아이템을 그만큼 넣는다.</summary>
    private void Produce(int npc, ItemId item, int amount, Tick now, CorrelationId correlation)
    {
        if (item.Value == 0)
        {
            return;
        }

        Span<int> inventory = _world.InventoryOf(npc);
        int batches = amount < 1 ? 1 : amount;

        if (_world.Data.Items.TryGetRecipe(_world.Data.Items[item].Id, out RecipeDef recipe))
        {
            foreach (RecipeSlot input in recipe.Inputs)
            {
                Adjust(inventory, input.Item, -input.Count * batches, npc, now, correlation);
            }

            foreach (RecipeSlot output in recipe.Outputs)
            {
                Adjust(inventory, output.Item, output.Count * batches, npc, now, correlation);
            }

            return;
        }

        Adjust(inventory, item, batches, npc, now, correlation);
    }

    private void Adjust(
        Span<int> inventory, ItemId item, int delta, int npc, Tick now, CorrelationId correlation)
    {
        if ((uint)item.Value >= (uint)inventory.Length || delta == 0)
        {
            return;
        }

        int before = inventory[item.Value];
        inventory[item.Value] = Math.Max(0, before + delta);
        int applied = inventory[item.Value] - before;

        if (applied == 0)
        {
            return;
        }

        InventoryChanges++;

        _world.Emit(new GameEvent
        {
            Kind = GameEventKind.NpcInventoryChanged,
            Sequence = 0,
            OccurredAt = now,
            Npc = new NpcId(npc),
            Correlation = correlation,
            Item = item,
            Amount = applied,
        });
    }

    private void ApplyInventory(in NpcCommand command, Tick now)
    {
        int npc = command.Npc.Value;
        Span<int> inventory = _world.InventoryOf(npc);

        // Equip 은 수량 0 으로 온다. 상태만 확인하고 끝낸다.
        if (command.Amount == 0)
        {
            _world.Complete(in command, now);
            return;
        }

        int before = (uint)command.Item.Value < (uint)inventory.Length ? inventory[command.Item.Value] : 0;

        if (command.Amount < 0 && before < -command.Amount)
        {
            _world.Fail(in command, now, ActionFailReason.InsufficientResource);
            return;
        }

        Adjust(inventory, command.Item, command.Amount, npc, now, command.Correlation);
    }

    /// <summary>전투 판정은 하지 않는다. 시드 고정 확률로 성공/실패만 답한다 (docs/02 §5).</summary>
    private void ResolveCombat(in NpcCommand command, Tick now)
    {
        uint roll = PlanHash.Mix(
            PlanHash.Mix(_world.Options.Seed, command.Npc.Value) ^ PlanHash.Mix((uint)now.Value));

        if (roll % 100 < (uint)(CombatSuccessRate * 100))
        {
            _world.Complete(in command, now);
            return;
        }

        _world.Fail(in command, now, ActionFailReason.Interrupted);
    }
}
