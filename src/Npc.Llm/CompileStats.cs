using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using Npc.Core;
using Npc.Core.Validation;

namespace Npc.Llm;

/// <summary>
/// 컴파일 계측을 받는 쪽. docs/12 §2 · §5.
/// <b>모든 호출에서 불린다</b> — 성공도 실패도, 재시도 하나하나도.
/// 실패만 빠지면 통과율의 분모가 조용히 줄어든다.
/// </summary>
public interface ICompileStatsSink
{
    /// <summary>호출 하나를 기록한다.</summary>
    /// <param name="bucket">어느 버킷의 요청이었나.</param>
    /// <param name="stats">이 호출의 계측값.</param>
    /// <param name="validation">이 호출의 검증 결과.</param>
    void Record(BucketKey bucket, in CompileStats stats, in ValidationResult validation);
}

/// <summary>실패 하나의 3축 좌표. docs/12 §5 · §8 의 집계 축이다.</summary>
/// <param name="Stage">검증 단계.</param>
/// <param name="Code">실패 코드.</param>
/// <param name="Archetype">아키타입 code.</param>
public readonly record struct FailureKey(ValidationStage Stage, string Code, int Archetype);

/// <summary>
/// 계측 누계. P3 의 프리베이크 manifest(docs/03 §7)와 W12 보고서의 원자료다.
///
/// 여러 워커가 동시에 기록하므로 잠금으로 묶는다. <b>이 코드는 틱 루프 밖이다</b> —
/// CLAUDE.md §2.1 의 lock 금지는 틱 루프 규칙이고, 여기는 재계획 워커·프리베이크 쪽이다.
/// </summary>
public sealed class CompileStatsCollector : ICompileStatsSink
{
    private readonly Lock _gate = new();
    private readonly ICompileStatsSink? _inner;
    private readonly Dictionary<FailureKey, int> _failures = [];
    private readonly HashSet<string> _prefixHashes = new(StringComparer.Ordinal);
    private readonly List<int> _attemptCounts = [];

    /// <summary>누계기를 만든다. <paramref name="inner"/> 를 주면 그쪽에도 그대로 흘린다.</summary>
    public CompileStatsCollector(ICompileStatsSink? inner = null) => _inner = inner;

    /// <summary>총 호출 수 (재시도 포함).</summary>
    public long Calls { get; private set; }

    /// <summary>검증까지 통과한 호출 수.</summary>
    public long Passed { get; private set; }

    /// <summary>서버에 닿지도 못한 호출 수 (429 · 타임아웃 등).</summary>
    public long CallFailures { get; private set; }

    /// <summary>입력 토큰 누계.</summary>
    public long PromptTokens { get; private set; }

    /// <summary>캐시 적중 입력 토큰 누계.</summary>
    public long CachedTokens { get; private set; }

    /// <summary>출력 토큰 누계.</summary>
    public long CompletionTokens { get; private set; }

    /// <summary>지연 누계(ms).</summary>
    public double LatencyMs { get; private set; }

    /// <summary>비용 누계(USD).</summary>
    public double CostUsd { get; private set; }

    /// <summary>
    /// 관측된 프리픽스 SHA 의 종류 수.
    /// <b>2 이상이면 즉시 경보다</b> — 프롬프트 캐시가 깨졌다는 뜻이다 (docs/01 §10.2).
    /// </summary>
    public int UniquePrefixHashes
    {
        get
        {
            lock (_gate)
            {
                return _prefixHashes.Count;
            }
        }
    }

    /// <summary>캐시 적중률 (입력 토큰 기준). 로컬 엔진은 항상 0 이다 (W1_env.md §4.4).</summary>
    public double CacheHitRate => PromptTokens == 0 ? 0 : (double)CachedTokens / PromptTokens;

    /// <summary>(stage, code, archetype) 3축 집계. 건수 내림차순, 같으면 코드 오름차순.</summary>
    public ImmutableArray<KeyValuePair<FailureKey, int>> Failures
    {
        get
        {
            lock (_gate)
            {
                return
                [
                    .. _failures
                        .OrderByDescending(e => e.Value)
                        .ThenBy(e => e.Key.Code, StringComparer.Ordinal)
                        .ThenBy(e => e.Key.Archetype),
                ];
            }
        }
    }

