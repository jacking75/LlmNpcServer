using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Npc.Core;
using Npc.MasterData;
using Npc.MasterData.Authoring;

namespace Npc.Studio.Services;

/// <summary>시작 소지품 한 줄 (T10).</summary>
/// <param name="Item">아이템 id.</param>
/// <param name="Count">개수.</param>
public sealed record StudioInventoryLine(string Item, int Count);

/// <summary>
/// 아키타입 폼 (T10). <b>JSON 원문 대신 이 값들이 입력 칸이 된다.</b>
/// </summary>
/// <param name="Id">직업 id. 읽기 전용.</param>
/// <param name="Desc">설명. LLM 프롬프트에 그대로 실린다.</param>
/// <param name="AllowedActions">할 수 있는 행동 (id 오름차순).</param>
/// <param name="HomePoiType">사는 곳 유형.</param>
/// <param name="WorkplacePoiType">일터 유형. 없으면 null.</param>
/// <param name="PrimaryRecipes">주력 제작품.</param>
/// <param name="Diligence">근면.</param>
/// <param name="Sociability">사교.</param>
/// <param name="Courage">용기.</param>
/// <param name="Greed">탐욕.</param>
/// <param name="DefaultGoals">기본 목표.</param>
/// <param name="InitialInventory">시작 소지품.</param>
/// <param name="DutyHours">근무 시간대.</param>
/// <param name="CombatCapable">싸울 수 있나.</param>
/// <param name="PopulationWeight">인구 비율.</param>
public sealed record StudioArchetypeForm(
    string Id,
    string Desc,
    ImmutableArray<string> AllowedActions,
    string HomePoiType,
    string? WorkplacePoiType,
    ImmutableArray<string> PrimaryRecipes,
    int Diligence,
    int Sociability,
    int Courage,
    int Greed,
    ImmutableArray<string> DefaultGoals,
    ImmutableArray<StudioInventoryLine> InitialInventory,
    ImmutableArray<TimeOfDay> DutyHours,
    bool CombatCapable,
    double PopulationWeight)
{
    /// <summary>
    /// 폼 속성 → JSON 키. <b>드리프트 테스트(T20)가 이 표를 본다</b> —
    /// 새 필드를 폼에 넣고 사전(FieldGuide)에 빼먹으면 그 칸은 뜻을 모르는 입력란이 된다.
    /// </summary>
    public static ImmutableDictionary<string, string> JsonKeys { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(Id)] = "id",
            [nameof(Desc)] = "desc",
            [nameof(AllowedActions)] = "allowed_actions",
            [nameof(HomePoiType)] = "home_poi_type",
            [nameof(WorkplacePoiType)] = "workplace_poi_type",
            [nameof(PrimaryRecipes)] = "primary_recipes",
            [nameof(Diligence)] = "traits/diligence",
            [nameof(Sociability)] = "traits/sociability",
            [nameof(Courage)] = "traits/courage",
            [nameof(Greed)] = "traits/greed",
            [nameof(DefaultGoals)] = "default_goals",
            [nameof(InitialInventory)] = "initial_inventory",
            [nameof(DutyHours)] = "duty_hours",
            [nameof(CombatCapable)] = "combat_capable",
            [nameof(PopulationWeight)] = "population_weight",
        }.ToImmutableDictionary(StringComparer.Ordinal);
}

/// <summary>스텝 인자 하나 (T11).</summary>
/// <param name="Name">인자 이름.</param>
/// <param name="Raw">값의 JSON 표현.</param>
public readonly record struct StudioArg(string Name, string Raw);

/// <summary>
/// 하루 일과 스텝 하나 (T11).
///
/// <b>인자는 배열이다.</b> 사전으로 두면 쓸 때 순서를 정해야 하는데, 파일의 키 순서는
/// 사람이 쓴 순서지 사전순이 아니다 — 정렬해서 다시 쓰면 무변경 저장이 바이트 동일이 아니게 된다.
/// </summary>
/// <param name="Action">행동 id.</param>
/// <param name="Args">인자. 파일에 적힌 순서 그대로다.</param>
/// <param name="TimeoutSeconds">최대 시간(초).</param>
public sealed record StudioPlanStep(string Action, ImmutableArray<StudioArg> Args, int TimeoutSeconds)
{
    /// <summary>이 인자의 값. 없으면 빈 문자열.</summary>
    /// <param name="name">인자 이름.</param>
    public string Arg(string name)
    {
        foreach (StudioArg arg in Args)
        {
            if (string.Equals(arg.Name, name, StringComparison.Ordinal))
            {
                return arg.Raw;
            }
        }

        return string.Empty;
    }

    /// <summary>인자를 바꾸거나 없으면 <b>맨 뒤에</b> 더한다. 빈 값이면 지운다.</summary>
    /// <param name="name">인자 이름.</param>
    /// <param name="raw">값의 JSON 표현. 비면 지운다.</param>
    public StudioPlanStep WithArg(string name, string raw)
    {
        if (raw.Length == 0)
        {
            return this with { Args = [.. Args.Where(a => !string.Equals(a.Name, name, StringComparison.Ordinal))] };
        }

        for (int i = 0; i < Args.Length; i++)
        {
            if (string.Equals(Args[i].Name, name, StringComparison.Ordinal))
            {
                return this with { Args = Args.SetItem(i, new StudioArg(name, raw)) };
            }
        }

        return this with { Args = Args.Add(new StudioArg(name, raw)) };
    }
}

/// <summary>하루 일과 폼 (T11).</summary>
/// <param name="Id">일과 id.</param>
/// <param name="Archetype">직업 id.</param>
/// <param name="Goal">하루 목표.</param>
/// <param name="Steps">스텝.</param>
/// <param name="Loop">반복하는가.</param>
/// <param name="OnStepFail">스텝 실패 시 정책.</param>
public sealed record StudioFallbackForm(
    string Id,
    string Archetype,
    string Goal,
    ImmutableArray<StudioPlanStep> Steps,
    bool Loop,
    string OnStepFail);

