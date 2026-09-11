using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npc.Contracts;
using Npc.Core.Plan;

namespace Npc.Core.Validation;

/// <summary>수선 한 번의 결과.</summary>
/// <param name="Repaired">고쳤는가. false 면 <paramref name="Document"/> 는 원본 그대로다.</param>
/// <param name="Document">수선된 문서. 실패면 원본.</param>
/// <param name="Rule">적용한 규칙 이름. 실패면 빈 문자열.</param>
/// <param name="Detail">사람이 읽는 한 줄. <b>실패면 왜 못 고쳤는지가 여기 있다.</b></param>
public readonly record struct RepairResult(
    bool Repaired, PlanDocument Document, string Rule, string Detail)
{
    /// <summary>못 고쳤다.</summary>
    /// <param name="document">원본.</param>
    /// <param name="why">사유.</param>
    public static RepairResult No(PlanDocument document, string why) =>
        new(false, document, string.Empty, why);
}

/// <summary>
/// 결정론 자동 수선 (C-05).
///
/// <para>
/// <b>검증을 건너뛰는 것이 아니다.</b> 수선은 <b>재검증 전의 변환</b>이고, 고친 플랜은
/// 4단 검증을 <b>처음부터 다시</b> 통과해야 채택된다 (CLAUDE.md §8 "정확성 vs 처리량 → 정확성").
/// 여기서 하는 일은 "모델이 한 번 더 시도하면 고쳤을 실수" 를 토큰 없이 고치는 것뿐이다.
/// </para>
///
/// <para>
/// <b>왜 기계가 고칠 수 있는가.</b> 실측에서 <c>V3.PRECONDITION_UNMET</c> 이 실패의 47.9%,
/// 그중 스텝 1번이 271건이었다 — 대부분 <b>"장소 플래그 앞에 <c>MoveTo</c> 가 없다"</b> 다.
/// 어느 심볼로 가야 하는지는 플래그 → 심볼 표가 이미 안다(<c>system_rules.md</c> 규칙 6).
/// </para>
///
/// <para>
/// <b>결정론이다.</b> 같은 입력이면 같은 출력이어야 한다 — 난수도 시각도 없고, 규칙은
/// 실패 코드 하나당 하나다. 그래야 "왜 이렇게 고쳤나" 에 답할 수 있다.
/// </para>
/// </summary>
public static class PlanRepair
{
    /// <summary>
    /// 한 플랜에 시도할 수선 횟수 상한.
    ///
    /// <b>수선이 다음 실패를 낳는 경우가 있다</b> — <c>MoveTo</c> 를 넣었더니 그 자리에서
    /// <c>DEGENERATE</c> 가 나는 식이다. 무한히 돌지 않게 막되, 서너 번은 준다:
    /// 실측의 실패는 대개 한두 곳에 몰려 있다.
    /// </summary>
    public const int MaxRounds = 4;

    /// <summary>
    /// 실패 하나를 고친다. <b>한 번에 한 규칙만</b> 적용한다 — 여러 개를 한꺼번에 고치면
    /// 어느 수선이 통과를 만들었는지 알 수 없고, 그러면 규칙의 효과를 잴 수 없다.
    /// </summary>
    /// <param name="document">원본.</param>
    /// <param name="failure">검증 결과. 통과면 아무것도 안 한다.</param>
    /// <param name="vocabulary">어휘.</param>
    /// <param name="archetype">아키타입. 바인딩 가능한 심볼을 여기서 본다.</param>
    public static RepairResult TryRepair(
        PlanDocument document,
        in ValidationResult failure,
        IPlanValidationVocabulary vocabulary,
        ArchetypeId archetype)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(vocabulary);

        if (failure.IsValid)
        {
            return RepairResult.No(document, "통과한 플랜이다. 고칠 것이 없다.");
        }

