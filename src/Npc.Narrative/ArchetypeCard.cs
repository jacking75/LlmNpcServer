using System.Collections.Immutable;
using System.Text;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Narrative;

/// <summary>
/// 아키타입 카드 (F-03). 정의를 <b>사람이 읽는 한 장</b>으로 만든다.
///
/// <b>검수자가 지금 보는 것은 "허용 22종" 이라는 개수다.</b> 어떤 22종인지, 무엇이 빠졌는지,
/// 그 성향이면 어느 인터럽트에 걸리는지는 파일 여섯 개를 대조해야 알 수 있었다.
/// 이 카드가 그 대조를 대신한다.
///
/// <para>
/// <b>LLM 을 쓰지 않는다. 시각도 난수도 쓰지 않는다</b> — 같은 마스터데이터면 바이트 동일이라
/// 골든 스냅샷으로 회귀를 잡을 수 있다.
/// </para>
/// </summary>
/// <summary>
/// 아키타입 정의에서 뽑은 사실들 (T05). <b>카드가 문장으로 만들기 전의 값</b>이다.
///
/// <para>
/// 화면은 마크다운 한 장을 열로 쪼갤 수 없다 — "정원이 인구를 감당하는가" 를 ✓/✗ 로 그리려면
/// 두 숫자가 따로 있어야 한다. <see cref="ArchetypeCard.Render(MasterDataSet, ArchetypeDef, int)"/>
/// 는 이 값들을 문자열로 만들 뿐이라 <b>카드 출력은 바이트 불변</b>이다.
/// </para>
/// </summary>
/// <param name="Def">정의 원본.</param>
/// <param name="Population">이 아키타입의 인구.</param>
/// <param name="PopulationBase">인구를 센 기준 NPC 수.</param>
/// <param name="HomeType">집 POI 유형.</param>
/// <param name="WorkplaceType">일터 POI 세부 유형. 없으면 null.</param>
/// <param name="WorkplaceSites">그 세부 유형의 장소 수.</param>
/// <param name="WorkplaceCapacity">그 장소들의 정원 합.</param>
/// <param name="DutyHours">근무 시간대.</param>
/// <param name="DutyMissingButNeeded">근무 시간대가 없는데 경계 근무·순찰을 허용하는가 (V12 의 병).</param>
/// <param name="DutyActionsAllowed">허용된 근무 계열 액션 id.</param>
/// <param name="AllowedByCategory">카테고리별 허용 액션 id (빈 카테고리는 없다).</param>
/// <param name="Denied">허용하지 않는 액션 id.</param>
/// <param name="Recipes">주력 제작품.</param>
/// <param name="Inventory">시작 소지품.</param>
/// <param name="StartFlags">시작 소지품이 세우는 플래그.</param>
/// <param name="Interrupts">성향·전투 가능으로 걸릴 수 있는 인터럽트 (매칭 순서).</param>
/// <param name="Fallback">하루 일과. 못 찾으면 null.</param>
public sealed record ArchetypeFacts(
    ArchetypeDef Def,
    int Population,
    int PopulationBase,
    string HomeType,
    string? WorkplaceType,
    int WorkplaceSites,
    int WorkplaceCapacity,
    ImmutableArray<TimeOfDay> DutyHours,
    bool DutyMissingButNeeded,
    ImmutableArray<string> DutyActionsAllowed,
    ImmutableArray<(ActionCategory Category, ImmutableArray<string> Ids)> AllowedByCategory,
    ImmutableArray<string> Denied,
    ImmutableArray<(string Recipe, string Inputs, int Seconds, bool Exists)> Recipes,
    ImmutableArray<(string Item, int Count)> Inventory,
    WorldFlags StartFlags,
    ImmutableArray<InterruptRule> Interrupts,
    CompiledPlan? Fallback)
{
    /// <summary>일터 정원이 인구를 감당하는가. V10 이 보는 것과 같은 계산이다.</summary>
    public bool WorkplaceFits => WorkplaceType is null || WorkplaceCapacity >= Population;

    /// <summary>일터 정원 여유 비율. 일터가 없으면 1.</summary>
    public double CapacityMargin =>
        WorkplaceType is null || WorkplaceCapacity <= 0
            ? 1
            : (WorkplaceCapacity - Population) / (double)WorkplaceCapacity;

    /// <summary>허용 액션 수.</summary>
    public int AllowedCount => Def.AllowedActions.Length;
}

