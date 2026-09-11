using System.Collections.Immutable;
using System.Globalization;
using Npc.Contracts;
using Npc.MasterData;

namespace Npc.TestGameServer;

/// <summary>
/// 게임서버 대역 실행 옵션. docs/20 §7.5.
///
/// <para>
/// <b>파싱 방식은 <c>Npc.Host/HostOptions.cs</c> 를 본떴다</b> (경로 해석 포함).
/// 그 코드를 참조하지 않고 다시 쓴 이유는 의존 방향이다 — 이 프로젝트는 단방향 잎이고
/// <c>Npc.Host</c> 를 참조하지 않는다 (docs/20 §4).
/// </para>
///
/// <para>
/// <b><c>--time-scale</c>·<c>--npcs</c>·<c>--zone</c> 은 NPC 서버와 반드시 같아야 한다.</b>
/// 다르면 핸드셰이크에서 거절된다 (docs/20 §5.5) — 그것이 의도된 동작이다.
/// 첨자가 어긋난 채로 도는 것보다 연결 시점에 죽는 편이 싸다.
/// </para>
/// </summary>
public sealed record GameServerOptions
{
    /// <summary>NPC 서버 링크 수신 포트. docs/20 §12.</summary>
    public const int DefaultLinkPort = 7010;

    /// <summary>클라이언트 수신 포트. docs/20 §12.</summary>
    public const int DefaultClientPort = 7020;

    /// <summary>NPC 서버 링크 수신 포트. <b>0 이면 OS 가 고른다</b> — 테스트가 쓴다 (docs/20 §13).</summary>
    public int LinkPort { get; init; } = DefaultLinkPort;

    /// <summary>클라이언트 수신 포트. 0 이면 OS 가 고른다.</summary>
    public int ClientPort { get; init; } = DefaultClientPort;

    /// <summary>NPC 수. 데모 기본은 300 이다 (docs/20 §14.2).</summary>
    public int Npcs { get; init; } = 300;

    /// <summary>
    /// 로스터 존 필터. 비어 있으면 전체다.
    /// <b>NPC 서버의 <c>--zone</c> 과 같아야 한다</b> — 다르면 로스터 해시가 어긋난다.
    /// </summary>
    public ImmutableArray<string> Zones { get; init; } = [];

    /// <summary>
    /// 맡을 샤드 번호 (A-08). 0 = 단일 샤드.
    ///
    /// <b>NPC 서버의 <c>--shard</c> 와 같아야 한다</b> — 다르면 <c>ShardMismatch</c> 로 거절된다.
    /// <see cref="Zones"/> 보다 우선한다.
    ///
    /// <para>
    /// <b>대역도 1 프로세스 = 1 샤드다.</b> 한 프로세스가 세션 N 개를 받는 것은 아직 없다 —
    /// <c>SimWorld.Events</c> 가 단일 독자 채널이라 팬아웃이 아니고, 세션마다 존으로 거른 큐를
    /// 주려면 시퀀스 스트림도 세션별로 갈라야 한다. 샤드 2개 데모는 프로세스 쌍 2개다.
    /// </para>
    /// </summary>
    public int Shard { get; init; }

    /// <summary>샤드 정의 파일 (A-08). 기본 <c>deploy/shards.json</c>.</summary>
    public string ShardsPath { get; init; } = ShardTable.DefaultPath;

    /// <summary>이 샤드가 맡는 존 비트마스크 (A-08). 조립 시 채워진다. 0 = 전체.</summary>
    public ulong ZoneMask { get; init; }

    /// <summary>시간 압축. 1=실시간, 60=1초당 게임 1분. <b>NPC 서버와 같아야 한다.</b></summary>
    public int TimeScale { get; init; } = 60;

    /// <summary>마스터데이터 디렉터리.</summary>
    public string MasterData { get; init; } = "./masterdata";

    /// <summary>
    /// 이벤트 주입 시나리오 jsonl.
    /// <b><c>KillSwitch</c> 줄은 무시한다</b> — 그것은 NPC 서버 안쪽 상태다 (docs/20 §11.4).
    /// </summary>
    public string? Scenario { get; init; }

    /// <summary>
    /// 기억 저장소 폴더 (D-03). null 이면 쓰지 않는다.
    ///
    /// <b>대역이 쓰고 NPC 서버가 읽는다.</b> 두 프로세스에 같은 폴더를 주면
    /// "거래 세 번 한 상인이 우호로 보인다" 를 손으로 확인할 수 있다.
    /// </summary>
    public string? MemoryDir { get; init; }

