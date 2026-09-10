using System.Runtime.InteropServices;
using MemoryPack;
using Npc.Wire;
using Npc.Wire.V2;

namespace Npc.Tests.Wire;

/// <summary>
/// B-03 — 오프셋 표와 골든 바이트 벡터가 코드와 같은가.
///
/// <para>
/// <b>이 회차는 문서를 다시 쓴다.</b> 어긋나면 새로 뽑은 것을 파일에 써 놓고 실패한다 —
/// 사람이 diff 를 보고 "의도한 변경인가" 를 판단한 뒤 커밋한다. 실패만 하고 안 쓰면
/// 손으로 표를 고치게 되고, 그러면 표가 또 코드와 어긋난다.
/// </para>
///
/// <para>
/// <b>골든 벡터가 바뀌는 것은 프로토콜이 바뀌었다는 뜻이다.</b> 다른 언어 구현이 그 벡터로
/// 자기 코덱을 시험하고 있으므로, 벡터 diff 는 <b>연동 팀에 알려야 하는 변경</b>이다.
/// </para>
/// </summary>
[Trait("Category", "Wire")]
public sealed class LayoutDocTests
{
    /// <summary>커밋된 <c>docs/wire/layout_v2.md</c> 가 코드와 같아야 한다.</summary>
    [Fact]
    public void LayoutDoc_MatchesTheCode()
    {
        string generated = WireLayoutDoc.Render();
        string path = TestPaths.At(WireLayoutDoc.Path.Split('/'));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string? existing = File.Exists(path)
            ? File.ReadAllText(path).ReplaceLineEndings("\n")
            : null;

        if (existing == generated)
        {
            return;
        }

        File.WriteAllText(path, generated);

        Assert.Fail(
            existing is null
                ? $"{WireLayoutDoc.Path} 이 없어 새로 썼다. 커밋한다."
                : $"{WireLayoutDoc.Path} 이 코드와 다르다. 새로 뽑은 것으로 덮었으니 "
                  + "diff 를 보고 의도한 변경인지 확인한 뒤 커밋한다. "
                  + "벡터·표가 바뀌었다면 연동 팀에 알려야 하는 변경이다.");
    }

    /// <summary>커밋된 골든 벡터가 코드와 같아야 한다.</summary>
    [Fact]
    public void GoldenVectors_MatchTheCode()
    {
        string directory = TestPaths.At(WireLayoutDoc.VectorDirectory.Split('/'));

        Directory.CreateDirectory(directory);

        var stale = new List<string>();

        foreach (WireLayoutDoc.Vector vector in WireLayoutDoc.Vectors())
        {
            string hexPath = Path.Combine(directory, vector.Name + ".hex");
            string jsonPath = Path.Combine(directory, vector.Name + ".json");

            string hex = WireLayoutDoc.ToHex(vector.Bytes);
            string json = Meaning(vector);

            if (!Same(hexPath, hex))
            {
                File.WriteAllText(hexPath, hex);
                stale.Add(vector.Name + ".hex");
            }

            if (!Same(jsonPath, json))
            {
                File.WriteAllText(jsonPath, json);
                stale.Add(vector.Name + ".json");
            }
        }

        Assert.True(
            stale.Count == 0,
            $"골든 벡터가 코드와 다르다: {string.Join(", ", stale)}. 새로 썼으니 diff 를 확인한 뒤 "
            + "커밋한다. 이 파일들은 다른 언어 구현이 자기 코덱을 시험하는 근거다.");
    }

    /// <summary>
    /// <b>골든 벡터를 다시 읽으면 같은 값이 나온다.</b> 벡터가 "우리가 쓴 것" 일 뿐 아니라
    /// "우리가 읽을 수 있는 것" 이어야 한다 — 쓰기만 맞고 읽기가 틀린 코덱은 상대만 깨뜨린다.
    /// </summary>
    [Fact]
    public void GoldenVectors_DecodeBackToTheSameValues()
    {
        string directory = TestPaths.At(WireLayoutDoc.VectorDirectory.Split('/'));

        byte[] commandBytes = WireLayoutDoc.FromHex(
            File.ReadAllText(Path.Combine(directory, "command_batch_v2.hex")));

        var commands = new WireCommandV2[8];

        Assert.True(WireWriter.TryReadCommands(commandBytes, commands, out int commandCount));
        Assert.Equal(2, commandCount);
        Assert.Equal(WireWriterTests.Command(1), commands[0]);
        Assert.Equal(WireWriterTests.Command(2), commands[1]);

        byte[] eventBytes = WireLayoutDoc.FromHex(
            File.ReadAllText(Path.Combine(directory, "event_batch_v2.hex")));

        var events = new WireEventV2[8];

        Assert.True(WireWriter.TryReadEvents(eventBytes, events, out int eventCount));
        Assert.Equal(3, eventCount);
        Assert.Equal(WireWriterTests.Event(3), events[2]);

        byte[] helloBytes = WireLayoutDoc.FromHex(
            File.ReadAllText(Path.Combine(directory, "hello_v2.hex")));

        Assert.Equal(
            WireLayoutDoc.SampleHello(),
            MemoryPackSerializer.Deserialize<WireHelloV2>(helloBytes));
    }

