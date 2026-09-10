using System.Collections.Immutable;
using System.Globalization;

namespace Npc.Prebake;

/// <summary>생성 티어. docs/13 §4 의 <c>--tier</c>.</summary>
public enum PrebakeTier
{
    /// <summary>로컬 엔진 (dotLLM · llama.cpp).</summary>
    T1,

    /// <summary>외부 API.</summary>
    T2,
}

/// <summary>
/// 프리베이크 CLI 옵션. docs/13 §4.
///
/// <b>인자 파싱을 직접 한다.</b> <c>ConfigurationBuilder(args)</c> 는 <c>--resume</c> 같은
/// 값 없는 플래그를 거부한다 — <c>HostOptions.TryParse</c>(T1-57)와 같은 형태를 따른다.
///
/// <para>
/// <b>T2-19 러너의 <c>--out</c> 의미가 바뀌었다.</b> docs/13 §4 가 <c>--out ./planstore</c> 로
/// 정해 놓았으므로 <c>--out</c> 은 플랜 스토어 폴더이고, 버킷별 JSONL 결과는 <c>--report</c> 다.
/// 예전 이름 <c>--planstore</c>·<c>--engine</c> 은 별칭으로 남긴다.
/// </para>
/// </summary>
public sealed record PrebakeOptions
{
    /// <summary>AIMD 초기 동시성 기본값.</summary>
    /// <remarks>
    /// <b>8 이다.</b> "동시 32" 는 상위 계획의 추정이고 W1 은 외부 API 동시성을 재지 않았다.
    /// T2-19 첫 회차에서 동시 18까지 429 가 한 번도 나지 않았다 (<c>W6_compile_stats.md §6</c>).
    /// </remarks>
    public const int DefaultConcurrency = 8;

    /// <summary>예산 하드 캡 기본값(USD). docs/13 §4.</summary>
    public const double DefaultBudgetUsd = 5.00;

    /// <summary>마스터데이터 폴더.</summary>
    public string MasterData { get; init; } = "./masterdata";

    /// <summary>플랜 스토어 폴더. <c>plans/</c>·<c>pinned/</c>·<c>rejected/</c> 가 그 밑이다.</summary>
    public string Out { get; init; } = "./planstore";

    /// <summary>버킷별 결과 JSONL. 회차 간 diff 와 실패 집계(T2-20)가 읽는다.</summary>
    public string Report { get; init; } = "./docs/measurements/W8_prebake.jsonl";

    /// <summary>생성 티어.</summary>
    public PrebakeTier Tier { get; init; } = PrebakeTier.T2;

    /// <summary>엔진 id. <c>appsettings.Llm.json</c> 의 것. null 이면 그 파일의 default.</summary>
    public string? Model { get; init; }

    /// <summary>AIMD 초기 동시성.</summary>
    public int Concurrency { get; init; } = DefaultConcurrency;

    /// <summary>AIMD 상한.</summary>
    public int MaxConcurrency { get; init; } = 32;

    /// <summary>드라이런(4단)을 돌릴 비율. <b>프리베이크는 1.0(전수)이 기본이다</b> (docs/13 §8).</summary>
    public double DryRunSample { get; init; } = 1.0;

    /// <summary>기존 스토어에서 이어서. 미생성 버킷만 만든다.</summary>
    public bool Resume { get; init; }

    /// <summary>부분 재생성 glob. 비어 있으면 전 대상.</summary>
    public ImmutableArray<string> Only { get; init; } = [];

    /// <summary>예산 하드 캡(USD). 초과 시 중단하고 manifest 를 부분 상태로 기록한다.</summary>
    public double BudgetUsd { get; init; } = DefaultBudgetUsd;

    /// <summary>
    /// 생성 시각 문자열. <b>manifest 에 그대로 실린다</b> — 도구가 <c>DateTime.Now</c> 를 부르지 않는다
    /// (CLAUDE.md §2.3). 비워 두면 결정론적 산출물이 된다.
    /// </summary>
    public string GeneratedAt { get; init; } = string.Empty;

    /// <summary>앞에서 n 개만. 0 이면 제한 없음. 측정 회차용 (T2-19 호환).</summary>
    public int Limit { get; init; }

    /// <summary>버킷을 n 간격으로 골라 표본을 흩는다. 측정 회차용 (T2-19 호환).</summary>
    public int Stride { get; init; } = 1;

