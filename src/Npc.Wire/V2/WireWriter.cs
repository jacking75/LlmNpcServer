using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Npc.Wire.V2;

/// <summary>
/// v2 배치의 <b>명시 직렬화</b> (B-03).
///
/// <para>
/// <b>왜 MemoryPack 을 쓰지 않는가.</b> unmanaged struct 배열의 원시 복사는 사실상 구조체
/// 메모리 덤프다. 그것이 도는 이유는 <b>우리 쪽 두 구현이 같은 C# 컴파일러를 쓰기 때문</b>이고,
/// 상대가 C++ 이나 다른 런타임이면 배치·엔디언·패딩을 <b>추측</b>해야 한다. 여기서는 필드마다
/// 리틀엔디언으로 명시해 쓴다 — 그러면 명세가 "C# DTO 의 선언 순서" 가 아니라
/// <c>docs/wire/layout_v2.md</c> 의 오프셋 표가 된다.
/// </para>
///
/// <para>
/// <b>바이트는 그대로다.</b> 우리가 설계한 v2 배치에는 암묵 패딩이 없고(<c>Reserved</c> 로
/// 꼬리를 명시했다) x86·ARM 둘 다 리틀엔디언이라, 이 직렬화기가 내는 바이트는
/// MemoryPack 이 내던 것과 <b>같다</b>. <c>WireWriter_MatchesMemoryPackBytes</c> 가 그것을
/// 못 박는다 — 형식이 바뀌지 않았음을 증명해야 이미 붙어 있는 상대가 안 깨진다.
/// </para>
///
/// <para>
/// <b>할당이 없다.</b> 호출부가 준 <see cref="Span{T}"/> 에 쓴다. 센더는 기동 시 잡은 버퍼
/// 하나를 재사용한다.
/// </para>
/// </summary>
public static class WireWriter
{
    /// <summary>배치 앞에 붙는 원소 수. int32 리틀엔디언 — MemoryPack 의 배열 접두와 같다.</summary>
    public const int CountPrefixSize = 4;

    /// <summary><see cref="WireCommandV2"/> 한 개의 바이트 수.</summary>
    public const int CommandSize = 72;

    /// <summary><see cref="WireEventV2"/> 한 개의 바이트 수.</summary>
    public const int EventSize = 80;

    /// <summary>명령 <paramref name="count"/> 개를 담는 데 필요한 바이트 수.</summary>
    public static int CommandBatchSize(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return CountPrefixSize + (count * CommandSize);
    }

    /// <summary>이벤트 <paramref name="count"/> 개를 담는 데 필요한 바이트 수.</summary>
    public static int EventBatchSize(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return CountPrefixSize + (count * EventSize);
    }

    // ---------------------------------------------------------------- 쓰기

    /// <summary>명령 배치를 쓴다.</summary>
    /// <param name="destination">쓸 곳. <see cref="CommandBatchSize"/> 이상이어야 한다.</param>
    /// <param name="batch">보낼 명령들.</param>
    /// <returns>쓴 바이트 수.</returns>
    public static int WriteCommands(Span<byte> destination, ReadOnlySpan<WireCommandV2> batch)
    {
        int size = CommandBatchSize(batch.Length);

        if (destination.Length < size)
        {
            throw new ArgumentException(
                $"버퍼가 모자란다: {destination.Length}B < {size}B", nameof(destination));
        }

        BinaryPrimitives.WriteInt32LittleEndian(destination, batch.Length);

        int offset = CountPrefixSize;

        foreach (ref readonly WireCommandV2 command in batch)
        {
            WriteCommand(destination[offset..], in command);
            offset += CommandSize;
        }

        return size;
    }

    /// <summary>이벤트 배치를 쓴다.</summary>
    /// <param name="destination">쓸 곳. <see cref="EventBatchSize"/> 이상이어야 한다.</param>
    /// <param name="batch">보낼 이벤트들.</param>
    /// <returns>쓴 바이트 수.</returns>
    public static int WriteEvents(Span<byte> destination, ReadOnlySpan<WireEventV2> batch)
    {
        int size = EventBatchSize(batch.Length);

        if (destination.Length < size)
        {
            throw new ArgumentException(
                $"버퍼가 모자란다: {destination.Length}B < {size}B", nameof(destination));
        }

        BinaryPrimitives.WriteInt32LittleEndian(destination, batch.Length);

        int offset = CountPrefixSize;

        foreach (ref readonly WireEventV2 gameEvent in batch)
        {
            WriteEvent(destination[offset..], in gameEvent);
            offset += EventSize;
        }

        return size;
    }

