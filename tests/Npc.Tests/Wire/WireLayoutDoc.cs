using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MemoryPack;
using Npc.Wire;
using Npc.Wire.V2;

namespace Npc.Tests.Wire;

/// <summary>
/// 와이어 배치 표를 <b>코드에서 뽑아</b> markdown 으로 만든다 (B-03).
///
/// <para>
/// <b>명세가 "C# DTO 의 선언 순서" 이면 다른 언어 구현은 추측을 한다.</b> 여기서 뽑는 것은
/// 추측할 수 없는 것들이다 — 필드별 오프셋 · 크기 · 부호 · 엔디언. C++ 이나 파이썬 구현은
/// 이 표 하나로 코덱을 쓸 수 있어야 한다.
/// </para>
///
/// <para>
/// <b>생성물이다.</b> 손으로 고치지 않는다 — <c>LayoutDocTests</c> 가 다시 만들고 대조한다.
/// </para>
/// </summary>
internal static class WireLayoutDoc
{
    /// <summary>발행 경로. 저장소 루트 기준이다.</summary>
    public const string Path = "docs/wire/layout_v2.md";

    /// <summary>
    /// 표에 싣는 타입. <b>다른 언어 구현이 바이트를 해석해야 하는 것 전부</b>다.
    ///
    /// 순서는 읽는 순서 — 프레임 → 핸드셰이크 → 배치 → 제어다.
    /// </summary>
    public static readonly (Type Type, string Note)[] Documented =
    [
        (typeof(WireHello), "v1 핸드셰이크. 게임서버 → NPC 서버"),
        (typeof(WireHelloAck), "v1 핸드셰이크 응답"),
        (typeof(WireHelloV2), "v2 핸드셰이크. 버전 범위·계약 버전·기능 비트·인증 (B-01 · A-06)"),
        (typeof(WireHelloAckV2), "v2 핸드셰이크 응답"),
        (typeof(WireCommand), "v1 명령. **동결이다** — 새 필드는 v2 에만"),
        (typeof(WireEvent), "v1 이벤트. **동결**"),
        (typeof(WireCommandV2), "v2 명령. 확장 슬롯 포함 (B-02)"),
        (typeof(WireEventV2), "v2 이벤트. 확장 슬롯 포함 (B-02)"),
        (typeof(WireHeartbeat), "하트비트. 양방향"),
        (typeof(WireBye), "종료 통보. 양방향"),
    ];

