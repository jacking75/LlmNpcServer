using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Npc.Contracts;
using Npc.Core.Plan;
using Npc.Core.Validation;
using Npc.MasterData;

namespace Npc.Llm;

/// <summary>
/// 단건 플랜 생성. docs/12 §2 · §6.
///
/// <b>이 타입은 틱 루프에서 절대 불리지 않는다.</b> <c>Npc.Runtime</c> 은 <c>Npc.Llm</c> 을
/// 참조하지 않고(CLAUDE.md §3), 재계획은 별도 워커가 돈다.
/// 그래서 여기서는 <c>await</c> 도 <c>Stopwatch</c> 도 쓴다 — 게임 로직이 아니다.
///
/// 흐름은 docs/12 §6 이고, 이 태스크(T2-09)가 만드는 것은 <b>시도 1</b> 까지다.
/// 재시도는 T2-15, 인접 버킷 재사용은 T2-16, 폴백은 T2-17 이 얹는다.
/// </summary>
public sealed class LlmPlanCompiler : IPlanCompiler
{
    /// <summary>
    /// 내용 모더레이터 (C-06). 없으면 <c>reasoning</c> 을 그대로 둔다.
    ///
    /// 조립 순서상 컴파일러가 먼저 생기므로 init 이 아니다.
    /// </summary>
    public IContentModerator? Moderator { get; set; }

    private readonly MasterDataSet _data;
    private readonly PromptPrefix _prefix;
    private readonly LlmEngineOptions _engine;
    private readonly IChatClient _client;
    private readonly ICompileStatsSink? _stats;
    private readonly IDryRunValidator? _dryRun;
    private readonly IPlanReuseSource? _reuse;
    private readonly RejectedStore? _rejected;

    /// <summary>컴파일러를 만든다. 프리픽스는 기동 시 1회 조립된 것을 그대로 받는다.</summary>
    /// <param name="data">마스터데이터. 검증 어휘이자 서픽스의 재료다.</param>
    /// <param name="prefix">기동 시 1회 조립한 프리픽스.</param>
    /// <param name="engine">엔진 설정.</param>
    /// <param name="client">엔진 클라이언트.</param>
    /// <param name="stats">계측 수집기. null 이면 기록하지 않는다.</param>
    /// <param name="dryRun">
    /// 검증기 4단. null 이면 3단까지만 본다 —
    /// 4단 구현은 <c>Npc.Sim</c> 에 있고 이 프로젝트는 그것을 참조하지 않는다 (CLAUDE.md §3).
    /// 주입은 호스트·프리베이크가 한다.
    /// </param>
    /// <param name="reuse">
    /// 인접 버킷 재사용 공급원. null 이면 두 번 실패한 뒤 곧장 아키타입 폴백으로 간다.
    /// </param>
    /// <param name="rejected">
    /// 검증 실패 산출물 보존소. null 이면 보존하지 않는다 —
    /// 프리베이크·품질 개선 회차에서는 <b>반드시</b> 넣는다 (docs/13 §8).
    /// </param>
    public LlmPlanCompiler(
        MasterDataSet data,
        PromptPrefix prefix,
        LlmEngineOptions engine,
        IChatClient client,
        ICompileStatsSink? stats = null,
        IDryRunValidator? dryRun = null,
        IPlanReuseSource? reuse = null,
        RejectedStore? rejected = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(client);

        _data = data;
        _prefix = prefix;
        _engine = engine;
        _client = client;
        _stats = stats;
        _dryRun = dryRun;
        _reuse = reuse;
        _rejected = rejected;
    }

    /// <summary>
    /// 재시도 상한. <b>1회만이다</b> (docs/12 §6).
    /// 2회 이상은 성공률이 거의 안 오르고 토큰만 태우며, 지연이 선형으로 늘어 런타임 재계획이 무의미해진다.
    /// </summary>
    public const int MaxAttempts = 2;

