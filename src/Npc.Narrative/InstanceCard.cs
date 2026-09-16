using System.Collections.Immutable;
using System.Text;
using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;

namespace Npc.Narrative;

/// <summary>
/// NPC 인스턴스 카드 (F-03). 아키타입 카드가 <b>정의</b>라면 이것은 <b>개체</b>다 —
/// 실제로 어느 집에 살고 어느 일터로 가며 그 사이가 몇 미터인가.
///
/// <b>거리를 여기서만 셀 수 있다.</b> 플랜의 예상 소요 시간이 이동 시간에 좌우되는데,
/// 이동 거리는 개체 바인딩 이후에야 정해진다 — 그래서 아키타입 카드는 타임아웃 합만 낸다.
/// </summary>
/// <summary>
/// 개체 하나에서 뽑은 사실들 (T07). 아키타입 카드가 <b>정의</b>라면 이것은 <b>개체</b>다.
/// </summary>
/// <param name="Npc">인스턴스 원본.</param>
/// <param name="ArchetypeId">직업 id.</param>
/// <param name="ZoneId">지역 id.</param>
/// <param name="Home">집 POI.</param>
/// <param name="Workplace">일터 POI. 없으면 null.</param>
/// <param name="CommuteMeters">집 ↔ 일터 거리(m). 일터가 없으면 0.</param>
/// <param name="CanEnterWorkplace">일터에 근무 허가가 있는가.</param>
public readonly record struct InstanceFacts(
    NpcInstanceDef Npc,
    string ArchetypeId,
    string ZoneId,
    PoiDef Home,
    PoiDef? Workplace,
    float CommuteMeters,
    bool CanEnterWorkplace);

public static class InstanceCard
{
    /// <summary>개체 하나의 사실들 (T07). 화면·예측이 카드 대신 이것을 읽는다.</summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="npc">인스턴스.</param>
    public static InstanceFacts Facts(MasterDataSet data, in NpcInstanceDef npc)
    {
        ArgumentNullException.ThrowIfNull(data);

        PoiDef? workplace = npc.Workplace == default ? null : data.Pois[npc.Workplace];

        return new InstanceFacts(
            npc,
            data.Archetypes[npc.Archetype].Id,
            data.Zones[npc.Zone].Id,
            data.Pois[npc.Home],
            workplace,
            workplace is null ? 0 : data.Pois.Distance(npc.Home, npc.Workplace),
            workplace is null || workplace.CanEnter(npc.Archetype));
    }

