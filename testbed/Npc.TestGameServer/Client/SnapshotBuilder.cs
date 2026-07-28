using Npc.Contracts;
using Npc.MasterData;
using Npc.Sim;
using Npc.TestBed.Protocol;
using Npc.TestGameServer.World;

namespace Npc.TestGameServer.Client;

/// <summary>
/// AOI 스냅샷 조립. docs/20 §8.1.
///
/// <para>
/// <b>전량을 보내지 않는다.</b> NPC 1,000 마리를 5Hz 로 보내면 세션당 160KB/s 다.
/// 플레이어 반경 <see cref="AoiRadius"/> m 안에서 가까운 순 <see cref="MaxEntities"/> 개만 싣는다 —
/// 그 상한이 곧 대역폭 상한이다(256 × 32B × 5Hz ≈ 41KB/s).
/// </para>
///
/// <para>
/// <b>NPC 위치는 <see cref="TransformEmitter.Interpolate"/> 로 구한다.</b> POI 로 구하면
/// 도착 전까지 출발지에 붙어 있다가 순간이동하는 화면이 된다. NPC 서버로 나가는
/// <c>NpcTransform</c> 은 관측 대상만 발행되지만(<c>MaxPerTick</c>), 이 스냅샷은
/// 게임서버 내부 상태에서 직접 만들므로 그 상한에 걸리지 않는다.
/// </para>
///
/// <para>
/// <b>보는 사람 자신도 넣는다.</b> 위치의 주인은 서버라 클라이언트가 제 위치를 모른다 —
/// 안 넣으면 화면에 제 캐릭터가 안 보인다. 거리 0 이라 상한에 잘릴 일도 없다.
/// </para>
///
/// <para><b>틱 스레드 전용이다.</b> 버퍼 한 벌을 세션들이 돌려 쓴다.</para>
/// </summary>
public sealed class SnapshotBuilder
{
    /// <summary>한 스냅샷의 엔티티 상한. docs/20 §8.1.</summary>
    public const int MaxEntities = 256;

    /// <summary>AOI 반경(m).</summary>
    public const float AoiRadius = 1_200f;

    /// <summary>스냅샷 주기(틱). 2틱 = 5Hz.</summary>
    public const int PeriodTicks = 2;

    /// <summary>
    /// 게임 시작 시각(시).
    ///
    /// <b><c>Npc.Runtime.GameClock</c> 의 기본값과 같아야 한다.</b> 다르면 상태바의 게임 시각이
    /// NPC 서버가 보는 시각과 어긋나고, 사람은 그 차이를 "NPC 가 엉뚱한 시간대 행동을 한다" 로 읽는다.
    /// 여기서 그 클래스를 참조하지 못하는 것은 의존 방향 때문이다 (docs/20 §4) —
    /// 게임서버 대역은 <c>Npc.Runtime</c> 을 모른다.
    /// </summary>
    public const int StartGameHour = 6;

    /// <summary>게임 하루의 초.</summary>
    private const int SecondsPerGameDay = 24 * 3600;

    private readonly GameWorld _world;
    private readonly MasterDataSet _data;
    private readonly PlayerRegistry _players;
    private readonly int _timeScale;

    // 가까운 순 MaxEntities 개만 남기는 최대 힙. 뿌리가 "지금 담긴 것 중 가장 먼 것" 이라
    // 그것보다 가까운 후보만 뿌리를 밀어낸다 — NPC 전수를 훑어도 O(N log 256) 이다.
    private readonly float[] _distance = new float[MaxEntities];
    private readonly EntityState[] _heap = new EntityState[MaxEntities];

    private int _count;

    /// <summary>빌더를 만든다. 기동 시 1회. 버퍼는 여기서 전부 잡는다.</summary>
    public SnapshotBuilder(GameWorld world, MasterDataSet data, PlayerRegistry players, int timeScale)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(players);

