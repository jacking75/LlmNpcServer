namespace Npc.Studio;

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

    /// <summary>기본값과 명령행을 합친다.</summary>
    public static StudioOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string masterData = "./masterdata";
        string planStore = "./planstore";
        string bind = "127.0.0.1";
        string server = string.Empty;
        string token = string.Empty;
        int port = 25_056;
        bool readOnly = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--masterdata" when i + 1 < args.Length:
                    masterData = args[++i];
                    break;
                case "--planstore" when i + 1 < args.Length:
                    planStore = args[++i];
                    break;
                case "--server" when i + 1 < args.Length:
                    server = args[++i];
                    break;
                case "--token" when i + 1 < args.Length:
                    token = args[++i];
                    break;
                case "--bind" when i + 1 < args.Length:
                    bind = args[++i];
                    break;
                case "--port" when i + 1 < args.Length
                    && int.TryParse(args[++i], out int parsed) && parsed is > 0 and <= 65_535:
                    port = parsed;
                    break;
                case "--read-only":
                    readOnly = true;
                    break;
            }
        }

        string resolved = ResolveMasterData(masterData);

        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"마스터데이터 디렉터리가 없다: {resolved}");
        }

        return new StudioOptions(resolved, bind, port, readOnly)
        {
            PlanStore = Path.GetFullPath(planStore, Path.GetDirectoryName(resolved) ?? Environment.CurrentDirectory),
            Server = server,
            Token = token,
        };
    }

    private static string ResolveMasterData(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        string fromWorkingDirectory = Path.GetFullPath(path);

        if (Directory.Exists(fromWorkingDirectory))
        {
            return fromWorkingDirectory;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NpcServer.sln")))
            {
                return Path.GetFullPath(Path.Combine(directory.FullName, path));
            }

            directory = directory.Parent;
        }

        return fromWorkingDirectory;
    }
}