    /// <summary>액션 실패 주입 확률 0~1.</summary>
    public double FailRate { get; init; }

    /// <summary>명령 유실 주입 확률 0~1.</summary>
    public double DropRate { get; init; }

    /// <summary>가상 플레이어 수. 클라이언트 없이도 데모가 돌게 한다 (docs/20 §7.3).</summary>
    public int Bots { get; init; }

    /// <summary>Sim·봇의 시드.</summary>
    public int Seed { get; init; } = 20260725;

    /// <summary>
    /// 플레이어 걷기 속도. 단위는 <b>게임m/게임초</b>다 (docs/20 §7.3).
    ///
    /// 게임 시간 기준인 이유는 NPC 가 게임 시간으로 움직이기 때문이다 —
    /// <c>--time-scale 60</c> 이면 NPC 의 겉보기 속도가 실시간의 60배라
    /// 플레이어만 실시간이면 화면에서 멈춰 있는 것처럼 보인다.
    /// </summary>
    public double PlayerSpeed { get; init; } = 2.5;

    /// <summary>클라이언트 동시 접속 상한.</summary>
    public int MaxClients { get; init; } = 4;

    /// <summary>콘솔 통계만 찍는다. <b>클라이언트 접속은 계속 받는다.</b></summary>
    public bool Headless { get; init; }

    /// <summary>
    /// 대역이 말할 프로토콜 버전 (B-01). 기본 2.
    ///
    /// <b>1 로 낮추면 v1 게임서버를 흉내낸다</b> — v1↔v2 호환을 실제로 재현하는 유일한 방법이다.
    /// </summary>
    public int ProtocolVersion { get; init; } = 2;

    /// <summary>대역이 지원한다고 알릴 기능 비트 (B-01).</summary>
    /// <summary>
    /// 동적 로스터 (B-05). 켜면 <b>로스터 해시가 인스턴스 테이블 전체의 것</b>이 된다.
    ///
    /// <b>NPC 서버도 <c>--dynamic-roster</c> 로 켜야 한다.</b> 기능 협상은 핸드셰이크 중에
    /// 끝나므로 해시를 협상 결과로 고를 수 없다 — 한쪽만 켜면 <c>RosterMismatch</c> 로
    /// 거절되고, 그것이 의도된 동작이다.
    /// </summary>
    public bool DynamicRoster { get; init; }

    public ulong Features { get; init; } =
        (ulong)(LinkFeatures.GlobalIds | LinkFeatures.ExtSlots | LinkFeatures.DynamicRoster
                | LinkFeatures.Hostility | LinkFeatures.SessionEpoch);

    /// <summary>
    /// 세션 에포크 (G-02). 프로세스가 다시 뜨면 올린다 — 그것이 시퀀스 리셋을 알리는 신호다.
    /// </summary>
    public uint SessionEpoch { get; init; } = 1;

    /// <summary>
    /// 링크 HMAC 비밀 (A-06). 32바이트. 비어 있으면 인증하지 않는다.
    ///
    /// 대역도 값은 환경변수 <c>NPC_LINK_SECRET</c> 에서만 읽는다 — 실제 게임서버가 그래야 하고,
    /// 대역이 다른 길을 열어 두면 그 길로 시험하게 된다.
    /// </summary>
    public byte[] LinkSecret { get; init; } = [];

    /// <summary>도움말만 출력한다.</summary>
    public bool Help { get; init; }

    /// <summary>사용법. docs/20 §7.5 의 전 옵션.</summary>
    public static string Usage =>
        """
        사용법: Npc.TestGameServer [옵션]

          --link-port N          NPC 서버 링크 수신 포트 (기본 7010). 0=자동 할당
          --client-port N        클라이언트 수신 포트 (기본 7020). 0=자동 할당
          --npcs N               NPC 수 (기본 300)
          --zone <id>[,<id>]     이 존의 NPC 만 뽑는다 (기본 전체). NPC 서버와 같아야 한다
          --shard N              맡을 샤드 (A-08). 0=단일. NPC 서버와 같아야 한다
          --shards <path>        샤드 정의 파일 (기본 deploy/shards.json)
          --time-scale N         시간 압축 (기본 60). NPC 서버와 같아야 한다
          --masterdata <dir>     마스터데이터 디렉터리 (기본 ./masterdata)
          --scenario <jsonl>     이벤트 주입. KillSwitch 줄은 무시한다
          --memory <dir>         NPC 기억 저장소 폴더 (D-03). 대역이 쓴다
          --fail-rate <0~1>      액션 실패 주입
          --drop-rate <0~1>      명령 유실 주입
          --dynamic-roster       런타임 스폰·디스폰 (B-05). NPC 서버도 켜야 붙는다
          --bots N               가상 플레이어 (기본 0)
          --seed N               시드 (기본 20260725)
          --player-speed F       걷기 속도, 게임m/게임초 (기본 2.5)
          --max-clients N        클라이언트 동시 접속 상한 (기본 4)
          --headless             콘솔 통계만. 클라 접속은 계속 받는다
          -h, --help             이 도움말
        """;

