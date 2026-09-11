using System.Collections.Immutable;
using System.Globalization;
using Npc.Conformance;
using Npc.Contracts;
using Npc.Gateway;
using Npc.MasterData;
using Npc.TestGameServer;
using Npc.Wire;

namespace Npc.Tests.Conformance;

/// <summary>
/// B-07 완료 조건 — <b>게임서버 대역이 적합성 키트를 통과한다.</b>
///
/// <para>
/// <b>우리 대역을 먼저 통과시키는 것이 순서다.</b> 남의 게임서버에 들이대기 전에
/// 우리 것이 규약을 지키는지 확인해야 하고, 지키지 않는다면 규약과 대역 중 하나가 버그다.
/// </para>
///
/// <para>
/// <b>여기서 NPC 서버는 안 띄운다.</b> 적합성 키트가 NPC 서버 <b>대신</b> 붙는 것이
/// 이 도구의 요점이다 — 상대 게임서버에 우리 NPC 서버를 붙일 수 없는 상황(연동 전 검증)이
/// 이 도구가 쓰이는 자리다. 대신 스크립트가 <c>MoveTo</c> 를 내고 플레이어를 움직여
/// 응답·근접 규약을 실제로 관찰한다.
/// </para>
/// </summary>
[Trait("Category", "TestBed")]
[Collection(Npc.Tests.Runtime.AllocationCollection.Name)]
public sealed class ConformanceBedTests
{
    /// <summary>회차 길이. 근접 판정이 5틱마다라 넉넉히 준다.</summary>
    private const int RunTicks = 240;

    /// <summary>로스터 크기. 작을수록 빠르고, 규약 검사에는 충분하다.</summary>
    private const int Npcs = 16;

    /// <summary>보고서 경로. <b>고정 이름이다</b> — 회차마다 파일이 늘면 diff 를 못 읽는다.</summary>
    private const string ReportStem = "conformance_testbed";

