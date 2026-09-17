using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.MasterData.Authoring;
using Npc.MasterData.Validation;
using Npc.Narrative;

namespace Npc.Studio.Services;

/// <summary>
/// Studio의 파일 경계다. 화면은 JSON 파일을 직접 쓰지 않고 이 서비스를 통해 검증된 변경만 저장한다.
/// </summary>
public sealed class StudioWorkspace(StudioOptions options)
{
    /// <summary>
    /// Studio 가 원문 편집을 허용하는 파일 (이름 오름차순). <b>생성물은 여기 없다.</b>
    ///
    /// <b>목록은 한 곳에만 있다</b> (H28) — 화면이 같은 목록을 따로 들고 있으면
    /// 파일을 하나 더할 때 한쪽만 고쳐지는 날이 온다.
    /// </summary>
    public static ImmutableArray<string> EditableFiles { get; } =
    [
        "actions.json", "archetypes.json", "context_buckets.json", "dialogue_lines.json",
        "factions.json", "fallback_plans.json", "interrupts.json", "items.json",
        "npc_overrides.json", "pois.json", "world_flags.json", "zones.json",
    ];

    private static readonly ImmutableHashSet<string> s_editableFiles =
        [.. EditableFiles];

    private readonly object _gate = new();
    private static readonly JsonSerializerOptions s_indentedJson = new(JsonSurgeonText.Options)
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 마스터데이터 로드 캐시 (H25). 키는 <c>디렉터리 + 편집 파일 해시 합</c> 이다 —
    /// <b>슬라이더 한 번에 5,000명 명단을 다시 파싱하면 화면이 "죽은 것" 으로 보인다.</b>
    /// </summary>
    private string _cacheKey = string.Empty;
    private MasterDataSet? _cacheData;
    private NpcInstanceTable? _cacheInstances;
    private bool _cacheInstancesLoaded;

    /// <summary>캐시를 건너뛴 로드 횟수. 테스트·수동 확인이 본다 (H25).</summary>
    public int LoadCount { get; private set; }

    /// <summary>
    /// 보고 있는 디렉터리가 바뀌었다 (H04). <b>워크스페이스는 싱글턴이라 전역이다</b> —
    /// 탭 A 가 연습장을 열면 탭 B 의 저장도 그리로 간다. 그래서 알린다.
    /// </summary>
    public event Action? DirectoryChanged;

    /// <summary>밖에서 파일이 바뀌었다 (H13). 인자는 파일 이름이다.</summary>
    public event Action<string>? ExternalChange;

    /// <summary>생성기가 도는 동안은 저장을 거절한다 (H13·H16). 밖에서 세운다.</summary>
    public bool GeneratorBusy { get; set; }

    private FileSystemWatcher? _watcher;

    /// <summary>
    /// 밖의 편집(VS Code · 다른 탭 · 생성기)을 지켜본다 (H13).
    ///
    /// <b>이것이 없어도 저장은 안전하다</b> — 지문 검사가 마지막 방어선이다.
    /// 감시는 <b>알려 주는 것</b>이 목적이다: 거절당하고 나서야 아는 것보다 낫다.
    /// </summary>
    public void Watch()
    {
        lock (_gate)
        {
            _watcher?.Dispose();

            try
            {
                var watcher = new FileSystemWatcher(_directory, "*.json")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };

                watcher.Changed += OnFileEvent;
                watcher.Created += OnFileEvent;
                watcher.Deleted += OnFileEvent;
                watcher.Renamed += (_, e) => OnExternal(e.Name ?? string.Empty);
                watcher.EnableRaisingEvents = true;

                _watcher = watcher;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or PlatformNotSupportedException)
            {
                // 감시를 못 걸어도 저장 경로의 지문 검사가 남는다.
                _watcher = null;
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => OnExternal(e.Name ?? string.Empty);

    private void OnExternal(string name)
    {
        if (name.Length == 0 || name.EndsWith(".studio.tmp", StringComparison.Ordinal))
        {
            return;
        }

        // 우리가 방금 쓴 것도 여기로 들어온다. 캐시만 비우고 알린다 —
        // 화면은 배너를 띄울 뿐이고, 진짜 판정은 저장할 때의 지문이 한다.
        lock (_gate)
        {
            InvalidateCache();
        }

        ExternalChange?.Invoke(name.Replace('\\', '/'));
    }

    /// <summary>
    /// 지금 보고 있는 디렉터리 (T30). 원본이거나 연습장이다.
    /// <b><c>_gate</c> 안에서만 바꾼다</b> — 읽는 중에 바뀌면 절반은 원본, 절반은 연습장을 읽는다.
    /// </summary>
    private string _directory = options.MasterData;

    /// <summary>연습장 이름. 원본을 보고 있으면 빈 문자열이다.</summary>
    private string _sandbox = string.Empty;

    /// <summary>연습장 이름 (T30).</summary>
    public string SandboxName
    {
        get { lock (_gate) { return _sandbox; } }
    }

    /// <summary>지금 읽고 쓰는 디렉터리. 연습장이면 연습장 경로다.</summary>
    public string CurrentDirectory
    {
        get { lock (_gate) { return _directory; } }
    }

    /// <summary>원본 마스터데이터 경로. 연습장에서도 바뀌지 않는다.</summary>
    public string OriginDirectory => options.MasterData;

    /// <summary>
    /// 연습장을 연다 (T30). 원본을 통째로 복사하고 이후 저장은 전부 그 사본으로 간다.
    ///
    /// <b>초보자가 손대지 못하는 가장 큰 이유가 "망가뜨릴까 봐" 다.</b> 실습서는
    /// <c>lab/&lt;이름&gt;/masterdata</c> 사본을 쓰라고 하는데 Studio 에는 그 개념이 없었다.
    /// </summary>
    /// <param name="name">연습장 이름. 파일 이름에 쓸 수 있는 글자만.</param>
    /// <exception cref="SandboxExistsException">같은 이름의 연습장이 이미 있을 때.</exception>
    public string OpenSandbox(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureWritable();

        string folder = SandboxFolder(name);

        lock (_gate)
        {
            if (Directory.Exists(Path.Combine(folder, "masterdata")))
            {
                // 회귀 — 기본 이름이 `MMdd` 라 같은 날 두 번 누르면 아침 작업이 조용히 지워졌다.
                throw new SandboxExistsException(name, ChangedIn(Path.Combine(folder, "masterdata")));
            }

            return CreateSandbox(name, folder);
        }
    }

    /// <summary>같은 이름의 연습장을 버리고 새로 만든다 (H04). 화면이 확인을 받은 뒤에만 부른다.</summary>
    /// <param name="name">연습장 이름.</param>
    public string RecreateSandbox(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureWritable();

        string folder = SandboxFolder(name);

        lock (_gate)
        {
            if (Directory.Exists(folder))
            {
                MoveToTrash(folder);
            }

            return CreateSandbox(name, folder);
        }
    }

    /// <summary>이미 있는 연습장을 이어서 연다 (H04).</summary>
    /// <param name="name">연습장 이름.</param>
    public string ResumeSandbox(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        string target = Path.Combine(SandboxFolder(name), "masterdata");

        if (!Directory.Exists(target))
        {
            throw new InvalidDataException($"연습장 '{name}' 이 없다.");
        }

        lock (_gate)
        {
            _directory = target;
            _sandbox = name;
            InvalidateCache();
        }

        DirectoryChanged?.Invoke();

        return target;
    }

    /// <summary>있는 연습장 목록 (최신 순).</summary>
    public ImmutableArray<StudioSandbox> Sandboxes()
    {
        string root = SandboxRoot();

        if (!Directory.Exists(root))
        {
            return [];
        }

        var list = ImmutableArray.CreateBuilder<StudioSandbox>();

        foreach (string folder in Directory.EnumerateDirectories(root, "studio-*"))
        {
            string masterData = Path.Combine(folder, "masterdata");

            if (!Directory.Exists(masterData))
            {
                continue;
            }

            list.Add(new StudioSandbox(
                DisplayNameOf(folder),
                masterData,
                Directory.GetCreationTime(folder),
                ChangedIn(masterData).Length));
        }

        return [.. list.OrderByDescending(item => item.CreatedAt)];
    }

    private string CreateSandbox(string name, string folder)
    {
        string target = Path.Combine(folder, "masterdata");

        Directory.CreateDirectory(target);
        CopyFrom(options.MasterData, target);
        WriteOriginLock(folder);
        File.WriteAllText(Path.Combine(folder, "name.txt"), name.Trim(), new UTF8Encoding(false));

        _directory = target;
        _sandbox = name.Trim();
        InvalidateCache();

        DirectoryChanged?.Invoke();

        return target;
    }

    /// <summary>
    /// 연습장 폴더 이름. <b>한글 이름도 받는다</b> — 그대로 치환하면 전부 <c>-</c> 가 되어
    /// <c>studio----</c> 하나로 뭉치고, 서로 다른 연습장이 같은 폴더를 쓴다.
    /// 짧은 해시를 붙여 충돌을 없애고 화면에는 원래 이름을 보여 준다.
    /// </summary>
    private string SandboxFolder(string name)
    {
        string trimmed = name.Trim();
        string safe = new([.. trimmed.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);
        bool plain = trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        string suffix = plain
            ? string.Empty
            : "-" + Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)), 0, 4);

        return Path.Combine(SandboxRoot(), "studio-" + safe + suffix);
    }

    /// <summary>폴더에 적어 둔 원래 이름. 없으면 폴더 이름에서 접두만 뗀다.</summary>
    private static string DisplayNameOf(string folder)
    {
        string path = Path.Combine(folder, "name.txt");

        if (File.Exists(path))
        {
            try
            {
                return File.ReadAllText(path).Trim();
            }
            catch (IOException)
            {
                // 아래로 떨어진다.
            }
        }

        string name = Path.GetFileName(folder);

        return name.StartsWith("studio-", StringComparison.Ordinal) ? name[7..] : name;
    }

    /// <summary>연습장을 닫고 원본으로 돌아간다. 파일은 남는다.</summary>
    public void CloseSandbox()
    {
        lock (_gate)
        {
            _directory = options.MasterData;
            _sandbox = string.Empty;
            InvalidateCache();
        }

        DirectoryChanged?.Invoke();
    }

    /// <summary>
    /// 연습장을 버리고 원본으로 돌아간다.
    ///
    /// <b>지우지 않고 <c>lab/.trash/</c> 로 옮긴다</b> (H04) — 확인 모달을 눌러도 사람은
    /// 무엇이 사라지는지 다 읽지 않는다. 30일 뒤 정리가 같이 치운다.
    /// </summary>
    public void DiscardSandbox()
    {
        EnsureWritable();

        lock (_gate)
        {
            if (_sandbox.Length == 0)
            {
                return;
            }

            string folder = Path.GetDirectoryName(_directory)!;

            _directory = options.MasterData;
            _sandbox = string.Empty;
            InvalidateCache();

            if (Directory.Exists(folder))
            {
                MoveToTrash(folder);
            }
        }

        DirectoryChanged?.Invoke();
    }

    /// <summary>버린 연습장이 가는 곳. 돌려준 값은 옮긴 경로다.</summary>
    private string MoveToTrash(string folder)
    {
        string trash = Path.Combine(SandboxRoot(), ".trash");
        Directory.CreateDirectory(trash);

        string target = Path.Combine(
            trash,
            Path.GetFileName(folder) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

        for (int n = 2; Directory.Exists(target); n++)
        {
            target = string.Create(CultureInfo.InvariantCulture, $"{target}-{n}");
        }

        Directory.Move(folder, target);
        PruneTrash(trash);

        return target;
    }

    private static void PruneTrash(string trash)
    {
        DateTime cutoff = DateTime.Now.AddDays(-BackupDays);

        foreach (string folder in Directory.EnumerateDirectories(trash))
        {
            try
            {
                if (Directory.GetLastWriteTime(folder) < cutoff)
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 정리는 보조다. 못 지워도 나머지는 계속한다.
            }
        }
    }

    /// <summary>
    /// 연습장이 원본과 다른 파일 (이름 오름차순). 바이트 비교다 — 서식까지 같아야 같다고 본다.
    /// <b><c>localization/</c> 도 본다</b> (H15) — 마법사가 넣은 표시 이름이 거기 있다.
    /// </summary>
    public ImmutableArray<string> SandboxChanges()
    {
        lock (_gate)
        {
            return _sandbox.Length == 0 ? [] : ChangedIn(_directory);
        }
    }

    /// <summary>이 폴더가 원본과 다른 트랜잭션 대상 파일.</summary>
    private ImmutableArray<string> ChangedIn(string directory)
    {
        var changed = ImmutableArray.CreateBuilder<string>();

        foreach (string file in TransactionFiles(directory))
        {
            string mine = Path.Combine(directory, file);
            string origin = Path.Combine(options.MasterData, file);

            bool hasMine = File.Exists(mine);
            bool hasOrigin = File.Exists(origin);

            if (!hasMine || !hasOrigin)
            {
                // 한쪽에만 있으면 그것도 차이다 — 새 로케일 파일이 이 경우다.
                if (hasMine != hasOrigin)
                {
                    changed.Add(file);
                }

                continue;
            }

            if (!File.ReadAllBytes(mine).AsSpan().SequenceEqual(File.ReadAllBytes(origin)))
            {
                changed.Add(file);
            }
        }

        return changed.ToImmutable();
    }

    /// <summary>
    /// 비교·적용·백업의 대상 (H15). 편집 파일 + <c>localization/*.json</c> 이다.
    ///
    /// <b><see cref="EditableFiles"/> 와 다른 목록이다.</b> 로케일 파일은 원문 편집기가 열지
    /// 않지만 마법사가 쓰고, 연습장에서 원본으로도 옮겨야 한다 — 예전에는 그 파일이 조용히 버려졌다.
    /// </summary>
    private static ImmutableArray<string> TransactionFiles(string directory)
    {
        var files = ImmutableArray.CreateBuilder<string>(EditableFiles.Length + 4);

        files.AddRange(EditableFiles);
        files.AddRange(Locales(directory));

        return [.. files.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)];
    }

    /// <summary>이 폴더의 로케일 파일 상대 경로 (<c>localization/ko-KR.json</c>).</summary>
    private static IEnumerable<string> Locales(string directory)
    {
        string folder = Path.Combine(directory, LocalizationTable.FolderName);

        if (!Directory.Exists(folder))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(folder, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return LocalizationTable.FolderName + "/" + Path.GetFileName(path);
        }
    }

