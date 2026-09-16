using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using Npc.MasterData.Validation;

namespace Npc.Studio.Services;

/// <summary>검증 오류 하나를 화면에서 어떻게 보여 주는가 (T15).</summary>
/// <param name="Issue">원본.</param>
/// <param name="Title">초보자용 제목.</param>
/// <param name="Link">고치러 갈 주소. 없으면 빈 문자열.</param>
/// <param name="LinkLabel">링크 문구.</param>
public sealed record StudioIssueView(StudioIssue Issue, string Title, string Link, string LinkLabel);

/// <summary>
/// 검증 코드 → 초보자 제목 (T15).
///
/// <para>
/// <b><c>V10 · pois.json</c> 은 사람이 읽는 말이 아니다.</b> <c>FixHints</c> 는 LLM 이 읽는
/// 명령형 한 문장이고, 여기는 <b>사람이 목록에서 훑을 한 줄</b>이다 — 둘 다 필요하다.
/// </para>
///
/// <para>
/// <c>IssueGuide_CoversEveryFixHintCode</c> 가 <see cref="FixHints.Codes"/> 전수를 강제한다.
/// </para>
/// </summary>
public static class IssueGuide
{
    private static readonly FrozenDictionary<string, string> s_titles = Build();

    /// <summary>이 코드의 초보자 제목. 모르면 코드 그대로.</summary>
    /// <param name="code">검증 코드.</param>
    public static string TitleOf(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (s_titles.TryGetValue(code, out string? title))
        {
            return title;
        }

        // V3.PRECONDITION_UNMET 처럼 세부 코드가 없으면 큰 코드로 떨어진다.
        int dot = code.IndexOf('.', StringComparison.Ordinal);

        return dot > 0 && s_titles.TryGetValue(code[..dot], out string? parent) ? parent : code;
    }

    /// <summary>사전에 든 코드 전부.</summary>
    public static IEnumerable<string> Codes => s_titles.Keys;

    private static FrozenDictionary<string, string> Build() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["V0"] = "필수 파일이 없다",
            ["V1"] = "파일 형식이 맞지 않는다",
            ["V2"] = "행동이나 인자가 카탈로그에 없다",
            ["V3"] = "하루 일과의 앞뒤가 맞지 않는다",
            ["V4"] = "하루를 굴려 보니 진행이 막힌다",
            ["V5"] = "인구 비율의 합이 1.0 이 아니다",
            ["V6"] = "버킷 수가 직업 수와 맞지 않는다",
            ["V7"] = "하루 일과가 없는 직업이 있다",
            ["V8"] = "하루 일과가 못 하는 행동을 쓴다",
            ["V9"] = "만들 수 없는 것을 만들려 한다",
            ["V10"] = "일터 정원이 인구를 감당하지 못한다",
            ["V11"] = "갈 수 없는 곳을 가리킨다",
            ["V12"] = "근무 시간대 없이 경계 근무·순찰을 허용한다",
            ["V13"] = "개별 NPC 설정이 규칙을 벗어난다",
            ["V14"] = "표시 이름이 빠졌다",
            ["V15"] = "대사 주제가 맞지 않는다",
            ["V1.PARSE"] = "JSON 을 읽을 수 없다",
            ["V1.SCHEMA"] = "필드 이름이나 타입이 스키마와 다르다",
            ["V1.EXTRA_FIELD"] = "스키마에 없는 필드가 있다",
            ["V1.STEP_COUNT"] = "스텝 수가 허용 범위 밖이다",
            ["V2.UNKNOWN_ACTION"] = "없는 행동을 쓴다",
            ["V2.ACTION_NOT_ALLOWED"] = "이 직업이 못 하는 행동을 쓴다",
            ["V2.UNKNOWN_POI"] = "없는 장소 심볼을 쓴다",
            ["V2.UNKNOWN_ARG"] = "그 행동에 없는 인자를 준다",
            ["V2.MISSING_REQUIRED_ARG"] = "필수 인자가 빠졌다",
            ["V2.TYPE_MISMATCH"] = "인자의 타입이 다르다",
            ["V2.RANGE"] = "인자 값이 허용 범위 밖이다",
            ["V2.UNKNOWN_ITEM"] = "없는 아이템을 쓴다",
            ["V2.UNKNOWN_RECIPE"] = "없는 제작법을 쓴다",
            ["V3.PRECONDITION_UNMET"] = "그 스텝의 전제를 세우는 앞 스텝이 없다",
            ["V3.FORBIDDEN_FLAG"] = "금지된 상태에서 그 스텝을 한다",
            ["V3.RESOURCE_IMBALANCE"] = "쓰는 재료보다 모으는 재료가 적다",
            ["V3.UNREACHABLE_POI"] = "이 직업이 갈 수 없는 장소를 가리킨다",
            ["V3.NO_TERMINAL"] = "하루가 휴식으로 끝나지 않는다",
            ["V3.LOOP_NOT_CLOSED"] = "하루가 한 바퀴만 돌고 멈춘다",
            ["V3.DEGENERATE"] = "같은 행동이 너무 여러 번 연속이다",
            ["V4.DEADLOCK"] = "하루가 어느 스텝에서 멈춘다",
            ["V4.INFINITE_LOOP"] = "하루가 끝나지 않는다",
            ["V4.OSCILLATION"] = "두 장소를 왔다 갔다 한다",
            ["V4.RESOURCE_STARVE"] = "필요한 재료가 바닥난다",
            ["V4.TIMEOUT"] = "하루가 시간 예산을 넘는다",
            ["LOAD"] = "로더가 이 파일을 거절했다",
            ["ProtocolVersion"] = "게임서버와 프로토콜 버전이 다르다",
            ["ContractMismatch"] = "게임서버와 계약이 다르다",
            ["MasterDataMismatch"] = "게임서버와 마스터데이터가 다르다",
            ["RosterMismatch"] = "게임서버와 로스터가 다르다",
            ["TimeScaleMismatch"] = "게임서버와 시간 배속이 다르다",
            ["AuthFailed"] = "인증에 실패했다",
            ["ShardMismatch"] = "게임서버와 샤드 구성이 다르다",
        }.ToFrozenDictionary(StringComparer.Ordinal);
}

