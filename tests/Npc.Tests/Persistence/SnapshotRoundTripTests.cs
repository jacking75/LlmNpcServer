using Npc.Contracts;
using Npc.Core;
using Npc.Host.Persistence;
using Npc.MasterData;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Tests.Persistence;

/// <summary>A-01 — 스냅샷 왕복. PRODUCTION_ROADMAP §4 A-01.</summary>
public sealed class SnapshotRoundTripTests : IDisposable
{
    private const int Npcs = 64;
    private const int Stride = 16;
    private const int Zones = 8;

    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "npc-snap-" + Guid.NewGuid().ToString("N"));

    public SnapshotRoundTripTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void RoundTrip_PreservesStateHash()
    {
        NpcStore store = Populated(seed: 3);
        ulong before = store.StateHash();

        var shadow = new ShadowBuffer(Npcs, Stride, Zones);

        store.CopyTo(shadow);

        // 원본을 흔들어 놓는다. 복원이 실제로 값을 되돌리는지 봐야 한다.
        Array.Clear(store.Flags);
        Array.Clear(store.Inventory);
        Array.Clear(store.PlanId);

        Assert.NotEqual(before, store.StateHash());

        store.LoadFrom(shadow);

        Assert.Equal(before, store.StateHash());
    }

    [Fact]
    public void FileRoundTrip_PreservesEverything()
    {
        NpcStore store = Populated(seed: 7);
        ulong before = store.StateHash();

        var shadow = new ShadowBuffer(Npcs, Stride, Zones);

        store.CopyTo(shadow);
        shadow.ZoneRegion[2] = (byte)RegionState.War;
        shadow.ZoneClimate[3] = (byte)Climate.Storm;

        var header = Header(tick: 1234);
        string path = Path.Combine(_dir, SnapshotFile.NameOf(1234));

        long bytes = SnapshotFile.Write(path, shadow, header, [new IndividualPlanRecord(5, 12, "{}")]);

        Assert.True(bytes > 0);
        Assert.True(File.Exists(path));

        Assert.True(
            SnapshotFile.TryRead(
                path, Zones, out SnapshotHeader read, out ShadowBuffer? loaded,
                out IReadOnlyList<IndividualPlanRecord> plans, out string? error),
            error);

        Assert.Equal(header, read);
        Assert.Equal((byte)RegionState.War, loaded!.ZoneRegion[2]);
        Assert.Equal((byte)Climate.Storm, loaded.ZoneClimate[3]);

        IndividualPlanRecord plan = Assert.Single(plans);

        Assert.Equal(5, plan.Npc);
        Assert.Equal(12, plan.Bucket);

        NpcStore restored = Empty();

        restored.LoadFrom(loaded);

        Assert.Equal(before, restored.StateHash());
    }

    [Fact]
    public void RecentRing_SurvivesTheFile()
    {
        NpcStore store = Populated(seed: 11);

        var ring = default(RingBuffer8<RecentEvent>);
        var hit = new RecentEvent(GameEventKind.DamageTaken, new Tick(90), 7, 200);
        var seen = new RecentEvent(GameEventKind.PlayerProximity, new Tick(91), 8, 10);

        ring.Add(in hit);
        ring.Add(in seen);
        store.Recent[3] = ring;

        var shadow = new ShadowBuffer(Npcs, Stride, Zones);

        store.CopyTo(shadow);

        string path = Path.Combine(_dir, SnapshotFile.NameOf(1));

        SnapshotFile.Write(path, shadow, Header(tick: 1), []);

        Assert.True(SnapshotFile.TryRead(path, Zones, out _, out ShadowBuffer? loaded, out _, out string? error), error);

        ReadOnlySpan<RecentEvent> items = loaded!.Recent[3].AsSpan();

        Assert.Equal(2, items.Length);
        Assert.Contains(hit, items.ToArray());
        Assert.Contains(seen, items.ToArray());
    }

    [Fact]
    public void CorruptCrc_IsRejected()
    {
        var shadow = new ShadowBuffer(Npcs, Stride, Zones);
        string path = Path.Combine(_dir, SnapshotFile.NameOf(2));

        SnapshotFile.Write(path, shadow, Header(tick: 2), []);

        byte[] bytes = File.ReadAllBytes(path);

        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.False(SnapshotFile.TryRead(path, Zones, out _, out _, out _, out string? error));
        Assert.Contains("CRC", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void ListNewestFirst_OrdersByTick()
    {
        var shadow = new ShadowBuffer(Npcs, Stride, Zones);

        foreach (long tick in new long[] { 10, 200, 3000 })
        {
            SnapshotFile.Write(Path.Combine(_dir, SnapshotFile.NameOf(tick)), shadow, Header(tick), []);
        }

        IReadOnlyList<string> files = SnapshotFile.ListNewestFirst(_dir);

        Assert.Equal(3, files.Count);
        Assert.Equal(SnapshotFile.NameOf(3000), Path.GetFileName(files[0]));
        Assert.Equal(SnapshotFile.NameOf(10), Path.GetFileName(files[2]));
    }

    [Fact]
    public void ReissueWaitingSteps_TurnsWaitingIntoReady()
    {
        NpcStore store = Empty();

        store.StepStatus[0] = (byte)StepStatus.Waiting;
        store.StepIssuedTick[0] = 999;
        store.StepStatus[1] = (byte)StepStatus.Completed;

        int reissued = store.ReissueWaitingSteps();

        Assert.Equal(1, reissued);
        Assert.Equal((byte)StepStatus.Ready, store.StepStatus[0]);
        Assert.Equal(0, store.StepIssuedTick[0]);

        // 다른 상태는 건드리지 않는다.
        Assert.Equal((byte)StepStatus.Completed, store.StepStatus[1]);
    }

    [Fact]
    public void CorrelationJump_SkipsPastInFlightIds()
    {
        var table = new CorrelationTable(4);

        for (int i = 0; i < 10; i++)
        {
            table.Next(0);
        }

        uint snapshotted = table.NextId;

        table.JumpTo(snapshotted, gap: 1_000);

        CorrelationId next = table.Next(0);

        // 크래시 전에 나가 있던 ID 와 겹치지 않는다.
        Assert.True(next.Value >= snapshotted + 1_000);
    }

    [Fact]
    public void Port_CopiesOnlyWhenRequested()
    {
        NpcStore store = Populated(seed: 5);
        MasterDataSet data = MasterDataLoader.Load(TestPaths.MasterData);

        var port = new SnapshotPort
        {
            Buffer = new ShadowBuffer(Npcs, Stride, Zones),
            Store = store,
            Zones = new ZoneStateTable(data),
            Correlations = new CorrelationTable(Npcs),
        };

        // 요청이 없으면 복사하지 않는다.
        Assert.False(port.TryCapture(tick: 1, syncedTick: 1));
        Assert.Equal(0, port.ReadyTick);

        port.Request();

        Assert.True(port.TryCapture(tick: 2, syncedTick: 2));
        Assert.Equal(2, port.ReadyTick);
        Assert.Equal(2, port.Buffer.Tick);

        // 사본이 아직 소비되지 않았으면 덮어쓰지 않는다 — 쓰기 스레드가 읽는 중일 수 있다.
        port.Request();
        Assert.False(port.TryCapture(tick: 3, syncedTick: 3));
        Assert.Equal(2, port.ReadyTick);

        port.Release();
        Assert.True(port.TryCapture(tick: 4, syncedTick: 4));
        Assert.Equal(4, port.ReadyTick);
    }

    private static SnapshotHeader Header(long tick) => new(
        SnapshotFile.FormatVersion, tick, tick, Npcs, Stride, Zones, 42,
        "abcd1234", "ef567890", "0f0f0f0f");

    private static NpcStore Empty()
    {
        var store = new NpcStore();

        store.Allocate(Npcs, Stride);
        return store;
    }

    private static NpcStore Populated(int seed)
    {
        NpcStore store = Empty();

        for (int i = 0; i < Npcs; i++)
        {
            uint h = (uint)HashCode.Combine(seed, i);

            store.Flags[i] = (WorldFlags)(h & 0xFFFF);
            store.PlanId[i] = (int)(h % 97);
            store.StepIndex[i] = (byte)(h % 7);
            store.StepStatus[i] = (byte)(h % 6);
            store.StepIssuedTick[i] = h % 5000;
            store.Lod[i] = (byte)(h % 4);
            store.Pos[i] = new WorldPos(h % 100, (h / 3) % 100, (h / 7) % 100);
            store.Hp[i] = (short)(h % 100);
            store.Stamina[i] = (short)((h / 2) % 100);
            store.ZoneCode[i] = (ushort)(h % Zones);
            store.ArchetypeCode[i] = (ushort)(h % TestPaths.ArchetypeCount);
            store.CurrentPoi[i] = (ushort)(h % 500);
            store.HomePoi[i] = (ushort)(h % 300);
            store.WorkPoi[i] = (ushort)(h % 200);
            store.PlanAssignedTick[i] = h % 900;
            store.PendingUrgency[i] = (byte)(h % 101);
            store.LastEventSequence[i] = h % 10_000;
            store.LastFailReason[i] = (byte)(h % 5);
            store.StepRetries[i] = (byte)(h % 3);

            Span<int> inventory = store.InventoryOf(i);

            for (int slot = 0; slot < inventory.Length; slot++)
            {
                inventory[slot] = (int)((h + (uint)slot) % 9);
            }
        }

        return store;
    }
}
