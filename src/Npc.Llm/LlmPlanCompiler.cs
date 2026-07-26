using System.Diagnostics;
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
    private readonly MasterDataSet _data;
    private readonly PromptPrefix _prefix;
    private readonly LlmEngineOptions _engine;
    private readonly IChatClient _client;

    /// <summary>컴파일러를 만든다. 프리픽스는 기동 시 1회 조립된 것을 그대로 받는다.</summary>
    public LlmPlanCompiler(
        MasterDataSet data,
        PromptPrefix prefix,
        LlmEngineOptions engine,
        IChatClient client)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(client);

        _data = data;
        _prefix = prefix;
        _engine = engine;
        _client = client;
    }

    /// <summary>쓰고 있는 엔진.</summary>
    public LlmEngineOptions Engine => _engine;

    /// <summary>쓰고 있는 프리픽스.</summary>
    public PromptPrefix Prefix => _prefix;

    /// <inheritdoc />
    public async ValueTask<PlanCompileResult> CompileAsync(
        PlanRequest request, CancellationToken cancellationToken) =>
        await GenerateAndValidateAsync(request, attempt: 1, cancellationToken).ConfigureAwait(false);

    /// <summary>한 번 생성하고 검증한다. 재시도 파이프라인(T2-15)이 이 메서드를 두 번 부른다.</summary>
    internal async ValueTask<PlanCompileResult> GenerateAndValidateAsync(
        PlanRequest request, int attempt, CancellationToken cancellationToken)
    {
        (string text, CompileStats stats) = await GenerateAsync(request, attempt, cancellationToken)
            .ConfigureAwait(false);

        if (stats.Error is { } error)
        {
            return new PlanCompileResult(
                null,
                ValidationResult.Fail(ValidationStage.Schema, "V0.CALL_FAILED", -1, error),
                stats,
                text);
        }

        return Validate(request, text, stats);
    }

    /// <summary>
    /// 검증 1·2단 + 컴파일. <b>강제 디코딩을 신뢰하지 않고 재검증한다</b> (CLAUDE.md §2.6).
    /// 3·4단은 T2-13 이후에 붙는다.
    /// </summary>
    private PlanCompileResult Validate(in PlanRequest request, string text, in CompileStats stats)
    {
        ValidationResult schema = SchemaValidator.Validate(text, out PlanDocument? document);

        if (!schema.IsValid || document is null)
        {
            return new PlanCompileResult(null, schema, stats, text);
        }

        ArchetypeId archetype = request.Bucket.A;

        ValidationResult vocabulary = VocabularyValidator.Validate(document, archetype, _data);
        if (!vocabulary.IsValid)
        {
            return new PlanCompileResult(null, vocabulary, stats, text);
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

            return new PlanCompileResult(plan, ValidationResult.Ok, stats, text);
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