    /// <summary>
    /// 결정론 수선을 켤까 (C-05). 기본은 <b>켜짐</b>이다.
    ///
    /// <b>끄는 자리는 측정이다.</b> "수선 전 실패율" 을 재려면 이것이 꺼져 있어야 하고,
    /// 그 숫자가 있어야 수선이 얼마나 벌어 주는지 말할 수 있다.
    /// </summary>
    public bool Repair { get; init; } = true;

    /// <summary>쓰고 있는 엔진.</summary>
    public LlmEngineOptions Engine => _engine;

    /// <summary>쓰고 있는 프리픽스.</summary>
    public PromptPrefix Prefix => _prefix;

    /// <inheritdoc />
    public async ValueTask<PlanCompileResult> CompileAsync(
        PlanRequest request, CancellationToken cancellationToken)
    {
        PlanCompileResult first = await GenerateAndValidateAsync(request, attempt: 1, cancellationToken)
            .ConfigureAwait(false);

        if (first.Validation.IsValid)
        {
            return first;
        }

        // 시도 2 — 실패 코드를 <b>서픽스에만</b> 피드백한다. 프리픽스는 건드리지 않는다 (캐시 유지).
        // temperature 는 0.4 → 0.6 으로 올린다. 같은 실수를 그대로 반복하지 않게.
        PlanCompileResult second = await GenerateAndValidateAsync(
            request with { PreviousFailure = first.Validation },
            attempt: 2,
            cancellationToken).ConfigureAwait(false);

        // 비용 보고는 "이 버킷 하나에 얼마 들었나"여야 한다 — 마지막 호출값이 아니다.
        CompileStats total = first.Stats.Accumulate(second.Stats);

        if (second.Validation.IsValid)
        {
            return second with { Stats = total };
        }

        return Rescue(request, second with { Stats = total });
    }

    /// <summary>
    /// 두 번 다 실패했을 때의 마지막 경로. docs/12 §6.
    ///
    /// <b><see cref="PlanCompileResult.Plan"/> 은 여기서 절대 null 이 되지 않는다.</b>
    /// <c>PlanStore.Resolve</c> 가 T1-39 부터 갖고 있는 보장을 컴파일러도 갖는다 —
    /// 시나리오 C(LLM 전면 차단)가 통과하는 이유가 이것이다 (CLAUDE.md §2.6).
    ///
    /// <see cref="PlanCompileResult.Validation"/> 은 <b>실패한 채로 남는다.</b>
    /// 무엇 때문에 폴백으로 떨어졌는지가 통과율 집계의 원자료이기 때문이다 —
    /// 돌려받은 플랜의 출처는 <see cref="CompiledPlan.Origin"/> 이 말해 준다.
    /// </summary>
    private PlanCompileResult Rescue(in PlanRequest request, in PlanCompileResult failed)
    {
        // (1) 인접 버킷 재사용 — 같은 아키타입의 비슷한 상황. 이미 재검증된 것만 온다.
        if (_reuse is { } reuse
            && reuse.TryReuse(request.Bucket, out CompiledPlan? borrowed, out _)
            && borrowed is not null)
        {
            return failed with { Plan = borrowed };
        }

        // (2) 아키타입 폴백. V7 이 기동 시점에 40개 전부의 존재와 4단 통과를 보장한다 (docs/01 §11).
        CompiledPlan? fallback = _data.Fallbacks?.For(request.Bucket.A);

        return fallback is null
            ? failed
            : failed with { Plan = fallback with { Bucket = request.Bucket, Origin = PlanOrigin.Fallback } };
    }

    /// <summary>한 번 생성하고 검증한다. 재시도 파이프라인(T2-15)이 이 메서드를 두 번 부른다.</summary>
    internal async ValueTask<PlanCompileResult> GenerateAndValidateAsync(
        PlanRequest request, int attempt, CancellationToken cancellationToken)
    {
        (string text, CompileStats stats) = await GenerateAsync(request, attempt, cancellationToken)
            .ConfigureAwait(false);

        PlanCompileResult result = stats.Error is { } error
            ? new PlanCompileResult(
                null,
                ValidationResult.Fail(ValidationStage.Schema, "V0.CALL_FAILED", -1, error),
                stats,
                text)
            : Validate(request, text, stats);

        // 모든 호출에서 기록한다 — 실패만 빠지면 통과율 분모가 조용히 줄어든다 (docs/12 §2).
        if (_stats is { } sink)
        {
            CompileStats recorded = result.Stats;
            ValidationResult validation = result.Validation;

            sink.Record(request.Bucket, in recorded, in validation);
        }

        // 실패 산출물은 시도별로 남긴다. 조용히 버리면 품질 개선의 원자료가 사라진다 (docs/13 §8).
        if (_rejected is { } store && !result.Validation.IsValid)
        {
            CompileStats saved = result.Stats;
            ValidationResult failure = result.Validation;

            store.Save(request.Bucket, in saved, in failure, result.ResponseText);
        }

        return result;
    }

