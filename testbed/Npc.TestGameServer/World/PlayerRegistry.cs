using System.Collections.Immutable;
using Npc.Contracts;
using Npc.Core.Plan;
using Npc.MasterData;
using Npc.Sim;

namespace Npc.TestGameServer.World;

/// <summary>
/// 플레이어 이동 · 존 판정 · 근접 판정. docs/20 §7.3.
///
/// <para>
/// <b><see cref="PlayerBots"/> 를 쓰지 않는다.</b> 대신 같은 자리(틱 3단계)에 들어가 같은 일
/// (<c>ObservedByPlayer</c> 갱신 + <c>PlayerProximity</c> 발행)을 한다 — 둘이 같은 배열에 쓰면
/// 서로의 판정을 덮어쓴다. <b>이 클래스가 <c>ObservedByPlayer</c> 를 갱신하는 유일한 주체다.</b>
/// </para>
///
/// <para>
/// <b>속도가 게임 시간 기준인 이유.</b> NPC 가 게임 시간으로 움직이기 때문이다.
/// <c>--time-scale 60</c> 이면 NPC 의 겉보기 속도가 실시간의 60배라, 플레이어만 실시간이면
/// 화면에서 멈춰 있는 것처럼 보인다 (docs/20 §7.3).
/// </para>
///
/// <para>
/// <b>히스테리시스 20m 을 지운다면.</b> 경계에 선 NPC 가 판정마다 Enter/Leave 를 번갈아 내고,
/// 그 이벤트 하나하나가 NPC 서버의 재계획 큐에 들어가 큐가 폭주한다.
/// </para>
///
/// <para><b>틱 스레드 전용이다.</b> <see cref="SimWorld"/> 의 이벤트 채널은 기록자가 하나여야 한다.</para>
/// </summary>
public sealed class PlayerRegistry
{
    /// <summary>근접 판정 주기(틱). 매 틱 재면 NPC 수 × 플레이어 수를 10Hz 로 돌게 된다.</summary>
    public const int ProximityPeriodTicks = 5;

    /// <summary>이 거리(m) 이하면 관측 시작. docs/11 §4 의 LOD 1 경계와 같다.</summary>
    public const float EnterRange = 200f;

    /// <summary>이 거리(m) 이상이면 관측 종료. 차이 20m 이 히스테리시스다.</summary>
    public const float LeaveRange = 220f;

    /// <summary>달리기 배수. 걷기 2.5 · 뛰기 5.0 게임m/게임초 (docs/20 §7.3).</summary>
    public const double RunMultiplier = 2.0;

    /// <summary>
    /// <c>Interact</c>·<c>Attack</c> 사거리(m). docs/20 §7.3.
    ///
    /// <b>왜 제한을 두는가.</b> 클라이언트가 화면 밖 NPC 를 찍어서 인터럽트를 걸 수 있으면
    /// "플레이어 근접이 인지 LOD 를 바꾼다" 는 검증이 무의미해진다.
    /// </summary>
    public const float InteractRange = 30f;

    /// <summary>봇이 이 틱마다 다음 목적지를 고른다. <see cref="PlayerBots.StepTicks"/> 와 같다.</summary>
    public const int BotStepTicks = 20;

    /// <summary>접속 시작 지점의 존. docs/20 §7.3.</summary>
    public const string SpawnZoneId = "town_center";

    private readonly GameWorld _world;
    private readonly MasterDataSet _data;
    private readonly PlayerState[] _slots;
    private readonly bool[] _observed;

    /// <summary>이번 근접 판정에서 볼 존. 매 판정마다 다시 칠한다 — 재할당하지 않는다.</summary>
    private readonly bool[] _candidateZones;

    private readonly WorldPos _spawn;
    private readonly double _walkPerTick;
    private readonly int _seed;

