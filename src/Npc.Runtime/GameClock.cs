using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;

namespace Npc.Runtime;

/// <summary>
/// 게임 시계. docs/11 §5.
///
/// <b><see cref="DateTime"/>·<see cref="System.Diagnostics.Stopwatch"/> 를 쓰지 않는다</b> (CLAUDE.md §2.3).
/// 시간 기준은 게임서버가 보내는 <c>TickSync</c> 하나뿐이다 (docs/02 §3.3).
/// 그래야 리플레이가 100% 일치한다.
///
/// 환산: 실시간 1초 = 10틱. 게임 초 = 틱 × TimeScale ÷ 10.
/// TimeScale 60 이면 게임 하루(24시간 = 86,400 게임초)가 실시간 24분이다.
/// </summary>
public sealed class GameClock
{
    private readonly BucketSpace _buckets;
    private long _originGameSeconds;
    private long _syncedTick;

    // 게임서버가 알려준 시각을 틱 루프 밖에서 받아 두는 자리 (A-10).
    // 소켓 스레드가 쓰고 틱 루프가 읽는다 — 락 없이 Volatile 만 쓴다 (CLAUDE.md §2.1).
    private long _pendingOriginTick = -1;
    private int _pendingOriginMinute = -1;
    private int _originRequested;

    /// <summary>게임 하루의 초.</summary>
    public const int SecondsPerGameDay = 24 * 3600;

