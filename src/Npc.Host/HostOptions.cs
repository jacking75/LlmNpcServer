using System.Collections.Immutable;
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

    /// <summary>
    /// 실제 게임서버에 TCP 로 붙는다 (P6 · docs/20 §10.3).
    ///
    /// <b><c>Npc.Sim</c> 을 만들지 않는다</b> — <see cref="Replay"/> 와 같은 경로다.
    /// 세계를 미는 것은 게임서버이고, 우리는 이벤트를 받아 명령을 낼 뿐이다.
    /// </summary>
    Tcp,
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

    /// <summary>게임서버 호스트. <c>--link tcp</c> 일 때만 쓴다 (docs/20 §10.3).</summary>
    public string GameServerHost { get; init; } = "127.0.0.1";

    /// <summary>게임서버 링크 포트. docs/20 §12 의 7010.</summary>
    public int GameServerPort { get; init; } = 7010;

    /// <summary>
    /// <c>--gs-host</c>·<c>--gs-port</c> 를 <b>명시했는가.</b>
    ///
    /// <c>--link record</c> 가 무엇을 감쌀지 이 값으로 가른다 (docs/20 §10.3) —
    /// 기본값과 명시값을 구별하지 못하면 "포트가 7010 이니까 TCP 겠지" 라는 추측이 된다.
    /// </summary>
    public bool UsesGameServer { get; init; }

    /// <summary>
    /// 로스터 존 필터. 비어 있으면 전체다.
    ///
    /// <b>게임서버와 같아야 한다</b> — 다르면 로스터 해시가 어긋나 핸드셰이크에서 거절된다
    /// (docs/20 §5.5). 그게 의도된 동작이다: 첨자가 어긋난 채로 도는 것보다 낫다.
    /// </summary>
    public ImmutableArray<string> Zones { get; init; } = [];

    /// <summary>대시보드·메트릭 포트.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>가상 플레이어 봇 수. 인지 LOD 가 실제로 갈리는지 보려면 0 보다 커야 한다.</summary>
    public int PlayerBots { get; init; } = 20;

    /// <summary>Sim·지터의 시드.</summary>
    public int Seed { get; init; } = 20260725;

    /// <summary>
    /// 재계획 점수 가중치 세트 이름. null 이면 <c>Weights.Default</c>.
    /// <c>docs/14 §2</c> 표의 <c>A-proximity</c>·<c>B-baseline</c>·<c>C-deviation</c>·<c>D-uniform</c>
    /// 또는 그 앞 글자(<c>a</c>~<c>d</c>)를 받는다.
    ///
    /// <b>T4-17 의 A/B 자동화가 쓴다</b> — 감으로 튜닝하면 재현이 안 된다 (docs/14 §10).
    /// </summary>
    public string? Weights { get; init; }

    /// <summary>
    /// 인지 스캔 틱당 상한. 기본은 <c>CognitionScheduler.MaxScansPerTick</c>(150),
    /// <b>0 이면 상한을 푼다</b>.
    ///
    /// <b>측정 전용이다</b> — 상한이 걸린 채로는 차수를 판정할 수 없다(무엇을 넣어도 150 에서 잘려
    /// O(1) 로 보인다). T4-16 의 스케일 곡선이 이 옵션을 쓴다 (docs/14 §6).
    /// 운영에서 풀면 그것이 틱 예산을 지키는 장치를 없애는 것이다.
    /// </summary>
    public int ScanCap { get; init; } = -1;

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
          --link null|record|replay|loopback|tcp
                                  링크 구현체 교체. tcp 는 실제 게임서버에 붙는다 (P6)
          --trace <path>          --link record 의 출력 · --link replay 의 입력 (jsonl)
          --gs-host <host>        게임서버 호스트 (기본 127.0.0.1). --link tcp 전용
          --gs-port N             게임서버 링크 포트 (기본 7010)
          --zone <id>[,<id>]      로스터 존 필터. 게임서버와 같아야 한다
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
          --weights A|B|C|D       재계획 점수 가중치 세트 (docs/14 §2 표. 기본 B)
          --scan-cap N            인지 스캔 틱당 상한. 0=상한 해제 (측정 전용, T4-16)
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

    /// <summary>
    /// 파일 경로 옵션을 전부 절대경로로 푼다. 기동 직후 <b>1회</b>.
    ///
    /// <see cref="ResolveMasterData"/> 와 같은 이유다 — <c>dotnet run --project src/Npc.Host</c> 는
    /// 작업 폴더를 프로젝트 폴더로 잡으므로, 문서에 적힌 <c>./scenarios/siege.jsonl</c> 을 그대로 치면
    /// <c>src/Npc.Host/scenarios/</c> 를 보고 죽는다. <c>--masterdata</c> 만 위로 올라가 찾고
    /// <c>--scenario</c>·<c>--trace</c> 는 안 찾는 비대칭이 이 버그의 원인이었다.
    ///
    /// <b>여기서 미리 풀어 두면 아래 코드는 절대경로만 다룬다.</b>
    /// 못 찾은 것은 사용자 입력 문제이므로 예외 대신 <paramref name="error"/> 로 돌려준다 —
    /// 경로 오타에 스택트레이스를 쏟지 않는다.
    /// </summary>
    /// <param name="resolved">경로가 전부 절대경로로 바뀐 옵션.</param>
    /// <param name="error">실패 사유. 성공이면 null.</param>
    public bool TryResolvePaths(out HostOptions resolved, out string? error)
    {
        resolved = this;
        error = null;

        string masterData;

        try
        {
            masterData = Path.GetFullPath(ResolveMasterData());
        }
        catch (DirectoryNotFoundException e)
        {
            error = e.Message;
            return false;
        }

        string? scenario = Scenario;

        if (scenario is not null)
        {
            if (!TryFindFile(scenario, out string found))
            {
                error = $"시나리오 파일을 찾지 못했다: {scenario}";
                return false;
            }

            scenario = found;
        }

        string? trace = TracePath;

        if (trace is not null)
        {
            if (Link == LinkKind.Replay)
            {
                if (!TryFindFile(trace, out string found))
                {
                    error = $"재생할 트레이스 파일을 찾지 못했다: {trace}";
                    return false;
                }

                trace = found;
            }
            else
            {
                // 기록은 아직 없는 파일을 만든다. 상대경로를 작업 폴더 기준으로 두면
                // 같은 명령이 실행 방식에 따라 다른 곳에 쓴다 — 저장소 루트로 고정한다.
                trace = ToRepoAbsolute(trace);
            }
        }

        resolved = this with
        {
            MasterData = masterData,
            PlanStore = Path.GetFullPath(ResolvePlanStore()),
            Scenario = scenario,
            TracePath = trace,
        };

        return true;
    }

    /// <summary>있는 파일을 찾는다. 작업 폴더 → 저장소 루트 순.</summary>
    private static bool TryFindFile(string path, out string found)
    {
        if (File.Exists(path))
        {
            found = Path.GetFullPath(path);
            return true;
        }

        if (!Path.IsPathRooted(path) && FindRepoRoot() is { } root)
        {
            string candidate = Path.Combine(root, path);

            if (File.Exists(candidate))
            {
                found = Path.GetFullPath(candidate);
                return true;
            }
        }

        found = path;
        return false;
    }

    /// <summary>상대경로를 저장소 루트 기준 절대경로로. 루트를 못 찾으면 작업 폴더 기준이다.</summary>
    private static string ToRepoAbsolute(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        return FindRepoRoot() is { } root
            ? Path.GetFullPath(Path.Combine(root, path))
            : Path.GetFullPath(path);
    }

    /// <summary>
    /// 저장소 루트. 표식은 <c>masterdata/archetypes.json</c> 이다 —
    /// <see cref="ResolveMasterData"/> 가 이미 같은 파일을 표식으로 쓴다.
    /// </summary>
    private static string? FindRepoRoot()
    {
        foreach (string from in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "masterdata", "archetypes.json")))
                {
                    return dir.FullName;
                }
            }
        }

        return null;
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
                        error ??= $"--link 값이 잘못됐다: '{link}'. null|record|replay|loopback|tcp 중 하나다.";
                        options = result;
                        return false;
                    }

                    result = result with { Link = kind };
                    break;

                case "--gs-host":
                    if (!TryValue(args, ref i, arg, out string? gsHost, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { GameServerHost = gsHost!, UsesGameServer = true };
                    break;

                case "--gs-port":
                    if (!TryInt(args, ref i, arg, 1, 65_535, out int gsPort, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { GameServerPort = gsPort, UsesGameServer = true };
                    break;

                case "--zone":
                    if (!TryValue(args, ref i, arg, out string? zones, out error))
                    {
                        options = result;
                        return false;
                    }

                    // 쉼표로 여러 존. 공백은 버린다 — "--zone a, b" 를 오타로 죽이지 않는다.
                    result = result with
                    {
                        Zones = [.. zones!.Split(',',
                            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                    };
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

                case "--weights":
                    if (!TryValue(args, ref i, arg, out string? weights, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Weights = weights };
                    break;

                case "--scan-cap":
                    if (!TryInt(args, ref i, arg, 0, 1_000_000, out int scanCap, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { ScanCap = scanCap };
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
