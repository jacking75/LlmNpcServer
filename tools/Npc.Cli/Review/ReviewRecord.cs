using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Npc.Cli.Review;

/// <summary>
/// 폐기 사유 (F-06). <c>masterdata/prompt/system_rules.md</c> 의 "WHAT MAKES A PLAN GOOD" 4항목 그대로다.
///
/// <para>
/// <b>자유 문장이 아니라 코드다.</b> 사유가 문장이면 집계가 안 되고, 집계가 안 되면
/// "무엇을 고쳐야 통과율이 오르나" 에 답할 수 없다 — C-05 의 few-shot 후보도 여기서 나온다.
/// </para>
/// </summary>
public enum RejectReason
{
    /// <summary>사유 없음. 채택·수정일 때.</summary>
    None = 0,

    /// <summary>상황에 맞지 않는다 — 시간대·지역상태·기후와 어긋난다.</summary>
    Situation = 1,

    /// <summary>성향에 맞지 않는다 — 이 아키타입이 할 법한 일이 아니다.</summary>
    Character = 2,

    /// <summary>동선이 비경제적이다 — 같은 곳을 오가거나 헛걸음이 있다.</summary>
    Route = 3,

    /// <summary>생존 불가 — 먹지도 자지도 않거나 자원이 모자란다.</summary>
    Survival = 4,
}

/// <summary>검수 판정 (F-06).</summary>
public enum ReviewVerdict
{
    /// <summary>건너뜀. 기록하지 않는다.</summary>
    Skip = 0,

    /// <summary>그대로 채택.</summary>
    Accept = 1,

    /// <summary>고쳐서 채택. <c>pinned/</c> 에 저장된다.</summary>
    Edit = 2,

    /// <summary>폐기.</summary>
    Reject = 3,
}

/// <summary>
/// 판정 한 줄 (F-06).
///
/// <para>
/// <b>기존 형식과 호환된다.</b> <c>bucket</c>·<c>verdict</c>·<c>minutes</c>·<c>note</c> 는
/// <c>review_W8.jsonl</c> 과 같은 이름·같은 뜻이고, 게이트 테스트가 그 파서를 쓴다.
/// 새 필드(<c>reason_code</c>·<c>edited_steps</c>·<c>origin</c>)는 뒤에 붙는다.
/// </para>
/// </summary>
/// <param name="Bucket">버킷 이름.</param>
/// <param name="Verdict">판정. jsonl 에는 <c>accept</c>·<c>edit</c>·<c>reject</c> 로 적는다.</param>
/// <param name="Minutes">판정에 걸린 분.</param>
/// <param name="Note">검수자가 남긴 한 줄.</param>
/// <param name="Reason">폐기 사유 코드.</param>
/// <param name="EditedSteps">수정한 스텝 수. 채택·폐기면 0.</param>
/// <param name="Origin">검수 시점의 플랜 출처.</param>
public readonly record struct ReviewRecord(
    string Bucket,
    ReviewVerdict Verdict,
    double Minutes,
    string Note = "",
    RejectReason Reason = RejectReason.None,
    int EditedSteps = 0,
    string Origin = "")
{
    /// <summary>jsonl 한 줄. <b>손으로 쓴다</b> — 이 프로젝트의 다른 측정 파일과 같은 방식이다.</summary>
    public string ToJsonLine()
    {
        var sb = new StringBuilder(256);

        sb.Append(CultureInfo.InvariantCulture, $"{{\"bucket\":\"{Escape(Bucket)}\"");
        sb.Append(CultureInfo.InvariantCulture, $",\"verdict\":\"{Text(Verdict)}\"");
        sb.Append(CultureInfo.InvariantCulture, $",\"minutes\":{Minutes.ToString("0.##", CultureInfo.InvariantCulture)}");

        if (Note.Length > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $",\"note\":\"{Escape(Note)}\"");
        }

        if (Reason != RejectReason.None)
        {
            sb.Append(CultureInfo.InvariantCulture, $",\"reason_code\":\"{Reason}\"");
        }

        if (EditedSteps > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $",\"edited_steps\":{EditedSteps}");
        }

        if (Origin.Length > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $",\"origin\":\"{Escape(Origin)}\"");
        }

        sb.Append('}');

        return sb.ToString();
    }

    /// <summary>jsonl 에 적는 판정 문자열. <b>옛 파일과 같은 낱말이다.</b></summary>
    public static string Text(ReviewVerdict verdict) => verdict switch
    {
        ReviewVerdict.Accept => "accept",
        ReviewVerdict.Edit => "edit",
        ReviewVerdict.Reject => "reject",
        _ => "skip",
    };

    /// <summary>사유 코드 목록. 검수 화면이 이 순서로 보여 준다.</summary>
    public static ImmutableArray<(RejectReason Reason, string Label)> Reasons { get; } =
    [
        (RejectReason.Situation, "상황 부적합 — 시간대·지역상태·기후와 어긋난다"),
        (RejectReason.Character, "성향 부적합 — 이 아키타입이 할 법한 일이 아니다"),
        (RejectReason.Route, "비경제 동선 — 같은 곳을 오가거나 헛걸음이 있다"),
        (RejectReason.Survival, "생존 불가 — 먹지도 자지도 않거나 자원이 모자란다"),
    ];

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal);
}
