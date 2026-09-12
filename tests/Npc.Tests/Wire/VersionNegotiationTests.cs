using Npc.Contracts;
using Npc.Wire;
using Npc.Wire.V2;

namespace Npc.Tests.Wire;

/// <summary>B-01 — 프로토콜·계약 협상. PRODUCTION_ROADMAP §5 B-01.</summary>
[Trait("Category", "Wire")]
public sealed class VersionNegotiationTests
{
    [Fact]
    public void V1GameServer_GetsV1()
    {
        // v1 게임서버는 MinProtocolVersion 을 모른다 — 0 이 온다. 한 점 범위로 읽는다.
        NegotiationResult result = VersionNegotiation.Negotiate(
            theirMax: 1, theirMin: 0, theirContractMajor: 0, theirContractMinor: 0, theirFeatures: 0);

        Assert.True(result.Accepted, result.Detail);
        Assert.Equal(1, result.ProtocolVersion);

        // v1 회차에는 기능 비트가 없다. 협상되지 않은 기능을 켜면 상대가 이해하지 못한다.
        Assert.Equal(0UL, result.Features);
    }

    [Fact]
    public void V2GameServer_GetsTheMaximumOfTheIntersection()
    {
        NegotiationResult result = VersionNegotiation.Negotiate(
            theirMax: 2, theirMin: 1,
            ContractVersion.Major, ContractVersion.Minor,
            (ulong)(LinkFeatures.GlobalIds | LinkFeatures.Hostility));

        Assert.True(result.Accepted, result.Detail);

        // 최솟값을 고르면 양쪽이 새 버전을 지원해도 영원히 옛 버전으로 돈다.
        Assert.Equal(2, result.ProtocolVersion);
        Assert.True(VersionNegotiation.Has(result.Features, LinkFeatures.GlobalIds));
        Assert.True(VersionNegotiation.Has(result.Features, LinkFeatures.Hostility));
        Assert.False(VersionNegotiation.Has(result.Features, LinkFeatures.Auth));
    }

    [Fact]
    public void FutureGameServer_FallsBackToOurMaximum()
    {
        // 게임서버가 우리보다 앞서 나가도 우리가 아는 최대까지는 붙는다.
        NegotiationResult result = VersionNegotiation.Negotiate(
            theirMax: 9, theirMin: 1, ContractVersion.Major, ContractVersion.Minor, 0);

        Assert.True(result.Accepted, result.Detail);
        Assert.Equal(VersionNegotiation.SupportedMax, result.ProtocolVersion);
    }

    [Fact]
    public void EmptyIntersection_IsRejected()
    {
        NegotiationResult result = VersionNegotiation.Negotiate(
            theirMax: 9, theirMin: 5, ContractVersion.Major, ContractVersion.Minor, 0);

        Assert.False(result.Accepted);
        Assert.Equal(LinkRejectCode.ProtocolVersion, result.Reject);
        Assert.Contains("교집합", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ContractMajorMismatch_IsRejected()
    {
        NegotiationResult result = VersionNegotiation.Negotiate(
            theirMax: 2, theirMin: 2,
            theirContractMajor: (ushort)(ContractVersion.Major + 1),
            theirContractMinor: 0,
            theirFeatures: 0);

        Assert.False(result.Accepted);
        Assert.Equal(LinkRejectCode.ContractMismatch, result.Reject);
    }

    [Fact]
    public void ContractMinor_TakesTheLowerSide()
    {
        NegotiationResult result = VersionNegotiation.Negotiate(
            theirMax: 2, theirMin: 2, ContractVersion.Major, theirContractMinor: 0, theirFeatures: 0);

        Assert.True(result.Accepted, result.Detail);
        Assert.Equal(0, result.ContractMinor);
    }

    [Fact]
    public void RequireAuth_RejectsGameServerWithoutIt()
    {
        NegotiationResult result = VersionNegotiation.Negotiate(
            theirMax: 2, theirMin: 2, ContractVersion.Major, ContractVersion.Minor,
            theirFeatures: 0, requireAuth: true);

        Assert.False(result.Accepted);
        Assert.Equal(LinkRejectCode.AuthFailed, result.Reject);
    }

    [Fact]
    public void RequireAuth_AcceptsWhenTheBitIsOn()
    {
        NegotiationResult result = VersionNegotiation.Negotiate(
            theirMax: 2, theirMin: 2, ContractVersion.Major, ContractVersion.Minor,
            theirFeatures: (ulong)LinkFeatures.Auth, requireAuth: true);

        Assert.True(result.Accepted, result.Detail);
        Assert.True(VersionNegotiation.Has(result.Features, LinkFeatures.Auth));
    }

    [Fact]
    public void InvertedRange_IsRejected()
    {
        NegotiationResult result = VersionNegotiation.Negotiate(
            theirMax: 1, theirMin: 2, ContractVersion.Major, ContractVersion.Minor, 0);

        Assert.False(result.Accepted);
        Assert.Equal(LinkRejectCode.ProtocolVersion, result.Reject);
    }

    [Fact]
    public void FrameCodec_AcceptsTheSupportedRange()
    {
        // v1 은 동결이다 (CLAUDE.md §7). 하한을 올리는 순간 이미 붙어 있는 v1 게임서버가 전부 끊긴다.
        // 상한은 프로토콜이 늘면 같이 는다 — 여기서 못박지 않는다.
        Assert.Equal(1, FrameCodec.MinVersion);
        Assert.True(FrameCodec.MaxVersion >= FrameCodec.MinVersion);

        // 기본으로 쓰는 것은 v1 이다 — 핸드셰이크 전에는 상대를 모른다.
        Assert.Equal(FrameCodec.MinVersion, FrameCodec.Version);
    }

    [Fact]
    public void FrameCodec_RejectsOutOfRangeVersion()
    {
        var writer = new System.Buffers.ArrayBufferWriter<byte>();

        FrameCodec.WriteHeader(writer, LinkMessageKind.Heartbeat, 0, FrameCodec.MaxVersion);

        byte[] frame = writer.WrittenSpan.ToArray();

        frame[5] = 9;

        var buffer = new System.Buffers.ReadOnlySequence<byte>(frame);

        Assert.Throws<InvalidDataException>(() =>
        {
            System.Buffers.ReadOnlySequence<byte> local = buffer;

            FrameCodec.TryReadFrame(ref local, out _, out _, out _);
        });
    }

    [Fact]
    public void FrameCodec_RoundTripsVersionTwo()
    {
        var writer = new System.Buffers.ArrayBufferWriter<byte>();

        FrameCodec.WriteHeader(writer, LinkMessageKind.Hello, 0, 2);

        var buffer = new System.Buffers.ReadOnlySequence<byte>(writer.WrittenSpan.ToArray());

        Assert.True(FrameCodec.TryReadFrame(ref buffer, out LinkMessageKind kind, out _, out byte version));
        Assert.Equal(LinkMessageKind.Hello, kind);
        Assert.Equal(2, version);
    }
}
