using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Sim;

/// <summary>
/// 드라이런 한 번의 흔적. docs/12 §5 가 판정에 쓰는 값들.
/// </summary>
/// <param name="StalledAtStep">진행이 멈춘 스텝. -1 이면 끝까지 갔다.</param>
/// <param name="StallReason">멈춘 이유(사람이 읽는 문장). 안 멈췄으면 빈 문자열.</param>
/// <param name="GameHours">전체 소요 게임 시간.</param>
/// <param name="CycleHours">
/// <b>마지막</b> 사이클의 게임 시간. 첫 사이클은 하루 중간에서 시작하는 과도기라 짧다 —
/// "하루에 한 번 도는가"는 정상 상태(두 번째 사이클)로 봐야 한다.
/// </param>
/// <param name="MaxCycleHours">가장 긴 사이클의 게임 시간. 시간 예산(V4.TIMEOUT) 판정에 쓴다.</param>
/// <param name="StarvedResource">사이클 중 바닥난 필수 자원의 이름. 없으면 null.</param>
/// <param name="MaxPoiOscillation">같은 두 POI 를 왕복한 횟수.</param>
/// <param name="Steps">실행한 스텝 수 (사이클 합계).</param>
public readonly record struct DryRunTrace(
    int StalledAtStep,
    string StallReason,
    double GameHours,
    double CycleHours,
    double MaxCycleHours,
    string? StarvedResource,
    int MaxPoiOscillation,
    int Steps);

/// <summary>
/// 축소 인스턴스 — NPC 1마리로 플랜을 굴려 본다. docs/03 §3 4단 · docs/12 §5.
///
/// <b>여기는 이벤트 루프를 돌리지 않는다.</b> 4단이 알고 싶은 것은 "이 플랜이 시간 안에
/// 스스로 굴러가는가"이지 명령·이벤트 왕복이 아니다. 그래서 명령을 보내는 대신
/// 액션 정의(소요시간·플래그·자원)를 그대로 적용하며 게임 시간을 밀어 본다.
/// 대신 세계 상태(POI · 존 · 인벤토리)는 <see cref="SimWorld"/> 의 배열을 그대로 쓴다 —
/// 판정용 세계를 따로 만들면 그 순간 진짜 Sim 과 갈라진다.
///
/// <b>난수는 <see cref="SimOptions.Seed"/> 하나에서만 나온다</b> (docs/12 §5 · CLAUDE.md §2.3).
/// 새 <c>Random</c> 을 만들지 않는다 — 같은 플랜은 100번 돌려도 같은 판정이어야 한다.
/// </summary>
public sealed partial class SimWorld
{
    /// <summary>소요시간 지터 폭(%). 시드에서 결정론적으로 나온다.</summary>
    public const int JitterPercent = 10;

    /// <summary>왕복이 이만큼 반복되면 <c>V4.OSCILLATION</c>. docs/03 §3.</summary>
    public const int OscillationLimit = 4;

    private CompiledPlan? _plan;

    /// <summary>
    /// 드라이런용 축소 월드. NPC 1마리 · 시간 1000배속.
    /// </summary>
    public static SimWorld CreateMinimal(MasterDataSet data, BucketKey bucket, int seed)
    {
        ArgumentNullException.ThrowIfNull(data);

        // 시계를 버킷의 시간대에서 시작한다. 자정에서 시작하면 Noon 버킷의 플랜이
        // "아침까지 잔다"를 19시간으로 계산해 멀쩡한 일과가 시간 예산에 걸린다.
        (int from, int _) = data.Buckets.GameHoursOf(bucket.T);

        return new SimWorld(data, capacity: 1, new SimOptions(Seed: seed, TimeScale: 1_000))
        {
            _startHour = from,
        };
    }

    private int _startHour;

    /// <summary>드라이런 시계의 시작 시각(게임 시). 버킷의 시간대에서 나온다.</summary>
    public int StartHour => _startHour;

    /// <summary>
    /// NPC 한 마리를 집에 놓고 아키타입 기본 인벤토리를 채운다.
    /// 반환값은 NPC 첨자다 (축소 월드에서는 언제나 0).
    /// </summary>
    public int Spawn(ArchetypeId archetype)
    {
        ArchetypeDef def = _data.Archetypes[archetype];

        PoiId home = FirstOfType(PoiType.Home, archetype);
        Place(0, archetype, home);
        _spawned[0] = true;

        Span<int> inventory = InventoryOf(0);
        inventory.Clear();

        foreach (InventorySlot slot in def.InitialInventory)
        {
            inventory[slot.Item.Value] = slot.Count;
        }

        return 0;
    }

