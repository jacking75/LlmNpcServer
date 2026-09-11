using System.Globalization;
using Npc.Contracts;
using Npc.Core;
using Npc.Host;
using Npc.Runtime;

namespace Npc.Tests.Scenarios;

/// <summary>
/// B-06 완료 조건 — <b>적대 플레이어가 지나가면 경비병이 공격하고, 멀어지면 순찰로 돌아간다.</b>
///
/// <para>
/// 루프백으로 돈다. 게임서버 대역의 <c>--hostile-bots</c> 가 <c>PlayerHostility(Hostile)</c> 를
/// 내고, 봇이 랜덤 워크로 멀어지면 <c>PlayerProximity(Leave)</c> 가 적대를 내린다 —
/// <b>둘 다 게임서버가 판정한다.</b> NPC 서버는 받은 결과로 인터럽트를 돌릴 뿐이다.
/// </para>
///
/// <para>
/// <b>정확한 수를 세지 않는다.</b> 봇의 랜덤 워크는 결정론이지만 어느 NPC 가 몇 번 걸릴지는
/// POI 배치에 달렸다 — 회차 길이를 늘리면 값이 바뀐다. 여기서 보는 것은 <b>경로가 도는가</b> 다.
/// </para>
/// </summary>
[Trait("Category", "Determinism")]
public sealed class HostilePlayerTests
{
    /// <summary>
    /// 적대 플래그가 서고, 그 NPC 가 <c>CombatAction(TargetPlayer≠0)</c> 을 낸다.
    /// </summary>
    [Fact]
    public async Task Hostile_GuardAttacksThePlayerAndReturnsToPatrol()
    {
        await using NpcHost host = Host(npcs: 300, hostileBots: 8);

        await host.RunAsync(CancellationToken.None);

        HostSnapshot snapshot = host.Snapshot();

        // 대역이 적대 이벤트를 실제로 냈는가. 0 이면 아래 단언들이 아무것도 안 본다.
        Assert.True(
            snapshot.HostilityEvents > 0,
            $"PlayerHostility 가 0건이다 — 회차가 비어 있다. {Describe(snapshot)}");

        // 플레이어를 대상으로 한 전투 명령이 나갔는가. 이것이 B-06 의 핵심이다 —
        // 전에는 Attack 의 대상이 TargetNpc 뿐이라 플레이어를 찍을 길이 없었다.
        Assert.True(
            snapshot.PlayerTargetedCommands > 0,
            $"TargetPlayer 를 찍은 명령이 0건이다. {Describe(snapshot)}");

        // 회차 끝에 적대 플래그가 전부 남아 있으면 내리는 경로가 없는 것이다 —
        // 그 상태면 경비병이 영원히 공격 자세로 굳는다.
        int stuck = 0;

        for (int npc = 0; npc < host.Npcs; npc++)
        {
            if ((host.Store.Flags[npc] & WorldFlags.HostilePlayerNearby) != 0)
            {
                stuck++;
            }
        }

        Assert.True(
            stuck < host.Npcs,
            $"모든 NPC 가 적대 상태로 굳었다 — 내리는 경로가 없다. {Describe(snapshot)}");

        // 크래시 없이 완주했는가.
        Assert.Equal(0, snapshot.Link.EventGapsDetected);
        Assert.True(snapshot.StepsAdvanced > 0, Describe(snapshot));
    }

    /// <summary>
    /// <b>적대 봇이 없으면 적대 이벤트도 없다.</b> 기본이 꺼져 있음을 못 박는다 —
    /// 기본으로 켜지면 모든 측정 회차에 전투가 섞여 들어간다.
    /// </summary>
    [Fact]
    public async Task NoHostileBots_MeansNoHostilityEvents()
    {
        await using NpcHost host = Host(npcs: 120, hostileBots: 0);

        await host.RunAsync(CancellationToken.None);

        HostSnapshot snapshot = host.Snapshot();

        Assert.Equal(0, snapshot.HostilityEvents);

        for (int npc = 0; npc < host.Npcs; npc++)
        {
            Assert.Equal(0, host.Store.HostilePlayer[npc]);
        }
    }

    private static NpcHost Host(int npcs, int hostileBots)
    {
        string[] args =
        [
            "--loopback",
            "--npcs", npcs.ToString(CultureInfo.InvariantCulture),
            "--time-scale", "600",
            "--days", "1",
            "--max-speed",
            "--no-dashboard",
            "--no-llm",
            "--player-bots", "24",
            "--hostile-bots", hostileBots.ToString(CultureInfo.InvariantCulture),

            // 프리베이크 스토어를 읽지 않는다 — 이 회차가 보는 것은 적대 경로이지 플랜 품질이 아니다.
            "--planstore", "no-planstore-hostile",
        ];

        Assert.True(HostOptions.TryParse(args, out HostOptions options, out string? error), error);

        return NpcHost.Create(options with { MasterData = TestPaths.MasterData }, TextWriter.Null);
    }

    private static string Describe(HostSnapshot snapshot) =>
        $"hostility {snapshot.HostilityEvents} · targetPlayer {snapshot.PlayerTargetedCommands} · "
        + $"steps {snapshot.StepsAdvanced} · cmd {snapshot.CommandsEmitted}";
}