    /// <summary>
    /// 값 타입을 표에 적을 이름으로. <b>부호와 폭을 이름에 담는다</b> —
    /// C++ 의 <c>int</c> 가 몇 바이트인지는 플랫폼이 정하므로 <c>int32</c> 라고 쓴다.
    /// </summary>
    public static string NameOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type == typeof(long) ? "int64"
            : type == typeof(ulong) ? "uint64"
            : type == typeof(int) ? "int32"
            : type == typeof(uint) ? "uint32"
            : type == typeof(short) ? "int16"
            : type == typeof(ushort) ? "uint16"
            : type == typeof(sbyte) ? "int8"
            : type == typeof(byte) ? "uint8"
            : type == typeof(float) ? "float32 (IEEE 754)"
            : type.Name;
    }

    /// <summary>
    /// 표에 실을 한 줄. <paramref name="Endian"/> 은 <b>필드마다 다르다</b> —
    /// <see cref="WireNonce"/> 만 빅엔디언이다(HMAC 입력이라 네트워크 순서로 쓴다).
    /// </summary>
    /// <param name="Name">필드 이름. 중첩이면 <c>부모.자식</c>.</param>
    /// <param name="Offset">구조체 시작으로부터의 바이트 오프셋.</param>
    /// <param name="Size">바이트 수.</param>
    /// <param name="Type">표기 타입.</param>
    /// <param name="Endian">바이트 순서.</param>
    public readonly record struct Row(string Name, int Offset, int Size, string Type, string Endian);

    /// <summary>한 타입의 배치를 뽑는다. 중첩 해시·논스는 펼친다.</summary>
    public static List<Row> RowsOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var rows = new List<Row>();

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            int offset = Marshal.OffsetOf(type, field.Name).ToInt32();

            // WireHash·WireNonce 는 record struct 라 필드가 아니라 속성이다.
            // 다른 언어 구현에는 "ulong 4개" 로 보이면 되므로 여기서 펼쳐 적는다.
            if (field.FieldType == typeof(WireHash))
            {
                foreach ((string part, int index) in new[] { ("A", 0), ("B", 1), ("C", 2), ("D", 3) })
                {
                    rows.Add(new Row($"{field.Name}.{part}", offset + (index * 8), 8, "uint64", "LE"));
                }

                continue;
            }

            if (field.FieldType == typeof(WireNonce))
            {
                foreach ((string part, int index) in new[] { ("A", 0), ("B", 1) })
                {
                    rows.Add(new Row($"{field.Name}.{part}", offset + (index * 8), 8, "uint64", "LE"));
                }

                continue;
            }

            rows.Add(new Row(
                field.Name, offset, Marshal.SizeOf(field.FieldType), NameOf(field.FieldType), "LE"));
        }

        rows.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        return Fill(rows, Marshal.SizeOf(type));
    }

    /// <summary>
    /// 필드 사이의 구멍을 <b>패딩 줄로 채운다</b> (B-03).
    ///
    /// <para>
    /// <b>구멍을 안 적으면 다른 언어 구현이 필드를 붙여 읽는다.</b> v1 핸드셰이크에는
    /// 실제로 구멍이 있다 — <c>WireHash</c> 가 8바이트 정렬이라 앞의 <c>int</c> 뒤에
    /// 4바이트가 뜬다. MemoryPack 은 unmanaged struct 를 원시 복사하므로 그 바이트도
    /// 그대로 소켓에 나간다.
    /// </para>
    ///
    /// <para>
    /// <b>v1 을 고치지 않는다.</b> 필드를 옮기면 이미 붙어 있는 v1 게임서버가 통째로 깨진다.
    /// 대신 표에 적어 둔다 — 읽는 쪽은 그 바이트를 건너뛰고, 쓰는 쪽은 0 으로 채운다.
    /// </para>
    /// </summary>
    private static List<Row> Fill(List<Row> rows, int size)
    {
        var filled = new List<Row>(rows.Count);
        int at = 0;

        foreach (Row row in rows)
        {
            if (row.Offset > at)
            {
                filled.Add(new Row(PaddingName, at, row.Offset - at, "uint8[]", "0 으로 채운다"));
            }

            filled.Add(row);
            at = row.Offset + row.Size;
        }

        if (size > at)
        {
            filled.Add(new Row(PaddingName, at, size - at, "uint8[]", "0 으로 채운다"));
        }

        return filled;
    }

    /// <summary>패딩 줄의 이름. 테스트가 이 이름으로 "필드가 아니다" 를 가른다.</summary>
    public const string PaddingName = "(패딩)";

    /// <summary>문서 전체를 만든다.</summary>
    public static string Render()
    {
        var text = new StringBuilder();

        text.Append("""
            # 와이어 배치 — 오프셋 표 (v1 · v2)

            > **생성물이다. 손으로 고치지 않는다.**
            > `dotnet test --filter FullyQualifiedName~LayoutDocTests` 가 코드에서 다시 뽑아
            > 이 파일과 대조한다. 어긋나면 테스트가 깨지고, 그때 새로 쓴 것이 여기 남는다.

            이 문서는 **다른 언어로 게임서버를 구현할 때** 읽는 것이다. C# DTO 의 선언 순서를
            추측하지 않아도 되도록 필드별 오프셋·크기·부호·엔디언을 그대로 적는다.

            참조 코덱은 `docs/wire/reference/` 에, 골든 바이트 벡터는 `docs/wire/vectors_v2/` 에 있다.

            ## 공통 규칙

            | 항목 | 값 |
            |---|---|
            | 바이트 순서 | **리틀엔디언**. 예외는 `WireNonce` 하나로, HMAC 입력이라 빅엔디언으로 푼다 |
            | 패딩 | **배치(명령·이벤트)에는 없다.** v1 핸드셰이크에는 있다 — 아래 표에 `(패딩)` 줄로 적었다. 쓰는 쪽은 0 으로 채우고 읽는 쪽은 건너뛴다 |
            | 부동소수 | IEEE 754 `binary32`. 좌표는 게임서버 좌표계 그대로다 |
            | 문자열 | **없다** (N3). 해시도 `uint64` 4개다 |
            | 시각 | `Tick`(int64) 뿐이다 (N4) |

            ## 프레임 헤더 (8바이트)

            | 필드 | 오프셋 | 크기 | 타입 | 뜻 |
            |---|---:|---:|---|---|
            | `PayloadLength` | 0 | 4 | uint32 | 헤더를 뺀 페이로드 바이트 수. 상한 1 MiB |
            | `Kind` | 4 | 1 | uint8 | `LinkMessageKind` |
            | `Ver` | 5 | 1 | uint8 | 와이어 버전. 받아 줄 범위 [1, 2] |
            | `Reserved` | 6 | 2 | uint16 | 0 |

            **`Ver` 가 배치를 정한다.** 협상 결과가 아니라 프레임이 말하는 버전을 믿는다 —
            협상 직후 경계에서 두 버전이 섞여 도착할 수 있다.

            ## 배치 페이로드

            | 항목 | 값 |
            |---|---|
            | 접두 | `int32` 원소 수 (리틀엔디언). **음수는 null 배열**이고 빈 배치(0)와 구분한다 |
            | 원소 | 접두 뒤에 크기 고정 구조체가 연속으로. 원소 사이에 태그·구분자가 없다 |
            | 명령 | v1 56 B · **v2 72 B** |
            | 이벤트 | v1 64 B · **v2 80 B** |


            """);

        foreach ((Type type, string note) in Documented)
        {
            List<Row> rows = RowsOf(type);
            int size = Marshal.SizeOf(type);
            int sum = rows.Sum(r => r.Size);

            text.Append(CultureInfo.InvariantCulture, $"## `{type.Name}` — {size} B\n\n");
            text.Append(CultureInfo.InvariantCulture, $"{note}\n\n");
            text.Append("| 필드 | 오프셋 | 크기 | 타입 | 바이트 순서 |\n");
            text.Append("|---|---:|---:|---|---|\n");

            foreach (Row row in rows)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $"| `{row.Name}` | {row.Offset} | {row.Size} | {row.Type} | {row.Endian} |\n");
            }

            // 합이 안 맞으면 표가 잘못된 것이다 — 그 사실을 문서에 그대로 남긴다.
            string relation = sum == size ? "=" : "≠";
            string verdict = sum == size ? string.Empty : " — **표가 잘못됐다**";

            text.Append(CultureInfo.InvariantCulture,
                $"\n줄 크기 합 **{sum} B** {relation} 구조체 크기 **{size} B**{verdict}.\n\n");
        }

        return text.ToString().ReplaceLineEndings("\n");
    }

    // ---------------------------------------------------------------- 골든 벡터

    /// <summary>골든 벡터 폴더. 저장소 루트 기준이다.</summary>
    public const string VectorDirectory = "docs/wire/vectors_v2";

    /// <summary>벡터 하나 — 이름 · 바이트 · 사람이 읽는 뜻.</summary>
    /// <param name="Name">파일 이름(확장자 없이).</param>
    /// <param name="Bytes">바이트.</param>
    /// <param name="Meaning">무엇을 담았는가. json 으로 같이 나간다.</param>
    public readonly record struct Vector(string Name, byte[] Bytes, string Meaning);

    /// <summary>
    /// 골든 벡터를 만든다.
    ///
    /// <b>값은 전부 다르다.</b> 같은 값을 넣으면 오프셋이 뒤바뀐 구현도 통과한다 —
    /// 그러면 벡터가 아무것도 검사하지 않는다.
    /// </summary>
    public static List<Vector> Vectors()
    {
        WireCommandV2[] commands = [WireWriterTests.Command(1), WireWriterTests.Command(2)];
        WireEventV2[] events = [WireWriterTests.Event(1), WireWriterTests.Event(2), WireWriterTests.Event(3)];

        byte[] commandBatch = new byte[WireWriter.CommandBatchSize(commands.Length)];
        WireWriter.WriteCommands(commandBatch, commands);

        byte[] eventBatch = new byte[WireWriter.EventBatchSize(events.Length)];
        WireWriter.WriteEvents(eventBatch, events);

        return
        [
            new Vector(
                "command_batch_v2",
                commandBatch,
                $"WireCommandV2 {commands.Length}개. 접두 int32 + 원소 {WireWriter.CommandSize}B."),
            new Vector(
                "event_batch_v2",
                eventBatch,
                $"WireEventV2 {events.Length}개. 접두 int32 + 원소 {WireWriter.EventSize}B."),
            new Vector(
                "command_batch_v2_empty",
                [.. new byte[WireWriter.CommandBatchSize(0)]],
                "빈 배치. 접두 4바이트만. 빈 배치도 프레임 하나다 (N8)."),
            new Vector(
                "hello_v2",
                MemoryPackSerializer.Serialize(SampleHello()),
                "WireHelloV2 하나. 배치가 아니라 단일 구조체라 접두가 없다."),
            new Vector(
                "hello_ack_v2",
                MemoryPackSerializer.Serialize(SampleAck()),
                "WireHelloAckV2 하나."),
            new Vector(
                "heartbeat",
                MemoryPackSerializer.Serialize(new WireHeartbeat { Tick = 1_234_567L, Sequence = 89_012L }),
                "WireHeartbeat 하나."),
            new Vector(
                "bye",
                MemoryPackSerializer.Serialize(new WireBye { Code = (byte)LinkByeCode.Shutdown }),
                "WireBye 하나. Code = Shutdown."),
        ];
    }

    /// <summary>핸드셰이크 표본. 해시 넷을 서로 다르게 준다.</summary>
    public static WireHelloV2 SampleHello() => new()
    {
        ProtocolVersion = 2,
        MinProtocolVersion = 1,
        ContractMajor = 1,
        ContractMinor = 2,
        Features = 0x3F,
        TickRate = 10,
        TimeScale = 600,
        NpcCount = 5_000,
        StartTick = 1_234_567L,
        StartGameMinuteOfDay = 361,
        MasterDataStructural = new WireHash(0x0102030405060708UL, 2, 3, 4),
        MasterDataContent = new WireHash(5, 6, 7, 8),
        Roster = new WireHash(9, 10, 11, 12),
        Auth = new WireHash(13, 14, 15, 16),
        Nonce = new WireNonce(17, 18),
    };

    /// <summary>핸드셰이크 응답 표본.</summary>
    public static WireHelloAckV2 SampleAck() => new()
    {
        ProtocolVersion = 2,
        ContractMajor = 1,
        ContractMinor = 2,
        Features = 0x3F,
        TimeScale = 600,
        NpcCount = 5_000,
        MasterDataStructural = new WireHash(0x0102030405060708UL, 2, 3, 4),
        MasterDataContent = new WireHash(5, 6, 7, 8),
        Roster = new WireHash(9, 10, 11, 12),
        Auth = new WireHash(13, 14, 15, 16),
        Accepted = 1,
        RejectCode = 0,
        ContentHashWarning = 1,
    };

    /// <summary>벡터 파일에 쓰는 hex. 소문자 · 16바이트마다 줄바꿈.</summary>
    public static string ToHex(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder();

        for (int i = 0; i < bytes.Length; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"{bytes[i]:x2}");

            if ((i + 1) % 16 == 0)
            {
                text.Append('\n');
            }
            else if (i + 1 < bytes.Length)
            {
                text.Append(' ');
            }
        }

        if (bytes.Length % 16 != 0)
        {
            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>hex 파일을 바이트로. 공백·줄바꿈은 무시한다.</summary>
    public static byte[] FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);

        var bytes = new List<byte>(hex.Length / 3);
        int high = -1;

        foreach (char c in hex)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            int value = Convert.ToInt32(c.ToString(), 16);

            if (high < 0)
            {
                high = value;
            }
            else
            {
                bytes.Add((byte)((high << 4) | value));
                high = -1;
            }
        }

        if (high >= 0)
        {
            throw new FormatException("hex 자릿수가 홀수다.");
        }

        return [.. bytes];
    }
}
