using Npc.Contracts;
using Npc.Gateway;
using Npc.Host;

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
