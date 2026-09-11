using Npc.Contracts;
using Npc.TestGameServer.World;
using Npc.Tests.Runtime;

namespace Npc.Tests.TestBed;

/// <summary>
/// T6-21 — <see cref="MirrorLog"/>. docs/20 §7.4.
///
/// <para>
/// 클라이언트 로그 패널의 원천이다. NPC 500 이면 초당 수백 건이 나오므로 전량을 들고
/// 있을 수 없고, 최근 1,024건만 남긴 채 세션마다 커서로 이어 읽는다.
/// </para>
///
/// <para>
/// <b>할당 측정이 있어서 <see cref="AllocationCollection"/> 이다.</b>
/// <c>GC.GetAllocatedBytesForCurrentThread</c> 는 스레드 단위지만, 계층형 JIT 승격이
/// 다른 테스트와 경합하면 측정 창 안으로 밀려 들어온다 (2026-07-28 결정 18).
/// </para>
/// </summary>
[Collection(AllocationCollection.Name)]
[Trait("Category", "TestBed")]
public sealed class MirrorLogTests
{
    /// <summary>완료 조건 — 최근 1,024건만 남는다. 넘친 것은 조용히 사라지지 않는다.</summary>
    [Fact]
    public void MirrorLog_KeepsLatest1024()
    {
        const int Written = MirrorLog.Capacity + 500;

        var log = new MirrorLog();
        MirrorLog.Cursor cursor = log.NewCursor();

        for (int i = 0; i < Written; i++)
        {
            log.Record(Command(i), slot: i, new Tick(i));
        }

        Assert.Equal(Written, log.CommandsWritten);

        var into = new LoggedCommand[MirrorLog.Capacity * 2];
        int read = log.ReadCommands(cursor, into);

        // 링에 남은 것은 1,024건뿐이다. 나머지는 덮였고, 그 수가 Missed 에 남는다.
        Assert.Equal(MirrorLog.Capacity, read);
        Assert.Equal(Written - MirrorLog.Capacity, cursor.Missed);

        // 남은 것은 <b>최근</b> 1,024건이다 — 앞이 아니라 뒤가 살아 있어야 한다.
        Assert.Equal(Written - MirrorLog.Capacity, into[0].Npc);
        Assert.Equal(Written - 1, into[read - 1].Npc);

        // 순서는 오래된 것부터다.
        for (int i = 1; i < read; i++)
        {
            Assert.Equal(into[i - 1].Npc + 1, into[i].Npc);
        }
    }

    /// <summary>
    /// 완료 조건 — 커서는 세션별로 따로 움직인다.
    ///
    /// 하나가 읽었다고 다른 세션이 못 읽으면, 클라이언트 두 개를 붙였을 때
    /// 로그가 둘로 쪼개져 나뉜다.
    /// </summary>
    [Fact]
    public void MirrorLog_CursorAdvancesPerSession()
    {
        var log = new MirrorLog();

        MirrorLog.Cursor a = log.NewCursor();

        for (int i = 0; i < 10; i++)
        {
            log.Record(Event(i), slot: i);
        }

        // 늦게 붙은 세션은 <b>지금 꼬리</b>에서 시작한다 — 과거를 쏟지 않는다.
        MirrorLog.Cursor b = log.NewCursor();

        var into = new LoggedEvent[32];

        Assert.Equal(10, log.ReadEvents(a, into));
        Assert.Equal(0, log.ReadEvents(b, into));

        // 다시 읽으면 새 것만 나온다. a 가 읽었다고 b 가 못 읽지 않는다.
        for (int i = 10; i < 15; i++)
        {
            log.Record(Event(i), slot: i);
        }

        Assert.Equal(5, log.ReadEvents(a, into));
        Assert.Equal(10, into[0].Npc);

        Assert.Equal(5, log.ReadEvents(b, into));
        Assert.Equal(10, into[0].Npc);

        // 셋 다 소진됐다.
        Assert.Equal(0, log.ReadEvents(a, into));
        Assert.Equal(0, log.ReadEvents(b, into));
        Assert.Equal(0, a.Missed);
        Assert.Equal(0, b.Missed);
    }

    /// <summary>
    /// 완료 조건 — 할당 0. 배열은 기동 시 잡고 그 뒤로는 첨자 연산만 한다.
    ///
    /// 이 링은 매 틱 명령·이벤트 전량이 지나가는 자리다. 여기서 할당이 새면
    /// 게임서버의 틱 예산이 그대로 무너진다.
    /// </summary>
    [Fact]
    public void MirrorLog_RecordAndReadDoNotAllocate()
    {
        const int Rounds = 10_000;

        var log = new MirrorLog();
        MirrorLog.Cursor cursor = log.NewCursor();
        var into = new LoggedCommand[64];

        // 워밍업. 계층형 JIT 승격이 측정 창 안에서 일어나면 그것이 할당으로 잡힌다.
        for (int i = 0; i < 1_000; i++)
        {
            log.Record(Command(i), slot: i, new Tick(i));
            log.Record(Event(i), slot: i);
            log.ReadCommands(cursor, into);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < Rounds; i++)
        {
            log.Record(Command(i), slot: i, new Tick(i));
            log.Record(Event(i), slot: i);
            log.ReadCommands(cursor, into);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static NpcCommand Command(int i) => new()
    {
        Kind = NpcCommandKind.MoveTo,
        Npc = new NpcId(i),
        IssuedAt = new Tick(i),
        Correlation = new CorrelationId((uint)(i + 1)),
        Priority = CommandPriority.Normal,
        TargetPoi = new PoiId((ushort)((i % 200) + 1)),
    };

    private static GameEvent Event(int i) => new()
    {
        Kind = GameEventKind.NpcArrived,
        Sequence = i + 1,
        OccurredAt = new Tick(i),
        Npc = new NpcId(i),
        Amount = i * 2,
    };
}
