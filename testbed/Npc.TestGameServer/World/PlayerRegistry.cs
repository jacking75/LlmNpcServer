using System.Collections.Immutable;
using Npc.Contracts;
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

        // 게임m/게임초 → m/틱. TimeScale 이 곱해지는 것이 이 식의 요점이다.
        _walkPerTick = options.PlayerSpeed * options.TimeScale / GameWorld.TickRate;
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

    /// <summary>발행한 <c>PlayerProximity</c> 수.</summary>
    public long ProximityEvents { get; private set; }

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
        state.DirX = 0;
        state.DirZ = 0;

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
    /// 틱 3단계. 입력 적용 → 이동 → 존 판정, 그리고 5틱마다 근접 판정.
    /// <b><see cref="GameWorld.Players"/> 에 이 메서드를 꽂는다.</b>
    /// </summary>
    public void Tick(Tick now)
    {
        foreach (PlayerState player in _slots)
        {
            if (!player.Active)
            {
                continue;
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
                Npc = new NpcId(npc),
                Player = nearest,
                Amount = float.IsFinite(distance) ? (int)distance : int.MaxValue,
                Code = (byte)(observed ? ProximityChange.Enter : ProximityChange.Leave),
            });

            ProximityEvents++;
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
    }
}