    /// <summary>연습장을 만들 때의 원본 해시. "그 뒤에 원본이 바뀌었나" 를 이것으로 본다.</summary>
    private void WriteOriginLock(string folder)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string file in TransactionFiles(options.MasterData))
        {
            string path = Path.Combine(options.MasterData, file);

            if (File.Exists(path))
            {
                map[file] = Sha(path);
            }
        }

        File.WriteAllText(
            Path.Combine(folder, "origin.lock.json"),
            JsonSerializer.Serialize(map, s_indentedJson),
            new UTF8Encoding(false));
    }

    /// <summary>연습장을 만든 뒤 <b>원본이</b> 바뀐 파일 (H04). 적용하면 그 변경이 덮인다.</summary>
    public ImmutableArray<string> OriginChangedSinceOpen()
    {
        lock (_gate)
        {
            if (_sandbox.Length == 0)
            {
                return [];
            }

            string path = Path.Combine(Path.GetDirectoryName(_directory)!, "origin.lock.json");

            if (!File.Exists(path))
            {
                return [];
            }

            Dictionary<string, string>? locked;

            try
            {
                locked = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            }
            catch (JsonException)
            {
                return [];
            }

            if (locked is null)
            {
                return [];
            }

            var changed = ImmutableArray.CreateBuilder<string>();

            foreach ((string file, string sha) in locked.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string origin = Path.Combine(options.MasterData, file);

                if (!File.Exists(origin) || !string.Equals(Sha(origin), sha, StringComparison.Ordinal))
                {
                    changed.Add(file);
                }
            }

            return changed.ToImmutable();
        }
    }

    /// <summary>
    /// 연습장에서 바뀐 파일을 원본에 옮긴다. <b>원본에서 다시 검증한다</b> —
    /// 연습장에서 통과한 것이 원본에서도 통과한다는 보장은 없다 (생성물이 다를 수 있다).
    ///
    /// <para>
    /// <b>장소가 늘었으면 로더 게이트를 끈다</b> (H07). 원본의 거리표는 연습장에서 더한
    /// 장소를 모르므로, 게이트를 켜 두면 <b>적용 자체가 영원히 막힌다</b> — 거리표를 다시
    /// 만들려면 먼저 적용해야 하는데 그 적용이 막혀 있는 것이다.
    /// </para>
    /// </summary>
    public StudioSaveResult ApplyToOrigin()
    {
        EnsureWritable();

        bool returned = false;
        StudioSaveResult result;

        lock (_gate)
        {
            if (_sandbox.Length == 0)
            {
                return new StudioSaveResult(false, "연습장이 아니다.", []);
            }

            ImmutableArray<string> changed = ChangedIn(_directory);

            if (changed.IsEmpty)
            {
                return new StudioSaveResult(false, "연습장에서 바뀐 파일이 없다.", []);
            }

            var candidates = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string file in changed)
            {
                string mine = Path.Combine(_directory, file);

                if (File.Exists(mine))
                {
                    candidates[file] = File.ReadAllText(mine);
                }
            }

            bool poisChanged = changed.Contains("pois.json", StringComparer.Ordinal);
            string sandbox = _directory;
            string name = _sandbox;

            _directory = options.MasterData;
            InvalidateCache();

            try
            {
                result = ValidateAndWrite(
                    candidates, loaderGate: !poisChanged, label: $"연습장 {name} 적용");
            }
            catch (Exception)
            {
                _directory = sandbox;
                InvalidateCache();
                throw;
            }

            if (result.Saved)
            {
                result = result with
                {
                    Message = $"연습장의 {changed.Length}개 파일을 원본에 적용했다."
                        + (poisChanged ? " 장소가 늘었다 — 거리표를 다시 만들어야 서버가 뜬다." : string.Empty),
                };

                // 적용에 성공하면 원본으로 돌아간다 — 연습장에 남아 있으면 파급 패널이
                // "연습장에서는 다시 만들 수 없다" 를 방금 적용한 사람에게 보여 준다.
                _sandbox = string.Empty;
                returned = true;
            }
            else
            {
                _directory = sandbox;
            }

            InvalidateCache();
        }

        if (returned)
        {
            DirectoryChanged?.Invoke();
        }

        return result;
    }

    /// <summary>
    /// 연습장을 둘 곳. 저장소 안이면 <c>lab/</c> (실습서와 같은 자리, gitignore 됨),
    /// 밖이면 <c>%LOCALAPPDATA%\NpcStudio\lab\</c>.
    /// </summary>
    private string SandboxRoot()
    {
        DirectoryInfo? directory = new(options.MasterData);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NpcServer.sln")))
            {
                return Path.Combine(directory.FullName, "lab");
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NpcStudio", "lab");
    }

    /// <summary>
    /// 마스터데이터와 명단을 읽어 함수에 넘긴다 (T24·T27).
    ///
    /// <b>읽기 경로를 하나로 묶는 이유가 둘이다</b> — <c>_gate</c> (연습장 전환이 디렉터리를
    /// 바꾸므로 읽는 도중에 바뀌면 절반은 원본 절반은 연습장을 읽는다) 와 <b>캐시</b>(H25):
    /// 슬라이더 한 번에 5,000명 명단을 다시 파싱하면 화면이 "죽은 것" 으로 보인다.
    /// </summary>
    /// <typeparam name="T">돌려줄 값.</typeparam>
    /// <param name="read">읽기 함수. 명단이 없으면 두 번째 인자가 null 이다.</param>
    public T WithData<T>(Func<MasterDataSet, NpcInstanceTable?, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        lock (_gate)
        {
            MasterDataSet data = Data();

            return read(data, Instances(data));
        }
    }

    /// <summary>
    /// 캐시된 마스터데이터. 키는 <b>편집 파일과 파생물의 크기·수정 시각</b>이다 —
    /// 해시를 매번 다시 재면 캐시의 뜻이 없다.
    /// </summary>
    private MasterDataSet Data()
    {
        string key = CacheKey();

        if (_cacheData is { } cached && string.Equals(key, _cacheKey, StringComparison.Ordinal))
        {
            return cached;
        }

        MasterDataSet data = LoadTolerant(_directory);

        LoadCount++;
        _cacheKey = key;
        _cacheData = data;
        _cacheInstances = null;
        _cacheInstancesLoaded = false;

        return data;
    }

    private NpcInstanceTable? Instances(MasterDataSet data)
    {
        if (_cacheInstancesLoaded)
        {
            return _cacheInstances;
        }

        _cacheInstances = TryLoadInstances(data);
        _cacheInstancesLoaded = true;

        return _cacheInstances;
    }

    private void InvalidateCache()
    {
        _cacheKey = string.Empty;
        _cacheData = null;
        _cacheInstances = null;
        _cacheInstancesLoaded = false;
    }

    private string CacheKey()
    {
        var sb = new StringBuilder(512);

        sb.Append(_directory).Append('|');

        foreach (string file in TransactionFiles(_directory).Concat(["npc_instances.json", "poi_distances.bin"]))
        {
            var info = new FileInfo(Path.Combine(_directory, file));

            sb.Append(file).Append(':')
              .Append(info.Exists ? info.Length : -1).Append(':')
              .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('|');
        }

        return sb.ToString();
    }

    /// <summary>
    /// 편집 도구용 로드 (H07). 거리표가 낡았거나 없으면 <b>거리 없이</b> 다시 읽는다 —
    /// 그래야 "다시 만들라" 고 말해 줄 화면을 그릴 수 있다. 다른 실패는 그대로 던진다.
    /// </summary>
    private static MasterDataSet LoadTolerant(string directory)
    {
        try
        {
            return MasterDataLoader.Load(directory);
        }
        catch (Exception ex) when (IsDistanceProblem(ex))
        {
            return MasterDataLoader.Load(directory, new MasterDataLoadOptions { SkipDistances = true });
        }
    }

    /// <summary>거리표 때문에 실패했는가. 파일 이름이 메시지에 있는지로 가른다.</summary>
    private static bool IsDistanceProblem(Exception error) =>
        error is FileNotFoundException or InvalidDataException
        && error.Message.Contains("poi_distances", StringComparison.Ordinal);

    /// <summary>
    /// 현재 카탈로그와 검증 상태를 읽는다.
    ///
    /// <b>여기서 죽지 않는다</b> (H07). 예전에는 검증기와 명단 로드가 <c>try</c> 밖에 있어
    /// JSON 한 글자가 깨지면 <see cref="StudioSession"/> 생성자에서 터졌고, 그러면 DI 가
    /// 실패해 <b>빈 화면</b>이 떴다 — 고치러 갈 화면까지 같이 죽은 것이다.
    /// </summary>
    public StudioCatalog LoadCatalog()
    {
        lock (_gate)
        {
            MasterDataSet data;

            try
            {
                data = Data();
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException
                or ArgumentException or InvalidOperationException)
            {
                // 파생물이 입력과 어긋나면 로더가 옳게 거절한다 (243개 행렬에 244번째 POI 는 없다).
                // 그때 화면까지 같이 죽으면 다시 만들라고 말해 줄 화면이 없어진다 — 막힌 상태로 연다.
                return Blocked(ex);
            }

            ImmutableArray<StudioIssue> issues;
            int instances;

            try
            {
                issues = ValidateLocked(data);
                string instancesPath = Path.Combine(_directory, "npc_instances.json");
                instances = File.Exists(instancesPath) ? (Instances(data)?.Count ?? 0) : 0;
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException
                or ArgumentException or InvalidOperationException)
            {
                return Blocked(ex);
            }

            ImmutableArray<StudioArchetype> archetypes =
            [
                .. data.Archetypes.Archetypes.Select(a => new StudioArchetype(
                    a.Id,
                    a.Code.Value,
                    a.Description,
                    a.PopulationWeight,
                    (int)Math.Round(a.PopulationWeight * ArchetypeCard.PopulationBase),
                    a.WorkplacePoiType ?? "—",
                    a.CombatCapable)),
            ];

            return new StudioCatalog(
                _directory,
                options.ReadOnly,
                archetypes,
                instances,
                issues)
            {
                PoiCount = data.Pois.Count,
                ZoneCount = data.Zones.Count,
                InterruptCount = data.Interrupts.Count,
                ActionCount = data.Actions.Count,
                ItemCount = data.Items.Items.Length,
                StaleArtifacts = [.. DerivedArtifacts.Stale(_directory).Select(status => status.Artifact)],
                Sandbox = SandboxName,
                DistancesAvailable = data.DistancesAvailable,
                BackupWritable = BackupWritable(),
            };
        }
    }

    /// <summary>
    /// 로더가 이 폴더를 읽지 못할 때의 카탈로그. <b>목록은 비어 있고 <see cref="StudioCatalog.Blocked"/> 만 있다</b> —
    /// 화면은 사유 종류를 보고 탈출구를 고른다 (H07).
    /// </summary>
    private StudioCatalog Blocked(Exception error) =>
        new(_directory, options.ReadOnly, [], 0, [])
        {
            Blocked = StudioErrors.Humanize(error),
            BlockedDetail = error.Message,
            Kind = Classify(error),
            StaleArtifacts = [.. DerivedArtifacts.Stale(_directory).Select(status => status.Artifact)],
            Sandbox = SandboxName,
            BackupWritable = BackupWritable(),
        };

    /// <summary>
    /// 막힌 사유의 종류 (H07). <b>"파생물이 입력과 어긋난다" 한 문장으로 뭉뚱그리면
    /// VS Code 로 괄호 하나 지운 사람도 같은 문구를 본다</b> — 그리고 그 사람에게는
    /// "다시 만들기" 버튼이 아무 소용이 없다.
    /// </summary>
    private static StudioBlockKind Classify(Exception error) => error switch
    {
        FileNotFoundException missing when missing.Message.Contains("poi_distances", StringComparison.Ordinal)
            => StudioBlockKind.MissingArtifact,
        FileNotFoundException => StudioBlockKind.MissingArtifact,
        InvalidDataException stale when stale.Message.Contains("poi_distances", StringComparison.Ordinal)
            => StudioBlockKind.StaleDistances,
        JsonException => StudioBlockKind.JsonSyntax,
        _ => StudioBlockKind.LoaderReject,
    };

    /// <summary>백업 폴더에 쓸 수 있는가 (H14). 못 쓰면 되돌리기를 만들 수 없다.</summary>
    private bool BackupWritable()
    {
        try
        {
            string root = BackupRoot;
            Directory.CreateDirectory(root);

            string probe = Path.Combine(root, ".write-probe");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>아키타입 한 항목의 원문과 설명 카드를 읽는다.</summary>
    public StudioArchetypeDocument LoadArchetype(string id)
    {
        lock (_gate)
        {
            MasterDataSet data = Data();
            string source = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", id);

            return new StudioArchetypeDocument(id, source[start..end], ArchetypeCard.Render(data, id))
            {
                Stamp = StampLocked(["archetypes.json"]),
            };
        }
    }

    /// <summary>저장하지 않은 아키타입 JSON을 현재 마스터데이터와 합쳐 설명 카드로 만든다.</summary>
    public string PreviewArchetype(string id, string itemJson)
    {
        EnsureItemId(itemJson, id);

        lock (_gate)
        {
            MasterDataSet data = Data();
            string source = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", id);
            string candidate = source[..start] + itemJson + source[end..];
            ArchetypeTable archetypes = ArchetypeTable.Parse(candidate, data.Actions, data.Items);

            if (!archetypes.TryGet(id, out ArchetypeDef draft))
            {
                throw new InvalidDataException($"직업 '{id}' 초안을 읽지 못했다.");
            }

            return ArchetypeCard.Render(data, draft);
        }
    }

    /// <summary>생성된 NPC를 아키타입·지역으로 탐색할 수 있는 가벼운 목록으로 읽는다.</summary>
    public ImmutableArray<StudioNpcSummary> LoadNpcDirectory()
    {
        lock (_gate)
        {
            MasterDataSet data = Data();
            NpcInstanceTable instances = Instances(data)
                ?? throw new InvalidDataException("NPC 명단(npc_instances.json)이 없다 — 다시 만든다.");
            HashSet<int> overridden = LoadOverrideIds();
            var result = ImmutableArray.CreateBuilder<StudioNpcSummary>(instances.Count);

            foreach (NpcInstanceDef npc in instances.Instances)
            {
                string workplace = npc.Workplace == default ? "—" : data.Pois[npc.Workplace].Id;
                string faction = npc.Faction == default || data.Factions is null
                    ? "—"
                    : data.Factions.NameOf(npc.Faction);

                result.Add(new StudioNpcSummary(
                    npc.Id,
                    data.Archetypes[npc.Archetype].Id,
                    data.Zones[npc.Zone].Id,
                    data.Pois[npc.Home].Id,
                    workplace,
                    faction,
                    overridden.Contains(npc.Id)));
            }

            return result.ToImmutable();
        }
    }

    /// <summary>생성된 NPC 한 명을 사람이 읽는 카드로 보여 준다.</summary>
    public string LoadNpcCard(int id)
    {
        lock (_gate)
        {
            MasterDataSet data = Data();
            NpcInstanceTable instances = Instances(data)
                ?? throw new InvalidDataException("NPC 명단(npc_instances.json)이 없다 — 다시 만든다.");
            int index = id - 1;

            if ((uint)index >= (uint)instances.Count || instances[index].Id != id)
            {
                throw new ArgumentOutOfRangeException(nameof(id), $"NPC {id}가 없다.");
            }

            return InstanceCard.Render(data, instances, index);
        }
    }

    /// <summary>
    /// 아키타입 하나를 <b>개요 보기</b>에 필요한 만큼 읽는다 (T05).
    /// JSON 원문 대신 이 값들이 섹션 카드가 된다.
    /// </summary>
    /// <param name="id">아키타입 id.</param>
    public StudioArchetypeView LoadArchetypeView(string id)
    {
        lock (_gate)
        {
            MasterDataSet data = Data();

            if (!data.Archetypes.TryGet(id, out ArchetypeDef def))
            {
                throw new InvalidDataException($"직업 '{id}' 이 없다.");
            }

            NpcInstanceTable? instances = Instances(data);

            return BuildView(data, def, instances);
        }
    }

    /// <summary>저장하지 않은 초안으로 개요를 만든다 (T25). 나머지 마스터데이터는 현재 값이다.</summary>
    /// <param name="id">아키타입 id.</param>
    /// <param name="itemJson">초안 JSON.</param>
    public StudioArchetypeView PreviewArchetypeView(string id, string itemJson)
    {
        EnsureItemId(itemJson, id);

        lock (_gate)
        {
            MasterDataSet data = Data();
            string source = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", id);
            string candidate = source[..start] + itemJson + source[end..];
            ArchetypeTable archetypes = ArchetypeTable.Parse(candidate, data.Actions, data.Items);

            if (!archetypes.TryGet(id, out ArchetypeDef draft))
            {
                throw new InvalidDataException($"직업 '{id}' 초안을 읽지 못했다.");
            }

            return BuildView(data, draft, Instances(data));
        }
    }

    /// <summary>
    /// NPC 한 명을 <b>누구인가 · 어디 사는가 · 무엇을 하는가</b> 로 읽는다 (T07).
    /// 지도를 그릴 좌표와 개별 설정 폼이 같이 온다.
    /// </summary>
    /// <param name="id">NPC 번호.</param>
    public StudioNpcOverview LoadNpcOverview(int id)
    {
        lock (_gate)
        {
            MasterDataSet data = Data();
            NpcInstanceTable instances = Instances(data)
                ?? throw new InvalidDataException("NPC 명단(npc_instances.json)이 없다 — 다시 만든다.");
            int index = id - 1;

            if ((uint)index >= (uint)instances.Count || instances[index].Id != id)
            {
                throw new ArgumentOutOfRangeException(nameof(id), $"NPC {id}가 없다.");
            }

            NpcInstanceDef npc = instances[index];
            InstanceFacts facts = InstanceCard.Facts(data, npc);

            // 순찰로가 없는 NPC 는 이 배열이 default 다 — 그대로 Select 하면 NullReference 다.
            ImmutableArray<string> route = npc.PatrolRoute.IsDefaultOrEmpty
                ? []
                : [.. npc.PatrolRoute.Select(p => data.Pois[p].Id)];

            return new StudioNpcOverview(
                npc,
                facts,
                InstanceCard.Sentence(data, npc),
                Lexicon.ArchetypeName(data, facts.ArchetypeId),
                Lexicon.Zone(data, facts.ZoneId),
                Lexicon.Group(facts.ArchetypeId),
                PoiChoices(data, npc),
                route);
        }
    }

    private StudioArchetypeView BuildView(MasterDataSet data, ArchetypeDef def, NpcInstanceTable? instances)
    {
        ArchetypeFacts facts = ArchetypeCard.Facts(data, def);
        CompiledPlan? fallback = facts.Fallback;

        ImmutableArray<StepTrace> trace = fallback is null
            ? []
            : PlanExplain.Trace(data, fallback, fallback.Bucket, def.Code);

        LoopVerdict loop = fallback is null
            ? new LoopVerdict(false, "하루 일과를 찾지 못했다.")
            : PlanExplain.LoopOf(fallback, trace.IsEmpty ? data.InitialFlags(fallback.Bucket) : trace[^1].After);

        return new StudioArchetypeView(
            facts,
            Lexicon.ArchetypeName(data, def.Id),
            Lexicon.Group(def.Id),
            trace,
            loop,
            ByZone(data, def, instances, facts.Population),
            ArchetypeLint.Run(data, def, instances));
    }

    /// <summary>
    /// 지역별 인구 (T33). 명단이 있으면 세고, 없으면(새 직업) 일터 정원 비례로 예측한다.
    /// </summary>
    private static ImmutableArray<StudioZoneCount> ByZone(
        MasterDataSet data, ArchetypeDef def, NpcInstanceTable? instances, int population)
    {
        if (instances is not null && instances.Instances.Any(n => n.Archetype == def.Code))
        {
            return
            [
                .. instances.Instances
                    .Where(n => n.Archetype == def.Code)
                    .GroupBy(n => n.Zone)
                    .Select(g => new StudioZoneCount(
                        data.Zones[g.Key].Id, Lexicon.Zone(data, data.Zones[g.Key].Id), g.Count(), false))
                    .OrderByDescending(z => z.Count)
                    .ThenBy(z => z.ZoneId, StringComparer.Ordinal),
            ];
        }

        return
        [
            .. PlacementForecast.ByZone(data, def, population)
                .Select(z => new StudioZoneCount(
                    data.Zones[z.Zone].Id, Lexicon.Zone(data, data.Zones[z.Zone].Id), z.Count, true)),
        ];
    }

    private NpcInstanceTable? TryLoadInstances(MasterDataSet data)
    {
        string path = Path.Combine(_directory, "npc_instances.json");

        return File.Exists(path) ? NpcInstanceTable.Load(path, data) : null;
    }

    private static ImmutableArray<StudioPoiChoice> PoiChoices(MasterDataSet data, NpcInstanceDef npc)
    {
        ZoneId zone = npc.Zone;
        PoiId home = npc.Home;
        PoiId workplace = npc.Workplace;

        return
        [
            .. data.Pois.Pois
                .Where(p => p.Zone == zone)
                .OrderBy(p => p.Type)
                .ThenBy(p => p.Subtype, StringComparer.Ordinal)
                .ThenBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => new StudioPoiChoice(
                    p.Id,
                    PoiTypeLabel(p.Type),
                    p.Subtype,
                    p.Capacity,
                    p.Pos.X,
                    p.Pos.Z,
                    p.Code == home,
                    p.Code == workplace)
                {
                    Kind = p.Type,
                    Label = Lexicon.PlaceName(p),
                }),
        ];
    }

    /// <summary>개별 NPC의 손편집 오버라이드와 선택 가능한 같은 지역 POI·세력을 읽는다.</summary>
    public StudioNpcOverrideEditor LoadNpcOverride(int id)
    {
        lock (_gate)
        {
            MasterDataSet data = Data();
            NpcInstanceTable instances = Instances(data)
                ?? throw new InvalidDataException("NPC 명단(npc_instances.json)이 없다 — 다시 만든다.");
            int index = id - 1;
            if ((uint)index >= (uint)instances.Count || instances[index].Id != id)
            {
                throw new ArgumentOutOfRangeException(nameof(id), $"NPC {id}가 없다.");
            }

            NpcInstanceDef npc = instances[index];
            JsonObject root = JsonNode.Parse(Read(NpcInstanceTable.OverrideFileName))?.AsObject()
                ?? throw new InvalidDataException("npc_overrides.json을 읽지 못했다.");
            JsonArray overrides = root["overrides"]?.AsArray()
                ?? throw new InvalidDataException("npc_overrides.json에 overrides 배열이 없다.");
            JsonObject? item = FindOverride(overrides, id);
            ImmutableArray<string> route = item?["patrol_route"] is JsonArray routeNode
                ? [.. routeNode.Select(n => n?.GetValue<string>() ?? string.Empty).Where(v => v.Length > 0)]
                : [];

            return new StudioNpcOverrideEditor(
                id,
                item is not null,
                route,
                item?["aggro_radius_m"]?.GetValue<int>(),
                item?["faction"]?.GetValue<string>() ?? string.Empty,
                item?["dialogue_profile"]?.GetValue<string>() ?? string.Empty,
                item?["schedule_offset_min"]?.GetValue<int>(),
                data.Factions is null ? [] : [.. data.Factions.Factions.Select(f => f.Id)],
                [.. data.Pois.Pois
                    .Where(p => p.Zone == npc.Zone)
                    .OrderBy(p => p.Type)
                    .ThenBy(p => p.Subtype, StringComparer.Ordinal)
                    .ThenBy(p => p.Id, StringComparer.Ordinal)
                    .Select(p => new StudioPoiChoice(
                        p.Id,
                        PoiTypeLabel(p.Type),
                        p.Subtype,
                        p.Capacity,
                        p.Pos.X,
                        p.Pos.Z,
                        p.Code == npc.Home,
                        p.Code == npc.Workplace)
                    {
                        Kind = p.Type,
                        Label = Lexicon.PlaceName(p),
                    })])
            {
                Stamp = StampLocked([NpcInstanceTable.OverrideFileName]),
                DialogueProfiles = DialogueProfiles(overrides),
                FactionNotes = data.Factions is null
                    ? []
                    : [.. data.Factions.Factions.Select(f => (f.Id, f.Description))],
            };
        }
    }

    /// <summary>
    /// 대화 프로필 후보 (H17). <b>이미 쓰인 값에서 모은다</b> —
    /// 화면에 세 개를 하드코딩해 두면 네 번째 프로필이 생겨도 아무도 모른다.
    /// </summary>
    private static ImmutableArray<string> DialogueProfiles(JsonArray overrides) =>
    [
        .. overrides.OfType<JsonObject>()
            .Select(item => item["dialogue_profile"]?.GetValue<string>() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    /// <summary>
    /// 개별 NPC 오버라이드를 추가·교체한다. 빈 초안이면 해당 항목을 제거한다.
    ///
    /// <para>
    /// <b>파일을 통째로 다시 직렬화하지 않는다</b> (H17). <c>JsonNode</c> 로 읽어 다시 쓰면
    /// 주석(<c>_comment</c>)·키 순서·들여쓰기가 우리 서식으로 바뀌어 <b>첫 저장의 diff 가
    /// 파일 전체</b>가 된다. 항목 하나만 도려내고 갈아 끼운다 — 다른 경로와 같은 규칙이다.
    /// </para>
    /// </summary>
    /// <param name="draft">저장할 초안.</param>
    public StudioSaveResult SaveNpcOverride(StudioNpcOverrideDraft draft)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(draft);

        lock (_gate)
        {
            MasterDataSet data = Data();
            NpcInstanceTable instances = Instances(data)
                ?? throw new InvalidDataException("NPC 명단(npc_instances.json)이 없다 — 다시 만든다.");
            int index = draft.Id - 1;

            if ((uint)index >= (uint)instances.Count || instances[index].Id != draft.Id)
            {
                throw new InvalidDataException($"NPC {draft.Id}가 없다.");
            }

            string source = Read(NpcInstanceTable.OverrideFileName);
            bool empty = draft.PatrolRoute.IsDefaultOrEmpty
                && draft.AggroRadiusM is null
                && string.IsNullOrWhiteSpace(draft.Faction)
                && string.IsNullOrWhiteSpace(draft.DialogueProfile)
                && draft.ScheduleOffsetMinutes is null;

            string candidate = Overrides(source, draft, empty);

            StudioSaveResult result = ValidateAndWrite(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [NpcInstanceTable.OverrideFileName] = candidate,
                },
                stamp: draft.Stamp,
                label: $"NPC {draft.Id} 개별 설정");

            return result.Saved
                ? result with
                {
                    Message = empty
                        ? $"NPC {draft.Id} 의 개별 설정을 지웠다."
                        : $"NPC {draft.Id} 의 개별 설정을 저장했다.",
                }
                : result;
        }
    }

    /// <summary>
    /// 오버라이드 항목 하나를 갈아 끼운 원문. 빈 초안이면 지운다.
    ///
    /// <b>바뀐 필드만 고친다</b> — 통째로 다시 쓰면 <c>patrol_route</c> 의 한 줄 배열이
    /// 여러 줄로 바뀌어, 아무것도 안 고친 저장이 파일 전체 diff 가 된다.
    /// </summary>
    private static string Overrides(string source, StudioNpcOverrideDraft draft, bool empty)
    {
        string key = draft.Id.ToString(CultureInfo.InvariantCulture);
        bool exists = HasOverride(source, draft.Id);

        if (empty)
        {
            return exists ? JsonSurgeon.RemoveFromArray(source, "overrides", "id", key) : source;
        }

        if (!exists)
        {
            string indent = StudioJsonFormat.IndentOf(source, "overrides", "  ") + "  ";

            return JsonSurgeon.AppendToArray(
                source, "overrides", StudioJsonFormat.Dedent(NewOverride(draft, indent)));
        }

        (int start, int end) = JsonSurgeon.ItemRange(source, "overrides", "id", key);
        string item = source[start..end];
        string fieldIndent = StudioJsonFormat.IndentOf(item, "id", "      ");

        item = Field(item, "patrol_route", Inline(draft.PatrolRoute), "aggro_radius_m");
        item = Field(
            item,
            "aggro_radius_m",
            draft.AggroRadiusM is { } aggro ? StudioJsonFormat.Number(aggro) : string.Empty,
            "faction");
        item = Field(
            item,
            "faction",
            string.IsNullOrWhiteSpace(draft.Faction) ? string.Empty : StudioJsonFormat.Text(draft.Faction.Trim()),
            "dialogue_profile");
        item = Field(
            item,
            "dialogue_profile",
            string.IsNullOrWhiteSpace(draft.DialogueProfile)
                ? string.Empty
                : StudioJsonFormat.Text(draft.DialogueProfile.Trim()),
            "schedule_offset_min");
        item = Field(
            item,
            "schedule_offset_min",
            draft.ScheduleOffsetMinutes is { } offset ? StudioJsonFormat.Number(offset) : string.Empty,
            null);

        _ = fieldIndent;

        return source[..start] + item + source[end..];
    }

    /// <summary>필드 하나를 넣거나 바꾸거나 지운다. 값이 같으면 원문 그대로다.</summary>
    private static string Field(string item, string name, string rawValue, string? before)
    {
        bool has = JsonSurgeon.HasTopLevel(item, name);

        if (rawValue.Length == 0)
        {
            return has ? JsonSurgeon.RemoveTopLevel(item, name) : item;
        }

        if (has)
        {
            (int start, int end) = JsonSurgeon.ValueRange(item, name);

            return string.Equals(item[start..end], rawValue, StringComparison.Ordinal)
                ? item
                : item[..start] + rawValue + item[end..];
        }

        return before is null
            ? JsonSurgeon.SetOrAddTopLevel(item, name, rawValue)
            : JsonSurgeon.SetOrAddTopLevel(item, name, rawValue, before);
    }

    /// <summary>순찰로는 한 줄 배열이다 — 파일이 그렇게 쓰여 있고, 짧아서 읽기도 낫다.</summary>
    private static string Inline(ImmutableArray<string> values) =>
        values.IsDefaultOrEmpty
            ? string.Empty
            : "[" + string.Join(", ", values.Select(StudioJsonFormat.Text)) + "]";

    private static string NewOverride(StudioNpcOverrideDraft draft, string indent)
    {
        var fields = new List<(string, string)>(6) { ("id", StudioJsonFormat.Number(draft.Id)) };

        if (Inline(draft.PatrolRoute) is { Length: > 0 } route)
        {
            fields.Add(("patrol_route", route));
        }

        if (draft.AggroRadiusM is { } aggro)
        {
            fields.Add(("aggro_radius_m", StudioJsonFormat.Number(aggro)));
        }

        if (!string.IsNullOrWhiteSpace(draft.Faction))
        {
            fields.Add(("faction", StudioJsonFormat.Text(draft.Faction.Trim())));
        }

        if (!string.IsNullOrWhiteSpace(draft.DialogueProfile))
        {
            fields.Add(("dialogue_profile", StudioJsonFormat.Text(draft.DialogueProfile.Trim())));
        }

        if (draft.ScheduleOffsetMinutes is { } offset)
        {
            fields.Add(("schedule_offset_min", StudioJsonFormat.Number(offset)));
        }

        return StudioJsonFormat.Object(fields, indent);
    }

    private static bool HasOverride(string source, int id)
    {
        using JsonDocument document = JsonDocument.Parse(source);

        if (!document.RootElement.TryGetProperty("overrides", out JsonElement overrides)
            || overrides.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement item in overrides.EnumerateArray())
        {
            if (item.TryGetProperty("id", out JsonElement value) && value.TryGetInt32(out int number) && number == id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 개별 설정이 붙어 있는 NPC 의 <b>지금</b> 직업·지역 (H16).
    /// 명단을 다시 만들기 전에 찍어 두고, 만든 뒤 견준다.
    /// </summary>
    public ImmutableArray<(int Npc, string Archetype, string Zone)> OverrideTargets()
    {
        lock (_gate)
        {
            try
            {
                MasterDataSet data = Data();
                NpcInstanceTable? instances = Instances(data);

                if (instances is null)
                {
                    return [];
                }

                var list = ImmutableArray.CreateBuilder<(int, string, string)>();

                foreach (int id in LoadOverrideIds().Order())
                {
                    int index = id - 1;

                    if ((uint)index >= (uint)instances.Count || instances[index].Id != id)
                    {
                        continue;
                    }

                    NpcInstanceDef npc = instances[index];

                    list.Add((id, data.Archetypes[npc.Archetype].Id, data.Zones[npc.Zone].Id));
                }

                return list.ToImmutable();
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                return [];
            }
        }
    }

    /// <summary>
    /// 재생성 뒤 <b>번호가 가리키는 NPC 가 바뀐</b> 개별 설정 (H16).
    ///
    /// <para>
    /// <c>gen_npcs.cs</c> 는 직업 code 순 결정론 배치라 인구가 안 바뀌면 번호가 유지된다 —
    /// 그러나 인구를 하나라도 옮기면 <b>그 뒤 번호가 전부 밀린다</b>. 같은 지역 다른 직업은
    /// V13 도 잡지 못하므로, 순찰로가 조용히 엉뚱한 사람에게 붙는다.
    /// </para>
    /// </summary>
    /// <param name="before">재생성 전 스냅샷.</param>
    public ImmutableArray<string> OverrideDrift(ImmutableArray<(int Npc, string Archetype, string Zone)> before)
    {
        if (before.IsDefaultOrEmpty)
        {
            return [];
        }

        var now = OverrideTargets().ToDictionary(t => t.Npc, t => (t.Archetype, t.Zone));
        var lines = ImmutableArray.CreateBuilder<string>();

        foreach ((int npc, string archetype, string zone) in before)
        {
            if (!now.TryGetValue(npc, out (string Archetype, string Zone) after))
            {
                lines.Add($"#{npc} 이(가) 명단에서 사라졌다.");
            }
            else if (!string.Equals(after.Archetype, archetype, StringComparison.Ordinal)
                || !string.Equals(after.Zone, zone, StringComparison.Ordinal))
            {
                lines.Add(
                    $"#{npc} 이(가) {Lexicon.Archetype(archetype)}({zone}) 에서 "
                    + $"{Lexicon.Archetype(after.Archetype)}({after.Zone}) 가 됐다.");
            }
        }

        return lines.ToImmutable();
    }

    /// <summary>사람이 편집할 수 있는 JSON 파일 원문을 읽는다.</summary>
    /// <param name="fileName">파일 이름.</param>
    public string LoadFile(string fileName) => LoadFileDocument(fileName).Json;

    /// <summary>원문과 그때의 지문 (H13). 저장이 이 지문으로 외부 편집을 거절한다.</summary>
    /// <param name="fileName">파일 이름.</param>
    public StudioFileDocument LoadFileDocument(string fileName)
    {
        EnsureEditable(fileName);

        lock (_gate)
        {
            return new StudioFileDocument(fileName, Read(fileName), StampLocked([fileName]));
        }
    }

    /// <summary>아키타입 항목을 교체한다. 전체 검증과 로더를 모두 통과해야 저장한다.</summary>
    /// <param name="id">아키타입 id.</param>
    /// <param name="itemJson">항목 JSON.</param>
    /// <param name="stamp">읽은 시점의 지문 (H13). 없으면 외부 편집을 검사하지 않는다.</param>
    public StudioSaveResult SaveArchetype(string id, string itemJson, StudioStamp? stamp = null)
    {
        EnsureWritable();
        EnsureItemId(itemJson, id);

        lock (_gate)
        {
            string source = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", id);
            string candidate = source[..start] + itemJson + source[end..];

            return ValidateAndWrite(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["archetypes.json"] = candidate,
                },
                stamp: stamp,
                label: $"{id} 원문 편집");
        }
    }

    /// <summary>일반 JSON 파일을 저장한다. 생성물은 이 경로에 들어오지 못한다.</summary>
    /// <param name="fileName">파일 이름.</param>
    /// <param name="json">새 원문.</param>
    /// <param name="stamp">읽은 시점의 지문 (H13).</param>
    public StudioSaveResult SaveFile(string fileName, string json, StudioStamp? stamp = null)
    {
        EnsureWritable();
        EnsureEditable(fileName);

        // 문법 오류는 검증기에 넘기지 않는다 — 여기서 한국어 한 문장으로 잡는다.
        using (JsonDocument.Parse(json))
        {
        }

        lock (_gate)
        {
            return ValidateAndWrite(
                new Dictionary<string, string>(StringComparer.Ordinal) { [fileName] = json },
                stamp: stamp,
                label: $"{fileName} 원문 편집");
        }
    }

    // ------------------------------------------------------------------ 폼 편집 (T10 · T11)

    /// <summary>아키타입 하나를 폼으로 읽는다 (T10).</summary>
    /// <param name="id">아키타입 id.</param>
    public StudioArchetypeForm LoadArchetypeForm(string id)
    {
        lock (_gate)
        {
            return FormOf(ItemJson(id)) with { Stamp = StampLocked(["archetypes.json"]) };
        }
    }

    /// <summary>
    /// 폼을 저장한다 (T10). <b>바뀐 필드만 고친다</b> — 통째로 다시 직렬화하면
    /// 서식·키 순서·주석 배열이 무너지고 그 diff 는 리뷰할 수 없다.
    /// </summary>
    /// <param name="form">저장할 폼.</param>
    public StudioSaveResult SaveArchetypeForm(StudioArchetypeForm form)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(form);

        lock (_gate)
        {
            string item = ApplyForm(ItemJson(form.Id), form);
            string source = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", form.Id);
            string candidate = source[..start] + item + source[end..];

            // H03 — 인구 재배분은 <b>이 저장의 일부</b>다. 예전에는 "이 안으로" 버튼이
            // 곧바로 파일을 쓰고 폼을 다시 읽어, 저장하지 않은 다른 편집을 통째로 지웠다.
            candidate = ApplyRebalanceTo(candidate, form.Rebalance, form.Id);

            return ValidateAndWrite(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["archetypes.json"] = candidate },
                stamp: form.Stamp,
                label: $"{form.Id} 편집");
        }
    }

    /// <summary>재배분 줄을 후보에 얹는다. 자기 줄과 변화 없는 줄은 건너뛴다.</summary>
    private static string ApplyRebalanceTo(string source, ImmutableArray<WeightChange> changes, string skip)
    {
        if (changes.IsDefaultOrEmpty)
        {
            return source;
        }

        foreach (WeightChange change in changes)
        {
            if (string.Equals(change.Archetype, skip, StringComparison.Ordinal)
                || Math.Abs(change.From - change.To) <= 1e-9)
            {
                continue;
            }

            source = JsonSurgeon.SetInArrayItem(
                source, "archetypes", "id", change.Archetype,
                "population_weight", StudioJsonFormat.Weight(change.To));
        }

        return source;
    }

    /// <summary>폼 초안을 저장하지 않고 정의로 읽는다 (T25).</summary>
    /// <param name="form">초안.</param>
    public ArchetypeDef PreviewArchetypeForm(StudioArchetypeForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        lock (_gate)
        {
            MasterDataSet data = Data();
            string source = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", form.Id);
            string candidate = source[..start] + ApplyForm(source[start..end], form) + source[end..];
            ArchetypeTable table = ArchetypeTable.Parse(candidate, data.Actions, data.Items);

            return table.TryGet(form.Id, out ArchetypeDef draft)
                ? draft
                : throw new InvalidDataException($"직업 '{form.Id}' 초안을 읽지 못했다.");
        }
    }

    /// <summary>
    /// 폼 편집기가 고를 수 있는 값들 (T10). 마스터데이터에서 읽는다 —
    /// 일터 유형·레시피·아이템을 코드에 하드코딩하지 않는다.
    /// </summary>
    public StudioArchetypeChoices LoadArchetypeChoices()
    {
        lock (_gate)
        {
            MasterDataSet data = Data();

            return new StudioArchetypeChoices(
                [.. data.Pois.Pois.Where(p => p.Type == PoiType.Home).Select(p => p.Subtype).Distinct().Order(StringComparer.Ordinal)],
                [.. data.Pois.Pois.Where(p => p.Type != PoiType.Home).Select(p => p.Subtype).Distinct().Order(StringComparer.Ordinal)],
                [.. data.Items.Recipes.Select(r => r.Id).Order(StringComparer.Ordinal)],
                [.. data.Items.Items.Select(i => i.Id).Order(StringComparer.Ordinal)],
                [.. data.Actions.Actions.Select(a => a.Id)]);
        }
    }

    /// <summary>
    /// 새 장소를 <c>pois.json</c> 맨 뒤에 넣는다 (T13).
    ///
    /// <b><c>code</c> 는 <see cref="CodeAllocator"/> 가 정한다</b> — 눈으로 세면 중복·예약 구간 침범이 난다.
    /// 저장 뒤에는 거리표가 낡는다 (파급 패널이 그것을 말한다).
    /// </summary>
    /// <param name="draft">새 장소 초안.</param>
    public StudioSaveResult AppendPoi(StudioNewPlace draft)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(draft);

        lock (_gate)
        {
            // H11 — 같은 id 의 장소가 둘 생기면 아무도 잡지 못했다. 로더의 `byId` 는 마지막이
            // 이기고, 이후 편집이 첫 것과 마지막 것을 엇갈려 가리킨다. 저장 전에 거절한다.
            if (CheckNewPlace(draft) is { Length: > 0 } problems)
            {
                return new StudioSaveResult(false, problems[0].Detail, problems);
            }

            string source = Read("pois.json");
            int code = CodeAllocator.Next(_directory, "pois.json");
            // AppendToArray 가 배열의 들여쓰기를 붙인다 — 여기서는 0 기준으로 만든다.
            const string Nested = "  ";

            var fields = new List<(string, string)>(10)
            {
                ("id", StudioJsonFormat.Text(draft.Id)),
                ("code", StudioJsonFormat.Number(code)),
                ("zone", StudioJsonFormat.Text(draft.Zone)),
                ("type", StudioJsonFormat.Text(draft.Type)),
                ("subtype", StudioJsonFormat.Text(draft.Subtype)),
                ("pos", StudioJsonFormat.Object(
                    [
                        ("x", draft.X.ToString("0.##", CultureInfo.InvariantCulture)),
                        ("y", "0"),
                        ("z", draft.Z.ToString("0.##", CultureInfo.InvariantCulture)),
                    ],
                    Nested)),
                ("capacity", StudioJsonFormat.Number(draft.Capacity)),
                ("open_hours", StudioJsonFormat.Object(
                    [("from", StudioJsonFormat.Text(draft.OpenFrom)), ("to", StudioJsonFormat.Text(draft.OpenTo))],
                    Nested)),
            };

            if (!draft.AllowedArchetypes.IsDefaultOrEmpty)
            {
                fields.Add(("allowed_archetypes", StudioJsonFormat.Strings(draft.AllowedArchetypes, Nested)));
            }

            if (!draft.Resources.IsDefaultOrEmpty)
            {
                fields.Add(("resources", StudioJsonFormat.Strings(draft.Resources, Nested)));
            }

            StudioSaveResult result = ValidateAndWrite(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["pois.json"] = JsonSurgeon.AppendToArray(
                        source, "pois", StudioJsonFormat.Object(fields, string.Empty)),
                },
                loaderGate: false,
                label: $"장소 {draft.Id} 추가");

            return result.Saved
                ? result with
                {
                    Message = $"장소 {draft.Id} (code {code}) 를 만들었다. "
                        + "거리표(poi_distances.bin)가 낡았다 — 다시 만들어야 서버가 뜬다.",
                }
                : result;
        }
    }

    /// <summary>
    /// 새 장소를 저장하기 전에 보는 것 (H11). 문제가 없으면 빈 배열이다.
    ///
    /// <b>검사기의 자체 시험이 있다</b> — 대조군 없이 두면 "아무것도 못 찾는 상태" 로도 통과한다.
    /// </summary>
    public ImmutableArray<StudioIssue> CheckNewPlace(StudioNewPlace draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var issues = ImmutableArray.CreateBuilder<StudioIssue>();
        string id = draft.Id.Trim();

        if (id.Length == 0
            || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))
            || char.IsDigit(id[0])
            || id.Any(char.IsUpper))
        {
            issues.Add(new StudioIssue(
                "V1",
                $"장소 id '{draft.Id}' 의 형식이 맞지 않는다 — 영문 소문자로 시작하는 소문자·숫자·밑줄이어야 한다.",
                "pois.json",
                "/id",
                "규칙은 `유형_번호_지역번호` 다 (house_012_04)."));
        }

        MasterDataSet data = Data();

        if (id.Length > 0 && data.Pois.TryGet(id, out _))
        {
            issues.Add(new StudioIssue(
                "V1",
                $"장소 id '{id}' 가 이미 있다.",
                "pois.json",
                "/id",
                "다른 번호를 쓴다 — 제안 버튼이 쓰이지 않은 최소 번호를 준다."));
        }

        if (!data.Zones.TryGet(draft.Zone, out _))
        {
            issues.Add(new StudioIssue(
                "V3", $"지역 '{draft.Zone}' 이 없다.", "pois.json", "/zone", "있는 지역을 고른다."));
        }

        if (draft.Capacity < 1)
        {
            issues.Add(new StudioIssue(
                "V10",
                "정원은 1 이상이어야 한다.",
                "pois.json",
                "/capacity",
                "정원 0 이면 아무도 배정되지 않는다."));
        }

        if (draft.Subtype.Trim().Length == 0)
        {
            issues.Add(new StudioIssue(
                "V1", "세부 유형이 비었다.", "pois.json", "/subtype", "직업의 일터 유형이 가리키는 값이다."));
        }

        return issues.ToImmutable();
    }

    // ------------------------------------------------------------------ 하루 일과 폼 (T11)

    /// <summary>하루 일과를 폼으로 읽는다 (T11).</summary>
    /// <param name="archetypeId">직업 id.</param>
    public StudioFallbackForm LoadFallbackForm(string archetypeId)
    {
        lock (_gate)
        {
            (string item, string _) = FallbackItem(archetypeId);

            return FallbackOf(item) with { Stamp = StampLocked(["fallback_plans.json"]) };
        }
    }

    /// <summary>
    /// 하루 일과를 저장한다 (T11). 스텝의 키 순서는 <c>action → args → timeout_s</c> 로 고정한다 —
    /// 순서가 회차마다 다르면 diff 가 읽히지 않는다.
    /// </summary>
    /// <param name="form">저장할 폼.</param>
    public StudioSaveResult SaveFallbackForm(StudioFallbackForm form)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(form);

        lock (_gate)
        {
            return ValidateAndWrite(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["fallback_plans.json"] = FallbackCandidate(form),
                },
                stamp: form.Stamp,
                label: $"{form.Archetype} 하루 일과");
        }
    }

    /// <summary>저장하지 않은 하루 일과를 컴파일해 판정한다 (T11).</summary>
    /// <param name="form">초안.</param>
    public StudioFallbackPreview PreviewFallbackForm(StudioFallbackForm form)
    {
        ArgumentNullException.ThrowIfNull(form);

        lock (_gate)
        {
            MasterDataSet data = Data();

            if (!data.Archetypes.TryGet(form.Archetype, out ArchetypeDef def))
            {
                return new StudioFallbackPreview(null, [], new LoopVerdict(false, "직업을 찾지 못했다."), "직업을 찾지 못했다.");
            }

            try
            {
                PlanTable table = PlanTable.Parse(FallbackCandidate(form), data);
                CompiledPlan? plan = table.For(def.Code);

                if (plan is null)
                {
                    return new StudioFallbackPreview(null, [], new LoopVerdict(false, "플랜을 찾지 못했다."), "플랜을 찾지 못했다.");
                }

                ImmutableArray<StepTrace> trace = PlanExplain.Trace(data, plan, plan.Bucket, def.Code);
                WorldFlags final = trace.IsEmpty ? data.InitialFlags(plan.Bucket) : trace[^1].After;

                return new StudioFallbackPreview(plan, trace, PlanExplain.LoopOf(plan, final), string.Empty);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
            {
                return new StudioFallbackPreview(null, [], new LoopVerdict(false, ex.Message), ex.Message);
            }
        }
    }

    /// <summary>
    /// 저장하지 않은 하루 일과를 <b>파일에 없는 직업</b>으로 판정한다 (H06).
    /// 마법사 4단계가 이것을 쓴다 — 아직 <c>fallback_plans.json</c> 에 항목이 없다.
    /// </summary>
    /// <param name="form">초안.</param>
    /// <param name="fromArchetypeId">복제 원본 직업 id. 허용 행동·심볼 바인딩을 여기서 빌린다.</param>
    public StudioFallbackPreview PreviewDraftFallback(StudioFallbackForm form, string fromArchetypeId)
    {
        ArgumentNullException.ThrowIfNull(form);

        lock (_gate)
        {
            MasterDataSet data = Data();

            if (!data.Archetypes.TryGet(fromArchetypeId, out ArchetypeDef source))
            {
                return new StudioFallbackPreview(
                    null, [], new LoopVerdict(false, "닮은 직업을 찾지 못했다."), "닮은 직업을 찾지 못했다.");
            }

            try
            {
                // 원본의 플랜 항목을 초안 스텝으로 갈아 끼워 컴파일한다 — 새 직업의 code 는
                // 아직 없으므로 <b>원본의 code 로</b> 판정한다. 허용 행동이 같으므로 결과도 같다.
                string candidate = FallbackCandidateFor(source.FallbackPlanId, form);
                PlanTable table = PlanTable.Parse(candidate, data);
                CompiledPlan? plan = table.For(source.Code);

                if (plan is null)
                {
                    return new StudioFallbackPreview(
                        null, [], new LoopVerdict(false, "플랜을 찾지 못했다."), "플랜을 찾지 못했다.");
                }

                ImmutableArray<StepTrace> trace = PlanExplain.Trace(data, plan, plan.Bucket, source.Code);
                WorldFlags final = trace.IsEmpty ? data.InitialFlags(plan.Bucket) : trace[^1].After;

                return new StudioFallbackPreview(plan, trace, PlanExplain.LoopOf(plan, final), string.Empty);
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException
                or InvalidOperationException)
            {
                return new StudioFallbackPreview(null, [], new LoopVerdict(false, ex.Message), ex.Message);
            }
        }
    }

    /// <summary>스텝 편집기가 고를 수 있는 값들 (T11).</summary>
    /// <param name="archetypeId">직업 id.</param>
    public StudioStepChoices LoadStepChoices(string archetypeId)
    {
        lock (_gate)
        {
            MasterDataSet data = Data();

            if (!data.Archetypes.TryGet(archetypeId, out ArchetypeDef def))
            {
                throw new InvalidDataException($"직업 '{archetypeId}' 이 없다.");
            }

            return new StudioStepChoices(
                [.. data.Actions.Actions.Where(a => def.Allows(a.Code)).Select(a => new StudioActionInfo(
                    a.Id,
                    Lexicon.Action(a.Id),
                    a.DefaultTimeoutSeconds,
                    [.. a.Params.Select(p => new StudioParamInfo(
                        p.Name, p.Type.ToString(), p.Required, p.EnumValues, p.Min, p.Max))]))],
                [.. PoiSymbols.Names.Skip(1).Select(s => new StudioSymbolInfo(
                    s,
                    Lexicon.Of(PoiSymbols.TryParse(s, out PoiSymbol symbol) ? symbol : PoiSymbol.None),
                    PoiSymbols.TryParse(s, out PoiSymbol parsed) && data.CanBindSymbol(def.Code, parsed)))],
                [.. data.Items.Items.Select(i => i.Id).Order(StringComparer.Ordinal)],
                [.. data.Items.Recipes.Select(r => r.Id).Order(StringComparer.Ordinal)]);
        }
    }

    private (string Item, string Source) FallbackItem(string archetypeId)
    {
        MasterDataSet data = Data();

        if (!data.Archetypes.TryGet(archetypeId, out ArchetypeDef def))
        {
            throw new InvalidDataException($"직업 '{archetypeId}' 이 없다.");
        }

        string source = Read("fallback_plans.json");
        (int start, int end) = JsonSurgeon.ItemRange(source, "plans", "id", def.FallbackPlanId);

        return (source[start..end], source);
    }

    private string FallbackCandidate(StudioFallbackForm form) => FallbackCandidateFor(form.Id, form);

    /// <summary>그 플랜 항목을 이 폼의 내용으로 갈아 끼운 원문.</summary>
    private string FallbackCandidateFor(string planId, StudioFallbackForm form)
    {
        string source = Read("fallback_plans.json");
        (int start, int end) = JsonSurgeon.ItemRange(source, "plans", "id", planId);
        string item = source[start..end];
        string indent = StudioJsonFormat.IndentOf(item, "id", "      ");

        item = JsonSurgeon.SetOrAddTopLevel(item, "goal", StudioJsonFormat.Text(form.Goal), "steps");
        item = JsonSurgeon.SetOrAddTopLevel(
            item,
            "steps",
            StudioJsonFormat.Raw(form.Steps.Select(step => StepJson(step, indent + "  ")), indent),
            "loop");

        return source[..start] + item + source[end..];
    }

    /// <summary>스텝 한 줄. 키 순서는 <c>action → args → timeout_s</c> 다.</summary>
    private static string StepJson(StudioPlanStep step, string indent)
    {
        // `args` 는 비어 있어도 쓴다 — 스키마가 객체를 요구한다 (V1.SCHEMA "스텝에 args 객체가 없다").
        var fields = new List<(string, string)>(3)
        {
            ("action", StudioJsonFormat.Text(step.Action)),
            ("args", StudioJsonFormat.Object(step.Args.Select(a => (a.Name, a.Raw)), indent + "  ")),
            ("timeout_s", StudioJsonFormat.Number(step.TimeoutSeconds)),
        };

        return StudioJsonFormat.Object(fields, indent);
    }

    private static StudioFallbackForm FallbackOf(string item)
    {
        using JsonDocument document = JsonDocument.Parse(item);
        JsonElement root = document.RootElement;

        var steps = ImmutableArray.CreateBuilder<StudioPlanStep>();

        if (root.TryGetProperty("steps", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement step in list.EnumerateArray())
            {
                // 파일에 적힌 순서를 그대로 둔다 — 정렬하면 무변경 저장이 바이트 동일이 아니게 된다.
                ImmutableArray<StudioArg> args = step.TryGetProperty("args", out JsonElement a)
                        && a.ValueKind == JsonValueKind.Object
                    ? [.. a.EnumerateObject().Select(p => new StudioArg(p.Name, p.Value.GetRawText()))]
                    : [];

                steps.Add(new StudioPlanStep(
                    step.GetProperty("action").GetString() ?? string.Empty,
                    args,
                    step.TryGetProperty("timeout_s", out JsonElement t) ? t.GetInt32() : 0));
            }
        }

        return new StudioFallbackForm(
            root.GetProperty("id").GetString()!,
            root.TryGetProperty("archetype", out JsonElement arch) ? arch.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("goal", out JsonElement goal) ? goal.GetString() ?? string.Empty : string.Empty,
            steps.ToImmutable(),
            root.TryGetProperty("loop", out JsonElement loop) && loop.GetBoolean(),
            root.TryGetProperty("on_step_fail", out JsonElement fail) ? fail.GetString() ?? "skip" : "skip");
    }

    private string ItemJson(string id)
    {
        string source = Read("archetypes.json");
        (int start, int end) = JsonSurgeon.ItemRange(source, "archetypes", "id", id);

        return source[start..end];
    }

    private static StudioArchetypeForm FormOf(string item)
    {
        using JsonDocument document = JsonDocument.Parse(item);
        JsonElement root = document.RootElement;

        JsonElement traits = root.TryGetProperty("traits", out JsonElement t) ? t : default;

        return new StudioArchetypeForm(
            root.GetProperty("id").GetString()!,
            root.TryGetProperty("desc", out JsonElement desc) ? desc.GetString() ?? string.Empty : string.Empty,
            Strings(root, "allowed_actions"),
            root.TryGetProperty("home_poi_type", out JsonElement home) ? home.GetString() ?? "house" : "house",
            root.TryGetProperty("workplace_poi_type", out JsonElement work) && work.ValueKind == JsonValueKind.String
                ? work.GetString()
                : null,
            Strings(root, "primary_recipes"),
            Trait(traits, "diligence"),
            Trait(traits, "sociability"),
            Trait(traits, "courage"),
            Trait(traits, "greed"),
            Strings(root, "default_goals"),
            Inventory(root),
            Duty(root),
            root.TryGetProperty("combat_capable", out JsonElement combat) && combat.GetBoolean(),
            root.TryGetProperty("population_weight", out JsonElement weight) ? weight.GetDouble() : 0);
    }

    /// <summary>
    /// 폼과 원문을 비교해 <b>바뀐 필드만</b> 고친다. 무변경이면 원문을 그대로 돌려준다 —
    /// <c>SaveArchetypeForm_WithoutChanges_IsByteIdentical</c> 이 그것을 강제한다.
    /// </summary>
    private static string ApplyForm(string item, StudioArchetypeForm form)
    {
        StudioArchetypeForm before = FormOf(item);
        string indent = StudioJsonFormat.IndentOf(item, "id", "      ");

        if (!string.Equals(before.Desc, form.Desc, StringComparison.Ordinal))
        {
            item = JsonSurgeon.SetOrAddTopLevel(item, "desc", StudioJsonFormat.Text(form.Desc), "allowed_actions");
        }

        if (!before.AllowedActions.SequenceEqual(form.AllowedActions, StringComparer.Ordinal))
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item, "allowed_actions", StudioJsonFormat.Strings(form.AllowedActions, indent), "home_poi_type");
        }

        if (!string.Equals(before.HomePoiType, form.HomePoiType, StringComparison.Ordinal))
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item, "home_poi_type", StudioJsonFormat.Text(form.HomePoiType), "workplace_poi_type");
        }

        if (!string.Equals(before.WorkplacePoiType, form.WorkplacePoiType, StringComparison.Ordinal))
        {
            item = form.WorkplacePoiType is { Length: > 0 } workplace
                ? JsonSurgeon.SetOrAddTopLevel(item, "workplace_poi_type", StudioJsonFormat.Text(workplace), "primary_recipes")
                : JsonSurgeon.SetOrAddTopLevel(item, "workplace_poi_type", "null", "primary_recipes");
        }

        if (!before.PrimaryRecipes.SequenceEqual(form.PrimaryRecipes, StringComparer.Ordinal))
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item, "primary_recipes", StudioJsonFormat.Strings(form.PrimaryRecipes, indent), "traits");
        }

        if (before.Diligence != form.Diligence || before.Sociability != form.Sociability
            || before.Courage != form.Courage || before.Greed != form.Greed)
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item,
                "traits",
                StudioJsonFormat.Object(
                    [
                        ("diligence", StudioJsonFormat.Number(form.Diligence)),
                        ("sociability", StudioJsonFormat.Number(form.Sociability)),
                        ("courage", StudioJsonFormat.Number(form.Courage)),
                        ("greed", StudioJsonFormat.Number(form.Greed)),
                    ],
                    indent),
                "default_goals");
        }

        if (!before.DefaultGoals.SequenceEqual(form.DefaultGoals, StringComparer.Ordinal))
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item, "default_goals", StudioJsonFormat.Strings(form.DefaultGoals, indent), "initial_inventory");
        }

        if (!before.InitialInventory.SequenceEqual(form.InitialInventory))
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item,
                "initial_inventory",
                StudioJsonFormat.Raw(
                    form.InitialInventory.Select(line => StudioJsonFormat.Object(
                        [("item", StudioJsonFormat.Text(line.Item)), ("count", StudioJsonFormat.Number(line.Count))],
                        indent + "  ")),
                    indent),
                "duty_hours");
        }

        if (!before.DutyHours.SequenceEqual(form.DutyHours))
        {
            item = form.DutyHours.IsEmpty
                ? JsonSurgeon.RemoveTopLevel(item, "duty_hours")
                : JsonSurgeon.SetOrAddTopLevel(
                    item,
                    "duty_hours",
                    StudioJsonFormat.Strings(form.DutyHours.Select(d => d.ToString()), indent),
                    "fallback_plan");
        }

        if (before.CombatCapable != form.CombatCapable)
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item, "combat_capable", StudioJsonFormat.Bool(form.CombatCapable), "population_weight");
        }

        if (Math.Abs(before.PopulationWeight - form.PopulationWeight) > 1e-9)
        {
            item = JsonSurgeon.SetOrAddTopLevel(
                item, "population_weight", StudioJsonFormat.Weight(form.PopulationWeight));
        }

        return item;
    }

    private static ImmutableArray<string> Strings(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0)]
            : [];

    private static int Trait(JsonElement traits, string name) =>
        traits.ValueKind == JsonValueKind.Object && traits.TryGetProperty(name, out JsonElement value)
            ? value.GetInt32()
            : 50;

    private static ImmutableArray<StudioInventoryLine> Inventory(JsonElement root) =>
        root.TryGetProperty("initial_inventory", out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(e => new StudioInventoryLine(
                e.GetProperty("item").GetString() ?? string.Empty,
                e.TryGetProperty("count", out JsonElement c) ? c.GetInt32() : 1))]
            : [];

    private static ImmutableArray<TimeOfDay> Duty(JsonElement root) =>
        root.TryGetProperty("duty_hours", out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray()
                .Select(e => Enum.TryParse(e.GetString(), out TimeOfDay time) ? (TimeOfDay?)time : null)
                .Where(t => t is not null)
                .Select(t => t!.Value)]
            : [];

    /// <summary>
    /// 마법사가 만든 새 직업을 한 트랜잭션으로 넣는다 (T12).
    ///
    /// <para>
    /// <b>중간에 깨진 상태를 만들지 않는다</b> — 직업만 있고 하루 일과가 없으면 V7 로 기동이 막히고,
    /// 버킷 수를 안 올리면 V6 이 걸린다. 표시 이름(V14)까지 같이 넣는다.
    /// </para>
    /// </summary>
    /// <param name="draft">마법사 초안.</param>
    public StudioSaveResult CreateArchetype(StudioNewArchetype draft)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(draft);
        ValidateNewId(draft.Id);

        lock (_gate)
        {
            MasterDataSet data = Data();

            // InvalidDataException 이다 — ArgumentException 은 메시지 뒤에
            // " (Parameter 'draft')" 가 붙어 "내가 뭘 잘못 넣었나" 로 읽힌다 (H08).
            if (data.Archetypes.TryGet(draft.Id, out _))
            {
                throw new InvalidDataException($"직업 '{draft.Id}' 가 이미 있다.");
            }

            if (!data.Archetypes.TryGet(draft.From, out ArchetypeDef source))
            {
                throw new InvalidDataException($"복제할 직업 '{draft.From}' 이 없다.");
            }

            string archetypes = Read("archetypes.json");
            (int start, int end) = JsonSurgeon.ItemRange(archetypes, "archetypes", "id", draft.From);
            string item = archetypes[start..end];
            int code = CodeAllocator.Next(_directory, "archetypes.json");

            item = JsonSurgeon.SetTopLevel(item, "id", StudioJsonFormat.Text(draft.Id));
            item = JsonSurgeon.SetTopLevel(item, "code", StudioJsonFormat.Number(code));
            item = JsonSurgeon.SetTopLevel(item, "name_key", StudioJsonFormat.Text("npc." + draft.Id));
            item = JsonSurgeon.SetTopLevel(item, "desc", StudioJsonFormat.Text(draft.Description));
            item = JsonSurgeon.SetTopLevel(item, "fallback_plan", StudioJsonFormat.Text("fb_" + draft.Id));
            item = JsonSurgeon.SetOrAddTopLevel(
                item,
                "workplace_poi_type",
                draft.WorkplacePoiType is { Length: > 0 } workplace
                    ? StudioJsonFormat.Text(workplace)
                    : "null",
                "primary_recipes");
            item = JsonSurgeon.SetTopLevel(item, "population_weight", StudioJsonFormat.Weight(draft.Weight));

            // 재배분 안의 다른 줄을 먼저 반영하고, 새 항목을 뒤에 붙인다.
            foreach (WeightChange change in draft.Rebalance)
            {
                if (string.Equals(change.Archetype, draft.Id, StringComparison.Ordinal)
                    || Math.Abs(change.From - change.To) <= 1e-9)
                {
                    continue;
                }

                archetypes = JsonSurgeon.SetInArrayItem(
                    archetypes, "archetypes", "id", change.Archetype,
                    "population_weight", StudioJsonFormat.Weight(change.To));
            }

            archetypes = JsonSurgeon.AppendToArray(archetypes, "archetypes", StudioJsonFormat.Dedent(item));

            string fallbacks = Read("fallback_plans.json");
            (start, end) = JsonSurgeon.ItemRange(fallbacks, "plans", "id", source.FallbackPlanId);
            string plan = fallbacks[start..end];
            plan = JsonSurgeon.SetTopLevel(plan, "id", StudioJsonFormat.Text("fb_" + draft.Id));
            plan = JsonSurgeon.SetTopLevel(plan, "archetype", StudioJsonFormat.Text(draft.Id));

            // H06 — 마법사 4단계에서 스텝을 손봤으면 복제 대신 그것을 쓴다.
            if (!draft.Steps.IsDefaultOrEmpty)
            {
                string planIndent = StudioJsonFormat.IndentOf(plan, "id", "      ");

                plan = JsonSurgeon.SetOrAddTopLevel(
                    plan,
                    "steps",
                    StudioJsonFormat.Raw(
                        draft.Steps.Select(step => StepJson(step, planIndent + "  ")), planIndent),
                    "loop");
            }

            if (draft.Goal.Length > 0)
            {
                plan = JsonSurgeon.SetOrAddTopLevel(plan, "goal", StudioJsonFormat.Text(draft.Goal), "steps");
            }

            fallbacks = JsonSurgeon.AppendToArray(fallbacks, "plans", StudioJsonFormat.Dedent(plan));

            string buckets = JsonSurgeon.SetTopLevel(
                Read("context_buckets.json"),
                "total_keys",
                StudioJsonFormat.Number((data.Archetypes.Count + 1) * BucketSpace.PerArchetype));

            var candidates = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["archetypes.json"] = archetypes,
                ["fallback_plans.json"] = fallbacks,
                ["context_buckets.json"] = buckets,
            };

            // 근무 허가를 같이 준다. 원본의 하루 일과가 $workplace·$nearest_field 를 쓰는데
            // 그 장소들의 allowed_archetypes 에 새 직업이 없으면 V3.UNREACHABLE_POI 로 기동이 막힌다 —
            // 사람이 손으로 해야 했던 일이고, 빠뜨리면 "만들었는데 안 돈다" 가 된다.
            if (Permit(data, draft, source) is { Length: > 0 } pois)
            {
                candidates["pois.json"] = pois;
            }

            // 표시 이름이 없으면 V14 가 잡는다 — 마법사가 받은 이름을 그대로 넣는다.
            foreach ((string locale, string name) in draft.Names)
            {
                string path = Path.Combine(
                    _directory, LocalizationTable.FolderName, locale + ".json");

                if (!File.Exists(path))
                {
                    continue;
                }

                // 키는 언제나 슬래시다 — `Path.Combine` 은 Windows 에서 역슬래시를 주고,
                // 그러면 미리보기 목록(H05)과 실제 쓴 목록이 글자만 달라 드리프트 테스트가 깨진다.
                candidates[LocalizationTable.FolderName + "/" + locale + ".json"] =
                    JsonSurgeon.SetOrAddTopLevel(File.ReadAllText(path), "npc." + draft.Id, StudioJsonFormat.Text(name));
            }

            return ValidateAndWrite(candidates, label: $"새 직업 {draft.Id}");
        }
    }

    /// <summary>
    /// 이 초안이 <b>만질 파일 목록</b> (H05). 마법사 5단계가 이것을 그대로 보여 준다.
    ///
    /// <b>손으로 적지 않는다</b> — 매뉴얼이 "네 파일" 이라 적고 코드는 여섯 개를 쓰고 있었다.
    /// </summary>
    /// <param name="draft">마법사 초안.</param>
    public ImmutableArray<string> PreviewCreateArchetype(StudioNewArchetype draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        lock (_gate)
        {
            var files = ImmutableArray.CreateBuilder<string>(6);

            files.Add("archetypes.json");
            files.Add("fallback_plans.json");
            files.Add("context_buckets.json");

            MasterDataSet data = Data();

            if (data.Archetypes.TryGet(draft.From, out ArchetypeDef source)
                && Permit(data, draft, source).Length > 0)
            {
                files.Add("pois.json");
            }

            foreach ((string locale, string _) in draft.Names.IsDefaultOrEmpty ? [] : draft.Names)
            {
                string path = Path.Combine(_directory, LocalizationTable.FolderName, locale + ".json");

                if (File.Exists(path))
                {
                    files.Add(LocalizationTable.FolderName + "/" + locale + ".json");
                }
            }

            return [.. files.Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)];
        }
    }

    /// <summary>이 초안에게 근무 허가가 붙는 장소 id (H05). 5단계 요약이 보여 준다.</summary>
    /// <param name="draft">마법사 초안.</param>
    public ImmutableArray<string> PermittedPlaces(StudioNewArchetype draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        lock (_gate)
        {
            MasterDataSet data = Data();

            if (!data.Archetypes.TryGet(draft.From, out ArchetypeDef source))
            {
                return [];
            }

            return
            [
                .. data.Pois.Pois
                    .Where(poi => poi.AllowedArchetypeMask != 0 && poi.IsWorkSite)
                    .Where(poi =>
                        (draft.WorkplacePoiType is { Length: > 0 } type
                            && string.Equals(poi.Subtype, type, StringComparison.Ordinal))
                        || poi.Allows(source.Code))
                    .Select(poi => poi.Id)
                    .Order(StringComparer.Ordinal),
            ];
        }
    }

    /// <summary>
    /// 새 직업에게 근무 허가를 준다 (T12). 바꿀 것이 없으면 빈 문자열.
    ///
    /// <para>
    /// 대상은 두 가지다 — 새 일터 유형의 장소, 그리고 <b>복제 원본이 일하던 장소</b>.
    /// 하루 일과를 베껴 왔으므로 <c>$nearest_field</c> 같은 심볼이 원본과 같은 곳을 가리킨다.
    /// </para>
    /// </summary>
    private string Permit(MasterDataSet data, StudioNewArchetype draft, ArchetypeDef source)
    {
        var targets = new HashSet<string>(StringComparer.Ordinal);

        foreach (PoiDef poi in data.Pois.Pois)
        {
            // 제한이 없는 장소는 건드릴 것이 없다 (마스크 0 = 누구나).
            if (poi.AllowedArchetypeMask == 0 || !poi.IsWorkSite)
            {
                continue;
            }

            bool sameType = draft.WorkplacePoiType is { Length: > 0 } type
                && string.Equals(poi.Subtype, type, StringComparison.Ordinal);

            if (sameType || poi.Allows(source.Code))
            {
                _ = targets.Add(poi.Id);
            }
        }

        if (targets.Count == 0)
        {
            return string.Empty;
        }

        string pois = Read("pois.json");
        string indent = StudioJsonFormat.IndentOf(pois, "id", "    ");

        foreach (string id in targets.OrderBy(t => t, StringComparer.Ordinal))
        {
            (int start, int end) = JsonSurgeon.ItemRange(pois, "pois", "id", id);
            string item = pois[start..end];

            if (!JsonSurgeon.HasTopLevel(item, "allowed_archetypes"))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(item);

            ImmutableArray<string> allowed =
            [
                .. document.RootElement.GetProperty("allowed_archetypes")
                    .EnumerateArray()
                    .Select(e => e.GetString() ?? string.Empty)
                    .Where(v => v.Length > 0),
            ];

            if (allowed.Contains(draft.Id, StringComparer.Ordinal))
            {
                continue;
            }

            item = JsonSurgeon.SetTopLevel(
                item,
                "allowed_archetypes",
                StudioJsonFormat.Strings(allowed.Add(draft.Id), indent + "  "));

            pois = pois[..start] + item + pois[end..];
        }

        return pois;
    }

    /// <summary>
    /// 전역 검색 색인 (T32). 카탈로그를 읽을 때 한 번 만든다 —
    /// <b>NPC 5,000명은 넣지 않는다</b>. 번호는 검색어에서 바로 주소로 만든다.
    /// </summary>
    public ImmutableArray<StudioSearchEntry> SearchIndex()
    {
        lock (_gate)
        {
            MasterDataSet data = Data();
            var entries = ImmutableArray.CreateBuilder<StudioSearchEntry>(256);

            foreach (ArchetypeDef def in data.Archetypes.Archetypes)
            {
                entries.Add(new StudioSearchEntry(
                    "직업", def.Id, Lexicon.ArchetypeName(data, def.Id), "/archetypes/" + def.Id));
            }

            foreach (ZoneDef zone in data.Zones.Zones)
            {
                entries.Add(new StudioSearchEntry(
                    "지역", zone.Id, Lexicon.Zone(data, zone.Id), "/places/" + zone.Id));
            }

            foreach (PoiDef poi in data.Pois.Pois)
            {
                entries.Add(new StudioSearchEntry(
                    "장소",
                    poi.Id,
                    Lexicon.PlaceName(poi),
                    $"/places/{data.Zones[poi.Zone].Id}?poi={poi.Id}"));
            }

            foreach (InterruptRule rule in data.Interrupts.Rules)
            {
                entries.Add(new StudioSearchEntry("돌발 반응", rule.Id, rule.Id, "/interrupts"));
            }

            foreach (ActionDef action in data.Actions.Actions)
            {
                entries.Add(new StudioSearchEntry("행동", action.Id, Lexicon.Action(action.Id), "/actions"));
            }

            return entries.ToImmutable();
        }
    }

    /// <summary>현재 디스크 상태를 검증한다. <b>V14(표시 이름)까지 본다</b> (H15).</summary>
    public ImmutableArray<StudioIssue> Validate()
    {
        lock (_gate)
        {
            return ValidateLocked(Data());
        }
    }

    /// <summary>검증기(V0~V13) + 표시 이름(V14). 화면의 "검증 결과" 가 이 합이다.</summary>
    private ImmutableArray<StudioIssue> ValidateLocked(MasterDataSet data) =>
        [.. ToIssues(MasterDataValidator.Validate(_directory)), .. MissingNames(data, _directory)];

    /// <summary>
    /// V14 — 표시 이름 누락 (H15).
    ///
    /// <para>
    /// <b>검증기는 V0~V13 만 돈다.</b> V14 는 <c>Npc.Host</c> 기동 경고라 <c>MasterDataValidator</c>
    /// 밖에 있고, 그래서 Studio 화면이 "V1~V15 위반이 없다" 라고 적는데 정작 V14 는 한 번도
    /// 돌지 않았다 — 마법사로 만든 직업의 한국어 이름이 빠져도 초록이었다.
    /// 여기서 합쳐 준다. <b>검증기 자체는 건드리지 않는다</b> (기동 경로의 판정이 바뀌면 안 된다).
    /// </para>
    /// </summary>
    private static ImmutableArray<StudioIssue> MissingNames(MasterDataSet data, string directory)
    {
        var issues = ImmutableArray.CreateBuilder<StudioIssue>();

        foreach (LocalizationTable locale in data.Locales)
        {
            foreach (string key in locale.Missing(data))
            {
                issues.Add(new StudioIssue(
                    "V14",
                    $"{locale.Locale}: 표시 이름 '{key}' 가 없다.",
                    LocalizationTable.FolderName + "/" + locale.Locale + ".json",
                    key,
                    FixHints.HintOf("V14")));
            }
        }

        _ = directory;

        return issues.ToImmutable();
    }

    /// <summary>
    /// 후보를 검증하고 통과하면 쓴다.
    /// </summary>
    /// <param name="candidates">파일 이름 → 내용.</param>
    /// <param name="loaderGate">
    /// 로더까지 통과시킬 것인가.
    ///
    /// <para>
    /// <b>장소를 더할 때와 연습장을 적용할 때만 <c>false</c> 다.</b> 장소를 하나 더하면
    /// <c>poi_distances.bin</c> 은 <b>정의상</b> 낡는다 (행렬이 옛 POI 수 기준이다).
    /// 로더는 그것을 정확히 잡아내고, 그래서 저장 자체가 막힌다 — 그런데 그 낡음을 없애려면
    /// 먼저 장소를 저장해야 한다. 순서를 뒤집을 수 없으므로 이 경로만 로더를 건너뛰고,
    /// <b>대신 파급 패널이 "거리표를 다시 만들어라" 를 즉시 띄운다</b> (T16·T17).
    /// 검증은 그대로 돈다.
    /// </para>
    /// </param>
    /// <param name="stamp">읽은 시점의 파일 해시 (H13). 디스크가 그 사이 바뀌었으면 거절한다.</param>
    /// <param name="label">무슨 작업이었는가. 되돌리기 목록이 이것을 보여 준다 (H14).</param>
    /// <param name="pinCheck">
    /// 번호 재배치를 막을 것인가 (H12).
    ///
    /// <b>되돌리기만 <c>false</c> 다.</b> 백업으로 돌아가면 그 사이에 추가된 id 가 사라지는데,
    /// 그것은 재배치가 아니라 <b>되돌리기의 정의</b>다 — 여기서 막으면 되돌릴 수 없게 된다.
    /// </param>
    private StudioSaveResult ValidateAndWrite(
        Dictionary<string, string> candidates,
        bool loaderGate = true,
        StudioStamp? stamp = null,
        string label = "",
        bool pinCheck = true)
    {
        if (GeneratorBusy)
        {
            return new StudioSaveResult(
                false, "명단을 다시 만드는 중이다 — 끝나면 저장한다.", []);
        }

        // H13 — 낙관적 동시성. 읽은 뒤 디스크가 바뀌었으면 마지막 저장이 조용히 이기게 두지 않는다.
        if (stamp?.Changed(_directory) is { Length: > 0 } stale)
        {
            return new StudioSaveResult(
                false,
                $"읽은 뒤 디스크가 바뀌었다 — 다시 읽고 고친다 (바뀐 파일: {string.Join(" · ", stale)}).",
                [.. stale.Select(file => new StudioIssue(
                    "STALE",
                    $"{file} 이(가) Studio 밖에서 바뀌었다. 상단 '새로고침' 을 누르면 새 값이 보인다.",
                    file,
                    string.Empty,
                    "다른 탭 · VS Code · 생성기가 같은 파일을 고쳤다. 새로고침한 뒤 다시 편집한다."))]);
        }

        // H12 — 번호 재배치는 검증도 로더도 못 잡는다. 여기서 막는다.
        if (pinCheck && PinViolations(candidates) is { Length: > 0 } pinned)
        {
            return new StudioSaveResult(false, "번호(code·bit)를 재배치해 저장하지 않았다.", pinned);
        }

        string temporary = Path.Combine(Path.GetTempPath(), "npc-studio-" + Path.GetRandomFileName());
        Directory.CreateDirectory(temporary);

        try
        {
            CopyMasterData(temporary);

            foreach ((string file, string content) in candidates)
            {
                string path = Path.Combine(temporary, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content, new UTF8Encoding(false));
            }

            MasterDataValidationReport report;

            try
            {
                report = MasterDataValidator.Validate(temporary);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                // 검증기는 모양이 스키마와 다르면 `catch` 없이 던진다 (배열 자리에 객체 등).
                // 그 예외가 화면까지 올라가면 회로가 죽는다 — 여기서 문제 하나로 바꾼다.
                return new StudioSaveResult(
                    false,
                    "파일 모양이 스키마와 다르다.",
                    [new StudioIssue("V0", StudioErrors.Humanize(ex), FirstFile(candidates), string.Empty,
                        "docs/schema/ 의 스키마를 에디터에 물려 필드 이름·타입을 맞춘다.")]);
            }

            ImmutableArray<StudioIssue> issues = ToIssues(report);

            if (!report.IsValid)
            {
                return new StudioSaveResult(false, "검증 실패로 저장하지 않았다.", issues);
            }

            if (loaderGate)
            {
                try
                {
                    MasterDataSet loaded = MasterDataLoader.Load(temporary);
                    _ = NpcInstanceTable.Load(Path.Combine(temporary, "npc_instances.json"), loaded);
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException)
                {
                    return new StudioSaveResult(
                        false,
                        "로더가 후보를 거절해 저장하지 않았다.",
                        [new StudioIssue("LOAD", StudioErrors.Humanize(ex), FirstFile(candidates), string.Empty,
                            FixHints.HintOf("V1"))]);
                }
            }

            // 후보 검증은 통과했다. 여기부터가 쓰기다 — 실패하면 되감는다 (H14).
            ImmutableArray<string> written = Commit(candidates, label);

            InvalidateCache();

            return new StudioSaveResult(
                true,
                $"{candidates.Count}개 파일을 저장했다.",
                issues,
                written)
            {
                Label = label,
            };
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private void CopyMasterData(string destination) => CopyFrom(_directory, destination);

    /// <summary>
    /// 마스터데이터 한 벌을 <b>재귀로</b> 복사한다 (H04).
    ///
    /// 예전에는 최상위와 <c>localization/</c> 만 옮겼다. <c>prompt/</c> 가 빠져 있어서,
    /// 앞으로 검증이 그 폴더를 읽으면 연습장과 원본의 판정이 갈린다 — 그 어긋남은
    /// "연습장에서는 되는데 원본에서는 안 된다" 로 나타나고 원인을 찾을 계기가 없다.
    /// </summary>
    private static void CopyFrom(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string folder in Directory.EnumerateDirectories(source))
        {
            CopyFrom(folder, Path.Combine(destination, Path.GetFileName(folder)));
        }
    }

    /// <summary>후보 중 첫 파일 이름. 오류를 어느 파일에 붙일지 정한다.</summary>
    private static string FirstFile(Dictionary<string, string> candidates) =>
        candidates.Keys.OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault() ?? string.Empty;

    /// <summary>번호 고정 위반 (H12). 없으면 빈 배열.</summary>
    private ImmutableArray<StudioIssue> PinViolations(Dictionary<string, string> candidates)
    {
        var issues = ImmutableArray.CreateBuilder<StudioIssue>();

        foreach ((string file, string content) in candidates.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!CodePinning.IsNumbered(file))
            {
                continue;
            }

            string path = Path.Combine(_directory, file);

            if (!File.Exists(path))
            {
                continue;
            }

            foreach (CodeViolation violation in CodePinning.Check(file, File.ReadAllText(path), content))
            {
                issues.Add(new StudioIssue(
                    "PIN",
                    violation.Detail,
                    violation.File,
                    violation.Path,
                    "번호는 그대로 두고 필요한 항목을 맨 뒤에 더한다. 삭제는 `npc` CLI 로 파급을 보고 한다."));
            }
        }

        return issues.ToImmutable();
    }

    /// <summary>
    /// 실제 쓰기 (H14). ① 전부 백업 → ② 전부 <c>.studio.tmp</c> → ③ 전부 <c>Move</c>.
    ///
    /// <para>
    /// <b>③ 에서 하나라도 실패하면 이미 옮긴 것을 백업으로 되감는다.</b> 완전한 원자성은
    /// 파일 시스템이 주지 않지만 <b>되감기까지</b> 는 준다 — 마법사가 6개 파일 중 2개만 쓴 채
    /// 남으면 V7 로 기동이 막히고, 그 상태는 사람이 손으로 풀 수 없다.
    /// </para>
    /// </summary>
    private ImmutableArray<string> Commit(Dictionary<string, string> candidates, string label)
    {
        ImmutableArray<string> files = [.. candidates.Keys.OrderBy(f => f, StringComparer.Ordinal)];
        string stamp = BackupTransaction(files, label);
        var moved = new List<string>(files.Length);
        var temporaries = new List<string>(files.Length);

        try
        {
            foreach (string file in files)
            {
                string path = Path.Combine(_directory, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                string temporary = path + ".studio.tmp";
                File.WriteAllText(temporary, candidates[file], new UTF8Encoding(false));
                temporaries.Add(temporary);
            }

            foreach (string file in files)
            {
                string path = Path.Combine(_directory, file);
                File.Move(path + ".studio.tmp", path, overwrite: true);
                moved.Add(file);
            }

            return files;
        }
        catch (Exception)
        {
            Rewind(stamp, moved);
            throw;
        }
        finally
        {
            foreach (string temporary in temporaries)
            {
                try
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 남은 .studio.tmp 는 다음 저장이 덮는다.
                }
            }
        }
    }

    /// <summary>옮긴 파일을 방금 만든 백업으로 되돌린다 (H14).</summary>
    private void Rewind(string stamp, List<string> moved)
    {
        if (stamp.Length == 0)
        {
            return;
        }

        string folder = Path.Combine(BackupRoot, stamp);

        foreach (string file in moved)
        {
            string source = Path.Combine(folder, file);
            string target = Path.Combine(_directory, file);

            try
            {
                if (File.Exists(source))
                {
                    File.Copy(source, target, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 되감기까지 실패하면 사람이 백업 폴더에서 꺼내야 한다 — 그 경로를 결과가 말한다.
            }
        }
    }

    // ------------------------------------------------------------------ 되돌리기 (T18 · H14)

    /// <summary>
    /// 백업을 두는 곳. <b><c>masterdata/</c> 안에 두지 않는다</b> —
    /// <c>CopyMasterData</c>·<c>ContentHash</c> 가 폴더 전체를 보므로 백업이 입력으로 섞인다.
    ///
    /// <b>편집 대상 폴더마다 따로 둔다.</b> 한 곳에 모으면 연습장·다른 <c>masterdata/</c> 의
    /// 백업이 같은 목록에 섞이고, 되돌리기가 남의 파일을 덮어쓴다.
    /// </summary>
    private string BackupRoot =>
        Path.Combine(
            options.BackupRoot.Length > 0
                ? options.BackupRoot
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NpcStudio",
                    "backup"),
            FolderKey(_directory));

    /// <summary>백업 보존 기간(일). 지난 것은 저장할 때 지운다.</summary>
    public const int BackupDays = 30;

    /// <summary>파일마다 날짜와 무관하게 남기는 최근 백업 수 (H14).</summary>
    public const int BackupKeepPerFile = 5;

    /// <summary>
    /// 되돌릴 수 있는 트랜잭션 (최신 순).
    ///
    /// <b>파일 하나가 아니라 저장 한 번이 단위다</b> (H14). 마법사는 6개 파일을 같이 쓰는데,
    /// <c>archetypes.json</c> 만 되돌리면 V7(하루 일과 없는 직업)로 기동이 막히고
    /// <c>fallback_plans.json</c> 만 먼저 되돌려도 V7 이다 — 어느 순서로도 단일 파일 복원은
    /// 통과하지 못한다.
    /// </summary>
    public ImmutableArray<StudioBackup> Backups()
    {
        lock (_gate)
        {
            string root = BackupRoot;

            if (!Directory.Exists(root))
            {
                return [];
            }

            var list = ImmutableArray.CreateBuilder<StudioBackup>();

            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                string stamp = Path.GetFileName(folder);
                ImmutableArray<string> files = BackedUpFiles(folder);

                if (files.IsEmpty)
                {
                    continue;
                }

                list.Add(new StudioBackup(stamp, files[0], folder, StampTime(stamp))
                {
                    Files = files,
                    Label = LabelOf(folder),
                });
            }

            // 폴더 이름이 저장한 시각이다. 파일의 수정 시각으로 세우지 않는다 —
            // File.Copy 가 원본의 시각을 그대로 옮기므로 그 값은 "저장한 때" 가 아니다.
            return [.. list.OrderByDescending(b => b.Stamp, StringComparer.Ordinal)];
        }
    }

    /// <summary>백업 폴더에 든 파일의 상대 경로 (<c>localization/ko-KR.json</c> 포함).</summary>
    private static ImmutableArray<string> BackedUpFiles(string folder)
    {
        var files = ImmutableArray.CreateBuilder<string>();

        foreach (string path in Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(folder, path).Replace('\\', '/');

            if (!string.Equals(relative, "manifest.json", StringComparison.Ordinal))
            {
                files.Add(relative);
            }
        }

        return [.. files.OrderBy(f => f, StringComparer.Ordinal)];
    }

    private static string LabelOf(string folder)
    {
        string path = Path.Combine(folder, "manifest.json");

        if (!File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));

            return document.RootElement.TryGetProperty("label", out JsonElement label)
                ? label.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 백업으로 되돌린다. <b>묶음 전부를 한 트랜잭션으로 되돌린다</b> (H14) —
    /// 되돌린 상태도 다시 검증한다 (옛 파일이 지금 다른 파일과 맞는다는 보장이 없다).
    /// </summary>
    /// <param name="backup">되돌릴 트랜잭션.</param>
    public StudioSaveResult Restore(StudioBackup backup)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(backup);

        lock (_gate)
        {
            var candidates = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string file in backup.Files.IsDefaultOrEmpty ? [backup.File] : backup.Files)
            {
                EnsureRestorable(file);

                string path = Directory.Exists(backup.Path)
                    ? Path.Combine(backup.Path, file)
                    : backup.Path;

                if (File.Exists(path))
                {
                    candidates[file] = File.ReadAllText(path);
                }
            }

            if (candidates.Count == 0)
            {
                return new StudioSaveResult(false, "그 백업에서 되돌릴 파일을 찾지 못했다.", []);
            }

            // 장소가 줄어드는 복원이면 거리표가 낡는다 — 로더 게이트를 켜면 복원이 영영 막힌다.
            bool poisChanged = candidates.ContainsKey("pois.json");

            StudioSaveResult result = ValidateAndWrite(
                candidates, loaderGate: !poisChanged, label: $"백업 복원 {backup.Stamp}", pinCheck: false);

            return result.Saved
                ? result with { Message = $"{candidates.Count}개 파일을 {backup.SavedAt:MM-dd HH:mm} 상태로 되돌렸다." }
                : result;
        }
    }

    /// <summary>이 파일을 복원해도 되는가. 편집 파일과 로케일 파일만 받는다.</summary>
    private static void EnsureRestorable(string file)
    {
        bool locale = file.StartsWith(LocalizationTable.FolderName + "/", StringComparison.Ordinal)
            && file.EndsWith(".json", StringComparison.Ordinal);

        if (!locale)
        {
            EnsureEditable(file);
        }
    }

    /// <summary>
    /// 쓰기 직전의 원본을 한 폴더에 묶어 백업한다 (H14). 폴더 이름을 돌려준다.
    ///
    /// <para>
    /// <b>백업 실패는 저장 실패다.</b> 예전에는 삼켰다 — 되돌릴 수 있다고 믿고 저장한 사람은
    /// 끝까지 모른다. 기동 시 쓰기 검사(<c>BackupWritable</c>)가 먼저 알려 주고,
    /// 그래도 실패하면 여기서 던진다.
    /// </para>
    /// </summary>
    private string BackupTransaction(ImmutableArray<string> files, string label)
    {
        string root = BackupRoot;
        string stampName = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string folder = Path.Combine(root, stampName);

        // 같은 초에 두 번 저장하면 앞의 백업이 덮인다 — 첫 번째 상태로 되돌릴 곳이 없어진다.
        for (int n = 2; Directory.Exists(folder); n++)
        {
            stampName = string.Create(CultureInfo.InvariantCulture, $"{stampName}-{n}");
            folder = Path.Combine(root, stampName);
        }

        Directory.CreateDirectory(folder);

        int copied = 0;

        foreach (string file in files)
        {
            string source = Path.Combine(_directory, file);

            if (!File.Exists(source))
            {
                // 새로 만드는 파일(새 로케일 등)은 되돌릴 원본이 없다.
                continue;
            }

            // 경로를 보존한다 — `localization/ko-KR.json` 을 `ko-KR.json` 으로 눕히면
            // 목록엔 뜨는데 복원할 때 "편집할 수 없는 파일" 로 거절된다.
            string target = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            copied++;
        }

        File.WriteAllText(
            Path.Combine(folder, "manifest.json"),
            JsonSerializer.Serialize(
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["label"] = label,
                    ["files"] = files.ToArray(),
                    ["count"] = copied,
                },
                s_indentedJson),
            new UTF8Encoding(false));

        Prune(root);

        return stampName;
    }

    /// <summary>
    /// 오래된 백업을 지운다. <b>파일별 최근 <see cref="BackupKeepPerFile"/> 개는 날짜와 무관하게 남긴다</b> —
    /// 한 달 쉬었다 돌아온 사람에게 되돌릴 것이 하나도 없으면 안 된다.
    /// </summary>
    private static void Prune(string root)
    {
        DateTime cutoff = DateTime.Now.AddDays(-BackupDays);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var perFile = new Dictionary<string, int>(StringComparer.Ordinal);

        string[] folders = [.. Directory.EnumerateDirectories(root).OrderByDescending(f => f, StringComparer.Ordinal)];

        foreach (string folder in folders)
        {
            foreach (string file in BackedUpFiles(folder))
            {
                int seen = perFile.GetValueOrDefault(file);

                if (seen < BackupKeepPerFile)
                {
                    perFile[file] = seen + 1;
                    _ = keep.Add(folder);
                }
            }
        }

        foreach (string folder in folders)
        {
            if (keep.Contains(folder) || Directory.GetLastWriteTime(folder) >= cutoff)
            {
                continue;
            }

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 정리는 보조다.
            }
        }
    }

    /// <summary>파일 하나의 SHA-256. 없으면 빈 문자열.</summary>
    private static string Sha(string path)
    {
        if (!File.Exists(path))
        {
            return string.Empty;
        }

        using FileStream stream = File.OpenRead(path);

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>
    /// 지금 디스크 상태의 지문 (H13). 저장할 때 이것과 견주어 <b>그 사이의 외부 편집</b>을 거절한다.
    /// </summary>
    /// <param name="files">지문에 넣을 파일 (상대 경로).</param>
    public StudioStamp StampOf(params string[] files)
    {
        ArgumentNullException.ThrowIfNull(files);

        lock (_gate)
        {
            return StampLocked(files);
        }
    }

    private StudioStamp StampLocked(IEnumerable<string> files) =>
        new([.. files.Distinct(StringComparer.Ordinal)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, Sha(Path.Combine(_directory, f))))]);

    /// <summary>폴더 경로를 파일 이름으로 쓸 수 있는 짧은 키로 바꾼다.</summary>
    private static string FolderKey(string directory)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant();
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(full));

        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>백업 폴더 이름에서 저장 시각을 읽는다. 읽지 못하면 <see cref="DateTime.MinValue"/>.</summary>
    private static DateTime StampTime(string stamp) =>
        stamp.Length >= 15
        && DateTime.TryParseExact(
            stamp[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime at)
            ? at
            : DateTime.MinValue;

    private static void EnsureItemId(string json, string expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("id", out JsonElement id)
            || !string.Equals(id.GetString(), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"편집 중에는 id를 바꿀 수 없다. 예상 id는 '{expected}'이다.");
        }
    }

    private static void ValidateNewId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)
            || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))
            || char.IsDigit(id[0]))
        {
            throw new ArgumentException("id는 영문자로 시작하는 영문 소문자·숫자·밑줄 조합이어야 한다.", nameof(id));
        }

        if (id.Any(char.IsUpper))
        {
            throw new ArgumentException("id에는 대문자를 쓸 수 없다.", nameof(id));
        }
    }

    private void EnsureWritable()
    {
        if (options.ReadOnly)
        {
            throw new InvalidOperationException("읽기 전용 모드에서는 저장할 수 없다.");
        }
    }

    private static void EnsureEditable(string fileName)
    {
        if (!s_editableFiles.Contains(fileName) || Path.GetFileName(fileName) != fileName)
        {
            throw new ArgumentException($"Studio에서 편집할 수 없는 파일이다: {fileName}", nameof(fileName));
        }
    }

    private string Read(string fileName) => File.ReadAllText(Path.Combine(_directory, fileName));

    private HashSet<int> LoadOverrideIds()
    {
        using JsonDocument document = JsonDocument.Parse(Read(NpcInstanceTable.OverrideFileName));
        var ids = new HashSet<int>();

        if (document.RootElement.TryGetProperty("overrides", out JsonElement overrides))
        {
            foreach (JsonElement item in overrides.EnumerateArray())
            {
                if (item.TryGetProperty("id", out JsonElement id) && id.TryGetInt32(out int value))
                {
                    ids.Add(value);
                }
            }
        }

        return ids;
    }

    private static JsonObject? FindOverride(JsonArray overrides, int id) =>
        overrides.OfType<JsonObject>().FirstOrDefault(item => item["id"]?.GetValue<int>() == id);

    private static string PoiTypeLabel(PoiType type) => type switch
    {
        PoiType.Home => "집",
        PoiType.Workplace => "일터",
        PoiType.Market => "시장",
        PoiType.Tavern => "선술집",
        PoiType.Temple => "신전",
        PoiType.Gate => "성문",
        PoiType.Field => "농경지",
        PoiType.Wilderness => "야외",
        _ => type.ToString(),
    };

    private static string Text(string value) => JsonSerializer.Serialize(value, JsonSurgeonText.Options);

    private static ImmutableArray<StudioIssue> ToIssues(MasterDataValidationReport report) =>
    [
        .. report.Violations.Select(v => new StudioIssue(v.Code, v.Detail, v.File, v.Path, v.FixHint)),
    ];
}