    /// <summary>이 NPC 가 돌릴 플랜.</summary>
    public void AssignPlan(int npc, CompiledPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNotEqual(npc, 0);

        _plan = plan;
    }

    /// <summary>
    /// 플랜을 <paramref name="cycles"/> 번 굴린다. 게임 시간이 <paramref name="maxGameHours"/> 를
    /// 넘으면 거기서 멈추고 흔적을 돌려준다.
    /// </summary>
    public DryRunTrace RunCycles(int maxGameHours, int cycles)
    {
        CompiledPlan plan = _plan ?? throw new InvalidOperationException("AssignPlan 을 먼저 불러야 한다.");

        // 이 플랜이 스스로 대는 자원(채집·제작). 그 밖의 것은 집·일터의 보관함에서 온다고 본다 —
        // 안 그러면 석탄을 캐지 않는 대장장이도, 물을 긷지 않는 누구도 전부 4단에서 걸린다.
        // 3단이 "플랜이 스스로 모으기도 하고 쓰기도 한 자원만 본다"고 한 것과 같은 범위다 (docs/03 §3).
        HashSet<int> selfSupplied = SelfSuppliedItems(plan);

        ArchetypeId archetype = ArchetypeOf(0);
        WorldFlags state = _dryRunFlags;
        Span<int> inventory = InventoryOf(0);

        double seconds = 0;
        double cycleSeconds = 0;
        double maxCycleSeconds = 0;
        int steps = 0;
        int oscillation = 0;

        // 왕복 검출 — 직전과 그 전의 POI 를 기억한다. A→B→A→B 가 반복되면 왕복이다.
        PoiId previous = default;
        PoiId beforePrevious = default;

        for (int cycle = 0; cycle < cycles; cycle++)
        {
            double cycleStart = seconds;

            for (int i = 0; i < plan.Steps.Length; i++)
            {
                CompiledStep step = plan.Steps[i];
                ActionDef action = _data.Actions[step.Action];
                StepFlags flags = plan.FlagsOf(i);

                if (!flags.IsSatisfiedBy(state))
                {
                    return Stalled(
                        i,
                        $"{action.Id} cannot run: requires {WorldFlagTable.Format(flags.Requires & ~state)}",
                        seconds,
                        cycleSeconds,
                        oscillation,
                        steps);
                }

                PoiId target = PoiOf(0);

                if (step.Poi != PoiSymbol.None)
                {
                    if (!TryBind(step.Poi, archetype, out target))
                    {
                        return Stalled(
                            i,
                            $"{action.Id} targets {PoiSymbols.ToText(step.Poi)}, which cannot be bound",
                            seconds,
                            cycleSeconds,
                            oscillation,
                            steps);
                    }
                }

                if (!TryConsume(action, step, inventory, selfSupplied, out string? starved))
                {
                    return new DryRunTrace(
                        -1, string.Empty, seconds / 3600.0, cycleSeconds / 3600.0, maxCycleSeconds / 3600.0,
                        starved, oscillation, steps);
                }

                double duration = DurationSeconds(action, step, PoiOf(0), target, (_startHour * 3600.0) + seconds, i);

                seconds += duration;
                cycleSeconds = seconds - cycleStart;
                maxCycleSeconds = Math.Max(maxCycleSeconds, cycleSeconds);
                steps++;

                // 한 사이클이 예산의 두 배를 넘어가면 더 굴려 봐야 결론이 같다.
                if (cycleSeconds / 3600.0 > maxGameHours * 2)
                {
                    return new DryRunTrace(
                        -1, string.Empty, seconds / 3600.0, cycleSeconds / 3600.0, maxCycleSeconds / 3600.0,
                        null, oscillation, steps);
                }

                // --- 효과 적용 ---
                Produce(action, step, inventory);

                WorldFlags grants = flags.Grants;

                if (action.CompletesOn.Contains(GameEventKind.NpcArrived))
                {
                    grants |= CoherenceGrantsOf(step.Poi);

                    if (target.Value != 0 && target != PoiOf(0))
                    {
                        beforePrevious = previous;
                        previous = PoiOf(0);
                        MoveTo(0, target);

                        // A → B → A 로 돌아온 것이 왕복 한 번이다.
                        if (beforePrevious.Value != 0 && beforePrevious == target)
                        {
                            oscillation++;
                        }
                    }
                }

                state = (state & ~flags.Clears) | grants;

                // 인벤토리가 세우는 플래그는 데이터에서 다시 계산한다 —
                // 액션의 clears 만 믿으면 "빵 2개 중 1개를 먹었는데 HasFood 가 내려가는" 일이 생긴다.
                state = (state & ~_data.Items.AllGrants) | _data.Items.ComputeFlags(inventory);
            }
        }

        return new DryRunTrace(
            -1, string.Empty, seconds / 3600.0, cycleSeconds / 3600.0, maxCycleSeconds / 3600.0,
            null, oscillation, steps);
    }

