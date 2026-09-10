using MemoryPack;
using Npc.Wire.V2;

namespace Npc.Tests.Wire;

/// <summary>
/// B-03 — v2 배치의 명시 직렬화.
///
/// <b>여기서 지키는 것은 "형식이 안 바뀌었다" 이다.</b> MemoryPack 원시 복사를 명시 쓰기로
/// 갈아 끼우면서 바이트가 한 개라도 달라지면 이미 붙어 있는 상대가 조용히 깨진다.
/// 그래서 두 경로의 바이트를 직접 대조한다.
/// </summary>
[Trait("Category", "Wire")]
public sealed class WireWriterTests
{
    /// <summary>
    /// <b>명시 쓰기의 바이트가 MemoryPack 의 것과 같다.</b>
    ///
    /// <para>
    /// 같은 이유는 셋이 겹쳤기 때문이다 — v2 배치에 암묵 패딩이 없고(<c>Reserved</c> 로 꼬리를
    /// 명시했다), 이 기계가 리틀엔디언이고, MemoryPack 이 unmanaged 배열을 길이 접두 + 원시 복사로
    /// 쓴다. <b>셋 중 하나라도 깨지면 이 테스트가 먼저 알려 준다.</b>
    /// </para>
    /// </summary>
    [Fact]
    public void WireWriter_MatchesMemoryPackBytes()
    {
        Assert.True(
            WireWriter.RawCopyMatchesWireOrder,
            "빅엔디언 기계다. 명시 직렬화는 그대로 돌지만 원시 복사와의 바이트 일치는 성립하지 않는다.");

        WireCommandV2[] commands = [Command(1), Command(2), Command(3)];
        WireEventV2[] events = [Event(1), Event(2)];

        byte[] fromWriter = new byte[WireWriter.CommandBatchSize(commands.Length)];

        Assert.Equal(fromWriter.Length, WireWriter.WriteCommands(fromWriter, commands));
        Assert.Equal(MemoryPackSerializer.Serialize(commands), fromWriter);

        byte[] eventBytes = new byte[WireWriter.EventBatchSize(events.Length)];

        Assert.Equal(eventBytes.Length, WireWriter.WriteEvents(eventBytes, events));
        Assert.Equal(MemoryPackSerializer.Serialize(events), eventBytes);
    }

    /// <summary>빈 배치도 프레임 하나다 (N8). 접두 4바이트만 나간다.</summary>
    [Fact]
    public void WireWriter_EmptyBatchIsFourBytes()
    {
        byte[] buffer = new byte[WireWriter.CommandBatchSize(0)];

        Assert.Equal(4, WireWriter.WriteCommands(buffer, []));
        Assert.Equal(MemoryPackSerializer.Serialize(Array.Empty<WireCommandV2>()), buffer);

        Assert.True(WireWriter.TryReadCommands(buffer, new WireCommandV2[4], out int count));
        Assert.Equal(0, count);
    }

    /// <summary>
    /// <b>null 배열(접두 −1)은 빈 배치가 아니다.</b> MemoryPack 이 그렇게 쓰고, 읽는 쪽은
    /// 둘 다 "할 일 없음" 으로 다루되 <b>구분해서</b> 다뤄야 한다 — 잘라 읽으면 안 된다.
    /// </summary>
    [Fact]
    public void WireWriter_RejectsNullArrayPrefix()
    {
        byte[] nullArray = MemoryPackSerializer.Serialize<WireCommandV2[]>(null);

        Assert.True(WireWriter.TryReadCount(nullArray, out int declared));
        Assert.True(declared < 0, $"null 배열의 접두가 음수가 아니다: {declared}");

        Assert.False(WireWriter.TryReadCommands(nullArray, new WireCommandV2[4], out int count));
        Assert.Equal(0, count);
    }

    /// <summary>왕복. 모든 필드에 서로 다른 값을 넣어 매핑이 뒤바뀌는 것도 잡는다.</summary>
    [Fact]
    public void WireWriter_RoundTrips()
    {
        WireCommandV2[] commands = [Command(11), Command(12)];
        byte[] bytes = new byte[WireWriter.CommandBatchSize(commands.Length)];

        WireWriter.WriteCommands(bytes, commands);

        var back = new WireCommandV2[8];

        Assert.True(WireWriter.TryReadCommands(bytes, back, out int count));
        Assert.Equal(2, count);
        Assert.Equal(commands[0], back[0]);
        Assert.Equal(commands[1], back[1]);

        WireEventV2[] events = [Event(21), Event(22), Event(23)];
        byte[] eventBytes = new byte[WireWriter.EventBatchSize(events.Length)];

        WireWriter.WriteEvents(eventBytes, events);

        var eventsBack = new WireEventV2[8];

        Assert.True(WireWriter.TryReadEvents(eventBytes, eventsBack, out int eventCount));
        Assert.Equal(3, eventCount);
        Assert.Equal(events[2], eventsBack[2]);
    }