/// <summary>목록 화면 데이터.</summary>
public sealed record StudioCatalog(
    string Directory,
    bool ReadOnly,
    ImmutableArray<StudioArchetype> Archetypes,
    int InstanceCount,
    ImmutableArray<StudioIssue> Issues)
{
    /// <summary>장소 수. 시작 화면의 "마을 한눈에" 타일이 쓴다.</summary>
    public int PoiCount { get; init; }

    /// <summary>지역 수.</summary>
    public int ZoneCount { get; init; }

    /// <summary>돌발 반응 규칙 수.</summary>
    public int InterruptCount { get; init; }

    /// <summary>행동 수. 상한 40 중 몇 개인지 화면이 적는다.</summary>
    public int ActionCount { get; init; }

    /// <summary>아이템 수.</summary>
    public int ItemCount { get; init; }

    /// <summary>낡은 파생물 이름. 비어 있으면 최신이다 (T16·T17).</summary>
    public ImmutableArray<string> StaleArtifacts { get; init; } = [];

    /// <summary>연습장 이름 (T30). 원본을 보고 있으면 빈 문자열이다.</summary>
    public string Sandbox { get; init; } = string.Empty;

    /// <summary>지금 보고 있는 것이 연습장인가.</summary>
    public bool IsSandbox => Sandbox.Length > 0;

    /// <summary>
    /// 로더가 이 폴더를 거절한 이유. 비어 있으면 정상이다.
    /// <b>차 있으면 목록·개수가 전부 0 이다</b> — 읽을 수 없어서 못 센 것이지 없는 것이 아니다.
    /// </summary>
    public string Blocked { get; init; } = string.Empty;

    /// <summary>화면을 열 수 없는 상태인가.</summary>
    public bool IsBlocked => Blocked.Length > 0;

    /// <summary>막힌 사유의 종류 (H07). 화면이 이것으로 탈출구를 고른다.</summary>
    public StudioBlockKind Kind { get; init; }

    /// <summary>로더가 준 원문. 접이식으로 보여 준다.</summary>
    public string BlockedDetail { get; init; } = string.Empty;

    /// <summary>
    /// 거리표를 읽었는가 (H07). 거짓이면 화면에 배너가 뜨고 소요 예측을 믿을 수 없다.
    /// </summary>
    public bool DistancesAvailable { get; init; } = true;

    /// <summary>백업 폴더에 쓸 수 있는가 (H14). 거짓이면 되돌리기를 만들 수 없다.</summary>
    public bool BackupWritable { get; init; } = true;
}

