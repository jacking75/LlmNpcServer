using System.Collections.Immutable;
using System.Text.Json;
using Npc.Contracts;

namespace Npc.Core.Plan;

/// <summary>플랜의 출처. docs/03 §5.</summary>
public enum PlanOrigin : byte
{
    /// <summary>프리베이크된 플랜.</summary>
    Prebaked = 0,

    /// <summary>런타임 재계획으로 만들어진 플랜.</summary>
    Runtime = 1,

    /// <summary>아키타입 폴백. 사람이 쓴다.</summary>
    Fallback = 2,

    /// <summary>사람이 검수·수정해서 고정한 플랜. 프리베이크가 덮어쓰지 않는다.</summary>
    Pinned = 3,
}

/// <summary>
/// 스텝 하나의 플래그 전이. docs/03 §5.
/// <see cref="CompiledStep"/> 밖의 병렬 배열에 둔다 —
/// 40바이트짜리를 스텝마다 인라인으로 물면 5,000 NPC × 10스텝이 캐시에서 넘친다.
/// </summary>
public readonly record struct StepFlags(
    WorldFlags Requires,
    WorldFlags RequiresAny,
    WorldFlags Forbids,
    WorldFlags Grants,
    WorldFlags Clears)
{
    /// <summary>이 상태에서 실행 가능한가. 비트 연산 세 번. docs/01 §2.1.</summary>
    public bool IsSatisfiedBy(WorldFlags state) =>
        (Requires & ~state) == 0
        && (RequiresAny == WorldFlags.None || (RequiresAny & state) != 0)
        && (Forbids & state) == 0;

    /// <summary>이 스텝이 끝난 뒤의 상태.</summary>
    public WorldFlags Apply(WorldFlags state) => (state & ~Clears) | Grants;
}

/// <summary>
/// 컴파일된 스텝. docs/03 §5.
///
/// <b>32바이트 이하 값 타입으로 유지한다</b> — 5,000 NPC × 최대 10스텝이 캐시에 들어가야 한다.
/// 플래그 4~5종(각 8바이트)은 여기 넣지 않고 <see cref="CompiledPlan.StepFlagSets"/> 로 뺐다.
/// 실제 크기는 16바이트다 (<c>CompiledStep_SizeIsBounded</c> 가 강제).
/// <see cref="NpcRef"/> 를 12비트 payload 로 넓히며 14 → 16 이 됐다 (F-05).
/// </summary>
/// <param name="Action">액션 code. ActionCatalog 의 첨자.</param>
/// <param name="Poi">POI 심볼. 실행 시점에 개체 바인딩한다.</param>
/// <param name="Item">아이템/레시피. 없으면 default.</param>
/// <param name="Count">수량·시간 등 정수 인자.</param>
/// <param name="TimeoutSeconds">타임아웃. 0 이면 안 된다 — 모든 스텝에 강제한다.</param>
/// <param name="ArgFlags">열거 인자의 ordinal (speed, topic, until_time …).</param>
/// <param name="NpcRef">npc_ref 심볼. <see cref="NpcRefCodes"/> 참조.</param>
/// <param name="FlagSetIndex"><see cref="CompiledPlan.StepFlagSets"/> 첨자.</param>
public readonly record struct CompiledStep(
    ActionId Action,
    PoiSymbol Poi,
    ItemId Item,
    ushort Count,
    ushort TimeoutSeconds,
    byte ArgFlags,
    ushort NpcRef,
    ushort FlagSetIndex);

/// <summary>npc_ref 심볼의 인코딩. docs/01 §2.3 — 인스턴스 ID 직접 지정은 금지다.</summary>
public enum NpcRefKind : byte
{
    /// <summary>인자 없음.</summary>
    None = 0,

    /// <summary>자기 자신.</summary>
    Self = 1,

    /// <summary>가장 가까운 해당 아키타입.</summary>
    NearestArchetype = 2,

    /// <summary>그 POI 의 주인.</summary>
    PoiOwner = 3,

    /// <summary>
    /// 최근에 적대로 판정된 플레이어 (B-06). <b>페이로드를 쓰지 않는다.</b>
    ///
    /// <para>
    /// 대상은 <c>NpcStore.HostilePlayer</c> 가 들고 있다 — 플랜에 플레이어 id 를 박을 수
    /// 없으므로(매 회차 다르다) 발행 시점에 읽는다. <c>TargetNpc</c> 대신
    /// <c>TargetPlayer</c> 에 실린다.
    /// </para>
    /// </summary>
    HostilePlayer = 4,
}