        return failure.Code switch
        {
            "V3.PRECONDITION_UNMET" => InsertMove(document, failure, vocabulary, archetype),
            "V3.LOOP_NOT_CLOSED" => CloseLoop(document, failure, vocabulary, archetype),
            "V3.RESOURCE_IMBALANCE" => LowerCount(document, failure),
            _ => RepairResult.No(document, $"{failure.Code} 는 수선 규칙이 없다."),
        };
    }

    /// <summary>
    /// <b>장소 플래그를 세우는 <c>MoveTo</c> 를 그 스텝 앞에 넣는다.</b>
    ///
    /// <para>
    /// 실측 실패의 절반이 이것이다. 어느 심볼인지는 플래그가 정한다 —
    /// <c>AtHome</c> → <c>$home</c>, <c>AtWorkplace</c> → <c>$workplace</c> …
    /// </para>
    ///
    /// <para>
    /// <b>아키타입이 그 심볼을 못 쓰면 포기한다.</b> 억지로 넣으면 <c>V3.UNREACHABLE_POI</c> 로
    /// 옮겨 갈 뿐이고, 그것은 고친 것이 아니라 실패 코드를 바꾼 것이다.
    /// </para>
    /// </summary>
    private static RepairResult InsertMove(
        PlanDocument document,
        in ValidationResult failure,
        IPlanValidationVocabulary vocabulary,
        ArchetypeId archetype)
    {
        int at = failure.StepIndex;

        if ((uint)at >= (uint)document.Steps.Length)
        {
            return RepairResult.No(document, $"스텝 {at} 이 범위 밖이다.");
        }

        if (document.Steps.Length >= PlanDocument.MaxSteps)
        {
            // 스텝 상한을 넘기면 스키마 1단이 거절한다. 수선이 다른 실패를 만드는 셈이다.
            return RepairResult.No(
                document, $"스텝이 이미 {document.Steps.Length}개다 (상한 {PlanDocument.MaxSteps}). 포기한다.");
        }

        if (!TryReadFlags(failure.Detail, out WorldFlags missing)
            || !TryPickSymbol(missing, vocabulary, archetype, out PoiSymbol symbol))
        {
            return RepairResult.No(
                document, "장소 플래그로 설명되는 실패가 아니다 — MoveTo 로 고칠 수 없다.");
        }

        // 바로 앞 스텝이 이미 같은 곳으로 가는 MoveTo 면 넣지 않는다. 넣으면 DEGENERATE 다.
        if (at > 0 && IsMoveTo(document.Steps[at - 1], vocabulary, symbol))
        {
            return RepairResult.No(document, $"앞 스텝이 이미 {PoiSymbols.ToText(symbol)} 로 간다.");
        }

        PlanStep move = MoveStep(symbol);
        PlanStep[] steps = new PlanStep[document.Steps.Length + 1];

        Array.Copy(document.Steps, steps, at);
        steps[at] = move;
        Array.Copy(document.Steps, at, steps, at + 1, document.Steps.Length - at);

        return new RepairResult(
            true,
            document with { Steps = steps },
            "InsertMove",
            string.Create(
                CultureInfo.InvariantCulture,
                $"스텝 {at} 앞에 MoveTo {PoiSymbols.ToText(symbol)} 를 넣었다 "
                + $"({WorldFlagTable.Format(missing)} 이 필요했다)."));
    }

    /// <summary>
    /// <b><c>loop</c> 가 닫히지 않으면 마지막에 <c>MoveTo</c> 를 붙인다.</b>
    ///
    /// <c>Sleep</c> 이 마지막인데 <c>AtHome</c> 이 없는 것이 전형이다 — 첫 스텝이 요구하는
    /// 장소로 돌아가야 다음 바퀴가 성립한다.
    /// </summary>
    private static RepairResult CloseLoop(
        PlanDocument document,
        in ValidationResult failure,
        IPlanValidationVocabulary vocabulary,
        ArchetypeId archetype)
    {
        if (document.Steps.Length >= PlanDocument.MaxSteps)
        {
            return RepairResult.No(
                document, $"스텝이 이미 {document.Steps.Length}개다 (상한 {PlanDocument.MaxSteps}). 포기한다.");
        }

        if (!TryReadFlags(failure.Detail, out WorldFlags missing)
            || !TryPickSymbol(missing, vocabulary, archetype, out PoiSymbol symbol))
        {
            return RepairResult.No(document, "장소 플래그로 설명되는 실패가 아니다.");
        }

        PlanStep[] steps = [.. document.Steps, MoveStep(symbol)];

        return new RepairResult(
            true,
            document with { Steps = steps },
            "CloseLoop",
            $"마지막에 MoveTo {PoiSymbols.ToText(symbol)} 를 붙여 loop 를 닫았다.");
    }

    /// <summary>
    /// <b>모으는 양보다 많이 쓰면 쓰는 쪽을 줄인다.</b>
    ///
    /// <para>
    /// 반대로 "더 모으게" 고칠 수도 있지만 그것은 스텝을 늘리고, 어느 채집 액션을 쓸지는
    /// 아키타입마다 다르다 — <b>확실히 맞는 쪽</b>을 고른다. 줄여서 0 이 되면 포기한다:
    /// <c>count: 0</c> 은 아무것도 안 하는 스텝이라 고친 것이 아니다.
    /// </para>
    /// </summary>
    private static RepairResult LowerCount(PlanDocument document, in ValidationResult failure)
    {
        int at = failure.StepIndex;

        if ((uint)at >= (uint)document.Steps.Length)
        {
            return RepairResult.No(document, $"스텝 {at} 이 범위 밖이다.");
        }

        if (!TryReadImbalance(failure.Detail, out int produced, out int consumed) || produced <= 0)
        {
            return RepairResult.No(document, "수지 숫자를 읽지 못했다.");
        }

        PlanStep step = document.Steps[at];

        if (!step.Args.TryGetValue("count", out JsonElement countValue)
            || countValue.ValueKind != JsonValueKind.Number
            || !countValue.TryGetInt32(out int count)
            || count <= 1)
        {
            return RepairResult.No(document, "줄일 count 가 없다.");
        }

        // 초과분만큼 줄인다. 이 스텝이 소비의 전부가 아닐 수 있으므로 하한 1 을 지킨다.
        int lowered = Math.Max(1, count - (consumed - produced));

        if (lowered == count)
        {
            return RepairResult.No(document, "줄여도 같은 값이다.");
        }

        var args = new Dictionary<string, JsonElement>(step.Args, StringComparer.Ordinal)
        {
            ["count"] = JsonDocument.Parse(
                lowered.ToString(CultureInfo.InvariantCulture)).RootElement.Clone(),
        };

        PlanStep[] steps = [.. document.Steps];

        steps[at] = step with { Args = args };

        return new RepairResult(
            true,
            document with { Steps = steps },
            "LowerCount",
            string.Create(
                CultureInfo.InvariantCulture,
                $"스텝 {at} 의 count 를 {count} → {lowered} 로 줄였다 (모음 {produced} · 씀 {consumed})."));
    }

    /// <summary>
    /// 이 플래그를 세우는 POI 심볼 중 <b>이 아키타입이 실제로 갈 수 있는</b> 첫 번째.
    ///
    /// <b>심볼 번호 순으로 본다.</b> 결정론이 필요하다 — 순서가 흔들리면 같은 입력이
    /// 회차마다 다르게 고쳐진다.
    /// </summary>
    public static bool TryPickSymbol(
        WorldFlags missing,
        IPlanValidationVocabulary vocabulary,
        ArchetypeId archetype,
        out PoiSymbol symbol)
    {
        for (int i = 1; i < PoiSymbols.Names.Length; i++)
        {
            var candidate = (PoiSymbol)i;
            WorldFlags grants = CoherenceValidator.GrantsOf(candidate);

            if (grants == WorldFlags.None || (missing & grants) == 0)
            {
                continue;
            }

            if (vocabulary.CanBindSymbol(archetype, candidate))
            {
                symbol = candidate;
                return true;
            }
        }

        symbol = PoiSymbol.None;
        return false;
    }

    /// <summary>
    /// 실패 설명에서 플래그 이름을 읽는다.
    ///
    /// <b>설명 문자열을 파싱하는 것이 이상해 보이지만</b>, <see cref="ValidationResult"/> 는
    /// 코드·스텝·설명만 싣고 플래그를 구조화해 담지 않는다. 담게 고치면 4단 검증기 전체의
    /// 시그니처가 바뀌고, 그 변경은 이 수선기보다 훨씬 크다 — <b>설명 형식은
    /// <c>WorldFlagTable.Format</c> 하나가 만들므로</b> 파싱도 그 역으로 한 곳이면 된다.
    /// </summary>
    /// <param name="detail">실패 설명.</param>
    /// <param name="flags">읽은 플래그. 하나도 없으면 <c>None</c>.</param>
    public static bool TryReadFlags(string detail, out WorldFlags flags)
    {
        flags = WorldFlags.None;

        if (string.IsNullOrEmpty(detail))
        {
            return false;
        }

        for (int i = 0; i < WorldFlagTable.Names.Length; i++)
        {
            if (detail.Contains(WorldFlagTable.Names[i], StringComparison.Ordinal))
            {
                flags |= WorldFlagTable.Values[i];
            }
        }

        return flags != WorldFlags.None;
    }

    /// <summary><c>gathers N x but consumes M</c> 에서 두 숫자를 읽는다.</summary>
    public static bool TryReadImbalance(string detail, out int produced, out int consumed)
    {
        produced = 0;
        consumed = 0;

        if (string.IsNullOrEmpty(detail))
        {
            return false;
        }

        var numbers = new List<int>(2);
        int at = 0;

        while (at < detail.Length && numbers.Count < 2)
        {
            if (!char.IsAsciiDigit(detail[at]))
            {
                at++;
                continue;
            }

            int start = at;

            while (at < detail.Length && char.IsAsciiDigit(detail[at]))
            {
                at++;
            }

            numbers.Add(int.Parse(detail.AsSpan(start, at - start), CultureInfo.InvariantCulture));
        }

        if (numbers.Count < 2)
        {
            return false;
        }

        produced = numbers[0];
        consumed = numbers[1];

        return true;
    }

    private static bool IsMoveTo(PlanStep step, IPlanValidationVocabulary vocabulary, PoiSymbol symbol)
    {
        if (!vocabulary.TryGetAction(step.Action, out ActionId action)
            || !vocabulary.CompletesOnArrival(action))
        {
            return false;
        }

        return step.Args.TryGetValue("poi", out JsonElement poi)
            && poi.ValueKind == JsonValueKind.String
            && string.Equals(poi.GetString(), PoiSymbols.ToText(symbol), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>MoveTo</c> 스텝 하나.
    ///
    /// <b>액션 이름을 문자열로 박는다.</b> <c>actions.json</c> 의 id 이고, 이 이름이 바뀌면
    /// 마스터데이터 검증이 먼저 깨진다 — 여기서 code 를 쓰면 오히려 재배치에 약해진다.
    /// </summary>
    private static PlanStep MoveStep(PoiSymbol symbol) => new()
    {
        Action = "MoveTo",
        Args = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["poi"] = JsonDocument.Parse(
                JsonSerializer.Serialize(PoiSymbols.ToText(symbol))).RootElement.Clone(),
        },
        TimeoutSeconds = MoveTimeoutSeconds,
    };

    /// <summary>
    /// 넣는 <c>MoveTo</c> 의 타임아웃(초).
    ///
    /// <b>액션 기본값을 쓰지 않고 못 박는다</b> — 모든 스텝에 <c>timeout_s</c> 가 있어야 하고
    /// (docs/03 §2), 수선이 넣은 스텝만 기본값에 기대면 그 값이 바뀔 때 조용히 따라 움직인다.
    /// 600초는 마을 끝에서 끝까지 걷는 시간의 넉넉한 상한이다.
    /// </summary>
    public const int MoveTimeoutSeconds = 600;

    /// <summary>수선 규칙 이름. 보고서·검수 표본이 이 이름으로 센다.</summary>
    public static ImmutableArray<string> Rules => ["InsertMove", "CloseLoop", "LowerCount"];
}
