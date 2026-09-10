using Npc.Contracts;
using Npc.Wire;
using Npc.Wire.V2;

namespace Npc.Tests.Wire;

/// <summary>A-06 — 링크 상호 인증. PRODUCTION_ROADMAP §4 A-06.</summary>
[Trait("Category", "Wire")]
public sealed class LinkAuthTests
{
    private static readonly byte[] s_secret =
        Convert.FromHexString("00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff");

    [Fact]
    public void Secret_MustBeThirtyTwoBytesOfHex()
    {
        Assert.True(LinkAuth.TryParseSecret(
            "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff",
            out byte[] secret,
            out _));

        Assert.Equal(LinkAuth.SecretBytes, secret.Length);

        Assert.False(LinkAuth.TryParseSecret("짧다", out _, out string? shortError));
        Assert.Contains("hex 64자", shortError!, StringComparison.Ordinal);

        Assert.False(LinkAuth.TryParseSecret(null, out _, out _));
        Assert.False(LinkAuth.TryParseSecret(new string('z', 64), out _, out string? hexError));
        Assert.Contains("hex 가 아니다", hexError!, StringComparison.Ordinal);
    }

    [Fact]
    public void Hello_RoundTripsWithTheSameSecret()
    {
        WireHelloV2 hello = Hello(LinkAuth.NewNonce());

        hello.Auth = LinkAuth.ComputeHello(s_secret, in hello);

        WireHash recomputed = LinkAuth.ComputeHello(s_secret, in hello);

        Assert.True(LinkAuth.Verify(in recomputed, in hello.Auth));
    }

    [Fact]
    public void Hello_FailsWithADifferentSecret()
    {
        WireHelloV2 hello = Hello(LinkAuth.NewNonce());

        hello.Auth = LinkAuth.ComputeHello(s_secret, in hello);

        byte[] other = (byte[])s_secret.Clone();

        other[0] ^= 0xFF;

        WireHash wrong = LinkAuth.ComputeHello(other, in hello);

        Assert.False(LinkAuth.Verify(in wrong, in hello.Auth));
    }

    [Theory]
    [InlineData("timescale")]
    [InlineData("npccount")]
    [InlineData("features")]
    [InlineData("epoch")]
    [InlineData("roster")]
    [InlineData("structural")]
    [InlineData("nonce")]
    public void Hello_TagCoversEveryNegotiatedField(string field)
    {
        WireHelloV2 hello = Hello(LinkAuth.NewNonce());

        hello.Auth = LinkAuth.ComputeHello(s_secret, in hello);

        // 중간에서 한 필드를 바꿔치기한다. 재료에서 빠진 필드가 있으면 태그가 그대로 맞는다.
        WireHelloV2 tampered = hello;

        switch (field)
        {
            case "timescale": tampered.TimeScale = 999; break;
            case "npccount": tampered.NpcCount = 1; break;
            case "features": tampered.Features = 0xFF; break;
            case "epoch": tampered.SessionEpoch = 77; break;
            case "roster": tampered.Roster = WireHash.FromHex(new string('b', 64)); break;
            case "structural": tampered.MasterDataStructural = WireHash.FromHex(new string('c', 64)); break;
            default: tampered.Nonce = LinkAuth.NewNonce(); break;
        }

        WireHash recomputed = LinkAuth.ComputeHello(s_secret, in tampered);

        Assert.False(
            LinkAuth.Verify(in recomputed, in tampered.Auth),
            $"{field} 를 바꿨는데 태그가 그대로 맞는다 — 재료에서 빠졌다");
    }

    [Fact]
    public void Ack_IsBoundToTheHelloNonce()
    {
        WireNonce nonce = LinkAuth.NewNonce();
        var ack = new WireHelloAckV2
        {
            ProtocolVersion = 2,
            ContractMajor = ContractVersion.Major,
            ContractMinor = ContractVersion.Minor,
            Features = 0x1F,
            TimeScale = 60,
            NpcCount = 500,
            MasterDataStructural = WireHash.FromHex(new string('a', 64)),
            MasterDataContent = WireHash.FromHex(new string('d', 64)),
            Roster = WireHash.FromHex(new string('e', 64)),
            Accepted = 1,
        };

        ack.Auth = LinkAuth.ComputeAck(s_secret, in ack, in nonce);

        Assert.True(LinkAuth.Verify(in ack.Auth, LinkAuth.ComputeAck(s_secret, in ack, in nonce)));

        // 다른 nonce 로는 안 맞는다 — 예전 응답을 그대로 재생할 수 없다.
        WireNonce other = LinkAuth.NewNonce();

        Assert.False(LinkAuth.Verify(in ack.Auth, LinkAuth.ComputeAck(s_secret, in ack, in other)));
    }

    [Fact]
    public void Nonce_IsRememberedOnce()
    {
        var cache = new NonceCache();
        WireNonce nonce = LinkAuth.NewNonce();

        Assert.True(cache.TryRemember(in nonce));

        // 태그가 맞아도 같은 nonce 는 거절이다. 태그는 "그때 이 비밀을 아는 누군가가 만들었다"
        // 만 증명하지 "지금 만들었다" 를 증명하지 않는다.
        Assert.False(cache.TryRemember(in nonce));

        WireNonce fresh = LinkAuth.NewNonce();

        Assert.True(cache.TryRemember(in fresh));
    }

    [Fact]
    public void Nonce_CacheForgetsTheOldest()
    {
        var cache = new NonceCache();
        WireNonce first = LinkAuth.NewNonce();

        Assert.True(cache.TryRemember(in first));

        for (int i = 0; i < LinkAuth.NonceCacheSize; i++)
        {
            WireNonce nonce = LinkAuth.NewNonce();

            Assert.True(cache.TryRemember(in nonce));
        }

        // 무한히 기억하면 그것이 메모리 누수다. 세션 하나에 핸드셰이크가 256번 일어날 일이 없다.
        Assert.True(cache.TryRemember(in first));
    }

    [Fact]
    public void Nonce_IsRandom()
    {
        var seen = new HashSet<WireNonce>();

        for (int i = 0; i < 1_000; i++)
        {
            Assert.True(seen.Add(LinkAuth.NewNonce()), "nonce 가 겹쳤다");
        }
    }

    [Fact]
    public void NonceZero_MeansNoAuth()
    {
        Assert.True(WireNonce.Zero.IsZero);
        Assert.False(LinkAuth.NewNonce().IsZero);
    }

    private static WireHelloV2 Hello(WireNonce nonce) => new()
    {
        ProtocolVersion = FrameCodec.MaxVersion,
        MinProtocolVersion = FrameCodec.MinVersion,
        ContractMajor = ContractVersion.Major,
        ContractMinor = ContractVersion.Minor,
        Features = (ulong)LinkFeatures.Auth,
        TickRate = 10,
        TimeScale = 60,
        NpcCount = 500,
        StartTick = 12_345,
        StartGameMinuteOfDay = 720,
        ShardId = 1,
        ZoneMask = 0b1011,
        SessionEpoch = 7,
        MasterDataStructural = WireHash.FromHex(new string('a', 64)),
        MasterDataContent = WireHash.FromHex(new string('d', 64)),
        Roster = WireHash.FromHex(new string('e', 64)),
        Nonce = nonce,
    };
}