/// <summary>
/// npc_ref 를 2바이트에 담는다. 상위 4비트 = 종류, 하위 12비트 = 아키타입 code 또는 POI 심볼.
///
/// <b>6비트(64종)였다</b> (F-05). 아키타입 수가 코드 상수를 벗어나면서 64 가 조용한 상한이
/// 되어 버렸다 — 65번째 아키타입은 <see cref="NearestArchetype"/> 에서 code 를 잃고
/// 엉뚱한 NPC 를 가리켰을 것이고, 그런 오류는 런타임에 티가 나지 않는다.
/// </summary>
public static class NpcRefCodes
{
    /// <summary>payload 비트 수.</summary>
    public const int PayloadBits = 12;

    /// <summary>payload 상한. 아키타입 code · POI 심볼이 이 값을 넘을 수 없다.</summary>
    public const int MaxPayload = (1 << PayloadBits) - 1;

    private const int PayloadMask = MaxPayload;

    /// <summary>인자 없음.</summary>
    public const ushort None = 0;

    /// <summary>자기 자신.</summary>
    public static ushort Self() => (ushort)((int)NpcRefKind.Self << PayloadBits);

    /// <summary>
    /// 가장 가까운 해당 아키타입.
    /// <b>code 가 <see cref="MaxPayload"/> 를 넘으면 던진다</b> — 조용히 자르면
    /// 다른 아키타입을 가리키는 플랜이 검증을 통과한다.
    /// </summary>
    public static ushort NearestArchetype(int archetypeCode)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(archetypeCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(archetypeCode, MaxPayload);

        return (ushort)(((int)NpcRefKind.NearestArchetype << PayloadBits) | archetypeCode);
    }

    /// <summary>그 POI 의 주인.</summary>
    /// <summary>
    /// 최근 적대 플레이어 (B-06). 페이로드가 없다 — 대상은 런타임 상태에서 온다.
    /// </summary>
    public static ushort HostilePlayer() => (ushort)((int)NpcRefKind.HostilePlayer << PayloadBits);

    public static ushort PoiOwner(PoiSymbol symbol) =>
        (ushort)(((int)NpcRefKind.PoiOwner << PayloadBits) | ((int)symbol & PayloadMask));

    /// <summary>종류.</summary>
    public static NpcRefKind KindOf(ushort value) => (NpcRefKind)(value >> PayloadBits);

    /// <summary>하위 12비트 (아키타입 code 또는 POI 심볼).</summary>
    public static int PayloadOf(ushort value) => value & PayloadMask;
}

/// <summary>
/// 런타임이 쓰는 플랜 표현. docs/03 §5.
/// JSON 은 저장·검수용이고, 실행기는 이 구조만 본다.
///
/// <c>class</c> 가 아니라 <c>record</c> 인 이유는 <c>with</c> 하나 때문이다 —
/// PlanStore 가 등록 시점에 <see cref="Id"/> 를 박아 넣어야 하는데,
/// 그것 하나 때문에 세터를 열면 런타임 중에 플랜이 바뀔 수 있게 된다.
/// </summary>
public sealed record CompiledPlan
{
    /// <summary>플랜 id. PlanStore 의 첨자.</summary>
    public required PlanId Id { get; init; }

    /// <summary>어느 버킷의 플랜인가.</summary>
    public required BucketKey Bucket { get; init; }

    /// <summary>플랜 버전. 같은 버킷을 다시 만들 때 올린다.</summary>
    public int Version { get; init; }

    /// <summary>목표 식별자. 로그·검수용.</summary>
    public required string Goal { get; init; }

    /// <summary>마지막 스텝 뒤에 첫 스텝으로 돌아가는가.</summary>
    public required bool Loop { get; init; }

    /// <summary>스텝 실패 시 정책.</summary>
    public StepFailPolicy OnFail { get; init; } = StepFailPolicy.Fallback;

    /// <summary>
    /// 이 플랜을 시작하기 위해 이미 서 있어야 하는 플래그.
    /// 앞선 스텝이 세워주는 것은 빼고 계산한다 (docs/03 §5).
    /// </summary>
    public required WorldFlags RequiredFlags { get; init; }

