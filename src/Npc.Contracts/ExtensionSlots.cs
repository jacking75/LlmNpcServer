namespace Npc.Contracts;

/// <summary>확장 슬롯 하나. <see cref="ExtensionSlots"/> 의 조회 키다.</summary>
public enum ExtSlot : byte
{
    /// <summary><c>ExtA</c>.</summary>
    A = 0,

    /// <summary><c>ExtB</c>.</summary>
    B = 1,
}

/// <summary>
/// 예약 슬롯의 의미 표 (B-02).
///
/// <b>예약 슬롯은 "아무거나 넣는 칸" 이 아니다.</b> 그렇게 쓰면 두 팀이 같은 슬롯에 서로 다른
/// 것을 넣고, 그 사실을 알아채는 계기가 없다. 그래서 <b>Kind 별로 의미를 여기에 등록</b>하고,
/// 등록되지 않은 Kind 에서는 <b>0 이어야 한다</b>. <c>Ext_ZeroForUndefinedKinds</c> 가 강제한다.
///
/// <para>
/// <b>지금은 등록된 것이 하나도 없다.</b> 슬롯을 쓰려면 여기에 줄을 추가하고,
/// <c>docs/reference_link.html</c> 의 표를 같은 커밋에서 고친다.
/// </para>
///
/// <para>
/// <see cref="InstanceId"/>·<see cref="FactionId"/> 는 예약 슬롯이 아니다 — 의미가 이미 정해져
/// 있으므로 이 표를 거치지 않는다.
/// </para>
/// </summary>
public static class ExtensionSlots
{
    /// <summary>
    /// 명령 쪽 등록부. <c>(Kind, Slot)</c> 로 조회한다.
    /// <b>빈 배열이 정상이다</b> — 지금 의미가 정해진 슬롯이 없다.
    /// </summary>
    private static readonly (NpcCommandKind Kind, ExtSlot Slot, string Meaning)[] CommandSlots = [];

    /// <summary>이벤트 쪽 등록부.</summary>
    private static readonly (GameEventKind Kind, ExtSlot Slot, string Meaning)[] EventSlots = [];

    /// <summary>이 명령 종류의 이 슬롯에 의미가 정해져 있는가.</summary>
    public static bool IsDefined(NpcCommandKind kind, ExtSlot slot)
    {
        foreach ((NpcCommandKind k, ExtSlot s, _) in CommandSlots)
        {
            if (k == kind && s == slot)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>이 이벤트 종류의 이 슬롯에 의미가 정해져 있는가.</summary>
    public static bool IsDefined(GameEventKind kind, ExtSlot slot)
    {
        foreach ((GameEventKind k, ExtSlot s, _) in EventSlots)
        {
            if (k == kind && s == slot)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>정해진 의미. 없으면 null.</summary>
    public static string? MeaningOf(NpcCommandKind kind, ExtSlot slot)
    {
        foreach ((NpcCommandKind k, ExtSlot s, string meaning) in CommandSlots)
        {
            if (k == kind && s == slot)
            {
                return meaning;
            }
        }

        return null;
    }

    /// <summary>정해진 의미. 없으면 null.</summary>
    public static string? MeaningOf(GameEventKind kind, ExtSlot slot)
    {
        foreach ((GameEventKind k, ExtSlot s, string meaning) in EventSlots)
        {
            if (k == kind && s == slot)
            {
                return meaning;
            }
        }

        return null;
    }

    /// <summary>
    /// 정의되지 않은 슬롯이 0 인가 (B-02).
    ///
    /// <b>발행 직전에 부른다.</b> 여기서 걸러야 "누가 임시로 넣어 둔 값" 이 게임서버에
    /// 도달하지 않는다.
    /// </summary>
    public static bool IsClean(in NpcCommand command) =>
        (command.ExtA == 0 || IsDefined(command.Kind, ExtSlot.A))
        && (command.ExtB == 0 || IsDefined(command.Kind, ExtSlot.B));

    /// <summary>같다. 이벤트 쪽.</summary>
    public static bool IsClean(in GameEvent gameEvent) =>
        (gameEvent.ExtA == 0 || IsDefined(gameEvent.Kind, ExtSlot.A))
        && (gameEvent.ExtB == 0 || IsDefined(gameEvent.Kind, ExtSlot.B));
}
