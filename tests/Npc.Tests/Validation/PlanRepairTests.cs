using System.Text.Json;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;

namespace Npc.Tests.Validation;

/// <summary>
/// C-05 — 결정론 자동 수선.
///
/// <para>
/// <b>수선은 검증을 건너뛰는 것이 아니다.</b> 여기서 확인하는 것은 "고친 뒤 3단을 실제로
/// 통과하는가" 이지 "고쳤는가" 가 아니다 — 고쳤는데 여전히 반려되면 그것은 실패 코드를
/// 옮긴 것뿐이다.
/// </para>
///
/// <para>
/// <b>결정론이다.</b> 같은 입력이면 같은 출력이어야 한다. 아니면 "왜 이렇게 고쳤나" 에
/// 답할 수 없고, 회차마다 다른 플랜이 통과한다.
/// </para>
/// </summary>
public sealed class PlanRepairTests
{
    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    /// <summary>
    /// <b>장소 전제 미충족 → 그 앞에 <c>MoveTo</c>.</b> 실측 실패의 47.9% 가 이것이다.
    /// </summary>
    [Fact]
    public void Repair_InsertsMoveBeforeAPlaceRequirement()
    {
        // Pray 는 AtTemple 을 요구한다. 앞에 MoveTo 가 없다 —
        // few-shot 04 가 모델에게 가르치는 바로 그 실수다.
        (PlanDocument document, BucketKey bucket, ArchetypeId archetype) = Plan(
            "elder",
            """
            {"schema":1,"goal":"morning_prayer","loop":true,"steps":[
              {"action":"Pray","args":{"duration_s":900},"timeout_s":1200},
              {"action":"MoveTo","args":{"poi":"$home"},"timeout_s":600},
              {"action":"Sleep","args":{"until_time":"Morning"},"timeout_s":7200}]}
            """);

        ValidationResult before = CoherenceValidator.Validate(document, bucket, archetype, s_data);

        Assert.False(before.IsValid);
        Assert.Equal("V3.PRECONDITION_UNMET", before.Code);

        RepairResult repair = PlanRepair.TryRepair(document, before, s_data, archetype);

        Assert.True(repair.Repaired, repair.Detail);
        Assert.Equal("InsertMove", repair.Rule);
        Assert.Equal(document.Steps.Length + 1, repair.Document.Steps.Length);
        Assert.Equal("MoveTo", repair.Document.Steps[0].Action);
        Assert.Equal("$temple", repair.Document.Steps[0].Args["poi"].GetString());

        // <b>고친 뒤 실제로 통과해야 한다.</b> 여기가 이 테스트의 요점이다.
        ValidationResult after = CoherenceValidator.Validate(repair.Document, bucket, archetype, s_data);

        Assert.True(
            after.IsValid,
            $"수선했는데 3단을 여전히 통과하지 못한다 — {after.Code} step {after.StepIndex}: {after.Detail}");
    }

    /// <summary>
    /// <b>같은 입력이면 같은 출력이다.</b> 심볼을 고르는 순서가 흔들리면 회차마다
    /// 다른 플랜이 통과한다.
    /// </summary>
    [Fact]
    public void Repair_IsDeterministic()
    {
        (PlanDocument document, BucketKey bucket, ArchetypeId archetype) = Plan(
            "elder",
            """
            {"schema":1,"goal":"morning_prayer","loop":true,"steps":[
              {"action":"Pray","args":{"duration_s":900},"timeout_s":1200},
              {"action":"MoveTo","args":{"poi":"$home"},"timeout_s":600},
              {"action":"Sleep","args":{"until_time":"Morning"},"timeout_s":7200}]}
            """);

        ValidationResult failure = CoherenceValidator.Validate(document, bucket, archetype, s_data);

        string once = Render(PlanRepair.TryRepair(document, failure, s_data, archetype).Document);
        string twice = Render(PlanRepair.TryRepair(document, failure, s_data, archetype).Document);

        Assert.Equal(once, twice);
    }