    /// <summary>앞 n 개 아키타입의 72버킷을 연속으로. 인접 재사용을 태우려면 연속이어야 한다 (T2-19 호환).</summary>
    public int Archetypes { get; init; }

    /// <summary>프리픽스 절별 토큰 수만 찍고 끝낸다.</summary>
    public bool PrintPrefix { get; init; }

    /// <summary>LLM 을 부르지 않고 대상 산출까지만 한다. 대상 목록·무효화 판정 확인용.</summary>
    public bool Plan { get; init; }

    /// <summary>
    /// 워밍업 유무 비용 차이만 실측하고 끝낸다 (T3-11). 값은 회차당 동시 요청 수다.
    /// <b>외부 엔진에서만 의미가 있다</b> — 로컬은 <c>cached_tokens</c> 를 항상 0 으로 보고한다.
    /// </summary>
    public int WarmupExperiment { get; init; }

    /// <summary>도움말만 출력한다.</summary>
    public bool Help { get; init; }

    /// <summary>사용법. docs/13 §4.</summary>
    public static string Usage =>
        """
        사용법: Npc.Prebake [옵션]

          --masterdata <dir>     마스터데이터 폴더 (기본 ./masterdata)
          --out <dir>            플랜 스토어 폴더 (기본 ./planstore). 별칭 --planstore
          --report <file>        버킷별 결과 JSONL (기본 ./docs/measurements/W8_prebake.jsonl)
          --tier T1|T2           T1(로컬) | T2(외부). 기본 T2
          --model <id>           appsettings.Llm.json 의 엔진 id. 별칭 --engine
          --concurrency N        AIMD 초기 동시성 (기본 8). 상위 계획의 "동시 32" 는 추정치다
          --max-concurrency N    AIMD 상한 (기본 32)
          --dryrun-sample <0~1>  4단 드라이런 비율 (기본 1.0 = 전수)
          --resume               기존 planstore 에서 이어서. 미생성 버킷만
          --only <glob>          부분 재생성. 예: "blacksmith@*" · "*@Dawn.*.*". 여러 번 줄 수 있다
          --budget-usd <n>       예산 하드 캡 (기본 5.00). 초과 시 중단 + --resume 으로 재개
          --generated-at <text>  manifest 의 생성 시각. 비우면 결정론적 산출물이 된다
          --limit N              앞에서 N 개만 (측정 회차용)
          --stride N             버킷을 N 간격으로 골라 표본을 흩는다 (측정 회차용)
          --archetypes N         앞 N 개 아키타입의 72버킷을 연속으로 (측정 회차용)
          --print-prefix         프리픽스 절별 토큰 수만 찍고 끝낸다
          --plan                 LLM 을 부르지 않고 대상 산출까지만 (무효화 판정 확인)
          --warmup-experiment N  워밍업 유무 비용 차이만 실측하고 끝낸다 (T3-11). N = 동시 요청 수
          -h, --help             이 도움말
        """;

    /// <summary>
    /// glob 하나가 이 버킷 이름과 맞는가. <c>*</c>(0자 이상)과 <c>?</c>(1자)만 지원한다.
    /// 대소문자를 구분하지 않는다 — <c>blacksmith@dawn.*.*</c> 도 통한다.
    /// </summary>
    public static bool Matches(string pattern, string bucketName)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(bucketName);

        // 고전적인 2포인터 와일드카드 매칭. Regex 를 쓰지 않는 이유는 이스케이프 사고를 피하려는 것뿐이다.
        int p = 0;
        int b = 0;
        int star = -1;
        int mark = 0;

