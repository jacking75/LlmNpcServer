using Npc.Contracts;

namespace Npc.Tests.Contracts;

/// <summary>
/// B-01 — 계약 버전이 열거형 증가를 따라간다.
///
/// <b>이 픽스처는 손으로 갱신하는 스냅샷이다.</b> 열거형에 값을 뒤에 추가하면 여기 숫자를 올리고
/// <see cref="ContractVersion.Minor"/> 도 올린다. 둘 중 하나만 하면 테스트가 깨진다 —
/// 그것이 "알리지 않고 늘어나는" 것을 막는 유일한 장치다.
/// </summary>
[Trait("Category", "Contracts")]
public sealed class ContractVersionTests
{
    /// <summary>
    /// 이 부 버전에서의 멤버 수. 늘리려면 <c>Minor</c> 도 같이 올린다.
    ///
    /// <b>3 에서 <c>GameEventKind</c> 가 17 → 18 이다</b> — B-06 이 <c>PlayerHostility</c> 를
    /// 뒤에 추가했다. 2 에서는 패킷에 필드만 더했고 Kind 는 그대로였다.
    /// </summary>
    private const int ExpectedMinorForCounts = 3;

    private const int NpcCommandKindCount = 12;
    private const int GameEventKindCount = 18;
    private const int ActionFailReasonCount = 10;

    [Fact]
    public void EnumCounts_MatchTheDeclaredMinor()
    {
        // 실제 개수를 먼저 보여 준다 — 깨졌을 때 무엇을 올려야 하는지가 바로 나온다.
        int commands = Enum.GetValues<NpcCommandKind>().Length;
        int events = Enum.GetValues<GameEventKind>().Length;
        int reasons = Enum.GetValues<ActionFailReason>().Length;

        Assert.True(
            commands == NpcCommandKindCount
            && events == GameEventKindCount
            && reasons == ActionFailReasonCount,
            $"열거형이 늘었다: NpcCommandKind {commands}(기대 {NpcCommandKindCount}) · "
            + $"GameEventKind {events}(기대 {GameEventKindCount}) · "
            + $"ActionFailReason {reasons}(기대 {ActionFailReasonCount}). "
            + "뒤에 추가만 했다면 ContractVersion.Minor 를 올리고 이 상수들도 갱신한다.");

        Assert.Equal(ExpectedMinorForCounts, ContractVersion.Minor);
    }

    [Fact]
    public void Major_RejectsDifferentMajor()
    {
        Assert.True(ContractVersion.IsCompatible(ContractVersion.Major));
        Assert.False(ContractVersion.IsCompatible((ushort)(ContractVersion.Major + 1)));
        Assert.False(ContractVersion.IsCompatible(0));
    }

    [Fact]
    public void Minor_TakesTheLowerSide()
    {
        // 낮은 쪽이 이긴다. 높은 쪽은 상대가 모르는 Kind 를 보내지 않는다.
        Assert.Equal(0, ContractVersion.NegotiateMinor(0));
        Assert.Equal(ContractVersion.Minor, ContractVersion.NegotiateMinor(ContractVersion.Minor));
        Assert.Equal(ContractVersion.Minor, ContractVersion.NegotiateMinor(99));
    }

    [Fact]
    public void FeatureBits_AreNotRearranged()
    {
        // 비트 번호는 마스터데이터의 code·bit 와 같은 규칙이다 — 재배치 금지, 추가는 뒤에만.
        Assert.Equal(1UL << 0, (ulong)LinkFeatures.Auth);
        Assert.Equal(1UL << 1, (ulong)LinkFeatures.GlobalIds);
        Assert.Equal(1UL << 2, (ulong)LinkFeatures.ExtSlots);
        Assert.Equal(1UL << 3, (ulong)LinkFeatures.DynamicRoster);
        Assert.Equal(1UL << 4, (ulong)LinkFeatures.Hostility);
        Assert.Equal(1UL << 5, (ulong)LinkFeatures.SessionEpoch);
    }
}
