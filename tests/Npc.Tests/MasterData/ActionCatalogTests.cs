using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;

namespace Npc.Tests.MasterData;

/// <summary>docs/01 §2 · docs/03 §5. ActionId.Value 가 곧 배열 첨자여야 한다.</summary>
public sealed class ActionCatalogTests
{
    private static readonly ItemTable s_items =
        ItemTable.Load(Path.Combine(TestPaths.MasterData, "items.json"));

    private static readonly ActionCatalog s_catalog =
        ActionCatalog.Load(Path.Combine(TestPaths.MasterData, "actions.json"), s_items);

    [Fact]
    public void ActionCatalog_LoadsThirtySeven()
    {
        Assert.Equal(37, s_catalog.Count);
    }

    [Fact]
    public void ActionCatalog_IndexesByCode()
    {
        foreach (ActionDef def in s_catalog.Actions)
        {
            Assert.Equal(def, s_catalog[def.Code]);
            Assert.True(s_catalog.IsDefined(def.Code));
        }

        Assert.Equal(37, s_catalog.MaxCode);
        Assert.False(s_catalog.IsDefined(new ActionId(0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => s_catalog[new ActionId(38)]);
    }

    [Fact]
    public void ActionCatalog_ParsesFlagsIntoWorldFlags()
    {
        Assert.True(s_catalog.TryGet("Work", out ActionDef work));
        Assert.Equal(WorldFlags.AtWorkplace | WorldFlags.HasTool, work.Requires);
        Assert.Equal(WorldFlags.HasProduct, work.Grants);
        Assert.Equal(WorldFlags.IsSleeping, work.Forbids);
        Assert.Equal(WorldFlags.None, work.RequiresAny);
    }

    /// <summary>docs/01 §2.1 의 requires_any (OR 전제).</summary>
    [Fact]
    public void ActionCatalog_ParsesRequiresAny()
    {
        Assert.True(s_catalog.TryGet("Cook", out ActionDef cook));
        Assert.Equal(WorldFlags.AtHome | WorldFlags.AtTavern, cook.RequiresAny);
        Assert.Equal(WorldFlags.HasRawMaterial, cook.Requires);

        // 둘 중 하나만 있어도 성립한다.
        Assert.True(cook.IsSatisfiedBy(WorldFlags.HasRawMaterial | WorldFlags.AtHome));
        Assert.True(cook.IsSatisfiedBy(WorldFlags.HasRawMaterial | WorldFlags.AtTavern));

        // 둘 다 없으면 성립하지 않는다.
        Assert.False(cook.IsSatisfiedBy(WorldFlags.HasRawMaterial));

        // AND 전제가 빠져도 성립하지 않는다.
        Assert.False(cook.IsSatisfiedBy(WorldFlags.AtHome));

        // 금지 플래그가 서면 성립하지 않는다.
        Assert.False(cook.IsSatisfiedBy(WorldFlags.HasRawMaterial | WorldFlags.AtHome | WorldFlags.IsSleeping));
    }

    [Fact]
    public void ActionCatalog_UndefinedFlagReferenceThrows()
    {
        const string Json = """
            {
              "version": 1,
              "actions": [
                {
                  "id": "Ghost", "code": 1, "category": "misc", "desc": "없는 플래그를 참조하는 액션이다.",
                  "params": {},
                  "requires": ["NoSuchFlag"], "requires_any": [], "forbids": [], "grants": [], "clears": [],
                  "duration": { "kind": "fixed", "base_s": 1, "per_meter_s": 0 },
                  "cost": 0, "default_timeout_s": 60,
                  "emits": [{ "command": "Stop", "map": {} }],
                  "completes_on": ["NpcActionCompleted"], "fails_on": ["NpcActionFailed"]
                }
              ]
            }
            """;

        InvalidDataException ex =
            Assert.Throws<InvalidDataException>(() => ActionCatalog.Parse(Json, s_items));

        Assert.Contains("NoSuchFlag", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionCatalog_UnknownParamTypeThrows()
    {
        const string Json = """
            {
              "version": 1,
              "actions": [
                {
                  "id": "Ghost", "code": 1, "category": "misc", "desc": "없는 파라미터 타입을 쓰는 액션이다.",
                  "params": { "thing": { "type": "recipe_ref", "required": true } },
                  "requires": [], "requires_any": [], "forbids": [], "grants": [], "clears": [],
                  "duration": { "kind": "fixed", "base_s": 1, "per_meter_s": 0 },
                  "cost": 0, "default_timeout_s": 60,
                  "emits": [{ "command": "Stop", "map": {} }],
                  "completes_on": ["NpcActionCompleted"], "fails_on": ["NpcActionFailed"]
                }
              ]
            }
            """;

        InvalidDataException ex =
            Assert.Throws<InvalidDataException>(() => ActionCatalog.Parse(Json, s_items));

        Assert.Contains("recipe_ref", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionCatalog_UnknownItemLiteralInEmitsThrows()
    {
        const string Json = """
            {
              "version": 1,
              "actions": [
                {
                  "id": "Ghost", "code": 1, "category": "misc", "desc": "없는 아이템을 발행하는 액션이다.",
                  "params": {},
                  "requires": [], "requires_any": [], "forbids": [], "grants": [], "clears": [],
                  "duration": { "kind": "fixed", "base_s": 1, "per_meter_s": 0 },
                  "cost": 0, "default_timeout_s": 60,
                  "emits": [{ "command": "InventoryChange", "map": { "Item": "unobtainium", "Amount": 1 } }],
                  "completes_on": ["NpcInventoryChanged"], "fails_on": ["NpcActionFailed"]
                }
              ]
            }
            """;

        InvalidDataException ex =
            Assert.Throws<InvalidDataException>(() => ActionCatalog.Parse(Json, s_items));

        Assert.Contains("unobtainium", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>액션 → NpcCommand 매핑은 actions.json 의 emits 가 결정한다 (T1-32 의 전제).</summary>
    [Fact]
    public void ActionCatalog_CompilesEmitMappings()
    {
        Assert.True(s_catalog.TryGet("MoveTo", out ActionDef moveTo));
        EmitDef emit = Assert.Single(moveTo.Emits);

        Assert.Equal(NpcCommandKind.MoveTo, emit.Command);
        Assert.Equal(CommandPriority.Normal, emit.Priority);

        EmitMapping poi = emit.Map.Single(m => m.Field == CommandField.TargetPoi);
        Assert.Equal(EmitSource.StepPoi, poi.Source);

        EmitMapping flags = emit.Map.Single(m => m.Field == CommandField.Flags);
        Assert.Equal(EmitSource.StepArgFlags, flags.Source);
    }

    [Fact]
    public void ActionCatalog_CompilesLiteralAndFlagSources()
    {
        // Flee 는 항상 뛴다 — 리터럴.
        Assert.True(s_catalog.TryGet("Flee", out ActionDef flee));
        EmitMapping runFlag = flee.Emits[0].Map.Single(m => m.Field == CommandField.Flags);
        Assert.Equal(EmitSource.LiteralSymbol, runFlag.Source);
        Assert.Equal((int)MoveSpeed.Run, runFlag.Number);
        Assert.Equal(CommandPriority.Critical, flee.Emits[0].Priority);

        // Eat 은 "가진 식량 중 첫 번째"를 소비한다 — 아이템 이름이 코드에 없다.
        Assert.True(s_catalog.TryGet("Eat", out ActionDef eat));
        EmitMapping food = eat.Emits[0].Map.Single(m => m.Field == CommandField.Item);
        Assert.Equal(EmitSource.FirstWithFlag, food.Source);
        Assert.Equal(WorldFlags.HasFood, food.Flag);

        EmitMapping amount = eat.Emits[0].Map.Single(m => m.Field == CommandField.Amount);
        Assert.Equal(EmitSource.LiteralNumber, amount.Source);
        Assert.Equal(-1, amount.Number);
    }

    /// <summary>대사·애니메이션 심볼표는 정렬돼 있어 같은 입력이면 같은 id 가 나온다 (결정론).</summary>
    [Fact]
    public void ActionCatalog_SymbolTablesAreSortedAndStable()
    {
        // ImmutableArray<T> 끼리 Assert.Equal 하면 내부 배열 참조를 비교한다. 배열로 펴서 비교한다.
        Assert.Equal(s_catalog.Animations.Order(StringComparer.Ordinal).ToArray(), s_catalog.Animations.ToArray());
        Assert.Equal(s_catalog.Dialogues.Order(StringComparer.Ordinal).ToArray(), s_catalog.Dialogues.ToArray());

        ActionCatalog again =
            ActionCatalog.Load(Path.Combine(TestPaths.MasterData, "actions.json"), s_items);

        Assert.Equal(s_catalog.Animations.ToArray(), again.Animations.ToArray());
        Assert.Equal(s_catalog.Dialogues.ToArray(), again.Dialogues.ToArray());
    }

    /// <summary>enum 인자를 대사 id 로 옮기는 변환표가 만들어진다 (Talk 의 topic → Dialogue).</summary>
    [Fact]
    public void ActionCatalog_BuildsArgMapForDialogue()
    {
        Assert.True(s_catalog.TryGet("Talk", out ActionDef talk));
        EmitMapping dialogue = talk.Emits[0].Map.Single(m => m.Field == CommandField.Dialogue);

        Assert.Equal(EmitSource.StepArgFlags, dialogue.Source);

        ParamDef topic = talk.Param("topic")!;
        Assert.Equal(topic.EnumValues.Length, dialogue.ArgMap.Length);

        for (int i = 0; i < topic.EnumValues.Length; i++)
        {
            Assert.Equal(topic.EnumValues[i], s_catalog.Dialogues[dialogue.ArgMap[i]]);
        }
    }

    [Fact]
    public void ActionCatalog_EveryActionHasTimeoutAndCompletionEvents()
    {
        foreach (ActionDef def in s_catalog.Actions)
        {
            Assert.InRange(def.DefaultTimeoutSeconds, 5, 7200);
            Assert.NotEmpty(def.CompletesOn);
            Assert.NotEmpty(def.FailsOn);
            Assert.NotEmpty(def.Emits);
        }
    }

    [Fact]
    public void ActionCatalog_ParamsAreSortedByNameForDeterminism()
    {
        foreach (ActionDef def in s_catalog.Actions)
        {
            Assert.Equal(
                def.Params.Select(p => p.Name).Order(StringComparer.Ordinal).ToArray(),
                def.Params.Select(p => p.Name).ToArray());
        }
    }
}
