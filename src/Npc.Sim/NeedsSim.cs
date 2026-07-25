using Npc.Contracts;
using Npc.Core.Plan;

namespace Npc.Sim;

/// <summary>
/// 욕구 진행. docs/11 §6.
///
/// <b>이게 없으면 이 프로젝트 전체가 정지 화면이 된다.</b>
/// 배고픔·피로가 시간에 따라 오르지 않으면 NPC 가 계획을 바꿀 이유가 없고,
/// 재계획도 인터럽트도 영원히 발동하지 않는다.
///
/// 개체 편차는 <c>npcId</c> 해시로 준다 — 난수를 쓰면 리플레이가 깨진다 (CLAUDE.md §2.3).
/// </summary>
public sealed class NeedsSim
{
    /// <summary>이 게임 시간(시)마다 스태미나가 <see cref="StaminaDropPerPeriod"/> 만큼 준다.</summary>
    public const int PeriodGameHours = 1;

    /// <summary>
    /// 주기마다 떨어지는 스태미나. 게임 12시간이면 탈진(20 미만)해야 한다 —
    /// 하루 안에 쉴 이유가 생기지 않으면 계획이 절대 안 바뀐다.
    /// </summary>
    public const int StaminaDropPerPeriod = 8;

    /// <summary>주기마다 떨어지는 HP (허기 대용). 0 이면 굶어도 안 죽는다.</summary>
    public const int HungerDropPerPeriod = 3;

    /// <summary>이 값 아래로 내려가면 IsExhausted 가 선다 (docs/01 §1).</summary>
    public const int ExhaustedThreshold = 20;

    private readonly SimWorld _world;
    private readonly short[] _hp;
    private readonly short[] _stamina;
    private readonly long _periodTicks;

    /// <summary>욕구 시뮬을 만든다.</summary>
    public NeedsSim(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        _world = world;
        _hp = new short[world.Capacity];
        _stamina = new short[world.Capacity];

        // 게임 1시간 = 3,600 게임초 → 틱으로 환산.
        _periodTicks = Math.Max(1, (long)PeriodGameHours * 3_600 * Contracts.Tick.PerSecond / world.Options.TimeScale);

        for (int npc = 0; npc < world.Capacity; npc++)
        {
            _hp[npc] = 100;
            _stamina[npc] = 100;
        }
    }

    /// <summary>발행한 NpcVitalsChanged 수.</summary>
    public long VitalsEmitted { get; private set; }

    /// <summary>주기의 틱 수.</summary>
    public long PeriodTicks => _periodTicks;

    /// <summary>이 NPC 의 HP.</summary>
    public short HpOf(int npc) => _hp[npc];

    /// <summary>이 NPC 의 스태미나.</summary>
    public short StaminaOf(int npc) => _stamina[npc];

    /// <summary>먹거나 자면 회복한다. InteractionSim 이 아니라 호출자가 부른다.</summary>
    public void Restore(int npc, int hp, int stamina, Tick now)
    {
        _hp[npc] = (short)Math.Clamp(_hp[npc] + hp, 0, 100);
        _stamina[npc] = (short)Math.Clamp(_stamina[npc] + stamina, 0, 100);
        EmitVitals(npc, now);
    }

    /// <summary>
    /// 시간에 따라 배고픔·피로가 오른다.
    /// NPC 별로 주기가 어긋나 있어서 전원이 같은 틱에 이벤트를 쏟지 않는다.
    /// </summary>
    public void Tick(Tick now)
    {
        for (int npc = 0; npc < _world.Capacity; npc++)
        {
            if (!_world.IsSpawned(npc))
            {
                continue;
            }

            // 개체별 위상차. 5,000마리가 같은 틱에 몰리면 이벤트 스파이크가 생긴다.
            long phase = PlanHash.Mix(npc, 0x5EED) % (uint)_periodTicks;

            if ((now.Value + phase) % _periodTicks != 0)
            {
                continue;
            }

            _stamina[npc] = (short)Math.Max(0, _stamina[npc] - StaminaDropPerPeriod);
            _hp[npc] = (short)Math.Max(1, _hp[npc] - HungerDropPerPeriod);

            EmitVitals(npc, now);
        }
    }

    private void EmitVitals(int npc, Tick now)
    {
        _world.Emit(new GameEvent
        {
            Kind = GameEventKind.NpcVitalsChanged,
            Sequence = 0,
            OccurredAt = now,
            Npc = new NpcId(npc),
            Hp = _hp[npc],
            Stamina = _stamina[npc],
        });

        VitalsEmitted++;
    }
}
