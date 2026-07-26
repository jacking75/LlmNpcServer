using Npc.Contracts;
using Npc.Core;
using Npc.Core.Validation;
using Npc.Llm;
using Npc.Tests.Fakes;

namespace Npc.Tests.Llm;

/// <summary>
/// T2-11 — 검증기 1·2단 결선. docs/03 §3 · docs/12 §5 · docs/03 §8 의 <c>Validator_Fuzz</c>.
///
/// <b>검증 우회 경로가 하나도 없어야 한다.</b> 모델이 무엇을 뱉든
/// (1) 컴파일러가 예외를 던지지 않고, (2) 통과하지 못한 산출물에서 플랜이 나오지 않고,
/// (3) 실패에는 반드시 코드가 붙어야 한다 — 코드 없는 실패는 T2-20 집계에서 사라진다.
/// </summary>
public sealed class CompilerValidationFuzzTests
{
    private const string Seed = """
        {"schema":1,"goal":"forge_batch","reasoning":"seed","loop":true,"steps":[
          {"action":"MoveTo","args":{"poi":"$nearest_field","speed":"walk"},"timeout_s":600},
          {"action":"Mine","args":{"resource":"iron_ore","count":6},"timeout_s":1800},
          {"action":"MoveTo","args":{"poi":"$workplace"},"timeout_s":600},
          {"action":"Craft","args":{"recipe":"iron_sword","count":3},"timeout_s":1800},
          {"action":"Store","args":{"item":"iron_sword","count":3},"timeout_s":120},
          {"action":"MoveTo","args":{"poi":"$home"},"timeout_s":600},
          {"action":"Sleep","args":{"until_time":"Morning"}}]}
        """;

    /// <summary>사양 예시를 결정론적으로 망가뜨리는 변형들. 각각 어느 코드로 잡히는지는 묻지 않는다.</summary>
    private static readonly (string Name, Func<string, string> Mutate)[] s_mutations =
    [
        ("as-is", s => s),
        ("truncate", s => s[..(s.Length / 2)]),
        ("drop-braces", s => s.Replace("}", string.Empty, StringComparison.Ordinal)),
        ("unknown-action", s => s.Replace("\"Mine\"", "\"Excavate\"", StringComparison.Ordinal)),
        ("forbidden-action", s => s.Replace("\"Mine\"", "\"Guard\"", StringComparison.Ordinal)),
        ("unknown-arg", s => s.Replace("\"speed\":\"walk\"", "\"recipe\":\"iron_sword\"", StringComparison.Ordinal)),
        ("hallucinated-poi", s => s.Replace("\"$workplace\"", "\"smithy_01\"", StringComparison.Ordinal)),
        ("unknown-item", s => s.Replace("\"iron_ore\"", "\"mithril\"", StringComparison.Ordinal)),
        ("out-of-range", s => s.Replace("\"count\":3", "\"count\":9999", StringComparison.Ordinal)),
        ("missing-required", s => s.Replace("\"resource\":\"iron_ore\",", string.Empty, StringComparison.Ordinal)),
        ("extra-field", s => s.Replace("\"schema\":1,", "\"schema\":1,\"npc\":\"bob\",", StringComparison.Ordinal)),
        ("bad-goal", s => s.Replace("\"forge_batch\"", "\"Forge Batch!\"", StringComparison.Ordinal)),
        ("wrong-schema", s => s.Replace("\"schema\":1", "\"schema\":2", StringComparison.Ordinal)),
        ("one-step", s => """{"schema":1,"goal":"nap","loop":false,"steps":[{"action":"Rest","args":{}}]}"""),
        ("empty", _ => string.Empty),
        ("prose", _ => "Sure! Here is a plan for the blacksmith."),
        ("fenced", s => "```json\n" + s + "\n```"),
        ("array-root", s => "[" + s + "]"),
        ("null-args", s => s.Replace("\"args\":{}", "\"args\":null", StringComparison.Ordinal)),
        ("timeout-zero", s => s.Replace("\"timeout_s\":600", "\"timeout_s\":0", StringComparison.Ordinal)),
    ];

