using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;

namespace Npc.Tests.MasterData;

/// <summary>docs/01 §7. 인터럽트는 LLM 없이 결정론 규칙으로만 돈다.</summary>
public sealed class InterruptRulesTests
{
    private static readonly ItemTable s_items =
        ItemTable.Load(Path.Combine(TestPaths.MasterData, "items.json"));

    private static readonly ActionCatalog s_actions =
        ActionCatalog.Load(Path.Combine(TestPaths.MasterData, "actions.json"), s_items);

    private static readonly ArchetypeTable s_archetypes =
        ArchetypeTable.Load(Path.Combine(TestPaths.MasterData, "archetypes.json"), s_actions, s_items);

    private static readonly InterruptRules s_rules =
        InterruptRules.Load(Path.Combine(TestPaths.MasterData, "interrupts.json"), s_actions);

    private static GameEvent Event(GameEventKind kind) => new()
    {
        Kind = kind,
        Sequence = 1,
        OccurredAt = new Tick(100),
        Npc = new NpcId(7),
    };

    private static ArchetypeDef Archetype(string id)
    {
        Assert.True(s_archetypes.TryGet(id, out ArchetypeDef def));
        return def;
    }

    [Fact]
    public void InterruptRules_LoadsSpecFiveAndMore()
    {
        Assert.True(s_rules.Count >= 12, $"규칙이 {s_rules.Count}개다. docs/01 §7 의 5개 + 추가 ~7개가 필요하다.");

        foreach (string id in new[]
        {
            "flee_on_threat", "fight_on_threat", "yield_to_player", "eat_when_starving", "seek_shelter",
        })
        {
            Assert.Contains(s_rules.Rules, r => r.Id == id);
        }
    }

    /// <summary>T1-18 완료 조건 — 우선순위 높은 규칙이 먼저 매칭된다.</summary>
    [Fact]
    public void InterruptRules_MatchByPriority()
    {
        // 정렬이 우선순위 내림차순, 같으면 id 오름차순이어야 결정론이다.
        for (int i = 1; i < s_rules.Count; i++)
        {
            InterruptRule prev = s_rules.Rules[i - 1];
            InterruptRule cur = s_rules.Rules[i];

            Assert.True(
                prev.Priority > cur.Priority
                || (prev.Priority == cur.Priority && string.CompareOrdinal(prev.Id, cur.Id) < 0),
                $"{prev.Id}({prev.Priority}) 다음에 {cur.Id}({cur.Priority}) 가 왔다.");
        }

        // 겁 많은 아키타입(courage 30)이 위협을 만나면 배고픔(우선순위 50)이 아니라
        // 도주(우선순위 100)가 먼저 걸려야 한다.
        ArchetypeDef child = Archetype("child");
        Assert.True(child.Traits.Courage < 40);

        WorldFlags state = WorldFlags.ThreatNearby | WorldFlags.IsHungry | WorldFlags.HasFood;

        Assert.True(s_rules.TryMatch(Event(GameEventKind.CombatStarted), state, child, out InterruptRule rule));
        Assert.Equal("flee_on_threat", rule.Id);
        Assert.Equal(100, rule.Urgency);
    }

    /// <summary>같은 상황이라도 성향이 다르면 다른 규칙이 걸린다.</summary>
    [Fact]
    public void InterruptRules_TraitConditionSelectsFightOrFlight()
    {
        ArchetypeDef guard = Archetype("town_guard");
        ArchetypeDef child = Archetype("child");

        Assert.True(guard.Traits.Courage >= 40);
        Assert.True(guard.CombatCapable);
        Assert.False(child.CombatCapable);

        Assert.True(s_rules.TryMatch(
            Event(GameEventKind.CombatStarted), WorldFlags.ThreatNearby, guard, out InterruptRule brave));
        Assert.Equal("fight_on_threat", brave.Id);

        Assert.True(s_rules.TryMatch(
            Event(GameEventKind.CombatStarted), WorldFlags.ThreatNearby, child, out InterruptRule timid));
        Assert.Equal("flee_on_threat", timid.Id);
    }

