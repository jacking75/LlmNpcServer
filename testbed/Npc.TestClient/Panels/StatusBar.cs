using System.Globalization;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;
using Npc.TestBed.Protocol;
using Npc.TestClient.Format;
using Npc.TestClient.Net;

namespace Npc.TestClient.Panels;

/// <summary>
/// 창 아래 한 줄. docs/20 §9.2.
///
/// <code>
/// day 2  09:14 Morning │ town_center: Peace/Fair
/// link ● 12ms  gs 5,412  npc 5,410 (−2)  drop 0
/// </code>
///
/// <para>
/// <b>여기 있는 네 가지가 데모에서 제일 자주 보는 값이다.</b> 게임 시각(시간대가 언제
/// 넘어가는가) · 존 상태(공성이 걸렸는가) · 링크 지연(NPC 서버가 따라오고 있는가) ·
/// 드롭 수(역압이 걸렸는가). 나머지는 탭으로 들어간다.
/// </para>
///
/// <para>
/// <b>존 상태는 내 플레이어가 선 존의 것이다.</b> 12개를 다 적으면 읽을 수 없고,
/// 사람이 알고 싶은 것은 "지금 내가 보고 있는 곳" 이다. 아직 <c>ZoneStates</c> 를 못 받았으면
/// 마스터데이터의 기본값을 쓴다 — <c>zones.json</c> 은 12개 중 2개를 <c>Alert</c> 로,
/// 2개를 <c>Cold</c> 로 선언하므로 여기서 "전부 Peace/Fair" 라고 쓰면 거짓말이 된다.
/// </para>
///
/// <para>
/// <b>링크 지연의 부호에 주의한다.</b> <c>LinkStatus.NpcServerTick</c> 은 명령의
/// <c>IssuedAt</c> 에서 따온 값이라 <b>NPC 서버가 조용하면 늙는다</b> (docs/20 §7.2 주석) —
/// 지연이 커 보이는 것이 곧 장애는 아니다.
/// </para>
/// </summary>
public sealed class StatusBar : StatusStrip
{
    private readonly MasterDataSet _data;
    private readonly IdNames _names;

    private readonly ToolStripStatusLabel _clock =
        new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    private readonly ToolStripStatusLabel _link =
        new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };

    /// <summary>상태바를 만든다.</summary>
    /// <param name="data">마스터데이터. 존 상태를 아직 못 받았을 때의 기본값이 여기 있다.</param>
    /// <param name="names">id 를 사람 말로 푸는 이름표.</param>
    public StatusBar(MasterDataSet data, IdNames names)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(names);

        _data = data;
        _names = names;

        BackColor = Color.FromArgb(40, 40, 46);
        ForeColor = Color.Gainsboro;
        SizingGrip = false;

        Items.Add(_clock);
        Items.Add(_link);
    }

    /// <summary>
    /// 한 줄을 다시 쓴다. <b>UI 스레드에서, 렌더 타이머마다 부른다.</b>
    /// </summary>
    /// <param name="connection">게임서버 연결.</param>
    /// <param name="playerZone">내 플레이어가 선 존 code. 아직 없으면 0.</param>
    /// <param name="frameMillis">프레임 간격 실측(ms).</param>
    /// <param name="drawMillis">그리기에 쓴 시간 실측(ms). 60fps 예산과 견주는 값은 이쪽이다.</param>
    /// <param name="stalled">스냅샷이 끊겨 보간이 멈췄는가.</param>
    public void Update(
        GameConnection connection, ushort playerZone, double frameMillis, double drawMillis, bool stalled)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _clock.Text = Clock(connection, playerZone);
        _link.Text = Link(connection, frameMillis, drawMillis, stalled);
    }

    // ---------------------------------------------------------------- 왼쪽

    private string Clock(GameConnection connection, ushort playerZone)
    {
        SnapshotFrame? frame = connection.Latest;

        if (frame is not { Snapshot: var shot })
        {
            return connection.State switch
            {
                ConnectionState.Connecting =>
                    string.Create(CultureInfo.InvariantCulture, $"게임서버를 찾는 중… ({connection.Attempts}회)"),
                ConnectionState.Connected => "접속됨 — 첫 스냅샷을 기다리는 중",
                _ => "게임서버 미연결",
            };
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"day {shot.GameDay}  {shot.GameHour:00}:00 {(TimeOfDay)shot.TimeOfDay}   "
            + $"tick {shot.Tick}   entities {shot.EntityCount}   │   {Zone(connection, playerZone)}");
    }

    /// <summary>
    /// <c>town_center: Peace/Fair</c>. 존을 모르면 "-".
    /// </summary>
    private string Zone(GameConnection connection, ushort playerZone)
    {
        if (playerZone == 0)
        {
            return "zone -";
        }

        var zone = new ZoneId(playerZone);
        RegionState region;
        Climate climate;

        if (TryFind(connection.Zones, playerZone, out ZoneState state))
        {
            region = (RegionState)state.RegionState;
            climate = (Climate)state.Climate;
        }
        else
        {
            // 아직 ZoneStates 를 못 받았다. 게임서버도 이 값에서 출발한다 (GameServer.cs).
            ZoneDef def = _data.Zones[zone];

            region = def.DefaultRegionState;
            climate = def.DefaultClimate;
        }

        return string.Create(
            CultureInfo.InvariantCulture, $"{_names.Zone(playerZone)}: {region}/{climate}");
    }

    private static bool TryFind(ZoneStates zones, ushort code, out ZoneState found)
    {
        if (zones.Zones is { } states)
        {
            foreach (ZoneState state in states)
            {
                if (state.ZoneCode == code)
                {
                    found = state;
                    return true;
                }
            }
        }

        found = default;
        return false;
    }

    // ---------------------------------------------------------------- 오른쪽

    private static string Link(
        GameConnection connection, double frameMillis, double drawMillis, bool stalled)
    {
        LinkStatus link = connection.Link;
        long rtt = connection.RoundTripMillis;
        string dot = connection.State == ConnectionState.Connected ? "●" : "○";
        long lag = link.GsTick - link.NpcServerTick;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"client {dot} {(rtt < 0 ? "--" : rtt.ToString(CultureInfo.InvariantCulture))}ms   "
            + $"npc-link {(link.Connected == 1 ? "●" : "○")}   "
            + $"gs {link.GsTick}  npc {link.NpcServerTick} ({-lag})   drop {link.Dropped}   gap {link.Gaps}   "
            + $"frame {frameMillis:F1}ms  draw {drawMillis:F1}ms"
            + $"{(stalled ? "  [스냅샷 지연]" : string.Empty)}");
    }
}