    /// <summary>
    /// <b>배치 타입에는 패딩이 없다.</b> 있으면 초기화되지 않은 바이트가 소켓에 나가고
    /// 골든 벡터가 회차마다 달라진다 — 그러면 다른 언어 구현이 그 바이트를 해석하려 든다.
    /// </summary>
    [Theory]
    [InlineData(typeof(WireCommand))]
    [InlineData(typeof(WireEvent))]
    [InlineData(typeof(WireCommandV2))]
    [InlineData(typeof(WireEventV2))]
    public void BatchTypes_HaveNoPadding(Type type)
    {
        Assert.DoesNotContain(
            WireLayoutDoc.RowsOf(type),
            r => string.Equals(r.Name, WireLayoutDoc.PaddingName, StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>핸드셰이크의 패딩은 못 박는다</b> (B-03).
    ///
    /// <para>
    /// v1·v2 핸드셰이크에는 실제로 구멍이 있다 — <c>WireHash</c> 가 8바이트 정렬이라
    /// 앞의 <c>int</c> 뒤에 4바이트가 뜬다. MemoryPack 은 unmanaged struct 를 원시 복사하므로
    /// <b>그 바이트도 그대로 소켓에 나간다.</b>
    /// </para>
    ///
    /// <para>
    /// <b>고치지 않는다.</b> 필드를 옮기면 이미 붙어 있는 게임서버가 통째로 깨진다.
    /// 대신 여기서 위치를 고정해, 누가 필드를 옮겨 구멍이 달라지면 알아채게 한다 —
    /// 다른 언어 구현은 이 구멍을 건너뛰도록 코덱을 짜 두었다.
    /// </para>
    /// </summary>
    [Fact]
    public void HandshakePadding_IsPinned()
    {
        // v1 Hello 는 운 좋게 구멍이 없다 — int 넷 뒤가 8의 배수라 해시가 그대로 붙는다.
        Assert.Empty(PaddingOf(typeof(WireHello)));

        // Ack 는 int 셋(12B) 뒤에 해시가 와서 4바이트, 꼬리 정렬로 6바이트가 뜬다.
        Assert.Equal(new[] { (12, 4), (82, 6) }, PaddingOf(typeof(WireHelloAck)));

        // v2 는 필드가 많아 구멍도 넷이다. 그래도 옮기지 않는다 — 이미 붙어 있는 상대가 깨진다.
        Assert.Equal(new[] { (12, 4), (36, 4), (52, 4), (68, 4) }, PaddingOf(typeof(WireHelloV2)));
        Assert.Equal(new[] { (155, 5) }, PaddingOf(typeof(WireHelloAckV2)));

        Assert.Empty(PaddingOf(typeof(WireHeartbeat)));
        Assert.Empty(PaddingOf(typeof(WireBye)));
    }

    /// <summary>패딩 줄의 (오프셋, 크기) 목록.</summary>
    private static (int Offset, int Size)[] PaddingOf(Type type) =>
        [.. WireLayoutDoc.RowsOf(type)
            .Where(r => string.Equals(r.Name, WireLayoutDoc.PaddingName, StringComparison.Ordinal))
            .Select(r => (r.Offset, r.Size))];

    /// <summary>
    /// <b>오프셋이 겹치거나 구멍이 나지 않는다.</b> 합만 맞으면 배치가 뒤죽박죽이어도 통과한다.
    /// </summary>
    [Fact]
    public void EveryDocumentedType_IsContiguous()
    {
        foreach ((Type type, _) in WireLayoutDoc.Documented)
        {
            int expected = 0;

            foreach (WireLayoutDoc.Row row in WireLayoutDoc.RowsOf(type))
            {
                Assert.True(
                    row.Offset == expected,
                    $"{type.Name}.{row.Name}: 오프셋 {row.Offset} 인데 {expected} 여야 한다");

                expected += row.Size;
            }

            Assert.Equal(Marshal.SizeOf(type), expected);
        }
    }

    private static bool Same(string path, string content) =>
        File.Exists(path) && File.ReadAllText(path).ReplaceLineEndings("\n") == content;

    /// <summary>벡터의 뜻을 담는 json. 손으로 읽는 것이지 파서용이 아니다.</summary>
    private static string Meaning(WireLayoutDoc.Vector vector) =>
        $$"""
        {
          "name": "{{vector.Name}}",
          "bytes": {{vector.Bytes.Length}},
          "meaning": "{{vector.Meaning}}",
          "generated_by": "tests/Npc.Tests/Wire/LayoutDocTests.cs",
          "layout": "docs/wire/layout_v2.md"
        }

        """.ReplaceLineEndings("\n");
}
