using Npc.Contracts;

namespace Npc.Wire.V2;

/// <summary>협상 결과 (B-01).</summary>
/// <param name="Accepted">붙을 수 있는가.</param>
/// <param name="ProtocolVersion">이후 프레임의 <c>Ver</c>. 실패면 0.</param>
/// <param name="ContractMinor">낮은 쪽 부 버전.</param>
/// <param name="Features">양쪽 교집합.</param>
/// <param name="Reject">거절 사유. 성공이면 <see cref="LinkRejectCode.None"/>.</param>
/// <param name="Detail">사람이 읽는 사유.</param>
public readonly record struct NegotiationResult(
    bool Accepted,
    int ProtocolVersion,
    ushort ContractMinor,
    ulong Features,
    LinkRejectCode Reject,
    string Detail);

/// <summary>
/// 프로토콜·계약 버전 협상 (B-01).
///
/// <b>NPC 서버가 고른다.</b> 게임서버는 자기가 받아 줄 범위 <c>[Min, Max]</c> 와 계약 버전·기능
/// 비트를 보내고, NPC 서버가 자기 범위와의 교집합에서 <b>최댓값</b>을 골라 되돌린다.
/// 교집합이 비면 <c>ProtocolVersion</c> 거절, 계약 주 버전이 다르면 <c>ContractMismatch</c> 다.
///
/// <para>
/// <b>왜 최댓값인가.</b> 최솟값을 고르면 양쪽이 새 버전을 지원해도 영원히 옛 버전으로 도는데,
/// 그러면 새 버전을 올릴 이유가 사라진다.
/// </para>
/// </summary>
public static class VersionNegotiation
{
    /// <summary>NPC 서버가 말할 수 있는 최소 프로토콜 버전.</summary>
    public const int SupportedMin = 1;

    /// <summary>NPC 서버가 말할 수 있는 최대 프로토콜 버전.</summary>
    public const int SupportedMax = 2;

    /// <summary>
    /// NPC 서버가 지원하는 기능 비트 전부.
    ///
    /// 게임서버가 켜지 않은 비트는 협상에서 꺼진다 — 우리가 지원한다고 상대가 이해하는 것은 아니다.
    /// </summary>
    public const ulong SupportedFeatures =
        (ulong)(LinkFeatures.Auth | LinkFeatures.GlobalIds | LinkFeatures.ExtSlots
                | LinkFeatures.DynamicRoster | LinkFeatures.Hostility | LinkFeatures.SessionEpoch);

    /// <summary>
    /// 협상한다.
    /// </summary>
    /// <param name="theirMax">게임서버가 원하는 최대 프로토콜 버전.</param>
    /// <param name="theirMin">게임서버가 받아 줄 최소. 0 이면 v1 게임서버라 <paramref name="theirMax"/> 와 같다.</param>
    /// <param name="theirContractMajor">게임서버의 계약 주 버전. 0 이면 v1 이라 검사하지 않는다.</param>
    /// <param name="theirContractMinor">게임서버의 계약 부 버전.</param>
    /// <param name="theirFeatures">게임서버의 기능 비트.</param>
    /// <param name="requireAuth">인증을 반드시 요구하는가 (A-06).</param>
    public static NegotiationResult Negotiate(
        int theirMax,
        int theirMin,
        ushort theirContractMajor,
        ushort theirContractMinor,
        ulong theirFeatures,
        bool requireAuth = false)
    {
        // v1 게임서버는 MinProtocolVersion 을 모른다. 한 점 범위로 읽는다.
        int min = theirMin <= 0 ? theirMax : theirMin;
        int max = theirMax;

        if (min > max)
        {
            return new NegotiationResult(
                false, 0, 0, 0, LinkRejectCode.ProtocolVersion,
                $"게임서버 범위가 뒤집혔다: [{min}, {max}]");
        }

        int chosen = Math.Min(max, SupportedMax);

        if (chosen < Math.Max(min, SupportedMin))
        {
            return new NegotiationResult(
                false, 0, 0, 0, LinkRejectCode.ProtocolVersion,
                $"프로토콜 교집합이 없다: 게임서버 [{min}, {max}] vs NPC 서버 [{SupportedMin}, {SupportedMax}]");
        }

        // v1 은 계약 버전을 모른다. 그 회차는 계약 검사를 건너뛰고 v1 의 의미로 돈다.
        ushort minor = 0;

        if (chosen >= 2)
        {
            if (!ContractVersion.IsCompatible(theirContractMajor))
            {
                return new NegotiationResult(
                    false, 0, 0, 0, LinkRejectCode.ContractMismatch,
                    $"계약 주 버전이 다르다: 게임서버 {theirContractMajor} vs NPC 서버 {ContractVersion.Major}");
            }

            minor = ContractVersion.NegotiateMinor(theirContractMinor);
        }

        ulong features = chosen >= 2 ? theirFeatures & SupportedFeatures : 0;

        if (requireAuth && (features & (ulong)LinkFeatures.Auth) == 0)
        {
            return new NegotiationResult(
                false, 0, 0, 0, LinkRejectCode.AuthFailed,
                "인증을 요구했는데 게임서버가 Auth 기능을 켜지 않았다");
        }

        return new NegotiationResult(
            true, chosen, minor, features, LinkRejectCode.None,
            $"protocol {chosen} · contract {ContractVersion.Major}.{minor} · features 0x{features:X}");
    }

    /// <summary>기능 비트가 켜져 있는가.</summary>
    public static bool Has(ulong features, LinkFeatures flag) => (features & (ulong)flag) != 0;
}
