using System.Globalization;
using Npc.Contracts;
using Npc.Core;
using Npc.Host;
using Npc.Host.Api;
using Npc.Runtime;

namespace Npc.Tests.Host;

/// <summary>
/// B-08 — 읽기 전용 질의 API.
///
/// <para>
/// <b>게임서버가 NPC 서버에 물어볼 수단이 없는 것은 옳은 설계다</b> (N1). 그래서 질의는
/// 링크가 아니라 HTTP 에 둔다 — GM 도구·대화 서비스·라이브 장애 대응은 "저 NPC 왜 저래" 에
/// 답해야 하고, 그 답이 링크를 동기 호출로 바꾸면 안 된다.
/// </para>
///
/// <para>
/// 여기서 지키는 것 — <b>필터·페이지네이션이 실제로 거른다</b> · <b>맥락에 자연어가 없다</b> ·
/// <b>조회가 틱 루프를 막지 않는다</b>.
/// </para>
/// </summary>
[Collection(Npc.Tests.Runtime.AllocationCollection.Name)]
public sealed class QueryEndpointTests
{
    private const int Npcs = 120;

    /// <summary>필터 없이 부르면 기본 쪽 크기만큼 온다.</summary>
    [Fact]
    public async Task Npcs_PagesWithACursor()
    {
        await using NpcHost host = await RunAsync();

        NpcListPage first = host.QueryNpcs(limit: 40);

        Assert.Equal(Npcs, first.Total);
        Assert.Equal(Npcs, first.Matched);
        Assert.Equal(40, first.Returned);
        Assert.Equal(40, first.NextCursor);

        NpcListPage second = host.QueryNpcs(limit: 40, cursor: first.NextCursor!.Value);

        Assert.Equal(40, second.Returned);

        // A-08 — 커서는 슬롯 공간이고 Npc 는 전역 id 다. 둘을 같이 싣는 이유가 이것이다.
        Assert.Equal(40, second.Npcs[0].Slot);
        Assert.NotEqual(0, second.Npcs[0].Npc);

        // 마지막 쪽에는 다음 커서가 없다.
        NpcListPage last = host.QueryNpcs(limit: 1_000);

        Assert.Equal(Npcs, last.Returned);
        Assert.Null(last.NextCursor);
    }

    /// <summary>
    /// <b><c>Matched</c> 는 쪽 크기가 아니라 필터에 걸린 총수다.</b> 이것이 없으면
    /// 클라이언트가 "더 있나" 를 커서 유무로만 짐작해야 한다.
    /// </summary>
    [Fact]
    public async Task Npcs_ReportsMatchedSeparatelyFromReturned()
    {
        await using NpcHost host = await RunAsync();

        NpcListPage page = host.QueryNpcs(limit: 5);

        Assert.Equal(Npcs, page.Matched);
        Assert.Equal(5, page.Returned);
    }

    /// <summary>아키타입 필터가 실제로 거른다.</summary>
    [Fact]
    public async Task Npcs_FiltersByArchetype()
    {
        await using NpcHost host = await RunAsync();

        string archetype = host.QueryNpcs(limit: 1).Npcs[0].Archetype;

        NpcListPage page = host.QueryNpcs(archetype: archetype, limit: 1_000);

        Assert.True(page.Matched > 0);
        Assert.True(page.Matched < Npcs, "전부 같은 아키타입이면 필터를 시험하지 못한다");
        Assert.All(page.Npcs, n => Assert.Equal(archetype, n.Archetype));
    }

    /// <summary>
    /// <b>모르는 아키타입은 빈 결과다.</b> 404 로 만들지 않는다 — 필터는 조건이지 자원이 아니고,
    /// 오타 하나로 도구가 죽는 것보다 "0건" 이 낫다.
    /// </summary>
    [Fact]
    public async Task Npcs_UnknownFilterYieldsNothing()
    {
        await using NpcHost host = await RunAsync();

        Assert.Equal(0, host.QueryNpcs(archetype: "없는아키타입", limit: 100).Matched);
        Assert.Equal(0, host.QueryNpcs(zone: "없는존", limit: 100).Matched);
    }