/// <summary>
/// 검증 오류 → 화면 링크 (T15).
///
/// <b>JSON Pointer 는 사람이 따라갈 수 없다.</b> <c>/archetypes/3/population_weight</c> 는
/// 첨자 3 이 누구인지 말해 주지 않는다 — 여기서 파일을 열어 그 항목의 id 를 찾아 준다.
/// </summary>
public sealed class IssueLocator(StudioWorkspace workspace)
{
    /// <summary>오류 하나를 화면에 보여 줄 모양으로.</summary>
    /// <param name="issue">검증 오류.</param>
    public StudioIssueView View(StudioIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);

        (string link, string label) = Link(issue);

        return new StudioIssueView(issue, IssueGuide.TitleOf(issue.Code), link, label);
    }

    /// <summary>오류 목록 전체.</summary>
    /// <param name="issues">검증 오류.</param>
    public ImmutableArray<StudioIssueView> Views(ImmutableArray<StudioIssue> issues) =>
        [.. issues.Select(View)];

    private (string Link, string Label) Link(StudioIssue issue)
    {
        string file = Path.GetFileName(issue.File);

        return file switch
        {
            "archetypes.json" when ItemId(issue, "archetypes") is { Length: > 0 } id =>
                ($"/archetypes/{id}?tab=edit", $"{id} 고치러 가기"),
            "fallback_plans.json" when Archetype(issue) is { Length: > 0 } id =>
                ($"/archetypes/{id}?tab=fallback", $"{id} 의 하루 일과로"),
            "pois.json" when Zone(issue) is { Length: > 0 } zone =>
                ($"/places/{zone}", "그 지역으로"),
            "npc_overrides.json" when Npc(issue) is { } npc =>
                ($"/npcs/{npc}", $"NPC {npc} 로"),
            "interrupts.json" => ("/interrupts", "돌발 반응 화면으로"),
            "actions.json" => ("/actions", "행동 카탈로그로"),
            "zones.json" => ("/places", "지역 목록으로"),
            { Length: > 0 } => ($"/files/{file}", $"{file} 원문으로"),
            _ => (string.Empty, string.Empty),
        };
    }

    /// <summary>
    /// <c>/archetypes/3/population_weight</c> → 그 첨자 항목의 <c>id</c>.
    /// </summary>
    private string ItemId(StudioIssue issue, string array)
    {
        if (Index(issue.Path, array) is not { } index)
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(workspace.CurrentDirectory, Path.GetFileName(issue.File))));

            if (!document.RootElement.TryGetProperty(array, out JsonElement items)
                || index >= items.GetArrayLength())
            {
                return string.Empty;
            }

            return items[index].TryGetProperty("id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>하루 일과 오류는 그 직업 화면으로 보낸다.</summary>
    private string Archetype(StudioIssue issue)
    {
        if (Index(issue.Path, "plans") is not { } index)
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(workspace.CurrentDirectory, "fallback_plans.json")));

            if (!document.RootElement.TryGetProperty("plans", out JsonElement plans)
                || index >= plans.GetArrayLength())
            {
                return string.Empty;
            }

            return plans[index].TryGetProperty("archetype", out JsonElement archetype)
                ? archetype.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return string.Empty;
        }
    }

    private string Zone(StudioIssue issue)
    {
        if (Index(issue.Path, "pois") is not { } index)
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(workspace.CurrentDirectory, "pois.json")));

            if (!document.RootElement.TryGetProperty("pois", out JsonElement pois)
                || index >= pois.GetArrayLength())
            {
                return string.Empty;
            }

            return pois[index].TryGetProperty("zone", out JsonElement zone) ? zone.GetString() ?? string.Empty : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return string.Empty;
        }
    }

    private static int? Npc(StudioIssue issue)
    {
        foreach (string part in (issue.Path ?? string.Empty).Split('/'))
        {
            if (int.TryParse(part, out int number) && number > 0)
            {
                return number;
            }
        }

        return null;
    }

    /// <summary>JSON Pointer 에서 배열 첨자를 읽는다. <c>/archetypes/3/…</c> → 3.</summary>
    public static int? Index(string? pointer, string array)
    {
        if (string.IsNullOrEmpty(pointer))
        {
            return null;
        }

        string[] parts = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (string.Equals(parts[i], array, StringComparison.Ordinal)
                && int.TryParse(parts[i + 1], out int index))
            {
                return index;
            }
        }

        return null;
    }
}
