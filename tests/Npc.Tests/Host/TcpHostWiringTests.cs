using Npc.Contracts;
using Npc.Gateway;
using Npc.Host;
using Npc.MasterData;

namespace Npc.Tests.Host;

/// <summary>
/// <c>--link tcp</c> 배선. docs/20 §10.3 · T6-12.
///
/// <b>핵심은 "대역을 만들지 않는다" 하나다.</b> TCP 는 <c>Replay</c> 와 같은 경로다 —
/// 세계를 미는 것은 게임서버이고 우리는 이벤트를 받아 명령을 낼 뿐이다.
/// <c>Npc.Sim</c> 이 같이 돌면 <b>같은 세계를 두 곳에서 밀게 되어</b> NPC 가 이중으로 움직인다.
/// </summary>
public sealed class TcpHostWiringTests
{
    private static HostOptions Options(params string[] args)
    {
        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return options with { MasterData = TestPaths.MasterData };
    }

    /// <summary>T6-12 완료 조건 — <c>Tcp</c> 는 <c>SimDriver</c> 를 만들지 않는다.</summary>
    [Fact]
    public async Task Host_TcpLinkDoesNotCreateSimDriver()
    {
        // 게임서버가 없는 포트를 준다. 기동 자체는 성공해야 한다 —
        // 붙는 것은 RunAsync 의 몫이고, 없으면 Connecting 으로 재시도한다.
        await using NpcHost host = NpcHost.Create(
            Options("--link", "tcp", "--gs-port", "17010", "--npcs", "8", "--no-dashboard"),
            TextWriter.Null);

        Assert.Null(host.Driver);
        Assert.IsType<TcpGameServerLink>(host.Link);

        // 아직 붙지 않았다. 크래시하지 않고 이 상태로 서 있는 것이 맞다.
        Assert.Equal(LinkState.Disconnected, host.Link.State);
    }

    /// <summary>대조군 — <c>Loopback</c> 은 대역을 만든다. 위 단언이 공허하지 않다는 뜻이다.</summary>
    [Fact]
    public async Task Host_LoopbackCreatesSimDriver()
    {
        await using NpcHost host = NpcHost.Create(
            Options("--link", "loopback", "--npcs", "8", "--no-dashboard"),
            TextWriter.Null);

        Assert.NotNull(host.Driver);
    }

    /// <summary>
    /// <c>--link record --gs-port ...</c> 는 <b>TCP 링크를 감싼다</b> (docs/20 §10.3).
    ///
    /// 그러면 소켓으로 받은 이벤트 열을 그대로 기록해 나중에 <c>--link replay</c> 로 재생할 수 있다.
    /// </summary>
    [Fact]
    public async Task Host_RecordWrapsTcpWhenGameServerGiven()
    {
        string trace = Path.Combine(Path.GetTempPath(), $"npc-t612-{Guid.NewGuid():N}.jsonl");

        try
        {
            await using NpcHost host = NpcHost.Create(
                Options(
                    "--link", "record", "--gs-port", "17011",
                    "--trace", trace, "--npcs", "8", "--no-dashboard"),
                TextWriter.Null);

            // 데코레이터가 감싸므로 바깥은 Recording 이고, 대역은 만들지 않는다.
            Assert.IsType<RecordingGameServerLink>(host.Link);
            Assert.Null(host.Driver);
        }
        finally
        {
            if (File.Exists(trace))
            {
                File.Delete(trace);
            }
        }
    }

    /// <summary>
    /// T6-12 남은 일 — <c>--zone</c> 이 <b>로스터에 실제로 걸린다.</b>
    ///
    /// <para>
    /// 파싱만 되고 <c>NpcRoster.Select</c> 에 안 넘어가던 구간이 있었다. 게임서버는 필터를
    /// 적용하므로 그 상태로 <c>--zone</c> 을 주면 <b>로스터 해시가 어긋나 핸드셰이크에서
    /// 거절된다</b> — 증상이 연결 실패로만 나타나 "게임서버가 이상하다" 로 오해하기 쉽다.
    /// </para>
    ///
    /// <para>양쪽이 같은 함수를 부르는지를 해시로 못 박는다 (docs/20 §10.2).</para>
    /// </summary>
    [Fact]
    public async Task Host_ZoneFilterAppliesToRoster()
    {
        const string Zone = "town_center";

        await using NpcHost filtered = NpcHost.Create(
            Options("--link", "null", "--zone", Zone, "--npcs", "64", "--no-dashboard"),
            TextWriter.Null);

        // 게임서버(T6-14)가 부르는 것과 같은 호출이다.
        MasterDataSet data = MasterDataLoader.Load(TestPaths.MasterData);
        NpcInstanceTable instances = NpcInstanceTable.Load(
            Path.Combine(TestPaths.MasterData, "npc_instances.json"), data);

        Assert.True(data.Zones.TryGet(Zone, out ZoneDef zone));

        NpcRoster expected = NpcRoster.Select(instances, 64, [zone.Code]);

        Assert.Equal(expected.Hash, filtered.Roster.Hash);
        Assert.Equal(expected.Count, filtered.Npcs);

        // 전부 그 존이다. 필터가 안 걸리면 여기서 깨진다.
        Assert.All(filtered.Roster.Npcs, npc => Assert.Equal(zone.Code, npc.Zone));

        // 대조군 — 필터가 없으면 다른 집합이다. 위 단언이 공허하지 않다는 뜻이다.
        await using NpcHost all = NpcHost.Create(
            Options("--link", "null", "--npcs", "64", "--no-dashboard"),
            TextWriter.Null);

        Assert.NotEqual(filtered.Roster.Hash, all.Roster.Hash);
    }

    /// <summary>
    /// T6-12 남은 일 — 모르는 존 id 는 <b>기동 실패</b>다.
    ///
    /// 조용히 무시하면 필터가 없는 것처럼 로스터가 커지고, 그 사고는 핸드셰이크 거절로만
    /// 나타나 원인이 멀어진다.
    /// </summary>
    [Fact]
    public void Host_UnknownZoneFailsStartup()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            NpcHost.Create(
                Options("--link", "null", "--zone", "atlantis", "--npcs", "8", "--no-dashboard"),
                TextWriter.Null));

        // 무엇이 틀렸는지와 무엇이 있는지를 같이 알려 준다.
        Assert.Contains("atlantis", error.Message, StringComparison.Ordinal);
        Assert.Contains("town_center", error.Message, StringComparison.Ordinal);
    }

    /// <summary><c>--gs-*</c> 없이 <c>--link record</c> 면 예전처럼 대역을 감싼다. 회귀 방지.</summary>
    [Fact]
    public async Task Host_RecordWrapsSimWhenNoGameServerGiven()
    {
        string trace = Path.Combine(Path.GetTempPath(), $"npc-t612-{Guid.NewGuid():N}.jsonl");

        try
        {
            await using NpcHost host = NpcHost.Create(
                Options("--link", "record", "--trace", trace, "--npcs", "8", "--no-dashboard"),
                TextWriter.Null);

            Assert.IsType<RecordingGameServerLink>(host.Link);
            Assert.NotNull(host.Driver);
        }
        finally
        {
            if (File.Exists(trace))
            {
                File.Delete(trace);
            }
        }
    }
}
