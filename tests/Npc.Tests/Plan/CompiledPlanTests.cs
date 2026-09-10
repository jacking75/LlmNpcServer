using System.Runtime.CompilerServices;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Tests.Plan;

/// <summary>docs/03 §5 · §8. 컴파일된 플랜의 크기·전제 사전계산·왕복.</summary>
public sealed class CompiledPlanTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static readonly BucketKey s_bucket =
        new(new ArchetypeId(0), TimeOfDay.Morning, RegionState.Peace, Climate.Fair);

    private static PlanDocument Document(string json) =>
        JsonSerializer.Deserialize(json, PlanJsonContext.Default.PlanDocument)!;

    private static CompiledPlan Compile(string json) =>
        PlanCompiler.Compile(Document(json), s_bucket, new PlanId(1), s_data, sourceJson: json);

    /// <summary>docs/03 §8 — CompiledStep_SizeIsBounded.</summary>
    [Fact]
    public void CompiledStep_SizeIsBounded()
    {
        Assert.True(
            Unsafe.SizeOf<CompiledStep>() <= 32,
            $"CompiledStep 이 {Unsafe.SizeOf<CompiledStep>()} 바이트다. 32 이하여야 한다.");

        // 크기를 동결한다 (F-05). 상한만 보면 필드가 하나씩 늘어 32 에 닿을 때까지 아무도
        // 모른다 — 5,000 NPC × 10스텝이 L2 에 들어가야 한다는 것이 이 상한의 이유다.
        Assert.Equal(16, Unsafe.SizeOf<CompiledStep>());
    }

    [Fact]
    public void CompiledPlan_CompilesSpecExample()
    {
        CompiledPlan plan = Compile(PlanDocumentTests.SpecExample);

        Assert.Equal(7, plan.Steps.Length);
        Assert.Equal(7, plan.StepFlagSets.Length);
        Assert.True(plan.Loop);
        Assert.Equal("restock_and_forge", plan.Goal);
        Assert.Equal(StepFailPolicy.Fallback, plan.OnFail);

        Assert.True(s_data.Actions.TryGet("MoveTo", out ActionDef moveTo));
        Assert.Equal(moveTo.Code, plan.Steps[0].Action);
        Assert.Equal(PoiSymbol.NearestField, plan.Steps[0].Poi);
        Assert.Equal(300, plan.Steps[0].TimeoutSeconds);

        // speed=run 은 walk/run 의 1번이다.
        Assert.Equal(1, plan.Steps[0].ArgFlags);

        Assert.True(s_data.Items.TryGet("iron_ore", out ItemDef ore));
        Assert.Equal(ore.Code, plan.Steps[1].Item);
        Assert.Equal(8, plan.Steps[1].Count);
    }

    /// <summary>timeout_s 가 없는 스텝은 액션의 default_timeout_s 로 채운다.</summary>
    [Fact]
    public void CompiledPlan_FillsMissingTimeoutFromCatalog()
    {
        CompiledPlan plan = Compile(PlanDocumentTests.SpecExample);

        Assert.True(s_data.Actions.TryGet("Store", out ActionDef store));
        Assert.Equal(store.DefaultTimeoutSeconds, plan.Steps[4].TimeoutSeconds);

        // 모든 스텝에 타임아웃이 있어야 NPC 가 영구 정지하지 않는다 (docs/11 §12).
        Assert.All(plan.Steps, s => Assert.True(s.TimeoutSeconds >= PlanDocument.MinTimeoutSeconds));
    }

    /// <summary>
    /// docs/03 §5 — RequiredFlags 는 <b>앞선 스텝이 세워주는 것을 뺀</b> 진입 조건이다.
    /// 전부 OR 하면 Mine 이 세워줄 HasRawMaterial 을 Craft 때문에 요구하게 되고,
    /// 인지 스캔이 매 틱 "이탈"이라고 답한다.
    /// </summary>
    [Fact]
    public void CompiledPlan_RequiredFlagsExcludeWhatEarlierStepsGrant()
    {
        CompiledPlan plan = Compile(PlanDocumentTests.SpecExample);

        // Mine 이 HasRawMaterial 을 세우므로 Craft 의 그 전제는 진입 조건이 아니다.
        Assert.Equal(WorldFlags.None, plan.RequiredFlags & WorldFlags.HasRawMaterial);

        // Mine 자체는 AtField 와 HasTool 을 요구하는데, AtField 는 앞의 MoveTo 가 세워주지 않는다
        // (MoveTo 의 grants 는 POI 타입에 따라 동적이라 카탈로그에는 비어 있다).
        Assert.NotEqual(WorldFlags.None, plan.RequiredFlags & WorldFlags.HasTool);
    }

    [Fact]
    public void CompiledPlan_NeedsReplanIsASingleBitTest()
    {
        CompiledPlan plan = Compile(PlanDocumentTests.SpecExample);

        Assert.True(plan.NeedsReplan(WorldFlags.None));
        Assert.False(plan.NeedsReplan(plan.RequiredFlags));
        Assert.True(plan.NeedsReplan(plan.RequiredFlags | plan.ForbiddenFlags));
    }

    [Fact]
    public void CompiledPlan_NeedsReplanDoesNotAllocate()
    {
        CompiledPlan plan = Compile(PlanDocumentTests.SpecExample);
        WorldFlags state = plan.RequiredFlags;

        // JIT 티어 승격이 끝날 때까지 돌린다. 승격 시점에 한 번 할당이 잡힌다.
        for (int i = 0; i < 30_000; i++)
        {
            _ = plan.NeedsReplan(state);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            _ = plan.NeedsReplan(state);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>docs/03 §8 — Plan_RoundTrip. JSON → Compiled → JSON 왕복 시 의미가 같아야 한다.</summary>
    [Fact]
    public void Plan_RoundTrip()
    {
        PlanDocument original = Document(PlanDocumentTests.SpecExample);
        CompiledPlan compiled = PlanCompiler.Compile(original, s_bucket, new PlanId(1), s_data);
        PlanDocument back = PlanCompiler.ToDocument(compiled, s_data);

        Assert.Equal(original.Goal, back.Goal);
        Assert.Equal(original.Loop, back.Loop);
        Assert.Equal(original.OnStepFail, back.OnStepFail);
        Assert.Equal(original.Steps.Length, back.Steps.Length);

        for (int i = 0; i < original.Steps.Length; i++)
        {
            Assert.Equal(original.Steps[i].Action, back.Steps[i].Action);

            // 원본이 명시한 인자는 전부 살아남아야 한다.
            foreach ((string key, JsonElement value) in original.Steps[i].Args)
            {
                Assert.True(back.Steps[i].Args.ContainsKey(key), $"스텝 {i} 의 '{key}' 가 사라졌다.");
                Assert.Equal(value.ToString(), back.Steps[i].Args[key].ToString());
            }
        }

        // 다시 컴파일하면 같은 스텝이 나와야 한다.
        CompiledPlan again = PlanCompiler.Compile(back, s_bucket, new PlanId(1), s_data);

        // ImmutableArray 끼리 Assert.Equal 하면 내부 배열 참조를 비교한다. 배열로 펴서 비교한다.
        Assert.Equal(compiled.Steps.ToArray(), again.Steps.ToArray());
        Assert.Equal(compiled.RequiredFlags, again.RequiredFlags);
        Assert.Equal(compiled.ForbiddenFlags, again.ForbiddenFlags);
    }

    [Fact]
    public void CompiledPlan_UnknownActionThrows()
    {
        const string Json = """
            {
              "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "Teleport", "args": {} },
                { "action": "Rest", "args": { "duration_s": 60 } },
                { "action": "Rest", "args": { "duration_s": 60 } }
              ]
            }
            """;

        PlanCompilationException ex = Assert.Throws<PlanCompilationException>(() => Compile(Json));

        Assert.Equal(0, ex.StepIndex);
        Assert.Contains("Teleport", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompiledPlan_InventedPoiSymbolThrows()
    {
        const string Json = """
            {
              "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "MoveTo", "args": { "poi": "smithy_01" } },
                { "action": "Rest", "args": { "duration_s": 60 } },
                { "action": "Rest", "args": { "duration_s": 60 } }
              ]
            }
            """;

        PlanCompilationException ex = Assert.Throws<PlanCompilationException>(() => Compile(Json));

        Assert.Equal(0, ex.StepIndex);
    }

    [Fact]
    public void CompiledPlan_MissingRequiredArgumentThrows()
    {
        const string Json = """
            {
              "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "MoveTo", "args": {} },
                { "action": "Rest", "args": { "duration_s": 60 } },
                { "action": "Rest", "args": { "duration_s": 60 } }
              ]
            }
            """;

        Assert.Throws<PlanCompilationException>(() => Compile(Json));
    }

    [Fact]
    public void NpcRefCodes_RoundTripEveryKind()
    {
        Assert.Equal(NpcRefKind.None, NpcRefCodes.KindOf(NpcRefCodes.None));
        Assert.Equal(NpcRefKind.Self, NpcRefCodes.KindOf(NpcRefCodes.Self()));

        // payload 전 구간을 본다 (F-05). 6비트였을 때 65번째 아키타입은 code 를 잃고
        // 엉뚱한 NPC 를 가리켰고, 그 오류는 런타임에 티가 나지 않았다.
        for (int code = 0; code <= NpcRefCodes.MaxPayload; code++)
        {
            ushort packed = NpcRefCodes.NearestArchetype(code);

            Assert.Equal(NpcRefKind.NearestArchetype, NpcRefCodes.KindOf(packed));
            Assert.Equal(code, NpcRefCodes.PayloadOf(packed));
        }

        foreach (PoiSymbol symbol in Enum.GetValues<PoiSymbol>())
        {
            ushort packed = NpcRefCodes.PoiOwner(symbol);

            Assert.Equal(NpcRefKind.PoiOwner, NpcRefCodes.KindOf(packed));
            Assert.Equal((int)symbol, NpcRefCodes.PayloadOf(packed));
        }

        // 상한 밖은 조용히 자르지 않고 던진다.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NpcRefCodes.NearestArchetype(NpcRefCodes.MaxPayload + 1));
    }

    [Fact]
    public void CompiledPlan_NpcRefSymbolsRoundTrip()
    {
        const string Json = """
            {
              "schema": 1, "goal": "test_goal", "loop": true,
              "steps": [
                { "action": "Talk", "args": { "npc": "nearest:merchant", "topic": "trade" } },
                { "action": "Greet", "args": { "npc": "self" } },
                { "action": "Rest", "args": { "duration_s": 60 } }
              ]
            }
            """;

        CompiledPlan plan = Compile(Json);
        PlanDocument back = PlanCompiler.ToDocument(plan, s_data);

        Assert.Equal("nearest:merchant", back.Steps[0].Args["npc"].GetString());
        Assert.Equal("trade", back.Steps[0].Args["topic"].GetString());
        Assert.Equal("self", back.Steps[1].Args["npc"].GetString());
    }

    [Fact]
    public void StepFlags_SatisfiedByFollowsCatalog()
    {
        Assert.True(s_data.Actions.TryGet("Cook", out ActionDef cook));
        StepFlags flags = s_data.FlagsOf(cook.Code);

        Assert.True(flags.IsSatisfiedBy(WorldFlags.HasRawMaterial | WorldFlags.AtTavern));
        Assert.False(flags.IsSatisfiedBy(WorldFlags.HasRawMaterial));
        Assert.Equal(
            WorldFlags.HasFood,
            flags.Apply(WorldFlags.HasRawMaterial | WorldFlags.AtTavern) & WorldFlags.HasFood);
    }
}
