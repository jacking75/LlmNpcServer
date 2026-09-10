using System.Collections.Immutable;
using System.Globalization;
using Npc.Conformance;
using Npc.Contracts;
using Npc.Gateway;
using Npc.MasterData;
using Npc.Wire;
using Npc.Wire.V2;

// B-07 — 게임서버 적합성 테스트 키트.
//
// NPC 서버 대신 게임서버에 붙어 발행 규약을 지키는지 관찰하고 보고서를 낸다.
// 판정은 순수 함수(Checks/)가 하고 여기서는 소켓과 인자만 다룬다.

return await Cli.RunAsync(args).ConfigureAwait(false);

/// <summary>
/// 명령줄. <b>로직을 두지 않는다</b> — 인자를 읽고 <see cref="Observer"/>·<see cref="Report"/> 를 부른다.
/// </summary>
internal static class Cli
{
    /// <summary>합격.</summary>
    public const int Ok = 0;

    /// <summary>불합격. <b>붙었지만 규약을 어겼다.</b></summary>
    public const int Violations = 1;

    /// <summary>인자가 틀렸거나 붙지 못했다.</summary>
    public const int BadUsage = 2;

    /// <summary>돌린다.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.Out.WriteLine(Usage);
            return Ok;
        }

        string masterdata = Flag(args, "--masterdata") ?? "./masterdata";
        string host = Flag(args, "--host") ?? "127.0.0.1";
        int port = Int(args, "--port", 7010);
        int npcs = Int(args, "--npcs", 500);
        int timeScale = Int(args, "--time-scale", 600);
        int seconds = Int(args, "--seconds", 60);
        string outDir = Flag(args, "--out") ?? "docs/measurements";

        MasterDataSet data;

        try
        {
            data = MasterDataLoader.Load(masterdata);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"마스터데이터를 못 읽었다: {e.Message}");
            return BadUsage;
        }

        NpcInstanceTable instances = NpcInstanceTable.Load(
            Path.Combine(masterdata, "npc_instances.json"), data);

        // 로스터 선택 규칙은 NpcRoster 한 곳에 있다 — 게임서버도 같은 함수를 부른다.
        // 여기서 다른 규칙을 쓰면 해시가 어긋나 붙지 못한다.
        NpcRoster roster = NpcRoster.Select(instances, npcs);

        var options = new TcpLinkOptions
        {
            Host = host,
            Port = port,
            TimeScale = timeScale,
            NpcCount = roster.Count,
            MasterData = WireHash.FromHex(data.ContentHash),
            MasterDataStructural = WireHash.FromHex(data.StructuralHash),
            MasterDataContent = WireHash.FromHex(data.ContentHash),
            Roster = WireHash.FromHex(roster.Hash),
            Secret = Secret(),
        };

        await using var link = new TcpGameServerLink(options);
        var observer = new Observer(link, roster.Count, data.Zones.Zones.Length);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.Out.WriteLine($"{host}:{port} 에 붙는다. {seconds}초 관찰한다.");

        Task run = link.RunAsync(cts.Token);

        observer.Start();

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Out.WriteLine("중단됐다. 여기까지의 관찰로 판정한다.");
        }

        observer.Stop();
        observer.Detach();

        await cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 종료 지시다.
        }

        // 게임서버가 스스로 페이싱한 회차다 — 틱 속도를 판정할 수 있다.
        Observation observation = observer.Snapshot(paced: true);

        if (!observation.Connected)
        {
            Console.Error.WriteLine($"붙지 못했다: {observation.NegotiationDetail}");
            return BadUsage;
        }

        ImmutableArray<CheckResult> results = Report.Run(observation);

        // 시각은 밖에서 넣는다. 보고서 안에서 DateTime 을 부르면 결정론이 깨진다 (CLAUDE.md §7).
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        string target = $"{host}:{port}";

        Directory.CreateDirectory(outDir);

        string stem = "conformance_" + DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);

        File.WriteAllText(
            Path.Combine(outDir, stem + ".md"), Report.Markdown(results, observation, target, stamp));
        File.WriteAllText(
            Path.Combine(outDir, stem + ".json"), Report.Json(results, target, stamp));

        foreach (CheckResult result in results)
        {
            Console.Out.WriteLine($"[{result.Verdict,-10}] {result.Id,-16} {result.Detail}");

            foreach (string violation in result.Violations)
            {
                Console.Out.WriteLine($"             - {violation}");
            }
        }

        bool passed = Report.Passed(results);

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{Path.Combine(outDir, stem)}.md · .json 에 썼다.");
        Console.Out.WriteLine(passed ? "합격." : "불합격 — 위 위반을 게임서버 팀에 전달한다.");

        return passed ? Ok : Violations;
    }

    /// <summary>링크 비밀. <b>환경변수로만 온다</b> — 인자는 <c>ps</c> 에 보인다 (A-06).</summary>
    private static byte[] Secret()
    {
        if (Environment.GetEnvironmentVariable("NPC_LINK_SECRET") is not { Length: > 0 } hex)
        {
            return [];
        }

        return LinkAuth.TryParseSecret(hex, out byte[] secret, out string? error)
            ? secret
            : throw new ArgumentException($"NPC_LINK_SECRET: {error}");
    }

    private static string? Flag(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);

        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Int(string[] args, string name, int fallback) =>
        Flag(args, name) is { } text
        && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    private const string Usage = """
        Npc.Conformance — 게임서버 적합성 테스트 키트 (B-07)

          NPC 서버 대신 게임서버에 붙어 발행 규약(docs/reference_link.html §11)을
          지키는지 관찰하고 보고서를 낸다. 명령을 내지 않으므로 세계를 바꾸지 않는다.

        사용:
          Npc.Conformance [옵션]

        옵션:
          --host <이름>        게임서버 호스트 (기본 127.0.0.1)
          --port <번호>        링크 포트 (기본 7010)
          --masterdata <경로>  마스터데이터 (기본 ./masterdata)
          --npcs <수>          로스터 크기. 게임서버와 같아야 붙는다 (기본 500)
          --time-scale <배속>  게임서버와 같아야 붙는다 (기본 600)
          --seconds <초>       관찰 시간 (기본 60)
          --out <폴더>         보고서 폴더 (기본 docs/measurements)

        환경변수:
          NPC_LINK_SECRET      링크 HMAC 비밀 hex 64자. 게임서버가 인증을 켰으면 필요하다

        종료 코드:
          0  합격
          1  불합격 — 규약 위반이 있다
          2  붙지 못했거나 인자가 틀렸다

        미판정은 통과가 아니다. 보고서의 "미판정" 절을 반드시 읽는다.
        """;
}
