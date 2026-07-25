using Npc.Contracts;
using Npc.Core;
using Npc.MasterData;

namespace Npc.Runtime;

/// <summary>
/// 게임서버 이벤트를 월드 뷰에 반영한다. docs/02 §3.3 · N6 · N7.
///
/// <b>멱등이다 (N7).</b> 같은 이벤트를 두 번 받아도 상태 해시가 같다.
/// 시퀀스가 건너뛰면 갭을 세어 경보로 올린다 (N6) — 유실을 조용히 넘기면 NPC 가 영원히 멈춘다.
///
/// <b>인벤토리 → 플래그 재계산은 items.json 의 grants 만 본다</b> (docs/01 §3).
/// "빵을 가지면 HasFood" 같은 규칙이 여기 하드코딩돼 있으면 안 된다.
/// </summary>
public sealed class EventApplier
{
    /// <summary>POI 에 있을 때 서는 장소 플래그 전체. 이동하면 이 비트를 통째로 지운다.</summary>
    public const WorldFlags LocationFlags =
        WorldFlags.AtHome | WorldFlags.AtWorkplace | WorldFlags.AtMarket | WorldFlags.AtTavern
        | WorldFlags.AtTemple | WorldFlags.AtGate | WorldFlags.AtField | WorldFlags.InWilderness;

    /// <summary>시간대 플래그. 상호 배타다 (world_flags.json 의 exclusive_groups).</summary>
    public const WorldFlags TimeFlags =
        WorldFlags.IsDawn | WorldFlags.IsDay | WorldFlags.IsEvening | WorldFlags.IsNight;

    /// <summary>지역 상태 플래그.</summary>
    public const WorldFlags RegionFlags = WorldFlags.RegionPeaceful | WorldFlags.RegionUnderAttack;

    /// <summary>HP 가 이 비율 아래면 IsInjured. docs/01 §1.</summary>
    public const int InjuredPercent = 50;

    /// <summary>스태미나가 이 비율 아래면 IsExhausted. docs/01 §1.</summary>
    public const int ExhaustedPercent = 20;

    /// <summary>LOD 0 이 되는 플레이어 거리(m). docs/11 §4.</summary>
    public const int LodZeroDistance = 50;

    /// <summary>LOD 1 이 되는 플레이어 거리(m). docs/11 §4.</summary>
    public const int LodOneDistance = 200;

    /// <summary>기억에 남길 이벤트의 중요도. 0 이면 기억하지 않는다.</summary>
    private static readonly byte[] s_salience = BuildSalience();

    private readonly MasterDataSet _data;
    private readonly NpcStore _store;
    private readonly GameClock _clock;
    private readonly CorrelationTable _correlations;

    private long _expectedSequence = -1;

    /// <summary>적용기를 만든다. 기동 시 1회.</summary>
    public EventApplier(MasterDataSet data, NpcStore store, GameClock clock, CorrelationTable correlations)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(correlations);

