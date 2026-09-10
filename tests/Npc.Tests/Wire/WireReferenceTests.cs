using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Npc.Wire;
using Npc.Wire.V2;

namespace Npc.Tests.Wire;

/// <summary>
/// B-03 — 참조 코덱(C++ · 파이썬)이 오프셋 표와 같은가.
///
/// <para>
/// <b>참조 코덱은 연동 팀이 베끼는 것이다.</b> 그것이 코드와 어긋나면 <b>없는 것보다 나쁘다</b> —
/// 상대는 그것을 근거로 자기 구현을 만들고, 어긋난 사실은 통합 시험에서야 드러난다.
/// </para>
///
/// <para>
/// <b>여기서는 컴파일하지 않는다.</b> 이 저장소 환경에는 C++ 컴파일러가 없다(미실시).
/// 대신 <b>숫자를 대조한다</b> — 크기 상수와 디코더의 오프셋 리터럴이 표와 같아야 한다.
/// 문법 오류는 못 잡지만, 프로토콜이 어긋나는 것은 여기서 잡힌다.
/// <c>tools/check_wire_reference.ps1</c> 이 도구가 있는 기계에서 컴파일·실행까지 한다.
/// </para>
/// </summary>
[Trait("Category", "Wire")]
public sealed partial class WireReferenceTests
{
    private static string Header => File.ReadAllText(
        TestPaths.At("docs", "wire", "reference", "npc_wire.h"));

    private static string Python => File.ReadAllText(
        TestPaths.At("docs", "wire", "reference", "npc_wire.py"));

    /// <summary>참조 코덱 두 개가 저장소에 있어야 한다.</summary>
    [Fact]
    public void ReferenceCodecs_Exist()
    {
        Assert.True(File.Exists(TestPaths.At("docs", "wire", "reference", "npc_wire.h")));
        Assert.True(File.Exists(TestPaths.At("docs", "wire", "reference", "npc_wire.py")));
        Assert.True(File.Exists(TestPaths.At("tools", "check_wire_reference.ps1")));
    }

