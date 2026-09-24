using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using Npc.Llm;
using Npc.MasterData;
using Npc.Planning;

namespace Npc.Host.Commands;

/// <summary>환경·데이터·스토어·엔진을 기동 없이 진단한다.</summary>
public static class DoctorCommand
{
    /// <summary>서브커맨드 이름.</summary>
    public const string Name = "doctor";

    /// <summary>사용법.</summary>
    public const string Usage = "Npc.Host doctor [--masterdata <dir>] [--planstore <dir>] [--online] [--engine <id>] [--json]";

    /// <summary>진단 한 줄. 비밀값은 어느 필드에도 넣지 않는다.</summary>
    public sealed record Check(string Id, string Status, string Detail, string? FixHint = null);

    /// <summary>0 = 실패 없음, 1 = 실패 있음, 2 = 인자 오류.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string masterdata = "./masterdata";
        string planstore = "./planstore";
        bool online = false;
        bool json = false;
        string? engineId = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--masterdata" when i + 1 < args.Length:
                    masterdata = args[++i];
                    break;
                case "--planstore" when i + 1 < args.Length:
                    planstore = args[++i];
                    break;
                case "--online":
                    online = true;
                    break;
                case "--engine" when i + 1 < args.Length:
                    engineId = args[++i];
                    online = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;
                default:
                    output.WriteLine($"모르는 인자: {args[i]}\n{Usage}");
                    return 2;
            }
        }

        var checks = new List<Check>(8);
        checks.Add(Environment.Version.Major >= 10
            ? new Check("runtime", "ok", $".NET {Environment.Version}")
            : new Check("runtime", "fail", $".NET {Environment.Version}",
                "https://dotnet.microsoft.com/download/dotnet/10.0 에서 .NET 10을 설치한다."));

        string directory;
        try
        {
            directory = (new HostOptions { MasterData = masterdata }).ResolveMasterData();
        }
        catch (DirectoryNotFoundException error)
        {
            checks.Add(new Check("masterdata", "fail", error.Message,
                "--masterdata <폴더>를 확인한다."));
            directory = masterdata;
        }

        MasterDataSet? data = null;
        if (Directory.Exists(directory))
        {
            string? flagsError = null;
            try
            {
                flagsError = CoreVocabulary.WorldFlagsMismatch(Path.Combine(directory, "world_flags.json"));
            }
            catch (Exception error) when (error is IOException or JsonException or ArgumentException)
            {
                flagsError = error.Message;
            }
            checks.Add(flagsError is null
                ? new Check("world_flags", "ok", "빌드된 enum과 이름·bit가 같다")
                : new Check("world_flags", "fail", flagsError,
                    "저장소 masterdata/world_flags.json을 갱신하고 다시 빌드한다."));

            using var validation = new StringWriter();
            int code = ValidateCommand.Run(["--masterdata", directory], validation);
            string first = validation.ToString().Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("FAIL", StringComparison.Ordinal)) ?? "검증 통과";
            checks.Add(code == 0
                ? new Check("masterdata", "ok", "V1~V13과 로더 통과")
                : new Check("masterdata", "fail", first,
                    validation.ToString().Split('\n').Select(line => line.Trim())
                        .FirstOrDefault(line => line.StartsWith('→'))));

            if (code == 0)
            {
                data = MasterDataLoader.Load(directory);
                checks.Add(data.StaleArtifacts.Length == 0
                    ? new Check("derived", "ok", "파생물이 최신이다")
                    : new Check("derived", "fail",
                        $"낡은 파생물 {data.StaleArtifacts.Length}건",
                        string.Join("; ", data.StaleArtifacts.Select(item => item.Generator))));
            }
        }

        if (data is not null)
        {
            try
            {
                string sha = PromptPrefix.Build(data, directory).Sha256;
                string root = (new HostOptions { PlanStore = planstore }).ResolvePlanStore();
                PlanStoreLayout layout = PlanStoreLayout.Resolve(root, sha);
                PlanStore plans = PlanStore.CreateIdleOnly(data);
                PlanStoreLoadReport loaded = PlanStoreIo.LoadAll(layout.Directory, layout.PinnedRoot, plans, data);
                int fallback = data.Fallbacks?.Plans.Length ?? 0;
                checks.Add(new Check("planstore", "info",
                    $"프리픽스 {sha[..8]} · LLM 생성 {loaded.Total - loaded.Pinned} · "
                    + $"사람 고정 {loaded.Pinned} · 폴백 {fallback} · 미생성 {plans.ColdBuckets}",
                    plans.ColdBuckets > 0 ? "필요하면 tools/Npc.Prebake로 플랜을 생성한다." : null));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException)
            {
                checks.Add(new Check("planstore", "fail", error.Message));
            }
        }

        try
        {
            LlmOptions llm = LlmOptions.LoadDefault();
            string[] absent = [.. llm.Engines
                .Where(engine => engine.ApiKeyEnv is { Length: > 0 }
                    && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(engine.ApiKeyEnv)))
                .Select(engine => engine.ApiKeyEnv!)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
            checks.Add(absent.Length == 0
                ? new Check("llm_keys", "ok", "설정된 엔진 키가 있다")
                : new Check("llm_keys", "info", $"키 없음: {string.Join(", ", absent)} — LLM 없이도 동작한다",
                    "필요한 엔진의 환경변수만 설정한다."));
            if (online)
            {
                await CheckOnline(llm, checks, engineId).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            checks.Add(new Check("llm_config", "fail", error.Message,
                "appsettings.Llm.json을 확인한다."));
        }

        foreach (int port in new[] { 5080, 7010, 25056 })
        {
            bool free = PortAvailable(port);
            checks.Add(free
                ? new Check($"port_{port}", "ok", "포트를 사용할 수 있다")
                : new Check($"port_{port}", "fail", "포트가 사용 중이다",
                    "충돌한 서비스나 --port, --gs-port 설정을 확인한다."));
        }

        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                Ok = checks.All(check => check.Status != "fail"),
                Checks = checks,
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        }
        else
        {
            foreach (Check check in checks)
            {
                output.WriteLine($"[{check.Status}] {check.Id}: {check.Detail}");
                if (check.FixHint is { Length: > 0 } hint) output.WriteLine($"  → {hint}");
            }
        }

        return checks.Any(check => check.Status == "fail") ? 1 : 0;
    }

    private static bool PortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task CheckOnline(LlmOptions llm, List<Check> checks, string? engineId)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        IEnumerable<LlmEngineOptions> engines = engineId is null
            ? llm.Chain("t1").Concat(llm.Chain("t2")).DistinctBy(engine => engine.Id)
            : [llm.Engine(engineId)];
        foreach (LlmEngineOptions engine in engines)
        {
            if (!ChatClientFactory.IsAvailable(engine))
            {
                checks.Add(new Check($"engine_{engine.Id}", "fail", $"키 환경변수 {engine.ApiKeyEnv}가 비어 있다",
                    "키를 설정한 뒤 다시 진단한다."));
                continue;
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    engine.Endpoint.TrimEnd('/') + "/models");
                if (engine.ApiKeyEnv is { } name
                    && Environment.GetEnvironmentVariable(name) is { Length: > 0 } key)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                }
                using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
                checks.Add(response.IsSuccessStatusCode
                    ? new Check($"engine_{engine.Id}", "ok", "엔진에 연결됐다")
                    : new Check($"engine_{engine.Id}", "fail", $"HTTP {(int)response.StatusCode}",
                        "엔드포인트와 키를 확인한다."));
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or UriFormatException)
            {
                checks.Add(new Check($"engine_{engine.Id}", "fail", error.GetType().Name,
                    "엔드포인트와 키를 확인한다."));
            }
        }
    }
}
