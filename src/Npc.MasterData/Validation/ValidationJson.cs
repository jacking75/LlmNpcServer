using System.Text.Json;
using System.Text.Json.Serialization;

namespace Npc.MasterData.Validation;

/// <summary>기계가 읽는 위반 하나 (E-04).</summary>
/// <param name="Code">검증 코드.</param>
/// <param name="File">파일 이름. 특정할 수 없으면 빈 문자열.</param>
/// <param name="Path">JSON Pointer.</param>
/// <param name="Message">사람이 읽는 한 줄.</param>
/// <param name="FixHint">무엇을 하면 되는가.</param>
/// <param name="Related">근거 문서.</param>
public sealed record ViolationJson(
    string Code,
    string File,
    string Path,
    string Message,
    string FixHint,
    IReadOnlyList<string> Related);

/// <summary>건너뛴 규칙 하나.</summary>
/// <param name="Code">코드.</param>
/// <param name="Reason">왜 건너뛰었나.</param>
public sealed record SkippedJson(string Code, string Reason);

/// <summary>
/// 검증 결과 한 장 (E-04).
///
/// <b>스키마는 <c>docs/schema/validation_result.schema.json</c> 이 발행한다</b> (E-02).
/// </summary>
/// <param name="Ok">위반이 없으면 true.</param>
/// <param name="MasterData">검증한 디렉터리.</param>
/// <param name="ContentHash">내용 해시. 로드에 실패했으면 빈 문자열.</param>
/// <param name="StructuralHash">구조 해시 (B-04).</param>
/// <param name="Violations">위반 목록.</param>
/// <param name="Skipped">건너뛴 규칙.</param>
public sealed record ValidationResultJson(
    bool Ok,
    string MasterData,
    string ContentHash,
    string StructuralHash,
    IReadOnlyList<ViolationJson> Violations,
    IReadOnlyList<SkippedJson> Skipped);

/// <summary>
/// 검증 결과를 기계가 읽는 JSON 으로 (E-04).
///
/// <b>사람용 출력을 없애지 않는다.</b> 한국어 한 줄은 그대로 좋고, <c>--format json</c> 은
/// 그 옆에 붙는 다른 표현이다 — LLM·에디터·CI 가 파싱한다.
/// </summary>
public static class ValidationJson
{
    /// <summary>
    /// 직렬화 옵션.
    ///
    /// <b>한글을 escape 하지 않는다</b> — LLM 도 사람도 같은 것을 읽는다.
    ///
    /// <b>필드는 snake_case 다.</b> 마스터데이터 JSON 이 전부 그렇고
    /// (<c>population_weight</c>·<c>total_keys</c>·<c>home_poi_type</c>),
    /// 검증 출력만 camelCase 면 LLM 이 두 표기를 오가게 된다.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>보고서를 JSON 문서로 만든다.</summary>
    /// <param name="report">검증 결과.</param>
    /// <param name="directory">검증한 디렉터리.</param>
    /// <param name="data">로드가 성공했으면 그 결과. 실패했으면 null.</param>
    /// <param name="loadError">로더 예외 메시지. 없으면 null.</param>
    public static ValidationResultJson From(
        MasterDataValidationReport report,
        string directory,
        MasterDataSet? data,
        string? loadError)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(directory);

        var violations = new List<ViolationJson>(report.Violations.Length + 1);

        foreach (MasterDataViolation violation in report.Violations)
        {
            violations.Add(new ViolationJson(
                violation.Code,
                violation.File,
                violation.Path,
                violation.Detail,
                violation.FixHint,
                [.. violation.Related]));
        }

        // 로더 실패는 규칙 위반이 아니지만 같은 목록에 넣는다 —
        // 호출부가 "왜 못 썼나" 를 한 곳에서 읽어야 한다.
        if (loadError is { Length: > 0 })
        {
            violations.Add(new ViolationJson(
                "LOAD",
                string.Empty,
                string.Empty,
                loadError,
                "참조 무결성이나 거리 행렬이 어긋났다. 마지막에 고친 파일부터 본다 — "
                + "poi_distances.bin 은 pois.json 을 바꾸면 `npc regen` 으로 다시 만든다.",
                ["docs/reference_masterdata.html"]));
        }

        var skipped = new List<SkippedJson>(report.Skipped.Length);

        foreach (SkippedRule rule in report.Skipped)
        {
            skipped.Add(new SkippedJson(rule.Code, rule.Reason));
        }

        return new ValidationResultJson(
            Ok: violations.Count == 0,
            MasterData: System.IO.Path.GetFullPath(directory),
            ContentHash: data?.ContentHash ?? string.Empty,
            StructuralHash: data?.StructuralHash ?? string.Empty,
            violations,
            skipped);
    }

    /// <summary>직렬화한다.</summary>
    public static string Serialize(ValidationResultJson result) =>
        JsonSerializer.Serialize(result, Options);
}
