using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npc.Core;

namespace Npc.MasterData;

/// <summary>
/// 구조 해시 (B-04).
///
/// <b>마스터데이터가 한 글자만 달라도 링크가 안 붙던 것을 고친다.</b> 상용에서는 게임서버·
/// NPC 서버·클라이언트가 다른 파이프라인으로 배포되고 핫픽스로 한쪽만 갱신되는 일이 잦다.
/// 그때마다 NPC 전체가 멈추는 것(성능 저하가 아니라 링크 거절)은 과잉이다.
///
/// <para><b>무엇이 구조인가.</b> 두 프로세스가 <b>같은 번호로 같은 것을 가리켜야</b> 하는 값들이다.</para>
/// <list type="bullet">
///   <item><description>액션·아이템·아키타입·존·POI 의 <c>id ↔ code</c> 대응</description></item>
///   <item><description>월드 플래그의 <c>id ↔ bit</c> 대응</description></item>
///   <item><description>POI 의 소속 존과 좌표 — 게임서버가 여기를 다르게 알면 NPC 가 엉뚱한 곳으로 간다</description></item>
///   <item><description>버킷 공간의 차원 — 플랜 캐시 키가 어긋나면 다른 버킷의 플랜을 쓴다</description></item>
/// </list>
///
/// <para>
/// <b>무엇이 내용인가.</b> 나머지 전부 — <c>desc</c>·<c>traits</c>·<c>allowed_actions</c>·
/// 인터럽트·폴백·레시피·<c>open_hours</c>·정원. 이것이 다른 것은 "한쪽이 밸런스를 먼저 받은
/// 상태" 이고 정상 운영의 일부다. 그래서 <b>경고 후 수락</b>한다.
/// </para>
///
/// <para>
/// <b>번호 재배치 금지는 여전히 강제된다</b> (CLAUDE.md §2.4) — <c>code</c>·<c>bit</c> 를 바꾸면
/// 구조 해시가 바뀌고 링크가 거절된다. 그것이 이 분할이 지키는 것이다.
/// </para>
///
/// 파일 바이트가 아니라 <b>로드된 표</b>에서 계산한다. 같은 파일 안에서 구조와 내용을 가르는
/// 방법은 그것뿐이다 — 줄바꿈·키 순서·주석이 바뀌어도 구조 해시는 그대로여야 한다.
/// </summary>
public static class StructuralHash
{
    /// <summary>로드된 마스터데이터의 구조 해시를 계산한다. 기동 시 1회.</summary>
    public static string Compute(
        ActionCatalog actions,
        ItemTable items,
        ZoneTable zones,
        PoiTable pois,
        ArchetypeTable archetypes,
        BucketSpace buckets,
        DialogueTable? dialogues = null,
        FactionTable? factions = null)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(pois);
        ArgumentNullException.ThrowIfNull(archetypes);
        ArgumentNullException.ThrowIfNull(buckets);

        var sb = new StringBuilder(64 * 1024);

        // 순서는 code 오름차순으로 고정한다. 표의 순회 순서에 기대면 결정론이 깨진다.
        sb.Append("v1\n");

        sb.Append("flags\n");

        foreach (WorldFlags flag in Enum.GetValues<WorldFlags>().Order())
        {
            if (flag == WorldFlags.None)
            {
                continue;
            }

            sb.Append(flag.ToString()).Append(':').Append(BitIndex(flag)).Append('\n');
        }

        sb.Append("actions\n");

        foreach (ActionDef action in actions.Actions.OrderBy(a => a.Code.Value))
        {
            sb.Append(action.Id).Append(':').Append(action.Code.Value).Append('\n');
        }

        // D-02 — 대사 code 는 구조다. 두 프로세스가 다른 번호를 쓰면 NPC 가 엉뚱한 말을 하고,
        // 그 사고는 아무 로그도 안 남긴다. 표가 없는 회차(최소 픽스처)에서는 이 절이 비어 있다 —
        // 옛 해시와 같은 값이 나와야 기존 게임서버가 그대로 붙는다.
        if (dialogues is { Count: > 0 })
        {
            sb.Append("dialogues\n");

            foreach (DialogueLine line in dialogues.Lines)
            {
                sb.Append(line.Id).Append(':').Append(line.Code.Value).Append('\n');
            }
        }

        // D-04 — 세력 code 는 구조다. 나가는 명령·들어오는 이벤트의 Faction 슬롯 값이라
        // 두 프로세스가 다른 번호를 쓰면 위병대를 도적으로 읽는다. 표가 없는 회차에서는
        // 이 절이 비어 있다 — 옛 해시와 같은 값이 나와야 기존 게임서버가 그대로 붙는다.
        if (factions is { Count: > 0 })
        {
            sb.Append("factions\n");

            foreach (FactionDef faction in factions.Factions)
            {
                sb.Append(faction.Id).Append(':').Append(faction.Code.Value).Append('\n');
            }
        }

        sb.Append("items\n");

        foreach (ItemDef item in items.Items.OrderBy(i => i.Code.Value))
        {
            sb.Append(item.Id).Append(':').Append(item.Code.Value).Append('\n');
        }

        sb.Append("archetypes\n");

        foreach (ArchetypeDef archetype in archetypes.Archetypes.OrderBy(a => a.Code.Value))
        {
            sb.Append(archetype.Id).Append(':').Append(archetype.Code.Value).Append('\n');
        }

        sb.Append("zones\n");

        foreach (ZoneDef zone in zones.Zones.OrderBy(z => z.Code.Value))
        {
            sb.Append(zone.Id).Append(':').Append(zone.Code.Value).Append('\n');
        }

        sb.Append("pois\n");

        foreach (PoiDef poi in pois.Pois.OrderBy(p => p.Code.Value))
        {
            // 좌표는 비트 패턴으로 넣는다. 반올림 차이를 놓치면 게임서버와 다른 곳을 가리키게 된다.
            sb.Append(poi.Id).Append(':').Append(poi.Code.Value)
              .Append(':').Append(poi.Zone.Value)
              .Append(':').Append(Bits(poi.Pos.X))
              .Append(':').Append(Bits(poi.Pos.Y))
              .Append(':').Append(Bits(poi.Pos.Z))
              .Append('\n');
        }

        // 버킷 공간의 차원. 어긋나면 플랜 캐시 키가 달라져 다른 버킷의 플랜을 쓴다.
        sb.Append("buckets:")
          .Append(archetypes.Count).Append('x')
          .Append(BucketKey.TimeOfDayCount).Append('x')
          .Append(BucketKey.RegionStateCount).Append('x')
          .Append(BucketKey.ClimateCount).Append(':')
          .Append(buckets.DeclaredTotalKeys).Append('\n');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string Bits(float value) =>
        BitConverter.SingleToUInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture);

    private static int BitIndex(WorldFlags flag)
    {
        ulong value = (ulong)flag;

        return value == 0 ? -1 : System.Numerics.BitOperations.TrailingZeroCount(value);
    }
}
