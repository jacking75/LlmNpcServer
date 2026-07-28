using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npc.Contracts;

namespace Npc.MasterData;

/// <summary>
/// NPC 서버와 게임서버가 <b>같은 NPC 집합을 보고 있는가</b>. docs/20 §10.
///
/// <para>
/// <b>이 타입이 없으면 어떤 사고가 나는가.</b> 두 프로세스가 각자
/// <c>npc_instances.json</c>(5,000마리)에서 NPC 를 뽑는데 뽑는 규칙이 다르면
/// <b>첨자 7번이 서로 다른 NPC</b> 가 된다. 그러면 대장장이에게 밭을 갈라고 명령하게 되고,
/// 증상은 "가끔 이상하게 행동한다" 로 나타나 원인을 찾기가 매우 어렵다.
/// </para>
///
/// <para>
/// 그래서 선택 규칙과 해시를 한 곳에 모으고 <b>양쪽이 같은 함수를 부른다.</b>
/// <see cref="Hash"/> 는 핸드셰이크에 실리고, 다르면 연결이 거절된다 (docs/20 §5.5).
/// </para>
/// </summary>
public sealed class NpcRoster
{
    private NpcRoster(ImmutableArray<NpcInstanceDef> npcs, string hash)
    {
        Npcs = npcs;
        Hash = hash;
    }

    /// <summary>고른 NPC. 순서가 곧 런타임 첨자다.</summary>
    public ImmutableArray<NpcInstanceDef> Npcs { get; }

    /// <summary>고른 수.</summary>
    public int Count => Npcs.Length;

    /// <summary>
    /// 이 집합의 SHA-256 (소문자 hex 64자).
    ///
    /// <b>순서까지 포함한다.</b> 같은 NPC 를 다른 순서로 뽑으면 첨자가 어긋나므로
    /// 그것도 불일치다 — 집합이 아니라 <b>배열</b>의 해시다.
    /// </summary>
    public string Hash { get; }

    /// <summary>
    /// <paramref name="count"/> 마리를 <b>균등 간격</b>으로 뽑는다.
    ///
    /// <para>
    /// <b>앞에서부터 자르지 않는다.</b> <c>npc_instances.json</c> 은 아키타입 code 순이라
    /// 앞에서 자르면 대장장이·목수만 뽑히고 농부가 한 마리도 안 나온다.
    /// </para>
    ///
    /// <para>
    /// <b>이 공식은 바꾸지 않는다.</b> <c>Npc.Host</c> 에 인라인으로 있던 것을 그대로 옮긴 것이고,
    /// 바꾸면 기존 프리베이크·리플레이 산출물의 첨자가 전부 어긋난다
    /// (<c>Roster_SelectionMatchesLegacyFormula</c> 가 못 박는다).
    /// </para>
    /// </summary>
    /// <param name="all">전체 인스턴스 표.</param>
    /// <param name="count">뽑을 수. 전체보다 크면 전체를 준다.</param>
    /// <param name="zoneFilter">
    /// 비어 있지 않으면 <b>그 존의 인스턴스만 남긴 뒤</b> 같은 균등 간격을 적용한다.
    /// 존을 나눠 여러 NPC 서버를 붙일 때 쓴다.
    /// </param>
    public static NpcRoster Select(NpcInstanceTable all, int count, ReadOnlySpan<ZoneId> zoneFilter = default)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        ImmutableArray<NpcInstanceDef> pool = zoneFilter.IsEmpty
            ? AllOf(all)
            : InZones(all, zoneFilter);

        int take = Math.Min(count, pool.Length);
        var chosen = ImmutableArray.CreateBuilder<NpcInstanceDef>(take);

        for (int i = 0; i < take; i++)
        {
            // Npc.Host 에 있던 공식 그대로다. long 캐스팅을 빼면 i * Count 가 넘친다.
            chosen.Add(pool[(int)((long)i * pool.Length / take)]);
        }

        ImmutableArray<NpcInstanceDef> result = chosen.MoveToImmutable();

        return new NpcRoster(result, HashOf(result));
    }

    /// <summary>
    /// 로스터 해시. <c>id,archetype,home,workplace</c> 줄들의 SHA-256.
    ///
    /// <b>좌표(<c>Spawn</c>)는 넣지 않는다.</b> 게임서버가 스폰 위치를 조정해도 같은 NPC 이고,
    /// 그것 때문에 연결이 거절되면 우회 옵션을 만들고 싶어진다 — 그게 이 검사를 무력화하는 길이다.
    /// </summary>
    public static string HashOf(ImmutableArray<NpcInstanceDef> npcs)
    {
        var text = new StringBuilder(npcs.Length * 24);

        foreach (NpcInstanceDef npc in npcs)
        {
            text.Append(CultureInfo.InvariantCulture, $"{npc.Id},{npc.Archetype.Value},");
            text.Append(CultureInfo.InvariantCulture, $"{npc.Home.Value},{npc.Workplace.Value}\n");
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static ImmutableArray<NpcInstanceDef> AllOf(NpcInstanceTable all)
    {
        var builder = ImmutableArray.CreateBuilder<NpcInstanceDef>(all.Count);

        for (int i = 0; i < all.Count; i++)
        {
            builder.Add(all[i]);
        }

        return builder.MoveToImmutable();
    }

    private static ImmutableArray<NpcInstanceDef> InZones(NpcInstanceTable all, ReadOnlySpan<ZoneId> zones)
    {
        var builder = ImmutableArray.CreateBuilder<NpcInstanceDef>();

        for (int i = 0; i < all.Count; i++)
        {
            NpcInstanceDef npc = all[i];

            foreach (ZoneId zone in zones)
            {
                if (npc.Zone == zone)
                {
                    builder.Add(npc);
                    break;
                }
            }
        }

        return builder.ToImmutable();
    }
}