    /// <summary>플래그 필터. 회차 끝에 플래그가 하나도 안 선 경우는 없다.</summary>
    [Fact]
    public async Task Npcs_FiltersByFlag()
    {
        await using NpcHost host = await RunAsync();

        // 시간대 넷은 상호 배타다 — 합이 인원을 넘으면 배타가 깨진 것이고,
        // 0 이면 어느 NPC 에게도 시간대가 안 선 것이다.
        string[] times =
        [
            nameof(WorldFlags.IsDawn),
            nameof(WorldFlags.IsDay),
            nameof(WorldFlags.IsEvening),
            nameof(WorldFlags.IsNight),
        ];

        int total = 0;

        foreach (string time in times)
        {
            NpcListPage page = host.QueryNpcs(flag: time, limit: 1_000);

            Assert.All(page.Npcs, n => Assert.True(n.Npc > 0 && n.Slot >= 0));

            total += page.Matched;
        }

        Assert.True(total > 0, "시간대 플래그가 아무에게도 안 섰다");
        Assert.True(total <= Npcs, $"시간대가 상호 배타가 아니다: {total} > {Npcs}");
    }

    /// <summary>
    /// 쪽 크기를 <b>양쪽에서</b> 조인다. 0·음수는 기본값으로, 상한 초과는 상한으로.
    /// 조이지 않으면 <c>limit=1000000</c> 하나로 응답 조립이 몇 초를 먹는다.
    /// </summary>
    [Fact]
    public async Task Npcs_ClampsTheLimit()
    {
        await using NpcHost host = await RunAsync();

        // NPC 120 < 상한 1,000 이라 전원이 온다.
        Assert.Equal(Npcs, host.QueryNpcs(limit: 100_000).Returned);

        // 0·음수는 기본값(100)이다. 빈 응답이 아니다.
        Assert.Equal(QueryEndpoints.DefaultLimit, host.QueryNpcs(limit: 0).Returned);
        Assert.Equal(QueryEndpoints.DefaultLimit, host.QueryNpcs(limit: -5).Returned);
    }

    /// <summary>
    /// <b>질의 라우트는 전부 토큰 뒤에 있다</b> (A-06). <c>/healthz</c>·수집기만 무인증이다 —
    /// 그 둘은 상태를 바꾸지 않고, 토큰을 들고 다니게 하면 그 토큰이 사방에 퍼진다.
    /// </summary>
    [Theory]
    [InlineData("/npcs")]
    [InlineData("/npc/1")]
    [InlineData("/npc/1/context")]
    [InlineData("/buckets")]
    [InlineData("/stream/npcs")]
    public void QueryRoutes_RequireTheAdminToken(string route)
    {
        Assert.True(AdminAuth.IsProtected(route), $"{route} 가 무인증이다");
    }

    // ---------------------------------------------------------------- 맥락

    /// <summary>
    /// <b>맥락에 자연어가 없다.</b> 이 응답이 그대로 프롬프트에 실릴 수 있고,
    /// 플레이어가 쓴 문자열이 섞이면 그 순간 인젝션 경로가 열린다 (§2.5).
    /// </summary>
    [Fact]
    public async Task Context_IsIdsAndEnumsOnly()
    {
        await using NpcHost host = await RunAsync();

        // A-08 — /npc/{id}/context 는 전역 id 를 받는다.
        NpcContext context = host.Context(host.Store.Occupant[0]);

        Assert.True(context.Found);
        Assert.NotEmpty(context.Archetype);
        Assert.True(context.StepCount >= 0);

        // 마스터데이터에 있는 id 여야 한다 — 즉 우리가 만든 어휘다.
        Assert.True(
            string.IsNullOrEmpty(context.Action) || context.Action.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'),
            $"액션 id 가 아니다: {context.Action}");

        foreach (string flag in context.Flags)
        {
            Assert.True(WorldFlagTable.TryParse(flag, out _), $"모르는 플래그 이름이다: {flag}");
        }
    }

    /// <summary>없는 첨자는 <c>Found=false</c> 다. 던지지 않는다.</summary>
    [Fact]
    public async Task Context_MissingSlotIsNotAnError()
    {
        await using NpcHost host = await RunAsync();

        Assert.False(host.Context(99_999).Found);
        Assert.False(host.Context(-1).Found);
    }

    // ---------------------------------------------------------------- 버킷

    /// <summary>
    /// 집계는 항상 준다. <b>줄은 <c>state</c> 를 줬을 때만</b> — 2,880줄을 기본으로 뱉지 않는다.
    /// </summary>
    [Fact]
    public async Task Buckets_SummarisesAndFiltersOnDemand()
    {
        await using NpcHost host = await RunAsync();

        BucketReport all = host.Buckets();

        Assert.Equal(TestPaths.TotalKeys, all.Total);
        Assert.Equal(0, all.Returned);
        Assert.Equal(all.Total, all.Filled + all.Missing);

        BucketReport missing = host.Buckets("missing");

        Assert.Equal(all.Missing, missing.Missing);
        Assert.True(missing.Returned > 0, "미생성 버킷이 하나도 없다 — 이 회차는 planstore 를 안 읽었다");
        Assert.All(missing.Buckets, b => Assert.Equal("missing", b.State));
    }

