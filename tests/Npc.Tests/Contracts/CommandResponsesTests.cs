using System.Text.RegularExpressions;
using Npc.Contracts;

namespace Npc.Tests.Contracts;

public sealed class CommandResponsesTests
{
    [Fact]
    public void ReferenceTableCoversEveryCommand()
    {
        string html = File.ReadAllText(TestPaths.At("docs", "reference_link.html"));
        string[] documented = Regex.Matches(html, "data-command=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToArray();
        string[] defined = CommandResponses.All.ToArray().Select(s => s.Command.ToString()).ToArray();

        Assert.Equal(Enum.GetNames<NpcCommandKind>(), defined);
        Assert.Equal(defined, documented);
    }
}