    /// <summary>
    /// 4단 검증 + 컴파일. 실패하면 <b>결정론 수선</b>을 시도하고 <b>처음부터 다시</b> 검증한다 (C-05).
    ///
    /// <para>
    /// <b>수선은 검증을 건너뛰는 것이 아니다.</b> 고친 문서는 1단부터 다시 지난다 —
    /// 통과했다고 적힌 플랜은 실제로 통과한 것이어야 한다 (CLAUDE.md §8).
    /// </para>
    ///
    /// <para>
    /// <b>수선이 실패를 옮기기만 하는 경우가 있다</b> — <c>MoveTo</c> 를 넣었더니 그 자리에서
    /// <c>DEGENERATE</c> 가 나는 식이다. <see cref="PlanRepair.MaxRounds"/> 로 막는다.
    /// </para>
    /// </summary>
    private PlanCompileResult Validate(in PlanRequest request, string text, in CompileStats stats)
    {
        PlanCompileResult result = ValidateOnce(request, text, stats);

        if (result.Validation.IsValid || !Repair)
        {
            return result;
        }

        string current = text;

        for (int round = 0; round < PlanRepair.MaxRounds; round++)
        {
            // 실패한 문서를 다시 읽는다. 1단이 못 읽는 응답이면 수선할 대상이 없다.
            if (!SchemaValidator.Validate(current, out PlanDocument? broken).IsValid || broken is null)
            {
                return result;
            }

            RepairResult repair = PlanRepair.TryRepair(
                broken, result.Validation, _data, request.Bucket.A);

            if (!repair.Repaired)
            {
                return result;
            }

            current = JsonSerializer.Serialize(repair.Document, PlanJsonContext.Default.PlanDocument);

            PlanCompileResult retried = ValidateOnce(request, current, stats);

            if (retried.Validation.IsValid)
            {
                // 출처를 갈라 둔다. 검수 표본에서 "기계가 고친 것" 을 가려 볼 수 있어야
                // 수선 규칙이 나쁜 플랜을 통과시키는 것을 알아챈다.
                return retried with
                {
                    Plan = retried.Plan is { } plan ? plan with { Origin = PlanOrigin.Repaired } : null,
                    Repairs = round + 1,
                };
            }

            result = retried;
        }

        return result;
    }

