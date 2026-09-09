using Npc.Core.Plan;
using Npc.Planning;
using Npc.Runtime;

namespace Npc.Host.Persistence;

/// <summary>스냅샷 쓰기 결과 한 건. 계측·경보가 읽는다.</summary>
/// <param name="Tick">쓴 스냅샷의 게임 틱.</param>
/// <param name="Bytes">파일 크기.</param>
/// <param name="ElapsedMs">쓰는 데 걸린 밀리초.</param>
/// <param name="Error">실패 사유. 성공이면 null.</param>
public readonly record struct SnapshotWriteResult(long Tick, long Bytes, double ElapsedMs, string? Error);

/// <summary>
/// 주기 스냅샷 쓰기 (A-01).
///
/// <b>틱 루프는 복사만 한다.</b> 여기가 그림자 버퍼를 파일로 옮기는 유일한 곳이고,
/// 틱 루프와는 <see cref="SnapshotPort"/> 의 <c>ReadyTick</c> 하나로만 만난다 — 락이 없다.
///
/// 실패해도 던지지 않는다. 스냅샷을 못 썼다고 서버를 멈추는 것은 과잉이다 —
/// 경보를 올리고 다음 주기에 다시 시도한다. 상태 손실 창이 늘어난 것을 아는 것이 중요하다.
/// </summary>
public sealed class SnapshotWriter : IAsyncDisposable
{
    private readonly SnapshotPort _port;
    private readonly string _dir;
    private readonly int _keep;
    private readonly Func<SnapshotHeader> _header;
    private readonly Func<IReadOnlyList<IndividualPlanRecord>> _individuals;
    private readonly Action<SnapshotWriteResult> _report;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>쓰기를 조립한다. 기동 시 1회.</summary>
    /// <param name="port">틱 루프와의 통로.</param>
    /// <param name="dir">스냅샷 디렉터리.</param>
    /// <param name="intervalSeconds">주기(초). 이 값이 상태 손실 창의 상한이다.</param>
    /// <param name="keep">보존할 파일 수.</param>
    /// <param name="header">머리를 만드는 함수. 해시는 기동 시 정해지므로 매번 같다.</param>
    /// <param name="individuals">개별 플랜을 뽑는 함수.</param>
    /// <param name="report">결과 보고. 계측·경보가 받는다.</param>
    public SnapshotWriter(
        SnapshotPort port,
        string dir,
        int intervalSeconds,
        int keep,
        Func<SnapshotHeader> header,
        Func<IReadOnlyList<IndividualPlanRecord>> individuals,
        Action<SnapshotWriteResult> report)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(individuals);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keep);

        _port = port;
        _dir = dir;
        _keep = keep;
        _header = header;
        _individuals = individuals;
        _report = report;
        _interval = TimeSpan.FromSeconds(intervalSeconds);
    }

    /// <summary>마지막으로 성공한 스냅샷의 틱. 0 이면 아직 없다.</summary>
    public long LastTick { get; private set; }

    /// <summary>실패 누계. 0 이 아니면 상태 손실 창이 주기보다 크다.</summary>
    public long Failures { get; private set; }

    /// <summary>주기 루프를 시작한다.</summary>
    public void Start()
    {
        Directory.CreateDirectory(_dir);

        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <summary>
    /// 지금 한 장 쓴다. 정상 종료(A-02)와 <c>POST /admin/snapshot</c>(A-11)이 부른다.
    ///
    /// 틱 루프가 돌고 있어야 사본이 나온다 — 이미 멈췄으면 <paramref name="timeout"/> 뒤에
    /// 포기하고 실패를 보고한다.
    /// </summary>
    public async Task<SnapshotWriteResult> CaptureNowAsync(TimeSpan timeout, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);

        _port.Request();

        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;

        while (_port.ReadyTick == 0)
        {
            if (Environment.TickCount64 > deadline || ct.IsCancellationRequested)
            {
                var timedOut = new SnapshotWriteResult(0, 0, 0, "틱 루프가 사본을 만들지 않았다 (타임아웃)");

                Failures++;
                _report(timedOut);
                return timedOut;
            }

            await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
        }

        return WriteReady();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);

        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 정상 종료다.
            }
        }

        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await CaptureNowAsync(_interval, ct).ConfigureAwait(false);
        }
    }

    private SnapshotWriteResult WriteReady()
    {
        long tick = _port.ReadyTick;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        string path = Path.Combine(_dir, SnapshotFile.NameOf(tick));

        SnapshotWriteResult result;

        try
        {
            long bytes = SnapshotFile.Write(path, _port.Buffer, _header(), _individuals());
            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            LastTick = tick;
            result = new SnapshotWriteResult(tick, bytes, elapsed, null);

            Prune();
        }
        catch (IOException e)
        {
            Failures++;
            result = new SnapshotWriteResult(tick, 0, 0, e.Message);
        }
        catch (UnauthorizedAccessException e)
        {
            Failures++;
            result = new SnapshotWriteResult(tick, 0, 0, e.Message);
        }
        finally
        {
            // 사본을 놓아 준다. 실패해도 놓아야 다음 주기에 새 사본이 온다.
            _port.Release();
        }

        _report(result);
        return result;
    }

    private void Prune()
    {
        IReadOnlyList<string> files = SnapshotFile.ListNewestFirst(_dir);

        for (int i = _keep; i < files.Count; i++)
        {
            try
            {
                File.Delete(files[i]);
            }
            catch (IOException)
            {
                // 지우지 못한 낡은 스냅샷은 다음 주기에 다시 시도한다. 실패가 아니다.
            }
        }
    }
}

/// <summary>
/// 개별 플랜을 스냅샷용 레코드로 뽑는다 (A-01).
///
/// 버킷 플랜의 <c>PlanId</c> 는 스토어 첨자라 플랜 스토어가 같으면 그대로 복원된다.
/// 개별 플랜(음수 id)만 원본 JSON 으로 담고 복원 시 다시 컴파일한다.
/// </summary>
public static class IndividualPlanCapture
{
    /// <summary>지금 살아 있는 개별 플랜을 전부 뽑는다.</summary>
    public static IReadOnlyList<IndividualPlanRecord> From(NpcStore store, IndividualPlanPool pool, long tick)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(pool);

        var records = new List<IndividualPlanRecord>();

        for (int npc = 0; npc < store.Count; npc++)
        {
            int planId = store.PlanId[npc];

            if (!IndividualPlanPool.IsIndividual(planId))
            {
                continue;
            }

            if (!pool.TryGet(planId, npc, tick, out CompiledPlan plan) || plan.SourceJson.Length == 0)
            {
                continue;
            }

            records.Add(new IndividualPlanRecord(npc, plan.Bucket.ToIndex(), plan.SourceJson));
        }

        return records;
    }
}