/// <summary>마스터데이터를 읽지 못한 이유 (H07).</summary>
public enum StudioBlockKind
{
    /// <summary>막히지 않았다.</summary>
    None,

    /// <summary>거리표가 입력과 어긋난다 — 다시 만들면 된다.</summary>
    StaleDistances,

    /// <summary>파생물이 아예 없다 — 갓 클론한 저장소.</summary>
    MissingArtifact,

    /// <summary>JSON 문법이 깨졌다 — 원문을 고쳐야 한다.</summary>
    JsonSyntax,

    /// <summary>로더가 참조 무결성으로 거절했다.</summary>
    LoaderReject,
}

/// <summary>연습장 하나 (H04).</summary>
/// <param name="Name">사람이 붙인 이름.</param>
/// <param name="Path">마스터데이터 경로.</param>
/// <param name="CreatedAt">만든 시각.</param>
/// <param name="ChangedFiles">원본과 다른 파일 수.</param>
public sealed record StudioSandbox(string Name, string Path, DateTime CreatedAt, int ChangedFiles);

/// <summary>
/// 같은 이름의 연습장이 이미 있다 (H04).
/// <b>덮어쓰지 않고 묻는다</b> — 기본 이름이 <c>MMdd</c> 라 같은 날 두 번 누르면 아침 작업이 사라졌다.
/// </summary>
public sealed class SandboxExistsException : Exception
{
    /// <summary>이미 있는 연습장.</summary>
    /// <param name="name">이름.</param>
    /// <param name="changedFiles">그 연습장이 원본과 다른 파일.</param>
    public SandboxExistsException(string name, ImmutableArray<string> changedFiles)
        : base($"연습장 '{name}' 이 이미 있다.")
    {
        Name = name;
        ChangedFiles = changedFiles;
    }