        _data = data;
        _store = store;
        _clock = clock;
        _correlations = correlations;
    }

    /// <summary>처리한 이벤트 수.</summary>
    public long EventsApplied { get; private set; }

    /// <summary>중복이라 건너뛴 이벤트 수 (N7).</summary>
    public long DuplicatesIgnored { get; private set; }

    /// <summary>검출한 시퀀스 갭의 총 개수 (N6).</summary>
    public long GapsDetected { get; private set; }

    /// <summary>낡은 상관 ID 라 무시한 응답 수 (docs/02 §3.4).</summary>
    public long StaleResponsesIgnored { get; private set; }

    /// <summary>
    /// 이벤트 하나를 반영한다. <b>할당 0.</b> 틱 루프의 배수 구간에서 돈다.
    /// </summary>
    public void Apply(in GameEvent ev)
    {
        // --- N6 · N7: 시퀀스 판정 ---
        if (_expectedSequence >= 0)
        {
            if (ev.Sequence < _expectedSequence)
            {
                // 재전송이거나 재정렬. 이미 반영했다.
                DuplicatesIgnored++;
                return;
            }

            if (ev.Sequence > _expectedSequence)
            {
                GapsDetected += ev.Sequence - _expectedSequence;
            }
        }

        _expectedSequence = ev.Sequence + 1;
        EventsApplied++;

        switch (ev.Kind)
        {
            case GameEventKind.TickSync:
                _clock.SyncTo(ev.OccurredAt);
                return;

            case GameEventKind.GameTimeChanged:
                ApplyTimeOfDay((TimeOfDay)ev.Code);
                return;

            case GameEventKind.ZoneStateChanged:
                ApplyZoneState(ev.Zone, (RegionState)ev.Code);
                return;

            case GameEventKind.WeatherChanged:
                ApplyWeather(ev.Zone, (Climate)ev.Code);
                return;

            default:
                break;
        }

        int npc = ev.Npc.Value;
        if ((uint)npc >= (uint)_store.Count)
        {
            return;
        }

        // NPC 별 중복 방어. 링크가 재전송하면 시퀀스가 앞서 있을 수 있다.
        if (ev.Sequence <= _store.LastEventSequence[npc])
        {
            DuplicatesIgnored++;
            return;
        }

        _store.LastEventSequence[npc] = ev.Sequence;

        switch (ev.Kind)
        {
            case GameEventKind.NpcSpawned:
                _store.Pos[npc] = ev.Pos;
                if (ev.Poi.Value != 0)
                {
                    MoveTo(npc, ev.Poi);
                }

                _store.StepStatus[npc] = (byte)StepStatus.Ready;
                break;

            case GameEventKind.NpcDespawned:
                _store.StepStatus[npc] = (byte)StepStatus.Unspawned;
                break;

            case GameEventKind.NpcTransform:
                _store.Pos[npc] = ev.Pos;
                break;

            case GameEventKind.NpcArrived:
                MoveTo(npc, ev.Poi);
                CompleteStep(npc, ev.Correlation);
                break;

            case GameEventKind.NpcActionCompleted:
                CompleteStep(npc, ev.Correlation);
                break;

            case GameEventKind.NpcActionFailed:
                FailStep(npc, ev.Correlation, (ActionFailReason)ev.Code);
                break;

            case GameEventKind.NpcVitalsChanged:
                ApplyVitals(npc, ev.Hp, ev.Stamina);
                break;

            case GameEventKind.NpcInventoryChanged:
                ApplyInventory(npc, ev.Item, ev.Amount);
                CompleteStep(npc, ev.Correlation);
                break;

            case GameEventKind.PlayerProximity:
                ApplyProximity(npc, (ProximityChange)ev.Code, ev.Amount);
                break;

            case GameEventKind.PlayerInteracted:
                _store.Flags[npc] |= WorldFlags.PlayerNearby;
                break;

            case GameEventKind.CombatStarted:
                _store.Flags[npc] |= WorldFlags.InCombat | WorldFlags.ThreatNearby;
                break;

            case GameEventKind.CombatEnded:
                _store.Flags[npc] &= ~(WorldFlags.InCombat | WorldFlags.ThreatNearby);
                break;

            case GameEventKind.DamageTaken:
                _store.Flags[npc] |= WorldFlags.InCombat | WorldFlags.ThreatNearby;
                break;

            default:
                break;
        }

        Remember(npc, in ev);
    }

    /// <summary>스폰 전 초기 상태를 심는다. 시퀀스 판정을 거치지 않는다.</summary>
    public void Seed(int npc, PoiId poi, ZoneId zone, ArchetypeId archetype, PoiId home, PoiId workplace)
    {
        _store.ArchetypeCode[npc] = archetype.Value;
        _store.HomePoi[npc] = home.Value;
        _store.WorkPoi[npc] = workplace.Value;
        _store.ZoneCode[npc] = zone.Value;

        ArchetypeDef def = _data.Archetypes[archetype];
        Span<int> inventory = _store.InventoryOf(npc);

        foreach (InventorySlot slot in def.InitialInventory)
        {
            if (slot.Item.Value < inventory.Length)
            {
                inventory[slot.Item.Value] = slot.Count;
            }
        }

        MoveTo(npc, poi);
        RecomputeItemFlags(npc);
        _store.Flags[npc] |= _clock.TimeOfDay switch
        {
            TimeOfDay.Dawn => WorldFlags.IsDawn,
            TimeOfDay.Evening => WorldFlags.IsEvening,
            TimeOfDay.Night => WorldFlags.IsNight,
            _ => WorldFlags.IsDay,
        };
        _store.Flags[npc] |= WorldFlags.RegionPeaceful | WorldFlags.IsRested;
    }

    // ---------------------------------------------------------------- 개별 반영

    private void MoveTo(int npc, PoiId poi)
    {
        if (poi.Value == 0)
        {
            return;
        }

        PoiDef def = _data.Pois[poi];

        _store.CurrentPoi[npc] = poi.Value;
        _store.ZoneCode[npc] = def.Zone.Value;
        _store.Pos[npc] = def.Pos;

        // 장소 플래그는 POI 가 정한다. 코드가 아는 것은 "장소 플래그 집합" 뿐이다.
        _store.Flags[npc] = (_store.Flags[npc] & ~LocationFlags) | def.Grants;
    }

    private void CompleteStep(int npc, CorrelationId correlation)
    {
        if (_correlations.IsStale(npc, correlation))
        {
            StaleResponsesIgnored++;
            return;
        }

        _correlations.Invalidate(npc);
        _store.StepStatus[npc] = (byte)StepStatus.Completed;
    }

    private void FailStep(int npc, CorrelationId correlation, ActionFailReason reason)
    {
        if (_correlations.IsStale(npc, correlation))
        {
            StaleResponsesIgnored++;
            return;
        }

        _correlations.Invalidate(npc);
        _store.StepStatus[npc] = (byte)StepStatus.Failed;
        _store.LastFailReason[npc] = (byte)reason;
    }

    private void ApplyVitals(int npc, short hp, short stamina)
    {
        _store.Hp[npc] = hp;
        _store.Stamina[npc] = stamina;

        WorldFlags flags = _store.Flags[npc] & ~(WorldFlags.IsInjured | WorldFlags.IsExhausted);

        if (hp < InjuredPercent)
        {
            flags |= WorldFlags.IsInjured;
        }

        if (stamina < ExhaustedPercent)
        {
            flags |= WorldFlags.IsExhausted;
        }

        _store.Flags[npc] = flags;
    }

    private void ApplyInventory(int npc, ItemId item, int amount)
    {
        Span<int> inventory = _store.InventoryOf(npc);

        if ((uint)item.Value >= (uint)inventory.Length)
        {
            return;
        }

        inventory[item.Value] = Math.Max(0, inventory[item.Value] + amount);
        RecomputeItemFlags(npc);
    }

    /// <summary>인벤토리에서 오는 플래그만 다시 계산한다. 다른 출처의 플래그는 건드리지 않는다.</summary>
    private void RecomputeItemFlags(int npc)
    {
        WorldFlags fromItems = _data.Items.ComputeFlags(_store.ReadInventoryOf(npc));

        _store.Flags[npc] = (_store.Flags[npc] & ~_data.Items.AllGrants) | fromItems;
    }

    private void ApplyProximity(int npc, ProximityChange change, int distance)
    {
        if (change == ProximityChange.Leave)
        {
            _store.Flags[npc] &= ~WorldFlags.PlayerNearby;

            // docs/11 §4 — 존 활성도에 따라 2 또는 3. 평시 존은 비활성으로 내린다.
            // 여기서 3 으로 안 내리면 한 번이라도 플레이어를 만난 NPC 가 영원히 스캔 대상으로 남고,
            // NPC 를 늘릴수록 틱당 스캔이 선형으로 자란다.
            bool active = (_store.Flags[npc] & WorldFlags.RegionUnderAttack) != 0;
            _store.Lod[npc] = active ? (byte)2 : NpcStore.InactiveLod;
            return;
        }

        _store.Flags[npc] |= WorldFlags.PlayerNearby;
        _store.Lod[npc] = distance switch
        {
            < LodZeroDistance => (byte)0,
            < LodOneDistance => (byte)1,
            _ => (byte)2,
        };
    }

    private void ApplyTimeOfDay(TimeOfDay time)
    {
        WorldFlags set = time switch
        {
            TimeOfDay.Dawn => WorldFlags.IsDawn,
            TimeOfDay.Evening => WorldFlags.IsEvening,
            TimeOfDay.Night => WorldFlags.IsNight,
            _ => WorldFlags.IsDay,
        };

        for (int i = 0; i < _store.Count; i++)
        {
            _store.Flags[i] = (_store.Flags[i] & ~TimeFlags) | set;
        }
    }

    private void ApplyZoneState(ZoneId zone, RegionState state)
    {
        WorldFlags set = _data.Buckets.FlagsOf(state);

        for (int i = 0; i < _store.Count; i++)
        {
            if (_store.ZoneCode[i] != zone.Value)
            {
                continue;
            }

            _store.Flags[i] = (_store.Flags[i] & ~RegionFlags) | set;

            // 지역이 공격받으면 그 존 전체를 LOD 1 로 승격한다 (docs/11 §4).
            if (state is RegionState.War or RegionState.Disaster && _store.Lod[i] > 1)
            {
                _store.Lod[i] = 1;
            }
        }
    }

    private void ApplyWeather(ZoneId zone, Climate climate)
    {
        WorldFlags set = _data.Buckets.FlagsOf(climate);

        for (int i = 0; i < _store.Count; i++)
        {
            if (_store.ZoneCode[i] != zone.Value)
            {
                continue;
            }

            _store.Flags[i] = (_store.Flags[i] & ~WorldFlags.WeatherHarsh) | set;
        }
    }

    private void Remember(int npc, in GameEvent ev)
    {
        byte salience = s_salience[(int)ev.Kind];

        if (salience == 0)
        {
            return;
        }

        int subject = ev.Kind switch
        {
            GameEventKind.PlayerProximity or GameEventKind.PlayerInteracted => ev.Player.Value,
            GameEventKind.NpcInventoryChanged => ev.Item.Value,
            GameEventKind.NpcArrived => ev.Poi.Value,
            _ => ev.OtherNpc.Value,
        };

        _store.Recent[npc].Add(new RecentEvent(ev.Kind, ev.OccurredAt, subject, salience));
    }

    /// <summary>
    /// 이벤트 종류별 기억 중요도. 0 = 기억하지 않는다.
    /// NpcTransform 은 최다 이벤트라 기억에 넣으면 8슬롯이 그것만으로 찬다 (docs/11 §12).
    /// </summary>
    private static byte[] BuildSalience()
    {
        var table = new byte[Enum.GetValues<GameEventKind>().Length + 1];

        table[(int)GameEventKind.DamageTaken] = 220;
        table[(int)GameEventKind.CombatStarted] = 200;
        table[(int)GameEventKind.PlayerInteracted] = 180;
        table[(int)GameEventKind.CombatEnded] = 140;
        table[(int)GameEventKind.NpcActionFailed] = 120;
        table[(int)GameEventKind.PlayerProximity] = 80;
        table[(int)GameEventKind.NpcInventoryChanged] = 50;
        table[(int)GameEventKind.NpcActionCompleted] = 40;
        table[(int)GameEventKind.NpcArrived] = 30;
        table[(int)GameEventKind.NpcVitalsChanged] = 20;

        return table;
    }
}
