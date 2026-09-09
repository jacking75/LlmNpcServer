using Npc.Contracts;
using Npc.Core;
using Npc.Host.Persistence;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Persistence;

/// <summary>A-01 — 복원 조건. PRODUCTION_ROADMAP §4 A-01 설계 7항.</summary>
public sealed class SnapshotRestoreTests : IDisposable
{
    private const string MasterHash = "aaaa1111";
    private const string RosterHash = "bbbb2222";
    private const string PrefixHash = "cccc3333";

    private static readonly MasterDataSet s_data = MasterDataLoader.Load(TestPaths.MasterData);

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "npc-restore-" + Guid.NewGuid().ToString("N"));

    public SnapshotRestoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Restore_None_DoesNothing()
    {
        Rig rig = NewRig();

        Write(tick: 100, MasterHash, RosterHash, PrefixHash);

        RestoreResult result = rig.Restorer.TryRestore(
            RestoreMode.None, _dir, MasterHash, RosterHash, PrefixHash, TextWriter.Null);

        Assert.False(result.Restored);
        Assert.Equal(0, rig.Clock.Current.Value);
    }

    [Fact]
    public void Restore_Auto_TakesTheNewest()
    {
        Rig rig = NewRig();

        Write(tick: 100, MasterHash, RosterHash, PrefixHash);
        Write(tick: 500, MasterHash, RosterHash, PrefixHash);

        RestoreResult result = rig.Restorer.TryRestore(
            RestoreMode.Auto, _dir, MasterHash, RosterHash, PrefixHash, TextWriter.Null);

        Assert.True(result.Restored, result.Detail);
        Assert.Equal(500, result.Tick);
        Assert.Equal(500, rig.Clock.Current.Value);
    }

    [Fact]
    public void Restore_FallsBackWhenHashMismatch()
    {
        Rig rig = NewRig();

        // 최신 것이 다른 마스터데이터로 만들어졌다 — 건너뛰고 그 앞으로 물러난다.
        Write(tick: 100, MasterHash, RosterHash, PrefixHash);
        Write(tick: 500, "deadbeef", RosterHash, PrefixHash);

        RestoreResult result = rig.Restorer.TryRestore(
            RestoreMode.Auto, _dir, MasterHash, RosterHash, PrefixHash, TextWriter.Null);

        Assert.True(result.Restored, result.Detail);
        Assert.Equal(100, result.Tick);
    }

    [Fact]
    public void Restore_SeedsWhenEveryCandidateMismatches()
    {
        Rig rig = NewRig();

        Write(tick: 100, "deadbeef", RosterHash, PrefixHash);

        RestoreResult result = rig.Restorer.TryRestore(
            RestoreMode.Auto, _dir, MasterHash, RosterHash, PrefixHash, TextWriter.Null);

        Assert.False(result.Restored);
        Assert.Contains("마스터데이터 해시", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_ReissuesWaitingSteps()
    {
        Rig rig = NewRig();

        var shadow = new ShadowBuffer(rig.Store.Count, rig.Store.InventoryStride, rig.Zones.Capacity);

        shadow.StepStatus[0] = (byte)StepStatus.Waiting;
        shadow.StepIssuedTick[0] = 42;

        SnapshotFile.Write(
            Path.Combine(_dir, SnapshotFile.NameOf(200)),
            shadow,
            new SnapshotHeader(
                SnapshotFile.FormatVersion, 200, 200,
                rig.Store.Count, rig.Store.InventoryStride, rig.Zones.Capacity, 1,
                MasterHash, RosterHash, PrefixHash),
            []);

        RestoreResult result = rig.Restorer.TryRestore(
            RestoreMode.Auto, _dir, MasterHash, RosterHash, PrefixHash, TextWriter.Null);

        Assert.True(result.Restored, result.Detail);
        Assert.Equal(1, result.ReissuedSteps);
        Assert.Equal((byte)StepStatus.Ready, rig.Store.StepStatus[0]);
    }

    [Fact]
    public void Restore_MissingDirectory_IsNotAnError()
    {
        Rig rig = NewRig();

        RestoreResult result = rig.Restorer.TryRestore(
            RestoreMode.Auto,
            Path.Combine(_dir, "nope"),
            MasterHash, RosterHash, PrefixHash,
            TextWriter.Null);

        Assert.False(result.Restored);
        Assert.Contains("스냅샷이 없다", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_ZoneStates_ComeBack()
    {
        Rig rig = NewRig();

        var shadow = new ShadowBuffer(rig.Store.Count, rig.Store.InventoryStride, rig.Zones.Capacity);

        shadow.ZoneRegion[1] = (byte)RegionState.Disaster;
        shadow.ZoneClimate[1] = (byte)Climate.Storm;

        SnapshotFile.Write(
            Path.Combine(_dir, SnapshotFile.NameOf(300)),
            shadow,
            new SnapshotHeader(
                SnapshotFile.FormatVersion, 300, 300,
                rig.Store.Count, rig.Store.InventoryStride, rig.Zones.Capacity, 1,
                MasterHash, RosterHash, PrefixHash),
            []);

        Assert.True(
            rig.Restorer.TryRestore(
                RestoreMode.Auto, _dir, MasterHash, RosterHash, PrefixHash, TextWriter.Null).Restored);

        Assert.Equal(RegionState.Disaster, rig.Zones.RegionOf(new ZoneId(1)));
        Assert.Equal(Climate.Storm, rig.Zones.ClimateOf(new ZoneId(1)));
    }

    private sealed record Rig(
        NpcStore Store,
        GameClock Clock,
        ZoneStateTable Zones,
        CorrelationTable Correlations,
        SnapshotRestorer Restorer);

    private static Rig NewRig()
    {
        var store = new NpcStore();

        store.Allocate(32, s_data.Items.MaxCode + 1);

        var clock = new GameClock(s_data.Buckets, timeScale: 60);
        var zones = new ZoneStateTable(s_data);
        var correlations = new CorrelationTable(32);
        var pool = new IndividualPlanPool();

        return new Rig(
            store, clock, zones, correlations,
            new SnapshotRestorer(store, clock, zones, correlations, pool, s_data));
    }

    private void Write(long tick, string master, string roster, string prefix)
    {
        var store = new NpcStore();

        store.Allocate(32, s_data.Items.MaxCode + 1);

        var zones = new ZoneStateTable(s_data);
        var shadow = new ShadowBuffer(32, store.InventoryStride, zones.Capacity);

        store.CopyTo(shadow);

        SnapshotFile.Write(
            Path.Combine(_dir, SnapshotFile.NameOf(tick)),
            shadow,
            new SnapshotHeader(
                SnapshotFile.FormatVersion, tick, tick,
                32, store.InventoryStride, zones.Capacity, 1,
                master, roster, prefix),
            []);
    }
}