    /// <summary>
    /// <b>앞 스텝이 이미 그리로 가면 넣지 않는다.</b> 넣으면 <c>V3.DEGENERATE</c> 로 옮겨 갈 뿐이다.
    /// </summary>
    [Fact]
    public void Repair_DoesNotDuplicateAnExistingMove()
    {
        (PlanDocument document, _, ArchetypeId archetype) = Plan(
            "blacksmith",
            """
            {"schema":1,"goal":"forge_now","loop":true,"steps":[
              {"action":"MoveTo","args":{"poi":"$workplace"},"timeout_s":600},
              {"action":"Craft","args":{"recipe":"iron_sword","count":1},"timeout_s":1800},
              {"action":"Sleep","args":{"until_time":"Morning"},"timeout_s":7200}]}
            """);

        // 스텝 1 이 AtWorkplace 를 요구한다고 가정한 인위적 실패.
        var failure = ValidationResult.Fail(
            ValidationStage.Coherence, "V3.PRECONDITION_UNMET", 1,
            "Craft requires AtWorkplace but no preceding step grants it.");

        RepairResult repair = PlanRepair.TryRepair(document, failure, s_data, archetype);

        Assert.False(repair.Repaired);
        Assert.Contains("이미", repair.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>스텝 상한을 넘기지 않는다.</b> 넘기면 1단이 거절하므로 고친 것이 아니다.
    /// </summary>
    [Fact]
    public void Repair_GivesUpAtTheStepLimit()
    {
        var steps = new List<string>(PlanDocument.MaxSteps)
        {
            """{"action":"Craft","args":{"recipe":"iron_sword","count":1},"timeout_s":1800}""",
        };

        while (steps.Count < PlanDocument.MaxSteps)
        {
            steps.Add("""{"action":"Wait","args":{"duration_s":60},"timeout_s":120}""");
        }

        (PlanDocument document, BucketKey bucket, ArchetypeId archetype) = Plan(
            "blacksmith",
            $$"""{"schema":1,"goal":"forge_now","loop":false,"steps":[{{string.Join(",", steps)}}]}""");

        ValidationResult failure = CoherenceValidator.Validate(document, bucket, archetype, s_data);

        Assert.False(failure.IsValid);

        RepairResult repair = PlanRepair.TryRepair(document, failure, s_data, archetype);

        Assert.False(repair.Repaired);
        Assert.Contains("상한", repair.Detail, StringComparison.Ordinal);
    }

    /// <summary>규칙이 없는 코드는 건드리지 않는다. <b>모르는 실패를 억지로 고치지 않는다.</b></summary>
    [Fact]
    public void Repair_SkipsCodesWithoutARule()
    {
        (PlanDocument document, _, ArchetypeId archetype) = Plan(
            "blacksmith",
            """
            {"schema":1,"goal":"forge_now","loop":true,"steps":[
              {"action":"MoveTo","args":{"poi":"$workplace"},"timeout_s":600},
              {"action":"Craft","args":{"recipe":"iron_sword","count":1},"timeout_s":1800},
              {"action":"Sleep","args":{"until_time":"Morning"},"timeout_s":7200}]}
            """);

        RepairResult repair = PlanRepair.TryRepair(
            document,
            ValidationResult.Fail(ValidationStage.Coherence, "V3.DEGENERATE", 1, "repeats"),
            s_data,
            archetype);

        Assert.False(repair.Repaired);
        Assert.Contains("수선 규칙이 없다", repair.Detail, StringComparison.Ordinal);

        // 통과한 플랜도 건드리지 않는다.
        Assert.False(PlanRepair.TryRepair(document, ValidationResult.Ok, s_data, archetype).Repaired);
    }

    /// <summary>
    /// <b>수지 불균형은 쓰는 쪽을 줄인다.</b> 더 모으게 고치면 스텝이 늘고 어느 채집 액션을
    /// 쓸지는 아키타입마다 다르다 — 확실히 맞는 쪽을 고른다.
    /// </summary>
    [Fact]
    public void Repair_LowersTheCountOnResourceImbalance()
    {
        (PlanDocument document, _, ArchetypeId archetype) = Plan(
            "baker",
            """
            {"schema":1,"goal":"bake_batch","loop":true,"steps":[
              {"action":"MoveTo","args":{"poi":"$workplace"},"timeout_s":600},
              {"action":"Work","args":{"recipe":"flour","count":1},"timeout_s":900},
              {"action":"Work","args":{"recipe":"bread","count":3},"timeout_s":1800}]}
            """);

        var failure = ValidationResult.Fail(
            ValidationStage.Coherence, "V3.RESOURCE_IMBALANCE", 2,
            "The plan gathers 2 flour but consumes 6. Gather more flour before this step, or lower the count.");

        RepairResult repair = PlanRepair.TryRepair(document, failure, s_data, archetype);

        Assert.True(repair.Repaired, repair.Detail);
        Assert.Equal("LowerCount", repair.Rule);

        // 6 - 2 = 4 초과. 3 - 4 는 음수라 하한 1 로 잘린다.
        Assert.Equal(1, repair.Document.Steps[2].Args["count"].GetInt32());

        // 다른 스텝은 그대로다.
        Assert.Equal(1, repair.Document.Steps[1].Args["count"].GetInt32());
    }

    /// <summary>수지 설명에서 숫자를 못 읽으면 고치지 않는다. <b>추측하지 않는다.</b></summary>
    [Fact]
    public void Repair_ReadsTheImbalanceNumbersOrGivesUp()
    {
        Assert.True(PlanRepair.TryReadImbalance("gathers 2 x but consumes 6.", out int a, out int b));
        Assert.Equal(2, a);
        Assert.Equal(6, b);

        Assert.False(PlanRepair.TryReadImbalance("not enough flour", out _, out _));
        Assert.False(PlanRepair.TryReadImbalance(string.Empty, out _, out _));
    }

    /// <summary>
    /// 실패 설명에서 플래그 이름을 읽는다. <b>이름은 <c>WorldFlagTable</c> 이 만든 것과 같다.</b>
    /// </summary>
    [Fact]
    public void Repair_ReadsFlagNamesFromTheDetail()
    {
        Assert.True(
            PlanRepair.TryReadFlags(
                "Craft requires AtWorkplace but no preceding step grants it.",
                out WorldFlags flags));

        Assert.Equal(WorldFlags.AtWorkplace, flags);
        Assert.False(PlanRepair.TryReadFlags("something else entirely", out _));
        Assert.False(PlanRepair.TryReadFlags(string.Empty, out _));
    }

    /// <summary>
    /// <b>이 아키타입이 못 가는 곳으로 고치지 않는다.</b> 억지로 넣으면
    /// <c>V3.UNREACHABLE_POI</c> 로 옮겨 갈 뿐이다.
    /// </summary>
    [Fact]
    public void Repair_RefusesSymbolsTheArchetypeCannotReach()
    {
        Assert.True(s_data.Archetypes.TryGet("child", out ArchetypeDef child));

        // 아이에게 일터는 없다 — $workplace 로 고칠 수 없어야 한다.
        Assert.False(
            PlanRepair.TryPickSymbol(WorldFlags.AtWorkplace, s_data, child.Code, out PoiSymbol symbol));

        Assert.Equal(PoiSymbol.None, symbol);
    }

    // ---------------------------------------------------------------- 도우미

    private static (PlanDocument Document, BucketKey Bucket, ArchetypeId Archetype) Plan(
        string archetype, string json)
    {
        Assert.True(s_data.Archetypes.TryGet(archetype, out ArchetypeDef def));
        Assert.True(SchemaValidator.Validate(json, out PlanDocument? document).IsValid, json);

        return (
            document!,
            new BucketKey(def.Code, TimeOfDay.Morning, RegionState.Peace, Climate.Fair),
            def.Code);
    }

    private static string Render(PlanDocument document) =>
        JsonSerializer.Serialize(document, PlanJsonContext.Default.PlanDocument);
}
