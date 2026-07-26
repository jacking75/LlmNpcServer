using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using Npc.Core;
using Npc.Core.Validation;
using Npc.Llm;
using Npc.Tests.Fakes;

namespace Npc.Tests.Llm;

/// <summary>T2-10 — 계측 수집. docs/12 §2 · §5.</summary>
public sealed class CompileStatsTests
{
    private const string ValidPlan = """
        {"schema":1,"goal":"forge_batch","loop":true,"steps":[
          {"action":"MoveTo","args":{"poi":"$workplace"},"timeout_s":600},
          {"action":"Work","args":{"recipe":"iron_sword","count":2},"timeout_s":1800},
          {"action":"Store","args":{"item":"iron_sword","count":2},"timeout_s":120},
          {"action":"MoveTo","args":{"poi":"$home"},"timeout_s":600},
          {"action":"Sleep","args":{"until_time":"Morning"}}]}
        """;

    private const string UnknownAction = """
        {"schema":1,"goal":"digging","loop":true,"steps":[
          {"action":"Excavate","args":{}},{"action":"Rest","args":{}},{"action":"Wait","args":{}}]}
        """;

    [Fact]
    public async Task Stats_RecordedOnEveryCall()
    {
        var collector = new CompileStatsCollector();
        BucketKey bucket = LlmPlanCompilerTests.BlacksmithMorning();
        var request = new PlanRequest(bucket, LlmPlanCompilerTests.Data.InitialFlags(bucket));

        // 성공 · 어휘 실패 · 호출 실패 — 셋 다 기록돼야 한다.
        await Compile(new FakeChatClient(ValidPlan), collector, request);
        await Compile(new FakeChatClient(UnknownAction), collector, request);
        await Compile(new FakeChatClient(new HttpRequestException("429 Too Many Requests")), collector, request);

        // 성공은 1회, 실패한 둘은 재시도까지 2회씩 = 5회 (docs/12 §6 — 재시도는 1회만).
        Assert.Equal(5, collector.Calls);
        Assert.Equal(1, collector.Passed);
        Assert.Equal(2, collector.CallFailures);

        // 토큰·비용은 서버에 닿은 3건분만 쌓인다.
        Assert.Equal(36_000, collector.PromptTokens);
        Assert.Equal(34_500, collector.CachedTokens);
        Assert.Equal(1_200, collector.CompletionTokens);
        Assert.True(collector.CostUsd > 0);
        Assert.True(collector.LatencyMs >= 0);

        // 프리픽스는 한 종류여야 한다 (docs/01 §10.2).
        Assert.Equal(1, collector.UniquePrefixHashes);

        // 실패는 (stage, code, archetype) 3축으로 쌓인다 (docs/12 §5).
        ImmutableArray<KeyValuePair<FailureKey, int>> failures = collector.Failures;
        Assert.Equal(2, failures.Length);
        Assert.Contains(
            failures,
            f => f.Key == new FailureKey(ValidationStage.Vocabulary, "V2.UNKNOWN_ACTION", bucket.A.Value));
        Assert.Contains(
            failures,
            f => f.Key == new FailureKey(ValidationStage.Schema, "V0.CALL_FAILED", bucket.A.Value));

        // 1회 만에 성공한 건이 1건.
        Assert.Equal(1, collector.AttemptHistogram[0]);
    }

    [Fact]
    public async Task Stats_ForwardToMeterWithPrefixShaTag()
    {
        using var meter = new CompileMeter();
        var collector = new CompileStatsCollector(meter);

        var tags = new List<KeyValuePair<string, object?>>();
        long calls = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == CompileMeter.MeterName && instrument.Name == "npc.llm.calls")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, measurementTags, _) =>
        {
            calls += measurement;
            tags.AddRange(measurementTags.ToArray());
        });
        listener.Start();

        BucketKey bucket = LlmPlanCompilerTests.BlacksmithMorning();
        await Compile(
            new FakeChatClient(ValidPlan),
            collector,
            new PlanRequest(bucket, LlmPlanCompilerTests.Data.InitialFlags(bucket)));

        listener.RecordObservableInstruments();

        Assert.Equal(1, calls);
        Assert.Contains(tags, t => t.Key == "prefix_sha" && (string?)t.Value == LlmPlanCompilerTests.Prefix.Sha256);
        Assert.Contains(tags, t => t.Key == "model");
        Assert.Contains(tags, t => t.Key == "attempt");
    }

    [Fact]
    public void Stats_AccumulateSumsRetryCost()
    {
        var first = new CompileStats(12_000, 11_000, 300, 2_000, 0.001, "m", 1, "sha", false, null);
        var second = new CompileStats(12_200, 11_000, 400, 2_500, 0.0012, "m", 2, "sha", false, null);

        CompileStats total = first.Accumulate(second);

        Assert.Equal(24_200, total.PromptTokens);
        Assert.Equal(700, total.CompletionTokens);
        Assert.Equal(4_500, total.LatencyMs);
        Assert.Equal(0.0022, total.CostUsd, 6);
        Assert.Equal(2, total.Attempt);
    }

    [Fact]
    public void Stats_UniquePrefixHashesDetectsDrift()
    {
        var collector = new CompileStatsCollector();
        BucketKey bucket = LlmPlanCompilerTests.BlacksmithMorning();

        collector.Record(
            bucket,
            new CompileStats(1, 0, 1, 1, 0, "m", 1, "aaa", false, null),
            ValidationResult.Ok);
        collector.Record(
            bucket,
            new CompileStats(1, 0, 1, 1, 0, "m", 1, "bbb", false, null),
            ValidationResult.Ok);

        // 캐시가 깨졌다는 신호. 대시보드가 이 값을 상시 본다.
        Assert.Equal(2, collector.UniquePrefixHashes);
    }

    private static async Task Compile(
        FakeChatClient client, CompileStatsCollector collector, PlanRequest request)
    {
        var compiler = new LlmPlanCompiler(
            LlmPlanCompilerTests.Data,
            LlmPlanCompilerTests.Prefix,
            LlmPlanCompilerTests.Options.Engine("gemini-3.1-flash-lite"),
            client,
            collector);

        await compiler.CompileAsync(request, CancellationToken.None);
    }
}