    /// <summary>
    /// 대역이 규약을 지킨다. <b>불합격이 하나도 없어야 한다.</b>
    ///
    /// <para>
    /// 보고서를 <c>docs/measurements/</c> 에 쓴다 — 실측 원자료다 (CLAUDE.md §6).
    /// 미판정 항목은 보고서에 그대로 남는다. <b>미판정은 통과가 아니다.</b>
    /// </para>
    /// </summary>
    [Fact]
    public async Task Conformance_TestBedPasses()
    {
        MasterDataSet data = MasterDataLoader.Load(TestPaths.MasterData);
        NpcInstanceTable instances = NpcInstanceTable.Load(
            Path.Combine(TestPaths.MasterData, "npc_instances.json"), data);
        NpcRoster roster = NpcRoster.Select(instances, Npcs);

        GameServer server = GameServer.Create(
            new GameServerOptions
            {
                Npcs = Npcs,
                LinkPort = 0,
                ClientPort = 0,
                TimeScale = 60,
                MasterData = TestPaths.MasterData,
            },
            TextWriter.Null);

        server.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        Task accept = server.AcceptAsync(cts.Token);

        await using var link = new TcpGameServerLink(new TcpLinkOptions
        {
            Host = "127.0.0.1",
            Port = server.LinkPort,
            TimeScale = 60,
            NpcCount = roster.Count,
            MasterData = WireHash.FromHex(data.ContentHash),
            MasterDataStructural = WireHash.FromHex(data.StructuralHash),
            MasterDataContent = WireHash.FromHex(data.ContentHash),
            Roster = WireHash.FromHex(roster.Hash),
        });

        var observer = new Observer(link, roster.Count, data.Zones.Zones.Length);

        Task run = link.RunAsync(cts.Token);

        long tick = 0;

        // 붙을 때까지 민다. accept 도 접속도 다른 태스크라 몇 틱 걸린다.
        for (int i = 0; i < 600 && server.Link.Session is not { IsAccepted: true }; i++)
        {
            await server.TickAsync(new Tick(++tick), cts.Token);
            await Task.Delay(5, cts.Token);
        }

        Assert.True(
            server.Link.Session is { IsAccepted: true },
            $"적합성 키트가 대역에 붙지 못했다: {link.NegotiationDetail}");

        // 재동기화가 도착할 때까지 민다.
        for (int i = 0; i < 200 && observer.EventCount < Npcs; i++)
        {
            await server.TickAsync(new Tick(++tick), cts.Token);
            await Task.Delay(2, cts.Token);
        }

        PlayerId player = server.Players.Add();
        uint correlation = 0;

        for (int i = 0; i < RunTicks; i++)
        {
            await server.TickAsync(new Tick(++tick), cts.Token);

            // 스크립트 — 회차 초반에 NPC 마다 MoveTo 를 한 번씩 낸다.
            // 응답 규약(C6)은 명령을 내야만 볼 수 있다.
            if (i == 20)
            {
                correlation = Probe(link, observer, roster, tick);
            }

            // 근접 규약(C4)은 플레이어가 움직여야 볼 수 있다.
            // 앞 절반은 NPC 0 을 따라가고, 뒤 절반은 멀리 떨어뜨려 Leave 를 만든다.
            server.Players.Teleport(
                player,
                i < RunTicks / 2
                    ? server.World.Transforms.Interpolate(0, new Tick(tick))
                    : new WorldPos(100_000f, 0f, 100_000f));

            await Task.Delay(1, cts.Token);
        }

        observer.Detach();
        await cts.CancelAsync();

        await Ignore(run);
        await Ignore(accept);
        await server.DisposeAsync();

        Assert.True(correlation > 0, "스크립트가 명령을 하나도 못 냈다");

        // 테스트가 틱을 직접 미는 회차다 — 게임서버의 페이싱을 볼 수 없으므로
        // 틱 속도는 미판정이 된다. 통과로 세면 거짓이 된다.
        Observation observation = observer.Snapshot(paced: false);

        ImmutableArray<CheckResult> results = Report.Run(observation);

        string markdown = Report.Markdown(
            results, observation, "게임서버 대역 (Npc.TestGameServer)", "테스트 회차 — 시각은 싣지 않는다");

        string directory = TestPaths.At("docs", "measurements");

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ReportStem + ".md"), markdown);
        File.WriteAllText(
            Path.Combine(directory, ReportStem + ".json"),
            Report.Json(results, "게임서버 대역 (Npc.TestGameServer)", "테스트 회차"));

        string[] failures =
        [
            .. results.Where(r => r.Verdict == Verdict.Fail)
                .Select(r => $"{r.Id}: {r.Detail} · {string.Join(" / ", r.Violations)}"),
        ];

        Assert.True(failures.Length == 0, string.Join("\n", failures));

        // 관찰이 비어 있으면 "전부 미판정" 으로도 합격이 된다. 그것은 통과가 아니다.
        Assert.True(
            results.Count(r => r.Verdict == Verdict.Pass) >= 4,
            "판정된 검사가 너무 적다 — 회차가 비어 있다. "
            + string.Join(" · ", results.Select(r => $"{r.Id}={r.Verdict}")));
    }

    /// <summary>
    /// 스크립트 — NPC 마다 <c>MoveTo</c> 를 하나씩 낸다.
    ///
    /// <b>집으로 보낸다.</b> 목적지가 무엇인지는 규약과 무관하고, 필요한 것은
    /// "명령을 냈으면 응답이 온다" 하나다.
    /// </summary>
    private static uint Probe(
        TcpGameServerLink link, Observer observer, NpcRoster roster, long tick)
    {
        uint correlation = 0;

        for (int i = 0; i < roster.Count; i++)
        {
            NpcInstanceDef instance = roster.Npcs[i];

            var command = new NpcCommand
            {
                Kind = NpcCommandKind.MoveTo,

                // A-08 — 와이어의 NpcId 는 전역 인스턴스 id 다. 슬롯 첨자가 아니다.
                Npc = new NpcId(instance.Id),
                IssuedAt = new Tick(tick),
                Correlation = new CorrelationId(++correlation),
                Priority = CommandPriority.Normal,
                TargetPoi = instance.Home,
            };

            link.Enqueue(in command);

            // 넉넉히 준다. 이 검사가 보는 것은 "응답이 오는가" 이지 "얼마나 빠른가" 가 아니다.
            observer.RecordIssued(in command, timeoutTicks: 400);
        }

        link.FlushAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

        return correlation;
    }

    /// <summary>취소로 끝난 태스크를 조용히 거둔다.</summary>
    private static async Task Ignore(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // 종료 지시다.
        }
    }

    /// <summary>보고서가 <c>docs/measurements/</c> 에 커밋돼 있어야 한다 (B-07 완료 조건).</summary>
    [Fact]
    public void ConformanceReport_IsCommitted()
    {
        string path = TestPaths.At("docs", "measurements", ReportStem + ".md");

        Assert.True(
            File.Exists(path),
            "대역 적합성 보고서가 없다. Conformance_TestBedPasses 를 돌리면 만들어진다.");

        string text = File.ReadAllText(path);

        Assert.Contains("판정: 합격", text, StringComparison.Ordinal);
        Assert.Contains("미판정은 통과가 아니다", text, StringComparison.Ordinal);
    }

    /// <summary>보고서의 판정 수를 세는 보조. 회차가 비었는지 눈으로 볼 때 쓴다.</summary>
    internal static string Summarize(ImmutableArray<CheckResult> results) => string.Create(
        CultureInfo.InvariantCulture,
        $"통과 {results.Count(r => r.Verdict == Verdict.Pass)} · " +
        $"불합격 {results.Count(r => r.Verdict == Verdict.Fail)} · " +
        $"미판정 {results.Count(r => r.Verdict == Verdict.NotChecked)}");
}