        while (b < bucketName.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], bucketName[b])))
            {
                p++;
                b++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = b;
            }
            else if (star >= 0)
            {
                p = star + 1;
                b = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;

        static bool Same(char a, char b) => char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
    }

    /// <summary>이 버킷 이름이 <see cref="Only"/> 중 하나에 맞는가. 목록이 비어 있으면 항상 참이다.</summary>
    public bool IncludesBucket(string bucketName)
    {
        if (Only.IsEmpty)
        {
            return true;
        }

        foreach (string pattern in Only)
        {
            if (Matches(pattern, bucketName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>인자를 파싱한다. 실패하면 <paramref name="error"/> 에 이유가 담긴다.</summary>
    public static bool TryParse(string[] args, out PrebakeOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        var result = new PrebakeOptions();
        var only = ImmutableArray.CreateBuilder<string>();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "-h" or "--help":
                    result = result with { Help = true };
                    break;

                case "--resume":
                    result = result with { Resume = true };
                    break;

                case "--print-prefix":
                    result = result with { PrintPrefix = true };
                    break;

                case "--plan":
                    result = result with { Plan = true };
                    break;

                case "--masterdata":
                    if (!TryValue(args, ref i, arg, out string? masterData, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { MasterData = masterData! };
                    break;

                case "--out" or "--planstore":
                    if (!TryValue(args, ref i, arg, out string? outDir, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Out = outDir! };
                    break;

                case "--report":
                    if (!TryValue(args, ref i, arg, out string? report, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Report = report! };
                    break;

                case "--tier":
                    if (!TryValue(args, ref i, arg, out string? tier, out error)
                        || !Enum.TryParse(tier, ignoreCase: true, out PrebakeTier parsedTier)
                        || !Enum.IsDefined(parsedTier))
                    {
                        error ??= $"--tier 값이 잘못됐다: '{tier}'. T1 또는 T2 다.";
                        options = result;
                        return false;
                    }

                    result = result with { Tier = parsedTier };
                    break;

                case "--model" or "--engine":
                    if (!TryValue(args, ref i, arg, out string? model, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Model = model };
                    break;

                case "--generated-at":
                    if (!TryValue(args, ref i, arg, out string? generatedAt, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { GeneratedAt = generatedAt! };
                    break;

                case "--only":
                    if (!TryValue(args, ref i, arg, out string? pattern, out error))
                    {
                        options = result;
                        return false;
                    }

                    only.Add(pattern!);
                    break;

                case "--concurrency":
                    if (!TryInt(args, ref i, arg, 1, 512, out int concurrency, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Concurrency = concurrency };
                    break;

                case "--max-concurrency":
                    if (!TryInt(args, ref i, arg, 1, 512, out int maxConcurrency, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { MaxConcurrency = maxConcurrency };
                    break;

                case "--warmup-experiment":
                    if (!TryInt(args, ref i, arg, 1, 64, out int experiment, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { WarmupExperiment = experiment };
                    break;

                case "--limit":
                    if (!TryInt(args, ref i, arg, 0, 100_000, out int limit, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Limit = limit };
                    break;

                case "--stride":
                    if (!TryInt(args, ref i, arg, 1, 100_000, out int stride, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Stride = stride };
                    break;

                case "--archetypes":
                    if (!TryInt(args, ref i, arg, 0, Npc.MasterData.BucketSpace.MaxArchetypes, out int archetypes, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Archetypes = archetypes };
                    break;

                case "--dryrun-sample":
                    if (!TryDouble(args, ref i, arg, 0, 1, out double sample, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { DryRunSample = sample };
                    break;

                case "--budget-usd":
                    if (!TryDouble(args, ref i, arg, 0, 10_000, out double budget, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { BudgetUsd = budget };
                    break;

                default:
                    error = $"모르는 인자다: {arg}";
                    options = result;
                    return false;
            }
        }

        result = result with { Only = only.ToImmutable() };

        if (result.MaxConcurrency < result.Concurrency)
        {
            error = $"--max-concurrency({result.MaxConcurrency}) 가 --concurrency({result.Concurrency}) 보다 작다.";
            options = result;
            return false;
        }

        options = result;
        return true;
    }

    private static bool TryValue(string[] args, ref int i, string name, out string? value, out string? error)
    {
        if (i + 1 >= args.Length)
        {
            value = null;
            error = $"{name} 에 값이 없다.";
            return false;
        }

        value = args[++i];
        error = null;
        return true;
    }

    private static bool TryInt(
        string[] args, ref int i, string name, int min, int max, out int value, out string? error)
    {
        value = 0;

        if (!TryValue(args, ref i, name, out string? text, out error))
        {
            return false;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"{name} 값이 정수가 아니다: '{text}'";
            return false;
        }

        if (value < min || value > max)
        {
            error = $"{name} 값이 {min}~{max} 범위를 벗어난다: {value}";
            return false;
        }

        return true;
    }

    private static bool TryDouble(
        string[] args, ref int i, string name, double min, double max, out double value, out string? error)
    {
        value = 0;

        if (!TryValue(args, ref i, name, out string? text, out error))
        {
            return false;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            error = $"{name} 값이 실수가 아니다: '{text}'";
            return false;
        }

        if (value < min || value > max)
        {
            error = $"{name} 값이 {min}~{max} 범위를 벗어난다: {value}";
            return false;
        }

        return true;
    }
}
