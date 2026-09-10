using Npc.Contracts;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Runtime;

namespace Npc.Tests.MasterData;

/// <summary>docs/01 §8 · docs/11 §7. 폴백 40개는 로드 시점에 검증기 1~3단을 통과해야 한다.</summary>
public sealed class PlanTableTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private static PlanTable Table => s_data.Fallbacks
        ?? throw new InvalidOperationException("fallback_plans.json 이 로드되지 않았다.");

    /// <summary>T1-54 완료 조건 — 아키타입 전부 검증기 1~3단 통과 (Validator_AcceptsAllFallbacks).</summary>
    [Fact]
    public void Validator_AcceptsAllFallbacks()
    {
        // PlanTable.Load 가 1~3단을 통과시키지 못하면 예외를 던진다.
        // 여기까지 왔다는 것은 전부 통과했다는 뜻이다.
        Assert.Equal(s_data.Archetypes.Count, Table.Count);

        foreach (ArchetypeDef archetype in s_data.Archetypes.Archetypes)
        {
            CompiledPlan? plan = Table.For(archetype.Code);

            Assert.NotNull(plan);
            Assert.True(plan!.Loop, $"{archetype.Id} 의 폴백이 loop:true 가 아니다.");
            Assert.InRange(plan.Steps.Length, PlanDocument.MinSteps, PlanDocument.MaxSteps);
            Assert.Equal(PlanOrigin.Fallback, plan.Origin);

            // 모든 스텝에 타임아웃이 있어야 영구 정지하지 않는다.
            Assert.All(plan.Steps, s => Assert.True(s.TimeoutSeconds >= PlanDocument.MinTimeoutSeconds));
        }
    }

    /// <summary>V8 — 폴백이 그 아키타입의 allowed_actions 만 쓴다.</summary>
    [Fact]
    public void Fallback_UsesOnlyAllowedActions()
    {
        foreach (FallbackPlanEntry entry in Table.Plans)
        {
            ArchetypeDef archetype = s_data.Archetypes[entry.Archetype];

            foreach (CompiledStep step in entry.Plan.Steps)
            {
                Assert.True(
                    archetype.Allows(step.Action),
                    $"{archetype.Id} 의 폴백이 허용되지 않은 액션 {s_data.ActionName(step.Action)} 를 쓴다.");
            }
        }
    }

    /// <summary>$primary 는 로드 시점에 아키타입의 대표 레시피로 풀린다.</summary>
    [Fact]
    public void Fallback_ResolvesPrimarySymbol()
    {
        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));
        Assert.True(Table.TryGet(smith.FallbackPlanId, out FallbackPlanEntry entry));

        // 어떤 스텝의 args 에도 $primary 가 남아 있으면 안 된다.
        foreach (PlanStep step in entry.Document.Steps)
        {
            foreach ((string _, System.Text.Json.JsonElement value) in step.Args)
            {
                Assert.NotEqual(PlanTable.PrimarySymbol, value.ToString());
            }
        }

        Assert.True(s_data.Items.TryGet(smith.PrimaryRecipes[0], out ItemDef primary));
        Assert.Contains(entry.Plan.Steps, s => s.Item == primary.Code);
    }

    /// <summary>T1-55 완료 조건 — $workplace·$home 이 NPC 인스턴스별로 다르게 바인딩된다.</summary>
    [Fact]
    public void Fallback_BindsPerInstance()
    {
        Assert.True(s_data.Archetypes.TryGet("blacksmith", out ArchetypeDef smith));
        CompiledPlan plan = Table.For(smith.Code)!;

        var binder = new PoiBinder(s_data.Pois);

        // 같은 플랜, 다른 인스턴스 — 심볼이 각자의 POI 로 풀려야 한다.
        PoiId homeA = s_data.Pois.OfSubtype("house")[0];
        PoiId homeB = s_data.Pois.OfSubtype("house")[7];
        PoiId workA = s_data.Pois.OfSubtype("smithy")[0];
        PoiId workB = s_data.Pois.OfSubtype("smithy")[1];

        var a = new PoiBindContext(new NpcId(1), smith.Code, homeA, workA, homeA);
        var b = new PoiBindContext(new NpcId(2), smith.Code, homeB, workB, homeB);

        var boundA = new List<PoiId>();
        var boundB = new List<PoiId>();

        foreach (CompiledStep step in plan.Steps)
        {
            if (step.Poi == PoiSymbol.None)
            {
                continue;
            }

            Assert.True(binder.TryBind(step.Poi, a, out PoiId pa), $"{step.Poi} 바인딩 실패");
            Assert.True(binder.TryBind(step.Poi, b, out PoiId pb), $"{step.Poi} 바인딩 실패");

            boundA.Add(pa);
            boundB.Add(pb);
        }

        Assert.NotEmpty(boundA);
        Assert.NotEqual(boundA, boundB);

        // $home·$workplace 는 인스턴스 값을 그대로 쓴다.
        Assert.Contains(homeA, boundA);
        Assert.Contains(workA, boundA);
        Assert.Contains(homeB, boundB);
        Assert.Contains(workB, boundB);
    }

    /// <summary>모든 폴백의 POI 심볼이 그 아키타입에서 바인딩 가능해야 한다.</summary>
    [Fact]
    public void Fallback_EverySymbolIsBindable()
    {
        foreach (FallbackPlanEntry entry in Table.Plans)
        {
            foreach (CompiledStep step in entry.Plan.Steps)
            {
                if (step.Poi == PoiSymbol.None)
                {
                    continue;
                }

                Assert.True(
                    s_data.CanBindSymbol(entry.Archetype, step.Poi),
                    $"{s_data.Archetypes[entry.Archetype].Id} 가 {PoiSymbols.ToText(step.Poi)} 를 못 쓴다.");
            }
        }
    }

    /// <summary>docs/11 §7 — 작성 소요 시간 기록이 40행이어야 한다.</summary>
    [Fact]
    public void Fallback_AuthoringTimeIsLogged()
    {
        string[] lines = File.ReadAllLines(
            TestPaths.At("docs", "measurements", "authoring_time.jsonl"));

        string[] rows = [.. lines.Where(l => l.TrimStart().StartsWith('{') && l.Contains("\"archetype\"", StringComparison.Ordinal))];

        Assert.Equal(40, rows.Length);

        foreach (FallbackPlanEntry entry in Table.Plans)
        {
            string id = s_data.Archetypes[entry.Archetype].Id;

            Assert.Contains(rows, r => r.Contains($"\"archetype\":\"{id}\"", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Fallback_RejectsMismatchedArchetype()
    {
        const string Json = """
            { "version": 1, "plans": [
              { "id": "fb_wrong", "archetype": "blacksmith", "goal": "test_goal",
                "steps": [
                  { "action": "MoveTo", "args": { "poi": "$home" } },
                  { "action": "Rest", "args": { "duration_s": 600 } },
                  { "action": "Wait", "args": { "duration_s": 60 } }
                ], "loop": true }
            ] }
            """;

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PlanTable.Parse(Json, s_data));

        Assert.Contains("fb_blacksmith", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fallback_RejectsPlanThatFailsValidation()
    {
        // 대장장이는 Fish 를 못 쓴다 → 2단에서 걸린다.
        const string Json = """
            { "version": 1, "plans": [
              { "id": "fb_blacksmith", "archetype": "blacksmith", "goal": "test_goal",
                "steps": [
                  { "action": "Fish", "args": { "count": 1 } },
                  { "action": "Rest", "args": { "duration_s": 600 } },
                  { "action": "Wait", "args": { "duration_s": 60 } }
                ], "loop": true }
            ] }
            """;

        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => PlanTable.Parse(Json, s_data));

        Assert.Contains("Vocabulary", ex.Message, StringComparison.Ordinal);
    }
}
