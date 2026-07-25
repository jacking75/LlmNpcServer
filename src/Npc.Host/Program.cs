using Npc.Host.Commands;

// 조립 루트. T1-57 에서 --loopback/--npcs/--time-scale 등 본체 옵션이 붙는다.
// 지금은 validate 서브커맨드만 라우팅한다.

if (args.Length > 0 && args[0] == ValidateCommand.Name)
{
    return ValidateCommand.Run(args[1..], Console.Out);
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
WebApplication app = builder.Build();

app.MapGet("/", () => "Npc.Host");

app.Run();
return 0;
