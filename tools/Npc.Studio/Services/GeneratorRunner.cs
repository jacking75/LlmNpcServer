using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Npc.MasterData.Authoring;

namespace Npc.Studio.Services;

/// <summary>
/// 파생물 재생성기 (T17 · H16). <c>tools/gen_npcs.cs</c>·<c>gen_poi_distances.cs</c> 를 돌린다.
///
/// <b>생성물은 읽기 전용이다.</b> 손으로 고치는 길을 열지 않고, 다시 만드는 버튼만 준다 —
/// 낡은 거리표로 돌면 검증도 통과하고 기동도 되지만 NPC 가 없는 POI 로 걸어간다.
///
/// <para>
/// <b>저장소 안에서만 돈다.</b> 생성기는 저장소의 <c>tools/</c> 에 있고, 저장소 밖에서
/// 열린 마스터데이터에는 그 파일이 없다 — 그때는 명령줄을 알려 준다.
/// </para>
/// </summary>
public sealed class GeneratorRunner(StudioOptions options) : IDisposable
{
    /// <summary>한 번 실행의 상한. 빌드가 길어도 10분이면 무언가 잘못된 것이다.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _output = new();
    private CancellationTokenSource? _cancel;

    /// <summary>생성기가 있는 저장소 루트. 못 찾으면 null.</summary>
    public string? RepositoryRoot { get; } = FindRepository(options.MasterData);

    /// <summary>재생성 버튼을 켤 수 있는가.</summary>
    public bool Available => RepositoryRoot is not null;

    /// <summary>지금 돌고 있는가.</summary>
    public bool Busy { get; private set; }

    /// <summary>마지막 실행의 stdout·stderr.</summary>
    public string Output
    {
        get { lock (_output) { return _output.ToString(); } }
    }

    /// <summary>마지막 실행이 성공했는가.</summary>
    public bool LastSucceeded { get; private set; }

    /// <summary>출력·상태가 바뀌면 부른다. 화면이 진행을 흘려 보여 준다 (H16).</summary>
    public event Action? Changed;

    /// <summary>이 파생물을 만들 정확한 명령. 확인 모달이 그대로 보여 준다.</summary>
    /// <param name="status">파생물.</param>
    public string CommandOf(DerivedStatus status) => $"dotnet run {status.Generator}";

    /// <summary>
    /// 이 디렉터리에 대고 생성기를 돌릴 수 있는가.
    ///
    /// <b>생성기는 저장소의 <c>masterdata/</c> 를 읽는다</b> — 입력 디렉터리 인자가 없다
    /// (<c>gen_npcs.cs</c> 는 <c>--out</c> 만, <c>gen_poi_distances.cs</c> 는 인자가 없다).
    /// 연습장에 대고 돌리면 <b>원본을 읽어 연습장에 쓴다</b> — 연습장에서 고친 <c>pois.json</c> 이
    /// 반영되지 않은 거리표가 나오고, 그것은 조용히 틀린 파일이다. 그래서 막는다.
    /// </summary>
    /// <param name="masterData">대상 폴더.</param>
    public bool CanRunFor(string masterData) =>
        RepositoryRoot is { } root
        && string.Equals(
            Path.GetFullPath(masterData).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(Path.Combine(root, "masterdata")).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>돌고 있는 생성기를 멈춘다. 자식 프로세스까지 같이 죽인다.</summary>
    public void Cancel()
    {
        try { _cancel?.Cancel(); }
        catch (ObjectDisposedException) { /* 이미 끝났다. */ }
    }

    /// <summary>
    /// 생성기를 돌린다. 한 번에 하나만 — 두 생성기가 같은 잠금 파일을 동시에 쓰면 기록이 깨진다.
    /// </summary>
    /// <param name="status">다시 만들 파생물.</param>
    /// <param name="masterData">대상 마스터데이터 경로. 저장소의 <c>masterdata/</c> 여야 한다.</param>
    public async Task RunAsync(DerivedStatus status, string masterData)
    {
        if (RepositoryRoot is not { } root)
        {
            throw new InvalidOperationException("생성기를 찾지 못했다 — 저장소 안에서 실행해야 한다.");
        }

        if (options.ReadOnly)
        {
            throw new InvalidOperationException("읽기 전용 모드에서는 파생물을 다시 만들 수 없다.");
        }

        if (!CanRunFor(masterData))
        {
            throw new InvalidOperationException(
                "생성기는 저장소의 masterdata/ 를 읽는다. 연습장에서는 다시 만들 수 없다 — 원본에 적용한 뒤 만든다.");
        }

        // 두 탭에서 누르면 예전에는 줄을 서서 두 번 돌았다. 지금은 두 번째가 거절된다.
        if (!_gate.Wait(0))
        {
            throw new InvalidOperationException("이미 생성기가 돌고 있다 — 끝나면 다시 누른다.");
        }

        using var cancel = new CancellationTokenSource(Timeout);
        _cancel = cancel;

        try
        {
            Busy = true;
            LastSucceeded = false;

            lock (_output)
            {
                _output.Clear();
                _output.Append("$ ").Append(CommandOf(status)).AppendLine();
            }

            Changed?.Invoke();

            var info = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            info.ArgumentList.Add("run");
            info.ArgumentList.Add(status.Generator);

            using Process process = Start(info);

            // stdout 을 다 읽은 <b>뒤</b> stderr 를 읽으면 파이프 버퍼가 차는 순간 교착한다 —
            // 빌드 오류가 길면 정확히 그 일이 났다. 둘을 동시에 읽는다.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancel.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancel.Token);

            try
            {
                await process.WaitForExitAsync(cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);

                lock (_output)
                {
                    _output.AppendLine().Append("취소했다.");
                }

                throw new InvalidOperationException(
                    $"생성기를 멈췄다 (상한 {Timeout.TotalMinutes:0}분). 터미널에서 직접 돌리면 진행을 볼 수 있다: {CommandOf(status)}");
            }

            string[] streams = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

            lock (_output)
            {
                _output.Append(streams[0]);

                if (streams[1].Length > 0)
                {
                    _output.AppendLine().Append(streams[1]);
                }
            }

            LastSucceeded = process.ExitCode == 0;
        }
        finally
        {
            Busy = false;
            _cancel = null;
            _ = _gate.Release();
            Changed?.Invoke();
        }
    }

    private static Process Start(ProcessStartInfo info)
    {
        try
        {
            return Process.Start(info)
                ?? throw new InvalidOperationException("생성기를 시작하지 못했다.");
        }
        catch (Win32Exception ex)
        {
            // PATH 에 dotnet 이 없으면 여기서 난다 — 예전에는 그대로 올라가 회로가 죽었다.
            throw new InvalidOperationException(
                "`dotnet` 을 찾지 못했다 — PATH 를 확인한다. 명령을 터미널에서 직접 돌릴 수 있다.", ex);
        }
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // 이미 끝났다.
        }
    }

    /// <summary><c>NpcServer.sln</c> 과 <c>tools/gen_npcs.cs</c> 가 둘 다 있는 폴더.</summary>
    private static string? FindRepository(string masterData)
    {
        DirectoryInfo? directory = new(masterData);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NpcServer.sln"))
                && File.Exists(Path.Combine(directory.FullName, "tools", "gen_npcs.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>세마포어를 놓는다.</summary>
    public void Dispose()
    {
        _cancel?.Dispose();
        _gate.Dispose();
    }
}
