using Npc.Contracts;
using Npc.Core.Plan;
using Npc.MasterData;

namespace Npc.Sim;

/// <summary>
/// 가상 플레이어 봇. docs/02 §5 · docs/11 §6.
///
/// <b>인지 LOD 와 우선순위 큐의 w1(플레이어 근접도)을 검증하려면 플레이어가 움직여야 한다.</b>
/// 봇이 없으면 모든 NPC 가 비활성 밴드에 머물러 스캐너가 하는 일이 없다.
///
/// 난수는 시드 고정 해시다 — <c>Random</c> 을 쓰면 리플레이가 깨진다 (CLAUDE.md §2.3).
/// </summary>
public sealed class PlayerBots
{
    /// <summary>봇이 이 틱마다 다음 POI 로 옮겨간다.</summary>
    public const int StepTicks = 20;

    /// <summary>이 거리(m) 안이면 근접으로 본다. docs/11 §4 의 LOD 1 경계와 같다.</summary>
    public const int ProximityRange = 200;

    private readonly SimWorld _world;
    private readonly ushort[] _at;          // 봇별 현재 POI
    private readonly bool[] _wasObserved;   // NPC 별 직전 상태

    /// <summary>봇을 만든다. 전부 서로 다른 POI 에서 출발한다.</summary>
    public PlayerBots(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        _world = world;
        _at = new ushort[Math.Max(0, world.Options.PlayerBots)];
        _wasObserved = new bool[world.Capacity];

        for (int bot = 0; bot < _at.Length; bot++)
        {
            _at[bot] = NextPoi(bot, 0);
        }
    }

    /// <summary>봇 수.</summary>
    public int Count => _at.Length;

    /// <summary>발행한 PlayerProximity 수.</summary>
    public long ProximityEvents { get; private set; }

    /// <summary>지금 플레이어에게 관측되는 NPC 수.</summary>
    public int ObservedNpcs
    {
        get
        {
            int observed = 0;
            foreach (bool o in _world.ObservedByPlayer)
            {
                if (o)
                {
                    observed++;
                }
            }

            return observed;
        }
    }

    /// <summary>이 봇이 지금 있는 POI.</summary>
    public PoiId PoiOf(int bot) => new(_at[bot]);

    /// <summary>봇을 움직이고 근접 변화만 이벤트로 낸다.</summary>
    public void Tick(Tick now)
    {
        if (_at.Length == 0)
        {
            return;
        }

        if (now.Value % StepTicks == 0)
        {
            for (int bot = 0; bot < _at.Length; bot++)
            {
                _at[bot] = NextPoi(bot, now.Value);
            }
        }

        for (int npc = 0; npc < _world.Capacity; npc++)
        {
            if (!_world.IsSpawned(npc))
            {
                continue;
            }

            (bool observed, int distance, int bot) = Observe(npc);

            if (observed == _wasObserved[npc])
            {
                continue;   // 변화가 없으면 이벤트를 내지 않는다 — 큐가 폭주한다
            }

            _wasObserved[npc] = observed;
            _world.ObservedByPlayer[npc] = observed;

            _world.Emit(new GameEvent
            {
                Kind = GameEventKind.PlayerProximity,
                Sequence = 0,
                OccurredAt = now,
                Npc = new NpcId(npc),
                Player = new PlayerId(bot + 1),
                Amount = distance,
                Code = (byte)(observed ? ProximityChange.Enter : ProximityChange.Leave),
            });

            ProximityEvents++;
        }
    }

    /// <summary>이 NPC 가 어느 봇에게 얼마나 가까운가.</summary>
    private (bool Observed, int Distance, int Bot) Observe(int npc)
    {
        PoiId at = _world.PoiOf(npc);

        if (at.Value == 0)
        {
            return (false, int.MaxValue, 0);
        }

        int nearest = int.MaxValue;
        int nearestBot = 0;

        for (int bot = 0; bot < _at.Length; bot++)
        {
            float distance = _world.Data.Pois.Distance(at, new PoiId(_at[bot]));

            if (distance < nearest)
            {
                nearest = (int)distance;
                nearestBot = bot;
            }
        }

        return (nearest <= ProximityRange, nearest, nearestBot);
    }

    /// <summary>다음 POI. 결정론 해시로 고르는 랜덤 워크다.</summary>
    private ushort NextPoi(int bot, long tick)
    {
        PoiTable pois = _world.Data.Pois;

        if (pois.Count == 0)
        {
            return 0;
        }

        uint roll = PlanHash.Mix(
            PlanHash.Mix(_world.Options.Seed, bot) ^ PlanHash.Mix((uint)(tick / StepTicks)));

        return pois.Pois[(int)(roll % (uint)pois.Count)].Code.Value;
    }
}
