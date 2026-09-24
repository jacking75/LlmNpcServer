using System.Collections.Immutable;

namespace Npc.Contracts;

/// <summary>명령의 지속 시간을 누가 소유하는가.</summary>
public enum CommandTimeOwner : byte
{
    Instant,
    GameServer,
    NpcServer,
}

/// <summary>명령별 응답 규약. 성공 이벤트가 오기 전의 진행 이벤트는 스텝을 끝내지 않는다.</summary>
public sealed record CommandResponseSpec(
    NpcCommandKind Command,
    GameEventKind Success,
    GameEventKind? AlternativeSuccess,
    GameEventKind? Progress,
    CommandTimeOwner TimeOwner,
    ImmutableArray<ActionFailReason> Failures);

/// <summary>모든 명령의 응답·시간 소유자 단일 등록부.</summary>
public static class CommandResponses
{
    private static readonly ImmutableArray<ActionFailReason> s_general =
        [ActionFailReason.Interrupted, ActionFailReason.TargetGone, ActionFailReason.Dead, ActionFailReason.Rejected];
    private static readonly ImmutableArray<ActionFailReason> s_movement =
        [ActionFailReason.Unreachable, ActionFailReason.Interrupted, ActionFailReason.TargetGone];
    private static readonly ImmutableArray<ActionFailReason> s_inventory =
        [ActionFailReason.InsufficientResource, ActionFailReason.InventoryFull];

    private static readonly CommandResponseSpec[] s_specs =
    [
        new(NpcCommandKind.Spawn, GameEventKind.NpcSpawned, null, null, CommandTimeOwner.Instant, s_general),
        new(NpcCommandKind.Despawn, GameEventKind.NpcDespawned, null, null, CommandTimeOwner.Instant, s_general),
        new(NpcCommandKind.MoveTo, GameEventKind.NpcArrived, GameEventKind.NpcActionCompleted,
            GameEventKind.NpcTransform, CommandTimeOwner.GameServer, s_movement),
        new(NpcCommandKind.Stop, GameEventKind.NpcActionCompleted, null, null, CommandTimeOwner.Instant, s_general),
        new(NpcCommandKind.FaceTo, GameEventKind.NpcActionCompleted, null, null, CommandTimeOwner.NpcServer, s_general),
        new(NpcCommandKind.PlayAnimation, GameEventKind.NpcActionCompleted, null, null, CommandTimeOwner.NpcServer, s_general),
        new(NpcCommandKind.SetVisualState, GameEventKind.NpcActionCompleted, null, null, CommandTimeOwner.NpcServer, s_general),
        new(NpcCommandKind.Interact, GameEventKind.NpcActionCompleted, null,
            GameEventKind.NpcInventoryChanged, CommandTimeOwner.GameServer, s_inventory),
        new(NpcCommandKind.Speak, GameEventKind.NpcActionCompleted, null, null, CommandTimeOwner.NpcServer, s_general),
        new(NpcCommandKind.InventoryChange, GameEventKind.NpcInventoryChanged, null, null,
            CommandTimeOwner.Instant, s_inventory),
        new(NpcCommandKind.CombatAction, GameEventKind.NpcActionCompleted, null, null,
            CommandTimeOwner.GameServer, s_general),
        new(NpcCommandKind.SetAggro, GameEventKind.NpcActionCompleted, null, null, CommandTimeOwner.Instant, s_general),
    ];

    /// <summary>번호 순서의 규약 표.</summary>
    public static ReadOnlySpan<CommandResponseSpec> All => s_specs;

    /// <summary>명령 종류로 조회한다.</summary>
    public static CommandResponseSpec For(NpcCommandKind command) => s_specs[(int)command - 1];

    /// <summary>MoveTo의 Follow·Wander는 즉시 완료된다. POI 이동만 NpcArrived를 기다린다.</summary>
    public static bool IsSuccess(NpcCommandKind command, bool moveToPoi, GameEventKind response)
    {
        CommandResponseSpec spec = For(command);
        if (command == NpcCommandKind.MoveTo)
        {
            return response == (moveToPoi ? GameEventKind.NpcArrived : GameEventKind.NpcActionCompleted);
        }

        return response == spec.Success;
    }
}
