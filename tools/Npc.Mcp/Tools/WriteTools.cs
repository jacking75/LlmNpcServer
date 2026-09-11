using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using ModelContextProtocol.Server;
using Npc.Cli;

namespace Npc.Mcp.Tools;

/// <summary>
/// 파일이나 서버를 <b>바꾸는</b> 툴 (E-03).
///
/// <para>
/// <b><c>--allow-write</c> 없이는 등록조차 되지 않는다.</b> "있는데 거절" 이 아니라 "없다" 여야 한다 —
/// 있는데 거절하면 모델이 우회를 시도하고, 그 시도가 로그를 어지럽힌다.
/// 등록 여부는 <c>Program</c> 이 정한다.
/// </para>
/// </summary>
[McpServerToolType]
public sealed class WriteTools
{
    private readonly McpOptions _options;

    /// <summary>DI 가 만든다.</summary>
    /// <param name="options">서버 설정.</param>
    public WriteTools(McpOptions options) => _options = options;

    /// <summary>스캐폴드를 실제로 적용한다 (F-04).</summary>
    /// <param name="kind">종류.</param>
    /// <param name="id">새 id.</param>
    /// <param name="from">본뜰 id.</param>
    /// <param name="weight">인구 가중치.</param>
    /// <param name="dryRun">true 면 고치지 않고 diff 만 낸다.</param>
    [McpServerTool(Name = "masterdata_apply")]
    [Description(
        "스캐폴드 초안을 마스터데이터 파일에 적용한다. **서식을 보존한다**(JsonSurgeon). "
        + "dry_run=true 가 기본이다 — 먼저 diff 를 보고, 그 다음에 false 로 다시 부른다. "
        + "적용 뒤에는 반드시 masterdata_validate 를 부른다.")]
    public string Apply(
        [Description("지금은 archetype 만")] string kind,
        [Description("새 id")] string id,
        [Description("본뜰 기존 id")] string? from = null,
        [Description("인구 가중치 0~1")] double weight = 0,
        [Description("true 면 파일을 고치지 않는다. 기본 true")] bool dryRun = true) =>
        CliShell.Run(
            _options,
            ScaffoldCommand.Run,
            CliShell.Args(
                kind, id,
                from is null ? null : "--from", from,
                weight <= 0 ? null : "--weight",
                weight <= 0 ? null : weight.ToString("0.####", CultureInfo.InvariantCulture)),
            apply: !dryRun)
            .ToString();

    /// <summary>돌고 있는 서버에 관리 명령 (A-11).</summary>
    /// <param name="action">무엇을.</param>
    /// <param name="token">관리 토큰.</param>
    /// <param name="url">서버 주소.</param>
    /// <param name="reason">사유. 감사 로그에 남는다.</param>
    /// <param name="target">킬스위치 대상.</param>
    /// <param name="state">킬스위치 상태.</param>
    /// <param name="scope">리로드 범위.</param>
    [McpServerTool(Name = "server_admin")]
    [Description(
        "돌고 있는 서버에 관리 명령을 건다 — reload · snapshot · killswitch. "
        + "**모든 호출이 감사 로그에 남는다**(누가·언제·무엇을·왜). reason 을 반드시 적는다.")]
    public static async Task<string> AdminAsync(
        [Description("reload | snapshot | killswitch")] string action,
        [Description("NPC_ADMIN_TOKEN 값")] string token,
        [Description("서버 주소")] string? url = null,
        [Description("사유. 감사 로그에 그대로 남는다")] string? reason = null,
        [Description("killswitch 대상: T2 | T1 | PlanStore")] string? target = null,
        [Description("killswitch 상태: on | off")] string? state = null,
        [Description("reload 범위: planstore | content")] string? scope = null)
    {
        if (string.IsNullOrEmpty(token))
        {
            return "[거절] token 이 없다. NPC_ADMIN_TOKEN 값을 준다.";
        }

        var query = new List<string>();

        if (!string.IsNullOrEmpty(reason))
        {
            query.Add("reason=" + Uri.EscapeDataString(reason));
        }

        string path = action switch
        {
            "reload" => Path("/admin/reload", query, ("scope", scope ?? "planstore")),
            "snapshot" => Path("/admin/snapshot", query),
            "killswitch" => Path(
                "/admin/killswitch", query, ("target", target ?? string.Empty), ("state", state ?? "on")),
            _ => string.Empty,
        };

        if (path.Length == 0)
        {
            return $"[거절] 모르는 action: {action}. reload | snapshot | killswitch 중 하나다.";
        }

        return await PostAsync(url, path, token).ConfigureAwait(false);
    }

    /// <summary>프리베이크를 돌린다. <b>비용이 든다.</b></summary>
    /// <param name="only">버킷 글롭.</param>
    /// <param name="budgetUsd">예산.</param>
    [McpServerTool(Name = "prebake_run")]
    [Description(
        "플랜 프리베이크를 돌린다. **실제 비용이 든다**(LLM 호출). "
        + "budget_usd 는 코드가 1.00 로 자른다 — 더 큰 회차는 사람이 터미널에서 돌린다. "
        + "only 로 버킷을 좁히지 않으면 거절한다.")]
    public string Prebake(
        [Description("버킷 글롭. 예: blacksmith@* · *@Dawn.*.*")] string only,
        [Description("예산 USD. 1.00 을 넘기면 잘린다")] double budgetUsd = 0.25)
    {
        if (string.IsNullOrWhiteSpace(only))
        {
            return
                "[거절] only 가 비어 있다. 전량 회차는 $5 가 넘고 수십 분이 걸린다 — "
                + "사람이 터미널에서 돌린다.";
        }

        // <b>상한은 코드가 강제한다</b> (CLAUDE.md §2.7). 인자로 우회할 수 없다.
        double budget = Math.Clamp(budgetUsd, 0.01, McpOptions.PrebakeBudgetCapUsd);

        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _options.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in new[]
        {
            "run", "-c", "Release", "--project", "tools/Npc.Prebake", "--",
            "--masterdata", _options.MasterData,
            "--out", _options.PlanStore,
            "--only", only,
            "--budget-usd", budget.ToString("0.##", CultureInfo.InvariantCulture),
        })
        {
            info.ArgumentList.Add(arg);
        }

        using Process? process = Process.Start(info);

        if (process is null)
        {
            return "[실패] dotnet 을 띄우지 못했다.";
        }

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        return process.ExitCode == 0
            ? output
            : $"[exit {process.ExitCode}] 예산 ${budget:0.##}\n{output}\n{error}";
    }

    private static string Path(string route, List<string> query, params (string Key, string Value)[] extra)
    {
        foreach ((string key, string value) in extra)
        {
            if (!string.IsNullOrEmpty(value))
            {
                query.Add(key + "=" + Uri.EscapeDataString(value));
            }
        }

        return query.Count == 0 ? route : route + "?" + string.Join('&', query);
    }

    private static async Task<string> PostAsync(string? url, string path, string token)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(ServerTools.TimeoutSeconds * 4),
        };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        string target = (string.IsNullOrEmpty(url) ? ServerTools.DefaultUrl : url.TrimEnd('/')) + path;

        try
        {
            using HttpResponseMessage response = await client
                .PostAsync(new Uri(target), content: null)
                .ConfigureAwait(false);

            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            return response.IsSuccessStatusCode ? body : $"[HTTP {(int)response.StatusCode}] {body}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"[연결 실패] {target}\n{ex.Message}";
        }
    }
}