    /// <summary>기본 생성자. 직접 쓰지 않는다.</summary>
    public SandboxExistsException()
        : base("연습장이 이미 있다.") => Name = string.Empty;

    /// <summary>메시지만 있는 생성자.</summary>
    /// <param name="message">메시지.</param>
    public SandboxExistsException(string message)
        : base(message) => Name = string.Empty;

    /// <summary>메시지와 내부 예외.</summary>
    /// <param name="message">메시지.</param>
    /// <param name="innerException">내부 예외.</param>
    public SandboxExistsException(string message, Exception innerException)
        : base(message, innerException) => Name = string.Empty;

    /// <summary>연습장 이름.</summary>
    public string Name { get; }

    /// <summary>그 연습장이 원본과 다른 파일.</summary>
    public ImmutableArray<string> ChangedFiles { get; } = [];
}

/// <summary>
/// 읽은 시점의 파일 지문 (H13). <b>진실은 디스크 해시다</b> —
/// 마지막 저장이 조용히 이기는 것은 "안전한 편집" 이 아니다.
/// </summary>
/// <param name="Files">파일 상대 경로와 그때의 SHA-256.</param>
public sealed record StudioStamp(ImmutableArray<(string File, string Sha)> Files)
{
    /// <summary>이 지문을 찍은 뒤 바뀐 파일. 없으면 빈 배열.</summary>
    /// <param name="directory">견줄 폴더.</param>
    public ImmutableArray<string> Changed(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var changed = ImmutableArray.CreateBuilder<string>();

        foreach ((string file, string sha) in Files)
        {
            string path = System.IO.Path.Combine(directory, file);
            string now = File.Exists(path)
                ? Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
                : string.Empty;

            if (!string.Equals(now, sha, StringComparison.Ordinal))
            {
                changed.Add(file);
            }
        }

        return changed.ToImmutable();
    }
}

