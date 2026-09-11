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

    /// <summary>LOD 0 이 되는 플레이어 거리(m). 규칙 본체는 <see cref="LodUpdater"/> 다 (T4-14).</summary>
    public const int LodZeroDistance = LodUpdater.LodZeroDistance;

    /// <summary>LOD 1 이 되는 플레이어 거리(m). 규칙 본체는 <see cref="LodUpdater"/> 다 (T4-14).</summary>
    public const int LodOneDistance = LodUpdater.LodOneDistance;

    /// <summary>기억에 남길 이벤트의 중요도. 0 이면 기억하지 않는다.</summary>
    private static readonly byte[] s_salience = BuildSalience();

    private readonly MasterDataSet _data;
    private readonly NpcStore _store;
    private readonly GameClock _clock;
    private readonly CorrelationTable _correlations;
    private readonly LodUpdater _lod;

    private long _expectedSequence = -1;

    /// <summary>적용기를 만든다. 기동 시 1회.</summary>
    /// <param name="data">마스터데이터.</param>
    /// <param name="store">NPC 상태.</param>
    /// <param name="clock">게임 시계.</param>
    /// <param name="correlations">상관 ID 표.</param>
    /// <param name="lod">
    /// LOD 등급 갱신기 (T4-14). 없으면 여기서 하나 만든다 —
    /// 등급 규칙은 한 곳에만 있어야 하고, 호스트는 계측을 위해 같은 인스턴스를 넘긴다.
    /// </param>
    public EventApplier(
        MasterDataSet data,
        NpcStore store,
        GameClock clock,
        CorrelationTable correlations,
        LodUpdater? lod = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(correlations);

        _data = data;
        _store = store;
        _clock = clock;
        _correlations = correlations;
        _lod = lod ?? new LodUpdater(store);
    }

    /// <summary>LOD 등급 갱신기. 대시보드가 승격·강등 수를 읽는다.</summary>
    public LodUpdater Lod => _lod;

    /// <summary>처리한 이벤트 수.</summary>
    public long EventsApplied { get; private set; }

    /// <summary>중복이라 건너뛴 이벤트 수 (N7).</summary>
    public long DuplicatesIgnored { get; private set; }

    /// <summary>검출한 시퀀스 갭의 총 개수 (N6).</summary>
    public long GapsDetected { get; private set; }

    /// <summary>낡은 상관 ID 라 무시한 응답 수 (docs/02 §3.4).</summary>
    public long StaleResponsesIgnored { get; private set; }

    /// <summary>
    /// 런타임 스폰·디스폰을 받는 곳 (B-05). <b>null 이면 정적 로스터다</b> —
    /// 그때는 스폰이 슬롯을 새로 배정하지 않고 오늘처럼 상태만 <c>Ready</c> 로 바꾼다.
    ///
    /// <para>
    /// <b>속성으로 둔 이유는 조립 순서다.</b> 이 적용기는 <c>PlanExecutor</c> 보다 먼저
    /// 만들어지는데 동적 로스터는 실행기를 필요로 한다.
    /// </para>
    /// </summary>
    public IDynamicRoster? Roster { get; set; }

    /// <summary>동적 로스터가 스폰을 거절한 횟수 (B-05). <b>0 이 아니면 경보다.</b></summary>
    public long RosterRejections { get; private set; }

    /// <summary>
    /// 모르는 전역 <c>NpcId</c> 로 온 이벤트 수 (A-08).
    ///
    /// <b>0 이 아니면 게임서버가 우리 샤드가 아닌 NPC 를 보내고 있다.</b> 라우팅이 틀렸거나
    /// 로스터가 어긋난 것이고, 둘 다 조용히 넘기면 안 된다 — 넘기면 "게임서버에는 있는데
    /// 우리에겐 없는 NPC" 가 아무 흔적 없이 생긴다.
    /// </summary>
    public long UnknownNpcEvents { get; private set; }

    /// <summary>
    /// 적대 판정을 반영한 횟수 (B-06). <b>0 이면 적대 경로가 한 번도 안 돌았다</b> —
    /// 게임서버가 <c>PlayerHostility</c> 를 안 내고 있다는 뜻이다.
    /// </summary>
    public long HostilityChanges { get; private set; }

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

        // ── A-08: 전역 id → 슬롯 ─────────────────────────────────
        //
        // <b>와이어의 NpcId 는 npc_instances.json 의 id 다.</b> 슬롯은 우리 안쪽 사정이라
        // 게임서버가 알 이유가 없다. 배열 조회 한 번이라 할당 0 이다.
        int npc = _store.SlotOf(ev.Npc.Value);

        if (npc < 0 && !TrySeat(in ev, out npc))
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
                // A-08 — 자리 잡기는 위의 TrySeat 가 이미 했다. 여기 오는 스폰은
                // <b>이미 앉아 있는 NPC 의 재스폰</b>이므로 위치·상태만 갱신한다 (N7 멱등).
                _store.Pos[npc] = ev.Pos;
                if (ev.Poi.Value != 0)
                {
                    MoveTo(npc, ev.Poi);
                }

                // 인스턴스는 스폰이 정한다 (B-02). 이후 그 NPC 로 나가는 명령에 그대로 찍힌다.
                // 값을 해석하지 않는다 — 무엇이 인스턴스인가는 게임서버의 개념이다.
                _store.Instance[npc] = ev.Instance.Value;
                _store.StepStatus[npc] = (byte)StepStatus.Ready;
                break;

            case GameEventKind.NpcDespawned:
                _store.StepStatus[npc] = (byte)StepStatus.Unspawned;

                // B-05 — 동적 로스터면 슬롯을 비운다. 비우지 않으면 다음 거주자가
                // 이전 거주자의 인벤토리·플래그·플랜을 물려받는다.
                //
                // <b>정적 로스터에서는 비우지 않는다.</b> 비우면 그 전역 id 의 슬롯이 사라져
                // 이후 이벤트가 전부 UnknownNpcEvents 로 떨어지고, 게임서버가 다시 스폰해도
                // 앉을 자리를 못 찾는다 — 정적 회차에서 디스폰은 "잠깐 없다" 이지 "사라졌다" 가 아니다.
                Roster?.Deactivate(_store.Occupant[npc]);
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

            case GameEventKind.PlayerHostility:
                ApplyHostility(npc, (Hostility)ev.Code, ev.Player);
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

        ApplyDuty(npc, def, _clock.TimeOfDay);
    }

    /// <summary>
    /// 근무 플래그. <c>archetypes.json</c> 의 <c>duty_hours</c> 가 유일한 출처다 (docs/01 §5).
    ///
    /// <b>런타임이 이걸 세우지 않으면 <c>Guard</c>·<c>Patrol</c> 을 쓰는 플랜이 전부 깨진다.</b>
    /// 두 액션은 <c>OnDuty</c> 를 요구하고, 검증기 3단은 <c>MasterDataSet.InitialFlags</c> 에서
    /// 이 플래그를 세워 두고 판정한다 — 그래서 검증은 통과하는데 런타임에서는 인지 스캔이
    /// 매번 이탈로 읽어 재계획 큐가 포화한다 (T4-23 실측: 5,000마리 하루에 이탈 29,535건).
    /// </summary>
    private void ApplyDuty(int npc, ArchetypeDef archetype, TimeOfDay time)
    {
        if (archetype.IsOnDuty(time))
        {
            _store.Flags[npc] |= WorldFlags.OnDuty;
        }
        else
        {
            _store.Flags[npc] &= ~WorldFlags.OnDuty;
        }
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

    /// <summary>
    /// <c>PlayerNearby</c> 플래그만 여기서 정한다. <b>등급 산출은 <see cref="LodUpdater"/> 의 몫이다</b>
    /// (T4-14) — 거리 임계가 두 곳에 있으면 반드시 어긋난다.
    /// </summary>
    private void ApplyProximity(int npc, ProximityChange change, int distance)
    {
        if (change == ProximityChange.Leave)
        {
            _store.Flags[npc] &= ~WorldFlags.PlayerNearby;
        }
        else
        {
            _store.Flags[npc] |= WorldFlags.PlayerNearby;
        }

        if (change == ProximityChange.Leave)
        {
            // B-06 — 떠났으면 적대도 없다. 플래그만 내리고 플레이어 id 는 지우지 않으면
            // 인터럽트가 이미 떠난 플레이어를 공격 대상으로 찍는다.
            _store.Flags[npc] &= ~WorldFlags.HostilePlayerNearby;
            _store.HostilePlayer[npc] = 0;
        }

        _lod.OnProximity(npc, change, distance);
    }

    /// <summary>
    /// 적대 판정을 반영한다 (B-06).
    ///
    /// <para>
    /// <b>판정은 게임서버가 한다.</b> 여기서는 플래그를 세우고 플레이어 id 를 기억할 뿐이다 —
    /// 세력·PK 상태·퀘스트가 섞인 판단을 두 쪽에서 하면 어긋나고, 어긋난 순간
    /// "경비병이 아군을 공격한다" 가 된다.
    /// </para>
    ///
    /// <para><b>멱등이다</b> (N7). 같은 값을 두 번 받아도 상태가 같다.</para>
    /// </summary>
    private void ApplyHostility(int npc, Hostility hostility, PlayerId player)
    {
        HostilityChanges++;

        if (hostility == Hostility.Hostile)
        {
            _store.Flags[npc] |= WorldFlags.HostilePlayerNearby | WorldFlags.PlayerNearby;
            _store.HostilePlayer[npc] = player.Value;

            return;
        }

        // 중립·우호는 같이 내린다. 구분은 앞으로의 일이다 — 지금 우호를 따로 다루면
        // 쓰이지 않는 분기가 남는다.
        _store.Flags[npc] &= ~WorldFlags.HostilePlayerNearby;

        // <b>그 플레이어의 판정만 지운다.</b> 다른 플레이어가 적대인 상태를 덮으면
        // 경비병이 공격을 멈춘다 — 게임서버는 NPC 당 1건만 보내므로 보통 같은 id 다.
        if (_store.HostilePlayer[npc] == player.Value)
        {
            _store.HostilePlayer[npc] = 0;
        }
    }

    /// <summary>
    /// 아직 자리가 없는 전역 id 에 슬롯을 잡아 준다 (A-08).
    ///
    /// <para>
    /// <b>스폰일 때만 잡는다.</b> 이동·완료 이벤트가 자리를 만들면 게임서버의 라우팅 실수가
    /// 조용히 정상으로 보인다 — "우리 샤드가 아닌 NPC 가 여기서 살기 시작" 하는 것이다.
    /// </para>
    ///
    /// <para>
    /// <b>정적 로스터면 잡지 않는다.</b> 기동 시 로스터 전원이 이미 묶여 있으므로,
    /// 모르는 id 는 정말로 모르는 NPC 다.
    /// </para>
    /// </summary>
    private bool TrySeat(in GameEvent ev, out int npc)
    {
        npc = -1;

        if (ev.Kind != GameEventKind.NpcSpawned || Roster is not { } roster)
        {
            UnknownNpcEvents++;
            return false;
        }

        if (!roster.TryActivate(ev.Npc.Value, ev.OccurredAt, out npc))
        {
            RosterRejections++;
            return false;
        }

        return true;
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

            // 근무는 (아키타입 × 시간대)에서 나온다. 시간대가 바뀌면 같이 바뀐다 (docs/01 §5).
            ApplyDuty(i, _data.Archetypes[new ArchetypeId(_store.ArchetypeCode[i])], time);
        }
    }

    private void ApplyZoneState(ZoneId zone, RegionState state)
    {
        WorldFlags set = _data.Buckets.FlagsOf(state);

        for (int i = 0; i < _store.Count; i++)
        {
            if (_store.ZoneCode[i] == zone.Value)
            {
                _store.Flags[i] = (_store.Flags[i] & ~RegionFlags) | set;
            }
        }

        // 지역이 공격받으면 그 존 전체를 LOD 1 로 승격한다 (docs/11 §4).
        // 등급 규칙은 LodUpdater 한 곳에만 둔다 (T4-14).
        _lod.OnZoneState(zone, state);
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