    /// <summary>
    /// 마스터데이터 폴더를 실제 경로로 푼다.
    ///
    /// <c>dotnet run --project testbed/Npc.TestGameServer</c> 는 작업 폴더를 프로젝트 폴더로 잡는다.
    /// docs/20 §12 에 적힌 그대로 쳤을 때 <c>./masterdata</c> 가 없다고 죽으면 안 되므로,
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
    /// 파일 경로 옵션을 전부 절대경로로 푼다. 기동 직후 <b>1회</b>.
    ///
    /// 못 찾은 것은 사용자 입력 문제이므로 예외 대신 <paramref name="error"/> 로 돌려준다 —
    /// 경로 오타에 스택트레이스를 쏟지 않는다.
    /// </summary>
    /// <param name="resolved">경로가 전부 절대경로로 바뀐 옵션.</param>
    /// <param name="error">실패 사유. 성공이면 null.</param>
    public bool TryResolvePaths(out GameServerOptions resolved, out string? error)
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

        resolved = this with { MasterData = masterData, Scenario = scenario };
        return true;
    }

    /// <summary>인자를 파싱한다. 실패하면 <paramref name="error"/> 에 이유가 담긴다.</summary>
    public static bool TryParse(string[] args, out GameServerOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        var result = new GameServerOptions();
        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "-h" or "--help":
                    result = result with { Help = true };
                    break;

                case "--link-port":
                    // 하한이 0 이다. 테스트는 포트 0(자동 할당)으로 연다 (docs/20 §13) —
                    // 고정 포트를 쓰면 개발자가 데모를 띄워 둔 채 테스트를 돌릴 때 깨진다.
                    if (!TryInt(args, ref i, arg, 0, 65_535, out int linkPort, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { LinkPort = linkPort };
                    break;

                case "--client-port":
                    if (!TryInt(args, ref i, arg, 0, 65_535, out int clientPort, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { ClientPort = clientPort };
                    break;

                case "--npcs":
                    if (!TryInt(args, ref i, arg, 1, 1_000_000, out int npcs, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Npcs = npcs };
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

                case "--shard":
                    if (!TryInt(args, ref i, arg, 0, ushort.MaxValue, out int shard, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Shard = shard };
                    break;

                case "--shards":
                    if (!TryValue(args, ref i, arg, out string? shardsPath, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { ShardsPath = shardsPath! };
                    break;

                case "--time-scale":
                    if (!TryInt(args, ref i, arg, 1, 86_400, out int timeScale, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { TimeScale = timeScale };
                    break;

                case "--masterdata":
                    if (!TryValue(args, ref i, arg, out string? masterData, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { MasterData = masterData! };
                    break;

                case "--dynamic-roster":
                    result = result with { DynamicRoster = true };
                    break;

                case "--memory":
                    if (!TryValue(args, ref i, arg, out string? memoryDir, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { MemoryDir = memoryDir };
                    break;

                case "--scenario":
                    if (!TryValue(args, ref i, arg, out string? scenario, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Scenario = scenario };
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

                case "--bots":
                    if (!TryInt(args, ref i, arg, 0, 10_000, out int bots, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Bots = bots };
                    break;

                case "--seed":
                    if (!TryInt(args, ref i, arg, int.MinValue, int.MaxValue, out int seed, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Seed = seed };
                    break;

                case "--player-speed":
                    if (!TryDouble(args, ref i, arg, 0.1, 1_000, out double speed, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { PlayerSpeed = speed };
                    break;

                case "--max-clients":
                    if (!TryInt(args, ref i, arg, 0, 64, out int maxClients, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { MaxClients = maxClients };
                    break;

                case "--headless":
                    result = result with { Headless = true };
                    break;

                default:
                    error = $"모르는 인자다: {arg}";
                    options = result;
                    return false;
            }
        }

        options = result;
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

    private static bool TryRate(string[] args, ref int i, string name, out double value, out string? error)
        => TryDouble(args, ref i, name, 0, 1, out value, out error);
}