    /// <summary>
    /// "이 NPC 가 누구인가" 를 한 문장으로 (T07). 표를 읽기 전에 사람이 먼저 읽는 줄이다.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="npc">인스턴스.</param>
    public static string Sentence(MasterDataSet data, in NpcInstanceDef npc)
    {
        ArgumentNullException.ThrowIfNull(data);

        InstanceFacts facts = Facts(data, npc);
        string zone = Lexicon.Zone(data, facts.ZoneId);
        string job = Lexicon.ArchetypeName(data, facts.ArchetypeId);
        var sb = new StringBuilder(256);

        sb.Append('#').Append(npc.Id).Append(" 은 ").Append(Lexicon.With(zone, "의", "의"))
          .Append(' ').Append(Lexicon.With(job, "이다", "다")).Append(". ");

        sb.Append(Lexicon.PlaceName(facts.Home)).Append("에 살고 ");

        if (facts.Workplace is { } work)
        {
            sb.Append(Lexicon.PlaceName(work)).Append("에서 일한다 (걸어서 약 ")
              .Append(Md.F(facts.CommuteMeters, 0)).Append(" m). ");
        }
        else
        {
            sb.Append("일터는 없다. ");
        }

        var extras = new List<string>(4);

        if (!npc.PatrolRoute.IsDefaultOrEmpty)
        {
            extras.Add($"순찰로 {npc.PatrolRoute.Length}곳");
        }

        if (npc.Faction != default && data.Factions is { } factions)
        {
            extras.Add("세력 " + factions.NameOf(npc.Faction));
        }

        if (npc.ScheduleOffsetMinutes != 0)
        {
            extras.Add(npc.ScheduleOffsetMinutes > 0
                ? $"일정이 {npc.ScheduleOffsetMinutes}분 늦다"
                : $"일정이 {-npc.ScheduleOffsetMinutes}분 이르다");
        }

        if (npc.AggroRadiusM != 0)
        {
            extras.Add($"경계 반경 {npc.AggroRadiusM} m");
        }

        if (extras.Count > 0)
        {
            sb.Append(string.Join(" · ", extras)).Append('.');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>NPC 한 마리. <paramref name="index"/> 는 <c>NpcStore</c> 첨자다.</summary>
    public static string Render(MasterDataSet data, NpcInstanceTable instances, int index)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, instances.Count);

        NpcInstanceDef npc = instances[index];
        ArchetypeDef archetype = data.Archetypes[npc.Archetype];
        PoiDef home = data.Pois[npc.Home];

        var sb = new StringBuilder(2 * 1024);

        sb.Append("# NPC ").Append(Md.N(npc.Id)).Append(" · ")
          .Append(Lexicon.Archetype(archetype.Id))
          .Append(" (`").Append(archetype.Id).AppendLine("`)");
        sb.AppendLine();

        Md.TableHead(sb, "항목", "값", "근거");

        Md.Row(sb, "존", Md.Code(data.Zones[npc.Zone].Id), "`npc_instances.json`");
        Md.Row(sb, "집", Place(data, npc.Home), "`home_poi`");
        Md.Row(sb, "일터", npc.Workplace == default ? "없음" : Place(data, npc.Workplace), "`workplace_poi`");
        Md.Row(sb, "집 ↔ 일터", Commute(data, npc), "`poi_distances.bin`");
        Md.Row(sb, "스폰", $"({Md.F(npc.Spawn.X, 1)}, {Md.F(npc.Spawn.Y, 1)})", "`spawn`");
        Md.Row(sb, "출입 허가", Entry(data, npc, home), "`allowed_archetypes` · V10");

        sb.AppendLine();
        sb.AppendLine("정의(허용 액션 · 성향 · 폴백 하루 · 걸릴 인터럽트)는 아키타입 카드에 있다.");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>이 아키타입의 인스턴스 첨자 목록. <c>--npcs</c> 를 작게 잡으면 안 보이는 이유를 여기서 본다.</summary>
    public static string RenderRoster(MasterDataSet data, NpcInstanceTable instances, ArchetypeId archetype)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(instances);

        ArchetypeDef def = data.Archetypes[archetype];
        var sb = new StringBuilder(1024);
        int first = -1;
        int last = -1;
        int count = 0;

        for (int i = 0; i < instances.Count; i++)
        {
            if (instances[i].Archetype != archetype)
            {
                continue;
            }

            first = first < 0 ? i : first;
            last = i;
            count++;
        }

        sb.Append("# ").Append(Lexicon.Archetype(def.Id)).Append(" 인스턴스 ")
          .Append(Md.N(count)).AppendLine("마리");
        sb.AppendLine();

        if (count == 0)
        {
            sb.AppendLine("한 마리도 없다. `population_weight` 가 0 이거나 `gen_npcs` 를 다시 돌리지 않았다.");
            sb.AppendLine();
            return sb.ToString();
        }

        sb.Append("첨자 ").Append(Md.N(first)).Append(" ~ ").Append(Md.N(last))
          .Append(" (전체 ").Append(Md.N(instances.Count)).AppendLine("마리 중).");
        sb.AppendLine();

        // gen_npcs 는 아키타입 code 순서로 배치한다. code 가 뒤쪽이면 --npcs 를 작게 잡을 때
        // 한 마리도 안 뜬다 — "새 직업을 넣었는데 안 보인다" 의 가장 흔한 원인이다.
        sb.Append("`--npcs ").Append(Md.N(first)).AppendLine(" 이하로 돌리면 한 마리도 뜨지 않는다 — ")
          .Append("`gen_npcs` 가 아키타입 code 순서로 배치하기 때문이다 (이 아키타입은 code ")
          .Append(Md.N(def.Code.Value)).AppendLine(").");
        sb.AppendLine();

        return sb.ToString();
    }

    private static string Place(MasterDataSet data, PoiId poi)
    {
        PoiDef def = data.Pois[poi];

        return $"{Lexicon.Place(def.Subtype)} `{def.Id}` (정원 {Md.N(def.Capacity)})";
    }

    private static string Commute(MasterDataSet data, in NpcInstanceDef npc)
    {
        if (npc.Workplace == default)
        {
            return "—";
        }

        float distance = data.Pois.Distance(npc.Home, npc.Workplace);

        return $"{Md.F(distance, 1)} m";
    }

    /// <summary>
    /// 일터에 들어갈 수 있는가. <b>정원과 허가는 다른 것이다</b> — 정원이 남아도
    /// <c>allowed_archetypes</c> 에 없으면 일할 수 없다.
    /// </summary>
    private static string Entry(MasterDataSet data, in NpcInstanceDef npc, PoiDef home)
    {
        if (npc.Workplace == default)
        {
            return $"집 `{home.Id}` 만 — 일터가 없다";
        }

        PoiDef workplace = data.Pois[npc.Workplace];
        bool allowed = workplace.CanEnter(npc.Archetype);

        return allowed
            ? $"{Md.Mark(true)} 일터 `{workplace.Id}` 에 근무 허가가 있다"
            : $"{Md.Mark(false)} 일터 `{workplace.Id}` 의 `allowed_archetypes` 에 이 아키타입이 없다";
    }
}
