using Npc.Contracts;

namespace Npc.Tests.Contracts;

/// <summary>docs/02 §3.2 "명령별 사용 필드" 표의 12종이 전부 구성 가능한지 확인한다.</summary>
public sealed class NpcCommandTests
{
    private static NpcCommand Header(NpcCommandKind kind, CommandPriority priority = CommandPriority.Normal) => new()
    {
        Kind = kind,
        Npc = new NpcId(7),
        IssuedAt = new Tick(1_000),
        Correlation = new CorrelationId(1042),
        Priority = priority,
    };

    [Fact]
    public void NpcCommand_AllTwelveKindsAreConstructible()
    {
        NpcCommand[] commands =
        [
            Header(NpcCommandKind.Spawn) with
            {
                Archetype = new ArchetypeId(3),
                Zone = new ZoneId(1),
                TargetPos = new WorldPos(120.5f, 0f, -90f),
            },
            Header(NpcCommandKind.Despawn),
            Header(NpcCommandKind.MoveTo) with
            {
                TargetPoi = new PoiId(12),
                Flags = (byte)MoveSpeed.Run,
            },
            Header(NpcCommandKind.Stop, CommandPriority.Critical),
            Header(NpcCommandKind.FaceTo) with { TargetNpc = new NpcId(9) },
            Header(NpcCommandKind.PlayAnimation, CommandPriority.Cosmetic) with
            {
                Animation = new AnimationId(5),
                Amount = 2,
            },
            Header(NpcCommandKind.SetVisualState, CommandPriority.Cosmetic) with
            {
                Visual = VisualState.Working,
            },
            Header(NpcCommandKind.Interact) with
            {
                TargetPoi = new PoiId(12),
                Item = new ItemId(1),
            },
            Header(NpcCommandKind.Speak, CommandPriority.Cosmetic) with
            {
                Dialogue = new DialogueId(31),
                TargetNpc = new NpcId(9),
            },
            Header(NpcCommandKind.InventoryChange) with
            {
                Item = new ItemId(40),
                Amount = -1,
            },
            Header(NpcCommandKind.CombatAction, CommandPriority.Critical) with
            {
                TargetPlayer = new PlayerId(4),
                Flags = (byte)CombatActionKind.MeleeAttack,
            },
            Header(NpcCommandKind.SetAggro, CommandPriority.Critical) with { Flags = 1 },
        ];

        Assert.Equal(12, commands.Length);
        Assert.Equal(12, commands.Select(c => c.Kind).Distinct().Count());
        Assert.Equal(Enum.GetValues<NpcCommandKind>().Length, commands.Length);
        Assert.All(commands, c => Assert.Equal(new CorrelationId(1042), c.Correlation));
    }

    [Fact]
    public void NpcCommand_InventoryChangeAmountIsSigned()
    {
        NpcCommand withdraw = Header(NpcCommandKind.InventoryChange) with
        {
            Item = new ItemId(40),
            Amount = -3,
        };

        Assert.True(withdraw.Amount < 0);
    }
}
