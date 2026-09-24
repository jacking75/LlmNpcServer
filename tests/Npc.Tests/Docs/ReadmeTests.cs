using System.Text.RegularExpressions;
using Npc.Host;

namespace Npc.Tests.Docs;

public sealed class ReadmeTests
{
    private static readonly Regex Commands = new(@"(?<![\w-])npc\s+([a-z][a-z-]+)", RegexOptions.Compiled);
    private static readonly Regex Options = new(@"--[a-z][a-z0-9-]*", RegexOptions.Compiled);
    private static readonly Regex Links = new(@"!?\[[^\]]+\]\(([^)]+)\)", RegexOptions.Compiled);

    [Fact]
    public void Readme_CommandsOptionsAndRelativeLinksStayValid()
    {
        string readme = File.ReadAllText(TestPaths.At("README.md"));
        Assert.Empty(UnknownCommands(readme));
        Assert.Empty(UnknownOptions(readme));
        Assert.Empty(MissingLinks(readme, TestPaths.RepoRoot));
        Assert.True(readme.Split('\n').Length <= 250);
    }

    [Fact]
    public void AdoptionGuide_RelativeLinksResolve()
    {
        string text = File.ReadAllText(TestPaths.At("docs", "ADOPTION.md"));
        Assert.Empty(MissingLinks(text, TestPaths.At("docs")));
    }

    [Fact]
    public void Scanner_CatchesUnknownCommandAndOption()
    {
        Assert.Contains("teleport", UnknownCommands("`npc teleport`"));
        Assert.Contains("--unicorn", UnknownOptions("`Npc.Host --unicorn`"));
    }

    private static string[] UnknownCommands(string text)
    {
        string usage = Npc.Cli.Program.Usage;
        return [.. Commands.Matches(text).Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(command => !Regex.IsMatch(usage, @"(?m)^\s+" + Regex.Escape(command) + @"(?:\s|$)"))];
    }

    private static string[] UnknownOptions(string text)
    {
        var known = HostOptionCatalog.Options.Select(entry => entry.Option).ToHashSet(StringComparer.Ordinal);
        known.UnionWith(["--project", "--out", "--help"]);
        return [.. Options.Matches(text).Select(match => match.Value)
            .Distinct(StringComparer.Ordinal).Where(option => !known.Contains(option))];
    }

    private static string[] MissingLinks(string text, string baseDirectory) =>
        [.. Links.Matches(text).Select(match => match.Groups[1].Value)
            .Where(link => !link.Contains("://", StringComparison.Ordinal) && !link.StartsWith('#'))
            .Where(link => !File.Exists(Path.Combine(baseDirectory, link.Split('#')[0]))
                && !Directory.Exists(Path.Combine(baseDirectory, link.Split('#')[0])))];
}
