namespace Npc.Studio;

/// <summary>기동 인자가 잘못됐다 (H26). 도움말을 찍고 멈춘다.</summary>
public sealed class StudioArgumentException : Exception
{
    /// <summary>기본 생성자.</summary>
    public StudioArgumentException()
        : base("기동 인자가 잘못됐다.")
    {
    }

    /// <summary>메시지.</summary>
    /// <param name="message">사람이 읽는 한 줄.</param>
    public StudioArgumentException(string message)
        : base(message)
    {
    }

    /// <summary>메시지와 내부 예외.</summary>
    /// <param name="message">사람이 읽는 한 줄.</param>
    /// <param name="innerException">내부 예외.</param>
    public StudioArgumentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Studio 실행 옵션.</summary>
/// <param name="MasterData">마스터데이터 경로.</param>
/// <param name="Bind">바인드 주소.</param>
/// <param name="Port">포트.</param>
/// <param name="ReadOnly">읽기 전용인가.</param>
public sealed record StudioOptions(string MasterData, string Bind, int Port, bool ReadOnly)
{
    /// <summary>미리 구운 플랜 경로 (T27). 없으면 상황별 탭을 숨긴다.</summary>
    public string PlanStore { get; init; } = string.Empty;

    /// <summary>실행 중인 NPC 서버 주소 (T28). 비면 라이브 관찰을 쓰지 않는다.</summary>
    public string Server { get; init; } = string.Empty;

    /// <summary>대시보드 토큰 (A-06). 비면 헤더를 붙이지 않는다.</summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>
    /// 되돌리기 백업을 두는 곳 (T18). 비면 <c>%LOCALAPPDATA%/NpcStudio/backup</c> 이다.
    /// <b><c>masterdata/</c> 안을 가리키지 않는다</b> — 백업이 입력으로 섞인다.
    /// </summary>
    public string BackupRoot { get; init; } = string.Empty;

    /// <summary>
    /// 요청한 경로가 없어 저장소의 <c>masterdata/</c> 로 떨어졌는가 (H26).
    /// <b>조용히 다른 폴더를 여는 것은 위험하다</b> — 오타 한 번에 진짜 데이터를 고치게 된다.
    /// </summary>
    public string FellBackFrom { get; init; } = string.Empty;

    /// <summary>루프백이 아닌 주소에 열려 있는가 (H26). 편집 모드면 경고한다.</summary>
    public bool IsPublic => Bind is not ("127.0.0.1" or "localhost" or "::1");

    /// <summary>도움말. 인자가 잘못되면 이것을 찍는다.</summary>
    public static string Usage =>
        """
        NPC Studio — NPC 정의를 읽고 · 예측하고 · 만든다.

          --masterdata <경로>    마스터데이터 폴더 (기본 ./masterdata)
          --planstore  <경로>    미리 구운 계획 폴더 (기본 ./planstore). 없으면 상황별 탭을 숨긴다
          --server     <주소>    실행 중인 NPC 서버 (http://127.0.0.1:25055). 라이브 관찰에 쓴다
          --token      <값>      대시보드 토큰
          --backup-root <경로>   되돌리기 백업 폴더 (기본 %LOCALAPPDATA%\NpcStudio\backup)
          --bind       <주소>    바인드 주소 (기본 127.0.0.1)
          --port       <번호>    포트 1~65535 (기본 25056)
          --read-only            저장을 막는다
          --help                 이 도움말
        """;

    /// <summary>
    /// 기본값과 명령행을 합친다.
    ///
    /// <b>모르는 인자는 거절한다</b> (H26). 예전에는 조용히 무시해서
    /// <c>--readonly</c>(오타)로 띄우면 <b>편집 모드로</b> 떴다.
    /// </summary>
    /// <param name="args">명령행 인자.</param>
    /// <exception cref="StudioArgumentException">모르는 인자·값 누락·범위 밖.</exception>
    public static StudioOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string masterData = "./masterdata";
        string planStore = "./planstore";
        string bind = "127.0.0.1";
        string server = string.Empty;
        string token = string.Empty;
        string backupRoot = string.Empty;
        int port = 25_056;
        bool readOnly = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--masterdata":
                    masterData = Value(args, ref i);
                    break;
                case "--planstore":
                    planStore = Value(args, ref i);
                    break;
                case "--server":
                    server = Value(args, ref i);
                    break;
                case "--token":
                    token = Value(args, ref i);
                    break;
                case "--backup-root":
                    backupRoot = Path.GetFullPath(Value(args, ref i));
                    break;
                case "--bind":
                    bind = Value(args, ref i);
                    break;
                case "--port":
                    string raw = Value(args, ref i);

                    if (!int.TryParse(raw, out port) || port is < 1 or > 65_535)
                    {
                        throw new StudioArgumentException($"--port 값이 1~65535 가 아니다: {raw}");
                    }

                    break;
                case "--read-only":
                    readOnly = true;
                    break;
                case "--help" or "-h" or "-?":
                    throw new StudioArgumentException(string.Empty);
                default:
                    throw new StudioArgumentException($"모르는 인자다: {args[i]}");
            }
        }

        (string resolved, string fellBackFrom) = ResolveMasterData(masterData);

        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"마스터데이터 디렉터리가 없다: {resolved}");
        }

        return new StudioOptions(resolved, bind, port, readOnly)
        {
            PlanStore = Path.GetFullPath(planStore, Path.GetDirectoryName(resolved) ?? Environment.CurrentDirectory),
            Server = server,
            Token = token,
            BackupRoot = backupRoot,
            FellBackFrom = fellBackFrom,
        };
    }

    private static string Value(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new StudioArgumentException($"{args[i]} 에 값이 없다.");
        }

        return args[++i];
    }

    /// <summary>
    /// 경로를 푼다. 작업 디렉터리에 없으면 저장소의 폴더로 떨어지는데,
    /// <b>그 사실을 돌려준다</b> — 화면이 배지로 알린다 (H26).
    /// </summary>
    private static (string Resolved, string FellBackFrom) ResolveMasterData(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return (Path.GetFullPath(path), string.Empty);
        }

        string fromWorkingDirectory = Path.GetFullPath(path);

        if (Directory.Exists(fromWorkingDirectory))
        {
            return (fromWorkingDirectory, string.Empty);
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NpcServer.sln")))
            {
                return (Path.GetFullPath(Path.Combine(directory.FullName, path)), fromWorkingDirectory);
            }

            directory = directory.Parent;
        }

        return (fromWorkingDirectory, string.Empty);
    }
}