    /// <summary>이 플랜이 성립하지 않게 만드는 플래그. 앞선 스텝이 내려주는 것은 뺐다.</summary>
    public required WorldFlags ForbiddenFlags { get; init; }

    /// <summary>스텝. 값 타입 배열이라 순회가 싸다.</summary>
    public required ImmutableArray<CompiledStep> Steps { get; init; }

    /// <summary>스텝별 플래그 전이. <c>Steps[i].FlagSetIndex</c> 로 찾는다.</summary>
    public required ImmutableArray<StepFlags> StepFlagSets { get; init; }

    /// <summary>검수·리플레이용 원본 JSON.</summary>
    public string SourceJson { get; init; } = string.Empty;

    /// <summary>출처.</summary>
    public PlanOrigin Origin { get; init; } = PlanOrigin.Runtime;

    /// <summary>i번 스텝의 플래그 전이.</summary>
    public StepFlags FlagsOf(int stepIndex) => StepFlagSets[Steps[stepIndex].FlagSetIndex];

    /// <summary>
    /// 재계획이 필요한가. <b>틱당 115회 실행되는 핫패스다</b> (docs/03 §5).
    /// 스텝을 순회하지 않고 사전 OR 해둔 마스크와 비트 연산 한 번으로 끝낸다.
    /// </summary>
    public bool NeedsReplan(WorldFlags current) =>
        (RequiredFlags & ~current) != 0 || (ForbiddenFlags & current) != 0;
}

/// <summary>
/// 컴파일된 스텝의 인자 슬롯. <see cref="IPlanVocabulary"/> 가 액션별 파라미터를 여기로 옮긴다.
/// </summary>
public readonly record struct PackedArgs(PoiSymbol Poi, ItemId Item, ushort Count, byte ArgFlags, ushort NpcRef);

/// <summary>
/// 플랜 컴파일에 필요한 어휘. docs/03 §5.
///
/// <b>이 인터페이스가 있는 이유는 의존 방향 때문이다.</b>
/// <c>CompiledPlan</c> 은 <c>Npc.Core</c> 에 있고 <c>ActionCatalog</c> 는 <c>Npc.MasterData</c> 에 있는데,
/// CLAUDE.md §3 의 의존 그래프는 <c>Core → MasterData</c> 를 금지한다.
/// 그래서 Core 가 필요한 것만 인터페이스로 선언하고 MasterData 가 구현한다.
/// </summary>
public interface IPlanVocabulary
{
    /// <summary>액션 id 문자열 → code.</summary>
    bool TryGetAction(string actionId, out ActionId action);

    /// <summary>code → 액션 id 문자열.</summary>
    string ActionName(ActionId action);

    /// <summary>액션의 플래그 전이.</summary>
    StepFlags FlagsOf(ActionId action);

    /// <summary>액션의 기본 타임아웃(초).</summary>
    int DefaultTimeoutSeconds(ActionId action);

    /// <summary>아이템 id 문자열 → code.</summary>
    bool TryGetItem(string itemId, out ItemId item);

    /// <summary>플랜 스텝의 args 를 컴파일된 슬롯으로. 실패하면 이유를 준다.</summary>
    bool TryPackArgs(
        ActionId action,
        IReadOnlyDictionary<string, JsonElement> args,
        out PackedArgs packed,
        out string error);

    /// <summary>컴파일된 슬롯을 다시 args 로. 왕복 검증과 검수 화면이 쓴다.</summary>
    void UnpackArgs(ActionId action, in PackedArgs packed, IDictionary<string, JsonElement> args);
}

/// <summary>플랜 컴파일 실패.</summary>
public sealed class PlanCompilationException : Exception
{
    /// <summary>실패한 스텝 번호. -1 = 문서 전체.</summary>
    public int StepIndex { get; }

    /// <summary>컴파일 실패.</summary>
    public PlanCompilationException(string message, int stepIndex)
        : base(message) => StepIndex = stepIndex;

    /// <summary>컴파일 실패.</summary>
    public PlanCompilationException(string message)
        : base(message) => StepIndex = -1;

    /// <summary>컴파일 실패.</summary>
    public PlanCompilationException(string message, Exception innerException)
        : base(message, innerException) => StepIndex = -1;

    /// <summary>컴파일 실패.</summary>
    public PlanCompilationException()
        : base("플랜을 컴파일할 수 없다.") => StepIndex = -1;
}