/// <summary>아키타입 목록 한 줄.</summary>
public sealed record StudioArchetype(
    string Id,
    int Code,
    string Description,
    double Weight,
    int Population,
    string Workplace,
    bool CombatCapable);

/// <summary>편집 대상과 설명 카드.</summary>
public sealed record StudioArchetypeDocument(string Id, string Json, string Card)
{
    /// <summary>읽은 시점의 지문 (H13).</summary>
    public StudioStamp? Stamp { get; init; }
}

/// <summary>원문 편집기가 여는 파일 하나 (H13).</summary>
/// <param name="Name">파일 이름.</param>
/// <param name="Json">원문.</param>
/// <param name="Stamp">읽은 시점의 지문.</param>
public sealed record StudioFileDocument(string Name, string Json, StudioStamp Stamp);

/// <summary>
/// 아키타입 개요 보기 한 장 (T05). JSON 원문 대신 이 값들이 섹션 카드가 된다.
/// </summary>
/// <param name="Facts">정의에서 뽑은 사실들.</param>
/// <param name="Name">한국어 표시 이름.</param>
/// <param name="Group">직군.</param>
/// <param name="Fallback">하루 일과의 스텝 판정.</param>
/// <param name="Loop">고리 판정.</param>
/// <param name="ByZone">지역별 인구 (없으면 예측).</param>
/// <param name="Lint">건강 진단 (T29).</param>
public sealed record StudioArchetypeView(
    ArchetypeFacts Facts,
    string Name,
    Lexicon.ArchetypeGroup Group,
    ImmutableArray<StepTrace> Fallback,
    LoopVerdict Loop,
    ImmutableArray<StudioZoneCount> ByZone,
    ImmutableArray<LintFinding> Lint)
{
    /// <summary>아키타입 id.</summary>
    public string Id => Facts.Def.Id;

    /// <summary>직군 색 (부록 E).</summary>
    public string Color => Lexicon.ColorOf(Group);
}

