using System.Collections.Immutable;
using Npc.Narrative;

namespace Npc.Studio.Services;

/// <summary>건강 진단 한 직업 (H10·H25).</summary>
/// <param name="Id">직업 id.</param>
/// <param name="Name">표시 이름.</param>
/// <param name="Findings">발견.</param>
public sealed record StudioLintRow(string Id, string Name, ImmutableArray<LintFinding> Findings);

/// <summary>
/// 회로(브라우저 탭) 하나의 상태. 카탈로그 캐시 · 검증 결과 · 이번 세션에서 바꾼 파일 · 토스트.
///
/// <b>여기는 파일을 쓰지 않는다.</b> 쓰기는 <see cref="StudioWorkspace"/> 하나뿐이고
/// 이 클래스는 그 결과를 화면들이 공유하도록 들고 있을 뿐이다 — 화면이 여럿으로 갈린 뒤
/// (T01) 페이지마다 카탈로그를 다시 읽으면 5,000명 명단을 매번 파싱하게 된다.
/// </summary>
public sealed class StudioSession : IDisposable
{
    private readonly StudioWorkspace _workspace;
    private readonly Dictionary<object, (Func<bool> IsDirty, string What)> _dirty = [];
    private bool _anyDirty;
    private ImmutableArray<StudioNpcSummary> _npcs;
    private ImmutableArray<StudioLintRow> _lint;
    private bool _npcsLoaded;
    private bool _lintLoaded;

    /// <summary>기동 시 카탈로그를 한 번 읽는다.</summary>
    /// <param name="workspace">파일 경계.</param>
    public StudioSession(StudioWorkspace workspace)
    {
        _workspace = workspace;
        Catalog = workspace.LoadCatalog();
        Issues = Catalog.Issues;

        _workspace.DirectoryChanged += OnDirectoryChanged;
        _workspace.ExternalChange += OnExternalChange;

        Directory = Catalog.Directory;
    }

    /// <summary>현재 카탈로그.</summary>
    public StudioCatalog Catalog { get; private set; }

    /// <summary>마지막 검증 결과 (디스크 상태).</summary>
    public ImmutableArray<StudioIssue> Issues { get; private set; }

    /// <summary>
    /// 방금 거절된 <b>초안</b>의 문제 (H09). <see cref="Issues"/> 와 섞지 않는다 —
    /// 저장에 실패한 초안의 오류가 전역 "검증 ✗ N건" 으로 올라가면
    /// "내 파일이 망가졌나" 로 읽힌다. 파일은 그대로다.
    /// </summary>
    public ImmutableArray<StudioIssue> LastRejected { get; private set; } = [];

    /// <summary>이번 세션에서 저장된 파일 (파일 이름 오름차순). 파급 패널이 이것을 본다 (T16).</summary>
    public ImmutableArray<string> ChangedFiles { get; private set; } = [];

    /// <summary>마지막 안내 문구.</summary>
    public string Toast { get; private set; } = string.Empty;

    /// <summary>마지막 안내가 오류인가.</summary>
    public bool ToastIsError { get; private set; }

    /// <summary>마지막 저장 결과 (H09). 성공 카드가 이것을 보여 준다.</summary>
    public StudioSaveResult? LastSaved { get; private set; }

    /// <summary>결과 카드를 접었는가.</summary>
    public bool LastSavedCollapsed { get; private set; }

    /// <summary>
    /// 페이지 캐시 세대 (H01·H13). 새로고침·저장·연습장 전환마다 오른다 —
    /// 화면의 <c>_loaded</c> 가드가 <c>(id, Generation)</c> 쌍을 보면 옛 값이 남지 않는다.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>이 세션이 알고 있는 디렉터리. 다른 탭이 바꾸면 배너가 뜬다 (H04).</summary>
    public string Directory { get; private set; }

    /// <summary>다른 탭이 폴더를 바꿨다 (H04). 배너 문구다. 비면 아무 일도 없다.</summary>
    public string DirectoryNotice { get; private set; } = string.Empty;

    /// <summary>밖에서 바뀐 파일 (H13). 배너가 "새로고침" 을 권한다.</summary>
    public ImmutableArray<string> ExternallyChanged { get; private set; } = [];

