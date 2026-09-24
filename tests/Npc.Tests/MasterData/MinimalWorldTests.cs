using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.MasterData.Validation;

namespace Npc.Tests.MasterData;

public sealed class MinimalWorldTests
{
    [Fact]
    public void CheckedInTemplate_ValidatesAndHasFreshDerivedFiles()
    {
        string path = TestPaths.At("samples", "worlds", "minimal");
        MasterDataValidationReport report = MasterDataValidator.Validate(path);
        Assert.True(report.IsValid, string.Join("\n", report.Violations.Select(v => v.Detail)));
        MasterDataSet data = MasterDataLoader.Load(path);
        Assert.Equal(2, data.Zones.Count);
        Assert.Equal(12, data.Pois.Count);
        Assert.Equal(3, data.Archetypes.Count);
        Assert.Equal(8, data.Items.Items.Length);
        Assert.Equal(60, NpcInstanceTable.Load(Path.Combine(path, "npc_instances.json"), data).Count);
        Assert.Empty(DerivedArtifacts.Stale(path));
    }
}
