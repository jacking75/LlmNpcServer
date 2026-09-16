using System.Collections.Immutable;

namespace Npc.Studio.Services;

/// <summary>
/// 회로(브라우저 탭) 하나의 상태. 카탈로그 캐시 · 검증 결과 · 이번 세션에서 바꾼 파일 · 토스트.
///
/// <b>여기는 파일을 쓰지 않는다.</b> 쓰기는 <see cref="StudioWorkspace"/> 하나뿐이고
/// 이 클래스는 그 결과를 화면들이 공유하도록 들고 있을 뿐이다 — 화면이 여럿으로 갈린 뒤
/// (T01) 페이지마다 카탈로그를 다시 읽으면 5,000명 명단을 매번 파싱하게 된다.
/// </summary>
public sealed class StudioSession
{
    private readonly StudioWorkspace _workspace;
    private ImmutableArray<StudioNpcSummary> _npcs;
    private bool _npcsLoaded;

    /// <summary>기동 시 카탈로그를 한 번 읽는다.</summary>
    public StudioSession(StudioWorkspace workspace)
    {
        _workspace = workspace;
        Catalog = workspace.LoadCatalog();
        Issues = Catalog.Issues;
    }

    /// <summary>현재 카탈로그.</summary>
    public StudioCatalog Catalog { get; private set; }

    /// <summary>마지막 검증 결과.</summary>
    public ImmutableArray<StudioIssue> Issues { get; private set; }

    /// <summary>이번 세션에서 저장된 파일 (파일 이름 오름차순). 파급 패널이 이것을 본다 (T16).</summary>
    public ImmutableArray<string> ChangedFiles { get; private set; } = [];

    /// <summary>마지막 안내 문구.</summary>
    public string Toast { get; private set; } = string.Empty;

    /// <summary>마지막 안내가 오류인가.</summary>
    public bool ToastIsError { get; private set; }

    /// <summary>따라하기 체크리스트 진행 (T31). 회로에만 남는다.</summary>
    public HashSet<string> Steps { get; } = new(StringComparer.Ordinal);

    /// <summary>카탈로그·검증·토스트 중 무엇이든 바뀌면 부른다.</summary>
    public event Action? Changed;

    /// <summary>디스크를 다시 읽는다. 연습장 전환·파생물 재생성 뒤에 부른다.</summary>
    public void Reload()
    {
        Catalog = _workspace.LoadCatalog();
        Issues = Catalog.Issues;
        _npcsLoaded = false;
        _npcs = [];
        Changed?.Invoke();
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

    /// <summary>저장 결과를 반영한다. 성공이면 바뀐 파일을 쌓는다.</summary>
    public void Apply(StudioSaveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Issues = result.Issues;
        Toast = result.Message;
        ToastIsError = !result.Saved;

        if (result.Saved)
        {
            ChangedFiles = [.. ChangedFiles.Union(result.Files, StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal)];
            Catalog = _workspace.LoadCatalog();
            _npcsLoaded = false;
            _npcs = [];
        }

        Changed?.Invoke();
    }

    /// <summary>전체 검증을 다시 돌린다.</summary>
    public void Validate()
    {
        Issues = _workspace.Validate();
        Notify(Issues.IsEmpty ? "전체 검증을 통과했다." : $"검증 문제 {Issues.Length}건이 있다.", !Issues.IsEmpty);
    }

    /// <summary>안내 문구를 띄운다.</summary>
    public void Notify(string message, bool error = false)
    {
        Toast = message;
        ToastIsError = error;
        Changed?.Invoke();
    }

    /// <summary>안내 문구를 지운다.</summary>
    public void ClearToast()
    {
        Toast = string.Empty;
        ToastIsError = false;
        Changed?.Invoke();
    }

    /// <summary>따라하기 항목을 체크한다 (T31). 이미 체크돼 있으면 아무 일도 하지 않는다.</summary>
    public void MarkStep(string step)
    {
        if (Steps.Add(step))
        {
            Changed?.Invoke();
        }
    }
}
