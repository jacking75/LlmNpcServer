using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;

namespace Npc.TestClient.Format;

/// <summary>
/// id 를 사람 말로 푼다. docs/20 §9.6.
///
/// <para>
/// <b>여기가 프로토콜에 문자열이 없어도 되는 이유다.</b> 링크에도 클라이언트 프로토콜에도
/// <c>string</c> 이 0개인 것은 프롬프트 인젝션을 "넣지 마라" 가 아니라 "넣을 수 없다" 로
/// 만들기 위해서다 (docs/20 §3.5). 이름은 <b>양쪽이 같은 마스터데이터를 읽어</b> 각자 푼다 —
/// 그래서 <c>SrvHello</c> 가 마스터데이터 해시를 실어 보낸다 (docs/20 §8.1).
/// </para>
///
/// <para>
/// <b>모르는 code 에 예외를 던지지 않는다.</b> 로그 한 줄을 못 푼 것이 창을 죽일 이유가 아니다 —
/// 숫자를 그대로 보이고 넘어간다.
/// </para>
/// </summary>
public sealed class IdNames
{
    private readonly MasterDataSet _data;

    /// <summary>이름표를 만든다.</summary>
    public IdNames(MasterDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);

        _data = data;
    }

    /// <summary>POI code → <c>pois.json</c> 의 id. 0 이면 "-".</summary>
    public string Poi(ushort code)
    {
        if (code == 0)
        {
            return "-";
        }

        foreach (PoiDef poi in _data.Pois.Pois)
        {
            if (poi.Code.Value == code)
            {
                return poi.Id;
            }
        }

        return $"poi#{code}";
    }

    /// <summary>존 code → id.</summary>
    public string Zone(ushort code)
    {
        foreach (ZoneDef zone in _data.Zones.Zones)
        {
            if (zone.Code.Value == code)
            {
                return zone.Id;
            }
        }

        return $"zone#{code}";
    }

    /// <summary>아키타입 code → id.</summary>
    public string Archetype(ushort code)
    {
        foreach (ArchetypeDef archetype in _data.Archetypes.Archetypes)
        {
            if (archetype.Code.Value == code)
            {
                return archetype.Id;
            }
        }

        return $"archetype#{code}";
    }

    /// <summary>아이템 code → id.</summary>
    public string Item(ushort code)
    {
        foreach (ItemDef item in _data.Items.Items)
        {
            if (item.Code.Value == code)
            {
                return item.Id;
            }
        }

        return $"item#{code}";
    }

    /// <summary>액션 code → id.</summary>
    public string Action(ushort code)
    {
        foreach (ActionDef action in _data.Actions.Actions)
        {
            if (action.Code.Value == code)
            {
                return action.Id;
            }
        }

        return $"action#{code}";
    }

    /// <summary>명령 종류 이름.</summary>
    public static string CommandKind(byte kind) =>
        Enum.IsDefined((NpcCommandKind)kind) ? ((NpcCommandKind)kind).ToString() : $"cmd#{kind}";

    /// <summary>이벤트 종류 이름.</summary>
    public static string EventKind(byte kind) =>
        Enum.IsDefined((GameEventKind)kind) ? ((GameEventKind)kind).ToString() : $"ev#{kind}";

    /// <summary>
    /// 이벤트의 <c>Code</c> 를 종류에 맞게 푼다.
    ///
    /// <b>같은 바이트가 종류마다 다른 뜻이다</b> — <c>NpcActionFailed</c> 면 실패 사유,
    /// <c>PlayerProximity</c> 면 Enter/Leave, 존 이벤트면 지역 상태·기후다.
    /// 숫자만 보이면 <c>ActionFailed(3)</c> 이 무엇인지 매번 표를 찾아야 한다.
    /// </summary>
    /// <returns>풀린 이름. 뜻이 없는 종류면 빈 문자열.</returns>
    public static string EventCode(byte kind, byte code) => (GameEventKind)kind switch
    {
        GameEventKind.NpcActionFailed => Name<ActionFailReason>(code),
        GameEventKind.PlayerProximity => Name<ProximityChange>(code),
        GameEventKind.ZoneStateChanged => Name<RegionState>(code),
        GameEventKind.WeatherChanged => Name<Climate>(code),
        _ => string.Empty,
    };

    /// <summary>이 이벤트가 인터럽트 계열인가. 로그 필터가 쓴다 (docs/20 §9.6).</summary>
    /// <remarks>
    /// <b>인터럽트 규칙을 여기서 다시 적지 않는다.</b> 실제 판정은 NPC 서버의
    /// <c>InterruptMatcher</c> 가 하고, 이 목록은 "사람이 눈으로 좇고 싶은 계열" 이다 —
    /// 플레이어가 건드린 것, 싸움, 존 상태 변화. 둘이 어긋나도 화면만 조금 넓게 보일 뿐이다.
    /// </remarks>
    public static bool IsInterruptish(byte kind) => (GameEventKind)kind is
        GameEventKind.PlayerProximity
        or GameEventKind.PlayerInteracted
        or GameEventKind.CombatStarted
        or GameEventKind.CombatEnded
        or GameEventKind.DamageTaken
        or GameEventKind.ZoneStateChanged
        or GameEventKind.WeatherChanged
        or GameEventKind.NpcDespawned;

    /// <summary>이 이벤트가 실패인가.</summary>
    public static bool IsFailure(byte kind) => (GameEventKind)kind == GameEventKind.NpcActionFailed;

    private static string Name<T>(byte code)
        where T : struct, Enum =>
        Enum.IsDefined((T)(object)code) ? ((T)(object)code).ToString() : code.ToString();
}