    /// <summary>시계를 만든다.</summary>
    /// <param name="buckets">시간대 구간 정의. context_buckets.json 에서 온다.</param>
    /// <param name="timeScale">1 = 실시간, 60 = 60배속.</param>
    /// <param name="startGameHour">시작 게임 시각 0~23.</param>
    public GameClock(BucketSpace buckets, int timeScale, int startGameHour = 6)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeScale);
        ArgumentOutOfRangeException.ThrowIfNegative(startGameHour);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startGameHour, 23);

        _buckets = buckets;
        TimeScale = timeScale;
        _originGameSeconds = (long)startGameHour * 3600;
        TimeOfDay = buckets.TimeOfDayAt(startGameHour);
    }

    /// <summary>현재 틱.</summary>
    public Tick Current { get; private set; }

    /// <summary>1 = 실시간, 60 = 60배속.</summary>
    public int TimeScale { get; }

    /// <summary>현재 시간대.</summary>
    public TimeOfDay TimeOfDay { get; private set; }

    /// <summary>마지막 <see cref="TryAdvance"/> 에서 시간대가 바뀌었는가.</summary>
    public bool TimeOfDayChanged { get; private set; }

    /// <summary>게임 시작부터의 게임 초.</summary>
    public long GameSeconds => _originGameSeconds + (Current.Value * TimeScale / Tick.PerSecond);

    /// <summary>
    /// 게임서버가 알려준 시각을 예약한다 (A-10). <b>소켓 스레드에서 부른다.</b>
    ///
    /// 여기서 시계를 직접 고치지 않는다 — 틱 루프가 돌고 있으면 데이터 레이스다.
    /// 예약만 하고 반영은 틱 경계에서 <see cref="TryApplyPendingOrigin"/> 이 한다.
    /// </summary>
    /// <param name="startTick">게임서버의 현재 틱.</param>
    /// <param name="minuteOfDay">게임 안 하루의 몇 분째인가 (0~1439). 음수면 "모른다".</param>
    public void RequestOrigin(long startTick, int minuteOfDay)
    {
        if (minuteOfDay < 0)
        {
            return;   // v1 게임서버는 시각을 모른다. 없는 것을 있는 척하지 않는다.
        }

        Volatile.Write(ref _pendingOriginTick, startTick);
        Volatile.Write(ref _pendingOriginMinute, minuteOfDay);
        Volatile.Write(ref _originRequested, 1);
    }

    /// <summary>
    /// 예약된 시각을 반영한다 (A-10). <b>틱 경계에서만 부른다.</b>
    ///
    /// <b>게임서버 값이 이긴다.</b> 스냅샷(A-01)이 복원한 시계와 충돌하면 게임서버 쪽으로 맞춘다 —
    /// 세계를 미는 것은 게임서버이고, NPC 서버가 다른 시각을 들고 있으면 스케줄 전체가 어긋난다.
    /// </summary>
    /// <returns>반영했으면 true.</returns>
    public bool TryApplyPendingOrigin()
    {
        if (Volatile.Read(ref _originRequested) == 0)
        {
            return false;
        }

        Volatile.Write(ref _originRequested, 0);

        long startTick = Volatile.Read(ref _pendingOriginTick);
        int minute = Volatile.Read(ref _pendingOriginMinute);

        // 원점을 [0, 하루) 로 정규화한다. 그러지 않으면 GameSeconds 가 음수가 되어
        // GameDay·GameHour 가 뒤집힌다.
        long elapsed = startTick * TimeScale / Tick.PerSecond;
        long wanted = (long)minute * 60;

        _originGameSeconds = (((wanted - elapsed) % SecondsPerGameDay) + SecondsPerGameDay)
            % SecondsPerGameDay;

        Current = new Tick(startTick);
        _syncedTick = Math.Max(_syncedTick, startTick);

        TimeOfDay = _buckets.TimeOfDayAt(GameHour);
        TimeOfDayChanged = false;

        return true;
    }

    /// <summary>현재 게임 시각 0~23.</summary>
    public int GameHour => (int)(GameSeconds / 3600 % 24);

    /// <summary>몇 번째 게임 날인가. 0부터.</summary>
    public long GameDay => GameSeconds / SecondsPerGameDay;

    /// <summary>게임 하루에 해당하는 틱 수.</summary>
    public long TicksPerGameDay => (long)SecondsPerGameDay * Tick.PerSecond / TimeScale;

    /// <summary>게임 <paramref name="days"/> 일에 해당하는 틱 수.</summary>
    public long TicksForGameDays(int days) => TicksPerGameDay * days;

    /// <summary>
    /// 게임서버 틱과 동기화한다. <c>TickSync</c> 수신 시 <see cref="EventApplier"/> 가 부른다.
    /// 과거로는 되돌리지 않는다 — 재전송된 이벤트가 시계를 되감으면 안 된다 (N7).
    /// </summary>
    public void SyncTo(Tick serverTick)
    {
        if (serverTick.Value > _syncedTick)
        {
            _syncedTick = serverTick.Value;
        }
    }

    /// <summary>게임서버가 알려준 마지막 틱. 스냅샷이 담는다 (A-01).</summary>
    public long SyncedTick => _syncedTick;

    /// <summary>
    /// 스냅샷에서 시계를 되돌린다 (A-01). <b>기동 중에만 부른다.</b>
    ///
    /// 게임 시각은 <see cref="GameSeconds"/> 가 틱에서 유도하므로 틱만 되돌리면 따라온다.
    /// 시간대는 되돌린 시각으로 다시 계산한다 — 그러지 않으면 첫 틱에 시간대 전환이
    /// 한꺼번에 터진다.
    /// </summary>
    public void RestoreTo(Tick tick, long syncedTick)
    {
        Current = tick;
        _syncedTick = Math.Max(syncedTick, tick.Value);
        TimeOfDay = _buckets.TimeOfDayAt(GameHour);
        TimeOfDayChanged = false;
    }

    /// <summary>
    /// 동기화된 틱까지 한 틱 진행한다. docs/11 §5 의 틱 루프가 이걸 본다.
    /// 진행할 것이 없으면 false — 루프는 다음 이벤트를 기다린다.
    /// </summary>
    public bool TryAdvance(out Tick tick)
    {
        TimeOfDayChanged = false;

        if (Current.Value >= _syncedTick)
        {
            tick = Current;
            return false;
        }

        Current = new Tick(Current.Value + 1);

        TimeOfDay next = _buckets.TimeOfDayAt(GameHour);
        if (next != TimeOfDay)
        {
            TimeOfDay = next;
            TimeOfDayChanged = true;
        }

        tick = Current;
        return true;
    }

    /// <summary>테스트·루프백에서 시계를 스스로 굴린다. 게임서버 없이 진행할 때만 쓴다.</summary>
    public bool Step(out Tick tick)
    {
        SyncTo(new Tick(Current.Value + 1));
        return TryAdvance(out tick);
    }
}
