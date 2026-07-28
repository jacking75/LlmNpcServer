using Npc.TestGameServer;

// 게임서버 대역의 조립 루트. docs/20 §7.1.
//
// 순서는 Npc.Host/Program.cs 와 같다 — 파싱 → 도움말 → 경로 해석 → 조립.
// 월드 조립(GameWorld)과 소켓 리스너는 T6-15·T6-16 에서 이 아래에 붙는다.

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

Console.Out.WriteLine(
    $"game server: npcs={options.Npcs} time-scale={options.TimeScale} " +
    $"link-port={options.LinkPort} client-port={options.ClientPort} " +
    $"bots={options.Bots} max-clients={options.MaxClients}");
Console.Out.WriteLine($"masterdata: {options.MasterData}");

if (options.Scenario is { } scenario)
{
    Console.Out.WriteLine($"scenario: {scenario}");
}

return 0;
