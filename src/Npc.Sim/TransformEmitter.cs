using Npc.Contracts;

namespace Npc.Sim;

/// <summary>
/// 이동 중 위치 보간 발행. docs/11 §12 (이벤트 큐 폭주 대응).
///
/// <b>플레이어가 보고 있지 않은 NPC 는 발행하지 않는다.</b>
/// <c>NpcTransform</c> 은 최다 이벤트라, 5,000마리 전원이 매 틱 보내면 이벤트 큐가 무한히 자란다
/// (docs/11 §12 의 세 번째 증상). NPC 서버의 LOD 2·3 에 해당하는 NPC 는 여기서 걸러진다.
/// </summary>
public sealed class TransformEmitter
{
    /// <summary>
    /// 틱당 발행 상한. 관측 대상이 몰려도 이벤트/초가 3,000 을 넘지 않게 한다 (docs/11 §12).
    /// 10Hz 기준 256 × 10 = 2,560/s.
    /// </summary>
    public const int MaxPerTick = 256;

    private readonly SimWorld _world;
    private readonly MovementSim _movement;
    private int _cursor;

    /// <summary>발행기를 만든다.</summary>
    public TransformEmitter(SimWorld world, MovementSim movement)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(movement);

        _world = world;
        _movement = movement;
    }

    /// <summary>발행한 NpcTransform 수.</summary>
    public long Emitted { get; private set; }

    /// <summary>보고 있지 않아서 건너뛴 수.</summary>
    public long SkippedUnobserved { get; private set; }

    /// <summary>틱당 상한에 걸려 다음 틱으로 넘긴 수.</summary>
    public long Throttled { get; private set; }

    /// <summary>발행 주기(틱).</summary>
    public int PeriodTicks => Math.Max(1, _world.Options.TransformPeriodTicks);

    /// <summary>이번 틱의 보간 위치를 낸다.</summary>
    public void Tick(Tick now)
    {
        if (now.Value % PeriodTicks != 0)
        {
            return;
        }

        if (_world.Capacity == 0)
        {
            return;
        }

        int sent = 0;

        // 커서를 들고 돌기 때문에 상한에 걸려도 특정 NPC 만 계속 굶지 않는다.
        for (int scanned = 0; scanned < _world.Capacity && sent < MaxPerTick; scanned++)
        {
            int npc = _cursor;
            _cursor = _cursor + 1 >= _world.Capacity ? 0 : _cursor + 1;

            if (!_movement.IsMoving(npc) || !_world.IsSpawned(npc))
            {
                continue;
            }

            if (!_world.ObservedByPlayer[npc])
            {
                SkippedUnobserved++;
                continue;
            }

            _world.Emit(new GameEvent
            {
                Kind = GameEventKind.NpcTransform,
                Sequence = 0,
                OccurredAt = now,
                Npc = new NpcId(npc),
                Pos = Interpolate(npc, now),
                Heading = 0,
            });

            Emitted++;
            sent++;
        }

        if (sent >= MaxPerTick)
        {
            Throttled++;
        }
    }

    /// <summary>출발지와 목적지 사이를 시간 비율로 선형 보간한다. 패스파인딩이 없으니 직선이다.</summary>
    public WorldPos Interpolate(int npc, Tick now)
    {
        PoiId from = _movement.OriginOf(npc);
        PoiId to = _movement.TargetOf(npc);

        if (from.Value == 0 || to.Value == 0)
        {
            return _world.PositionOf(npc);
        }

        long start = _movement.DepartureTickOf(npc);
        long end = _movement.ArrivalTickOf(npc);
        long span = end - start;

        double t = span <= 0 ? 1.0 : Math.Clamp((now.Value - start) / (double)span, 0.0, 1.0);

        WorldPos a = _world.Data.Pois[from].Pos;
        WorldPos b = _world.Data.Pois[to].Pos;

        return new WorldPos(
            (float)(a.X + ((b.X - a.X) * t)),
            (float)(a.Y + ((b.Y - a.Y) * t)),
            (float)(a.Z + ((b.Z - a.Z) * t)));
    }
}
