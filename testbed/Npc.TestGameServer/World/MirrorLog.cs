using Npc.Contracts;

namespace Npc.TestGameServer.World;

/// <summary>명령 한 줄. docs/20 §7.4.</summary>
/// <param name="Tick">발행 틱 (N4 — 여기도 시간은 <see cref="Tick"/> 뿐이다).</param>
/// <param name="Npc">대상 NPC 첨자.</param>
/// <param name="Kind"><see cref="NpcCommandKind"/>.</param>
/// <param name="TargetPoi">목표 POI code. 없으면 0.</param>
/// <param name="Correlation">상관 ID (N5). 로그 패널이 명령과 응답 이벤트를 잇는 유일한 끈이다.</param>
public readonly record struct LoggedCommand(
    long Tick, int Npc, byte Kind, ushort TargetPoi, uint Correlation);

/// <summary>이벤트 한 줄. docs/20 §7.4.</summary>
/// <param name="Tick">발생 틱.</param>
/// <param name="Npc">대상 NPC 첨자.</param>
/// <param name="Kind"><see cref="GameEventKind"/>.</param>
/// <param name="Code">Kind 별 소형 코드. <c>NpcActionFailed</c> 면 <see cref="ActionFailReason"/> 다.</param>
/// <param name="Amount">수량. 근접이면 거리(m), 피해면 피해량.</param>
public readonly record struct LoggedEvent(
    long Tick, int Npc, byte Kind, byte Code, int Amount);

/// <summary>
/// 클라이언트 로그 패널의 원천. docs/20 §7.4.
///
/// <para>
/// 명령·이벤트 각 <see cref="Capacity"/> 칸 링이다. <b>전량을 클라이언트로 보내지 않는다</b> —
/// NPC 500 이면 초당 수백 건이 나온다. 세션마다 <see cref="Cursor"/> 를 하나 들고
/// 자기가 마지막으로 읽은 자리부터 이어 읽는다.
/// </para>
///
/// <para>
/// <b>여기서 거르지 않는다.</b> §7.4 의 세션별 필터(선택 NPC 는 전부 · 그 외는 AOI 안에서
/// 배치당 32건)는 AOI 를 아는 쪽, 즉 클라이언트 세션의 몫이다 (T6-24).
/// 이 클래스가 아는 것은 "무엇이 언제 있었는가" 뿐이다.
/// </para>
///
/// <para>
/// <b>할당이 0 이다.</b> 두 배열을 기동 시 잡고 그 뒤로는 첨자 연산만 한다.
/// 커서만 세션당 하나 만들어지고, 읽기는 호출자가 준 <see cref="Span{T}"/> 에 채운다.
/// </para>
///
/// <para>
/// <b>스레드 계약.</b> 기록은 틱 스레드 하나가 한다. 읽기는 클라이언트 세션 태스크가 하므로
/// 꼬리를 <see cref="Volatile"/> 로만 오간다 — 링을 덮어쓴 구간은
/// <see cref="Cursor.Missed"/> 로 드러난다. <c>lock</c> 은 쓰지 않는다.
/// </para>
/// </summary>
public sealed class MirrorLog
{
    /// <summary>칸 수. 명령·이벤트 각각이다. 2의 거듭제곱이라 마스크로 감는다.</summary>
    public const int Capacity = 1_024;

    private const int Mask = Capacity - 1;

    private readonly LoggedCommand[] _commands = new LoggedCommand[Capacity];
    private readonly LoggedEvent[] _events = new LoggedEvent[Capacity];

    private long _commandTail;
    private long _eventTail;

    /// <summary>지금까지 기록한 명령 수. 링을 덮어쓴 것도 센다.</summary>
    public long CommandsWritten => Volatile.Read(ref _commandTail);

    /// <summary>지금까지 기록한 이벤트 수.</summary>
    public long EventsWritten => Volatile.Read(ref _eventTail);

