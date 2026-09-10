using System.Text;
using Microsoft.Extensions.AI;
using Npc.Core;
using Npc.Core.Validation;
using Npc.Llm;

namespace Npc.Tests.Llm;

/// <summary>
/// T2-09 — 실제 LLM 호출. <b>비용이 발생한다.</b> CLAUDE.md §5 에 따라 Golden 카테고리다
/// (CI 기본 실행에서 빠지고 수동·릴리스 전에만 돈다).
///
/// 골든 테스트는 <b>속성 단언만</b> 쓴다. 정확한 문자열 비교를 하지 않는다 —
/// LLM 출력은 매번 다르고 그게 정상이다.
/// </summary>
[Trait("Category", "Golden")]
public sealed class LlmPlanCompilerGoldenTests
{
    /// <summary>docs/12 T2-09 의 완료 조건 — 임의 버킷 20건.</summary>
    private const int SampleSize = 20;

    /// <summary>2,880 과 서로소라 표본이 겹치지 않는다. 난수를 쓰면 재현이 안 된다 (CLAUDE.md §2.3).</summary>
    private const int Stride = 991;

    [Fact]
    public async Task Compiler_ProducesValidJson()
    {
        LlmEngineOptions engine = LlmPlanCompilerTests.Options.PreferredEngine();

        // xunit 2.x 에는 동적 skip 이 없다. 키가 없으면 부를 수 없으므로 그냥 끝낸다 —
        // 이 카테고리는 어차피 CI 기본 실행에서 빠진다 (CLAUDE.md §5).
        if (!engine.IsConfigured)
        {
            return;
        }

        (int valid, int passed, string detail) = await RunAsync(engine, CancellationToken.None);

        // 완료 조건: 유효 JSON 100%.
        Assert.True(valid == SampleSize, $"유효 JSON {valid}/{SampleSize} · 1·2단 통과 {passed}\n{detail}");
        Assert.True(passed > 0, detail);
    }

    /// <summary>
    /// 두 디코딩 모드의 1·2단 통과율을 나란히 잰다 (T2-09 완료 조건).
    /// 결과는 <c>docs/measurements/W6_compile_stats.md §2</c> 에 옮긴다.
    /// </summary>
    [Fact]
    public async Task Compiler_ComparesDecodingModes()
    {
        LlmEngineOptions engine = LlmPlanCompilerTests.Options.PreferredEngine();

        // xunit 2.x 에는 동적 skip 이 없다. 키가 없으면 부를 수 없으므로 그냥 끝낸다 —
        // 이 카테고리는 어차피 CI 기본 실행에서 빠진다 (CLAUDE.md §5).
        if (!engine.IsConfigured)
        {
            return;
        }

        var report = new StringBuilder();

        foreach (bool forced in new[] { false, true })
        {
            (int valid, int passed, string detail) =
                await RunAsync(engine with { ForceJsonSchema = forced }, CancellationToken.None);

            report.AppendLine(
                $"{engine.Id} [{(forced ? "forced" : "prompt")}] 유효 JSON {valid}/{SampleSize} · "
                + $"1·2단 통과 {passed}/{SampleSize}");
            report.AppendLine(detail);
        }

        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "decoding_modes.txt"), report.ToString(), Encoding.UTF8);

        Assert.NotEmpty(report.ToString());
    }

    private static async Task<(int Valid, int Passed, string Detail)> RunAsync(
        LlmEngineOptions engine, CancellationToken cancellationToken)
    {
        using IChatClient client = ChatClientFactory.Create(engine);
        var compiler = new LlmPlanCompiler(
            LlmPlanCompilerTests.Data, LlmPlanCompilerTests.Prefix, engine, client);

        int valid = 0;
        int passed = 0;
        var detail = new StringBuilder();

        for (int i = 0; i < SampleSize; i++)
        {
            BucketKey bucket = BucketKey.FromIndex(i * Stride % TestPaths.TotalKeys);

            PlanCompileResult result = await compiler.CompileAsync(
                new PlanRequest(bucket, LlmPlanCompilerTests.Data.InitialFlags(bucket)), cancellationToken);

            // 프리픽스 SHA 는 전 요청에서 1종이어야 한다 (P2 게이트).
            Assert.Equal(LlmPlanCompilerTests.Prefix.Sha256, result.Stats.PrefixSha);

            bool parsed = SchemaValidator.Validate(result.ResponseText, out _).IsValid;

            if (parsed)
            {
                valid++;
            }

            if (result.Validation.IsValid)
            {
                passed++;
            }
            else
            {
                detail.AppendLine(
                    $"  {bucket} {result.Validation.Code}@{result.Validation.StepIndex} {result.Validation.Detail}");
            }
        }

        return (valid, passed, detail.ToString());
    }
}