/// <summary>되돌릴 수 있는 백업 하나 (T18).</summary>
/// <param name="Stamp">백업 시각 폴더 이름.</param>
/// <param name="File">파일 이름.</param>
/// <param name="Path">백업 파일 경로.</param>
/// <param name="SavedAt">백업된 시각.</param>
public sealed record StudioBackup(string Stamp, string File, string Path, DateTime SavedAt)
{
    /// <summary>이 트랜잭션이 담은 파일 전부 (H14). 복원은 묶음 단위다.</summary>
    public ImmutableArray<string> Files { get; init; } = [];

    /// <summary>무슨 작업이었는가 ("새 직업 beekeeper"). 목록이 이것을 보여 준다.</summary>
    public string Label { get; init; } = string.Empty;
}

/// <summary>전역 검색 결과 한 줄 (T32).</summary>
/// <param name="Kind">무엇인가 (직업·지역·장소·행동·돌발 반응).</param>
/// <param name="Key">id.</param>
/// <param name="Label">표시 이름.</param>
/// <param name="Url">갈 곳.</param>
public sealed record StudioSearchEntry(string Kind, string Key, string Label, string Url);

/// <summary>새 직업 마법사의 초안 (T12).</summary>
/// <param name="Id">새 직업 id.</param>
/// <param name="Description">설명. LLM 프롬프트에 그대로 실린다.</param>
/// <param name="From">복제 원본 id.</param>
/// <param name="Weight">인구 비율.</param>
/// <param name="WorkplacePoiType">일터 세부 유형. 없으면 null.</param>
/// <param name="Rebalance">인구를 어디서 뗄 것인가.</param>
/// <param name="Names">로케일 → 표시 이름 (V14).</param>
public sealed record StudioNewArchetype(
    string Id,
    string Description,
    string From,
    double Weight,
    string? WorkplacePoiType,
    ImmutableArray<WeightChange> Rebalance,
    ImmutableArray<(string Locale, string Name)> Names)
{
    /// <summary>
    /// 마법사 4단계에서 손본 하루 일과 스텝 (H06). 비면 닮은 직업의 것을 그대로 복제한다.
    /// </summary>
    public ImmutableArray<StudioPlanStep> Steps { get; init; } = [];

    /// <summary>하루 목표. 비면 원본의 것을 쓴다.</summary>
    public string Goal { get; init; } = string.Empty;
}

