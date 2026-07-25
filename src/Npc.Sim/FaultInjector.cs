using Npc.Contracts;
using Npc.Core.Plan;

namespace Npc.Sim;

/// <summary>
/// 실패·드롭 주입. docs/11 §6.
///
/// <list type="bullet">
///   <item><c>--fail-rate</c> — 확률로 <c>NpcActionFailed</c> 를 낸다. 폴백 경로가 실제로 도는지 본다.</item>
///   <item><c>--drop-rate</c> — 명령을 <b>조용히</b> 버린다. 응답이 아예 안 오므로
///         NPC 서버의 타임아웃 합성(docs/03 §6)이 돌아야 진행이 재개된다.</item>
/// </list>
///
/// <b>이 경로는 상시 테스트한다.</b> 명령 유실 방어는 한 번 깨지면 NPC 가 영원히 멈추는데,
/// 평소에는 아무 일도 안 일어나므로 일부러 깨뜨려야 보인다.
///
/// 난수는 시드 고정 해시다 — 같은 시드·같은 명령열이면 같은 곳에서 같은 고장이 난다.
/// </summary>
public sealed class FaultInjector
{
    /// <summary>드롭 판정의 소금. 실패 판정과 다른 값이라 두 확률이 독립이다.</summary>
    private const int DropSalt = 0x0D_0D_0D;

    /// <summary>실패 판정의 소금.</summary>
    private const int FailSalt = 0x0F_A1_1E;

    private readonly SimWorld _world;
    private readonly SimWorld.CommandHandler _inner;
    private readonly uint _dropThreshold;
    private readonly uint _failThreshold;

    /// <summary>주입기를 만든다. 하위 시뮬 핸들러를 감싼다.</summary>
    public FaultInjector(SimWorld world, SimWorld.CommandHandler inner)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(inner);

        _world = world;
        _inner = inner;
        _dropThreshold = (uint)Math.Clamp(world.Options.DropRate * 1_000_000, 0, 1_000_000);
        _failThreshold = (uint)Math.Clamp(world.Options.FailRate * 1_000_000, 0, 1_000_000);
    }

    /// <summary>조용히 버린 명령 수.</summary>
    public long Dropped { get; private set; }

    /// <summary>실패로 답한 명령 수.</summary>
    public long Failed { get; private set; }

    /// <summary>통과시킨 명령 수.</summary>
    public long Passed { get; private set; }

    /// <summary><see cref="SimWorld.Handler"/> 에 붙는다.</summary>
    public bool TryHandle(in NpcCommand command, Tick now)
    {
        // 드롭은 응답을 아예 주지 않는다. 실패 이벤트조차 보내지 않는 것이 핵심이다 —
        // 그래야 NPC 서버가 타임아웃을 합성해야만 진행된다.
        if (_dropThreshold > 0 && Roll(in command, salt: DropSalt) < _dropThreshold)
        {
            Dropped++;
            return true;
        }

        if (_failThreshold > 0 && Roll(in command, salt: FailSalt) < _failThreshold)
        {
            Failed++;
            _world.Fail(in command, now, ActionFailReason.PreconditionFailed);
            return true;
        }

        Passed++;
        return _inner(in command, now);
    }

    /// <summary>0..999,999 의 결정론 난수. 명령의 상관 ID 가 씨앗이라 같은 명령은 같은 운명을 맞는다.</summary>
    private uint Roll(in NpcCommand command, int salt)
    {
        uint mixed = PlanHash.Mix(
            PlanHash.Mix(_world.Options.Seed, salt)
            ^ PlanHash.Mix(command.Correlation.Value)
            ^ PlanHash.Mix((uint)command.Npc.Value));

        return mixed % 1_000_000;
    }
}
