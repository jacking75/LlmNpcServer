using Npc.Host;
using Npc.Host.Persistence;

namespace Npc.Tests.Persistence;

/// <summary>
/// A-01 완료 조건 — 프로세스를 다시 띄웠을 때 NPC 가 같은 자리에서 같은 스텝을 이어 간다.
///
/// 실제 <c>kill</c> 대신 호스트를 두 번 조립한다. 두 번째 조립이 스냅샷을 읽어 상태를 얹으면
/// "재기동 복구" 의 코드 경로는 전부 지난 것이다 — 남은 것은 프로세스 수명뿐이고 그것은 G-02 가 잰다.
/// </summary>
public sealed class SnapshotHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "npc-host-snap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task Restart_ContinuesFromSnapshot()
    {
        HostOptions options = Options();

        long snapshotTick;
        ulong atSnapshot;

        // 무제한으로 돌리다가 도중에 한 장 뜬다. --days 1 회차는 --max-speed 에서 1초도 안 걸려
        // 주기 스냅샷이 뜰 창이 없다 — "동작 중에" 뜨는 것이 이 기능의 전부이므로 그 창을 만든다.
        using var run = new CancellationTokenSource();

        await using (NpcHost first = NpcHost.Create(options with { Days = 0 }, TextWriter.Null))
        {
            Task loop = first.RunAsync(run.Token);

            while (first.Loop.TicksProcessed < 200)
            {
                await Task.Delay(10, CancellationToken.None);
            }

            SnapshotWriteResult written = await first.Snapshots!.CaptureNowAsync(
                TimeSpan.FromSeconds(10), CancellationToken.None);

            Assert.Null(written.Error);
            Assert.True(written.Bytes > 0);

            snapshotTick = written.Tick;
            atSnapshot = 0;

            await run.CancelAsync();

            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // 취소로 끝난다. 정상이다.
            }
        }

        Assert.True(snapshotTick > 0, "스냅샷 틱이 0 이다");
        _ = atSnapshot;

        await using NpcHost second = NpcHost.Create(options with { Days = 0 }, TextWriter.Null);

        Assert.True(second.Restore.Restored, second.RestoreDetail);
        Assert.Equal(snapshotTick, second.Restore.Tick);

        // 시계까지 이어진다 — 재기동해도 게임 시각이 새벽 6시로 돌아가지 않는다.
        Assert.Equal(snapshotTick, second.Clock.Current.Value);

        // 진행 중이던 스텝은 재발행 대기로 돌아간다 (N7 — MoveTo 재발행은 멱등이다).
        Assert.DoesNotContain(
            second.Store.StepStatus.Take(second.Npcs),
            status => status == (byte)Npc.Runtime.StepStatus.Waiting);
    }

    [Fact]
    public async Task Restore_None_SeedsFromScratch()
    {
        HostOptions options = Options() with { Restore = RestoreMode.None };

        await using NpcHost host = NpcHost.Create(options, TextWriter.Null);

        Assert.False(host.Restore.Restored);
        Assert.Contains("--restore none", host.RestoreDetail, StringComparison.Ordinal);
    }

    private HostOptions Options()
    {
        Assert.True(
            HostOptions.TryParse(
                [
                    "--loopback", "--npcs", "60", "--time-scale", "600", "--days", "1",
                    "--max-speed", "--no-dashboard",
                    "--snapshot-dir", _dir, "--snapshot-interval-s", "1",
                ],
                out HostOptions options,
                out string? error),
            error);

        return options with { MasterData = TestPaths.MasterData };
    }
}