    /// <summary>
    /// 세션 하나의 읽기 위치. <see cref="MirrorLog.NewCursor"/> 로 만든다.
    ///
    /// <b>지금 꼬리에서 시작한다.</b> 0 에서 시작하면 새로 붙은 클라이언트가 과거 1,024건을
    /// 한꺼번에 받고, 그 화면은 "지금 무슨 일이 일어나는가" 를 보여 주지 못한다.
    /// </summary>
    public sealed class Cursor
    {
        internal long Command;
        internal long Event;

        /// <summary>
        /// 링이 덮어써서 못 읽고 지나간 항목 수.
        ///
        /// 0 이 아니면 그 세션이 밀린 것이다 — 로그 패널에 구멍이 있다는 뜻이고,
        /// 조용히 넘기면 사람이 "이벤트가 안 온다" 로 오해한다.
        /// </summary>
        public long Missed { get; internal set; }
    }

    /// <summary>세션용 커서를 만든다. <b>지금 꼬리에서 시작한다</b> — 과거를 쏟지 않는다.</summary>
    public Cursor NewCursor() => new()
    {
        Command = CommandsWritten,
        Event = EventsWritten,
    };

    /// <summary>명령 한 줄을 남긴다. <b>틱 스레드에서 부른다.</b> 할당 0.</summary>
    public void Record(in NpcCommand command, Tick now)
    {
        long tail = _commandTail;

        _commands[tail & Mask] = new LoggedCommand(
            now.Value,
            command.Npc.Value,
            (byte)command.Kind,
            command.TargetPoi.Value,
            command.Correlation.Value);

        // 값 쓰기가 먼저 보이고 그 다음 꼬리가 보여야 한다.
        Volatile.Write(ref _commandTail, tail + 1);
    }

    /// <summary>이벤트 한 줄을 남긴다. <b>틱 스레드에서 부른다.</b> 할당 0.</summary>
    public void Record(in GameEvent ev)
    {
        long tail = _eventTail;

        _events[tail & Mask] = new LoggedEvent(
            ev.OccurredAt.Value,
            ev.Npc.Value,
            (byte)ev.Kind,
            ev.Code,
            ev.Amount);

        Volatile.Write(ref _eventTail, tail + 1);
    }

    /// <summary>
    /// 커서 이후의 명령을 <paramref name="into"/> 에 채우고 커서를 그만큼 민다.
    /// 오래된 것부터 나온다.
    /// </summary>
    /// <returns>채운 수. 새 것이 없으면 0.</returns>
    public int ReadCommands(Cursor cursor, Span<LoggedCommand> into)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        long tail = CommandsWritten;
        long from = Catch(cursor.Command, tail, cursor);
        int count = (int)Math.Min(into.Length, tail - from);

        for (int i = 0; i < count; i++)
        {
            into[i] = _commands[(from + i) & Mask];
        }

        cursor.Command = from + count;

        return count;
    }

    /// <summary>커서 이후의 이벤트를 <paramref name="into"/> 에 채우고 커서를 민다.</summary>
    /// <returns>채운 수.</returns>
    public int ReadEvents(Cursor cursor, Span<LoggedEvent> into)
    {
        ArgumentNullException.ThrowIfNull(cursor);

        long tail = EventsWritten;
        long from = Catch(cursor.Event, tail, cursor);
        int count = (int)Math.Min(into.Length, tail - from);

        for (int i = 0; i < count; i++)
        {
            into[i] = _events[(from + i) & Mask];
        }

        cursor.Event = from + count;

        return count;
    }

    /// <summary>
    /// 커서가 링 밖으로 밀렸으면 가장 오래된 유효 항목으로 당긴다.
    ///
    /// <b>당긴 만큼을 <see cref="Cursor.Missed"/> 에 적는다.</b> 조용히 당기면 로그 패널의
    /// 구멍이 아무 흔적도 남기지 않는다.
    /// </summary>
    private static long Catch(long position, long tail, Cursor cursor)
    {
        long oldest = Math.Max(0, tail - Capacity);

        if (position >= oldest)
        {
            return position;
        }

        cursor.Missed += oldest - position;

        return oldest;
    }
}
