using System.Globalization;

namespace Npc.Host;

/// <summary>게임서버 링크 구현체. README §주요 실행 옵션.</summary>
public enum LinkKind
{
    /// <summary>Npc.Sim 인프로세스 월드에 직결 (기본).</summary>
    Loopback,

    /// <summary>아무것도 보내지 않는다. 링크 없이 기동되는지 확인할 때.</summary>
    Null,

    /// <summary>Loopback 을 감싸 명령·이벤트를 jsonl 로 기록한다.</summary>
    Record,

    /// <summary>기록된 jsonl 을 재생한다. 게임서버 없이 같은 이벤트 열을 다시 먹인다.</summary>
    Replay,
}

/// <summary>
/// 어느 티어까지 켤 것인가. docs/14 §6 부하 매트릭스의 "티어 구성" 축.
///
/// <b>P1 에는 <c>--no-llm</c> 하나뿐이라 "T0만"과 "T0+T1+T2"를 가를 수 없었다</b> (T4-15).
/// </summary>
public enum TierMode
{
    /// <summary>T0 만 — 프리베이크 캐시 + 폴백. LLM 호출이 0 이다. <c>--no-llm</c> 의 정식 이름.</summary>
    None = 0,

    /// <summary>T0 + T1 — 로컬 엔진으로 개별 재계획만 한다. 무비용.</summary>
    T1 = 1,

    /// <summary>T0 + T2 — 외부 엔진으로 아키타입 버킷 미스만 채운다.</summary>
    T2 = 2,

    /// <summary>T0 + T1 + T2 — 전부.</summary>
    All = 3,
}

/// <summary>
/// 호스트 실행 옵션. README §주요 실행 옵션 · docs/11 §11.
///
/// <b>인자 파싱은 여기서만 한다.</b> ASP.NET 의 명령줄 설정 공급자는
/// <c>--loopback</c> 같은 값 없는 플래그를 거부하므로 <c>CreateBuilder(args)</c> 에 넘기지 않는다.
/// </summary>
public sealed record HostOptions
{
    /// <summary>대시보드·메트릭 기본 포트.</summary>
    public const int DefaultPort = 5080;

    /// <summary>링크 구현체.</summary>
    public LinkKind Link { get; init; } = LinkKind.Loopback;

    /// <summary>NPC 수.</summary>
    public int Npcs { get; init; } = 500;

    /// <summary>시간 압축. 1=실시간, 60=1초당 게임 1분.</summary>
    public int TimeScale { get; init; } = 60;

    /// <summary>돌릴 게임 일수. 0 이면 무제한.</summary>
    public int Days { get; init; } = 1;

    /// <summary>
    /// 어느 티어까지 켤 것인가. docs/14 §6 의 티어 축.
    /// <c>--no-llm</c> 은 <see cref="TierMode.None"/> 의 별칭이다 (P1 게이트 스크립트가 쓴다).
    /// </summary>
    public TierMode Tier { get; init; } = TierMode.None;

    /// <summary>T1·T2 비활성. 캐시 + 폴백만. <c>--no-llm</c> 의 상태값.</summary>
    public bool NoLlm => Tier == TierMode.None;

    /// <summary>T1 이 켜졌는가.</summary>
    public bool UsesT1 => Tier is TierMode.T1 or TierMode.All;

    /// <summary>T2 가 켜졌는가.</summary>
    public bool UsesT2 => Tier is TierMode.T2 or TierMode.All;

    /// <summary>
    /// T1(로컬) 워커 수. 기본 2 — W1 실측에서 동시 2 에서 처리량이 최대였다
    /// (<c>W1_concurrency.md</c>). T4-15 에서 1/2/4 를 재서 확정한다.
    /// </summary>
    public int T1Workers { get; init; } = 2;

    /// <summary>T2(외부) 워커 수. 기본 8 — 파일럿에서 동시 24 까지 429 가 없었다.</summary>
    public int T2Workers { get; init; } = 8;

    /// <summary>T1 엔진 id. null 이면 <c>appsettings.Llm.json</c> 의 로컬 엔진 중 첫 번째.</summary>
    public string? T1Engine { get; init; }

    /// <summary>T2 엔진 id. null 이면 <c>appsettings.Llm.json</c> 의 <c>default</c>.</summary>
    public string? T2Engine { get; init; }

    /// <summary>시나리오 jsonl 경로.</summary>
    public string? Scenario { get; init; }

    /// <summary>Sim 의 액션 실패 확률 0~1.</summary>
    public double FailRate { get; init; }

    /// <summary>Sim 의 명령 유실 확률 0~1.</summary>
    public double DropRate { get; init; }

    /// <summary>마스터데이터 디렉터리.</summary>
    public string MasterData { get; init; } = "./masterdata";