    [Fact]
    public async Task Compiler_NeverReturnsPlanWithoutPassingValidation()
    {
        var collector = new CompileStatsCollector();
        int compiled = 0;

        // 전 아키타입 × 전 변형. 아키타입마다 allowed_actions 가 달라 걸리는 코드도 달라진다.
        for (int archetype = 0; archetype < BucketKey.ArchetypeCount; archetype++)
        {
            var bucket = new BucketKey(
                new ArchetypeId((ushort)archetype),
                TimeOfDay.Morning,
                RegionState.Peace,
                Climate.Fair);

            var request = new PlanRequest(bucket, LlmPlanCompilerTests.Data.InitialFlags(bucket));

            foreach ((string name, Func<string, string> mutate) in s_mutations)
            {
                var compiler = new LlmPlanCompiler(
                    LlmPlanCompilerTests.Data,
                    LlmPlanCompilerTests.Prefix,
                    LlmPlanCompilerTests.Options.Engine("gemini-3.1-flash-lite"),
                    new FakeChatClient(mutate(Seed)),
                    collector);

                PlanCompileResult result = await compiler.CompileAsync(request, CancellationToken.None);

                string where = $"{bucket} [{name}]";

                // (2) 통과하지 못한 산출물에서 플랜이 나오면 안 된다. 통과했으면 반드시 나와야 한다.
                Assert.Equal(result.Validation.IsValid, result.Plan is not null);

                if (result.Validation.IsValid)
                {
                    compiled++;
                    continue;
                }

                // (3) 실패에는 반드시 코드가 붙는다.
                Assert.False(string.IsNullOrEmpty(result.Validation.Code), where);
                Assert.NotEqual(ValidationStage.None, result.Validation.FailedAt);
            }
        }

        // 변형이 전부 실패로만 끝나면 테스트가 아무것도 지키지 못한다 — 통과 경로도 최소한 하나 돌아야 한다.
        Assert.True(compiled > 0, "통과한 조합이 하나도 없다. 시드 플랜이 이미 깨져 있다.");

        // 모든 호출이 기록됐다. 통과한 건은 1회, 실패한 건은 재시도까지 2회다 (docs/12 §6).
        int total = BucketKey.ArchetypeCount * s_mutations.Length;
        Assert.Equal((total * 2) - compiled, collector.Calls);
        Assert.Equal(compiled, collector.Passed);
        Assert.Equal(1, collector.UniquePrefixHashes);
    }

    [Fact]
    public async Task Compiler_TagsFailuresByStageCodeAndArchetype()
    {
        var collector = new CompileStatsCollector();

        foreach (string archetypeId in new[] { "blacksmith", "farmer", "town_guard" })
        {
            Assert.True(LlmPlanCompilerTests.Data.Archetypes.TryGet(archetypeId, out Npc.MasterData.ArchetypeDef def));

            var bucket = new BucketKey(def.Code, TimeOfDay.Morning, RegionState.Peace, Climate.Fair);
            var compiler = new LlmPlanCompiler(
                LlmPlanCompilerTests.Data,
                LlmPlanCompilerTests.Prefix,
                LlmPlanCompilerTests.Options.Engine("gemini-3.1-flash-lite"),
                new FakeChatClient(Seed.Replace("\"Mine\"", "\"Excavate\"", StringComparison.Ordinal)),
                collector);

            await compiler.CompileAsync(
                new PlanRequest(bucket, LlmPlanCompilerTests.Data.InitialFlags(bucket)),
                CancellationToken.None);
        }

        // 같은 코드라도 아키타입이 다르면 다른 칸에 쌓인다 — 그래야 §8 처방을 어디에 적용할지 보인다.
        Assert.Equal(3, collector.Failures.Length);
        Assert.All(collector.Failures, f => Assert.Equal("V2.UNKNOWN_ACTION", f.Key.Code));
        Assert.All(collector.Failures, f => Assert.Equal(ValidationStage.Vocabulary, f.Key.Stage));
        Assert.Equal(3, collector.Failures.Select(f => f.Key.Archetype).Distinct().Count());
    }
}