    /// <summary>
    /// <b>C++ 헤더의 크기 상수가 실제 크기와 같다.</b> 이 숫자가 틀리면 배치 오프셋 계산이
    /// 전부 어긋나고, 증상은 "가끔 이상한 명령이 온다" 로만 나타난다.
    /// </summary>
    [Theory]
    [InlineData("kCommandV2Size", typeof(WireCommandV2))]
    [InlineData("kEventV2Size", typeof(WireEventV2))]
    [InlineData("kCommandV1Size", typeof(WireCommand))]
    [InlineData("kEventV1Size", typeof(WireEvent))]
    [InlineData("kHelloSize", typeof(WireHello))]
    [InlineData("kHelloAckSize", typeof(WireHelloAck))]
    [InlineData("kHelloV2Size", typeof(WireHelloV2))]
    [InlineData("kHelloAckV2Size", typeof(WireHelloAckV2))]
    [InlineData("kHeartbeatSize", typeof(WireHeartbeat))]
    [InlineData("kByeSize", typeof(WireBye))]
    public void CppHeader_SizesMatchTheCode(string constant, Type type)
    {
        Match match = Regex.Match(
            Header,
            @"constexpr std::size_t " + Regex.Escape(constant) + @"\s*=\s*(\d+)\s*;",
            RegexOptions.None,
            TimeSpan.FromSeconds(2));

        Assert.True(match.Success, $"npc_wire.h 에 {constant} 가 없다");

        Assert.Equal(
            Marshal.SizeOf(type),
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>파이썬 코덱의 크기 상수도 같아야 한다.</summary>
    [Theory]
    [InlineData("COMMAND_V2_SIZE", typeof(WireCommandV2))]
    [InlineData("EVENT_V2_SIZE", typeof(WireEventV2))]
    [InlineData("COMMAND_V1_SIZE", typeof(WireCommand))]
    [InlineData("EVENT_V1_SIZE", typeof(WireEvent))]
    [InlineData("HELLO_SIZE", typeof(WireHello))]
    [InlineData("HELLO_ACK_SIZE", typeof(WireHelloAck))]
    [InlineData("HELLO_V2_SIZE", typeof(WireHelloV2))]
    [InlineData("HELLO_ACK_V2_SIZE", typeof(WireHelloAckV2))]
    [InlineData("HEARTBEAT_SIZE", typeof(WireHeartbeat))]
    [InlineData("BYE_SIZE", typeof(WireBye))]
    public void PythonCodec_SizesMatchTheCode(string constant, Type type)
    {
        Match match = Regex.Match(
            Python,
            @"^" + Regex.Escape(constant) + @"\s*=\s*(\d+)\s*$",
            RegexOptions.Multiline,
            TimeSpan.FromSeconds(2));

        Assert.True(match.Success, $"npc_wire.py 에 {constant} 가 없다");

        Assert.Equal(
            Marshal.SizeOf(type),
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// <b>C++ 디코더의 오프셋 리터럴이 표와 같다</b> (B-03).
    ///
    /// 크기만 맞고 오프셋이 어긋나면 값이 섞여 들어온다 — 그것이 가장 찾기 어려운 종류의
    /// 프로토콜 버그다. 여기서는 <c>decode</c> 본문의 <c>p + N</c> 를 순서대로 뽑아
    /// 표의 오프셋과 대조한다.
    /// </summary>
    [Theory]
    [InlineData("CommandV2", typeof(WireCommandV2))]
    [InlineData("EventV2", typeof(WireEventV2))]
    public void CppHeader_DecoderOffsetsMatchTheTable(string cppType, Type type)
    {
        string body = DecoderBody(cppType);

        int[] expected = [.. WireLayoutDoc.RowsOf(type).Select(r => r.Offset)];
        int[] actual = [.. OffsetLiterals().Matches(body).Select(m =>
            m.Groups[1].Success
                ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
                : 0)];

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// <b>파이썬의 필드 이름 목록이 표와 같다.</b> 이름이 밀리면 값이 통째로 밀린다 —
    /// 크기·오프셋은 맞는데 뜻만 어긋나는, 가장 조용한 종류의 오류다.
    /// </summary>
    [Theory]
    [InlineData("COMMAND_V2_FIELDS", typeof(WireCommandV2))]
    [InlineData("EVENT_V2_FIELDS", typeof(WireEventV2))]
    [InlineData("COMMAND_V1_FIELDS", typeof(WireCommand))]
    [InlineData("EVENT_V1_FIELDS", typeof(WireEvent))]
    public void PythonCodec_FieldNamesMatchTheTable(string tuple, Type type)
    {
        string[] expected = [.. WireLayoutDoc.RowsOf(type).Select(r => r.Name)];

        Assert.Equal(expected, PythonTuple(tuple));
    }

    /// <summary>
    /// <b>핸드셰이크 패딩이 참조 코덱에도 적혀 있다.</b> 안 적으면 다른 언어 구현이
    /// 필드를 붙여 읽고, 해시부터 통째로 어긋난다.
    /// </summary>
    [Fact]
    public void ReferenceCodecs_DocumentTheHandshakePadding()
    {
        // 파이썬은 포맷 문자열의 'x' 가 패딩이다. HelloV2 는 구멍이 넷이다.
        Assert.Contains("4xQ3i4xq2H4xQI4x", Python, StringComparison.Ordinal);

        // C++ 은 주석으로 위치를 적고, encode 가 memset 으로 0 을 채운다.
        foreach (string offset in new[] { "12", "36", "52", "68" })
        {
            Assert.Contains($"// 오프셋 {offset}: 패딩", Header, StringComparison.Ordinal);
        }

        Assert.Contains("std::memset(p, 0, kHelloV2Size);", Header, StringComparison.Ordinal);
        Assert.Contains("std::memset(p, 0, kHelloAckV2Size);", Header, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>파이썬 코덱은 골든 벡터를 스스로 확인한다.</b> 그 자체 시험이 있는지를 여기서 본다 —
    /// 없으면 참조 코덱이 "돈다" 는 근거가 아무 데도 없다.
    /// </summary>
    [Fact]
    public void PythonCodec_HasASelfTestAgainstTheVectors()
    {
        Assert.Contains("def self_test()", Python, StringComparison.Ordinal);
        Assert.Contains("vectors_v2", Python, StringComparison.Ordinal);
        Assert.Contains("command_batch_v2", Python, StringComparison.Ordinal);
        Assert.Contains("sys.exit(self_test())", Python, StringComparison.Ordinal);
    }

    /// <summary><c>decode(const std::uint8_t* p, T&amp; x)</c> 의 본문.</summary>
    private static string DecoderBody(string cppType)
    {
        int start = Header.IndexOf(
            $"inline void decode(const std::uint8_t* p, {cppType}&", StringComparison.Ordinal);

        Assert.True(start >= 0, $"npc_wire.h 에 {cppType} 디코더가 없다");

        int open = Header.IndexOf('{', start);
        int close = Header.IndexOf("\n}", open, StringComparison.Ordinal);

        Assert.True(close > open, $"{cppType} 디코더의 끝을 못 찾았다");

        return Header[open..close];
    }

    /// <summary>파이썬 튜플 리터럴의 문자열들.</summary>
    private static string[] PythonTuple(string name)
    {
        int start = Python.IndexOf($"{name} = (", StringComparison.Ordinal);

        Assert.True(start >= 0, $"npc_wire.py 에 {name} 이 없다");

        int open = Python.IndexOf('(', start);
        int close = Python.IndexOf("\n)", open, StringComparison.Ordinal);

        Assert.True(close > open, $"{name} 의 끝을 못 찾았다");

        return [.. QuotedStrings().Matches(Python[open..close]).Select(m => m.Groups[1].Value)];
    }

    /// <summary><c>read_xxx(p)</c> 또는 <c>read_xxx(p + N)</c>.</summary>
    [GeneratedRegex(@"read_\w+\(p(?:\s*\+\s*(\d+))?\)", RegexOptions.None, 2000)]
    private static partial Regex OffsetLiterals();

    /// <summary>큰따옴표 문자열.</summary>
    [GeneratedRegex("\"([^\"]*)\"", RegexOptions.None, 2000)]
    private static partial Regex QuotedStrings();
}
