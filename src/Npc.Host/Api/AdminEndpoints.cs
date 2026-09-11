using Npc.Core;

namespace Npc.Host.Api;

/// <summary>관리 요청 결과. 라우트가 그대로 JSON 으로 낸다.</summary>
/// <param name="Ok">성공했는가.</param>
/// <param name="Detail">사람이 읽는 결과.</param>
/// <param name="Fired">지금 끊겨 있는 킬스위치 대상.</param>
public readonly record struct AdminResult(bool Ok, string Detail, IReadOnlyList<string> Fired);

/// <summary>
/// 운영 제어 API (A-11).
///
/// <b>예전에는 <c>/control/killswitch</c> 하나였고 되돌릴 수 없었다.</b> 오조작 복구가
/// 재기동뿐이었는데, 재기동은 상태 전손이었다 — 그것이 킬스위치를 누르기 무섭게 만들었다.
///
/// <para>
/// <b>모든 호출이 감사 로그에 남는다</b> — 누가·언제·무엇을·왜. 장애 회고에서
/// "그때 누가 T2 를 껐나" 에 답할 수 있어야 한다.
/// </para>
///
/// <para>
/// 인증은 <see cref="AdminAuth"/> 미들웨어가 앞에서 본다 (A-06). 여기서는 인증을 다시 보지 않는다 —
/// 두 곳에서 보면 한쪽만 고쳐지는 날이 온다.
/// </para>
/// </summary>
public static class AdminEndpoints
{
    /// <summary>킬스위치 경로.</summary>
    public const string KillSwitchRoute = "/admin/killswitch";

    /// <summary>즉시 스냅샷 경로 (A-01).</summary>
    public const string SnapshotRoute = "/admin/snapshot";

    /// <summary>플랜 스토어 리로드 경로 (A-07).</summary>
    public const string ReloadRoute = "/admin/reload";

    /// <summary>
    /// 플레이어 기억 삭제 경로 (D-03 · 개인정보 요건).
    ///
    /// <b>탈퇴 처리의 종착지다.</b> 관계·기억·평판 셋 다 지운다 —
    /// 하나라도 남으면 "지웠다" 고 말할 수 없다.
    /// </summary>
    public const string ForgetPlayerRoute = "/admin/memory/forget";

    /// <summary>
    /// 킬스위치를 켜거나 끈다.
    /// </summary>
    /// <param name="switches">킬스위치 상태.</param>
    /// <param name="audit">감사 로그.</param>
    /// <param name="tick">지금 게임 틱.</param>
    /// <param name="target">대상 이름. <c>T2</c>·<c>T1</c>·<c>PlanStore</c>.</param>
    /// <param name="state"><c>on</c> 또는 <c>off</c>.</param>
    /// <param name="reason">사유. 감사 로그에 그대로 남는다.</param>
    /// <param name="client">호출자.</param>
    public static AdminResult KillSwitch(
        KillSwitchState switches,
        AuditLog audit,
        long tick,
        string? target,
        string? state,
        string? reason,
        string client)
    {
        ArgumentNullException.ThrowIfNull(switches);
        ArgumentNullException.ThrowIfNull(audit);

        string why = string.IsNullOrWhiteSpace(reason) ? "(사유 없음)" : reason.Trim();

        if (!KillSwitchState.TryParse(target, out KillSwitchTarget parsed))
        {
            var failure = new AdminResult(
                false,
                $"target 이 {KillSwitchState.TargetNames} 중 하나여야 한다: {target ?? "(없음)"}",
                Names(switches));

            audit.Write(new AuditEntry(
                tick, "killswitch", target ?? string.Empty, client, why, failure.Detail));

            return failure;
        }

        bool on = !string.Equals(state, "off", StringComparison.OrdinalIgnoreCase);

        if (on)
        {
            switches.Fire(parsed);
        }
        else
        {
            switches.Clear(parsed);
        }

        var result = new AdminResult(
            true,
            $"{parsed} 를 {(on ? "끊었다" : "되살렸다")}",
            Names(switches));

        audit.Write(new AuditEntry(
            tick, on ? "killswitch.on" : "killswitch.off",
            parsed.ToString(), client, why, "ok"));

        return result;
    }

    /// <summary>지금 끊겨 있는 대상 이름.</summary>
    public static IReadOnlyList<string> Names(KillSwitchState switches)
    {
        ArgumentNullException.ThrowIfNull(switches);

        var names = new List<string>();

        foreach (KillSwitchTarget target in switches.Fired)
        {
            names.Add(target.ToString());
        }

        return names;
    }
}
