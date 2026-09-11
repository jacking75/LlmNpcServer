using System.Collections.Immutable;
using System.Globalization;

namespace Npc.Eval;

/// <summary>
/// <c>Npc.Eval</c> 인자 (C-04).
/// </summary>
/// <param name="Engines">대조할 엔진 id. 첫 엔진이 기준선이다.</param>
/// <param name="Sample">버킷 표본 수.</param>
/// <param name="Runs">같은 표본을 몇 번 돌릴까. <b>통과율의 분산을 보려면 2 이상</b>이다.</param>
/// <param name="BudgetUsd">회차 전체의 하드 캡. 0 이면 제한 없음.</param>
/// <param name="OutDirectory">보고서를 쓸 폴더.</param>
/// <param name="Title">보고서 제목에 붙일 회차 이름.</param>
/// <param name="MasterData">마스터데이터 폴더.</param>
/// <param name="Concurrency">동시성. 외부 엔진 기본은 8 이다 (실측 24 까지 429 0회).</param>
/// <param name="GoldenRate">
/// 골든 회귀 단언 합격률 0~1. <b>여기서 돌리지 않는다</b> — 골든 러너는 테스트 스위트에
/// 있고(<c>dotnet test --filter Category=Golden</c>) 그 결과를 사람이 넣는다. 도구가 러너를
/// 다시 만들면 두 벌이 갈라져 "테스트는 통과인데 평가는 불합격" 이 생긴다. -1 = 안 돌렸다.
/// </param>
/// <param name="GoldenAssertions">그 회차의 단언 수. 0 이면 미판정이다.</param>
/// <param name="Judge">LLM 심사원 엔진 id. 비어 있으면 안 돌린다. <b>게이트가 아니다.</b></param>
/// <param name="DryRunSample">드라이런 표본 비율 0~1.</param>
public sealed record EvalOptions(
    ImmutableArray<string> Engines,
    int Sample = 48,
    int Runs = 1,
    double BudgetUsd = 0,
    string OutDirectory = "docs/measurements/eval",
    string Title = "eval",
    string MasterData = "./masterdata",
    int Concurrency = 8,
    double GoldenRate = -1,
    int GoldenAssertions = 0,
    string Judge = "",
    double DryRunSample = 1.0)
{
    /// <summary>사용법. <c>--help</c> 가 낸다.</summary>
    public const string Usage = """
        Npc.Eval — 단일 평가 파이프라인 (C-04)

          --engines <id>[,<id>]   대조할 엔진. 첫 엔진이 기준선이다 (필수)
          --sample N              버킷 표본 수 (기본 48). 균등 간격으로 뽑는다
          --runs N                같은 표본을 몇 번 (기본 1)
          --budget-usd N          회차 전체 하드 캡. 0=무제한 (기본 0)
          --out <dir>             보고서 폴더 (기본 docs/measurements/eval)
          --title <name>          보고서 제목 (기본 eval)
          --masterdata <dir>      마스터데이터 (기본 ./masterdata)
          --concurrency N         동시 요청 수 (기본 8)
          --golden-rate <0..1>    골든 단언 합격률. dotnet test --filter Category=Golden 의 결과
          --golden-assertions N   그 회차의 단언 수. 둘 다 줘야 판정한다
          --judge <engine>        LLM 심사원. 권고 신호이지 게이트가 아니다
          --dry-run-sample <0..1> 드라이런 표본 비율 (기본 1.0)

        게이트 — 통과율 ≥ 90% · 다양성 ≥ 60% · 골든 ≥ 90% · 폴백률 ≤ 5%
        불합격이 하나라도 있으면 종료 코드 1 이다. 미판정은 통과가 아니지만 막지도 않는다.
        """;

    /// <summary>인자를 읽는다. 모르는 인자는 실패다 — 조용히 넘기면 오타가 기본값으로 돈다.</summary>
    /// <param name="args">명령줄.</param>
    /// <param name="options">읽은 값.</param>
    /// <param name="error">실패 사유. 성공이면 null.</param>
    public static bool TryParse(string[] args, out EvalOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        var result = new EvalOptions([]);

        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "--engines" when i + 1 < args.Length:
                    result = result with
                    {
                        Engines = [.. args[++i].Split(
                            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                    };
                    break;

                case "--sample" when i + 1 < args.Length:
                    result = result with { Sample = Int(args[++i]) };
                    break;

                case "--runs" when i + 1 < args.Length:
                    result = result with { Runs = Int(args[++i]) };
                    break;

                case "--budget-usd" when i + 1 < args.Length:
                    result = result with { BudgetUsd = Number(args[++i]) };
                    break;

                case "--out" when i + 1 < args.Length:
                    result = result with { OutDirectory = args[++i] };
                    break;

                case "--title" when i + 1 < args.Length:
                    result = result with { Title = args[++i] };
                    break;

                case "--masterdata" when i + 1 < args.Length:
                    result = result with { MasterData = args[++i] };
                    break;

                case "--concurrency" when i + 1 < args.Length:
                    result = result with { Concurrency = Int(args[++i]) };
                    break;

                case "--golden-rate" when i + 1 < args.Length:
                    result = result with { GoldenRate = Number(args[++i]) };
                    break;

                case "--golden-assertions" when i + 1 < args.Length:
                    result = result with { GoldenAssertions = Int(args[++i]) };
                    break;

                case "--judge" when i + 1 < args.Length:
                    result = result with { Judge = args[++i] };
                    break;

                case "--dry-run-sample" when i + 1 < args.Length:
                    result = result with { DryRunSample = Number(args[++i]) };
                    break;

                default:
                    error = $"모르는 인자다: {arg}";
                    options = result;
                    return false;
            }
        }

        if (result.Engines.Length == 0)
        {
            error = "--engines 가 없다. 대조할 엔진을 하나 이상 준다.";
            options = result;
            return false;
        }

        if (result.Sample <= 0 || result.Runs <= 0 || result.Concurrency <= 0)
        {
            error = "--sample · --runs · --concurrency 는 1 이상이어야 한다.";
            options = result;
            return false;
        }

        options = result;
        return true;
    }

    private static int Int(string text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : -1;

    private static double Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : -1;
}
