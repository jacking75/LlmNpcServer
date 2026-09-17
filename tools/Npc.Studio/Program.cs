using Microsoft.Extensions.FileProviders;
using Npc.Studio.Services;
using App = Npc.Studio.Components.App;

namespace Npc.Studio;

/// <summary>NPC Studio 웹 호스트.</summary>
public static class Program
{
    /// <summary>진입점.</summary>
    /// <param name="args">명령행 인자.</param>
    public static int Main(string[] args)
    {
        StudioOptions options;

        try
        {
            options = StudioOptions.Parse(args);
        }
        catch (StudioArgumentException ex)
        {
            // H26 — 모르는 인자로 뜨지 않는다. `--readonly`(오타)로 편집 모드로 뜨던 자리다.
            if (ex.Message.Length > 0)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine();
            }

            Console.Error.WriteLine(StudioOptions.Usage);

            return 2;
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);

            return 2;
        }

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
        builder.Services.AddSingleton<GeneratorRunner>();
        builder.Services.AddSingleton<StudioForecastService>();
        builder.Services.AddSingleton<PlanStoreReader>();
        builder.Services.AddSingleton<LiveClient>();
        builder.Services.AddSingleton<StudioPlaces>();
        builder.Services.AddSingleton<IssueLocator>();

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

        // H13 — 밖의 편집(VS Code · 생성기)을 지켜본다. 폴더가 바뀌면 다시 건다.
        StudioWorkspace workspace = app.Services.GetRequiredService<StudioWorkspace>();

        workspace.Watch();
        workspace.DirectoryChanged += workspace.Watch;

        Console.WriteLine($"NPC Studio: http://{options.Bind}:{options.Port}");
        Console.WriteLine($"마스터데이터: {options.MasterData}");

        if (options.FellBackFrom is { Length: > 0 } requested)
        {
            Console.WriteLine($"경고: 요청한 경로({requested})가 없어 저장소의 masterdata/ 를 열었다.");
        }

        Console.WriteLine(options.ReadOnly ? "읽기 전용 모드다." : "편집 모드다. 저장 전 전체 검증을 수행한다.");

        if (options.IsPublic && !options.ReadOnly)
        {
            Console.WriteLine(
                $"경고: --bind {options.Bind} 은 루프백이 아니다 — 같은 망의 누구나 이 마스터데이터를 고칠 수 있다.");
        }

        app.Run();

        return 0;
    }
}