/// <summary>
/// 파일 서식을 흉내 내는 JSON 조각 만들기 (T10).
///
/// <b>서식을 원문에서 읽는다.</b> 4칸이라고 가정하면 diff 에 공백 변경이 섞이고,
/// 그 diff 는 리뷰할 수 없다 — <c>JsonSurgeon</c> 이 같은 이유로 같은 모양이다.
/// </summary>
public static class StudioJsonFormat
{
    /// <summary>문자열 배열. 원소마다 줄바꿈한다. 비면 <c>[]</c>.</summary>
    /// <param name="values">원소.</param>
    /// <param name="indent">닫는 대괄호의 들여쓰기.</param>
    public static string Strings(IEnumerable<string> values, string indent) =>
        Raw(values.Select(Text), indent);

    /// <summary>이미 JSON 표현인 원소들의 배열.</summary>
    /// <param name="values">원소의 JSON 표현.</param>
    /// <param name="indent">닫는 대괄호의 들여쓰기.</param>
    public static string Raw(IEnumerable<string> values, string indent)
    {
        ArgumentNullException.ThrowIfNull(values);

        ImmutableArray<string> list = [.. values];

        if (list.IsEmpty)
        {
            return "[]";
        }

        var sb = new StringBuilder(list.Length * 24);

        sb.Append('[');

        for (int i = 0; i < list.Length; i++)
        {
            sb.Append(i == 0 ? string.Empty : ",").AppendLine()
              .Append(indent).Append("  ").Append(list[i]);
        }

        sb.AppendLine().Append(indent).Append(']');

        return sb.ToString();
    }

    /// <summary>키 → JSON 표현의 객체. 키 순서를 그대로 쓴다.</summary>
    /// <param name="fields">키와 값.</param>
    /// <param name="indent">닫는 중괄호의 들여쓰기.</param>
    public static string Object(IEnumerable<(string Key, string Value)> fields, string indent)
    {
        ArgumentNullException.ThrowIfNull(fields);

        ImmutableArray<(string Key, string Value)> list = [.. fields];

        if (list.IsEmpty)
        {
            return "{}";
        }

        var sb = new StringBuilder(list.Length * 24);

        sb.Append('{');

        for (int i = 0; i < list.Length; i++)
        {
            sb.Append(i == 0 ? string.Empty : ",").AppendLine()
              .Append(indent).Append("  ").Append(Text(list[i].Key)).Append(": ").Append(list[i].Value);
        }

        sb.AppendLine().Append(indent).Append('}');

        return sb.ToString();
    }

    /// <summary>
    /// 이 속성이 놓인 줄의 들여쓰기. 없으면 <paramref name="fallback"/>.
    /// <b>새 값의 들여쓰기를 원문에서 읽는 유일한 방법이다.</b>
    /// </summary>
    /// <param name="json">객체 하나를 담은 JSON.</param>
    /// <param name="property">속성 이름.</param>
    /// <param name="fallback">속성이 없을 때 쓸 들여쓰기.</param>
    public static string IndentOf(string json, string property, string fallback)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        ArgumentException.ThrowIfNullOrEmpty(property);

        int at = json.IndexOf("\"" + property + "\"", StringComparison.Ordinal);

        if (at < 0)
        {
            return fallback;
        }

        int line = json.LastIndexOf('\n', at);
        int begin = line + 1;
        int i = begin;

        while (i < at && (json[i] == ' ' || json[i] == '\t'))
        {
            i++;
        }

        return i > begin ? json[begin..i] : fallback;
    }

    /// <summary>
    /// 항목 하나의 들여쓰기를 0 으로 되돌린다.
    ///
    /// <b><see cref="JsonSurgeon.AppendToArray"/> 가 배열의 들여쓰기를 다시 붙인다</b> —
    /// 이미 들여쓴 텍스트를 그대로 주면 두 번 들어가 diff 가 지저분해진다.
    /// </summary>
    /// <param name="item">항목 JSON.</param>
    public static string Dedent(string item)
    {
        ArgumentException.ThrowIfNullOrEmpty(item);

        string[] lines = item.ReplaceLineEndings("\n").Split('\n');

        if (lines.Length == 1)
        {
            return item;
        }

        // 마지막 줄(닫는 괄호)의 들여쓰기가 이 항목의 기준이다.
        int baseIndent = lines[^1].Length - lines[^1].TrimStart().Length;

        if (baseIndent == 0)
        {
            return item;
        }

        var sb = new StringBuilder(item.Length);

        sb.Append(lines[0]);

        for (int i = 1; i < lines.Length; i++)
        {
            int strip = 0;

            while (strip < baseIndent && strip < lines[i].Length && lines[i][strip] is ' ' or '\t')
            {
                strip++;
            }

            sb.Append(Environment.NewLine).Append(lines[i][strip..]);
        }

        return sb.ToString();
    }

    /// <summary>문자열 리터럴. 한글을 escape 하지 않는다.</summary>
    /// <param name="value">문자열.</param>
    public static string Text(string value) => JsonSerializer.Serialize(value, JsonSurgeonText.Options);

    /// <summary>정수 리터럴.</summary>
    /// <param name="value">값.</param>
    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>가중치 리터럴. 파일과 같은 자릿수여야 표와 파일이 어긋나지 않는다.</summary>
    /// <param name="value">값.</param>
    public static string Weight(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>불리언 리터럴.</summary>
    /// <param name="value">값.</param>
    public static string Bool(bool value) => value ? "true" : "false";
}
