using System.Text.Json;

namespace Npc.Tests.MasterData;

/// <summary>
/// masterdata/poi_distances.bin 의 무결성. docs/01 §4.
/// tools/gen_poi_distances.cs 가 만든다. 손으로 만들지 않는다.
/// </summary>
public sealed class PoiDistanceTests
{
    private static readonly (int Count, Half[] Matrix) s_matrix = LoadMatrix();

    private static (int, Half[]) LoadMatrix()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(TestPaths.MasterData, "poi_distances.bin"));

        Assert.True(bytes.Length > 8, "poi_distances.bin 이 헤더보다 짧다.");
        Assert.Equal("POID"u8.ToArray(), bytes[..4]);

        int n = BitConverter.ToInt32(bytes, 4);
        var matrix = new Half[n * n];

        for (int i = 0; i < matrix.Length; i++)
        {
            matrix[i] = BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes, 8 + (i * 2)));
        }

        return (n, matrix);
    }

    private static int PoiCount()
    {
        using JsonDocument doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(TestPaths.MasterData, "pois.json")));

        return doc.RootElement.GetProperty("pois").GetArrayLength();
    }

    [Fact]
    public void PoiDistances_CoversEveryPoi()
    {
        Assert.Equal(PoiCount(), s_matrix.Count);
    }

    /// <summary>docs/01 §4 — 250×250 Half 이면 ≈122KB. POI 수 × POI 수 × 2 + 헤더 8바이트.</summary>
    [Fact]
    public void PoiDistances_FileSizeMatchesMatrix()
    {
        long size = new FileInfo(Path.Combine(TestPaths.MasterData, "poi_distances.bin")).Length;
        int n = s_matrix.Count;

        Assert.Equal((n * (long)n * 2) + 8, size);
        Assert.InRange(size, 100 * 1024, 140 * 1024);
    }

    [Fact]
    public void PoiDistances_IsSymmetric()
    {
        int n = s_matrix.Count;
        Half[] m = s_matrix.Matrix;

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                Assert.Equal(m[(i * n) + j], m[(j * n) + i]);
            }
        }
    }

    [Fact]
    public void PoiDistances_DiagonalIsZero()
    {
        int n = s_matrix.Count;

        for (int i = 0; i < n; i++)
        {
            Assert.Equal((Half)0f, s_matrix.Matrix[(i * n) + i]);
        }
    }

    /// <summary>T1-15 완료 조건 — 무한대(도달 불가) 항목이 0 이어야 한다.</summary>
    [Fact]
    public void PoiDistances_HasNoUnreachableEntry()
    {
        int n = s_matrix.Count;
        Half[] m = s_matrix.Matrix;

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                Half d = m[(i * n) + j];

                Assert.False(Half.IsNaN(d), $"({i},{j}) 가 NaN 이다.");
                Assert.False(Half.IsInfinity(d), $"({i},{j}) 가 무한대다 — 도달 불가 POI 가 있다.");
                Assert.True(d >= (Half)0f, $"({i},{j}) 가 음수다.");

                if (i != j)
                {
                    Assert.True(d > (Half)0f, $"({i},{j}) 가 서로 다른 POI 인데 거리가 0 이다.");
                }
            }
        }
    }

    /// <summary>삼각부등식은 존 그래프 최단거리 위에서 성립해야 한다 (Half 반올림 여유 5%).</summary>
    [Fact]
    public void PoiDistances_SatisfiesTriangleInequalityOnSamples()
    {
        int n = s_matrix.Count;
        Half[] m = s_matrix.Matrix;

        // 전수는 O(N³) 이라 느리다. 결정론적으로 고르게 표본을 잡는다.
        for (int i = 0; i < n; i += 7)
        {
            for (int j = 0; j < n; j += 11)
            {
                for (int k = 0; k < n; k += 13)
                {
                    float ij = (float)m[(i * n) + j];
                    float ik = (float)m[(i * n) + k];
                    float kj = (float)m[(k * n) + j];

                    Assert.True(ij <= (ik + kj) * 1.05f + 1f, $"삼각부등식 위반: ({i},{j})={ij} > ({i},{k})+({k},{j})={ik + kj}");
                }
            }
        }
    }
}