    /// <summary>드라이런의 시작 플래그. <see cref="RunCycles"/> 전에 넣는다.</summary>
    public WorldFlags DryRunInitialFlags
    {
        get => _dryRunFlags;
        set => _dryRunFlags = value;
    }

    private WorldFlags _dryRunFlags;

    private static DryRunTrace Stalled(
        int step, string reason, double seconds, double cycleSeconds, int oscillation, int steps) =>
        new(step, reason, seconds / 3600.0, cycleSeconds / 3600.0, cycleSeconds / 3600.0, null, oscillation, steps);

    /// <summary>POI 심볼이 함의하는 장소 플래그. 3단과 같은 표를 쓴다.</summary>
    private static WorldFlags CoherenceGrantsOf(PoiSymbol symbol) =>
        Npc.Core.Validation.CoherenceValidator.GrantsOf(symbol);

    /// <summary>심볼을 실제 POI 로 바인딩한다. 런타임 <c>PoiBinder</c> 와 같은 규칙이다.</summary>
    private bool TryBind(PoiSymbol symbol, ArchetypeId archetype, out PoiId poi)
    {
        ArchetypeDef def = _data.Archetypes[archetype];

        switch (symbol)
        {
            case PoiSymbol.Workplace when def.WorkplacePoiType is { } subtype:
                poi = FirstOfSubtype(subtype);
                return poi.Value != 0;

            case PoiSymbol.Workplace:
                poi = default;
                return false;

            case PoiSymbol.Home or PoiSymbol.NearestShelter:
                poi = FirstOfType(PoiType.Home, archetype);
                return poi.Value != 0;

            case PoiSymbol.Market:
                poi = Nearest(PoiType.Market, archetype);
                return poi.Value != 0;

            case PoiSymbol.Tavern:
                poi = Nearest(PoiType.Tavern, archetype);
                return poi.Value != 0;

            case PoiSymbol.Temple:
                poi = Nearest(PoiType.Temple, archetype);
                return poi.Value != 0;

            case PoiSymbol.Gate or PoiSymbol.NearestSafe:
                poi = Nearest(PoiType.Gate, archetype);
                if (poi.Value == 0)
                {
                    poi = FirstOfType(PoiType.Home, archetype);
                }

                return poi.Value != 0;

            case PoiSymbol.NearestField:
                poi = Nearest(PoiType.Field, archetype);
                return poi.Value != 0;

            default:
                poi = PoiOf(0);
                return true;
        }
    }

    private PoiId FirstOfType(PoiType type, ArchetypeId archetype)
    {
        foreach (PoiId id in _data.Pois.OfType(type))
        {
            if (_data.Pois[id].CanEnter(archetype))
            {
                return id;
            }
        }

        return default;
    }

    private PoiId FirstOfSubtype(string subtype)
    {
        ImmutableArray<PoiId> list = _data.Pois.OfSubtype(subtype);

        return list.Length > 0 ? list[0] : default;
    }

    /// <summary>지금 위치에서 가장 가까운 그 타입의 POI. 거리 행렬 조회뿐이라 싸다.</summary>
    private PoiId Nearest(PoiType type, ArchetypeId archetype)
    {
        PoiId from = PoiOf(0);
        PoiId best = default;
        float bestDistance = float.PositiveInfinity;

        foreach (PoiId id in _data.Pois.OfType(type))
        {
            if (!_data.Pois[id].CanEnter(archetype))
            {
                continue;
            }

            float distance = from.Value == 0 ? 0 : _data.Pois.Distance(from, id);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = id;
            }
        }

