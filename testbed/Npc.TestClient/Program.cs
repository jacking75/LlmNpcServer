using System.Globalization;

namespace Npc.TestClient;

/// <summary>
/// 테스트 클라이언트 실행 옵션. docs/20 §12.
///
/// <b><c>--masterdata</c> 가 있는 이유.</b> POI·존·아키타입의 <b>이름</b>은 클라이언트가
/// <c>Npc.MasterData</c> 를 참조해 직접 읽는다 (docs/20 §3.2) — 파서를 새로 쓰지 않고,
/// 게임서버가 문자열을 중계하지도 않는다.
/// </summary>
/// <param name="Host">게임서버 주소.</param>
/// <param name="Port">게임서버 클라이언트 포트.</param>
/// <param name="NpcHttp">NPC 서버의 디버그 HTTP. 인스펙터가 직접 폴링한다 (docs/20 §9.5).</param>
/// <param name="MasterData">마스터데이터 디렉터리.</param>
/// <param name="Help">도움말만 보이고 끝낸다.</param>
public sealed record ClientOptions(
    string Host,
    int Port,
    string NpcHttp,
    string MasterData,
    bool Help)
{
    /// <summary>기본값. docs/20 §12 의 포트 요약과 같다.</summary>
    public static ClientOptions Default { get; } =
        new("127.0.0.1", 7020, "http://localhost:5080", "./masterdata", Help: false);

    /// <summary>사용법.</summary>
    public static string Usage =>
        """
        사용법: Npc.TestClient [옵션]

          --host <host>       게임서버 주소 (기본 127.0.0.1)
          --port N            게임서버 클라이언트 포트 (기본 7020)
          --npc-http <url>    NPC 서버 디버그 HTTP (기본 http://localhost:5080)
          --masterdata <dir>  마스터데이터 디렉터리 (기본 ./masterdata)
          -h, --help          이 도움말
        """;

    /// <summary>인자를 파싱한다. 실패하면 <paramref name="error"/> 에 이유가 담긴다.</summary>
    public static bool TryParse(string[] args, out ClientOptions options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        ClientOptions result = Default;

        error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "-h" or "--help":
                    result = result with { Help = true };
                    break;

                case "--host":
                    if (!TryValue(args, ref i, arg, out string? host, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { Host = host! };
                    break;

                case "--port":
                    if (!TryValue(args, ref i, arg, out string? port, out error)
                        || !int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                        || number is < 0 or > 65_535)
                    {
                        error ??= $"{arg} 값이 포트가 아니다: '{port}'";
                        options = result;
                        return false;
                    }

                    result = result with { Port = number };
                    break;

                case "--npc-http":
                    if (!TryValue(args, ref i, arg, out string? http, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { NpcHttp = http!.TrimEnd('/') };
                    break;

                case "--masterdata":
                    if (!TryValue(args, ref i, arg, out string? dir, out error))
                    {
                        options = result;
                        return false;
                    }

                    result = result with { MasterData = dir! };
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

    /// <summary>
    /// 마스터데이터 폴더를 실제 경로로 푼다.
    ///
    /// <c>dotnet run --project testbed/Npc.TestClient</c> 는 작업 폴더를 프로젝트 폴더로 잡는다.
    /// docs/20 §12 에 적힌 그대로 쳤을 때 <c>./masterdata</c> 가 없다고 죽으면 안 되므로,
    /// 없으면 작업 폴더와 실행 파일 폴더에서 저장소 루트를 위로 찾아 올라간다 —
    /// <c>GameServerOptions.ResolveMasterData</c> 와 같은 규칙이다.
    /// </summary>
    public string ResolveMasterData()
    {
        if (File.Exists(Path.Combine(MasterData, "archetypes.json")))
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
            for (var directory = new DirectoryInfo(from); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, name);

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
}

/// <summary>
/// 테스트 클라이언트의 조립 루트. docs/20 §9.
///
/// <b>WinForms 디자이너를 쓰지 않는다.</b> 폼은 코드로만 짠다 — <c>.Designer.cs</c> 가 생기면
/// 리뷰에서 사람이 읽을 수 없는 diff 가 나온다.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!ClientOptions.TryParse(args, out ClientOptions options, out string? error))
        {
            // 콘솔이 없는 WinExe 다. 창으로 말한다 — 조용히 죽으면 원인을 모른다.
            MessageBox.Show(
                $"{error}{Environment.NewLine}{Environment.NewLine}{ClientOptions.Usage}",
                "Npc.TestClient",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            return 2;
        }

        if (options.Help)
        {
            MessageBox.Show(
                ClientOptions.Usage, "Npc.TestClient", MessageBoxButtons.OK, MessageBoxIcon.Information);

            return 0;
        }

        ApplicationConfiguration.Initialize();

        try
        {
            Application.Run(new MainForm(options));
        }
        catch (DirectoryNotFoundException e)
        {
            MessageBox.Show(e.Message, "Npc.TestClient", MessageBoxButtons.OK, MessageBoxIcon.Error);

            return 2;
        }

        return 0;
    }
}