public static class ArchetypeCard
{
    /// <summary>인구를 셀 때 쓰는 기준 NPC 수. <c>gen_npcs</c> 의 기본값과 같다.</summary>
    public const int PopulationBase = 5_000;

    /// <summary>
    /// 이 아키타입에 대해 카드가 아는 것 전부 (T05). 화면·진단(T29)·요약(T34) 이 같이 쓴다.
    /// </summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="def">아키타입 정의. 저장 전 초안이어도 된다.</param>
    /// <param name="population">인구를 셀 기준 NPC 수.</param>
    public static ArchetypeFacts Facts(MasterDataSet data, ArchetypeDef def, int population = PopulationBase)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(def);

        int count = (int)Math.Round(def.PopulationWeight * population);
        int sites = 0;
        int capacity = 0;

        if (def.WorkplacePoiType is { Length: > 0 } workplace)
        {
            ImmutableArray<PoiId> list = data.Pois.OfSubtype(workplace);
            sites = list.Length;

            foreach (PoiId poi in list)
            {
                capacity += data.Pois[poi].Capacity;
            }
        }

        ImmutableArray<string> dutyActions = [.. DutyActionIds.Where(id => Allows(data, def, id))];

        var byCategory = ImmutableArray.CreateBuilder<(ActionCategory, ImmutableArray<string>)>();

        // 카테고리 순서는 열거형 ordinal 이다 — 같은 입력이면 같은 순서여야 한다.
        foreach (ActionCategory category in Enum.GetValues<ActionCategory>())
        {
            ImmutableArray<string> allowed =
            [
                .. data.Actions.Actions
                    .Where(a => a.Category == category && def.Allows(a.Code))
                    .Select(a => a.Id),
            ];

            if (allowed.Length > 0)
            {
                byCategory.Add((category, allowed));
            }
        }

        var recipes = ImmutableArray.CreateBuilder<(string, string, int, bool)>(def.PrimaryRecipes.Length);

        foreach (string id in def.PrimaryRecipes)
        {
            if (!data.Items.TryGetRecipe(id, out RecipeDef recipe))
            {
                recipes.Add((id, string.Empty, 0, false));
                continue;
            }

            recipes.Add((
                id,
                string.Join(" + ", recipe.Inputs.Select(i => $"{Lexicon.Item(data.Items[i.Item].Id)} {Md.N(i.Count)}")),
                recipe.DurationSeconds,
                true));
        }

        WorldFlags granted = WorldFlags.None;
        var inventory = ImmutableArray.CreateBuilder<(string, int)>(def.InitialInventory.Length);

        foreach (InventorySlot slot in def.InitialInventory)
        {
            ItemDef item = data.Items[slot.Item];

            inventory.Add((item.Id, slot.Count));
            granted |= item.Grants;
        }