    /// <summary>등록기를 만든다. 기동 시 1회.</summary>
    /// <param name="world">게임서버 월드.</param>
    /// <param name="data">마스터데이터.</param>
    /// <param name="options">옵션. 속도·타임스케일·정원이 여기서 온다.</param>
    /// <param name="capacity">
    /// 동시 플레이어 상한. 기본은 <c>--max-clients</c> + <c>--bots</c> 다 —
    /// 봇도 같은 경로로 도는 것이 §7.3 의 요구다.
    /// </param>
    public PlayerRegistry(GameWorld world, MasterDataSet data, GameServerOptions options, int capacity = 0)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);

        _world = world;
        _data = data;

        int slots = capacity > 0 ? capacity : Math.Max(1, options.MaxClients + options.Bots);

        _slots = new PlayerState[slots];

        for (int i = 0; i < slots; i++)
        {
            _slots[i] = new PlayerState();
        }

        _observed = new bool[world.World.Capacity];
        _candidateZones = new bool[MaxZoneCode(data) + 1];
        _spawn = SpawnPoint(data);
        _seed = options.Seed;

        // 게임m/게임초 → m/틱. TimeScale 이 곱해지는 것이 이 식의 요점이다.
        _walkPerTick = options.PlayerSpeed * options.TimeScale / GameWorld.TickRate;

        // 봇은 사람보다 앞 슬롯을 잡는다. 클라이언트 없이도 데모가 돌게 하는 장치다 (docs/20 §7.3).
        for (int bot = 0; bot < options.Bots; bot++)
        {
            PlayerId id = Add();

            if (id.Value == 0)
            {
                break;   // 정원이 모자라면 거기까지다. 기동을 막을 일은 아니다
            }

            _slots[id.Value - 1].Bot = true;
        }
    }

    /// <summary>동시 플레이어 상한.</summary>
    public int Capacity => _slots.Length;

    /// <summary>지금 접속해 있는 수.</summary>
    public int Count
    {
        get
        {
            int active = 0;

            foreach (PlayerState player in _slots)
            {
                if (player.Active)
                {
                    active++;
                }
            }

            return active;
        }
    }

    /// <summary>발행한 <c>PlayerHostility</c> 수 (B-06).</summary>
    public long HostilityEvents { get; private set; }

    /// <summary>발행한 <c>PlayerProximity</c> 수.</summary>
    public long ProximityEvents { get; private set; }

    /// <summary>발행한 <c>PlayerInteracted</c> 수.</summary>
    public long Interacts { get; private set; }

    /// <summary>사거리 밖이라 무시한 <c>Interact</c> 수.</summary>
    public long InteractsOutOfRange { get; private set; }

    /// <summary>발행한 <c>CombatStarted</c> 수.</summary>
    public long Attacks { get; private set; }

    /// <summary>사거리 밖이라 무시한 <c>Attack</c> 수.</summary>
    public long AttacksOutOfRange { get; private set; }

    /// <summary>지금 도는 봇 수.</summary>
    public int Bots
    {
        get
        {
            int bots = 0;

            foreach (PlayerState player in _slots)
            {
                if (player.Active && player.Bot)
                {
                    bots++;
                }
            }

            return bots;
        }
    }

    /// <summary>
    /// 거리를 실제로 재 본 NPC 수의 누계.
    ///
    /// <b>존 필터가 듣고 있는지를 보는 값이다.</b> 전수 검사로 돌아가면 이 값이 NPC 수만큼
    /// 늘어나고, 5,000 규모에서 그 차이가 곧 틱 예산이다.
    /// </summary>
    public long NpcsScanned { get; private set; }

    /// <summary>지금 누군가에게 관측되는 NPC 수.</summary>
    public int ObservedNpcs
    {
        get
        {
            int observed = 0;

            foreach (bool o in _observed)
            {
                if (o)
                {
                    observed++;
                }
            }

            return observed;
        }
    }

    /// <summary>접속 시작 지점. <see cref="SpawnZoneId"/> 존의 POI 무게중심이다.</summary>
    public WorldPos SpawnPos => _spawn;

    /// <summary>
    /// 플레이어를 등록한다. 자리가 없으면 <c>PlayerId(0)</c> 을 준다 — 예외를 던지지 않는다.
    /// 정원 초과는 클라이언트 세션이 거절 메시지로 답할 일이다 (T6-23).
    /// </summary>
    /// <summary>이 플레이어가 적대 세력인가 (B-06).</summary>
    /// <param name="player">플레이어.</param>
    public bool IsHostile(PlayerId player) =>
        (uint)(player.Value - 1) < (uint)_slots.Length
        && _slots[player.Value - 1] is { Active: true, Hostile: true };

    /// <summary>
    /// 플레이어를 적대/중립으로 둔다 (B-06). <b>모르는 id 는 false 다.</b>
    ///
    /// <para>
    /// <b>이벤트를 여기서 내지 않는다.</b> 근접 판정이 도는 자리에서 에지 트리거로 내야
    /// "상태가 안 바뀐 NPC 에 이벤트를 내지 않는다" 를 지킬 수 있다.
    /// </para>
    /// </summary>
    /// <param name="player">플레이어.</param>
    /// <param name="hostile">적대면 true.</param>
    /// <returns>바꿨으면 true.</returns>
    public bool SetHostile(PlayerId player, bool hostile)
    {
        if ((uint)(player.Value - 1) >= (uint)_slots.Length)
        {
            return false;
        }

        PlayerState state = _slots[player.Value - 1];

        if (!state.Active)
        {
            return false;
        }

        state.Hostile = hostile;

        return true;
    }

    public PlayerId Add()
    {
        for (int slot = 0; slot < _slots.Length; slot++)
        {
            PlayerState player = _slots[slot];

            if (player.Active)
            {
                continue;
            }

            player.Active = true;
            player.Pos = _spawn;
            player.Heading = 0;
            player.Run = false;
            player.DirX = 0;
            player.DirZ = 0;
            player.Bot = false;
            player.Target = default;
            player.Zone = ZoneAt(_spawn);

            return new PlayerId(slot + 1);
        }

        return default;
    }

    /// <summary>플레이어를 지운다. 세션이 끊기면 부른다.</summary>
    /// <returns>실제로 지웠으면 true.</returns>
    public bool Remove(PlayerId player)
    {
        if (!TryGet(player, out PlayerState state))
        {
            return false;
        }

        state.Active = false;
        state.Bot = false;
        state.DirX = 0;
        state.DirZ = 0;
        state.Target = default;

        return true;
    }

    /// <summary>이 플레이어가 접속해 있는가.</summary>
    public bool IsActive(PlayerId player) => TryGet(player, out _);

    /// <summary>지금 위치.</summary>
    public WorldPos PositionOf(PlayerId player) => TryGet(player, out PlayerState s) ? s.Pos : default;

    /// <summary>지금 바라보는 방향(라디안).</summary>
    public float HeadingOf(PlayerId player) => TryGet(player, out PlayerState s) ? s.Heading : 0;

    /// <summary>지금 있는 존. 가장 가까운 POI 의 존이다.</summary>
    public ZoneId ZoneOf(PlayerId player) => TryGet(player, out PlayerState s) ? s.Zone : default;

    /// <summary>
    /// 이동 입력. 다음 <see cref="Tick"/> 부터 적용된다.
    ///
    /// <paramref name="dirX"/>·<paramref name="dirZ"/> 는 <b>정규화된다</b> —
    /// 반쯤 기운 스틱이 반 속도가 되지 않는다 (docs/20 §7.3).
    /// </summary>
    public void SetInput(PlayerId player, float dirX, float dirZ, bool run)
    {
        if (!TryGet(player, out PlayerState state))
        {
            return;
        }

        state.DirX = dirX;
        state.DirZ = dirZ;
        state.Run = run;
    }

    /// <summary>
    /// 플레이어를 그 자리로 옮긴다.
    ///
    /// <b>클라이언트 입력 경로가 아니다.</b> 봇의 랜덤 워크(T6-20)와 근접 히스테리시스 검증이
    /// 쓰는 이음매다 — 경계를 왕복시키려면 위치를 직접 정할 수 있어야 한다.
    /// </summary>
    public void Teleport(PlayerId player, in WorldPos pos)
    {
        if (!TryGet(player, out PlayerState state))
        {
            return;
        }

        state.Pos = pos;
        state.Zone = ZoneAt(pos);
    }

    /// <summary>
    /// 이 플레이어가 NPC 에게 말을 건다. docs/20 §7.3.
    ///
    /// <b>사거리 밖이면 아무 일도 없다.</b> 예외를 던지지 않고 <see cref="InteractsOutOfRange"/>
    /// 만 오른다 — 클라이언트가 화면 밖 NPC 를 찍어서 인터럽트를 걸 수 있으면
    /// "플레이어 근접이 인지 LOD 를 바꾼다" 는 검증이 무의미해진다.
    /// </summary>
    /// <returns>이벤트가 나갔으면 true.</returns>
    public bool TryInteract(PlayerId player, int slot, Tick now)
    {
        if (!InRange(player, slot, now, out PlayerState state))
        {
            InteractsOutOfRange++;
            return false;
        }

        _world.World.Emit(new GameEvent
        {
            Kind = GameEventKind.PlayerInteracted,
            Sequence = 0,
            OccurredAt = now,

            // A-08 — 와이어의 NpcId 는 전역 인스턴스 id 다.
            Npc = _world.World.NpcIdOf(slot),
            Player = player,
            Zone = state.Zone,
        });

        Interacts++;

        return true;
    }

    /// <summary>
    /// 이 플레이어가 NPC 를 때린다. docs/20 §7.3.
    ///
    /// <c>CombatStarted</c> + <c>DamageTaken</c> 을 내고 <see cref="NeedsSim"/> 의 HP 를 깎는다 —
    /// HP 를 여기서 따로 들면 <c>NpcVitalsChanged</c> 와 어긋난 두 벌의 진실이 생긴다.
    /// </summary>
    /// <returns>이벤트가 나갔으면 true.</returns>
    public bool TryAttack(PlayerId player, int slot, int amount, Tick now)
    {
        if (!InRange(player, slot, now, out _))
        {
            AttacksOutOfRange++;
            return false;
        }

        int damage = Math.Clamp(amount, 1, 100);

        _world.World.Emit(new GameEvent
        {
            Kind = GameEventKind.CombatStarted,
            Sequence = 0,
            OccurredAt = now,
            Npc = _world.World.NpcIdOf(slot),
            Player = player,
        });

        _world.World.Emit(new GameEvent
        {
            Kind = GameEventKind.DamageTaken,
            Sequence = 0,
            OccurredAt = now,
            Npc = _world.World.NpcIdOf(slot),
            Player = player,
            Amount = damage,
        });

        // NpcVitalsChanged 는 여기서 나간다. 순서가 CombatStarted → DamageTaken → Vitals 다.
        _world.Needs.Restore(slot, -damage, 0, now);

        Attacks++;

        return true;
    }

    /// <summary>이 슬롯이 봇인가.</summary>
    public bool IsBot(PlayerId player) => TryGet(player, out PlayerState s) && s.Bot;

    /// <summary>
    /// 틱 3단계. 봇 조종 → 입력 적용 → 이동 → 존 판정, 그리고 5틱마다 근접 판정.
    /// <b><see cref="GameWorld.Players"/> 에 이 메서드를 꽂는다.</b>
    /// </summary>
    public void Tick(Tick now)
    {
        for (int slot = 0; slot < _slots.Length; slot++)
        {
            PlayerState player = _slots[slot];

            if (!player.Active)
            {
                continue;
            }

            if (player.Bot)
            {
                // 봇도 사람과 <b>같은 경로로</b> 돈다 (docs/20 §7.3) — 입력을 대신 넣어 줄 뿐이다.
                // 별도 이동 코드를 두면 둘의 동작이 갈리고, 데모에서 본 것이 사람 경로가 아니게 된다.
                SteerBot(slot, player, now);
            }

            Move(player);

            // 존은 매 틱 다시 판정한다 (docs/20 §7.3). 근접 후보 집합의 근거라
            // 5틱마다로 미루면 빠르게 지나가는 플레이어가 존을 건너뛴다.
            player.Zone = ZoneAt(player.Pos);
        }

        if (now.Value % ProximityPeriodTicks == 0)
        {
            Proximity(now);
        }
    }

    // ---------------------------------------------------------------- 내부

    /// <summary>
    /// 사거리 판정. 사거리 밖·모르는 플레이어·스폰 안 된 NPC 는 전부 여기서 걸린다.
    ///
    /// <b>거절 사유를 나누지 않는다.</b> 클라이언트에 "왜 안 됐는지" 를 돌려주면 그것으로
    /// 화면 밖 NPC 의 존재를 알아낼 수 있고, 그건 사거리 제한을 두는 이유와 어긋난다.
    /// </summary>
    private bool InRange(PlayerId player, int slot, Tick now, out PlayerState state)
    {
        if (!TryGet(player, out state))
        {
            return false;
        }

        if (!_world.World.IsSpawned(slot))
        {
            return false;
        }

        float distance = Distance(_world.Transforms.Interpolate(slot, now), state.Pos);

        return distance <= InteractRange;
    }

    /// <summary>
    /// 봇의 이번 틱 입력. <see cref="BotStepTicks"/> 마다 목적지 POI 를 새로 고르고,
    /// 그 사이에는 그쪽을 향해 걷는다.
    ///
    /// <para>
    /// <b>난수를 쓰지 않는다</b> (CLAUDE.md §2.3). <c>PlanHash.Mix(seed, bot, tick/20)</c> 하나로
    /// 정해지므로 같은 시드는 같은 위치열을 낸다 — 데모를 두 번 돌려 비교할 수 있어야 한다.
    /// </para>
    ///
    /// <para>
    /// <b>방향을 직접 뽑지 않고 목적지를 뽑는 이유.</b> 방향만 뽑으면 봇이 월드 밖으로
    /// 표류한다. POI 를 향하게 하면 시드가 무엇이든 지도 안에 머문다.
    /// </para>
    /// </summary>
    private void SteerBot(int slot, PlayerState player, Tick now)
    {
        ImmutableArray<PoiDef> pois = _data.Pois.Pois;

        if (pois.Length == 0)
        {
            return;
        }

        if (now.Value % BotStepTicks == 0 || player.Target.Value == 0)
        {
            uint roll = PlanHash.Mix(
                PlanHash.Mix(_seed, slot) ^ PlanHash.Mix((uint)(now.Value / BotStepTicks)));

            player.Target = pois[(int)(roll % (uint)pois.Length)].Code;
        }

        WorldPos to = _data.Pois[player.Target].Pos;

        player.DirX = to.X - player.Pos.X;
        player.DirZ = to.Z - player.Pos.Z;
        player.Run = false;

        // 목적지에 닿았으면 멈춘다. 안 그러면 한 걸음 거리를 사이에 두고 진동한다.
        if (Math.Abs(player.DirX) + Math.Abs(player.DirZ) < 1f)
        {
            player.DirX = 0;
            player.DirZ = 0;
        }
    }

    private void Move(PlayerState player)
    {
        double length = Math.Sqrt((player.DirX * player.DirX) + (player.DirZ * player.DirZ));

        if (length <= 0)
        {
            return;
        }

        double step = _walkPerTick * (player.Run ? RunMultiplier : 1.0);
        double dx = player.DirX / length;
        double dz = player.DirZ / length;

        player.Pos = new WorldPos(
            (float)(player.Pos.X + (dx * step)),
            player.Pos.Y,
            (float)(player.Pos.Z + (dz * step)));

        player.Heading = MathF.Atan2((float)dx, (float)dz);
    }

    /// <summary>
    /// 근접 판정. 플레이어가 있는 존 + 인접 존의 NPC 만 거리를 잰다 (docs/20 §7.3).
    ///
    /// <para>
    /// <b>후보 밖인데 관측 중이던 NPC 도 본다.</b> 플레이어가 멀리 걸어가면 그 NPC 의 존이
    /// 후보에서 빠지는데, 거기서 그냥 넘어가면 <c>Leave</c> 가 영원히 안 나가고
    /// 그 NPC 는 죽을 때까지 LOD 1 에 남는다. 관측 중인 집합은 작아서 이 훑기는 싸다.
    /// </para>
    /// </summary>
    private void Proximity(Tick now)
    {
        Array.Clear(_candidateZones);

        foreach (PlayerState player in _slots)
        {
            if (player.Active)
            {
                MarkCandidate(player.Zone);
            }
        }

        SimWorld world = _world.World;

        for (int npc = 0; npc < world.Capacity; npc++)
        {
            if (!world.IsSpawned(npc))
            {
                continue;
            }

            ZoneId zone = world.ZoneOf(npc);
            bool candidate = (uint)zone.Value < (uint)_candidateZones.Length && _candidateZones[zone.Value];

            if (!candidate && !_observed[npc])
            {
                continue;   // 볼 이유가 없다
            }

            if (candidate)
            {
                NpcsScanned++;
            }

            (float distance, PlayerId nearest) = Nearest(npc, now);

            // 히스테리시스. 관측 중이면 220m 까지 버티고, 아니면 200m 안에 들어와야 잡힌다.
            bool observed = _observed[npc] ? distance < LeaveRange : distance <= EnterRange;

            if (observed == _observed[npc])
            {
                continue;   // 변화가 없으면 이벤트를 내지 않는다 — 재계획 큐가 폭주한다
            }

            _observed[npc] = observed;
            world.ObservedByPlayer[npc] = observed;

            world.Emit(new GameEvent
            {
                Kind = GameEventKind.PlayerProximity,
                Sequence = 0,
                OccurredAt = now,
                Npc = world.NpcIdOf(npc),
                Player = nearest,
                Amount = float.IsFinite(distance) ? (int)distance : int.MaxValue,
                Code = (byte)(observed ? ProximityChange.Enter : ProximityChange.Leave),
            });

            ProximityEvents++;

            // B-06 — 들어온 플레이어가 적대면 같이 알린다. 근접과 같은 에지 트리거다.
            //
            // <b>떠날 때는 안 낸다.</b> PlayerProximity(Leave) 가 이미 적대를 내리므로
            // 두 번 알리면 이벤트만 두 배가 된다.
            if (observed && IsHostile(nearest))
            {
                world.Emit(new GameEvent
                {
                    Kind = GameEventKind.PlayerHostility,
                    Sequence = 0,
                    OccurredAt = now,
                    Npc = world.NpcIdOf(npc),
                    Player = nearest,
                    Code = (byte)Hostility.Hostile,
                });

                HostilityEvents++;
            }
        }
    }

    /// <summary>이 NPC 에게 가장 가까운 플레이어와 그 거리. 아무도 없으면 무한대다.</summary>
    private (float Distance, PlayerId Player) Nearest(int npc, Tick now)
    {
        // 이동 중인 NPC 는 POI 가 아니라 보간 위치에 있다. POI 로 재면 도착 전까지
        // 출발지 기준이라 플레이어가 따라 걸어도 근접이 안 잡힌다.
        WorldPos at = _world.Transforms.Interpolate(npc, now);

        float nearest = float.PositiveInfinity;
        PlayerId who = default;

        for (int slot = 0; slot < _slots.Length; slot++)
        {
            PlayerState player = _slots[slot];

            if (!player.Active)
            {
                continue;
            }

            float distance = Distance(at, player.Pos);

            if (distance < nearest)
            {
                nearest = distance;
                who = new PlayerId(slot + 1);
            }
        }

        return (nearest, who);
    }

    private void MarkCandidate(ZoneId zone)
    {
        if ((uint)zone.Value >= (uint)_candidateZones.Length || zone.Value == 0)
        {
            return;
        }

        _candidateZones[zone.Value] = true;

        foreach (ZoneId adjacent in _data.Zones[zone].Adjacent)
        {
            if ((uint)adjacent.Value < (uint)_candidateZones.Length)
            {
                _candidateZones[adjacent.Value] = true;
            }
        }
    }

    /// <summary>가장 가까운 POI 의 존. 매 틱 도는 자리라 243개 전수 비교로 끝낸다.</summary>
    private ZoneId ZoneAt(in WorldPos pos)
    {
        ImmutableArray<PoiDef> pois = _data.Pois.Pois;
        float nearest = float.PositiveInfinity;
        ZoneId zone = default;

        foreach (PoiDef poi in pois)
        {
            float distance = Distance(poi.Pos, pos);

            if (distance < nearest)
            {
                nearest = distance;
                zone = poi.Zone;
            }
        }

        return zone;
    }

    /// <summary>
    /// 유클리드 거리(m).
    ///
    /// 3차원으로 잰다. 지금 <c>pois.json</c> 은 y 가 전부 0 이라 평면 거리와 같지만,
    /// 높이가 생기면 그때 이 식이 맞는 답을 준다.
    /// </summary>
    private static float Distance(in WorldPos a, in WorldPos b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;

        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private bool TryGet(PlayerId player, out PlayerState state)
    {
        int slot = player.Value - 1;

        if ((uint)slot < (uint)_slots.Length && _slots[slot].Active)
        {
            state = _slots[slot];
            return true;
        }

        state = null!;
        return false;
    }

    private static int MaxZoneCode(MasterDataSet data)
    {
        int max = 0;

        foreach (ZoneDef zone in data.Zones.Zones)
        {
            max = Math.Max(max, zone.Code.Value);
        }

        return max;
    }

    /// <summary>
    /// 접속 시작 지점 — <see cref="SpawnZoneId"/> 존 POI 들의 무게중심.
    ///
    /// 존 id 를 코드에 적은 곳은 여기 하나다. docs/20 §7.3 이 지정한 값이고,
    /// 그 존이 없어지면 첫 POI 로 물러난다 — 기동을 못 하게 만들 값은 아니다.
    /// </summary>
    private static WorldPos SpawnPoint(MasterDataSet data)
    {
        if (!data.Zones.TryGet(SpawnZoneId, out ZoneDef zone))
        {
            return data.Pois.Count > 0 ? data.Pois.Pois[0].Pos : default;
        }

        ImmutableArray<PoiId> pois = data.Pois.InZone(zone.Code);

        if (pois.Length == 0)
        {
            return default;
        }

        double x = 0;
        double y = 0;
        double z = 0;

        foreach (PoiId poi in pois)
        {
            WorldPos pos = data.Pois[poi].Pos;

            x += pos.X;
            y += pos.Y;
            z += pos.Z;
        }

        return new WorldPos((float)(x / pois.Length), (float)(y / pois.Length), (float)(z / pois.Length));
    }

    /// <summary>
    /// 플레이어 한 명. <b>클래스인 이유는 슬롯을 제자리에서 고치기 때문이다</b> —
    /// struct 배열이면 <c>foreach</c> 가 복사본을 준다.
    /// </summary>
    private sealed class PlayerState
    {
        public bool Active;
        public WorldPos Pos;
        public float Heading;
        public ZoneId Zone;
        public bool Run;
        public float DirX;
        public float DirZ;

        /// <summary>봇이면 참. 사람과 같은 경로로 돌되 입력을 <c>SteerBot</c> 이 넣는다.</summary>
        public bool Bot;

        /// <summary>봇의 목적지 POI. 사람은 0 이다.</summary>
        public PoiId Target;

        /// <summary>
        /// 적대 세력인가 (B-06). 뷰어가 <c>ControlKind.SetHostile</c> 로 켠다.
        ///
        /// <b>대역의 단순화다.</b> 실제 게임서버는 세력·PK 상태·퀘스트로 판정한다 —
        /// 그 자료는 전부 게임서버의 것이고 NPC 서버는 결과만 받는다.
        /// </summary>
        public bool Hostile;
    }
}
