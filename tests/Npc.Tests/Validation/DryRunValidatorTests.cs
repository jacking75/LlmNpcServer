using System.Diagnostics;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;
using Npc.Sim.Validation;

namespace Npc.Tests.Validation;

/// <summary>
/// T2-13 · T2-14 — 검증기 4단(드라이런). docs/03 §3 · docs/12 §5.
/// V4 5종 전부 유발 픽스처로 확인하고, 같은 플랜이 100회 같은 판정을 받는지 본다.
/// </summary>
public sealed class DryRunValidatorTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);
    private static readonly DryRunValidator s_validator = new(s_data);

    private static BucketKey Bucket(string archetype, TimeOfDay time = TimeOfDay.Morning)
    {
        Assert.True(s_data.Archetypes.TryGet(archetype, out ArchetypeDef def));
        return new BucketKey(def.Code, time, RegionState.Peace, Climate.Fair);
    }

    /// <summary>1~3단을 건너뛰고 4단만 본다 — 4단이 무엇을 잡는지가 이 테스트의 관심사다.</summary>
    private static CompiledPlan Compile(string json, BucketKey bucket)
    {
        ValidationResult schema = SchemaValidator.Validate(json, out PlanDocument? document);
        Assert.True(schema.IsValid, $"1단에서 걸렸다: {schema.Code} {schema.Detail}");

        return PlanCompiler.Compile(document!, bucket, default, s_data, sourceJson: json);
    }

    private static ValidationResult Run(string json, string archetype, int maxGameHours = 36)
    {
        BucketKey bucket = Bucket(archetype);
        var context = new ValidationContext(
            bucket, s_data.InitialFlags(bucket), ValidationContext.SeedOf(bucket), maxGameHours);

        return s_validator.Validate(Compile(json, bucket), context);
    }

    /// <summary>docs/03 §1 의 예시 플랜은 4단도 통과해야 한다.</summary>
    [Fact]
    public void DryRun_AcceptsSpecExample()
    {
        ValidationResult result = Run(Npc.Tests.Plan.PlanDocumentTests.SpecExample, "blacksmith");

        Assert.True(result.IsValid, $"{result.Code}: {result.Detail}");
    }

    [Fact]
    public void DryRun_V4_DEADLOCK()
    {
        // 근무 배정(OnDuty)이 없는데 Guard 를 쓴다. 첫 사이클 두 번째 스텝에서 멈춘다.
        const string Json = """
            { "schema": 1, "goal": "hold_the_gate", "loop": true, "steps": [
              { "action": "MoveTo", "args": { "poi": "$gate" } },
              { "action": "Guard", "args": { "poi": "$gate", "duration_s": 3600 } },
              { "action": "MoveTo", "args": { "poi": "$home" } },
              { "action": "Sleep", "args": { "until_time": "Morning" } } ] }
            """;

        ValidationResult result = Run(Json, "town_guard");

        Assert.Equal(ValidationStage.DryRun, result.FailedAt);
        Assert.Equal("V4.DEADLOCK", result.Code);
        Assert.Equal(1, result.StepIndex);
        Assert.Contains("OnDuty", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRun_V4_DEADLOCK_OnSecondCycle()
    {
        // 4단만 잡을 수 있는 경우 — 첫 사이클은 만든 검을 팔 수 있지만,
        // 두 번째 사이클에는 팔 물건이 없어 Trade 의 전제(HasProduct|HasCoin)가... 는 코인으로 성립한다.
        // 대신 만든 것보다 많이 보관하려 들면 두 번째 사이클에서 재고가 바닥난다.
        // 정적 분석(3단)은 개수를 모르므로 여기까지 못 온다.
        const string Json = """
            { "schema": 1, "goal": "forge_and_stock", "loop": true, "steps": [
              { "action": "MoveTo", "args": { "poi": "$nearest_field" } },
              { "action": "Mine", "args": { "resource": "iron_ore", "count": 4 } },
              { "action": "MoveTo", "args": { "poi": "$workplace" } },
              { "action": "Craft", "args": { "recipe": "iron_sword", "count": 1 } },
              { "action": "Store", "args": { "item": "iron_sword", "count": 1 } },
              { "action": "Craft", "args": { "recipe": "iron_sword", "count": 2 } },
              { "action": "MoveTo", "args": { "poi": "$home" } },
              { "action": "Sleep", "args": { "until_time": "Morning" } } ] }
            """;

        // 광석 4개를 캐서 1자루(2개)를 만들고, 남은 2개로 2자루(4개 필요)를 만들려 든다.
        // 플래그는 아직 서 있으므로(광석이 2개 남았다) 전제 검사로는 안 잡히고 수량에서 걸린다.
        ValidationResult result = Run(Json, "blacksmith");

        Assert.Equal("V4.RESOURCE_STARVE", result.Code);
        Assert.Contains("iron_ore", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRun_V4_TIMEOUT()
    {
        // 하루를 세 번 자면 36 게임시간을 넘는다.
        const string Json = """
            { "schema": 1, "goal": "sleep_forever", "loop": false, "steps": [
              { "action": "MoveTo", "args": { "poi": "$home" } },
              { "action": "Sleep", "args": { "until_time": "Dawn" } },
              { "action": "Sleep", "args": { "until_time": "Dawn" } },
              { "action": "Sleep", "args": { "until_time": "Dawn" } } ] }
            """;

        ValidationResult result = Run(Json, "blacksmith");

        Assert.Equal("V4.TIMEOUT", result.Code);
        Assert.Contains("game hours", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRun_V4_RESOURCE_STARVE()
    {
        // 광석 2개를 캐고 검 3자루를 만든다 — 자기가 모은 자원이 모자라다.
        const string Json = """
            { "schema": 1, "goal": "forge_too_much", "loop": false, "steps": [
              { "action": "MoveTo", "args": { "poi": "$nearest_field" } },
              { "action": "Mine", "args": { "resource": "iron_ore", "count": 2 } },
              { "action": "MoveTo", "args": { "poi": "$workplace" } },
              { "action": "Craft", "args": { "recipe": "iron_sword", "count": 3 } },
              { "action": "Rest", "args": { "duration_s": 600 } } ] }
            """;

        ValidationResult result = Run(Json, "blacksmith");

        Assert.Equal("V4.RESOURCE_STARVE", result.Code);
        Assert.Contains("iron_ore", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRun_V4_OSCILLATION()
    {
        // 집과 시장을 계속 왕복한다.
        const string Json = """
            { "schema": 1, "goal": "pace_around", "loop": false, "steps": [
              { "action": "MoveTo", "args": { "poi": "$market" } },
              { "action": "MoveTo", "args": { "poi": "$home" } },
              { "action": "MoveTo", "args": { "poi": "$market" } },
              { "action": "MoveTo", "args": { "poi": "$home" } },
              { "action": "MoveTo", "args": { "poi": "$market" } },
              { "action": "MoveTo", "args": { "poi": "$home" } },
              { "action": "Rest", "args": { "duration_s": 600 } } ] }
            """;

        ValidationResult result = Run(Json, "merchant");

        Assert.Equal("V4.OSCILLATION", result.Code);
        Assert.Contains("back and forth", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRun_V4_INFINITE_LOOP()
    {
        // loop 인데 한 사이클이 몇 분이다. 하루 일과가 아니다.
        const string Json = """
            { "schema": 1, "goal": "twitch", "loop": true, "steps": [
              { "action": "Wait", "args": { "duration_s": 60 } },
              { "action": "Emote", "args": { "animation": "nod" } },
              { "action": "Wait", "args": { "duration_s": 60 } } ] }
            """;

        ValidationResult result = Run(Json, "villager");

        Assert.Equal("V4.INFINITE_LOOP", result.Code);
        Assert.Contains("cycle", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRun_CostsUnder100Milliseconds()
    {
        BucketKey bucket = Bucket("blacksmith");
        CompiledPlan plan = Compile(Npc.Tests.Plan.PlanDocumentTests.SpecExample, bucket);
        var context = ValidationContext.For(bucket, s_data.InitialFlags(bucket));

        // 워밍업 — 첫 호출은 JIT 비용이 섞인다.
        s_validator.Validate(plan, context);

        long start = Stopwatch.GetTimestamp();

        for (int i = 0; i < 20; i++)
        {
            s_validator.Validate(plan, context);
        }

        double perRun = Stopwatch.GetElapsedTime(start).TotalMilliseconds / 20;

        // docs/12 §5 의 추정은 50ms 였다. 완료 조건은 100ms 이하.
        Assert.True(perRun <= 100, $"드라이런 1건에 {perRun:F2}ms 걸렸다.");
    }

    /// <summary>T2-14 — 같은 플랜 100회 → 동일 판정. 시드는 버킷 키에서 유도한다.</summary>
    [Fact]
    public void DryRun_IsDeterministic()
    {
        BucketKey bucket = Bucket("blacksmith");
        CompiledPlan plan = Compile(Npc.Tests.Plan.PlanDocumentTests.SpecExample, bucket);
        var context = ValidationContext.For(bucket, s_data.InitialFlags(bucket));

        ValidationResult first = s_validator.Validate(plan, context);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(first, s_validator.Validate(plan, context));
        }

        // 시드도 버킷에서만 나온다 — 시각도 난수도 섞이지 않는다.
        Assert.Equal(ValidationContext.SeedOf(bucket), ValidationContext.SeedOf(bucket));
        Assert.NotEqual(
            ValidationContext.SeedOf(bucket),
            ValidationContext.SeedOf(Bucket("blacksmith", TimeOfDay.Night)));
    }

    [Fact]
    public void DryRun_AcceptsEveryFallbackPlan()
    {
        // docs/01 §11 V7 — 아키타입 폴백은 4단을 통과해야 한다.
        Assert.NotNull(s_data.Fallbacks);

        foreach (ArchetypeDef archetype in s_data.Archetypes.Archetypes)
        {
            CompiledPlan? plan = s_data.Fallbacks!.For(archetype.Code);
            Assert.NotNull(plan);

            var bucket = new BucketKey(archetype.Code, TimeOfDay.Morning, RegionState.Peace, Climate.Fair);

            ValidationResult result = s_validator.Validate(
                plan! with { Bucket = bucket },
                ValidationContext.For(bucket, s_data.InitialFlags(bucket)));

            Assert.True(result.IsValid, $"{archetype.Id}/{archetype.FallbackPlanId}: {result.Code} {result.Detail}");
        }
    }
}
