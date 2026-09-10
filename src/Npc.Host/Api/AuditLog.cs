using System.Text.Json;

namespace Npc.Host.Api;

/// <summary>감사 항목 한 줄 (A-11).</summary>
/// <param name="Tick">게임 틱. <b>벽시계가 아니다</b> — 리플레이와 같은 좌표계에 남긴다.</param>
/// <param name="Action">무엇을 했나. <c>killswitch.on</c>·<c>reload</c>·<c>snapshot</c> 같은 점 표기.</param>
/// <param name="Target">대상. 없으면 빈 문자열.</param>
/// <param name="Client">누가. 원격 IP.</param>
/// <param name="Reason">왜. 호출자가 준다.</param>
/// <param name="Result">결과. <c>ok</c> 또는 사유.</param>
public readonly record struct AuditEntry(
    long Tick,
    string Action,
    string Target,
    string Client,
    string Reason,
    string Result);

/// <summary>
/// 감사 로그 (A-11).
///
/// <b>운영 제어에 흔적이 없었다.</b> 누가 언제 무엇을 왜 껐는지 알 방법이 <c>/control/killswitch</c>
/// 호출 뒤에는 남지 않았고, 장애 회고에서 "그때 누가 T2 를 껐나" 를 물으면 답이 없었다.
///
/// <para>
/// 두 곳에 남긴다 — 구조화 로그(<c>audit=true</c>)와 파일(<c>state/audit.jsonl</c>).
/// 파일은 프로세스가 죽어도 남고, 로그는 수집기가 가져간다. 둘 중 하나만 두면
/// 어느 한쪽이 없는 환경에서 흔적이 통째로 사라진다.
/// </para>
///
/// <para>
/// <b>시각은 게임 틱이다</b> (CLAUDE.md §2.3). 리플레이·스냅샷과 같은 좌표계여야
/// "이 틱에 무엇이 있었나" 를 맞춰 볼 수 있다.
/// </para>
/// </summary>
public sealed class AuditLog
{
    /// <summary>
    /// 한글을 \uXXXX 로 escape 하지 않는다.
    ///
    /// 감사 로그는 <b>사람이 읽는 것</b>이 첫 목적이다. HTML 로 나가지 않으므로
    /// 기본 escape 를 유지할 이유가 없고, escape 된 사유는 회고에서 아무도 못 읽는다.
    /// </summary>
    private static readonly JsonSerializerOptions s_json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string? _path;
    private readonly TextWriter _log;
    private readonly Lock _gate = new();

    /// <summary>감사 로그를 만든다.</summary>
    /// <param name="path">jsonl 파일 경로. null 이면 로그에만 남긴다.</param>
    /// <param name="log">구조화 로그를 적을 곳.</param>
    public AuditLog(string? path, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(log);

        _path = path;
        _log = log;

        if (path is { } file && Path.GetDirectoryName(Path.GetFullPath(file)) is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>남긴 항목 수. 테스트가 읽는다.</summary>
    public long Written { get; private set; }

    /// <summary>
    /// 한 줄 남긴다. <b>실패해도 던지지 않는다</b> — 감사 기록을 못 썼다고 운영 조치를
    /// 되돌리는 것은 과잉이고, 그때는 로그 쪽이 남는다.
    /// </summary>
    public void Write(in AuditEntry entry)
    {
        string json = JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["audit"] = true,
            ["tick"] = entry.Tick,
            ["action"] = entry.Action,
            ["target"] = entry.Target,
            ["client"] = entry.Client,
            ["reason"] = entry.Reason,
            ["result"] = entry.Result,
        }, s_json);

        lock (_gate)
        {
            Written++;
            _log.WriteLine(json);
            _log.Flush();

            if (_path is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_path, json + Environment.NewLine);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.WriteLine($"warn: 감사 파일을 못 썼다 ({_path}): {e.Message}");
            }
        }
    }
}
