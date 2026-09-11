using Npc.Contracts;
using Npc.MasterData;

namespace Npc.Sim;

/// <summary>
/// 이동 시뮬. docs/02 §5 · docs/11 §6.
///
/// <b>패스파인딩을 하지 않는다.</b> 거리 행렬 ÷ 이동속도 = 소요 틱이다.
/// 경로 계산은 실제 게임서버 소관이고, 여기서 흉내내봐야 NPC 서버 검증에 아무 도움이 안 된다.
///
/// 대기 목록은 NPC 당 한 칸이다 — 한 NPC 는 한 번에 한 곳으로만 간다.
/// 매 틱 전원을 훑지만 <c>long</c> 비교 하나뿐이라 5,000마리도 무시할 만하다.
/// </summary>
public sealed class MovementSim
{
    /// <summary>뛸 때의 속도 배수. 걷기보다 이만큼 빠르다.</summary>
    public const double RunSpeedFactor = 0.6;

    private readonly SimWorld _world;
    private readonly double _walkSecondsPerMeter;
    private readonly long[] _arriveAt;      // 0 = 이동 중 아님
    private readonly ushort[] _target;
    private readonly uint[] _correlation;
    private readonly long[] _startedAt;
    private readonly ushort[] _origin;

    /// <summary>이동 시뮬을 만든다. SimWorld 의 Handler 에 자동으로 붙는다.</summary>
    public MovementSim(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        _world = world;
        _arriveAt = new long[world.Capacity];
        _target = new ushort[world.Capacity];
        _correlation = new uint[world.Capacity];
        _startedAt = new long[world.Capacity];
        _origin = new ushort[world.Capacity];

        // 걷기 속도는 actions.json 의 MoveTo.duration.per_meter_s 에서 온다.
        // 코드에 박아두면 마스터데이터를 고쳐도 Sim 이 따라오지 않는다.
        _walkSecondsPerMeter = world.Data.Actions.TryGet("MoveTo", out ActionDef moveTo)
            ? moveTo.Duration.PerMeterSeconds
            : 0.6;
    }

    /// <summary>도착시킨 횟수.</summary>
    public long Arrivals { get; private set; }

    /// <summary>지금 이동 중인 NPC 수.</summary>
    public int Moving
    {
        get
        {
            int moving = 0;
            foreach (long at in _arriveAt)
            {
                if (at != 0)
                {
                    moving++;
                }
            }

            return moving;
        }
    }

    /// <summary>이 NPC 가 이동 중인가.</summary>
    public bool IsMoving(int npc) => _arriveAt[npc] != 0;

    /// <summary>이 NPC 의 도착 예정 틱. 이동 중이 아니면 0.</summary>
    public long ArrivalTickOf(int npc) => _arriveAt[npc];

    /// <summary>출발 틱. 보간 발행이 쓴다.</summary>
    public long DepartureTickOf(int npc) => _startedAt[npc];

    /// <summary>출발 POI. 보간 발행이 쓴다.</summary>
    public PoiId OriginOf(int npc) => new(_origin[npc]);

    /// <summary>목표 POI. 보간 발행이 쓴다.</summary>
    public PoiId TargetOf(int npc) => new(_target[npc]);

    /// <summary>이동 명령을 받는다. <see cref="SimWorld.Handler"/> 가 부른다.</summary>
    public bool TryHandle(in NpcCommand command, Tick now)
    {
        if (command.Kind != NpcCommandKind.MoveTo)
        {
            return false;
        }

        int npc = command.Npc.Value;

        if (command.TargetPoi.Value == 0)
        {
            // Follow(TargetNpc) 나 Wander(Zone) — 목적지가 POI 가 아니다.
            // 배회는 제자리 근처를 도는 것으로 보고 짧게 끝낸다.
            _world.Complete(in command, now);
            return true;
        }

        PoiId from = _world.PoiOf(npc);
        float distance = _world.Data.Pois.Distance(from, command.TargetPoi);

        if (float.IsInfinity(distance))
        {
            _world.Fail(in command, now, ActionFailReason.Unreachable);
            return true;
        }

        _origin[npc] = from.Value;
        _target[npc] = command.TargetPoi.Value;
        _correlation[npc] = command.Correlation.Value;
        _startedAt[npc] = now.Value;
        _arriveAt[npc] = now.Value + TravelTicks(distance, (MoveSpeed)command.Flags);

        return true;
    }

    /// <summary>거리(m)를 소요 틱으로. 최소 1틱.</summary>
    public long TravelTicks(double meters, MoveSpeed speed)
    {
        double secondsPerMeter = speed == MoveSpeed.Run
            ? _walkSecondsPerMeter * RunSpeedFactor
            : _walkSecondsPerMeter;

        double gameSeconds = meters * secondsPerMeter;
        long ticks = (long)Math.Round(gameSeconds * Contracts.Tick.PerSecond / _world.Options.TimeScale);

        return ticks < 1 ? 1 : ticks;
    }

    /// <summary>도착한 NPC 에게 NpcArrived 를 낸다.</summary>
    public void Tick(Tick now)
    {
        for (int npc = 0; npc < _arriveAt.Length; npc++)
        {
            if (_arriveAt[npc] == 0 || _arriveAt[npc] > now.Value)
            {
                continue;
            }

            var poi = new PoiId(_target[npc]);
            var correlation = new CorrelationId(_correlation[npc]);

            _arriveAt[npc] = 0;
            _world.MoveTo(npc, poi);

            _world.Emit(new GameEvent
            {
                Kind = GameEventKind.NpcArrived,
                Sequence = 0,
                OccurredAt = now,
                Npc = _world.NpcIdOf(npc),
                Correlation = correlation,
                Poi = poi,
                Zone = _world.ZoneOf(npc),
                Pos = _world.PositionOf(npc),
            });

            Arrivals++;
        }
    }

    /// <summary>이동을 취소한다. 인터럽트로 새 명령이 오면 링크가 아니라 상관 ID 가 정리한다.</summary>
    public void Cancel(int npc) => _arriveAt[npc] = 0;
}
