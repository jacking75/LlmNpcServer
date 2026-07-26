using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;
using Npc.Tests.Fakes;

namespace Npc.Tests.Llm;

/// <summary>
/// <see cref="BucketNeighbors"/> 를 컴파일러에 물리는 어댑터.
/// 재사용 공급원 계약은 <c>Npc.Llm</c> 에 있고 구현은 <c>Npc.Planning</c> 을 아는 쪽이 갖는다 —
/// 실제 배선은 프리베이크 도구가 같은 모양으로 한다.
/// </summary>
internal sealed class NeighborReuseSource(PlanStore store, MasterDataSet data) : IPlanReuseSource
{
    public bool TryReuse(BucketKey target, out CompiledPlan? plan, out BucketKey source) =>
        BucketNeighbors.TryReuse(target, store, data, out plan, out source);
}

/// <summary>T2-17 — 폴백 경로. docs/12 §6 · CLAUDE.md §2.6.</summary>
public sealed class CompilerFallbackTests
{
    /// <summary>어떤 아키타입에서도 3단을 못 넘기는 산출물.</summary>
    private const string AlwaysFails = """
        {"schema":1,"goal":"forge_now","loop":true,"steps":[
          {"action":"Craft","args":{"recipe":"iron_sword","count":1}},
          {"action":"MoveTo","args":{"poi":"$home"}},
          {"action":"Sleep","args":{"until_time":"Morning"}}]}
        """;

    private const string ValidPlan = """
        {"schema":1,"goal":"forge_batch","loop":true,"steps":[
          {"action":"MoveTo","args":{"poi":"$workplace"},"timeout_s":600},
          {"action":"Work","args":{"recipe":"iron_sword","count":1},"timeout_s":1800},
          {"action":"Store","args":{"item":"iron_sword","count":1},"timeout_s":120},
          {"action":"MoveTo","args":{"poi":"$home"},"timeout_s":600},
          {"action":"Sleep","args":{"until_time":"Morning"}}]}
        """;

    private static LlmPlanCompiler Compiler(FakeChatClient client, IPlanReuseSource? reuse = null) =>
        new(
            LlmPlanCompilerTests.Data,
            LlmPlanCompilerTests.Prefix,
            LlmPlanCompilerTests.Options.Engine("gemini-3.1-flash-lite"),
            client,
            stats: null,
            dryRun: null,
            reuse: reuse);

    [Fact]
    public async Task Compiler_NeverReturnsNullPlan()
    {
        // LLM 이 전면 실패해도(호출 자체가 안 되어도) 플랜은 나온다.
        foreach (FakeChatClient client in new[]
        {
            new FakeChatClient(new HttpRequestException("503 Service Unavailable")),
            new FakeChatClient("garbage"),
            new FakeChatClient(AlwaysFails),
        })
        {
            for (int archetype = 0; archetype < BucketKey.ArchetypeCount; archetype++)
            {
                var bucket = new BucketKey(
                    new ArchetypeId((ushort)archetype), TimeOfDay.Morning, RegionState.Peace, Climate.Fair);

                PlanCompileResult result = await Compiler(client).CompileAsync(
                    new PlanRequest(bucket, LlmPlanCompilerTests.Data.InitialFlags(bucket)),
                    CancellationToken.None);

                Assert.NotNull(result.Plan);
                Assert.Equal(PlanOrigin.Fallback, result.Plan!.Origin);
                Assert.Equal(bucket, result.Plan.Bucket);

                // 실패 이유는 남는다 — 이게 통과율 집계의 원자료다.
                Assert.False(result.Validation.IsValid);
                Assert.False(string.IsNullOrEmpty(result.Validation.Code));
            }
        }
    }

    [Fact]
    public async Task Compiler_PrefersNeighborReuseOverFallback()
    {
        BucketKey source = Bucket(TimeOfDay.Evening, RegionState.Peace, Climate.Fair);
        BucketKey target = Bucket(TimeOfDay.Evening, RegionState.War, Climate.Cold);

        PlanStore store = PlanStore.CreateIdleOnly(LlmPlanCompilerTests.Data);
        Assert.True(SchemaValidator.Validate(ValidPlan, out PlanDocument? document).IsValid);

        store.SetBucket(
            source,
            store.Register(PlanCompiler.Compile(
                document!, source, default, LlmPlanCompilerTests.Data, sourceJson: ValidPlan)));

        var reuse = new NeighborReuseSource(store, LlmPlanCompilerTests.Data);

        PlanCompileResult result = await Compiler(new FakeChatClient(AlwaysFails), reuse).CompileAsync(
            new PlanRequest(target, LlmPlanCompilerTests.Data.InitialFlags(target)),
            CancellationToken.None);

        Assert.NotNull(result.Plan);
        Assert.Equal("forge_batch", result.Plan!.Goal);
        Assert.NotEqual(PlanOrigin.Fallback, result.Plan.Origin);
        Assert.Equal(target, result.Plan.Bucket);
    }

    [Fact]
    public async Task Compiler_FallsBackWhenNoNeighborFits()
    {
        BucketKey target = Bucket(TimeOfDay.Evening, RegionState.War, Climate.Cold);

        // 이웃 스토어가 비어 있다.
        var reuse = new NeighborReuseSource(
            PlanStore.CreateIdleOnly(LlmPlanCompilerTests.Data), LlmPlanCompilerTests.Data);

        PlanCompileResult result = await Compiler(new FakeChatClient(AlwaysFails), reuse).CompileAsync(
            new PlanRequest(target, LlmPlanCompilerTests.Data.InitialFlags(target)),
            CancellationToken.None);

        Assert.NotNull(result.Plan);
        Assert.Equal(PlanOrigin.Fallback, result.Plan!.Origin);
    }

    [Fact]
    public async Task Compiler_KeepsGeneratedPlanWhenItPasses()
    {
        BucketKey bucket = Bucket(TimeOfDay.Morning, RegionState.Peace, Climate.Fair);

        PlanCompileResult result = await Compiler(new FakeChatClient(ValidPlan)).CompileAsync(
            new PlanRequest(bucket, LlmPlanCompilerTests.Data.InitialFlags(bucket)),
            CancellationToken.None);

        Assert.True(result.Validation.IsValid);
        Assert.NotNull(result.Plan);
        Assert.Equal(PlanOrigin.Runtime, result.Plan!.Origin);
    }

    private static BucketKey Bucket(TimeOfDay time, RegionState region, Climate climate)
    {
        Assert.True(LlmPlanCompilerTests.Data.Archetypes.TryGet("blacksmith", out ArchetypeDef def));
        return new BucketKey(def.Code, time, region, climate);
    }
}
