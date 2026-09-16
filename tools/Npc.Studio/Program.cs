using Microsoft.Extensions.FileProviders;
using Npc.Studio.Services;
using App = Npc.Studio.Components.App;

namespace Npc.Studio;

/// <summary>NPC Studio 웹 호스트.</summary>
public static class Program
{
    /// <summary>진입점.</summary>
    public static void Main(string[] args)
    {
        StudioOptions options = StudioOptions.Parse(args);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        // Windows Event Log는 제한된 개발 계정에서 쓰기 권한이 없을 수 있다.
        // Studio는 로컬 도구이므로 콘솔 로그만으로 충분하며 시작 실패도 피할 수 있다.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        // `dotnet run -c Release`처럼 Production 환경에서 빌드 산출물을 직접 실행해도
        // Blazor 프레임워크 정적 자산을 런타임 매니페스트에서 찾도록 한다.
        builder.WebHost.UseStaticWebAssets();
        builder.WebHost.UseUrls($"http://{options.Bind}:{options.Port}");
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<StudioWorkspace>();

        // 회로(브라우저 탭)마다 하나. 화면이 여럿으로 갈렸으므로 카탈로그를 공유한다 (T01).
        builder.Services.AddScoped<StudioSession>();
        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        WebApplication app = builder.Build();

        app.UseStaticFiles();
        app.MapStaticAssets();

        string docs = Path.GetFullPath(Path.Combine(options.MasterData, "..", "docs"));

        if (Directory.Exists(docs))
        {
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(docs),
                RequestPath = "/docs",
            });
        }

        app.UseAntiforgery();
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

        Console.WriteLine($"NPC Studio: http://{options.Bind}:{options.Port}");
        Console.WriteLine($"마스터데이터: {options.MasterData}");
        Console.WriteLine(options.ReadOnly ? "읽기 전용 모드다." : "편집 모드다. 저장 전 전체 검증을 수행한다.");

        app.Run();
    }
}