    // ---------------------------------------------------------------- 스트림

    /// <summary>
    /// <c>ids</c> 파싱. <b>모르는 값은 버린다</b> — 스트림은 진단용이라 오타 하나로 연결이
    /// 끊기는 것보다 조용히 빠지는 편이 낫다.
    /// </summary>
    [Fact]
    public void ParseIds_DropsUnknownAndDuplicates()
    {
        int[] ids = QueryEndpoints.ParseIds("3, 1 ,3,abc,-1,9999", count: 10, max: 8);

        Assert.Equal([3, 1], ids);
        Assert.Empty(QueryEndpoints.ParseIds(null, 10, 8));
        Assert.Empty(QueryEndpoints.ParseIds("   ", 10, 8));
    }

    /// <summary>상한까지만 받는다.</summary>
    [Fact]
    public void ParseIds_HonoursTheCap()
    {
        string ids = string.Join(',', Enumerable.Range(0, 50));

        Assert.Equal(8, QueryEndpoints.ParseIds(ids, count: 100, max: 8).Length);
    }

    /// <summary>
    /// <b>연결 수 상한이 실제로 막는다.</b> 상한이 없으면 GM 도구를 여러 개 띄운 것만으로
    /// 응답 조립이 틱마다 수십 번 돈다.
    /// </summary>
    [Fact]
    public void StreamLimiter_BlocksBeyondTheLimit()
    {
        var limiter = new StreamLimiter(2);

        Assert.True(limiter.TryEnter());
        Assert.True(limiter.TryEnter());
        Assert.False(limiter.TryEnter());
        Assert.Equal(2, limiter.Open);

        limiter.Exit();

        Assert.True(limiter.TryEnter());

        // 0 이면 아예 안 연다.
        Assert.False(new StreamLimiter(0).TryEnter());
    }

    // ---------------------------------------------------------------- 예산

    /// <summary>
    /// <b>조회가 값을 바꾸지 않는다.</b> 같은 틱에 두 번 물으면 같은 답이어야 하고,
    /// 개별 플랜의 LRU 회수 순서도 조회로 흔들리면 안 된다 — <b>대시보드를 열어 둔 것만으로
    /// 게임 상태가 달라지면</b> 그 서버는 관측할 수 없다 (T4-12).
    /// </summary>
    [Fact]
    public async Task Query_DoesNotMutateState()
    {
        await using NpcHost host = await RunAsync();

        ulong before = host.Store.StateHash();

        for (int i = 0; i < 20; i++)
        {
            host.QueryNpcs(limit: 1_000);
            host.Context(i % Npcs);
            host.Buckets("missing");
        }

        Assert.Equal(before, host.Store.StateHash());
    }

    /// <summary>
    /// 벌크 응답이 빠르다. <b>정확한 ms 를 게이트로 삼지 않는다</b> — 기계마다 다르다.
    /// 여기서 잡는 것은 "N² 가 되지 않았는가" 다.
    /// </summary>
    [Fact]
    public async Task Npcs_BulkResponseIsNotQuadratic()
    {
        await using NpcHost host = await RunAsync();

        // 예열.
        host.QueryNpcs(limit: 1_000);

        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        for (int i = 0; i < 20; i++)
        {
            host.QueryNpcs(limit: 1_000);
        }

        double perCall = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds / 20;

        Assert.True(
            perCall < 50,
            string.Create(CultureInfo.InvariantCulture, $"벌크 응답이 {perCall:F1}ms 다 (NPC {Npcs})"));
    }

    // ---------------------------------------------------------------- 조립

    /// <summary>한 회차 돌린 호스트. 플랜·플래그가 채워져 있어야 조회가 의미 있다.</summary>
    private static async Task<NpcHost> RunAsync()
    {
        string[] args =
        [
            "--loopback",
            "--npcs", Npcs.ToString(CultureInfo.InvariantCulture),
            "--time-scale", "600",
            "--days", "1",
            "--max-speed",
            "--no-dashboard",
            "--no-llm",
        ];

        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        NpcHost host = NpcHost.Create(
            options with { MasterData = TestPaths.MasterData }, TextWriter.Null);

        await host.RunAsync(CancellationToken.None);

        return host;
    }
}
