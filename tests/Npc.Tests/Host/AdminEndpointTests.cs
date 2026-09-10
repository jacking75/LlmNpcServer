using System.Text.Json;
using Npc.Core;
using Npc.Host.Api;

namespace Npc.Tests.Host;

/// <summary>A-11 — 킬스위치 가역화 · 감사 로그. PRODUCTION_ROADMAP §4 A-11.</summary>
public sealed class AdminEndpointTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "npc-audit-" + Guid.NewGuid().ToString("N"));

    public AdminEndpointTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void KillSwitch_TurnsOnAndBackOff()
    {
        var switches = new KillSwitchState();
        var audit = NewAudit(out _);

        AdminResult on = AdminEndpoints.KillSwitch(
            switches, audit, tick: 100, "T2", "on", "제공사 장애", "1.2.3.4");

        Assert.True(on.Ok, on.Detail);
        Assert.True(switches.IsDisabled(KillSwitchTarget.T2));
        Assert.Contains("T2", on.Fired);

        // 예전에는 여기서 되돌릴 수 없었고, 복구가 재기동 = 상태 전손이었다.
        AdminResult off = AdminEndpoints.KillSwitch(
            switches, audit, tick: 200, "T2", "off", "복구됨", "1.2.3.4");

        Assert.True(off.Ok, off.Detail);
        Assert.False(switches.IsDisabled(KillSwitchTarget.T2));
        Assert.Empty(off.Fired);
    }

    [Fact]
    public void KillSwitch_IsIdempotentBothWays()
    {
        var switches = new KillSwitchState();
        var audit = NewAudit(out _);

        AdminEndpoints.KillSwitch(switches, audit, 1, "T1", "on", "x", "c");
        AdminEndpoints.KillSwitch(switches, audit, 2, "T1", "on", "x", "c");

        Assert.True(switches.IsDisabled(KillSwitchTarget.T1));

        AdminEndpoints.KillSwitch(switches, audit, 3, "T1", "off", "x", "c");
        AdminEndpoints.KillSwitch(switches, audit, 4, "T1", "off", "x", "c");

        Assert.False(switches.IsDisabled(KillSwitchTarget.T1));
    }

    [Fact]
    public void KillSwitch_ClearingOneLeavesTheOthers()
    {
        var switches = new KillSwitchState();
        var audit = NewAudit(out _);

        AdminEndpoints.KillSwitch(switches, audit, 1, "T2", "on", "x", "c");
        AdminEndpoints.KillSwitch(switches, audit, 2, "PlanStore", "on", "x", "c");
        AdminEndpoints.KillSwitch(switches, audit, 3, "T2", "off", "x", "c");

        Assert.False(switches.IsDisabled(KillSwitchTarget.T2));
        Assert.True(switches.IsDisabled(KillSwitchTarget.PlanStore));
    }

    [Fact]
    public void KillSwitch_RejectsUnknownTarget()
    {
        var switches = new KillSwitchState();
        AuditLog audit = NewAudit(out StringWriter log);

        AdminResult result = AdminEndpoints.KillSwitch(
            switches, audit, 1, "T3", "on", "오타", "1.2.3.4");

        Assert.False(result.Ok);
        Assert.Contains("T2", result.Detail, StringComparison.Ordinal);

        // 실패도 감사에 남는다. "왜 안 됐나" 를 나중에 물을 수 있어야 한다.
        Assert.Contains("\"action\":\"killswitch\"", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_RecordsWhoWhatWhyAndWhen()
    {
        var switches = new KillSwitchState();
        AuditLog audit = NewAudit(out StringWriter log);

        AdminEndpoints.KillSwitch(switches, audit, tick: 4_242, "T2", "on", "비용 급증", "10.0.0.7");

        string line = log.ToString().Trim().Split('\n')[^1];

        using JsonDocument document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;

        Assert.True(root.GetProperty("audit").GetBoolean());

        // 시각은 게임 틱이다 (CLAUDE.md §2.3). 리플레이·스냅샷과 같은 좌표계여야 맞춰 볼 수 있다.
        Assert.Equal(4_242, root.GetProperty("tick").GetInt64());
        Assert.Equal("killswitch.on", root.GetProperty("action").GetString());
        Assert.Equal("T2", root.GetProperty("target").GetString());
        Assert.Equal("10.0.0.7", root.GetProperty("client").GetString());
        Assert.Equal("비용 급증", root.GetProperty("reason").GetString());
        Assert.Equal("ok", root.GetProperty("result").GetString());
    }

    [Fact]
    public void Audit_AlsoLandsInTheFile()
    {
        string path = Path.Combine(_dir, "audit.jsonl");
        var audit = new AuditLog(path, TextWriter.Null);

        audit.Write(new AuditEntry(7, "reload", "planstore", "c", "왜", "ok"));
        audit.Write(new AuditEntry(8, "snapshot", string.Empty, "c", "왜", "ok"));

        // 프로세스가 죽어도 남아야 한다. 로그만 두면 수집기가 없는 환경에서 흔적이 사라진다.
        string[] lines = File.ReadAllLines(path);

        Assert.Equal(2, lines.Length);
        Assert.Contains("\"action\":\"reload\"", lines[0], StringComparison.Ordinal);
        Assert.Equal(2, audit.Written);
    }

    [Fact]
    public void Audit_SurvivesAnUnwritableFile()
    {
        // 감사 기록을 못 썼다고 운영 조치를 되돌리는 것은 과잉이다. 로그 쪽은 남는다.
        var log = new StringWriter();
        var audit = new AuditLog(Path.Combine(_dir, "nope", "deep", "audit.jsonl"), log);

        audit.Write(new AuditEntry(1, "killswitch.on", "T2", "c", "왜", "ok"));

        Assert.Equal(1, audit.Written);
        Assert.Contains("killswitch.on", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void KillSwitch_ReasonlessCallIsRecorded()
    {
        var switches = new KillSwitchState();
        AuditLog audit = NewAudit(out StringWriter log);

        AdminEndpoints.KillSwitch(switches, audit, 1, "T2", "on", reason: null, "c");

        Assert.Contains("(사유 없음)", log.ToString(), StringComparison.Ordinal);
    }

    private static AuditLog NewAudit(out StringWriter log)
    {
        log = new StringWriter();
        return new AuditLog(path: null, log);
    }
}