    /// <summary>명령 하나를 쓴다. <b>오프셋은 <c>docs/wire/layout_v2.md</c> 가 표로 갖는다.</b></summary>
    /// <param name="destination">쓸 곳. <see cref="CommandSize"/> 이상.</param>
    /// <param name="command">쓸 값.</param>
    public static void WriteCommand(Span<byte> destination, in WireCommandV2 command)
    {
        if (destination.Length < CommandSize)
        {
            throw new ArgumentException(
                $"버퍼가 모자란다: {destination.Length}B < {CommandSize}B", nameof(destination));
        }

        BinaryPrimitives.WriteInt64LittleEndian(destination, command.IssuedAt);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], command.Npc);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], command.TargetNpc);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], command.TargetPlayer);
        BinaryPrimitives.WriteInt32LittleEndian(destination[20..], command.Amount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], command.Correlation);
        BinaryPrimitives.WriteSingleLittleEndian(destination[28..], command.PosX);
        BinaryPrimitives.WriteSingleLittleEndian(destination[32..], command.PosY);
        BinaryPrimitives.WriteSingleLittleEndian(destination[36..], command.PosZ);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[40..], command.ExtA);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[44..], command.ExtB);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[48..], command.Reserved);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[52..], command.TargetPoi);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[54..], command.Item);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[56..], command.Animation);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[58..], command.Dialogue);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[60..], command.Archetype);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[62..], command.Zone);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[64..], command.Instance);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[66..], command.Faction);
        destination[68] = command.Kind;
        destination[69] = command.Priority;
        destination[70] = command.Visual;
        destination[71] = command.Flags;
    }

    /// <summary>이벤트 하나를 쓴다.</summary>
    /// <param name="destination">쓸 곳. <see cref="EventSize"/> 이상.</param>
    /// <param name="gameEvent">쓸 값.</param>
    public static void WriteEvent(Span<byte> destination, in WireEventV2 gameEvent)
    {
        if (destination.Length < EventSize)
        {
            throw new ArgumentException(
                $"버퍼가 모자란다: {destination.Length}B < {EventSize}B", nameof(destination));
        }

        BinaryPrimitives.WriteInt64LittleEndian(destination, gameEvent.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], gameEvent.OccurredAt);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], gameEvent.Npc);
        BinaryPrimitives.WriteInt32LittleEndian(destination[20..], gameEvent.OtherNpc);
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..], gameEvent.Player);
        BinaryPrimitives.WriteInt32LittleEndian(destination[28..], gameEvent.Amount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[32..], gameEvent.Correlation);
        BinaryPrimitives.WriteSingleLittleEndian(destination[36..], gameEvent.PosX);
        BinaryPrimitives.WriteSingleLittleEndian(destination[40..], gameEvent.PosY);
        BinaryPrimitives.WriteSingleLittleEndian(destination[44..], gameEvent.PosZ);
        BinaryPrimitives.WriteSingleLittleEndian(destination[48..], gameEvent.Heading);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[52..], gameEvent.ExtA);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[56..], gameEvent.ExtB);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[60..], gameEvent.Reserved);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[64..], gameEvent.Poi);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[66..], gameEvent.Zone);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[68..], gameEvent.Item);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[70..], gameEvent.Instance);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[72..], gameEvent.Faction);
        BinaryPrimitives.WriteInt16LittleEndian(destination[74..], gameEvent.Hp);
        BinaryPrimitives.WriteInt16LittleEndian(destination[76..], gameEvent.Stamina);
        destination[78] = gameEvent.Kind;
        destination[79] = gameEvent.Code;
    }

    // ---------------------------------------------------------------- 읽기

    /// <summary>
    /// 배치 접두의 원소 수를 읽는다.
    ///
    /// <b>음수는 "null 배열" 이다</b> — MemoryPack 이 그렇게 쓴다. 빈 배치(0)와 구분해
    /// 그대로 돌려준다. 호출부는 둘 다 "할 일 없음" 으로 다룬다.
    /// </summary>
    /// <param name="source">읽을 곳.</param>
    /// <param name="count">원소 수. 음수면 null 배열이다.</param>
    /// <returns>접두를 읽었으면 true.</returns>
    public static bool TryReadCount(ReadOnlySpan<byte> source, out int count)
    {
        if (source.Length < CountPrefixSize)
        {
            count = 0;
            return false;
        }

        count = BinaryPrimitives.ReadInt32LittleEndian(source);

        return true;
    }

    /// <summary>
    /// 명령 배치를 읽는다. <b>길이가 안 맞으면 false 다</b> — 잘라 읽지 않는다.
    /// 프레임 경계가 밀린 스트림을 조용히 해석하면 그때부터 전부 쓰레기다.
    /// </summary>
    /// <param name="source">읽을 곳.</param>
    /// <param name="destination">담을 곳. 모자라면 false.</param>
    /// <param name="count">읽은 원소 수.</param>
    /// <returns>온전히 읽었으면 true.</returns>
    public static bool TryReadCommands(
        ReadOnlySpan<byte> source, Span<WireCommandV2> destination, out int count)
    {
        count = 0;

        if (!TryReadCount(source, out int declared))
        {
            return false;
        }

        if (declared <= 0)
        {
            return declared == 0;   // 0 = 빈 배치. 음수 = null 배열이라 읽을 것이 없다
        }

        if (declared > destination.Length || source.Length < CommandBatchSize(declared))
        {
            return false;
        }

        for (int i = 0; i < declared; i++)
        {
            destination[i] = ReadCommand(source[(CountPrefixSize + (i * CommandSize))..]);
        }

        count = declared;

        return true;
    }

    /// <summary>이벤트 배치를 읽는다.</summary>
    /// <param name="source">읽을 곳.</param>
    /// <param name="destination">담을 곳.</param>
    /// <param name="count">읽은 원소 수.</param>
    /// <returns>온전히 읽었으면 true.</returns>
    public static bool TryReadEvents(
        ReadOnlySpan<byte> source, Span<WireEventV2> destination, out int count)
    {
        count = 0;

        if (!TryReadCount(source, out int declared))
        {
            return false;
        }

        if (declared <= 0)
        {
            return declared == 0;
        }

        if (declared > destination.Length || source.Length < EventBatchSize(declared))
        {
            return false;
        }

        for (int i = 0; i < declared; i++)
        {
            destination[i] = ReadEvent(source[(CountPrefixSize + (i * EventSize))..]);
        }

        count = declared;

        return true;
    }

    /// <summary>명령 하나를 읽는다.</summary>
    /// <param name="source">읽을 곳. <see cref="CommandSize"/> 이상.</param>
    public static WireCommandV2 ReadCommand(ReadOnlySpan<byte> source)
    {
        if (source.Length < CommandSize)
        {
            throw new ArgumentException(
                $"입력이 모자란다: {source.Length}B < {CommandSize}B", nameof(source));
        }

        return new WireCommandV2
        {
            IssuedAt = BinaryPrimitives.ReadInt64LittleEndian(source),
            Npc = BinaryPrimitives.ReadInt32LittleEndian(source[8..]),
            TargetNpc = BinaryPrimitives.ReadInt32LittleEndian(source[12..]),
            TargetPlayer = BinaryPrimitives.ReadInt32LittleEndian(source[16..]),
            Amount = BinaryPrimitives.ReadInt32LittleEndian(source[20..]),
            Correlation = BinaryPrimitives.ReadUInt32LittleEndian(source[24..]),
            PosX = BinaryPrimitives.ReadSingleLittleEndian(source[28..]),
            PosY = BinaryPrimitives.ReadSingleLittleEndian(source[32..]),
            PosZ = BinaryPrimitives.ReadSingleLittleEndian(source[36..]),
            ExtA = BinaryPrimitives.ReadUInt32LittleEndian(source[40..]),
            ExtB = BinaryPrimitives.ReadUInt32LittleEndian(source[44..]),
            Reserved = BinaryPrimitives.ReadUInt32LittleEndian(source[48..]),
            TargetPoi = BinaryPrimitives.ReadUInt16LittleEndian(source[52..]),
            Item = BinaryPrimitives.ReadUInt16LittleEndian(source[54..]),
            Animation = BinaryPrimitives.ReadUInt16LittleEndian(source[56..]),
            Dialogue = BinaryPrimitives.ReadUInt16LittleEndian(source[58..]),
            Archetype = BinaryPrimitives.ReadUInt16LittleEndian(source[60..]),
            Zone = BinaryPrimitives.ReadUInt16LittleEndian(source[62..]),
            Instance = BinaryPrimitives.ReadUInt16LittleEndian(source[64..]),
            Faction = BinaryPrimitives.ReadUInt16LittleEndian(source[66..]),
            Kind = source[68],
            Priority = source[69],
            Visual = source[70],
            Flags = source[71],
        };
    }

    /// <summary>이벤트 하나를 읽는다.</summary>
    /// <param name="source">읽을 곳. <see cref="EventSize"/> 이상.</param>
    public static WireEventV2 ReadEvent(ReadOnlySpan<byte> source)
    {
        if (source.Length < EventSize)
        {
            throw new ArgumentException(
                $"입력이 모자란다: {source.Length}B < {EventSize}B", nameof(source));
        }

        return new WireEventV2
        {
            Sequence = BinaryPrimitives.ReadInt64LittleEndian(source),
            OccurredAt = BinaryPrimitives.ReadInt64LittleEndian(source[8..]),
            Npc = BinaryPrimitives.ReadInt32LittleEndian(source[16..]),
            OtherNpc = BinaryPrimitives.ReadInt32LittleEndian(source[20..]),
            Player = BinaryPrimitives.ReadInt32LittleEndian(source[24..]),
            Amount = BinaryPrimitives.ReadInt32LittleEndian(source[28..]),
            Correlation = BinaryPrimitives.ReadUInt32LittleEndian(source[32..]),
            PosX = BinaryPrimitives.ReadSingleLittleEndian(source[36..]),
            PosY = BinaryPrimitives.ReadSingleLittleEndian(source[40..]),
            PosZ = BinaryPrimitives.ReadSingleLittleEndian(source[44..]),
            Heading = BinaryPrimitives.ReadSingleLittleEndian(source[48..]),
            ExtA = BinaryPrimitives.ReadUInt32LittleEndian(source[52..]),
            ExtB = BinaryPrimitives.ReadUInt32LittleEndian(source[56..]),
            Reserved = BinaryPrimitives.ReadUInt32LittleEndian(source[60..]),
            Poi = BinaryPrimitives.ReadUInt16LittleEndian(source[64..]),
            Zone = BinaryPrimitives.ReadUInt16LittleEndian(source[66..]),
            Item = BinaryPrimitives.ReadUInt16LittleEndian(source[68..]),
            Instance = BinaryPrimitives.ReadUInt16LittleEndian(source[70..]),
            Faction = BinaryPrimitives.ReadUInt16LittleEndian(source[72..]),
            Hp = BinaryPrimitives.ReadInt16LittleEndian(source[74..]),
            Stamina = BinaryPrimitives.ReadInt16LittleEndian(source[76..]),
            Kind = source[78],
            Code = source[79],
        };
    }

    /// <summary>
    /// 이 기계가 리틀엔디언인가.
    ///
    /// <b>이 직렬화기는 엔디언과 무관하게 돈다</b> — <see cref="BinaryPrimitives"/> 가 변환한다.
    /// 이 속성은 골든 벡터 테스트가 "원시 복사와 바이트가 같다" 를 주장할 수 있는 조건을
    /// 밝히는 데만 쓴다. 빅엔디언 기계에서는 그 주장이 성립하지 않는다.
    /// </summary>
    public static bool RawCopyMatchesWireOrder
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => BitConverter.IsLittleEndian;
    }
}
