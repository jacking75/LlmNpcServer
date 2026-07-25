using Npc.Contracts;

namespace Npc.Tests.Contracts;

/// <summary>docs/02 §3.3 "이벤트별 의미" 표의 17종이 전부 구성 가능한지 확인한다.</summary>
public sealed class GameEventTests
{
    private static GameEvent Header(GameEventKind kind, long sequence) => new()
    {
        Kind = kind,
        Sequence = sequence,
        OccurredAt = new Tick(1_000),
    };

    [Fact]
    public void GameEvent_AllSeventeenKindsAreConstructible()
    {
        long seq = 0;

        GameEvent[] events =
        [
            Header(GameEventKind.TickSync, ++seq),
            Header(GameEventKind.GameTimeChanged, ++seq) with { Code = 4 },
            Header(GameEventKind.NpcSpawned, ++seq) with
            {
                Npc = new NpcId(7),
                Pos = new WorldPos(120.5f, 0f, -90f),
            },
            Header(GameEventKind.NpcDespawned, ++seq) with { Npc = new NpcId(7) },
            Header(GameEventKind.NpcTransform, ++seq) with
            {
                Npc = new NpcId(7),
                Pos = new WorldPos(121f, 0f, -90f),
                Heading = 1.57f,
            },
            Header(GameEventKind.NpcArrived, ++seq) with
            {
                Npc = new NpcId(7),
                Correlation = new CorrelationId(1042),
                Poi = new PoiId(12),
            },
            Header(GameEventKind.NpcActionCompleted, ++seq) with
            {
                Npc = new NpcId(7),
                Correlation = new CorrelationId(1043),
            },
            Header(GameEventKind.NpcActionFailed, ++seq) with
            {
                Npc = new NpcId(7),
                Correlation = new CorrelationId(1043),
                Code = (byte)ActionFailReason.Unreachable,
            },
            Header(GameEventKind.NpcVitalsChanged, ++seq) with
            {
                Npc = new NpcId(7),
                Hp = 40,
                Stamina = 15,
            },
            Header(GameEventKind.NpcInventoryChanged, ++seq) with
            {
                Npc = new NpcId(7),
                Item = new ItemId(40),
                Amount = -1,
            },
            Header(GameEventKind.PlayerProximity, ++seq) with
            {
                Npc = new NpcId(7),
                Player = new PlayerId(4),
                Amount = 30,
                Code = (byte)ProximityChange.Enter,
            },
            Header(GameEventKind.PlayerInteracted, ++seq) with
            {
                Npc = new NpcId(7),
                Player = new PlayerId(4),
            },
            Header(GameEventKind.CombatStarted, ++seq) with
            {
                Npc = new NpcId(7),
                OtherNpc = new NpcId(88),
            },
            Header(GameEventKind.CombatEnded, ++seq) with { Npc = new NpcId(7) },
            Header(GameEventKind.DamageTaken, ++seq) with
            {
                Npc = new NpcId(7),
                OtherNpc = new NpcId(88),
                Amount = 12,
            },
            Header(GameEventKind.ZoneStateChanged, ++seq) with
            {
                Zone = new ZoneId(1),
                Code = 2,
            },
            Header(GameEventKind.WeatherChanged, ++seq) with
            {
                Zone = new ZoneId(1),
                Code = 2,
            },
        ];

        Assert.Equal(17, events.Length);
        Assert.Equal(17, events.Select(e => e.Kind).Distinct().Count());
        Assert.Equal(Enum.GetValues<GameEventKind>().Length, events.Length);
    }

    [Fact]
    public void GameEvent_UncorrelatedEventsHaveDefaultCorrelation()
    {
        GameEvent transform = Header(GameEventKind.NpcTransform, 1) with { Npc = new NpcId(7) };

        Assert.Equal(default, transform.Correlation);
    }
}
