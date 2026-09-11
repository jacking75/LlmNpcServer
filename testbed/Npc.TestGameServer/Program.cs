using Npc.TestGameServer;

// 게임서버 대역의 조립 루트. docs/20 §7.1 · §12.
//
// 순서는 Npc.Host/Program.cs 와 같다 — 파싱 → 도움말 → 경로 해석 → 조립 → 루프 → 요약.
// 실제 조립과 틱 루프는 GameServer 에 있다 (T6-37). 여기 있는 것은 CLI 껍데기뿐이다.

if (!GameServerOptions.TryParse(args, out GameServerOptions options, out string? parseError))
{
    Console.Error.WriteLine(parseError);
    Console.Error.WriteLine();
    Console.Error.WriteLine(GameServerOptions.Usage);
    return 2;
}

if (options.Help)
{
    Console.Out.WriteLine(GameServerOptions.Usage);
    return 0;
}

// 경로는 여기서 한 번에 푼다. 아래 코드는 절대경로만 다룬다.
// 경로 오타는 사용자 입력 문제이므로 미처리 예외로 스택트레이스를 쏟지 않는다.
if (!options.TryResolvePaths(out GameServerOptions resolved, out string? pathError))
{
    Console.Error.WriteLine(pathError);
    return 2;
}

options = resolved;

GameServer server;

try
{
    server = GameServer.Create(options, Console.Out);
}
catch (Exception e) when (e is ArgumentException or FileNotFoundException or DirectoryNotFoundException)
{
    // 모르는 존 id · 0 마리 · 없는 파일. 전부 사용자 입력 문제다 (docs/20 §10.3).
    Console.Error.WriteLine(e.Message);
    return 2;
}

await using (server.ConfigureAwait(false))
{
    server.Start();

    Console.Out.WriteLine(
        $"game server: npcs={server.World.Roster.Count} time-scale={options.TimeScale} " +
        $"bots={options.Bots} max-clients={options.MaxClients}");
    Console.Out.WriteLine($"masterdata: {options.MasterData} ({server.Data.ContentHash[..12]})");
    Console.Out.WriteLine($"roster hash: {server.World.RosterHash[..12]}");
    Console.Out.WriteLine($"listening: link :{server.LinkPort} | client :{server.ClientPort}");

    if (options.Scenario is { } scenario)
    {
        Console.Out.WriteLine($"scenario: {scenario}");
    }

    using var stopping = new CancellationTokenSource();

    // Ctrl-C 에 루프만 멈춘다. 프로세스를 즉사시키면 종료 요약이 안 나오고,
    // 그 한 줄이 회차가 정상이었는지 말하는 유일한 값이다 (docs/20 §7.5).
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopping.Cancel();
    };

    await server.RunAsync(stopping.Token).ConfigureAwait(false);

    Console.Out.WriteLine(server.Summarize());
}

return 0;
