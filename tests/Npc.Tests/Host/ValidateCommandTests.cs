using System.Text.Json.Nodes;
using Npc.Host.Commands;

namespace Npc.Tests.Host;

/// <summary>docs/11 §11 — validate 서브커맨드. 정상 0, 손상 1, 인자 오류 2.</summary>
public sealed class ValidateCommandTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 정리 실패는 테스트 실패가 아니다.
            }
        }
    }

    private string CorruptedMasterData()
    {
        string dir = Path.Combine(Path.GetTempPath(), "npc-cli-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        foreach (string source in Directory.GetFiles(TestPaths.MasterData))
        {
            File.Copy(source, Path.Combine(dir, Path.GetFileName(source)));
        }

        string target = Path.Combine(dir, "context_buckets.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(target))!;
        root["total_keys"] = 1;
        File.WriteAllText(target, root.ToJsonString());

        return dir;
    }

    [Fact]
    public void ValidateCommand_ExitsZeroOnGoodData()
    {
        var output = new StringWriter();

        int code = ValidateCommand.Run(["--masterdata", TestPaths.MasterData], output);

        Assert.Equal(0, code);
        Assert.Contains("검증 통과", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("content_hash:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateCommand_ExitsNonZeroOnCorruptedDataAndPrintsCode()
    {
        var output = new StringWriter();

        int code = ValidateCommand.Run(["--masterdata", CorruptedMasterData()], output);

        Assert.Equal(1, code);
        Assert.Contains("FAIL V6", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateCommand_ReportsSkippedRules()
    {
        var output = new StringWriter();

        ValidateCommand.Run(["--masterdata", TestPaths.MasterData], output);

        // P1 에는 폴백 플랜과 프롬프트 프리픽스가 없다. 무엇을 왜 건너뛰었는지 보여야 한다.
        Assert.Contains("SKIP V9", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateCommand_PrefixTokenOptionTriggersV9()
    {
        var output = new StringWriter();

        int code = ValidateCommand.Run(
            ["--masterdata", TestPaths.MasterData, "--prefix-tokens", "100"], output);

        Assert.Equal(1, code);
        Assert.Contains("FAIL V9", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateCommand_MissingDirectoryExitsTwo()
    {
        var output = new StringWriter();

        int code = ValidateCommand.Run(["--masterdata", Path.Combine(TestPaths.RepoRoot, "nope")], output);

        Assert.Equal(2, code);
    }

    [Fact]
    public void ValidateCommand_UnknownArgumentExitsTwo()
    {
        var output = new StringWriter();

        int code = ValidateCommand.Run(["--nope"], output);

        Assert.Equal(2, code);
        Assert.Contains(ValidateCommand.Usage, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateCommand_HelpExitsZero()
    {
        var output = new StringWriter();

        Assert.Equal(0, ValidateCommand.Run(["--help"], output));
        Assert.Contains(ValidateCommand.Usage, output.ToString(), StringComparison.Ordinal);
    }
}
