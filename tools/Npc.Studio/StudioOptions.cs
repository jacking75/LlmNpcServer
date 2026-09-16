namespace Npc.Studio;

/// <summary>Studio 실행 옵션.</summary>
public sealed record StudioOptions(string MasterData, string Bind, int Port, bool ReadOnly)
{
    /// <summary>기본값과 명령행을 합친다.</summary>
    public static StudioOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string masterData = "./masterdata";
        string bind = "127.0.0.1";
        int port = 25_056;
        bool readOnly = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--masterdata" when i + 1 < args.Length:
                    masterData = args[++i];
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

        return new StudioOptions(resolved, bind, port, readOnly);
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
