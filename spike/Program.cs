// W1 스파이크 진입점.
// 이 폴더의 코드는 W2 에 전부 버린다. 추상화·인터페이스·테스트를 신경 쓰지 않는다.
// 남는 것은 docs/measurements/ 아래의 측정값뿐이다.
namespace Spike;

internal static class Program
{
    /// <summary>스파이크 소스 폴더 절대 경로. data/ · out/ 을 여기 기준으로 읽고 쓴다.</summary>
    public static string SpikeRoot { get; } = FindSpikeRoot();

    private static async Task<int> Main(string[] args)
    {
        var cmd = args.Length > 0 ? args[0] : "ready";
        var rest = args.Length > 1 ? args[1..] : [];

        switch (cmd)
        {
            case "ready":
                Console.WriteLine("spike ready");
                Console.WriteLine($"root = {SpikeRoot}");
                Console.WriteLine($"net  = {Environment.Version}");
                return 0;

            case "clients":
                return await Clients.RunSmokeAsync(rest);

            case "schema":
                return SchemaGen.Run();

            case "prefix":
                return PromptPrefix.Run();

            case "suffix":
                return SuffixGen.Run();

            case "validate":
                return Validate.Run();

            case "schemacheck":
                return await RunSchemaCheck.RunAsync(rest);

            case "bench":
                return await Bench.RunAsync(rest);

            case "concurrency":
                return await BenchConcurrency.RunAsync(rest);

            case "quality":
                return await Quality.RunAsync(rest);

            default:
                Console.Error.WriteLine($"unknown command: {cmd}");
                Console.Error.WriteLine(
                    "usage: spike [ready|clients|schema|prefix|suffix|validate|schemacheck|bench|concurrency|quality]");
                return 2;
        }
    }

    /// <summary>
    /// Spike.csproj 가 있는 폴더를 찾는다. 실행 위치에서 위로, 그 다음 현재 디렉터리에서 위로,
    /// 마지막으로 &lt;cwd&gt;/spike 를 본다 — 빌드 출력을 저장소 밖에 두고 돌릴 때가 있다.
    /// </summary>
    private static string FindSpikeRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Spike.csproj")))
                {
                    return dir.FullName;
                }

                if (File.Exists(Path.Combine(dir.FullName, "spike", "Spike.csproj")))
                {
                    return Path.Combine(dir.FullName, "spike");
                }

                dir = dir.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }
}
