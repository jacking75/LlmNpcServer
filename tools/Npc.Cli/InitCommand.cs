using Npc.MasterData;
using Npc.MasterData.Validation;

namespace Npc.Cli;

/// <summary>작은 유효한 세계를 별도 폴더에 만든다.</summary>
public static class InitCommand
{
    /// <summary>템플릿을 복사하고 검증한다.</summary>
    public static int Run(CliContext ctx)
    {
        string[] positional = [.. Program.Positional(ctx, "--template")];
        if (positional.Length != 1 || Program.Flag(ctx, "--template") is { } choice && choice != "minimal")
        {
            ctx.Out.WriteLine("사용법: npc init <dir> [--template minimal]");
            return Program.BadUsage;
        }

        string destination = Path.GetFullPath(positional[0]);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            ctx.Out.WriteLine($"이미 파일이 있는 폴더다: {destination}");
            return Program.Failed;
        }

        string source = FindTemplate();
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        MasterDataValidationReport report = MasterDataValidator.Validate(destination);
        if (!report.IsValid)
        {
            foreach (MasterDataViolation violation in report.Violations)
                ctx.Out.WriteLine($"{violation.Code} {violation.Detail}");
            return Program.Failed;
        }

        MasterDataSet data = MasterDataLoader.Load(destination);
        if (!data.StaleArtifacts.IsEmpty)
        {
            ctx.Out.WriteLine("템플릿 파생물이 낡았다. 생성기를 다시 실행한다.");
            return Program.Failed;
        }

        ctx.Out.WriteLine($"세계 생성 완료: {destination}");
        ctx.Out.WriteLine($"검증 통과 · NPC 60 · 존 {data.Zones.Count} · POI {data.Pois.Count} · 아키타입 {data.Archetypes.Count}");
        ctx.Out.WriteLine($"다음: Npc.Host --masterdata {destination} --planstore {Path.Combine(destination, "planstore")} --npcs 60 --no-llm --days 1");
        return Program.Ok;
    }

    private static string FindTemplate()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var folder = new DirectoryInfo(start);
            while (folder is not null)
            {
                string candidate = Path.Combine(folder.FullName, "samples", "worlds", "minimal");
                if (File.Exists(Path.Combine(candidate, "archetypes.json"))) return candidate;
                folder = folder.Parent;
            }
        }
        throw new DirectoryNotFoundException("samples/worlds/minimal 템플릿을 찾지 못했다.");
    }
}
