// W1 스파이크 진입점.
// 이 폴더의 코드는 W2 에 전부 버린다. 추상화·인터페이스·테스트를 신경 쓰지 않는다.
// 남는 것은 docs/measurements/ 아래의 측정값뿐이다.
namespace Spike;

internal static class Program
{
    /// <summary>스파이크 소스 폴더 절대 경로. data/ · out/ 을 여기 기준으로 읽고 쓴다.</summary>
    public static string SpikeRoot { get; } = FindSpikeRoot();

    private static int Main(string[] args)
    {
        var cmd = args.Length > 0 ? args[0] : "ready";

        switch (cmd)
        {
            case "ready":
                Console.WriteLine("spike ready");
                Console.WriteLine($"root = {SpikeRoot}");
                Console.WriteLine($"net  = {Environment.Version}");
                return 0;

            default:
                Console.Error.WriteLine($"unknown command: {cmd}");
                Console.Error.WriteLine("usage: spike [ready]");
                return 2;
        }
    }

    /// <summary>실행 위치(bin/…)에서 위로 올라가며 Spike.csproj 가 있는 폴더를 찾는다.</summary>
    private static string FindSpikeRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Spike.csproj")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