    /// <summary>
    /// <b>잘린 페이로드는 false 다.</b> 잘라 읽으면 그 프레임부터 스트림 전체가 쓰레기가 되고,
    /// 증상은 "가끔 이상한 명령이 온다" 로만 나타난다.
    /// </summary>
    [Fact]
    public void WireWriter_RejectsTruncatedPayload()
    {
        WireCommandV2[] commands = [Command(1), Command(2)];
        byte[] bytes = new byte[WireWriter.CommandBatchSize(commands.Length)];

        WireWriter.WriteCommands(bytes, commands);

        Assert.False(WireWriter.TryReadCommands(bytes.AsSpan(0, bytes.Length - 1), new WireCommandV2[4], out _));
        Assert.False(WireWriter.TryReadCommands(bytes.AsSpan(0, 3), new WireCommandV2[4], out _));

        // 담을 곳이 모자라도 false 다. 넘치는 만큼 버리면 명령이 조용히 사라진다.
        Assert.False(WireWriter.TryReadCommands(bytes, new WireCommandV2[1], out _));
    }

    /// <summary>버퍼가 모자라면 던진다. 조용히 잘라 쓰는 경로를 만들지 않는다.</summary>
    [Fact]
    public void WireWriter_ThrowsOnShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => WireWriter.WriteCommands(new byte[8], [Command(1)]));
        Assert.Throws<ArgumentException>(() => WireWriter.WriteEvents(new byte[8], [Event(1)]));
        Assert.Throws<ArgumentException>(() => WireWriter.WriteCommand(new byte[71], Command(1)));
        Assert.Throws<ArgumentException>(() => WireWriter.WriteEvent(new byte[79], Event(1)));
    }

    // ---------------------------------------------------------------- 표본

    /// <summary>필드마다 다른 값. 오프셋이 어긋나면 왕복에서 값이 섞인다.</summary>
    internal static WireCommandV2 Command(int seed) => new()
    {
        IssuedAt = seed + 1_000_000_000L,
        Npc = seed + 2,
        TargetNpc = seed + 3,
        TargetPlayer = seed + 4,
        Amount = -(seed + 5),
        Correlation = (uint)seed + 6,
        PosX = seed + 7.5f,
        PosY = seed + 8.25f,
        PosZ = -(seed + 9.125f),
        ExtA = (uint)seed + 10,
        ExtB = (uint)seed + 11,
        Reserved = 0,
        TargetPoi = (ushort)(seed + 12),
        Item = (ushort)(seed + 13),
        Animation = (ushort)(seed + 14),
        Dialogue = (ushort)(seed + 15),
        Archetype = (ushort)(seed + 16),
        Zone = (ushort)(seed + 17),
        Instance = (ushort)(seed + 18),
        Faction = (ushort)(seed + 19),
        Kind = (byte)(seed + 20),
        Priority = (byte)(seed + 21),
        Visual = (byte)(seed + 22),
        Flags = (byte)(seed + 23),
    };

    /// <summary>같다. 이벤트 쪽.</summary>
    internal static WireEventV2 Event(int seed) => new()
    {
        Sequence = seed + 2_000_000_000L,
        OccurredAt = seed + 3_000_000_000L,
        Npc = seed + 3,
        OtherNpc = seed + 4,
        Player = seed + 5,
        Amount = -(seed + 6),
        Correlation = (uint)seed + 7,
        PosX = seed + 8.5f,
        PosY = seed + 9.25f,
        PosZ = -(seed + 10.125f),
        Heading = seed + 11.75f,
        ExtA = (uint)seed + 12,
        ExtB = (uint)seed + 13,
        Reserved = 0,
        Poi = (ushort)(seed + 14),
        Zone = (ushort)(seed + 15),
        Item = (ushort)(seed + 16),
        Instance = (ushort)(seed + 17),
        Faction = (ushort)(seed + 18),
        Hp = (short)(seed + 19),
        Stamina = (short)-(seed + 20),
        Kind = (byte)(seed + 21),
        Code = (byte)(seed + 22),
    };
}