/// <summary><see cref="PlanDocument"/> ↔ <see cref="CompiledPlan"/>.</summary>
public static class PlanCompiler
{
    /// <summary>JSON 모델 → 런타임 표현.</summary>
    public static CompiledPlan Compile(
        PlanDocument document,
        BucketKey bucket,
        PlanId id,
        IPlanVocabulary vocabulary,
        PlanOrigin origin = PlanOrigin.Runtime,
        int version = 1,
        string sourceJson = "")
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(vocabulary);

        var steps = ImmutableArray.CreateBuilder<CompiledStep>(document.Steps.Length);
        var flagSets = ImmutableArray.CreateBuilder<StepFlags>(document.Steps.Length);

        // 이 플랜을 시작하기 위해 이미 서 있어야 하는 것만 남긴다.
        // 앞선 스텝이 세워주는 플래그는 진입 조건이 아니다 —
        // 전부 OR 하면 Mine 이 세워줄 HasRawMaterial 을 Craft 때문에 요구하게 되고,
        // 인지 스캔이 매 틱 "이탈"이라고 답한다.
        WorldFlags granted = WorldFlags.None;
        WorldFlags cleared = WorldFlags.None;
        WorldFlags required = WorldFlags.None;
        WorldFlags forbidden = WorldFlags.None;

        for (int i = 0; i < document.Steps.Length; i++)
        {
            PlanStep step = document.Steps[i];

            if (!vocabulary.TryGetAction(step.Action, out ActionId action))
            {
                throw new PlanCompilationException($"카탈로그에 없는 액션 '{step.Action}'.", i);
            }

            if (!vocabulary.TryPackArgs(action, step.Args, out PackedArgs packed, out string error))
            {
                throw new PlanCompilationException($"{step.Action} 의 인자를 해석할 수 없다: {error}", i);
            }

            StepFlags flags = vocabulary.FlagsOf(action);

            required |= flags.Requires & ~granted;
            forbidden |= flags.Forbids & ~cleared;
            granted = (granted & ~flags.Clears) | flags.Grants;
            cleared = (cleared & ~flags.Grants) | flags.Clears;

            int timeout = step.TimeoutSeconds ?? vocabulary.DefaultTimeoutSeconds(action);

            // 타임아웃이 없으면 완료 이벤트를 못 받았을 때 NPC 가 영구 정지한다 (docs/11 §12).
            if (timeout <= 0)
            {
                timeout = PlanDocument.MinTimeoutSeconds;
            }

            flagSets.Add(flags);
            steps.Add(new CompiledStep(
                action,
                packed.Poi,
                packed.Item,
                packed.Count,
                (ushort)Math.Clamp(timeout, PlanDocument.MinTimeoutSeconds, PlanDocument.MaxTimeoutSeconds),
                packed.ArgFlags,
                packed.NpcRef,
                (ushort)i));
        }

        return new CompiledPlan
        {
            Id = id,
            Bucket = bucket,
            Version = version,
            Goal = document.Goal,
            Loop = document.Loop,
            OnFail = document.OnStepFail,
            RequiredFlags = required,
            ForbiddenFlags = forbidden,
            Steps = steps.ToImmutable(),
            StepFlagSets = flagSets.ToImmutable(),
            SourceJson = sourceJson,
            Origin = origin,
        };
    }

    /// <summary>런타임 표현 → JSON 모델. 검수 화면과 왕복 테스트가 쓴다.</summary>
    public static PlanDocument ToDocument(CompiledPlan plan, IPlanVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(vocabulary);

        var steps = new PlanStep[plan.Steps.Length];

        for (int i = 0; i < plan.Steps.Length; i++)
        {
            CompiledStep step = plan.Steps[i];
            var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

            vocabulary.UnpackArgs(
                step.Action,
                new PackedArgs(step.Poi, step.Item, step.Count, step.ArgFlags, step.NpcRef),
                args);

            steps[i] = new PlanStep
            {
                Action = vocabulary.ActionName(step.Action),
                Args = args,
                TimeoutSeconds = step.TimeoutSeconds,
            };
        }

        return new PlanDocument
        {
            Schema = 1,
            Goal = plan.Goal,
            Steps = steps,
            Loop = plan.Loop,
            OnStepFail = plan.OnFail,
        };
    }
}