    /// <summary>시도 횟수별 성공 건수. 첨자 0 = 1회 만에 성공. 재시도 성공률 계산에 쓴다.</summary>
    public ImmutableArray<int> AttemptHistogram
    {
        get
        {
            lock (_gate)
            {
                return [.. _attemptCounts];
            }
        }
    }

    /// <inheritdoc />
    public void Record(BucketKey bucket, in CompileStats stats, in ValidationResult validation)
    {
        lock (_gate)
        {
            Calls++;
            PromptTokens += stats.PromptTokens;
            CachedTokens += stats.CachedTokens;
            CompletionTokens += stats.CompletionTokens;
            LatencyMs += stats.LatencyMs;
            CostUsd += stats.CostUsd;

            if (!string.IsNullOrEmpty(stats.PrefixSha))
            {
                _prefixHashes.Add(stats.PrefixSha);
            }

            if (!stats.Reached)
            {
                CallFailures++;
            }

            if (validation.IsValid)
            {
                Passed++;

                int index = Math.Max(0, stats.Attempt - 1);
                while (_attemptCounts.Count <= index)
                {
                    _attemptCounts.Add(0);
                }

                _attemptCounts[index]++;
            }
            else
            {
                var key = new FailureKey(validation.FailedAt, validation.Code, bucket.A.Value);
                _failures[key] = _failures.GetValueOrDefault(key) + 1;
            }
        }

        _inner?.Record(bucket, in stats, in validation);
    }
}

/// <summary>
/// OpenTelemetry 계측기. docs/12 §5 의 태그 구성을 그대로 쓴다.
///
/// <b>모든 요청에 <c>prefix_sha</c> 태그를 붙인다</b> (CLAUDE.md §2.5) —
/// 대시보드에서 유니크 해시가 1개인지 상시 확인하기 위해서다. 2개 이상이면 캐시가 깨진 것이다.
/// </summary>
public sealed class CompileMeter : ICompileStatsSink, IDisposable
{
    /// <summary>계측기 이름.</summary>
    public const string MeterName = "Npc.Llm";

    private readonly Meter _meter;
    private readonly Counter<long> _calls;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _promptTokens;
    private readonly Counter<long> _cachedTokens;
    private readonly Counter<long> _completionTokens;
    private readonly Counter<double> _cost;
    private readonly Histogram<double> _latency;

    /// <summary>계측기를 건다. 기동 시 1회.</summary>
    public CompileMeter()
    {
        _meter = new Meter(MeterName);

        _calls = _meter.CreateCounter<long>("npc.llm.calls", description: "LLM 호출 수 (재시도 포함)");
        _failures = _meter.CreateCounter<long>("npc.llm.validation_failures", description: "검증 실패");
        _promptTokens = _meter.CreateCounter<long>("npc.llm.prompt_tokens", unit: "token");
        _cachedTokens = _meter.CreateCounter<long>("npc.llm.cached_tokens", unit: "token");
        _completionTokens = _meter.CreateCounter<long>("npc.llm.completion_tokens", unit: "token");
        _cost = _meter.CreateCounter<double>("npc.llm.cost_usd", unit: "USD");
        _latency = _meter.CreateHistogram<double>("npc.llm.latency", unit: "ms");
    }

    /// <inheritdoc />
    public void Record(BucketKey bucket, in CompileStats stats, in ValidationResult validation)
    {
        var model = new KeyValuePair<string, object?>("model", stats.Model);
        var prefix = new KeyValuePair<string, object?>("prefix_sha", stats.PrefixSha);
        var forced = new KeyValuePair<string, object?>("forced", stats.Forced);
        var attempt = new KeyValuePair<string, object?>("attempt", stats.Attempt);

        _calls.Add(1, model, prefix, forced, attempt);
        _promptTokens.Add(stats.PromptTokens, model, prefix);
        _cachedTokens.Add(stats.CachedTokens, model, prefix);
        _completionTokens.Add(stats.CompletionTokens, model, prefix);
        _cost.Add(stats.CostUsd, model, prefix);
        _latency.Record(stats.LatencyMs, model, prefix);

        if (!validation.IsValid)
        {
            // docs/12 §5 — (stage, code, archetype) 3축. T2-20 의 집계 리포트가 같은 축을 쓴다.
            _failures.Add(
                1,
                new KeyValuePair<string, object?>("stage", validation.FailedAt.ToString()),
                new KeyValuePair<string, object?>("code", validation.Code),
                new KeyValuePair<string, object?>("archetype", bucket.A.Value),
                prefix);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
