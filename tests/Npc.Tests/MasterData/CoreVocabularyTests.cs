using Npc.MasterData;

namespace Npc.Tests.MasterData;

public sealed class CoreVocabularyTests
{
    [Fact]
    public void CodeReferences_AreRegistered()
    {
        foreach (string path in Directory.EnumerateFiles(TestPaths.At("src"), "*.cs",
            SearchOption.AllDirectories).Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            Assert.Empty(CoreVocabulary.UnregisteredFlagReferences(File.ReadAllText(path)));
        }

        Assert.Equal(["UnknownFlag"],
            CoreVocabulary.UnregisteredFlagReferences("if (WorldFlags.UnknownFlag != WorldFlags.None)"));
    }

    [Fact]
    public void MissingFlag_ReportsBuildMismatch()
    {
        string source = File.ReadAllText(Path.Combine(TestPaths.MasterData, "world_flags.json"));
        string path = Path.Combine(Path.GetTempPath(), $"npc-flags-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, source.Replace("\"AtHome\"", "\"MissingAtHome\"",
                StringComparison.Ordinal));
            string? error = CoreVocabulary.WorldFlagsMismatch(path);
            Assert.Contains("+MissingAtHome", error);
            Assert.Contains("−AtHome", error);
            Assert.Contains("다시 빌드", error);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