/// <summary>하루 일과 초안의 판정 (T11).</summary>
/// <param name="Plan">컴파일된 플랜. 실패면 null.</param>
/// <param name="Trace">스텝별 판정.</param>
/// <param name="Loop">고리 판정.</param>
/// <param name="Error">컴파일 실패 사유. 성공이면 빈 문자열.</param>
public sealed record StudioFallbackPreview(
    CompiledPlan? Plan, ImmutableArray<StepTrace> Trace, LoopVerdict Loop, string Error);

/// <summary>스텝 편집기가 고를 수 있는 값들 (T11).</summary>
/// <param name="Actions">이 직업이 쓸 수 있는 행동.</param>
/// <param name="Symbols">POI 심볼과 바인딩 가능 여부.</param>
/// <param name="Items">아이템 id.</param>
/// <param name="Recipes">레시피 id.</param>
public sealed record StudioStepChoices(
    ImmutableArray<StudioActionInfo> Actions,
    ImmutableArray<StudioSymbolInfo> Symbols,
    ImmutableArray<string> Items,
    ImmutableArray<string> Recipes);

/// <summary>스텝 편집기가 보는 행동 하나 (T11).</summary>
/// <param name="Id">액션 id.</param>
/// <param name="Name">한국어 표기.</param>
/// <param name="DefaultTimeoutSeconds">기본 최대 시간.</param>
/// <param name="Params">인자 정의.</param>
public sealed record StudioActionInfo(
    string Id, string Name, int DefaultTimeoutSeconds, ImmutableArray<StudioParamInfo> Params);

/// <summary>행동 인자 하나 (T11).</summary>
/// <param name="Name">인자 이름.</param>
/// <param name="Type">타입 이름 (<c>PoiRef</c>·<c>ItemRef</c>·<c>Enum</c>·<c>Int</c>…).</param>
/// <param name="Required">필수인가.</param>
/// <param name="EnumValues">열거 값.</param>
/// <param name="Min">정수 하한.</param>
/// <param name="Max">정수 상한.</param>
public sealed record StudioParamInfo(
    string Name, string Type, bool Required, ImmutableArray<string> EnumValues, int Min, int Max);

/// <summary>POI 심볼 하나 (T11). <b>바인딩 못 하는 심볼은 회색으로 보여 준다</b> — 고를 수는 없다.</summary>
/// <param name="Symbol">심볼 문자열 (<c>$home</c>).</param>
/// <param name="Name">한국어 표기.</param>
/// <param name="Bindable">이 직업이 붙일 수 있는가.</param>
public sealed record StudioSymbolInfo(string Symbol, string Name, bool Bindable);

/// <summary>폼 편집기가 고를 수 있는 값들 (T10). 전부 마스터데이터에서 온다.</summary>
/// <param name="HomeTypes">집 세부 유형.</param>
/// <param name="WorkplaceTypes">일터 세부 유형.</param>
/// <param name="Recipes">레시피 id.</param>
/// <param name="Items">아이템 id.</param>
/// <param name="Actions">액션 id (code 순).</param>
public sealed record StudioArchetypeChoices(
    ImmutableArray<string> HomeTypes,
    ImmutableArray<string> WorkplaceTypes,
    ImmutableArray<string> Recipes,
    ImmutableArray<string> Items,
    ImmutableArray<string> Actions);

/// <summary>지역별 인구 한 줄 (T33).</summary>
/// <param name="ZoneId">지역 id.</param>
/// <param name="ZoneName">지역 표시 이름.</param>
/// <param name="Count">인원.</param>
/// <param name="Predicted">명단에서 센 것이 아니라 예측인가.</param>
public sealed record StudioZoneCount(string ZoneId, string ZoneName, int Count, bool Predicted);

/// <summary>
/// NPC 한 명의 개요 (T07). "누구인가" 문장과 지도를 그릴 좌표가 같이 온다.
/// </summary>
/// <param name="Npc">인스턴스.</param>
/// <param name="Facts">거리·출입 계산.</param>
/// <param name="Sentence">누구인가 한 문장.</param>
/// <param name="ArchetypeName">직업 표시 이름.</param>
/// <param name="ZoneName">지역 표시 이름.</param>
/// <param name="Group">직군.</param>
/// <param name="ZonePois">같은 지역의 장소 (지도·순찰로 선택기).</param>
/// <param name="PatrolRoute">순찰로 POI id.</param>
public sealed record StudioNpcOverview(
    NpcInstanceDef Npc,
    InstanceFacts Facts,
    string Sentence,
    string ArchetypeName,
    string ZoneName,
    Lexicon.ArchetypeGroup Group,
    ImmutableArray<StudioPoiChoice> ZonePois,
    ImmutableArray<string> PatrolRoute)
{
    /// <summary>집 POI id.</summary>
    public string HomeId => Facts.Home.Id;

    /// <summary>일터 POI id. 없으면 빈 문자열.</summary>
    public string WorkplaceId => Facts.Workplace?.Id ?? string.Empty;

    /// <summary>직군 색.</summary>
    public string Color => Lexicon.ColorOf(Group);
}

/// <summary>개별 NPC 탐색 목록에 필요한 읽기 전용 요약.</summary>
public sealed record StudioNpcSummary(
    int Id,
    string Archetype,
    string Zone,
    string Home,
    string Workplace,
    string Faction,
    bool HasOverride);

/// <summary>개별 NPC 오버라이드 편집 화면 데이터.</summary>
public sealed record StudioNpcOverrideEditor(
    int Id,
    bool Exists,
    ImmutableArray<string> PatrolRoute,
    int? AggroRadiusM,
    string Faction,
    string DialogueProfile,
    int? ScheduleOffsetMinutes,
    ImmutableArray<string> Factions,
    ImmutableArray<StudioPoiChoice> ZonePois)
{
    /// <summary>읽은 시점의 지문 (H13).</summary>
    public StudioStamp? Stamp { get; init; }

    /// <summary>이미 쓰인 대화 프로필 (H17). 화면이 <c>datalist</c> 로 쓴다.</summary>
    public ImmutableArray<string> DialogueProfiles { get; init; } = [];

    /// <summary>세력 id 와 설명 (H17). select 가 id 대신 설명을 보여 준다.</summary>
    public ImmutableArray<(string Id, string Description)> FactionNotes { get; init; } = [];
}

/// <summary>순찰 경로 선택기와 지역 지도에 표시할 같은 지역 POI 설명.</summary>
/// <param name="Id">POI id.</param>
/// <param name="Type">유형 한국어 이름.</param>
/// <param name="Subtype">세부 유형 id.</param>
/// <param name="Capacity">정원.</param>
/// <param name="X">지역 중심 기준 가로 좌표.</param>
/// <param name="Z">지역 중심 기준 세로 좌표.</param>
/// <param name="IsHome">이 NPC 의 집인가.</param>
/// <param name="IsWorkplace">이 NPC 의 일터인가.</param>
public sealed record StudioPoiChoice(
    string Id,
    string Type,
    string Subtype,
    int Capacity,
    float X,
    float Z,
    bool IsHome,
    bool IsWorkplace)
{
    /// <summary>유형. 지도 점 색이 이것으로 갈린다 (T07).</summary>
    public PoiType Kind { get; init; }

    /// <summary>사람이 읽는 장소 이름 ("집 #12").</summary>
    public string Label { get; init; } = string.Empty;
}

/// <summary>개별 NPC 오버라이드 저장 초안.</summary>
public sealed record StudioNpcOverrideDraft(
    int Id,
    ImmutableArray<string> PatrolRoute,
    int? AggroRadiusM,
    string Faction,
    string DialogueProfile,
    int? ScheduleOffsetMinutes)
{
    /// <summary>읽은 시점의 지문 (H13).</summary>
    public StudioStamp? Stamp { get; init; }
}

/// <summary>검증 문제.</summary>
public sealed record StudioIssue(string Code, string Detail, string File, string Path, string FixHint);

/// <summary>저장 결과.</summary>
/// <param name="Saved">저장했는가.</param>
/// <param name="Message">사람이 읽는 결과 문구.</param>
/// <param name="Issues">저장 후보를 검증한 결과.</param>
/// <param name="Files">
/// 실제로 쓰인 파일 이름. 파급 패널(T16)이 이것을 모아 "이제 무엇을 해야 하나" 를 낸다 —
/// 세션이 파일 목록을 모르면 <c>ImpactAnalyzer</c> 에게 물어볼 것이 없다.
/// </param>
public sealed record StudioSaveResult(
    bool Saved,
    string Message,
    ImmutableArray<StudioIssue> Issues,
    ImmutableArray<string> Files = default)
{
    /// <summary>쓰인 파일. 기본값이면 빈 배열이다.</summary>
    public ImmutableArray<string> Files { get; init; } = Files.IsDefault ? [] : Files;

    /// <summary>무슨 작업이었는가 (H09·H14). 결과 카드와 되돌리기 목록이 쓴다.</summary>
    public string Label { get; init; } = string.Empty;
}