        return new ArchetypeFacts(
            def,
            count,
            population,
            def.HomePoiType,
            def.WorkplacePoiType is { Length: > 0 } type ? type : null,
            sites,
            capacity,
            def.DutyHours,
            def.DutyHours.IsEmpty && !dutyActions.IsEmpty,
            dutyActions,
            byCategory.ToImmutable(),
            [.. data.Actions.Actions.Where(a => !def.Allows(a.Code)).Select(a => a.Id)],
            recipes.ToImmutable(),
            inventory.ToImmutable(),
            granted,
            MatchingInterrupts(data, def),
            data.Fallbacks?.For(def.Code));
    }

    /// <summary>문자열 id 로 사실을 읽는다. 없으면 던진다.</summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="archetypeId">아키타입 id.</param>
    /// <param name="population">인구 기준.</param>
    public static ArchetypeFacts Facts(MasterDataSet data, string archetypeId, int population = PopulationBase)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!data.Archetypes.TryGet(archetypeId, out ArchetypeDef def))
        {
            throw new ArgumentException($"아키타입 '{archetypeId}' 이 없다.", nameof(archetypeId));
        }

        return Facts(data, def, population);
    }

    /// <summary>문자열 id 로. 없으면 던진다 — 조용히 빈 카드를 내면 오타를 못 잡는다.</summary>
    public static string Render(MasterDataSet data, string archetypeId, int population = PopulationBase)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!data.Archetypes.TryGet(archetypeId, out ArchetypeDef def))
        {
            throw new ArgumentException($"아키타입 '{archetypeId}' 이 없다.", nameof(archetypeId));
        }

        return Render(data, def.Code, population);
    }

    /// <summary>code 로.</summary>
    public static string Render(MasterDataSet data, ArchetypeId archetype, int population = PopulationBase)
    {
        ArgumentNullException.ThrowIfNull(data);

        return Render(data, data.Archetypes[archetype], population);
    }

    /// <summary>
    /// 아직 저장하지 않은 아키타입 정의로 카드를 만든다. Studio의 실시간 미리보기처럼
    /// 나머지 마스터데이터는 현재 값을 쓰되 이 정의만 초안으로 바꿔 읽을 때 사용한다.
    /// </summary>
    public static string Render(MasterDataSet data, ArchetypeDef def, int population = PopulationBase)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(def);

        var sb = new StringBuilder(4 * 1024);
        ArchetypeFacts facts = Facts(data, def, population);

        Header(sb, def);
        Overview(sb, data, facts);
        Actions(sb, data, facts);
        Interrupts(sb, data, facts);
        Fallback(sb, data, def);
        Buckets(sb, data, def);

        return sb.ToString();
    }

    private static void Header(StringBuilder sb, ArchetypeDef def)
    {
        sb.Append("# ").Append(Lexicon.Archetype(def.Id))
          .Append(" (`").Append(def.Id).Append("` · code ").Append(Md.N(def.Code.Value)).AppendLine(")");
        sb.AppendLine();
        sb.AppendLine(def.Description);
        sb.AppendLine();
    }

    private static void Overview(StringBuilder sb, MasterDataSet data, ArchetypeFacts facts)
    {
        ArchetypeDef def = facts.Def;

        Md.TableHead(sb, "항목", "값", "근거");

        Md.Row(
            sb,
            "인구",
            $"{Md.N(facts.Population)} / {Md.N(facts.PopulationBase)} (weight {Md.F(def.PopulationWeight, 4)})",
            "`archetypes.json` · V5");

        Md.Row(sb, "집 · 일터", Workplace(facts), "`pois.json` · V10");
        Md.Row(sb, "근무 시간", Duty(facts), "`duty_hours` · `EventApplier`");
        Md.Row(sb, "성향", Traits(def), "`traits`");
        Md.Row(sb, "전투 가능", def.CombatCapable ? "예" : "아니오", "`combat_capable`");
        Md.Row(sb, "주력 레시피", Recipes(facts), "`items.json` recipes");
        Md.Row(sb, "기본 목표", Md.Codes(def.DefaultGoals), "`default_goals` · 서픽스에 실린다");
        Md.Row(sb, "초기 소지품", Inventory(facts), "`initial_inventory` · `grants`");
        Md.Row(sb, "폴백 플랜", Md.Code(def.FallbackPlanId), "`fallback_plans.json` · V7");

        sb.AppendLine();

        _ = data;
    }

    /// <summary>
    /// 집·일터와 <b>정원이 인구를 감당하는지</b>. V10 이 보는 것과 같은 계산이다 —
    /// 카드에서 먼저 보이면 검증 실패 전에 알아챈다.
    /// </summary>
    private static string Workplace(ArchetypeFacts facts)
    {
        string home = "`" + facts.HomeType + "`";

        if (facts.WorkplaceType is not { } workplace)
        {
            return $"{home} · 일터 없음";
        }

        bool fits = facts.WorkplaceCapacity >= facts.Population;
        string verdict =
            $"정원 합 {Md.N(facts.WorkplaceCapacity)} {(fits ? "≥" : "<")} {Md.N(facts.Population)} {Md.Mark(fits)}";

        return $"{home} · `{workplace}` ({Md.N(facts.WorkplaceSites)}곳, {verdict})";
    }

    /// <summary>
    /// 근무 시간대. <b>없으면 그 사실이 결론이다</b> — <c>OnDuty</c> 를 세우는 것은
    /// <c>duty_hours</c> 뿐이라, 비어 있는데 Guard·Patrol 을 허용하면 인지 스캔이
    /// 매 틱 이탈로 읽어 재계획 큐가 포화된다 (CLAUDE.md §7).
    /// </summary>
    private static string Duty(ArchetypeFacts facts)
    {
        if (!facts.DutyHours.IsEmpty)
        {
            return string.Join(" · ", facts.DutyHours.Select(Lexicon.Of));
        }

        return facts.DutyActionsAllowed.IsEmpty
            ? "없음 → `OnDuty` 가 서지 않는다. " + string.Join("/", DutyActionIds) + " 미허용 ✓"
            : "없음 → `OnDuty` 가 서지 않는데 " + string.Join("/", facts.DutyActionsAllowed)
              + " 을 허용한다 ✗ 인지 스캔이 매번 이탈로 읽어 재계획 큐가 포화된다";
    }

    /// <summary>
    /// <c>OnDuty</c> 를 전제로 하는 액션. <b>이 조합이 CLAUDE.md §7 의 가장 흔한 실수다</b> —
    /// <c>duty_hours</c> 없이 허용하면 플랜이 영원히 전제조건을 만족하지 못한다.
    /// </summary>
    private static readonly ImmutableArray<string> DutyActionIds = ["Guard", "Patrol"];

    private static bool Allows(MasterDataSet data, ArchetypeDef def, string actionId) =>
        data.TryGetAction(actionId, out ActionId action) && def.Allows(action);

    private static string Traits(ArchetypeDef def) =>
        $"근면 {def.Traits.Diligence} · 사교 {def.Traits.Sociability} · "
        + $"용기 {def.Traits.Courage} · 탐욕 {def.Traits.Greed}";

    private static string Recipes(ArchetypeFacts facts)
    {
        if (facts.Recipes.IsEmpty)
        {
            return "—";
        }

        var parts = new List<string>(facts.Recipes.Length);

        foreach ((string recipe, string inputs, int seconds, bool exists) in facts.Recipes)
        {
            parts.Add(exists
                ? $"{Lexicon.Item(recipe)}({(inputs.Length == 0 ? "재료 없음" : inputs)} → {Md.Seconds(seconds)})"
                : $"`{recipe}` (레시피 없음 ✗)");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>초기 소지품과 <b>그것이 세우는 시작 플래그</b>. 플랜 1스텝의 전제조건이 여기서 나온다.</summary>
    private static string Inventory(ArchetypeFacts facts)
    {
        if (facts.Inventory.IsEmpty)
        {
            return "없음";
        }

        var parts = new List<string>(facts.Inventory.Length);

        foreach ((string item, int count) in facts.Inventory)
        {
            parts.Add($"{Lexicon.Item(item)} {Md.N(count)}");
        }

        return $"{string.Join(" · ", parts)} → 시작 플래그 `{WorldFlagTable.Format(facts.StartFlags)}`";
    }

    private static void Actions(StringBuilder sb, MasterDataSet data, ArchetypeFacts facts)
    {
        sb.Append("## 허용 액션 ").Append(Md.N(facts.AllowedCount))
          .Append(" / ").AppendLine(Md.N(data.Actions.Count));
        sb.AppendLine();

        foreach ((ActionCategory category, ImmutableArray<string> allowed) in facts.AllowedByCategory)
        {
            sb.Append("- **").Append(Lexicon.Of(category)).Append("** ")
              .AppendLine(string.Join(" · ", allowed));
        }

        if (facts.Denied.Length > 0)
        {
            sb.AppendLine();
            sb.Append("미허용 ").Append(Md.N(facts.Denied.Length)).Append(": ")
              .AppendLine(string.Join(" · ", facts.Denied));
        }

        sb.AppendLine();
    }

    /// <summary>
    /// 이 아키타입이 걸릴 수 있는 인터럽트. <b>성향 조건과 전투 가능 여부만으로 판정한다</b> —
    /// 플래그 조건은 실행 중 상태라 정적으로는 알 수 없으므로 조건 자체를 같이 적는다.
    /// </summary>
    /// <summary>
    /// 성향 조건과 전투 가능 여부만으로 걸릴 수 있는 인터럽트를 고른다.
    /// 매칭 순서와 같게 정렬한다 — 우선순위 내림차순, 같으면 id 오름차순.
    /// </summary>
    public static ImmutableArray<InterruptRule> MatchingInterrupts(MasterDataSet data, ArchetypeDef def)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(def);

        var matched = new List<InterruptRule>();

        foreach (InterruptRule rule in data.Interrupts.Rules)
        {
            if (rule.CombatCapable is { } needsCombat && needsCombat != def.CombatCapable)
            {
                continue;
            }

            if (rule.Traits.Any(t => !t.Matches(def.Traits)))
            {
                continue;
            }

            matched.Add(rule);
        }

        return [.. matched.OrderByDescending(r => r.Priority).ThenBy(r => r.Id, StringComparer.Ordinal)];
    }

    private static void Interrupts(StringBuilder sb, MasterDataSet data, ArchetypeFacts facts)
    {
        sb.Append("## 걸릴 수 있는 인터럽트 ").Append(Md.N(facts.Interrupts.Length))
          .Append(" / ").AppendLine(Md.N(data.Interrupts.Count));
        sb.AppendLine();

        if (facts.Interrupts.IsEmpty)
        {
            sb.AppendLine("없다. 성향·전투 가능 조건에 하나도 맞지 않는다.");
            sb.AppendLine();
            return;
        }

        Md.TableHead(sb, "우선순위", "규칙", "언제", "무엇을");

        foreach (InterruptRule rule in facts.Interrupts)
        {
            Md.Row(
                sb,
                Md.N(rule.Priority),
                Md.Code(rule.Id),
                Md.Cell(InterruptExplain.When(rule)),
                Md.Cell(InterruptExplain.Then(data, rule)));
        }

        sb.AppendLine();
    }

    private static void Fallback(StringBuilder sb, MasterDataSet data, ArchetypeDef def)
    {
        if (data.Fallbacks?.For(def.Code) is not { } plan)
        {
            sb.AppendLine("## 폴백 하루");
            sb.AppendLine();
            sb.Append("`").Append(def.FallbackPlanId)
              .AppendLine("` 을 찾지 못했다 — `fallback_plans.json` 을 읽지 않았거나 V7 위반이다.");
            sb.AppendLine();
            return;
        }

        sb.Append("## 폴백 하루 (`").Append(def.FallbackPlanId).Append("` · ")
          .Append(plan.Loop ? "loop" : "1회").Append(" · on_step_fail: ")
          .Append(plan.OnFail.ToString().ToLowerInvariant()).AppendLine(")");
        sb.AppendLine();
        sb.Append(PlanExplain.Steps(data, plan, plan.Bucket, def.Code));
        sb.AppendLine();
    }

    /// <summary>버킷 차원. <b>개수는 마스터데이터가 정한다</b> (F-05).</summary>
    private static void Buckets(StringBuilder sb, MasterDataSet data, ArchetypeDef def)
    {
        sb.Append("## 버킷 ").Append(Md.N(BucketKey.PerArchetype))
          .Append(" = 시간대 ").Append(Md.N(BucketKey.TimeOfDayCount))
          .Append(" × 지역 ").Append(Md.N(BucketKey.RegionStateCount))
          .Append(" × 기후 ").AppendLine(Md.N(BucketKey.ClimateCount));
        sb.AppendLine();

        var first = new BucketKey(def.Code, TimeOfDay.Dawn, RegionState.Peace, Climate.Fair);

        sb.Append("첨자 ").Append(Md.N(first.ToIndex()))
          .Append(" ~ ").Append(Md.N(first.ToIndex() + BucketKey.PerArchetype - 1))
          .Append(" · 전체 ").Append(Md.N(data.Buckets.TotalKeys)).AppendLine(" 중");
        sb.AppendLine();
        sb.AppendLine("생성·핀·폴백 대체·반려 내역은 `planstore/manifest.json` 이 근거다 — 카드는 그것을 읽지 않는다.");
        sb.AppendLine();
    }
}
