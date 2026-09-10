using System.Collections.Immutable;

namespace Npc.Conformance;

/// <summary>검사 하나의 판정.</summary>
public enum Verdict
{
    /// <summary>규약을 지켰다.</summary>
    Pass = 0,

    /// <summary>어겼다. <b>보고서에서 이것 하나면 불합격이다.</b></summary>
    Fail = 1,

    /// <summary>
    /// 판정하지 않았다. <b>통과가 아니다.</b>
    ///
    /// 이 회차의 조건에서 볼 수 없는 항목(예: 플레이어가 없어 근접 이벤트가 0건)이나,
    /// 관찰만으로는 판정할 수 없는 항목(구동 회차의 틱 속도)이 여기 온다.
    /// <b>사유를 반드시 적는다</b> — 사유 없는 미판정은 통과로 읽힌다.
    /// </summary>
    NotChecked = 2,
}

/// <summary>
/// 검사 결과 한 줄.
/// </summary>
/// <param name="Id">검사 id. 보고서·JSON 의 키다. 재배치하지 않는다.</param>
/// <param name="Title">사람이 읽는 이름.</param>
/// <param name="Verdict">판정.</param>
/// <param name="Detail">근거. <b>수치를 적는다</b> — "통과" 만으로는 다음 사람이 확인할 수 없다.</param>
/// <param name="Violations">위반 사례. 최대 <see cref="MaxViolations"/> 건만 싣는다.</param>
public readonly record struct CheckResult(
    string Id,
    string Title,
    Verdict Verdict,
    string Detail,
    ImmutableArray<string> Violations)
{
    /// <summary>보고서에 싣는 위반 사례 상한. 넘으면 "외 N 건" 으로 줄인다.</summary>
    public const int MaxViolations = 5;

    /// <summary>통과.</summary>
    public static CheckResult Pass(string id, string title, string detail) =>
        new(id, title, Conformance.Verdict.Pass, detail, []);

    /// <summary>불합격.</summary>
    public static CheckResult Fail(
        string id, string title, string detail, IEnumerable<string> violations)
    {
        ArgumentNullException.ThrowIfNull(violations);

        string[] all = [.. violations];

        ImmutableArray<string> shown = all.Length <= MaxViolations
            ? [.. all]
            : [.. all.Take(MaxViolations), $"… 외 {all.Length - MaxViolations}건"];

        return new CheckResult(id, title, Conformance.Verdict.Fail, detail, shown);
    }

    /// <summary>미판정. 사유가 곧 <paramref name="reason"/> 다.</summary>
    public static CheckResult NotChecked(string id, string title, string reason) =>
        new(id, title, Conformance.Verdict.NotChecked, reason, []);
}

/// <summary>검사 하나. <b>순수 함수다</b> — 관찰 기록만 보고 판정한다.</summary>
public interface IConformanceCheck
{
    /// <summary>검사 id.</summary>
    string Id { get; }

    /// <summary>사람이 읽는 이름.</summary>
    string Title { get; }

    /// <summary>판정한다.</summary>
    /// <param name="observation">관찰 기록.</param>
    CheckResult Run(Observation observation);
}