    /// <summary>
    /// 검증 1·2단 + 컴파일. <b>강제 디코딩을 신뢰하지 않고 재검증한다</b> (CLAUDE.md §2.6).
    /// </summary>
    private PlanCompileResult ValidateOnce(in PlanRequest request, string text, in CompileStats stats)
    {
        ValidationResult schema = SchemaValidator.Validate(text, out PlanDocument? document);

        if (!schema.IsValid || document is null)
        {
            return new PlanCompileResult(null, schema, stats, text);
        }

        // reasoning 정화 (C-06). 플랜 문서의 나머지는 전부 enum·id 인데 이것만 자유 자연어이고,
        // planstore 에 그대로 저장돼 검수자가 읽는다. 제어문자·URL 이 파일에 남으면
        // 터미널·웹 뷰어를 그대로 지난다.
        //
        // <b>플랜 자체는 건드리지 않는다.</b> 정화가 스텝을 바꾸면 검증을 지난 뒤에 플랜을
        // 고치는 것이고, 그러면 "검증된 플랜" 이라는 말이 거짓이 된다.
        if (Moderator is { } moderator && document.Reasoning is { Length: > 0 })
        {
            document = document with { Reasoning = moderator.Sanitize(document.Reasoning, out _) };
        }

        ArchetypeId archetype = request.Bucket.A;

        ValidationResult vocabulary = VocabularyValidator.Validate(document, archetype, _data);
        if (!vocabulary.IsValid)
        {
            return new PlanCompileResult(null, vocabulary, stats, text);
        }

        // 3단 — 정합성. 여기가 실질적으로 가장 많이 잡는다 (docs/12 §5).
        ValidationResult coherence = CoherenceValidator.Validate(document, request.Bucket, archetype, _data);
        if (!coherence.IsValid)
        {
            return new PlanCompileResult(null, coherence, stats, text);
        }

        try
        {
            CompiledPlan plan = Npc.Core.Plan.PlanCompiler.Compile(
                document,
                request.Bucket,
                default,
                _data,
                PlanOrigin.Runtime,
                version: 1,
                sourceJson: text);

            // 4단 — 드라이런. 구현이 주입되지 않았으면 3단까지가 전부다.
            ValidationResult dryRun = _dryRun?.Validate(
                plan, ValidationContext.For(request.Bucket, _data.InitialFlags(request.Bucket)))
                ?? ValidationResult.Ok;

            return dryRun.IsValid
                ? new PlanCompileResult(plan, ValidationResult.Ok, stats, text)
                : new PlanCompileResult(null, dryRun, stats, text);
        }
        catch (PlanCompilationException ex)
        {
            // 2단을 통과했는데 컴파일이 깨지면 어휘 검증에 구멍이 있다는 뜻이다.
            // 조용히 넘기지 않고 실패 코드로 남긴다.
            return new PlanCompileResult(
                null,
                ValidationResult.Fail(ValidationStage.Vocabulary, "V2.NOT_COMPILABLE", ex.StepIndex, ex.Message),
                stats,
                text);
        }
    }

    /// <summary>LLM 호출 한 번. 예외를 밖으로 던지지 않고 <see cref="CompileStats.Error"/> 로 돌려준다.</summary>
    private async ValueTask<(string Text, CompileStats Stats)> GenerateAsync(
        PlanRequest request, int attempt, CancellationToken cancellationToken)
    {
        string suffix = PlanRequestSuffix.Build(request, _data);
        List<ChatMessage> messages = ChatClientFactory.BuildMessages(_engine, _prefix.Text, suffix);
        ChatOptions options = ChatClientFactory.BuildChatOptions(_engine, _prefix.Schema, attempt);

        long start = Stopwatch.GetTimestamp();

        try
        {
            ChatResponse response = await _client
                .GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);

            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            (long prompt, long cached, long completion) = ChatClientFactory.ReadUsage(response);

            return (
                StripFences(response.Text),
                new CompileStats(
                    (int)prompt,
                    (int)cached,
                    (int)completion,
                    elapsed,
                    _engine.CostUsd(prompt, cached, completion),
                    _engine.Id,
                    attempt,
                    _prefix.Sha256,
                    _engine.ForceJsonSchema,
                    null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            return (
                string.Empty,
                new CompileStats(
                    0, 0, 0, elapsed, 0, _engine.Id, attempt, _prefix.Sha256, _engine.ForceJsonSchema,
                    Describe(ex)));
        }
    }

    /// <summary>
    /// ```json 울타리를 벗긴다. 강제 디코딩을 끄면 모델이 자주 붙인다.
    /// W1(T0-09)의 "유효 JSON" 집계도 같은 처리를 한 뒤에 셌으므로 기준선이 어긋나지 않는다.
    /// </summary>
    public static string StripFences(string? text)
    {
        string trimmed = (text ?? string.Empty).Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed;
        }

        trimmed = trimmed[(firstNewline + 1)..];
        int closing = trimmed.LastIndexOf("```", StringComparison.Ordinal);

        return (closing >= 0 ? trimmed[..closing] : trimmed).Trim();
    }

    private static string Describe(Exception ex)
    {
        string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');

        return message.Length > 300 ? message[..300] : message;
    }
}
