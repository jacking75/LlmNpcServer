using Npc.Host.Api;

namespace Npc.Tests.Host;

/// <summary>A-06 — 관리·질의 API 인증. PRODUCTION_ROADMAP §4 A-06.</summary>
public sealed class AdminAuthTests
{
    private const string Token = "s3cr3t-token";

    [Fact]
    public void NoToken_MeansNoGate()
    {
        var auth = new AdminAuth(token: null);

        Assert.False(auth.Enabled);

        // 토큰이 없으면 이 미들웨어는 아무것도 막지 않는다.
        // 대신 --bind 0.0.0.0 이 기동 단계에서 거절된다 (A-04).
        Assert.Null(auth.Check(header: null, "1.2.3.4"));
    }

    [Fact]
    public void CorrectBearer_Passes()
    {
        var auth = new AdminAuth(Token);

        Assert.True(auth.Enabled);
        Assert.Null(auth.Check($"Bearer {Token}", "1.2.3.4"));
        Assert.Equal(0, auth.Rejected);
    }

    // 기제가 다른 넷만 둔다: 헤더 없음 / 접두만 있고 값이 빈 것 / 스킴 없는 생토큰 / 스킴은 맞고 값이 틀린 것.
    // ""·"Basic abc" 는 null 과 같은 TryReadBearer 실패 경로다.
    [Theory]
    [InlineData(null)]
    [InlineData("Bearer ")]
    [InlineData("s3cr3t-token")]
    [InlineData("Bearer wrong-token")]
    public void BadHeader_Is401(string? header)
    {
        var auth = new AdminAuth(Token);

        Assert.Equal(401, auth.Check(header, "1.2.3.4"));
        Assert.Equal(1, auth.Rejected);
    }

    [Fact]
    public void RepeatedFailures_Become429()
    {
        var auth = new AdminAuth(Token, () => 0);

        for (int i = 0; i < AdminAuth.FailuresPerMinute; i++)
        {
            Assert.Equal(401, auth.Check("Bearer nope", "1.2.3.4"));
        }

        // 무한 재시도를 허용하면 토큰을 무차별 대입할 수 있다.
        Assert.Equal(429, auth.Check("Bearer nope", "1.2.3.4"));

        // 맞는 토큰도 창 안에서는 막힌다 — 열쇠는 호출자이지 자격이 아니다.
        Assert.Equal(429, auth.Check($"Bearer {Token}", "1.2.3.4"));
    }

    [Fact]
    public void Throttle_IsPerClient()
    {
        var auth = new AdminAuth(Token, () => 0);

        for (int i = 0; i < AdminAuth.FailuresPerMinute + 1; i++)
        {
            auth.Check("Bearer nope", "1.2.3.4");
        }

        // 한 호출자의 실패가 다른 호출자를 막으면 그것이 서비스 거부다.
        Assert.Null(auth.Check($"Bearer {Token}", "5.6.7.8"));
    }

    [Fact]
    public void Throttle_ExpiresAfterAMinute()
    {
        long now = 0;
        var auth = new AdminAuth(Token, () => now);

        for (int i = 0; i < AdminAuth.FailuresPerMinute + 1; i++)
        {
            auth.Check("Bearer nope", "1.2.3.4");
        }

        Assert.Equal(429, auth.Check("Bearer nope", "1.2.3.4"));

        now = 60_001;

        Assert.Null(auth.Check($"Bearer {Token}", "1.2.3.4"));
    }

    [Theory]
    [InlineData("/healthz/live", false)]
    [InlineData("/healthz/ready", false)]
    [InlineData("/metrics/prometheus", false)]
    [InlineData("/dashboard", false)]
    [InlineData("/", false)]
    [InlineData("/status", true)]
    [InlineData("/metrics", true)]
    [InlineData("/npc/7", true)]
    [InlineData("/npcs", true)]
    [InlineData("/control/killswitch", true)]
    [InlineData("/admin/reload", true)]
    [InlineData("/heatmap.csv", true)]
    [InlineData("/buckets", true)]
    [InlineData("/stream/npcs", true)]
    public void ProtectedPaths_AreTheOnesThatLeakOrMutate(string path, bool expected)
    {
        // 프로브와 수집기는 상태를 바꾸지 않는다. 토큰을 들고 다니게 하면 사방에 퍼진다.
        Assert.Equal(expected, AdminAuth.IsProtected(path));
    }
}
