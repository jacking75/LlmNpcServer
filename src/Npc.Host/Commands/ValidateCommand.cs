using System.Globalization;
using Npc.MasterData;
using Npc.MasterData.Validation;

namespace Npc.Host.Commands;

/// <summary>
/// <c>Npc.Host validate --masterdata ./masterdata</c>. docs/11 §11.
///
/// V1~V13 결과를 출력하고, 위반이 하나라도 있으면 비0으로 끝난다.
/// CI 와 기동 스크립트가 이 종료 코드를 본다 — 검증 실패는 기동 실패다.
/// </summary>
public static class ValidateCommand
{
    /// <summary>서브커맨드 이름.</summary>
    public const string Name = "validate";

    /// <summary>사용법.</summary>
    public const string Usage =
        "사용법: Npc.Host validate --masterdata <경로> [--prefix-tokens <n>] [--format text|json]";

    /// <summary>검증 실행. 0 = 통과, 1 = 위반, 2 = 인자 오류.</summary>
    public static int Run(string[] args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        string? directory = null;
        int? prefixTokens = null;
        bool json = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--masterdata" when i + 1 < args.Length:
                    directory = args[++i];
                    break;

                case "--prefix-tokens" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tokens))
                    {
                        output.WriteLine($"--prefix-tokens 값이 정수가 아니다: {args[i]}");
                        return 2;
                    }

                    prefixTokens = tokens;
                    break;

                case "--format" when i + 1 < args.Length:
                    switch (args[++i])
                    {
                        case "json":
                            json = true;
                            break;

                        case "text":
                            json = false;
                            break;

                        default:
                            output.WriteLine($"--format 값이 잘못됐다: {args[i]}. text|json 중 하나다.");
                            return 2;
                    }

                    break;

                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;

                default:
                    output.WriteLine($"모르는 인자: {args[i]}");
                    output.WriteLine(Usage);
                    return 2;
            }
        }

        directory ??= "./masterdata";

        if (ResolveDirectory(directory) is not { } resolved)
        {
            output.WriteLine($"masterdata 폴더를 찾지 못했다: {directory}");
            return 2;
        }

        directory = resolved;

        MasterDataValidationReport report =
            MasterDataValidator.Validate(directory, new MasterDataValidationOptions(prefixTokens));

        if (json)
        {
            return RunJson(report, directory, output);
        }

        output.WriteLine($"masterdata: {Path.GetFullPath(directory)}");

        foreach (SkippedRule skip in report.Skipped)
        {
            output.WriteLine($"  SKIP {skip.Code}  {skip.Reason}");
        }

        if (report.IsValid)
        {
            // 로더까지 돌려봐야 참조 무결성과 거리 행렬 정합성이 확인된다.
            try
            {
                MasterDataSet data = MasterDataLoader.Load(directory);

                output.WriteLine(
                    $"  OK   액션 {data.Actions.Count} · 아이템 {data.Items.Items.Length} · 존 {data.Zones.Count}"
                    + $" · POI {data.Pois.Count} · 아키타입 {data.Archetypes.Count} · 인터럽트 {data.Interrupts.Count}");
                output.WriteLine($"  content_hash: {data.ContentHash}");

                // 파생물 신선도 (F-04). 검증 실패로 세지 않는다 — 규칙 위반이 아니라
                // "다시 만들어야 한다" 는 사실이고, 판정은 사람이 한다.
                foreach (Npc.MasterData.Authoring.DerivedStatus stale in data.StaleArtifacts)
                {
                    output.WriteLine($"  WARN 파생물 {stale}");
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
            {
                output.WriteLine($"  FAIL LOAD  {ex.Message}");
                return 1;
            }

            output.WriteLine($"검증 통과 (V1~V13, 건너뜀 {report.Skipped.Length}건)");
            return 0;
        }

        foreach (MasterDataViolation violation in report.Violations)
        {
            output.WriteLine($"  FAIL {violation.Code}  {violation.Detail}");

            // 힌트는 사람에게도 도움이 된다 (E-04). 없는 코드는 조용히 넘어간다 —
            // 그런 코드가 있으면 FixHintTests 가 먼저 깨진다.
            if (violation.FixHint is { Length: > 0 } hint)
            {
                output.WriteLine($"       → {hint}");
            }
        }

        output.WriteLine($"검증 실패 {report.Violations.Length}건");
        return 1;
    }

    /// <summary>
    /// 기계가 읽는 출력 (E-04). <b>로더까지 돌려 본다</b> — 규칙만 통과하고 참조가 깨진
    /// 상태를 <c>ok: true</c> 로 내면 호출부가 그 위에 다음 작업을 쌓는다.
    /// </summary>
    private static int RunJson(
        MasterDataValidationReport report, string directory, TextWriter output)
    {
        MasterDataSet? data = null;
        string? loadError = null;

        try
        {
            data = MasterDataLoader.Load(directory);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            loadError = ex.Message;
        }

        ValidationResultJson result = ValidationJson.From(report, directory, data, loadError);

        output.WriteLine(ValidationJson.Serialize(result));

        return result.Ok ? 0 : 1;
    }

    /// <summary>
    /// 상대 경로를 현재 폴더와 실행 파일 위치의 조상에서 찾는다.
    /// <c>dotnet run --project src/Npc.Host</c> 는 작업 폴더를 프로젝트 폴더로 바꾸므로,
    /// 저장소 루트에서 <c>--masterdata ./masterdata</c> 라고 써도 찾을 수 있어야 한다.
    /// </summary>
    private static string? ResolveDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            return path;
        }

        if (Path.IsPathRooted(path))
        {
            return null;
        }

        foreach (string origin in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(origin);

            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, path);
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }
        }

        return null;
    }
}
