using System.Text.Json;

namespace Npc.Tests.MasterData;

/// <summary>
/// masterdata/world_flags.json 자체의 무결성. docs/01 §1.
/// bit 번호를 재배치하면 프리베이크된 플랜 2,880개가 통째로 깨지므로,
/// 개수·중복·예약 구간을 테스트로 못박는다.
/// </summary>
public sealed class WorldFlagsDataTests
{
    private static readonly JsonDocument s_doc = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(TestPaths.MasterData, "world_flags.json")));

    private static (int Bit, string Id)[] Flags() =>
        s_doc.RootElement.GetProperty("flags").EnumerateArray()
            .Select(e => (e.GetProperty("bit").GetInt32(), e.GetProperty("id").GetString()!))
            .ToArray();

    [Fact]
    public void WorldFlagsJson_HasFortyTwoFlags()
    {
        Assert.Equal(42, Flags().Length);
    }

    [Fact]
    public void WorldFlagsJson_BitsAreUnique()
    {
        int[] bits = Flags().Select(f => f.Bit).ToArray();

        Assert.Equal(bits.Length, bits.Distinct().Count());
    }

    [Fact]
    public void WorldFlagsJson_IdsAreUnique()
    {
        string[] ids = Flags().Select(f => f.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void WorldFlagsJson_ReservedBitsAreEmpty()
    {
        int[] reserved = s_doc.RootElement.GetProperty("reserved_bits")
            .EnumerateArray().Select(e => e.GetInt32()).ToArray();
        int[] used = Flags().Select(f => f.Bit).ToArray();

        Assert.Equal([22, 23, .. Enumerable.Range(44, 20)], reserved);
        Assert.Empty(used.Intersect(reserved));
    }

    [Fact]
    public void WorldFlagsJson_BitsFitInUInt64()
    {
        Assert.All(Flags(), f => Assert.InRange(f.Bit, 0, 63));
    }
}