    /// <summary>따라하기 체크리스트 진행 (T31). 회로에만 남는다.</summary>
    public HashSet<string> Steps { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 폼 초안 보관 (H01). 탭을 옮겼다 돌아와도 이어서 고칠 수 있다.
    /// 키는 화면이 정한다 (<c>archetype:blacksmith</c>).
    /// </summary>
    public Dictionary<string, object> Drafts { get; } = new(StringComparer.Ordinal);

    /// <summary>카탈로그·검증·토스트 중 무엇이든 바뀌면 부른다.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------ 초안 등록부 (H01)

    /// <summary>
    /// 저장하지 않은 초안이 있다고 알린다 (H01).
    ///
    /// <b>이탈 가드의 전제다.</b> 폼이 스스로 "나 더럽다" 를 말하지 않으면
    /// 레이아웃은 탭 전환·새로고침·탭 닫기를 막을 근거가 없다.
    /// </summary>
    /// <param name="owner">폼 컴포넌트. <see cref="Unregister"/> 의 키다.</param>
    /// <param name="isDirty">지금 더러운가.</param>
    /// <param name="what">무엇이 더러운가 ("대장장이 편집").</param>
    public void RegisterDirty(object owner, Func<bool> isDirty, string what)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(isDirty);

        _dirty[owner] = (isDirty, what);
    }

    /// <summary>초안 등록을 해제한다. 컴포넌트가 사라질 때 부른다.</summary>
    /// <param name="owner">폼 컴포넌트.</param>
    public void Unregister(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (_dirty.Remove(owner))
        {
            SyncDirty();
        }
    }

    /// <summary>
    /// 초안 상태가 바뀌었으면 레이아웃에 알린다 (H01).
    ///
    /// <para>
    /// <b>이것이 없으면 "초안 취소" 를 눌러도 브라우저 이탈 경고가 남는다.</b>
    /// <c>NavigationLock.ConfirmExternalNavigation</c> 은 레이아웃에 있고, 레이아웃은
    /// 폼이 다시 그려지는 것을 모른다 — 폼이 알려야 한다.
    /// </para>
    ///
    /// <para>
    /// <b>값이 달라졌을 때만 이벤트를 낸다.</b> 매 입력마다 알리면 레이아웃이 같이 다시 그려진다.
    /// </para>
    /// </summary>
    public void SyncDirty()
    {
        bool now = AnyDirty;

        if (now != _anyDirty)
        {
            _anyDirty = now;
            Changed?.Invoke();
        }
    }

    /// <summary>저장하지 않은 초안이 하나라도 있는가.</summary>
    public bool AnyDirty => _dirty.Values.Any(entry => Safe(entry.IsDirty));

    /// <summary>무엇이 더러운가. 확인 문장이 이것을 쓴다.</summary>
    public string DirtyWhat =>
        string.Join(" · ", _dirty.Values.Where(entry => Safe(entry.IsDirty)).Select(entry => entry.What).Distinct(StringComparer.Ordinal));