    /// <summary>
    /// 플랜 스토어 디렉터리. 기동 시 <c>plans/</c>·<c>pinned/</c> 를 로드한다 (docs/13 §2).
    /// 없으면 폴백 40개로만 돈다 — P1 과 같은 동작이다.
    /// </summary>
    public string PlanStore { get; init; } = "./planstore";

    /// <summary><c>--link record</c> 의 출력 경로 · <c>--link replay</c> 의 입력 경로.</summary>
    public string? TracePath { get; init; }

    /// <summary>대시보드·메트릭 포트.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>가상 플레이어 봇 수. 인지 LOD 가 실제로 갈리는지 보려면 0 보다 커야 한다.</summary>
    public int PlayerBots { get; init; } = 20;

    /// <summary>Sim·지터의 시드.</summary>
    public int Seed { get; init; } = 20260725;

    /// <summary>
    /// 10Hz 실시간 페이싱을 끄고 최대 속도로 돈다. 부하·게이트 측정용.
    /// 켜면 벽시계를 보지만 <b>게임 로직은 여전히 Tick 만 본다</b> — 리플레이는 깨지지 않는다.
    /// </summary>
    public bool MaxSpeed { get; init; }

    /// <summary>웹 호스트를 띄우지 않는다. 헤드리스 스모크용.</summary>
    public bool NoDashboard { get; init; }

    /// <summary>도움말만 출력한다.</summary>
    public bool Help { get; init; }

    /// <summary>사용법.</summary>
    public static string Usage =>
        """
        사용법: Npc.Host [validate ...] [옵션]

          --loopback              Npc.Sim 인프로세스 월드에 직결 (기본)
          --link null|record|replay|loopback
                                  링크 구현체 교체
          --trace <path>          --link record 의 출력 · --link replay 의 입력 (jsonl)
          --npcs N                NPC 수 (기본 500)
          --time-scale N          시간 압축. 1=실시간, 60=1초당 게임 1분 (기본 60)
          --days N                돌릴 게임 일수. 0=무제한 (기본 1)
          --tier none|t1|t2|all   어느 티어까지 켤까 (기본 none)
          --no-llm                --tier none 의 별칭
          --t1-workers N          T1(로컬) 워커 수 (기본 2)
          --t2-workers N          T2(외부) 워커 수 (기본 8)
          --t1-engine <id>        T1 엔진 id (기본: appsettings.Llm.json 의 첫 로컬 엔진)
          --t2-engine <id>        T2 엔진 id (기본: appsettings.Llm.json 의 default)
          --scenario <jsonl>      시나리오 이벤트 주입
          --fail-rate <0~1>       Sim 의 액션 실패 주입
          --drop-rate <0~1>       Sim 의 명령 유실 주입
          --player-bots N         가상 플레이어 수 (기본 20)
          --masterdata <dir>      마스터데이터 디렉터리 (기본 ./masterdata)
          --planstore <dir>       프리베이크된 플랜 스토어 (기본 ./planstore). 없으면 폴백만
          --seed N                Sim 시드 (기본 20260725)
          --port N                대시보드·메트릭 포트 (기본 5080)
          --max-speed             10Hz 페이싱 없이 최대 속도로 (측정용)
          --no-dashboard          웹 호스트를 띄우지 않는다
          -h, --help              이 도움말
        """;

    /// <summary>
    /// 마스터데이터 폴더를 실제 경로로 푼다.
    ///
    /// <c>dotnet run --project src/Npc.Host</c> 는 작업 폴더를 프로젝트 폴더로 잡는다.
    /// README 에 적힌 그대로 쳤을 때 <c>./masterdata</c> 가 없다고 죽으면 안 되므로,
    /// 없으면 작업 폴더와 실행 파일 폴더에서 저장소 루트를 위로 찾아 올라간다.
    /// </summary>
    public string ResolveMasterData()
    {
        if (Directory.Exists(MasterData))
        {
            return MasterData;
        }

        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(MasterData));

        if (name.Length == 0)
        {
            name = "masterdata";
        }

        foreach (string from in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, name);

