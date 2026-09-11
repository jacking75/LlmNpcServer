namespace Npc.Host.Reload;

/// <summary>
/// <c>--watch</c> — 파일이 바뀌면 리로드한다 (A-07).
///
/// <para>
/// <b>개발용이다.</b> 운영에서는 <c>POST /admin/reload</c> 로 사람이 부른다. 이유는 경쟁이다 —
/// 파일 하나가 반쯤 쓰인 순간에도 <see cref="FileSystemWatcher"/> 는 깨어나고, 프리베이크가
/// 2,880개를 쏟아내는 동안에는 수천 번 깨어난다. 운영에서 그 경쟁을 감수할 이유가 없다.
/// </para>
///
/// <para>
/// <b>디바운스가 전부다.</b> 마지막 변경 뒤 <see cref="QuietMillis"/> 동안 조용해야 한 번 돈다.
/// 실패해도 <see cref="ReloadService"/> 가 트랜잭션이라 현 상태 그대로이므로, 반쯤 쓰인 파일을
/// 읽는 최악의 경우도 "로그가 시끄럽다" 로 끝난다.
/// </para>
/// </summary>
public sealed class ReloadWatcher : IDisposable
{
    /// <summary>마지막 변경 뒤 이만큼 조용하면 돈다(ms).</summary>
    public const int QuietMillis = 1_500;

    private readonly ReloadService _service;
    private readonly TextWriter _log;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Lock _gate = new();
    private readonly Timer _debounce;

    private ReloadScope _pending = ReloadScope.PlanStore;
    private bool _armed;
    private bool _disposed;

    /// <summary>감시를 시작한다.</summary>
    /// <param name="service">리로드 서비스.</param>
    /// <param name="planStoreRoot"><c>planstore/</c>. 없으면 건너뛴다.</param>
    /// <param name="masterDataDirectory"><c>masterdata/</c>. 없으면 건너뛴다.</param>
    /// <param name="log">로그.</param>
    public ReloadWatcher(
        ReloadService service, string planStoreRoot, string masterDataDirectory, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(log);

        _service = service;
        _log = log;
        _debounce = new Timer(OnQuiet, state: null, Timeout.Infinite, Timeout.Infinite);

        Add(planStoreRoot, "*.json", ReloadScope.PlanStore);
        Add(masterDataDirectory, "*.json", ReloadScope.Content);
    }

    /// <summary>감시 중인 경로 수. 0 이면 아무것도 안 본다.</summary>
    public int WatchedPaths => _watchers.Count;

    /// <summary>깨어난 횟수. 디바운스 <b>전</b> 이라 리로드 수보다 훨씬 크다.</summary>
    public long Signals { get; private set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _debounce.Dispose();

        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
    }

    private void Add(string directory, string filter, ReloadScope scope)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var watcher = new FileSystemWatcher(directory, filter)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };

        watcher.Changed += (_, _) => Signal(scope);
        watcher.Created += (_, _) => Signal(scope);
        watcher.Deleted += (_, _) => Signal(scope);
        watcher.Renamed += (_, _) => Signal(scope);

        // 버퍼가 넘치면 이벤트가 통째로 사라진다. 그때는 "무엇이" 를 모르므로 넓은 쪽으로 돈다.
        watcher.Error += (_, _) => Signal(ReloadScope.Content);

        watcher.EnableRaisingEvents = true;

        _watchers.Add(watcher);
    }

    private void Signal(ReloadScope scope)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Signals++;

            // 둘이 같이 깨면 넓은 쪽(Content)으로 돈다 — 좁은 쪽만 돌면 인터럽트가 안 올라간다.
            _pending = _armed && _pending > scope ? _pending : scope;
            _armed = true;

            _debounce.Change(QuietMillis, Timeout.Infinite);
        }
    }

    private void OnQuiet(object? _)
    {
        ReloadScope scope;

        lock (_gate)
        {
            if (_disposed || !_armed)
            {
                return;
            }

            scope = _pending;
            _armed = false;
            _pending = ReloadScope.PlanStore;
        }

        ReloadResult result = _service.Reload(scope);

        _log.WriteLine($"reload({scope}): {(result.Ok ? "ok" : "거절")} — {result.Detail}");
    }
}