        return best;
    }

    /// <summary>
    /// 이 스텝의 소요 시간(게임 초). 액션의 <c>duration</c> 정의를 그대로 쓴다.
    /// 지터는 시드에서 나온다 — 여기서 <c>Random</c> 을 만들면 판정이 흔들린다.
    /// </summary>
    private double DurationSeconds(
        ActionDef action, in CompiledStep step, PoiId from, PoiId to, double elapsedSeconds, int stepIndex)
    {
        double seconds = action.Duration.Kind switch
        {
            DurationKind.Fixed => action.Duration.BaseSeconds,
            DurationKind.Distance => Travel(action, from, to),
            DurationKind.Param => step.Count > 0 ? step.Count : action.Duration.BaseSeconds,
            DurationKind.UntilTime => UntilTime(action, step, elapsedSeconds),
            _ => action.Duration.BaseSeconds,
        };

        if (seconds <= 0)
        {
            seconds = 1;
        }

        // "아침까지 잔다"는 아침에 일어난다는 뜻이다. 여기에 지터를 주면 하루가 24시간이
        // 아니게 되고, 오차가 사이클마다 누적돼 멀쩡한 일과가 V4.INFINITE_LOOP 으로 걸린다.
        if (action.Duration.Kind == DurationKind.UntilTime)
        {
            return seconds;
        }

        // ±JitterPercent 를 결정론 해시로. 같은 (시드, 스텝)이면 언제나 같은 값이다.
        int span = JitterPercent;
        int jitter = (int)(PlanHash.Mix((uint)Options.Seed ^ (uint)(stepIndex * 2654435761u)) % (uint)((span * 2) + 1))
            - span;

        return seconds * (1.0 + (jitter / 100.0));
    }

    private double Travel(ActionDef action, PoiId from, PoiId to)
    {
        if (from.Value == 0 || to.Value == 0 || from == to)
        {
            return action.Duration.BaseSeconds;
        }

        float distance = _data.Pois.Distance(from, to);

        if (float.IsInfinity(distance))
        {
            return action.Duration.BaseSeconds;
        }

        return action.Duration.BaseSeconds + (distance * action.Duration.PerMeterSeconds);
    }

    /// <summary>지정한 시간대가 될 때까지. 하루를 넘기면 다음 날 그 시각이다.</summary>
    private double UntilTime(ActionDef action, in CompiledStep step, double elapsedSeconds)
    {
        ParamDef? param = action.Param(action.Duration.Param ?? string.Empty);

        if (param is null || step.ArgFlags >= param.EnumValues.Length)
        {
            return action.Duration.BaseSeconds;
        }

        if (!Enum.TryParse(param.EnumValues[step.ArgFlags], out TimeOfDay target))
        {
            return action.Duration.BaseSeconds;
        }

        (int from, int _) = _data.Buckets.GameHoursOf(target);

        double nowHours = elapsedSeconds / 3600.0 % 24.0;
        double waitHours = from - nowHours;

        if (waitHours <= 0)
        {
            waitHours += 24;
        }

        return waitHours * 3600.0;
    }

    /// <summary>
    /// 이 스텝이 자원을 쓴다면 인벤토리에서 뺀다. 모자라면 false 와 자원 이름을 준다.
    /// 레시피 입력과, 인벤토리 플래그를 내리는 액션(먹기·마시기 등)을 본다.
    /// </summary>
    private bool TryConsume(
        ActionDef action, in CompiledStep step, Span<int> inventory, HashSet<int> selfSupplied, out string? starved)
    {
        starved = null;
        int count = step.Count > 0 ? step.Count : 1;

        if (action.Param("recipe") is not null
            && step.Item.Value != 0
            && _data.Items.TryGetRecipe(_data.Items[step.Item].Id, out RecipeDef recipe))
        {
            foreach (RecipeSlot slot in recipe.Inputs)
            {
                int need = slot.Count * count;

                // 플랜이 스스로 대는 자원만 엄격하게 본다. 나머지는 보관함에서 온다.
                if (selfSupplied.Contains(slot.Item.Value) && inventory[slot.Item.Value] < need)
                {
                    starved = _data.Items[slot.Item].Id;
                    return false;
                }
            }

            foreach (RecipeSlot slot in recipe.Inputs)
            {
                // 보관함에서 오는 재료는 깎지 않는다. 깎으면 물 한 병으로 물약을 빚은 연금술사가
                // 다음 스텝에서 마실 물이 없어진다 — 그건 플랜의 결함이 아니라 모델의 결함이다.
                if (!selfSupplied.Contains(slot.Item.Value))
                {
                    continue;
                }

                inventory[slot.Item.Value] = Math.Max(0, inventory[slot.Item.Value] - (slot.Count * count));
            }

            return true;
        }

        // 인벤토리 플래그를 내리는 액션은 그 플래그를 세우는 아이템을 쓴다 (Eat · Drink · Store …).
        WorldFlags consumedFlags = action.Clears & _data.Items.AllGrants;

        if (consumedFlags == WorldFlags.None)
        {
            return true;
        }

        for (int i = 0; i < WorldFlagTable.Values.Length; i++)
        {
            WorldFlags flag = WorldFlagTable.Values[i];

            if ((consumedFlags & flag) == 0)
            {
                continue;
            }

            // 플랜이 스스로 대지 않는 소모품(빵·물 …)은 보관함에서 다시 채운다고 본다.
            // 여기서 깎으면 loop 플랜이 두 번째 날에 전부 굶어 죽는다 — 그건 플랜의 결함이 아니다.
            if (!SuppliesFlag(selfSupplied, flag))
            {
                continue;
            }

            if (!TrySpend(inventory, flag, count))
            {
                starved = WorldFlagTable.Names[i];
                return false;
            }
        }

        return true;
    }

    /// <summary>플랜이 스스로 대는 아이템 중 그 플래그를 세우는 것이 있는가.</summary>
    private bool SuppliesFlag(HashSet<int> selfSupplied, WorldFlags flag)
    {
        foreach (int code in selfSupplied)
        {
            if ((_data.Items[new ItemId((ushort)code)].Grants & flag) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 그 플래그를 세우는 아이템을 code 오름차순으로 최대 <paramref name="count"/> 개 쓴다.
    /// 하나도 없을 때만 false 다 — "3개 보관하려 했는데 2개뿐"은 고갈이 아니다.
    /// </summary>
    private bool TrySpend(Span<int> inventory, WorldFlags flag, int count)
    {
        int remaining = count;

        foreach (ItemDef item in _data.Items.Items)
        {
            if ((item.Grants & flag) == 0 || inventory[item.Code.Value] <= 0)
            {
                continue;
            }

            int spend = Math.Min(remaining, inventory[item.Code.Value]);
            inventory[item.Code.Value] -= spend;
            remaining -= spend;

            if (remaining == 0)
            {
                break;
            }
        }

        return remaining < count;
    }

    /// <summary>이 플랜이 스스로 대는 아이템의 code 집합 — 채집물과 제작 산출물.</summary>
    private HashSet<int> SelfSuppliedItems(CompiledPlan plan)
    {
        var items = new HashSet<int>();

        foreach (CompiledStep step in plan.Steps)
        {
            ActionDef action = _data.Actions[step.Action];

            if (step.Item.Value == 0)
            {
                continue;
            }

            if (action.Param("resource") is not null || action.Param("crop") is not null)
            {
                items.Add(step.Item.Value);
                continue;
            }

            if (action.Param("recipe") is not null
                && _data.Items.TryGetRecipe(_data.Items[step.Item].Id, out RecipeDef recipe))
            {
                foreach (RecipeSlot slot in recipe.Outputs)
                {
                    items.Add(slot.Item.Value);
                }
            }
        }

        return items;
    }

    /// <summary>채집·제작 산출물을 인벤토리에 넣는다.</summary>
    private void Produce(ActionDef action, in CompiledStep step, Span<int> inventory)
    {
        int count = step.Count > 0 ? step.Count : 1;

        if (action.Param("recipe") is not null
            && step.Item.Value != 0
            && _data.Items.TryGetRecipe(_data.Items[step.Item].Id, out RecipeDef recipe))
        {
            foreach (RecipeSlot slot in recipe.Outputs)
            {
                inventory[slot.Item.Value] += slot.Count * count;
            }

            return;
        }

        bool gathers = action.Param("resource") is not null || action.Param("crop") is not null;

        if (gathers && step.Item.Value != 0)
        {
            inventory[step.Item.Value] += count;
            return;
        }

        // 수령 액션(Withdraw · PickUp)은 보관함에서 물건을 꺼내 온다.
        // 3단이 이걸 인정하는데(ItemGrants) 4단이 안 하면, 재료를 꺼내 만드는 멀쩡한 플랜이
        // 3단을 통과하고 4단에서 V4.DEADLOCK 으로 걸린다 — 두 단이 어긋나면 안 된다.
        bool receives = (action.Forbids & WorldFlags.InventoryFull) != 0 && action.Param("item") is not null;

        if (receives && step.Item.Value != 0)
        {
            inventory[step.Item.Value] += count;
        }
    }
}