                // 이름만 보면 안 된다. 윈도우는 대소문자를 구분하지 않아서
                // tests/Npc.Tests/MasterData 같은 소스 폴더가 먼저 걸린다.
                if (File.Exists(Path.Combine(candidate, "archetypes.json")))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException($"마스터데이터 폴더를 찾지 못했다: {MasterData}");
    }

    /// <summary>
    /// 플랜 스토어 폴더를 실제 경로로 푼다.
    ///
    /// <see cref="ResolveMasterData"/> 와 같은 이유로 위로 올라가며 찾는다 —
    /// <c>dotnet run --project src/Npc.Host</c> 는 작업 폴더를 프로젝트 폴더로 잡는다.
    /// <b>못 찾으면 던지지 않는다.</b> 첫 기동에는 스토어가 없고, 그때는 폴백으로 돈다.
    /// </summary>
    public string ResolvePlanStore()
    {
        if (Directory.Exists(PlanStore))
        {
            return PlanStore;
        }

        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(PlanStore));

        if (name.Length == 0)
        {
            name = "planstore";
        }

        foreach (string from in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, name);

                // 폴더 이름만 보면 안 된다. 스토어의 표식은 manifest 나 3계층 폴더다.
                if (File.Exists(Path.Combine(candidate, "manifest.json"))
                    || Directory.Exists(Path.Combine(candidate, "plans"))
                    || Directory.Exists(Path.Combine(candidate, "pinned")))
                {
                    return candidate;
                }
            }
        }

        return PlanStore;
    }

    /// <summary>인자를 파싱한다. 실패하면 <paramref name="error"/> 에 이유가 담긴다.</summary>
    public static bool TryParse(string[] args, out HostOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        var result = new HostOptions();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "-h" or "--help":
                    result = result with { Help = true };
                    break;

                case "--loopback":
                    result = result with { Link = LinkKind.Loopback };
                    break;

                case "--no-llm":
                    result = result with { Tier = TierMode.None };
                    break;

                case "--tier":
                    if (!TryValue(args, ref i, arg, out string? tier, out error)
                        || !Enum.TryParse(tier, ignoreCase: true, out TierMode tierMode))
                    {
                        error ??= $"--tier 값이 잘못됐다: '{tier}'. none|t1|t2|all 중 하나다.";
                        options = result;
                        return false;
                    }

                    result = result with { Tier = tierMode };
                    break;

                case "--t1-engine":
                    if (!TryValue(args, ref i, arg, out string? t1Engine, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { T1Engine = t1Engine };
                    break;

                case "--t2-engine":
                    if (!TryValue(args, ref i, arg, out string? t2Engine, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { T2Engine = t2Engine };
                    break;

                case "--t1-workers":
                    if (!TryInt(args, ref i, arg, 1, 64, out int t1Workers, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { T1Workers = t1Workers };
                    break;

                case "--t2-workers":
                    if (!TryInt(args, ref i, arg, 1, 64, out int t2Workers, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { T2Workers = t2Workers };
                    break;

                case "--max-speed":
                    result = result with { MaxSpeed = true };
                    break;

                case "--no-dashboard":
                    result = result with { NoDashboard = true };
                    break;

                case "--link":
                    if (!TryValue(args, ref i, arg, out string? link, out error)
                        || !Enum.TryParse(link, ignoreCase: true, out LinkKind kind))
                    {
                        error ??= $"--link 값이 잘못됐다: '{link}'. null|record|replay|loopback 중 하나다.";
                        options = result;
                        return false;
                    }

                    result = result with { Link = kind };
                    break;

                case "--trace":
                    if (!TryValue(args, ref i, arg, out string? trace, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { TracePath = trace };
                    break;

                case "--scenario":
                    if (!TryValue(args, ref i, arg, out string? scenario, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Scenario = scenario };
                    break;

                case "--masterdata":
                    if (!TryValue(args, ref i, arg, out string? masterData, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { MasterData = masterData! };
                    break;

                case "--planstore":
                    if (!TryValue(args, ref i, arg, out string? planStore, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { PlanStore = planStore! };
                    break;

                case "--npcs":
                    if (!TryInt(args, ref i, arg, 1, 1_000_000, out int npcs, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Npcs = npcs };
                    break;

                case "--time-scale":
                    if (!TryInt(args, ref i, arg, 1, 86_400, out int timeScale, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { TimeScale = timeScale };
                    break;

                case "--days":
                    if (!TryInt(args, ref i, arg, 0, 3_650, out int days, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Days = days };
                    break;

                case "--player-bots":
                    if (!TryInt(args, ref i, arg, 0, 10_000, out int bots, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { PlayerBots = bots };
                    break;

                case "--port":
                    if (!TryInt(args, ref i, arg, 1, 65_535, out int port, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Port = port };
                    break;

                case "--seed":
                    if (!TryInt(args, ref i, arg, int.MinValue, int.MaxValue, out int seed, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Seed = seed };
                    break;

                case "--fail-rate":
                    if (!TryRate(args, ref i, arg, out double failRate, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { FailRate = failRate };
                    break;

                case "--drop-rate":
                    if (!TryRate(args, ref i, arg, out double dropRate, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { DropRate = dropRate };
                    break;

                default:
                    error = $"모르는 인자다: {arg}";
                    options = result;
                    return false;
            }
        }

        if (result.Link == LinkKind.Replay && result.TracePath is null)
        {
            error = "--link replay 에는 --trace <path> 가 필요하다.";
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

    private static bool TryInt(string[] args, ref int i, string name, int min, int max, out int value, out string? error)
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

    private static bool TryRate(string[] args, ref int i, string name, out double value, out string? error)
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

        if (value is < 0 or > 1)
        {
            error = $"{name} 값이 0~1 범위를 벗어난다: {value}";
            return false;
        }

        return true;
    }
}
