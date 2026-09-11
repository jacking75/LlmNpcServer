using System.Collections.Immutable;
using Npc.Cli;

namespace Npc.Tests.Cli;

/// <summary>
/// G-03 — 성능 회귀 판정.
///
/// <para>
/// <b>완료 조건이 "틱 루프에 LINQ 한 줄을 넣으면 로컬에서도 빨간불" 이다.</b>
/// 그 한 줄이 만드는 것은 틱당 할당이고, 여기서는 그 값이 든 CSV 를 넣어 판정이
/// 실제로 떨어지는지를 본다 — 판정기가 언제나 통과하면 회귀 검사는 장식이다.
/// </para>
/// </summary>
public sealed class PerfCheckTests
{
    private static PerfRow Clean(string cell = "n5000-x600-none-b20-w2-max") =>
        new(cell, TickP99Ms: 0.8, ScanPerTick: 45, BytesPerTick: 0, Overruns: 0, SequenceGaps: 0,
            ScanCapSetting: -1);

    /// <summary>깨끗한 회차는 통과한다.</summary>
    [Fact]
    public void Clean_Passes()
    {
        Assert.Empty(PerfCommand.Judge([Clean()], []));
    }

    /// <summary>
    /// <b>틱 루프에 LINQ 한 줄 = 틱당 할당.</b> 그 한 가지만으로 판정이 떨어져야 한다.
    /// </summary>
    [Fact]
    public void Allocation_FailsEvenWhenEverythingElseIsFine()
    {
        ImmutableArray<PerfViolation> violations =
            PerfCommand.Judge([Clean() with { BytesPerTick = 4096 }], []);

        PerfViolation violation = Assert.Single(violations);

        Assert.Equal("bytes_per_tick", violation.Rule);
    }

    /// <summary>예산 초과·스캔 초과·오버런·시퀀스 갭도 각각 잡는다.</summary>
    [Theory]
    [InlineData("tick_p99")]
    [InlineData("scan_per_tick")]
    [InlineData("tick_overruns")]
    [InlineData("sequence_gaps")]
    public void EachAbsoluteRule_Fires(string rule)
    {
        PerfRow row = rule switch
        {
            "tick_p99" => Clean() with { TickP99Ms = PerfCommand.TickBudgetMs + 0.1 },
            "scan_per_tick" => Clean() with { ScanPerTick = PerfCommand.ScanCap + 1 },
            "tick_overruns" => Clean() with { Overruns = 1 },
            _ => Clean() with { SequenceGaps = 1 },
        };

        Assert.Contains(PerfCommand.Judge([row], []), v => v.Rule == rule);
    }

    /// <summary>
    /// <b>상한을 일부러 끈 대조 회차는 스캔 규칙에서 뺀다</b> (<c>scan_cap == 0</c>).
    /// 그러지 않으면 그 실험이 영영 빨간불이고, 사람이 판정을 꺼 버린다.
    /// </summary>
    [Fact]
    public void UncappedControlCell_IsExempt()
    {
        PerfRow uncapped = Clean("n5000-uncapped") with { ScanPerTick = 614, ScanCapSetting = 0 };

        Assert.DoesNotContain(PerfCommand.Judge([uncapped], []), v => v.Rule == "scan_per_tick");
    }

    /// <summary>기준선 대비 회귀를 잡는다.</summary>
    [Fact]
    public void Regression_AgainstBaseline_Fails()
    {
        PerfRow before = Clean() with { TickP99Ms = 1.0 };
        PerfRow after = Clean() with { TickP99Ms = 1.0 * PerfCommand.RegressionFactor + 0.01 };

        Assert.Contains(PerfCommand.Judge([after], [before]), v => v.Rule == "p99_regression");

        // 허용 폭 안이면 통과한다 — 노이즈마다 빨간불이면 사람이 판정을 끈다.
        PerfRow noise = Clean() with { TickP99Ms = 1.0 * PerfCommand.RegressionFactor - 0.01 };

        Assert.DoesNotContain(PerfCommand.Judge([noise], [before]), v => v.Rule == "p99_regression");
    }

    /// <summary>기준선에 없는 새 셀은 회귀로 세지 않는다 — 그러면 매트릭스를 넓힐 수 없다.</summary>
    [Fact]
    public void NewCell_IsNotARegression()
    {
        PerfRow before = Clean("old-cell") with { TickP99Ms = 0.5 };
        PerfRow added = Clean("brand-new-cell") with { TickP99Ms = 3.0 };

        Assert.Empty(PerfCommand.Judge([added], [before]));
    }

    /// <summary>
    /// <b>기준선이 저장소에 있다.</b> 기준이 밖에 있으면 판정이 재현되지 않는다.
    /// </summary>
    [Fact]
    public void Baseline_IsCommitted()
    {
        string path = TestPaths.At(PerfCommand.DefaultBaseline.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(path), $"{PerfCommand.DefaultBaseline} 이 없다 — 기준선을 커밋한다");

        ImmutableArray<PerfRow> baseline = PerfCommand.Read(path);

        Assert.NotEmpty(baseline);
        Assert.All(baseline, row => Assert.NotEqual(string.Empty, row.Cell));
    }

    /// <summary>
    /// <b>지금 저장소의 부하 결과가 기준선에 걸리지 않는다.</b>
    /// 걸린다면 커밋된 값이 이미 회귀 상태라는 뜻이다.
    /// </summary>
    [Fact]
    public void CommittedLoadResult_PassesItsOwnBaseline()
    {
        string csv = TestPaths.At(PerfCommand.DefaultCsv.Replace('/', Path.DirectorySeparatorChar));
        string baseline = TestPaths.At(PerfCommand.DefaultBaseline.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(csv), $"{PerfCommand.DefaultCsv} 이 없다");

        ImmutableArray<PerfViolation> violations =
            PerfCommand.Judge(PerfCommand.Read(csv), PerfCommand.Read(baseline));

        Assert.True(
            violations.IsEmpty,
            "커밋된 부하 결과가 판정에 걸린다: "
            + string.Join(" · ", violations.Select(v => $"{v.Cell} {v.Rule}")));
    }

    /// <summary>기준선 왕복. 쓰고 읽으면 같은 값이다.</summary>
    [Fact]
    public void Baseline_RoundTrips()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "npc-perf-" + Guid.NewGuid().ToString("N")[..8] + ".csv");

        try
        {
            PerfCommand.Write(path, [Clean(), Clean("other") with { ScanCapSetting = 0 }]);

            ImmutableArray<PerfRow> read = PerfCommand.Read(path);

            Assert.Equal(2, read.Length);
            Assert.Equal(0.8, read.First(r => r.Cell == "n5000-x600-none-b20-w2-max").TickP99Ms);
            Assert.Equal(0, read.First(r => r.Cell == "other").ScanCapSetting);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
