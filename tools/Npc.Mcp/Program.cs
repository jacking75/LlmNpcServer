using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npc.Mcp;
using Npc.Mcp.Tools;

// MCP 서버 (E-03). LLM 호스트가 이 저장소의 도구를 "부를" 수 있게 한다.
//
// <b>전송은 stdio 이고 표준출력이 프로토콜이다.</b> 부주의한 Console.WriteLine 하나가
// JSON-RPC 스트림을 깨뜨린다 — 이 파일 아래 어디에도 Console.Out 쓰기가 없고,
// 로그는 전부 표준오류로 간다. (--help 는 예외다: 그때는 서버를 띄우지 않는다.)
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.Out.WriteLine(McpOptions.Usage);
    return 0;
}

if (!McpOptions.TryParse([.. args], out McpOptions options, out string? error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(McpOptions.Usage);
    return 2;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder();

// 기본 콘솔 로거는 표준출력으로 간다 — 그대로 두면 첫 로그 줄에서 프로토콜이 깨진다.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(options);

IMcpServerBuilder mcp = builder.Services
    .AddMcpServer(server => server.ServerInfo = new ModelContextProtocol.Protocol.Implementation
    {
        Name = McpToolSet.ServerName,
        Version = typeof(McpToolSet).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
    })
    .WithStdioServerTransport()
    .WithResources<Resources>()
    .WithTools<MasterDataTools>()
    .WithTools<PlanTools>()
    .WithTools<ServerTools>()
    .WithTools<DocsTools>();

// <b>쓰기 툴은 목록에 뜨지도 않는다.</b> "있는데 거절" 이 아니라 "없다" 여야 한다 —
// 있는데 거절하면 모델이 우회를 시도한다.
if (options.AllowWrite)
{
    mcp.WithTools<WriteTools>();
}

Console.Error.WriteLine(
    $"npc-mcp: masterdata={options.MasterData} planstore={options.PlanStore} "
    + $"root={Path.GetFullPath(options.Root)} write={(options.AllowWrite ? "on" : "off")}");

await builder.Build().RunAsync().ConfigureAwait(false);

return 0;