        _world = world;
        _data = data;
        _players = players;
        _timeScale = timeScale;
    }

    /// <summary>만든 스냅샷 수.</summary>
    public long Built { get; private set; }

    /// <summary>AOI 밖이라 뺀 엔티티 수의 누계.</summary>
    public long CulledByRange { get; private set; }

    /// <summary>상한(<see cref="MaxEntities"/>)에 걸려 뺀 엔티티 수의 누계.</summary>
    public long CulledByCap { get; private set; }

    /// <summary>이 틱에 스냅샷을 보내는가. 2틱마다다 (docs/20 §7.2 10단계).</summary>
    public static bool DueAt(Tick now) => now.Value % PeriodTicks == 0;

    /// <summary>
    /// 이 플레이어가 보는 스냅샷을 만든다.
    ///
    /// <b>엔티티는 가까운 순으로 정렬돼 나간다.</b> 클라이언트가 겹친 것을 그릴 때 가까운 것이
    /// 위에 오고, 무엇보다 상한에 잘린 회차의 결과가 회차마다 흔들리지 않는다.
    /// </summary>
    public Snapshot Build(PlayerId viewer, Tick now)
    {
        _count = 0;
        Built++;

        long gameSeconds = ((long)StartGameHour * 3600) + (now.Value * _timeScale / Tick.PerSecond);
        byte gameHour = (byte)(gameSeconds / 3600 % 24);

        var snapshot = new Snapshot
        {
            Tick = now.Value,
            GameDay = (int)(gameSeconds / SecondsPerGameDay),
            GameHour = gameHour,
            TimeOfDay = (byte)_data.Buckets.TimeOfDayAt(gameHour),
            EntityCount = 0,
            Entities = [],
        };

        if (!_players.IsActive(viewer))
        {
            return snapshot;   // 이미 나간 세션이다. 빈 스냅샷이 맞다
        }

        WorldPos eye = _players.PositionOf(viewer);

        Gather(eye, now);
        SortByDistance();

        snapshot.EntityCount = _count;
        snapshot.Entities = _heap.AsSpan(0, _count).ToArray();

        return snapshot;
    }

    // ---------------------------------------------------------------- 수집

    private void Gather(in WorldPos eye, Tick now)
    {
        SimWorld world = _world.World;

        for (int npc = 0; npc < world.Capacity; npc++)
        {
            if (!world.IsSpawned(npc))
            {
                continue;
            }

            WorldPos at = _world.Transforms.Interpolate(npc, now);
            float distance = Distance(at, eye);

            if (distance > AoiRadius)
            {
                CulledByRange++;
                continue;
            }

            Offer(distance, NpcOf(npc, at));
        }

        // 다른 클라이언트의 플레이어도 넣는다 (docs/20 §8.1). 봇도 같은 배열에 있으므로
        // 클라이언트 없이 돌리는 데모에서도 사람이 봇을 본다 (docs/20 §7.3).
        for (int slot = 1; slot <= _players.Capacity; slot++)
        {
            var id = new PlayerId(slot);

            if (!_players.IsActive(id))
            {
                continue;
            }

            WorldPos at = _players.PositionOf(id);
            float distance = Distance(at, eye);

            if (distance > AoiRadius)
            {
                CulledByRange++;
                continue;
            }

            Offer(distance, PlayerOf(id, at));
        }
    }

    private EntityState NpcOf(int npc, in WorldPos at)
    {
        SimWorld world = _world.World;
        MovementSim movement = _world.Movement;

        bool moving = movement.IsMoving(npc);
        bool working = _world.Interaction.IsWorking(npc);
        PoiId target = movement.TargetOf(npc);

        var flags = EntityFlags.None;

        if (moving)
        {
            flags |= EntityFlags.Moving;
        }

        if (working)
        {
            flags |= EntityFlags.Working;
        }

        if (world.ObservedByPlayer[npc])
        {
            flags |= EntityFlags.Observed;
        }

        // EntityFlags.InCombat 은 채우지 않는다. 전투 "상태" 를 들고 있는 곳이 없기 때문이다 —
        // PlayerRegistry 의 공격도 InteractionSim 의 CombatAction 도 그 자리에서 끝나고
        // 아무 표시를 남기지 않는다. 없는 상태를 HP 같은 것으로 흉내내면 화면이 거짓말을 한다.
        // 채우려면 먼저 전투 상태를 어디에 둘지 정해야 한다 (docs/20 §7.3).

        return new EntityState
        {
            Id = npc,
            X = at.X,
            Z = at.Z,
            Heading = HeadingOf(npc, moving),

            // 이동 중이면 0 이다 (docs/20 §8.1). SimWorld 의 POI 는 도착해야 바뀌므로
            // 그대로 실으면 클라이언트가 "출발지에 서 있다" 로 읽는다.
            Poi = moving ? (ushort)0 : world.PoiOf(npc).Value,
            TargetPoi = moving ? target.Value : (ushort)0,
            Zone = world.ZoneOf(npc).Value,
            Archetype = world.ArchetypeOf(npc).Value,
            Hp = _world.Needs.HpOf(npc),
            Kind = (byte)EntityKind.Npc,
            Visual = (byte)VisualOf(moving, working),
            StateFlags = (byte)flags,
            Reserved = 0,
        };
    }

    private EntityState PlayerOf(PlayerId player, in WorldPos at) => new()
    {
        Id = player.Value,
        X = at.X,
        Z = at.Z,
        Heading = _players.HeadingOf(player),
        Poi = 0,
        TargetPoi = 0,
        Zone = _players.ZoneOf(player).Value,
        Archetype = 0,
        Hp = 100,   // 플레이어 HP 를 들고 있는 곳이 없다. 화면에 쓰이지도 않는다
        Kind = (byte)EntityKind.Player,
        Visual = (byte)VisualState.Idle,
        StateFlags = 0,
        Reserved = 0,
    };

    /// <summary>
    /// 겉보기 상태. <b>추론이다</b> — <c>SetVisualState</c> 명령을 받아 두는 곳이 없다.
    ///
    /// 실제 게임서버라면 마지막 <c>SetVisualState</c> 를 들고 있겠지만, 대역은 그 명령을
    /// 즉시 완료로 답하고 버린다 (<c>SimWorld.ApplyCommand</c>). 이동·작업에서 되짚는 편이
    /// 화면에 보이는 것과 어긋나지 않는다.
    /// </summary>
    private static VisualState VisualOf(bool moving, bool working) => moving
        ? VisualState.Walking
        : working ? VisualState.Working : VisualState.Idle;

    /// <summary>
    /// 이동 중인 NPC 가 바라보는 방향. 정지 중이면 0 이다.
    ///
    /// <c>TransformEmitter</c> 는 <c>Heading = 0</c> 으로 발행하므로(패스파인딩이 없어 직선이다)
    /// 여기서 출발지 → 목적지 벡터로 되짚는다. <see cref="PlayerRegistry"/> 와 같은 규약
    /// (<c>Atan2(x, z)</c>)을 쓴다 — 다르면 NPC 와 플레이어가 서로 다른 쪽을 본다.
    /// </summary>
    private float HeadingOf(int npc, bool moving)
    {
        if (!moving)
        {
            return 0;
        }

        PoiId from = _world.Movement.OriginOf(npc);
        PoiId to = _world.Movement.TargetOf(npc);

        if (from.Value == 0 || to.Value == 0)
        {
            return 0;
        }

        WorldPos a = _data.Pois[from].Pos;
        WorldPos b = _data.Pois[to].Pos;

        return MathF.Atan2(b.X - a.X, b.Z - a.Z);
    }

    private static float Distance(in WorldPos a, in WorldPos b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;

        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    // ---------------------------------------------------------------- 가까운 순 K개

    /// <summary>후보 하나를 들이민다. 가까운 <see cref="MaxEntities"/> 개만 살아남는다.</summary>
    private void Offer(float distance, in EntityState entity)
    {
        if (_count < MaxEntities)
        {
            _distance[_count] = distance;
            _heap[_count] = entity;

            SiftUp(_count);
            _count++;

            return;
        }

        if (distance >= _distance[0])
        {
            CulledByCap++;
            return;   // 지금 담긴 것 중 가장 먼 것보다도 멀다
        }

        // 가장 먼 것을 밀어낸다.
        CulledByCap++;
        _distance[0] = distance;
        _heap[0] = entity;

        SiftDown(0, _count);
    }

    /// <summary>최대 힙을 오름차순으로 편다. 힙 정렬이라 추가 배열이 없다.</summary>
    private void SortByDistance()
    {
        for (int end = _count - 1; end > 0; end--)
        {
            Swap(0, end);
            SiftDown(0, end);
        }
    }

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;

            if (_distance[parent] >= _distance[index])
            {
                return;
            }

            Swap(parent, index);
            index = parent;
        }
    }

    private void SiftDown(int index, int length)
    {
        while (true)
        {
            int left = (index * 2) + 1;

            if (left >= length)
            {
                return;
            }

            int largest = left;
            int right = left + 1;

            if (right < length && _distance[right] > _distance[left])
            {
                largest = right;
            }

            if (_distance[index] >= _distance[largest])
            {
                return;
            }

            Swap(index, largest);
            index = largest;
        }
    }

    private void Swap(int a, int b)
    {
        (_distance[a], _distance[b]) = (_distance[b], _distance[a]);
        (_heap[a], _heap[b]) = (_heap[b], _heap[a]);
    }
}