    private static bool Safe(Func<bool> isDirty)
    {
        try { return isDirty(); }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ 읽기

    /// <summary>디스크를 다시 읽는다. 연습장 전환·파생물 재생성 뒤에 부른다.</summary>
    public void Reload()
    {
        Catalog = _workspace.LoadCatalog();
        Issues = Catalog.Issues;
        Directory = Catalog.Directory;
        DirectoryNotice = string.Empty;
        ExternallyChanged = [];
        LastRejected = [];
        Generation++;
        _npcsLoaded = false;
        _npcs = [];
        _lintLoaded = false;
        _lint = [];
        Changed?.Invoke();
    }

    /// <summary>
    /// 건강 진단 건수 (H10). 아직 안 돌렸으면 null 이다 —
    /// <b>0 과 "안 봤다" 는 다르다.</b>
    /// </summary>
    public int? LintCount => _lintLoaded ? _lint.Sum(row => row.Findings.Length) : null;

    /// <summary>건강 진단. 처음 부를 때만 돌린다.</summary>
    /// <param name="force">이미 돌렸어도 다시 돌린다.</param>
    public ImmutableArray<StudioLintRow> Lint(bool force = false)
    {
        if (force || !_lintLoaded)
        {
            _lint = _workspace.WithData<ImmutableArray<StudioLintRow>>((data, instances) =>
            [
                .. data.Archetypes.Archetypes.Select(def => new StudioLintRow(
                    def.Id, Lexicon.Archetype(def.Id), ArchetypeLint.Run(data, def, instances))),
            ]);
            _lintLoaded = true;
            Changed?.Invoke();
        }

        return _lint;
    }

    /// <summary>NPC 명단. 처음 부를 때만 읽는다.</summary>
    public ImmutableArray<StudioNpcSummary> Npcs()
    {
        if (!_npcsLoaded)
        {
            _npcs = _workspace.LoadNpcDirectory();
            _npcsLoaded = true;
        }

        return _npcs;
    }

    // ------------------------------------------------------------------ 쓰기 결과

    /// <summary>저장 결과를 반영한다. 성공이면 바뀐 파일을 쌓는다.</summary>
    /// <param name="result">저장 결과.</param>
    public void Apply(StudioSaveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Toast = result.Message;
        ToastIsError = !result.Saved;

        if (result.Saved)
        {
            Issues = result.Issues;
            LastRejected = [];
            LastSaved = result;
            LastSavedCollapsed = false;
            ChangedFiles = [.. ChangedFiles.Union(result.Files, StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)];
            Catalog = _workspace.LoadCatalog();
            Directory = Catalog.Directory;
            ExternallyChanged = [];
            Generation++;
            _npcsLoaded = false;
            _npcs = [];
            _lintLoaded = false;
            _lint = [];
        }
        else
        {
            // 디스크는 그대로다 — 전역 검증 수를 초안의 오류로 덮지 않는다 (H09).
            LastRejected = result.Issues;
        }

        Changed?.Invoke();
    }

    /// <summary>결과 카드를 접는다 (H09). 사라지지는 않는다.</summary>
    public void CollapseLastSaved()
    {
        LastSavedCollapsed = true;
        Changed?.Invoke();
    }

    /// <summary>결과 카드를 닫는다.</summary>
    public void DismissLastSaved()
    {
        LastSaved = null;
        Changed?.Invoke();
    }

    /// <summary>전체 검증을 다시 돌린다.</summary>
    public void Validate()
    {
        Issues = _workspace.Validate();
        LastRejected = [];
        Notify(Issues.IsEmpty ? "전체 검증을 통과했다." : $"검증 문제 {Issues.Length}건이 있다.", !Issues.IsEmpty);
    }

    /// <summary>안내 문구를 띄운다.</summary>
    /// <param name="message">문구.</param>
    /// <param name="error">오류인가.</param>
    public void Notify(string message, bool error = false)
    {
        Toast = message;
        ToastIsError = error;
        Changed?.Invoke();
    }

    /// <summary>
    /// 예외를 삼키지 않고 화면에 낸다 (H08).
    ///
    /// <b>모든 핸들러가 이것을 거친다.</b> 예전에는 <c>try</c> 없는 핸들러가 여럿이었고,
    /// 거기서 예외가 나면 회로가 조용히 죽어 버튼이 아무 반응도 하지 않았다 —
    /// 초보자는 새로고침을 모른다.
    /// </summary>
    /// <param name="action">할 일.</param>
    /// <param name="what">무엇을 하는 중인가 ("저장").</param>
    public bool Try(Action action, string what)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Notify(StudioErrors.Humanize(ex, what), true);
            return false;
        }
    }

    /// <summary>비동기판 <see cref="Try(Action, string)"/>.</summary>
    /// <param name="action">할 일.</param>
    /// <param name="what">무엇을 하는 중인가.</param>
    public async Task<bool> TryAsync(Func<Task> action, string what)
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            Notify(StudioErrors.Humanize(ex, what), true);
            return false;
        }
    }

    /// <summary>안내 문구를 지운다.</summary>
    public void ClearToast()
    {
        Toast = string.Empty;
        ToastIsError = false;
        Changed?.Invoke();
    }

    /// <summary>따라하기 항목을 체크한다 (T31). 이미 체크돼 있으면 아무 일도 하지 않는다.</summary>
    /// <param name="step">항목 키.</param>
    public void MarkStep(string step)
    {
        if (Steps.Add(step))
        {
            Changed?.Invoke();
        }
    }

    private void OnDirectoryChanged()
    {
        string now = _workspace.CurrentDirectory;

        if (string.Equals(now, Directory, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 워크스페이스는 싱글턴이라 폴더가 전역이다 — 다른 탭이 연습장을 열면 이 탭도 따라간다.
        // 모르는 채로 저장하면 "원본인 줄 알고 연습장에 썼다" 가 된다.
        DirectoryNotice = _workspace.SandboxName.Length > 0
            ? $"다른 탭이 연습장 '{_workspace.SandboxName}' 을 열었다 — 이 탭도 그 폴더를 본다."
            : "다른 탭이 원본으로 돌아갔다 — 이 탭도 원본을 본다.";

        Reload();
    }

    private void OnExternalChange(string file)
    {
        if (ExternallyChanged.Contains(file, StringComparer.Ordinal))
        {
            return;
        }

        ExternallyChanged = [.. ExternallyChanged.Add(file).OrderBy(f => f, StringComparer.Ordinal)];
        Changed?.Invoke();
    }

    /// <summary>회로가 끊기면 워크스페이스 이벤트를 놓는다.</summary>
    public void Dispose()
    {
        _workspace.DirectoryChanged -= OnDirectoryChanged;
        _workspace.ExternalChange -= OnExternalChange;
    }
}