    /// <summary>event 가 붙은 규칙은 그 이벤트에만 반응한다.</summary>
    [Fact]
    public void InterruptRules_EventConditionIsHonoured()
    {
        ArchetypeDef villager = Archetype("villager");

        Assert.True(s_rules.TryMatch(
            Event(GameEventKind.PlayerInteracted), WorldFlags.None, villager, out InterruptRule yield));
        Assert.Equal("yield_to_player", yield.Id);

        // 아무 조건도 안 맞는 상태 + 다른 이벤트 → 매칭 없음
        Assert.False(s_rules.TryMatch(
            Event(GameEventKind.NpcTransform), WorldFlags.AtHome, villager, out _));
    }

    [Fact]
    public void InterruptRules_NoneFlagBlocksMatch()
    {
        ArchetypeDef villager = Archetype("villager");

        // 배고프고 식량이 있으면 먹는다.
        Assert.True(s_rules.TryMatch(
            Event(GameEventKind.NpcVitalsChanged),
            WorldFlags.IsHungry | WorldFlags.HasFood,
            villager,
            out InterruptRule eat));
        Assert.Equal("eat_when_starving", eat.Id);

        // 자는 중이면 깨우지 않는다 (none_flag: IsSleeping). 다른 규칙도 안 걸린다.
        Assert.False(s_rules.TryMatch(
            Event(GameEventKind.NpcVitalsChanged),
            WorldFlags.IsHungry | WorldFlags.HasFood | WorldFlags.IsSleeping,
            villager,
            out _));
    }

    /// <summary>그 아키타입이 못 하는 액션을 강제하지 않는다.</summary>
    [Fact]
    public void InterruptRules_SkipsActionsTheArchetypeCannotUse()
    {
        ArchetypeDef child = Archetype("child");

        Assert.True(s_actions.TryGet("Attack", out ActionDef attack));
        Assert.False(child.Allows(attack.Code));

        // courage 조건을 통과해도 Attack 을 못 쓰면 fight_on_threat 은 건너뛴다.
        foreach (InterruptRule rule in s_rules.Rules)
        {
            if (rule.Id != "fight_on_threat")
            {
                continue;
            }

            Assert.False(child.Allows(rule.Action));
        }
    }

    [Fact]
    public void InterruptRules_EveryRuleActionExistsAndHasUrgency()
    {
        foreach (InterruptRule rule in s_rules.Rules)
        {
            Assert.True(s_actions.IsDefined(rule.Action));
            Assert.InRange(rule.Urgency, 0, 100);
            Assert.InRange(rule.Priority, 0, 1000);
            Assert.NotEmpty(rule.Id);
        }
    }

    [Fact]
    public void InterruptRules_UnknownFlagThrows()
    {
        const string Json = """
            {
              "version": 1,
              "rules": [
                {
                  "id": "ghost", "priority": 1,
                  "when": { "all_flag": ["NoSuchFlag"] },
                  "then": { "action": "Wait", "params": { "duration_s": 5 } },
                  "replan": { "urgency": 1 }
                }
              ]
            }
            """;

        InvalidDataException ex =
            Assert.Throws<InvalidDataException>(() => InterruptRules.Parse(Json, s_actions));

        Assert.Contains("NoSuchFlag", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InterruptRules_UnknownActionThrows()
    {
        const string Json = """
            {
              "version": 1,
              "rules": [
                {
                  "id": "ghost", "priority": 1,
                  "when": { "all_flag": ["IsHungry"] },
                  "then": { "action": "Teleport", "params": {} },
                  "replan": { "urgency": 1 }
                }
              ]
            }
            """;

        InvalidDataException ex =
            Assert.Throws<InvalidDataException>(() => InterruptRules.Parse(Json, s_actions));

        Assert.Contains("Teleport", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InterruptRules_TraitExpressionParsesAllOperators()
    {
        var traits = new TraitSet(50, 50, 40, 50);

        Assert.True(new TraitCondition(TraitKind.Courage, TraitComparison.Less, 41).Matches(traits));
        Assert.True(new TraitCondition(TraitKind.Courage, TraitComparison.LessOrEqual, 40).Matches(traits));
        Assert.True(new TraitCondition(TraitKind.Courage, TraitComparison.GreaterOrEqual, 40).Matches(traits));
        Assert.True(new TraitCondition(TraitKind.Courage, TraitComparison.Equal, 40).Matches(traits));
        Assert.False(new TraitCondition(TraitKind.Courage, TraitComparison.Greater, 40).Matches(traits));
    }
}
